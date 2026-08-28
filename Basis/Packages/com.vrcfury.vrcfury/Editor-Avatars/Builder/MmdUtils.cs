using VF.Model.Feature;

namespace VF.Builder {
    internal static class MmdUtils {
        public static bool IsMaybeMmdBlendshape(string name) {
            return MmdCompatibility.IsMaybeMmdBlendshape(name);
        }
    }
}
