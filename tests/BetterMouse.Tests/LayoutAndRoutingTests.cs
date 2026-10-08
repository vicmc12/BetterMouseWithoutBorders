using System.Collections.Generic;
using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace BetterMouse.Tests
{
    public class LayoutSyncTests
    {
        [Fact]
        public void FreshInstallsAgreeOnTheHostsLayout()
        {
            // Both default to "right" with no timestamp: the client mirrors the host.
            Assert.Equal(Edge.Left, EdgeExtensions.SyncLayout(Edge.Right, 0, Role.Client, Edge.Right, 0));
            Assert.Null(EdgeExtensions.SyncLayout(Edge.Right, 0, Role.Host, Edge.Right, 0));
        }

        [Fact]
        public void NewerChoiceWinsOnEitherSide()
        {
            // The client user said "host is on my right" later than the host's setting.
            Assert.Equal(Edge.Left, EdgeExtensions.SyncLayout(Edge.Right, 100, Role.Host, Edge.Right, 200));
            Assert.Null(EdgeExtensions.SyncLayout(Edge.Top, 300, Role.Client, Edge.Left, 200)); // mine is newer: keep it
        }

        [Fact]
        public void AlreadyMirroredDoesNothing()
        {
            Assert.Null(EdgeExtensions.SyncLayout(Edge.Left, 1, Role.Client, Edge.Right, 999));
            Assert.Null(EdgeExtensions.SyncLayout(Edge.Bottom, 1, Role.Host, Edge.Top, 999));
        }

        [Fact]
        public void LayoutMessageRoundTrips()
        {
            var m = Msg.Layout(Edge.Bottom, 638640000000000000);
            var r = new PacketReader(m);
            Assert.Equal(MsgType.Layout, (MsgType)m[0]);
            Assert.Equal(Edge.Bottom, (Edge)r.Byte());
            Assert.Equal(638640000000000000, r.Int64());
        }
    }

    public class StaleEventTests
    {
        [Fact]
        public void EventsComputedFromThePreviousCursorSpotAreRecognised()
        {
            var middle = new System.Drawing.Point(960, 540);   // where the hidden cursor was parked
            var border = new System.Drawing.Point(1917, 300);  // where it was just put back
            Assert.True(KvmEngine.IsStale(new System.Drawing.Point(966, 541), middle, border));  // old event: middle + delta
            Assert.False(KvmEngine.IsStale(new System.Drawing.Point(1910, 302), middle, border)); // fresh event near the border
            Assert.False(KvmEngine.IsStale(new System.Drawing.Point(1700, 400), middle, border)); // big real flick inwards
        }

        [Fact]
        public void OnlyEventsFromBeforeTheWarpAreDropped()
        {
            var middle = new System.Drawing.Point(960, 540);
            var border = new System.Drawing.Point(1917, 300);
            const uint warp = 1000;
            // Produced before the warp: stale wherever it points.
            Assert.True(KvmEngine.IsStaleEvent(new System.Drawing.Point(966, 541), 990, warp, middle, border));
            // Produced after the warp: never dropped, even a huge fast flick that lands near the old spot.
            Assert.False(KvmEngine.IsStaleEvent(new System.Drawing.Point(1100, 500), 1001, warp, middle, border));
            // Same millisecond: decided by distance.
            Assert.True(KvmEngine.IsStaleEvent(new System.Drawing.Point(966, 541), warp, warp, middle, border));
            Assert.False(KvmEngine.IsStaleEvent(new System.Drawing.Point(1910, 302), warp, warp, middle, border));
            // Tick counter wrap-around.
            Assert.True(KvmEngine.IsStaleEvent(middle, uint.MaxValue - 5, 3, middle, border));
            Assert.False(KvmEngine.IsStaleEvent(border, 4, uint.MaxValue - 5, middle, border));
        }
    }

    public class RoutingTests
    {
        readonly ITestOutputHelper output;
        public RoutingTests(ITestOutputHelper output) { this.output = output; }

        static LocalAddress Addr(string ip, string mask, string name, bool vpn = false) => new LocalAddress
        {
            Address = IPAddress.Parse(ip),
            Mask = IPAddress.Parse(mask),
            PrefixLength = 24,
            AdapterName = name,
            AdapterDescription = vpn ? "Fortinet SSL VPN Virtual Ethernet Adapter" : "Intel Wi-Fi",
            IsVirtualOrVpn = vpn,
        };

        [Fact]
        public void HostOnTheWifiSubnetIsReachedThroughWifiNotTheVpn()
        {
            var locals = new List<LocalAddress>
            {
                Addr("192.168.3.20", "255.255.255.0", "Wi-Fi"),
                Addr("192.168.3.77", "255.255.255.0", "FortiClient", vpn: true), // VPN reusing the same range
                Addr("10.0.0.5", "255.255.255.0", "Ethernet 2"),
            };
            var sources = NetUtil.OnLinkSources(IPAddress.Parse("192.168.3.5"), locals);
            Assert.Equal(new[] { IPAddress.Parse("192.168.3.20") }, sources);
            Assert.Empty(NetUtil.OnLinkSources(IPAddress.Parse("172.16.0.1"), locals)); // not local: leave routing alone
            Assert.Empty(NetUtil.OnLinkSources(IPAddress.Parse("192.168.3.20"), locals)); // ourselves
        }

        [Fact]
        public void VpnAdaptersAreRecognised()
        {
            Assert.True(NetUtil.LooksLikeVpn("Ethernet 3", "Fortinet SSL VPN Virtual Ethernet Adapter"));
            Assert.True(NetUtil.LooksLikeVpn("FortiClient", "Fortinet Virtual Ethernet Adapter (NDIS 6.30)"));
            Assert.False(NetUtil.LooksLikeVpn("Wi-Fi 3", "Intel(R) Wi-Fi 6E AX211 160MHz"));
            Assert.False(NetUtil.LooksLikeVpn("Ethernet", "Realtek PCIe GbE Family Controller"));
        }

        [Fact]
        public void RouteLookupWorksWithoutAdmin()
        {
            var locals = NetUtil.LocalIPv4();
            foreach (var l in locals) output.WriteLine(l + (l.IsVirtualOrVpn ? " [virtual]" : ""));
            foreach (var l in locals)
            {
                var route = NetUtil.RouteFor(l.Address, locals); // an own address routes via its own adapter (or loopback)
                output.WriteLine($"route to {l.Address}: {route?.AdapterName}");
            }
        }

        [Fact]
        public void NetworkCheckProducesAVerdict()
        {
            var s = new Settings { Role = Role.Client, PeerAddress = "127.0.0.1", Port = 1, Discovery = false };
            var report = NetworkCheck.Run(s, null);
            output.WriteLine(report);
            Assert.Contains("Verdict:", report);
            Assert.Contains("refused", report); // loopback answers, nothing listens on port 1
        }
    }
}
