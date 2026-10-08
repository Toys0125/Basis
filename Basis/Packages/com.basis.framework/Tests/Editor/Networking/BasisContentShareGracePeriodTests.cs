using Basis.Network.Core;
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.TestTools;
using static SerializableBasis;

namespace Basis.Tests.Networking
{
    /// <summary>
    /// Simulates a server orb whose Addressables instantiation has not completed yet.
    /// This exercises client removal and expiry without requiring a scene or a running network peer.
    /// </summary>
    public class BasisContentShareGracePeriodTests
    {
        private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private FieldInfo _delayField;
        private FieldInfo _dispatchField;
        private Func<TimeSpan, Task> _originalDelay;
        private Action<Action> _originalDispatch;
        private TaskCompletionSource<bool> _releaseDelay;
        private ConcurrentQueue<Action> _scheduledActions;
        private int _scheduledDelays;

        private static HashSet<string> PendingCreates =>
            (HashSet<string>)typeof(BasisContentShareManager).GetField("PendingSpheres", PrivateStatic).GetValue(null);

        private static Dictionary<string, object> PendingExpiry =>
            (Dictionary<string, object>)typeof(BasisContentShareManager).GetField("PendingDepartureCleanup", PrivateStatic).GetValue(null);

        [SetUp]
        public void SetUp()
        {
            BasisContentShareManager.Reset();
            _delayField = typeof(BasisContentShareManager).GetField("DepartureCleanupDelay", PrivateStatic);
            _dispatchField = typeof(BasisContentShareManager).GetField("DepartureCleanupDispatch", PrivateStatic);
            Assert.That(_delayField, Is.Not.Null);
            Assert.That(_dispatchField, Is.Not.Null);
            _originalDelay = (Func<TimeSpan, Task>)_delayField.GetValue(null);
            _originalDispatch = (Action<Action>)_dispatchField.GetValue(null);
            _releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _scheduledActions = new ConcurrentQueue<Action>();
            _scheduledDelays = 0;
            _delayField.SetValue(null, (Func<TimeSpan, Task>)(duration =>
            {
                Assert.That(duration, Is.EqualTo(TimeSpan.FromMinutes(1)));
                _scheduledDelays++;
                return _releaseDelay.Task;
            }));
            _dispatchField.SetValue(null, (Action<Action>)(action => _scheduledActions.Enqueue(action)));
        }

        [TearDown]
        public void TearDown()
        {
            BasisContentShareManager.Reset();
            _releaseDelay.TrySetResult(true);
            _delayField.SetValue(null, _originalDelay);
            _dispatchField.SetValue(null, _originalDispatch);
        }

        private static void ReceiveCleanup(string sphereId, bool ownerDeparted)
        {
            ServerContentShareCleanupMessage cleanup = new ServerContentShareCleanupMessage
            {
                playerIdMessage = new PlayerIdMessage { playerID = 42 },
                contentShareCleanupMessage = new ContentShareCleanupMessage { SphereNetID = sphereId },
                OwnerDeparted = ownerDeparted,
            };
            NetDataWriter writer = new NetDataWriter();
            cleanup.Serialize(writer);
            byte[] payload = writer.CopyData();
            NetPacketReader reader = NetPacketReader.Create(payload, 0, payload.Length, () => { });
            try
            {
                BasisContentShareManager.HandleContentShareCleanup(reader);
            }
            finally
            {
                reader.Recycle();
            }
        }

        private IEnumerator ReleaseAndRunExpiry()
        {
            _releaseDelay.SetResult(true);
            // Yield frames so Unity's main-thread synchronization context can resume the timer.
            float deadline = Time.realtimeSinceStartup + 5f;
            while (_scheduledActions.IsEmpty && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(_scheduledActions.TryDequeue(out Action action), Is.True,
                "The delayed task did not dispatch its main-thread cleanup.");
            action();
        }

        [UnityTest]
        public IEnumerator DepartedServerOrbInFlight_IsKeptLocallyUntilExpiry()
        {
            const string id = "departure-test-1";
            PendingCreates.Add(id);
            ReceiveCleanup(id, true);

            Assert.That(PendingCreates.Contains(id), Is.True);
            Assert.That(PendingExpiry.ContainsKey(id), Is.True);
            Assert.That(_scheduledDelays, Is.EqualTo(1));

            yield return ReleaseAndRunExpiry();
            Assert.That(PendingCreates.Contains(id), Is.False);
            Assert.That(PendingExpiry.ContainsKey(id), Is.False);
        }

        [Test]
        public void OrdinaryCleanup_RemovesImmediately()
        {
            const string id = "departure-test-2";
            PendingCreates.Add(id);
            ReceiveCleanup(id, false);

            Assert.That(PendingCreates.Contains(id), Is.False);
            Assert.That(PendingExpiry.ContainsKey(id), Is.False);
            Assert.That(_scheduledDelays, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ResetInvalidatesOldExpiry_EvenWhenSphereIdIsReused()
        {
            const string id = "departure-test-3";
            PendingCreates.Add(id);
            ReceiveCleanup(id, true);
            BasisContentShareManager.Reset();
            PendingCreates.Add(id);

            yield return ReleaseAndRunExpiry();
            Assert.That(PendingCreates.Contains(id), Is.True);
            Assert.That(PendingExpiry.ContainsKey(id), Is.False);
        }

        [Test]
        public void LocalDeletionOfDepartedOrb_DoesNotAskServerToDeleteAgain()
        {
            const string id = "departure-test-4";
            PendingCreates.Add(id);
            ReceiveCleanup(id, true);
            BasisContentShareManager.RequestRemoveSphere(id);

            Assert.That(PendingCreates.Contains(id), Is.False);
            Assert.That(PendingExpiry.ContainsKey(id), Is.False);
        }

        [Test]
        public void DuplicateDeparture_DoesNotExtendGracePeriod()
        {
            const string id = "departure-test-5";
            PendingCreates.Add(id);
            ReceiveCleanup(id, true);
            ReceiveCleanup(id, true);

            Assert.That(_scheduledDelays, Is.EqualTo(1));
            Assert.That(PendingExpiry.ContainsKey(id), Is.True);
        }

        [Test]
        public void UnknownDeparture_DoesNotKeepOrCreateAnOrb()
        {
            ReceiveCleanup("departure-test-unseen", true);
            Assert.That(_scheduledDelays, Is.Zero);
            Assert.That(PendingExpiry, Is.Empty);
        }

        [Test]
        public void ExplicitCleanupAfterDeparture_OverridesGracePeriod()
        {
            const string id = "departure-test-6";
            PendingCreates.Add(id);
            ReceiveCleanup(id, true);
            ReceiveCleanup(id, false);

            Assert.That(PendingCreates.Contains(id), Is.False);
            Assert.That(PendingExpiry.ContainsKey(id), Is.False);
        }
    }
}
