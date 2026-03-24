using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

public static class BasisHeadlessBuild
{
    public static void BuildLinuxServer()
    {
        BuildServer(BuildTarget.StandaloneLinux64);
    }

    public static void BuildWindowsServer()
    {
        BuildServer(BuildTarget.StandaloneWindows64);
    }

    private static void BuildServer(BuildTarget target)
    {
        string buildPath = RequireArgument("customBuildPath");
        string buildName = GetArgument("customBuildName") ?? Path.GetFileNameWithoutExtension(buildPath);
        string projectPath = GetArgument("projectPath") ?? Directory.GetCurrentDirectory();
        string standaloneSubtargetArg = GetArgument("standaloneBuildSubtarget") ?? "Server";

        Debug.Log($"[BasisHeadlessBuild] Starting {target} build");
        Debug.Log($"[BasisHeadlessBuild] projectPath={projectPath}");
        Debug.Log($"[BasisHeadlessBuild] buildName={buildName}");
        Debug.Log($"[BasisHeadlessBuild] buildPath={buildPath}");
        Debug.Log($"[BasisHeadlessBuild] activeBuildTarget(before)={EditorUserBuildSettings.activeBuildTarget}");
        Debug.Log($"[BasisHeadlessBuild] activeBuildTargetGroup(before)={BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget)}");
        Debug.Log($"[BasisHeadlessBuild] standaloneBuildSubtarget(arg)={standaloneSubtargetArg}");

        BuildTargetGroup targetGroup = BuildPipeline.GetBuildTargetGroup(target);
        if (!BuildPipeline.IsBuildTargetSupported(targetGroup, target))
        {
            throw new BuildFailedException($"Build target {target} is not supported in this editor.");
        }

        if (EditorUserBuildSettings.activeBuildTarget != target)
        {
            bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(targetGroup, target);
            Debug.Log($"[BasisHeadlessBuild] SwitchActiveBuildTarget({target}) => {switched}");
        }

        StandaloneBuildSubtarget standaloneSubtarget = ParseStandaloneSubtarget(standaloneSubtargetArg);
        EditorUserBuildSettings.standaloneBuildSubtarget = standaloneSubtarget;
        Debug.Log($"[BasisHeadlessBuild] activeBuildTarget(after)={EditorUserBuildSettings.activeBuildTarget}");
        Debug.Log($"[BasisHeadlessBuild] standaloneBuildSubtarget(set)={EditorUserBuildSettings.standaloneBuildSubtarget}");

        EnsureBuildDirectory(buildPath);
        LogEnabledScenes();
        BuildAddressablesWithDiagnostics(target);

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(),
            locationPathName = buildPath,
            target = target,
            targetGroup = targetGroup,
            subtarget = (int)standaloneSubtarget,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        Debug.Log($"[BasisHeadlessBuild] Build result={report.summary.result}");
        Debug.Log($"[BasisHeadlessBuild] Build output path={report.summary.outputPath}");
        Debug.Log($"[BasisHeadlessBuild] Build totalErrors={report.summary.totalErrors}");
        Debug.Log($"[BasisHeadlessBuild] Build totalWarnings={report.summary.totalWarnings}");

        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            throw new BuildFailedException($"Player build failed: {report.summary.result}");
        }
    }

    private static void BuildAddressablesWithDiagnostics(BuildTarget target)
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            throw new BuildFailedException("Addressables settings not found.");
        }

        Debug.Log($"[BasisHeadlessBuild] Addressables active profile={settings.activeProfileId}");
        Debug.Log($"[BasisHeadlessBuild] Addressables player data builder index={settings.ActivePlayerDataBuilderIndex}");

        foreach (AddressableAssetGroup group in settings.groups.Where(group => group != null))
        {
            List<AddressableAssetEntry> entries = group.entries.ToList();
            List<string> missingAssets = entries
                .Select(entry => entry.AssetPath)
                .Where(assetPath => string.IsNullOrWhiteSpace(assetPath) || (!AssetDatabase.IsValidFolder(assetPath) && AssetDatabase.LoadMainAssetAtPath(assetPath) == null))
                .Distinct()
                .ToList();

            Debug.Log($"[BasisHeadlessBuild] Addressables group='{group.Name}' entries={entries.Count} schemas={group.Schemas.Count} readOnly={group.ReadOnly}");
            if (missingAssets.Count > 0)
            {
                foreach (string assetPath in missingAssets)
                {
                    Debug.LogError($"[BasisHeadlessBuild] Addressables group='{group.Name}' missing asset path='{assetPath}'");
                }
            }
        }

        try
        {
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
            Debug.Log($"[BasisHeadlessBuild] Addressables result.Error='{result.Error}'");
            Debug.Log($"[BasisHeadlessBuild] Addressables outputPath='{result.OutputPath}'");
            if (!string.IsNullOrEmpty(result.Error))
            {
                throw new BuildFailedException($"Addressables build returned error: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BasisHeadlessBuild] Addressables build threw for target={target}");
            LogExceptionChain(ex);
            throw;
        }
    }

    private static void LogEnabledScenes()
    {
        foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
        {
            Debug.Log($"[BasisHeadlessBuild] Scene enabled={scene.enabled} path={scene.path}");
        }
    }

    private static void LogExceptionChain(Exception ex)
    {
        int depth = 0;
        Exception current = ex;
        while (current != null)
        {
            Debug.LogError($"[BasisHeadlessBuild] Exception[{depth}] {current.GetType().FullName}: {current.Message}");
            Debug.LogError(current.StackTrace ?? "<no stack trace>");
            current = current.InnerException;
            depth++;
        }
    }

    private static void EnsureBuildDirectory(string buildPath)
    {
        string directory = Path.GetDirectoryName(buildPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static StandaloneBuildSubtarget ParseStandaloneSubtarget(string value)
    {
        if (Enum.TryParse(value, true, out StandaloneBuildSubtarget parsed))
        {
            return parsed;
        }

        return StandaloneBuildSubtarget.Server;
    }

    private static string RequireArgument(string name)
    {
        string value = GetArgument(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new BuildFailedException($"Required command line argument '-{name}' was not provided.");
        }

        return value;
    }

    private static string GetArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == $"-{name}")
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
