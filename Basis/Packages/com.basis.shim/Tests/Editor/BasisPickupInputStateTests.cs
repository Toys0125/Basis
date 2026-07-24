using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Shims.Tests
{
    public sealed class BasisInputSnapshotTestInput : BasisInput
    {
        public override void LateDoPollData() { }
        public override void ShowTrackedVisual() { }
        public override void PlayHaptic(float duration = 0.25f, float amplitude = 0.5f, float frequency = 0.5f) { }
        public override void PlaySoundEffect(string soundEffectName, float volume) { }
        public new void OnDestroy() { }
    }

    public sealed class BasisInputSnapshotTestPickup : BasisPickupInteractable
    {
        public override void Awake() { }
        public override void OnDestroy() { }
    }

    [Cilboxable]
    public sealed class BasisInputSnapshotCilboxProbe : MonoBehaviour
    {
        [SerializeField]
        private BasisPickupInteractable pickup;

        public float LastTrigger { get; private set; }
        public Vector2 LastPrimaryAxisRaw { get; private set; }
        public Vector2 LastPrimaryAxis { get; private set; }
        public bool LastPrimaryAxisClick { get; private set; }
        public bool LastGripButton { get; private set; }

        public void OnPickupUse(BasisPickUpUseMode mode)
        {
            if (mode != BasisPickUpUseMode.OnPickUpStillDown ||
                pickup == null ||
                !pickup.TryGetActiveInputSnapshot(out BasisInputSnapshot state))
            {
                return;
            }

            LastTrigger = state.Trigger;
            LastPrimaryAxisRaw = state.Primary2DAxisRaw;
            LastPrimaryAxis = state.Primary2DAxisDeadZoned;
            LastPrimaryAxisClick = state.Primary2DAxisClick;
            LastGripButton = state.GripButton;
        }
    }

    public sealed class BasisInputSnapshotTests
    {
        private static readonly FieldInfo WrapperStateField = typeof(BasisInputWrapper).GetField(
            "State",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private GameObject pickupObject;
        private BasisPickupInteractable pickup;
        private BasisInputSnapshotTestInput desktop;
        private BasisInputSnapshotTestInput left;
        private BasisInputSnapshotTestInput right;

        [SetUp]
        public void SetUp()
        {
            Assert.That(WrapperStateField, Is.Not.Null);

            pickupObject = new GameObject("pickup-input-snapshot-test");
            pickup = pickupObject.AddComponent<BasisInputSnapshotTestPickup>();
            pickup.Inputs = new BasisInputSources(0);

            desktop = CreateInput("desktop");
            SetInputState(
                desktop.CurrentInputState,
                0.15f,
                0.25f,
                new Vector2(0.2f, 0.3f),
                false,
                new Vector2(0.1f, -0.2f),
                true,
                true,
                false,
                true,
                false);

            left = CreateInput("left");
            SetInputState(
                left.CurrentInputState,
                0.35f,
                0.45f,
                new Vector2(-0.7f, 0.4f),
                false,
                new Vector2(0.6f, 0.2f),
                false,
                false,
                true,
                false,
                true);

            right = CreateInput("right");
            SetInputState(
                right.CurrentInputState,
                0.75f,
                0.85f,
                new Vector2(0.8f, -0.5f),
                true,
                new Vector2(-0.4f, 0.9f),
                true,
                true,
                true,
                false,
                true);
        }

        [TearDown]
        public void TearDown()
        {
            if (pickup != null)
            {
                pickup.Inputs = new BasisInputSources(0);
            }

            DestroyInput(desktop);
            DestroyInput(left);
            DestroyInput(right);
            if (pickupObject != null)
            {
                UnityEngine.Object.DestroyImmediate(pickupObject);
            }
        }

        [Test]
        public void NoActiveInteraction_FailsClosedAndReturnsDefault()
        {
            AssertReadFailsClosed();
        }

        [Test]
        public void DesktopInteraction_HasPriorityOverHands()
        {
            SetWrapper(ref pickup.Inputs.desktopCenterEye, desktop, BasisBoneTrackedRole.CenterEye, BasisInteractInputState.Interacting);
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Interacting);
            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Interacting);

            AssertReadMatches(desktop.CurrentInputState);
        }

        [Test]
        public void LeftHandInteraction_ReadsLeftController()
        {
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Interacting);
            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Ignored);

            AssertReadMatches(left.CurrentInputState);
        }

        [Test]
        public void RightHandInteraction_ReadsRightController()
        {
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Ignored);
            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Interacting);

            AssertReadMatches(right.CurrentInputState);
        }

        [Test]
        public void BothHandsInteracting_FollowsDominantHandSelection()
        {
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Interacting);
            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Interacting);

            BasisInputState expected = BasisDominantHand.IsLeftHanded
                ? left.CurrentInputState
                : right.CurrentInputState;
            AssertReadMatches(expected);
        }

        [Test]
        public void DropStealAndTransfer_DoNotRetainStaleState()
        {
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Interacting);
            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Ignored);
            AssertReadMatches(left.CurrentInputState);

            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Ignored);
            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Interacting);
            AssertReadMatches(right.CurrentInputState);

            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Ignored);
            AssertReadFailsClosed();
        }

        [Test]
        public void DisabledPickup_FailsClosed()
        {
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Interacting);
            pickup.enabled = false;

            AssertReadFailsClosed();
        }

        [Test]
        public void Snapshot_DoesNotRetainMutableNativeState()
        {
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Interacting);
            Assert.That(pickup.TryGetActiveInputSnapshot(out BasisInputSnapshot snapshot), Is.True);

            float trigger = snapshot.Trigger;
            Vector2 primaryAxis = snapshot.Primary2DAxisRaw;
            bool grip = snapshot.GripButton;

            left.CurrentInputState.Trigger = 1f;
            left.CurrentInputState.Primary2DAxisRaw = Vector2.one;
            left.CurrentInputState.GripButton = !grip;

            Assert.That(snapshot.Trigger, Is.EqualTo(trigger));
            Assert.That(snapshot.Primary2DAxisRaw, Is.EqualTo(primaryAxis));
            Assert.That(snapshot.GripButton, Is.EqualTo(grip));
        }

        [Test]
        public void Snapshot_MirrorsEveryReadableInputPropertyAndIsImmutable()
        {
            Type snapshotType = typeof(BasisInputSnapshot);

            Assert.That(snapshotType.IsValueType, Is.True);
            Assert.That(snapshotType.IsDefined(typeof(IsReadOnlyAttribute), false), Is.True);

            foreach (FieldInfo field in snapshotType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(field.IsInitOnly, Is.True, $"{field.Name} must be readonly");
            }

            foreach (PropertyInfo inputProperty in typeof(BasisInputState).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (inputProperty.GetMethod == null)
                {
                    continue;
                }

                PropertyInfo snapshotProperty = snapshotType.GetProperty(inputProperty.Name);
                Assert.That(snapshotProperty, Is.Not.Null, $"Snapshot is missing {inputProperty.Name}");
                Assert.That(snapshotProperty.PropertyType, Is.EqualTo(inputProperty.PropertyType));
                Assert.That(snapshotProperty.GetMethod, Is.Not.Null);
                Assert.That(snapshotProperty.SetMethod, Is.Null, $"{snapshotProperty.Name} must not be writable");
            }
        }

        [Test]
        public void RepeatedReads_AllocateNoManagedMemory()
        {
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Interacting);
            for (int index = 0; index < 128; index++)
            {
                pickup.TryGetActiveInputSnapshot(out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10000; index++)
            {
                pickup.TryGetActiveInputSnapshot(out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero);
        }

        [Test]
        public void CilboxBoundary_AllowsImmutableInputSnapshotButRejectsNativeState()
        {
            var cilbox = new CilboxPropBasis();
            var usage = new CilboxUsage(cilbox);
            Serializee[] noParameters = Array.Empty<Serializee>();

            Assert.That(cilbox.CheckTypeAllowed(typeof(BasisPickupInteractable).FullName), Is.True);
            Assert.That(cilbox.CheckTypeAllowed(typeof(BasisInputSnapshot).FullName), Is.True);
            Assert.That(cilbox.CheckTypeAllowed(typeof(BasisInputState).FullName), Is.False);
            Assert.That(cilbox.CheckFieldAllowed(typeof(BasisInput).FullName, nameof(BasisInput.CurrentInputState)), Is.False);

            MethodInfo readMethod = typeof(BasisPickupInteractable).GetMethod(
                nameof(BasisPickupInteractable.TryGetActiveInputSnapshot));
            Assert.That(readMethod, Is.Not.Null);
            Serializee[] readParameters =
            {
                CilboxUtil.GetSerializeeFromNativeType(typeof(BasisInputSnapshot).MakeByRefType())
            };
            Assert.That(usage.GetNativeMethodFromTypeAndName(
                typeof(BasisPickupInteractable),
                readMethod.Name,
                readParameters,
                noParameters,
                readMethod.ToString()), Is.EqualTo(readMethod));

            foreach (PropertyInfo property in typeof(BasisInputSnapshot).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                MethodInfo getter = property.GetMethod;
                Assert.That(getter, Is.Not.Null);
                Assert.That(usage.GetNativeMethodFromTypeAndName(
                    typeof(BasisInputSnapshot),
                    getter.Name,
                    noParameters,
                    noParameters,
                    getter.ToString()), Is.EqualTo(getter));
                Assert.That(property.SetMethod, Is.Null);
            }

            Assert.That(CilboxUtil.HasCilboxableAttribute(typeof(BasisInputSnapshotCilboxProbe)), Is.True);
        }

        private BasisInputSnapshotTestInput CreateInput(string name)
        {
            GameObject inputObject = new GameObject(name);
            return inputObject.AddComponent<BasisInputSnapshotTestInput>();
        }

        private static void SetInputState(
            BasisInputState state,
            float trigger,
            float secondaryTrigger,
            Vector2 primaryAxis,
            bool primaryAxisClick,
            Vector2 secondaryAxis,
            bool secondaryAxisClick,
            bool primaryButton,
            bool secondaryButton,
            bool systemOrMenuButton,
            bool gripButton)
        {
            state.Trigger = trigger;
            state.SecondaryTrigger = secondaryTrigger;
            state.Primary2DAxisRaw = primaryAxis;
            state.Primary2DAxisClick = primaryAxisClick;
            state.Secondary2DAxisRaw = secondaryAxis;
            state.Secondary2DAxisClick = secondaryAxisClick;
            state.PrimaryButtonGetState = primaryButton;
            state.SecondaryButtonGetState = secondaryButton;
            state.SystemOrMenuButton = systemOrMenuButton;
            state.GripButton = gripButton;
        }

        private static void DestroyInput(BasisInputSnapshotTestInput input)
        {
            if (input != null)
            {
                UnityEngine.Object.DestroyImmediate(input.gameObject);
            }
        }

        private static void SetWrapper(
            ref BasisInputWrapper target,
            BasisInput source,
            BasisBoneTrackedRole role,
            BasisInteractInputState state)
        {
            object boxed = new BasisInputWrapper
            {
                Source = source,
                Role = role
            };
            WrapperStateField.SetValue(boxed, state);
            target = (BasisInputWrapper)boxed;
        }

        private void AssertReadMatches(BasisInputState expected)
        {
            Assert.That(pickup.TryGetActiveInputSnapshot(out BasisInputSnapshot actual), Is.True);

            foreach (PropertyInfo inputProperty in typeof(BasisInputState).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (inputProperty.GetMethod == null)
                {
                    continue;
                }

                PropertyInfo snapshotProperty = typeof(BasisInputSnapshot).GetProperty(inputProperty.Name);
                Assert.That(snapshotProperty, Is.Not.Null, $"Snapshot is missing {inputProperty.Name}");
                Assert.That(
                    snapshotProperty.GetValue(actual),
                    Is.EqualTo(inputProperty.GetValue(expected)),
                    inputProperty.Name);
            }
        }

        private void AssertReadFailsClosed()
        {
            Assert.That(pickup.TryGetActiveInputSnapshot(out BasisInputSnapshot state), Is.False);
            Assert.That(state, Is.EqualTo(default(BasisInputSnapshot)));
        }
    }
}
