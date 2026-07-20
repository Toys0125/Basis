using LinkerGenerator;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public class BasisBuildDialogAndSettings : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    // ====== Version bump config ======
    private const bool AutoIncrementBundleVersion = true;   // PlayerSettings.bundleVersion (also Android versionName)
    private const bool AutoIncrementAndroidVersionCode = true; // PlayerSettings.Android.bundleVersionCode

    // If true, forces bundleVersion into X.Y.Z format (best practice).
    private static bool ForceSemanticVersionFormat = true;

    // Platforms that effectively require IL2CPP (commonly true in modern Unity).
    private static readonly HashSet<BuildTarget> Il2CppOnlyTargets = new HashSet<BuildTarget>
    {
        BuildTarget.Android,
        BuildTarget.iOS,
        BuildTarget.tvOS,
        BuildTarget.WebGL,
#if UNITY_2019_1_OR_NEWER
        BuildTarget.PS4,
        BuildTarget.XboxOne,
        BuildTarget.Switch,
#endif
    };

    // Platforms you want to force Mono (example: your Linux choice).
    private static readonly HashSet<BuildTarget> MonoOnlyTargets = new HashSet<BuildTarget>
    {
        BuildTarget.StandaloneLinux64,
        BuildTarget.LinuxHeadlessSimulation,
    };

    public void OnPreprocessBuild(BuildReport report)
    {
        // 0) Generate link.xml
        BasisLinkGenerator.GenerateLinkXml();

        // 0.5) Versioning
        BumpVersionsIfNeeded(report.summary.platform);

        var namedBuildTarget =
            UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup);

        var currentBackend = PlayerSettings.GetScriptingBackend(namedBuildTarget);
        var target = report.summary.platform;

        // 1) Force IL2CPP-only targets
        if (Il2CppOnlyTargets.Contains(target))
        {
            ApplyRequestedBackend(
                target,
                namedBuildTarget,
                currentBackend,
                ScriptingImplementation.IL2CPP,
                allowIl2CppFallback: false);
            return;
        }

        // 2) Force Mono-only targets
        if (MonoOnlyTargets.Contains(target))
        {
            ApplyRequestedBackend(
                target,
                namedBuildTarget,
                currentBackend,
                ScriptingImplementation.Mono2x,
                allowIl2CppFallback: true);
            return;
        }

        // Standalone is one Unity build-target group, but macOS may be
        // installed with Mono-only support on a Windows editor. Do not show an
        // IL2CPP prompt that can only end in a failed build; select Mono before
        // any target-specific post-processing runs.
        string il2CppAvailabilityReason;
        if (!BasisBuildTargetCapabilities.IsScriptingBackendAvailable(
                target,
                ScriptingImplementation.IL2CPP,
                out il2CppAvailabilityReason))
        {
            Debug.LogWarning(
                $"[BasisBuild] IL2CPP is unavailable for {target}; automatically selecting Mono. " +
                il2CppAvailabilityReason);
            ApplyRequestedBackend(
                target,
                namedBuildTarget,
                currentBackend,
                ScriptingImplementation.Mono2x,
                allowIl2CppFallback: true);
            return;
        }

        // 3) Use the remembered backend; prompt only when set to Ask
        bool useIl2Cpp;
        var backendPref = BasisBuildScriptingBackendPreference.Current;
        if (backendPref == BasisBuildScriptingBackendPreference.Mode.IL2CPP)
        {
            useIl2Cpp = true;
        }
        else if (backendPref == BasisBuildScriptingBackendPreference.Mode.Mono)
        {
            useIl2Cpp = false;
        }
        else if (Application.isBatchMode)
        {
            // Safe default for CI: keep current backend (or change to true to default IL2CPP)
            useIl2Cpp = (currentBackend == ScriptingImplementation.IL2CPP);
        }
        else
        {
            useIl2Cpp = EditorUtility.DisplayDialog(
                "Scripting Backend",
                $"Build target: {target}\n\nUse IL2CPP for this build?\n\nYour choice is remembered for next time. Change it under Basis ▸ Project Setup ▸ Build & Modules.",
                "Yes (IL2CPP)",
                "No (Mono)"
            );

            BasisBuildScriptingBackendPreference.Current = useIl2Cpp
                ? BasisBuildScriptingBackendPreference.Mode.IL2CPP
                : BasisBuildScriptingBackendPreference.Mode.Mono;
        }

        ApplyRequestedBackend(
            target,
            namedBuildTarget,
            currentBackend,
            useIl2Cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x,
            allowIl2CppFallback: true
        );
    }

    private static void BumpVersionsIfNeeded(BuildTarget target)
    {
        if (AutoIncrementBundleVersion)
        {
            var before = PlayerSettings.bundleVersion;
            if (IncrementBundleVersion(before, out var after))
            {
                if (after != before)
                {
                    PlayerSettings.bundleVersion = after;
                    BasisDebug.Log($"[Build] bundleVersion: {before} -> {after}");
                }
            }
        }

        // Only bump Android versionCode when building Android (usually what you want).
        // If you want it bumped on *any* build, remove the target check.
        if (AutoIncrementAndroidVersionCode && target == BuildTarget.Android)
        {
            int before = PlayerSettings.Android.bundleVersionCode;
            int after = Mathf.Max(1, before + 1);
            PlayerSettings.Android.bundleVersionCode = after;
            BasisDebug.Log($"[Build] Android versionCode: {before} -> {after}");

            // Android versionName comes from PlayerSettings.bundleVersion by default.
            // If you want it explicitly logged:
            BasisDebug.Log($"[Build] Android versionName: {PlayerSettings.bundleVersion}");
        }

        // If you want the changes to definitely persist to ProjectSettings on disk:
        // AssetDatabase.SaveAssets();
    }

    private static bool IncrementBundleVersion(string version,out string ComputedVersion)
    {
        // Match "major.minor.patch" with optional extra junk ignored
        var m = Regex.Match(version ?? "", @"^\s*(\d+)\.(\d+)\.(\d+)\s*$");
        if (m.Success)
        {
            int major = int.Parse(m.Groups[1].Value);
            int minor = int.Parse(m.Groups[2].Value);
            int patch = int.Parse(m.Groups[3].Value) + 1;
            ComputedVersion = $"{major}.{minor}.{patch}";
        }

        // If it isn't semver, coerce it into semver and start at .0.1
        // Examples:
        // "1" -> "1.0.1"
        // "1.2" -> "1.2.1"
        // "v1.2" -> "1.2.1" (extracts digits)
        var nums = Regex.Matches(version ?? "", @"\d+");
        int majorC = nums.Count > 0 ? int.Parse(nums[0].Value) : 0;
        int minorC = nums.Count > 1 ? int.Parse(nums[1].Value) : 0;
        int patchC = 1;
        ComputedVersion = $"{majorC}.{minorC}.{patchC}";
        return ForceSemanticVersionFormat;
    }

    private static void SetBackendIfNeeded(
        UnityEditor.Build.NamedBuildTarget namedBuildTarget,
        ScriptingImplementation current,
        ScriptingImplementation desired)
    {
        if (current == desired) return;
        PlayerSettings.SetScriptingBackend(namedBuildTarget, desired);
    }

    private static void ApplyRequestedBackend(
        BuildTarget target,
        UnityEditor.Build.NamedBuildTarget namedBuildTarget,
        ScriptingImplementation current,
        ScriptingImplementation requested,
        bool allowIl2CppFallback)
    {
        ScriptingImplementation desired = requested;
        if (requested == ScriptingImplementation.IL2CPP)
        {
            string reason;
            if (!BasisBuildTargetCapabilities.TryResolveBackend(
                    target,
                    requested,
                    out desired,
                    out reason))
            {
                throw new BuildFailedException(
                    $"The requested IL2CPP backend is not available for {target}, and Mono cannot be selected. {reason}");
            }

            if (desired != requested)
            {
                if (!allowIl2CppFallback)
                {
                    throw new BuildFailedException(
                        $"The build target {target} requires IL2CPP, but this Unity installation does not provide it. {reason}");
                }

                Debug.LogWarning($"[BasisBuild] {reason} Automatically using Mono for {target}.");
            }
        }

        SetBackendIfNeeded(namedBuildTarget, current, desired);
    }
}
