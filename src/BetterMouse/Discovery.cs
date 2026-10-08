using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BetterMouse
{
    /// <summary>
    /// Finds the host with a UDP broadcast on the local network, so the client still connects
    /// when the host's IP changes (DHCP, router reboot, direct cable with 169.254.x.x addresses).
    /// Packets are authenticated with the security key: only a matching host answers.
    /// The client only sends and reads replies, so it never needs a firewall rule.
    /// </summary>
    internal static class Discovery
    {
        static readonly byte[] QueryMagic = Encoding.ASCII.GetBytes("BMWBQ1");
        static readonly byte[] ReplyMagic = Encoding.ASCII.GetBytes("BMWBR1");
        const int NonceSize = 16, MacSize = 16;
        const int SIO_UDP_CONNRESET = -1744830452;

        public static byte[] BuildQuery(byte[] nonce, byte[] key)
        {
            var mac = Crypto.Hmac(key, Crypto.Label("discovery query"), nonce);
            return Concat(QueryMagic, nonce, mac.Take(MacSize).ToArray());
        }

        /// <summary>Returns the reply for a valid query, or null.</summary>
        public static byte[] BuildReply(byte[] query, byte[] key, int tcpPort)
        {
            if (query.Length != QueryMagic.Length + NonceSize + MacSize || !StartsWith(query, QueryMagic)) return null;
            var nonce = query.Skip(QueryMagic.Length).Take(NonceSize).ToArray();
            var expected = Crypto.Hmac(key, Crypto.Label("discovery query"), nonce);
            if (!Crypto.FixedTimeEquals(expected, 0, query, QueryMagic.Length + NonceSize, MacSize)) return null;
            var port = new[] { (byte)tcpPort, (byte)(tcpPort >> 8) };
            var mac = Crypto.Hmac(key, Crypto.Label("discovery reply"), nonce, port);
            return Concat(ReplyMagic, nonce, port, mac.Take(MacSize).ToArray());
        }

        public static bool CheckReply(byte[] reply, byte[] nonce, byte[] key, out int tcpPort)
        {
            tcpPort = 0;
            if (reply.Length != ReplyMagic.Length + NonceSize + 2 + MacSize || !StartsWith(reply, ReplyMagic)) return false;
            if (!Crypto.FixedTimeEquals(nonce, 0, reply, ReplyMagic.Length, NonceSize)) return false;
            var port = new[] { reply[ReplyMagic.Length + NonceSize], reply[ReplyMagic.Length + NonceSize + 1] };
            var expected = Crypto.Hmac(key, Crypto.Label("discovery reply"), nonce, port);
            if (!Crypto.FixedTimeEquals(expected, 0, reply, ReplyMagic.Length + NonceSize + 2, MacSize)) return false;
            tcpPort = port[0] | port[1] << 8;
            return true;
        }

        /// <summary>Broadcasts a query and collects the hosts that answer within the timeout.</summary>
        public static List<IPEndPoint> Find(int port, byte[] key, int timeoutMs, IEnumerable<IPAddress> targets = null)
        {
            var found = new List<IPEndPoint>();
            using (var udp = new UdpClient(AddressFamily.InterNetwork))
            {
                udp.EnableBroadcast = true;
                IgnoreConnReset(udp.Client);
                var nonce = Crypto.RandomBytes(NonceSize);
                var query = BuildQuery(nonce, key);
                bool sent = false;
                foreach (var target in targets ?? BroadcastTargets())
                {
                    try { udp.Send(query, query.Length, new IPEndPoint(target, port)); sent = true; }
                    catch (SocketException) { /* interface went away */ }
                }
                if (!sent) return found;

                var sw = Stopwatch.StartNew();
                while (true)
                {
                    int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
                    if (remaining <= 0) break;
                    udp.Client.ReceiveTimeout = remaining;
                    try
                    {
                        IPEndPoint from = null;
                        var data = udp.Receive(ref from);
                        if (CheckReply(data, nonce, key, out int tcpPort))
                        {
                            var ep = new IPEndPoint(from.Address, tcpPort);
                            if (!found.Any(f => f.Equals(ep))) found.Add(ep);
                        }
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        // ICMP noise from some interface; keep listening.
                    }
                }
            }
            return found;
        }

        /// <summary>255.255.255.255 plus the directed broadcast address of every active IPv4 interface.</summary>
        public static List<IPAddress> BroadcastTargets()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        var ip = ua.Address.GetAddressBytes();
                        var mask = ua.IPv4Mask.GetAddressBytes();
                        if (mask.All(b => b == 0)) continue;
                        var bc = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                        var addr = new IPAddress(bc);
                        if (!list.Contains(addr)) list.Add(addr);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not enumerate network interfaces: " + ex.Message);
            }
            return list;
        }

        public static void IgnoreConnReset(Socket s)
        {
            try { s.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { }
        }

        static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) if (data[i] != prefix[i]) return false;
            return true;
        }

        static byte[] Concat(params byte[][] parts)
        {
            var result = new byte[parts.Sum(p => p.Length)];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, result, o, p.Length); o += p.Length; }
            return result;
        }
    }

    /// <summary>Host side: answers discovery queries.</summary>
    internal sealed class DiscoveryResponder : IDisposable
    {
        readonly UdpClient udp;
        readonly byte[] key;
        readonly int tcpPort;
        volatile bool running = true;

        public DiscoveryResponder(int port, byte[] key, IPAddress bindAddress = null)
        {
            this.key = key;
            tcpPort = port;
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            Discovery.IgnoreConnReset(udp.Client);
            udp.Client.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, port));
            new Thread(Run) { IsBackground = true, Name = "BetterMouse discovery" }.Start();
        }

        void Run()
        {
            while (running)
            {
                try
                {
                    IPEndPoint from = null;
                    var data = udp.Receive(ref from);
                    var reply = Discovery.BuildReply(data, key, tcpPort);
                    if (reply != null) udp.Send(reply, reply.Length, from);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (!running) break;
                    Thread.Sleep(50);
                }
            }
        }

        public void Dispose()
        {
            running = false;
            try { udp.Close(); } catch { }
        }
    }
}
