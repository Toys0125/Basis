using GatorDragonGames.JigglePhysics;
using UnityEngine;

namespace HVR.Vixxy
{
    /// <summary>
    /// Rising-edge JiggleRig reset used by generated compatibility controls.
    /// Vixxy writes <see cref="Active"/> as an ordinary boolean property; the rig is snapped only
    /// when that value changes from false to true, so a held toggle does not reset every frame.
    /// </summary>
    public sealed class HVRVixxyJiggleReset : MonoBehaviour
    {
        [SerializeField] public JiggleRig rig;
        [SerializeField] private bool active;

        public bool Active
        {
            get => active;
            set
            {
                if (value && !active && rig != null) rig.SnapToRestPose();
                active = value;
            }
        }
    }
}
