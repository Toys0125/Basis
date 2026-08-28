using System;
using NUnit.Framework;
using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
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
        public void BasisOnlyVrcfuryComponent_StaysHiddenLikeUpstreamVrcfury() {
            var attribute = (AddComponentMenu)Attribute.GetCustomAttribute(typeof(VRCFury), typeof(AddComponentMenu));
            Assert.That(attribute, Is.Not.Null);
            Assert.That(attribute.componentMenu, Is.Empty);
        }

        [Test]
        public void BasisAuthoring_UsesUpstreamStyleFeatureMenuPaths() {
            Assert.That(BasisVrcfuryAuthoringMenus.ArmatureLinkMenuPath, Is.EqualTo("Component/VRCFury/Armature Link (VRCFury)"));
            Assert.That(BasisVrcfuryAuthoringMenus.BlendshapeOptimizerMenuPath, Is.EqualTo("Component/VRCFury/Blendshape Optimizer (VRCFury)"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void BasisAuthoring_UsesCustomVrcfuryInspector(bool armatureLink) {
            var root = new GameObject("Avatar");
            try {
                var fury = root.AddComponent<VRCFury>();
                fury.content = armatureLink
                    ? (FeatureModel)new ArmatureLink { propBone = root }
                    : new BlendshapeOptimizer();

                var editor = UnityEditor.Editor.CreateEditor(fury);
                try {
                    Assert.That(editor, Is.TypeOf<BasisVrcfuryAuthoringEditor>());
                    Assert.That(editor.CreateInspectorGUI(), Is.Not.Null);
                } finally {
                    UnityEngine.Object.DestroyImmediate(editor);
                }
            } finally {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void BasisAuthoring_HeaderOverlayUsesUpstreamRelativeWrapper() {
            var wrapper = BasisVrcfuryHeader.CreateOverlayHeader("Blendshape Optimizer");
            Assert.That(wrapper.childCount, Is.EqualTo(1));

            var area = wrapper[0];
            Assert.That(area.style.position.value, Is.EqualTo(Position.Absolute));
            Assert.That(area.style.top.value.value, Is.EqualTo(-21f));
            Assert.That(wrapper.style.position.value, Is.Not.EqualTo(Position.Absolute));
        }

        [Test]
        public void BasisAuthoring_AddFeatureCreatesNormalVrcfuryComponent() {
            var root = new GameObject("Avatar");
            try {
                var fury = BasisVrcfuryAuthoringMenus.AddFeature(root, new BlendshapeOptimizer(), "Test VRCFury Authoring");
                Assert.That(fury, Is.Not.Null);
                Assert.That(root.GetComponent<VRCFury>(), Is.SameAs(fury));
                Assert.That(fury.content, Is.TypeOf<BlendshapeOptimizer>());
            } finally {
                UnityEngine.Object.DestroyImmediate(root);
            }
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
