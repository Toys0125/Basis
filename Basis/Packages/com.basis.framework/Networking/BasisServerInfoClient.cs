using Basis.Network.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// Registers the LiteNetLib server-info probe with <see cref="BasisNetworkStackRegistry"/>.
    /// Fires a single unconnected "server info" packet at the listening port (the LiteNetLib
    /// equivalent of a Minecraft Server List Ping) and awaits the response.
    ///
    /// When the hostname resolves to both AAAA and A records, IPv6 is attempted first with
    /// half the total timeout budget. If IPv6 times out or fails the probe falls through to
    /// IPv4 with the remaining budget. The winning <see cref="IPAddress"/> is stored in
    /// <see cref="ServerProbeResult.ResolvedAddress"/> so callers can connect directly to the
    /// confirmed-reachable address family without re-resolving DNS.
    /// </summary>
    public static class BasisServerInfoClient
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoRegister()
        {
            BasisNetworkStackRegistry.RegisterProbe(BasisNetworkStackRegistry.LiteNetLibId, ProbeAsync);
        }

        public static async Task<ServerProbeResult> ProbeAsync(ConnectionTarget target, int timeoutMs, CancellationToken ct)
        {
            ServerProbeResult fail = new ServerProbeResult();
            if (target == null) { fail.Error = "Target is null"; return fail; }

            string host = target.Get(ConnectionTarget.Keys.Address, string.Empty);
            if (string.IsNullOrWhiteSpace(host)) { fail.Error = "Host is empty"; return fail; }

            string portString = target.Get(ConnectionTarget.Keys.Port,
                LNLConnectionTargetParser.DefaultPort.ToString(CultureInfo.InvariantCulture));
            if (!ushort.TryParse(portString, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort port) || port == 0)
            {
                fail.Error = "Port is invalid";
                return fail;
            }

            // Resolve all addresses upfront so we can pick the right family.
            IPAddress[] ipv6, ipv4;
            try
            {
                ResolvedAddresses resolved = await ResolveAllAsync(host, ct).ConfigureAwait(false);
                ipv6 = resolved.IPv6;
                ipv4 = resolved.IPv4;
            }
            catch (Exception ex)
            {
                fail.Error = "DNS resolution failed: " + ex.Message;
                return fail;
            }

            if (ipv6.Length == 0 && ipv4.Length == 0)
            {
                fail.Error = "DNS resolution returned no addresses";
                return fail;
            }

            // Split budget: half for the IPv6 attempt when both families exist.
            // If only one family is present it gets the full budget.
            bool bothFamilies = ipv6.Length > 0 && ipv4.Length > 0;
            int v6Budget = bothFamilies ? timeoutMs / 2 : timeoutMs;
            int v4Budget = bothFamilies ? Math.Max(timeoutMs - v6Budget, 1000) : timeoutMs;

            EventBasedNetListener listener = new EventBasedNetListener();
            Configuration probeConfig = new Configuration { NetworkStackId = BasisNetworkStackRegistry.LiteNetLibId };
            NetManager manager = BasisNetworkStackRegistry.Create(probeConfig.NetworkStackId, listener, probeConfig);
            // Start with dual-stack so we can send to either address family.
            manager.Start(IPAddress.Any, IPAddress.IPv6Any, 0);

            ushort nonce;
            unchecked { nonce = (ushort)Guid.NewGuid().GetHashCode(); }

            try
            {
                // ── IPv6 first ────────────────────────────────────────────────────────
                if (ipv6.Length > 0)
                {
                    ServerProbeResult r = await ProbeAddressesAsync(
                        listener, manager, nonce, port, ipv6, v6Budget, ct).ConfigureAwait(false);
                    if (r.Reachable || ct.IsCancellationRequested)
                        return r;
                }

                // ── IPv4 fallback ─────────────────────────────────────────────────────
                if (ipv4.Length > 0)
                {
                    return await ProbeAddressesAsync(
                        listener, manager, nonce, port, ipv4, v4Budget, ct).ConfigureAwait(false);
                }

                fail.Error = "No reachable address found";
                fail.TimedOut = true;
                return fail;
            }
            finally
            {
                try { manager.Stop(); } catch { }
            }
        }

        private static async Task<ServerProbeResult> ProbeAddressesAsync(
            EventBasedNetListener listener,
            NetManager manager,
            ushort nonce,
            ushort port,
            IPAddress[] addresses,
            int timeoutMs,
            CancellationToken ct)
        {
            Stopwatch budget = Stopwatch.StartNew();
            ServerProbeResult last = new ServerProbeResult { TimedOut = true };

            for (int index = 0; index < addresses.Length; index++)
            {
                if (ct.IsCancellationRequested)
                    return last;

                int remainingMs = Math.Max(1, timeoutMs - (int)budget.ElapsedMilliseconds);
                int remainingAddresses = addresses.Length - index;
                int addressBudget = Math.Max(1, remainingMs / remainingAddresses);

                IPAddress address = addresses[index];
                last = await SendProbeAsync(
                    listener, manager, nonce, port, address, addressBudget, ct).ConfigureAwait(false);
                if (last.Reachable)
                {
                    last.ResolvedAddress = address;
                    return last;
                }

                if (budget.ElapsedMilliseconds >= timeoutMs)
                    break;
            }

            return last;
        }

        /// <summary>
        /// Sends a single probe packet to <paramref name="address"/>:<paramref name="port"/>
        /// and waits up to <paramref name="timeoutMs"/> for a matching response.
        /// The handler also checks the source endpoint to reject stale packets from a
        /// previous attempt that arrive late.
        /// </summary>
        private static async Task<ServerProbeResult> SendProbeAsync(
            EventBasedNetListener listener,
            NetManager manager,
            ushort nonce,
            ushort port,
            IPAddress address,
            int timeoutMs,
            CancellationToken ct)
        {
            IPEndPoint endpoint = new IPEndPoint(address, port);
            TaskCompletionSource<ServerProbeResult> tcs =
                new TaskCompletionSource<ServerProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Stopwatch rtt = new Stopwatch();

            void OnReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader)
            {
                try
                {
                    // Reject packets from the wrong endpoint (stale reply from a prior attempt).
                    if (!remoteEndPoint.Equals(endpoint)) return;
                    if (reader.AvailableBytes < 8) return;

                    uint magic = reader.GetUInt();
                    if (magic != BasisNetworkCommons.ServerInfoResponseMagic) return;

                    ushort proto = reader.GetUShort();
                    ushort returnedNonce = reader.GetUShort();
                    if (returnedNonce != nonce) return;

                    ushort online = reader.GetUShort();
                    ushort max = reader.GetUShort();
                    string name = reader.GetString();
                    string motd = reader.GetString();

                    tcs.TrySetResult(new ServerProbeResult
                    {
                        Reachable = true,
                        Online = online,
                        Max = max,
                        ProtocolVersion = proto,
                        Name = name,
                        Motd = motd,
                        RoundTripMs = (int)rtt.ElapsedMilliseconds,
                    });
                }
                catch { }
                finally { reader.Recycle(true); }
            }

            listener.NetworkReceiveUnconnectedEvent += OnReceiveUnconnected;
            try
            {
                NetDataWriter writer = new NetDataWriter(true, BasisNetworkCommons.ServerInfoMinRequestBytes);
                writer.Put(BasisNetworkCommons.ServerInfoQueryMagic);
                writer.Put(BasisNetworkCommons.ServerInfoProtocolVersion);
                writer.Put(nonce);
                int padBytes = BasisNetworkCommons.ServerInfoMinRequestBytes - writer.Length;
                if (padBytes > 0) writer.Put(new byte[padBytes]);

                rtt.Start();
                if (!manager.SendUnconnectedMessage(writer, endpoint))
                    return new ServerProbeResult { Error = "Send failed" };

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                using CancellationTokenRegistration reg = cts.Token.Register(
                    () => tcs.TrySetCanceled());

                try
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new ServerProbeResult { TimedOut = true };
                }
            }
            finally
            {
                listener.NetworkReceiveUnconnectedEvent -= OnReceiveUnconnected;
            }
        }

        private readonly struct ResolvedAddresses
        {
            public readonly IPAddress[] IPv6;
            public readonly IPAddress[] IPv4;
            public ResolvedAddresses(IPAddress[] ipv6, IPAddress[] ipv4)
            { IPv6 = ipv6; IPv4 = ipv4; }
        }

        /// <summary>
        /// Resolves <paramref name="host"/> and splits the results into IPv6 and IPv4 buckets.
        /// Literal IP addresses are placed directly into the matching bucket without DNS.
        /// </summary>
        private static async Task<ResolvedAddresses> ResolveAllAsync(string host, CancellationToken ct)
        {
            if (IPAddress.TryParse(host, out IPAddress parsed))
            {
                if (parsed.AddressFamily == AddressFamily.InterNetworkV6)
                    return new ResolvedAddresses(new[] { parsed }, Array.Empty<IPAddress>());
                return new ResolvedAddresses(Array.Empty<IPAddress>(), new[] { parsed });
            }

            IPAddress[] all;
            try
            {
                all = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            }
            catch
            {
#if UNITY_SERVER && UNITY_STANDALONE_LINUX
                // Unity's Linux Dedicated Server currently runs Mono. Mono can discard valid AAAA
                // answers when its cached Socket.OSSupportsIPv6 flag is false, even though Linux
                // can create AF_INET6 sockets. Ask libc directly before treating the hostname as
                // unresolvable so IPv6-only servers remain reachable from headless builds.
                return await ResolveAllWithLinuxGetAddrInfoAsync(host, ct).ConfigureAwait(false);
#else
                throw;
#endif
            }

            List<IPAddress> v6 = new List<IPAddress>();
            List<IPAddress> v4 = new List<IPAddress>();
            foreach (IPAddress a in all)
            {
                if (a.AddressFamily == AddressFamily.InterNetworkV6) v6.Add(a);
                else if (a.AddressFamily == AddressFamily.InterNetwork) v4.Add(a);
            }

#if UNITY_SERVER && UNITY_STANDALONE_LINUX
            if (v6.Count == 0 && v4.Count == 0)
                return await ResolveAllWithLinuxGetAddrInfoAsync(host, ct).ConfigureAwait(false);
#endif

            return new ResolvedAddresses(v6.ToArray(), v4.ToArray());
        }

#if UNITY_SERVER && UNITY_STANDALONE_LINUX
        private const int LinuxAfInet = 2;
        private const int LinuxAfInet6 = 10;

        [StructLayout(LayoutKind.Sequential)]
        private struct LinuxAddrInfo
        {
            public int Flags;
            public int Family;
            public int SocketType;
            public int Protocol;
            public uint AddressLength;
            public IntPtr Address;
            public IntPtr CanonicalName;
            public IntPtr Next;
        }

        [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = false)]
        private static extern int getaddrinfo(string node, string service, IntPtr hints, out IntPtr result);

        [DllImport("libc", SetLastError = false)]
        private static extern void freeaddrinfo(IntPtr result);

        private static Task<ResolvedAddresses> ResolveAllWithLinuxGetAddrInfoAsync(string host, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return ResolveAllWithLinuxGetAddrInfo(host);
            }, ct);
        }

        private static ResolvedAddresses ResolveAllWithLinuxGetAddrInfo(string host)
        {
            IntPtr head = IntPtr.Zero;
            int status = getaddrinfo(host, null, IntPtr.Zero, out head);
            if (status != 0 || head == IntPtr.Zero)
                throw new InvalidOperationException($"getaddrinfo failed for '{host}' with status {status}");

            var v6 = new List<IPAddress>();
            var v4 = new List<IPAddress>();
            var seen = new HashSet<IPAddress>();

            try
            {
                for (IntPtr current = head; current != IntPtr.Zero;)
                {
                    LinuxAddrInfo info = Marshal.PtrToStructure<LinuxAddrInfo>(current);
                    if (info.Address != IntPtr.Zero)
                    {
                        IPAddress address = ReadLinuxSocketAddress(info.Family, info.Address, info.AddressLength);
                        if (address != null && seen.Add(address))
                        {
                            if (address.AddressFamily == AddressFamily.InterNetworkV6) v6.Add(address);
                            else if (address.AddressFamily == AddressFamily.InterNetwork) v4.Add(address);
                        }
                    }
                    current = info.Next;
                }
            }
            finally
            {
                freeaddrinfo(head);
            }

            if (v6.Count == 0 && v4.Count == 0)
                throw new InvalidOperationException($"getaddrinfo returned no IP addresses for '{host}'");

            return new ResolvedAddresses(v6.ToArray(), v4.ToArray());
        }

        private static IPAddress ReadLinuxSocketAddress(int family, IntPtr socketAddress, uint addressLength)
        {
            if (family == LinuxAfInet && addressLength >= 8)
            {
                byte[] bytes = new byte[4];
                Marshal.Copy(IntPtr.Add(socketAddress, 4), bytes, 0, bytes.Length);
                return new IPAddress(bytes);
            }

            if (family == LinuxAfInet6 && addressLength >= 28)
            {
                byte[] bytes = new byte[16];
                Marshal.Copy(IntPtr.Add(socketAddress, 8), bytes, 0, bytes.Length);
                uint scopeId = unchecked((uint)Marshal.ReadInt32(socketAddress, 24));
                return new IPAddress(bytes, scopeId);
            }

            return null;
        }
#endif
    }
}
