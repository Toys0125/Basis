#if ENABLE_UPSCALER_FRAMEWORK && ENABLE_NVIDIA && ENABLE_NVIDIA_MODULE
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// XR adapter for Unity's native DLSS 4 provider. Core RP now owns the temporal
    /// context and its lifecycle; Basis only opts into multipass XR and keeps each
    /// eye's history separate through the per-eye camera ID in UpscalerPostProcessPass.
    /// </summary>
    public sealed class BasisDlssXrUpscaler : DLSSIUpscaler
    {
        public const string UpscalerId = "basis.nvidia.dlss.xr";
        public const string UpscalerName = "Basis NVIDIA DLSS 4 XR";

        public override string upscalerId => UpscalerId;
        public override string name => UpscalerName;
    }

#if UNITY_EDITOR
    [InitializeOnLoad]
#endif
    internal static class RegisterBasisDlssXr
    {
        static RegisterBasisDlssXr() => Register();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void InitializeRuntime() => Register();

        internal static void Register()
        {
            UpscalerRegistry.Register<BasisDlssXrUpscaler, DLSSOptions>(
                BasisDlssXrUpscaler.UpscalerId, BasisDlssXrUpscaler.UpscalerName);
        }
    }
}
#else
// Allow renderer and settings code to reference the stable ID on platforms
// where Unity's NVIDIA module/upscaling framework is not compiled.
namespace UnityEngine.Rendering.Universal
{
    public static class BasisDlssXrUpscaler
    {
        public const string UpscalerId = "basis.nvidia.dlss.xr";
        public const string UpscalerName = "Basis NVIDIA DLSS 4 XR";
    }
}
#endif
