using System;
using UnityEngine;
#if VRCF_AVATARS
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace VF.Model.StateAction {
    [Serializable]
    internal class ResetPhysboneAction : Action {
#if VRCF_AVATARS
        public VRCPhysBone physBone;
#else
        // Preserve the serialized object reference in non-VRChat projects without taking a
        // compile-time dependency on the VRC SDK. Basis adapters may inspect this Component
        // by serialized/reference identity if a compatible source component is present.
        public UnityEngine.Component physBone;
#endif
    }
}
