using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace VF.Integration.Basis {
    internal sealed class BasisVrcfuryConversionReport {
        private readonly Dictionary<string, int> unsupportedFeatures = new();
        private readonly Dictionary<string, int> unsupportedActions = new();
        private readonly Dictionary<string, int> deferredFeatures = new();
        private readonly Dictionary<string, int> deferredActions = new();
        private readonly HashSet<string> warnings = new();

        public int SourceFeatures;
        public int GeneratedControls;
        public int ConvertedBindings;
        public int ReusedBuilderFeatures;
        public bool CreatedBasisAvatar;

        public IEnumerable<string> Warnings => warnings.OrderBy(v => v);
        public IReadOnlyDictionary<string, int> UnsupportedFeatures => unsupportedFeatures;
        public IReadOnlyDictionary<string, int> UnsupportedActions => unsupportedActions;

        public void Warn(string message) {
            if (!string.IsNullOrWhiteSpace(message)) warnings.Add(message);
        }

        public void UnsupportedFeature(string name) => Increment(unsupportedFeatures, name);
        public void UnsupportedAction(string name) => Increment(unsupportedActions, name);
        public void DeferredFeature(string name) => Increment(deferredFeatures, name);
        public void DeferredAction(string name) => Increment(deferredActions, name);

        public string Summary(GameObject avatar) {
            var b = new StringBuilder();
            b.Append("VRCFury BasisVR conversion for ").Append(avatar != null ? avatar.name : "<null>")
                .Append(": features=").Append(SourceFeatures)
                .Append(", controls=").Append(GeneratedControls)
                .Append(", bindings=").Append(ConvertedBindings)
                .Append(", reusedBuilderFeatures=").Append(ReusedBuilderFeatures);
            AppendMap(b, "unsupportedFeatures", unsupportedFeatures);
            AppendMap(b, "unsupportedActions", unsupportedActions);
            AppendMap(b, "deferredFeatures", deferredFeatures);
            AppendMap(b, "deferredActions", deferredActions);
            return b.ToString();
        }

        public string DialogSummary() {
            return $"VRCFury features scanned: {SourceFeatures}\n" +
                   $"Basis/Vixxy controls generated: {GeneratedControls}\n" +
                   $"Converted bindings/actions: {ConvertedBindings}\n" +
                   $"VRCFury build-time features reused: {ReusedBuilderFeatures}\n" +
                   $"Unsupported feature kinds: {unsupportedFeatures.Count}\n" +
                   $"Unsupported action kinds: {unsupportedActions.Count}\n" +
                   $"Deferred SPS/haptic kinds: {deferredFeatures.Count + deferredActions.Count}";
        }

        private static void Increment(Dictionary<string, int> map, string name) {
            name ??= "<unknown>";
            map.TryGetValue(name, out var count);
            map[name] = count + 1;
        }

        private static void AppendMap(StringBuilder b, string label, Dictionary<string, int> map) {
            if (map.Count == 0) return;
            b.Append(", ").Append(label).Append("=[")
                .Append(string.Join(", ", map.OrderBy(p => p.Key).Select(p => $"{p.Key} x{p.Value}")))
                .Append(']');
        }
    }
}
