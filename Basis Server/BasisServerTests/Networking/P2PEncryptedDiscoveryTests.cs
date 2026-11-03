using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Basis.Network.Core;
using BasisNetworkServer;
using Xunit;
using L = LiteNetLib;

namespace BasisServerTests;

// Real local UDP + encrypted direct payloads. These are deliberately not WAN,
// Unity, DID-authentication, or an IPv6 main-server connection test.
[Collection("BasisServer shared network statics")]
public class P2PEncryptedDiscoveryTests
{
    private sealed class WireIntroducer : IPeerIntroducer
    {
        private readonly L.NetManager server;
        public WireIntroducer(L.NetManager server) => this.server = server;
        public bool Initialize(PeerIntroductionRequest onRequest) => true;
        public void Shutdown() { }
        public void Introduce(IPEndPoint ai, IPEndPoint ae, IPEndPoint bi, IPEndPoint be, bool predictPorts, string token)
            => server.NatPunchModule.NatIntroduce(ai, ae, bi, be, token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DirectSocketDiscoveryCarriesEncryptedPayloads(bool dualStackIsTarget, bool nativeIPv6)
    {
        Assert.True(Socket.OSSupportsIPv6);
        BasisServerP2PBroker.ResetForTests();
        BasisServerP2PBroker.RegisterSessionForTests("encrypted-session", 1, 2, readyForPunch: true);
        BasisServerP2PBroker.ArmForTests("encrypted-session", "credential-a", "credential-b");
        string aCredential = dualStackIsTarget ? "credential-b" : "credential-a";
        string bCredential = dualStackIsTarget ? "credential-a" : "credential-b";
        int aId = dualStackIsTarget ? 2 : 1;
        int bId = dualStackIsTarget ? 1 : 2;
        BasisCryptoHandshake.GenerateKeyPair(out var aPrivate, out var aPublic);
        BasisCryptoHandshake.GenerateKeyPair(out var bPrivate, out var bPublic);
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(aPrivate, aPublic, bPublic, out var aSend, out var aRecv));
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(bPrivate, bPublic, aPublic, out var bSend, out var bRecv));

        var failures = new ConcurrentQueue<string>();
        var aReceived = new ConcurrentDictionary<int, byte>();
        var bReceived = new ConcurrentDictionary<int, byte>();
        var aConnected = new TaskCompletionSource<NetPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bConnected = new TaskCompletionSource<L.NetPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        IPEndPoint? aEndpoint = null;
        IPEndPoint? bEndpoint = null;
        var server = new L.NetManager(new L.EventBasedNetListener()) { NatPunchEnabled = true, UnsyncedEvents = true };
        var serverNat = new L.EventBasedNatPunchListener();
        server.NatPunchModule.Init(serverNat);
        server.NatPunchModule.UnsyncedEvents = true;
        var introducer = new WireIntroducer(server);
        serverNat.NatIntroductionRequest += (local, remote, credential) =>
            BasisServerP2PBroker.CollectCandidate(local, remote, credential, introducer);
        var aListener = new EventBasedNetListener();
        BasisLiteNetLibP2PSocket? a = null;
        int aConnectIssued = 0;
        a = new BasisLiteNetLibP2PSocket(aListener, (endpoint, _, token) =>
        {
            if (token != "encrypted-session" || Interlocked.Exchange(ref aConnectIssued, 1) != 0) return;
            aEndpoint = endpoint;
            a!.SetEndpointKeys(endpoint, aSend, aRecv, 0);
            var data = new NetDataWriter(); data.Put(token);
            a.Connect(endpoint, data);
        });
        var bListener = new L.EventBasedNetListener();
        var bCrypto = new BasisCryptoLayer();
        var b = new L.NetManager(bListener, bCrypto)
        {
            NatPunchEnabled = true, UnsyncedEvents = true, IPv6Enabled = nativeIPv6,
            ChannelsCount = BasisNetworkCommons.TotalChannels
        };
        var bNat = new L.EventBasedNatPunchListener();
        b.NatPunchModule.Init(bNat);
        b.NatPunchModule.UnsyncedEvents = true;
        int bConnectIssued = 0;
        bNat.NatIntroductionSuccess += (endpoint, _, token) =>
        {
            if (token != "encrypted-session" || Interlocked.Exchange(ref bConnectIssued, 1) != 0) return;
            bEndpoint = endpoint;
            bCrypto.SetEndpointKeys(endpoint, bSend, bRecv);
            var data = new L.Utils.NetDataWriter(); data.Put(token);
            b.Connect(endpoint, data);
        };
        aListener.ConnectionRequestEvent += request =>
        {
            if (request.Data.GetString() == "encrypted-session")
            {
                a.SetEndpointKeys(request.RemoteEndPoint, aSend, aRecv, 0);
                request.Accept();
            }
        };
        bListener.ConnectionRequestEvent += request =>
        {
            if (request.Data.GetString() == "encrypted-session")
            {
                bCrypto.SetEndpointKeys(request.RemoteEndPoint, bSend, bRecv);
                request.Accept();
            }
        };
        aListener.PeerConnectedEvent += peer => { BasisServerP2PBroker.ApplyLinkUp(aId, "encrypted-session"); aConnected.TrySetResult(peer); };
        bListener.PeerConnectedEvent += peer => { BasisServerP2PBroker.ApplyLinkUp(bId, "encrypted-session"); bConnected.TrySetResult(peer); };
        aListener.NetworkReceiveEvent += (_, reader, _, _) =>
        {
            try { int i = reader.GetInt(); if (reader.GetString() != "from-b:" + i) failures.Enqueue("A payload mismatch"); aReceived.TryAdd(i, 0); }
            finally { reader.Recycle(); }
        };
        bListener.NetworkReceiveEvent += (_, reader, _, _) =>
        {
            try { int i = reader.GetInt(); if (reader.GetString() != "from-a:" + i) failures.Enqueue("B payload mismatch"); bReceived.TryAdd(i, 0); }
            finally { reader.Recycle(); }
        };
        try
        {
            Assert.True(server.Start());
            Assert.True(a.Start());
            Assert.True(b.Start());
            // A discovers from its DIRECT socket. B has IPv6 disabled for the mixed case.
            // Retransmit as in the production manager; a lost first introduction is harmless.
            for (int attempt = 0; attempt < 4 && !(aConnected.Task.IsCompleted && bConnected.Task.IsCompleted); attempt++)
            {
                a.RequestIntroductionCandidates("::1", server.LocalPort, aCredential, nativeIPv6 ? null! : "127.0.0.1");
                b.NatPunchModule.SendNatIntroduceRequest(new IPEndPoint(nativeIPv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, server.LocalPort), bCredential);
                await Task.Delay(150);
            }
            var ap = await aConnected.Task.WaitAsync(TimeSpan.FromSeconds(6));
            var bp = await bConnected.Task.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal(nativeIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, aEndpoint!.AddressFamily);
            Assert.Equal(aEndpoint.AddressFamily, bEndpoint!.AddressFamily);
            Assert.True(BasisServerP2PBroker.IsP2POffloaded(1, 2));
            for (int i = 0; i < 100; i++)
            {
                var aw = new NetDataWriter(); aw.Put(i); aw.Put("from-a:" + i);
                ap.Send(aw, 0, DeliveryMethod.ReliableOrdered);
                var bw = new L.Utils.NetDataWriter(); bw.Put(i); bw.Put("from-b:" + i);
                bp.Send(bw, 0, L.DeliveryMethod.ReliableOrdered);
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while (aReceived.Count != 100 || bReceived.Count != 100) await Task.Delay(10, deadline.Token);
            Assert.Empty(failures);
            Assert.Equal(Enumerable.Range(0, 100), aReceived.Keys.Order());
            Assert.Equal(Enumerable.Range(0, 100), bReceived.Keys.Order());
        }
        finally
        {
            a.Stop(); b.Stop(); server.Stop();
            BasisServerP2PBroker.ResetForTests();
        }
    }
}
