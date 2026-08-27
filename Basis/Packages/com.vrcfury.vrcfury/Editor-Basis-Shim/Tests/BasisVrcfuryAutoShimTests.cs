using System;
using NUnit.Framework;
using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEngine;
using VF.Model;
using VF.Model.Feature;

namespace VF.Integration.Basis.Shim.Tests {
    internal class BasisVrcfuryAutoShimTests {
        private const string TempFolder = "Assets/__VRCFuryBasisShimTests";

        [SetUp]
        public void SetUp() {
            if (!AssetDatabase.IsValidFolder(TempFolder)) {
                AssetDatabase.CreateFolder("Assets", "__VRCFuryBasisShimTests");
            }
        }

        [TearDown]
        public void TearDown() {
            AssetDatabase.DeleteAsset(TempFolder);
        }

        [Test]
        public void ArmatureLink_ReparentsSimpleLinkOnBasisClone() {
            var root = new GameObject("Avatar");
            try {
                var avatar = root.AddComponent<BasisAvatar>();
                var target = new GameObject("Target");
                target.transform.SetParent(root.transform, false);
                var source = new GameObject("Prop");
                source.transform.SetParent(root.transform, false);

                var model = new ArmatureLink {
                    propBone = source,
                    recursive = false,
                    linkTo = {
                        new ArmatureLink.LinkTo {
                            useBone = false,
                            useObj = true,
                            obj = target
                        }
                    }
                };

                BasisArmatureLinkShim.Apply(root, avatar, null, model);

                Assert.That(source.transform.parent, Is.EqualTo(target.transform));
            } finally {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void BlendshapeOptimizer_UsesBasisStripperWithoutVrchatSdk() {
            var root = new GameObject("Avatar");
            try {
                var avatar = root.AddComponent<BasisAvatar>();
                var renderObject = new GameObject("Face");
                renderObject.transform.SetParent(root.transform, false);
                var renderer = renderObject.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = MakeBlendshapeMesh();
                renderer.SetBlendShapeWeight(0, 0f);
                renderer.SetBlendShapeWeight(1, 50f);

                avatar.FaceVisemeMesh = renderer;
                avatar.FaceVisemeMovement = new int[15];
                for (var i = 0; i < avatar.FaceVisemeMovement.Length; i++) avatar.FaceVisemeMovement[i] = -1;
                avatar.FaceVisemeMovement[0] = 0;

                var fury = root.AddComponent<VRCFury>();
                fury.content = new BlendshapeOptimizer();

                var settings = ScriptableObject.CreateInstance<BasisAssetBundleObject>();
                try {
                    settings.TemporaryStorage = TempFolder;
                    BasisVrcfuryAutoShim.ProcessBuildClone(root, settings);
                } finally {
                    UnityEngine.Object.DestroyImmediate(settings);
                }

                Assert.That(renderer.sharedMesh, Is.Not.Null);
                Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(1));
                Assert.That(renderer.sharedMesh.GetBlendShapeName(0), Is.EqualTo("KeepMe"));
                Assert.That(avatar.FaceVisemeMovement[0], Is.EqualTo(0));
                Assert.That(root.GetComponent<VRCFury>(), Is.Null, "Build-only VRCFury metadata should be stripped from the Basis clone.");
            } finally {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static Mesh MakeBlendshapeMesh() {
            var mesh = new Mesh { name = "VRCFuryBasisShimTestMesh" };
            mesh.vertices = new[] {
                Vector3.zero,
                Vector3.right,
                Vector3.up
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();

            var keepDelta = new[] {
                new Vector3(0.01f, 0f, 0f),
                Vector3.zero,
                Vector3.zero
            };
            var bakeDelta = new[] {
                Vector3.zero,
                new Vector3(0f, 0.02f, 0f),
                Vector3.zero
            };
            var zeros = new Vector3[3];
            mesh.AddBlendShapeFrame("KeepMe", 100f, keepDelta, zeros, zeros);
            mesh.AddBlendShapeFrame("BakeMe", 100f, bakeDelta, zeros, zeros);
            return mesh;
        }
    }
}
