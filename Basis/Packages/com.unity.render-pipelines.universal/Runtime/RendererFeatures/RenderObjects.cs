using UnityEngine.Scripting.APIUpdating;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// The queue type for the objects to render.
    /// </summary>
    [MovedFrom(true, "UnityEngine.Experimental.Rendering.Universal")]
    public enum RenderQueueType
    {
        /// <summary>
        /// Use this for opaque objects.
        /// </summary>
        Opaque,

        /// <summary>
        /// Use this for transparent objects.
        /// </summary>
        Transparent,
    }

    /// <summary>
    /// The class for the render objects renderer feature.
    /// </summary>
    [ExcludeFromPreset]
    [MovedFrom(true, "UnityEngine.Experimental.Rendering.Universal")]
    [Tooltip("Render Objects simplifies the injection of additional render passes by exposing a selection of commonly used settings.")]
    [URPHelpURL("renderer-features/renderer-feature-render-objects")]
    public class RenderObjects : ScriptableRendererFeature
    {
        /// <summary>
        /// Settings class used for the render objects renderer feature.
        /// </summary>
        [System.Serializable]
        public class RenderObjectsSettings
        {
            /// <summary>
            /// The profiler tag used with the pass.
            /// </summary>
            public string passTag = "RenderObjectsFeature";

            /// <summary>
            /// Controls when the render pass executes.
            /// </summary>
            public RenderPassEvent Event = RenderPassEvent.AfterRenderingOpaques;

            /// <summary>
            /// The filter settings for the pass.
            /// </summary>
            public FilterSettings filterSettings = new FilterSettings();

            /// <summary>
            /// Optional subset of <see cref="FilterSettings.LayerMask"/> that should be drawn
            /// after image upscaling instead of before post processing. This is intended for
            /// native-resolution UI that must not be fed through FSR/DLSS.
            /// </summary>
            public LayerMask renderAfterUpscalingLayerMask = 0;

            /// <summary>
            /// The override material to use.
            /// </summary>
            public Material overrideMaterial = null;

            /// <summary>
            /// The pass index to use with the override material.
            /// </summary>
            public int overrideMaterialPassIndex = 0;

            /// <summary>
            /// The override shader to use.
            /// </summary>
            public Shader overrideShader = null;

            /// <summary>
            /// The pass index to use with the override shader.
            /// </summary>
            public int overrideShaderPassIndex = 0;

            /// <summary>
            /// Options to select which type of override mode should be used.
            /// </summary>
            public enum OverrideMaterialMode
            {
                /// <summary>
                /// Use this to not override.
                /// </summary>
                None,

                /// <summary>
                /// Use this to use an override material.
                /// </summary>
                Material,

                /// <summary>
                /// Use this to use an override shader.
                /// </summary>
                Shader
            };

            /// <summary>
            /// The selected override mode.
            /// </summary>
            public OverrideMaterialMode overrideMode = OverrideMaterialMode.Material; //default to Material as this was previously the only option

            /// <summary>
            /// Sets whether it should override depth or not.
            /// </summary>
            public bool overrideDepthState = false;

            /// <summary>
            /// The depth comparison function to use.
            /// </summary>
            public CompareFunction depthCompareFunction = CompareFunction.LessEqual;

            /// <summary>
            /// Sets whether it should write to depth or not.
            /// </summary>
            public bool enableWrite = true;

            /// <summary>
            /// The stencil settings to use.
            /// </summary>
            public StencilStateData stencilSettings = new StencilStateData();

            /// <summary>
            /// The camera settings to use.
            /// </summary>
            public CustomCameraSettings cameraSettings = new CustomCameraSettings();
        }

        /// <summary>
        /// The filter settings used.
        /// </summary>
        [System.Serializable]
        public class FilterSettings
        {
            // TODO: expose opaque, transparent, all ranges as drop down

            /// <summary>
            /// The queue type for the objects to render.
            /// </summary>
            public RenderQueueType RenderQueueType;

            /// <summary>
            /// The layer mask to use.
            /// </summary>
            public LayerMask LayerMask;

            /// <summary>
            /// The passes to render.
            /// </summary>
            public string[] PassNames;

            /// <summary>
            /// The constructor for the filter settings.
            /// </summary>
            public FilterSettings()
            {
                RenderQueueType = RenderQueueType.Opaque;
                LayerMask = 0;
            }
        }

        /// <summary>
        /// The settings for custom cameras values.
        /// </summary>
        [System.Serializable]
        public class CustomCameraSettings
        {
            /// <summary>
            /// Used to mark whether camera values should be changed or not.
            /// </summary>
            public bool overrideCamera = false;

            /// <summary>
            /// Should the values be reverted after rendering the objects?
            /// </summary>
            public bool restoreCamera = true;

            /// <summary>
            /// Changes the camera offset.
            /// </summary>
            public Vector4 offset;

            /// <summary>
            /// Changes the camera field of view.
            /// </summary>
            public float cameraFieldOfView = 60.0f;
        }

        /// <summary>
        /// The settings used for the Render Objects renderer feature.
        /// </summary>
        public RenderObjectsSettings settings = new RenderObjectsSettings();

        RenderObjectsPass renderObjectsPass;
        RenderObjectsPass renderObjectsBeforeUpscalingPass;
        RenderObjectsPass renderObjectsAfterUpscalingPass;

        /// <summary>
        /// Layers this feature removes from the normal transparent pass while upscaling is active.
        /// </summary>
        internal int renderAfterUpscalingLayerMask => settings.renderAfterUpscalingLayerMask.value & settings.filterSettings.LayerMask.value;

        /// <inheritdoc/>
        public override void Create()
        {
            FilterSettings filter = settings.filterSettings;

            // Render Objects pass doesn't support events before rendering prepasses.
            // The camera is not setup before this point and all rendering is monoscopic.
            // Events before BeforeRenderingPrepasses should be used for input texture passes (shadow map, LUT, etc) that doesn't depend on the camera.
            // These events are filtering in the UI, but we still should prevent users from changing it from code or
            // by changing the serialized data.
            if (settings.Event < RenderPassEvent.BeforeRenderingPrePasses)
                settings.Event = RenderPassEvent.BeforeRenderingPrePasses;

            int afterUpscalingMask = renderAfterUpscalingLayerMask;
            int beforeUpscalingMask = filter.LayerMask.value & ~afterUpscalingMask;

            // Preserve the original single-pass path whenever the camera is not being upscaled.
            renderObjectsPass = CreatePass(settings.passTag, settings.Event, filter, filter.LayerMask.value);
            renderObjectsBeforeUpscalingPass = afterUpscalingMask != 0 && beforeUpscalingMask != 0
                ? CreatePass($"{settings.passTag} Before Upscaling", settings.Event, filter, beforeUpscalingMask)
                : null;
            renderObjectsAfterUpscalingPass = afterUpscalingMask != 0
                ? CreatePass($"{settings.passTag} After Upscaling", RenderPassEvent.AfterRenderingPostProcessing, filter, afterUpscalingMask)
                : null;
            if (renderObjectsAfterUpscalingPass != null)
            {
                renderObjectsAfterUpscalingPass.SetUseNonJitteredProjection(true);
                renderObjectsAfterUpscalingPass.SetRenderNativeOverlayAfterPostProcessing(true);
            }
        }

        private RenderObjectsPass CreatePass(string passTag, RenderPassEvent renderPassEvent, FilterSettings filter, int layerMask)
        {
            RenderObjectsPass pass = new RenderObjectsPass(passTag, renderPassEvent, filter.PassNames,
                filter.RenderQueueType, layerMask, settings.cameraSettings);

            switch (settings.overrideMode)
            {
                case RenderObjectsSettings.OverrideMaterialMode.None:
                    pass.overrideMaterial = null;
                    pass.overrideShader = null;
                    break;
                case RenderObjectsSettings.OverrideMaterialMode.Material:
                    pass.overrideMaterial = settings.overrideMaterial;
                    pass.overrideMaterialPassIndex = settings.overrideMaterialPassIndex;
                    pass.overrideShader = null;
                    break;
                case RenderObjectsSettings.OverrideMaterialMode.Shader:
                    pass.overrideMaterial = null;
                    pass.overrideShader = settings.overrideShader;
                    pass.overrideShaderPassIndex = settings.overrideShaderPassIndex;
                    break;
            }

            if (settings.overrideDepthState)
                pass.SetDepthState(settings.enableWrite, settings.depthCompareFunction);

            if (settings.stencilSettings.overrideStencilState)
                pass.SetStencilState(settings.stencilSettings.stencilReference,
                    settings.stencilSettings.stencilCompareFunction, settings.stencilSettings.passOperation,
                    settings.stencilSettings.failOperation, settings.stencilSettings.zFailOperation);

            return pass;
        }

        /// <inheritdoc/>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (renderingData.cameraData.cameraType == CameraType.Preview
                || UniversalRenderer.IsOffscreenDepthTexture(ref renderingData.cameraData))
                return;

            bool isTemporalVendorUpscaling = renderingData.cameraData.imageScalingMode == ImageScalingMode.Upscaling
                && renderingData.cameraData.IsTemporalAAEnabled()
                && UniversalRenderPipeline.IsBasisTemporalUpscalerActive();
            if (!isTemporalVendorUpscaling || renderObjectsAfterUpscalingPass == null)
            {
                renderer.EnqueuePass(renderObjectsPass);
                return;
            }

            if (renderObjectsBeforeUpscalingPass != null)
                renderer.EnqueuePass(renderObjectsBeforeUpscalingPass);
            renderer.EnqueuePass(renderObjectsAfterUpscalingPass);
        }
    }
}
