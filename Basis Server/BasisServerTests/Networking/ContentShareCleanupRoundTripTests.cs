using Basis.Network.Core;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.Security;
using BasisPermissions;
using Xunit;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class ContentShareCleanupRoundTripTests
{
    private static readonly MapAuthIdentity Identity = new();
    private static int peerIdCounter = 26_000;

    private static (FakeNetPeer Peer, string Uuid) NewAuthenticatedPeer()
    {
        NetworkServer.AuthIdentity = Identity;
        PermissionIntegration.Manager.EnsureDefaults();
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.9.9") { Tag = NetworkServer.AuthenticatedPeerTag };
        string uuid = $"share-user-{Guid.NewGuid():N}";
        Identity.Register(uuid, id, peer);
        NetworkServer.AuthenticatedPeers[id] = peer;
        NetworkServer.RebuildPeerSnapshot();
        return (peer, uuid);
    }

    private static void Remove(params FakeNetPeer[] peers)
    {
        foreach (FakeNetPeer peer in peers)
        {
            NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);
        }
        NetworkServer.RebuildPeerSnapshot();
    }

    private static NetPacketReader Packet(Action<NetDataWriter> write)
    {
        NetDataWriter w = new NetDataWriter();
        write(w);
        byte[] bytes = w.AsReadOnlySpan().ToArray();
        return NetPacketReader.Create(bytes, 0, bytes.Length, () => { });
    }

    private static void SendDrop(FakeNetPeer from, string sphereId, ContentShareType type = ContentShareType.Prop)
    {
        ContentShareMessage msg = new ContentShareMessage
        {
            SphereNetID = sphereId,
            ContentURL = type == ContentShareType.Server ? "localhost:4296" : "https://cdn.example/prop.BEE",
            UnlockPassword = type == ContentShareType.Server ? "" : "pw",
            ContentType = type,
            PositionX = 1f, PositionY = 2f, PositionZ = 3f,
        };
        BasisNetworkMessageProcessor.ProcessMessage(from, Packet(w =>
        {
            w.Put(BasisNetworkCommons.ContentShareSub_Drop);
            msg.Serialize(w);
        }), BasisNetworkCommons.ContentShareChannel, DeliveryMethod.ReliableOrdered);
    }

    private static void SendCleanup(FakeNetPeer from, string sphereId)
    {
        ContentShareCleanupMessage msg = new ContentShareCleanupMessage { SphereNetID = sphereId };
        BasisNetworkMessageProcessor.ProcessMessage(from, Packet(w =>
        {
            w.Put(BasisNetworkCommons.ContentShareSub_Cleanup);
            msg.Serialize(w);
        }), BasisNetworkCommons.ContentShareChannel, DeliveryMethod.ReliableOrdered);
    }

    private static List<(byte Sub, ushort PlayerId, string SphereId)> ShareTraffic(FakeNetPeer peer)
    {
        List<(byte, ushort, string)> found = new();
        foreach ((byte[] data, byte channel, DeliveryMethod _) in peer.Sent)
        {
            if (channel != BasisNetworkCommons.ContentShareChannel) continue;
            NetDataReader r = new NetDataReader(data);
            byte sub = r.GetByte();
            if (sub == BasisNetworkCommons.ContentShareSub_Cleanup)
            {
                ServerContentShareCleanupMessage m = new ServerContentShareCleanupMessage();
                m.Deserialize(r);
                found.Add((sub, m.playerIdMessage.playerID, m.contentShareCleanupMessage.SphereNetID));
            }
            else
            {
                ServerContentShareMessage m = new ServerContentShareMessage();
                m.Deserialize(r);
                found.Add((sub, m.playerIdMessage.playerID, m.contentShareMessage.SphereNetID));
            }
        }
        return found;
    }

    [Fact]
    public void Sharer_DeletesOwnSphere_ServerDropsItAndBroadcastsCleanup()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer();
        (FakeNetPeer b, string _) = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        try
        {
            SendDrop(a, sphereId);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "drop was not stored");
            Assert.Contains(ShareTraffic(a), t => t.Sub == BasisNetworkCommons.ContentShareSub_Drop && t.SphereId == sphereId);
            Assert.Contains(ShareTraffic(b), t => t.Sub == BasisNetworkCommons.ContentShareSub_Drop && t.SphereId == sphereId);

            SendCleanup(a, sphereId);
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "cleanup did not remove the sphere");
            Assert.Contains(ShareTraffic(a), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId && t.PlayerId == (ushort)a.Id);
            Assert.Contains(ShareTraffic(b), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId && t.PlayerId == (ushort)a.Id);
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a, b);
        }
    }

    [Fact]
    public void UnknownSphere_RequesterAloneGetsCleanup_SoStaleOrbSelfHeals()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer();
        (FakeNetPeer b, string _) = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        try
        {
            SendCleanup(a, sphereId);
            Assert.Contains(ShareTraffic(a), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId && t.PlayerId == (ushort)a.Id);
            Assert.DoesNotContain(ShareTraffic(b), t => t.SphereId == sphereId);
        }
        finally
        {
            Remove(a, b);
        }
    }

    [Fact]
    public void DepartingServerShare_StaysForExistingPeers_ButIsNotReplayedToNewcomers()
    {
        (FakeNetPeer sharer, string _) = NewAuthenticatedPeer();
        (FakeNetPeer existing, string _) = NewAuthenticatedPeer();
        FakeNetPeer newcomer = null!;
        string sphereId = $"server-share-{Guid.NewGuid():N}";
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan requestedDelay = TimeSpan.Zero;
        var originalDelay = BasisNetworkContentShare.DepartureCleanupDelay;
        try
        {
            BasisNetworkContentShare.DepartureCleanupDelay = duration =>
            {
                requestedDelay = duration;
                return releaseDelay.Task;
            };
            SendDrop(sharer, sphereId, ContentShareType.Server);
            Assert.Contains(ShareTraffic(existing), t => t.Sub == BasisNetworkCommons.ContentShareSub_Drop && t.SphereId == sphereId);

            BasisNetworkContentShare.RemovePlayerSpheres(sharer.Id);
            Assert.Equal(TimeSpan.FromMinutes(1), requestedDelay);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId));
            Assert.DoesNotContain(ShareTraffic(existing), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId);

            (newcomer, _) = NewAuthenticatedPeer();
            BasisNetworkContentShare.SendAllSpheresToPeer(newcomer);
            Assert.DoesNotContain(ShareTraffic(newcomer), t => t.Sub == BasisNetworkCommons.ContentShareSub_Drop && t.SphereId == sphereId);

            releaseDelay.SetResult(true);
            Assert.True(SpinWait.SpinUntil(() => !BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => existing.Sent.Count >= 2, TimeSpan.FromSeconds(5)));
            Assert.Contains(ShareTraffic(existing), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId);
        }
        finally
        {
            BasisNetworkContentShare.Reset();
            releaseDelay.TrySetResult(true);
            BasisNetworkContentShare.DepartureCleanupDelay = originalDelay;
            if (newcomer != null) Remove(sharer, existing, newcomer);
            else Remove(sharer, existing);
        }
    }

    [Fact]
    public async Task ManuallyDeletedShare_DoesNotGetCleanedUpAgainAfterGracePeriod()
    {
        (FakeNetPeer sharer, string _) = NewAuthenticatedPeer();
        (FakeNetPeer existing, string moderatorUuid) = NewAuthenticatedPeer();
        string sphereId = $"server-share-{Guid.NewGuid():N}";
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalDelay = BasisNetworkContentShare.DepartureCleanupDelay;
        try
        {
            BasisNetworkContentShare.DepartureCleanupDelay = _ => releaseDelay.Task;
            SendDrop(sharer, sphereId, ContentShareType.Server);
            BasisNetworkContentShare.RemovePlayerSpheres(sharer.Id);

            PermissionIntegration.Manager.AddUserNode(moderatorUuid, PermNodes.protection);
            SendCleanup(existing, sphereId);
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId));
            int sentBeforeExpiry = existing.Sent.Count;

            releaseDelay.SetResult(true);
            await Task.Delay(25);
            Assert.Equal(sentBeforeExpiry, existing.Sent.Count);
            Assert.Single(ShareTraffic(existing), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId);
        }
        finally
        {
            BasisNetworkContentShare.Reset();
            releaseDelay.TrySetResult(true);
            BasisNetworkContentShare.DepartureCleanupDelay = originalDelay;
            PermissionIntegration.Manager.RemoveUserNode(moderatorUuid, PermNodes.protection);
            Remove(sharer, existing);
        }
    }

    [Fact]
    public async Task Reset_InvalidatesOldDepartureTimer_EvenIfSphereIdIsReused()
    {
        (FakeNetPeer sharer, string _) = NewAuthenticatedPeer();
        string sphereId = $"server-share-{Guid.NewGuid():N}";
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalDelay = BasisNetworkContentShare.DepartureCleanupDelay;
        try
        {
            BasisNetworkContentShare.DepartureCleanupDelay = _ => releaseDelay.Task;
            SendDrop(sharer, sphereId, ContentShareType.Server);
            BasisNetworkContentShare.RemovePlayerSpheres(sharer.Id);
            BasisNetworkContentShare.Reset();

            SendDrop(sharer, sphereId, ContentShareType.Server);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId));
            releaseDelay.SetResult(true);
            await Task.Delay(25);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId));
        }
        finally
        {
            BasisNetworkContentShare.Reset();
            releaseDelay.TrySetResult(true);
            BasisNetworkContentShare.DepartureCleanupDelay = originalDelay;
            Remove(sharer);
        }
    }

    [Fact]
    public void ReusedPeerId_CannotRemoveDepartedPlayersServerShare()
    {
        (FakeNetPeer sharer, string _) = NewAuthenticatedPeer();
        (FakeNetPeer existing, string _) = NewAuthenticatedPeer();
        string sphereId = $"server-share-{Guid.NewGuid():N}";
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalDelay = BasisNetworkContentShare.DepartureCleanupDelay;
        try
        {
            BasisNetworkContentShare.DepartureCleanupDelay = _ => releaseDelay.Task;
            SendDrop(sharer, sphereId, ContentShareType.Server);
            BasisNetworkContentShare.RemovePlayerSpheres(sharer.Id);
            Remove(sharer);

            // The previous player's ID is reused by a different, newly authenticated peer.
            FakeNetPeer successor = new FakeNetPeer(sharer.Id, "10.9.9.10") { Tag = NetworkServer.AuthenticatedPeerTag };
            Identity.Register($"share-user-{Guid.NewGuid():N}", successor.Id, successor);
            NetworkServer.AuthenticatedPeers[successor.Id] = successor;
            NetworkServer.RebuildPeerSnapshot();
            SendCleanup(successor, sphereId);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId));
            Assert.DoesNotContain(ShareTraffic(existing), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId);
            Remove(successor);
        }
        finally
        {
            BasisNetworkContentShare.Reset();
            releaseDelay.TrySetResult(true);
            BasisNetworkContentShare.DepartureCleanupDelay = originalDelay;
            Remove(sharer, existing);
        }
    }

    [Fact]
    public void DepartingPlayer_OtherContentTypesAreRemovedImmediately()
    {
        (FakeNetPeer sharer, string _) = NewAuthenticatedPeer();
        (FakeNetPeer existing, string _) = NewAuthenticatedPeer();
        string sphereId = $"prop-share-{Guid.NewGuid():N}";
        try
        {
            SendDrop(sharer, sphereId, ContentShareType.Prop);
            BasisNetworkContentShare.RemovePlayerSpheres(sharer.Id);
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId));
            Assert.Contains(ShareTraffic(existing), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId);
        }
        finally
        {
            BasisNetworkContentShare.Reset();
            Remove(sharer, existing);
        }
    }

    [Fact]
    public void NonSharer_WithoutProtection_IsRefused_WithProtection_IsAllowed()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer();
        (FakeNetPeer b, string bUuid) = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        try
        {
            SendDrop(a, sphereId);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId));

            SendCleanup(b, sphereId);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "a non-sharer without protection removed the sphere");
            Assert.DoesNotContain(ShareTraffic(a), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup);

            PermissionIntegration.Manager.AddUserNode(bUuid, PermNodes.protection);
            try
            {
                SendCleanup(b, sphereId);
                Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "a protected non-sharer could not remove the sphere");
                Assert.Contains(ShareTraffic(a), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId && t.PlayerId == (ushort)b.Id);
            }
            finally
            {
                PermissionIntegration.Manager.RemoveUserNode(bUuid, PermNodes.protection);
            }
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a, b);
        }
    }
}
