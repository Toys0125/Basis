using System;
using System.Collections;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Cilbox;
using HVR.Vixxy;
using NUnit.Framework;
using Unity.Burst;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;

#if UNITY_EDITOR
[UnityEditor.InitializeOnLoad]
internal static class CilboxFoxMothPlaytestBurstGuard
{
    static CilboxFoxMothPlaytestBurstGuard()
    {
        BurstCompiler.Options.EnableBurstCompilation = false;
    }
}
#endif

public class CilboxFoxMothLivePlaytest
{
    private const string AvatarUrl = "https://dipcdn.net/Fox-Moth-v1.41-3rVn";
    private const string AvatarPassword = "a0225a75691b5e83169c4c045c3588cbdaa48ae0af558c4722c3b21e58485768";
    private const int SampleFrames = 300;

    [TearDown]
    public void ResetValidationHooks()
    {
        BasisBundleLoadAsset.DisableFrameSplitForValidation = false;
        BasisSceneFactory.SkipSceneCameraSetupForValidation = false;
        LogAssert.ignoreFailingMessages = false;
    }

    [UnityTest]
    [Timeout(240000)]
    public IEnumerator FoxMoth_ReachesVixxyFilterLoop_AndProfilesUpdate()
    {
        LogAssert.ignoreFailingMessages = true;
        BasisBundleLoadAsset.DisableFrameSplitForValidation = true;
        BasisSceneFactory.SkipSceneCameraSetupForValidation = true;

        float bootDeadline = Time.realtimeSinceStartup + 120f;
        while ((BasisLocalPlayer.Instance == null || !BasisLocalPlayer.PlayerReady) && Time.realtimeSinceStartup < bootDeadline)
            yield return null;

        Assert.IsNotNull(BasisLocalPlayer.Instance, "Basis local player did not finish booting.");
        Assert.IsTrue(BasisLocalPlayer.PlayerReady, "Basis local player never reached PlayerReady.");

        if (BasisLocalPlayer.CurrentAvatarUniqueID != AvatarUrl)
        {
            var bundle = new BasisLoadableBundle
            {
                UnlockPassword = AvatarPassword,
                BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = AvatarUrl },
                BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle()
            };

            var loadTask = BasisLocalPlayer.Instance.CreateAvatar((byte)BasisLoadMode.Download, bundle);
            float loadDeadline = Time.realtimeSinceStartup + 120f;
            while (!loadTask.IsCompleted && Time.realtimeSinceStartup < loadDeadline)
                yield return null;

            Assert.IsTrue(loadTask.IsCompleted, "Fox Moth avatar load timed out.");
            if (loadTask.IsFaulted) throw loadTask.Exception;
        }

        Assert.AreEqual(AvatarUrl, BasisLocalPlayer.CurrentAvatarUniqueID, "Fox Moth is not the active local avatar.");
        Assert.IsNotNull(BasisLocalPlayer.Instance.BasisAvatar, "Fox Moth avatar load completed without a BasisAvatar.");

        var orchestrator = BasisLocalPlayer.Instance.BasisAvatar.GetComponentInChildren<HVRVixxyOrchestrator>(true);
        Assert.IsNotNull(orchestrator, "Loaded Fox Moth avatar did not create a Vixxy orchestrator.");

        int registeredFilteredActuators = orchestrator.ValidationRegisteredFilteredActuatorCount;
        Assert.Greater(registeredFilteredActuators, 0, "Fox Moth registered no Vixxy actuators with filters.");

        int filterApplyBefore = orchestrator.ValidationFilterApplyCount;
        Assert.IsTrue(orchestrator.ScheduleFirstFilteredActuatorForValidation(), "Unable to schedule a Fox Moth filtered actuator.");

        float readyDeadline = Time.realtimeSinceStartup + 10f;
        int readyFrame = -1;
        while (Time.realtimeSinceStartup < readyDeadline)
        {
            yield return null;
            if (orchestrator.ValidationFilterApplyCount > filterApplyBefore)
            {
                readyFrame = Time.frameCount;
                break;
            }
        }

        Assert.GreaterOrEqual(readyFrame, 0, "Fox Moth's scheduled Vixxy actuator never reached ApplyFilters().");

        int proxyCount = BasisLocalPlayer.Instance.BasisAvatar.GetComponentsInChildren<CilboxProxy>(true).Length;
        Debug.Log($"CILBOX_LIVE_PLAYTEST|READY|frame={readyFrame}|registeredFilteredActuators={registeredFilteredActuators}|filterApplyCount={orchestrator.ValidationFilterApplyCount}|cilboxProxies={proxyCount}");

        var options = ProfilerRecorderOptions.WrapAroundWhenCapacityReached |
                      ProfilerRecorderOptions.StartImmediately |
                      ProfilerRecorderOptions.SumAllSamplesInFrame;

        using var cilboxRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "CilboxProxy.Update.LivePlaytest", 1, options);
        using var vixxyRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "HVRComms.VixxyOrchestrator", 1, options);
        using var gcRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1, options);

        var cilboxNs = new List<long>(SampleFrames);
        var vixxyNs = new List<long>(SampleFrames);
        var gcBytes = new List<long>(SampleFrames);

        for (int i = 0; i < SampleFrames; i++)
        {
            yield return null;
            long c = cilboxRecorder.Valid ? cilboxRecorder.LastValue : 0;
            long v = vixxyRecorder.Valid ? vixxyRecorder.LastValue : 0;
            long g = gcRecorder.Valid ? gcRecorder.LastValue : 0;
            cilboxNs.Add(c);
            vixxyNs.Add(v);
            gcBytes.Add(g);
            if ((i + 1) % 60 == 0)
                Debug.Log($"CILBOX_LIVE_PLAYTEST|FRAME|sample={i + 1}|cilboxUs={c / 1000.0:F3}|vixxyUs={v / 1000.0:F3}|gcBytes={g}");
        }

        cilboxNs.Sort();
        vixxyNs.Sort();
        gcBytes.Sort();

        Debug.Log(
            $"CILBOX_LIVE_PLAYTEST|SUMMARY|frames={SampleFrames}|cilboxProxies={proxyCount}" +
            $"|cilboxAvgUs={Average(cilboxNs) / 1000.0:F3}|cilboxP50Us={Percentile(cilboxNs, 0.50) / 1000.0:F3}|cilboxP95Us={Percentile(cilboxNs, 0.95) / 1000.0:F3}|cilboxMaxUs={cilboxNs[cilboxNs.Count - 1] / 1000.0:F3}" +
            $"|vixxyAvgUs={Average(vixxyNs) / 1000.0:F3}|vixxyP95Us={Percentile(vixxyNs, 0.95) / 1000.0:F3}" +
            $"|gcAvgBytes={Average(gcBytes):F1}|gcP95Bytes={Percentile(gcBytes, 0.95)}|gcMaxBytes={gcBytes[gcBytes.Count - 1]}|gcZeroFrames={CountZero(gcBytes)}");
    }

    private static double Average(List<long> values)
    {
        double sum = 0;
        for (int i = 0; i < values.Count; i++) sum += values[i];
        return sum / values.Count;
    }

    private static long Percentile(List<long> sorted, double p)
    {
        int index = (int)Math.Ceiling(sorted.Count * p) - 1;
        if (index < 0) index = 0;
        if (index >= sorted.Count) index = sorted.Count - 1;
        return sorted[index];
    }

    private static int CountZero(List<long> values)
    {
        int count = 0;
        for (int i = 0; i < values.Count; i++) if (values[i] == 0) count++;
        return count;
    }
}
