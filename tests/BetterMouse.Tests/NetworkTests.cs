using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Xunit;

namespace BetterMouse.Tests
{
    /// <summary>
    /// Real sockets on 127.0.0.1 (no firewall prompt): handshake, delivery, wrong key,
    /// heartbeat timeout and automatic reconnection.
    /// </summary>
    public class NetworkTests : IDisposable
    {
        static readonly byte[] Key = Crypto.DeriveMasterKey("test-key-123");
        readonly List<NetworkManager> managers = new List<NetworkManager>();
        readonly int port = FreePort();

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        NetworkManager Host(Action<Connection, byte[]> onMessage = null, byte[] key = null)
        {
            var s = new Settings { Role = Role.Host, Port = port, SecurityKey = "unused" };
            var m = new NetworkManager(s, key ?? Key, "HOST-PC", onMessage ?? ((c, b) => { })) { ListenAddress = IPAddress.Loopback };
            managers.Add(m);
            return m;
        }

        NetworkManager Client(Action<Connection, byte[]> onMessage = null, byte[] key = null)
        {
            var s = new Settings { Role = Role.Client, Port = port, PeerAddress = "127.0.0.1", Discovery = false, SecurityKey = "unused" };
            var m = new NetworkManager(s, key ?? Key, "CLIENT-PC", onMessage ?? ((c, b) => { }));
            managers.Add(m);
            return m;
        }

        static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        public void Dispose()
        {
            foreach (var m in managers) m.Stop();
        }

        [Fact]
        public void ConnectsAndDeliversMessagesBothWays()
        {
            var atHost = new ConcurrentQueue<byte[]>();
            var atClient = new ConcurrentQueue<byte[]>();
            var host = Host((c, m) => atHost.Enqueue(m));
            var client = Client((c, m) => atClient.Enqueue(m));
            host.Start();
            client.Start();

            Assert.True(WaitFor(() => host.IsConnected && client.IsConnected, 5000), "not connected");
            Assert.Equal("HOST-PC", client.Current.PeerName);
            Assert.Equal("CLIENT-PC", host.Current.PeerName);
            Assert.Equal(LinkState.Connected, client.State);

            for (int i = 0; i < 500; i++) client.Send(Msg.MouseMove(i, -i));
            host.Send(Msg.Enter(Edge.Left, 0.5));

            Assert.True(WaitFor(() => atHost.Count == 500 && atClient.Count == 1, 5000));
            int n = 0;
            foreach (var m in atHost)
            {
                var r = new PacketReader(m);
                Assert.Equal(n, r.Int32());
                Assert.Equal(-n, r.Int32());
                n++;
            }
        }

        [Fact]
        public void InputIsNotStuckBehindABigClipboardTransfer()
        {
            var received = new ConcurrentQueue<(MsgType type, long ticks)>();
            var host = Host((c, m) => received.Enqueue(((MsgType)m[0], Stopwatch.GetTimestamp())));
            var client = Client();
            host.Start();
            client.Start();
            Assert.True(WaitFor(() => client.IsConnected && host.IsConnected, 5000));

            var big = new byte[32 * 1024 * 1024]; // 1024 chunks
            client.SendBulk(OutgoingClip.ForBytes(1, ClipKind.Text, big));
            Assert.True(WaitFor(() => received.Any(r => r.type == MsgType.ClipData), 5000), "transfer did not start");
            client.Send(Msg.MouseMove(1, 1));

            Assert.True(WaitFor(() => received.Any(r => r.type == MsgType.ClipEnd), 30000), "transfer did not finish");
            var list = received.ToList();
            int moveIndex = list.FindIndex(r => r.type == MsgType.MouseMove);
            int endIndex = list.FindIndex(r => r.type == MsgType.ClipEnd);
            Assert.True(moveIndex >= 0, "move lost");
            // The move must overtake the queued bulk data, not wait behind it.
            Assert.True(endIndex - moveIndex > 500, $"mouse move waited behind the transfer (arrived {endIndex - moveIndex} frames before the end)");
        }

        [Fact]
        public void RoundTripIsMeasuredAndWarmHeartbeatsAreFast()
        {
            var host = Host();
            var client = Client();
            host.Start();
            client.Start();
            Assert.True(WaitFor(() => client.IsConnected && host.IsConnected, 5000));
            Assert.True(WaitFor(() => client.Current.LastRttMs >= 0 && host.Current.LastRttMs >= 0, 3000), "no round-trip measurement");
            Assert.InRange(client.Current.LastRttMs, 0, 100); // loopback

            // While "warm", the other side hears from us every ~100 ms instead of every 500 ms.
            var conn = client.Current;
            var hostSide = host.Current;
            int maxSilence = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1200)
            {
                conn.KeepWarm();
                maxSilence = Math.Max(maxSilence, hostSide.SilenceMs);
                Thread.Sleep(10);
            }
            Assert.InRange(maxSilence, 0, 250);
        }

        [Fact]
        public void WrongSecurityKeyIsReportedAndNeverConnects()
        {
            var host = Host();
            var client = Client(key: Crypto.DeriveMasterKey("a-different-key"));
            host.Start();
            client.Start();
            Assert.True(WaitFor(() => client.State == LinkState.AuthFailed, 6000), "client state: " + client.State + " " + client.Detail);
            Assert.False(host.IsConnected);
            Assert.False(client.IsConnected);
            Assert.Contains("key", client.Detail);
        }

        [Fact]
        public void BothPcsAsHostIsExplained()
        {
            var host = Host();
            host.Start();
            Thread.Sleep(200);
            var tcp = new TcpClient();
            tcp.Connect(IPAddress.Loopback, port);
            var ex = Record.Exception(() => Connection.Establish(tcp, Role.Host, Key, "OTHER"));
            Assert.IsType<ProtocolException>(ex);
            Assert.Contains("Host", ex.Message);
        }

        [Fact]
        public void ClientStartedBeforeHostConnectsWhenHostAppears()
        {
            var client = Client();
            client.Start();
            Thread.Sleep(1500);
            Assert.False(client.IsConnected);
            var host = Host();
            host.Start();
            Assert.True(WaitFor(() => client.IsConnected, 6000), "client did not find the late host: " + client.Detail);
        }

        [Fact]
        public void ReconnectsAfterHostRestart()
        {
            var host = Host();
            var client = Client();
            int connects = 0, disconnects = 0;
            client.Connected += c => Interlocked.Increment(ref connects);
            client.Disconnected += c => Interlocked.Increment(ref disconnects);
            host.Start();
            client.Start();
            Assert.True(WaitFor(() => client.IsConnected, 5000));

            host.Stop(); // graceful: client is told immediately
            Assert.True(WaitFor(() => !client.IsConnected && disconnects == 1, 3000), "client did not notice the host leaving");

            Thread.Sleep(1000);
            var host2 = Host();
            host2.Start();
            Assert.True(WaitFor(() => client.IsConnected && host2.IsConnected, 6000), "no reconnect: " + client.Detail);
            Assert.Equal(2, connects);
        }

        [Fact]
        public void SilentPeerIsDetectedByHeartbeatTimeout()
        {
            // A fake host that completes the handshake and then goes silent, like a PC whose
            // network cable was pulled (no FIN/RST ever arrives).
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            var silent = new List<Connection>();
            var acceptThread = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var tcp = listener.AcceptTcpClient();
                        silent.Add(Connection.Establish(tcp, Role.Host, Key, "SILENT")); // never Start()ed
                    }
                }
                catch { }
            }) { IsBackground = true };
            acceptThread.Start();

            try
            {
                var client = Client();
                var lost = new ManualResetEventSlim();
                client.Disconnected += c => lost.Set();
                client.Start();
                Assert.True(WaitFor(() => client.IsConnected, 5000));
                var sw = Stopwatch.StartNew();
                Assert.True(lost.Wait(Connection.TimeoutMs + 3000), "dead link not detected");
                Assert.InRange(sw.ElapsedMilliseconds, Connection.TimeoutMs - 1500, Connection.TimeoutMs + 3000);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public void DiscoveryFindsOnlyHostsWithTheSameKey()
        {
            using (new DiscoveryResponder(port, Key, IPAddress.Loopback))
            {
                var found = Discovery.Find(port, Key, 800, new[] { IPAddress.Loopback });
                Assert.Single(found);
                Assert.Equal(IPAddress.Loopback, found[0].Address);
                Assert.Equal(port, found[0].Port);

                var none = Discovery.Find(port, Crypto.DeriveMasterKey("other-key"), 500, new[] { IPAddress.Loopback });
                Assert.Empty(none);
            }
        }

        [Fact]
        public void BroadcastTargetsIncludeLimitedBroadcast()
        {
            Assert.Contains(IPAddress.Broadcast, Discovery.BroadcastTargets());
        }
    }
}
