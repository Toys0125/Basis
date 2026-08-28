using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Basis.Scripts.BasisSdk;
using HVR.Basis.Comms;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using VF.Model;
using VF.Model.Feature;

namespace VF.Integration.Basis.Shim.Tests {
    internal class BasisVrcfuryAutoShimTests {
        private const string TempFolder = "Assets/__VRCFuryBasisShimTests";

        [OneTimeSetUp]
        public void OneTimeSetUp() {
            if (!AssetDatabase.IsValidFolder(TempFolder)) {
                AssetDatabase.CreateFolder("Assets", "__VRCFuryBasisShimTests");
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown() {
            BasisVrcfuryAutoShim.CleanupTestInEditorStorage();
            if (System.IO.Directory.Exists(TempFolder)) System.IO.Directory.Delete(TempFolder, true);
            if (System.IO.File.Exists(TempFolder + ".meta")) System.IO.File.Delete(TempFolder + ".meta");
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
            Assert.That(BasisVrcfuryAuthoringMenus.MmdCompatibilityMenuPath, Is.EqualTo("Component/VRCFury/MMD Compatibility (VRCFury)"));
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
        public void BasisAuthoring_UsesCustomVrcfuryInspectorForMmdCompatibility() {
            var root = new GameObject("Avatar");
            try {
                var fury = root.AddComponent<VRCFury>();
                fury.content = new MmdCompatibility();

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
        public void TestInEditorCleanup_PreservesUnownedFolders() {
            var unowned = $"{TempFolder}/UnownedTestInEditorStorage";
            AssetDatabase.CreateFolder(TempFolder, "UnownedTestInEditorStorage");

            Assert.That(BasisVrcfuryAutoShim.TryDeleteOwnedTestInEditorStorage(unowned), Is.False);
            Assert.That(AssetDatabase.IsValidFolder(unowned), Is.True);
        }

        [Test]
        public void TestInEditorHook_IsRegisteredWithBasis() {
            RuntimeHelpers.RunClassConstructor(typeof(BasisVrcfuryAutoShim).TypeHandle);
            var callbacks = BasisAvatarSDKInspector.OnBeforeTestInEditor?.GetInvocationList() ?? Array.Empty<Delegate>();
            Assert.That(callbacks.Any(callback => callback.Method.DeclaringType == typeof(BasisVrcfuryAutoShim)
                                                  && callback.Method.Name == nameof(BasisVrcfuryAutoShim.OnBeforeTestInEditor)), Is.True);
        }

        [Test]
        public void TestInEditor_AppliesArmatureLinkAndStripsAuthoringComponent() {
            var root = new GameObject("Avatar");
            try {
                root.AddComponent<BasisAvatar>();
                var target = new GameObject("Target");
                target.transform.SetParent(root.transform, false);
                var source = new GameObject("Prop");
                source.transform.SetParent(root.transform, false);

                var fury = root.AddComponent<VRCFury>();
                fury.content = new ArmatureLink {
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

                BasisVrcfuryAutoShim.OnBeforeTestInEditor(root);

                Assert.That(source.transform.parent, Is.EqualTo(target.transform));
                Assert.That(root.GetComponent<VRCFury>(), Is.Null);
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
        public void BlendshapeOptimizer_PreservesAutomaticFaceTrackingTargets() {
            var root = new GameObject("Avatar");
            var definitionFile = ScriptableObject.CreateInstance<BlendshapeActuationDefinitionFile>();
            var settings = ScriptableObject.CreateInstance<BasisAssetBundleObject>();
            try {
                root.AddComponent<BasisAvatar>();
                var renderObject = new GameObject("Face");
                renderObject.transform.SetParent(root.transform, false);
                var renderer = renderObject.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = MakeBlendshapeMesh("FaceTrackMe", "BakeMe");

                definitionFile.definitions = new[] {
                    new BlendshapeActuationDefinition {
                        address = "FT/Test",
                        inStart = 0,
                        inEnd = 1,
                        outStart = 0,
                        outEnd = 100,
                        blendshapes = new[] { "FaceTrackMe" },
                        onlyFirstMatch = true
                    }
                };

                var automatic = root.AddComponent<AutomaticFaceTracking>();
                SetField(automatic, "useOverrideDefinitionFiles", true);
                SetField(automatic, "overrideDefinitionFiles", new[] { definitionFile });

                var fury = root.AddComponent<VRCFury>();
                fury.content = new BlendshapeOptimizer();

                settings.TemporaryStorage = TempFolder;
                BasisVrcfuryAutoShim.ProcessBuildClone(root, settings);

                Assert.That(renderer.sharedMesh, Is.Not.Null);
                Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(1));
                Assert.That(renderer.sharedMesh.GetBlendShapeName(0), Is.EqualTo("FaceTrackMe"));
            } finally {
                UnityEngine.Object.DestroyImmediate(settings);
                UnityEngine.Object.DestroyImmediate(definitionFile);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void BlendshapeOptimizer_PreservesDefaultUnifiedExpressionsFaceTrackingTargets() {
            var root = new GameObject("Avatar");
            var settings = ScriptableObject.CreateInstance<BasisAssetBundleObject>();
            try {
                root.AddComponent<BasisAvatar>();
                var renderObject = new GameObject("Face");
                renderObject.transform.SetParent(root.transform, false);
                var renderer = renderObject.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = MakeBlendshapeMesh("MouthRaiserLower", "BakeMe");

                root.AddComponent<AutomaticFaceTracking>();
                var fury = root.AddComponent<VRCFury>();
                fury.content = new BlendshapeOptimizer();

                settings.TemporaryStorage = TempFolder;
                BasisVrcfuryAutoShim.ProcessBuildClone(root, settings);

                Assert.That(renderer.sharedMesh, Is.Not.Null);
                Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(1));
                Assert.That(renderer.sharedMesh.GetBlendShapeName(0), Is.EqualTo("MouthRaiserLower"));
            } finally {
                UnityEngine.Object.DestroyImmediate(settings);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void BlendshapeOptimizer_MmdCompatibilityPreservesKnownMmdShapesOnBody() {
            var root = new GameObject("Avatar");
            var settings = ScriptableObject.CreateInstance<BasisAssetBundleObject>();
            try {
                root.AddComponent<BasisAvatar>();
                var body = new GameObject("Body");
                body.transform.SetParent(root.transform, false);
                var renderer = body.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = MakeBlendshapeMesh("あ２", "BakeMe");

                var optimizer = root.AddComponent<VRCFury>();
                optimizer.content = new BlendshapeOptimizer();
                var mmd = root.AddComponent<VRCFury>();
                mmd.content = new MmdCompatibility();

                settings.TemporaryStorage = TempFolder;
                BasisVrcfuryAutoShim.ProcessBuildClone(root, settings);

                Assert.That(renderer.sharedMesh, Is.Not.Null);
                Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(1));
                Assert.That(renderer.sharedMesh.GetBlendShapeName(0), Is.EqualTo("あ２"));
                Assert.That(root.GetComponents<VRCFury>(), Is.Empty);
            } finally {
                UnityEngine.Object.DestroyImmediate(settings);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TestInEditor_AppliesBlendshapeOptimizerWithTemporaryStorage() {
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

                BasisVrcfuryAutoShim.OnBeforeTestInEditor(root);

                Assert.That(renderer.sharedMesh, Is.Not.Null);
                Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(1));
                Assert.That(renderer.sharedMesh.GetBlendShapeName(0), Is.EqualTo("KeepMe"));
                Assert.That(avatar.FaceVisemeMovement[0], Is.EqualTo(0));
                Assert.That(root.GetComponent<VRCFury>(), Is.Null);
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

        private static void SetField(object target, string name, object value) {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Expected field '{name}' on {target.GetType().Name}");
            field.SetValue(target, value);
        }

        private static Mesh MakeBlendshapeMesh() => MakeBlendshapeMesh("KeepMe", "BakeMe");

        private static Mesh MakeBlendshapeMesh(params string[] names) {
            var mesh = new Mesh { name = "VRCFuryBasisShimTestMesh" };
            mesh.vertices = new[] {
                Vector3.zero,
                Vector3.right,
                Vector3.up
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();

            var zeros = new Vector3[3];
            for (var i = 0; i < names.Length; i++) {
                var delta = new[] {
                    i % 3 == 0 ? new Vector3(0.01f * (i + 1), 0f, 0f) : Vector3.zero,
                    i % 3 == 1 ? new Vector3(0f, 0.01f * (i + 1), 0f) : Vector3.zero,
                    i % 3 == 2 ? new Vector3(0f, 0f, 0.01f * (i + 1)) : Vector3.zero
                };
                mesh.AddBlendShapeFrame(names[i], 100f, delta, zeros, zeros);
            }
            return mesh;
        }
    }
}
