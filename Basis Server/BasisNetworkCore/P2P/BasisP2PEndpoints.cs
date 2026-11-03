using System.Net;

namespace Basis.Network.Core
{
    public static class BasisP2PEndpoints
    {
        public static IPEndPoint Canonical(IPEndPoint endpoint) =>
            endpoint != null && endpoint.Address.IsIPv4MappedToIPv6
                ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;
    }
}
