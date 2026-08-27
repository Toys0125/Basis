using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Scripts.BasisSdk;
using GatorDragonGames.JigglePhysics;
using HVR.Basis.Comms;
using HVR.Vixxy;
using UnityEditor;
using UnityEngine;
using VF.Model;
using VF.Model.Feature;
using VF.Model.StateAction;
using ToggleModel = VF.Model.Feature.Toggle;

namespace VF.Integration.Basis {
    internal static class BasisVrcfuryConverter {
        internal const string GeneratedRootName = "__VRCFury_BasisVR_Generated_DoNotEdit";

        private sealed class ToggleRecord {
            public ToggleModel Model;
            public HVRVixxyControl SourceControl;
            public string Address;
        }

        public static BasisVrcfuryConversionReport Generate(BasisAvatar avatar, bool buildClone) {
            var report = new BasisVrcfuryConversionReport();
            if (avatar == null) return report;

            RemoveGenerated(avatar.gameObject);
            var root = BasisVrcfuryUtil.CreateChild(avatar.transform, GeneratedRootName);
            var toggles = new List<ToggleRecord>();
            var icons = new List<SetIcon>();
            var moves = new List<MoveMenuItem>();
            var reorders = new List<ReorderMenuItem>();
            var featureIndex = 0;

            foreach (var fury in avatar.GetComponentsInChildren<VRCFury>(true).ToArray()) {
                if (fury == null) continue;
                foreach (var sourceFeature in fury.GetAllFeatures()) {
                    foreach (var feature in ExpandFeature(sourceFeature, fury.gameObject)) {
                        if (feature == null) continue;
                        featureIndex++;
                        report.SourceFeatures++;
                        ConvertFeature(avatar, root.transform, fury.gameObject, feature, featureIndex, buildClone,
                            toggles, icons, moves, reorders, report);
                    }
                }
            }

            ApplyExclusiveGroups(toggles, report);
            ApplyMenuEdits(root, icons, moves, reorders, report);
            EditorUtility.SetDirty(avatar);
            return report;
        }

        public static int RemoveGenerated(GameObject avatarRoot) {
            if (avatarRoot == null) return 0;
            var count = 0;
            for (var i = avatarRoot.transform.childCount - 1; i >= 0; i--) {
                var child = avatarRoot.transform.GetChild(i);
                if (child != null && child.name == GeneratedRootName) {
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                    count++;
                }
            }
            return count;
        }

        private static IEnumerable<FeatureModel> ExpandFeature(FeatureModel feature, GameObject sourceObject, int depth = 0) {
            if (feature == null || depth > 16) yield break;
            IList<FeatureModel> migrated;
            try {
                migrated = feature.Migrate(new FeatureModel.MigrateRequest {
                    fakeUpgrade = true,
                    gameObject = sourceObject
                });
            } catch {
                yield return feature;
                yield break;
            }
            if (migrated == null || migrated.Count == 0) yield break;
            foreach (var next in migrated) {
                if (next == null) continue;
                if (ReferenceEquals(next, feature)) {
                    yield return next;
                } else {
                    foreach (var expanded in ExpandFeature(next, sourceObject, depth + 1)) yield return expanded;
                }
            }
        }

        private static void ConvertFeature(
            BasisAvatar avatar,
            Transform generatedRoot,
            GameObject sourceObject,
            FeatureModel feature,
            int featureIndex,
            bool buildClone,
            List<ToggleRecord> toggles,
            List<SetIcon> icons,
            List<MoveMenuItem> moves,
            List<ReorderMenuItem> reorders,
            BasisVrcfuryConversionReport report
        ) {
            switch (feature) {
                case ToggleModel toggle:
                    var record = ConvertToggle(avatar, generatedRoot, sourceObject, toggle, featureIndex, report);
                    if (record != null) toggles.Add(record);
                    return;
                case SetIcon setIcon:
                    icons.Add(setIcon);
                    return;
                case MoveMenuItem move:
                    moves.Add(move);
                    return;
                case ReorderMenuItem reorder:
                    reorders.Add(reorder);
                    return;
                case Visemes visemes:
                    ConvertVisemes(avatar, generatedRoot, sourceObject, visemes, featureIndex, report);
                    return;
                case Talking talking:
                    ConvertTalking(avatar, generatedRoot, sourceObject, talking, featureIndex, report);
                    return;
                case Puppet puppet:
                    ConvertPuppet(avatar, generatedRoot, sourceObject, puppet, featureIndex, report);
                    return;
                case GestureDriver gestures:
                    ConvertGestureDriver(avatar, generatedRoot, sourceObject, gestures, featureIndex, report);
                    return;
                case Toes toes:
                    ConvertToes(avatar, generatedRoot, sourceObject, toes, featureIndex, report);
                    return;
                case Blinking blinking:
                    if (buildClone && TryConvertNativeBlink(avatar, blinking, report)) return;
                    report.UnsupportedFeature(feature.GetType().Name + " (non-blendshape custom blink state)");
                    return;
                case RemoveBlinking:
                    if (buildClone) avatar.BlinkViseme = new[] { -1 };
                    return;
                case AdvancedCollider collider:
                    ConvertAdvancedCollider(generatedRoot, collider, featureIndex, report);
                    return;
                case SpsOptions:
                case TPSIntegration:
                case TPSIntegration2:
                case OGBIntegration:
                case OGBIntegration2:
                case ZawooIntegration:
                    report.DeferredFeature(feature.GetType().Name);
                    return;
                default:
                    if (IsReusableBuildFeature(feature)) {
                        report.ReusedBuilderFeatures++;
                        return;
                    }
                    if (IsNoOpOrVrchatOnlyFeature(feature)) return;
                    report.UnsupportedFeature(feature.GetType().Name);
                    return;
            }
        }

        private static ToggleRecord ConvertToggle(
            BasisAvatar avatar,
            Transform generatedRoot,
            GameObject sourceObject,
            ToggleModel model,
            int featureIndex,
            BasisVrcfuryConversionReport report
        ) {
            var label = string.IsNullOrWhiteSpace(model.name) ? $"Toggle {featureIndex}" : model.name;
            var address = ToggleAddress(model, featureIndex);
            var defaultValue = model.slider ? Mathf.Clamp01(model.defaultSliderValue) : model.defaultOn ? 1f : 0f;

            // VRCFury's Flipbook Builder is explicitly discrete even when driven by a radial slider.
            if (model.slider && !model.separateLocal && TryConvertFlipbookSlider(
                    avatar, generatedRoot, sourceObject, model, featureIndex, label, address, defaultValue, out var flipbookControl, report)) {
                AddToggleMenuItem(flipbookControl, model, label);
                AddToggleDrives(flipbookControl, model);
                WarnToggleLimitations(model, label, report);
                return new ToggleRecord { Model = model, SourceControl = flipbookControl, Address = address };
            }

            var host = BasisVrcfuryUtil.CreateChild(generatedRoot, $"{featureIndex:000} - {label}");
            var sourceData = new BasisVrcfuryControlData();
            if (!model.separateLocal) {
                BasisVrcfuryActionConverter.ConvertState(avatar, sourceObject, host, model.state, sourceData, report, label,
                    HVRVixxyLocality.Both, includeUnscoped: true);
                if (model.slider && !model.sliderInactiveAtZero) {
                    foreach (var activation in sourceData.Activations) {
                        if (activation?.choices?.Length > 1) activation.choices[0] = activation.choices[1];
                    }
                }
            }

            var sourceControl = CreateControl(host, address, DefaultChoices(), defaultValue, sourceData,
                HVRVixxyLocality.Both, networked: true, model.hasTransition, model.transitionTimeIn, model.transitionTimeOut);
            AddToggleMenuItem(sourceControl, model, label);
            AddToggleDrives(sourceControl, model);

            if (model.separateLocal) {
                CreateScopedControl(avatar, host.transform, sourceObject, model.state, label + " Remote", address, defaultValue,
                    HVRVixxyLocality.RemoteOnly, model.hasTransition, model.transitionTimeIn, model.transitionTimeOut, report, true);
                CreateScopedControl(avatar, host.transform, sourceObject, model.localState, label + " Local", address, defaultValue,
                    HVRVixxyLocality.WearerOnly, model.hasTransition, model.localTransitionTimeIn, model.localTransitionTimeOut, report, true);
            } else {
                if (HasScopedActions(model.state, local: true)) {
                    CreateScopedControl(avatar, host.transform, sourceObject, model.state, label + " Local Actions", address, defaultValue,
                        HVRVixxyLocality.WearerOnly, model.hasTransition, model.transitionTimeIn, model.transitionTimeOut, report, false);
                }
                if (HasScopedActions(model.state, local: false)) {
                    CreateScopedControl(avatar, host.transform, sourceObject, model.state, label + " Remote Actions", address, defaultValue,
                        HVRVixxyLocality.RemoteOnly, model.hasTransition, model.transitionTimeIn, model.transitionTimeOut, report, false);
                }
            }

            WarnToggleLimitations(model, label, report);
            report.GeneratedControls++;
            report.ConvertedBindings += sourceData.Activations.Count + sourceData.Subjects.Count + sourceData.AddressDrives.Count;
            return new ToggleRecord { Model = model, SourceControl = sourceControl, Address = address };
        }

        private static void CreateScopedControl(
            BasisAvatar avatar,
            Transform parent,
            GameObject sourceObject,
            State state,
            string label,
            string address,
            float defaultValue,
            HVRVixxyLocality locality,
            bool hasTransition,
            float transitionIn,
            float transitionOut,
            BasisVrcfuryConversionReport report,
            bool includeUnscoped
        ) {
            if (state?.actions == null || state.actions.Count == 0) return;
            var host = BasisVrcfuryUtil.CreateChild(parent, label);
            var data = new BasisVrcfuryControlData();
            BasisVrcfuryActionConverter.ConvertState(avatar, sourceObject, host, state, data, report, label, locality, includeUnscoped);
            if (data.Activations.Count + data.Subjects.Count + data.AddressDrives.Count == 0) {
                UnityEngine.Object.DestroyImmediate(host);
                return;
            }
            CreateControl(host, address, DefaultChoices(), defaultValue, data, locality, networked: false,
                hasTransition, transitionIn, transitionOut);
            report.GeneratedControls++;
            report.ConvertedBindings += data.Activations.Count + data.Subjects.Count + data.AddressDrives.Count;
        }

        private static bool TryConvertFlipbookSlider(
            BasisAvatar avatar,
            Transform generatedRoot,
            GameObject sourceObject,
            ToggleModel model,
            int featureIndex,
            string label,
            string address,
            float defaultValue,
            out HVRVixxyControl control,
            BasisVrcfuryConversionReport report
        ) {
            control = null;
            var actions = model.state?.actions;
            if (actions == null) return false;
            var flipbooks = actions.OfType<FlipBookBuilderAction>().ToArray();
            if (flipbooks.Length != 1 || flipbooks[0].pages == null || flipbooks[0].pages.Count < 2) return false;
            if (actions.Any(a => a != null && (a.localOnly || a.remoteOnly))) return false;

            var host = BasisVrcfuryUtil.CreateChild(generatedRoot, $"{featureIndex:000} - {label}");
            var baseActions = actions.Where(a => a is not FlipBookBuilderAction).ToArray();
            var states = new List<BasisVrcfuryMultiChoice.ChoiceState>();
            var pages = flipbooks[0].pages;
            for (var i = 0; i < pages.Count; i++) {
                var combined = new State();
                combined.actions.AddRange(baseActions);
                if (pages[i]?.state?.actions != null) combined.actions.AddRange(pages[i].state.actions);
                var data = new BasisVrcfuryControlData();
                BasisVrcfuryActionConverter.ConvertState(avatar, sourceObject, host, combined, data, report, label);
                states.Add(new BasisVrcfuryMultiChoice.ChoiceState(
                    pages.Count > 1 ? i / (float)(pages.Count - 1) : 0f,
                    $"Page {i + 1}", data));
            }

            if (!BasisVrcfuryMultiChoice.TryMerge(states, out var choices, out var merged, report, label)) {
                UnityEngine.Object.DestroyImmediate(host);
                return false;
            }
            control = CreateControl(host, address, choices, defaultValue, merged, HVRVixxyLocality.Both, true,
                model.hasTransition, model.transitionTimeIn, model.transitionTimeOut, snapToClosestChoice: true);
            report.GeneratedControls++;
            report.ConvertedBindings += merged.Activations.Count + merged.Subjects.Count + merged.AddressDrives.Count;
            return true;
        }

        private static void ConvertPuppet(
            BasisAvatar avatar,
            Transform generatedRoot,
            GameObject sourceObject,
            Puppet model,
            int featureIndex,
            BasisVrcfuryConversionReport report
        ) {
            if (model.stops == null || model.stops.Count < 2) return;
            var y = model.stops[0].y;
            if (model.stops.Any(stop => !Mathf.Approximately(stop.y, y))) {
                report.UnsupportedFeature(nameof(Puppet) + " (2D freeform puppet)");
                return;
            }

            var ordered = model.stops.OrderBy(stop => stop.x).ToArray();
            if (ordered.Select(stop => stop.x).Distinct().Count() != ordered.Length) {
                report.UnsupportedFeature(nameof(Puppet) + " (duplicate 1D stop values)");
                return;
            }

            var host = BasisVrcfuryUtil.CreateChild(generatedRoot, $"{featureIndex:000} - {model.name}");
            var states = new List<BasisVrcfuryMultiChoice.ChoiceState>();
            foreach (var stop in ordered) {
                var data = new BasisVrcfuryControlData();
                BasisVrcfuryActionConverter.ConvertState(avatar, sourceObject, host, stop.state, data, report, model.name);
                states.Add(new BasisVrcfuryMultiChoice.ChoiceState(stop.x, stop.x.ToString("0.###"), data));
            }
            if (!BasisVrcfuryMultiChoice.TryMerge(states, out var choices, out var merged, report, model.name)) {
                UnityEngine.Object.DestroyImmediate(host);
                return;
            }

            var address = BasisVrcfuryActionConverter.NormalizeAddress(model.name + "_x");
            var control = CreateControl(host, address, choices, model.defaultX, merged, HVRVixxyLocality.Both, true, false, 0, 0);
            var menu = host.AddComponent<HVRVixxyMenuItem>();
            BasisVrcfuryUtil.SetField(menu, "control", control);
            BasisVrcfuryUtil.SetField(menu, "title", model.name);
            BasisVrcfuryUtil.SetField(menu, "titleSelection", HVRVixxyTitleSelection.UseCustomTitle);
            BasisVrcfuryUtil.SetField(menu, "presentation", HVRVixxyControlPresentation.Slider);
            BasisVrcfuryUtil.SetField(menu, "icon", model.enableIcon ? BasisVrcfuryUtil.ResolveAsset<Texture2D>(model.icon) : null);
            BasisVrcfuryUtil.SetField(menu, "remember", model.saved ? HVRVixxyRememberScope.RememberInThisAvatar : HVRVixxyRememberScope.DoNotRemember);
            report.GeneratedControls++;
            report.ConvertedBindings += merged.Activations.Count + merged.Subjects.Count + merged.AddressDrives.Count;
        }

        private static void ConvertGestureDriver(
            BasisAvatar avatar,
            Transform generatedRoot,
            GameObject sourceObject,
            GestureDriver model,
            int featureIndex,
            BasisVrcfuryConversionReport report
        ) {
            var gestures = model.gestures?.Where(gesture => gesture != null && gesture.state != null).ToArray()
                ?? Array.Empty<GestureDriver.Gesture>();
            if (gestures.Length == 0) return;

            // A lone weighted gesture can retain VRCFury's continuous fist weight exactly enough to
            // use Vixxy's ordinary 0..1 interpolation. With multiple gesture layers we must use the
            // atomic pair lookup below so one gesture switching off cannot overwrite another switching on.
            if (gestures.Length == 1 && gestures[0].enableWeight && UsesFistWeight(gestures[0])) {
                var gesture = gestures[0];
                var host = BasisVrcfuryUtil.CreateChild(generatedRoot, $"{featureIndex:000} - Gesture {gesture.hand} {gesture.sign}");
                var data = new BasisVrcfuryControlData();
                BasisVrcfuryActionConverter.ConvertState(avatar, sourceObject, host, gesture.state, data, report, "Gesture Driver");
                var address = WeightedGestureAddress(gesture);
                CreateControl(host, address, DefaultChoices(), 0f, data, HVRVixxyLocality.Both, false,
                    true, 0.15f, 0.15f);
                WarnGestureOptions(gesture, report, weightedPreserved: true);
                report.GeneratedControls++;
                report.ConvertedBindings += data.Activations.Count + data.Subjects.Count + data.AddressDrives.Count;
                return;
            }

            var hostRoot = BasisVrcfuryUtil.CreateChild(generatedRoot, $"{featureIndex:000} - Gesture Driver");
            var dataByMask = new Dictionary<ulong, BasisVrcfuryControlData>();
            var choices = new List<BasisVrcfuryMultiChoice.ChoiceState>(64);
            for (var left = 0; left < 8; left++) {
                for (var right = 0; right < 8; right++) {
                    ulong mask = 0;
                    for (var i = 0; i < gestures.Length && i < 64; i++) {
                        if (GestureMatches(gestures[i], (GestureDriver.HandSign)left, (GestureDriver.HandSign)right))
                            mask |= 1UL << i;
                    }

                    if (!dataByMask.TryGetValue(mask, out var data)) {
                        data = new BasisVrcfuryControlData();
                        if (mask != 0) {
                            var combined = new State();
                            for (var i = 0; i < gestures.Length && i < 64; i++) {
                                if ((mask & (1UL << i)) == 0 || gestures[i].state?.actions == null) continue;
                                combined.actions.AddRange(gestures[i].state.actions);
                            }
                            BasisVrcfuryActionConverter.ConvertState(avatar, sourceObject, hostRoot, combined, data, report, "Gesture Driver");
                        }
                        dataByMask[mask] = data;
                    }

                    var pairValue = left * 8 + right;
                    choices.Add(new BasisVrcfuryMultiChoice.ChoiceState(
                        pairValue,
                        $"L {(GestureDriver.HandSign)left} / R {(GestureDriver.HandSign)right}",
                        data));
                }
            }

            if (gestures.Length > 64) {
                report.Warn("Gesture Driver contains more than 64 entries. Basis imported the first 64 because its compatibility lookup uses a 64-bit matched-gesture mask.");
            }
            if (!BasisVrcfuryMultiChoice.TryMerge(choices, out var mergedChoices, out var merged, report, "Gesture Driver")) {
                UnityEngine.Object.DestroyImmediate(hostRoot);
                return;
            }

            CreateControl(hostRoot, HVRAddress.System.User.Gesture.Pair.address, mergedChoices, 0f, merged,
                HVRVixxyLocality.Both, false, false, 0f, 0f, snapToClosestChoice: true);
            report.GeneratedControls++;
            report.ConvertedBindings += merged.Activations.Count + merged.Subjects.Count + merged.AddressDrives.Count;

            foreach (var gesture in gestures) WarnGestureOptions(gesture, report, weightedPreserved: false);
        }

        private static bool GestureMatches(GestureDriver.Gesture gesture, GestureDriver.HandSign left, GestureDriver.HandSign right) {
            return gesture.hand switch {
                GestureDriver.Hand.LEFT => left == gesture.sign,
                GestureDriver.Hand.RIGHT => right == gesture.sign,
                GestureDriver.Hand.COMBO => left == gesture.sign && right == gesture.comboSign,
                _ => left == gesture.sign || right == gesture.sign
            };
        }

        private static bool UsesFistWeight(GestureDriver.Gesture gesture) {
            return gesture.hand switch {
                GestureDriver.Hand.COMBO => gesture.sign == GestureDriver.HandSign.FIST || gesture.comboSign == GestureDriver.HandSign.FIST,
                _ => gesture.sign == GestureDriver.HandSign.FIST
            };
        }

        private static string WeightedGestureAddress(GestureDriver.Gesture gesture) {
            var sign = (HVRAddress.System.User.HandGestureSign)(int)gesture.sign;
            var combo = (HVRAddress.System.User.HandGestureSign)(int)gesture.comboSign;
            return gesture.hand switch {
                GestureDriver.Hand.LEFT => HVRAddress.System.User.Gesture.WeightForLeft(sign),
                GestureDriver.Hand.RIGHT => HVRAddress.System.User.Gesture.WeightForRight(sign),
                GestureDriver.Hand.COMBO => HVRAddress.System.User.Gesture.WeightForCombo(sign, combo),
                _ => HVRAddress.System.User.Gesture.WeightForEither(sign)
            };
        }

        private static void WarnGestureOptions(GestureDriver.Gesture gesture, BasisVrcfuryConversionReport report, bool weightedPreserved) {
            if (gesture.customTransitionTime && gesture.transitionTime > 0f)
                report.Warn("Gesture Driver custom transition timing is approximated by Basis/Vixxy; atomic multi-gesture lookups switch immediately to avoid passing through unrelated gesture-pair values.");
            if (gesture.enableWeight && UsesFistWeight(gesture) && !weightedPreserved)
                report.Warn("Gesture Driver fist weighting is imported as a binary gesture when the feature contains multiple gesture entries. A lone weighted gesture preserves continuous Basis finger-curl weight.");
            if (gesture.enableLockMenuItem && !string.IsNullOrWhiteSpace(gesture.lockMenuItem))
                report.Warn($"Gesture lock menu item '{gesture.lockMenuItem}' is not yet imported because it requires OR-combining a persistent menu value with a live system gesture.");
            if (gesture.enableExclusiveTag)
                report.Warn($"Gesture Driver exclusive tag '{gesture.exclusiveTag}' is not yet reproduced across independent gesture features; conditions inside this Gesture Driver are evaluated atomically.");
        }

        private static void ConvertAdvancedCollider(
            Transform generatedRoot,
            AdvancedCollider model,
            int featureIndex,
            BasisVrcfuryConversionReport report
        ) {
            if (model.rootTransform == null) {
                report.Warn("VRCFury Advanced Collider has no Transform and was skipped.");
                return;
            }

            var host = BasisVrcfuryUtil.CreateChild(generatedRoot, $"{featureIndex:000} - Advanced Collider {model.colliderName}");
            var component = host.AddComponent<JiggleColliderExample>();
            var collider = new JiggleCollider {
                type = model.height > 0f ? JiggleCollider.JiggleColliderType.Capsule : JiggleCollider.JiggleColliderType.Sphere,
                radius = Mathf.Max(0f, model.radius),
                height = Mathf.Max(0f, model.height),
                capsuleAxis = JiggleCollider.CapsuleAxis.Y,
                localOffset = Unity.Mathematics.float3.zero
            };
            BasisVrcfuryUtil.SetField(component, "jiggleCollider", new JiggleColliderSerializable {
                transform = model.rootTransform,
                collider = collider
            });
            report.ConvertedBindings++;
            if (!string.IsNullOrWhiteSpace(model.colliderName))
                report.Warn($"Advanced Collider '{model.colliderName}' was imported as a Basis global Jiggle collider. Basis does not have VRChat's named avatar collider slots, so the slot name itself is not retained.");
        }

        private static void ConvertToes(
            BasisAvatar avatar,
            Transform generatedRoot,
            GameObject sourceObject,
            Toes model,
            int featureIndex,
            BasisVrcfuryConversionReport report
        ) {
            // VRCFury Toes is a 2D puppet (up/down + left/right splay). It remains intentionally
            // separate from the 1D puppet importer until Vixxy exposes a true 2D control.
            report.UnsupportedFeature(nameof(Toes) + " (requires 2D Vixxy puppet input)");
        }

        private static void ConvertVisemes(
            BasisAvatar avatar,
            Transform generatedRoot,
            GameObject sourceObject,
            Visemes model,
            int featureIndex,
            BasisVrcfuryConversionReport report
        ) {
            var states = new[] {
                ("PP", HVRAddress.System.User.Viseme.PP.address, model.state_PP),
                ("FF", HVRAddress.System.User.Viseme.FF.address, model.state_FF),
                ("TH", HVRAddress.System.User.Viseme.TH.address, model.state_TH),
                ("DD", HVRAddress.System.User.Viseme.DD.address, model.state_DD),
                ("kk", HVRAddress.System.User.Viseme.kk.address, model.state_kk),
                ("CH", HVRAddress.System.User.Viseme.CH.address, model.state_CH),
                ("SS", HVRAddress.System.User.Viseme.SS.address, model.state_SS),
                ("nn", HVRAddress.System.User.Viseme.nn.address, model.state_nn),
                ("RR", HVRAddress.System.User.Viseme.RR.address, model.state_RR),
                ("aa", HVRAddress.System.User.Viseme.aa.address, model.state_aa),
                ("E", HVRAddress.System.User.Viseme.E.address, model.state_E),
                ("I", HVRAddress.System.User.Viseme.ih.address, model.state_I),
                ("O", HVRAddress.System.User.Viseme.oh.address, model.state_O),
                ("U", HVRAddress.System.User.Viseme.ou.address, model.state_U)
            };
            foreach (var entry in states) {
                if (entry.Item3?.actions == null || entry.Item3.actions.Count == 0) continue;
                CreateSystemStateControl(avatar, generatedRoot, sourceObject, $"{featureIndex:000} - Viseme {entry.Item1}",
                    entry.Item2, entry.Item3, report, model.instant);
            }
        }

        private static void ConvertTalking(
            BasisAvatar avatar,
            Transform generatedRoot,
            GameObject sourceObject,
            Talking model,
            int featureIndex,
            BasisVrcfuryConversionReport report
        ) {
            if (model.state?.actions == null || model.state.actions.Count == 0) return;
            CreateSystemStateControl(avatar, generatedRoot, sourceObject, $"{featureIndex:000} - When Talking",
                HVRAddress.System.User.VoiceGain.address, model.state, report, false, 0.099f, 0.1f);
        }

        private static void CreateSystemStateControl(
            BasisAvatar avatar,
            Transform generatedRoot,
            GameObject sourceObject,
            string label,
            string address,
            State state,
            BasisVrcfuryConversionReport report,
            bool snap,
            float offValue = 0f,
            float onValue = 1f
        ) {
            var host = BasisVrcfuryUtil.CreateChild(generatedRoot, label);
            var data = new BasisVrcfuryControlData();
            BasisVrcfuryActionConverter.ConvertState(avatar, sourceObject, host, state, data, report, label);
            var choices = new[] {
                new HVRVixxyChoiceControl { title = "OFF", value = offValue },
                new HVRVixxyChoiceControl { title = "ON", value = onValue }
            };
            // Snap mode for instant visemes needs a third choice so Vixxy's multi-choice snap path is used.
            if (snap) {
                choices = new[] {
                    new HVRVixxyChoiceControl { title = "OFF", value = 0f },
                    new HVRVixxyChoiceControl { title = "ON", value = 0.5f },
                    new HVRVixxyChoiceControl { title = "ON", value = 1f }
                };
                foreach (var activation in data.Activations) {
                    if (activation?.choices?.Length == 2) activation.choices = new[] { activation.choices[0], activation.choices[1], activation.choices[1] };
                }
                foreach (var subject in data.Subjects) {
                    foreach (var property in subject.properties) property.PruneArrays(3);
                }
            }
            CreateControl(host, address, choices, offValue, data, HVRVixxyLocality.Both, false, false, 0, 0, snap);
            report.GeneratedControls++;
            report.ConvertedBindings += data.Activations.Count + data.Subjects.Count + data.AddressDrives.Count;
        }

        private static bool TryConvertNativeBlink(BasisAvatar avatar, Blinking model, BasisVrcfuryConversionReport report) {
            var actions = model.state?.actions?.Where(a => a != null).ToArray() ?? Array.Empty<VF.Model.StateAction.Action>();
            if (actions.Length != 1 || actions[0] is not BlendShapeAction blend || string.IsNullOrWhiteSpace(blend.blendShape)) return false;
            IEnumerable<SkinnedMeshRenderer> renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (!blend.allRenderers && blend.renderer is SkinnedMeshRenderer selected) renderers = new[] { selected };
            foreach (var renderer in renderers) {
                if (renderer?.sharedMesh == null) continue;
                var index = renderer.sharedMesh.GetBlendShapeIndex(blend.blendShape);
                if (index < 0) continue;
                avatar.FaceBlinkMesh = renderer;
                avatar.BlinkViseme = new[] { index };
                report.Warn("VRCFury custom Blinking was mapped to Basis' native blink driver; VRCFury transition/hold timing is replaced by Basis blink timing.");
                return true;
            }
            return false;
        }

        internal static HVRVixxyControl CreateControl(
            GameObject host,
            string address,
            HVRVixxyChoiceControl[] choices,
            float defaultValue,
            BasisVrcfuryControlData data,
            HVRVixxyLocality locality,
            bool networked,
            bool hasTransition,
            float transitionIn,
            float transitionOut,
            bool snapToClosestChoice = false
        ) {
            var control = host.AddComponent<HVRVixxyControl>();
            control.choices = choices;
            control.defaultValue = defaultValue;
            BasisVrcfuryUtil.SetField(control, "address", new HVRAddressSelector { path = address });
            BasisVrcfuryUtil.SetField(control, "activations", data.Activations.ToArray());
            BasisVrcfuryUtil.SetField(control, "subjects", data.Subjects.ToArray());
            BasisVrcfuryUtil.SetField(control, "addressDrives", data.AddressDrives.ToArray());
            BasisVrcfuryUtil.SetField(control, "locality", locality);
            BasisVrcfuryUtil.SetField(control, "networked", networked);
            BasisVrcfuryUtil.SetField(control, "snapToClosestChoice", snapToClosestChoice);
            if (hasTransition) {
                var duration = Mathf.Max(transitionIn, transitionOut);
                if (duration > 0f) {
                    BasisVrcfuryUtil.SetField(control, "transition", HVRVixxyTransitionMode.Simplified);
                    BasisVrcfuryUtil.SetField(control, "transitionDuration", duration);
                }
            }
            return control;
        }

        private static HVRVixxyChoiceControl[] DefaultChoices() => new[] {
            new HVRVixxyChoiceControl { title = "OFF", value = 0f },
            new HVRVixxyChoiceControl { title = "ON", value = 1f }
        };

        private static void AddToggleMenuItem(HVRVixxyControl control, ToggleModel model, string label) {
            if (!model.addMenuItem || control == null) return;
            var menu = control.gameObject.AddComponent<HVRVixxyMenuItem>();
            BasisVrcfuryUtil.SetField(menu, "control", control);
            BasisVrcfuryUtil.SetField(menu, "title", label);
            BasisVrcfuryUtil.SetField(menu, "titleSelection", HVRVixxyTitleSelection.UseCustomTitle);
            BasisVrcfuryUtil.SetField(menu, "presentation", model.slider ? HVRVixxyControlPresentation.Slider : HVRVixxyControlPresentation.Default);
            BasisVrcfuryUtil.SetField(menu, "icon", model.enableIcon ? BasisVrcfuryUtil.ResolveAsset<Texture2D>(model.icon) : null);
            BasisVrcfuryUtil.SetField(menu, "remember", model.saved ? HVRVixxyRememberScope.RememberInThisAvatar : HVRVixxyRememberScope.DoNotRemember);
        }

        private static void AddToggleDrives(HVRVixxyControl control, ToggleModel model) {
            if (control == null || !model.enableDriveGlobalParam) return;
            foreach (var target in SeparateList(model.driveGlobalParam)) {
                AppendDrive(control, new HVRVixxyAddressDrive {
                    address = new HVRAddressSelector { path = BasisVrcfuryActionConverter.NormalizeAddress(target) },
                    choices = new[] { 0f, 1f },
                    applyChoices = new[] { true, true },
                    interpolate = model.slider
                });
            }
        }

        private static void ApplyExclusiveGroups(IEnumerable<ToggleRecord> toggles, BasisVrcfuryConversionReport report) {
            var records = toggles.ToArray();
            var groups = new Dictionary<string, List<ToggleRecord>>();
            foreach (var record in records) {
                if (!record.Model.enableExclusiveTag) continue;
                foreach (var tag in SeparateList(record.Model.exclusiveTag)) {
                    if (!groups.TryGetValue(tag, out var members)) groups[tag] = members = new List<ToggleRecord>();
                    members.Add(record);
                }
                if (record.Model.exclusiveOffState) {
                    report.Warn($"'{record.Model.name}' requests VRCFury Exclusive Off State. Mutual exclusion is preserved, but automatic fallback-to-this-toggle when every peer is off is not yet representable by Vixxy address drives.");
                }
            }

            foreach (var members in groups.Values) {
                foreach (var source in members) {
                    foreach (var other in members) {
                        if (ReferenceEquals(source, other) || source.Address == other.Address) continue;
                        AppendDrive(source.SourceControl, new HVRVixxyAddressDrive {
                            address = new HVRAddressSelector { path = other.Address },
                            choices = new[] { 0f, 0f },
                            applyChoices = new[] { false, true },
                            interpolate = false
                        });
                    }
                }
            }
        }

        private static void AppendDrive(HVRVixxyControl control, HVRVixxyAddressDrive drive) {
            var current = BasisVrcfuryUtil.GetField(control, "addressDrives", Array.Empty<HVRVixxyAddressDrive>()) ?? Array.Empty<HVRVixxyAddressDrive>();
            BasisVrcfuryUtil.SetField(control, "addressDrives", current.Append(drive).ToArray());
        }

        private static void ApplyMenuEdits(
            GameObject generatedRoot,
            IEnumerable<SetIcon> icons,
            IEnumerable<MoveMenuItem> moves,
            IEnumerable<ReorderMenuItem> reorders,
            BasisVrcfuryConversionReport report
        ) {
            var menuItems = generatedRoot.GetComponentsInChildren<HVRVixxyMenuItem>(true).ToList();
            foreach (var move in moves) {
                var matches = menuItems.Where(item => item.ResolveTitle() == move.fromPath).ToArray();
                foreach (var item in matches) {
                    BasisVrcfuryUtil.SetField(item, "title", move.toPath);
                    BasisVrcfuryUtil.SetField(item, "titleSelection", HVRVixxyTitleSelection.UseCustomTitle);
                }
                if (matches.Length == 0) report.Warn($"Move Menu Item '{move.fromPath}' did not match a generated Vixxy entry.");
            }
            foreach (var setIcon in icons) {
                var icon = BasisVrcfuryUtil.ResolveAsset<Texture2D>(setIcon.icon);
                var matches = menuItems.Where(item => item.ResolveTitle() == setIcon.path).ToArray();
                foreach (var item in matches) BasisVrcfuryUtil.SetField(item, "icon", icon);
                if (matches.Length == 0) report.Warn($"Set Icon path '{setIcon.path}' did not match a generated Vixxy entry.");
            }
            foreach (var reorder in reorders.OrderBy(r => r.position)) {
                var item = menuItems.FirstOrDefault(candidate => candidate.ResolveTitle() == reorder.path);
                if (item == null) continue;
                item.transform.SetSiblingIndex(Mathf.Clamp(reorder.position, 0, item.transform.parent.childCount - 1));
            }
        }

        private static string ToggleAddress(ToggleModel model, int featureIndex) {
            if (!string.IsNullOrWhiteSpace(model.paramOverride)) return BasisVrcfuryActionConverter.NormalizeAddress(model.paramOverride);
            if (model.useGlobalParam && !string.IsNullOrWhiteSpace(model.globalParam)) return BasisVrcfuryActionConverter.NormalizeAddress(model.globalParam);
            if (!model.usePrefixOnParam && !string.IsNullOrWhiteSpace(model.name)) return BasisVrcfuryActionConverter.NormalizeAddress(model.name);
            return $"VRCFury/Internal/{featureIndex}/{BasisVrcfuryUtil.SafeName(model.name)}";
        }

        private static string[] SeparateList(string value) {
            return (value ?? string.Empty).Split(',').Select(v => v.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToArray();
        }

        private static bool HasScopedActions(State state, bool local) {
            return state?.actions?.Any(action => action != null && (local ? action.localOnly : action.remoteOnly)) == true;
        }

        private static void WarnToggleLimitations(ToggleModel model, string label, BasisVrcfuryConversionReport report) {
            if (model.securityEnabled) report.Warn($"'{label}' uses VRCFury Security Lock; the Basis/Vixxy control is imported without the VRChat-specific PIN layer.");
            if (model.holdButton) report.Warn($"'{label}' is a VRChat hold-button; Basis currently presents it as a persistent Vixxy toggle.");
            if (model.hasExitTime) report.Warn($"'{label}' uses animator exit-time semantics; Basis applies the Vixxy value immediately.");
            if (model.hasTransition && (!Mathf.Approximately(model.transitionTimeIn, model.transitionTimeOut) ||
                model.transitionStateIn?.actions?.Count > 0 || model.transitionStateOut?.actions?.Count > 0)) {
                report.Warn($"'{label}' uses authored/asymmetric transition states; Basis preserves the closest symmetric Vixxy transition duration, not the transition clips.");
            }
            if (model.name?.Contains('/') == true) report.Warn($"'{label}' contains a VRChat submenu path. Vixxy currently keeps that path in the label because its avatar menu is flat.");
        }

        internal static bool IsSpsOrHapticFeature(FeatureModel feature) {
            return feature is SpsOptions or TPSIntegration or TPSIntegration2 or OGBIntegration or OGBIntegration2 or ZawooIntegration;
        }

        private static bool IsReusableBuildFeature(FeatureModel feature) {
            return feature is AnchorOverrideFix2
                or ApplyDuringUpload
                or ArmatureLink
                or BlendShapeLink
                or BlendshapeOptimizer
                or BoneConstraint
                or BoundingBoxFix2
                or ConstraintRetarget
                or DeleteDuringUpload
                or HeadChopHead
                or ShowInFirstPerson;
        }

        private static bool IsNoOpOrVrchatOnlyFeature(FeatureModel feature) {
            return feature is AvatarScale2
                or DescriptorDebug
                or DirectTreeOptimizer
                or FixWriteDefaults
                or MmdCompatibility
                or OverrideMenuSettings
                or RemoveHandGestures2
                or SecurityLock
                or SecurityRestricted
                or Slot4Fix
                or UnlimitedParameters;
        }
    }
}
