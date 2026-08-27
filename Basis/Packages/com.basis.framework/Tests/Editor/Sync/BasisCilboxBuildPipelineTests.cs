using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cilbox;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Basis.Tests.Sync
{
    [Cilboxable]
    public sealed class BasisCilboxBuildTestBehaviour : MonoBehaviour
    {
        public string Marker = "before";

        public void TouchMarker()
        {
        }
    }

    public sealed class BasisCilboxBuildPipelineTests
    {
        [Test]
        public void PrefabFinalization_SerializesPreparedStateWithoutTouchingOtherScenes()
        {
            EnsureBuildHookRegistered();

            Scene previousActiveScene = SceneManager.GetActiveScene();
            Scene externalScene = default;
            Scene contentScene = default;
            GameObject externalRoot = null;
            GameObject contentRoot = null;
            Action<Scene> handler = null;

            try
            {
                externalScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                externalRoot = new GameObject("CilboxExternalSceneRoot");
                SceneManager.MoveGameObjectToScene(externalRoot, externalScene);
                CilboxAvatarBasis externalCilbox = externalRoot.AddComponent<CilboxAvatarBasis>();
                string externalAssemblyBefore = externalCilbox.assemblyData;

                contentScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                contentRoot = new GameObject("CilboxContentRoot");
                SceneManager.MoveGameObjectToScene(contentRoot, contentScene);
                CilboxAvatarBasis contentCilbox = contentRoot.AddComponent<CilboxAvatarBasis>();
                BasisCilboxBuildTestBehaviour behaviour = contentRoot.AddComponent<BasisCilboxBuildTestBehaviour>();
                behaviour.Marker = "before";

                int loadedSceneCountBefore = SceneManager.sceneCount;
                Scene callbackScene = default;
                handler = scene =>
                {
                    callbackScene = scene;
                    behaviour.Marker = "prepared";
                };
                BasisCilboxBuildEvents.OnBeforeCilboxSerialize += handler;

                Assert.IsNotNull(BasisAssetBundlePipeline.OnBeforeBuildPrefabFinalize,
                    "The Cilbox finalization hook was not registered with the Basis build pipeline.");
                BasisAssetBundlePipeline.OnBeforeBuildPrefabFinalize.Invoke(contentRoot, null);

                Assert.AreEqual(contentScene, contentRoot.scene,
                    "The isolated content root was not restored to its owning scene after Cilbox conversion.");
                Assert.IsTrue(externalRoot.activeSelf,
                    "Cilbox conversion must not deactivate unrelated loaded scene roots.");
                Assert.AreEqual(externalAssemblyBefore, externalCilbox.assemblyData,
                    "Cilbox conversion leaked assembly data into another loaded scene.");
                Assert.AreEqual(loadedSceneCountBefore, SceneManager.sceneCount,
                    "The temporary Cilbox staging scene was not cleaned up.");
                Assert.IsTrue(callbackScene.IsValid(), "The pre-serialization callback did not receive a scene.");
                Assert.IsFalse(callbackScene.isLoaded,
                    "The temporary pre-serialization scene should be unloaded after finalization completes.");

                Assert.IsTrue(behaviour == null,
                    "The authored Cilboxable behaviour should be replaced by a CilboxProxy.");
                CilboxProxy proxy = contentRoot.GetComponent<CilboxProxy>();
                Assert.IsNotNull(proxy, "Cilbox did not create a proxy for the generic Cilboxable behaviour.");
                Assert.AreSame(contentCilbox, proxy.box,
                    "The generated proxy must point to the Cilbox that belongs to the isolated content.");
                Assert.AreEqual("prepared", ReadSerializedStringField(proxy, nameof(BasisCilboxBuildTestBehaviour.Marker)),
                    "The pre-serialization callback ran too late; the proxy captured the pre-build field value.");
            }
            finally
            {
                if (handler != null)
                {
                    BasisCilboxBuildEvents.OnBeforeCilboxSerialize -= handler;
                }

                if (contentRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(contentRoot);
                }
                if (externalRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(externalRoot);
                }
                if (contentScene.IsValid() && contentScene.isLoaded)
                {
                    EditorSceneManager.CloseScene(contentScene, true);
                }
                if (externalScene.IsValid() && externalScene.isLoaded)
                {
                    EditorSceneManager.CloseScene(externalScene, true);
                }
                if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                {
                    SceneManager.SetActiveScene(previousActiveScene);
                }
            }
        }

        [Test]
        public void PrefabFinalization_WorksWithoutPreSerializationSubscribers()
        {
            EnsureBuildHookRegistered();

            Scene previousActiveScene = SceneManager.GetActiveScene();
            Scene contentScene = default;
            GameObject contentRoot = null;

            try
            {
                contentScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                contentRoot = new GameObject("CilboxNoSubscriberRoot");
                SceneManager.MoveGameObjectToScene(contentRoot, contentScene);
                CilboxAvatarBasis contentCilbox = contentRoot.AddComponent<CilboxAvatarBasis>();
                contentRoot.AddComponent<BasisCilboxBuildTestBehaviour>();

                BasisAssetBundlePipeline.OnBeforeBuildPrefabFinalize.Invoke(contentRoot, null);

                CilboxProxy proxy = contentRoot.GetComponent<CilboxProxy>();
                Assert.IsNotNull(proxy, "Generic Cilbox conversion should not require an event subscriber.");
                Assert.AreSame(contentCilbox, proxy.box);
            }
            finally
            {
                if (contentRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(contentRoot);
                }
                if (contentScene.IsValid() && contentScene.isLoaded)
                {
                    EditorSceneManager.CloseScene(contentScene, true);
                }
                if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                {
                    SceneManager.SetActiveScene(previousActiveScene);
                }
            }
        }

        private static void EnsureBuildHookRegistered()
        {
            MethodInfo initialize = typeof(BasisCilboxBuildHook).GetMethod(
                "Initialize",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(initialize);
            initialize.Invoke(null, null);
        }

        private static string ReadSerializedStringField(CilboxProxy proxy, string fieldName)
        {
            byte[] bytes = Convert.FromBase64String(proxy.serializedObjectData);
            Serializee[] fields = new Serializee(bytes, Serializee.ElementType.List).AsArray();
            foreach (Serializee field in fields)
            {
                Dictionary<string, Serializee> map = field.AsMap();
                if (map.TryGetValue("n", out Serializee name) && name.AsString() == fieldName)
                {
                    return map["d"].AsString();
                }
            }

            Assert.Fail($"Serialized proxy data did not contain field '{fieldName}'. Fields: "
                + string.Join(", ", fields.Select(field => field.AsMap()["n"].AsString())));
            return null;
        }
    }
}
