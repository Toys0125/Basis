using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using HVR.Vixxy;
using UnityEditor;
using UnityEngine;
using VF.Component;
using VF.Model;
using VF.Model.Feature;

namespace VF.Integration.Basis.Shim {
    /// <summary>
    /// Basis-only VRCFury backend. This assembly exists only when Basis + Vixxy are installed and
    /// the real VRChat avatar SDK is absent. It intentionally implements the build-time features
    /// which have direct Basis equivalents instead of fabricating the complete VRC SDK API.
    /// </summary>
    [InitializeOnLoad]
    internal static class BasisVrcfuryAutoShim {
        private static bool running;
        private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static BasisVrcfuryAutoShim() {
            BasisAssetBundlePipeline.OnBeforeBuildPrefab -= OnBeforeBuildPrefab;
            BasisAssetBundlePipeline.OnBeforeBuildPrefab += OnBeforeBuildPrefab;
        }

        private static void OnBeforeBuildPrefab(GameObject buildRoot, BasisAssetBundleObject settings) {
            ProcessBuildClone(buildRoot, settings);
        }

        internal static void ProcessBuildClone(GameObject buildRoot, BasisAssetBundleObject settings) {
            if (running || buildRoot == null) return;
            var avatar = buildRoot.GetComponent<BasisAvatar>();
            if (avatar == null) return;

            var vrcfury = buildRoot.GetComponentsInChildren<VRCFury>(true);
            if (vrcfury.Length == 0) return;

            running = true;
            try {
                var features = ExpandFeatures(vrcfury).ToArray();

                // Armature Link must run before mesh optimization, exactly as in VRCFury's normal build order.
                foreach (var feature in features.OfType<ArmatureLink>()) {
                    BasisArmatureLinkShim.Apply(buildRoot, avatar, settings, feature);
                }

                if (features.Any(feature => feature is BlendshapeOptimizer)) {
                    Action<Dictionary<SkinnedMeshRenderer, HashSet<string>>> collect = requirements =>
                        CollectVixxyBlendshapeRequirements(buildRoot, requirements);
                    BasisBlendshapeBuildHooks.OnCollectRequirements += collect;
                    try {
                        BasisBuildBlendshapeStripper.StripForBuild(settings, buildRoot, avatar);
                    } finally {
                        BasisBlendshapeBuildHooks.OnCollectRequirements -= collect;
                    }
                }

                // VRCFury is authoring/build metadata in a Basis-only project. Never let it leak into the bundle.
                foreach (var component in buildRoot.GetComponentsInChildren<VRCFuryComponent>(true)) {
                    if (component != null) UnityEngine.Object.DestroyImmediate(component);
                }
            } finally {
                running = false;
            }
        }

        private static IEnumerable<FeatureModel> ExpandFeatures(IEnumerable<VRCFury> components) {
            foreach (var component in components) {
                if (component == null) continue;
                foreach (var feature in component.GetAllFeatures()) {
                    foreach (var migrated in ExpandFeature(feature, component.gameObject, 0)) yield return migrated;
                }
            }
        }

        private static IEnumerable<FeatureModel> ExpandFeature(FeatureModel feature, GameObject source, int depth) {
            if (feature == null || depth > 16) yield break;
            IList<FeatureModel> migrated;
            try {
                migrated = feature.Migrate(new FeatureModel.MigrateRequest {
                    fakeUpgrade = true,
                    gameObject = source
                });
            } catch {
                migrated = new[] { feature };
            }

            if (migrated == null || migrated.Count == 0) yield break;
            foreach (var item in migrated) {
                if (item == null) continue;
                if (ReferenceEquals(item, feature)) {
                    yield return item;
                } else {
                    foreach (var nested in ExpandFeature(item, source, depth + 1)) yield return nested;
                }
            }
        }

        private static void CollectVixxyBlendshapeRequirements(
            GameObject root,
            Dictionary<SkinnedMeshRenderer, HashSet<string>> requirements
        ) {
            foreach (var control in root.GetComponentsInChildren<HVRVixxyControl>(true)) {
                if (control == null) continue;
                var field = typeof(HVRVixxyControl).GetField("subjects", Fields);
                if (field?.GetValue(control) is not HVRVixxySubject[] subjects) continue;
                foreach (var subject in subjects) {
                    if (subject?.properties == null) continue;
                    var names = subject.properties
                        .Where(property => property != null && property.variant == HVRVixxyPropertyVariant.BlendShape)
                        .Select(property => property.propertyName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct()
                        .ToArray();
                    if (names.Length == 0) continue;

                    foreach (var target in ExpandSubjectTargets(root.transform, subject)) {
                        if (target == null || !target.TryGetComponent<SkinnedMeshRenderer>(out var smr) || smr.sharedMesh == null) continue;
                        if (!requirements.TryGetValue(smr, out var set)) {
                            set = new HashSet<string>();
                            requirements[smr] = set;
                        }
                        foreach (var name in names) {
                            if (smr.sharedMesh.GetBlendShapeIndex(name) >= 0) set.Add(name);
                        }
                    }
                }
            }
        }

        private static IEnumerable<GameObject> ExpandSubjectTargets(Transform root, HVRVixxySubject subject) {
            var exceptions = new HashSet<GameObject>(subject.exceptions ?? Array.Empty<GameObject>());
            switch (subject.selection) {
                case HVRVixxySelection.Normal:
                    foreach (var target in subject.targets ?? Array.Empty<GameObject>()) {
                        if (target != null && !exceptions.Contains(target)) yield return target;
                    }
                    break;
                case HVRVixxySelection.RecursiveSearch:
                    foreach (var parent in subject.childrenOf ?? Array.Empty<GameObject>()) {
                        if (parent == null) continue;
                        foreach (var transform in parent.GetComponentsInChildren<Transform>(true)) {
                            if (!exceptions.Contains(transform.gameObject)) yield return transform.gameObject;
                        }
                    }
                    break;
                case HVRVixxySelection.Everything:
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true)) {
                        if (!exceptions.Contains(transform.gameObject)) yield return transform.gameObject;
                    }
                    break;
            }
        }
    }

    internal static class BasisArmatureLinkShim {
        public static void Apply(GameObject root, BasisAvatar avatar, BasisAssetBundleObject settings, ArmatureLink model) {
            if (model == null || model.propBone == null) return;
            if (model.onlyIf != null && !model.onlyIf()) return;

            var source = model.propBone.transform;
            var target = ResolveTarget(root, avatar, model);
            if (target == null || source == target || target.IsChildOf(source)) {
                Debug.LogWarning($"VRCFury Basis shim could not resolve a safe Armature Link target for '{source.name}'.");
                return;
            }

            Align(source, target, model);

            if (!model.recursive) {
                source.SetParent(target, true);
                if (!string.IsNullOrWhiteSpace(model.forceMergedName)) source.name = model.forceMergedName;
                return;
            }

            var protectedTransforms = CollectRuntimeDrivenTransforms(root);
            var mapping = BuildMapping(source, target, model.removeBoneSuffix);
            RewriteSkins(root, settings, mapping, protectedTransforms);
            CollapseMappedBones(mapping, protectedTransforms);
        }

        private static Transform ResolveTarget(GameObject root, BasisAvatar avatar, ArmatureLink model) {
            foreach (var link in model.linkTo ?? new List<ArmatureLink.LinkTo>()) {
                Transform target = null;
                if (link.useObj && link.obj != null && link.obj.transform.IsChildOf(root.transform)) {
                    target = link.obj.transform;
                } else if (link.useBone && avatar.Animator != null) {
                    target = avatar.Animator.GetBoneTransform(link.bone);
                } else if (!link.useObj && !link.useBone) {
                    target = root.transform;
                }
                if (target == null) continue;
                if (!string.IsNullOrWhiteSpace(link.offset)) target = target.Find(link.offset);
                if (target != null) return target;
            }
            return null;
        }

        private static void Align(Transform source, Transform target, ArmatureLink model) {
            if (model.alignPosition) source.position = target.position;
            if (model.alignRotation) source.rotation = target.rotation;
            if (model.forceOneWorldScale) {
                SetWorldScale(source, Vector3.one);
            } else if (model.alignScale) {
                var factor = model.skinRewriteScalingFactor;
                if (model.autoScaleFactor) {
                    var avatarScale = Mathf.Abs(target.lossyScale.x);
                    var propScale = Mathf.Abs(source.lossyScale.x);
                    factor = avatarScale > 0.000001f ? propScale / avatarScale : 1f;
                    if (model.scalingFactorPowersOf10Only && factor > 0f) {
                        var log = Mathf.Log10(factor);
                        log = (log - Mathf.Floor(log)) > 0.75f ? Mathf.Ceil(log) : Mathf.Floor(log);
                        factor = Mathf.Pow(10f, log);
                    }
                }
                SetWorldScale(source, Vector3.Scale(target.lossyScale, Vector3.one * factor));
            }
        }

        private static void SetWorldScale(Transform transform, Vector3 desired) {
            if (transform.parent == null) {
                transform.localScale = desired;
                return;
            }
            var parent = transform.parent.lossyScale;
            transform.localScale = new Vector3(
                Mathf.Abs(parent.x) > 1e-7f ? desired.x / parent.x : desired.x,
                Mathf.Abs(parent.y) > 1e-7f ? desired.y / parent.y : desired.y,
                Mathf.Abs(parent.z) > 1e-7f ? desired.z / parent.z : desired.z
            );
        }

        private static Dictionary<Transform, Transform> BuildMapping(Transform sourceRoot, Transform targetRoot, string configuredDecoration) {
            var mapping = new Dictionary<Transform, Transform> { [sourceRoot] = targetRoot };
            var decoration = string.IsNullOrWhiteSpace(configuredDecoration)
                ? InferDecoration(sourceRoot.name, targetRoot.name)
                : configuredDecoration;

            var sources = sourceRoot.GetComponentsInChildren<Transform>(true)
                .Where(t => t != sourceRoot)
                .OrderBy(GetDepth)
                .ToArray();
            foreach (var source in sources) {
                if (!mapping.TryGetValue(source.parent, out var mappedParent)) continue;
                var normalized = Normalize(source.name, decoration);
                var direct = Enumerable.Range(0, mappedParent.childCount)
                    .Select(mappedParent.GetChild)
                    .FirstOrDefault(child => Normalize(child.name, decoration) == normalized && !mapping.ContainsValue(child));
                if (direct != null) {
                    mapping[source] = direct;
                    continue;
                }
                var fallback = targetRoot.GetComponentsInChildren<Transform>(true)
                    .FirstOrDefault(child => Normalize(child.name, decoration) == normalized && !mapping.ContainsValue(child));
                if (fallback != null) mapping[source] = fallback;
            }
            return mapping;
        }

        private static string InferDecoration(string source, string target) {
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (source.StartsWith(target, StringComparison.OrdinalIgnoreCase)) return source.Substring(target.Length);
            if (source.EndsWith(target, StringComparison.OrdinalIgnoreCase)) return source.Substring(0, source.Length - target.Length);
            return string.Empty;
        }

        private static string Normalize(string name, string decoration) {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            if (!string.IsNullOrEmpty(decoration)) name = name.Replace(decoration, string.Empty);
            return name.Trim().ToLowerInvariant();
        }

        private static int GetDepth(Transform t) {
            var depth = 0;
            while (t.parent != null) { depth++; t = t.parent; }
            return depth;
        }

        private static HashSet<Transform> CollectRuntimeDrivenTransforms(GameObject root) {
            var output = new HashSet<Transform>();

            foreach (var control in root.GetComponentsInChildren<HVRVixxyControl>(true)) {
                var field = typeof(HVRVixxyControl).GetField("subjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field?.GetValue(control) is not HVRVixxySubject[] subjects) continue;
                foreach (var subject in subjects) {
                    if (subject?.properties == null || !subject.properties.Any(IsTransformProperty)) continue;
                    foreach (var target in ExpandTargets(root.transform, subject)) if (target != null) output.Add(target.transform);
                }
            }

            foreach (var motion in root.GetComponentsInChildren<BasisAuthoredMotion>(true)) {
                foreach (var movement in motion.movements ?? Array.Empty<BasisAuthoredMotion.Movement>()) {
                    if (movement == null || !movement.enabled) continue;
                    if (movement.target != null) output.Add(movement.target);
                    if (movement.pivot != null) output.Add(movement.pivot);
                    if (movement.selectTarget != null) output.Add(movement.selectTarget);
                    if (movement.sequenceTarget != null) output.Add(movement.sequenceTarget);
                    if (movement.sequenceRoot != null) output.Add(movement.sequenceRoot);
                    foreach (var t in movement.chain ?? Array.Empty<Transform>()) if (t != null) output.Add(t);
                    foreach (var option in movement.options ?? Array.Empty<BasisAuthoredMotion.Option>()) if (option?.target != null) output.Add(option.target);
                }
            }
            return output;
        }

        private static bool IsTransformProperty(HVRVixxyPropertyBase property) {
            if (property == null || property.variant != HVRVixxyPropertyVariant.Standard) return false;
            if (property.fullClassName != typeof(Transform).FullName) return false;
            return property.propertyName == "localPosition" || property.propertyName == "position" ||
                   property.propertyName == "localRotation" || property.propertyName == "rotation" ||
                   property.propertyName == "localScale";
        }

        private static IEnumerable<GameObject> ExpandTargets(Transform root, HVRVixxySubject subject) {
            var exceptions = new HashSet<GameObject>(subject.exceptions ?? Array.Empty<GameObject>());
            if (subject.selection == HVRVixxySelection.Normal) {
                foreach (var target in subject.targets ?? Array.Empty<GameObject>()) if (target != null && !exceptions.Contains(target)) yield return target;
            } else if (subject.selection == HVRVixxySelection.RecursiveSearch) {
                foreach (var parent in subject.childrenOf ?? Array.Empty<GameObject>()) {
                    if (parent == null) continue;
                    foreach (var t in parent.GetComponentsInChildren<Transform>(true)) if (!exceptions.Contains(t.gameObject)) yield return t.gameObject;
                }
            } else if (subject.selection == HVRVixxySelection.Everything) {
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (!exceptions.Contains(t.gameObject)) yield return t.gameObject;
            }
        }

        private static void RewriteSkins(
            GameObject root,
            BasisAssetBundleObject settings,
            Dictionary<Transform, Transform> mapping,
            HashSet<Transform> protectedTransforms
        ) {
            TemporaryStorageHandler.EnsureDirectoryExists(settings.TemporaryStorage);
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
                var oldMesh = skin.sharedMesh;
                if (oldMesh == null || skin.bones == null || skin.bones.Length == 0) continue;

                var oldBones = skin.bones;
                var newBones = (Transform[])oldBones.Clone();
                var bindposes = oldMesh.bindposes;
                var newBindposes = (Matrix4x4[])bindposes.Clone();
                var changed = false;
                for (var i = 0; i < oldBones.Length && i < newBindposes.Length; i++) {
                    var from = oldBones[i];
                    if (from == null || protectedTransforms.Contains(from) || !mapping.TryGetValue(from, out var to) || to == null) continue;
                    newBindposes[i] = to.worldToLocalMatrix * from.localToWorldMatrix * bindposes[i];
                    newBones[i] = to;
                    changed = true;
                }
                if (!changed) continue;

                var mesh = UnityEngine.Object.Instantiate(oldMesh);
                mesh.name = oldMesh.name + " (VRCFury Armature Link)";
                mesh.bindposes = newBindposes;
                var path = AssetDatabase.GenerateUniqueAssetPath($"{settings.TemporaryStorage}/{Sanitize(mesh.name)}.asset");
                AssetDatabase.CreateAsset(mesh, path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                skin.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                skin.bones = newBones;
                EditorUtility.SetDirty(skin);
            }
        }

        private static string Sanitize(string name) {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "VRCFuryMesh" : name;
        }

        private static void CollapseMappedBones(Dictionary<Transform, Transform> mapping, HashSet<Transform> protectedTransforms) {
            foreach (var pair in mapping.OrderByDescending(pair => GetDepth(pair.Key)).ToArray()) {
                var source = pair.Key;
                var target = pair.Value;
                if (source == null || target == null || protectedTransforms.Contains(source)) continue;

                var extraComponents = source.GetComponents<UnityEngine.Component>().Where(component => component != null && component is not Transform).ToArray();
                if (extraComponents.Length > 0) {
                    source.SetParent(target, true);
                    continue;
                }

                var children = Enumerable.Range(0, source.childCount).Select(source.GetChild).ToArray();
                foreach (var child in children) {
                    if (mapping.ContainsKey(child)) continue;
                    child.SetParent(target, true);
                }
                UnityEngine.Object.DestroyImmediate(source.gameObject);
            }
        }
    }
}
