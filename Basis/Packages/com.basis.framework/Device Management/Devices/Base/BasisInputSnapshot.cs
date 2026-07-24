using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices
{
    /// <summary>
    /// Immutable point-in-time copy of a <see cref="BasisInputState"/>.
    /// It contains no reference to the mutable source input state.
    /// </summary>
    public readonly struct BasisInputSnapshot
    {
        public bool GripButton { get; }
        public bool SystemOrMenuButton { get; }
        public bool PrimaryButtonGetState { get; }
        public bool SecondaryButtonGetState { get; }
        public bool Secondary2DAxisClick { get; }
        public bool Primary2DAxisClick { get; }
        public float Trigger { get; }
        public float SecondaryTrigger { get; }
        public Vector2 Primary2DAxisRaw { get; }
        public Vector2 Secondary2DAxisRaw { get; }
        public Vector2 Primary2DAxisDeadZoned { get; }
        public Vector2 Secondary2DAxisDeadZoned { get; }
        public Vector2 Primary2DAxisButterfly { get; }
        public Vector2 Secondary2DAxisButterfly { get; }

        internal BasisInputSnapshot(BasisInputState state)
        {
            GripButton = state.GripButton;
            SystemOrMenuButton = state.SystemOrMenuButton;
            PrimaryButtonGetState = state.PrimaryButtonGetState;
            SecondaryButtonGetState = state.SecondaryButtonGetState;
            Secondary2DAxisClick = state.Secondary2DAxisClick;
            Primary2DAxisClick = state.Primary2DAxisClick;
            Trigger = state.Trigger;
            SecondaryTrigger = state.SecondaryTrigger;
            Primary2DAxisRaw = state.Primary2DAxisRaw;
            Secondary2DAxisRaw = state.Secondary2DAxisRaw;
            Primary2DAxisDeadZoned = state.Primary2DAxisDeadZoned;
            Secondary2DAxisDeadZoned = state.Secondary2DAxisDeadZoned;
            Primary2DAxisButterfly = state.Primary2DAxisButterfly;
            Secondary2DAxisButterfly = state.Secondary2DAxisButterfly;
        }
    }
}
