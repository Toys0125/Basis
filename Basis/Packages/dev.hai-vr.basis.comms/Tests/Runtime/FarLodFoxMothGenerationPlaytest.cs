using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using NUnit.Framework;
using Unity.Burst;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
using static SerializableBasis;

public class FarLodFoxMothGenerationPlaytest
{
    private const string AvatarUrl = "https://dipcdn.net/Fox-Moth-v1.41-3rVn";
    private const string AvatarPassword = "a0225a75691b5e83169c4c045c3588cbdaa48ae0af558c4722c3b21e58485768";
    private const float HeadlessRangeTickIntervalSeconds = 0.05f;

    private static readonly FieldInfo AvatarLoadGenerationField = typeof(BasisAvatarLoadThread).GetField("sGeneration", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo NetworkInstantiationParametersField = typeof(BasisNetworkManagement).GetField("instantiationParameters", BindingFlags.Static | BindingFlags.Public);
    private static readonly MethodInfo PreparedRemoteFactoryMethod = FindPreparedRemoteFactoryMethod();

    private ushort playerId;

    [TearDown]
    public void Cleanup()
    {
        if (playerId != 0)
        {
            if (BasisNetworkPlayers.Players.ContainsKey(playerId))
            {
                BasisNetworkHandleRemoval.HandleDisconnectIdImmediate(playerId);
            }
            BasisNetworkPlayers.JoiningPlayers.TryRemove(playerId, out _);
        }
        BasisBundleLoadAsset.DisableFrameSplitForValidation = false;
        BasisSceneFactory.SkipSceneCameraSetupForValidation = false;
        LogAssert.ignoreFailingMessages = false;
    }

    [UnityTest]
    [Timeout(420000)]
    public IEnumerator FoxMoth_GenerateFarLod_ScalarAndBurst()
    {
        LogAssert.ignoreFailingMessages = true;
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
        Assert.IsTrue(BasisNetworkManagement.IsInitialized, "Basis networking never initialized.");

        float drainDeadline = Time.realtimeSinceStartup + 10f;
        while (BasisNetworkHandleRemoval.LifecycleQueue.Count > 0 && Time.realtimeSinceStartup < drainDeadline)
        {
            yield return null;
        }
        Assert.AreEqual(0, BasisNetworkHandleRemoval.LifecycleQueue.Count);

        Assert.IsNotNull(AvatarLoadGenerationField);
        int generation = (int)AvatarLoadGenerationField.GetValue(null);
        BasisLoadableBundle bundle = CreateFoxMothBundle();
        byte[] avatarBytes = BasisBundleConversionNetwork.ConvertBasisLoadableBundleToBytes(bundle);

        playerId = FindUnusedPlayerId(52000);
        string uuid = Guid.NewGuid().ToString("N");
        BasisPlayerSettingsManager.Warm(uuid);
        var metadata = new ClientMetaDataMessage
        {
            playerUUID = uuid,
            playerDisplayName = "FoxMoth FarLOD Test",
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
                localAvatarSyncMessage = new LocalAvatarSyncMessage()
            }
        };
        var prepared = new BasisPreparedJoin
        {
            Ready = ready,
            PlayerId = playerId,
            SafeDisplayName = BasisRemotePlayer.BuildSafeDisplayName(metadata.playerDisplayName),
            InitialAvatar = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(avatarBytes),
            Generation = generation,
            SpawnPose = null
        };

        BasisRemotePlayer remote = null;
        BasisNetworkPlayers.JoiningPlayers.TryAdd(playerId, 0);
        BasisNetworkHandleRemoval.LifecycleQueue.Enqueue(() =>
        {
            BasisNetworkPlayer networkPlayer = InvokePreparedRemoteFactory(prepared);
            remote = networkPlayer?.Player as BasisRemotePlayer;
        });

        float rangeTickAccumulator = 0f;
        float loadStart = Time.realtimeSinceStartup;
        float loadDeadline = loadStart + 180f;
        while (Time.realtimeSinceStartup < loadDeadline)
        {
            yield return null;
            if (remote != null && remote.BasisAvatar != null && !remote.IsConsideredFallBackAvatar && !remote.IsLoadingAnAvatar)
            {
                break;
            }

            rangeTickAccumulator = Mathf.Min(rangeTickAccumulator + Time.deltaTime, HeadlessRangeTickIntervalSeconds * 2f);
            if (rangeTickAccumulator >= HeadlessRangeTickIntervalSeconds && remote != null)
            {
                AdvanceNormalInRangeTransition(remote);
                rangeTickAccumulator = Mathf.Max(0f, rangeTickAccumulator - HeadlessRangeTickIntervalSeconds);
            }
        }

        Assert.IsNotNull(remote, "Fox Moth remote player was never created.");
        Assert.IsNotNull(remote.BasisAvatar, "Fox Moth avatar never loaded.");
        Assert.IsFalse(remote.IsConsideredFallBackAvatar, "Fox Moth remained on the fallback avatar.");
        Debug.Log($"FARLOD_FOXMOTH|LOAD|elapsedMs={(Time.realtimeSinceStartup - loadStart) * 1000f:F1}|avatar={remote.BasisAvatar.name}");

        Type generatorType = Type.GetType("BasisFarLodGenerator, BasisSDKEditor");
        Assert.IsNotNull(generatorType, "BasisFarLodGenerator editor assembly was not loaded in PlayMode.");
        MethodInfo generateMethod = generatorType.GetMethod("Generate", BindingFlags.Public | BindingFlags.Static);
        FieldInfo activeReportField = generatorType.GetField("ActiveReport", BindingFlags.Public | BindingFlags.Static);
        FieldInfo verboseField = generatorType.GetField("VerboseLogging", BindingFlags.Public | BindingFlags.Static);
        Type reportType = generatorType.GetNestedType("GenerationReport", BindingFlags.Public);
        Assert.IsNotNull(generateMethod);
        Assert.IsNotNull(activeReportField);
        Assert.IsNotNull(reportType);

        if (verboseField != null) verboseField.SetValue(null, true);

        bool previousBurst = BurstCompiler.Options.EnableBurstCompilation;
        try
        {
            RunGeneration(generatorType, reportType, activeReportField, generateMethod, remote.BasisAvatar, false);
            RunGeneration(generatorType, reportType, activeReportField, generateMethod, remote.BasisAvatar, true);
        }
        finally
        {
            BurstCompiler.Options.EnableBurstCompilation = previousBurst;
            activeReportField.SetValue(null, null);
        }
    }

    private static void RunGeneration(Type generatorType, Type reportType, FieldInfo activeReportField,
        MethodInfo generateMethod, BasisAvatar avatar, bool burstEnabled)
    {
        BurstCompiler.Options.EnableBurstCompilation = burstEnabled;
        object report = Activator.CreateInstance(reportType);
        activeReportField.SetValue(null, report);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long beforeAllocated = GC.GetTotalMemory(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        object payload = generateMethod.Invoke(null, new object[] { avatar });
        stopwatch.Stop();
        long afterAllocated = GC.GetTotalMemory(false);

        Assert.IsNotNull(payload, $"FarLOD generation failed with Burst={burstEnabled}.");
        FieldInfo entriesField = reportType.GetField("Entries", BindingFlags.Public | BindingFlags.Instance);
        IEnumerable entries = entriesField?.GetValue(report) as IEnumerable;
        var stageParts = new List<string>();
        if (entries != null)
        {
            foreach (object entry in entries)
            {
                Type entryType = entry.GetType();
                string label = (string)entryType.GetField("Label").GetValue(entry);
                double seconds = (double)entryType.GetField("Seconds").GetValue(entry);
                string detail = (string)entryType.GetField("Detail").GetValue(entry);
                stageParts.Add($"{label}={seconds * 1000.0:F2}ms[{detail}]");
            }
        }

        Debug.Log($"FARLOD_FOXMOTH|GEN|burst={burstEnabled}|elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}|managedDeltaBytes={afterAllocated - beforeAllocated}|stages={string.Join(";", stageParts)}");
    }

    private static void AdvanceNormalInRangeTransition(BasisRemotePlayer remote)
    {
        float now = Time.unscaledTime;
        if (remote.InAvatarRange)
        {
            if (remote.PendingRangeActive) remote.PendingRangeActive = false;
            return;
        }

        if (!remote.PendingRangeActive || !remote.PendingRangeTarget)
        {
            remote.PendingRangeActive = true;
            remote.PendingRangeTarget = true;
            remote.PendingRangeCommitTime = now + BasisRemotePlayer.AvatarRangeDebounceSeconds;
            return;
        }
        if (now < remote.PendingRangeCommitTime)
        {
            return;
        }

        remote.InAvatarRange = true;
        remote.PendingRangeActive = false;
        if (!remote.IsLoadingAnAvatar)
        {
            remote.ReloadAvatar();
        }
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
        Assert.IsNotNull(NetworkInstantiationParametersField);
        Assert.IsNotNull(PreparedRemoteFactoryMethod);
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
            if (!BasisNetworkPlayers.Players.ContainsKey(id) && !BasisNetworkPlayers.JoiningPlayers.ContainsKey(id)) return id;
        }
        throw new InvalidOperationException("Unable to allocate a fake remote player id.");
    }
}
