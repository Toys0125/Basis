using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Cilbox;
using HVR.Vixxy;
using NUnit.Framework;
using Unity.Burst;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

#if UNITY_EDITOR
[UnityEditor.InitializeOnLoad]
internal static class CilboxFoxMothPlaytestBurstGuard
{
    static CilboxFoxMothPlaytestBurstGuard()
    {
        // Unity-Server's Linux editor currently emits unrelated Burst AOT linker
        // errors while the Test Framework is entering PlayMode. Those errors
        // occur before a test LogScope exists, so they cannot be suppressed with
        // LogAssert. Cilbox and Vixxy are managed; disable Burst only for this
        // temporary live playtest so their own CPU/GC markers remain measurable.
        BurstCompiler.Options.EnableBurstCompilation = false;
    }
}
#endif

public class CilboxFoxMothLivePlaytest
{
    private const string AvatarUrl = "https://dipcdn.net/Fox-Moth-v1.41-3rVn";
    private const string AvatarPassword = "a0225a75691b5e83169c4c045c3588cbdaa48ae0af558c4722c3b21e58485768";
    private const int InstanceCount = 20;
    private const int WarmupFrames = 120;
    private const int SampleFrames = 300;
    private readonly List<GameObject> spawnedClones = new List<GameObject>();

    [TearDown]
    public void ResetValidationHooks()
    {
        BasisBundleLoadAsset.DisableFrameSplitForValidation = false;
        for (int i = spawnedClones.Count - 1; i >= 0; i--)
        {
            if (spawnedClones[i] != null) UnityEngine.Object.DestroyImmediate(spawnedClones[i]);
        }
        spawnedClones.Clear();
        BasisSceneFactory.SkipSceneCameraSetupForValidation = false;
        LogAssert.ignoreFailingMessages = false;
    }

    [UnityTest]
    [Timeout(360000)]
    public IEnumerator FoxMoth_ReachesVixxyFilterLoop_AndProfilesUpdate()
    {
        // This live playtest boots the full Basis scene stack, which currently emits
        // unrelated scene/camera errors on the headless Linux Editor. Keep Unity Test
        // Framework from failing on those logs; every condition relevant to this test
        // (boot, target avatar, Vixxy readiness and profiler data) is asserted explicitly.
        LogAssert.ignoreFailingMessages = true;
        BasisBundleLoadAsset.DisableFrameSplitForValidation = true;
        BasisSceneFactory.SkipSceneCameraSetupForValidation = true;

        // BasisBootSequence runs automatically after the Test Framework's PlayMode scene
        // loads. Do not replace that scene here: the asynchronously-instantiated BasisFramework
        // belongs to it, so a Single scene load destroys DeviceManagement/CreationGameobject
        // while avatar boot is still in flight.
        float bootDeadline = Time.realtimeSinceStartup + 120f;
        while ((BasisLocalPlayer.Instance == null || !BasisLocalPlayer.PlayerReady) && Time.realtimeSinceStartup < bootDeadline)
        {
            yield return null;
        }
        Assert.IsNotNull(BasisLocalPlayer.Instance, "Basis local player did not finish booting.");
        Assert.IsTrue(BasisLocalPlayer.PlayerReady, "Basis local player never reached PlayerReady.");

        // PlayerReady is set only after Basis finishes restoring the last-used avatar.
        // Reuse that avatar if it is already our target; starting another CreateAvatar
        // while boot's restore is still active causes the two loads to cancel each other.
        if (BasisLocalPlayer.CurrentAvatarUniqueID != AvatarUrl)
        {
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

        GameObject sourceAvatar = BasisLocalPlayer.Instance.BasisAvatar.gameObject;
        CilboxProxy[] sourceProxies = sourceAvatar.GetComponentsInChildren<CilboxProxy>(true);
        Assert.AreEqual(1, sourceProxies.Length, "Fox Moth stress harness expects one Cilbox proxy per avatar instance.");
        Assert.IsTrue(sourceProxies[0].ValidationProxyIsSetup, "Source Fox Moth Cilbox proxy is not initialized.");
        Assert.IsFalse(string.IsNullOrEmpty(sourceProxies[0].ValidationSerializedObjectData), "Source Fox Moth Cilbox proxy did not retain validation bootstrap data.");

        for (int i = 1; i < InstanceCount; i++)
        {
            GameObject clone = UnityEngine.Object.Instantiate(sourceAvatar);
            clone.name = $"Fox Moth Stress Clone {i:D2}";
            clone.transform.position = sourceAvatar.transform.position + new Vector3((i % 5) * 2.0f, 0f, (i / 5) * 2.0f);
            BasisAvatar cloneAvatar = clone.GetComponent<BasisAvatar>();
            Assert.IsNotNull(cloneAvatar, $"Clone {i} is missing its BasisAvatar root.");
            // The stress clones are local synthetic avatar instances, not network peers. Mark
            // them local so Vixxy never attempts an avatar->network-player lookup each tick.
            cloneAvatar.IsOwnedLocally = true;

            CilboxProxy[] cloneProxies = clone.GetComponentsInChildren<CilboxProxy>(true);
            Assert.AreEqual(sourceProxies.Length, cloneProxies.Length, $"Clone {i} did not preserve the Fox Moth Cilbox proxy layout.");
            for (int proxyIndex = 0; proxyIndex < cloneProxies.Length; proxyIndex++)
            {
                cloneProxies[proxyIndex].ValidationReloadFromInitializedProxy(sourceProxies[proxyIndex]);
            }
            spawnedClones.Add(clone);
        }

        for (int i = 0; i < WarmupFrames; i++) yield return null;

        int proxyCount = sourceProxies.Length;
        int activeProxyCount = sourceProxies[0].ValidationProxyIsSetup ? 1 : 0;
        int orchestratorCount = sourceAvatar.GetComponentsInChildren<HVRVixxyOrchestrator>(true).Length;
        for (int i = 0; i < spawnedClones.Count; i++)
        {
            CilboxProxy[] cloneProxies = spawnedClones[i].GetComponentsInChildren<CilboxProxy>(true);
            proxyCount += cloneProxies.Length;
            for (int proxyIndex = 0; proxyIndex < cloneProxies.Length; proxyIndex++)
            {
                if (cloneProxies[proxyIndex].ValidationProxyIsSetup) activeProxyCount++;
            }
            orchestratorCount += spawnedClones[i].GetComponentsInChildren<HVRVixxyOrchestrator>(true).Length;
        }
        Assert.AreEqual(InstanceCount, proxyCount, "20-instance Fox Moth stress test did not create the expected Cilbox proxy count.");
        Assert.AreEqual(InstanceCount, activeProxyCount, "20-instance Fox Moth stress test did not initialize every Cilbox proxy.");
        Assert.GreaterOrEqual(orchestratorCount, InstanceCount, "20-instance Fox Moth stress test did not create the expected Vixxy orchestrators.");
        Debug.Log($"CILBOX_LIVE_PLAYTEST|READY|frame={Time.frameCount}|instances={InstanceCount}|warmupFrames={WarmupFrames}|registeredFilteredActuators={registeredFilteredActuators}|filterApplyCount={orchestrator.ValidationFilterApplyCount}|cilboxProxies={proxyCount}|activeCilboxProxies={activeProxyCount}|vixxyOrchestrators={orchestratorCount}");

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
            $"CILBOX_LIVE_PLAYTEST|SUMMARY|frames={SampleFrames}|instances={InstanceCount}|cilboxProxies={proxyCount}" +
            $"|cilboxAvgUs={Average(cilboxNs) / 1000.0:F3}|cilboxP50Us={Percentile(cilboxNs, 0.50) / 1000.0:F3}|cilboxP95Us={Percentile(cilboxNs, 0.95) / 1000.0:F3}|cilboxMaxUs={cilboxNs[cilboxNs.Count - 1] / 1000.0:F3}" +
            $"|cilboxPerProxyAvgUs={Average(cilboxNs) / 1000.0 / proxyCount:F3}|cilboxPerProxyP50Us={Percentile(cilboxNs, 0.50) / 1000.0 / proxyCount:F3}|cilboxPerProxyP95Us={Percentile(cilboxNs, 0.95) / 1000.0 / proxyCount:F3}" +
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
