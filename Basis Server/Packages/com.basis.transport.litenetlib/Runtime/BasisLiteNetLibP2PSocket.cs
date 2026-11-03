using System.Net;
using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace Basis.Network.Core
{
    public sealed class BasisLiteNetLibP2PSocket : IBasisP2PSocket
    {
        private readonly LiteNetLib.NetManager manager;
        private readonly LiteNetLib.EventBasedNatPunchListener punch = new LiteNetLib.EventBasedNatPunchListener();
        private readonly BasisCryptoLayer crypto = new BasisCryptoLayer();

        public BasisLiteNetLibP2PSocket(EventBasedNetListener listener, BasisP2PIntroduced onIntroduced)
        {
            punch.NatIntroductionSuccess += (target, type, token) => onIntroduced?.Invoke(target, type == LiteNetLib.NatAddressType.Internal, token);
            manager = new LiteNetLib.NetManager(listener, crypto)
            {
                NatPunchEnabled = true,
                UnsyncedEvents = true,
                AutoRecycle = false,
                ChannelsCount = BasisNetworkCommons.TotalChannels,
                UpdateTime = BasisNetworkCommons.NetworkIntervalPoll,
            };
            manager.NatPunchModule.Init(punch);
            manager.NatPunchModule.UnsyncedEvents = true;
        }

        public int LocalPort => manager.LocalPort;

        public bool Start()
        {
            return manager.Start();
        }

        public void Stop()
        {
            manager.Stop();
        }

        public void RequestIntroduction(string serverHost, int serverPort, string token)
        {
            manager.NatPunchModule.SendNatIntroduceRequest(serverHost, serverPort, token);
        }

        public void RequestIntroductionCandidates(string serverHost, int serverPort, string credential, string ipv4Host)
        {
            var addresses = new HashSet<IPAddress>();
            foreach (string host in new[] { serverHost, ipv4Host })
            {
                if (string.IsNullOrEmpty(host)) continue;
                try
                {
                    foreach (var ip in Dns.GetHostAddresses(host))
                        addresses.Add(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
                }
                catch (SocketException) { /* Other family/host may still work. */ }
            }
            foreach (var address in addresses)
            {
                try
                {
                    manager.NatPunchModule.SendNatIntroduceRequest(new IPEndPoint(address, serverPort), credential);
                }
                catch (SocketException) { /* No route for this family: leave relay available. */ }
                catch (InvalidOperationException) { /* No usable local address for this family. */ }
            }
        }

        public NetPeer Connect(IPEndPoint endPoint, NetDataWriter connectData)
        {
            LiteNetLib.NetPeer peer = manager.Connect(endPoint, connectData.AsReadOnlySpan());
            return peer == null ? null : new LNLNetPeer(peer);
        }

        public void SetEndpointKeys(IPEndPoint endPoint, byte[] sendKey, byte[] recvKey, long initialSendCounter)
        {
            crypto.SetEndpointKeys(BasisP2PEndpoints.Canonical(endPoint), sendKey, recvKey, initialSendCounter);
        }

        public void RemoveEndpoint(IPEndPoint endPoint)
        {
            crypto.RemoveEndpoint(BasisP2PEndpoints.Canonical(endPoint));
        }
    }
}
