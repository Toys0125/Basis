using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Basis.Scripts.BasisSdk;
using GatorDragonGames.JigglePhysics;
using HVR.Basis.Comms;
using HVR.Vixxy;
using UnityEditor;
using UnityEngine;
using VF.Model;
using VF.Model.StateAction;
using VF.Utils;
using ActionModel = VF.Model.StateAction.Action;

namespace VF.Integration.Basis {
    internal sealed class BasisVrcfuryControlData {
        public readonly List<HVRVixxyActivation> Activations = new();
        public readonly List<HVRVixxySubject> Subjects = new();
        public readonly List<HVRVixxyAddressDrive> AddressDrives = new();
    }

    internal static class BasisVrcfuryActionConverter {
        private static readonly Regex MaterialSlotBinding = new(@"^m_Materials\.Array\.data\[(\d+)\]$", RegexOptions.Compiled);

        public static void ConvertState(
            BasisAvatar avatar,
            GameObject sourceObject,
            GameObject generatedHost,
            State state,
            BasisVrcfuryControlData output,
            BasisVrcfuryConversionReport report,
            string label,
            HVRVixxyLocality locality = HVRVixxyLocality.Both,
            bool includeUnscoped = true
        ) {
            if (state?.actions == null) return;
            foreach (var action in state.actions) {
                ConvertAction(avatar, sourceObject, generatedHost, action, output, report, label, locality, includeUnscoped);
            }
        }

        private static void ConvertAction(
            BasisAvatar avatar,
            GameObject sourceObject,
            GameObject generatedHost,
            ActionModel action,
            BasisVrcfuryControlData output,
            BasisVrcfuryConversionReport report,
            string label,
            HVRVixxyLocality locality,
            bool includeUnscoped
        ) {
            if (action == null) return;
            if (action.localOnly && locality != HVRVixxyLocality.WearerOnly) return;
            if (action.remoteOnly && locality != HVRVixxyLocality.RemoteOnly) return;
            if (!action.localOnly && !action.remoteOnly && !includeUnscoped) return;

            if (action.desktopActive || action.androidActive) {
                report.Warn($"'{label}' contains platform-filtered {action.GetType().Name}; Basis currently applies the converted action on every supported platform.");
            }
            switch (action) {
                case ObjectToggleAction model:
                    ConvertObjectToggle(model, output);
                    return;
                case BlendShapeAction model:
                    ConvertBlendShape(avatar, model, output, report, label);
                    return;
                case ScaleAction model:
                    ConvertScale(model, output);
                    return;
                case MaterialPropertyAction model:
                    ConvertMaterialProperty(avatar, model, output, report, label);
                    return;
                case MaterialAction model:
                    ConvertMaterialSwap(model, output, report, label);
                    return;
                case PoiyomiUVTileAction model:
                    ConvertPoiyomiUvTile(model, output, report, label);
                    return;
                case FlipbookAction model:
                    ConvertPoiyomiFlipbook(model, output, report, label);
                    return;
                case ShaderInventoryAction model:
                    ConvertShaderInventory(model, output, report, label);
                    return;
                case FxFloatAction model:
                    ConvertParameterDrive(model, output);
                    return;
                case AnimationClipAction model:
                    ConvertAnimationClip(sourceObject, generatedHost, model, output, report, label);
                    return;
                case WorldDropAction model:
                    ConvertWorldDrop(model, generatedHost, output);
                    return;
                case ResetPhysboneAction model:
                    ConvertResetPhysbone(avatar, model, generatedHost, output, report, label);
                    return;
                case FlipBookBuilderAction model:
                    report.UnsupportedAction(model.GetType().Name + " (handled only by slider preset conversion)");
                    return;
                case SmoothLoopAction model:
                    ConvertSmoothLoop(avatar, sourceObject, generatedHost, model, output, report, label);
                    return;
                case BlockBlinkingAction:
                    ConvertFaceBlocker(generatedHost, HVRVixxyFaceBlockTarget.Blinking, output);
                    return;
                case BlockVisemesAction:
                    ConvertFaceBlocker(generatedHost, HVRVixxyFaceBlockTarget.Visemes, output);
                    return;
                case DisableGesturesAction:
                    report.UnsupportedAction(action.GetType().Name + " (Basis native hand-control suppression mapping pending)");
                    return;
                case SpsOnAction:
                case ChangeSpsTagAction:
                    report.DeferredAction(action.GetType().Name);
                    return;
                default:
                    report.UnsupportedAction(action.GetType().Name);
                    return;
            }
        }

        private static void ConvertObjectToggle(ObjectToggleAction model, BasisVrcfuryControlData output) {
            if (model.obj == null) return;
            var on = model.mode switch {
                ObjectToggleAction.Mode.TurnOff => false,
                ObjectToggleAction.Mode.Toggle => !model.obj.activeSelf,
                _ => true
            };
            output.Activations.Add(new HVRVixxyActivation {
                component = model.obj.transform,
                threshold = ActivationThreshold.Blended,
                choices = new[] { !on, on }
            });
        }

        private static void ConvertBlendShape(
            BasisAvatar avatar,
            BlendShapeAction model,
            BasisVrcfuryControlData output,
            BasisVrcfuryConversionReport report,
            string label
        ) {
            if (string.IsNullOrWhiteSpace(model.blendShape)) return;
            IEnumerable<SkinnedMeshRenderer> renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (!model.allRenderers) {
                renderers = model.renderer is SkinnedMeshRenderer selected
                    ? new[] { selected }
                    : Array.Empty<SkinnedMeshRenderer>();
            }

            var found = false;
            foreach (var renderer in renderers.Distinct()) {
                if (renderer == null || renderer.sharedMesh == null) continue;
                var index = renderer.sharedMesh.GetBlendShapeIndex(model.blendShape);
                if (index < 0) continue;
                found = true;
                output.Subjects.Add(BasisVrcfuryUtil.Subject(renderer.gameObject, new HVRVixxyPropertyFloat {
                    fullClassName = typeof(SkinnedMeshRenderer).FullName,
                    variant = HVRVixxyPropertyVariant.BlendShape,
                    propertyName = model.blendShape,
                    choices = new[] { renderer.GetBlendShapeWeight(index), model.blendShapeValue }
                }));
            }
            if (!found) report.Warn($"'{label}' references blendshape '{model.blendShape}', but no matching Basis renderer was found.");
        }

        private static void ConvertScale(ScaleAction model, BasisVrcfuryControlData output) {
            if (model.obj == null) return;
            var initial = model.obj.transform.localScale;
            output.Subjects.Add(BasisVrcfuryUtil.Subject(model.obj, new HVRVixxyPropertyVector3 {
                fullClassName = typeof(Transform).FullName,
                variant = HVRVixxyPropertyVariant.Standard,
                propertyName = "localScale",
                choices = new[] { initial, initial * model.scale }
            }));
        }

        private static void ConvertMaterialProperty(
            BasisAvatar avatar,
            MaterialPropertyAction model,
            BasisVrcfuryControlData output,
            BasisVrcfuryConversionReport report,
            string label
        ) {
            if (string.IsNullOrWhiteSpace(model.propertyName) || model.propertyName.Contains('.')) return;
            IEnumerable<Renderer> renderers;
            if (model.affectAllMeshes) {
                renderers = avatar.GetComponentsInChildren<Renderer>(true);
            } else {
                var renderer = model.renderer2 != null ? model.renderer2.GetComponent<Renderer>() : null;
                renderers = renderer != null ? new[] { renderer } : Array.Empty<Renderer>();
            }

            var converted = 0;
            foreach (var renderer in renderers.Distinct()) {
                if (!IsVixxyRenderer(renderer)) continue;
                var material = FindMaterial(renderer, model.propertyName);
                if (material == null) continue;
                var propertyType = model.propertyType == MaterialPropertyAction.Type.LegacyAuto
                    ? DetectMaterialType(material.shader, model.propertyName)
                    : model.propertyType;

                HVRVixxyPropertyBase property = propertyType switch {
                    MaterialPropertyAction.Type.Float => new HVRVixxyPropertyFloat {
                        choices = new[] { material.GetFloat(model.propertyName), model.value }
                    },
                    MaterialPropertyAction.Type.Color => new HVRVixxyPropertyColorHDR {
                        choices = new[] { material.GetColor(model.propertyName), model.valueColor }
                    },
                    MaterialPropertyAction.Type.Vector or MaterialPropertyAction.Type.St => new HVRVixxyPropertyVector4 {
                        choices = new[] { material.GetVector(model.propertyName), model.valueVector }
                    },
                    _ => null
                };
                if (property == null) continue;
                property.fullClassName = renderer.GetType().FullName;
                property.variant = HVRVixxyPropertyVariant.MaterialProperty;
                property.propertyName = model.propertyName;
                output.Subjects.Add(BasisVrcfuryUtil.Subject(renderer.gameObject, property));
                converted++;
            }
            if (converted == 0) report.Warn($"'{label}' material property '{model.propertyName}' could not be mapped to a Vixxy renderer/property.");
        }

        private static void ConvertMaterialSwap(
            MaterialAction model,
            BasisVrcfuryControlData output,
            BasisVrcfuryConversionReport report,
            string label
        ) {
            if (model.renderer == null || model.materialIndex < 0 || model.materialIndex >= model.renderer.sharedMaterials.Length) return;
            var active = BasisVrcfuryUtil.ResolveAsset<Material>(model.mat);
            if (active == null) {
                report.Warn($"'{label}' has a material swap with a missing material asset.");
                return;
            }
            output.Subjects.Add(BasisVrcfuryUtil.Subject(model.renderer.gameObject, new HVRVixxyPropertyMaterialSlot {
                fullClassName = model.renderer.GetType().FullName,
                variant = HVRVixxyPropertyVariant.RendererMaterialSlot,
                propertyName = "materialSlot",
                slot = model.materialIndex,
                choices = new[] { model.renderer.sharedMaterials[model.materialIndex], active }
            }));
        }

        private static void ConvertPoiyomiUvTile(
            PoiyomiUVTileAction model,
            BasisVrcfuryControlData output,
            BasisVrcfuryConversionReport report,
            string label
        ) {
            if (model.renderer == null) return;
            if (model.row < 0 || model.row > 3 || model.column < 0 || model.column > 3) {
                report.Warn($"'{label}' has an invalid Poiyomi UV tile ({model.row}, {model.column}).");
                return;
            }
            var propertyName = (model.dissolve ? "_UVTileDissolveAlpha_Row" : "_UDIMDiscardRow") + $"{model.row}_{model.column}";
            if (!string.IsNullOrEmpty(model.renamedMaterial)) propertyName += "_" + model.renamedMaterial;
            AddShaderFloat(model.renderer, propertyName, 1f, 0f, output, report, label);
        }

        private static void ConvertPoiyomiFlipbook(
            FlipbookAction model,
            BasisVrcfuryControlData output,
            BasisVrcfuryConversionReport report,
            string label
        ) {
            if (model.renderer == null) return;
            var material = FindMaterial(model.renderer, "_FlipbookCurrentFrame");
            if (material == null) {
                report.Warn($"'{label}' flipbook action could not find _FlipbookCurrentFrame.");
                return;
            }
            AddShaderFloat(model.renderer, "_FlipbookCurrentFrame", material.GetFloat("_FlipbookCurrentFrame"),
                Mathf.Floor(model.frame) + 0.5f, output, report, label);
        }

        private static void ConvertShaderInventory(
            ShaderInventoryAction model,
            BasisVrcfuryControlData output,
            BasisVrcfuryConversionReport report,
            string label
        ) {
            if (model.renderer == null) return;
            AddShaderFloat(model.renderer, $"_InventoryItem{model.slot:D2}Animated", 0f, 1f, output, report, label);
        }

        private static void AddShaderFloat(
            Renderer renderer,
            string propertyName,
            float inactive,
            float active,
            BasisVrcfuryControlData output,
            BasisVrcfuryConversionReport report,
            string label
        ) {
            if (!IsVixxyRenderer(renderer) || FindMaterial(renderer, propertyName) == null) {
                report.Warn($"'{label}' shader property '{propertyName}' was not found on {renderer.name}.");
                return;
            }
            output.Subjects.Add(BasisVrcfuryUtil.Subject(renderer.gameObject, new HVRVixxyPropertyFloat {
                fullClassName = renderer.GetType().FullName,
                variant = HVRVixxyPropertyVariant.MaterialProperty,
                propertyName = propertyName,
                choices = new[] { inactive, active }
            }));
        }

        private static void ConvertParameterDrive(FxFloatAction model, BasisVrcfuryControlData output) {
            if (string.IsNullOrWhiteSpace(model.name)) return;
            output.AddressDrives.Add(new HVRVixxyAddressDrive {
                address = new HVRAddressSelector { path = NormalizeAddress(model.name) },
                choices = new[] { 0f, model.value },
                applyChoices = new[] { false, true },
                interpolate = false
            });
        }

        private static void ConvertFaceBlocker(GameObject generatedHost, HVRVixxyFaceBlockTarget target, BasisVrcfuryControlData output) {
            var blocker = generatedHost.AddComponent<HVRVixxyFaceBlocker>();
            blocker.target = target;
            output.Subjects.Add(BasisVrcfuryUtil.Subject(blocker.gameObject, new HVRVixxyPropertyBool {
                fullClassName = typeof(HVRVixxyFaceBlocker).FullName,
                variant = HVRVixxyPropertyVariant.Standard,
                propertyName = nameof(HVRVixxyFaceBlocker.Active),
                choices = new[] { false, true }
            }));
        }

        private static void ConvertSmoothLoop(
            BasisAvatar avatar,
            GameObject sourceObject,
            GameObject generatedHost,
            SmoothLoopAction model,
            BasisVrcfuryControlData output,
            BasisVrcfuryConversionReport report,
            string label
        ) {
            if (model.state1 == null || model.state2 == null) return;

            var oscillator = generatedHost.AddComponent<HVRVixxyOscillator>();
            oscillator.loopTime = model.loopTime > 0f ? model.loopTime : 0.01f;
            oscillator.outputAddress = HVRAddress.GenerateAddressFromPath(oscillator, avatar.transform) + "/Loop";
            oscillator.enabled = false;

            var stateA = new BasisVrcfuryControlData();
            var stateB = new BasisVrcfuryControlData();
            ConvertState(avatar, sourceObject, generatedHost, model.state1, stateA, report, label + " loop A");
            ConvertState(avatar, sourceObject, generatedHost, model.state2, stateB, report, label + " loop B");

            var choices = new[] {
                new BasisVrcfuryMultiChoice.ChoiceState(-1f, "Rest", new BasisVrcfuryControlData()),
                new BasisVrcfuryMultiChoice.ChoiceState(0f, "A", stateA),
                new BasisVrcfuryMultiChoice.ChoiceState(1f, "B", stateB)
            };
            if (!BasisVrcfuryMultiChoice.TryMerge(choices, out var mergedChoices, out var merged, report, label + " smooth loop")) {
                UnityEngine.Object.DestroyImmediate(oscillator);
                return;
            }

            BasisVrcfuryConverter.CreateControl(
                generatedHost,
                oscillator.outputAddress,
                mergedChoices,
                -1f,
                merged,
                HVRVixxyLocality.Both,
                networked: false,
                simplifiedTransition: false,
                transitionIn: 0f,
                transitionOut: 0f);

            output.Activations.Add(new HVRVixxyActivation {
                component = oscillator,
                threshold = ActivationThreshold.Blended,
                choices = new[] { false, true }
            });
            report.GeneratedControls++;
            report.ConvertedBindings += merged.Activations.Count + merged.Subjects.Count + merged.AddressDrives.Count;
        }

        private static void ConvertWorldDrop(WorldDropAction model, GameObject generatedHost, BasisVrcfuryControlData output) {
            if (model.obj == null) return;
            var worldLock = generatedHost.AddComponent<HVRVixxyWorldLock>();
            worldLock.target = model.obj.transform;
            worldLock.enabled = false;
            output.Activations.Add(new HVRVixxyActivation {
                component = worldLock,
                threshold = ActivationThreshold.Strict,
                choices = new[] { false, true }
            });
        }

        private static void ConvertResetPhysbone(
            BasisAvatar avatar,
            ResetPhysboneAction model,
            GameObject generatedHost,
            BasisVrcfuryControlData output,
            BasisVrcfuryConversionReport report,
            string label
        ) {
            if (model.physBone == null) return;
            var root = model.physBone.GetRootTransform();
            var rig = avatar.GetComponentsInChildren<JiggleRig>(true)
                .FirstOrDefault(candidate => candidate != null && candidate.GetJiggleRigData().rootBone == root);
            if (rig == null) {
                report.Warn($"'{label}' resets PhysBone '{model.physBone.name}', but no Basis JiggleRig with the same root bone was found.");
                return;
            }
            var reset = generatedHost.AddComponent<HVRVixxyJiggleReset>();
            reset.rig = rig;
            output.Subjects.Add(BasisVrcfuryUtil.Subject(reset.gameObject, new HVRVixxyPropertyBool {
                fullClassName = typeof(HVRVixxyJiggleReset).FullName,
                variant = HVRVixxyPropertyVariant.Standard,
                propertyName = nameof(HVRVixxyJiggleReset.Active),
                choices = new[] { false, true }
            }));
        }

        private static void ConvertAnimationClip(
            GameObject sourceObject,
            GameObject generatedHost,
            AnimationClipAction model,
            BasisVrcfuryControlData output,
            BasisVrcfuryConversionReport report,
            string label
        ) {
            var clip = model.motion as AnimationClip ?? BasisVrcfuryUtil.ResolveAsset<AnimationClip>(model.clip);
            if (clip == null) {
                report.UnsupportedAction(model.GetType().Name + " (missing/non-clip motion)");
                return;
            }
            if (AnimationMode.InAnimationMode()) {
                report.Warn($"'{label}' clip '{clip.name}' could not be sampled because Unity is already in Animation Mode.");
                return;
            }

            var transforms = new Dictionary<Transform, TransformSnapshot>();
            var blendshapes = new Dictionary<(SkinnedMeshRenderer, string), float>();
            var enabled = new Dictionary<Component, bool>();
            var materialCurves = new Dictionary<(Renderer, string), MaterialCurveAccumulator>();
            var unsupported = 0;

            foreach (var binding in AnimationUtility.GetCurveBindings(clip)) {
                var target = ResolveAnimationTarget(sourceObject, binding.path);
                if (target == null) { unsupported++; continue; }
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null) continue;
                var endpoint = curve.Evaluate(clip.length);

                if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive") {
                    enabled[target.transform] = target.activeSelf;
                    continue;
                }
                if (binding.propertyName == "m_Enabled" && typeof(Component).IsAssignableFrom(binding.type)) {
                    var component = target.GetComponent(binding.type);
                    if (component != null && HVR_VixxyPermitted.IsPermitted(component.GetType().FullName)) {
                        enabled[component] = GetEnabled(component);
                        continue;
                    }
                }
                if (typeof(SkinnedMeshRenderer).IsAssignableFrom(binding.type) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) {
                    var renderer = target.GetComponent(binding.type) as SkinnedMeshRenderer ?? target.GetComponent<SkinnedMeshRenderer>();
                    var shape = binding.propertyName.Substring("blendShape.".Length);
                    if (renderer?.sharedMesh != null) {
                        var index = renderer.sharedMesh.GetBlendShapeIndex(shape);
                        if (index >= 0) {
                            blendshapes[(renderer, shape)] = renderer.GetBlendShapeWeight(index);
                            continue;
                        }
                    }
                }
                if (typeof(Transform).IsAssignableFrom(binding.type)) {
                    var transform = target.transform;
                    if (!transforms.TryGetValue(transform, out var snapshot)) snapshot = new TransformSnapshot(transform);
                    if (binding.propertyName.StartsWith("m_LocalScale.", StringComparison.Ordinal)) snapshot.Scale = true;
                    else if (binding.propertyName.StartsWith("m_LocalPosition.", StringComparison.Ordinal)) snapshot.Position = true;
                    else if (binding.propertyName.StartsWith("m_LocalRotation.", StringComparison.Ordinal) ||
                             binding.propertyName.StartsWith("localEulerAngles", StringComparison.Ordinal) ||
                             binding.propertyName.StartsWith("m_LocalEulerAnglesHint.", StringComparison.Ordinal)) snapshot.Rotation = true;
                    else { unsupported++; continue; }
                    transforms[transform] = snapshot;
                    continue;
                }
                if (typeof(Renderer).IsAssignableFrom(binding.type) && binding.propertyName.StartsWith("material.", StringComparison.Ordinal)) {
                    var renderer = target.GetComponent(binding.type) as Renderer ?? target.GetComponent<Renderer>();
                    if (renderer != null && TryParseMaterialCurve(binding.propertyName.Substring("material.".Length), out var property, out var channel)) {
                        var key = (renderer, property);
                        if (!materialCurves.TryGetValue(key, out var accumulator)) accumulator = new MaterialCurveAccumulator(renderer, property);
                        accumulator.Set(channel, endpoint);
                        materialCurves[key] = accumulator;
                        continue;
                    }
                }
                unsupported++;
            }

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip)) {
                var target = ResolveAnimationTarget(sourceObject, binding.path);
                if (target == null || !typeof(Renderer).IsAssignableFrom(binding.type)) { unsupported++; continue; }
                var match = MaterialSlotBinding.Match(binding.propertyName);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out var slot)) { unsupported++; continue; }
                var renderer = target.GetComponent(binding.type) as Renderer ?? target.GetComponent<Renderer>();
                if (renderer == null || slot < 0 || slot >= renderer.sharedMaterials.Length) { unsupported++; continue; }
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                var active = keys.Where(k => k.time <= clip.length + 0.0001f).OrderBy(k => k.time).LastOrDefault().value as Material;
                if (active == null) { unsupported++; continue; }
                output.Subjects.Add(BasisVrcfuryUtil.Subject(renderer.gameObject, new HVRVixxyPropertyMaterialSlot {
                    fullClassName = renderer.GetType().FullName,
                    variant = HVRVixxyPropertyVariant.RendererMaterialSlot,
                    propertyName = "materialSlot",
                    slot = slot,
                    choices = new[] { renderer.sharedMaterials[slot], active }
                }));
            }

            foreach (var accumulator in materialCurves.Values) {
                if (!accumulator.Emit(output)) unsupported++;
            }

            var sampling = false;
            try {
                AnimationMode.StartAnimationMode();
                AnimationMode.BeginSampling();
                sampling = true;
                AnimationMode.SampleAnimationClip(sourceObject, clip, clip.length);
                AnimationMode.EndSampling();
                sampling = false;

                foreach (var pair in enabled) {
                    if (pair.Key == null) continue;
                    output.Activations.Add(new HVRVixxyActivation {
                        component = pair.Key,
                        threshold = ActivationThreshold.Blended,
                        choices = new[] { pair.Value, GetEnabled(pair.Key) }
                    });
                }
                foreach (var pair in blendshapes) {
                    var renderer = pair.Key.Item1;
                    var shape = pair.Key.Item2;
                    if (renderer == null || renderer.sharedMesh == null) continue;
                    var index = renderer.sharedMesh.GetBlendShapeIndex(shape);
                    if (index < 0) continue;
                    output.Subjects.Add(BasisVrcfuryUtil.Subject(renderer.gameObject, new HVRVixxyPropertyFloat {
                        fullClassName = typeof(SkinnedMeshRenderer).FullName,
                        variant = HVRVixxyPropertyVariant.BlendShape,
                        propertyName = shape,
                        choices = new[] { pair.Value, renderer.GetBlendShapeWeight(index) }
                    }));
                }
                foreach (var pair in transforms) pair.Value.Emit(pair.Key, output);
            } catch (Exception e) {
                report.Warn($"'{label}' clip '{clip.name}' sampling failed: {e.Message}");
            } finally {
                if (sampling) AnimationMode.EndSampling();
                if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
            }

            if (unsupported > 0) {
                report.Warn($"'{label}' clip '{clip.name}' contains {unsupported} binding(s) without a safe Basis/Vixxy translation; supported bindings were retained.");
            }
        }

        private static GameObject ResolveAnimationTarget(GameObject sourceObject, string path) {
            if (sourceObject == null) return null;
            if (string.IsNullOrEmpty(path)) return sourceObject;
            return sourceObject.transform.Find(path)?.gameObject;
        }

        private static bool GetEnabled(Component component) {
            return component switch {
                Transform transform => transform.gameObject.activeSelf,
                Behaviour behaviour => behaviour.enabled,
                Renderer renderer => renderer.enabled,
                Collider collider => collider.enabled,
                Cloth cloth => cloth.enabled,
                LODGroup lod => lod.enabled,
                _ => true
            };
        }

        private static bool IsVixxyRenderer(Renderer renderer) {
            return renderer is MeshRenderer or SkinnedMeshRenderer or TrailRenderer or ParticleSystemRenderer;
        }

        private static Material FindMaterial(Renderer renderer, string propertyName) {
            if (renderer == null) return null;
            foreach (var material in renderer.sharedMaterials ?? Array.Empty<Material>()) {
                if (material != null && material.HasProperty(propertyName)) return material;
            }
            return null;
        }

        private static MaterialPropertyAction.Type DetectMaterialType(Shader shader, string propertyName) {
            if (shader == null) return MaterialPropertyAction.Type.LegacyAuto;
            var count = ShaderUtil.GetPropertyCount(shader);
            for (var i = 0; i < count; i++) {
                if (ShaderUtil.GetPropertyName(shader, i) != propertyName) continue;
                return ShaderUtil.GetPropertyType(shader, i).ToString() switch {
                    "Color" => MaterialPropertyAction.Type.Color,
                    "Vector" => MaterialPropertyAction.Type.Vector,
                    "Float" or "Range" or "Int" => MaterialPropertyAction.Type.Float,
                    _ => MaterialPropertyAction.Type.LegacyAuto
                };
            }
            return MaterialPropertyAction.Type.LegacyAuto;
        }

        internal static string NormalizeAddress(string address) {
            if (string.IsNullOrWhiteSpace(address)) return string.Empty;
            var trimmed = address.Trim();
            const string prefix = "/avatar/parameters/";
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal)) trimmed = trimmed.Substring(prefix.Length);
            return trimmed.TrimStart('/');
        }

        private static bool TryParseMaterialCurve(string raw, out string property, out char channel) {
            property = raw;
            channel = '\0';
            var dot = raw.LastIndexOf('.');
            if (dot <= 0 || dot != raw.Length - 2) return true;
            var candidate = raw[^1];
            if ("rgbaxyzw".IndexOf(candidate) < 0) return true;
            property = raw.Substring(0, dot);
            channel = candidate;
            return true;
        }

        private sealed class TransformSnapshot {
            private readonly Vector3 scale;
            private readonly Vector3 position;
            private readonly Vector3 rotation;
            public bool Scale;
            public bool Position;
            public bool Rotation;

            public TransformSnapshot(Transform target) {
                scale = target.localScale;
                position = target.localPosition;
                rotation = target.localEulerAngles;
            }

            public void Emit(Transform target, BasisVrcfuryControlData output) {
                if (target == null) return;
                if (Scale) {
                    output.Subjects.Add(BasisVrcfuryUtil.Subject(target.gameObject, new HVRVixxyPropertyVector3 {
                        fullClassName = typeof(Transform).FullName,
                        variant = HVRVixxyPropertyVariant.Standard,
                        propertyName = "localScale",
                        choices = new[] { scale, target.localScale }
                    }));
                }
                if (Position) {
                    output.Subjects.Add(BasisVrcfuryUtil.Subject(target.gameObject, new HVRVixxyPropertyVector3 {
                        fullClassName = typeof(Transform).FullName,
                        variant = HVRVixxyPropertyVariant.Standard,
                        propertyName = "localPosition",
                        choices = new[] { position, target.localPosition }
                    }));
                }
                if (Rotation) {
                    output.Subjects.Add(BasisVrcfuryUtil.Subject(target.gameObject, new HVRVixxyPropertyQuaternion {
                        fullClassName = typeof(Transform).FullName,
                        variant = HVRVixxyPropertyVariant.Standard,
                        propertyName = "localRotation",
                        interpolation = HVRVixxyPropertyQuaternionInterpolation.Spherical,
                        choices = new[] { rotation, target.localEulerAngles }
                    }));
                }
            }
        }

        private sealed class MaterialCurveAccumulator {
            private readonly Renderer renderer;
            private readonly string propertyName;
            private readonly Dictionary<char, float> channels = new();
            private bool scalarSet;
            private float scalar;

            public MaterialCurveAccumulator(Renderer renderer, string propertyName) {
                this.renderer = renderer;
                this.propertyName = propertyName;
            }

            public void Set(char channel, float value) {
                if (channel == '\0') {
                    scalarSet = true;
                    scalar = value;
                } else {
                    channels[channel] = value;
                }
            }

            public bool Emit(BasisVrcfuryControlData output) {
                if (!IsVixxyRenderer(renderer)) return false;
                var material = FindMaterial(renderer, propertyName);
                if (material == null) return false;

                HVRVixxyPropertyBase property;
                if (channels.Keys.Any(c => "rgba".IndexOf(c) >= 0)) {
                    var initial = material.GetColor(propertyName);
                    var active = initial;
                    active.r = Get('r', active.r); active.g = Get('g', active.g);
                    active.b = Get('b', active.b); active.a = Get('a', active.a);
                    property = new HVRVixxyPropertyColorHDR { choices = new[] { initial, active } };
                } else if (channels.Keys.Any(c => "xyzw".IndexOf(c) >= 0)) {
                    var initial = material.GetVector(propertyName);
                    var active = initial;
                    active.x = Get('x', active.x); active.y = Get('y', active.y);
                    active.z = Get('z', active.z); active.w = Get('w', active.w);
                    property = new HVRVixxyPropertyVector4 { choices = new[] { initial, active } };
                } else if (scalarSet) {
                    property = new HVRVixxyPropertyFloat { choices = new[] { material.GetFloat(propertyName), scalar } };
                } else {
                    return false;
                }
                property.fullClassName = renderer.GetType().FullName;
                property.variant = HVRVixxyPropertyVariant.MaterialProperty;
                property.propertyName = propertyName;
                output.Subjects.Add(BasisVrcfuryUtil.Subject(renderer.gameObject, property));
                return true;
            }

            private float Get(char c, float fallback) => channels.TryGetValue(c, out var value) ? value : fallback;
        }
    }
}
