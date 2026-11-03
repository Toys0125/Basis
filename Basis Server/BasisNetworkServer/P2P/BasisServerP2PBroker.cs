using Basis.Network.Core;
using System.Collections.Concurrent;
using System.Net;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static SerializableBasis;

namespace BasisNetworkServer
{
    public static class BasisServerP2PBroker
    {
        private enum SessionState : byte { Awaiting, ReadyForPunch, Punched }

        private sealed class Session
        {
            public string Token;
            public int InitiatorPeerId;
            public int TargetPeerId;
            public SessionState State;
            public bool Closed;
            public AddressFamily? SelectedFamily;

            public readonly Participant A = new();
            public readonly Participant B = new();

            public bool InitiatorLinkUp;
            public bool TargetLinkUp;
        }

        internal sealed class Participant
        {
            public string Credential;
            public readonly Dictionary<AddressFamily, (IPEndPoint Local, IPEndPoint External)> Candidates = new();

            public bool Add(IPEndPoint local, IPEndPoint external)
            {
                local = Canonical(local);
                external = Canonical(external);
                if (local == null || external == null || local.Port == 0 || external.Port == 0 ||
                    local.AddressFamily != external.AddressFamily) return false;
                // A credential identifies one participant, never the opposite slot. Pin each
                // family for the punch cycle; retries cannot replace it with an unrelated endpoint.
                if (Candidates.TryGetValue(external.AddressFamily, out var prior))
                    return prior.Local.Equals(local) && prior.External.Equals(external);
                Candidates.Add(external.AddressFamily, (local, external));
                return true;
            }
        }

        private static IPEndPoint Canonical(IPEndPoint ep) => BasisP2PEndpoints.Canonical(ep);

        private static readonly ConcurrentDictionary<string, (Session Session, Participant Participant)> _credentials = new();

        private static readonly ConcurrentDictionary<string, Session> _sessions = new();
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _peerSessions = new();
        private static readonly ConcurrentDictionary<long, byte> _offloadedPairs = new();

        // A direct link needs one session per peer you punch to, so a real mesh is bounded by the
        // instance population. This ceiling is far above any legitimate fan-out and only exists to
        // stop one client opening unbounded sessions (each a server-side record keyed by its own
        // token string, plus a reliable Request forwarded at a victim) as a memory/flood vector.
        private const int MaxSessionsPerPeer = 4096;

        private static long PackPair(int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        /// <summary>
        /// False when no pair is offloaded at all, which is the overwhelmingly common case. The avatar
        /// send loop tests this before <see cref="IsP2POffloaded"/> so a server with no NAT-punched
        /// sessions pays a register compare per pair instead of a ConcurrentDictionary lookup — at 1000
        /// players that is a million hash lookups per tick that no longer happen.
        /// </summary>
        public static bool HasOffloadedPairs => Volatile.Read(ref _offloadedPairCount) != 0;

        private static int _offloadedPairCount;

        public static bool IsP2POffloaded(int a, int b)
        {
            if (a == b) return false;
            if (Volatile.Read(ref _offloadedPairCount) == 0) return false;
            return _offloadedPairs.ContainsKey(PackPair(a, b));
        }

        private static IPeerIntroducer _introducer;

        public static void Initialize()
        {
            if (_introducer != null) return;

            NetManager server = NetworkServer.Server;
            if (server == null)
            {
                BNL.LogError("[P2P] NetManager not initialised, cannot start P2P broker.");
                return;
            }

            foreach (NetManager transport in server.Transports())
            {
                IPeerIntroducer introducer = BasisNetworkStackRegistry.CreateIntroducer(transport.StackId, transport);
                if (introducer == null) continue;
                if (!introducer.Initialize(OnNatIntroductionRequest))
                {
                    introducer.Shutdown();
                    continue;
                }
                _introducer = introducer;
                BNL.Log($"[P2P] Broker initialised on the '{transport.StackId}' transport.");
                return;
            }
            BNL.Log("[P2P] No installed transport can introduce peers, so direct peer connections are off.");
        }

        public static void Reset()
        {
            _introducer?.Shutdown();
            _introducer = null;
            _credentials.Clear();
            _sessions.Clear();
            _peerSessions.Clear();
            _offloadedPairs.Clear();
            Volatile.Write(ref _offloadedPairCount, 0);
        }

        public static void HandleP2PMessage(NetPacketReader reader, NetPeer peer)
        {
            byte sub = reader.GetByte();
            BasisP2PSignalMessage msg = default;
            msg.Deserialize(reader);
            reader.Recycle();

            switch (sub)
            {
                case BasisNetworkCommons.P2PSub_Request:
                    HandleRequest(peer, msg);
                    break;
                case BasisNetworkCommons.P2PSub_Accept:
                    HandleAccept(peer, msg);
                    break;
                case BasisNetworkCommons.P2PSub_Decline:
                    ForwardAndDrop(peer, msg, BasisNetworkCommons.P2PSub_Decline);
                    break;
                case BasisNetworkCommons.P2PSub_Cancel:
                    ForwardAndDrop(peer, msg, BasisNetworkCommons.P2PSub_Cancel);
                    break;
                case BasisNetworkCommons.P2PSub_LinkLost:
                    HandleLinkLost(peer, msg);
                    break;
                case BasisNetworkCommons.P2PSub_LinkUp:
                    HandleLinkUp(peer, msg);
                    break;
                default:
                    BNL.LogError($"[P2P] Unknown sub-type {sub} from peer {peer.Id}.");
                    break;
            }
        }

        private static void HandleLinkUp(NetPeer sender, BasisP2PSignalMessage msg)
            => ApplyLinkUp(sender.Id, msg.sessionToken);

        // Core LinkUp handling, keyed by peer id (the NetPeer entry point only ever needs
        // sender.Id). Exposed to tests so the offload lifecycle can be exercised without
        // constructing transport peers.
        internal static void ApplyLinkUp(int senderId, string sessionToken)
        {
            if (!_sessions.TryGetValue(sessionToken, out Session s)) return;

            lock (s)
            {
                // Late reports from an earlier cycle must not offload a pair while it is
                // gathering new candidates (or has no compatible family).
                if (s.Closed || s.State != SessionState.Punched) return;
                if (senderId == s.InitiatorPeerId) s.InitiatorLinkUp = true;
                else if (senderId == s.TargetPeerId) s.TargetLinkUp = true;
                else return;

                BNL.Log($"[P2P] LinkUp from peer {senderId} (token {Preview(s.Token)}); flags InitiatorUp={s.InitiatorLinkUp} TargetUp={s.TargetLinkUp}.");
                if (s.InitiatorLinkUp && s.TargetLinkUp)
                {
                    if (_offloadedPairs.TryAdd(PackPair(s.InitiatorPeerId, s.TargetPeerId), 0))
                    {
                        Interlocked.Increment(ref _offloadedPairCount);
                    }
                    BNL.Log($"[P2P] OFFLOADED pair ({s.InitiatorPeerId},{s.TargetPeerId}) — server will skip relaying voice + avatar between them.");

                    // Positive confirmation to BOTH peers that the pair is fully direct now. A
                    // client that reached Connected but never sees this treats its link as partial
                    // and falls back to the server relay (see BasisP2PManager confirm-timeout).
                    if (NetworkServer.AuthenticatedPeers.TryGetValue(s.InitiatorPeerId, out NetPeer initiatorPeer))
                        SendSub(initiatorPeer, BasisNetworkCommons.P2PSub_Offloaded, s.Token, (ushort)s.TargetPeerId);
                    if (NetworkServer.AuthenticatedPeers.TryGetValue(s.TargetPeerId, out NetPeer targetPeer))
                        SendSub(targetPeer, BasisNetworkCommons.P2PSub_Offloaded, s.Token, (ushort)s.InitiatorPeerId);
                }
            }
        }

        private static void HandleRequest(NetPeer sender, BasisP2PSignalMessage msg)
        {
            if (!SupportsDiscovery(msg.discoveryVersion))
            {
                // Legacy NAT requests have only a shared token: they cannot prove which
                // authenticated participant sent them. Keep these peers on the relay.
                SendSub(sender, BasisNetworkCommons.P2PSub_Cancel, msg.sessionToken, msg.otherPlayerId);
                return;
            }
            if (string.IsNullOrEmpty(msg.sessionToken))
            {
                BNL.LogError($"[P2P] Empty session token from peer {sender.Id}, dropping Request.");
                return;
            }
            // Admin-controlled instance lockout: non-admins may not establish direct (P2P) connections.
            // Admins (basis.moderation.globallock) are exempt so they can still connect for moderation.
            if (BasisNetworkServer.Security.BasisGlobalLockManager.DirectConnectLocked &&
                !BasisPermissions.PermissionManager.PermissionIntegration.HasValidRequirement(sender, BasisPermissions.PermNodes.ModerationGlobalLock))
            {
                BNL.Log($"[P2P] DirectConnectLocked: rejecting Request from non-admin peer {sender.Id}.");
                SendSub(sender, BasisNetworkCommons.P2PSub_Cancel, msg.sessionToken, msg.otherPlayerId);
                return;
            }
            if (msg.otherPlayerId == sender.Id)
            {
                BNL.LogError($"[P2P] Peer {sender.Id} tried to request a session with itself.");
                return;
            }
            if (!NetworkServer.AuthenticatedPeers.TryGetValue(msg.otherPlayerId, out NetPeer target))
            {
                SendSub(sender, BasisNetworkCommons.P2PSub_Cancel, msg.sessionToken, msg.otherPlayerId);
                return;
            }
            if (!sender.SupportsDirectConnect || !target.SupportsDirectConnect)
            {
                BNL.Log($"[P2P] Peer {sender.Id} asked for a direct link with {target.Id}, but one of them is on a transport that cannot take direct links ({sender.StackId}/{target.StackId}).");
                SendSub(sender, BasisNetworkCommons.P2PSub_Cancel, msg.sessionToken, msg.otherPlayerId);
                return;
            }

            // Cap distinct outstanding sessions. Existing tokens cannot overwrite a pair.
            if (_peerSessions.TryGetValue(sender.Id, out var openSessions)
                && openSessions.Count >= MaxSessionsPerPeer
                && !openSessions.ContainsKey(msg.sessionToken))
            {
                BNL.LogError($"[P2P] Peer {sender.Id} exceeded the per-peer session cap ({MaxSessionsPerPeer}); dropping Request.");
                SendSub(sender, BasisNetworkCommons.P2PSub_Cancel, msg.sessionToken, msg.otherPlayerId);
                return;
            }

            var session = new Session
            {
                Token = msg.sessionToken,
                InitiatorPeerId = sender.Id,
                TargetPeerId = msg.otherPlayerId,
                State = SessionState.Awaiting,
            };
            // A caller must not overwrite another pair's session or reset a live session.
            if (!_sessions.TryAdd(msg.sessionToken, session)) return;
            ArmParticipant(session, session.A);
            ArmParticipant(session, session.B);
            TrackPeerSession(sender.Id, msg.sessionToken);
            TrackPeerSession(msg.otherPlayerId, msg.sessionToken);

            BNL.Log($"[P2P] Forwarding Request from peer {sender.Id} to peer {msg.otherPlayerId} (token {Preview(msg.sessionToken)}).");
            SendSub(target, BasisNetworkCommons.P2PSub_Request, msg.sessionToken, (ushort)sender.Id, msg.ephemeralPublicKey, credential: session.B.Credential);

            // ServerArmed confirms registration before either side starts punching, avoiding a race.
            SendSub(sender, BasisNetworkCommons.P2PSub_ServerArmed, msg.sessionToken, msg.otherPlayerId, credential: session.A.Credential);
        }

        private static void HandleAccept(NetPeer sender, BasisP2PSignalMessage msg)
        {
            if (!_sessions.TryGetValue(msg.sessionToken, out Session s))
            {
                BNL.LogError($"[P2P] Accept for unknown token from peer {sender.Id}.");
                return;
            }
            if (s.TargetPeerId != sender.Id || s.InitiatorPeerId != msg.otherPlayerId)
            {
                BNL.LogError($"[P2P] Accept from peer {sender.Id} doesn't match session pair ({s.InitiatorPeerId},{s.TargetPeerId}).");
                return;
            }

            if (!SupportsDiscovery(msg.discoveryVersion))
            {
                ForwardAndDrop(sender, msg, BasisNetworkCommons.P2PSub_Cancel);
                SendSub(sender, BasisNetworkCommons.P2PSub_Cancel, s.Token, (ushort)s.InitiatorPeerId);
                return;
            }
            if (s.State != SessionState.Awaiting) return;
            s.State = SessionState.ReadyForPunch;
            SendSub(sender, BasisNetworkCommons.P2PSub_ServerArmed, s.Token, (ushort)s.InitiatorPeerId, credential: s.B.Credential);

            if (NetworkServer.AuthenticatedPeers.TryGetValue(s.InitiatorPeerId, out NetPeer initiator))
            {
                BNL.Log($"[P2P] Accept from peer {sender.Id} (token {Preview(s.Token)}); session armed, forwarding to initiator {s.InitiatorPeerId}.");
                SendSub(initiator, BasisNetworkCommons.P2PSub_Accept, s.Token, (ushort)sender.Id, msg.ephemeralPublicKey);
            }
            else
            {
                BNL.LogWarning($"[P2P] Accept arrived but initiator {s.InitiatorPeerId} already gone; dropping session {Preview(s.Token)}.");
                RemoveSession(s.Token);
            }
        }

        private static void HandleLinkLost(NetPeer sender, BasisP2PSignalMessage msg)
            => ApplyLinkLost(sender.Id, msg.sessionToken, msg.otherPlayerId);

        // Core LinkLost handling, keyed by peer id. Re-arms the session + clears offload so the
        // relay resumes during the re-punch window, then forwards LinkLost to the other peer.
        // Exposed to tests (the NetPeer entry point only needs sender.Id + msg fields).
        internal static void ApplyLinkLost(int senderId, string sessionToken, int otherPlayerId)
        {
            // Re-arm session + clear offload so relay resumes during re-punch window.
            if (_sessions.TryGetValue(sessionToken, out Session s))
            {
                if (!MatchesPair(s, senderId, otherPlayerId)) return;
                bool wasOffloaded = _offloadedPairs.ContainsKey(PackPair(s.InitiatorPeerId, s.TargetPeerId));
                lock (s)
                {
                    // An authenticated LinkLost starts a new collection cycle, allowing NAT
                    // mappings to change without permitting replacements within a cycle.
                    s.A.Candidates.Clear();
                    s.B.Candidates.Clear();
                    s.SelectedFamily = null;
                    s.InitiatorLinkUp = false;
                    s.TargetLinkUp = false;
                    s.State = SessionState.ReadyForPunch;
                    if (_offloadedPairs.TryRemove(PackPair(s.InitiatorPeerId, s.TargetPeerId), out _))
                    {
                        Interlocked.Decrement(ref _offloadedPairCount);
                    }
                }
                BNL.Log($"[P2P] LinkLost from peer {senderId} (token {Preview(s.Token)}); re-armed for punch, offload {(wasOffloaded ? "cleared (relay resumed)" : "already cleared")}.");
            }
            else return;
            // Forward to the other peer (guarded) without dropping the session (re-armed above).
            if (NetworkServer.AuthenticatedPeers.TryGetValue(otherPlayerId, out NetPeer other))
                SendSub(other, BasisNetworkCommons.P2PSub_LinkLost, sessionToken, (ushort)senderId);
        }

        private static void ForwardAndDrop(NetPeer sender, BasisP2PSignalMessage msg, byte sub, bool dropSession = true)
        {
            if (!_sessions.TryGetValue(msg.sessionToken, out Session session) ||
                !MatchesPair(session, sender.Id, msg.otherPlayerId)) return;
            if (NetworkServer.AuthenticatedPeers.TryGetValue(msg.otherPlayerId, out NetPeer other))
            {
                SendSub(other, sub, msg.sessionToken, (ushort)sender.Id);
            }
            if (dropSession && !string.IsNullOrEmpty(msg.sessionToken))
            {
                RemoveSession(msg.sessionToken);
            }
        }

        private static bool MatchesPair(Session s, int sender, int other) =>
            (sender == s.InitiatorPeerId && other == s.TargetPeerId) ||
            (sender == s.TargetPeerId && other == s.InitiatorPeerId);

        private static void ArmParticipant(Session s, Participant p)
        {
            p.Credential = Guid.NewGuid().ToString("N");
            _credentials[p.Credential] = (s, p);
        }

        private static void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
        {
            CollectCandidate(localEndPoint, remoteEndPoint, token, _introducer);
        }

        internal static bool CollectCandidate(IPEndPoint local, IPEndPoint remote, string token, IPeerIntroducer introducer)
        {
            if (string.IsNullOrEmpty(token)) return false;
            if (!_credentials.TryGetValue(token, out var binding)) return false;
            var s = binding.Session;
            var p = binding.Participant;
            remote = Canonical(remote);
            lock (s)
            {
                if (s.Closed || s.State < SessionState.ReadyForPunch || !p.Add(local, remote)) return false;
                // Select the first common family for this punch cycle. Both participants
                // must open the same family: independent IPv4/IPv6 races can otherwise leave
                // each side holding a different connection. Retries repeat prediction too.
                foreach (var entry in s.A.Candidates)
                {
                    if (s.SelectedFamily.HasValue && s.SelectedFamily.Value != entry.Key) continue;
                    if (!s.B.Candidates.TryGetValue(entry.Key, out var other)) continue;
                    var first = entry.Value;
                    if (first.External.Equals(other.External)) continue;
                    bool sameNat = first.External.Address.Equals(other.External.Address);
                    var aLocal = first.Local;
                    var bLocal = other.Local;
                    if (sameNat && aLocal.Address.Equals(bLocal.Address))
                    {
                        var loopback = entry.Key == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
                        aLocal = new IPEndPoint(loopback, aLocal.Port);
                        bLocal = new IPEndPoint(loopback, bLocal.Port);
                    }
                    s.SelectedFamily = entry.Key;
                    introducer?.Introduce(aLocal, first.External, bLocal, other.External, !sameNat, s.Token);
                    s.State = SessionState.Punched;
                    break;
                }
                return true;
            }
        }

        private static string Preview(string token)
        {
            if (string.IsNullOrEmpty(token)) return "(empty)";
            return token.Length <= 8 ? token : token.Substring(0, 8);
        }

        public static void RemovePeer(int peerId)
        {
            if (!_peerSessions.TryRemove(peerId, out var tokens)) return;
            BNL.Log($"[P2P] Peer {peerId} disconnected; closing out {tokens.Count} P2P session(s).");
            foreach (var kv in tokens)
            {
                string token = kv.Key;
                if (!_sessions.TryGetValue(token, out Session s)) continue;
                int otherId = s.InitiatorPeerId == peerId ? s.TargetPeerId : s.InitiatorPeerId;
                if (NetworkServer.AuthenticatedPeers.TryGetValue(otherId, out NetPeer other))
                {
                    BNL.Log($"[P2P] Notifying peer {otherId} via Cancel that peer {peerId} is gone (token {Preview(token)}).");
                    SendSub(other, BasisNetworkCommons.P2PSub_Cancel, token, (ushort)peerId);
                }
                RemoveSession(token);
            }
        }

        private static void RemoveSession(string token)
        {
            if (!_sessions.TryRemove(token, out Session s)) return;
            lock (s)
            {
                s.Closed = true;
                if (s.A.Credential != null) _credentials.TryRemove(s.A.Credential, out _);
                if (s.B.Credential != null) _credentials.TryRemove(s.B.Credential, out _);
                UntrackPeerSession(s.InitiatorPeerId, token);
                UntrackPeerSession(s.TargetPeerId, token);
                if (_offloadedPairs.TryRemove(PackPair(s.InitiatorPeerId, s.TargetPeerId), out _))
                {
                    Interlocked.Decrement(ref _offloadedPairCount);
                }
            }
        }

        private static void TrackPeerSession(int peerId, string token)
        {
            var inner = _peerSessions.GetOrAdd(peerId, _ => new ConcurrentDictionary<string, byte>());
            inner[token] = 0;
        }

        private static void UntrackPeerSession(int peerId, string token)
        {
            if (_peerSessions.TryGetValue(peerId, out var inner))
            {
                inner.TryRemove(token, out _);
            }
        }

        private static void SendSub(NetPeer to, byte sub, string token, ushort otherPlayerId, byte[] ephemeralPublicKey = null, string credential = null)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(sub);
            var body = new BasisP2PSignalMessage
            {
                otherPlayerId = otherPlayerId,
                sessionToken = token ?? string.Empty,
                ephemeralPublicKey = ephemeralPublicKey,
                discoveryVersion = (byte)(credential != null ? 2 : 0),
                discoveryCredential = credential,
                discoveryIPv4Host = Environment.GetEnvironmentVariable("BASIS_P2P_IPV4_HOST"),
            };
            body.Serialize(writer);
            NetworkServer.TrySend(to, writer, BasisNetworkCommons.P2PChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        // --- Test seams (InternalsVisibleTo BasisServerTests) -------------------------------
        // The NetPeer-based entry points (HandleRequest/HandleAccept) can't be driven from a
        // unit test because NetPeer has only internal constructors that need a live NetManager.
        // These let a test set up a session and clear state directly, keyed by peer id, so the
        // offload lifecycle (establish -> disconnect -> rejoin with a reused id) is testable.

        // Clears all broker state so each test starts from a clean slate (the dictionaries are
        // static and otherwise persist across tests in the same run).
        internal static void ResetForTests()
        {
            _credentials.Clear();
            _sessions.Clear();
            _peerSessions.Clear();
            _offloadedPairs.Clear();
            Volatile.Write(ref _offloadedPairCount, 0);
        }

        // Registers a session the way HandleRequest would (session record + per-peer tracking),
        // without needing NetPeers. Offload-only tests start introduced; discovery tests opt
        // into ReadyForPunch (the post-Accept state).
        internal static void RegisterSessionForTests(string token, int initiatorId, int targetId, bool readyForPunch = false)
        {
            var session = new Session
            {
                Token = token,
                InitiatorPeerId = initiatorId,
                TargetPeerId = targetId,
                State = readyForPunch ? SessionState.ReadyForPunch : SessionState.Punched,
            };
            _sessions[token] = session;
            TrackPeerSession(initiatorId, token);
            TrackPeerSession(targetId, token);
        }

        internal static bool SupportsDiscovery(byte version) => version == 2;

        internal static void ArmForTests(string token, string a, string b)
        {
            var s = _sessions[token];
            if (s.A.Credential != null) _credentials.TryRemove(s.A.Credential, out _);
            if (s.B.Credential != null) _credentials.TryRemove(s.B.Credential, out _);
            s.A.Credential = a;
            s.B.Credential = b;
            if (a != null) _credentials[a] = (s, s.A);
            if (b != null) _credentials[b] = (s, s.B);
        }

        // True if disconnect/cancel has not fully torn the session down.
        internal static bool HasSessionForTests(string token) => _sessions.ContainsKey(token);
    }
}
