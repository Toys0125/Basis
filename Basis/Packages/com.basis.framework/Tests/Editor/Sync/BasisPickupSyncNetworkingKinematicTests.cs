using Basis.Scripts.BasisSdk.Interactions;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public class BasisPickupSyncNetworkingKinematicTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void Initialization_PreservesAuthoredKinematicState(bool authoredKinematic)
        {
            GameObject go = CreatePickup(authoredKinematic, assignRigidbodyReference: true,
                out Rigidbody rigidbody, out _, out BasisPickupSyncNetworking sync);
            try
            {
                sync.InitializePickupRigidbody();

                Assert.AreEqual(authoredKinematic, rigidbody.isKinematic);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LocalOwner_ControlStateRestoresAuthoredKinematicState(bool authoredKinematic)
        {
            GameObject go = CreatePickup(authoredKinematic, assignRigidbodyReference: true,
                out Rigidbody rigidbody, out _, out BasisPickupSyncNetworking sync);
            try
            {
                sync.InitializePickupRigidbody();
                rigidbody.isKinematic = !authoredKinematic;
                sync.IsOwnedLocallyOnClient = true;

                sync.ControlState();

                Assert.AreEqual(authoredKinematic, rigidbody.isKinematic);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        public void RemoteOwner_ControlStateRespectsAuthoredStateAndDeadReckoning(
            bool authoredKinematic,
            bool remoteDeadReckon,
            bool expectedKinematic)
        {
            GameObject go = CreatePickup(authoredKinematic, assignRigidbodyReference: true,
                out Rigidbody rigidbody, out _, out BasisPickupSyncNetworking sync);
            try
            {
                sync.InitializePickupRigidbody();
                sync.IsOwnedLocallyOnClient = false;
                sync.RemoteDeadReckon = remoteDeadReckon;
                rigidbody.isKinematic = !expectedKinematic;

                sync.ControlState();

                Assert.AreEqual(expectedKinematic, rigidbody.isKinematic);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Initialization_AutoAssignsRigidbodyAndCapturesAuthoredState(bool authoredKinematic)
        {
            GameObject go = CreatePickup(authoredKinematic, assignRigidbodyReference: false,
                out Rigidbody rigidbody, out BasisPickupInteractable pickup, out BasisPickupSyncNetworking sync);
            try
            {
                Assert.IsNull(pickup.RigidRef);

                sync.InitializePickupRigidbody();
                rigidbody.isKinematic = !authoredKinematic;
                sync.IsOwnedLocallyOnClient = true;
                sync.ControlState();

                Assert.AreSame(rigidbody, pickup.RigidRef);
                Assert.AreEqual(authoredKinematic, rigidbody.isKinematic);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void LateRigidbodyResolution_DoesNotCaptureRuntimeMutatedKinematicState()
        {
            var go = new GameObject("pickup-late-rigidbody-test");
            go.SetActive(false);
            try
            {
                var pickup = go.AddComponent<BasisPickupInteractable>();
                var sync = go.AddComponent<BasisPickupSyncNetworking>();
                sync.BasisPickupInteractable = pickup;
                sync.Target = go.transform;

                sync.InitializePickupRigidbody();

                Rigidbody lateRigidbody = go.AddComponent<Rigidbody>();
                lateRigidbody.isKinematic = true;
                Assert.IsNull(pickup.RigidRef);
                sync.IsOwnedLocallyOnClient = true;

                sync.ControlState();

                Assert.AreSame(lateRigidbody, pickup.RigidRef,
                    "Late resolution should still populate the pickup reference.");
                Assert.IsFalse(lateRigidbody.isKinematic,
                    "A Rigidbody discovered after initialization must use the legacy dynamic fallback, not capture a runtime-mutated value as authored state.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static GameObject CreatePickup(
            bool authoredKinematic,
            bool assignRigidbodyReference,
            out Rigidbody rigidbody,
            out BasisPickupInteractable pickup,
            out BasisPickupSyncNetworking sync)
        {
            var go = new GameObject("pickup-kinematic-test");
            go.SetActive(false);

            rigidbody = go.AddComponent<Rigidbody>();
            rigidbody.isKinematic = authoredKinematic;

            pickup = go.AddComponent<BasisPickupInteractable>();
            if (assignRigidbodyReference)
            {
                pickup.RigidRef = rigidbody;
            }

            sync = go.AddComponent<BasisPickupSyncNetworking>();
            sync.BasisPickupInteractable = pickup;
            sync.Target = go.transform;
            return go;
        }
    }
}
