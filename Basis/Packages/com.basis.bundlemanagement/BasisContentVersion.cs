using System;
using System.Collections.Concurrent;

/// <summary>
/// Content-version policy for cached bees: decides whether an on-disc cache entry may still be
/// used for a requested version, and normalizes the opaque tags that decision runs on.
///
/// <para>Cache identity is the remote url, so re-uploading a bee to the SAME url used to be
/// invisible to every client that already had it cached — the load path never touched the network
/// again. A version tag rides alongside the url so "same address, new bytes" becomes expressible.</para>
///
/// <para>An empty requested tag means "no opinion", and the cache is then authoritative. Every
/// bundle published before versioning, every client built before it, and the GUID-per-upload flow
/// all produce empty tags, so they keep the exact pre-existing behavior.</para>
/// </summary>
public static class BasisContentVersion
{
    /// <summary>
    /// Minimum spacing between version-triggered re-downloads of the same url.
    ///
    /// <para>A tag that arrives over the network is attacker-controlled: a peer that broadcasts a
    /// different tag every few seconds would otherwise make every client in the instance re-download
    /// that avatar on command — bandwidth amplification aimed at the publisher's own host. The
    /// throttle bounds a hostile peer to one refresh per url per window while leaving a genuine
    /// update (which settles after a single refresh, because the observed validator then matches)
    /// completely unaffected.</para>
    ///
    /// <para>User-driven refreshes do not pass through here — they evict the entry outright.</para>
    /// </summary>
    public const double VersionRefreshThrottleSeconds = 60.0;

    // Stopwatch timestamps rather than wall clock so a system clock change cannot unblock or
    // permanently block the throttle, and rather than Environment.TickCount (a 32-bit millisecond
    // counter that wraps every ~49 days) so no wrap handling is needed. Keyed by canonical url.
    private static readonly ConcurrentDictionary<string, long> _lastVersionRefreshTicks = new ConcurrentDictionary<string, long>();

    /// <summary>
    /// Marks a stored tag as having come from <c>Last-Modified</c> rather than an <c>ETag</c>, so it
    /// can be turned back into the right conditional request header. Tags are stored VERBATIM
    /// otherwise (quotes and weak markers intact) because <c>If-None-Match</c> must echo the
    /// server's exact spelling; normalization happens only when comparing.
    /// </summary>
    public const string LastModifiedPrefix = "LM:";

    /// <summary>
    /// Splits a stored tag back into the conditional request headers it came from. A tag that is
    /// neither (a creator-stamped nonce from a host with no validators) is offered as
    /// <c>If-None-Match</c>, which such a host simply ignores — costing nothing and leaving the
    /// caller to compare tags itself.
    /// </summary>
    public static void ToConditionalHeaders(string cachedTag, out string ifNoneMatch, out string ifModifiedSince)
    {
        ifNoneMatch = null;
        ifModifiedSince = null;

        if (string.IsNullOrWhiteSpace(cachedTag))
        {
            return;
        }

        string trimmed = cachedTag.Trim();
        if (trimmed.StartsWith(LastModifiedPrefix, StringComparison.Ordinal))
        {
            ifModifiedSince = trimmed.Substring(LastModifiedPrefix.Length);
            return;
        }

        ifNoneMatch = trimmed;
    }

    /// <summary>
    /// Canonical form of a version tag for comparison. HTTP validators arrive spelled several ways
    /// for the same entity — <c>W/"abc"</c> (weak), <c>"abc"</c> (quoted strong), <c>abc</c> — and
    /// comparing those raw would read a re-served identical file as a change and re-download it.
    /// </summary>
    public static string Normalize(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        string trimmed = tag.Trim();

        // Weak validator prefix: W/"..." — weak and strong forms of the same ETag denote the same
        // content for our purposes (we only ever ask "did this change?").
        if (trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(2).Trim();
        }

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
        {
            trimmed = trimmed.Substring(1, trimmed.Length - 2);
        }

        return trimmed;
    }

    /// <summary>True when two tags denote the same content. Empty never matches anything.</summary>
    public static bool TagsMatch(string left, string right)
    {
        string a = Normalize(left);
        string b = Normalize(right);
        return a.Length != 0 && b.Length != 0 && string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>
    /// After a conditional request prompted by a version-claim mismatch: whether the host confirmed
    /// the CACHED copy is still exactly what it serves, making the claim the stale side. True on an
    /// explicit 304, or on a reported validator that matches the cached tag. False when the host
    /// reports a different validator (genuinely new content) or publishes none at all — a host with
    /// no validators is one where the tag scheme is a creator-stamped nonce, and a mismatched nonce
    /// means "changed" by construction.
    /// </summary>
    public static bool HostConfirmsCache(string cachedTag, BasisIOManagement.BasisRemoteValidator validator)
    {
        if (validator.NotModified)
        {
            return true;
        }
        return validator.HasValue && TagsMatch(cachedTag, validator.Tag);
    }

    /// <summary>
    /// Whether the cached entry is acceptable for the requested version, ignoring throttling.
    /// </summary>
    public static bool CacheSatisfies(BasisBEEExtensionMeta meta, string requestedTag)
    {
        string requested = Normalize(requestedTag);

        // No version declared: the caller has no opinion about freshness, so the cache wins. This
        // is the branch every pre-existing load takes, and it is byte-for-byte the old behavior.
        if (requested.Length == 0)
        {
            return true;
        }

        string cached = Normalize(meta?.CachedVersionTag);

        // Entry predates versioning (or came from a connector-only fetch that never saw a
        // validator). Trusting it would pin a stale bee forever the first time anyone asks for a
        // specific version, so treat unknown as "does not satisfy" and let one download settle it.
        // Self-healing: the refresh writes an observed tag, and later requests match cheaply.
        if (cached.Length == 0)
        {
            return false;
        }

        return string.Equals(cached, requested, StringComparison.Ordinal);
    }

    /// <summary>
    /// The load path's decision: may this cached entry serve the requested version?
    /// Applies <see cref="CacheSatisfies"/> and then the anti-amplification throttle.
    /// </summary>
    public static bool ShouldUseCache(BasisBEEExtensionMeta meta, string requestedTag, string remoteUrl)
    {
        if (CacheSatisfies(meta, requestedTag))
        {
            return true;
        }

        if (!TryBeginVersionRefresh(remoteUrl))
        {
            BasisDebug.LogWarning($"Version mismatch for {remoteUrl} but a refresh already ran within {VersionRefreshThrottleSeconds}s; serving the cached copy. Repeated mismatches from a peer are ignored by design.", BasisDebug.LogTag.Event);
            return true;
        }

        BasisDebug.Log($"Version claim for {remoteUrl} does not match the cache (cached '{Normalize(meta?.CachedVersionTag)}', requested '{Normalize(requestedTag)}').", BasisDebug.LogTag.Event);
        return false;
    }

    /// <summary>
    /// Claims the right to run a version-triggered refresh for a url, or reports that one ran too
    /// recently. Claiming is the side effect — callers that get true are expected to refresh.
    /// </summary>
    public static bool TryBeginVersionRefresh(string remoteUrl)
    {
        string key = BasisIOManagement.CanonicalizeRemoteUrl(remoteUrl);
        if (key.Length == 0)
        {
            return true;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long windowTicks = (long)(VersionRefreshThrottleSeconds * System.Diagnostics.Stopwatch.Frequency);

        while (true)
        {
            if (!_lastVersionRefreshTicks.TryGetValue(key, out long previous))
            {
                if (_lastVersionRefreshTicks.TryAdd(key, now))
                {
                    return true;
                }
                continue;
            }

            if (now - previous < windowTicks)
            {
                return false;
            }

            if (_lastVersionRefreshTicks.TryUpdate(key, now, previous))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Forgets the throttle for a url so the next mismatch refreshes immediately. Called after a
    /// user-driven update so an explicit action is never silently swallowed by the throttle.
    /// </summary>
    public static void ClearRefreshThrottle(string remoteUrl)
    {
        string key = BasisIOManagement.CanonicalizeRemoteUrl(remoteUrl);
        if (key.Length != 0)
        {
            _lastVersionRefreshTicks.TryRemove(key, out _);
        }
    }

    /// <summary>Unix seconds UTC, the storage form of <see cref="BasisBEEExtensionMeta.LastValidatedUnixUtc"/>.</summary>
    public static long NowUnixUtc()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>
    /// The version tag currently cached for a url, or empty when nothing is cached / the entry
    /// predates versioning. Used by the update check to build a conditional request.
    /// </summary>
    public static async System.Threading.Tasks.Task<string> GetCachedTagAsync(string remoteUrl)
    {
        (bool found, BasisBEEExtensionMeta meta) = await BasisLoadHandler.IsMetaDataOnDiscAsync(remoteUrl);
        if (!found || string.IsNullOrWhiteSpace(meta?.CachedVersionTag))
        {
            return string.Empty;
        }
        // Verbatim, not normalized: this feeds ToConditionalHeaders.
        return meta.CachedVersionTag.Trim();
    }

    /// <summary>
    /// Records that a url was checked against the remote and found current, without re-downloading.
    /// Stamps the observed validator so a cache entry written before versioning stops reporting
    /// "unknown" after the first successful check.
    /// </summary>
    public static System.Threading.Tasks.Task<bool> MarkValidatedAsync(string remoteUrl, string observedTag)
    {
        return MarkValidatedAsync(remoteUrl, observedTag, expectedUniqueVersion: null);
    }

    internal static async System.Threading.Tasks.Task<bool> MarkValidatedAsync(
        string remoteUrl,
        string observedTag,
        string expectedUniqueVersion)
    {
        (bool found, BasisBEEExtensionMeta meta) = await BasisLoadHandler.IsMetaDataOnDiscAsync(remoteUrl);
        if (!found || meta == null)
        {
            return false;
        }

        if (expectedUniqueVersion != null &&
            !string.Equals(meta.UniqueVersion, expectedUniqueVersion, StringComparison.Ordinal))
        {
            return false;
        }

        // Build a replacement record rather than mutating the live dictionary entry before the
        // disk write succeeds. If persistence fails, the in-memory cache must keep advertising the
        // last version that is actually recoverable after a restart.
        string cachedVersionTag = meta.CachedVersionTag;
        if (!string.IsNullOrWhiteSpace(observedTag) && !TagsMatch(cachedVersionTag, observedTag))
        {
            cachedVersionTag = observedTag.Trim();
        }

        var validated = new BasisBEEExtensionMeta
        {
            StoredRemote = meta.StoredRemote,
            StoredLocal = meta.StoredLocal,
            UniqueVersion = meta.UniqueVersion,
            DownloadedPlatform = meta.DownloadedPlatform,
            CachedVersionTag = cachedVersionTag,
            LastValidatedUnixUtc = NowUnixUtc(),
        };

        // The caller promises the user this baseline has been recorded, so do not return until the
        // .BME write is complete. A failed write must propagate as false; otherwise every later
        // check starts from an empty tag and repeats the "Now Tracking Updates" flow forever.
        if (string.IsNullOrWhiteSpace(validated.UniqueVersion))
        {
            BasisDebug.LogWarning($"Could not persist content version for {remoteUrl}; the cache entry has no UniqueVersion to file it under.", BasisDebug.LogTag.Event);
            return false;
        }

        // Commit only if the same cache generation is still current. A validator check can overlap
        // another refresh; without this guard an older result could relabel a newer generation.
        return await BasisLoadHandler.AddDiscInfo(validated, meta.UniqueVersion);
    }

    /// <summary>Outcome of asking a host whether cached content is still current.</summary>
    public readonly struct UpdateCheckResult
    {
        /// <summary>The check completed. False means the host could not be reached at all.</summary>
        public readonly bool Succeeded;
        /// <summary>The remote content differs from what is cached (or there is no baseline yet).</summary>
        public readonly bool HasUpdate;
        /// <summary>
        /// The host publishes neither ETag nor Last-Modified, so change cannot be detected. Callers
        /// must treat this as "cannot tell" — never as "unchanged" — and fall back to asking the
        /// user whether to refresh outright.
        /// </summary>
        public readonly bool VersioningUnavailable;
        /// <summary>
        /// This check had no recorded validator to compare against. The host's current validator was
        /// observed but was deliberately NOT attached to the existing cached bytes, because those
        /// bytes may have been downloaded before the host was updated. The UI should offer a refresh;
        /// only a successful fetch of the current BEE may establish the first trustworthy baseline.
        /// </summary>
        public readonly bool BaselineMissing;
        /// <summary>
        /// Legacy compatibility member. Update checks no longer establish a baseline merely by
        /// observing the host, because that can associate a new validator with old cached bytes.
        /// </summary>
        public readonly bool BaselineEstablished;
        public readonly string ObservedTag;
        public readonly string Error;

        public UpdateCheckResult(
            bool succeeded,
            bool hasUpdate,
            bool versioningUnavailable,
            string observedTag,
            string error,
            bool baselineEstablished = false,
            bool baselineMissing = false)
        {
            Succeeded = succeeded;
            HasUpdate = hasUpdate;
            VersioningUnavailable = versioningUnavailable;
            BaselineMissing = baselineMissing;
            BaselineEstablished = baselineEstablished;
            ObservedTag = observedTag ?? string.Empty;
            Error = error;
        }
    }

    /// <summary>
    /// Asks the host whether the bee at a url still matches the cached copy, using a conditional
    /// request when there is a baseline to condition on. Costs one small request and never
    /// downloads the payload.
    ///
    /// <para>On success with no update, the cache entry's validation timestamp is refreshed so the
    /// next check can be skipped or reported as recent.</para>
    /// </summary>
    public static async System.Threading.Tasks.Task<UpdateCheckResult> CheckForUpdateAsync(string remoteUrl, System.Threading.CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return new UpdateCheckResult(false, false, false, null, "No url was supplied.");
        }

        // A bee read straight off this device has no host to ask and no cache entry to invalidate.
        if (BasisIOManagement.IsLocalBeeUrl(remoteUrl))
        {
            return new UpdateCheckResult(false, false, true, null, "Local content is read from disk and is never cached, so it is always current.");
        }

        string cachedTag = await GetCachedTagAsync(remoteUrl);

        BeeResult<BasisIOManagement.BasisRemoteValidator> result = await BasisIOManagement.FetchRemoteValidatorAsync(remoteUrl, cachedTag, cancellationToken);
        if (!result.IsSuccess)
        {
            return new UpdateCheckResult(false, false, false, null, result.Error);
        }

        return await EvaluateValidatorAsync(remoteUrl, cachedTag, result.Value);
    }

    /// <summary>
    /// Applies a host validator to the locally recorded cache state. Kept separate from the HTTP
    /// request so the important cache-identity rules can be regression tested without a web server.
    /// </summary>
    internal static async System.Threading.Tasks.Task<UpdateCheckResult> EvaluateValidatorAsync(
        string remoteUrl,
        string cachedTag,
        BasisIOManagement.BasisRemoteValidator validator)
    {
        cachedTag ??= string.Empty;

        // The host itself confirmed our copy is current — the strongest and cheapest answer.
        if (validator.NotModified)
        {
            await MarkValidatedAsync(remoteUrl, cachedTag);
            return new UpdateCheckResult(true, false, false, cachedTag, null);
        }

        if (!validator.HasValue)
        {
            return new UpdateCheckResult(true, false, true, null, null);
        }

        string observed = validator.Tag;

        // No baseline: the entry predates versioning, or was cached by a fetch that never saw a
        // validator. The host's validator describes what is published NOW; it does not prove that
        // the bytes already on disk are that same revision. Recording it here would let an old BEE
        // masquerade as current after the user declines the offered refresh. Keep the baseline empty
        // until a successful download writes metadata for the bytes it actually fetched.
        if (cachedTag.Length == 0)
        {
            return new UpdateCheckResult(true, false, false, observed, null, baselineMissing: true);
        }

        if (TagsMatch(cachedTag, observed))
        {
            await MarkValidatedAsync(remoteUrl, observed);
            return new UpdateCheckResult(true, false, false, observed, null);
        }

        return new UpdateCheckResult(true, true, false, observed, null);
    }

    /// <summary>
    /// Finishes establishing a validator baseline after the replacement connector/BEE has actually
    /// been fetched. Range responses do not always repeat ETag/Last-Modified even when the host's
    /// HEAD response does, so a successful refresh can otherwise leave CachedVersionTag empty and
    /// make every later update check look like the first one again.
    ///
    /// <para>If the download itself recorded a validator, nothing else is needed. Otherwise we
    /// re-check the host using the validator observed immediately before the refresh. Only 304 or
    /// the same validator proves the host stayed on that revision across the download window; if it
    /// changed, leave the baseline empty rather than label the fetched bytes with the wrong tag.</para>
    /// </summary>
    internal static async System.Threading.Tasks.Task<bool> FinalizeRefreshBaselineAsync(
        string remoteUrl,
        string expectedObservedTag,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || string.IsNullOrWhiteSpace(expectedObservedTag))
        {
            return false;
        }

        (bool found, BasisBEEExtensionMeta refreshedMeta) = await BasisLoadHandler.IsMetaDataOnDiscAsync(remoteUrl);
        if (!found || refreshedMeta == null || string.IsNullOrWhiteSpace(refreshedMeta.UniqueVersion))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(refreshedMeta.CachedVersionTag))
        {
            // Prefer the validator captured by the actual download, even if the host changed between
            // the original check and the fetch. It describes the bytes that are now on disk.
            return true;
        }

        string refreshedUniqueVersion = refreshedMeta.UniqueVersion;
        BeeResult<BasisIOManagement.BasisRemoteValidator> result =
            await BasisIOManagement.FetchRemoteValidatorAsync(remoteUrl, expectedObservedTag, cancellationToken);
        if (!result.IsSuccess)
        {
            return false;
        }

        return await FinalizeRefreshBaselineFromValidatorAsync(
            remoteUrl,
            expectedObservedTag,
            refreshedUniqueVersion,
            result.Value);
    }

    /// <summary>Testable half of <see cref="FinalizeRefreshBaselineAsync"/> after the post-fetch host check.</summary>
    internal static async System.Threading.Tasks.Task<bool> FinalizeRefreshBaselineFromValidatorAsync(
        string remoteUrl,
        string expectedObservedTag,
        string expectedUniqueVersion,
        BasisIOManagement.BasisRemoteValidator validator)
    {
        if (string.IsNullOrWhiteSpace(expectedObservedTag))
        {
            return false;
        }

        string verifiedTag;
        if (validator.NotModified)
        {
            verifiedTag = expectedObservedTag.Trim();
        }
        else if (validator.HasValue && TagsMatch(expectedObservedTag, validator.Tag))
        {
            verifiedTag = validator.Tag;
        }
        else
        {
            return false;
        }

        return await MarkValidatedAsync(remoteUrl, verifiedTag, expectedUniqueVersion);
    }

    /// <summary>
    /// Drops every cached trace of a url — on-disc payload, meta index entry and any unused
    /// in-memory bundle — so the next load fetches fresh. Returns false when nothing was cached.
    ///
    /// <para>An in-memory bundle that is still in use is deliberately left alone (see
    /// <see cref="BasisLoadHandler.UnloadAllForUrl"/>): tearing assets out from under a worn
    /// avatar is worse than showing the previous version until it is next loaded.</para>
    /// </summary>
    public static bool Invalidate(string remoteUrl)
    {
        ClearRefreshThrottle(remoteUrl);
        return BasisStorageManagement.DeleteStoredFile(remoteUrl);
    }
}
