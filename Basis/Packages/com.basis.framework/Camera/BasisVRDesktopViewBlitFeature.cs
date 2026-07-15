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
    /// </summary>
    public sealed class BasisVRDesktopViewBlitFeature : ScriptableRendererFeature
    {
        [SerializeField] private Material blitMaterial;

        private static Camera s_OutputCamera;
        private static RenderTexture s_SourceTexture;
        private static RTHandle s_SourceHandle;

        private BlitPass _pass;

        /// <summary>True after an active renderer has created this feature.</summary>
        public static bool IsAvailable { get; private set; }

        /// <summary>True after the output pass has been recorded successfully at least once.</summary>
        public static bool HasPresentedOutput { get; private set; }

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
            IsAvailable = blitMaterial != null;

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
            if (_pass == null || blitMaterial == null || s_SourceHandle == null || s_SourceTexture == null)
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
            IsAvailable = false;
            HasPresentedOutput = false;
        }

        private sealed class BlitPass : ScriptableRenderPass
        {
            public Material BlitMaterial;

            public BlitPass()
            {
                requiresIntermediateTexture = true;
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
