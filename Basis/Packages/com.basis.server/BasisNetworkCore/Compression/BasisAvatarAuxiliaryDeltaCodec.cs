using System;
using System.Runtime.CompilerServices;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Exact baseline-relative delta codec for the non-armature portion of an avatar payload.
    /// It intentionally excludes the 51 bone rotations so Numerel can own the rotation stream
    /// while position/scale/body/hips/end-effector data retain Basis' exact packed values.
    ///
    /// Body layout:
    ///   [mask:1][changed fields in fixed order]
    ///
    /// mask bit 7 = auxiliary bootstrap. A bootstrap carries every field and replaces the
    /// auxiliary baseline. Normal deltas are always relative to that baseline (not the previous
    /// delta), so losing one normal packet cannot desynchronize later auxiliary packets.
    /// </summary>
    public static class BasisAvatarAuxiliaryDeltaCodec
    {
        private const byte BootstrapFlag = 0x80;
        private const byte ReservedFlag = 0x40;
        private const byte PositionBit = 1 << 0;
        private const byte ScaleBit = 1 << 1;
        private const byte BodyRotationBit = 1 << 2;
        private const byte HipsDeltaBit = 1 << 3;
        private const byte HipsRotationBit = 1 << 4;
        private const byte EndEffectorBit = 1 << 5;
        private const byte FieldMask = PositionBit | ScaleBit | BodyRotationBit | HipsDeltaBit | HipsRotationBit | EndEffectorBit;

        private sealed class Geometry
        {
            public int PayloadSize;
            public int PositionBytes;
            public int ScaleOffset;
            public int BodyRotationOffset;
            public int HipsDeltaOffset;
            public int HipsRotationOffset;
            public int EndEffectorOffset;
            public int EndEffectorBytes;
            public int FullAuxiliaryBytes;
        }

        private static readonly Geometry[] Geo = new Geometry[4];

        static BasisAvatarAuxiliaryDeltaCodec()
        {
            for (int qi = 0; qi < Geo.Length; qi++)
            {
                var quality = (BasisAvatarBitPacking.BitQuality)qi;
                int positionBytes = BasisAvatarBitPacking.PositionBytes(quality);
                int rotationBytes = BasisBoneRotationCompression.RotationBytes(quality);
                int tailOffset = positionBytes + rotationBytes;
                int endEffectorBytes = BasisBoneRotationCompression.EndEffectorBytes(quality);
                Geo[qi] = new Geometry
                {
                    PayloadSize = BasisBoneRotationCompression.ConvertToSize(quality),
                    PositionBytes = positionBytes,
                    ScaleOffset = tailOffset,
                    BodyRotationOffset = tailOffset + BasisBoneRotationCompression.WriteScale,
                    HipsDeltaOffset = tailOffset + BasisBoneRotationCompression.WriteScale + BasisBoneRotationCompression.WriteRotation,
                    HipsRotationOffset = tailOffset + BasisBoneRotationCompression.WriteScale + BasisBoneRotationCompression.WriteRotation + BasisBoneRotationCompression.WriteHipsDelta,
                    EndEffectorOffset = tailOffset + BasisBoneRotationCompression.TailBytes,
                    EndEffectorBytes = endEffectorBytes,
                    FullAuxiliaryBytes = positionBytes + BasisBoneRotationCompression.TailBytes + endEffectorBytes,
                };
            }
        }

        public static int PayloadSize(BasisAvatarBitPacking.BitQuality quality) => Geo[(int)quality].PayloadSize;
        public static int MaxBodySize(BasisAvatarBitPacking.BitQuality quality) => 1 + Geo[(int)quality].FullAuxiliaryBytes;

        public static bool TryGetBodyLength(
            byte[] source,
            int sourceStart,
            int availableBytes,
            BasisAvatarBitPacking.BitQuality quality,
            out int bodyLength,
            out bool bootstrap)
        {
            bodyLength = 0;
            bootstrap = false;
            if (source == null || sourceStart < 0 || availableBytes < 1 || sourceStart + availableBytes > source.Length)
                return false;
            int qualityIndex = (int)quality;
            if ((uint)qualityIndex >= (uint)Geo.Length) return false;

            Geometry g = Geo[qualityIndex];
            byte mask = source[sourceStart];
            if ((mask & ReservedFlag) != 0) return false;
            bootstrap = (mask & BootstrapFlag) != 0;

            if (bootstrap)
            {
                // Bootstrap is canonical: bit 7 only, followed by every auxiliary field.
                if ((mask & FieldMask) != 0) return false;
                bodyLength = 1 + g.FullAuxiliaryBytes;
                return bodyLength <= availableBytes;
            }

            int length = 1;
            if ((mask & PositionBit) != 0) length += g.PositionBytes;
            if ((mask & ScaleBit) != 0) length += BasisBoneRotationCompression.WriteScale;
            if ((mask & BodyRotationBit) != 0) length += BasisBoneRotationCompression.WriteRotation;
            if ((mask & HipsDeltaBit) != 0) length += BasisBoneRotationCompression.WriteHipsDelta;
            if ((mask & HipsRotationBit) != 0) length += BasisBoneRotationCompression.WriteHipsRotation;
            if ((mask & EndEffectorBit) != 0)
            {
                if (g.EndEffectorBytes == 0) return false;
                length += g.EndEffectorBytes;
            }

            bodyLength = length;
            return bodyLength <= availableBytes;
        }

        public sealed class Encoder
        {
            private readonly Geometry _geometry;
            private readonly byte[] _baseline;
            private bool _hasBaseline;

            public Encoder(BasisAvatarBitPacking.BitQuality quality)
            {
                _geometry = Geo[(int)quality];
                _baseline = new byte[_geometry.PayloadSize];
                Reset();
            }

            public int PayloadSize => _geometry.PayloadSize;
            public int MaxBodySize => 1 + _geometry.FullAuxiliaryBytes;
            public bool HasBaseline => _hasBaseline;
            public bool LastWasBootstrap { get; private set; }
            public byte LastMask { get; private set; }

            public void Reset()
            {
                Array.Clear(_baseline, 0, _baseline.Length);
                _hasBaseline = false;
                LastWasBootstrap = false;
                LastMask = 0;
            }

            public int Encode(byte[] payload, bool forceBootstrap, byte[] destination, int destinationStart)
            {
                if (payload == null || payload.Length < _geometry.PayloadSize || destination == null || destinationStart < 0)
                    return -1;
                if (destination.Length - destinationStart < MaxBodySize) return -1;

                bool bootstrap = forceBootstrap || !_hasBaseline;
                int o = destinationStart;
                if (bootstrap)
                {
                    destination[o++] = BootstrapFlag;
                    CopyAllAuxiliary(payload, destination, ref o, _geometry);
                    CopyAuxiliary(payload, _baseline, _geometry);
                    _hasBaseline = true;
                    LastWasBootstrap = true;
                    LastMask = BootstrapFlag;
                    return o - destinationStart;
                }

                byte mask = 0;
                if (!SpanEqual(payload, 0, _baseline, 0, _geometry.PositionBytes)) mask |= PositionBit;
                if (!SpanEqual(payload, _geometry.ScaleOffset, _baseline, _geometry.ScaleOffset, BasisBoneRotationCompression.WriteScale)) mask |= ScaleBit;
                if (!SpanEqual(payload, _geometry.BodyRotationOffset, _baseline, _geometry.BodyRotationOffset, BasisBoneRotationCompression.WriteRotation)) mask |= BodyRotationBit;
                if (!SpanEqual(payload, _geometry.HipsDeltaOffset, _baseline, _geometry.HipsDeltaOffset, BasisBoneRotationCompression.WriteHipsDelta)) mask |= HipsDeltaBit;
                if (!SpanEqual(payload, _geometry.HipsRotationOffset, _baseline, _geometry.HipsRotationOffset, BasisBoneRotationCompression.WriteHipsRotation)) mask |= HipsRotationBit;
                if (_geometry.EndEffectorBytes > 0
                    && !SpanEqual(payload, _geometry.EndEffectorOffset, _baseline, _geometry.EndEffectorOffset, _geometry.EndEffectorBytes))
                    mask |= EndEffectorBit;

                destination[o++] = mask;
                if ((mask & PositionBit) != 0) Copy(payload, 0, destination, ref o, _geometry.PositionBytes);
                if ((mask & ScaleBit) != 0) Copy(payload, _geometry.ScaleOffset, destination, ref o, BasisBoneRotationCompression.WriteScale);
                if ((mask & BodyRotationBit) != 0) Copy(payload, _geometry.BodyRotationOffset, destination, ref o, BasisBoneRotationCompression.WriteRotation);
                if ((mask & HipsDeltaBit) != 0) Copy(payload, _geometry.HipsDeltaOffset, destination, ref o, BasisBoneRotationCompression.WriteHipsDelta);
                if ((mask & HipsRotationBit) != 0) Copy(payload, _geometry.HipsRotationOffset, destination, ref o, BasisBoneRotationCompression.WriteHipsRotation);
                if ((mask & EndEffectorBit) != 0) Copy(payload, _geometry.EndEffectorOffset, destination, ref o, _geometry.EndEffectorBytes);

                LastWasBootstrap = false;
                LastMask = mask;
                return o - destinationStart;
            }
        }

        public sealed class Decoder
        {
            private readonly BasisAvatarBitPacking.BitQuality _quality;
            private readonly Geometry _geometry;
            private readonly byte[] _baseline;
            private bool _hasBaseline;

            public Decoder(BasisAvatarBitPacking.BitQuality quality)
            {
                _quality = quality;
                _geometry = Geo[(int)quality];
                _baseline = new byte[_geometry.PayloadSize];
                Reset();
            }

            public bool HasBaseline => _hasBaseline;
            public int MaxBodySize => 1 + _geometry.FullAuxiliaryBytes;

            public void Reset()
            {
                Array.Clear(_baseline, 0, _baseline.Length);
                _hasBaseline = false;
            }

            public bool CanDecode(byte[] source, int sourceStart, int availableBytes, out int bodyLength, out bool bootstrap)
            {
                if (!TryGetBodyLength(source, sourceStart, availableBytes, _quality, out bodyLength, out bootstrap))
                    return false;
                if (bodyLength != availableBytes) return false;
                return bootstrap || _hasBaseline;
            }

            public bool TryDecode(
                byte[] source,
                int sourceStart,
                int availableBytes,
                byte[] outputPayload,
                out int consumedBytes)
            {
                consumedBytes = 0;
                if (outputPayload == null || outputPayload.Length < _geometry.PayloadSize) return false;
                if (!CanDecode(source, sourceStart, availableBytes, out int bodyLength, out bool bootstrap))
                    return false;

                DecodeValidated(source, sourceStart, outputPayload, bootstrap);
                consumedBytes = bodyLength;
                return true;
            }

            /// <summary>
            /// Applies a body that has already passed <see cref="CanDecode"/>. This internal path
            /// has no parse-time failure after a stateful outer codec has committed another stream.
            /// </summary>
            internal void DecodeValidated(byte[] source, int sourceStart, byte[] outputPayload, bool bootstrap)
            {
                int o = sourceStart + 1;
                if (bootstrap)
                {
                    ReadAllAuxiliary(source, ref o, outputPayload, _geometry);
                    CopyAuxiliary(outputPayload, _baseline, _geometry);
                    _hasBaseline = true;
                    return;
                }

                CopyAuxiliary(_baseline, outputPayload, _geometry);
                byte mask = source[sourceStart];
                if ((mask & PositionBit) != 0) Read(source, ref o, outputPayload, 0, _geometry.PositionBytes);
                if ((mask & ScaleBit) != 0) Read(source, ref o, outputPayload, _geometry.ScaleOffset, BasisBoneRotationCompression.WriteScale);
                if ((mask & BodyRotationBit) != 0) Read(source, ref o, outputPayload, _geometry.BodyRotationOffset, BasisBoneRotationCompression.WriteRotation);
                if ((mask & HipsDeltaBit) != 0) Read(source, ref o, outputPayload, _geometry.HipsDeltaOffset, BasisBoneRotationCompression.WriteHipsDelta);
                if ((mask & HipsRotationBit) != 0) Read(source, ref o, outputPayload, _geometry.HipsRotationOffset, BasisBoneRotationCompression.WriteHipsRotation);
                if ((mask & EndEffectorBit) != 0) Read(source, ref o, outputPayload, _geometry.EndEffectorOffset, _geometry.EndEffectorBytes);
            }
        }

        private static void CopyAllAuxiliary(byte[] source, byte[] destination, ref int destinationOffset, Geometry g)
        {
            Copy(source, 0, destination, ref destinationOffset, g.PositionBytes);
            Copy(source, g.ScaleOffset, destination, ref destinationOffset, BasisBoneRotationCompression.WriteScale);
            Copy(source, g.BodyRotationOffset, destination, ref destinationOffset, BasisBoneRotationCompression.WriteRotation);
            Copy(source, g.HipsDeltaOffset, destination, ref destinationOffset, BasisBoneRotationCompression.WriteHipsDelta);
            Copy(source, g.HipsRotationOffset, destination, ref destinationOffset, BasisBoneRotationCompression.WriteHipsRotation);
            if (g.EndEffectorBytes > 0) Copy(source, g.EndEffectorOffset, destination, ref destinationOffset, g.EndEffectorBytes);
        }

        private static void ReadAllAuxiliary(byte[] source, ref int sourceOffset, byte[] destination, Geometry g)
        {
            Read(source, ref sourceOffset, destination, 0, g.PositionBytes);
            Read(source, ref sourceOffset, destination, g.ScaleOffset, BasisBoneRotationCompression.WriteScale);
            Read(source, ref sourceOffset, destination, g.BodyRotationOffset, BasisBoneRotationCompression.WriteRotation);
            Read(source, ref sourceOffset, destination, g.HipsDeltaOffset, BasisBoneRotationCompression.WriteHipsDelta);
            Read(source, ref sourceOffset, destination, g.HipsRotationOffset, BasisBoneRotationCompression.WriteHipsRotation);
            if (g.EndEffectorBytes > 0) Read(source, ref sourceOffset, destination, g.EndEffectorOffset, g.EndEffectorBytes);
        }

        private static void CopyAuxiliary(byte[] source, byte[] destination, Geometry g)
        {
            Buffer.BlockCopy(source, 0, destination, 0, g.PositionBytes);
            Buffer.BlockCopy(source, g.ScaleOffset, destination, g.ScaleOffset, BasisBoneRotationCompression.TailBytes);
            if (g.EndEffectorBytes > 0)
                Buffer.BlockCopy(source, g.EndEffectorOffset, destination, g.EndEffectorOffset, g.EndEffectorBytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SpanEqual(byte[] a, int aOffset, byte[] b, int bOffset, int length)
        {
            for (int i = 0; i < length; i++)
                if (a[aOffset + i] != b[bOffset + i]) return false;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Copy(byte[] source, int sourceOffset, byte[] destination, ref int destinationOffset, int length)
        {
            Buffer.BlockCopy(source, sourceOffset, destination, destinationOffset, length);
            destinationOffset += length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Read(byte[] source, ref int sourceOffset, byte[] destination, int destinationOffset, int length)
        {
            Buffer.BlockCopy(source, sourceOffset, destination, destinationOffset, length);
            sourceOffset += length;
        }
    }
}
