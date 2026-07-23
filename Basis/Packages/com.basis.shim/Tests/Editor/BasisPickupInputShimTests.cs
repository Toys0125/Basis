using System;
using System.Reflection;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Shims.Tests
{
    public sealed class BasisPickupInputShimTestInput : BasisInput
    {
        public override void LateDoPollData() { }
        public override void ShowTrackedVisual() { }
        public override void PlayHaptic(float duration = 0.25f, float amplitude = 0.5f, float frequency = 0.5f) { }
        public override void PlaySoundEffect(string soundEffectName, float volume) { }
        public new void OnDestroy() { }
    }

    public sealed class BasisPickupInputShimTestPickup : BasisPickupInteractable
    {
        public override void Awake() { }
        public override void OnDestroy() { }
    }

    [Cilboxable]
    public sealed class BasisPickupInputShimCilboxProbe : MonoBehaviour
    {
        [SerializeField]
        private BasisPickupInteractable pickup;

        private BasisPickupInputShim shim;

        public Vector2 LastAxis { get; private set; }
        public bool LastAxisClick { get; private set; }

        private void Start()
        {
            shim = SafeUtil.MakePickupInputReadable(pickup);
        }

        public void OnPickupUse(BasisPickUpUseMode mode)
        {
            if (mode != BasisPickUpUseMode.OnPickUpStillDown || shim == null)
            {
                return;
            }

            if (shim.TryReadPrimary2DAxis(out Vector2 axis, out bool axisClick))
            {
                LastAxis = axis;
                LastAxisClick = axisClick;
            }
        }
    }

    public sealed class BasisPickupInputShimTests
    {
        private static readonly FieldInfo WrapperStateField = typeof(BasisInputWrapper).GetField(
            "State",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private GameObject pickupObject;
        private BasisPickupInteractable pickup;
        private BasisPickupInputShim shim;
        private BasisPickupInputShimTestInput desktop;
        private BasisPickupInputShimTestInput left;
        private BasisPickupInputShimTestInput right;

        [SetUp]
        public void SetUp()
        {
            pickupObject = new GameObject("pickup-input-shim-test");
            pickup = pickupObject.AddComponent<BasisPickupInputShimTestPickup>();
            pickup.Inputs = new BasisInputSources(0);
            shim = SafeUtil.MakePickupInputReadable(pickup);

            desktop = CreateInput("desktop", new Vector2(0.2f, 0.3f), false);
            left = CreateInput("left", new Vector2(-0.7f, 0.4f), false);
            right = CreateInput("right", new Vector2(0.8f, -0.5f), true);
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
        public void NoActiveInteraction_FailsClosedAndZerosOutputs()
        {
            Vector2 axis = Vector2.one;
            bool axisClick = true;

            Assert.That(shim.TryReadPrimary2DAxis(out axis, out axisClick), Is.False);
            Assert.That(axis, Is.EqualTo(Vector2.zero));
            Assert.That(axisClick, Is.False);
        }

        [Test]
        public void DesktopInteraction_HasPriorityOverHands()
        {
            SetWrapper(ref pickup.Inputs.desktopCenterEye, desktop, BasisBoneTrackedRole.CenterEye, BasisInteractInputState.Interacting);
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Interacting);
            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Interacting);

            AssertReadMatches(desktop);
        }

        [Test]
        public void LeftHandInteraction_ReadsOnlyLeftController()
        {
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Interacting);
            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Ignored);

            AssertReadMatches(left);
        }

        [Test]
        public void RightHandInteraction_ReadsOnlyRightController()
        {
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Ignored);
            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Interacting);

            AssertReadMatches(right);
        }

        [Test]
        public void BothHandsInteracting_FollowsDominantHandSelection()
        {
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Interacting);
            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Interacting);

            BasisInput expected = BasisDominantHand.IsLeftHanded ? left : right;
            AssertReadMatches(expected);
        }

        [Test]
        public void DropStealAndHandTransfer_DoNotRetainStaleInput()
        {
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Interacting);
            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Ignored);
            AssertReadMatches(left);

            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Ignored);
            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Interacting);
            AssertReadMatches(right);

            SetWrapper(ref pickup.Inputs.rightHand, right, BasisBoneTrackedRole.RightHand, BasisInteractInputState.Ignored);
            Assert.That(shim.TryReadPrimary2DAxis(out Vector2 axis, out bool axisClick), Is.False);
            Assert.That(axis, Is.EqualTo(Vector2.zero));
            Assert.That(axisClick, Is.False);
        }

        [Test]
        public void RepeatedFactoryCalls_ReuseExactlyOneShim()
        {
            BasisPickupInputShim second = SafeUtil.MakePickupInputReadable(pickup);
            BasisPickupInputShim third = SafeUtil.MakePickupInputReadable(pickup);

            Assert.That(second, Is.SameAs(shim));
            Assert.That(third, Is.SameAs(shim));
            Assert.That(pickupObject.GetComponents<BasisPickupInputShim>(), Has.Length.EqualTo(1));
        }

        [Test]
        public void InvalidReferences_FailClosedWithoutThrowing()
        {
            Assert.That(SafeUtil.MakePickupInputReadable(null), Is.Null);

            pickup.enabled = false;
            Assert.That(shim.TryReadPrimary2DAxis(out Vector2 axis, out bool axisClick), Is.False);
            Assert.That(axis, Is.EqualTo(Vector2.zero));
            Assert.That(axisClick, Is.False);
        }

        [Test]
        public void RepeatedReads_AllocateNoManagedMemory()
        {
            SetWrapper(ref pickup.Inputs.leftHand, left, BasisBoneTrackedRole.LeftHand, BasisInteractInputState.Interacting);
            for (int index = 0; index < 128; index++)
            {
                shim.TryReadPrimary2DAxis(out _, out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10000; index++)
            {
                shim.TryReadPrimary2DAxis(out _, out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero);
        }

        [Test]
        public void RepeatedFactoryReuse_AllocatesNoManagedMemory()
        {
            for (int index = 0; index < 128; index++)
            {
                SafeUtil.MakePickupInputReadable(pickup);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10000; index++)
            {
                SafeUtil.MakePickupInputReadable(pickup);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero);
        }

        [Test]
        public void ShimHasNoPeriodicUnityLoopOrCoroutine()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type type = typeof(BasisPickupInputShim);

            Assert.That(type.GetMethod("Update", flags), Is.Null);
            Assert.That(type.GetMethod("LateUpdate", flags), Is.Null);
            Assert.That(type.GetMethod("FixedUpdate", flags), Is.Null);
            foreach (MethodInfo method in type.GetMethods(flags))
            {
                Assert.That(
                    typeof(System.Collections.IEnumerator).IsAssignableFrom(method.ReturnType),
                    Is.False,
                    $"{method.Name} must not be a coroutine");
            }
        }

        [Test]
        public void CilboxBoundary_AllowsShimButRejectsRawInputAndPickupAccessor()
        {
            var cilbox = new CilboxPropBasis();
            var usage = new CilboxUsage(cilbox);
            Serializee[] noParameters = Array.Empty<Serializee>();

            Assert.That(cilbox.CheckTypeAllowed(typeof(BasisPickupInputShim).FullName), Is.True);
            Assert.That(cilbox.CheckTypeAllowed(typeof(BasisPickupInteractable).FullName), Is.True);
            Assert.That(cilbox.CheckFieldAllowed(typeof(BasisInput).FullName, nameof(BasisInput.CurrentInputState)), Is.False);

            MethodInfo readMethod = typeof(BasisPickupInputShim).GetMethod(nameof(BasisPickupInputShim.TryReadPrimary2DAxis));
            Serializee[] readParameters =
            {
                CilboxUtil.GetSerializeeFromNativeType(typeof(Vector2).MakeByRefType()),
                CilboxUtil.GetSerializeeFromNativeType(typeof(bool).MakeByRefType())
            };
            Assert.That(usage.GetNativeMethodFromTypeAndName(
                typeof(BasisPickupInputShim),
                readMethod.Name,
                readParameters,
                noParameters,
                readMethod.ToString()), Is.EqualTo(readMethod));

            MethodInfo factoryMethod = typeof(SafeUtil).GetMethod(nameof(SafeUtil.MakePickupInputReadable));
            Serializee[] factoryParameters =
            {
                CilboxUtil.GetSerializeeFromNativeType(typeof(BasisPickupInteractable))
            };
            Assert.That(usage.GetNativeMethodFromTypeAndName(
                typeof(SafeUtil),
                factoryMethod.Name,
                factoryParameters,
                noParameters,
                factoryMethod.ToString()), Is.EqualTo(factoryMethod));

            Assert.That(cilbox.CheckMethodAllowed(
                out _,
                typeof(BasisPickupInteractable),
                nameof(BasisPickupInteractable.TryGetActiveInteractingInput),
                noParameters,
                noParameters,
                string.Empty), Is.False);

            Assert.That(CilboxUtil.HasCilboxableAttribute(typeof(BasisPickupInputShimCilboxProbe)), Is.True);
        }

        private BasisPickupInputShimTestInput CreateInput(string name, Vector2 rawAxis, bool axisClick)
        {
            GameObject inputObject = new GameObject(name);
            BasisPickupInputShimTestInput input = inputObject.AddComponent<BasisPickupInputShimTestInput>();
            input.CurrentInputState.Primary2DAxisRaw = rawAxis;
            input.CurrentInputState.Primary2DAxisClick = axisClick;
            return input;
        }

        private static void DestroyInput(BasisPickupInputShimTestInput input)
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

        private void AssertReadMatches(BasisInput expected)
        {
            Assert.That(shim.TryReadPrimary2DAxis(out Vector2 axis, out bool axisClick), Is.True);
            Assert.That(axis, Is.EqualTo(expected.CurrentInputState.Primary2DAxisDeadZoned));
            Assert.That(axisClick, Is.EqualTo(expected.CurrentInputState.Primary2DAxisClick));
        }
    }
}
