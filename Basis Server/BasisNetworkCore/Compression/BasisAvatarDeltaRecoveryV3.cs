using System;
using System.Runtime.CompilerServices;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Experimental third-generation avatar recovery codec.
    ///
    /// Normal updates retain <see cref="BasisAvatarDeltaCompression"/>'s exact absolute-field
    /// dirty-mask encoding. The full baseline is split into eight deterministic field groups and
    /// refreshed one group at a time instead of replacing the entire baseline with a monolithic
    /// keyframe. A receiver that misses a refresh invalidates only the affected group; all other
    /// groups continue decoding exactly. Invalid groups still apply dirty absolute fields; only
    /// omitted fields remain at their last displayed values until a complete refresh arrives.
    ///
    /// Healthy V3/V3.1 frames remain the ordinary Basis delta body with no extra per-frame bytes.
    /// V3.1 may append a second ordinary delta body containing only receiver-requested baseline
    /// repair groups. The repair section is transient, does not mutate the shared sender baseline,
    /// and can be safely retransmitted until the receiver reports no missing groups.
    ///
    /// This class is an experimental protocol primitive. Live negotiation/reset framing is not
    /// selected by the production avatar channel yet.
    /// </summary>
    public static class BasisAvatarDeltaRecoveryV3
    {
        public const int GroupCount = 8;
        public const byte AllGroupsMask = 0xFF;

        public readonly struct Options
        {
            /// <summary>
            /// Number of sequence frames in one recovery cycle. Exactly eight frames in each
            /// cycle carry a refresh shard; the remaining frames carry only the normal exact delta.
            /// Ten matches the current 500 ms keyframe cadence at a 20 Hz armature rate.
            /// </summary>
            public readonly byte RefreshCycleFrames;
            /// <summary>Maximum receiver-requested baseline groups appended in one recovery packet.</summary>
            public readonly byte MaxRecoveryGroupsPerFrame;
            /// <summary>Force all groups into a coordinated sequence-zero stream start so it is byte-exact.</summary>
            public readonly bool BootstrapOnReset;

            public Options(byte refreshCycleFrames, byte maxRecoveryGroupsPerFrame = 0, bool bootstrapOnReset = false)
            {
                if (refreshCycleFrames < GroupCount || refreshCycleFrames > 32)
                    throw new ArgumentOutOfRangeException(nameof(refreshCycleFrames));
                if (maxRecoveryGroupsPerFrame > GroupCount)
                    throw new ArgumentOutOfRangeException(nameof(maxRecoveryGroupsPerFrame));
                RefreshCycleFrames = refreshCycleFrames;
                MaxRecoveryGroupsPerFrame = maxRecoveryGroupsPerFrame;
                BootstrapOnReset = bootstrapOnReset;
            }

            /// <summary>V3.2: cycle8 plus up to four requested repair groups/frame.</summary>
            public static Options Default => new Options(8, 4, true);
            /// <summary>V3.1 low-overhead cycle12 plus four requested repair groups/frame.</summary>
            public static Options V31LowOverhead => new Options(12, 4, true);
            /// <summary>Original V3 cycle8 behavior retained for benchmark comparison.</summary>
            public static Options LegacyCycle8 => new Options(8);
            public static Options CurrentCadence => new Options(10);
            public static Options LowOverhead => new Options(12);
        }

        private sealed class QualityGeometry
        {
            public int PosBytes;
            public int PayloadSize;
            public int ScaleOffset;
            public int BodyRotOffset;
            public int HipsDeltaOffset;
            public int HipsRotOffset;
            public int EndEffectorOffset;
            public int EndEffectorBytes;
            public int[] BoneBitOffset = Array.Empty<int>();
            public int[] BoneWidth = Array.Empty<int>();
        }

        private static readonly QualityGeometry[] Geo = new QualityGeometry[4];
        private static readonly byte[] FieldGroups;

        static BasisAvatarDeltaRecoveryV3()
        {
            for (int qi = 0; qi < 4; qi++)
            {
                var q = (BasisAvatarBitPacking.BitQuality)qi;
                byte[] bpc = BasisBoneRotationCompression.GetBpcTable(q);
                int[] offsets = new int[bpc.Length];
                BasisBoneRotationCompression.ComputeBitOffsets(bpc, offsets);
                int[] widths = new int[bpc.Length];
                for (int i = 0; i < widths.Length; i++) widths[i] = 2 + 3 * bpc[i];

                int posBytes = BasisAvatarBitPacking.PositionBytes(q);
                int rotBytes = BasisBoneRotationCompression.RotationBytes(q);
                int tailStart = posBytes + rotBytes;
                Geo[qi] = new QualityGeometry
                {
                    PosBytes = posBytes,
                    PayloadSize = BasisBoneRotationCompression.ConvertToSize(q),
                    ScaleOffset = tailStart,
                    BodyRotOffset = tailStart + BasisBoneRotationCompression.WriteScale,
                    HipsDeltaOffset = tailStart + BasisBoneRotationCompression.WriteScale + BasisBoneRotationCompression.WriteRotation,
                    HipsRotOffset = tailStart + BasisBoneRotationCompression.WriteScale + BasisBoneRotationCompression.WriteRotation + BasisBoneRotationCompression.WriteHipsDelta,
                    EndEffectorOffset = tailStart + BasisBoneRotationCompression.TailBytes,
                    EndEffectorBytes = BasisBoneRotationCompression.EndEffectorBytes(q),
                    BoneBitOffset = offsets,
                    BoneWidth = widths,
                };
            }

            // Balance the fixed field-to-group mapping by High-quality wire weight. Using one
            // shared map for every quality keeps protocol behavior deterministic across endpoints,
            // while avoiding a pathological group that owns most of the large 12-BPC body bones.
            FieldGroups = BuildBalancedFieldGroups(Geo[(int)BasisAvatarBitPacking.BitQuality.High]);
        }

        public static int PayloadSize(BasisAvatarBitPacking.BitQuality quality)
            => Geo[(int)quality].PayloadSize;

        public static int MaxBodySize(BasisAvatarBitPacking.BitQuality quality)
            => BasisAvatarDeltaCompression.MaxDeltaSize(quality) * 2;

        public static int GetFieldGroup(int field)
        {
            if ((uint)field >= BasisAvatarDeltaCompression.FieldCount) throw new ArgumentOutOfRangeException(nameof(field));
            return FieldGroups[field];
        }

        public static int GetBoneGroup(int boneSlot)
        {
            if ((uint)boneSlot >= BasisBoneRotationCompression.SyncBoneCount) throw new ArgumentOutOfRangeException(nameof(boneSlot));
            return FieldGroups[BasisAvatarDeltaCompression.BoneFieldStart + boneSlot];
        }

        /// <summary>
        /// Returns the refresh-group bit for a sequence, or zero on one of the cycle's normal
        /// delta-only frames. Every group appears exactly once in each complete cycle. The skipped
        /// phases and group permutation change by cycle to avoid a fixed periodic-loss phase from
        /// permanently targeting one group.
        /// </summary>
        public static byte ScheduledRefreshMask(byte sequence, Options options)
        {
            int cycleFrames = options.RefreshCycleFrames;
            if (cycleFrames == GroupCount)
            {
                // Every eight-frame block contains all eight groups, but the phase shifts by one
                // group per block. A loss at a fixed packet period therefore walks across groups
                // instead of permanently targeting one. Across the byte-sequence wrap the maximum
                // gap between refreshes of the same group is nine frames.
                int block = sequence >> 3;
                int refreshGroup = (sequence + block * 7) & (GroupCount - 1);
                return (byte)(1 << refreshGroup);
            }

            int cycle = sequence / cycleFrames;
            int phase = sequence % cycleFrames;

            // Rotate which physical phases are the optional no-refresh slots each cycle.
            int mapped = (phase + (cycle * 3)) % cycleFrames;
            if (mapped >= GroupCount) return 0;

            // An odd affine permutation visits every 3-bit group exactly once.
            int step = (cycle & 3) switch
            {
                0 => 1,
                1 => 3,
                2 => 5,
                _ => 7,
            };
            int offset = (cycle * 5 + 1) & (GroupCount - 1);
            int group = (mapped * step + offset) & (GroupCount - 1);
            return (byte)(1 << group);
        }

        public sealed class Encoder
        {
            private readonly BasisAvatarBitPacking.BitQuality _quality;
            private readonly Options _options;
            private readonly QualityGeometry _geometry;
            private readonly byte[] _baseline;
            private readonly byte[] _forcedBaseline;
            private readonly byte[] _recoveryForcedBaseline;
            private readonly byte[] _recoveryScratch;
            private bool _bootstrapPending;

            public Encoder(BasisAvatarBitPacking.BitQuality quality)
                : this(quality, Options.Default)
            {
            }

            public Encoder(BasisAvatarBitPacking.BitQuality quality, Options options)
            {
                _quality = quality;
                _options = options;
                _geometry = Geo[(int)quality];
                _baseline = new byte[_geometry.PayloadSize];
                _forcedBaseline = new byte[_geometry.PayloadSize];
                _recoveryForcedBaseline = new byte[_geometry.PayloadSize];
                _recoveryScratch = new byte[BasisAvatarDeltaCompression.MaxDeltaSize(_quality)];
                Reset();
            }

            public int PayloadSize => _geometry.PayloadSize;
            public int MaxBodySize => BasisAvatarDeltaCompression.MaxDeltaSize(_quality) * 2;
            public byte LastRefreshMask { get; private set; }
            public byte LastRecoveryMask { get; private set; }

            public void Reset()
            {
                InitializeNeutralPayload(_baseline, _geometry);
                Buffer.BlockCopy(_baseline, 0, _forcedBaseline, 0, _baseline.Length);
                Buffer.BlockCopy(_baseline, 0, _recoveryForcedBaseline, 0, _baseline.Length);
                _bootstrapPending = _options.BootstrapOnReset;
                LastRefreshMask = 0;
                LastRecoveryMask = 0;
            }

            /// <summary>
            /// Builds one exact V3 body. Baseline state commits only after the complete underlying
            /// delta was successfully written.
            /// </summary>
            public int Encode(byte[] payload, byte sequence, byte[] destination, int destinationStart)
                => Encode(payload, sequence, 0, destination, destinationStart);

            /// <summary>
            /// Builds one exact V3.1 body. <paramref name="requestedRecoveryMask"/> comes from a
            /// receiver's <see cref="Decoder.MissingGroupMask"/>. Requested groups are appended as
            /// a second ordinary delta body containing the sender's existing baseline values; this
            /// repairs that receiver without mutating the shared sender baseline.
            /// </summary>
            public int Encode(
                byte[] payload,
                byte sequence,
                byte requestedRecoveryMask,
                byte[] destination,
                int destinationStart)
            {
                if (payload == null || payload.Length < _geometry.PayloadSize || destination == null || destinationStart < 0)
                    return -1;
                if (destination.Length - destinationStart < MaxBodySize) return -1;

                Buffer.BlockCopy(_baseline, 0, _forcedBaseline, 0, _baseline.Length);
                byte scheduledMask = ScheduledRefreshMask(sequence, _options);
                byte refreshMask = _bootstrapPending && sequence == 0 ? AllGroupsMask : scheduledMask;
                if (refreshMask != 0)
                    ForceGroupsDifferent(_forcedBaseline, payload, _geometry, refreshMask);

                int normalLength = BasisAvatarDeltaCompression.BuildDelta(_forcedBaseline, payload, _quality, destination, destinationStart);
                if (normalLength < 0) return -1;

                // A scheduled refresh already carries a complete current value for that group.
                byte recoveryCandidates = (byte)(requestedRecoveryMask & ~refreshMask);
                byte recoveryMask = SelectRecoveryGroups(recoveryCandidates, _options.MaxRecoveryGroupsPerFrame, sequence);
                int recoveryLength = 0;
                if (recoveryMask != 0)
                {
                    Buffer.BlockCopy(_baseline, 0, _recoveryForcedBaseline, 0, _baseline.Length);
                    ForceGroupsDifferent(_recoveryForcedBaseline, _baseline, _geometry, recoveryMask);
                    recoveryLength = BasisAvatarDeltaCompression.BuildDelta(
                        _recoveryForcedBaseline, _baseline, _quality, _recoveryScratch, 0);
                    if (recoveryLength < 0 || normalLength + recoveryLength > destination.Length - destinationStart)
                        return -1;
                    Buffer.BlockCopy(_recoveryScratch, 0, destination, destinationStart + normalLength, recoveryLength);
                }

                if (refreshMask != 0)
                    CopyGroups(payload, _baseline, _geometry, refreshMask);
                _bootstrapPending = false;
                LastRefreshMask = refreshMask;
                LastRecoveryMask = recoveryMask;
                return normalLength + recoveryLength;
            }
        }

        public sealed class Decoder
        {
            private readonly BasisAvatarBitPacking.BitQuality _quality;
            private readonly Options _options;
            private readonly QualityGeometry _geometry;
            private byte[] _baseline;
            private byte[] _baselineScratch;
            private byte[] _displayed;
            private byte[] _displayedScratch;
            private readonly byte[] _reconstructed;
            private bool _hasSequence;
            private byte _lastSequence;
            private byte _validGroups;

            public Decoder(BasisAvatarBitPacking.BitQuality quality)
                : this(quality, Options.Default)
            {
            }

            public Decoder(BasisAvatarBitPacking.BitQuality quality, Options options)
            {
                _quality = quality;
                _options = options;
                _geometry = Geo[(int)quality];
                _baseline = new byte[_geometry.PayloadSize];
                _baselineScratch = new byte[_geometry.PayloadSize];
                _displayed = new byte[_geometry.PayloadSize];
                _displayedScratch = new byte[_geometry.PayloadSize];
                _reconstructed = new byte[_geometry.PayloadSize];
                Reset();
            }

            public int PayloadSize => _geometry.PayloadSize;
            public int MaxBodySize => BasisAvatarDeltaCompression.MaxDeltaSize(_quality) * 2;
            public bool HasSequence => _hasSequence;
            public byte LastSequence => _lastSequence;
            public byte ValidGroupMask => _validGroups;
            public byte MissingGroupMask => (byte)(AllGroupsMask & ~_validGroups);
            public bool IsFullySynchronized => _validGroups == AllGroupsMask;

            public void Reset()
            {
                InitializeNeutralPayload(_baseline, _geometry);
                InitializeNeutralPayload(_baselineScratch, _geometry);
                InitializeNeutralPayload(_displayed, _geometry);
                InitializeNeutralPayload(_displayedScratch, _geometry);
                Array.Clear(_reconstructed, 0, _reconstructed.Length);
                _hasSequence = false;
                _lastSequence = 0;
                _validGroups = 0;
            }

            public void CopyDisplayedPose(byte[] destination)
            {
                if (destination == null || destination.Length < _geometry.PayloadSize)
                    throw new ArgumentException("Destination is smaller than the avatar payload.", nameof(destination));
                Buffer.BlockCopy(_displayed, 0, destination, 0, _geometry.PayloadSize);
            }

            /// <summary>
            /// Decodes one V3 body. Missing refresh packets invalidate only their scheduled groups.
            /// A complete later refresh revalidates the group without depending on the stale baseline.
            /// Duplicate/stale packets and malformed/truncated bodies never commit state.
            /// </summary>
            public bool TryDecode(
                byte[] source,
                int sourceStart,
                int availableBytes,
                byte sequence,
                byte[] outputPayload,
                out int consumedBytes)
            {
                consumedBytes = 0;
                if (source == null || outputPayload == null || outputPayload.Length < _geometry.PayloadSize) return false;
                if (sourceStart < 0 || availableBytes < 0 || sourceStart + availableBytes > source.Length) return false;

                int forward = 1;
                if (_hasSequence)
                {
                    forward = (byte)(sequence - _lastSequence);
                    if (forward == 0 || forward >= 128) return false;
                }

                int normalLength = BasisAvatarDeltaCompression.DeltaBodyLength(source, sourceStart, availableBytes, _quality);
                if (normalLength < 0 || normalLength > availableBytes || sourceStart + normalLength > source.Length) return false;

                byte scheduledMask = ScheduledRefreshMask(sequence, _options);
                byte normalRefreshMask = scheduledMask;
                if (!_hasSequence && sequence == 0 && _options.BootstrapOnReset)
                    normalRefreshMask = AllGroupsMask;
                if (!RefreshGroupsAreComplete(source, sourceStart, normalLength, _geometry, normalRefreshMask)) return false;

                Buffer.BlockCopy(_baseline, 0, _baselineScratch, 0, _baseline.Length);
                Buffer.BlockCopy(_displayed, 0, _displayedScratch, 0, _displayed.Length);
                byte nextValid = _validGroups;

                if (_hasSequence && forward > 1)
                {
                    for (int i = 1; i < forward; i++)
                    {
                        byte missingSequence = unchecked((byte)(_lastSequence + i));
                        nextValid &= (byte)~ScheduledRefreshMask(missingSequence, _options);
                    }
                }

                // V3.1 may append one baseline-repair delta. It contains only complete groups and
                // is applied before the normal body so the latter can be reconstructed against the
                // exact sender baseline. Losing this repair is harmless because the sender baseline
                // itself never changes as a consequence of a receiver request.
                int recoveryLength = 0;
                int recoveryStart = sourceStart + normalLength;
                int remaining = availableBytes - normalLength;
                if (remaining > 0)
                {
                    recoveryLength = BasisAvatarDeltaCompression.DeltaBodyLength(source, recoveryStart, remaining, _quality);
                    if (recoveryLength <= 0 || recoveryLength != remaining) return false;
                    byte recoveryDirtyGroups = DirtyGroupMask(source, recoveryStart, _geometry);
                    byte recoveryCompleteGroups = CompleteRefreshGroupMask(source, recoveryStart, _geometry);
                    if (recoveryDirtyGroups == 0 || recoveryDirtyGroups != recoveryCompleteGroups) return false;
                    if (!BasisAvatarDeltaCompression.TryApplyDelta(
                        _baselineScratch, source, recoveryStart, recoveryLength, _quality, _reconstructed))
                        return false;
                    Buffer.BlockCopy(_reconstructed, 0, _baselineScratch, 0, _baselineScratch.Length);
                    nextValid |= recoveryCompleteGroups;
                }

                // Decode the normal frame against the receiver's repaired/current group baselines.
                if (!BasisAvatarDeltaCompression.TryApplyDelta(
                    _baselineScratch, source, sourceStart, normalLength, _quality, _reconstructed))
                    return false;

                for (int group = 0; group < GroupCount; group++)
                {
                    byte bit = (byte)(1 << group);
                    if ((normalRefreshMask & bit) != 0)
                    {
                        CopyGroups(_reconstructed, _baselineScratch, _geometry, bit);
                        CopyGroups(_reconstructed, _displayedScratch, _geometry, bit);
                        nextValid |= bit;
                    }
                    else if ((nextValid & bit) != 0)
                    {
                        CopyGroups(_reconstructed, _displayedScratch, _geometry, bit);
                    }
                    else
                    {
                        // Dirty fields are encoded as absolute current values, not arithmetic
                        // deltas. Even when this group's baseline refresh was lost, those fields
                        // remain receiver-independent and can be displayed exactly. Only fields
                        // omitted because they match the sender's newer baseline must be held.
                        CopyDirtyFieldsForGroup(_reconstructed, _displayedScratch, _geometry, source, sourceStart, group);
                    }
                }

                byte[] oldBaseline = _baseline;
                _baseline = _baselineScratch;
                _baselineScratch = oldBaseline;

                byte[] oldDisplayed = _displayed;
                _displayed = _displayedScratch;
                _displayedScratch = oldDisplayed;

                _validGroups = nextValid;
                _hasSequence = true;
                _lastSequence = sequence;
                Buffer.BlockCopy(_displayed, 0, outputPayload, 0, _geometry.PayloadSize);
                consumedBytes = normalLength + recoveryLength;
                return true;
            }
        }

        private static byte[] BuildBalancedFieldGroups(QualityGeometry high)
        {
            int count = BasisAvatarDeltaCompression.FieldCount;
            var weights = new int[count];
            weights[0] = high.PosBytes * 8;
            for (int bone = 0; bone < BasisBoneRotationCompression.SyncBoneCount; bone++)
                weights[BasisAvatarDeltaCompression.BoneFieldStart + bone] = high.BoneWidth[bone];

            int scale = 1 + BasisBoneRotationCompression.SyncBoneCount;
            weights[scale] = BasisBoneRotationCompression.WriteScale * 8;
            weights[scale + 1] = BasisBoneRotationCompression.WriteRotation * 8;
            weights[scale + 2] = BasisBoneRotationCompression.WriteHipsDelta * 8;
            weights[scale + 3] = BasisBoneRotationCompression.WriteHipsRotation * 8;
            weights[scale + 4] = high.EndEffectorBytes * 8;

            var result = new byte[count];
            var loads = new int[GroupCount];
            var assigned = new bool[count];
            for (int n = 0; n < count; n++)
            {
                int field = -1;
                int weight = -1;
                for (int i = 0; i < count; i++)
                {
                    if (assigned[i]) continue;
                    if (weights[i] > weight)
                    {
                        weight = weights[i];
                        field = i;
                    }
                }

                int group = 0;
                for (int g = 1; g < GroupCount; g++)
                    if (loads[g] < loads[group]) group = g;
                result[field] = (byte)group;
                loads[group] += weight;
                assigned[field] = true;
            }
            return result;
        }

        private static void ForceGroupsDifferent(byte[] forcedBaseline, byte[] current, QualityGeometry geometry, byte groups)
        {
            for (int field = 0; field < BasisAvatarDeltaCompression.FieldCount; field++)
            {
                byte bit = (byte)(1 << FieldGroups[field]);
                if ((groups & bit) == 0 || !FieldExists(geometry, field)) continue;
                ForceFieldDifferent(forcedBaseline, current, geometry, field);
            }
        }

        private static bool RefreshGroupsAreComplete(byte[] body, int start, int length, QualityGeometry geometry, byte groups)
        {
            if (groups == 0) return true;
            if (length < BasisAvatarDeltaCompression.DirtyMaskBytes) return false;
            for (int field = 0; field < BasisAvatarDeltaCompression.FieldCount; field++)
            {
                byte bit = (byte)(1 << FieldGroups[field]);
                if ((groups & bit) == 0 || !FieldExists(geometry, field)) continue;
                int maskIndex = start + (field >> 3);
                if ((body[maskIndex] & (1 << (field & 7))) == 0) return false;
            }
            return true;
        }

        private static byte CompleteRefreshGroupMask(byte[] body, int start, QualityGeometry geometry)
        {
            byte complete = 0;
            for (int group = 0; group < GroupCount; group++)
            {
                bool allDirty = true;
                bool hasField = false;
                for (int field = 0; field < BasisAvatarDeltaCompression.FieldCount; field++)
                {
                    if (FieldGroups[field] != group || !FieldExists(geometry, field)) continue;
                    hasField = true;
                    int maskIndex = start + (field >> 3);
                    if ((body[maskIndex] & (1 << (field & 7))) == 0)
                    {
                        allDirty = false;
                        break;
                    }
                }
                if (hasField && allDirty) complete |= (byte)(1 << group);
            }
            return complete;
        }

        private static byte DirtyGroupMask(byte[] body, int start, QualityGeometry geometry)
        {
            byte dirtyGroups = 0;
            for (int field = 0; field < BasisAvatarDeltaCompression.FieldCount; field++)
            {
                if (!FieldExists(geometry, field)) continue;
                int maskIndex = start + (field >> 3);
                if ((body[maskIndex] & (1 << (field & 7))) != 0)
                    dirtyGroups |= (byte)(1 << FieldGroups[field]);
            }
            return dirtyGroups;
        }

        private static byte SelectRecoveryGroups(byte requestedMask, int maxGroups, byte sequence)
        {
            if (requestedMask == 0 || maxGroups <= 0) return 0;
            byte selected = 0;
            int count = 0;
            int start = sequence & (GroupCount - 1);
            for (int i = 0; i < GroupCount && count < maxGroups; i++)
            {
                int group = (start + i) & (GroupCount - 1);
                byte bit = (byte)(1 << group);
                if ((requestedMask & bit) == 0) continue;
                selected |= bit;
                count++;
            }
            return selected;
        }

        private static void CopyDirtyFieldsForGroup(
            byte[] source,
            byte[] destination,
            QualityGeometry geometry,
            byte[] deltaBody,
            int deltaStart,
            int group)
        {
            for (int field = 0; field < BasisAvatarDeltaCompression.FieldCount; field++)
            {
                if (FieldGroups[field] != group || !FieldExists(geometry, field)) continue;
                int maskIndex = deltaStart + (field >> 3);
                if ((deltaBody[maskIndex] & (1 << (field & 7))) == 0) continue;
                CopyField(source, destination, geometry, field);
            }
        }

        private static void CopyGroups(byte[] source, byte[] destination, QualityGeometry geometry, byte groups)
        {
            for (int field = 0; field < BasisAvatarDeltaCompression.FieldCount; field++)
            {
                byte bit = (byte)(1 << FieldGroups[field]);
                if ((groups & bit) == 0 || !FieldExists(geometry, field)) continue;
                CopyField(source, destination, geometry, field);
            }
        }

        private static bool FieldExists(QualityGeometry geometry, int field)
        {
            int endEffector = 1 + BasisBoneRotationCompression.SyncBoneCount + 4;
            return field != endEffector || geometry.EndEffectorBytes > 0;
        }

        private static void CopyField(byte[] source, byte[] destination, QualityGeometry geometry, int field)
        {
            if (field == 0)
            {
                Buffer.BlockCopy(source, 0, destination, 0, geometry.PosBytes);
                return;
            }

            int bone = field - BasisAvatarDeltaCompression.BoneFieldStart;
            if ((uint)bone < BasisBoneRotationCompression.SyncBoneCount)
            {
                int bit = geometry.PosBytes * 8 + geometry.BoneBitOffset[bone];
                int read = bit;
                ulong value = BasisBoneRotationCompression.ReadBits(source, ref read, geometry.BoneWidth[bone]);
                WriteBitsOverwrite(destination, bit, value, geometry.BoneWidth[bone]);
                return;
            }

            GetByteField(geometry, field, out int offset, out int length);
            if (length > 0) Buffer.BlockCopy(source, offset, destination, offset, length);
        }

        private static void ForceFieldDifferent(byte[] baseline, byte[] current, QualityGeometry geometry, int field)
        {
            if (field == 0)
            {
                Buffer.BlockCopy(current, 0, baseline, 0, geometry.PosBytes);
                baseline[0] ^= 1;
                return;
            }

            int bone = field - BasisAvatarDeltaCompression.BoneFieldStart;
            if ((uint)bone < BasisBoneRotationCompression.SyncBoneCount)
            {
                int bit = geometry.PosBytes * 8 + geometry.BoneBitOffset[bone];
                int read = bit;
                ulong value = BasisBoneRotationCompression.ReadBits(current, ref read, geometry.BoneWidth[bone]);
                WriteBitsOverwrite(baseline, bit, value ^ 1UL, geometry.BoneWidth[bone]);
                return;
            }

            GetByteField(geometry, field, out int offset, out int length);
            if (length <= 0) return;
            Buffer.BlockCopy(current, offset, baseline, offset, length);
            baseline[offset] ^= 1;
        }

        private static void GetByteField(QualityGeometry geometry, int field, out int offset, out int length)
        {
            int scale = 1 + BasisBoneRotationCompression.SyncBoneCount;
            if (field == scale)
            {
                offset = geometry.ScaleOffset;
                length = BasisBoneRotationCompression.WriteScale;
            }
            else if (field == scale + 1)
            {
                offset = geometry.BodyRotOffset;
                length = BasisBoneRotationCompression.WriteRotation;
            }
            else if (field == scale + 2)
            {
                offset = geometry.HipsDeltaOffset;
                length = BasisBoneRotationCompression.WriteHipsDelta;
            }
            else if (field == scale + 3)
            {
                offset = geometry.HipsRotOffset;
                length = BasisBoneRotationCompression.WriteHipsRotation;
            }
            else if (field == scale + 4)
            {
                offset = geometry.EndEffectorOffset;
                length = geometry.EndEffectorBytes;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(field));
            }
        }

        private static void InitializeNeutralPayload(byte[] payload, QualityGeometry geometry)
        {
            Array.Clear(payload, 0, payload.Length);
            int bit = geometry.PosBytes * 8;
            for (int bone = 0; bone < geometry.BoneWidth.Length; bone++)
            {
                int componentBits = (geometry.BoneWidth[bone] - 2) / 3;
                ulong midpoint = 1UL << (componentBits - 1);
                ulong identity = 3UL
                    | (midpoint << 2)
                    | (midpoint << (2 + componentBits))
                    | (midpoint << (2 + componentBits * 2));
                WriteBitsOverwrite(payload, bit + geometry.BoneBitOffset[bone], identity, geometry.BoneWidth[bone]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteBitsOverwrite(byte[] destination, int bitPosition, ulong value, int bitCount)
        {
            int bytePosition = bitPosition >> 3;
            int bitInByte = bitPosition & 7;
            int left = bitCount;
            while (left > 0)
            {
                int room = 8 - bitInByte;
                int take = left < room ? left : room;
                int lowMask = (1 << take) - 1;
                int clearMask = lowMask << bitInByte;
                byte chunk = (byte)((int)(value & (ulong)lowMask) << bitInByte);
                destination[bytePosition] = (byte)((destination[bytePosition] & ~clearMask) | chunk);
                value >>= take;
                left -= take;
                bytePosition++;
                bitInByte = 0;
            }
        }
    }
}
