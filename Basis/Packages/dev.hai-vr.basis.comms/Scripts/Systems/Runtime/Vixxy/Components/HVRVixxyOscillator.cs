using HVR.Basis.Comms;
using UnityEngine;

namespace HVR.Vixxy
{
    /// <summary>
    /// Small generated compatibility source for looping Vixxy states. While enabled it publishes a
    /// smooth 0 -> 1 -> 0 cycle to an internal address. When disabled it publishes -1 so a companion
    /// three-choice control can restore its authored resting values instead of stopping on state A.
    /// </summary>
    public sealed class HVRVixxyOscillator : MonoBehaviour, IHVRInitializable
    {
        [SerializeField] public string outputAddress;
        [SerializeField] public float loopTime = 5f;

        private HVRVariableStore _variableStore;
        private int _addressId;
        private double _cycleStart;
        private float _lastPublished = float.NaN;
        private bool _ready;

        public void OnHVRAvatarReady(bool isWearer)
        {
            var comms = HVRCommsUtil.GetComms(this);
            _variableStore = comms != null ? comms.VariableStore : AcquisitionService.SceneInstance.VariableStore;
            if (_variableStore == null || string.IsNullOrWhiteSpace(outputAddress)) return;
            _addressId = HVRAddress.AddressToId(outputAddress);
            _ready = true;
            _cycleStart = Time.timeAsDouble;
            Publish(enabled ? 0f : -1f);
        }

        public void OnHVRReadyBothAvatarAndNetwork(bool isWearer)
        {
        }

        private void OnEnable()
        {
            _cycleStart = Time.timeAsDouble;
            if (_ready) Publish(0f);
        }

        private void OnDisable()
        {
            if (_ready) Publish(-1f);
        }

        private void Update()
        {
            if (!_ready) return;
            var duration = loopTime > 0.0001f ? loopTime : 0.0001f;
            var phase = (float)((Time.timeAsDouble - _cycleStart) / duration);
            phase -= Mathf.Floor(phase);
            var value = 0.5f - 0.5f * Mathf.Cos(phase * Mathf.PI * 2f);
            Publish(value);
        }

        private void Publish(float value)
        {
            if (Mathf.Approximately(_lastPublished, value)) return;
            _variableStore.SubmitOrDefineDefaultValue(_addressId, value);
            _lastPublished = value;
        }
    }
}
