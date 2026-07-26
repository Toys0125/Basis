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
    }
}
