using System;
using Basis;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Produces a stabilized, mono spectator view for the desktop game window while the local
    /// player remains in VR. A capture camera renders into a persistent texture at the configured
    /// rate, and a cheap output camera presents the latest texture after the XR camera.
    /// </summary>
    public sealed class BasisVRDesktopViewDriver : MonoBehaviour
    {
        private enum HorizonStabilizationMode
        {
            Off,
            Reduced,
            Locked
        }

        // Basis has one local player and one desktop backbuffer. The singleton prevents an old
        // local-player hierarchy from continuing to own mirror callbacks during scene transitions.
        private static BasisVRDesktopViewDriver s_Instance;

        private const float PositionResponse = 12f;
        private const float RotationResponse = 7f;
        private const float RotationCatchup = 0.12f;
        private const float MaximumPositionLag = 0.10f;
        private const float PositionResetDistance = 0.50f;
        private const float RotationResetAngle = 65f;
        private const float ReducedRollFactor = 0.25f;
        private const float DefaultRenderRateHz = 60f;
        private const int MinimumTextureDimension = 16;
        private const int CaptureDepthBits = 24;

        // Keep the capture safely before the XR camera and the presenter after every ordinary
        // scene camera. The wide offset is deliberate because worlds may use non-zero depths.
        private const float CaptureDepthOffset = 100f;
        private const float OutputDepthOffset = 100f;

        // Renderer index 2 is DesktopRendererStabilizedView in Modified - Desktop.asset. It has
        // no world passes and exists only to execute BasisVRDesktopViewBlitFeature.
        private const int OutputRendererIndex = 2;

        private static readonly Rect FullViewport = new(0f, 0f, 1f, 1f);

        private BasisLocalCameraDriver _owner;
        private Camera _captureCamera;
        private Camera _outputCamera;
        private UniversalAdditionalCameraData _captureCameraData;
        private UniversalAdditionalCameraData _outputCameraData;
        private RenderTexture _captureTexture;
        private BasisRenderRateLimiter _renderRateLimiter = default;

        private Transform _trackingRoot;
        private Vector3 _smoothedLocalPosition;
        private Quaternion _smoothedLocalRotation = Quaternion.identity;
        private bool _poseInitialized;
        private bool _initialized;
        private bool _shuttingDown;
        private bool _ownerEnabled;
        private bool _active;
        private bool _forceCaptureNextFrame;
        private bool _hasCapturedFrame;
        private bool _cullingRegistered;
        private bool _hasOutputRenderer;
        private int _textureWidth;
        private int _textureHeight;
        private int _overlayUiLayer = -1;

        private float _requestedFov;
        private float _stabilizationStrength;
        private float _targetRenderRateHz;
        private bool _positionStabilization;
        private bool _rotationStabilization;
        private bool _showHud;
        private HorizonStabilizationMode _horizonMode;

        /// <summary>True while this view owns display zero.</summary>
        public bool IsActive => _active;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            s_Instance = null;
        }

        /// <summary>Initializes the spectator view for the active local camera driver.</summary>
        public void Initialize(BasisLocalCameraDriver owner)
        {
            if (owner == null || BasisDeviceManagement.IsMobileHardware())
            {
                return;
            }

            _shuttingDown = false;

            if (s_Instance != null && !ReferenceEquals(s_Instance, this))
            {
                BasisVRDesktopViewDriver previous = s_Instance;
                s_Instance = null;
                previous.Shutdown();
            }
            s_Instance = this;

            _owner = owner;
            _ownerEnabled = true;
            LoadCachedSettings();
            RefreshOutputRendererAvailability();

            if (!_initialized)
            {
                CreateCameras();
                Subscribe();
                UniversalRenderPipeline.RegisterXRMirrorViewRenderFilter(ShouldRenderXRMirrorView);
                _initialized = true;
            }

            SyncCaptureCameraSettings();
            RefreshState(forceCapture: true);
        }

        /// <summary>Temporarily releases desktop output while preserving the component.</summary>
        public void Suspend()
        {
            _ownerEnabled = false;
            SetActive(false, false);
        }

        /// <summary>Unsubscribes callbacks and destroys all transient camera resources.</summary>
        public void Shutdown()
        {
            if (_shuttingDown)
            {
                return;
            }
            _shuttingDown = true;

            if (!_initialized)
            {
                if (ReferenceEquals(s_Instance, this))
                {
                    s_Instance = null;
                }
                return;
            }

            Unsubscribe();
            UniversalRenderPipeline.UnregisterXRMirrorViewRenderFilter(ShouldRenderXRMirrorView);
            SetActive(false, false);

            if (_captureCamera != null)
            {
                Destroy(_captureCamera.gameObject);
                _captureCamera = null;
            }

            if (_outputCamera != null)
            {
                Destroy(_outputCamera.gameObject);
                _outputCamera = null;
            }

            _captureCameraData = null;
            _outputCameraData = null;
            _owner = null;
            _trackingRoot = null;
            _initialized = false;

            if (ReferenceEquals(s_Instance, this))
            {
                s_Instance = null;
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private void CreateCameras()
        {
            Transform parent = _owner.LocalPlayer != null ? _owner.LocalPlayer.transform : _owner.transform.parent;
            _overlayUiLayer = LayerMask.NameToLayer("OverlayUI");

            GameObject captureObject = new("Stabilized VR Desktop Capture")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            captureObject.transform.SetParent(parent, false);
            _captureCamera = captureObject.AddComponent<Camera>();
            _captureCameraData = captureObject.AddComponent<UniversalAdditionalCameraData>();
            _captureCameraData.allowXRRendering = false;
            _captureCameraData.SetRenderer(0);
            _captureCamera.stereoTargetEye = StereoTargetEyeMask.None;
            _captureCamera.rect = FullViewport;
            _captureCamera.enabled = false;

            GameObject outputObject = new("Stabilized VR Desktop Output")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            outputObject.transform.SetParent(parent, false);
            _outputCamera = outputObject.AddComponent<Camera>();
            _outputCameraData = outputObject.AddComponent<UniversalAdditionalCameraData>();
            _outputCameraData.allowXRRendering = false;
            _outputCameraData.renderPostProcessing = false;
            if (_hasOutputRenderer)
            {
                _outputCameraData.SetRenderer(OutputRendererIndex);
            }

            _outputCamera.stereoTargetEye = StereoTargetEyeMask.None;
            _outputCamera.clearFlags = CameraClearFlags.SolidColor;
            _outputCamera.backgroundColor = Color.black;
            // The output camera only triggers the presentation pass; it renders no scene geometry.
            _outputCamera.cullingMask = 0;
            _outputCamera.rect = FullViewport;
            _outputCamera.useOcclusionCulling = false;
            _outputCamera.allowHDR = false;
            _outputCamera.allowMSAA = false;
            _outputCamera.enabled = false;
        }

        private void Subscribe()
        {
            RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
            RenderPipelineManager.endCameraRendering += EndCameraRendering;
            BasisLocalCameraDriver.RenderSettingsApplied += OnRenderSettingsApplied;
            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
            BasisHandHeldCamera.DesktopOutputOverrideChanged += OnHandheldDesktopOutputChanged;

            BasisSettingsDefaults.EnableVRDesktopView.OnChanged += OnEnabledChanged;
            BasisSettingsDefaults.VRDesktopViewFOV.OnChanged += OnFovChanged;
            BasisSettingsDefaults.VRDesktopViewPositionStabilization.OnChanged += OnPositionStabilizationChanged;
            BasisSettingsDefaults.VRDesktopViewRotationStabilization.OnChanged += OnRotationStabilizationChanged;
            BasisSettingsDefaults.VRDesktopViewHorizonMode.OnChanged += OnHorizonModeChanged;
            BasisSettingsDefaults.VRDesktopViewStabilizationStrength.OnChanged += OnStabilizationStrengthChanged;
            BasisSettingsDefaults.VRDesktopViewShowHUD.OnChanged += OnShowHudChanged;
            BasisSettingsDefaults.VRDesktopViewRenderRate.OnChanged += OnRenderRateChanged;
        }

        private void Unsubscribe()
        {
            RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= EndCameraRendering;
            BasisLocalCameraDriver.RenderSettingsApplied -= OnRenderSettingsApplied;
            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
            BasisHandHeldCamera.DesktopOutputOverrideChanged -= OnHandheldDesktopOutputChanged;

            BasisSettingsDefaults.EnableVRDesktopView.OnChanged -= OnEnabledChanged;
            BasisSettingsDefaults.VRDesktopViewFOV.OnChanged -= OnFovChanged;
            BasisSettingsDefaults.VRDesktopViewPositionStabilization.OnChanged -= OnPositionStabilizationChanged;
            BasisSettingsDefaults.VRDesktopViewRotationStabilization.OnChanged -= OnRotationStabilizationChanged;
            BasisSettingsDefaults.VRDesktopViewHorizonMode.OnChanged -= OnHorizonModeChanged;
            BasisSettingsDefaults.VRDesktopViewStabilizationStrength.OnChanged -= OnStabilizationStrengthChanged;
            BasisSettingsDefaults.VRDesktopViewShowHUD.OnChanged -= OnShowHudChanged;
            BasisSettingsDefaults.VRDesktopViewRenderRate.OnChanged -= OnRenderRateChanged;
        }

        private bool ShouldRenderXRMirrorView(Camera camera)
        {
            if (_owner == null || !ReferenceEquals(camera, _owner.Camera))
            {
                return true;
            }

            // The handheld camera owns display zero directly. Suppress the native mirror even
            // though this spectator driver is inactive, otherwise camera-depth ordering becomes
            // an accidental dependency of the takeover path.
            if (BasisHandHeldCamera.IsDesktopOutputOverrideActive)
            {
                return false;
            }

            // The capture camera renders before the XR camera and the output camera renders after
            // it. Once capture completed this frame, suppressing the mirror is safe: the enabled
            // output camera will present the freshly rendered texture later in the same frame.
            return !(_active && _hasCapturedFrame && _outputCamera != null && _outputCamera.enabled);
        }

        /// <summary>Updates pose, resolution, and per-camera enable state once per late frame.</summary>
        public void Simulate(float deltaTime)
        {
            if (!_initialized || _owner == null)
            {
                return;
            }

            bool shouldBeActive = CanOwnDesktopOutput();
            if (shouldBeActive != _active)
            {
                SetActive(shouldBeActive, true);
            }

            if (!_active)
            {
                return;
            }

            EnsureCaptureTexture();
            SyncDynamicCameraSettings();
            UpdateStabilizedPose(deltaTime);

            bool shouldCapture;
            if (_forceCaptureNextFrame)
            {
                shouldCapture = true;
                _forceCaptureNextFrame = false;
            }
            else
            {
                shouldCapture = _renderRateLimiter.AllowThisFrame(
                    Mathf.Max(deltaTime, 0f), _targetRenderRateHz, _targetRenderRateHz > 0f);
            }

            if (_captureCamera.enabled != shouldCapture)
            {
                _captureCamera.enabled = shouldCapture;
            }
            if (!_outputCamera.enabled)
            {
                _outputCamera.enabled = true;
            }
        }

        private bool CanOwnDesktopOutput()
        {
            return _ownerEnabled
                && BasisSettingsDefaults.EnableVRDesktopView.RawValue
                && BasisVRDesktopViewBlitFeature.IsAvailable
                && _hasOutputRenderer
                && BasisDeviceManagement.IsCurrentModeVR()
                && !BasisDeviceManagement.IsMobileHardware()
                && !BasisHandHeldCamera.IsDesktopOutputOverrideActive;
        }

        private void RefreshState(bool forceCapture)
        {
            SetActive(CanOwnDesktopOutput(), forceCapture);
        }

        private void SetActive(bool active, bool forceCapture)
        {
            _active = active;
            _renderRateLimiter = default;

            if (!active)
            {
                if (_captureCamera != null)
                {
                    _captureCamera.enabled = false;
                }
                if (_outputCamera != null)
                {
                    _outputCamera.enabled = false;
                }
                if (_cullingRegistered)
                {
                    BasisCullingCameraRegistry.Unregister(_captureCamera);
                    _cullingRegistered = false;
                }

                BasisVRDesktopViewBlitFeature.ClearOutput(_outputCamera);
                ReleaseCaptureTexture();
                _poseInitialized = false;
                _hasCapturedFrame = false;
                _forceCaptureNextFrame = false;
                return;
            }

            EnsureCaptureTexture();
            SyncCaptureCameraSettings();
            ResetPose();
            BasisVRDesktopViewBlitFeature.SetOutput(_outputCamera, _captureTexture);
            if (!_cullingRegistered)
            {
                BasisCullingCameraRegistry.Register(_captureCamera);
                _cullingRegistered = true;
            }

            _hasCapturedFrame = false;
            _outputCamera.enabled = true;
            _forceCaptureNextFrame = forceCapture;
            _captureCamera.enabled = forceCapture;
        }

        private void EnsureCaptureTexture()
        {
            if (_owner == null || _captureCamera == null)
            {
                return;
            }

            int width = Mathf.Max(MinimumTextureDimension, Screen.width);
            int height = Mathf.Max(MinimumTextureDimension, Screen.height);
            if (_captureTexture != null && _captureTexture.IsCreated()
                && width == _textureWidth && height == _textureHeight)
            {
                return;
            }

            BasisVRDesktopViewBlitFeature.ClearOutput(_outputCamera);
            ReleaseCaptureTexture();

            RenderTextureFormat format = _owner.Camera.allowHDR
                ? RenderTextureFormat.DefaultHDR
                : RenderTextureFormat.Default;

            _captureTexture = new RenderTexture(
                width, height, CaptureDepthBits, format, RenderTextureReadWrite.Default)
            {
                name = "StabilizedVRDesktopView",
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            _captureTexture.Create();
            _forceCaptureNextFrame = true;
            _hasCapturedFrame = false;

            _textureWidth = width;
            _textureHeight = height;
            _captureCamera.targetTexture = _captureTexture;
            _captureCamera.aspect = (float)width / height;
            _captureCamera.ResetProjectionMatrix();
            BasisVRDesktopViewBlitFeature.SetOutput(_outputCamera, _captureTexture);
        }

        private void ReleaseCaptureTexture()
        {
            if (_captureCamera != null)
            {
                _captureCamera.targetTexture = null;
            }

            if (_captureTexture != null)
            {
                if (_captureTexture.IsCreated())
                {
                    _captureTexture.Release();
                }
                Destroy(_captureTexture);
                _captureTexture = null;
            }

            _textureWidth = 0;
            _textureHeight = 0;
        }

        private void SyncCaptureCameraSettings()
        {
            if (_owner == null || _owner.Camera == null || _captureCamera == null)
            {
                return;
            }

            RefreshOutputRendererAvailability();
            SyncCaptureCameraCore(_owner.Camera);
            SyncCaptureCullingMask(_owner.Camera);
            SyncCaptureSkybox(_owner.Camera);
            SyncCaptureUrpData();
            SyncOutputCameraSettings(_owner.Camera);
        }

        private void SyncCaptureCameraCore(Camera source)
        {
            _captureCamera.targetTexture = _captureTexture;
            _captureCamera.targetDisplay = source.targetDisplay;
            _captureCamera.depth = source.depth - CaptureDepthOffset;
            _captureCamera.rect = FullViewport;
            _captureCamera.stereoTargetEye = StereoTargetEyeMask.None;
            _captureCamera.clearFlags = source.clearFlags;
            _captureCamera.backgroundColor = source.backgroundColor;
            _captureCamera.nearClipPlane = source.nearClipPlane;
            _captureCamera.farClipPlane = source.farClipPlane;
            _captureCamera.useOcclusionCulling = source.useOcclusionCulling;
            _captureCamera.allowHDR = source.allowHDR;
            _captureCamera.allowMSAA = false;
            _captureCamera.allowDynamicResolution = false;
            _captureCamera.depthTextureMode = source.depthTextureMode;
            _captureCamera.renderingPath = source.renderingPath;
            _captureCamera.transparencySortMode = source.transparencySortMode;
            _captureCamera.transparencySortAxis = source.transparencySortAxis;
            _captureCamera.opaqueSortMode = source.opaqueSortMode;
            _captureCamera.layerCullDistances = source.layerCullDistances;
            _captureCamera.layerCullSpherical = source.layerCullSpherical;
            _captureCamera.usePhysicalProperties = false;
            _captureCamera.orthographic = false;
            _captureCamera.fieldOfView = _requestedFov;

            if (_textureHeight > 0)
            {
                _captureCamera.aspect = (float)_textureWidth / _textureHeight;
            }

            // The spectator camera always owns ordinary mono matrices; never inherit XR matrices.
            _captureCamera.ResetProjectionMatrix();
            _captureCamera.ResetWorldToCameraMatrix();
            _captureCamera.ResetCullingMatrix();
        }

        private void SyncCaptureCullingMask(Camera source)
        {
            int cullingMask = source.cullingMask;
            if (!_showHud && _overlayUiLayer >= 0)
            {
                cullingMask &= ~(1 << _overlayUiLayer);
            }
            _captureCamera.cullingMask = cullingMask;
        }

        private void SyncCaptureSkybox(Camera source)
        {
            bool hasMainSkybox = source.TryGetComponent(out Skybox mainSkybox)
                && mainSkybox.material != null;
            bool hasCaptureSkybox = _captureCamera.TryGetComponent(out Skybox captureSkybox);
            if (hasMainSkybox)
            {
                if (!hasCaptureSkybox)
                {
                    captureSkybox = _captureCamera.gameObject.AddComponent<Skybox>();
                }
                captureSkybox.material = mainSkybox.material;
            }
            else if (hasCaptureSkybox)
            {
                captureSkybox.material = null;
            }
        }

        private void SyncCaptureUrpData()
        {
            _captureCameraData.allowXRRendering = false;
            _captureCameraData.SetRenderer(0);
            if (_owner.CameraData == null)
            {
                return;
            }

            _captureCameraData.renderPostProcessing = _owner.CameraData.renderPostProcessing;
            _captureCameraData.antialiasing = _owner.CameraData.antialiasing;
            _captureCameraData.antialiasingQuality = _owner.CameraData.antialiasingQuality;
            _captureCameraData.stopNaN = _owner.CameraData.stopNaN;
            _captureCameraData.dithering = _owner.CameraData.dithering;
            _captureCameraData.volumeLayerMask = _owner.CameraData.volumeLayerMask;
            Transform mainVolumeTrigger = _owner.CameraData.volumeTrigger;
            _captureCameraData.volumeTrigger = mainVolumeTrigger == null
                || ReferenceEquals(mainVolumeTrigger, _owner.Camera.transform)
                ? _captureCamera.transform
                : mainVolumeTrigger;
        }

        private void SyncOutputCameraSettings(Camera source)
        {
            _outputCamera.targetDisplay = source.targetDisplay;
            _outputCamera.depth = source.depth + OutputDepthOffset;
            _outputCamera.rect = FullViewport;
            _outputCameraData.allowXRRendering = false;
            if (_hasOutputRenderer)
            {
                _outputCameraData.SetRenderer(OutputRendererIndex);
            }
        }

        private void SyncDynamicCameraSettings()
        {
            if (_owner == null || _owner.Camera == null || _captureCamera == null)
            {
                return;
            }

            if (_captureCamera.nearClipPlane != _owner.Camera.nearClipPlane)
            {
                _captureCamera.nearClipPlane = _owner.Camera.nearClipPlane;
            }
            if (_captureCamera.farClipPlane != _owner.Camera.farClipPlane)
            {
                _captureCamera.farClipPlane = _owner.Camera.farClipPlane;
            }
            if (_captureCamera.fieldOfView != _requestedFov)
            {
                _captureCamera.fieldOfView = _requestedFov;
                _captureCamera.ResetProjectionMatrix();
            }
        }

        private void UpdateStabilizedPose(float deltaTime)
        {
            Transform cameraTransform = _owner.Camera.transform;
            _trackingRoot = _owner.LocalPlayer != null ? _owner.LocalPlayer.transform : cameraTransform.parent;
            if (_trackingRoot == null)
            {
                _captureCamera.transform.SetPositionAndRotation(cameraTransform.position, cameraTransform.rotation);
                return;
            }

            Vector3 targetLocalPosition = _trackingRoot.InverseTransformPoint(cameraTransform.position);
            Quaternion targetLocalRotation = Quaternion.Inverse(_trackingRoot.rotation) * cameraTransform.rotation;
            targetLocalRotation = ApplyHorizonMode(targetLocalRotation);

            float positionErrorSquared = (_smoothedLocalPosition - targetLocalPosition).sqrMagnitude;
            float rotationError = Quaternion.Angle(_smoothedLocalRotation, targetLocalRotation);
            if (!_poseInitialized
                || positionErrorSquared > PositionResetDistance * PositionResetDistance
                || rotationError > RotationResetAngle)
            {
                _smoothedLocalPosition = targetLocalPosition;
                _smoothedLocalRotation = targetLocalRotation;
                _poseInitialized = true;
            }
            else
            {
                float dt = Mathf.Max(deltaTime, 0f);

                if (_positionStabilization)
                {
                    float alpha = 1f - Mathf.Exp(-PositionResponse * _stabilizationStrength * dt);
                    _smoothedLocalPosition = Vector3.LerpUnclamped(
                        _smoothedLocalPosition, targetLocalPosition, alpha);
                    Vector3 lag = targetLocalPosition - _smoothedLocalPosition;
                    if (lag.sqrMagnitude > MaximumPositionLag * MaximumPositionLag)
                    {
                        _smoothedLocalPosition = targetLocalPosition - lag.normalized * MaximumPositionLag;
                    }
                }
                else
                {
                    _smoothedLocalPosition = targetLocalPosition;
                }

                if (_rotationStabilization)
                {
                    float response = (RotationResponse + rotationError * RotationCatchup)
                        * _stabilizationStrength;
                    float alpha = 1f - Mathf.Exp(-response * dt);
                    _smoothedLocalRotation = Quaternion.Slerp(
                        _smoothedLocalRotation, targetLocalRotation, alpha);
                }
                else
                {
                    _smoothedLocalRotation = targetLocalRotation;
                }
            }

            Vector3 worldPosition = _trackingRoot.TransformPoint(_smoothedLocalPosition);
            Quaternion worldRotation = _trackingRoot.rotation * _smoothedLocalRotation;
            _captureCamera.transform.SetPositionAndRotation(worldPosition, worldRotation);
        }

        private Quaternion ApplyHorizonMode(Quaternion localRotation)
        {
            if (_horizonMode == HorizonStabilizationMode.Off)
            {
                return localRotation;
            }

            Vector3 euler = localRotation.eulerAngles;
            float roll = Mathf.DeltaAngle(0f, euler.z);
            if (_horizonMode == HorizonStabilizationMode.Locked)
            {
                roll = 0f;
            }
            else
            {
                roll *= ReducedRollFactor;
            }

            return Quaternion.Euler(euler.x, euler.y, roll);
        }

        private void ResetPose()
        {
            _poseInitialized = false;
        }

        private void LoadCachedSettings()
        {
            _requestedFov = ClampFov(BasisSettingsDefaults.VRDesktopViewFOV.RawValue);
            _positionStabilization = BasisSettingsDefaults.VRDesktopViewPositionStabilization.RawValue;
            _rotationStabilization = BasisSettingsDefaults.VRDesktopViewRotationStabilization.RawValue;
            _horizonMode = ParseHorizonMode(BasisSettingsDefaults.VRDesktopViewHorizonMode.RawValue);
            _stabilizationStrength = ClampStrength(
                BasisSettingsDefaults.VRDesktopViewStabilizationStrength.RawValue);
            _showHud = BasisSettingsDefaults.VRDesktopViewShowHUD.RawValue;
            _targetRenderRateHz = ParseRenderRate(BasisSettingsDefaults.VRDesktopViewRenderRate.RawValue);
        }

        private void RefreshOutputRendererAvailability()
        {
            UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
            _hasOutputRenderer = asset != null && asset.renderers.Length > OutputRendererIndex;
        }

        private static float ClampFov(float value)
        {
            return Mathf.Clamp(
                value,
                BasisSettingsDefaults.VR_DESKTOP_VIEW_FOV_MIN,
                BasisSettingsDefaults.VR_DESKTOP_VIEW_FOV_MAX);
        }

        private static float ClampStrength(float value)
        {
            return Mathf.Clamp(
                value,
                BasisSettingsDefaults.VR_DESKTOP_VIEW_STRENGTH_MIN,
                BasisSettingsDefaults.VR_DESKTOP_VIEW_STRENGTH_MAX);
        }

        private static HorizonStabilizationMode ParseHorizonMode(string value)
        {
            if (string.Equals(
                value, BasisSettingsDefaults.VR_DESKTOP_VIEW_HORIZON_OFF,
                StringComparison.OrdinalIgnoreCase))
            {
                return HorizonStabilizationMode.Off;
            }
            if (string.Equals(
                value, BasisSettingsDefaults.VR_DESKTOP_VIEW_HORIZON_LOCKED,
                StringComparison.OrdinalIgnoreCase))
            {
                return HorizonStabilizationMode.Locked;
            }
            return HorizonStabilizationMode.Reduced;
        }

        private static float ParseRenderRate(string value)
        {
            if (string.Equals(
                value, BasisSettingsDefaults.VR_DESKTOP_VIEW_RATE_UNLIMITED,
                StringComparison.OrdinalIgnoreCase))
            {
                return 0f;
            }

            return float.TryParse(value, out float hz) ? Mathf.Max(0f, hz) : DefaultRenderRateHz;
        }

        private void BeginCameraRendering(ScriptableRenderContext _context, Camera renderingCamera)
        {
            // Camera ordering is capture -> XR/main -> output. The same first-person crop is applied
            // independently around both the capture and XR renders, and each callback restores it.
            if (_active && ReferenceEquals(renderingCamera, _captureCamera)
                && BasisLocalAvatarDriver.Mapping.Hashead)
            {
                BasisLocalAvatarDriver.ScaleheadToZero();
            }
        }

        private void EndCameraRendering(ScriptableRenderContext _context, Camera renderingCamera)
        {
            if (!ReferenceEquals(renderingCamera, _captureCamera))
            {
                return;
            }

            _hasCapturedFrame = true;
            if (BasisLocalAvatarDriver.Mapping.Hashead)
            {
                BasisLocalAvatarDriver.ScaleHeadToNormal();
            }
        }

        private void OnRenderSettingsApplied()
        {
            SyncCaptureCameraSettings();
            RefreshState(true);
        }

        private void OnBootModeChanged(string _)
        {
            RefreshOutputRendererAvailability();
            ResetPose();
            RefreshState(true);
        }

        private void OnHandheldDesktopOutputChanged(bool _)
        {
            RefreshState(true);
        }

        private void OnEnabledChanged(bool _)
        {
            RefreshState(true);
        }

        private void OnFovChanged(float value)
        {
            _requestedFov = ClampFov(value);
            SyncDynamicCameraSettings();
        }

        private void OnPositionStabilizationChanged(bool value)
        {
            _positionStabilization = value;
        }

        private void OnRotationStabilizationChanged(bool value)
        {
            _rotationStabilization = value;
        }

        private void OnHorizonModeChanged(string value)
        {
            _horizonMode = ParseHorizonMode(value);
        }

        private void OnStabilizationStrengthChanged(float value)
        {
            _stabilizationStrength = ClampStrength(value);
        }

        private void OnShowHudChanged(bool value)
        {
            _showHud = value;
            if (_owner != null && _owner.Camera != null && _captureCamera != null)
            {
                SyncCaptureCullingMask(_owner.Camera);
            }
        }

        private void OnRenderRateChanged(string value)
        {
            _targetRenderRateHz = ParseRenderRate(value);
            _renderRateLimiter = default;
            if (_active)
            {
                _forceCaptureNextFrame = true;
            }
        }
    }
}
