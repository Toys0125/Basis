using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class SMModuleAntialiasingURP : BasisSettingsBase
{
    private const string UpscalerAutomatic = "Automatic";
    private const string UpscalerBilinear = "Bilinear";
    private const string UpscalerNearest = "Nearest-Neighbor";
    private const string UpscalerFsr1 = "FidelityFX Super Resolution 1.0";
    private const string UpscalerFsr2 = BasisFsr2Upscaler.UpscalerName;

    public Camera Camera;
    public UniversalAdditionalCameraData Data;
    public int LowmsaaSampleCount = 2;
    public int MediumLowmsaaSampleCount = 4;
    public int HighmsaaSampleCount = 8;

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        bool antialiasingChanged = matchedSettingName == BasisSettingsDefaults.Antialiasing.BindingKey;
        bool upscalingChanged = matchedSettingName == BasisSettingsDefaults.Upscaling.BindingKey;
        bool upscalingQualityChanged = matchedSettingName == BasisSettingsDefaults.UpscalingQuality.BindingKey;
        bool motionVectorEdgeRepairChanged = matchedSettingName == BasisSettingsDefaults.TemporalMotionVectorEdgeRepair.BindingKey;
        if (!antialiasingChanged && !upscalingChanged && !upscalingQualityChanged && !motionVectorEdgeRepairChanged)
            return;

        if (motionVectorEdgeRepairChanged)
        {
            if (bool.TryParse(optionValue, out bool enabled))
            {
                UniversalRenderPipeline.BasisTemporalMotionVectorEdgeRepair = enabled;
                BasisDebug.Log($"Temporal motion-vector edge repair {(enabled ? "enabled" : "disabled")}", BasisDebug.LogTag.Local);
            }
            return;
        }

        UniversalRenderPipelineAsset asset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
        if (asset == null)
        {
            BasisDebug.LogError("Missing Asset Pipeline!");
            return;
        }

        if (upscalingQualityChanged)
        {
            ApplyUpscalingQuality(asset, optionValue);
            return;
        }

        if (upscalingChanged)
        {
            ApplyUpscaling(asset, optionValue);
            ApplyUpscalingQuality(asset, BasisSettingsDefaults.UpscalingQuality.RawValue);
            return;
        }

        if (!TryResolveCamera())
        {
            BasisDebug.LogError("Missing Camera Or Data!");
            return;
        }

        BasisDebug.Log($"Antialiasing Changed to {optionValue}", BasisDebug.LogTag.Local);
        switch (optionValue)
        {
            case "off":
            case "msaa off":
                ApplyMsaa(asset, 1, false);
                break;
            case "msaa 2x":
                DisableTemporalUpscalerForMsaa(asset);
                ApplyMsaa(asset, LowmsaaSampleCount, true);
                break;
            case "msaa 4x":
                DisableTemporalUpscalerForMsaa(asset);
                ApplyMsaa(asset, MediumLowmsaaSampleCount, true);
                break;
            case "msaa 8x":
                DisableTemporalUpscalerForMsaa(asset);
                ApplyMsaa(asset, HighmsaaSampleCount, true);
                break;

            // Compatibility with values saved before upscaling became a separate setting.
            case "linear":
                MigrateLegacyUpscalerSetting(asset, "Bilinear");
                break;
            case "point":
                MigrateLegacyUpscalerSetting(asset, "Nearest-Neighbor");
                break;
            case "fsr":
                MigrateLegacyUpscalerSetting(asset, "FSR 1.0");
                break;
            case "stp":
                ApplyMsaa(asset, 1, false);
#if ENABLE_UPSCALER_FRAMEWORK
                BasisDlssXrState.SetActive(false);
                asset.upscalerName = "Spatial-Temporal Post-Processing";
#else
                asset.upscalingFilter = UpscalingFilterSelection.STP;
#endif
                break;
        }
    }

    private bool TryResolveCamera()
    {
        if (Camera != null && Data != null)
            return true;

        if (BasisLocalCameraDriver.Instance != null)
        {
            Camera = BasisLocalCameraDriver.Instance.Camera;
            Data = BasisLocalCameraDriver.Instance.CameraData;
        }

        if (Camera == null)
            Camera = Camera.main;

        if (Camera != null && Data == null)
            Camera.TryGetComponent(out Data);

        return Camera != null && Data != null;
    }

    private void ApplyMsaa(UniversalRenderPipelineAsset asset, int sampleCount, bool enabled)
    {
        asset.msaaSampleCount = sampleCount;
        Camera.allowMSAA = enabled;
        Data.antialiasing = AntialiasingMode.None;
        Data.antialiasingQuality = AntialiasingQuality.Low;
    }

    private void MigrateLegacyUpscalerSetting(UniversalRenderPipelineAsset asset, string upscalingValue)
    {
        BasisSettingsDefaults.Antialiasing.SetValue("Off");
        BasisSettingsDefaults.Upscaling.SetValue(upscalingValue);
        ApplyMsaa(asset, 1, false);
        ApplyUpscaling(asset, upscalingValue.ToLowerInvariant());
    }

    private static void DisableTemporalUpscalerForMsaa(UniversalRenderPipelineAsset asset)
    {
#if ENABLE_UPSCALER_FRAMEWORK
        if (asset.upscalerName == UpscalerFsr2 || asset.upscalerName == BasisDlssXrState.UpscalerName)
        {
            BasisDlssXrState.SetActive(false);
            BasisSettingsDefaults.Upscaling.SetValue("Automatic");
            asset.upscalerName = UpscalerAutomatic;
            BasisDebug.LogWarning("MSAA is incompatible with temporal upscaling. Upscaling was reset to Automatic.");
        }
#endif
    }

    private static void ApplyUpscaling(UniversalRenderPipelineAsset asset, string optionValue)
    {
        string requestedName = optionValue switch
        {
            "bilinear" => UpscalerBilinear,
            "nearest-neighbor" => UpscalerNearest,
            "point" => UpscalerNearest,
            "fsr" => UpscalerFsr1,
            "fsr 1.0" => UpscalerFsr1,
            "fsr1" => UpscalerFsr1,
            "fsr 2" => UpscalerFsr2,
            "fsr2" => UpscalerFsr2,
            "dlss" => BasisDlssXrState.UpscalerName,
            _ => UpscalerAutomatic,
        };

        bool dlssRequested = requestedName == BasisDlssXrState.UpscalerName;
        bool fsr2Requested = requestedName == UpscalerFsr2;

        if (fsr2Requested && BasisDeviceManagement.IsCurrentModeVR())
        {
            BasisDebug.LogWarning("Unity's FSR 2 provider is not XR-enabled. Falling back to Automatic in VR.");
            requestedName = UpscalerAutomatic;
            fsr2Requested = false;
        }

#if !(ENABLE_UPSCALER_FRAMEWORK && (UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN))
        if (dlssRequested || fsr2Requested)
        {
            BasisDebug.LogWarning("The selected temporal vendor upscaler is unavailable on this platform. Falling back to Automatic.");
            requestedName = UpscalerAutomatic;
            dlssRequested = false;
            fsr2Requested = false;
        }
#else
        if (fsr2Requested && !BasisFsr2Upscaler.IsRuntimeSupported())
        {
            BasisDebug.LogWarning("FSR2 is unavailable on this GPU, driver, or graphics API. Falling back to Automatic.");
            requestedName = UpscalerAutomatic;
            fsr2Requested = false;
        }

        if (dlssRequested && !BasisDlssXrUpscaler.IsRuntimeSupported())
        {
            BasisDebug.LogWarning("DLSS is unavailable on this NVIDIA GPU, driver, or graphics API. Falling back to Automatic.");
            requestedName = UpscalerAutomatic;
            dlssRequested = false;
        }
#endif

        if (dlssRequested && BasisDeviceManagement.IsCurrentModeVR())
        {
            BasisDlssXrState.SetActive(true);
            if (!BasisDlssXrState.EnsureCompatibleTextureLayout())
            {
                BasisDebug.LogWarning("The active XR runtime cannot provide separate eye textures required by Basis DLSS VR. Falling back to Automatic.");
                BasisDlssXrState.SetActive(false);
                requestedName = UpscalerAutomatic;
                dlssRequested = false;
            }
        }
        else
        {
            BasisDlssXrState.SetActive(false);
        }

        bool temporalVendorUpscaler = dlssRequested || fsr2Requested;
        if (temporalVendorUpscaler)
        {
            asset.msaaSampleCount = 1;
            BasisSettingsDefaults.Antialiasing.SetValue("Off");
        }

#if ENABLE_UPSCALER_FRAMEWORK
        if (!IsRegisteredUpscaler(requestedName))
        {
            BasisDebug.LogWarning($"Upscaler '{requestedName}' is unavailable on this platform. Falling back to Automatic.");
            BasisDlssXrState.SetActive(false);
            requestedName = UpscalerAutomatic;
        }

        asset.upscalerName = requestedName;
#else
        asset.upscalingFilter = requestedName switch
        {
            UpscalerBilinear => UpscalingFilterSelection.Linear,
            UpscalerNearest => UpscalingFilterSelection.Point,
            UpscalerFsr1 => UpscalingFilterSelection.FSR,
            _ => UpscalingFilterSelection.Auto,
        };
#endif

        // Changing the upscaler changes the meaning of URP's renderScale. Spatial/non-temporal
        // paths use the normal Render Resolution setting, while FSR2/DLSS use the dedicated
        // source-resolution percentage.
        SMModuleRenderResolutionURP.ApplyPipelineRenderScale(asset, BasisSettingsDefaults.RenderResolution.RawValue);

        BasisDebug.Log($"Upscaling Changed to {requestedName}", BasisDebug.LogTag.Local);
    }

    private static void ApplyUpscalingQuality(UniversalRenderPipelineAsset asset, string optionValue)
    {
        string quality = string.IsNullOrWhiteSpace(optionValue)
            ? "automatic"
            : optionValue.Trim().ToLowerInvariant();

#if ENABLE_UPSCALER_FRAMEWORK
        BasisFsr2Upscaler.SetQualityMode(quality);
        BasisDlssXrUpscaler.SetQualityMode(quality);
#endif

        BasisDebug.Log($"Upscaling Quality Changed to {quality}", BasisDebug.LogTag.Local);
    }

#if ENABLE_UPSCALER_FRAMEWORK
    private static bool IsRegisteredUpscaler(string requestedName)
    {
        if (RenderPipelineManager.currentPipeline is not UniversalRenderPipeline pipeline)
        {
            // Settings can be loaded before URP constructs its runtime registry.
            return true;
        }

        foreach (string availableName in pipeline.availableUpscalerNames)
        {
            if (string.Equals(availableName, requestedName, System.StringComparison.Ordinal))
                return true;
        }

        return false;
    }
#endif

    public override void ChangedSettings()
    {
    }
}
