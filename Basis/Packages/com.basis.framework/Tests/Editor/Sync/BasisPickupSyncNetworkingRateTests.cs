using NUnit.Framework;

namespace Basis.Tests.Sync
{
    public class BasisPickupSyncNetworkingRateTests
    {
        [TestCase(0.2f, 0.05f, 0.05f, TestName = "Held_5HzPickup_Uses20HzArmatureRate")]
        [TestCase(0.02f, 0.05f, 0.02f, TestName = "Held_50HzPickup_KeepsFasterConfiguredRate")]
        [TestCase(0.2f, 0f, 0.2f, TestName = "Held_NoArmatureRate_KeepsConfiguredRate")]
        [TestCase(0f, 0.05f, 0.05f, TestName = "Held_InvalidConfiguredRate_UsesArmatureRate")]
        public void ResolveHeldSendInterval_UsesFasterValidInterval(
            float configuredInterval,
            float armatureInterval,
            float expectedInterval)
        {
            float actual = BasisPickupSyncNetworking.ResolveHeldSendInterval(configuredInterval, armatureInterval);

            Assert.AreEqual(expectedInterval, actual, 1e-6f);
        }

        [Test]
        public void ResolveHeldSendInterval_RejectsNonFiniteIntervals()
        {
            Assert.AreEqual(0.05f,
                BasisPickupSyncNetworking.ResolveHeldSendInterval(float.NaN, 0.05f), 1e-6f);
            Assert.AreEqual(0.05f,
                BasisPickupSyncNetworking.ResolveHeldSendInterval(float.PositiveInfinity, 0.05f), 1e-6f);
            Assert.AreEqual(0.2f,
                BasisPickupSyncNetworking.ResolveHeldSendInterval(0.2f, float.NaN), 1e-6f);
            Assert.AreEqual(0.2f,
                BasisPickupSyncNetworking.ResolveHeldSendInterval(0.2f, float.NegativeInfinity), 1e-6f);
        }

        [Test]
        public void ResolveArmatureSendInterval_P2PDirectUsesP2PAvatarRateBeforeStaleServerRate()
        {
            float actual = BasisPickupSyncNetworking.ResolveArmatureSendInterval(
                useDirectP2P: true,
                p2pConnected: true,
                p2pInterval: 1f / 60f,
                transmitterInterval: 1f / 20f,
                serverIntervalMs: 50);

            Assert.AreEqual(1f / 60f, actual, 1e-6f);
        }

        [Test]
        public void HeldPickup_P2PDirectPromotes5HzPickupTo60HzAvatarRate()
        {
            float armatureInterval = BasisPickupSyncNetworking.ResolveArmatureSendInterval(
                useDirectP2P: true,
                p2pConnected: true,
                p2pInterval: 1f / 60f,
                transmitterInterval: 1f / 20f,
                serverIntervalMs: 50);

            float actual = BasisPickupSyncNetworking.ResolveHeldSendInterval(1f / 5f, armatureInterval);

            Assert.AreEqual(1f / 60f, actual, 1e-6f);
        }

        [Test]
        public void ResolveArmatureSendInterval_ServerOnlyIgnoresGloballyFastP2PTransmitterRate()
        {
            float actual = BasisPickupSyncNetworking.ResolveArmatureSendInterval(
                useDirectP2P: false,
                p2pConnected: true,
                p2pInterval: 1f / 60f,
                transmitterInterval: 1f / 60f,
                serverIntervalMs: 50);

            Assert.AreEqual(1f / 20f, actual, 1e-6f);
        }

        [Test]
        public void ResolveArmatureSendInterval_WithoutP2PUsesLiveTransmitterRate()
        {
            float actual = BasisPickupSyncNetworking.ResolveArmatureSendInterval(
                useDirectP2P: true,
                p2pConnected: false,
                p2pInterval: 0f,
                transmitterInterval: 1f / 20f,
                serverIntervalMs: 100);

            Assert.AreEqual(1f / 20f, actual, 1e-6f);
        }

        [Test]
        public void ResolveArmatureSendInterval_InvalidP2PRateFallsBackToTransmitterThenServer()
        {
            Assert.AreEqual(1f / 20f,
                BasisPickupSyncNetworking.ResolveArmatureSendInterval(
                    true, true, float.NaN, 1f / 20f, 50), 1e-6f);
            Assert.AreEqual(0.05f,
                BasisPickupSyncNetworking.ResolveArmatureSendInterval(
                    true, true, float.PositiveInfinity, 0f, 50), 1e-6f);
        }
    }
}
