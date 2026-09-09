using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

public class BasisContentVersionPersistenceTests
{
    private string _tempRoot;
    private string _originalPersistentDataPath;
    private RuntimePlatform _originalCachedPlatform;
    private bool _originalInitialized;
    private Dictionary<string, BasisBEEExtensionMeta> _originalDiscData;

    [SetUp]
    public void SetUp()
    {
        _originalPersistentDataPath = BasisIOManagement.PersistentDataPath;
        _originalCachedPlatform = BasisIOManagement.CachedPlatform;
        _originalInitialized = BasisLoadHandler.IsInitialized;
        _originalDiscData = new Dictionary<string, BasisBEEExtensionMeta>(BasisLoadHandler.OnDiscData);

        _tempRoot = Path.Combine(Path.GetTempPath(), "BasisContentVersionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        SetBasisIOProperty(nameof(BasisIOManagement.PersistentDataPath), _tempRoot);
        SetBasisIOProperty(nameof(BasisIOManagement.CachedPlatform), RuntimePlatform.WindowsEditor);

        BasisLoadHandler.OnDiscData.Clear();
        BasisLoadHandler.IsInitialized = false;
    }

    [TearDown]
    public void TearDown()
    {
        BasisLoadHandler.OnDiscData.Clear();
        foreach (KeyValuePair<string, BasisBEEExtensionMeta> pair in _originalDiscData)
        {
            BasisLoadHandler.OnDiscData[pair.Key] = pair.Value;
        }
        BasisLoadHandler.IsInitialized = _originalInitialized;

        SetBasisIOProperty(nameof(BasisIOManagement.PersistentDataPath), _originalPersistentDataPath);
        SetBasisIOProperty(nameof(BasisIOManagement.CachedPlatform), _originalCachedPlatform);

        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch
        {
            // A failed cleanup should not hide the actual assertion result.
        }
    }

    [Test]
    public async Task MarkValidatedPersistsTagBeforeReturning()
    {
        const string url = "https://example.com/avatar.bee";
        BasisBEEExtensionMeta meta = CreateMeta(url, "initial", string.Empty, 0);
        Assert.IsTrue(await BasisLoadHandler.AddDiscInfo(meta));

        Assert.IsTrue(await BasisContentVersion.MarkValidatedAsync(url, "\"v1\""));

        // Simulate a restart immediately after the awaited call returns. If MarkValidatedAsync only
        // scheduled the .BME write, clearing the in-memory index here exposes the lost baseline.
        BasisLoadHandler.OnDiscData.Clear();

        (bool found, BasisBEEExtensionMeta reloaded) = await BasisLoadHandler.IsMetaDataOnDiscAsync(url);
        Assert.IsTrue(found);
        Assert.AreEqual("\"v1\"", reloaded.CachedVersionTag);
        Assert.Greater(reloaded.LastValidatedUnixUtc, 0);
    }

    [Test]
    public async Task BaselineIsNotReportedEstablishedWhenItCannotBePersisted()
    {
        const string url = "https://example.com/no-version.bee";
        string connectorPath = Path.Combine(_tempRoot, "manual.bec");
        File.WriteAllBytes(connectorPath, new byte[] { 1 });

        var meta = new BasisBEEExtensionMeta
        {
            StoredRemote = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = url },
            StoredLocal = new BasisStoredEncryptedBundle { DownloadedConnectorFileLocation = connectorPath },
            UniqueVersion = string.Empty,
            DownloadedPlatform = BasisIOManagement.GetCurrentCachePlatform(),
            CachedVersionTag = string.Empty,
            LastValidatedUnixUtc = 0,
        };
        BasisLoadHandler.OnDiscData[BasisLoadHandler.GetDiscInfoKey(url, meta.DownloadedPlatform)] = meta;

        BasisContentVersion.UpdateCheckResult result = await BasisContentVersion.EstablishBaselineAsync(url, "\"v1\"");

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.BaselineEstablished);
        Assert.AreEqual(string.Empty, meta.CachedVersionTag, "A failed persistence attempt must not mutate the recoverable in-memory baseline.");
    }

    [Test]
    public async Task AddDiscInfoSupersedesPreviousGenerationAndDeletesItsFiles()
    {
        const string url = "https://example.com/static-url.bee";
        BasisBEEExtensionMeta oldMeta = CreateMeta(url, "old-version", "\"old\"", 10);
        Assert.IsTrue(await BasisLoadHandler.AddDiscInfo(oldMeta));
        string oldMetaPath = BasisIOManagement.GetMetaCacheFilePath(oldMeta.UniqueVersion, oldMeta.DownloadedPlatform);
        string oldConnectorPath = oldMeta.StoredLocal.DownloadedConnectorFileLocation;

        BasisBEEExtensionMeta newMeta = CreateMeta(url, "new-version", "\"new\"", 20);
        Assert.IsTrue(await BasisLoadHandler.AddDiscInfo(newMeta));

        Assert.IsFalse(File.Exists(oldMetaPath));
        Assert.IsFalse(File.Exists(oldConnectorPath));
        Assert.IsTrue(File.Exists(BasisIOManagement.GetMetaCacheFilePath(newMeta.UniqueVersion, newMeta.DownloadedPlatform)));
        Assert.IsTrue(File.Exists(newMeta.StoredLocal.DownloadedConnectorFileLocation));

        Assert.IsTrue(BasisLoadHandler.IsMetaDataOnDisc(url, out BasisBEEExtensionMeta selected));
        Assert.AreEqual("new-version", selected.UniqueVersion);
        Assert.AreEqual("\"new\"", selected.CachedVersionTag);
    }

    [Test]
    public async Task StartupChoosesNewestValidatedDuplicateAndCleansSupersededGeneration()
    {
        const string url = "https://example.com/duplicate.bee";
        BasisBEEExtensionMeta oldMeta = CreateMeta(url, "old-duplicate", string.Empty, 10);
        BasisBEEExtensionMeta newMeta = CreateMeta(url, "new-duplicate", "\"new\"", 20);

        string oldMetaPath = WriteMetaFile(oldMeta);
        string newMetaPath = WriteMetaFile(newMeta);

        // Make the stale file newer on disk to prove LastValidatedUnixUtc, not filesystem or
        // Directory.GetFiles ordering, decides which duplicate wins.
        File.SetLastWriteTimeUtc(newMetaPath, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(oldMetaPath, DateTime.UtcNow.AddMinutes(2));

        BasisLoadHandler.OnDiscData.Clear();
        BasisLoadHandler.IsInitialized = false;
        await BasisLoadHandler.EnsureInitializationComplete();

        Assert.IsTrue(BasisLoadHandler.IsMetaDataOnDisc(url, out BasisBEEExtensionMeta selected));
        Assert.AreEqual("new-duplicate", selected.UniqueVersion);
        Assert.AreEqual("\"new\"", selected.CachedVersionTag);
        Assert.IsFalse(File.Exists(oldMetaPath));
        Assert.IsFalse(File.Exists(oldMeta.StoredLocal.DownloadedConnectorFileLocation));
        Assert.IsTrue(File.Exists(newMetaPath));
        Assert.IsTrue(File.Exists(newMeta.StoredLocal.DownloadedConnectorFileLocation));
    }

    [Test]
    public void InvalidateDeletesUnindexedDuplicateGenerations()
    {
        const string url = "https://example.com/orphaned.bee";
        BasisBEEExtensionMeta oldMeta = CreateMeta(url, "orphan-old", "\"old\"", 10);
        BasisBEEExtensionMeta newMeta = CreateMeta(url, "orphan-new", "\"new\"", 20);

        string oldMetaPath = WriteMetaFile(oldMeta);
        string newMetaPath = WriteMetaFile(newMeta);

        // Reproduce the old broken state: only one URL+platform record is indexed even though two
        // physical generations remain on disk.
        BasisLoadHandler.OnDiscData[BasisLoadHandler.GetDiscInfoKey(url, newMeta.DownloadedPlatform)] = newMeta;

        Assert.IsTrue(BasisStorageManagement.DeleteStoredFile(url));

        Assert.IsFalse(File.Exists(oldMetaPath));
        Assert.IsFalse(File.Exists(newMetaPath));
        Assert.IsFalse(File.Exists(oldMeta.StoredLocal.DownloadedConnectorFileLocation));
        Assert.IsFalse(File.Exists(newMeta.StoredLocal.DownloadedConnectorFileLocation));
        Assert.IsFalse(BasisLoadHandler.IsMetaDataOnDisc(url, out _));
    }

    private BasisBEEExtensionMeta CreateMeta(string url, string uniqueVersion, string cachedTag, long validatedUnixUtc)
    {
        string platform = BasisIOManagement.GetCurrentCachePlatform();
        string connectorPath = BasisIOManagement.GetConnectorCacheFilePath(uniqueVersion, platform);
        Directory.CreateDirectory(Path.GetDirectoryName(connectorPath));
        File.WriteAllBytes(connectorPath, new byte[] { 1, 2, 3 });

        return new BasisBEEExtensionMeta
        {
            StoredRemote = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = url },
            StoredLocal = new BasisStoredEncryptedBundle { DownloadedConnectorFileLocation = connectorPath },
            UniqueVersion = uniqueVersion,
            DownloadedPlatform = platform,
            CachedVersionTag = cachedTag,
            LastValidatedUnixUtc = validatedUnixUtc,
        };
    }

    private static string WriteMetaFile(BasisBEEExtensionMeta meta)
    {
        string path = BasisIOManagement.GetMetaCacheFilePath(meta.UniqueVersion, meta.DownloadedPlatform);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, BasisSerialization.SerializeValue(meta));
        return path;
    }

    private static void SetBasisIOProperty(string propertyName, object value)
    {
        PropertyInfo property = typeof(BasisIOManagement).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(property, $"Missing BasisIOManagement.{propertyName}");
        MethodInfo setter = property.GetSetMethod(true);
        Assert.NotNull(setter, $"Missing setter for BasisIOManagement.{propertyName}");
        setter.Invoke(null, new[] { value });
    }
}
