using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Scripting.APIUpdating;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// The scriptable render pass used with the render objects renderer feature.
    /// </summary>
    [MovedFrom(true, "UnityEngine.Experimental.Rendering.Universal")]
    public partial class RenderObjectsPass : ScriptableRenderPass
    {
        RenderQueueType renderQueueType;
        FilteringSettings m_FilteringSettings;
        RenderObjects.CustomCameraSettings m_CameraSettings;
        bool m_UseNonJitteredProjection;
        static readonly int s_PostUpscaleOverlayTextureId = Shader.PropertyToID("_BasisPostUpscaleOverlayTexture");
        const string k_PostUpscaleCompositePassName = "BasisPostUpscaleOverlayComposite";

        internal void SetUseNonJitteredProjection(bool value)
        {
            m_UseNonJitteredProjection = value;
        }

        /// <summary>
        /// The override material to use.
        /// </summary>
        public Material overrideMaterial { get; set; }

        /// <summary>
        /// The pass index to use with the override material.
        /// </summary>
        public int overrideMaterialPassIndex { get; set; }

        /// <summary>
        /// The override shader to use.
        /// </summary>
        public Shader overrideShader { get; set; }

        /// <summary>
        /// The pass index to use with the override shader.
        /// </summary>
        public int overrideShaderPassIndex { get; set; }

        List<ShaderTagId> m_ShaderTagIdList = new List<ShaderTagId>();
        private PassData m_PassData;

        /// <summary>
        /// Sets the write and comparison function for depth.
        /// </summary>
        /// <param name="writeEnabled">Sets whether it should write to depth or not.</param>
        /// <param name="function">The depth comparison function to use.</param>
        [Obsolete("Use SetDepthState instead. #from(2023.1) #breakingFrom(2023.1)", true)]
        public void SetDetphState(bool writeEnabled, CompareFunction function = CompareFunction.Less)
        {
            SetDepthState(writeEnabled, function);
        }

        /// <summary>
        /// Sets the write and comparison function for depth.
        /// </summary>
        /// <param name="writeEnabled">Sets whether it should write to depth or not.</param>
        /// <param name="function">The depth comparison function to use.</param>
        public void SetDepthState(bool writeEnabled, CompareFunction function = CompareFunction.Less)
        {
            m_RenderStateBlock.mask |= RenderStateMask.Depth;
            m_RenderStateBlock.depthState = new DepthState(writeEnabled, function);
        }

        /// <summary>
        /// Sets up the stencil settings for the pass.
        /// </summary>
        /// <param name="reference">The stencil reference value.</param>
        /// <param name="compareFunction">The comparison function to use.</param>
        /// <param name="passOp">The stencil operation to use when the stencil test passes.</param>
        /// <param name="failOp">The stencil operation to use when the stencil test fails.</param>
        /// <param name="zFailOp">The stencil operation to use when the stencil test fails because of depth.</param>
        public void SetStencilState(int reference, CompareFunction compareFunction, StencilOp passOp, StencilOp failOp, StencilOp zFailOp)
        {
            StencilState stencilState = StencilState.defaultValue;
            stencilState.enabled = true;
            stencilState.SetCompareFunction(compareFunction);
            stencilState.SetPassOperation(passOp);
            stencilState.SetFailOperation(failOp);
            stencilState.SetZFailOperation(zFailOp);

            m_RenderStateBlock.mask |= RenderStateMask.Stencil;
            m_RenderStateBlock.stencilReference = reference;
            m_RenderStateBlock.stencilState = stencilState;
        }

        RenderStateBlock m_RenderStateBlock;

        /// <summary>
        /// The constructor for render objects pass.
        /// </summary>
        /// <param name="profilerTag">The profiler tag used with the pass.</param>
        /// <param name="renderPassEvent">Controls when the render pass executes.</param>
        /// <param name="shaderTags">List of shader tags to render with.</param>
        /// <param name="renderQueueType">The queue type for the objects to render.</param>
        /// <param name="layerMask">The layer mask to use for creating filtering settings that control what objects get rendered.</param>
        /// <param name="cameraSettings">The settings for custom cameras values.</param>
        public RenderObjectsPass(string profilerTag, RenderPassEvent renderPassEvent, string[] shaderTags, RenderQueueType renderQueueType, int layerMask, RenderObjects.CustomCameraSettings cameraSettings)
        {
            profilingSampler = new ProfilingSampler(profilerTag);
            Init(renderPassEvent, shaderTags, renderQueueType, layerMask, cameraSettings);
        }

        internal RenderObjectsPass(URPProfileId profileId, RenderPassEvent renderPassEvent, string[] shaderTags, RenderQueueType renderQueueType, int layerMask,
            RenderObjects.CustomCameraSettings cameraSettings)
        {
            profilingSampler = ProfilingSampler.Get(profileId);
            Init(renderPassEvent, shaderTags, renderQueueType, layerMask, cameraSettings);
        }

        internal void Init(RenderPassEvent renderPassEvent, string[] shaderTags, RenderQueueType renderQueueType, int layerMask, RenderObjects.CustomCameraSettings cameraSettings)
        {
            m_PassData = new PassData();

            this.renderPassEvent = renderPassEvent;
            this.renderQueueType = renderQueueType;
            this.overrideMaterial = null;
            this.overrideMaterialPassIndex = 0;
            this.overrideShader = null;
            this.overrideShaderPassIndex = 0;
            RenderQueueRange renderQueueRange = (renderQueueType == RenderQueueType.Transparent)
                ? RenderQueueRange.transparent
                : RenderQueueRange.opaque;
            m_FilteringSettings = new FilteringSettings(renderQueueRange, layerMask);

            if (shaderTags != null && shaderTags.Length > 0)
            {
                foreach (var tag in shaderTags)
                    m_ShaderTagIdList.Add(new ShaderTagId(tag));
            }
            else
            {
                m_ShaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
                m_ShaderTagIdList.Add(new ShaderTagId("UniversalForward"));
                m_ShaderTagIdList.Add(new ShaderTagId("UniversalForwardOnly"));
            }

            m_RenderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);
            m_CameraSettings = cameraSettings;
        }

        private static void ExecutePass(PassData passData, RasterCommandBuffer cmd, RendererList rendererList, bool isYFlipped)
        {
            Camera camera = passData.cameraData.camera;

            // In case of camera stacking we need to take the viewport rect from base camera
            Rect pixelRect = passData.cameraData.pixelRect;
            float cameraAspect = (float)pixelRect.width / (float)pixelRect.height;

            bool matricesOverridden = false;
            if (passData.cameraSettings.overrideCamera)
            {
                if (passData.cameraData.xr.enabled)
                {
                    Debug.LogWarning("RenderObjects pass is configured to override camera matrices. While rendering in stereo camera matrices cannot be overridden.");
                }
                else
                {
                    Matrix4x4 projectionMatrix = Matrix4x4.Perspective(passData.cameraSettings.cameraFieldOfView, cameraAspect,
                        camera.nearClipPlane, camera.farClipPlane);
                    projectionMatrix = GL.GetGPUProjectionMatrix(projectionMatrix, isYFlipped);

                    Matrix4x4 viewMatrix = passData.cameraData.GetViewMatrix();
                    Vector4 cameraTranslation = viewMatrix.GetColumn(3);
                    viewMatrix.SetColumn(3, cameraTranslation + passData.cameraSettings.offset);

                    RenderingUtils.SetViewAndProjectionMatrices(cmd, viewMatrix, projectionMatrix, false);
                    matricesOverridden = true;
                }
            }
            else if (passData.useNonJitteredProjection)
            {
                // A layer redrawn after a temporal upscaler is already at display resolution. Drawing it
                // with the frame's TAA/FSR2/DLSS projection jitter makes head-locked UI visibly hop even
                // though the scene behind it has already been stabilized by the upscaler.
                if (passData.cameraData.xr.enabled)
                {
                    // Go through URP's normal XR camera setup so stereo shader constants and late-latch
                    // properties stay valid. The earlier experiment wrote the XR matrices directly,
                    // which could make the world-space menu disappear entirely.
                    ScriptableRenderer.SetCameraMatrices(cmd, passData.cameraData, false, isYFlipped, useJitter: false, forceXRUpdate: true);
                }
                else
                {
                    Matrix4x4 projectionMatrix = GL.GetGPUProjectionMatrix(passData.cameraData.GetProjectionMatrixNoJitter(0), isYFlipped);
                    RenderingUtils.SetViewAndProjectionMatrices(cmd, passData.cameraData.GetViewMatrix(), projectionMatrix, false);
                }
                matricesOverridden = true;
            }

            var activeDebugHandler = GetActiveDebugHandler(passData.cameraData);
            if (activeDebugHandler != null)
            {
                passData.debugRendererLists.DrawWithRendererList(cmd);
            }
            else
            {
                cmd.DrawRendererList(rendererList);
            }

            bool restoreCameraMatrices = passData.useNonJitteredProjection
                || (passData.cameraSettings.overrideCamera && passData.cameraSettings.restoreCamera);
            if (matricesOverridden && restoreCameraMatrices)
            {
                if (passData.cameraData.xr.enabled)
                {
                    ScriptableRenderer.SetCameraMatrices(cmd, passData.cameraData, false, isYFlipped, useJitter: true, forceXRUpdate: true);
                }
                else
                {
                    RenderingUtils.SetViewAndProjectionMatrices(cmd, passData.cameraData.GetViewMatrix(), GL.GetGPUProjectionMatrix(passData.cameraData.GetProjectionMatrix(0), isYFlipped), false);
                }
            }
        }

        private class PassData
        {
            internal RenderObjects.CustomCameraSettings cameraSettings;
            internal RenderPassEvent renderPassEvent;
            internal bool useNonJitteredProjection;

            internal TextureHandle color;
            internal RendererListHandle rendererListHdl;
            internal DebugRendererLists debugRendererLists;

            internal UniversalCameraData cameraData;

            // Required for code sharing purpose between RG and non-RG.
            internal RendererList rendererList;
        }

        private class CompositePassData
        {
            internal TextureHandle source;
            internal TextureHandle overlay;
            internal Material material;
            internal int passIndex;
        }

        private void InitPassData(UniversalCameraData cameraData, ref PassData passData)
        {
            passData.cameraSettings = m_CameraSettings;
            passData.renderPassEvent = renderPassEvent;
            passData.useNonJitteredProjection = m_UseNonJitteredProjection;
            passData.cameraData = cameraData;
        }

        private void InitRendererLists(UniversalRenderingData renderingData, UniversalLightData lightData,
            ref PassData passData, RenderGraph renderGraph, bool premultiplyOverlayOutput = false)
        {
            SortingCriteria sortingCriteria = (renderQueueType == RenderQueueType.Transparent)
                ? SortingCriteria.CommonTransparent
                : passData.cameraData.defaultOpaqueSortFlags;
            DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(m_ShaderTagIdList, renderingData,
                passData.cameraData, lightData, sortingCriteria);
            drawingSettings.overrideMaterial = overrideMaterial;
            drawingSettings.overrideMaterialPassIndex = overrideMaterialPassIndex;
            drawingSettings.overrideShader = overrideShader;
            drawingSettings.overrideShaderPassIndex = overrideShaderPassIndex;

            RenderStateBlock renderStateBlock = m_RenderStateBlock;
            if (premultiplyOverlayOutput)
            {
                // Render the UI into a transparent intermediate as premultiplied color with straight
                // coverage alpha. This lets the composite pass blend it over the upscaled scene exactly
                // once while preserving material-driven stencil state used by uGUI Mask components.
                RenderTargetBlendState overlayBlend = new RenderTargetBlendState(
                    sourceColorBlendMode: BlendMode.SrcAlpha,
                    destinationColorBlendMode: BlendMode.OneMinusSrcAlpha,
                    sourceAlphaBlendMode: BlendMode.One,
                    destinationAlphaBlendMode: BlendMode.OneMinusSrcAlpha);
                renderStateBlock.blendState = new BlendState { blendState0 = overlayBlend };
                renderStateBlock.mask |= RenderStateMask.Blend;
            }

            var activeDebugHandler = GetActiveDebugHandler(passData.cameraData);
            if (activeDebugHandler != null)
            {
                passData.debugRendererLists = activeDebugHandler.CreateRendererListsWithDebugRenderState(renderGraph,
                    ref renderingData.cullResults, ref drawingSettings, ref m_FilteringSettings, ref renderStateBlock);
            }
            else
            {
                RenderingUtils.CreateRendererListWithRenderStateBlock(renderGraph, ref renderingData.cullResults, drawingSettings,
                    m_FilteringSettings, renderStateBlock, ref passData.rendererListHdl);
            }
        }

        private void RecordPostUpscaleOverlay(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            TextureHandle sceneColor = resourceData.activeColorTexture;
            TextureDesc sceneDesc = sceneColor.GetDescriptor(renderGraph);

            TextureDesc overlayColorDesc = sceneDesc;
            overlayColorDesc.name = "_PostUpscaleOverlayUIColor";
            overlayColorDesc.clearBuffer = true;
            overlayColorDesc.clearColor = Color.clear;
            overlayColorDesc.enableRandomWrite = false;
            overlayColorDesc.msaaSamples = MSAASamples.None;
            overlayColorDesc.useMipMap = false;
            overlayColorDesc.autoGenerateMips = false;
            overlayColorDesc.discardBuffer = true;
            overlayColorDesc.filterMode = FilterMode.Bilinear;
            if (!GraphicsFormatUtility.HasAlphaChannel(overlayColorDesc.format))
                overlayColorDesc.format = GraphicsFormat.R16G16B16A16_SFloat;
            TextureHandle overlayColor = renderGraph.CreateTexture(overlayColorDesc);

            TextureDesc overlayDepthDesc = overlayColorDesc;
            overlayDepthDesc.name = "_PostUpscaleOverlayUIDepth";
            overlayDepthDesc.format = CoreUtils.GetDefaultDepthStencilFormat();
            overlayDepthDesc.clearBuffer = true;
            overlayDepthDesc.clearColor = SystemInfo.usesReversedZBuffer ? Color.black : Color.white;
            overlayDepthDesc.enableRandomWrite = false;
            overlayDepthDesc.filterMode = FilterMode.Point;
            TextureHandle overlayDepth = renderGraph.CreateTexture(overlayDepthDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
            {
                InitPassData(cameraData, ref passData);
                passData.color = overlayColor;

                builder.SetRenderAttachment(overlayColor, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(overlayDepth, AccessFlags.ReadWrite);

                TextureHandle mainShadowsTexture = resourceData.mainShadowsTexture;
                TextureHandle additionalShadowsTexture = resourceData.additionalShadowsTexture;
                if (mainShadowsTexture.IsValid())
                    builder.UseTexture(mainShadowsTexture, AccessFlags.Read);
                if (additionalShadowsTexture.IsValid())
                    builder.UseTexture(additionalShadowsTexture, AccessFlags.Read);

                TextureHandle[] dBufferHandles = resourceData.dBuffer;
                for (int i = 0; i < dBufferHandles.Length; ++i)
                {
                    TextureHandle dBuffer = dBufferHandles[i];
                    if (dBuffer.IsValid())
                        builder.UseTexture(dBuffer, AccessFlags.Read);
                }

                TextureHandle ssaoTexture = resourceData.ssaoTexture;
                if (ssaoTexture.IsValid())
                    builder.UseTexture(ssaoTexture, AccessFlags.Read);

                InitRendererLists(renderingData, lightData, ref passData, renderGraph, premultiplyOverlayOutput: true);
                var activeDebugHandler = GetActiveDebugHandler(passData.cameraData);
                if (activeDebugHandler != null)
                    passData.debugRendererLists.PrepareRendererListForRasterPass(builder);
                else
                    builder.UseRendererList(passData.rendererListHdl);

                builder.AllowGlobalStateModification(true);
                if (cameraData.xr.enabled)
                {
                    builder.EnableFoveatedRasterization(false);
                    if (cameraData.xr.multipassId == 0)
                        builder.SetExtendedFeatureFlags(ExtendedFeatureFlags.MultiviewRenderRegionsCompatible);
                }

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    var isYFlipped = RenderingUtils.IsHandleYFlipped(rgContext, in data.color);
                    ExecutePass(data, rgContext.cmd, data.rendererListHdl, isYFlipped);
                });
            }

            Material compositeMaterial = Blitter.GetBlitMaterial(sceneDesc.dimension);
            int compositePassIndex = compositeMaterial != null ? compositeMaterial.FindPass(k_PostUpscaleCompositePassName) : -1;
            if (compositeMaterial == null || compositePassIndex < 0)
            {
                Debug.LogError("Unable to composite post-upscale OverlayUI: CoreBlit composite pass is unavailable.");
                return;
            }

            TextureDesc compositedDesc = sceneDesc;
            compositedDesc.name = "_PostUpscaleOverlayComposite";
            compositedDesc.clearBuffer = false;
            compositedDesc.enableRandomWrite = false;
            compositedDesc.msaaSamples = MSAASamples.None;
            compositedDesc.useMipMap = false;
            compositedDesc.autoGenerateMips = false;
            compositedDesc.discardBuffer = false;
            TextureHandle compositedColor = renderGraph.CreateTexture(compositedDesc);

            using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>(
                       "Composite Post-Upscale OverlayUI", out var passData, profilingSampler))
            {
                passData.source = sceneColor;
                passData.overlay = overlayColor;
                passData.material = compositeMaterial;
                passData.passIndex = compositePassIndex;

                builder.UseTexture(sceneColor, AccessFlags.Read);
                builder.UseTexture(overlayColor, AccessFlags.Read);
                builder.SetRenderAttachment(compositedColor, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (CompositePassData data, RasterGraphContext context) =>
                {
                    RTHandle sourceHandle = data.source;
                    data.material.SetTexture(s_PostUpscaleOverlayTextureId, data.overlay);
                    Vector2 viewportScale = sourceHandle.useScaling
                        ? new Vector2(sourceHandle.rtHandleProperties.rtHandleScale.x, sourceHandle.rtHandleProperties.rtHandleScale.y)
                        : Vector2.one;
                    Blitter.BlitTexture(context.cmd, sourceHandle, viewportScale, data.material, data.passIndex);
                });
            }

            resourceData.cameraColor = compositedColor;
        }

        /// <inheritdoc />
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();

            if (renderAfterTemporalUpscaling)
            {
                RecordPostUpscaleOverlay(renderGraph, frameData);
                return;
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

                InitPassData(cameraData, ref passData);

                passData.color = resourceData.activeColorTexture;
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);

                // TODO: Take into account user-specific settings to decide depth flag
                if (cameraData.imageScalingMode != ImageScalingMode.Upscaling || passData.renderPassEvent != RenderPassEvent.AfterRenderingPostProcessing)
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                TextureHandle mainShadowsTexture = resourceData.mainShadowsTexture;
                TextureHandle additionalShadowsTexture = resourceData.additionalShadowsTexture;

                if (mainShadowsTexture.IsValid())
                    builder.UseTexture(mainShadowsTexture, AccessFlags.Read);

                if (additionalShadowsTexture.IsValid())
                    builder.UseTexture(additionalShadowsTexture, AccessFlags.Read);

                TextureHandle[] dBufferHandles = resourceData.dBuffer;
                for (int i = 0; i < dBufferHandles.Length; ++i)
                {
                    TextureHandle dBuffer = dBufferHandles[i];
                    if (dBuffer.IsValid())
                        builder.UseTexture(dBuffer, AccessFlags.Read);
                }

                TextureHandle ssaoTexture = resourceData.ssaoTexture;
                if (ssaoTexture.IsValid())
                    builder.UseTexture(ssaoTexture, AccessFlags.Read);

                InitRendererLists(renderingData, lightData, ref passData, renderGraph);
                var activeDebugHandler = GetActiveDebugHandler(passData.cameraData);
                if (activeDebugHandler != null)
                {
                    passData.debugRendererLists.PrepareRendererListForRasterPass(builder);
                }
                else
                {
                    builder.UseRendererList(passData.rendererListHdl);
                }

                builder.AllowGlobalStateModification(true);
                if (cameraData.xr.enabled)
                {
                    // The native-resolution UI redraw should not inherit the scene's foveated rasterization.
                    // Otherwise the menu can still lose detail even though it bypasses the upscaler input.
                    bool allowFoveatedRasterization = !passData.useNonJitteredProjection
                        && cameraData.xr.supportsFoveatedRendering
                        && cameraData.xrUniversal.canFoveateIntermediatePasses;
                    builder.EnableFoveatedRasterization(allowFoveatedRasterization);
                    // Apply MultiviewRenderRegionsCompatible flag only to the peripheral view in Quad Views
                    if (cameraData.xr.multipassId == 0)
                    {
                        builder.SetExtendedFeatureFlags(ExtendedFeatureFlags.MultiviewRenderRegionsCompatible);
                    }
                }

                builder.SetRenderFunc(static (PassData data, RasterGraphContext rgContext) =>
                {
                    var isYFlipped = RenderingUtils.IsHandleYFlipped(rgContext, in data.color);
                    ExecutePass(data, rgContext.cmd, data.rendererListHdl, isYFlipped);
                });
            }
        }
    }
}