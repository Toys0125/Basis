using System;
using System.Collections.Generic;
using System.Linq;
using HVR.Basis.Comms;
using HVR.Vixxy;
using UnityEngine;

namespace VF.Integration.Basis {
    internal static class BasisVrcfuryMultiChoice {
        internal readonly struct ChoiceState {
            public readonly float Value;
            public readonly string Title;
            public readonly Texture2D Icon;
            public readonly BasisVrcfuryControlData Data;

            public ChoiceState(float value, string title, BasisVrcfuryControlData data, Texture2D icon = null) {
                Value = value;
                Title = title;
                Data = data;
                Icon = icon;
            }
        }

        public static bool TryMerge(
            IReadOnlyList<ChoiceState> states,
            out HVRVixxyChoiceControl[] choices,
            out BasisVrcfuryControlData merged,
            BasisVrcfuryConversionReport report,
            string label
        ) {
            choices = Array.Empty<HVRVixxyChoiceControl>();
            merged = new BasisVrcfuryControlData();
            if (states == null || states.Count < 2) return false;

            choices = states.Select(s => new HVRVixxyChoiceControl {
                title = string.IsNullOrWhiteSpace(s.Title) ? s.Value.ToString("0.###") : s.Title,
                icon = s.Icon,
                value = s.Value
            }).ToArray();

            MergeActivations(states, merged);
            MergeAddressDrives(states, merged);
            MergeSubjects(states, merged, report, label);
            return true;
        }

        private static void MergeActivations(IReadOnlyList<ChoiceState> states, BasisVrcfuryControlData merged) {
            var components = states.SelectMany(s => s.Data.Activations)
                .Where(a => a?.component != null)
                .Select(a => a.component)
                .Distinct()
                .ToArray();

            foreach (var component in components) {
                var first = states.SelectMany(s => s.Data.Activations)
                    .FirstOrDefault(a => a?.component == component);
                var resting = first?.choices?.Length > 0 ? first.choices[0] : GetToggleState(component);
                var values = new bool[states.Count];
                for (var i = 0; i < states.Count; i++) {
                    var activation = states[i].Data.Activations.LastOrDefault(a => a?.component == component);
                    values[i] = activation?.choices?.Length > 1 ? activation.choices[1] : resting;
                }
                merged.Activations.Add(new HVRVixxyActivation {
                    component = component,
                    threshold = ActivationThreshold.Strict,
                    choices = values
                });
            }
        }

        private static void MergeAddressDrives(IReadOnlyList<ChoiceState> states, BasisVrcfuryControlData merged) {
            var addresses = states.SelectMany(s => s.Data.AddressDrives)
                .Where(d => d != null && d.address.TryResolvePath(out _))
                .Select(d => d.address.TryResolvePath(out var path) ? path : null)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct()
                .ToArray();

            foreach (var address in addresses) {
                var values = new float[states.Count];
                var applies = new bool[states.Count];
                var interpolate = false;
                for (var i = 0; i < states.Count; i++) {
                    var drive = states[i].Data.AddressDrives.LastOrDefault(d => d != null && d.address.TryResolvePath(out var path) && path == address);
                    if (drive?.choices?.Length > 1) values[i] = drive.choices[1];
                    if (drive?.applyChoices?.Length > 1) applies[i] = drive.applyChoices[1];
                    interpolate |= drive?.interpolate == true;
                }
                merged.AddressDrives.Add(new HVRVixxyAddressDrive {
                    address = new HVRAddressSelector { path = address },
                    choices = values,
                    applyChoices = applies,
                    interpolate = interpolate
                });
            }
        }

        private static void MergeSubjects(
            IReadOnlyList<ChoiceState> states,
            BasisVrcfuryControlData merged,
            BasisVrcfuryConversionReport report,
            string label
        ) {
            var all = new Dictionary<PropertyKey, List<(int choice, HVRVixxyPropertyBase property)>>();
            for (var i = 0; i < states.Count; i++) {
                foreach (var subject in states[i].Data.Subjects) {
                    var target = subject?.targets?.FirstOrDefault();
                    if (target == null || subject.properties == null) continue;
                    foreach (var property in subject.properties) {
                        if (property == null) continue;
                        var key = new PropertyKey(target, property);
                        if (!all.TryGetValue(key, out var list)) {
                            list = new List<(int, HVRVixxyPropertyBase)>();
                            all[key] = list;
                        }
                        list.Add((i, property));
                    }
                }
            }

            foreach (var pair in all) {
                var template = pair.Value[0].property;
                var property = CloneWithChoices(template, states.Count, pair.Value);
                if (property == null) {
                    report.Warn($"'{label}' contains a multi-choice property type '{template.GetType().Name}' that cannot yet be merged.");
                    continue;
                }
                merged.Subjects.Add(BasisVrcfuryUtil.Subject(pair.Key.Target, property));
            }
        }

        private static HVRVixxyPropertyBase CloneWithChoices(
            HVRVixxyPropertyBase template,
            int count,
            List<(int choice, HVRVixxyPropertyBase property)> entries
        ) {
            HVRVixxyPropertyBase clone = template switch {
                HVRVixxyPropertyFloat p => New(new HVRVixxyPropertyFloat(), p, Fill(count, entries, p.choices[0], q => ((HVRVixxyPropertyFloat)q).choices[1])),
                HVRVixxyPropertyVector3 p => New(new HVRVixxyPropertyVector3 { interpolation = p.interpolation }, p, Fill(count, entries, p.choices[0], q => ((HVRVixxyPropertyVector3)q).choices[1])),
                HVRVixxyPropertyVector4 p => New(new HVRVixxyPropertyVector4 { interpolation = p.interpolation }, p, Fill(count, entries, p.choices[0], q => ((HVRVixxyPropertyVector4)q).choices[1])),
                HVRVixxyPropertyColorHDR p => New(new HVRVixxyPropertyColorHDR { interpolation = p.interpolation }, p, Fill(count, entries, p.choices[0], q => ((HVRVixxyPropertyColorHDR)q).choices[1])),
                HVRVixxyPropertyQuaternion p => New(new HVRVixxyPropertyQuaternion { interpolation = p.interpolation }, p, Fill(count, entries, p.choices[0], q => ((HVRVixxyPropertyQuaternion)q).choices[1])),
                HVRVixxyPropertyMaterialSlot p => New(new HVRVixxyPropertyMaterialSlot { slot = p.slot, threshold = p.threshold }, p, Fill(count, entries, p.choices[0], q => ((HVRVixxyPropertyMaterialSlot)q).choices[1])),
                HVRVixxyPropertyMaterial p => New(new HVRVixxyPropertyMaterial { threshold = p.threshold }, p, Fill(count, entries, p.choices[0], q => ((HVRVixxyPropertyMaterial)q).choices[1])),
                HVRVixxyPropertyBool p => New(new HVRVixxyPropertyBool { threshold = p.threshold }, p, Fill(count, entries, p.choices[0], q => ((HVRVixxyPropertyBool)q).choices[1])),
                _ => null
            };
            return clone;
        }

        private static TProperty New<TProperty, TValue>(TProperty clone, HVRVixxyPropertyBase template, TValue[] values)
            where TProperty : HVRVixxyProperty<TValue> {
            clone.fullClassName = template.fullClassName;
            clone.variant = template.variant;
            clone.propertyName = template.propertyName;
            clone.choices = values;
            return clone;
        }

        private static TValue[] Fill<TValue>(
            int count,
            List<(int choice, HVRVixxyPropertyBase property)> entries,
            TValue resting,
            Func<HVRVixxyPropertyBase, TValue> active
        ) {
            var output = Enumerable.Repeat(resting, count).ToArray();
            foreach (var entry in entries) output[entry.choice] = active(entry.property);
            return output;
        }

        private static bool GetToggleState(Component component) {
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

        private readonly struct PropertyKey : IEquatable<PropertyKey> {
            public readonly GameObject Target;
            private readonly string className;
            private readonly HVRVixxyPropertyVariant variant;
            private readonly string propertyName;
            private readonly int materialSlot;

            public PropertyKey(GameObject target, HVRVixxyPropertyBase property) {
                Target = target;
                className = property.fullClassName;
                variant = property.variant;
                propertyName = property.propertyName;
                materialSlot = property is HVRVixxyPropertyMaterialSlot slot ? slot.slot : -1;
            }

            public bool Equals(PropertyKey other) {
                return Target == other.Target && className == other.className && variant == other.variant &&
                       propertyName == other.propertyName && materialSlot == other.materialSlot;
            }

            public override bool Equals(object obj) => obj is PropertyKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Target, className, variant, propertyName, materialSlot);
        }
    }
}
