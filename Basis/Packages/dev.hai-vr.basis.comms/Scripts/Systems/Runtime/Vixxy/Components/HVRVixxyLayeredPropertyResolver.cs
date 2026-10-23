using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace HVR.Vixxy
{
    /// <summary>
    /// One resolver per orchestrator. Controls contribute properties, but only this class writes them.
    /// The original value is captured once so releasing the last override restores the scene.
    /// </summary>
    internal sealed class HVRVixxyLayeredPropertyResolver
    {
        private readonly struct Key : IEquatable<Key>
        {
            public readonly Component Component;
            public readonly HVRKindMarker Kind;
            public readonly string Name;

            public Key(Component component, HVRKindMarker kind, string name)
            {
                Component = component;
                Kind = kind;
                Name = name;
            }

            public bool Equals(Key other) => ReferenceEquals(Component, other.Component) && Kind == other.Kind && Name == other.Name;
            public override bool Equals(object obj) => obj is Key other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    return ((!ReferenceEquals(Component, null) ? RuntimeHelpers.GetHashCode(Component) : 0) * 397 ^ (int)Kind) * 397 ^ (Name?.GetHashCode() ?? 0);
                }
            }
        }

        private sealed class Contribution
        {
            public HVRVixxyControl Owner;
            public HVRVixxyPropertyBase Property;
            public int From;
            public int To;
            public float Amount;
            public float Absolute;
            public bool Activation;
            public bool ActivationValue;
        }

        private sealed class Binding
        {
            public Key Key;
            public HVRVixxyPropertyBase Property;
            public object Original;
            public object Resolved;
            public readonly Dictionary<HVRVixxyControl, Contribution> Contributions = new();
        }

        private readonly Dictionary<Key, Binding> _bindings = new();
        private readonly Dictionary<HVRVixxyControl, HashSet<Key>> _owners = new();
        private readonly HashSet<Key> _dirty = new();
        private readonly Dictionary<Renderer, MaterialPropertyBlock> _originalBlocks = new();
        private readonly MaterialPropertyBlock _workingBlock = new();
        private readonly List<Contribution> _orderedContributions = new();
        private readonly HashSet<Renderer> _dirtyRenderers = new();
        private readonly List<Key> _emptyKeys = new();

        public void BeginControl(HVRVixxyControl control)
        {
            ReleaseControl(control);
        }

        public void ReleaseControl(HVRVixxyControl control)
        {
            if (!_owners.TryGetValue(control, out var keys)) return;
            foreach (var key in keys)
            {
                if (_bindings.TryGetValue(key, out var binding))
                {
                    binding.Contributions.Remove(control);
                    _dirty.Add(key);
                }
            }
            _owners.Remove(control);
        }

        public void SubmitProperty(HVRVixxyControl control, HVRVixxyPropertyBase property,
            Component component, int from, int to, float amount, float absolute)
        {
            if (component == null) return;
            // Activation toggles and a regular 'enabled' property must resolve through the same key.
            var kind = property.KindMarker;
            var name = property.propertyName;
            if (name == "enabled" && (component is Behaviour || component is Renderer || component is Collider
                                      || component is Cloth || component is LODGroup))
            {
                kind = HVRKindMarker.Undefined;
                name = "#activation";
            }
            Submit(new Key(component, kind, name), new Contribution
            {
                Owner = control, Property = property, From = from, To = to,
                Amount = amount, Absolute = absolute
            });
        }

        public void SubmitActivation(HVRVixxyControl control, Component component, bool value, bool passthrough)
        {
            if (component == null || passthrough) return;
            Submit(new Key(component, HVRKindMarker.Undefined, "#activation"), new Contribution
            {
                Owner = control, Activation = true, ActivationValue = value
            });
        }

        private void Submit(Key key, Contribution contribution)
        {
            if (!_bindings.TryGetValue(key, out var binding))
            {
                if (key.Kind == HVRKindMarker.AffectsMaterialPropertyBlock && key.Component is Renderer renderer
                    && !_originalBlocks.ContainsKey(renderer))
                {
                    var originalBlock = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(originalBlock);
                    _originalBlocks.Add(renderer, originalBlock);
                }

                binding = new Binding
                {
                    Key = key,
                    Property = contribution.Property,
                    Original = CaptureOriginal(key, contribution.Property)
                };
                _bindings.Add(key, binding);
            }
            binding.Contributions[contribution.Owner] = contribution;
            if (!_owners.TryGetValue(contribution.Owner, out var keys))
            {
                keys = new HashSet<Key>();
                _owners.Add(contribution.Owner, keys);
            }
            keys.Add(key);
            _dirty.Add(key);
        }

        private static object CaptureOriginal(Key key, HVRVixxyPropertyBase property)
        {
            if (key.Component == null) return null;
            if (key.Kind == HVRKindMarker.Undefined)
                return HVR_VixxyUtil.GetToggleState(key.Component);
            try
            {
                switch (key.Kind)
                {
                    case HVRKindMarker.FieldAccess:
                        return property.FieldIfMarkedAsFieldAccess.GetValue(key.Component);
                    case HVRKindMarker.PropertyAccess:
                        return property.TPropertyIfMarkedAsTPropertyAccess.CanRead
                            ? property.TPropertyIfMarkedAsTPropertyAccess.GetValue(key.Component) : null;
                    case HVRKindMarker.BlendShape:
                        return ((SkinnedMeshRenderer)key.Component).GetBlendShapeWeight(
                            property.SmrToBlendshapeIndex[(SkinnedMeshRenderer)key.Component]);
                    case HVRKindMarker.AffectsMaterialPropertyBlock:
                    {
                        var material = ((Renderer)key.Component).sharedMaterial;
                        if (material == null || !material.HasProperty(property.ShaderMaterialProperty)) return null;
                        if (property is HVRVixxyPropertyTexture)
                            return material.GetTexture(property.ShaderMaterialProperty);
                        var sample = property.GetValueForChoice(0);
                        return sample switch
                        {
                            float => material.GetFloat(property.ShaderMaterialProperty),
                            int => material.GetInt(property.ShaderMaterialProperty),
                            Color => material.GetColor(property.ShaderMaterialProperty),
                            Vector4 => material.GetVector(property.ShaderMaterialProperty),
                            Vector3 => material.GetVector(property.ShaderMaterialProperty),
                            Vector2 => material.GetVector(property.ShaderMaterialProperty),
                            Texture => material.GetTexture(property.ShaderMaterialProperty),
                            _ => null
                        };
                    }
                }
            }
            catch (Exception e)
            {
                HVR_VixxyUtil.LogUnusual(typeof(HVRVixxyLayeredPropertyResolver), $"Could not capture original property {key.Name}: {e.Message}");
            }
            return null;
        }

        private object ResolveValue(Binding binding)
        {
            var resolved = binding.Original;
            _orderedContributions.Clear();
            _orderedContributions.AddRange(binding.Contributions.Values);
            // Stable order, independent of HashSet order and changed-address update order.
            // At equal priority, the higher instance ID wins.
            _orderedContributions.Sort((a, b) =>
            {
                var byPriority = a.Owner.layerPriority.CompareTo(b.Owner.layerPriority);
                return byPriority != 0 ? byPriority : a.Owner.GetInstanceID().CompareTo(b.Owner.GetInstanceID());
            });
            foreach (var c in _orderedContributions)
            {
                if (c.Activation)
                {
                    resolved = c.ActivationValue;
                    continue;
                }

                var fromPass = c.Property.IsPassthrough(c.From);
                var toPass = c.Property.IsPassthrough(c.To);
                if (fromPass && toPass) continue;
                if (!fromPass && !toPass)
                {
                    resolved = c.Property.CalculateLerpValue(c.Amount, c.From, c.To, c.Absolute);
                    continue;
                }

                // One choice is transparent. Blend the explicit side against the entire
                // stack below, rather than the (meaningless) authored value of the transparent side.
                var opacity = fromPass ? c.Amount : 1f - c.Amount;
                var explicitIndex = fromPass ? c.To : c.From;
                var explicitValue = c.Property.CalculateLerpValue(0f, explicitIndex, explicitIndex, c.Absolute);
                resolved = Mix(resolved, explicitValue, opacity, c.Property);
            }
            return resolved;
        }

        internal static object Mix(object lower, object upper, float opacity, HVRVixxyPropertyBase property)
        {
            opacity = Mathf.Clamp01(opacity);
            if (opacity <= 0f) return lower;
            if (opacity >= 1f) return upper;
            if (lower == null) return opacity > DiscreteThreshold(property) ? upper : null;
            switch (lower)
            {
                case float a when upper is float b: return Mathf.Lerp(a, b, opacity);
                case int a when upper is int b: return Mathf.RoundToInt(Mathf.Lerp(a, b, opacity));
                case Vector2 a when upper is Vector2 b: return Vector2.Lerp(a, b, opacity);
                case Vector3 a when upper is Vector3 b:
                    return property is HVRVixxyPropertyVector3 { interpolation: HVRVixxyPropertyVector3Interpolation.Spherical }
                        ? Vector3.Slerp(a, b, opacity) : Vector3.Lerp(a, b, opacity);
                case Vector4 a when upper is Vector4 b:
                    if (property is HVRVixxyPropertyVector4 { interpolation: HVRVixxyPropertyVector4Interpolation.VectorXYZSpherical })
                    {
                        var xyz = Vector3.Slerp(a, b, opacity);
                        return new Vector4(xyz.x, xyz.y, xyz.z, Mathf.Lerp(a.w, b.w, opacity));
                    }
                    return Vector4.Lerp(a, b, opacity);
                case Quaternion a when upper is Quaternion b:
                    return property is HVRVixxyPropertyQuaternion { interpolation: HVRVixxyPropertyQuaternionInterpolation.Euler }
                        ? Quaternion.Euler(Vector3.Lerp(a.eulerAngles, b.eulerAngles, opacity))
                        : Quaternion.Slerp(a, b, opacity);
                case Color a when upper is Color b:
                    var oklab = property is HVRVixxyPropertyColor { interpolation: HVRVixxyPropertyColorInterpolation.Oklab }
                                || (property is HVRVixxyPropertyColorHDR { interpolation: HVRVixxyPropertyColorHDRInterpolation.OklabWhenPossible }
                                    && HVR_VixxyUtil.IsBelowHDR(a) && HVR_VixxyUtil.IsBelowHDR(b));
                    return oklab ? HVR_VixxyUtil.OklabLerp(a, b, opacity) : Color.Lerp(a, b, opacity);
                default:
                {
                    // Discrete properties (bool, textures, meshes, strings, etc.) use the authored threshold.
                    return opacity > DiscreteThreshold(property) ? upper : lower;
                }
            }
        }

        private static float DiscreteThreshold(HVRVixxyPropertyBase property)
        {
            return property switch
            {
                HVRVixxyPropertyBool p => p.threshold,
                HVRVixxyPropertyTexture p => p.threshold,
                HVRVixxyPropertyMesh p => p.threshold,
                HVRVixxyPropertyMaterial p => p.threshold,
                HVRVixxyPropertyString p => p.threshold,
                _ => 0.5f
            };
        }

        public void Apply()
        {
            if (_dirty.Count == 0) return;
            var renderers = _dirtyRenderers;
            var empty = _emptyKeys;
            renderers.Clear();
            empty.Clear();
            foreach (var key in _dirty)
            {
                if (!_bindings.TryGetValue(key, out var binding)) continue;
                if (key.Component == null)
                {
                    empty.Add(key);
                    continue;
                }
                binding.Resolved = ResolveValue(binding);
                if (key.Kind == HVRKindMarker.AffectsMaterialPropertyBlock)
                {
                    renderers.Add((Renderer)key.Component);
                }
                else
                {
                    Write(binding);
                }
                if (binding.Contributions.Count == 0) empty.Add(key);
            }

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                // Recompose from the original block, not last frame's result: released
                // shader overrides must not stick around in a MaterialPropertyBlock.
                renderer.SetPropertyBlock(_originalBlocks[renderer]);
                renderer.GetPropertyBlock(_workingBlock);
                foreach (var binding in _bindings.Values)
                {
                    if (binding.Key.Kind != HVRKindMarker.AffectsMaterialPropertyBlock
                        || !ReferenceEquals(binding.Key.Component, renderer)
                        || binding.Contributions.Count == 0) continue;
                    if (binding.Resolved == null && binding.Property is HVRVixxyPropertyTexture)
                        _workingBlock.SetTexture(binding.Property.ShaderMaterialProperty, null);
                    else
                        binding.Property.ApplyMaterialProperty(_workingBlock, binding.Resolved);
                }
                renderer.SetPropertyBlock(_workingBlock);
            }

            foreach (var key in empty) _bindings.Remove(key);
            foreach (var renderer in renderers)
            {
                if (renderer == null) { _originalBlocks.Remove(renderer); continue; }
                if (!_bindings.Keys.Any(key => key.Kind == HVRKindMarker.AffectsMaterialPropertyBlock
                                                && ReferenceEquals(key.Component, renderer)))
                    _originalBlocks.Remove(renderer);
            }
            _dirty.Clear();
        }

        private static void Write(Binding binding)
        {
            var component = binding.Key.Component;
            if (component == null) return;
            switch (binding.Key.Kind)
            {
                case HVRKindMarker.Undefined:
                    if (binding.Resolved is bool state) HVR_VixxyUtil.SetToggleState(component, state);
                    break;
                case HVRKindMarker.BlendShape:
                    if (binding.Resolved is float weight)
                        ((SkinnedMeshRenderer)component).SetBlendShapeWeight(
                            binding.Property.SmrToBlendshapeIndex[(SkinnedMeshRenderer)component], weight);
                    break;
                case HVRKindMarker.FieldAccess:
                    binding.Property.FieldIfMarkedAsFieldAccess.SetValue(component, binding.Resolved);
                    break;
                case HVRKindMarker.PropertyAccess:
                    binding.Property.TPropertyIfMarkedAsTPropertyAccess.SetValue(component, binding.Resolved);
                    break;
            }
        }
    }
}
