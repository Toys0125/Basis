#if VRC_NEW_HOOK_API
using VRC.SDKBase;
#endif

namespace VF.VrcfEditorOnly {
    // This is here so we can be compatible with /either/ the whitelist patch, OR the new vrcsdk IEditorOnly.
    // In non-VRChat projects this remains a marker interface and introduces no VRC SDK dependency.
    internal interface IVrcfEditorOnly
#if VRC_NEW_HOOK_API
        : IEditorOnly
#endif
    {
    }
}
