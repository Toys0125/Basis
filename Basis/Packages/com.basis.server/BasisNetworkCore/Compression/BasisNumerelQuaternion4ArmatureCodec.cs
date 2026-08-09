using System;
using System.Runtime.CompilerServices;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Experimental Numerel armature codec that codes all four quaternion components independently.
    /// The input/output payload remains Basis smallest-three so existing capture and benchmark tooling
    /// can compare this representation directly with the current armature codec.
    /// </summary>
    public static class BasisNumerelQuaternion4ArmatureCodec
    {
        private const int ComponentsPerBone = 4;
        private const int StateCount = BasisBoneRotationCompression.SyncBoneCount * ComponentsPerBone;
        private const int MaxPredictedGapFrames = 32;
        private const int MaxZeroRuns = (BasisBoneRotationCompression.SyncBoneCount + 1) / 2;
        private const byte AbsoluteGroupRefreshMagic = 0xD3;

        public readonly struct Options
        {
            public readonly BasisNumerel.Tuning Numerel;
            public readonly bool PreserveSignContinuity;
            public readonly sbyte ComponentBitsAdjustment;
            public readonly bool AdaptivePrecision;
            public readonly byte FixedComponentBits;
            public readonly bool ZeroBoneRleRetainGray;

            public Options(BasisNumerel.Tuning numerel, bool preserveSignContinuity, sbyte componentBitsAdjustment = 0, bool adaptivePrecision = false, byte fixedComponentBits = 0, bool zeroBoneRleRetainGray = false)
            {
                if (componentBitsAdjustment < -3 || componentBitsAdjustment > 4)
                    throw new ArgumentOutOfRangeException(nameof(componentBitsAdjustment));
                if (fixedComponentBits != 0 && (fixedComponentBits < 3 || fixedComponentBits > 30))
                    throw new ArgumentOutOfRangeException(nameof(fixedComponentBits));
                Numerel = numerel;
                PreserveSignContinuity = preserveSignContinuity;
                ComponentBitsAdjustment = componentBitsAdjustment;
                AdaptivePrecision = adaptivePrecision;
                FixedComponentBits = fixedComponentBits;
                ZeroBoneRleRetainGray = zeroBoneRleRetainGray;
            }

            public static Options Upstream => new Options(BasisNumerel.Tuning.Reference, false, 0);
            public static Options UpstreamContinuous => new Options(BasisNumerel.Tuning.Reference, true, 0);
            public static Options UpstreamContinuousMinus1 => new Options(BasisNumerel.Tuning.Reference, true, -1);
            public static Options UpstreamContinuousMinus2 => new Options(BasisNumerel.Tuning.Reference, true, -2);
            public static Options UpstreamContinuousPlus1 => new Options(BasisNumerel.Tuning.Reference, true, 1);
            public static Options UpstreamContinuousPlus2 => new Options(BasisNumerel.Tuning.Reference, true, 2);
            public static Options UpstreamContinuousAdaptive => new Options(BasisNumerel.Tuning.Reference, true, 0, true);
            public static Options UpstreamContinuous12Bit => new Options(BasisNumerel.Tuning.Reference, true, fixedComponentBits: 12);
            public static Options SquareRoot04Continuous12Bit => new Options(BasisNumerel.Tuning.SquareRoot04Reference, true, fixedComponentBits: 12);
            public static Options NearestSquareRootContinuous12Bit => new Options(BasisNumerel.Tuning.NearestSquareRootReference, true, fixedComponentBits: 12);

            public static Options Power1Continuous16Bit => new Options(BasisNumerel.Tuning.Power1Reference, true, fixedComponentBits: 16);
            public static Options Power1_5Continuous16Bit => new Options(BasisNumerel.Tuning.Power1_5Reference, true, fixedComponentBits: 16);
            public static Options Power2Continuous16Bit => new Options(BasisNumerel.Tuning.Power2Reference, true, fixedComponentBits: 16);
            public static Options Power2RleGrayContinuous16Bit => new Options(BasisNumerel.Tuning.Power2Reference, true, fixedComponentBits: 16, zeroBoneRleRetainGray: true);
            /// <summary>
            /// Rotation stream used by the current hybrid experiment. Exact auxiliary fields,
            /// passive absolute refresh, requested repair/bootstrap, and playout scheduling are
            /// layered outside this scalar codec.
            /// </summary>
            public static Options Power2HybridRotation16Bit => Power2RleGrayContinuous16Bit;
            public static Options Power2_5Continuous16Bit => new Options(BasisNumerel.Tuning.Power2_5Reference, true, fixedComponentBits: 16);
            public static Options Power3Continuous16Bit => new Options(BasisNumerel.Tuning.Power3Reference, true, fixedComponentBits: 16);
            public static Options Power4Continuous16Bit => new Options(BasisNumerel.Tuning.Power4Reference, true, fixedComponentBits: 16);
            public static Options Power5Continuous16Bit => new Options(BasisNumerel.Tuning.Power5Reference, true, fixedComponentBits: 16);
        }

        public sealed class Encoder
        {
            private readonly BasisAvatarBitPacking.BitQuality _quality;
            private readonly Options _options;
            private readonly byte[] _bpc;
            private readonly int[] _componentBits;
            private readonly int _maxBodySize;
            private BasisNumerel.TxState[] _states;
            private BasisNumerel.TxState[] _scratchStates;
            private float[] _previousQuaternion;
            private float[] _scratchPreviousQuaternion;
            private readonly byte[] _lastScalarBits = new byte[StateCount];
            private readonly byte[] _scratchScalarBits = new byte[StateCount];
            private readonly uint[] _frameComponents = new uint[StateCount];
            private readonly byte[] _zeroBones = new byte[BasisBoneRotationCompression.SyncBoneCount];
            private readonly byte[] _zeroRunStarts = new byte[MaxZeroRuns];
            private readonly byte[] _zeroRunLengths = new byte[MaxZeroRuns];
            private readonly byte[] _refreshNegate = new byte[BasisBoneRotationCompression.SyncBoneCount];
            private readonly int _positionBytes;
            private readonly int _rotationBytes;
            private readonly int _tailOffset;
            private readonly int _tailBytes;
            private readonly int _payloadBytes;

            public Encoder(BasisAvatarBitPacking.BitQuality quality, Options options)
            {
                _quality = quality;
                _options = options;
                _bpc = BasisBoneRotationCompression.GetBpcTable(quality);
                _componentBits = BuildComponentBits(_bpc, options);
                _maxBodySize = GetMaxBodySize(quality, options);
                _positionBytes = BasisAvatarBitPacking.PositionBytes(quality);
                _rotationBytes = BasisBoneRotationCompression.RotationBytes(quality);
                _tailOffset = _positionBytes + _rotationBytes;
                _payloadBytes = BasisBoneRotationCompression.ConvertToSize(quality);
                _tailBytes = _payloadBytes - _tailOffset;
                _states = new BasisNumerel.TxState[StateCount];
                _scratchStates = new BasisNumerel.TxState[StateCount];
                _previousQuaternion = new float[StateCount];
                _scratchPreviousQuaternion = new float[StateCount];
                Reset();
            }

            public int PayloadSize => _payloadBytes;
            public int MaxBodySize => _maxBodySize;
            public int MaxRotationBodySize => GetMaxRotationBodySize(_quality, _options);
            public int LastArmatureBits { get; private set; }
            public byte[] LastScalarBits => _lastScalarBits;
            public bool LastUsedZeroBoneRle { get; private set; }
            public int LastZeroBoneCount { get; private set; }
            public int LastZeroRunCount { get; private set; }
            public int LastZeroBoneRleMetadataBits { get; private set; }
            public int LastZeroBoneRleNetSavedBits { get; private set; }

            public void Reset()
            {
                int state = 0;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int bits = _componentBits[bone];
                    uint midpoint = QuantizeComponent(0f, bits);
                    uint one = QuantizeComponent(1f, bits);
                    _states[state].Reset(midpoint);
                    _scratchStates[state].Reset(midpoint);
                    _previousQuaternion[state] = 0f;
                    _scratchPreviousQuaternion[state++] = 0f;
                    _states[state].Reset(midpoint);
                    _scratchStates[state].Reset(midpoint);
                    _previousQuaternion[state] = 0f;
                    _scratchPreviousQuaternion[state++] = 0f;
                    _states[state].Reset(midpoint);
                    _scratchStates[state].Reset(midpoint);
                    _previousQuaternion[state] = 0f;
                    _scratchPreviousQuaternion[state++] = 0f;
                    _states[state].Reset(one);
                    _scratchStates[state].Reset(one);
                    _previousQuaternion[state] = 1f;
                    _scratchPreviousQuaternion[state++] = 1f;
                }
                LastArmatureBits = 0;
                LastUsedZeroBoneRle = false;
                LastZeroBoneCount = 0;
                LastZeroRunCount = 0;
                LastZeroBoneRleMetadataBits = 0;
                LastZeroBoneRleNetSavedBits = 0;
                Array.Clear(_lastScalarBits, 0, _lastScalarBits.Length);
                Array.Clear(_scratchScalarBits, 0, _scratchScalarBits.Length);
                Array.Clear(_frameComponents, 0, _frameComponents.Length);
                Array.Clear(_zeroBones, 0, _zeroBones.Length);
                Array.Clear(_refreshNegate, 0, _refreshNegate.Length);
            }

            public int Encode(byte[] payload, byte sequence, byte[] destination, int destinationStart)
                => EncodeCore(payload, sequence, destination, destinationStart, includeAuxiliary: true);

            /// <summary>
            /// Encodes only the variable-length Quaternion-4 Numerel rotation stream. The caller
            /// can append an independent exact auxiliary representation and recovery side data.
            /// State evolution is identical to <see cref="Encode"/> for the rotation portion.
            /// </summary>
            public int EncodeRotations(byte[] payload, byte sequence, byte[] destination, int destinationStart)
                => EncodeCore(payload, sequence, destination, destinationStart, includeAuxiliary: false);

            private int EncodeCore(byte[] payload, byte sequence, byte[] destination, int destinationStart, bool includeAuxiliary)
            {
                if (payload == null || payload.Length < _payloadBytes || destination == null || destinationStart < 0)
                    return -1;
                int maxRequired = includeAuxiliary ? MaxBodySize : MaxRotationBodySize;
                if (destination.Length - destinationStart < maxRequired) return -1;

                Array.Copy(_states, _scratchStates, StateCount);
                Array.Copy(_previousQuaternion, _scratchPreviousQuaternion, StateCount);
                Array.Clear(destination, destinationStart, MaxBodySize);

                int bitPosition = destinationStart * 8;
                int bitLimit = (destinationStart + MaxBodySize) * 8;
                int sourceBit = _positionBytes * 8;
                int stateIndex = 0;

                if (!_options.ZeroBoneRleRetainGray)
                {
                    // Keep the existing one-pass path untouched for non-RLE controls.
                    for (int bone = 0; bone < _bpc.Length; bone++)
                    {
                        int sourceBits = _bpc[bone];
                        int width = 2 + sourceBits * 3;
                        ulong packed = BasisBoneRotationCompression.ReadBits(payload, ref sourceBit, width);
                        BasisBoneRotationCompression.DecodeSmallestThree(
                            packed, sourceBits,
                            out float qx, out float qy, out float qz, out float qw,
                            BasisBoneRotationCompression.MAX_COMPONENT[bone]);

                        if (_options.PreserveSignContinuity)
                        {
                            float dot = qx * _scratchPreviousQuaternion[stateIndex]
                                + qy * _scratchPreviousQuaternion[stateIndex + 1]
                                + qz * _scratchPreviousQuaternion[stateIndex + 2]
                                + qw * _scratchPreviousQuaternion[stateIndex + 3];
                            if (dot < 0f)
                            {
                                qx = -qx;
                                qy = -qy;
                                qz = -qz;
                                qw = -qw;
                            }
                        }

                        _scratchPreviousQuaternion[stateIndex] = qx;
                        _scratchPreviousQuaternion[stateIndex + 1] = qy;
                        _scratchPreviousQuaternion[stateIndex + 2] = qz;
                        _scratchPreviousQuaternion[stateIndex + 3] = qw;

                        int componentBits = _componentBits[bone];
                        int grayBit = BasisNumerel.GrayScramble(sequence % componentBits, componentBits);
                        uint x = QuantizeComponent(qx, componentBits);
                        uint y = QuantizeComponent(qy, componentBits);
                        uint z = QuantizeComponent(qz, componentBits);
                        uint w = QuantizeComponent(qw, componentBits);

                        if (!TryEncodeScalar(ref _scratchStates[stateIndex], x, grayBit, componentBits, destination, ref bitPosition, bitLimit, stateIndex++)
                            || !TryEncodeScalar(ref _scratchStates[stateIndex], y, grayBit, componentBits, destination, ref bitPosition, bitLimit, stateIndex++)
                            || !TryEncodeScalar(ref _scratchStates[stateIndex], z, grayBit, componentBits, destination, ref bitPosition, bitLimit, stateIndex++)
                            || !TryEncodeScalar(ref _scratchStates[stateIndex], w, grayBit, componentBits, destination, ref bitPosition, bitLimit, stateIndex++))
                            return -1;
                    }

                    LastUsedZeroBoneRle = false;
                    LastZeroBoneCount = 0;
                    LastZeroRunCount = 0;
                    LastZeroBoneRleMetadataBits = 0;
                    LastZeroBoneRleNetSavedBits = 0;
                }
                else
                {
                    int zeroBoneCount = 0;
                    Array.Clear(_zeroBones, 0, _zeroBones.Length);

                    // First pass computes the exact quantized values and finds bones for which all
                    // four Numerel components would emit the one-bit zero Golomb symbol.
                    for (int bone = 0; bone < _bpc.Length; bone++)
                    {
                        int sourceBits = _bpc[bone];
                        int width = 2 + sourceBits * 3;
                        ulong packed = BasisBoneRotationCompression.ReadBits(payload, ref sourceBit, width);
                        BasisBoneRotationCompression.DecodeSmallestThree(
                            packed, sourceBits,
                            out float qx, out float qy, out float qz, out float qw,
                            BasisBoneRotationCompression.MAX_COMPONENT[bone]);

                        if (_options.PreserveSignContinuity)
                        {
                            float dot = qx * _scratchPreviousQuaternion[stateIndex]
                                + qy * _scratchPreviousQuaternion[stateIndex + 1]
                                + qz * _scratchPreviousQuaternion[stateIndex + 2]
                                + qw * _scratchPreviousQuaternion[stateIndex + 3];
                            if (dot < 0f)
                            {
                                qx = -qx;
                                qy = -qy;
                                qz = -qz;
                                qw = -qw;
                            }
                        }

                        _scratchPreviousQuaternion[stateIndex] = qx;
                        _scratchPreviousQuaternion[stateIndex + 1] = qy;
                        _scratchPreviousQuaternion[stateIndex + 2] = qz;
                        _scratchPreviousQuaternion[stateIndex + 3] = qw;

                        int componentBits = _componentBits[bone];
                        uint x = QuantizeComponent(qx, componentBits);
                        uint y = QuantizeComponent(qy, componentBits);
                        uint z = QuantizeComponent(qz, componentBits);
                        uint w = QuantizeComponent(qw, componentBits);
                        _frameComponents[stateIndex] = x;
                        _frameComponents[stateIndex + 1] = y;
                        _frameComponents[stateIndex + 2] = z;
                        _frameComponents[stateIndex + 3] = w;

                        if (BasisNumerel.IsCompressedZero(_scratchStates[stateIndex].RemoteEstimate, x, componentBits, false, _options.Numerel)
                            && BasisNumerel.IsCompressedZero(_scratchStates[stateIndex + 1].RemoteEstimate, y, componentBits, false, _options.Numerel)
                            && BasisNumerel.IsCompressedZero(_scratchStates[stateIndex + 2].RemoteEstimate, z, componentBits, false, _options.Numerel)
                            && BasisNumerel.IsCompressedZero(_scratchStates[stateIndex + 3].RemoteEstimate, w, componentBits, false, _options.Numerel))
                        {
                            _zeroBones[bone] = 1;
                            zeroBoneCount++;
                        }
                        stateIndex += ComponentsPerBone;
                    }

                    int zeroRunCount = 0;
                    for (int bone = 0; bone < _zeroBones.Length;)
                    {
                        if (_zeroBones[bone] == 0)
                        {
                            bone++;
                            continue;
                        }

                        int start = bone;
                        while (bone < _zeroBones.Length && _zeroBones[bone] != 0) bone++;
                        int length = bone - start;
                        _zeroRunStarts[zeroRunCount] = checked((byte)start);
                        _zeroRunLengths[zeroRunCount] = checked((byte)length);
                        zeroRunCount++;
                    }

                    int rleMetadataBits = 0;
                    if (zeroRunCount > 0)
                    {
                        rleMetadataBits = UnsignedExpGolombBitCount((uint)(zeroRunCount - 1));
                        int previousEnd = 0;
                        for (int run = 0; run < zeroRunCount; run++)
                        {
                            int start = _zeroRunStarts[run];
                            int length = _zeroRunLengths[run];
                            int gap = start - previousEnd;
                            rleMetadataBits += UnsignedExpGolombBitCount((uint)gap);
                            rleMetadataBits += UnsignedExpGolombBitCount((uint)(length - 1));
                            previousEnd = start + length;
                        }
                    }

                    // Each zero bone removes four one-bit Golomb-zero markers while preserving
                    // all four Gray bits. Select RLE only when those savings exceed metadata.
                    bool useRle = zeroRunCount > 0
                        && zeroBoneCount * ComponentsPerBone > rleMetadataBits + 1;

                    if (!TryWriteMsbBits(destination, ref bitPosition, bitLimit, useRle ? 1u : 0u, 1)) return -1;
                    if (useRle)
                    {
                        if (!TryWriteUnsignedExpGolomb(destination, ref bitPosition, bitLimit, (uint)(zeroRunCount - 1))) return -1;
                        int previousEnd = 0;
                        for (int run = 0; run < zeroRunCount; run++)
                        {
                            int start = _zeroRunStarts[run];
                            int length = _zeroRunLengths[run];
                            int gap = start - previousEnd;
                            if (!TryWriteUnsignedExpGolomb(destination, ref bitPosition, bitLimit, (uint)gap)
                                || !TryWriteUnsignedExpGolomb(destination, ref bitPosition, bitLimit, (uint)(length - 1)))
                                return -1;
                            previousEnd = start + length;
                        }
                    }

                    stateIndex = 0;
                    for (int bone = 0; bone < _bpc.Length; bone++)
                    {
                        int componentBits = _componentBits[bone];
                        int grayBit = BasisNumerel.GrayScramble(sequence % componentBits, componentBits);
                        bool zeroBone = useRle && _zeroBones[bone] != 0;

                        for (int component = 0; component < ComponentsPerBone; component++, stateIndex++)
                        {
                            uint value = _frameComponents[stateIndex];
                            if (zeroBone)
                            {
                                if (!TryEncodeZeroGrayScalar(ref _scratchStates[stateIndex], value, grayBit, componentBits,
                                    destination, ref bitPosition, bitLimit, stateIndex))
                                    return -1;
                            }
                            else if (!TryEncodeScalar(ref _scratchStates[stateIndex], value, grayBit, componentBits,
                                destination, ref bitPosition, bitLimit, stateIndex))
                            {
                                return -1;
                            }
                        }
                    }

                    LastUsedZeroBoneRle = useRle;
                    LastZeroBoneCount = zeroBoneCount;
                    LastZeroRunCount = zeroRunCount;
                    LastZeroBoneRleMetadataBits = 1 + (useRle ? rleMetadataBits : 0);
                    LastZeroBoneRleNetSavedBits = useRle
                        ? zeroBoneCount * ComponentsPerBone - rleMetadataBits - 1
                        : -1;
                }

                int armatureBits = bitPosition - destinationStart * 8;
                int bodyOffset = (bitPosition + 7) >> 3;
                if (includeAuxiliary)
                {
                    int requiredEnd = bodyOffset + _positionBytes + _tailBytes;
                    if (requiredEnd > destinationStart + MaxBodySize || requiredEnd > destination.Length) return -1;

                    Buffer.BlockCopy(payload, 0, destination, bodyOffset, _positionBytes);
                    bodyOffset += _positionBytes;
                    Buffer.BlockCopy(payload, _tailOffset, destination, bodyOffset, _tailBytes);
                    bodyOffset += _tailBytes;
                }

                BasisNumerel.TxState[] oldStates = _states;
                _states = _scratchStates;
                _scratchStates = oldStates;
                float[] oldPrevious = _previousQuaternion;
                _previousQuaternion = _scratchPreviousQuaternion;
                _scratchPreviousQuaternion = oldPrevious;
                LastArmatureBits = armatureBits;
                Buffer.BlockCopy(_scratchScalarBits, 0, _lastScalarBits, 0, StateCount);
                return bodyOffset - destinationStart;
            }

            /// <summary>
            /// Returns the byte size of one absolute recovery-group record. The record carries the
            /// exact Basis smallest-three value for every bone in the group plus one q/-q sign bit
            /// per bone so the receiver can seed the same Quaternion-4 Numerel representation.
            /// </summary>
            public int GetAbsoluteGroupRefreshSize(byte[] boneGroups, byte groupId, byte groupCount)
            {
                if (!ValidateBoneGroups(boneGroups, groupCount) || groupId >= groupCount) return -1;
                int bits = 24; // magic + group-count + group-id
                bool hasBone = false;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    if (boneGroups[bone] != groupId) continue;
                    hasBone = true;
                    bits += 1 + 2 + 3 * _bpc[bone];
                }
                return hasBone ? ((bits + 7) >> 3) : -1;
            }

            /// <summary>
            /// Appends one exact absolute rotation-group record and rebases the shared sender
            /// predictor for that group only after the complete record has been written. This is
            /// the passive shared-stream recovery transition: receivers that get the record seed
            /// exactly the same fixed16 Quaternion-4 predictor as the sender for the next frame.
            /// </summary>
            public int EncodeAbsoluteGroupRefresh(
                byte[] payload,
                byte[] boneGroups,
                byte groupId,
                byte groupCount,
                byte[] destination,
                int destinationStart)
            {
                if (payload == null || payload.Length < _payloadBytes || destination == null || destinationStart < 0)
                    return -1;
                int recordSize = GetAbsoluteGroupRefreshSize(boneGroups, groupId, groupCount);
                if (recordSize <= 0 || destination.Length - destinationStart < recordSize) return -1;

                Array.Clear(destination, destinationStart, recordSize);
                destination[destinationStart] = AbsoluteGroupRefreshMagic;
                destination[destinationStart + 1] = groupCount;
                destination[destinationStart + 2] = groupId;

                int bitPosition = (destinationStart + 3) * 8;
                int sourceBit = _positionBytes * 8;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int sourceBits = _bpc[bone];
                    int width = 2 + sourceBits * 3;
                    ulong packed = BasisBoneRotationCompression.ReadBits(payload, ref sourceBit, width);
                    if (boneGroups[bone] != groupId) continue;

                    BasisBoneRotationCompression.DecodeSmallestThree(
                        packed, sourceBits,
                        out float qx, out float qy, out float qz, out float qw,
                        BasisBoneRotationCompression.MAX_COMPONENT[bone]);
                    int stateIndex = bone * ComponentsPerBone;
                    float dot = qx * _previousQuaternion[stateIndex]
                        + qy * _previousQuaternion[stateIndex + 1]
                        + qz * _previousQuaternion[stateIndex + 2]
                        + qw * _previousQuaternion[stateIndex + 3];
                    bool negate = dot < 0f;
                    _refreshNegate[bone] = negate ? (byte)1 : (byte)0;

                    BasisBoneRotationCompression.WriteBits(destination, bitPosition, negate ? 1UL : 0UL, 1);
                    bitPosition += 1;
                    BasisBoneRotationCompression.WriteBits(destination, bitPosition, packed, width);
                    bitPosition += width;
                }

                if (((bitPosition + 7) >> 3) != destinationStart + recordSize) return -1;

                // Commit the predictor rebase only after the record is complete.
                sourceBit = _positionBytes * 8;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int sourceBits = _bpc[bone];
                    int width = 2 + sourceBits * 3;
                    ulong packed = BasisBoneRotationCompression.ReadBits(payload, ref sourceBit, width);
                    if (boneGroups[bone] != groupId) continue;

                    BasisBoneRotationCompression.DecodeSmallestThree(
                        packed, sourceBits,
                        out float qx, out float qy, out float qz, out float qw,
                        BasisBoneRotationCompression.MAX_COMPONENT[bone]);
                    if (_refreshNegate[bone] != 0)
                    {
                        qx = -qx; qy = -qy; qz = -qz; qw = -qw;
                    }
                    RebaseBone(bone, qx, qy, qz, qw);
                }
                return recordSize;
            }

            private void RebaseBone(int bone, float qx, float qy, float qz, float qw)
            {
                int stateIndex = bone * ComponentsPerBone;
                int componentBits = _componentBits[bone];
                uint x = QuantizeComponent(qx, componentBits);
                uint y = QuantizeComponent(qy, componentBits);
                uint z = QuantizeComponent(qz, componentBits);
                uint w = QuantizeComponent(qw, componentBits);

                _states[stateIndex].Reset(x);
                _states[stateIndex + 1].Reset(y);
                _states[stateIndex + 2].Reset(z);
                _states[stateIndex + 3].Reset(w);
                _scratchStates[stateIndex].Reset(x);
                _scratchStates[stateIndex + 1].Reset(y);
                _scratchStates[stateIndex + 2].Reset(z);
                _scratchStates[stateIndex + 3].Reset(w);

                _previousQuaternion[stateIndex] = qx;
                _previousQuaternion[stateIndex + 1] = qy;
                _previousQuaternion[stateIndex + 2] = qz;
                _previousQuaternion[stateIndex + 3] = qw;
                _scratchPreviousQuaternion[stateIndex] = qx;
                _scratchPreviousQuaternion[stateIndex + 1] = qy;
                _scratchPreviousQuaternion[stateIndex + 2] = qz;
                _scratchPreviousQuaternion[stateIndex + 3] = qw;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool TryEncodeScalar(ref BasisNumerel.TxState state, uint value, int grayBit, int componentBits,
                byte[] destination, ref int bitPosition, int bitLimit, int scalarIndex)
            {
                int before = bitPosition;
                if (!BasisNumerel.TryEncode(ref state, value, grayBit, componentBits, false, _options.Numerel,
                    destination, ref bitPosition, bitLimit))
                    return false;
                _scratchScalarBits[scalarIndex] = checked((byte)(bitPosition - before));
                return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool TryEncodeZeroGrayScalar(ref BasisNumerel.TxState state, uint value, int grayBit, int componentBits,
                byte[] destination, ref int bitPosition, int bitLimit, int scalarIndex)
            {
                int before = bitPosition;
                if (!BasisNumerel.TryEncodeZeroGray(ref state, value, grayBit, componentBits, false, _options.Numerel,
                    destination, ref bitPosition, bitLimit))
                    return false;
                _scratchScalarBits[scalarIndex] = checked((byte)(bitPosition - before));
                return true;
            }
        }

        public sealed class Decoder
        {
            private readonly BasisAvatarBitPacking.BitQuality _quality;
            private readonly Options _options;
            private readonly byte[] _bpc;
            private readonly int[] _componentBits;
            private BasisNumerel.RxState[] _states;
            private BasisNumerel.RxState[] _scratchStates;
            private byte[] _payload;
            private byte[] _payloadScratch;
            private readonly byte[] _zeroBonesScratch = new byte[BasisBoneRotationCompression.SyncBoneCount];
            private readonly byte[] _refreshNegate = new byte[BasisBoneRotationCompression.SyncBoneCount];
            private readonly ulong[] _refreshPacked = new ulong[BasisBoneRotationCompression.SyncBoneCount];
            private readonly int[] _boneBitOffsets = new int[BasisBoneRotationCompression.SyncBoneCount];
            private readonly int _positionBytes;
            private readonly int _rotationBytes;
            private readonly int _tailOffset;
            private readonly int _tailBytes;
            private readonly int _payloadBytes;
            private bool _hasSequence;
            private byte _lastSequence;
            private byte _deadlinePredictionsSinceDecode;

            public Decoder(BasisAvatarBitPacking.BitQuality quality, Options options)
            {
                _quality = quality;
                _options = options;
                _bpc = BasisBoneRotationCompression.GetBpcTable(quality);
                _componentBits = BuildComponentBits(_bpc, options);
                BasisBoneRotationCompression.ComputeBitOffsets(_bpc, _boneBitOffsets);
                _positionBytes = BasisAvatarBitPacking.PositionBytes(quality);
                _rotationBytes = BasisBoneRotationCompression.RotationBytes(quality);
                _tailOffset = _positionBytes + _rotationBytes;
                _payloadBytes = BasisBoneRotationCompression.ConvertToSize(quality);
                _tailBytes = _payloadBytes - _tailOffset;
                _states = new BasisNumerel.RxState[StateCount];
                _scratchStates = new BasisNumerel.RxState[StateCount];
                _payload = new byte[_payloadBytes];
                _payloadScratch = new byte[_payloadBytes];
                Reset();
            }

            public int PayloadSize => _payloadBytes;
            public bool HasSequence => _hasSequence;
            public byte LastSequence => _lastSequence;
            public int DeadlinePredictionsSinceDecode => _deadlinePredictionsSinceDecode;
            public int LastArmatureBits { get; private set; }

            public void CopyDisplayedPose(byte[] outputPayload)
            {
                if (outputPayload == null || outputPayload.Length < _payloadBytes)
                    throw new ArgumentException("Output payload is too small.", nameof(outputPayload));
                Buffer.BlockCopy(_payload, 0, outputPayload, 0, _payloadBytes);
            }

            /// <summary>
            /// Resets all predictor/sequence state so the stream must bootstrap again, while keeping
            /// the caller-provided pose as the held visual output. This is used only as a defensive
            /// outer-transaction rollback if a later hybrid side-body commit unexpectedly fails.
            /// </summary>
            public void ResetForBootstrapKeepingDisplayedPose(byte[] displayedPayload)
            {
                if (displayedPayload == null || displayedPayload.Length < _payloadBytes)
                    throw new ArgumentException("Displayed payload is too small.", nameof(displayedPayload));
                Reset();
                Buffer.BlockCopy(displayedPayload, 0, _payload, 0, _payloadBytes);
                Buffer.BlockCopy(displayedPayload, 0, _payloadScratch, 0, _payloadBytes);
            }

            /// <summary>
            /// Replaces only the held non-armature bytes in the decoder's displayed state. The
            /// separated hybrid calls this after applying its exact auxiliary delta so a later
            /// deadline prediction holds the latest exact position/scale/hips state.
            /// </summary>
            public void SyncAuxiliaryFromPayload(byte[] payload)
            {
                if (payload == null || payload.Length < _payloadBytes)
                    throw new ArgumentException("Payload is too small.", nameof(payload));
                Buffer.BlockCopy(payload, 0, _payload, 0, _positionBytes);
                Buffer.BlockCopy(payload, 0, _payloadScratch, 0, _positionBytes);
                Buffer.BlockCopy(payload, _tailOffset, _payload, _tailOffset, _tailBytes);
                Buffer.BlockCopy(payload, _tailOffset, _payloadScratch, _tailOffset, _tailBytes);
            }

            /// <summary>
            /// Advances the Numerel rotation predictor at the playout deadline for one expected
            /// frame that has not arrived. This mirrors upstream's immediate NumerelApplyDelta
            /// behavior. Auxiliary Basis fields are intentionally held at their last received
            /// values; only the 51 rotation outputs are regenerated from predicted scalar state.
            ///
            /// The caller must pass exactly the next expected sequence. Advancing the sequence here
            /// makes a later copy of that missed packet stale and prevents the next received packet
            /// from applying the same missing frame a second time.
            /// </summary>
            public bool TryAdvanceDeadline(byte expectedSequence, byte[] outputPayload)
            {
                if (outputPayload == null || outputPayload.Length < _payloadBytes) return false;
                if (!_hasSequence) return false;
                if (expectedSequence != unchecked((byte)(_lastSequence + 1))) return false;
                if (_deadlinePredictionsSinceDecode >= MaxPredictedGapFrames) return false;

                int state = 0;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int componentBits = _componentBits[bone];
                    for (int component = 0; component < ComponentsPerBone; component++)
                        BasisNumerel.ApplyLastDelta(ref _states[state++], componentBits, false);
                }

                WriteArmatureFromStates(_states, _payload);
                _lastSequence = expectedSequence;
                _deadlinePredictionsSinceDecode++;
                Buffer.BlockCopy(_payload, 0, outputPayload, 0, _payloadBytes);
                return true;
            }

            public void Reset()
            {
                int state = 0;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int bits = _componentBits[bone];
                    uint midpoint = QuantizeComponent(0f, bits);
                    uint one = QuantizeComponent(1f, bits);
                    _states[state].Reset(midpoint);
                    _scratchStates[state++].Reset(midpoint);
                    _states[state].Reset(midpoint);
                    _scratchStates[state++].Reset(midpoint);
                    _states[state].Reset(midpoint);
                    _scratchStates[state++].Reset(midpoint);
                    _states[state].Reset(one);
                    _scratchStates[state++].Reset(one);
                }
                Array.Clear(_payload, 0, _payload.Length);
                Array.Clear(_payloadScratch, 0, _payloadScratch.Length);
                InitializeNeutralArmature(_payload, _positionBytes, _bpc);
                InitializeNeutralArmature(_payloadScratch, _positionBytes, _bpc);
                _hasSequence = false;
                _lastSequence = 0;
                _deadlinePredictionsSinceDecode = 0;
                LastArmatureBits = 0;
                Array.Clear(_zeroBonesScratch, 0, _zeroBonesScratch.Length);
                Array.Clear(_refreshNegate, 0, _refreshNegate.Length);
                Array.Clear(_refreshPacked, 0, _refreshPacked.Length);
            }

            public bool TryDecode(byte[] source, int sourceStart, int availableBytes, byte sequence, byte[] outputPayload, out int consumedBytes)
                => TryDecodeCore(source, sourceStart, availableBytes, sequence, outputPayload, out consumedBytes, includeAuxiliary: true);

            /// <summary>
            /// Decodes only the variable-length Quaternion-4 Numerel rotation stream. Existing
            /// auxiliary bytes in the decoder's displayed payload are held unchanged.
            /// </summary>
            public bool TryDecodeRotations(byte[] source, int sourceStart, int availableBytes, byte sequence, byte[] outputPayload, out int consumedBytes)
                => TryDecodeCore(source, sourceStart, availableBytes, sequence, outputPayload, out consumedBytes, includeAuxiliary: false);

            private bool TryDecodeCore(byte[] source, int sourceStart, int availableBytes, byte sequence, byte[] outputPayload, out int consumedBytes, bool includeAuxiliary)
            {
                consumedBytes = 0;
                if (source == null || outputPayload == null || outputPayload.Length < _payloadBytes) return false;
                if (sourceStart < 0 || availableBytes < 0 || sourceStart + availableBytes > source.Length) return false;

                int forward = 1;
                if (_hasSequence)
                {
                    forward = (byte)(sequence - _lastSequence);
                    if (forward == 0 || forward >= 128) return false;
                    if (_deadlinePredictionsSinceDecode + forward - 1 > MaxPredictedGapFrames) return false;
                }

                Array.Copy(_states, _scratchStates, StateCount);
                Buffer.BlockCopy(_payload, 0, _payloadScratch, 0, _payloadBytes);

                if (forward > 1)
                {
                    for (int missing = 1; missing < forward; missing++)
                    {
                        int state = 0;
                        for (int bone = 0; bone < _bpc.Length; bone++)
                        {
                            int componentBits = _componentBits[bone];
                            for (int component = 0; component < ComponentsPerBone; component++)
                                BasisNumerel.ApplyLastDelta(ref _scratchStates[state++], componentBits, false);
                        }
                    }
                }

                int bitPosition = sourceStart * 8;
                int bitLimit = (sourceStart + availableBytes) * 8;
                int destinationBit = _positionBytes * 8;
                int stateIndex = 0;
                bool useRle = false;
                Array.Clear(_zeroBonesScratch, 0, _zeroBonesScratch.Length);

                if (_options.ZeroBoneRleRetainGray)
                {
                    if (!TryReadMsbBits(source, ref bitPosition, bitLimit, 1, out uint mode)) return false;
                    useRle = mode != 0;
                    if (useRle)
                    {
                        if (!TryReadUnsignedExpGolomb(source, ref bitPosition, bitLimit, out uint encodedRunCount)) return false;
                        if (encodedRunCount >= MaxZeroRuns) return false;
                        int runCount = checked((int)encodedRunCount + 1);

                        int previousEnd = 0;
                        for (int run = 0; run < runCount; run++)
                        {
                            if (!TryReadUnsignedExpGolomb(source, ref bitPosition, bitLimit, out uint encodedGap)
                                || !TryReadUnsignedExpGolomb(source, ref bitPosition, bitLimit, out uint encodedLength))
                                return false;
                            if (encodedGap > BasisBoneRotationCompression.SyncBoneCount
                                || encodedLength >= BasisBoneRotationCompression.SyncBoneCount)
                                return false;

                            int gap = checked((int)encodedGap);
                            int start = previousEnd + gap;
                            int length = checked((int)encodedLength + 1);
                            int end = start + length;
                            if (start < 0 || start >= _zeroBonesScratch.Length || end > _zeroBonesScratch.Length) return false;
                            if (run > 0 && gap == 0) return false; // adjacent runs are non-canonical
                            for (int bone = start; bone < end; bone++) _zeroBonesScratch[bone] = 1;
                            previousEnd = end;
                        }
                    }
                }

                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int componentBits = _componentBits[bone];
                    int grayBit = BasisNumerel.GrayScramble(sequence % componentBits, componentBits);
                    bool zeroBone = useRle && _zeroBonesScratch[bone] != 0;
                    uint x, y, z, w;
                    if (zeroBone)
                    {
                        if (!BasisNumerel.TryDecodeZeroGray(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _options.Numerel, source, ref bitPosition, bitLimit, out x)
                            || !BasisNumerel.TryDecodeZeroGray(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _options.Numerel, source, ref bitPosition, bitLimit, out y)
                            || !BasisNumerel.TryDecodeZeroGray(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _options.Numerel, source, ref bitPosition, bitLimit, out z)
                            || !BasisNumerel.TryDecodeZeroGray(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _options.Numerel, source, ref bitPosition, bitLimit, out w))
                            return false;
                    }
                    else
                    {
                        if (!BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _options.Numerel, source, ref bitPosition, bitLimit, out x)
                            || !BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _options.Numerel, source, ref bitPosition, bitLimit, out y)
                            || !BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _options.Numerel, source, ref bitPosition, bitLimit, out z)
                            || !BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _options.Numerel, source, ref bitPosition, bitLimit, out w))
                            return false;
                    }

                    float qx = DequantizeComponent(x, componentBits);
                    float qy = DequantizeComponent(y, componentBits);
                    float qz = DequantizeComponent(z, componentBits);
                    float qw = DequantizeComponent(w, componentBits);
                    Normalize(ref qx, ref qy, ref qz, ref qw);

                    int outputBits = _bpc[bone];
                    ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(
                        qx, qy, qz, qw, outputBits, BasisBoneRotationCompression.MAX_COMPONENT[bone]);
                    WriteBitsOverwrite(_payloadScratch, destinationBit, packed, 2 + outputBits * 3);
                    destinationBit += 2 + outputBits * 3;
                }

                int armatureBits = bitPosition - sourceStart * 8;
                int bodyOffset = (bitPosition + 7) >> 3;
                if (includeAuxiliary)
                {
                    int bodyEnd = bodyOffset + _positionBytes + _tailBytes;
                    if (bodyEnd > sourceStart + availableBytes || bodyEnd > source.Length) return false;

                    Buffer.BlockCopy(source, bodyOffset, _payloadScratch, 0, _positionBytes);
                    bodyOffset += _positionBytes;
                    Buffer.BlockCopy(source, bodyOffset, _payloadScratch, _tailOffset, _tailBytes);
                    bodyOffset += _tailBytes;
                }

                BasisNumerel.RxState[] oldStates = _states;
                _states = _scratchStates;
                _scratchStates = oldStates;
                byte[] oldPayload = _payload;
                _payload = _payloadScratch;
                _payloadScratch = oldPayload;

                _hasSequence = true;
                _lastSequence = sequence;
                _deadlinePredictionsSinceDecode = 0;
                LastArmatureBits = armatureBits;
                Buffer.BlockCopy(_payload, 0, outputPayload, 0, _payloadBytes);
                consumedBytes = bodyOffset - sourceStart;
                return true;
            }

            public int GetAbsoluteGroupRefreshSize(byte[] boneGroups, byte groupId, byte groupCount)
            {
                if (!ValidateBoneGroups(boneGroups, groupCount) || groupId >= groupCount) return -1;
                int bits = 24;
                bool hasBone = false;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    if (boneGroups[bone] != groupId) continue;
                    hasBone = true;
                    bits += 1 + 2 + 3 * _bpc[bone];
                }
                return hasBone ? ((bits + 7) >> 3) : -1;
            }

            /// <summary>
            /// Performs structural validation only. This lets the outer hybrid reject malformed
            /// side data before committing the normal Numerel rotation decode.
            /// </summary>
            public bool TryValidateAbsoluteGroupRefresh(
                byte[] source,
                int sourceStart,
                int availableBytes,
                byte[] boneGroups,
                byte expectedGroupId,
                byte groupCount,
                out int recordLength)
            {
                recordLength = 0;
                if (source == null || sourceStart < 0 || availableBytes < 3 || sourceStart + availableBytes > source.Length)
                    return false;
                if (!ValidateBoneGroups(boneGroups, groupCount) || expectedGroupId >= groupCount) return false;
                if (source[sourceStart] != AbsoluteGroupRefreshMagic
                    || source[sourceStart + 1] != groupCount
                    || source[sourceStart + 2] != expectedGroupId)
                    return false;

                int expected = GetAbsoluteGroupRefreshSize(boneGroups, expectedGroupId, groupCount);
                if (expected <= 0 || expected > availableBytes) return false;

                // Validate the entire bit shape, including canonical zero byte-padding, before the
                // outer hybrid commits its normal Numerel decode. The sign bit is intrinsically
                // one bit; the packed rotation values accept every bit pattern at this layer.
                int bitPosition = (sourceStart + 3) * 8;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    if (boneGroups[bone] != expectedGroupId) continue;
                    int width = 1 + 2 + 3 * _bpc[bone];
                    if (bitPosition + width > (sourceStart + expected) * 8) return false;
                    bitPosition += width;
                }
                int paddingBits = ((sourceStart + expected) * 8) - bitPosition;
                if (paddingBits < 0 || paddingBits > 7) return false;
                if (paddingBits > 0
                    && BasisBoneRotationCompression.ReadBits(source, ref bitPosition, paddingBits) != 0)
                    return false;
                if (bitPosition != (sourceStart + expected) * 8) return false;

                recordLength = expected;
                return true;
            }

            /// <summary>
            /// Applies one already-validated absolute recovery group transactionally. The exact
            /// packed Basis rotations are displayed, while the q/-q sign bits seed RawEstimate,
            /// OutputValue, and LastDelta so the next shared Numerel packet uses the same state as
            /// the sender after its matching passive rebase.
            /// </summary>
            public bool TryApplyAbsoluteGroupRefresh(
                byte[] source,
                int sourceStart,
                int recordLength,
                byte[] boneGroups,
                byte expectedGroupId,
                byte groupCount,
                byte[] outputPayload)
            {
                if (outputPayload == null || outputPayload.Length < _payloadBytes) return false;
                if (!TryValidateAbsoluteGroupRefresh(source, sourceStart, recordLength, boneGroups, expectedGroupId, groupCount, out int expected)
                    || expected != recordLength)
                    return false;

                Array.Clear(_refreshNegate, 0, _refreshNegate.Length);
                Array.Clear(_refreshPacked, 0, _refreshPacked.Length);
                int bitPosition = (sourceStart + 3) * 8;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    if (boneGroups[bone] != expectedGroupId) continue;
                    int width = 2 + 3 * _bpc[bone];
                    _refreshNegate[bone] = checked((byte)BasisBoneRotationCompression.ReadBits(source, ref bitPosition, 1));
                    _refreshPacked[bone] = BasisBoneRotationCompression.ReadBits(source, ref bitPosition, width);
                }

                if (((bitPosition + 7) >> 3) != sourceStart + recordLength) return false;
                int paddingBits = ((sourceStart + recordLength) * 8) - bitPosition;
                if (paddingBits > 0 && BasisBoneRotationCompression.ReadBits(source, ref bitPosition, paddingBits) != 0)
                    return false;

                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    if (boneGroups[bone] != expectedGroupId) continue;
                    ulong packed = _refreshPacked[bone];
                    BasisBoneRotationCompression.DecodeSmallestThree(
                        packed, _bpc[bone],
                        out float qx, out float qy, out float qz, out float qw,
                        BasisBoneRotationCompression.MAX_COMPONENT[bone]);
                    if (_refreshNegate[bone] != 0)
                    {
                        qx = -qx; qy = -qy; qz = -qz; qw = -qw;
                    }

                    int componentBits = _componentBits[bone];
                    int stateIndex = bone * ComponentsPerBone;
                    uint x = QuantizeComponent(qx, componentBits);
                    uint y = QuantizeComponent(qy, componentBits);
                    uint z = QuantizeComponent(qz, componentBits);
                    uint w = QuantizeComponent(qw, componentBits);
                    _states[stateIndex].Reset(x);
                    _states[stateIndex + 1].Reset(y);
                    _states[stateIndex + 2].Reset(z);
                    _states[stateIndex + 3].Reset(w);
                    _scratchStates[stateIndex].Reset(x);
                    _scratchStates[stateIndex + 1].Reset(y);
                    _scratchStates[stateIndex + 2].Reset(z);
                    _scratchStates[stateIndex + 3].Reset(w);

                    int outputBit = _positionBytes * 8 + _boneBitOffsets[bone];
                    int width = 2 + 3 * _bpc[bone];
                    WriteBitsOverwrite(_payload, outputBit, packed, width);
                    WriteBitsOverwrite(_payloadScratch, outputBit, packed, width);
                    WriteBitsOverwrite(outputPayload, outputBit, packed, width);
                }
                return true;
            }

            private void WriteArmatureFromStates(BasisNumerel.RxState[] states, byte[] payload)
            {
                int destinationBit = _positionBytes * 8;
                int stateIndex = 0;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int componentBits = _componentBits[bone];
                    float qx = DequantizeComponent(states[stateIndex++].OutputValue, componentBits);
                    float qy = DequantizeComponent(states[stateIndex++].OutputValue, componentBits);
                    float qz = DequantizeComponent(states[stateIndex++].OutputValue, componentBits);
                    float qw = DequantizeComponent(states[stateIndex++].OutputValue, componentBits);
                    Normalize(ref qx, ref qy, ref qz, ref qw);

                    int outputBits = _bpc[bone];
                    ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(
                        qx, qy, qz, qw, outputBits, BasisBoneRotationCompression.MAX_COMPONENT[bone]);
                    WriteBitsOverwrite(payload, destinationBit, packed, 2 + outputBits * 3);
                    destinationBit += 2 + outputBits * 3;
                }
            }
        }

        private static bool ValidateBoneGroups(byte[] boneGroups, byte groupCount)
        {
            if (boneGroups == null || boneGroups.Length < BasisBoneRotationCompression.SyncBoneCount) return false;
            if (groupCount == 0 || groupCount > BasisBoneRotationCompression.SyncBoneCount) return false;
            ulong seen = 0;
            for (int bone = 0; bone < BasisBoneRotationCompression.SyncBoneCount; bone++)
            {
                byte group = boneGroups[bone];
                if (group >= groupCount) return false;
                seen |= 1UL << group;
            }
            ulong expected = groupCount == 64 ? ulong.MaxValue : ((1UL << groupCount) - 1UL);
            return seen == expected;
        }

        public static int GetMaxRotationBodySize(BasisAvatarBitPacking.BitQuality quality, Options options)
        {
            byte[] bpc = BasisBoneRotationCompression.GetBpcTable(quality);
            int armatureBits = 0;
            for (int bone = 0; bone < bpc.Length; bone++)
            {
                int componentBits = ComponentBits(bpc[bone], options);
                // Upstream non-looping transmitter estimates may temporarily overshoot the nominal
                // scalar range. A 32-bit budget per scalar safely covers the full int-domain cube
                // root code plus up to eight Gray bits without affecting actual encoded size.
                armatureBits += ComponentsPerBone * Math.Max(32, BasisNumerel.MaxEncodedBits(componentBits, options.Numerel));
            }
            if (options.ZeroBoneRleRetainGray) armatureBits += 1; // raw/RLE mode bit
            return (armatureBits + 7) >> 3;
        }

        public static int GetMaxBodySize(BasisAvatarBitPacking.BitQuality quality, Options options)
        {
            int position = BasisAvatarBitPacking.PositionBytes(quality);
            int absoluteBytes = position + BasisBoneRotationCompression.ConvertToSize(quality)
                - position - BasisBoneRotationCompression.RotationBytes(quality);
            return GetMaxRotationBodySize(quality, options) + absoluteBytes;
        }

        private static int[] BuildComponentBits(byte[] sourceBits, Options options)
        {
            var result = new int[sourceBits.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = ComponentBits(sourceBits[i], options);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ComponentBits(int sourceBits, Options options)
        {
            if (options.FixedComponentBits != 0) return options.FixedComponentBits;
            int adjustment = options.AdaptivePrecision
                ? (sourceBits <= 6 ? 2 : 1)
                : options.ComponentBitsAdjustment;
            return Math.Max(3, Math.Min(30, sourceBits + adjustment));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint QuantizeComponent(float value, int bits)
        {
            float clamped = value < -1f ? -1f : value > 1f ? 1f : value;
            uint max = (1u << bits) - 1u;
            double scaled = (clamped * 0.5 + 0.5) * max;
            long quantized = (long)Math.Floor(scaled + 0.5);
            if (quantized < 0) return 0;
            if ((ulong)quantized > max) return max;
            return (uint)quantized;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float DequantizeComponent(uint value, int bits)
        {
            uint max = (1u << bits) - 1u;
            return (float)(value / (double)max * 2.0 - 1.0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Normalize(ref float x, ref float y, ref float z, ref float w)
        {
            double lengthSquared = x * x + y * y + z * z + w * w;
            if (lengthSquared <= 1e-16)
            {
                x = 0f; y = 0f; z = 0f; w = 1f;
                return;
            }
            float inverse = (float)(1.0 / Math.Sqrt(lengthSquared));
            x *= inverse; y *= inverse; z *= inverse; w *= inverse;
        }

        private static void InitializeNeutralArmature(byte[] payload, int positionBytes, byte[] bpc)
        {
            int bit = positionBytes * 8;
            for (int bone = 0; bone < bpc.Length; bone++)
            {
                ulong identity = BasisBoneRotationCompression.EncodeSmallestThree(
                    0f, 0f, 0f, 1f, bpc[bone], BasisBoneRotationCompression.MAX_COMPONENT[bone]);
                WriteBitsOverwrite(payload, bit, identity, 2 + bpc[bone] * 3);
                bit += 2 + bpc[bone] * 3;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int UnsignedExpGolombBitCount(uint value)
        {
            uint code = value + 1u;
            int dataBits = 0;
            for (uint v = code; v > 1u; v >>= 1) dataBits++;
            return dataBits * 2 + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryWriteUnsignedExpGolomb(byte[] destination, ref int bitPosition, int bitLimit, uint value)
        {
            if (value == uint.MaxValue) return false;
            uint code = value + 1u;
            int dataBits = 0;
            for (uint v = code; v > 1u; v >>= 1) dataBits++;
            int totalBits = dataBits * 2 + 1;
            if (bitPosition < 0 || bitPosition + totalBits > bitLimit) return false;
            if (dataBits > 0 && !TryWriteMsbBits(destination, ref bitPosition, bitLimit, 0u, dataBits)) return false;
            return TryWriteMsbBits(destination, ref bitPosition, bitLimit, code, dataBits + 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadUnsignedExpGolomb(byte[] source, ref int bitPosition, int bitLimit, out uint value)
        {
            value = 0;
            int position = bitPosition;
            int leadingZeroes = 0;
            while (true)
            {
                if (!TryReadMsbBits(source, ref position, bitLimit, 1, out uint bit)) return false;
                if (bit != 0) break;
                leadingZeroes++;
                if (leadingZeroes > 31) return false;
            }

            uint code = 1u;
            if (leadingZeroes > 0)
            {
                if (!TryReadMsbBits(source, ref position, bitLimit, leadingZeroes, out uint remainder)) return false;
                code = (1u << leadingZeroes) | remainder;
            }
            if (code == 0) return false;
            value = code - 1u;
            bitPosition = position;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryWriteMsbBits(byte[] destination, ref int bitPosition, int bitLimit, uint value, int count)
        {
            if (destination == null || count < 0 || count > 32 || bitPosition < 0 || bitPosition + count > bitLimit)
                return false;
            int position = bitPosition;
            for (int i = count - 1; i >= 0; i--)
            {
                int byteIndex = position >> 3;
                int shift = 7 - (position & 7);
                byte mask = (byte)(1 << shift);
                if (((value >> i) & 1u) != 0) destination[byteIndex] |= mask;
                else destination[byteIndex] &= (byte)~mask;
                position++;
            }
            bitPosition = position;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadMsbBits(byte[] source, ref int bitPosition, int bitLimit, int count, out uint value)
        {
            value = 0;
            if (source == null || count < 0 || count > 32 || bitPosition < 0 || bitPosition + count > bitLimit)
                return false;
            int position = bitPosition;
            for (int i = 0; i < count; i++)
            {
                int byteIndex = position >> 3;
                int shift = 7 - (position & 7);
                value = (value << 1) | (uint)((source[byteIndex] >> shift) & 1);
                position++;
            }
            bitPosition = position;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteBitsOverwrite(byte[] destination, int bitPosition, ulong value, int count)
        {
            int bytePosition = bitPosition >> 3;
            int bitInByte = bitPosition & 7;
            int left = count;
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
