using System;
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
        private static BasisVRDesktopViewDriver s_Instance;

        private const float PositionResponse = 12f;
        private const float RotationResponse = 7f;
        private const float RotationCatchup = 0.12f;
        private const float MaximumPositionLag = 0.10f;
        private const float PositionResetDistance = 0.50f;
        private const float RotationResetAngle = 65f;
        private const float CaptureDepthOffset = 100f;
        private const float OutputDepthOffset = 100f;
        private const int OutputRendererIndex = 2;

        private BasisLocalCameraDriver _owner;
        private Camera _captureCamera;
        private Camera _outputCamera;
        private UniversalAdditionalCameraData _captureCameraData;
        private UniversalAdditionalCameraData _outputCameraData;
        private RenderTexture _captureTexture;
        private Basis.BasisRenderRateLimiter _renderRateLimiter;

        private Transform _trackingRoot;
        private Vector3 _smoothedLocalPosition;
        private Quaternion _smoothedLocalRotation = Quaternion.identity;
        private bool _poseInitialized;
        private bool _initialized;
        private bool _ownerEnabled;
        private bool _active;
        private bool _forceCaptureNextFrame;
        private bool _hasCapturedFrame;
        private bool _cullingRegistered;
        private int _textureWidth;
        private int _textureHeight;

        private Func<Camera, bool> _previousXRMirrorFilter;
        private Func<Camera, bool> _installedXRMirrorFilter;

        /// <summary>True while this view owns display zero.</summary>
        public bool IsActive => _active;

        public void Initialize(BasisLocalCameraDriver owner)
        {
            if (owner == null)
            {
                return;
            }

            if (BasisDeviceManagement.IsMobileHardware())
            {
                return;
            }

            if (s_Instance != null && !ReferenceEquals(s_Instance, this))
            {
                s_Instance.Shutdown();
            }
            s_Instance = this;

            _owner = owner;
            _ownerEnabled = true;

            if (!_initialized)
            {
                CreateCameras();
                Subscribe();
                InstallXRMirrorFilter();
                _initialized = true;
            }

            SyncCaptureCameraSettings();
            RefreshState(forceCapture: true);
        }

        public void Suspend()
        {
            _ownerEnabled = false;
            SetActive(false, false);
        }

        public void Shutdown()
        {
            if (!_initialized)
            {
                if (ReferenceEquals(s_Instance, this))
                {
                    s_Instance = null;
                }
                return;
            }

            Unsubscribe();
            RestoreXRMirrorFilter();
            SetActive(false, false);
            BasisVRDesktopViewBlitFeature.ClearOutput(_outputCamera);
            ReleaseCaptureTexture();

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

            GameObject captureObject = new GameObject("Stabilized VR Desktop Capture")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            captureObject.transform.SetParent(parent, false);
            _captureCamera = captureObject.AddComponent<Camera>();
            _captureCameraData = captureObject.AddComponent<UniversalAdditionalCameraData>();
            _captureCameraData.allowXRRendering = false;
            _captureCameraData.SetRenderer(0);
            _captureCamera.stereoTargetEye = StereoTargetEyeMask.None;
            _captureCamera.enabled = false;

            GameObject outputObject = new GameObject("Stabilized VR Desktop Output")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            outputObject.transform.SetParent(parent, false);
            _outputCamera = outputObject.AddComponent<Camera>();
            _outputCameraData = outputObject.AddComponent<UniversalAdditionalCameraData>();
            _outputCameraData.allowXRRendering = false;
            _outputCameraData.renderPostProcessing = false;
            if (HasOutputRenderer())
            {
                _outputCameraData.SetRenderer(OutputRendererIndex);
            }

            _outputCamera.stereoTargetEye = StereoTargetEyeMask.None;
            _outputCamera.clearFlags = CameraClearFlags.SolidColor;
            _outputCamera.backgroundColor = Color.black;
            _outputCamera.cullingMask = 0;
            _outputCamera.useOcclusionCulling = false;
            _outputCamera.allowHDR = false;
            _outputCamera.allowMSAA = false;
            _outputCamera.enabled = false;
        }

        private void Subscribe()
        {
            RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
            RenderPipelineManager.endCameraRendering += EndCameraRendering;
            BasisLocalCameraDriver.RenderSettingsApplied += SyncCaptureCameraSettings;
            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
            BasisHandHeldCamera.DesktopOutputOverrideChanged += OnHandheldDesktopOutputChanged;

            BasisSettingsDefaults.EnableVRDesktopView.OnChanged += OnEnabledChanged;
            BasisSettingsDefaults.VRDesktopViewFOV.OnChanged += OnFloatSettingChanged;
            BasisSettingsDefaults.VRDesktopViewPositionStabilization.OnChanged += OnBoolSettingChanged;
            BasisSettingsDefaults.VRDesktopViewRotationStabilization.OnChanged += OnBoolSettingChanged;
            BasisSettingsDefaults.VRDesktopViewHorizonMode.OnChanged += OnStringSettingChanged;
            BasisSettingsDefaults.VRDesktopViewStabilizationStrength.OnChanged += OnFloatSettingChanged;
            BasisSettingsDefaults.VRDesktopViewShowHUD.OnChanged += OnBoolSettingChanged;
            BasisSettingsDefaults.VRDesktopViewRenderRate.OnChanged += OnStringSettingChanged;
        }

        private void Unsubscribe()
        {
            RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= EndCameraRendering;
            BasisLocalCameraDriver.RenderSettingsApplied -= SyncCaptureCameraSettings;
            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
            BasisHandHeldCamera.DesktopOutputOverrideChanged -= OnHandheldDesktopOutputChanged;

            BasisSettingsDefaults.EnableVRDesktopView.OnChanged -= OnEnabledChanged;
            BasisSettingsDefaults.VRDesktopViewFOV.OnChanged -= OnFloatSettingChanged;
            BasisSettingsDefaults.VRDesktopViewPositionStabilization.OnChanged -= OnBoolSettingChanged;
            BasisSettingsDefaults.VRDesktopViewRotationStabilization.OnChanged -= OnBoolSettingChanged;
            BasisSettingsDefaults.VRDesktopViewHorizonMode.OnChanged -= OnStringSettingChanged;
            BasisSettingsDefaults.VRDesktopViewStabilizationStrength.OnChanged -= OnFloatSettingChanged;
            BasisSettingsDefaults.VRDesktopViewShowHUD.OnChanged -= OnBoolSettingChanged;
            BasisSettingsDefaults.VRDesktopViewRenderRate.OnChanged -= OnStringSettingChanged;
        }

        private void InstallXRMirrorFilter()
        {
            _previousXRMirrorFilter = UniversalRenderPipeline.xrMirrorViewRenderFilter;
            _installedXRMirrorFilter = ShouldRenderXRMirrorView;
            UniversalRenderPipeline.xrMirrorViewRenderFilter = _installedXRMirrorFilter;
        }

        private void RestoreXRMirrorFilter()
        {
            if (UniversalRenderPipeline.xrMirrorViewRenderFilter == _installedXRMirrorFilter)
            {
                UniversalRenderPipeline.xrMirrorViewRenderFilter = _previousXRMirrorFilter;
            }

            _previousXRMirrorFilter = null;
            _installedXRMirrorFilter = null;
        }

        private bool ShouldRenderXRMirrorView(Camera camera)
        {
            if (_active && _hasCapturedFrame && BasisVRDesktopViewBlitFeature.HasPresentedOutput
                && _outputCamera != null && _outputCamera.enabled
                && _owner != null && ReferenceEquals(camera, _owner.Camera))
            {
                return false;
            }

            return _previousXRMirrorFilter == null || _previousXRMirrorFilter(camera);
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

            float targetHz = ParseRenderRate(BasisSettingsDefaults.VRDesktopViewRenderRate.RawValue);
            bool limitRate = targetHz > 0f;
            if (_forceCaptureNextFrame)
            {
                _captureCamera.enabled = true;
                _forceCaptureNextFrame = false;
            }
            else
            {
                _captureCamera.enabled = _renderRateLimiter.AllowThisFrame(
                    Mathf.Max(deltaTime, 0f), targetHz, limitRate);
            }
            _outputCamera.enabled = _hasCapturedFrame;
        }

        private bool CanOwnDesktopOutput()
        {
            return _ownerEnabled
                && BasisSettingsDefaults.EnableVRDesktopView.RawValue
                && BasisVRDesktopViewBlitFeature.IsAvailable
                && HasOutputRenderer()
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

            if (!active)
            {
                if (_captureCamera != null) _captureCamera.enabled = false;
                if (_outputCamera != null) _outputCamera.enabled = false;
                if (_cullingRegistered)
                {
                    BasisCullingCameraRegistry.Unregister(_captureCamera);
                    _cullingRegistered = false;
                }
                _poseInitialized = false;
                _hasCapturedFrame = false;
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
            _outputCamera.enabled = false;
            _forceCaptureNextFrame = forceCapture;
            _captureCamera.enabled = forceCapture;
        }

        private void EnsureCaptureTexture()
        {
            int width = Mathf.Max(16, Screen.width);
            int height = Mathf.Max(16, Screen.height);
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

            _captureTexture = new RenderTexture(width, height, 24, format, RenderTextureReadWrite.Default)
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
            if (_owner == null || _captureCamera == null)
            {
                return;
            }

            RenderTexture target = _captureTexture;
            float captureDepth = _owner.Camera.depth - CaptureDepthOffset;
            _captureCamera.CopyFrom(_owner.Camera);
            _captureCamera.targetTexture = target;
            _captureCamera.targetDisplay = _owner.Camera.targetDisplay;
            _captureCamera.depth = captureDepth;
            _captureCamera.rect = new Rect(0f, 0f, 1f, 1f);
            _captureCamera.stereoTargetEye = StereoTargetEyeMask.None;
            _captureCamera.usePhysicalProperties = false;
            _captureCamera.fieldOfView = Mathf.Clamp(BasisSettingsDefaults.VRDesktopViewFOV.RawValue, 35f, 120f);
            if (_textureHeight > 0)
            {
                _captureCamera.aspect = (float)_textureWidth / _textureHeight;
            }
            // CopyFrom can inherit an XR/custom projection from the headset camera. The spectator
            // camera needs an ordinary mono projection driven only by its own FOV and window aspect.
            _captureCamera.ResetProjectionMatrix();
            _captureCamera.ResetWorldToCameraMatrix();
            _captureCamera.ResetCullingMatrix();
            _captureCamera.allowMSAA = false;

            int cullingMask = _owner.Camera.cullingMask;
            if (!BasisSettingsDefaults.VRDesktopViewShowHUD.RawValue)
            {
                int overlayLayer = LayerMask.NameToLayer("OverlayUI");
                if (overlayLayer >= 0)
                {
                    cullingMask &= ~(1 << overlayLayer);
                }
            }
            _captureCamera.cullingMask = cullingMask;

            bool hasMainSkybox = _owner.Camera.TryGetComponent(out Skybox mainSkybox)
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

            _captureCameraData.allowXRRendering = false;
            _captureCameraData.SetRenderer(0);
            if (_owner.CameraData != null)
            {
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

            _outputCamera.targetDisplay = _owner.Camera.targetDisplay;
            _outputCamera.depth = _owner.Camera.depth + OutputDepthOffset;
            _outputCamera.rect = new Rect(0f, 0f, 1f, 1f);
            _outputCameraData.allowXRRendering = false;
            if (HasOutputRenderer())
            {
                _outputCameraData.SetRenderer(OutputRendererIndex);
            }
        }

        private void SyncDynamicCameraSettings()
        {
            if (_captureCamera.nearClipPlane != _owner.Camera.nearClipPlane)
                _captureCamera.nearClipPlane = _owner.Camera.nearClipPlane;
            if (_captureCamera.farClipPlane != _owner.Camera.farClipPlane)
                _captureCamera.farClipPlane = _owner.Camera.farClipPlane;
            float requestedFov = Mathf.Clamp(BasisSettingsDefaults.VRDesktopViewFOV.RawValue, 35f, 120f);
            if (_captureCamera.fieldOfView != requestedFov)
            {
                _captureCamera.fieldOfView = requestedFov;
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

            float positionError = Vector3.Distance(_smoothedLocalPosition, targetLocalPosition);
            float rotationError = Quaternion.Angle(_smoothedLocalRotation, targetLocalRotation);
            if (!_poseInitialized || positionError > PositionResetDistance || rotationError > RotationResetAngle)
            {
                _smoothedLocalPosition = targetLocalPosition;
                _smoothedLocalRotation = targetLocalRotation;
                _poseInitialized = true;
            }
            else
            {
                float strength = Mathf.Clamp(BasisSettingsDefaults.VRDesktopViewStabilizationStrength.RawValue, 0.1f, 3f);
                float dt = Mathf.Max(deltaTime, 0f);

                if (BasisSettingsDefaults.VRDesktopViewPositionStabilization.RawValue)
                {
                    float alpha = 1f - Mathf.Exp(-PositionResponse * strength * dt);
                    _smoothedLocalPosition = Vector3.LerpUnclamped(_smoothedLocalPosition, targetLocalPosition, alpha);
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

                if (BasisSettingsDefaults.VRDesktopViewRotationStabilization.RawValue)
                {
                    float response = (RotationResponse + rotationError * RotationCatchup) * strength;
                    float alpha = 1f - Mathf.Exp(-response * dt);
                    _smoothedLocalRotation = Quaternion.Slerp(_smoothedLocalRotation, targetLocalRotation, alpha);
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

        private static Quaternion ApplyHorizonMode(Quaternion localRotation)
        {
            string mode = BasisSettingsDefaults.VRDesktopViewHorizonMode.RawValue;
            if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                return localRotation;
            }

            Vector3 euler = localRotation.eulerAngles;
            float roll = Mathf.DeltaAngle(0f, euler.z);
            if (string.Equals(mode, "Locked", StringComparison.OrdinalIgnoreCase))
            {
                roll = 0f;
            }
            else
            {
                roll *= 0.25f;
            }

            return Quaternion.Euler(euler.x, euler.y, roll);
        }

        private void ResetPose()
        {
            _poseInitialized = false;
        }

        private static bool HasOutputRenderer()
        {
            UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
            return asset != null && asset.renderers.Length > OutputRendererIndex;
        }

        private static float ParseRenderRate(string value)
        {
            if (string.Equals(value, "Unlimited", StringComparison.OrdinalIgnoreCase))
            {
                return 0f;
            }

            return float.TryParse(value, out float hz) ? Mathf.Max(0f, hz) : 60f;
        }

        private void BeginCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
        {
            if (_active && ReferenceEquals(renderingCamera, _captureCamera)
                && BasisLocalAvatarDriver.Mapping.Hashead)
            {
                BasisLocalAvatarDriver.ScaleheadToZero();
            }
        }

        private void EndCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
        {
            if (ReferenceEquals(renderingCamera, _captureCamera))
            {
                _hasCapturedFrame = true;
                if (BasisLocalAvatarDriver.Mapping.Hashead)
                {
                    BasisLocalAvatarDriver.ScaleHeadToNormal();
                }
            }
        }

        private void OnBootModeChanged(string mode)
        {
            ResetPose();
            RefreshState(true);
        }

        private void OnHandheldDesktopOutputChanged(bool active)
        {
            RefreshState(true);
        }

        private void OnEnabledChanged(bool enabled)
        {
            RefreshState(true);
        }

        private void OnFloatSettingChanged(float value)
        {
            ResetPose();
            SyncCaptureCameraSettings();
        }

        private void OnBoolSettingChanged(bool value)
        {
            ResetPose();
            SyncCaptureCameraSettings();
        }

        private void OnStringSettingChanged(string value)
        {
            ResetPose();
            SyncCaptureCameraSettings();
        }
    }
}
