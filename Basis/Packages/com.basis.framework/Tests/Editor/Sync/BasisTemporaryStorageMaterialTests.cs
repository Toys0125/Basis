using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisTemporaryStorageMaterialTests
    {
        [Test]
        public void SavePrefabToTemporaryStorage_PreservesTransientRendererMaterial()
        {
            string folderName = $"BasisTempMaterialTest_{Guid.NewGuid():N}";
            string temporaryStorage = $"Assets/{folderName}";
            string sourceMaterialPath = $"{temporaryStorage}/Source.mat";
            BasisAssetBundleObject settings = null;
            GameObject root = null;
            Material transientMaterial = null;

            try
            {
                AssetDatabase.CreateFolder("Assets", folderName);

                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ??
                                Shader.Find("Standard") ??
                                Shader.Find("Unlit/Color");
                Assert.IsNotNull(shader, "A shader is required to construct the material test fixture.");

                Material sourceMaterial = new Material(shader) { name = "Source" };
                AssetDatabase.CreateAsset(sourceMaterial, sourceMaterialPath);
                AssetDatabase.ImportAsset(sourceMaterialPath, ImportAssetOptions.ForceSynchronousImport);
                sourceMaterial = AssetDatabase.LoadAssetAtPath<Material>(sourceMaterialPath);
                Assert.IsNotNull(sourceMaterial);
                Assert.IsTrue(EditorUtility.IsPersistent(sourceMaterial));

                transientMaterial = UnityEngine.Object.Instantiate(sourceMaterial);
                transientMaterial.name = "Source_Stripped";
                Assert.IsFalse(EditorUtility.IsPersistent(transientMaterial),
                    "The fixture must exercise a transient material reference.");

                root = new GameObject("TransientMaterialPrefabRoot");
                MeshRenderer renderer = root.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = transientMaterial;

                settings = ScriptableObject.CreateInstance<BasisAssetBundleObject>();
                settings.TemporaryStorage = temporaryStorage;

                bool wasModified = false;
                string prefabPath = TemporaryStorageHandler.SavePrefabToTemporaryStorage(
                    root, settings, ref wasModified, out _);

                Assert.IsTrue(wasModified);
                UnityEngine.Object.DestroyImmediate(root);
                root = null;

                AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);
                GameObject savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert.IsNotNull(savedPrefab, "The staged prefab could not be reloaded from the AssetDatabase.");

                MeshRenderer savedRenderer = savedPrefab.GetComponent<MeshRenderer>();
                Assert.IsNotNull(savedRenderer);
                Assert.IsNotNull(savedRenderer.sharedMaterial,
                    "A transient material assigned before prefab staging was serialized as a missing material reference.");
                Assert.IsTrue(EditorUtility.IsPersistent(savedRenderer.sharedMaterial),
                    "The staged prefab still references a non-persistent material after serialization.");
                Assert.IsFalse(string.IsNullOrEmpty(AssetDatabase.GetAssetPath(savedRenderer.sharedMaterial)),
                    "The staged material does not have a durable AssetDatabase path.");
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }

                if (transientMaterial != null && !EditorUtility.IsPersistent(transientMaterial))
                {
                    UnityEngine.Object.DestroyImmediate(transientMaterial);
                }

                if (settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }

                AssetDatabase.DeleteAsset(temporaryStorage);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }
    }
}
