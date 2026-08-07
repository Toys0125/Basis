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
    /// The body is intentionally the ordinary Basis delta body with no extra per-frame bytes:
    /// the refresh schedule is derived from the frame sequence. Refresh groups force every field
    /// in that group dirty, making the shard receiver-independent and safe to apply over a stale
    /// group baseline.
    ///
    /// This class is an experimental protocol primitive. Live negotiation/reset framing is not
    /// selected by the production avatar channel yet.
    /// </summary>
    public static class BasisAvatarDeltaRecoveryV3
    {
        public const int GroupCount = 8;

        public readonly struct Options
        {
            /// <summary>
            /// Number of sequence frames in one recovery cycle. Exactly eight frames in each
            /// cycle carry a refresh shard; the remaining frames carry only the normal exact delta.
            /// Ten matches the current 500 ms keyframe cadence at a 20 Hz armature rate.
            /// </summary>
            public readonly byte RefreshCycleFrames;

            public Options(byte refreshCycleFrames)
            {
                if (refreshCycleFrames < GroupCount || refreshCycleFrames > 32)
                    throw new ArgumentOutOfRangeException(nameof(refreshCycleFrames));
                RefreshCycleFrames = refreshCycleFrames;
            }

            public static Options Default => new Options(8);
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
            => BasisAvatarDeltaCompression.MaxDeltaSize(quality);

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
                Reset();
            }

            public int PayloadSize => _geometry.PayloadSize;
            public int MaxBodySize => BasisAvatarDeltaCompression.MaxDeltaSize(_quality);
            public byte LastRefreshMask { get; private set; }

            public void Reset()
            {
                InitializeNeutralPayload(_baseline, _geometry);
                Buffer.BlockCopy(_baseline, 0, _forcedBaseline, 0, _baseline.Length);
                LastRefreshMask = 0;
            }

            /// <summary>
            /// Builds one exact V3 body. Baseline state commits only after the complete underlying
            /// delta was successfully written.
            /// </summary>
            public int Encode(byte[] payload, byte sequence, byte[] destination, int destinationStart)
            {
                if (payload == null || payload.Length < _geometry.PayloadSize || destination == null || destinationStart < 0)
                    return -1;
                if (destination.Length - destinationStart < MaxBodySize) return -1;

                Buffer.BlockCopy(_baseline, 0, _forcedBaseline, 0, _baseline.Length);
                byte refreshMask = ScheduledRefreshMask(sequence, _options);
                if (refreshMask != 0)
                    ForceGroupsDifferent(_forcedBaseline, payload, _geometry, refreshMask);

                int length = BasisAvatarDeltaCompression.BuildDelta(_forcedBaseline, payload, _quality, destination, destinationStart);
                if (length < 0) return -1;

                if (refreshMask != 0)
                    CopyGroups(payload, _baseline, _geometry, refreshMask);
                LastRefreshMask = refreshMask;
                return length;
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
            public bool HasSequence => _hasSequence;
            public byte LastSequence => _lastSequence;
            public byte ValidGroupMask => _validGroups;
            public bool IsFullySynchronized => _validGroups == 0xFF;

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

                int bodyLength = BasisAvatarDeltaCompression.DeltaBodyLength(source, sourceStart, availableBytes, _quality);
                if (bodyLength < 0 || bodyLength > availableBytes || sourceStart + bodyLength > source.Length) return false;

                byte refreshMask = ScheduledRefreshMask(sequence, _options);
                if (!RefreshGroupsAreComplete(source, sourceStart, bodyLength, _geometry, refreshMask)) return false;

                // Decode against the receiver's currently known group baselines. Reconstructed
                // values for invalid groups may be stale-base garbage and are deliberately ignored
                // unless this packet carries a complete refresh for that group.
                if (!BasisAvatarDeltaCompression.TryApplyDelta(_baseline, source, sourceStart, bodyLength, _quality, _reconstructed))
                    return false;

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

                for (int group = 0; group < GroupCount; group++)
                {
                    byte bit = (byte)(1 << group);
                    if ((refreshMask & bit) != 0)
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
                consumedBytes = bodyLength;
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
