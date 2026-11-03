using System.Net;
using System.Net.Sockets;
using Basis.Network.Core;
using BasisNetworkServer;
using Xunit;
using static SerializableBasis;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class P2PMixedFamilyTests
{
    private sealed class Introducer : IPeerIntroducer
    {
        public readonly List<(IPEndPoint A, IPEndPoint B, bool Predict, string Token)> Calls = new();
        public bool Initialize(PeerIntroductionRequest onRequest) => true;
        public void Shutdown() { }
        public void Introduce(IPEndPoint ai, IPEndPoint ae, IPEndPoint bi, IPEndPoint be, bool predictPorts, string token)
        {
            Assert.Equal(ae.AddressFamily, be.AddressFamily);
            Assert.Equal(ae.AddressFamily, ai.AddressFamily);
            Assert.Equal(be.AddressFamily, bi.AddressFamily);
            Calls.Add((ae, be, predictPorts, token));
        }
    }

    private readonly Introducer broker = new();
    private static IPEndPoint Ep(string ip, int port = 40000) => new(IPAddress.Parse(ip), port);
    public P2PMixedFamilyTests()
    {
        BasisServerP2PBroker.ResetForTests();
        BasisServerP2PBroker.RegisterSessionForTests("session", 1, 2, readyForPunch: true);
        BasisServerP2PBroker.ArmForTests("session", "private-a", "private-b");
    }
    private bool Send(string credential, string ip, int port = 40000) =>
        BasisServerP2PBroker.CollectCandidate(Ep(ip, port), Ep(ip, port), credential, broker);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IPv6SignalingDualStackDiscoversIPv4InEitherRole(bool dualStackIsTarget)
    {
        string dual = dualStackIsTarget ? "private-b" : "private-a";
        string v4 = dualStackIsTarget ? "private-a" : "private-b";
        Assert.True(Send(dual, "2001:db8::1"));
        Assert.True(Send(dual, "2001:db8::1")); // duplicate is not the second participant
        Assert.Empty(broker.Calls);
        Assert.True(Send(v4, "198.51.100.2"));
        Assert.Empty(broker.Calls);
        Assert.True(Send(dual, "192.0.2.1"));
        var call = Assert.Single(broker.Calls);
        Assert.Equal(AddressFamily.InterNetwork, call.A.AddressFamily);
        Assert.NotEqual(call.A, call.B);
        Assert.Equal("session", call.Token); // private credentials never reach the opposite peer
        Assert.False(BasisServerP2PBroker.HasOffloadedPairs);
        BasisServerP2PBroker.ApplyLinkUp(1, "session");
        Assert.False(BasisServerP2PBroker.HasOffloadedPairs);
        BasisServerP2PBroker.ApplyLinkUp(2, "session");
        Assert.True(BasisServerP2PBroker.IsP2POffloaded(1, 2));
    }

    [Fact]
    public void BothFamiliesCannotRaceParticipantsOntoDifferentConnections()
    {
        Send("private-a", "2001:db8::1");
        Send("private-b", "2001:db8::2");
        Send("private-a", "192.0.2.1");
        Send("private-b", "198.51.100.2");
        Assert.Equal(3, broker.Calls.Count);
        Assert.All(broker.Calls, call => Assert.Equal(AddressFamily.InterNetworkV6, call.A.AddressFamily));
    }

    [Fact]
    public void ReorderedAndLostRequestsRepeatIntroductionAndPrediction()
    {
        Send("private-b", "198.51.100.2");
        Send("private-a", "192.0.2.1");
        Send("private-b", "198.51.100.2"); // lost introduction response
        Send("private-a", "192.0.2.1");
        Assert.Equal(3, broker.Calls.Count);
        Assert.All(broker.Calls, call => Assert.True(call.Predict));
    }

    [Fact]
    public void SharedTokenThirdEndpointAndReplacementCannotFillOtherParticipant()
    {
        Assert.False(Send("session", "203.0.113.99"));
        Assert.False(Send("unrelated", "203.0.113.99"));
        Assert.True(Send("private-a", "192.0.2.1"));
        Assert.False(Send("private-a", "192.0.2.99"));
        Assert.False(Send("private-a", "192.0.2.1", 40001));
        Assert.True(Send("private-a", "2001:db8::1"));
        Assert.Empty(broker.Calls);
        Send("private-b", "198.51.100.2");
        Assert.Equal(Ep("192.0.2.1"), Assert.Single(broker.Calls).A);
    }

    [Fact]
    public void NativeIPv6IsPreservedAndNoCommonFamilyStaysRelayed()
    {
        Send("private-a", "2001:db8::1");
        Send("private-b", "198.51.100.2");
        Assert.Empty(broker.Calls);
        BasisServerP2PBroker.ApplyLinkUp(1, "session");
        BasisServerP2PBroker.ApplyLinkUp(2, "session");
        Assert.False(BasisServerP2PBroker.HasOffloadedPairs);
        Send("private-b", "2001:db8::2");
        Assert.Equal(AddressFamily.InterNetworkV6, Assert.Single(broker.Calls).A.AddressFamily);
    }

    [Fact]
    public void MappedIPv4AndNativeIPv6RemainDistinct()
    {
        Send("private-a", "::ffff:192.0.2.1");
        Send("private-b", "198.51.100.2");
        Assert.Equal(Ep("192.0.2.1"), Assert.Single(broker.Calls).A);
        Assert.Equal(Ep("2001:db8::1"), BasisP2PEndpoints.Canonical(Ep("2001:db8::1")));
        var writer = new LiteNetLib.Utils.NetDataWriter();
        writer.Put(Ep("::ffff:192.0.2.1"));
        Assert.Equal(7, writer.Length);
        Assert.Equal(Ep("192.0.2.1"), new LiteNetLib.Utils.NetDataReader(writer.CopyData()).GetIPEndPoint());
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)3)]
    public void UnsupportedPeersStayOnRelay(byte version)
    {
        Assert.False(BasisServerP2PBroker.SupportsDiscovery(version));
        Assert.True(BasisServerP2PBroker.SupportsDiscovery(2));
        Assert.False(Send("session", "198.51.100.2"));
        Assert.False(Send("session", "198.51.100.2", 40001));
        Assert.Empty(broker.Calls);
        Assert.False(BasisServerP2PBroker.HasOffloadedPairs);
    }

    [Fact]
    public void AuthenticatedLinkLostAllowsNewNatMappingAndResumesRelay()
    {
        Send("private-a", "192.0.2.1");
        Send("private-b", "198.51.100.2");
        BasisServerP2PBroker.ApplyLinkUp(1, "session");
        BasisServerP2PBroker.ApplyLinkUp(2, "session");
        BasisServerP2PBroker.ApplyLinkLost(1, "session", 2);
        Assert.False(BasisServerP2PBroker.HasOffloadedPairs);
        Assert.True(Send("private-b", "198.51.100.2", 40001));
        Assert.Single(broker.Calls); // cannot introduce against stale A
        Assert.True(Send("private-a", "192.0.2.1", 40002));
        Assert.Equal(2, broker.Calls.Count);
        Assert.Equal(40002, broker.Calls[1].A.Port);
        Assert.Equal(40001, broker.Calls[1].B.Port);
    }

    [Fact]
    public void SignalExtensionKeepsLegacyPrefixAndKeys()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var legacy = new BasisP2PSignalMessage { otherPlayerId = 2, sessionToken = "session", ephemeralPublicKey = key };
        var oldWriter = new NetDataWriter();
        legacy.Serialize(oldWriter);
        var modern = legacy;
        modern.discoveryVersion = 2;
        modern.discoveryCredential = "private-a";
        modern.discoveryIPv4Host = "broker.example";
        var newWriter = new NetDataWriter();
        modern.Serialize(newWriter);
        Assert.Equal(oldWriter.CopyData(), newWriter.CopyData().Take(oldWriter.Length).ToArray());
        BasisP2PSignalMessage parsed = default;
        parsed.Deserialize(new NetDataReader(oldWriter.CopyData()));
        Assert.Equal(0, parsed.discoveryVersion);
        Assert.Null(parsed.discoveryCredential);
        parsed.Deserialize(new NetDataReader(newWriter.CopyData()));
        Assert.Equal(2, parsed.discoveryVersion);
        Assert.Equal("private-a", parsed.discoveryCredential);
        Assert.Equal(key, parsed.ephemeralPublicKey);
    }

    [Fact]
    public void UnrelatedLinkLostCannotClearOffload()
    {
        Send("private-a", "192.0.2.1");
        Send("private-b", "198.51.100.2");
        BasisServerP2PBroker.ApplyLinkUp(1, "session");
        BasisServerP2PBroker.ApplyLinkUp(2, "session");
        BasisServerP2PBroker.ApplyLinkLost(3, "session", 2);
        Assert.True(BasisServerP2PBroker.IsP2POffloaded(1, 2));
        BasisServerP2PBroker.RemovePeer(1);
        Assert.False(Send("private-a", "192.0.2.1"));
    }
}
