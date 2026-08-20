using System;
using UnityEngine;

namespace Basis.ImageSandbox
{
    public static class BasisProfile1SandboxResources
    {
        public const string DecoderResourcePath = "BasisImageSandbox/profile1_decoder";

        public static bool TryCreateDecoder(
            BasisProfile1SandboxLimits limits,
            out BasisProfile1SandboxDecoder decoder,
            out string error
        )
        {
            decoder = null;
            error = null;

            TextAsset wasm = Resources.Load<TextAsset>(DecoderResourcePath);
            if (wasm == null || wasm.bytes == null || wasm.bytes.Length == 0)
            {
                error =
                    "Profile 1 JPEG XL decoder resource is missing. Build the pinned "
                    + "profile1_decoder WASM before enabling Profile 1 receive/publication.";
                return false;
            }

            try
            {
                decoder = new BasisProfile1SandboxDecoder(wasm.bytes, limits);
                return true;
            }
            catch (Exception exception)
            {
                error = $"Profile 1 JPEG XL sandbox initialization failed: {exception.Message}";
                decoder?.Dispose();
                decoder = null;
                return false;
            }
        }
    }
}
