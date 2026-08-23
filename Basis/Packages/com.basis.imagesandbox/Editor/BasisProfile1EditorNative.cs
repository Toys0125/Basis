using System;
using System.Runtime.InteropServices;

namespace Basis.ImageSandbox.Editor
{
    internal static class BasisProfile1EditorNative
    {
        private const string Library = "basis_profile1_editor";
        private const uint ExpectedAbiVersion = 1;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint basis_profile1_editor_abi_version();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int basis_profile1_editor_encode_timeline(
            byte[] input,
            UIntPtr inputSize,
            out IntPtr output,
            out UIntPtr outputSize
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int basis_profile1_editor_decode_jxl_timeline(
            byte[] input,
            UIntPtr inputSize,
            out IntPtr output,
            out UIntPtr outputSize
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int basis_profile1_editor_generate_synthetic_fixture(
            uint kind,
            out IntPtr output,
            out UIntPtr outputSize
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void basis_profile1_editor_free(IntPtr memory);

        public static bool TryDecodeJxlTimeline(byte[] jxl, out byte[] timeline, out string error)
        {
            timeline = null;
            error = null;
            if (jxl == null || jxl.Length == 0)
            {
                error = "JPEG XL source is empty.";
                return false;
            }
            try
            {
                if (!TryValidateAbi(out error))
                    return false;
                int status = basis_profile1_editor_decode_jxl_timeline(
                    jxl,
                    (UIntPtr)(ulong)jxl.LongLength,
                    out IntPtr output,
                    out UIntPtr outputSize
                );
                return TryCopyNativeOutput(status, output, outputSize, "JPEG XL local decode", out timeline, out error);
            }
            catch (DllNotFoundException exception)
            {
                error = MissingCodecError(exception);
                return false;
            }
            catch (EntryPointNotFoundException exception)
            {
                error = OutdatedCodecError(exception);
                return false;
            }
        }

        public static bool TryGenerateSyntheticFixture(uint kind, out byte[] jxl, out string error)
        {
            jxl = null;
            error = null;
            try
            {
                if (!TryValidateAbi(out error))
                    return false;
                int status = basis_profile1_editor_generate_synthetic_fixture(
                    kind,
                    out IntPtr output,
                    out UIntPtr outputSize
                );
                return TryCopyNativeOutput(status, output, outputSize, "Profile 1 synthetic fixture generation", out jxl, out error);
            }
            catch (DllNotFoundException exception)
            {
                error = MissingCodecError(exception);
                return false;
            }
            catch (EntryPointNotFoundException exception)
            {
                error = OutdatedCodecError(exception);
                return false;
            }
        }

        public static bool TryEncodeTimeline(byte[] timeline, out byte[] profile1, out string error)
        {
            profile1 = null;
            error = null;
            if (timeline == null || timeline.Length == 0)
            {
                error = "Profile 1 editor-native encoder input is empty.";
                return false;
            }

            try
            {
                if (!TryValidateAbi(out error))
                    return false;

                int status = basis_profile1_editor_encode_timeline(
                    timeline,
                    (UIntPtr)(ulong)timeline.LongLength,
                    out IntPtr output,
                    out UIntPtr outputSize
                );
                if (status != 0)
                {
                    error = $"Profile 1 editor-native encoder failed with status {status}.";
                    return false;
                }

                try
                {
                    ulong size64 = outputSize.ToUInt64();
                    if (output == IntPtr.Zero || size64 == 0 || size64 > int.MaxValue)
                    {
                        error = "Profile 1 editor-native encoder returned an invalid output buffer.";
                        return false;
                    }
                    profile1 = new byte[(int)size64];
                    Marshal.Copy(output, profile1, 0, profile1.Length);
                    return true;
                }
                finally
                {
                    if (output != IntPtr.Zero)
                        basis_profile1_editor_free(output);
                }
            }
            catch (DllNotFoundException exception)
            {
                error = MissingCodecError(exception);
                return false;
            }
            catch (EntryPointNotFoundException exception)
            {
                error = OutdatedCodecError(exception);
                return false;
            }
        }

        private static bool TryValidateAbi(out string error)
        {
            error = null;
            uint abi = basis_profile1_editor_abi_version();
            if (abi == ExpectedAbiVersion)
                return true;
            error = $"Profile 1 editor-native codec ABI mismatch: expected {ExpectedAbiVersion}, got {abi}.";
            return false;
        }

        private static bool TryCopyNativeOutput(
            int status,
            IntPtr output,
            UIntPtr outputSize,
            string operation,
            out byte[] bytes,
            out string error)
        {
            bytes = null;
            error = null;
            try
            {
                if (status != 0)
                {
                    error = $"{operation} failed with status {status}: {DescribeToolStatus(status)}.";
                    return false;
                }
                ulong size64 = outputSize.ToUInt64();
                if (output == IntPtr.Zero || size64 == 0 || size64 > int.MaxValue)
                {
                    error = $"{operation} returned an invalid output buffer.";
                    return false;
                }
                bytes = new byte[(int)size64];
                Marshal.Copy(output, bytes, 0, bytes.Length);
                return true;
            }
            finally
            {
                if (output != IntPtr.Zero)
                    basis_profile1_editor_free(output);
            }
        }

        private static string DescribeToolStatus(int status) => status switch
        {
            1 => "invalid argument",
            2 => "source decode failed",
            3 => "source timing cannot be represented exactly in microseconds",
            4 => "encode failed",
            5 => "allocation limit exceeded",
            _ => "unknown native error",
        };

        private static string MissingCodecError(Exception exception) =>
            "Profile 1 editor-native codec is not built for this Editor. Run "
            + "Basis/Debug/JPEG XL Profile 1/Build Editor Native Codec.\n\n"
            + exception.Message;

        private static string OutdatedCodecError(Exception exception) =>
            "Profile 1 editor-native codec is out of date. Rebuild it from "
            + "Basis/Debug/JPEG XL Profile 1/Build Editor Native Codec.\n\n"
            + exception.Message;
    }
}
