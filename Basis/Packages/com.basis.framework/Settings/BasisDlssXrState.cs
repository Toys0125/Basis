using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Shared state for Basis's XR DLSS integration. DLSS runs one native temporal context per eye,
/// so XR must expose separate 2D eye textures rather than a texture array/double-wide target.
/// </summary>
public static class BasisDlssXrState
{
    public const string UpscalerName = "Basis NVIDIA DLSS 4 XR";

    public static bool IsActive { get; private set; }
    public static event Action<bool> ActiveChanged;

    private static readonly List<XRDisplaySubsystem> Displays = new();
    private static readonly Dictionary<XRDisplaySubsystem, XRDisplaySubsystem.TextureLayout> PreviousLayouts = new();
    private static bool warnedUnsupportedLayout;

    public static void SetActive(bool active)
    {
        if (IsActive == active)
        {
            if (active)
                EnsureCompatibleTextureLayout();
            return;
        }

        IsActive = active;
        if (active)
        {
            EnsureCompatibleTextureLayout();
        }
        else
        {
            RestoreTextureLayouts();
        }

        ActiveChanged?.Invoke(active);
    }

    /// <summary>
    /// Requests separate eye textures on every running XR display. A display that has not started
    /// yet is handled later when the boot mode changes and the render-resolution module reapplies.
    /// </summary>
    public static bool EnsureCompatibleTextureLayout()
    {
        if (!IsActive)
            return true;

        Displays.Clear();
        SubsystemManager.GetSubsystems(Displays);

        bool compatible = true;
        for (int i = 0; i < Displays.Count; i++)
        {
            XRDisplaySubsystem display = Displays[i];
            if (display == null || !display.running)
                continue;

            XRDisplaySubsystem.TextureLayout supported = display.supportedTextureLayouts;
            if ((supported & XRDisplaySubsystem.TextureLayout.SeparateTexture2Ds) == 0)
            {
                compatible = false;
                if (!warnedUnsupportedLayout)
                {
                    warnedUnsupportedLayout = true;
                    BasisDebug.LogWarning("DLSS VR requires separate eye textures, but the active XR runtime does not advertise SeparateTexture2Ds support.");
                }
                continue;
            }

            if (!PreviousLayouts.ContainsKey(display))
                PreviousLayouts.Add(display, display.textureLayout);

            if (display.textureLayout != XRDisplaySubsystem.TextureLayout.SeparateTexture2Ds)
            {
                display.textureLayout = XRDisplaySubsystem.TextureLayout.SeparateTexture2Ds;
                BasisDebug.Log("DLSS VR: XR texture layout set to SeparateTexture2Ds", BasisDebug.LogTag.Video);
            }
        }

        return compatible;
    }

    private static void RestoreTextureLayouts()
    {
        foreach (KeyValuePair<XRDisplaySubsystem, XRDisplaySubsystem.TextureLayout> pair in PreviousLayouts)
        {
            XRDisplaySubsystem display = pair.Key;
            if (display == null || !display.running)
                continue;

            if ((display.supportedTextureLayouts & pair.Value) != 0 && display.textureLayout != pair.Value)
                display.textureLayout = pair.Value;
        }

        PreviousLayouts.Clear();
        warnedUnsupportedLayout = false;
    }
}
