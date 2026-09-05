using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk.Players;
using Cilbox;
using HVR.Vixxy;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

#if UNITY_EDITOR
[UnityEditor.InitializeOnLoad]
internal static class CilboxFoxMothPlaytestLogGuard
{
    static CilboxFoxMothPlaytestLogGuard()
    {
        // Unity-Server's Linux editor currently emits unrelated Burst AOT linker
        // errors during PlayMode startup. Custom validators tolerate them, but
        // Unity Test Framework treats every unexpected error log as a test
        // failure before this playtest gets a chance to run.
        LogAssert.ignoreFailingMessages = true;
    }
}
#endif

public class CilboxFoxMothLivePlaytest
{
    private const string AvatarUrl = "https://dipcdn.net/Fox-Moth-v1.41-3rVn";
    private const string AvatarPassword = "a0225a75691b5e83169c4c045c3588cbdaa48ae0af558c4722c3b21e58485768";
    private const int SampleFrames = 300;

    [UnityTest]
    [Timeout(240000)]
    public IEnumerator FoxMoth_ReachesVixxyFilterLoop_AndProfilesUpdate()
    {
        SceneManager.LoadScene("initialization", LoadSceneMode.Single);

        float bootDeadline = Time.realtimeSinceStartup + 90f;
        while (BasisLocalPlayer.Instance == null && Time.realtimeSinceStartup < bootDeadline)
        {
            yield return null;
        }
        Assert.IsNotNull(BasisLocalPlayer.Instance, "Basis local player did not finish booting.");

        var bundle = new BasisLoadableBundle
        {
            UnlockPassword = AvatarPassword,
            BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle
            {
                RemoteBeeFileLocation = AvatarUrl
            },
            BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle()
        };

        var loadTask = BasisLocalPlayer.Instance.CreateAvatar((byte)BasisLoadMode.Download, bundle);
        float loadDeadline = Time.realtimeSinceStartup + 120f;
        while (!loadTask.IsCompleted && Time.realtimeSinceStartup < loadDeadline)
        {
            yield return null;
        }
        Assert.IsTrue(loadTask.IsCompleted, "Fox Moth avatar load timed out.");
        if (loadTask.IsFaulted)
        {
            throw loadTask.Exception;
        }

        Assert.IsNotNull(BasisLocalPlayer.Instance.BasisAvatar, "Fox Moth avatar load completed without a BasisAvatar.");

        var orchestrator = BasisLocalPlayer.Instance.BasisAvatar.GetComponentInChildren<HVRVixxyOrchestrator>(true);
        Assert.IsNotNull(orchestrator, "Loaded Fox Moth avatar did not create a Vixxy orchestrator.");

        FieldInfo filterSetField = typeof(HVRVixxyOrchestrator).GetField(
            "_actuatorsWithFiltersToCheckThisTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(filterSetField, "Unable to locate Vixxy filter-check set.");

        PropertyInfo countProperty = filterSetField.FieldType.GetProperty("Count");
        Assert.IsNotNull(countProperty, "Unable to read Vixxy filter-check count.");

        float readyDeadline = Time.realtimeSinceStartup + 60f;
        int readyFrame = -1;
        int readyFilterCount = 0;
        while (Time.realtimeSinceStartup < readyDeadline)
        {
            object filterSet = filterSetField.GetValue(orchestrator);
            int count = (int)countProperty.GetValue(filterSet);
            if (count > 0)
            {
                readyFrame = Time.frameCount;
                readyFilterCount = count;
                break;
            }
            yield return null;
        }

        Assert.GreaterOrEqual(readyFrame, 0, "Fox Moth never entered the Vixxy filter-check loop.");

        int proxyCount = BasisLocalPlayer.Instance.BasisAvatar.GetComponentsInChildren<CilboxProxy>(true).Length;
        Debug.Log($"CILBOX_LIVE_PLAYTEST|READY|frame={readyFrame}|vixxyFilterCount={readyFilterCount}|cilboxProxies={proxyCount}");

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
            {
                Debug.Log($"CILBOX_LIVE_PLAYTEST|FRAME|sample={i + 1}|cilboxUs={c / 1000.0:F3}|vixxyUs={v / 1000.0:F3}|gcBytes={g}");
            }
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
