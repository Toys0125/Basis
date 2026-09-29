using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Profiling;
using static BundledContentHolder;
public static class BasisBundleLoadAsset
{
    private static readonly ProfilerMarker PrefabLoadMarker = new ProfilerMarker("Avatar.PrefabLoad");
    private static readonly ProfilerMarker InstantiateMarker = new ProfilerMarker("Avatar.Instantiate");
    private static readonly ProfilerMarker ContentPoliceMarker = new ProfilerMarker("Avatar.ContentPolice");
    private static readonly ProfilerMarker ActivateMarker = new ProfilerMarker("Avatar.Activate");
    public static async Task<GameObject> LoadFromWrapper(GameObject DisabledGameobject,BasisTrackedBundleWrapper BasisLoadableBundle, bool UseContentRemoval, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, Selector Selector, Transform Parent = null, bool DestroyColliders = false,bool ChangeColidersToCorrectLayer = false, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null, CancellationToken cancellationToken = default)
    {
        Stopwatch totalLoad = Stopwatch.StartNew();
        if (BasisLoadableBundle.AssetBundle != null || BasisLoadableBundle.HasGltfTemplate)
        {
            BasisLoadableBundle output = BasisLoadableBundle.LoadableBundle;
            if (output.BasisBundleConnector.GetPlatform(out BasisBundleGenerated Generated))
            {
                switch (Generated.AssetMode)
                {
                    case BasisBundleConnector.GameObjectAssetMode:
                        {
                            string ReplacedName = Generated.AssetToLoadName.Replace(".bundle", ".prefab");

                            cancellationToken.ThrowIfCancellationRequested();
                            AssetBundleRequest Request;
                            using (PrefabLoadMarker.Auto())
                                Request = BasisLoadableBundle.AssetBundle.LoadAssetAsync<GameObject>(ReplacedName);
                            await Request;
                            cancellationToken.ThrowIfCancellationRequested();
                            GameObject loadedObject = Request.asset as GameObject;
                            if (loadedObject == null)
                            {
                                BasisDebug.LogError("Unable to proceed, null Gameobject for request " + Generated.AssetToLoadName);

                                string[] assetNames = BasisLoadableBundle.AssetBundle.GetAllAssetNames();
                                BasisDebug.LogError("All assets in bundle: \n" + string.Join("\n", assetNames));

                                BasisLoadableBundle.DidErrorOccur = true;
                                await BasisLoadableBundle.AssetBundle.UnloadAsync(true);
                                return null;
                            }
                            GameObject result = await InstantiateContentControlled(DisabledGameobject, BasisLoadableBundle, Generated, loadedObject, UseContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop, cancellationToken);
                            LogTotalLoad(totalLoad, Generated, result != null);
                            return result;
                        }
                    case BasisBundleConnector.GltfAssetMode:
                        {
                            // Generic (glTF) fallback: the wrapper holds an inactive template
                            // instead of an AssetBundle; clone it like a prefab.
                            GameObject template = BasisLoadableBundle.GltfTemplateAvatarRoot;
                            if (template == null)
                            {
                                BasisDebug.LogError("Generic (glTF) template missing on wrapper for " + Generated.AssetToLoadName);
                                BasisLoadableBundle.DidErrorOccur = true;
                                return null;
                            }
                            GameObject result = await InstantiateContentControlled(DisabledGameobject, BasisLoadableBundle, Generated, template, UseContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop, cancellationToken);
                            LogTotalLoad(totalLoad, Generated, result != null);
                            return result;
                        }
                    default:
                        BasisDebug.LogError("Requested type " + Generated.AssetMode + " has no handler");
                        return null;
                }
            }
            else
            {
                BasisDebug.LogError("Missing Platform Bundle! can't find : " + Application.platform);
            }
        }
        else
        {
            BasisDebug.LogError("Missing Bundle!");
        }
        BasisDebug.LogError("Returning unable to load gameobject!");
        return null;
    }

    private static async Task<GameObject> InstantiateContentControlled(GameObject DisabledGameobject, BasisTrackedBundleWrapper BasisLoadableBundle, BasisBundleGenerated Generated, GameObject loadedObject, bool UseContentRemoval, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, Selector Selector, Transform Parent, bool DestroyColliders, bool ChangeColidersToCorrectLayer, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop, CancellationToken cancellationToken)
    {
        ChecksRequired ChecksRequired = new ChecksRequired();
        if (loadedObject.TryGetComponent<BasisAvatar>(out BasisAvatar BasisAvatar))
        {
            ChecksRequired.DisableAnimatorEvents = true;
        }
        ChecksRequired.UseContentRemoval = UseContentRemoval;
        ChecksRequired.RemoveColliders = DestroyColliders;
        ChecksRequired.ChangeCollidersToCorrectLayer = ChangeColidersToCorrectLayer;
        ChecksRequired.ScrubPersistentUnityEvents = true;
        BasisContentHarvest harvest = BasisAvatar != null ? new BasisContentHarvest() : null;
        // Instantiate (phase one) and the component strip/scrub walk (phase two) are
        // each a multi-ms main-thread cost; running both in one frame is the load hitch.
        // BeginContentControl parks the clone inactive after Instantiate; yield a frame
        // before FinishContentControl runs the walk + activate so the two never share a
        // frame. The budget gate still spreads concurrent loads across frames on top of that.
        await BasisLoadFrameBudget.WaitForBudgetAsync();
        double instantiateStart = BasisLoadFrameBudget.BeginStep();
        ContentPoliceControl.ContentControlState scrubState;
        using (InstantiateMarker.Auto())
            scrubState = ContentPoliceControl.BeginContentControl(DisabledGameobject, loadedObject, ChecksRequired, Position, Rotation, ModifyScale, Scale, Selector, Parent, LayerMask.NameToLayer("IgnoredByInteractable"), HarvestedHeadChop, harvest);
        BasisLoadFrameBudget.EndStep(instantiateStart);

        bool activated = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            GameObject CreatedCopy;
            if (scrubState.RemovalWalkPending)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                await BasisLoadFrameBudget.WaitForBudgetAsync();
                double walkStart = BasisLoadFrameBudget.BeginStep();
                using (ContentPoliceMarker.Auto())
                    CreatedCopy = ContentPoliceControl.SanitizeContentControl(scrubState);
                BasisLoadFrameBudget.EndStep(walkStart);
            }
            else
            {
                using (ContentPoliceMarker.Auto())
                    CreatedCopy = ContentPoliceControl.SanitizeContentControl(scrubState);
            }

            if (CreatedCopy == null)
            {
                BasisDebug.LogError("ContentControl returned null; clone was destroyed during the frame-split load.");
                return null;
            }

            // Material correction/blocklist replacement is complete at this point. Vulkan PSO
            // preparation is therefore a true per-avatar readiness barrier for the final states.
            await BasisAvatarPsoLoader.WarmAsync(BasisLoadableBundle, Generated, CreatedCopy, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            using (ActivateMarker.Auto())
                CreatedCopy = ContentPoliceControl.ActivateContentControl(scrubState);
            activated = CreatedCopy != null;
            if (!activated)
                return null;

            if (harvest != null && CreatedCopy.TryGetComponent(out Basis.Scripts.BasisSdk.BasisAvatar createdAvatar))
                createdAvatar.Harvest = harvest;

            string InstanceID = BasisGenerateUniqueID.GenerateUniqueID();
            CreatedCopy.name = InstanceID;

            // Fire-and-forget by design: this improves the next load and must never delay the
            // avatar that already passed the readiness barrier.
            _ = BasisAvatarPsoLoader.TraceRuntimeAsync(BasisLoadableBundle, Generated, CreatedCopy, cancellationToken);
            return CreatedCopy;
        }
        catch
        {
            if (!activated && scrubState.Clone != null)
                GameObject.Destroy(scrubState.Clone);
            throw;
        }
    }

    private static void LogTotalLoad(Stopwatch totalLoad, BasisBundleGenerated generated, bool success)
    {
        totalLoad.Stop();
        if (BasisAvatarPsoLoader.VerboseLogging)
        {
            BasisDebug.Log($"Avatar.TotalLoad hash={generated?.AssetBundleHash ?? "unknown"} success={success} ms={totalLoad.Elapsed.TotalMilliseconds:F1}", BasisDebug.LogTag.Event);
        }
    }

    public static async Task<Scene> LoadSceneFromBundleAsync(BasisTrackedBundleWrapper bundle, bool MakeActiveScene, BasisProgressReport progressCallback)
    {
        string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        bool AssignedIncrement = false;
        string[] scenePaths = bundle.AssetBundle.GetAllScenePaths();
        if (scenePaths.Length == 0)
        {
            BasisDebug.LogError("No scenes found in AssetBundle.");
            return new Scene();
        }
        if (scenePaths.Length > 1)
        {
            BasisDebug.LogError("More then one scene was found in The Asset Bundle, Please Correct!");
            return new Scene();
        }

        if (!string.IsNullOrEmpty(scenePaths[0]))
        {
            string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePaths[0]);
            // Load the scene asynchronously
            AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(scenePaths[0], LoadSceneMode.Additive);
            asyncLoad.allowSceneActivation = true;
            while (!asyncLoad.isDone)
            {
                progressCallback.ReportProgress(UniqueID, Mathf.Min(asyncLoad.progress, 0.99f) * 100, $"Activating scene {sceneName}");
                await Task.Yield();
            }

            BasisDebug.Log("Scene loaded successfully from AssetBundle.");
            Scene loadedScene = SceneManager.GetSceneByPath(scenePaths[0]);
            bundle.MetaLink = loadedScene.path;
            // Set the loaded scene as the active scene
            if (loadedScene.IsValid())
            {
                ChecksRequired ChecksRequired = new ChecksRequired();
                ChecksRequired.UseContentRemoval = true;
                ChecksRequired.ScrubPersistentUnityEvents = true;
                ContentPoliceControl.ContentControl(ChecksRequired, Selector.World, loadedScene, true);
                AssignedIncrement = bundle.Increment();
                if (MakeActiveScene)
                {
                    SceneManager.SetActiveScene(loadedScene);
                    BasisDebug.Log("Scene set as active: " + loadedScene.name);
                }
                BasisDebug.Log("Scene loaded: " + loadedScene.name + " (MakeActive=" + MakeActiveScene + ", Incremented=" + AssignedIncrement + ")");
#if UNITY_BUNDLEUNLOAD
                bundle.ReleaseBundleBackingStore();
#endif
                progressCallback.ReportProgress(UniqueID, 100, $"Loaded scene {sceneName}");
                return loadedScene;
            }
            else
            {
                BasisDebug.LogError("Failed to get loaded scene.");
            }
        }
        else
        {
            BasisDebug.LogError("Path was null or empty! this should not be happening!");
        }
        return new Scene();
    }
}

// Cooperative per-frame budget for the synchronous Instantiate + ContentPolice scrub tail of a
// bundle load (that work can't leave the main thread, so concurrent loads otherwise stack their
// tails into one hitch). Loads call WaitForBudgetAsync before the heavy step; once a frame has
// spent FrameBudgetMs on load tails, further loads defer to the next frame. Light frames still
// run many back-to-back, so mass joins aren't throttled. Main-thread only (no locks needed).
public static class BasisLoadFrameBudget
{
    // <= 0 disables the gate (original stack-in-one-frame behavior).
    public static double FrameBudgetMs = 4.0;

    private static int _budgetFrame = -1;
    private static double _spentThisFrameMs;

    public static async Task WaitForBudgetAsync()
    {
        if (FrameBudgetMs <= 0) return;
        ResetIfNewFrame();
        while (_spentThisFrameMs >= FrameBudgetMs)
        {
            await Task.Yield();
            ResetIfNewFrame();
        }
    }

    public static double BeginStep() => Time.realtimeSinceStartupAsDouble * 1000.0;

    public static void EndStep(double startMs)
    {
        if (FrameBudgetMs <= 0) return;
        ResetIfNewFrame();
        _spentThisFrameMs += Time.realtimeSinceStartupAsDouble * 1000.0 - startMs;
    }

    private static void ResetIfNewFrame()
    {
        int frame = Time.frameCount;
        if (frame != _budgetFrame)
        {
            _budgetFrame = frame;
            _spentThisFrameMs = 0;
        }
    }
}
