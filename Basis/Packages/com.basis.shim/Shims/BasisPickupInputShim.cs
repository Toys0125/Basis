using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;

namespace Basis.Shims
{
    /// <summary>
    /// Read-only bridge exposing the primary axis of the local input currently holding a pickup.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BasisPickupInputShim : MonoBehaviour
    {
        private BasisPickupInteractable pickup;
        private bool initialized;

        internal bool TryInitialize(BasisPickupInteractable target)
        {
            if (target == null)
            {
                return false;
            }

            if (initialized)
            {
                return ReferenceEquals(pickup, target);
            }

            pickup = target;
            initialized = true;
            return true;
        }

        /// <summary>
        /// Reads the dead-zoned primary joystick and click state from the local input holding the pickup.
        /// </summary>
        /// <param name="axis">The holding input's dead-zoned primary axis, or zero on failure.</param>
        /// <param name="axisClick">The holding input's primary-axis click state, or false on failure.</param>
        /// <returns>True only while the referenced pickup is held by a valid local input.</returns>
        public bool TryReadPrimary2DAxis(out Vector2 axis, out bool axisClick)
        {
            axis = Vector2.zero;
            axisClick = false;

            BasisPickupInteractable currentPickup = pickup;
            if (!initialized || currentPickup == null || !currentPickup.isActiveAndEnabled)
            {
                return false;
            }

            if (!currentPickup.TryGetActiveInteractingInput(out BasisInput input) || input == null)
            {
                return false;
            }

            BasisInputState state = input.CurrentInputState;
            if (state == null)
            {
                return false;
            }

            axis = state.Primary2DAxisDeadZoned;
            axisClick = state.Primary2DAxisClick;
            return true;
        }

        private void OnDestroy()
        {
            pickup = null;
        }
    }
}
