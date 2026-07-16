using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public sealed class BasisPreparedPrefabSource : IDisposable
{
    internal GameObject PrefabRoot { get; private set; }

    internal BasisPreparedPrefabSource(GameObject prefabRoot)
    {
        PrefabRoot = prefabRoot;
    }

    public void Dispose()
    {
        if (PrefabRoot != null)
        {
            Object.DestroyImmediate(PrefabRoot);
            PrefabRoot = null;
        }
    }
}

public static class BasisAssetBundlePipeline
{
    // Define static delegates
    public delegate void BeforeBuildGameobjectHandler(GameObject prefab, BasisAssetBundleObject settings);
    public delegate void BeforeBuildTargetPrefabHandler(GameObject prefab, BasisPrefabBuildContext context);
    public delegate bool PrefabBuildTargetRequiresActiveEditorTargetHandler(BasisPrefabBuildContext context);
    public delegate void BeforeBuildSceneHandler(Scene prefab, BasisAssetBundleObject settings);
    public delegate void AfterBuildHandler(string assetBundleName);
    public delegate void BuildErrorHandler(Exception ex, GameObject prefab, bool wasModified, string temporaryStorage);

    // Static delegates
    public static event Action<GameObject, BasisAssetBundleObject> OnPreparePrefabSource;
    public static event BeforeBuildTargetPrefabHandler OnBeforeBuildTargetPrefab;
    public static event Action<GameObject, BasisPrefabBuildContext> OnAfterBuildTargetPrefab;

    // A target-aware processor that still depends on Unity's global target can opt in
    // to the compatibility switch. Legacy hooks are treated as requiring it because
    // they cannot receive an explicit target.
    public static event PrefabBuildTargetRequiresActiveEditorTargetHandler OnPrefabBuildTargetRequiresActiveEditorTarget;

    // Compatibility hook. It intentionally remains per-target.
    public static BeforeBuildGameobjectHandler OnBeforeBuildPrefab;
    public static AfterBuildHandler OnAfterBuildPrefab;
    public static BuildErrorHandler OnBuildErrorPrefab;

    public static BeforeBuildSceneHandler OnBeforeBuildScene;
    public static AfterBuildHandler OnAfterBuildScene;
    public static BuildErrorHandler OnBuildErrorScene;

    public static async Task<(bool, (BasisBundleGenerated, AssetBundleBuilder.InformationHash))>
    BuildAssetBundle(GameObject originalPrefab, BasisAssetBundleObject settings, string Password, BuildTarget Target, string buildId)
    {
        BasisPreparedPrefabSource preparedSource = PreparePrefabSource(originalPrefab, settings);
        try
        {
            return await BuildAssetBundle(preparedSource, settings, Password, Target, buildId);
        }
        finally
        {
            preparedSource.Dispose();
        }
    }

    public static BasisPreparedPrefabSource PreparePrefabSource(GameObject originalPrefab, BasisAssetBundleObject settings)
    {
        if (originalPrefab == null)
        {
            throw new ArgumentNullException(nameof(originalPrefab));
        }

        GameObject preparedBase = Object.Instantiate(originalPrefab);
        preparedBase.name = originalPrefab.name;
        try
        {
            OnPreparePrefabSource?.Invoke(preparedBase, settings);
            return new BasisPreparedPrefabSource(preparedBase);
        }
        catch
        {
            Object.DestroyImmediate(preparedBase);
            throw;
        }
    }

    public static async Task<(bool, (BasisBundleGenerated, AssetBundleBuilder.InformationHash))>
    BuildAssetBundle(BasisPreparedPrefabSource preparedSource, BasisAssetBundleObject settings, string Password, BuildTarget Target, string buildId)
    {
        if (preparedSource == null)
        {
            throw new ArgumentNullException(nameof(preparedSource));
        }
        if (preparedSource.PrefabRoot == null)
        {
            throw new ObjectDisposedException(nameof(preparedSource));
        }

        return await BuildAssetBundle(false, preparedSource.PrefabRoot, new Scene(), settings, Password, Target, buildId);
    }

    public static async Task<(bool, (BasisBundleGenerated, AssetBundleBuilder.InformationHash))>
    BuildAssetBundle(Scene scene, BasisAssetBundleObject settings, string Password, BuildTarget Target, string buildId)
    {
        return await BuildAssetBundle(true, null, scene, settings, Password, Target, buildId);
    }
    public static async Task<(bool, (BasisBundleGenerated, AssetBundleBuilder.InformationHash))>
  BuildAssetBundle(
      bool isScene,
      GameObject asset,
      Scene scene,
      BasisAssetBundleObject settings,
      string Password,
      BuildTarget Target,
      string Folder)
    {
        BuildTarget originalActiveTarget = EditorUserBuildSettings.activeBuildTarget;
        Scene originalActiveScene = SceneManager.GetActiveScene();
        bool switchedActiveTarget = false;
        string uncombinedRoot = BasisBundleBuild.PathConversion(settings.AssetBundleUnCombined);
        string targetDirectory = Path.Combine(uncombinedRoot, Folder, Target.ToString());

        TemporaryStorageHandler.ClearTemporaryStorage(targetDirectory);
        TemporaryStorageHandler.EnsureDirectoryExists(targetDirectory);

        bool wasModified = false;
        string assetPath = null;
        string uniqueID = null;
        GameObject prefab = null;
        BasisPrefabBuildContext prefabContext = null;

        try
        {
            if (isScene)
            {
                switchedActiveTarget = SwitchActiveBuildTargetIfNeeded(Target);

                if (settings.RebakeOcclusionCulling)
                {
                    if (settings.RebakeOcclusionCullingInThese.Contains(Target))
                    {
                        StaticOcclusionCulling.Compute();
                    }
                    else
                    {
                        StaticOcclusionCulling.Clear();
                    }
                }

                OnBeforeBuildScene?.Invoke(scene, settings);
                assetPath = TemporaryStorageHandler.SaveScene(scene, settings, out uniqueID);
            }
            else
            {
                prefab = Object.Instantiate(asset);
                prefab.name = asset.name;
                prefabContext = CreatePrefabBuildContext(prefab, settings, Target);

                bool requiresActiveTarget = OnBeforeBuildPrefab != null || RequiresActiveEditorTarget(prefabContext);
                if (requiresActiveTarget)
                {
                    switchedActiveTarget = SwitchActiveBuildTargetIfNeeded(Target);
                    prefabContext = CreatePrefabBuildContext(prefab, settings, Target);
                }

                DestroyEditorOnlyInAvatar(prefab);
                OnBeforeBuildTargetPrefab?.Invoke(prefab, prefabContext);
                OnBeforeBuildPrefab?.Invoke(prefab, settings);
                PostProcessAvatar(prefab);

                assetPath = TemporaryStorageHandler.SavePrefabToTemporaryStorage(prefab, settings, ref wasModified, out uniqueID);
            }

            AssetBundleBuild Build = new AssetBundleBuild()
            {
                assetBundleName = uniqueID,
                assetNames = new string[] { assetPath }
            };

            AssetBundleBuild[] Builds = new AssetBundleBuild[] { Build };

            (BasisBundleGenerated, AssetBundleBuilder.InformationHash) value =
                await AssetBundleBuilder.BuildAssetBundle(
                    Builds,
                    targetDirectory,
                    settings,
                    uniqueID,
                    isScene ? "Scene" : "GameObject",
                    Password,
                    Target);

            if (string.IsNullOrWhiteSpace(value.Item2.EncyptedPath) || !File.Exists(value.Item2.EncyptedPath))
            {
                throw new InvalidOperationException($"AssetBundle build for {Target} did not produce an encrypted bundle.");
            }

            if (isScene) OnAfterBuildScene?.Invoke(uniqueID);
            else
            {
                OnAfterBuildTargetPrefab?.Invoke(prefab, prefabContext);
                OnAfterBuildPrefab?.Invoke(uniqueID);
            }

            return new(true, value);
        }
        catch (Exception ex)
        {
            if (isScene)
            {
                OnBuildErrorScene?.Invoke(ex, null, false, settings.TemporaryStorage);
                Debug.LogError($"Error while building AssetBundle from scene: {ex.Message}\n{ex.StackTrace}");
            }
            else
            {
                OnBuildErrorPrefab?.Invoke(ex, asset, wasModified, settings.TemporaryStorage);
                BasisBundleErrorHandler.HandleBuildError(ex, asset, wasModified, settings.TemporaryStorage);
                EditorUtility.DisplayDialog(
                    BasisEditorLocalization.Get("sdk.common.build.failedDialog.title"),
                    BasisEditorLocalization.Get("sdk.common.build.failedDialog.body", ex),
                    BasisEditorLocalization.Get("sdk.common.dialog.ok"));
            }

            return new(false, (null, new AssetBundleBuilder.InformationHash()));
        }
        finally
        {
            if (prefab != null)
            {
                Object.DestroyImmediate(prefab);
            }

            if (isScene || wasModified)
            {
                TemporaryStorageHandler.ClearTemporaryStorage(settings.TemporaryStorage);
                AssetDatabase.Refresh();
            }

            if (switchedActiveTarget && EditorUserBuildSettings.activeBuildTarget != originalActiveTarget)
            {
                EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildPipeline.GetBuildTargetGroup(originalActiveTarget),
                    originalActiveTarget);
            }

            if (originalActiveScene.IsValid() && originalActiveScene.isLoaded && SceneManager.GetActiveScene() != originalActiveScene)
            {
                SceneManager.SetActiveScene(originalActiveScene);
            }
        }
    }

    private static bool SwitchActiveBuildTargetIfNeeded(BuildTarget target)
    {
        if (EditorUserBuildSettings.activeBuildTarget == target)
        {
            return false;
        }

        BuildTargetGroup targetGroup = BuildPipeline.GetBuildTargetGroup(target);
        if (!EditorUserBuildSettings.SwitchActiveBuildTarget(targetGroup, target))
        {
            throw new InvalidOperationException($"Failed to switch Unity's active build target to {target}.");
        }

        return true;
    }

    private static BasisPrefabBuildContext CreatePrefabBuildContext(
        GameObject prefab,
        BasisAssetBundleObject settings,
        BuildTarget target)
    {
        BuildTargetGroup targetGroup = BuildPipeline.GetBuildTargetGroup(target);
        return new BasisPrefabBuildContext
        {
            Target = target,
            TargetGroup = targetGroup,
            NamedTarget = NamedBuildTarget.FromBuildTargetGroup(targetGroup),
            ContentKind = prefab.GetComponent<BasisAvatar>() != null
                ? BasisBundleContentKind.Avatar
                : BasisBundleContentKind.Prop,
            Settings = settings,
            IsActiveEditorTarget = EditorUserBuildSettings.activeBuildTarget == target,
            GraphicsApis = ResolveGraphicsApis(target)
        };
    }

    private static bool RequiresActiveEditorTarget(BasisPrefabBuildContext context)
    {
        if (OnPrefabBuildTargetRequiresActiveEditorTarget == null)
        {
            return false;
        }

        Delegate[] handlers = OnPrefabBuildTargetRequiresActiveEditorTarget.GetInvocationList();
        for (int index = 0; index < handlers.Length; index++)
        {
            var handler = (PrefabBuildTargetRequiresActiveEditorTargetHandler)handlers[index];
            if (handler(context))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] ResolveGraphicsApis(BuildTarget target)
    {
        try
        {
            var graphicsApis = PlayerSettings.GetGraphicsAPIs(target);
            if (graphicsApis == null || graphicsApis.Length == 0)
            {
                return Array.Empty<string>();
            }

            return graphicsApis.Select(api => api.ToString()).ToArray();
        }
        catch (Exception ex)
        {
            BasisDebug.LogWarning($"Failed to resolve graphics APIs for {target}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public static void PostProcessAvatar(GameObject prefab)
    {
        if (prefab.TryGetComponent<BasisAvatar>(out BasisAvatar avatar))
        {
            var processing = avatar.ProcessingAvatarOptions;
            if (processing != null)
            {
                if (!processing.doNotAutoRenameBones)
                {
                    ProcessAutoRenameBones(prefab);
                }

                // We do not want to keep this data at runtime.
                avatar.ProcessingAvatarOptions = null;
            }

            if (prefab.TryGetComponent<Animator>(out Animator animator))
            {
                avatar.TransformStorage = BasisAvatarTransformStorage.CaptureFrom(animator);
            }
        }
    }

    private static void ProcessAutoRenameBones(GameObject prefab)
    {
        if (!prefab.TryGetComponent<Animator>(out Animator animator))
        {
            return;
        }
        if (animator.avatar == null)
        {
            return;
        }

        var allHumanoidBoneTransforms = AllValidBonesOf(animator).ToHashSet();
        var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hips != null && hips.parent != null)
        {
            // Animation Rigging also fails if the "Armature" object itself has a duplicated name. Not sure why exactly.
            allHumanoidBoneTransforms.Add(hips.parent);
        }
        var allHumanoidBoneNames = allHumanoidBoneTransforms
            .Select(transform => transform.name)
            .ToHashSet();

        var allNonHumanoidBonesNamedSimilarly = prefab.GetComponentsInChildren<Transform>()
            .Where(transform => !allHumanoidBoneTransforms.Contains(transform))
            .Where(transform => allHumanoidBoneNames.Contains(transform.name))
            .ToList();

        if (allNonHumanoidBonesNamedSimilarly.Count == 0) return;

        var duplicateMessage = string.Join(", ", allNonHumanoidBonesNamedSimilarly.Select(transform => transform.name).Distinct().OrderBy(t => t));
        BasisDebug.Log($"This avatar has duplicate humanoid bone names ({duplicateMessage}); they will be auto-renamed in order to avoid an issue caused by AnimationRigging.");

        foreach (var grouping in allNonHumanoidBonesNamedSimilarly.GroupBy(transform => transform.name))
        {
            var originalName = grouping.Key;
            var elements = grouping.ToList();
            for (var index = 0; index < elements.Count; index++)
            {
                var element = elements[index];

                var number = index + 1;
                element.name = $"{originalName}_{number}";
            }
        }
    }

    private static List<Transform> AllValidBonesOf(Animator animator)
    {
        var results = new List<Transform>();
        if (animator.avatar == null) return results;

        for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
        {
            var t = animator.GetBoneTransform(bone);
            if (t != null)
            {
                results.Add(t);
            }
        }

        return results;
    }

    public static void DestroyEditorOnlyInAvatar(GameObject avatar)
    {
        // We need to do this instead of iterating on avatar.transform so that we can destroy
        // the objects that we're currently iterating through.
        var transforms = Enumerable.Range(0, avatar.transform.childCount)
            .Select(i => avatar.transform.GetChild(i))
            .ToList();
        foreach (Transform t in transforms)
        {
            DestroyIfEditorOnlyRecursive(t.gameObject);
        }
    }

    private static void DestroyIfEditorOnlyRecursive(GameObject subject)
    {
        if (subject.CompareTag("EditorOnly"))
        {
            Object.DestroyImmediate(subject);
        }
        else
        {
            foreach (Transform child in subject.transform)
            {
                DestroyIfEditorOnlyRecursive(child.gameObject);
            }
        }
    }
}
