using System.Net;

namespace Basis.Network.Core
{
    public delegate void BasisP2PIntroduced(IPEndPoint target, bool sameNetwork, string token);

    public interface IBasisP2PSocket
    {
        int LocalPort { get; }
        bool Start();
        void Stop();
        void RequestIntroduction(string serverHost, int serverPort, string token);
        // New brokers issue a private discovery credential; only that path probes multiple
        // addresses. The default preserves transports that only support one broker endpoint.
        void RequestIntroductionCandidates(string serverHost, int serverPort, string credential, string ipv4Host)
            => RequestIntroduction(serverHost, serverPort, credential);
        NetPeer Connect(IPEndPoint endPoint, NetDataWriter connectData);
        void SetEndpointKeys(IPEndPoint endPoint, byte[] sendKey, byte[] recvKey, long initialSendCounter);
        void RemoveEndpoint(IPEndPoint endPoint);
    }
}
