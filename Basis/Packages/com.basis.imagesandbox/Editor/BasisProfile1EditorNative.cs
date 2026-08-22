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
        private static extern void basis_profile1_editor_free(IntPtr memory);

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
                uint abi = basis_profile1_editor_abi_version();
                if (abi != ExpectedAbiVersion)
                {
                    error = $"Profile 1 editor-native codec ABI mismatch: expected {ExpectedAbiVersion}, got {abi}.";
                    return false;
                }

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
                error =
                    "Profile 1 editor-native codec is not built for this Editor. Run "
                    + "Basis/Debug/JPEG XL Profile 1/Build Editor Native Codec.\n\n"
                    + exception.Message;
                return false;
            }
            catch (EntryPointNotFoundException exception)
            {
                error =
                    "Profile 1 editor-native codec is out of date. Rebuild it from "
                    + "Basis/Debug/JPEG XL Profile 1/Build Editor Native Codec.\n\n"
                    + exception.Message;
                return false;
            }
        }
    }
}
