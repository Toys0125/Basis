using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Cilbox;
using HVR.Vixxy;
using NUnit.Framework;
using Unity.Burst;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
using static SerializableBasis;

#if UNITY_EDITOR
[UnityEditor.InitializeOnLoad]
internal static class CilboxFoxMothPlaytestBurstGuard
{
    static CilboxFoxMothPlaytestBurstGuard()
    {
        // Unity-Server's Linux editor currently emits unrelated Burst AOT linker
        // errors while the Test Framework is entering PlayMode. Cilbox/Vixxy load
        // measurements here are managed/main-thread measurements, so keep Burst off
        // only for this temporary validation harness.
        BurstCompiler.Options.EnableBurstCompilation = false;
    }
}
#endif

public class CilboxFoxMothLivePlaytest
{
    private const string AvatarUrl = "https://dipcdn.net/Fox-Moth-v1.41-3rVn";
    private const string AvatarPassword = "a0225a75691b5e83169c4c045c3588cbdaa48ae0af558c4722c3b21e58485768";
    private const int InstanceCount = 20;
    private const float ObservationTimeoutSeconds = 180f;
    private const int SettleFrames = 5;
    private const float HeadlessRangeTickIntervalSeconds = 0.05f;

    private static readonly FieldInfo AvatarLoadGenerationField = typeof(BasisAvatarLoadThread).GetField("sGeneration", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo NetworkInstantiationParametersField = typeof(BasisNetworkManagement).GetField("instantiationParameters", BindingFlags.Static | BindingFlags.Public);
    private static readonly MethodInfo PreparedRemoteFactoryMethod = FindPreparedRemoteFactoryMethod();
    private readonly List<ushort> fakePlayerIds = new List<ushort>(InstanceCount);

    private sealed class RemoteObservation
    {
        public ushort Id;
        public BasisRemotePlayer Player;
        public int SpawnFrame = -1;
        public float SpawnTime = -1f;
        public int RangeCommitFrame = -1;
        public float RangeCommitTime = -1f;
        public int RealAvatarFrame = -1;
        public float RealAvatarTime = -1f;
        public int CilboxReadyFrame = -1;
        public float CilboxReadyTime = -1f;
    }

    [TearDown]
    public void ResetValidationHooks()
    {
        // Tear fake remotes down through the same player-removal path used by networking.
        for (int i = fakePlayerIds.Count - 1; i >= 0; i--)
        {
            ushort id = fakePlayerIds[i];
            if (BasisNetworkPlayers.Players.ContainsKey(id))
            {
                BasisNetworkHandleRemoval.HandleDisconnectIdImmediate(id);
            }
            BasisNetworkPlayers.JoiningPlayers.TryRemove(id, out _);
        }
        fakePlayerIds.Clear();

        // Normal avatar loading deliberately keeps frame splitting enabled. Reset these
        // validation hooks in case the test exits through an assertion/exception.
        BasisBundleLoadAsset.DisableFrameSplitForValidation = false;
        BasisSceneFactory.SkipSceneCameraSetupForValidation = false;
        LogAssert.ignoreFailingMessages = false;
    }

    [UnityTest]
    [Timeout(420000)]
    public IEnumerator FoxMoth_RealRemoteJoinPath_LoadsTwentyAvatars()
    {
        // Full Basis boot currently emits unrelated headless-Linux graphics/audio warnings.
        // The benchmark asserts every condition it depends on explicitly.
        LogAssert.ignoreFailingMessages = true;

        // IMPORTANT: unlike the earlier synthetic burst benchmark, leave the real bundle
        // frame-splitting behavior ON. We want the same pacing a normal client receives.
        BasisBundleLoadAsset.DisableFrameSplitForValidation = false;
        BasisSceneFactory.SkipSceneCameraSetupForValidation = true;

        float bootDeadline = Time.realtimeSinceStartup + 120f;
        while ((BasisLocalPlayer.Instance == null || !BasisLocalPlayer.PlayerReady || !BasisNetworkManagement.IsInitialized) &&
               Time.realtimeSinceStartup < bootDeadline)
        {
            yield return null;
        }
        Assert.IsNotNull(BasisLocalPlayer.Instance, "Basis local player did not finish booting.");
        Assert.IsTrue(BasisLocalPlayer.PlayerReady, "Basis local player never reached PlayerReady.");
        Assert.IsTrue(BasisNetworkManagement.IsInitialized, "Basis networking never initialized; cannot exercise the normal remote-player load path.");

        // Let any boot-time lifecycle work drain before injecting the 20-player join event.
        float drainDeadline = Time.realtimeSinceStartup + 10f;
        while (BasisNetworkHandleRemoval.LifecycleQueue.Count > 0 && Time.realtimeSinceStartup < drainDeadline)
        {
            yield return null;
        }
        Assert.AreEqual(0, BasisNetworkHandleRemoval.LifecycleQueue.Count, "Lifecycle queue did not settle before the benchmark.");

        // Report whether this run starts from the same on-disc state a returning player would
        // have. The Unity validation worker intentionally keeps its normal persistent cache.
        var cacheTask = BasisLoadHandler.IsMetaDataOnDiscAsync(AvatarUrl);
        float cacheDeadline = Time.realtimeSinceStartup + 30f;
        while (!cacheTask.IsCompleted && Time.realtimeSinceStartup < cacheDeadline)
        {
            yield return null;
        }
        Assert.IsTrue(cacheTask.IsCompleted, "Timed out checking Fox Moth cache state.");
        bool cacheWarm = cacheTask.Result.Item1;

        Assert.IsNotNull(AvatarLoadGenerationField, "BasisAvatarLoadThread generation field changed; realistic prepared-join harness needs updating.");
        int generation = (int)AvatarLoadGenerationField.GetValue(null);

        // This is the same small avatar descriptor carried by the network spawn record. Each
        // prepared join gets its own decoded BasisLoadableBundle, matching BasisAvatarLoadThread.
        BasisLoadableBundle networkSourceBundle = CreateFoxMothBundle();
        byte[] avatarBytes = BasisBundleConversionNetwork.ConvertBasisLoadableBundleToBytes(networkSourceBundle);

        var observations = new List<RemoteObservation>(InstanceCount);
        int firstCandidateId = 50000;
        for (int i = 0; i < InstanceCount; i++)
        {
            ushort playerId = FindUnusedPlayerId(firstCandidateId + i);
            fakePlayerIds.Add(playerId);

            string uuid = Guid.NewGuid().ToString("N");
            string displayName = $"FoxMoth Load Test {i:D2}";
            var metadata = new ClientMetaDataMessage
            {
                playerUUID = uuid,
                playerDisplayName = displayName,
                playerPlatform = "Windows"
            };
            var avatarChange = new ClientAvatarChangeMessage
            {
                loadMode = (byte)BasisLoadMode.Download,
                byteArray = avatarBytes,
                LocalAvatarIndex = 1,
                ArmScale = 1f,
                LegScale = 1f,
                TorsoScale = 1f
            };
            var ready = new ServerReadyMessage
            {
                playerIdMessage = new PlayerIdMessage { playerID = playerId },
                localReadyMessage = new ReadyMessage
                {
                    playerMetaDataMessage = metadata,
                    clientAvatarChangeMessage = avatarChange,
                    // BasisAvatarLoadThread tolerates a missing initial pose and the receiver
                    // holds the loading avatar at rest until the first pose packet. That keeps
                    // this benchmark focused on the normal avatar-load path, not pose encoding.
                    localAvatarSyncMessage = new LocalAvatarSyncMessage()
                }
            };

            // The production load thread warms this cache before it queues the Unity-affine
            // lifecycle action. Do it before timing so the main-thread portion sees the same state.
            BasisPlayerSettingsManager.Warm(uuid);

            var prepared = new BasisPreparedJoin
            {
                Ready = ready,
                PlayerId = playerId,
                SafeDisplayName = BasisRemotePlayer.BuildSafeDisplayName(displayName),
                InitialAvatar = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(avatarBytes),
                Generation = generation,
                SpawnPose = null
            };

            var observation = new RemoteObservation { Id = playerId };
            observations.Add(observation);
            BasisNetworkPlayers.JoiningPlayers.TryAdd(playerId, 0);

            // This is the same queue BasisAvatarLoadThread.Prepare uses. BasisEventDriver drains
            // it with LifecycleBudgetMillisecondsPerFrame, so fallback-player spawn cost is paced
            // exactly as it is for real joins.
            BasisNetworkHandleRemoval.LifecycleQueue.Enqueue(() =>
            {
                BasisNetworkPlayer networkPlayer = InvokePreparedRemoteFactory(prepared);
                if (networkPlayer != null && networkPlayer.Player is BasisRemotePlayer remote)
                {
                    observation.Player = remote;
                    observation.SpawnFrame = Time.frameCount;
                    observation.SpawnTime = Time.realtimeSinceStartup;
                }
            });
        }

        var options = ProfilerRecorderOptions.WrapAroundWhenCapacityReached |
                      ProfilerRecorderOptions.StartImmediately |
                      ProfilerRecorderOptions.SumAllSamplesInFrame;

        using var cilboxStartRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "CilboxProxy.Start.LivePlaytest", 1, options);
        using var avatarInstallRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "BasisDriver.Avatar.Install", 1, options);
        using var avatarCalibrateRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "BasisDriver.Avatar.Calibrate", 1, options);
        using var gcRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1, options);
        using var playerLoopRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "PlayerLoop", 1, options);

        int enqueueFrame = Time.frameCount;
        float enqueueTime = Time.realtimeSinceStartup;
        int previousSpawned = -1;
        int previousRangeCommitted = -1;
        int previousReal = -1;
        int previousCilboxReady = -1;
        int allRealFrame = -1;
        int settleRemaining = SettleFrames;
        int peakNewRealInFrame = 0;
        int lastRealCount = 0;
        float rangeTickAccumulator = 0f;
        int rangeAdmissionTicks = 0;
        int peakRangeReloadsAdmitted = 0;

        var frameDeltaNs = new List<long>(2048);
        var playerLoopNs = new List<long>(2048);
        var gcBytes = new List<long>(2048);
        var cilboxStartNs = new List<long>(2048);
        var avatarInstallNs = new List<long>(2048);
        var avatarCalibrateNs = new List<long>(2048);

        float deadline = Time.realtimeSinceStartup + ObservationTimeoutSeconds;
        while (Time.realtimeSinceStartup < deadline)
        {
            yield return null;

            // The headless validator has no real remote receiver/pose stream, so
            // BasisTransmissionResults cannot produce pAvatarRange[] for these prepared joins.
            // Reproduce only that missing distance-result side here: every synthetic remote is
            // intentionally colocated with the local player, so its production range result is
            // "in range". The debounce, count budget, wall-clock budget, pending state, and
            // ReloadAvatar call below are copied from BasisTransmissionResults. All expensive
            // work after ReloadAvatar is still the normal Basis path.
            rangeTickAccumulator = Mathf.Min(
                rangeTickAccumulator + Time.deltaTime,
                HeadlessRangeTickIntervalSeconds * 2f);
            if (rangeTickAccumulator >= HeadlessRangeTickIntervalSeconds)
            {
                int admitted = AdvanceNormalInRangeTransitions(observations);
                rangeAdmissionTicks++;
                if (admitted > peakRangeReloadsAdmitted) peakRangeReloadsAdmitted = admitted;
                rangeTickAccumulator = Mathf.Max(0f, rangeTickAccumulator - HeadlessRangeTickIntervalSeconds);
            }

            int spawned = 0;
            int fallback = 0;
            int rangeCommitted = 0;
            int loading = 0;
            int real = 0;
            int cilboxReady = 0;
            int vixxyReady = 0;

            for (int i = 0; i < observations.Count; i++)
            {
                RemoteObservation observation = observations[i];
                BasisRemotePlayer remote = observation.Player;
                if (remote == null) continue;
                spawned++;

                if (remote.InAvatarRange)
                {
                    rangeCommitted++;
                    if (observation.RangeCommitFrame < 0)
                    {
                        observation.RangeCommitFrame = Time.frameCount;
                        observation.RangeCommitTime = Time.realtimeSinceStartup;
                    }
                }

                if (remote.IsLoadingAnAvatar) loading++;
                if (remote.BasisAvatar != null && remote.IsConsideredFallBackAvatar) fallback++;

                if (remote.BasisAvatar != null && !remote.IsConsideredFallBackAvatar)
                {
                    real++;
                    if (observation.RealAvatarFrame < 0)
                    {
                        observation.RealAvatarFrame = Time.frameCount;
                        observation.RealAvatarTime = Time.realtimeSinceStartup;
                    }

                    CilboxProxy[] proxies = remote.BasisAvatar.GetComponentsInChildren<CilboxProxy>(true);
                    bool allProxiesReady = proxies.Length > 0;
                    for (int p = 0; p < proxies.Length; p++)
                    {
                        if (!proxies[p].ValidationProxyIsSetup)
                        {
                            allProxiesReady = false;
                            break;
                        }
                    }
                    if (allProxiesReady)
                    {
                        cilboxReady++;
                        if (observation.CilboxReadyFrame < 0)
                        {
                            observation.CilboxReadyFrame = Time.frameCount;
                            observation.CilboxReadyTime = Time.realtimeSinceStartup;
                        }
                    }

                    if (remote.BasisAvatar.GetComponentInChildren<HVRVixxyOrchestrator>(true) != null)
                    {
                        vixxyReady++;
                    }
                }
            }

            int newRealThisFrame = real - lastRealCount;
            if (newRealThisFrame > peakNewRealInFrame) peakNewRealInFrame = newRealThisFrame;
            lastRealCount = real;

            long frameNs = (long)(Time.unscaledDeltaTime * 1000000000.0f);
            long loopNs = playerLoopRecorder.Valid ? playerLoopRecorder.LastValue : 0;
            long gc = gcRecorder.Valid ? gcRecorder.LastValue : 0;
            long cilboxStart = cilboxStartRecorder.Valid ? cilboxStartRecorder.LastValue : 0;
            long install = avatarInstallRecorder.Valid ? avatarInstallRecorder.LastValue : 0;
            long calibrate = avatarCalibrateRecorder.Valid ? avatarCalibrateRecorder.LastValue : 0;

            frameDeltaNs.Add(frameNs);
            playerLoopNs.Add(loopNs);
            gcBytes.Add(gc);
            cilboxStartNs.Add(cilboxStart);
            avatarInstallNs.Add(install);
            avatarCalibrateNs.Add(calibrate);

            bool stateChanged = spawned != previousSpawned || rangeCommitted != previousRangeCommitted ||
                                real != previousReal || cilboxReady != previousCilboxReady;
            bool hitchFrame = frameNs >= 50_000_000L;
            if (stateChanged || hitchFrame)
            {
                Debug.Log(
                    $"CILBOX_REAL_LOAD|FRAME|unityFrame={Time.frameCount}|elapsedMs={(Time.realtimeSinceStartup - enqueueTime) * 1000.0f:F1}" +
                    $"|spawned={spawned}/{InstanceCount}|rangeCommitted={rangeCommitted}/{InstanceCount}|fallback={fallback}|loading={loading}" +
                    $"|real={real}/{InstanceCount}|cilboxReady={cilboxReady}/{InstanceCount}|vixxyReady={vixxyReady}/{InstanceCount}" +
                    $"|lifecyclePending={BasisNetworkHandleRemoval.LifecycleQueue.Count}|setupPending={BasisAvatarSetupBudget.PendingSetups}" +
                    $"|frameDeltaMs={frameNs / 1000000.0:F3}|playerLoopUs={loopNs / 1000.0:F3}|gcBytes={gc}" +
                    $"|cilboxStartUs={cilboxStart / 1000.0:F3}|avatarInstallUs={install / 1000.0:F3}|avatarCalibrateUs={calibrate / 1000.0:F3}");
            }

            previousSpawned = spawned;
            previousRangeCommitted = rangeCommitted;
            previousReal = real;
            previousCilboxReady = cilboxReady;

            if (real == InstanceCount && cilboxReady == InstanceCount && vixxyReady == InstanceCount)
            {
                if (allRealFrame < 0) allRealFrame = Time.frameCount;
                settleRemaining--;
                if (settleRemaining <= 0) break;
            }
            else
            {
                settleRemaining = SettleFrames;
            }
        }

        int finalSpawned = 0;
        int finalReal = 0;
        int finalCilboxReady = 0;
        var spawnToRealMs = new List<long>(InstanceCount);
        var rangeToRealMs = new List<long>(InstanceCount);
        float firstSpawnTime = float.MaxValue;
        float lastSpawnTime = -1f;
        float firstRealTime = float.MaxValue;
        float lastRealTime = -1f;

        for (int i = 0; i < observations.Count; i++)
        {
            RemoteObservation observation = observations[i];
            if (observation.Player != null)
            {
                finalSpawned++;
                if (observation.SpawnTime >= 0f)
                {
                    if (observation.SpawnTime < firstSpawnTime) firstSpawnTime = observation.SpawnTime;
                    if (observation.SpawnTime > lastSpawnTime) lastSpawnTime = observation.SpawnTime;
                }
            }
            if (observation.RealAvatarTime >= 0f)
            {
                finalReal++;
                if (observation.RealAvatarTime < firstRealTime) firstRealTime = observation.RealAvatarTime;
                if (observation.RealAvatarTime > lastRealTime) lastRealTime = observation.RealAvatarTime;
                spawnToRealMs.Add((long)((observation.RealAvatarTime - observation.SpawnTime) * 1000.0f));
                if (observation.RangeCommitTime >= 0f)
                {
                    rangeToRealMs.Add((long)((observation.RealAvatarTime - observation.RangeCommitTime) * 1000.0f));
                }
            }
            if (observation.CilboxReadyTime >= 0f) finalCilboxReady++;
        }

        spawnToRealMs.Sort();
        rangeToRealMs.Sort();

        Assert.AreEqual(InstanceCount, finalSpawned, "Normal lifecycle queue did not spawn all 20 remote players.");
        Assert.AreEqual(InstanceCount, finalReal, "Normal remote-avatar pipeline did not install all 20 Fox Moth avatars.");
        Assert.AreEqual(InstanceCount, finalCilboxReady, "All real avatars installed, but not every Fox Moth Cilbox proxy reached setup.");
        Assert.GreaterOrEqual(allRealFrame, 0, "Real-avatar observation never reached the fully-loaded state.");

        Debug.Log(
            $"CILBOX_REAL_LOAD|SUMMARY|instances={InstanceCount}|cacheWarm={cacheWarm}|enqueueFrame={enqueueFrame}|allRealFrame={allRealFrame}" +
            $"|lifecycleBudgetCount={BasisNetworkHandleRemoval.LifecycleBudgetPerFrame}|lifecycleBudgetMs={BasisNetworkHandleRemoval.LifecycleBudgetMillisecondsPerFrame:F2}" +
            $"|reloadBudgetCount={BasisTransmissionResults.MaxAvatarReloadsPerTick}|reloadBudgetMs={BasisTransmissionResults.MaxAvatarReloadMillisecondsPerTick:F2}" +
            $"|rangeDebounceMs={BasisRemotePlayer.AvatarRangeDebounceSeconds * 1000.0f:F0}|rangeTickMs={HeadlessRangeTickIntervalSeconds * 1000.0f:F0}" +
            $"|rangeAdmissionTicks={rangeAdmissionTicks}|peakRangeReloadsAdmitted={peakRangeReloadsAdmitted}|setupBudgetCount={BasisAvatarSetupBudget.BudgetPerFrame}|setupBudgetMs={BasisAvatarSetupBudget.MaxInstallMillisecondsPerFrame:F2}" +
            $"|firstFallbackVisibleMs={(firstSpawnTime - enqueueTime) * 1000.0f:F1}|allFallbackSpawnedMs={(lastSpawnTime - enqueueTime) * 1000.0f:F1}" +
            $"|firstRealVisibleMs={(firstRealTime - enqueueTime) * 1000.0f:F1}|allRealVisibleMs={(lastRealTime - enqueueTime) * 1000.0f:F1}" +
            $"|spawnToRealP50Ms={Percentile(spawnToRealMs, 0.50)}|spawnToRealP95Ms={Percentile(spawnToRealMs, 0.95)}|spawnToRealMaxMs={Max(spawnToRealMs)}" +
            $"|rangeToRealP50Ms={Percentile(rangeToRealMs, 0.50)}|rangeToRealP95Ms={Percentile(rangeToRealMs, 0.95)}|rangeToRealMaxMs={Max(rangeToRealMs)}" +
            $"|peakNewRealInFrame={peakNewRealInFrame}|frameDeltaPeakMs={Max(frameDeltaNs) / 1000000.0:F3}|playerLoopPeakUs={Max(playerLoopNs) / 1000.0:F3}" +
            $"|gcTotalBytes={Sum(gcBytes)}|gcPeakFrameBytes={Max(gcBytes)}|cilboxStartTotalUs={Sum(cilboxStartNs) / 1000.0:F3}|cilboxStartPeakFrameUs={Max(cilboxStartNs) / 1000.0:F3}" +
            $"|avatarInstallTotalUs={Sum(avatarInstallNs) / 1000.0:F3}|avatarInstallPeakFrameUs={Max(avatarInstallNs) / 1000.0:F3}" +
            $"|avatarCalibrateTotalUs={Sum(avatarCalibrateNs) / 1000.0:F3}|avatarCalibratePeakFrameUs={Max(avatarCalibrateNs) / 1000.0:F3}");
    }

    private static int AdvanceNormalInRangeTransitions(List<RemoteObservation> observations)
    {
        int avatarReloadsAdmitted = 0;
        long avatarReloadBudgetTicks = (long)(BasisTransmissionResults.MaxAvatarReloadMillisecondsPerTick *
                                               (System.Diagnostics.Stopwatch.Frequency / 1000.0));
        var avatarReloadClock = new System.Diagnostics.Stopwatch();
        float now = Time.unscaledTime;

        for (int i = 0; i < observations.Count; i++)
        {
            BasisRemotePlayer remote = observations[i].Player;
            if (remote == null) continue;

            // These benchmark players are deliberately placed at the local origin, so this is
            // the pAvatarRange[i] value the production distance/cap jobs would return.
            bool inRange = true;
            if (remote.AvatarAlwaysLoaded)
            {
                inRange = true;
            }

            if (inRange != remote.InAvatarRange)
            {
                if (!remote.PendingRangeActive || remote.PendingRangeTarget != inRange)
                {
                    remote.PendingRangeActive = true;
                    remote.PendingRangeTarget = inRange;
                    remote.PendingRangeCommitTime = now + BasisRemotePlayer.AvatarRangeDebounceSeconds;
                }
                else if (now >= remote.PendingRangeCommitTime)
                {
                    bool willReload = !remote.IsLoadingAnAvatar &&
                                      (inRange || !remote.IsConsideredFallBackAvatar);
                    bool reloadBudgetSpent = avatarReloadsAdmitted >= BasisTransmissionResults.MaxAvatarReloadsPerTick ||
                                             avatarReloadClock.ElapsedTicks >= avatarReloadBudgetTicks;
                    if (willReload && reloadBudgetSpent)
                    {
                        continue;
                    }

                    remote.InAvatarRange = inRange;
                    remote.PendingRangeActive = false;

                    if (willReload)
                    {
                        avatarReloadsAdmitted++;
                        avatarReloadClock.Start();
                        remote.ReloadAvatar();
                        avatarReloadClock.Stop();
                    }
                }
            }
            else if (remote.PendingRangeActive)
            {
                remote.PendingRangeActive = false;
            }
        }

        return avatarReloadsAdmitted;
    }

    private static MethodInfo FindPreparedRemoteFactoryMethod()
    {
        MethodInfo[] methods = typeof(BasisRemotePlayerFactory).GetMethods(BindingFlags.Static | BindingFlags.Public);
        for (int i = 0; i < methods.Length; i++)
        {
            ParameterInfo[] parameters = methods[i].GetParameters();
            if (methods[i].Name == "CreateRemotePlayer" && parameters.Length == 2 && parameters[0].ParameterType == typeof(BasisPreparedJoin))
            {
                return methods[i];
            }
        }
        return null;
    }

    private static BasisNetworkPlayer InvokePreparedRemoteFactory(BasisPreparedJoin prepared)
    {
        Assert.IsNotNull(NetworkInstantiationParametersField, "BasisNetworkManagement.instantiationParameters field changed; realistic join harness needs updating.");
        Assert.IsNotNull(PreparedRemoteFactoryMethod, "BasisRemotePlayerFactory prepared-join overload changed; realistic join harness needs updating.");
        object instantiationParameters = NetworkInstantiationParametersField.GetValue(null);
        return (BasisNetworkPlayer)PreparedRemoteFactoryMethod.Invoke(null, new[] { (object)prepared, instantiationParameters });
    }

    private static BasisLoadableBundle CreateFoxMothBundle()
    {
        return new BasisLoadableBundle
        {
            UnlockPassword = AvatarPassword,
            BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle
            {
                RemoteBeeFileLocation = AvatarUrl,
                IsNetworkSourced = true
            },
            BasisBundleConnector = new BasisBundleConnector(),
            BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle()
        };
    }

    private static ushort FindUnusedPlayerId(int preferred)
    {
        for (int offset = 0; offset < ushort.MaxValue; offset++)
        {
            ushort id = (ushort)((preferred + offset) % ushort.MaxValue);
            if (id == 0) continue;
            if (!BasisNetworkPlayers.Players.ContainsKey(id) && !BasisNetworkPlayers.JoiningPlayers.ContainsKey(id))
            {
                return id;
            }
        }
        throw new InvalidOperationException("Unable to allocate a fake remote player id for the load benchmark.");
    }

    private static long Sum(List<long> values)
    {
        long sum = 0;
        for (int i = 0; i < values.Count; i++) sum += values[i];
        return sum;
    }

    private static long Max(List<long> values)
    {
        long max = 0;
        for (int i = 0; i < values.Count; i++) if (values[i] > max) max = values[i];
        return max;
    }

    private static long Percentile(List<long> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int index = (int)Math.Ceiling(sorted.Count * p) - 1;
        if (index < 0) index = 0;
        if (index >= sorted.Count) index = sorted.Count - 1;
        return sorted[index];
    }
}
