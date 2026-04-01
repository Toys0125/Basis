using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public enum StripTargetKind
{
    WorldScene,
    LocalAvatar,
    SpawnedProp,
}

public struct StripSummary
{
    public int renderersVisited;
    public int renderersDisabledOrRemoved;
    public int materialsProcessed;
    public int texturesCleared;
    public int meshFiltersCleared;
    public int audioSourcesRemoved;
    public int audioFiltersRemoved;
    public int lightsDisabled;
    public int fxComponentsRemoved;
    public int reflectionProbesRemoved;
    public int lightProbeGroupsRemoved;
    public int lightProbeProxyVolumesRemoved;
    public int canvasesRemoved;
}

public static class BasisHeadlessAssetStripper
{
    private static bool cleanupScheduled;
    private static readonly string[] CommonTextureProps =
    {
        "_MainTex",
        "_BaseMap",
        "_BumpMap",
        "_EmissionMap",
        "_MetallicGlossMap",
        "_ParallaxMap",
        "_OcclusionMap",
        "_DetailMask",
        "_DetailAlbedoMap",
        "_DetailNormalMap",
    };

    public static StripSummary StripScene(Scene scene)
    {
        if (!scene.IsValid())
        {
            return default;
        }

        GameObject[] roots = scene.GetRootGameObjects();
        StripSummary summary = StripRoots(roots, StripTargetKind.WorldScene);
        ClearSkyboxData();
        ClearBakedLightingData();
        LogSummary(StripTargetKind.WorldScene, string.IsNullOrEmpty(scene.path) ? scene.name : scene.path, summary);
        return summary;
    }

    public static StripSummary StripGameObject(GameObject root, StripTargetKind targetKind)
    {
        if (root == null)
        {
            return default;
        }

        StripSummary summary = StripRoots(new[] { root }, targetKind);
        LogSummary(targetKind, root.name, summary);
        return summary;
    }

    private static StripSummary StripRoots(GameObject[] roots, StripTargetKind targetKind)
    {
        StripSummary summary = default;
        HashSet<Material> processedMaterials = new HashSet<Material>();
        HashSet<GameObject> destroyedGameObjects = new HashSet<GameObject>();

        for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
        {
            GameObject root = roots[rootIndex];
            if (root == null)
            {
                continue;
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                Renderer renderer = renderers[rendererIndex];
                if (renderer == null)
                {
                    continue;
                }

                summary.renderersVisited++;
                renderer.lightmapIndex = -1;
                renderer.realtimeLightmapIndex = -1;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                Material[] materials = renderer.sharedMaterials;
                for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    Material material = materials[materialIndex];
                    if (material == null || !processedMaterials.Add(material))
                    {
                        continue;
                    }

                    summary.materialsProcessed++;
                    summary.texturesCleared += ClearKnownTextures(material);
                }

                if (targetKind == StripTargetKind.LocalAvatar)
                {
                    renderer.enabled = false;
                    summary.renderersDisabledOrRemoved++;
                }
                else if (ShouldRemoveRenderer(renderer))
                {
                    DestroyObject(renderer);
                    summary.renderersDisabledOrRemoved++;
                }
            }

            if (targetKind != StripTargetKind.LocalAvatar)
            {
                MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
                for (int filterIndex = 0; filterIndex < meshFilters.Length; filterIndex++)
                {
                    MeshFilter meshFilter = meshFilters[filterIndex];
                    if (meshFilter == null || meshFilter.sharedMesh == null)
                    {
                        continue;
                    }

                    // Preserve the mesh asset when the same object uses it for movement/collision.
                    if (meshFilter.TryGetComponent<MeshCollider>(out MeshCollider meshCollider) && meshCollider.sharedMesh != null)
                    {
                        continue;
                    }

                    meshFilter.sharedMesh = null;
                    summary.meshFiltersCleared++;
                }
            }

            AudioSource[] audioSources = root.GetComponentsInChildren<AudioSource>(true);
            for (int audioIndex = 0; audioIndex < audioSources.Length; audioIndex++)
            {
                AudioSource audioSource = audioSources[audioIndex];
                if (audioSource == null)
                {
                    continue;
                }

                audioSource.Stop();
                audioSource.clip = null;
                DestroyObject(audioSource);
                summary.audioSourcesRemoved++;
            }

            summary.audioFiltersRemoved += DestroyComponents(root.GetComponentsInChildren<AudioReverbZone>(true));
            summary.audioFiltersRemoved += DestroyComponents(root.GetComponentsInChildren<AudioLowPassFilter>(true));
            summary.audioFiltersRemoved += DestroyComponents(root.GetComponentsInChildren<AudioHighPassFilter>(true));
            summary.audioFiltersRemoved += DestroyComponents(root.GetComponentsInChildren<AudioEchoFilter>(true));
            summary.audioFiltersRemoved += DestroyComponents(root.GetComponentsInChildren<AudioChorusFilter>(true));
            summary.audioFiltersRemoved += DestroyComponents(root.GetComponentsInChildren<AudioDistortionFilter>(true));
            summary.audioFiltersRemoved += DestroyComponents(root.GetComponentsInChildren<AudioReverbFilter>(true));
            summary.fxComponentsRemoved += DestroyComponents(root.GetComponentsInChildren<ParticleSystem>(true));
            summary.fxComponentsRemoved += DestroyComponents(root.GetComponentsInChildren<LODGroup>(true));

            Light[] lights = root.GetComponentsInChildren<Light>(true);
            for (int lightIndex = 0; lightIndex < lights.Length; lightIndex++)
            {
                Light light = lights[lightIndex];
                if (light == null)
                {
                    continue;
                }

                light.enabled = false;
                summary.lightsDisabled++;
            }

            ReflectionProbe[] reflectionProbes = root.GetComponentsInChildren<ReflectionProbe>(true);
            for (int probeIndex = 0; probeIndex < reflectionProbes.Length; probeIndex++)
            {
                ReflectionProbe reflectionProbe = reflectionProbes[probeIndex];
                if (reflectionProbe == null)
                {
                    continue;
                }

                if (destroyedGameObjects.Add(reflectionProbe.gameObject))
                {
                    DestroyObject(reflectionProbe.gameObject);
                    summary.reflectionProbesRemoved++;
                }
            }

            LightProbeGroup[] probeGroups = root.GetComponentsInChildren<LightProbeGroup>(true);
            summary.lightProbeGroupsRemoved += DestroyComponents(probeGroups);

            LightProbeProxyVolume[] proxyVolumes = root.GetComponentsInChildren<LightProbeProxyVolume>(true);
            summary.lightProbeProxyVolumesRemoved += DestroyComponents(proxyVolumes);

            Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
            for (int canvasIndex = 0; canvasIndex < canvases.Length; canvasIndex++)
            {
                Canvas canvas = canvases[canvasIndex];
                if (canvas == null)
                {
                    continue;
                }

                if (destroyedGameObjects.Add(canvas.gameObject))
                {
                    DestroyObject(canvas.gameObject);
                    summary.canvasesRemoved++;
                }
            }
        }

        return summary;
    }

    private static int ClearKnownTextures(Material material)
    {
        int clearedCount = 0;

        for (int index = 0; index < CommonTextureProps.Length; index++)
        {
            string prop = CommonTextureProps[index];
            if (!material.HasProperty(prop))
            {
                continue;
            }

            if (material.GetTexture(prop) != null)
            {
                material.SetTexture(prop, null);
                clearedCount++;
            }
        }

        return clearedCount;
    }

    private static int DestroyComponents<T>(T[] components) where T : Component
    {
        int removedCount = 0;
        for (int index = 0; index < components.Length; index++)
        {
            T component = components[index];
            if (component == null)
            {
                continue;
            }

            DestroyObject(component);
            removedCount++;
        }

        return removedCount;
    }

    private static bool ShouldRemoveRenderer(Renderer renderer)
    {
        return renderer is MeshRenderer ||
               renderer is SkinnedMeshRenderer ||
               renderer is SpriteRenderer ||
               renderer is TrailRenderer ||
               renderer is LineRenderer ||
               renderer is ParticleSystemRenderer;
    }

    private static void DestroyObject(UnityEngine.Object obj)
    {
        if (obj == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(obj);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(obj);
        }
    }

    private static void LogSummary(StripTargetKind targetKind, string targetName, StripSummary summary)
    {
        BasisDebug.Log(
            $"Headless strip complete for {targetKind} '{targetName}': " +
            $"renderers={summary.renderersVisited}, " +
            $"renderersDisabledOrRemoved={summary.renderersDisabledOrRemoved}, " +
            $"materials={summary.materialsProcessed}, " +
            $"texturesCleared={summary.texturesCleared}, " +
            $"meshFiltersCleared={summary.meshFiltersCleared}, " +
            $"audioSourcesRemoved={summary.audioSourcesRemoved}, " +
            $"audioFiltersRemoved={summary.audioFiltersRemoved}, " +
            $"lightsDisabled={summary.lightsDisabled}, " +
            $"fxComponentsRemoved={summary.fxComponentsRemoved}, " +
            $"reflectionProbesRemoved={summary.reflectionProbesRemoved}, " +
            $"lightProbeGroupsRemoved={summary.lightProbeGroupsRemoved}, " +
            $"lightProbeProxyVolumesRemoved={summary.lightProbeProxyVolumesRemoved}, " +
            $"canvasesRemoved={summary.canvasesRemoved}",
            BasisDebug.LogTag.Device);
    }

    public static void ScheduleMemoryCleanup()
    {
        if (cleanupScheduled)
        {
            return;
        }

        cleanupScheduled = true;
        _ = RunScheduledCleanupAsync();
    }

    private static async Task RunScheduledCleanupAsync()
    {
        try
        {
            // Coalesce multiple strip passes that occur back-to-back during world/avatar/prop load.
            await Task.Yield();
            await Task.Yield();

            AsyncOperation unload = Resources.UnloadUnusedAssets();
            while (!unload.isDone)
            {
                await Task.Yield();
            }

            GC.Collect();
        }
        finally
        {
            cleanupScheduled = false;
        }
    }

    private static void ClearBakedLightingData()
    {
        if (LightmapSettings.lightmaps != null && LightmapSettings.lightmaps.Length > 0)
        {
            LightmapSettings.lightmaps = Array.Empty<LightmapData>();
        }

        if (LightmapSettings.lightProbes != null)
        {
            LightmapSettings.lightProbes = null;
        }
    }

    private static void ClearSkyboxData()
    {
        Material skyboxMaterial = RenderSettings.skybox;
        if (skyboxMaterial == null)
        {
            return;
        }

        ClearKnownTextures(skyboxMaterial);
        RenderSettings.skybox = null;
    }
}
