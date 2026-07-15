using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace Basis.Scripts.Rendering
{
    /// <summary>
    /// Final-output pass for the stabilized VR desktop view. The spectator camera renders the
    /// world into a cached RenderTexture at its own rate; this feature presents that texture from
    /// a cheap, non-XR camera after URP's XR camera has finished.
    ///
    /// This feature intentionally owns one process-wide output. Basis has one local desktop
    /// backbuffer, so multiple renderer-feature instances cannot present independent views.
    /// The newest created instance takes ownership and safely invalidates the previous instance.
    /// </summary>
    public sealed class BasisVRDesktopViewBlitFeature : ScriptableRendererFeature
    {
        [SerializeField] private Material blitMaterial;

        // RenderGraph recording and the driver callbacks that mutate this state both run on the
        // Unity main thread. The state is static because display zero has one active owner.
        private static BasisVRDesktopViewBlitFeature s_OwnerFeature;
        private static Camera s_OutputCamera;
        private static RenderTexture s_SourceTexture;
        private static RTHandle s_SourceHandle;

        private BlitPass _pass;
        private bool _ownsStaticState;

        /// <summary>True after an active renderer has created this feature.</summary>
        public static bool IsAvailable { get; private set; }

        /// <summary>True after the output pass has been recorded successfully at least once.</summary>
        public static bool HasPresentedOutput { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            ReleaseSourceHandle();
            s_OwnerFeature = null;
            s_OutputCamera = null;
            s_SourceTexture = null;
            IsAvailable = false;
            HasPresentedOutput = false;
        }

        /// <summary>Assigns the camera allowed to run this pass and the texture it presents.</summary>
        public static void SetOutput(Camera outputCamera, RenderTexture sourceTexture)
        {
            if (!ReferenceEquals(s_OutputCamera, outputCamera) || !ReferenceEquals(s_SourceTexture, sourceTexture))
            {
                HasPresentedOutput = false;
            }

            s_OutputCamera = outputCamera;

            if (ReferenceEquals(s_SourceTexture, sourceTexture) && s_SourceHandle != null)
            {
                return;
            }

            ReleaseSourceHandle();
            s_SourceTexture = sourceTexture;
            if (sourceTexture != null)
            {
                s_SourceHandle = RTHandles.Alloc(sourceTexture);
            }
        }

        /// <summary>Clears the active output without retaining a destroyed camera or texture.</summary>
        public static void ClearOutput(Camera outputCamera)
        {
            if (outputCamera != null && !ReferenceEquals(s_OutputCamera, outputCamera))
            {
                return;
            }

            s_OutputCamera = null;
            s_SourceTexture = null;
            HasPresentedOutput = false;
            ReleaseSourceHandle();
        }

        private static void ReleaseSourceHandle()
        {
            if (s_SourceHandle == null)
            {
                return;
            }

            s_SourceHandle.Release();
            s_SourceHandle = null;
        }

        public override void Create()
        {
            if (s_OwnerFeature != null && !ReferenceEquals(s_OwnerFeature, this))
            {
                Debug.LogWarning(
                    "Multiple stabilized VR desktop blit features were created. " +
                    "The newest feature will own the single desktop output.");
                s_OwnerFeature._ownsStaticState = false;
                ClearOutput(null);
            }

            s_OwnerFeature = this;
            _ownsStaticState = true;
            IsAvailable = blitMaterial != null;
            HasPresentedOutput = false;

            if (blitMaterial != null)
            {
                // The source texture already matches the camera color-space convention. Leaving either
                // conversion keyword enabled here can double-convert and wash out the desktop output.
                blitMaterial.DisableKeyword("_LINEAR_TO_SRGB_CONVERSION");
                blitMaterial.DisableKeyword("_SRGB_TO_LINEAR_CONVERSION");
            }

            _pass = new BlitPass
            {
                renderPassEvent = RenderPassEvent.AfterRendering
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!_ownsStaticState || _pass == null || blitMaterial == null
                || s_SourceHandle == null || s_SourceTexture == null)
            {
                return;
            }

            if (!ReferenceEquals(renderingData.cameraData.camera, s_OutputCamera))
            {
                return;
            }

            _pass.BlitMaterial = blitMaterial;
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            _pass = null;

            if (!_ownsStaticState)
            {
                return;
            }

            ClearOutput(null);
            IsAvailable = false;
            HasPresentedOutput = false;
            s_OwnerFeature = null;
            _ownsStaticState = false;
        }

        private sealed class BlitPass : ScriptableRenderPass
        {
            public Material BlitMaterial { private get; set; }

            public BlitPass()
            {
                // Source and destination are distinct textures. Forcing an intermediate target here
                // adds a redundant full-resolution copy before the presentation blit.
                requiresIntermediateTexture = false;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameContext)
            {
                if (BlitMaterial == null || s_SourceHandle == null)
                {
                    return;
                }

                UniversalResourceData resources = frameContext.Get<UniversalResourceData>();
                TextureHandle source = renderGraph.ImportTexture(s_SourceHandle);
                TextureHandle destination = resources.activeColorTexture;

                RenderGraphUtils.BlitMaterialParameters parameters =
                    new RenderGraphUtils.BlitMaterialParameters(source, destination, BlitMaterial, 0);
                renderGraph.AddBlitPass(parameters, "Stabilized VR Desktop View");
                HasPresentedOutput = true;
            }
        }
    }
}
