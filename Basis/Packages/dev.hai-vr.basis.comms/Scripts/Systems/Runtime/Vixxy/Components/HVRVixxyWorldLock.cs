using UnityEngine;

namespace HVR.Vixxy
{
    /// <summary>
    /// Keeps a target at its captured world-space position and rotation while enabled.
    /// Intended for generated compatibility controls such as VRCFury World Drop.
    /// </summary>
    public sealed class HVRVixxyWorldLock : MonoBehaviour
    {
        [SerializeField] public Transform target;

        private Vector3 _worldPosition;
        private Quaternion _worldRotation;

        private void OnEnable()
        {
            Transform actual = target != null ? target : transform;
            actual.GetPositionAndRotation(out _worldPosition, out _worldRotation);
        }

        private void LateUpdate()
        {
            Transform actual = target != null ? target : transform;
            actual.SetPositionAndRotation(_worldPosition, _worldRotation);
        }
    }
}
