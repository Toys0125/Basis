#if ENABLE_UPSCALER_FRAMEWORK
using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
#if ENABLE_NVIDIA && ENABLE_NVIDIA_MODULE
using UnityEngine.NVIDIA;
#endif
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
#if ENABLE_NVIDIA && ENABLE_NVIDIA_MODULE
    /// <summary>
    /// NVIDIA DLSS Super Resolution integration for URP that is safe to use with XR multipass.
    /// Unity's stock 6.5 DLSS IUpscaler currently reports supportsXR=false and owns one temporal
    /// context. VR needs one persistent DLSS history per eye, otherwise the two eyes feed each
    /// other's temporal history on alternating renders.
    /// </summary>
    public sealed class BasisDlssXrUpscaler : AbstractUpscaler
    {
        public const string UpscalerName = "Basis NVIDIA DLSS 4 XR";

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
            public DLSSContext Context;
            public Vector2Int InputResolution;
            public Vector2Int OutputResolution;
            public DLSSQuality Quality;
        }

        private sealed class PassData
        {
            public BasisDlssXrUpscaler Upscaler;
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
            public DLSSQuality Quality;
            public DLSSCommandExecutionData ExecutionData;
            public TextureHandle ColorInput;
            public TextureHandle Depth;
            public TextureHandle MotionVectors;
            public TextureHandle ColorOutput;
        }

        private readonly Dictionary<ViewKey, ViewState> _views = new();
        private UnityEngine.NVIDIA.GraphicsDevice _device;
        private Vector2Int _inputResolution = Vector2Int.one;
        private Vector2Int _outputResolution = Vector2Int.one;
        private bool _ready;
        private bool _warnedUnsupportedStereoLayout;
        private static string _qualityMode = "automatic";

        public BasisDlssXrUpscaler()
        {
            _ready = TryCreateDevice(out _device);
        }

        public override string name => UpscalerName;
        public override bool isTemporal => true;
        public override bool supportsSharpening => false;
        public override bool supportsXR => true;

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

        private static bool TryCreateDevice(out UnityEngine.NVIDIA.GraphicsDevice device)
        {
            device = null;
            if (!NVUnityPlugin.IsLoaded() && !NVUnityPlugin.Load())
            {
                Debug.LogWarning("[Basis DLSS] NVIDIA native plugin could not be loaded.");
                return false;
            }

            if (SystemInfo.graphicsDeviceVendor.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Debug.LogWarning("[Basis DLSS] DLSS requires an NVIDIA GPU.");
                return false;
            }

            device = UnityEngine.NVIDIA.GraphicsDevice.device ?? UnityEngine.NVIDIA.GraphicsDevice.CreateGraphicsDevice();
            if (device == null || !device.IsFeatureAvailable(GraphicsDeviceFeature.DLSS))
            {
                Debug.LogWarning("[Basis DLSS] DLSS is not available on this GPU/driver.");
                device = null;
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
                DLSSQuality quality = ResolveQuality(preUpscaleResolution, postUpscaleResolution);
                _device.GetOptimalSettings(
                    (uint)postUpscaleResolution.x,
                    (uint)postUpscaleResolution.y,
                    quality,
                    out OptimalDLSSSettingsData optimalSettings);
                preUpscaleResolution.x = (int)optimalSettings.outRenderWidth;
                preUpscaleResolution.y = (int)optimalSettings.outRenderHeight;
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

            // The NVIDIA Unity API exposes a single-view DLSS context. Basis requests separate
            // XR eye textures while DLSS is active so each invocation is one view and can retain
            // its own temporal history. Refuse array/double-wide input rather than mixing eyes.
            if (io.enableTexArray || io.numActiveViews != 1)
            {
                if (!_warnedUnsupportedStereoLayout)
                {
                    _warnedUnsupportedStereoLayout = true;
                    Debug.LogWarning("[Basis DLSS] XR runtime did not switch to separate eye textures; DLSS pass is skipped for this frame.");
                }
                return;
            }

            _inputResolution = io.preUpscaleResolution;
            _outputResolution = io.postUpscaleResolution;

            DLSSQuality quality = ResolveQuality(io.preUpscaleResolution, io.postUpscaleResolution);
            ViewKey key = new(io.cameraInstanceID, io.eyeIndex);
            if (!_views.TryGetValue(key, out ViewState state))
            {
                state = new ViewState();
                _views.Add(key, state);
            }

            bool reinitialize = state.Context == null
                || state.InputResolution != io.preUpscaleResolution
                || state.OutputResolution != io.postUpscaleResolution
                || state.Quality != quality;

            TextureHandle outputColor;
            {
                TextureDesc inputDesc = io.cameraColor.GetDescriptor(renderGraph);
                TextureDesc outputDesc = inputDesc;
                outputDesc.width = io.postUpscaleResolution.x;
                outputDesc.height = io.postUpscaleResolution.y;
                outputDesc.format = inputDesc.format;
                outputDesc.msaaSamples = MSAASamples.None;
                outputDesc.useMipMap = false;
                outputDesc.autoGenerateMips = false;
                outputDesc.useDynamicScale = false;
                outputDesc.anisoLevel = 0;
                outputDesc.discardBuffer = false;
                outputDesc.enableRandomWrite = true;
                outputDesc.name = "_BasisDlssXrOutput";
                outputDesc.clearBuffer = false;
                outputDesc.filterMode = FilterMode.Bilinear;
                outputColor = renderGraph.CreateTexture(outputDesc);
            }

            using (var builder = renderGraph.AddUnsafePass<PassData>(
                "Basis NVIDIA DLSS XR",
                out PassData passData,
                new ProfilingSampler("Basis DLSS XR")))
            {
                float motionVectorSign = io.motionVectorDirection == UpscalingIO.MotionVectorDirection.PreviousFrameToCurrentFrame ? -1.0f : 1.0f;
                float motionVectorScaleX = io.motionVectorDomain == UpscalingIO.MotionVectorDomain.NDC ? io.motionVectorTextureSize.x : 1.0f;
                float motionVectorScaleY = io.motionVectorDomain == UpscalingIO.MotionVectorDomain.NDC ? io.motionVectorTextureSize.y : 1.0f;

                passData.Upscaler = this;
                passData.State = state;
                passData.Reinitialize = reinitialize;
                passData.Quality = quality;
                passData.ExecutionData.mvScaleX = motionVectorSign * motionVectorScaleX;
                passData.ExecutionData.mvScaleY = motionVectorSign * motionVectorScaleY;
                passData.ExecutionData.subrectOffsetX = 0;
                passData.ExecutionData.subrectOffsetY = 0;
                passData.ExecutionData.subrectWidth = (uint)io.preUpscaleResolution.x;
                passData.ExecutionData.subrectHeight = (uint)io.preUpscaleResolution.y;
                passData.ExecutionData.jitterOffsetX = io.subpixelJitter.x;
                passData.ExecutionData.jitterOffsetY = io.subpixelJitter.y;
                passData.ExecutionData.preExposure = Mathf.Clamp(io.preExposureValue, 0.20f, 2.0f);
                passData.ExecutionData.invertYAxis = io.flippedY ? 1u : 0u;
                passData.ExecutionData.invertXAxis = io.flippedX ? 1u : 0u;
                passData.ExecutionData.reset = io.resetHistory ? 1 : 0;

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
                    {
                        data.Upscaler.RecreateContext(data.State, cmd, data);
                    }

                    if (data.State.Context == null)
                        return;

                    data.State.Context.executeData = data.ExecutionData;
                    DLSSTextureTable textures = new()
                    {
                        colorInput = data.ColorInput,
                        depth = data.Depth,
                        motionVectors = data.MotionVectors,
                        colorOutput = data.ColorOutput,
                    };
                    data.Upscaler._device.ExecuteDLSS(cmd, data.State.Context, textures);
                });
            }

            io.cameraColor = outputColor;
            state.InputResolution = io.preUpscaleResolution;
            state.OutputResolution = io.postUpscaleResolution;
            state.Quality = quality;
        }

        private void RecreateContext(ViewState state, CommandBuffer cmd, PassData data)
        {
            if (state.Context != null)
            {
                _device.DestroyFeature(cmd, state.Context);
                state.Context = null;
            }

            bool lowResolutionMotionVectors = data.MotionVectorSizeX <= data.ColorInputSizeX
                || data.MotionVectorSizeY <= data.ColorInputSizeY;

            DLSSCommandInitializationData settings = new();
            settings.SetFlag(DLSSFeatureFlags.IsHDR, data.InputIsHdr);
            settings.SetFlag(DLSSFeatureFlags.MVLowRes, lowResolutionMotionVectors);
            settings.SetFlag(DLSSFeatureFlags.DepthInverted, data.InvertedDepth);
            settings.SetFlag(DLSSFeatureFlags.MVJittered, data.MotionVectorsAreJittered);
            settings.inputRTWidth = data.ColorInputSizeX;
            settings.inputRTHeight = data.ColorInputSizeY;
            settings.outputRTWidth = data.ColorOutputSizeX;
            settings.outputRTHeight = data.ColorOutputSizeY;
            settings.quality = data.Quality;
            state.Context = _device.CreateFeature(cmd, settings);
            Debug.Log($"[Basis DLSS] Running {data.Quality}: {data.ColorInputSizeX}x{data.ColorInputSizeY} -> {data.ColorOutputSizeX}x{data.ColorOutputSizeY}");
        }

        private static DLSSQuality ResolveQuality(Vector2Int input, Vector2Int output)
        {
            switch (_qualityMode)
            {
                case "quality":
                    return DLSSQuality.MaximumQuality;
                case "balanced":
                    return DLSSQuality.Balanced;
                case "performance":
                    return DLSSQuality.MaximumPerformance;
                case "ultra performance":
                    return DLSSQuality.UltraPerformance;
            }

            float scale = output.x > 0 ? (float)input.x / output.x : 1.0f;
            if (scale >= 0.62f)
                return DLSSQuality.MaximumQuality;
            if (scale >= 0.54f)
                return DLSSQuality.Balanced;
            if (scale >= 0.42f)
                return DLSSQuality.MaximumPerformance;
            return DLSSQuality.UltraPerformance;
        }
    }

    internal static class BasisDlssXrRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            UpscalerRegistry.Register<BasisDlssXrUpscaler>(BasisDlssXrUpscaler.UpscalerName);
#endif
        }
    }
#else
    /// <summary>
    /// Compile-time fallback for platforms where Unity does not expose its NVIDIA module.
    /// This keeps URP and Basis settings portable while preventing an unavailable DLSS
    /// implementation from registering with the upscaler framework.
    /// </summary>
    public sealed class BasisDlssXrUpscaler : AbstractUpscaler
    {
        public const string UpscalerName = "Basis NVIDIA DLSS 4 XR";
        public static bool IsRuntimeSupported() => false;
        public static void SetQualityMode(string qualityMode) { }
        public override string name => UpscalerName;
        public override bool isTemporal => true;
        public override bool supportsSharpening => false;
        public override bool supportsXR => false;
    }
#endif
}
#endif
