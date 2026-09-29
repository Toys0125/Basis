using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

public readonly struct AvatarPsoWarmResult
{
    public readonly bool Attempted;
    public readonly bool PackagedPresent;
    public readonly bool RuntimePresent;
    public readonly int VariantCount;
    public readonly double QueueWaitMilliseconds;
    public readonly double WarmMilliseconds;

    public AvatarPsoWarmResult(bool attempted, bool packagedPresent, bool runtimePresent, int variantCount, double queueWaitMilliseconds, double warmMilliseconds)
    {
        Attempted = attempted;
        PackagedPresent = packagedPresent;
        RuntimePresent = runtimePresent;
        VariantCount = variantCount;
        QueueWaitMilliseconds = queueWaitMilliseconds;
        WarmMilliseconds = warmMilliseconds;
    }

    public static AvatarPsoWarmResult NoOp => default;
}

public sealed class BasisAvatarPsoPreparedData
{
    public string PackagedPath;
    public bool PackagedPresent;
}

/// <summary>
/// Per-avatar Vulkan GraphicsStateCollection readiness barrier. Packaged sidecar I/O can start as
/// soon as connector metadata is known; actual collection loading/warming waits until the avatar
/// prefab is resident and ContentPolice has finalized its materials.
/// </summary>
public static class BasisAvatarPsoLoader
{
    private const int DefaultMaxConcurrentWarmJobs = 2;
    private const int RuntimeTraceFrames = 3;

    private static readonly object StateLock = new object();
    private static readonly Dictionary<string, SemaphoreSlim> ContentGates = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
    private static readonly Dictionary<string, SemaphoreSlim> PreparationGates = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
    private static readonly HashSet<string> WarmedContent = new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedFailures = new HashSet<string>(StringComparer.Ordinal);
    private static readonly SemaphoreSlim RuntimeTraceGate = new SemaphoreSlim(1, 1);
    private static SemaphoreSlim _avatarPsoWarmGate = new SemaphoreSlim(DefaultMaxConcurrentWarmJobs, DefaultMaxConcurrentWarmJobs);
    private static bool _warmGateUsed;

    private static readonly ProfilerMarker PsoFileLoadMarker = new ProfilerMarker("Avatar.PsoFileLoad");
    private static readonly ProfilerMarker PsoWarmScheduleMarker = new ProfilerMarker("Avatar.PsoWarm");

    public static bool VerboseLogging { get; set; }
    public static int MaxConcurrentWarmJobs { get; private set; } = DefaultMaxConcurrentWarmJobs;

    /// <summary>
    /// Settings are applied during startup before content loading. Once the gate has been used its
    /// capacity stays fixed for the session so queued waiters can never be orphaned by a resize.
    /// </summary>
    public static void ConfigureConcurrency(int maxConcurrentWarmJobs)
    {
        int clamped = Mathf.Clamp(maxConcurrentWarmJobs, 1, 8);
        lock (StateLock)
        {
            if (_warmGateUsed)
            {
                if (VerboseLogging && clamped != MaxConcurrentWarmJobs)
                    BasisDebug.Log($"Avatar PSO concurrency change to {clamped} will apply next launch.", BasisDebug.LogTag.Event);
                return;
            }

            MaxConcurrentWarmJobs = clamped;
            _avatarPsoWarmGate = new SemaphoreSlim(clamped, clamped);
        }
    }

    public static void BeginPackagedPreparation(BasisTrackedBundleWrapper wrapper, BasisBundleGenerated contentSection, CancellationToken cancellationToken, long maxDownloadSizeInBytes)
    {
        if (wrapper == null || contentSection == null || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
            return;
        if (!BasisBundleConnector.TryGetGraphicsStateCollection(wrapper.LoadableBundle?.BasisBundleConnector, contentSection, out BasisBundleGenerated psoSection))
            return;

        if (wrapper.AvatarPsoPreparationTask == null)
        {
            wrapper.AvatarPsoGenerated = psoSection;
            wrapper.AvatarPsoPreparationTask = PreparePackagedAsync(wrapper, contentSection, psoSection, cancellationToken, maxDownloadSizeInBytes);
        }
    }

    public static async Task<AvatarPsoWarmResult> WarmAsync(BasisTrackedBundleWrapper bundle, BasisBundleGenerated generated, GameObject inactiveAvatar, CancellationToken cancellationToken)
    {
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan || bundle == null || generated == null || inactiveAvatar == null)
            return AvatarPsoWarmResult.NoOp;

        cancellationToken.ThrowIfCancellationRequested();

        // A GraphicsStateCollection sidecar is avatar-only today. Keep props/worlds on the existing
        // ShaderVariantCollection/global progressive path until they have their own readiness rules.
        if (!inactiveAvatar.TryGetComponent<BasisAvatar>(out _))
            return AvatarPsoWarmResult.NoOp;

        string cacheKey = GetCacheKey(generated);
        SemaphoreSlim contentGate = GetContentGate(cacheKey);
        await contentGate.WaitAsync(cancellationToken);
        try
        {
            lock (StateLock)
            {
                if (WarmedContent.Contains(cacheKey))
                    return AvatarPsoWarmResult.NoOp;
            }

            BasisAvatarPsoPreparedData prepared = null;
            if (bundle.AvatarPsoPreparationTask != null)
            {
                try
                {
                    prepared = await bundle.AvatarPsoPreparationTask;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogFailureOnce(cacheKey + ":prepare", $"Avatar PSO sidecar preparation failed for {generated.AssetBundleHash}; continuing without packaged PSOs ({ex.Message})");
                }
            }

            string runtimePath = GetRuntimePath(generated);
            bool packagedPresent = prepared != null && prepared.PackagedPresent && File.Exists(prepared.PackagedPath);
            bool runtimePresent = File.Exists(runtimePath);
            if (!packagedPresent && !runtimePresent)
                return new AvatarPsoWarmResult(false, false, false, 0, 0, 0);

            Stopwatch queueWait = Stopwatch.StartNew();
            lock (StateLock) _warmGateUsed = true;
            await _avatarPsoWarmGate.WaitAsync(cancellationToken);
            queueWait.Stop();

            int variants = 0;
            Stopwatch warmTimer = Stopwatch.StartNew();
            bool warmedAny = false;
            try
            {
                if (packagedPresent)
                    warmedAny |= await WarmFileAsync(prepared.PackagedPath, cacheKey + ":packaged", cancellationToken, count => variants += count);
                if (runtimePresent)
                    warmedAny |= await WarmFileAsync(runtimePath, cacheKey + ":runtime", cancellationToken, count => variants += count);
            }
            finally
            {
                warmTimer.Stop();
                _avatarPsoWarmGate.Release();
            }

            if (warmedAny)
            {
                lock (StateLock) WarmedContent.Add(cacheKey);
            }

            if (VerboseLogging)
            {
                BasisDebug.Log(
                    $"Avatar PSO warm hash={generated.AssetBundleHash} api={SystemInfo.graphicsDeviceType} variants={variants} " +
                    $"packaged={packagedPresent} runtime={runtimePresent} queueMs={queueWait.Elapsed.TotalMilliseconds:F1} warmMs={warmTimer.Elapsed.TotalMilliseconds:F1}",
                    BasisDebug.LogTag.Event);
            }

            return new AvatarPsoWarmResult(true, packagedPresent, runtimePresent, variants, queueWait.Elapsed.TotalMilliseconds, warmTimer.Elapsed.TotalMilliseconds);
        }
        finally
        {
            contentGate.Release();
        }
    }

    /// <summary>
    /// Starts a short per-content runtime trace immediately after activation so creator-capture
    /// misses can be replayed on the next load. Errors never affect the live avatar.
    /// </summary>
    public static async Task TraceRuntimeAsync(BasisTrackedBundleWrapper bundle, BasisBundleGenerated generated, GameObject activeAvatar, CancellationToken cancellationToken)
    {
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan || generated == null || activeAvatar == null || !activeAvatar.TryGetComponent<BasisAvatar>(out _))
            return;

        string cacheKey = GetCacheKey(generated);
        bool gateHeld = false;
        GraphicsStateCollection collection = null;
        try
        {
            await RuntimeTraceGate.WaitAsync(cancellationToken);
            gateHeld = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (activeAvatar == null)
                return;

            string runtimePath = GetRuntimePath(generated);
            collection = new GraphicsStateCollection();
            if (File.Exists(runtimePath))
            {
                try
                {
                    using (PsoFileLoadMarker.Auto())
                        collection.LoadFromFile(runtimePath);
                }
                catch (Exception ex)
                {
                    LogFailureOnce(cacheKey + ":runtime-load", $"Avatar runtime PSO cache was invalid and will be rebuilt ({ex.Message})");
                    TryDelete(runtimePath);
                    collection = new GraphicsStateCollection();
                }
            }

            collection.BeginTrace();
            if (!collection.isTracing)
                return;

            for (int i = 0; i < RuntimeTraceFrames; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (activeAvatar == null)
                    return;
                await Task.Yield();
            }

            collection.EndTrace();
            if (collection.variantCount > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(runtimePath));
                collection.SaveToFile(runtimePath);
                if (VerboseLogging)
                    BasisDebug.Log($"Avatar runtime PSO trace hash={generated.AssetBundleHash} variants={collection.variantCount}", BasisDebug.LogTag.Event);
            }
        }
        catch (OperationCanceledException)
        {
            if (collection != null && collection.isTracing)
            {
                try { collection.EndTrace(); } catch { }
            }
        }
        catch (Exception ex)
        {
            if (collection != null && collection.isTracing)
            {
                try { collection.EndTrace(); } catch { }
            }
            LogFailureOnce(cacheKey + ":trace", $"Avatar runtime PSO tracing failed; continuing normally ({ex.Message})");
        }
        finally
        {
            if (gateHeld)
                RuntimeTraceGate.Release();
        }
    }

    private static async Task<BasisAvatarPsoPreparedData> PreparePackagedAsync(BasisTrackedBundleWrapper wrapper, BasisBundleGenerated contentSection, BasisBundleGenerated psoSection, CancellationToken cancellationToken, long maxDownloadSizeInBytes)
    {
        string cacheKey = GetCacheKey(contentSection);
        string packagedPath = GetPackagedPath(contentSection);
        if (!IsCompatiblePsoMetadata(contentSection, psoSection, out string incompatibility))
        {
            LogFailureOnce(cacheKey + ":metadata", $"Ignoring incompatible avatar PSO sidecar for {contentSection.AssetBundleHash}: {incompatibility}");
            return new BasisAvatarPsoPreparedData { PackagedPath = packagedPath, PackagedPresent = false };
        }

        SemaphoreSlim preparationGate = GetPreparationGate(cacheKey);
        await preparationGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(packagedPath))
                return new BasisAvatarPsoPreparedData { PackagedPath = packagedPath, PackagedPresent = true };

            BasisBundleSection encryptedSection = default;
        string location = wrapper.LoadableBundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation;
        bool networkSourced = wrapper.LoadableBundle?.BasisRemoteBundleEncrypted?.IsNetworkSourced ?? false;

        if (!networkSourced && BasisIOManagement.TryResolveLocalBeePath(location, out string localBeePath))
        {
            BeeResult<BasisBundleSection> local = await BasisIOManagement.ReadGraphicsStateSectionFromFileEx(localBeePath, wrapper.LoadableBundle.BasisBundleConnector, contentSection, cancellationToken);
            if (local.IsSuccess)
                encryptedSection = local.Value;
            else
                LogFailureOnce(cacheKey + ":local-sidecar", $"Avatar PSO sidecar could not be read from local BEE ({local.Error})");
        }
        else if (!string.IsNullOrEmpty(location))
        {
            BeeResult<BasisBundleSection> remote = await BasisIOManagement.DownloadGraphicsStateSectionEx(location, wrapper.LoadableBundle.BasisBundleConnector, contentSection, cancellationToken, maxDownloadSizeInBytes);
            if (remote.IsSuccess)
                encryptedSection = remote.Value;
            else
                LogFailureOnce(cacheKey + ":remote-sidecar", $"Avatar PSO sidecar download failed ({remote.Error})");
        }

            if (!encryptedSection.HasPayload)
                return new BasisAvatarPsoPreparedData { PackagedPath = packagedPath, PackagedPresent = false };

            var password = new BasisEncryptionWrapper.BasisPassword { VP = wrapper.LoadableBundle.UnlockPassword };
            var decrypted = await BasisEncryptionToData.DecryptSection(BasisGenerateUniqueID.GenerateUniqueID(), password, encryptedSection, new BasisProgressReport(), cancellationToken);
            if (!decrypted.Success || decrypted.Data == null || decrypted.Data.Length == 0)
            {
                LogFailureOnce(cacheKey + ":decrypt", $"Avatar PSO sidecar decrypt failed ({decrypted.Error} | {decrypted.Message})");
                return new BasisAvatarPsoPreparedData { PackagedPath = packagedPath, PackagedPresent = false };
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(packagedPath));
            string tempPath = packagedPath + ".tmp";
            await WriteAllBytesAsync(tempPath, decrypted.Data, cancellationToken);
            if (File.Exists(packagedPath))
                File.Delete(packagedPath);
            File.Move(tempPath, packagedPath);

            return new BasisAvatarPsoPreparedData { PackagedPath = packagedPath, PackagedPresent = true };
        }
        finally
        {
            preparationGate.Release();
        }
    }

    private static bool IsCompatiblePsoMetadata(BasisBundleGenerated contentSection, BasisBundleGenerated psoSection, out string reason)
    {
        if (psoSection.PsoFormatVersion != BasisBundleConnector.AvatarPsoFormatVersion)
        {
            reason = $"format {psoSection.PsoFormatVersion}, expected {BasisBundleConnector.AvatarPsoFormatVersion}";
            return false;
        }
        if (!string.Equals(psoSection.PsoGraphicsApi, GraphicsDeviceType.Vulkan.ToString(), StringComparison.Ordinal))
        {
            reason = $"graphics API '{psoSection.PsoGraphicsApi}'";
            return false;
        }
        if (!string.Equals(psoSection.PsoUnityVersion, Application.unityVersion, StringComparison.Ordinal))
        {
            reason = $"Unity '{psoSection.PsoUnityVersion}', runtime '{Application.unityVersion}'";
            return false;
        }
        if (psoSection.PsoRenderConfigVersion != BasisBundleConnector.AvatarPsoRenderConfigVersion)
        {
            reason = $"render config {psoSection.PsoRenderConfigVersion}, expected {BasisBundleConnector.AvatarPsoRenderConfigVersion}";
            return false;
        }
        if (!string.IsNullOrEmpty(psoSection.PsoForAssetBundleHash) && !string.Equals(psoSection.PsoForAssetBundleHash, contentSection.AssetBundleHash, StringComparison.OrdinalIgnoreCase))
        {
            reason = "content hash mismatch";
            return false;
        }

        reason = null;
        return true;
    }

    private static async Task<bool> WarmFileAsync(string path, string failureKey, CancellationToken cancellationToken, Action<int> countVariants)
    {
        try
        {
            GraphicsStateCollection collection = new GraphicsStateCollection();
            using (PsoFileLoadMarker.Auto())
                collection.LoadFromFile(path);

            int count = collection.variantCount;
            countVariants?.Invoke(count);
            if (count == 0 || collection.isWarmedUp)
                return count > 0;

            JobHandle handle;
            using (PsoWarmScheduleMarker.Auto())
                handle = collection.WarmUp();

            bool cancelled = false;
            while (!handle.IsCompleted)
            {
                cancelled |= cancellationToken.IsCancellationRequested;
                await Task.Yield();
            }

            // Complete only after IsCompleted so this cannot turn into an immediate main-thread block.
            handle.Complete();
            if (cancelled)
                cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailureOnce(failureKey, $"Avatar PSO warm failed for '{Path.GetFileName(path)}'; avatar will still activate ({ex.Message})");
            TryDelete(path);
            return false;
        }
    }

    private static SemaphoreSlim GetContentGate(string cacheKey)
    {
        lock (StateLock)
        {
            if (!ContentGates.TryGetValue(cacheKey, out SemaphoreSlim gate))
            {
                gate = new SemaphoreSlim(1, 1);
                ContentGates.Add(cacheKey, gate);
            }
            return gate;
        }
    }

    private static SemaphoreSlim GetPreparationGate(string cacheKey)
    {
        lock (StateLock)
        {
            if (!PreparationGates.TryGetValue(cacheKey, out SemaphoreSlim gate))
            {
                gate = new SemaphoreSlim(1, 1);
                PreparationGates.Add(cacheKey, gate);
            }
            return gate;
        }
    }

    private static string GetPackagedPath(BasisBundleGenerated generated)
    {
        return Path.Combine(GetCacheDirectory(), GetCacheKey(generated) + ".packaged.gpsc");
    }

    private static string GetRuntimePath(BasisBundleGenerated generated)
    {
        return Path.Combine(GetCacheDirectory(), GetCacheKey(generated) + ".runtime.gpsc");
    }

    private static string GetCacheDirectory()
    {
        return Path.Combine(Application.persistentDataPath, "GraphicsState", "Avatars");
    }

    public static string GetCacheKey(BasisBundleGenerated generated)
    {
        string hash = Sanitize(generated?.AssetBundleHash ?? "unknown");
        string platform = Sanitize(generated?.Platform ?? Application.platform.ToString());
        string unity = Sanitize(Application.unityVersion);
        return $"{hash}.{platform}.{GraphicsDeviceType.Vulkan}.Unity{unity}.BasisPsoV{BasisBundleConnector.AvatarPsoRenderConfigVersion}";
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "unknown";
        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '-' || chars[i] == '_'))
                chars[i] = '_';
        }
        return new string(chars);
    }

    private static async Task WriteAllBytesAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        using FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await stream.WriteAsync(data, 0, data.Length, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void LogFailureOnce(string key, string message)
    {
        lock (StateLock)
        {
            if (!LoggedFailures.Add(key))
                return;
        }
        BasisDebug.LogWarning(message, BasisDebug.LogTag.Event);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
