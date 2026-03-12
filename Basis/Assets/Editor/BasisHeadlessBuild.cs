using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class BasisHeadlessBuild
{
    public static void BuildLinuxServer()
    {
        BuildServer(BuildTarget.StandaloneLinux64, "build/LinuxServer", "HeadlessLinuxServer");
    }

    public static void BuildWindowsServer()
    {
        BuildServer(BuildTarget.StandaloneWindows64, "build/WindowsServer", "HeadlessWindowsServer");
    }

    private static void BuildServer(BuildTarget target, string outputDir, string productName)
    {
        EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server;
        EditorUserBuildSettings.enableHeadlessMode = true;

        var scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            throw new InvalidOperationException("No enabled scenes in Build Settings.");
        }

        var buildPlayerOptions = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputDir,
            target = target,
            options = BuildOptions.EnableHeadlessMode | BuildOptions.EnableServerBuild
        };

        var previousProductName = PlayerSettings.productName;
        try
        {
            PlayerSettings.productName = productName;
            var report = BuildPipeline.BuildPlayer(buildPlayerOptions);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException($"Server build failed: {report.summary.result}");
            }
        }
        finally
        {
            PlayerSettings.productName = previousProductName;
        }
    }
}
