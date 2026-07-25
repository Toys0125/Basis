using System.Reflection;
using Basis.Scripts.BasisSdk.Interactions;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public class BasisPickupSyncNetworkingKinematicTests
    {
        private static readonly MethodInfo AwakeMethod = typeof(BasisPickupSyncNetworking).GetMethod(
            "Awake",
            BindingFlags.Instance | BindingFlags.NonPublic);

        [TestCase(false)]
        [TestCase(true)]
        public void Awake_PreservesAuthoredKinematicState(bool authoredKinematic)
        {
            GameObject go = CreatePickup(authoredKinematic, out Rigidbody rigidbody, out BasisPickupSyncNetworking sync);
            try
            {
                InvokeAwake(sync);

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
            GameObject go = CreatePickup(authoredKinematic, out Rigidbody rigidbody, out BasisPickupSyncNetworking sync);
            try
            {
                InvokeAwake(sync);
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
            GameObject go = CreatePickup(authoredKinematic, out Rigidbody rigidbody, out BasisPickupSyncNetworking sync);
            try
            {
                InvokeAwake(sync);
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

        private static GameObject CreatePickup(
            bool authoredKinematic,
            out Rigidbody rigidbody,
            out BasisPickupSyncNetworking sync)
        {
            var go = new GameObject("pickup-kinematic-test");
            go.SetActive(false);

            rigidbody = go.AddComponent<Rigidbody>();
            rigidbody.isKinematic = authoredKinematic;

            var pickup = go.AddComponent<BasisPickupInteractable>();
            pickup.RigidRef = rigidbody;

            sync = go.AddComponent<BasisPickupSyncNetworking>();
            sync.BasisPickupInteractable = pickup;
            sync.Target = go.transform;
            return go;
        }

        private static void InvokeAwake(BasisPickupSyncNetworking sync)
        {
            Assert.NotNull(AwakeMethod);
            AwakeMethod.Invoke(sync, null);
        }
    }
}
