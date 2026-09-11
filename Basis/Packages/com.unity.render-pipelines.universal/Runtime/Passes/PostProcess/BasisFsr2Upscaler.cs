#if ENABLE_UPSCALER_FRAMEWORK
using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
#if ENABLE_AMD && ENABLE_AMD_MODULE
using UnityEngine.AMD;
#endif
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
#if ENABLE_AMD && ENABLE_AMD_MODULE
    /// <summary>
    /// FSR2 integration for Basis using one native temporal context per camera/view.
    /// Core RP 17.5 keeps one context on the shared FSR2 provider and only rebuilds it
    /// when the output resolution changes. That allows cameras and quality/input-size
    /// changes to reuse incompatible history. Newer Core RP versions moved temporal
    /// upscalers to per-camera contexts; this provider backports that behavior.
    /// </summary>
    public sealed class BasisFsr2Upscaler : AbstractUpscaler
    {
        public const string UpscalerName = "Basis FidelityFX Super Resolution 2";

        private readonly struct ViewKey : IEquatable<ViewKey>
        {
            public readonly ulong CameraId;
            public readonly int EyeIndex;

            public ViewKey(ulong cameraId, int eyeIndex)
            {
                CameraId = cameraId;
                EyeIndex = eyeIndex;
            }

            public bool Equals(ViewKey other) => CameraId == other.CameraId && EyeIndex == other.EyeIndex;
            public override bool Equals(object obj) => obj is ViewKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(CameraId, EyeIndex);
        }

        private sealed class ViewState
        {
            public FSR2Context Context;
            public Vector2Int InputResolution;
            public Vector2Int OutputResolution;
            public bool InputIsHdr;
            public bool InvertedDepth;
            public bool DisplayResolutionMotionVectors;
            public bool MotionVectorsAreJittered;
        }

        private sealed class PassData
        {
            public BasisFsr2Upscaler Upscaler;
            public ViewState State;
            public bool Reinitialize;
            public uint ColorInputSizeX;
            public uint ColorInputSizeY;
            public uint ColorOutputSizeX;
            public uint ColorOutputSizeY;
            public uint MotionVectorSizeX;
            public uint MotionVectorSizeY;
            public bool InvertedDepth;
            public bool InputIsHdr;
            public bool MotionVectorsAreJittered;
            public FSR2CommandExecutionData ExecutionData;
            public TextureHandle ColorInput;
            public TextureHandle Depth;
            public TextureHandle MotionVectors;
            public TextureHandle ColorOutput;
        }

        private readonly Dictionary<ViewKey, ViewState> _views = new();
        private UnityEngine.AMD.GraphicsDevice _device;
        private Vector2Int _inputResolution = Vector2Int.one;
        private Vector2Int _outputResolution = Vector2Int.one;
        private bool _ready;
        private static string _qualityMode = "automatic";

        public BasisFsr2Upscaler()
        {
            _ready = TryCreateDevice(out _device);
        }

        public override string name => UpscalerName;
        public override bool isTemporal => true;
        public override bool supportsSharpening => true;
        public override bool supportsXR => false;

        public static bool IsRuntimeSupported()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return TryCreateDevice(out _);
#else
            return false;
#endif
        }

        public static void SetQualityMode(string qualityMode)
        {
            _qualityMode = string.IsNullOrWhiteSpace(qualityMode)
                ? "automatic"
                : qualityMode.Trim().ToLowerInvariant();
        }

        private static bool TryCreateDevice(out UnityEngine.AMD.GraphicsDevice device)
        {
            device = null;
            if (!AMDUnityPlugin.IsLoaded() && !AMDUnityPlugin.Load())
            {
                Debug.LogWarning("[Basis FSR2] AMD native plugin could not be loaded.");
                return false;
            }

            device = UnityEngine.AMD.GraphicsDevice.device ?? UnityEngine.AMD.GraphicsDevice.CreateGraphicsDevice();
            if (device == null)
            {
                Debug.LogWarning("[Basis FSR2] AMD graphics device could not be created.");
                return false;
            }

            return true;
        }

        public override void CalculateJitter(int frameIndex, out Vector2 jitter, out bool allowScaling)
        {
            float upscaleRatio = (float)_outputResolution.x / Mathf.Max(1, _inputResolution.x);
            int phaseCount = Mathf.Max(1, (int)(8.0f * upscaleRatio * upscaleRatio));
            int haltonIndex = (frameIndex % phaseCount) + 1;
            jitter = new Vector2(
                HaltonSequence.Get(haltonIndex, 2) - 0.5f,
                HaltonSequence.Get(haltonIndex, 3) - 0.5f);
            allowScaling = false;
        }

        public override void NegotiatePreUpscaleResolution(ref Vector2Int preUpscaleResolution, Vector2Int postUpscaleResolution)
        {
            if (_qualityMode != "automatic" && _device != null)
            {
                FSR2Quality quality = ResolveQuality();
                _device.GetRenderResolutionFromQualityMode(
                    quality,
                    (uint)postUpscaleResolution.x,
                    (uint)postUpscaleResolution.y,
                    out uint renderResolutionX,
                    out uint renderResolutionY);
                preUpscaleResolution.x = (int)renderResolutionX;
                preUpscaleResolution.y = (int)renderResolutionY;
            }

            _inputResolution = preUpscaleResolution;
            _outputResolution = postUpscaleResolution;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!_ready)
            {
                if (_device == null)
                    _ready = TryCreateDevice(out _device);
                if (!_ready)
                    return;
            }

            UpscalingIO io = frameData.Get<UpscalingIO>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            _inputResolution = io.preUpscaleResolution;
            _outputResolution = io.postUpscaleResolution;

            ViewKey key = new(io.cameraInstanceID, io.eyeIndex);
            if (!_views.TryGetValue(key, out ViewState state))
            {
                state = new ViewState();
                _views.Add(key, state);
            }

            bool displayResolutionMotionVectors = io.motionVectorTextureSize.x == io.postUpscaleResolution.x
                && io.motionVectorTextureSize.y == io.postUpscaleResolution.y;
            bool reinitialize = state.Context == null
                || state.InputResolution != io.preUpscaleResolution
                || state.OutputResolution != io.postUpscaleResolution
                || state.InputIsHdr != io.hdrInput
                || state.InvertedDepth != io.invertedDepth
                || state.DisplayResolutionMotionVectors != displayResolutionMotionVectors
                || state.MotionVectorsAreJittered != io.jitteredMotionVectors;

            TextureHandle outputColor;
            {
                TextureDesc inputDesc = io.cameraColor.GetDescriptor(renderGraph);
                TextureDesc outputDesc = inputDesc;
                outputDesc.width = io.postUpscaleResolution.x;
                outputDesc.height = io.postUpscaleResolution.y;
                outputDesc.format = GraphicsFormatUtility.GetLinearFormat(inputDesc.format);
                outputDesc.msaaSamples = MSAASamples.None;
                outputDesc.useMipMap = false;
                outputDesc.autoGenerateMips = false;
                outputDesc.useDynamicScale = false;
                outputDesc.anisoLevel = 0;
                outputDesc.discardBuffer = false;
                outputDesc.enableRandomWrite = true;
                outputDesc.name = "_BasisFSR2OutputTarget";
                outputDesc.clearBuffer = false;
                outputDesc.filterMode = FilterMode.Bilinear;
                outputColor = renderGraph.CreateTexture(outputDesc);
            }

            using (var builder = renderGraph.AddUnsafePass<PassData>(
                "Basis FidelityFX Super Resolution 2",
                out PassData passData,
                new ProfilingSampler("Basis FSR2")))
            {
                float motionVectorSign = io.motionVectorDirection == UpscalingIO.MotionVectorDirection.PreviousFrameToCurrentFrame ? -1.0f : 1.0f;
                float motionVectorScaleX = io.motionVectorDomain == UpscalingIO.MotionVectorDomain.NDC ? io.motionVectorTextureSize.x : 1.0f;
                float motionVectorScaleY = io.motionVectorDomain == UpscalingIO.MotionVectorDomain.NDC ? io.motionVectorTextureSize.y : 1.0f;

                passData.Upscaler = this;
                passData.State = state;
                passData.Reinitialize = reinitialize;
                passData.ExecutionData.enableSharpening = 0;
                passData.ExecutionData.sharpness = 0.92f;
                passData.ExecutionData.MVScaleX = motionVectorSign * motionVectorScaleX;
                passData.ExecutionData.MVScaleY = motionVectorSign * motionVectorScaleY;
                passData.ExecutionData.renderSizeWidth = (uint)io.preUpscaleResolution.x;
                passData.ExecutionData.renderSizeHeight = (uint)io.preUpscaleResolution.y;
                passData.ExecutionData.jitterOffsetX = cameraData.subpixelJitter.x;
                passData.ExecutionData.jitterOffsetY = cameraData.subpixelJitter.y;
                passData.ExecutionData.cameraNear = io.nearClipPlane;
                passData.ExecutionData.cameraFar = io.farClipPlane;
                passData.ExecutionData.cameraFovAngleVertical = 2.0f * (float)Math.PI * (1.0f / 360.0f) * io.fieldOfViewDegrees;
                passData.ExecutionData.preExposure = 1.0f;
                passData.ExecutionData.frameTimeDelta = io.deltaTime * 1000.0f;
                passData.ExecutionData.reset = io.resetHistory || reinitialize ? 1 : 0;

                builder.UseTexture(io.cameraColor);
                builder.UseTexture(io.cameraDepth);
                builder.UseTexture(io.motionVectorColor);
                builder.UseTexture(outputColor, AccessFlags.Write);

                passData.ColorInput = io.cameraColor;
                passData.Depth = io.cameraDepth;
                passData.MotionVectors = io.motionVectorColor;
                passData.ColorOutput = outputColor;
                passData.ColorInputSizeX = (uint)io.preUpscaleResolution.x;
                passData.ColorInputSizeY = (uint)io.preUpscaleResolution.y;
                passData.ColorOutputSizeX = (uint)io.postUpscaleResolution.x;
                passData.ColorOutputSizeY = (uint)io.postUpscaleResolution.y;
                passData.MotionVectorSizeX = (uint)io.motionVectorTextureSize.x;
                passData.MotionVectorSizeY = (uint)io.motionVectorTextureSize.y;
                passData.InvertedDepth = io.invertedDepth;
                passData.InputIsHdr = io.hdrInput;
                passData.MotionVectorsAreJittered = io.jitteredMotionVectors;

                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    if (data.Reinitialize)
                        data.Upscaler.RecreateContext(data.State, cmd, data);

                    if (data.State.Context == null)
                        return;

                    data.State.Context.executeData = data.ExecutionData;
                    FSR2TextureTable textures = new()
                    {
                        colorInput = data.ColorInput,
                        depth = data.Depth,
                        motionVectors = data.MotionVectors,
                        colorOutput = data.ColorOutput,
                    };
                    data.Upscaler._device.ExecuteFSR2(cmd, data.State.Context, textures);
                });
            }

            io.cameraColor = outputColor;
            state.InputResolution = io.preUpscaleResolution;
            state.OutputResolution = io.postUpscaleResolution;
            state.InputIsHdr = io.hdrInput;
            state.InvertedDepth = io.invertedDepth;
            state.DisplayResolutionMotionVectors = displayResolutionMotionVectors;
            state.MotionVectorsAreJittered = io.jitteredMotionVectors;
        }

        private void RecreateContext(ViewState state, CommandBuffer cmd, PassData data)
        {
            if (state.Context != null)
            {
                _device.DestroyFeature(cmd, state.Context);
                state.Context = null;
            }

            bool displayResolutionMotionVectors = data.MotionVectorSizeX == data.ColorOutputSizeX
                && data.MotionVectorSizeY == data.ColorOutputSizeY;

            FSR2CommandInitializationData settings = new();
            settings.SetFlag(FfxFsr2InitializationFlags.EnableHighDynamicRange, data.InputIsHdr);
            settings.SetFlag(FfxFsr2InitializationFlags.EnableDisplayResolutionMotionVectors, displayResolutionMotionVectors);
            settings.SetFlag(FfxFsr2InitializationFlags.DepthInverted, data.InvertedDepth);
            settings.SetFlag(FfxFsr2InitializationFlags.EnableMotionVectorsJitterCancellation, data.MotionVectorsAreJittered);
            settings.maxRenderSizeWidth = data.ColorInputSizeX;
            settings.maxRenderSizeHeight = data.ColorInputSizeY;
            settings.displaySizeWidth = data.ColorOutputSizeX;
            settings.displaySizeHeight = data.ColorOutputSizeY;
            state.Context = _device.CreateFeature(cmd, settings);
            Debug.Log($"[Basis FSR2] Running {data.ColorInputSizeX}x{data.ColorInputSizeY} -> {data.ColorOutputSizeX}x{data.ColorOutputSizeY}");
        }

        private static FSR2Quality ResolveQuality()
        {
            return _qualityMode switch
            {
                "balanced" => FSR2Quality.Balanced,
                "performance" => FSR2Quality.Performance,
                "ultra performance" => FSR2Quality.UltraPerformance,
                _ => FSR2Quality.Quality,
            };
        }
    }

    internal static class BasisFsr2Registration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            UpscalerRegistry.Register<BasisFsr2Upscaler>(BasisFsr2Upscaler.UpscalerName);
#endif
        }
    }
#else
    /// <summary>
    /// Compile-time fallback for platforms where Unity does not expose its AMD module.
    /// </summary>
    public sealed class BasisFsr2Upscaler : AbstractUpscaler
    {
        public const string UpscalerName = "Basis FidelityFX Super Resolution 2";
        public static bool IsRuntimeSupported() => false;
        public static void SetQualityMode(string qualityMode) { }
        public override string name => UpscalerName;
        public override bool isTemporal => true;
        public override bool supportsSharpening => true;
        public override bool supportsXR => false;
    }
#endif
}
#endif
