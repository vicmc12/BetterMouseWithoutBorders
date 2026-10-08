using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace BetterMouse
{
    /// <summary>One IPv4 address of a network adapter.</summary>
    internal sealed class LocalAddress
    {
        public IPAddress Address;
        public IPAddress Mask;
        public int PrefixLength;
        public string AdapterName;
        public string AdapterDescription;
        public int InterfaceIndex;
        public bool IsVirtualOrVpn;
        public bool HasGateway;

        public bool SameSubnet(IPAddress other)
        {
            if (Mask == null || other.AddressFamily != AddressFamily.InterNetwork) return false;
            var a = Address.GetAddressBytes();
            var b = other.GetAddressBytes();
            var m = Mask.GetAddressBytes();
            for (int i = 0; i < 4; i++)
                if ((a[i] & m[i]) != (b[i] & m[i])) return false;
            return PrefixLength > 0;
        }

        public override string ToString() => $"{Address}/{PrefixLength} on \"{AdapterName}\"";
    }

    internal static class NetUtil
    {
        static readonly string[] VirtualHints =
        {
            "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "wsl", "tap-", "tap adapter", "tun",
            "vpn", "fortinet", "forticlient", "ssl vpn", "anyconnect", "cisco", "globalprotect", "pangp",
            "juniper", "pulse", "wireguard", "tailscale", "zerotier", "openvpn", "nordlynx", "loopback",
        };

        static readonly string[] VpnHints =
        {
            "vpn", "fortinet", "forticlient", "anyconnect", "cisco", "globalprotect", "pangp", "juniper",
            "pulse", "wireguard", "openvpn", "nordlynx", "tap-", "tap adapter", "tun",
        };

        public static bool LooksVirtual(string name, string description) => ContainsAny(name + " " + description, VirtualHints);

        public static bool LooksLikeVpn(string name, string description) => ContainsAny(name + " " + description, VpnHints);

        static bool ContainsAny(string text, string[] hints)
        {
            text = text.ToLowerInvariant();
            return hints.Any(text.Contains);
        }

        /// <summary>All IPv4 addresses on adapters that are up, real LAN adapters first.</summary>
        public static List<LocalAddress> LocalIPv4()
        {
            var list = new List<LocalAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props;
                    try { props = ni.GetIPProperties(); } catch { continue; }
                    int index = -1;
                    try { index = props.GetIPv4Properties()?.Index ?? -1; } catch { }
                    bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        list.Add(new LocalAddress
                        {
                            Address = ua.Address,
                            Mask = ua.IPv4Mask,
                            PrefixLength = ua.IPv4Mask == null ? 0 : ua.IPv4Mask.GetAddressBytes().Sum(b => CountBits(b)),
                            AdapterName = ni.Name,
                            AdapterDescription = ni.Description,
                            InterfaceIndex = index,
                            IsVirtualOrVpn = LooksVirtual(ni.Name, ni.Description),
                            HasGateway = gateway,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not list network adapters: " + ex.Message);
            }
            return list.OrderBy(a => a.IsVirtualOrVpn ? 2 : a.HasGateway ? 0 : 1).ToList();
        }

        /// <summary>
        /// Real (non-VPN) adapter addresses whose subnet contains <paramref name="target"/>, i.e. the
        /// target is a device on that local network and should be reached through that adapter.
        /// </summary>
        public static List<IPAddress> OnLinkSources(IPAddress target, IEnumerable<LocalAddress> locals = null)
        {
            if (target.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(target)) return new List<IPAddress>();
            return (locals ?? LocalIPv4())
                .Where(l => !l.IsVirtualOrVpn && l.SameSubnet(target) && !l.Address.Equals(target))
                .Select(l => l.Address)
                .Distinct()
                .ToList();
        }

        /// <summary>The adapter Windows would use to reach <paramref name="target"/> (no admin needed).</summary>
        public static LocalAddress RouteFor(IPAddress target, List<LocalAddress> locals)
        {
            if (target.AddressFamily != AddressFamily.InterNetwork) return null;
            var bytes = target.GetAddressBytes();
            uint dest = BitConverter.ToUInt32(bytes, 0); // network byte order, as the API wants
            if (GetBestInterface(dest, out uint index) != 0) return null;
            return locals.FirstOrDefault(l => l.InterfaceIndex == index)
                   ?? new LocalAddress { AdapterName = "interface #" + index, AdapterDescription = "", InterfaceIndex = (int)index };
        }

        public static bool AnyVpnUp(List<LocalAddress> locals = null) =>
            (locals ?? LocalIPv4()).Any(l => LooksLikeVpn(l.AdapterName, l.AdapterDescription));

        static int CountBits(byte b)
        {
            int n = 0;
            for (; b != 0; b >>= 1) n += b & 1;
            return n;
        }

        [DllImport("iphlpapi.dll")]
        static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);
    }
}
