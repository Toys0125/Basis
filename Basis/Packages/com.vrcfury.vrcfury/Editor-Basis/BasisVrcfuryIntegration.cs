using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Basis.Scripts.BasisSdk;
using HVR.Vixxy;
using UnityEditor;
using UnityEngine;
using VF;
using VF.Builder;
using VF.Component;
using VF.Model;
using VF.Feature;
using VF.Service;
using VRC.SDK3.Avatars.Components;

namespace VF.Integration.Basis {
    [InitializeOnLoad]
    internal static class BasisVrcfuryIntegration {
        private static bool runningBuildHook;

        static BasisVrcfuryIntegration() {
            BasisAssetBundlePipeline.OnBeforeBuildPrefab -= BeforeBasisPrefabBuild;
            BasisAssetBundlePipeline.OnBeforeBuildPrefab += BeforeBasisPrefabBuild;
        }

        [MenuItem("Tools/VRCFury/BasisVR/Generate Compatibility For Selected Avatar")]
        private static void GenerateSelected() {
            var selected = Selection.activeGameObject;
            if (selected == null) {
                EditorUtility.DisplayDialog("VRCFury BasisVR", "Select a VRChat/Basis avatar or an object inside one.", "OK");
                return;
            }

            try {
                var avatar = ResolveOrCreateBasisAvatar(selected, out var created);
                if (avatar == null) {
                    EditorUtility.DisplayDialog("VRCFury BasisVR", "No BasisAvatar or VRCAvatarDescriptor could be found from the selection.", "OK");
                    return;
                }
                var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
                RefreshBasisAvatarMetadata(avatar, descriptor, overwriteExisting: created);
                var report = BasisVrcfuryConverter.Generate(avatar, buildClone: false);
                report.CreatedBasisAvatar = created;
                LogReport(avatar.gameObject, report);
                EditorUtility.DisplayDialog("VRCFury BasisVR", report.DialogSummary() +
                    "\n\nOriginal VRCFury and VRChat authoring components were kept. Only the generated Basis/Vixxy root is replaced on regeneration.", "OK");
            } catch (Exception e) {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("VRCFury BasisVR", "Compatibility generation failed. See the Console.\n\n" + e.Message, "OK");
            }
        }

        [MenuItem("Tools/VRCFury/BasisVR/Remove Generated Compatibility From Selected Avatar")]
        private static void RemoveSelected() {
            var selected = Selection.activeGameObject;
            if (selected == null) return;
            var avatar = selected.GetComponentInParent<BasisAvatar>(true) ?? selected.GetComponent<BasisAvatar>();
            if (avatar == null) return;
            var removed = BasisVrcfuryConverter.RemoveGenerated(avatar.gameObject);
            if (removed > 0) Debug.Log($"VRCFury BasisVR: removed {removed} generated compatibility root(s) from {avatar.name}.", avatar);
        }

        private static void BeforeBasisPrefabBuild(GameObject prefab, BasisAssetBundleObject settings) {
            if (runningBuildHook || prefab == null) return;
            var avatar = prefab.GetComponent<BasisAvatar>();
            if (avatar == null || prefab.GetComponentsInChildren<VRCFuryComponent>(true).Length == 0) return;

            runningBuildHook = true;
            try {
                var descriptor = prefab.GetComponent<VRCAvatarDescriptor>();
                if (descriptor == null) {
                    Debug.LogWarning("VRCFury BasisVR: VRCFury authoring exists, but this Basis build clone has no VRCAvatarDescriptor. " +
                                     "Vixxy-compatible features will be converted, but VRCFury's build-time processors cannot be reused.", prefab);
                }

                UpgradeCloneVrcfury(prefab);
                RefreshBasisAvatarMetadata(avatar, descriptor, overwriteExisting: false);
                var faceMetadata = CaptureBasisFaceMetadata(avatar);
                var report = BasisVrcfuryConverter.Generate(avatar, buildClone: true);

                SuppressDeferredSpsAndHaptics(prefab, report);

                if (descriptor != null && VRCFuryBuilder.ShouldRun(prefab.asVf())) {
                    InstallVixxyUsageHooks(prefab);
                    try {
                        VRCFuryBuilder.RunMain(prefab.asVf());
                    } finally {
                        ClearVixxyUsageHooks();
                    }
                }

                descriptor = prefab.GetComponent<VRCAvatarDescriptor>();
                RefreshBasisAvatarMetadata(avatar, descriptor, overwriteExisting: false);
                RestoreBasisFaceMetadata(avatar, faceMetadata);
                TranslateHeadChopComponents(prefab, report);
                StripVrcfuryAndVrchatRuntimeComponents(prefab);
                LogReport(prefab, report);
            } finally {
                runningBuildHook = false;
            }
        }

        private static void InstallVixxyUsageHooks(GameObject root) {
            BlendshapeOptimizerBuilder.GetExternalBlendshapesToKeep = skin => GetVixxyBlendshapesForRenderer(root, skin);
            FindAnimatedTransformsService.AddExternalAnimatedTransforms = (_, animated) => AddVixxyAnimatedTransforms(root, animated);
        }

        private static void ClearVixxyUsageHooks() {
            BlendshapeOptimizerBuilder.GetExternalBlendshapesToKeep = null;
            FindAnimatedTransformsService.AddExternalAnimatedTransforms = null;
        }

        private static IEnumerable<string> GetVixxyBlendshapesForRenderer(GameObject root, SkinnedMeshRenderer skin) {
            if (root == null || skin == null) yield break;
            var avatar = root.GetComponent<BasisAvatar>();
            if (avatar != null && skin.sharedMesh != null) {
                if (avatar.FaceVisemeMesh == skin && avatar.FaceVisemeMovement != null) {
                    foreach (var index in avatar.FaceVisemeMovement) {
                        if (index >= 0 && index < skin.sharedMesh.blendShapeCount) yield return skin.sharedMesh.GetBlendShapeName(index);
                    }
                    if (avatar.laughterBlendTarget >= 0 && avatar.laughterBlendTarget < skin.sharedMesh.blendShapeCount) {
                        yield return skin.sharedMesh.GetBlendShapeName(avatar.laughterBlendTarget);
                    }
                }
                if (avatar.FaceBlinkMesh == skin && avatar.BlinkViseme != null) {
                    foreach (var index in avatar.BlinkViseme) {
                        if (index >= 0 && index < skin.sharedMesh.blendShapeCount) yield return skin.sharedMesh.GetBlendShapeName(index);
                    }
                }
            }
            foreach (var control in root.GetComponentsInChildren<HVRVixxyControl>(true)) {
                var subjects = BasisVrcfuryUtil.GetField(control, "subjects", Array.Empty<HVRVixxySubject>()) ?? Array.Empty<HVRVixxySubject>();
                foreach (var subject in subjects) {
                    if (subject?.properties == null || !EnumerateVixxySubjectTargets(root, subject).Contains(skin.gameObject)) continue;
                    foreach (var property in subject.properties) {
                        if (property == null || property.variant != HVRVixxyPropertyVariant.BlendShape) continue;
                        if (!string.IsNullOrWhiteSpace(property.propertyName)) yield return property.propertyName;
                    }
                }
            }
        }

        private static void AddVixxyAnimatedTransforms(GameObject root, FindAnimatedTransformsService.AnimatedTransforms animated) {
            if (root == null || animated == null) return;
            foreach (var control in root.GetComponentsInChildren<HVRVixxyControl>(true)) {
                var subjects = BasisVrcfuryUtil.GetField(control, "subjects", Array.Empty<HVRVixxySubject>()) ?? Array.Empty<HVRVixxySubject>();
                foreach (var subject in subjects) {
                    if (subject?.properties == null) continue;
                    foreach (var target in EnumerateVixxySubjectTargets(root, subject)) {
                        if (target == null) continue;
                        var transform = target.asVf();
                        foreach (var property in subject.properties) {
                            if (property == null || property.fullClassName != typeof(Transform).FullName) continue;
                            var name = property.propertyName ?? string.Empty;
                            if (name.Contains("Position", StringComparison.OrdinalIgnoreCase)) animated.positionIsAnimated.Add(transform);
                            if (name.Contains("Rotation", StringComparison.OrdinalIgnoreCase) || name.Contains("Euler", StringComparison.OrdinalIgnoreCase)) animated.rotationIsAnimated.Add(transform);
                            if (name.Contains("Scale", StringComparison.OrdinalIgnoreCase)) animated.scaleIsAnimated.Add(transform);
                            animated.AddDebugSource(transform, "Basis/Vixxy runtime control");
                        }
                    }
                }

                var activations = BasisVrcfuryUtil.GetField(control, "activations", Array.Empty<HVRVixxyActivation>()) ?? Array.Empty<HVRVixxyActivation>();
                foreach (var activation in activations) {
                    if (activation?.component is not Transform target) continue;
                    var transform = target.gameObject.asVf();
                    animated.activated.Add(transform);
                    animated.AddDebugSource(transform, "Basis/Vixxy activation");
                }
            }

            foreach (var worldLock in root.GetComponentsInChildren<HVRVixxyWorldLock>(true)) {
                var target = worldLock != null && worldLock.target != null ? worldLock.target : worldLock?.transform;
                if (target == null) continue;
                var transform = target.gameObject.asVf();
                animated.positionIsAnimated.Add(transform);
                animated.rotationIsAnimated.Add(transform);
                animated.AddDebugSource(transform, "Basis/Vixxy world lock");
            }

            AddBasisAuthoredMotionTransforms(root, animated);
        }

        private static void AddBasisAuthoredMotionTransforms(GameObject root, FindAnimatedTransformsService.AnimatedTransforms animated) {
            void Mark(Transform target, BasisAuthoredMotion.Movement.Channel channel, string source) {
                if (target == null) return;
                var transform = target.gameObject.asVf();
                switch (channel) {
                    case BasisAuthoredMotion.Movement.Channel.Position:
                        animated.positionIsAnimated.Add(transform);
                        break;
                    case BasisAuthoredMotion.Movement.Channel.Scale:
                        animated.scaleIsAnimated.Add(transform);
                        break;
                    default:
                        animated.rotationIsAnimated.Add(transform);
                        break;
                }
                animated.AddDebugSource(transform, source);
            }

            foreach (var component in root.GetComponentsInChildren<BasisAuthoredMotion>(true)) {
                if (component == null || component.movements == null) continue;
                foreach (var movement in component.movements) {
                    if (movement == null || !movement.enabled) continue;
                    const string source = "Basis authored motion";
                    switch (movement.kind) {
                        case BasisAuthoredMotion.Movement.Kind.Oscillate:
                        case BasisAuthoredMotion.Movement.Kind.Noise:
                            foreach (var target in movement.chain ?? Array.Empty<Transform>()) Mark(target, movement.channel, source);
                            break;
                        case BasisAuthoredMotion.Movement.Kind.Rotate:
                            Mark(movement.target, BasisAuthoredMotion.Movement.Channel.Rotation, source);
                            break;
                        case BasisAuthoredMotion.Movement.Kind.Orbit:
                            Mark(movement.target, BasisAuthoredMotion.Movement.Channel.Position, source);
                            break;
                        case BasisAuthoredMotion.Movement.Kind.RandomSelect:
                            foreach (var option in movement.options ?? Array.Empty<BasisAuthoredMotion.Option>()) {
                                Mark(option?.target != null ? option.target : movement.selectTarget,
                                    BasisAuthoredMotion.Movement.Channel.Rotation, source);
                            }
                            break;
                        case BasisAuthoredMotion.Movement.Kind.Sequence:
                            var sequenceRoot = movement.sequenceRoot != null ? movement.sequenceRoot : root.transform;
                            foreach (var target in sequenceRoot.GetComponentsInChildren<Transform>(true)) {
                                Mark(target, BasisAuthoredMotion.Movement.Channel.Rotation, source + " sequence");
                            }
                            if (movement.sequenceTarget != null) Mark(movement.sequenceTarget, BasisAuthoredMotion.Movement.Channel.Rotation, source);
                            break;
                    }
                }
            }
        }

        private static IEnumerable<GameObject> EnumerateVixxySubjectTargets(GameObject root, HVRVixxySubject subject) {
            if (root == null || subject == null) yield break;
            var exceptions = (subject.exceptions ?? Array.Empty<GameObject>()).Where(obj => obj != null).ToHashSet();
            switch (subject.selection) {
                case HVRVixxySelection.Normal:
                    foreach (var target in subject.targets ?? Array.Empty<GameObject>()) {
                        if (target != null) yield return target;
                    }
                    yield break;
                case HVRVixxySelection.RecursiveSearch:
                    foreach (var parent in subject.childrenOf ?? Array.Empty<GameObject>()) {
                        if (parent == null) continue;
                        foreach (var transform in parent.GetComponentsInChildren<Transform>(true)) {
                            if (transform != null && !exceptions.Contains(transform.gameObject)) yield return transform.gameObject;
                        }
                    }
                    yield break;
                case HVRVixxySelection.Everything:
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true)) {
                        if (transform != null && !exceptions.Contains(transform.gameObject)) yield return transform.gameObject;
                    }
                    yield break;
                default:
                    yield break;
            }
        }

        private sealed class BasisFaceMetadataSnapshot {
            public SkinnedMeshRenderer VisemeMesh;
            public string[] VisemeNames;
            public string LaughterName;
            public SkinnedMeshRenderer BlinkMesh;
            public string[] BlinkNames;
        }

        private static BasisFaceMetadataSnapshot CaptureBasisFaceMetadata(BasisAvatar avatar) {
            if (avatar == null) return null;
            string ResolveName(SkinnedMeshRenderer renderer, int index) {
                var mesh = renderer != null ? renderer.sharedMesh : null;
                return mesh != null && index >= 0 && index < mesh.blendShapeCount ? mesh.GetBlendShapeName(index) : null;
            }
            return new BasisFaceMetadataSnapshot {
                VisemeMesh = avatar.FaceVisemeMesh,
                VisemeNames = (avatar.FaceVisemeMovement ?? Array.Empty<int>()).Select(index => ResolveName(avatar.FaceVisemeMesh, index)).ToArray(),
                LaughterName = ResolveName(avatar.FaceVisemeMesh, avatar.laughterBlendTarget),
                BlinkMesh = avatar.FaceBlinkMesh,
                BlinkNames = (avatar.BlinkViseme ?? Array.Empty<int>()).Select(index => ResolveName(avatar.FaceBlinkMesh, index)).ToArray()
            };
        }

        private static void RestoreBasisFaceMetadata(BasisAvatar avatar, BasisFaceMetadataSnapshot snapshot) {
            if (avatar == null || snapshot == null) return;
            int ResolveIndex(SkinnedMeshRenderer renderer, string name) {
                return renderer != null && renderer.sharedMesh != null && !string.IsNullOrWhiteSpace(name)
                    ? renderer.sharedMesh.GetBlendShapeIndex(name)
                    : -1;
            }
            if (snapshot.VisemeMesh != null) {
                avatar.FaceVisemeMesh = snapshot.VisemeMesh;
                avatar.FaceVisemeMovement = snapshot.VisemeNames.Select(name => ResolveIndex(snapshot.VisemeMesh, name)).ToArray();
                avatar.laughterBlendTarget = ResolveIndex(snapshot.VisemeMesh, snapshot.LaughterName);
            }
            if (snapshot.BlinkMesh != null) {
                avatar.FaceBlinkMesh = snapshot.BlinkMesh;
                avatar.BlinkViseme = snapshot.BlinkNames.Select(name => ResolveIndex(snapshot.BlinkMesh, name)).ToArray();
            }
            EditorUtility.SetDirty(avatar);
        }

        private static void UpgradeCloneVrcfury(GameObject root) {
            foreach (var component in root.GetComponentsInChildren<VRCFuryComponent>(true).ToArray()) {
                if (component != null) component.Upgrade();
            }
        }

        private static void SuppressDeferredSpsAndHaptics(GameObject root, BasisVrcfuryConversionReport report) {
            foreach (var fury in root.GetComponentsInChildren<VRCFury>(true).ToArray()) {
                if (fury == null || fury.content == null) continue;
                if (!BasisVrcfuryConverter.IsSpsOrHapticFeature(fury.content)) continue;
                report.DeferredFeature(fury.content.GetType().Name);
                UnityEngine.Object.DestroyImmediate(fury);
            }

            foreach (var component in root.GetComponentsInChildren<VRCFuryComponent>(true).ToArray()) {
                if (component == null || component is VRCFury) continue;
                var name = component.GetType().Name;
                if (name.Contains("Haptic", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Sps", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("GlobalCollider", StringComparison.OrdinalIgnoreCase)) {
                    report.DeferredFeature(name);
                    UnityEngine.Object.DestroyImmediate(component);
                }
            }
        }

        private static BasisAvatar ResolveOrCreateBasisAvatar(GameObject selected, out bool created) {
            created = false;
            var existing = selected.GetComponentInParent<BasisAvatar>(true) ?? selected.GetComponent<BasisAvatar>();
            if (existing != null) return existing;

            VRCAvatarDescriptor descriptor = null;
            for (var t = selected.transform; t != null; t = t.parent) {
                descriptor = t.GetComponent<VRCAvatarDescriptor>();
                if (descriptor != null) break;
            }
            if (descriptor == null) {
                descriptor = selected.transform.root.GetComponentInChildren<VRCAvatarDescriptor>(true);
            }
            if (descriptor == null) return null;

            var avatar = Undo.AddComponent<BasisAvatar>(descriptor.gameObject);
            created = true;
            return avatar;
        }

        private static void RefreshBasisAvatarMetadata(BasisAvatar avatar, VRCAvatarDescriptor descriptor, bool overwriteExisting) {
            if (avatar == null) return;
            if (avatar.Animator == null) avatar.Animator = avatar.GetComponent<Animator>();
            if (avatar.Renders == null || avatar.Renders.Length == 0 || overwriteExisting) {
                avatar.Renders = avatar.GetComponentsInChildren<Renderer>(true);
            }
            if (avatar.Animator != null && avatar.Animator.isHuman) {
                if (overwriteExisting || avatar.TransformStorage == null || !avatar.TransformStorage.HasData) {
                    avatar.TransformStorage = BasisAvatarTransformStorage.CaptureFrom(avatar.Animator);
                }
                if (overwriteExisting || avatar.HumanScale <= 0f) avatar.HumanScale = avatar.Animator.humanScale;
            }
            if (descriptor == null) {
                EditorUtility.SetDirty(avatar);
                return;
            }

            if (overwriteExisting || avatar.AvatarEyePosition == Vector2.zero) {
                var view = descriptor.ViewPosition;
                avatar.AvatarEyePosition = new Vector2(view.y, view.z);
            }

            if (descriptor.VisemeSkinnedMesh != null && (overwriteExisting || avatar.FaceVisemeMesh == null)) {
                avatar.FaceVisemeMesh = descriptor.VisemeSkinnedMesh;
            }
            if (avatar.FaceVisemeMesh != null && avatar.FaceVisemeMesh.sharedMesh != null && descriptor.VisemeBlendShapes != null) {
                if (overwriteExisting || avatar.FaceVisemeMovement == null || avatar.FaceVisemeMovement.Length != 15 || avatar.FaceVisemeMovement.All(i => i < 0)) {
                    avatar.FaceVisemeMovement = Enumerable.Repeat(-1, 15).ToArray();
                    for (var i = 0; i < Math.Min(15, descriptor.VisemeBlendShapes.Length); i++) {
                        var name = descriptor.VisemeBlendShapes[i];
                        if (!string.IsNullOrWhiteSpace(name)) avatar.FaceVisemeMovement[i] = avatar.FaceVisemeMesh.sharedMesh.GetBlendShapeIndex(name);
                    }
                }
            }

            // Use reflection for eyelid metadata because the field names have changed across supported VRCSDK versions.
            if (BasisVrcfuryUtil.TryReadMember(descriptor, "eyelidsSkinnedMesh", out SkinnedMeshRenderer blinkMesh) && blinkMesh != null &&
                BasisVrcfuryUtil.TryReadMember(descriptor, "eyelidsBlendshapes", out int[] blinkShapes) && blinkShapes != null && blinkShapes.Length > 0) {
                if (overwriteExisting || avatar.FaceBlinkMesh == null) avatar.FaceBlinkMesh = blinkMesh;
                if (overwriteExisting || avatar.BlinkViseme == null || avatar.BlinkViseme.All(i => i < 0)) avatar.BlinkViseme = (int[])blinkShapes.Clone();
            }
            EditorUtility.SetDirty(avatar);
        }

        private static void TranslateHeadChopComponents(GameObject root, BasisVrcfuryConversionReport report) {
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true).ToArray()) {
                if (behaviour == null || behaviour.GetType().FullName != "VRC.SDK3.Avatars.Components.VRCHeadChop") continue;
                if (!BasisVrcfuryUtil.TryReadMember(behaviour, "targetBones", out Array targetBones) || targetBones == null) continue;

                var targets = new List<BasisHeadChop.HeadChopTarget>();
                foreach (var entry in targetBones) {
                    if (entry == null || !BasisVrcfuryUtil.TryReadMember(entry, "transform", out Transform target) || target == null) continue;
                    var scale = 0f;
                    BasisVrcfuryUtil.TryReadMember(entry, "scaleFactor", out scale);
                    targets.Add(new BasisHeadChop.HeadChopTarget { Target = target, Scale = scale });
                }
                if (targets.Count == 0) continue;
                var basis = behaviour.GetComponent<BasisHeadChop>() ?? behaviour.gameObject.AddComponent<BasisHeadChop>();
                basis.Targets = (basis.Targets ?? Array.Empty<BasisHeadChop.HeadChopTarget>()).Concat(targets).ToArray();
                report.ConvertedBindings += targets.Count;
            }
        }

        private static void StripVrcfuryAndVrchatRuntimeComponents(GameObject root) {
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true).ToArray()) {
                if (behaviour == null || behaviour is BasisAvatar) continue;
                var type = behaviour.GetType();
                var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
                var ns = type.Namespace ?? string.Empty;
                if (assemblyName == "VRCFury" || ns.StartsWith("VRC.", StringComparison.Ordinal) || ns.StartsWith("VRCSDK", StringComparison.Ordinal)) {
                    UnityEngine.Object.DestroyImmediate(behaviour);
                }
            }
        }

        private static void LogReport(GameObject avatar, BasisVrcfuryConversionReport report) {
            Debug.Log(report.Summary(avatar), avatar);
            foreach (var warning in report.Warnings) Debug.LogWarning("VRCFury BasisVR: " + warning, avatar);
        }
    }
}
