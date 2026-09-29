using NUnit.Framework;
using UnityEngine;

public class BasisAvatarPsoMetadataTests
{
    [Test]
    public void OldBundleWithoutPsoMetadata_HasNoSidecar()
    {
        BasisBundleGenerated content = new BasisBundleGenerated
        {
            AssetBundleHash = "old-hash",
            AssetMode = BasisBundleConnector.GameObjectAssetMode,
            Platform = BasisBundleConnector.BuildTarget.StandaloneWindows64.ToString(),
            EndByte = 123,
        };
        BasisBundleConnector connector = new BasisBundleConnector
        {
            BasisBundleGenerated = new[] { content },
        };

        Assert.IsFalse(BasisBundleConnector.TryGetGraphicsStateCollection(connector, content, out BasisBundleGenerated pso));
        Assert.IsNull(pso);
    }

    [Test]
    public void BundleWithPsoMetadata_ResolvesMatchingSidecar()
    {
        const string key = "hash.Vulkan.Unity6000.BasisPsoV1";
        BasisBundleGenerated content = new BasisBundleGenerated
        {
            AssetBundleHash = "hash",
            AssetMode = BasisBundleConnector.GameObjectAssetMode,
            Platform = BasisBundleConnector.BuildTarget.StandaloneWindows64.ToString(),
            EndByte = 123,
            PsoSectionKey = key,
        };
        BasisBundleGenerated pso = new BasisBundleGenerated
        {
            AssetBundleHash = "pso-hash",
            AssetMode = BasisBundleConnector.GraphicsStateCollectionAssetMode,
            Platform = "StandaloneWindows64.VulkanPSO",
            EndByte = 50,
            PsoSectionKey = key,
            PsoFormatVersion = BasisBundleConnector.AvatarPsoFormatVersion,
            PsoGraphicsApi = UnityEngine.Rendering.GraphicsDeviceType.Vulkan.ToString(),
            PsoRenderConfigVersion = BasisBundleConnector.AvatarPsoRenderConfigVersion,
            PsoForAssetBundleHash = content.AssetBundleHash,
        };
        BasisBundleConnector connector = new BasisBundleConnector
        {
            BasisBundleGenerated = new[] { content, pso },
        };

        Assert.IsTrue(BasisBundleConnector.TryGetGraphicsStateCollection(connector, content, out BasisBundleGenerated resolved));
        Assert.AreSame(pso, resolved);
    }

    [Test]
    public void PsoSection_IsNeverSelectedAsPlatformContent()
    {
        BasisBundleGenerated pso = new BasisBundleGenerated
        {
            AssetMode = BasisBundleConnector.GraphicsStateCollectionAssetMode,
            // Deliberately use an otherwise valid platform value to prove AssetMode is also guarded.
            Platform = BasisBundleConnector.BuildTarget.StandaloneWindows64.ToString(),
            EndByte = 50,
        };

        Assert.IsFalse(BasisBundleConnector.IsPlatform(pso));
    }

    [Test]
    public void ContentControl_SanitizeDoesNotActivateClone()
    {
        GameObject host = new GameObject("disabled-host");
        GameObject source = new GameObject("source");
        host.SetActive(false);
        try
        {
            ChecksRequired checks = new ChecksRequired
            {
                UseContentRemoval = false,
                ScrubPersistentUnityEvents = true,
            };

            ContentPoliceControl.ContentControlState state = ContentPoliceControl.BeginContentControl(
                host,
                source,
                checks,
                Vector3.zero,
                Quaternion.identity,
                false,
                Vector3.one,
                BundledContentHolder.Selector.Avatar);

            Assert.NotNull(state.Clone);
            Assert.IsFalse(state.Clone.activeSelf);
            Assert.IsFalse(state.Clone.activeInHierarchy);

            GameObject sanitized = ContentPoliceControl.SanitizeContentControl(state);
            Assert.AreSame(state.Clone, sanitized);
            Assert.IsFalse(sanitized.activeSelf);
            Assert.IsFalse(sanitized.activeInHierarchy);

            GameObject activated = ContentPoliceControl.ActivateContentControl(state);
            Assert.AreSame(state.Clone, activated);
            Assert.IsTrue(activated.activeSelf);
            Assert.IsTrue(activated.activeInHierarchy);
        }
        finally
        {
            Object.DestroyImmediate(source);
            Object.DestroyImmediate(host);
        }
    }
}
