using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Basis.Scripts.BasisSdk;
using HVR.Basis.Comms;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
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
            var vrcfuryCallbacks = callbacks.Where(callback => callback.Method.DeclaringType == typeof(BasisVrcfuryAutoShim)
                                                               && callback.Method.Name == nameof(BasisVrcfuryAutoShim.OnBeforeTestInEditor)).ToArray();
            Assert.That(vrcfuryCallbacks, Has.Length.EqualTo(1), "VRCFury should be registered exactly once on the Basis Test in Editor pipeline.");
        }

        [UnityTest]
        public IEnumerator TestInEditorHook_RemainsRegisteredAfterEnteringPlayMode() {
            RuntimeHelpers.RunClassConstructor(typeof(BasisVrcfuryAutoShim).TypeHandle);
            Assert.That(GetVrcfuryTestInEditorCallbacks(), Has.Length.EqualTo(1),
                "VRCFury should start with exactly one Test in Editor callback before entering Play Mode.");

            yield return new EnterPlayMode();

            Assert.That(GetVrcfuryTestInEditorCallbacks(), Has.Length.EqualTo(1),
                "Basis enters Play Mode before cloning the avatar, so VRCFury's Test in Editor hook must survive the domain transition.");

            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator TestInEditor_ArmatureLinkProcessesCloneWhileInPlayMode() {
            yield return new EnterPlayMode();
            Assert.That(Application.isPlaying, Is.True);

            var authored = CreateSimpleArmatureLinkAvatar();
            GameObject clone = null;
            try {
                clone = UnityEngine.Object.Instantiate(authored);
                BasisAssetBundlePipeline.DestroyEditorOnlyInAvatar(clone);
                BasisAvatarSDKInspector.OnBeforeTestInEditor?.Invoke(clone);
                BasisAssetBundlePipeline.PostProcessAvatar(clone);

                var clonedTarget = clone.transform.Find("Target");
                var clonedSource = clonedTarget?.Find("Prop");
                Assert.That(clonedSource, Is.Not.Null,
                    "Armature Link must process the Basis clone when OnBeforeTestInEditor runs after Play Mode has already started.");
                Assert.That(clone.GetComponentsInChildren<VRCFury>(true), Is.Empty);
                Assert.That(authored.transform.Find("Wearable/Prop"), Is.Not.Null,
                    "Processing the Play Mode clone must leave the authored avatar unchanged.");
            } finally {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                UnityEngine.Object.DestroyImmediate(authored);
            }

            yield return new ExitPlayMode();
        }

        [Test]
        public void TestInEditorHook_CoexistsWithNdmfHookWhenInstalled() {
            RuntimeHelpers.RunClassConstructor(typeof(BasisVrcfuryAutoShim).TypeHandle);
            var ndmfHookType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("HVR.Basis.NDMF.BasisNDMFBuildHook", false))
                .FirstOrDefault(type => type != null);
            if (ndmfHookType == null) Assert.Ignore("The Basis NDMF hook is not installed in this test project.");

            RuntimeHelpers.RunClassConstructor(ndmfHookType.TypeHandle);
            var callbacks = BasisAvatarSDKInspector.OnBeforeTestInEditor?.GetInvocationList() ?? Array.Empty<Delegate>();
            Assert.That(callbacks.Any(callback => callback.Method.DeclaringType == typeof(BasisVrcfuryAutoShim)), Is.True);
            Assert.That(callbacks.Any(callback => callback.Method.DeclaringType == ndmfHookType), Is.True,
                "Basis NDMF and VRCFury must both remain subscribed to the shared Test in Editor clone callback.");
        }

        private static Delegate[] GetVrcfuryTestInEditorCallbacks() {
            return (BasisAvatarSDKInspector.OnBeforeTestInEditor?.GetInvocationList() ?? Array.Empty<Delegate>())
                .Where(callback => callback.Method.DeclaringType == typeof(BasisVrcfuryAutoShim)
                                   && callback.Method.Name == nameof(BasisVrcfuryAutoShim.OnBeforeTestInEditor))
                .ToArray();
        }

        [Test]
        public void TestInEditor_InstantiateRemapsArmatureLinkReferencesIntoClone() {
            var authored = CreateSimpleArmatureLinkAvatar();
            GameObject clone = null;
            try {
                clone = UnityEngine.Object.Instantiate(authored);
                var clonedFury = clone.GetComponentInChildren<VRCFury>(true);
                var clonedModel = clonedFury.content as ArmatureLink;
                var clonedSource = clone.transform.Find("Wearable/Prop").gameObject;
                var clonedTarget = clone.transform.Find("Target").gameObject;

                Assert.That(clonedModel, Is.Not.Null);
                Assert.That(clonedModel.propBone, Is.SameAs(clonedSource));
                Assert.That(clonedModel.propBone, Is.Not.SameAs(authored.transform.Find("Wearable/Prop").gameObject));
                var clonedObjectTarget = clonedModel.linkTo.Single(link => link.useObj);
                Assert.That(clonedObjectTarget.obj, Is.SameAs(clonedTarget));
                Assert.That(clonedObjectTarget.obj, Is.Not.SameAs(authored.transform.Find("Target").gameObject));
            } finally {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                UnityEngine.Object.DestroyImmediate(authored);
            }
        }

        [Test]
        public void TestInEditor_DefaultHumanoidHipsTargetWorksOnRealBasisAvatar() {
            const string prefabPath = "Packages/com.basis.sdk/Prefabs/Loadins/LoadingAvatar.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(prefab, Is.Not.Null, $"Expected Basis humanoid test prefab at {prefabPath}");

            GameObject authored = null;
            GameObject clone = null;
            try {
                authored = UnityEngine.Object.Instantiate(prefab);
                authored.name = "VRCFury Humanoid Armature Link Authored Avatar";
                var authoredAvatar = authored.GetComponent<BasisAvatar>();
                Assert.That(authoredAvatar, Is.Not.Null);
                Assert.That(authoredAvatar.Animator, Is.Not.Null);
                Assert.That(authoredAvatar.Animator.avatar, Is.Not.Null);
                Assert.That(authoredAvatar.Animator.isHuman, Is.True);
                var authoredHips = authoredAvatar.Animator.GetBoneTransform(HumanBodyBones.Hips);
                Assert.That(authoredHips, Is.Not.Null);

                var wearable = new GameObject("VRCFuryWearable");
                wearable.transform.SetParent(authored.transform, false);
                var source = new GameObject("VRCFuryProp");
                source.transform.SetParent(wearable.transform, false);
                BasisVrcfuryAuthoringMenus.AddFeature(wearable, new ArmatureLink {
                    propBone = source,
                    recursive = false
                    // Use the model's normal default linkTo: Humanoid Hips.
                }, "Create VRCFury Humanoid Armature Link Test Feature");

                clone = UnityEngine.Object.Instantiate(authored);
                var cloneAvatar = clone.GetComponent<BasisAvatar>();
                Assert.That(cloneAvatar.Animator, Is.Not.Null);
                var cloneHips = cloneAvatar.Animator.GetBoneTransform(HumanBodyBones.Hips);
                Assert.That(cloneHips, Is.Not.Null);
                var cloneSourceBefore = clone.transform.Find("VRCFuryWearable/VRCFuryProp");
                Assert.That(cloneSourceBefore, Is.Not.Null);

                BasisAssetBundlePipeline.DestroyEditorOnlyInAvatar(clone);
                BasisAvatarSDKInspector.OnBeforeTestInEditor?.Invoke(clone);
                BasisAssetBundlePipeline.PostProcessAvatar(clone);

                var cloneSourceAfter = cloneHips.Find("VRCFuryProp");
                Assert.That(cloneSourceAfter, Is.Not.Null,
                    "The default VRCFury Armature Link should resolve BasisAvatar.Animator Humanoid Hips during Test in Editor.");
                Assert.That(cloneSourceAfter.parent, Is.SameAs(cloneHips));
                Assert.That(clone.GetComponentsInChildren<VRCFury>(true), Is.Empty);
                Assert.That(source.transform.parent, Is.SameAs(wearable.transform),
                    "Processing the Test in Editor clone must leave the authored wearable unchanged.");
            } finally {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                if (authored != null) UnityEngine.Object.DestroyImmediate(authored);
            }
        }

        [Test]
        public void TestInEditor_FullBasisCallbackPipelineAppliesArmatureLinkOnlyToClone() {
            var authored = CreateSimpleArmatureLinkAvatar();
            GameObject clone = null;
            try {
                var authoredSource = authored.transform.Find("Wearable/Prop");
                var authoredParentBefore = authoredSource.parent;

                clone = UnityEngine.Object.Instantiate(authored);
                BasisAssetBundlePipeline.DestroyEditorOnlyInAvatar(clone);
                BasisAvatarSDKInspector.OnBeforeTestInEditor?.Invoke(clone);
                BasisAssetBundlePipeline.PostProcessAvatar(clone);

                var clonedTarget = clone.transform.Find("Target");
                var clonedSource = clonedTarget.Find("Prop");
                Assert.That(clonedSource, Is.Not.Null, "Armature Link should be applied by the actual shared Basis Test in Editor callback chain.");
                Assert.That(clonedSource.parent, Is.SameAs(clonedTarget));
                Assert.That(clone.GetComponentsInChildren<VRCFury>(true), Is.Empty,
                    "VRCFury authoring metadata must not survive on the Test in Editor clone.");
                Assert.That(authoredSource.parent, Is.SameAs(authoredParentBefore),
                    "Test in Editor must not destructively modify the authored avatar.");
            } finally {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                UnityEngine.Object.DestroyImmediate(authored);
            }
        }

        [Test]
        public void Build_FullBasisCallbackPipelineAppliesArmatureLinkOnlyToClone() {
            var authored = CreateSimpleArmatureLinkAvatar();
            var settings = ScriptableObject.CreateInstance<BasisAssetBundleObject>();
            GameObject clone = null;
            try {
                settings.TemporaryStorage = TempFolder;
                var authoredSource = authored.transform.Find("Wearable/Prop");
                var authoredParentBefore = authoredSource.parent;

                clone = UnityEngine.Object.Instantiate(authored);
                BasisAssetBundlePipeline.DestroyEditorOnlyInAvatar(clone);
                BasisAssetBundlePipeline.OnBeforeBuildPrefab?.Invoke(clone, settings);
                BasisAssetBundlePipeline.PostProcessAvatar(clone);

                var clonedTarget = clone.transform.Find("Target");
                var clonedSource = clonedTarget.Find("Prop");
                Assert.That(clonedSource, Is.Not.Null, "Armature Link should be applied by the actual Basis build callback chain.");
                Assert.That(clone.GetComponentsInChildren<VRCFury>(true), Is.Empty);
                Assert.That(authoredSource.parent, Is.SameAs(authoredParentBefore));
            } finally {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                UnityEngine.Object.DestroyImmediate(settings);
                UnityEngine.Object.DestroyImmediate(authored);
            }
        }

        [Test]
        public void TestInEditor_FullBasisCallbackPipelineRewritesRecursiveSkinnedArmature() {
            var authored = CreateRecursiveSkinnedArmatureLinkAvatar();
            GameObject clone = null;
            try {
                var authoredRenderer = authored.transform.Find("Wearable/Mesh").GetComponent<SkinnedMeshRenderer>();
                var authoredHips = authored.transform.Find("Wearable/Armature/Hips");

                clone = UnityEngine.Object.Instantiate(authored);
                BasisAssetBundlePipeline.DestroyEditorOnlyInAvatar(clone);
                BasisAvatarSDKInspector.OnBeforeTestInEditor?.Invoke(clone);
                BasisAssetBundlePipeline.PostProcessAvatar(clone);

                var targetHips = clone.transform.Find("TargetArmature/Hips");
                var targetSpine = targetHips.Find("Spine");
                var renderer = clone.transform.Find("Wearable/Mesh").GetComponent<SkinnedMeshRenderer>();
                Assert.That(renderer.bones, Has.Length.EqualTo(2));
                Assert.That(renderer.bones[0], Is.SameAs(targetHips));
                Assert.That(renderer.bones[1], Is.SameAs(targetSpine));
                Assert.That(clone.transform.Find("Wearable/Armature/Hips"), Is.Null,
                    "Matched clothing bones should be collapsed after the skin references are rewritten.");
                Assert.That(clone.GetComponentsInChildren<VRCFury>(true), Is.Empty);
                Assert.That(authoredRenderer.bones[0], Is.SameAs(authoredHips),
                    "The authored skinned mesh must retain its original clothing bones.");
            } finally {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                UnityEngine.Object.DestroyImmediate(authored);
            }
        }

        [Test]
        public void ArmatureLink_UsesFirstValidFallbackTargetAndOffset() {
            var root = new GameObject("Avatar");
            try {
                var avatar = root.AddComponent<BasisAvatar>();
                var target = new GameObject("Target");
                target.transform.SetParent(root.transform, false);
                var offset = new GameObject("Offset");
                offset.transform.SetParent(target.transform, false);
                var source = new GameObject("Prop");
                source.transform.SetParent(root.transform, false);
                var invalidExternalTarget = new GameObject("External");
                try {
                    var model = new ArmatureLink {
                        propBone = source,
                        recursive = false,
                        linkTo = {
                            new ArmatureLink.LinkTo { useBone = false, useObj = true, obj = invalidExternalTarget },
                            new ArmatureLink.LinkTo { useBone = false, useObj = true, obj = target, offset = "Offset" }
                        }
                    };

                    BasisArmatureLinkShim.Apply(root, avatar, null, model);
                    Assert.That(source.transform.parent, Is.SameAs(offset.transform));
                } finally {
                    UnityEngine.Object.DestroyImmediate(invalidExternalTarget);
                }
            } finally {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ArmatureLink_RecursiveKeepsMappedBoneWithExtraComponent() {
            var root = new GameObject("Avatar");
            var settings = ScriptableObject.CreateInstance<BasisAssetBundleObject>();
            try {
                settings.TemporaryStorage = TempFolder;
                var avatar = root.AddComponent<BasisAvatar>();
                var target = new GameObject("Hips");
                target.transform.SetParent(root.transform, false);
                var targetSpine = new GameObject("Spine");
                targetSpine.transform.SetParent(target.transform, false);
                var source = new GameObject("Hips");
                source.transform.SetParent(root.transform, false);
                var sourceSpine = new GameObject("Spine");
                sourceSpine.transform.SetParent(source.transform, false);
                sourceSpine.AddComponent<BoxCollider>();

                var model = new ArmatureLink {
                    propBone = source,
                    recursive = true,
                    linkTo = { new ArmatureLink.LinkTo { useBone = false, useObj = true, obj = target } }
                };

                BasisArmatureLinkShim.Apply(root, avatar, settings, model);

                Assert.That(sourceSpine, Is.Not.Null);
                Assert.That(sourceSpine.transform.parent, Is.SameAs(targetSpine.transform),
                    "Mapped bones with non-Transform components must be retained and parented to their mapped target.");
            } finally {
                UnityEngine.Object.DestroyImmediate(settings);
                UnityEngine.Object.DestroyImmediate(root);
            }
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

        private static GameObject CreateSimpleArmatureLinkAvatar() {
            var root = new GameObject("Avatar");
            root.AddComponent<BasisAvatar>();

            var target = new GameObject("Target");
            target.transform.SetParent(root.transform, false);

            var wearable = new GameObject("Wearable");
            wearable.transform.SetParent(root.transform, false);
            var source = new GameObject("Prop");
            source.transform.SetParent(wearable.transform, false);

            BasisVrcfuryAuthoringMenus.AddFeature(wearable, new ArmatureLink {
                propBone = source,
                recursive = false,
                linkTo = {
                    new ArmatureLink.LinkTo {
                        useBone = false,
                        useObj = true,
                        obj = target
                    }
                }
            }, "Create VRCFury Armature Link Test Feature");
            return root;
        }

        private static GameObject CreateRecursiveSkinnedArmatureLinkAvatar() {
            var root = new GameObject("Avatar");
            root.AddComponent<BasisAvatar>();

            var targetArmature = new GameObject("TargetArmature");
            targetArmature.transform.SetParent(root.transform, false);
            var targetHips = new GameObject("Hips");
            targetHips.transform.SetParent(targetArmature.transform, false);
            var targetSpine = new GameObject("Spine");
            targetSpine.transform.SetParent(targetHips.transform, false);

            var wearable = new GameObject("Wearable");
            wearable.transform.SetParent(root.transform, false);
            var sourceArmature = new GameObject("Armature");
            sourceArmature.transform.SetParent(wearable.transform, false);
            var sourceHips = new GameObject("Hips");
            sourceHips.transform.SetParent(sourceArmature.transform, false);
            var sourceSpine = new GameObject("Spine");
            sourceSpine.transform.SetParent(sourceHips.transform, false);

            var meshObject = new GameObject("Mesh");
            meshObject.transform.SetParent(wearable.transform, false);
            var renderer = meshObject.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh { name = "VRCFuryArmatureLinkSkinTestMesh" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.bindposes = new[] { sourceHips.transform.worldToLocalMatrix, sourceSpine.transform.worldToLocalMatrix };
            renderer.sharedMesh = mesh;
            renderer.rootBone = sourceHips.transform;
            renderer.bones = new[] { sourceHips.transform, sourceSpine.transform };

            BasisVrcfuryAuthoringMenus.AddFeature(wearable, new ArmatureLink {
                propBone = sourceHips,
                recursive = true,
                linkTo = {
                    new ArmatureLink.LinkTo {
                        useBone = false,
                        useObj = true,
                        obj = targetHips
                    }
                }
            }, "Create VRCFury Recursive Armature Link Test Feature");
            return root;
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
