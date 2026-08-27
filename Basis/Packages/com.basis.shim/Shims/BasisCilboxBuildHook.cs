#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using Cilbox;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Basis-owned extension point for editor preparation that must happen immediately before Cilbox
/// discovers and serializes Cilboxable behaviours. Feature packages may subscribe without adding
/// feature-specific knowledge to Basis or modifying the Cilbox package.
/// </summary>
public static class BasisCilboxBuildEvents
{
    public static event Action<Scene> OnBeforeCilboxSerialize;

    internal static void InvokeBeforeCilboxSerialize(Scene scene)
    {
        OnBeforeCilboxSerialize?.Invoke(scene);
    }
}

public class BasisCilboxBuildHook
{
    [InitializeOnLoadMethod]
    private static void Initialize()
    {
        BasisAssetBundlePipeline.OnBeforeBuildPrefabFinalize -= HandleBeforeBuildPrefab;
        BasisAssetBundlePipeline.OnBeforeBuildPrefabFinalize += HandleBeforeBuildPrefab;
        BasisAvatarSDKInspector.OnBeforeTestInEditorFinalize -= HandleBeforeTestInEditor;
        BasisAvatarSDKInspector.OnBeforeTestInEditorFinalize += HandleBeforeTestInEditor;
    }

    private static void HandleBeforeTestInEditor(GameObject prefabRoot)
    {
        HandleBeforeBuildPrefab(prefabRoot, null);
    }

    private static void HandleBeforeBuildPrefab(GameObject prefabRoot, BasisAssetBundleObject settings)
    {
        if (prefabRoot == null || !HasCilboxableComponents(prefabRoot))
        {
            return;
        }

        Debug.Log("Basis build finalization: generating Cilbox assembly data on the isolated build clone.");

        Scene originalScene = prefabRoot.scene;
        Scene originalActiveScene = SceneManager.GetActiveScene();
        Transform originalParent = prefabRoot.transform.parent;
        int originalSiblingIndex = originalParent != null ? prefabRoot.transform.GetSiblingIndex() : -1;
        Dictionary<Cilbox.Cilbox, string> cilboxAssemblySnapshot = CaptureCilboxAssemblySnapshot();
        Scene temporaryScene = default;
        bool detachedFromParent = false;

        try
        {
            temporaryScene = CreateTemporaryScene();

            if (originalParent != null)
            {
                prefabRoot.transform.SetParent(null, true);
                detachedFromParent = true;
            }

            SceneManager.MoveGameObjectToScene(prefabRoot, temporaryScene);
            SceneManager.SetActiveScene(temporaryScene);

            // This is deliberately before Cilbox searches the scene. Consumers may update serialized
            // state on the isolated clone, but Basis/Cilbox remains unaware of what those consumers are.
            BasisCilboxBuildEvents.InvokeBeforeCilboxSerialize(temporaryScene);

            Cilbox.Cilbox temporarySceneCilbox = FindCilboxInScene(temporaryScene);
            if (temporarySceneCilbox == null)
            {
                Debug.LogWarning("Basis build detected Cilboxable scripts, but the isolated content has no Cilbox component. Skipping Cilbox prebuild assembly rather than binding proxies to a temporary host that cannot exist at runtime.");
                return;
            }

            CilboxScenePostprocessor.OnPostprocessScene(temporaryScene);
            EnsureTemporarySceneHasAssemblyData(temporarySceneCilbox, cilboxAssemblySnapshot);
            RebindProxiesToTemporarySceneCilbox(prefabRoot, temporarySceneCilbox);
        }
        finally
        {
            // Cilbox currently locates its assembly host globally, so processing an isolated scene can
            // temporarily touch another loaded scene's Cilbox. Always restore those values, including
            // when serialization throws.
            RestoreExternalCilboxAssemblyData(cilboxAssemblySnapshot, temporaryScene);

            if (originalScene.IsValid() && originalScene.isLoaded && prefabRoot != null &&
                prefabRoot.scene.IsValid() && prefabRoot.scene == temporaryScene)
            {
                SceneManager.MoveGameObjectToScene(prefabRoot, originalScene);
            }
            else if (prefabRoot != null && prefabRoot.scene == temporaryScene &&
                     originalActiveScene.IsValid() && originalActiveScene.isLoaded &&
                     originalActiveScene != temporaryScene)
            {
                SceneManager.MoveGameObjectToScene(prefabRoot, originalActiveScene);
            }

            if (detachedFromParent && prefabRoot != null && originalParent != null &&
                prefabRoot.scene.IsValid() && prefabRoot.scene == originalScene)
            {
                prefabRoot.transform.SetParent(originalParent, true);
                int siblingIndex = Mathf.Clamp(originalSiblingIndex, 0, Math.Max(0, originalParent.childCount - 1));
                prefabRoot.transform.SetSiblingIndex(siblingIndex);
            }

            if (temporaryScene.IsValid() && temporaryScene.isLoaded &&
                (prefabRoot == null || prefabRoot.scene != temporaryScene))
            {
                CloseTemporaryScene(temporaryScene);
            }

            if (originalActiveScene.IsValid() && originalActiveScene.isLoaded)
            {
                SceneManager.SetActiveScene(originalActiveScene);
            }
        }
    }

    private static Scene CreateTemporaryScene()
    {
        if (Application.isPlaying)
        {
            return SceneManager.CreateScene($"BasisCilboxTemp-{Guid.NewGuid():N}");
        }

        return EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
    }

    private static void CloseTemporaryScene(Scene temporaryScene)
    {
        if (Application.isPlaying)
        {
            // Do not synchronously tear down a play-mode scene. The clone has already been moved back
            // to its owner scene, so the asynchronous unload only cleans the empty staging scene.
            SceneManager.UnloadSceneAsync(temporaryScene);
        }
        else
        {
            EditorSceneManager.CloseScene(temporaryScene, true);
        }
    }

    internal static bool HasCilboxableComponents(GameObject root)
    {
        if (root == null)
        {
            return false;
        }

        MonoBehaviour[] components = root.GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < components.Length; i++)
        {
            MonoBehaviour component = components[i];
            if (component != null && CilboxUtil.HasCilboxableAttribute(component.GetType()))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<Cilbox.Cilbox, string> CaptureCilboxAssemblySnapshot()
    {
        Dictionary<Cilbox.Cilbox, string> snapshot = new Dictionary<Cilbox.Cilbox, string>();
        Cilbox.Cilbox[] allCilboxes = Resources.FindObjectsOfTypeAll<Cilbox.Cilbox>();
        for (int i = 0; i < allCilboxes.Length; i++)
        {
            Cilbox.Cilbox cilbox = allCilboxes[i];
            if (cilbox != null)
            {
                snapshot[cilbox] = cilbox.assemblyData;
            }
        }

        return snapshot;
    }

    private static Cilbox.Cilbox FindCilboxInScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return null;
        }

        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            GameObject root = roots[i];
            if (root == null)
            {
                continue;
            }

            Cilbox.Cilbox cilbox = root.GetComponentInChildren<Cilbox.Cilbox>(true);
            if (cilbox != null)
            {
                return cilbox;
            }
        }

        return null;
    }

    private static void EnsureTemporarySceneHasAssemblyData(
        Cilbox.Cilbox temporarySceneCilbox,
        Dictionary<Cilbox.Cilbox, string> snapshot)
    {
        if (temporarySceneCilbox == null || !string.IsNullOrEmpty(temporarySceneCilbox.assemblyData))
        {
            return;
        }

        // Cilbox's postprocessor currently chooses an assembly host globally. If it selected a host
        // from another loaded scene, copy the newly generated data onto the isolated content's Cilbox.
        Cilbox.Cilbox[] allCilboxes = Resources.FindObjectsOfTypeAll<Cilbox.Cilbox>();
        for (int i = 0; i < allCilboxes.Length; i++)
        {
            Cilbox.Cilbox cilbox = allCilboxes[i];
            if (cilbox == null || cilbox == temporarySceneCilbox || string.IsNullOrEmpty(cilbox.assemblyData))
            {
                continue;
            }

            if (snapshot.TryGetValue(cilbox, out string original) && original == cilbox.assemblyData)
            {
                continue;
            }

            temporarySceneCilbox.assemblyData = cilbox.assemblyData;
            temporarySceneCilbox.ForceReinit();
            EditorUtility.SetDirty(temporarySceneCilbox);
            return;
        }
    }

    private static void RebindProxiesToTemporarySceneCilbox(GameObject contentRoot, Cilbox.Cilbox temporarySceneCilbox)
    {
        if (contentRoot == null || temporarySceneCilbox == null)
        {
            return;
        }

        CilboxProxy[] proxies = contentRoot.GetComponentsInChildren<CilboxProxy>(true);
        for (int i = 0; i < proxies.Length; i++)
        {
            CilboxProxy proxy = proxies[i];
            if (proxy == null || proxy.box == temporarySceneCilbox)
            {
                continue;
            }

            proxy.box = temporarySceneCilbox;
            EditorUtility.SetDirty(proxy);
        }
    }

    private static void RestoreExternalCilboxAssemblyData(
        Dictionary<Cilbox.Cilbox, string> snapshot,
        Scene keepScene)
    {
        foreach (KeyValuePair<Cilbox.Cilbox, string> entry in snapshot)
        {
            Cilbox.Cilbox cilbox = entry.Key;
            if (cilbox == null || !cilbox.gameObject.scene.IsValid() || cilbox.gameObject.scene == keepScene)
            {
                continue;
            }

            if (cilbox.assemblyData == entry.Value)
            {
                continue;
            }

            cilbox.assemblyData = entry.Value;
            cilbox.ForceReinit();
            EditorUtility.SetDirty(cilbox);
        }
    }
}

/// <summary>
/// Runs Basis-owned Cilbox preparation before Cilbox's callbackOrder 0 scene processor. Unity passes
/// a build copy of the scene here, so feature preparation does not mutate the authored scene asset.
/// </summary>
internal sealed class BasisCilboxPreSerializeSceneProcessor : IProcessSceneWithReport
{
    public int callbackOrder => -100;

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        BasisCilboxBuildEvents.InvokeBeforeCilboxSerialize(scene);
    }
}
#endif
