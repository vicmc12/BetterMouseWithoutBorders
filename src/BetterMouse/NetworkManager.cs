using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BetterMouse
{
    internal enum LinkState { Idle, Waiting, Connected, AuthFailed, Error }

    /// <summary>
    /// Keeps exactly one live connection to the other PC, forever.
    /// Host: listens on every interface (LAN, Wi-Fi, direct cable, IPv4 and IPv6) and replaces
    /// a stale connection whenever the client dials in again.
    /// Client: dials the configured address(es), the last address that worked and whatever LAN
    /// discovery finds, in parallel, and retries with a short back-off (max 3 s). Network changes
    /// and "Reconnect now" retry immediately. No DNS, internet or cloud service is involved
    /// unless you type a host name instead of an IP.
    /// </summary>
    internal sealed class NetworkManager : IDisposable
    {
        const int DialTimeoutMs = 2500;
        const int DiscoveryTimeoutMs = 1200;
        const int MaxBackoffMs = 3000;
        const int AuthFailureBackoffMs = 10000;

        readonly Settings settings;
        readonly byte[] masterKey;
        readonly string localName;
        readonly Action<Connection, byte[]> onMessage;
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool running;
        bool protocolFailure; // client loop only
        Connection current;
        Thread worker;
        TcpListener listener;
        DiscoveryResponder responder;

        public event Action<Connection> Connected;
        public event Action<Connection> Disconnected;
        public event Action<LinkState, string> StatusChanged;
        public event Action<string> LastGoodAddressChanged;

        /// <summary>Tests bind to loopback so no firewall prompt appears.</summary>
        internal IPAddress ListenAddress { get; set; }

        public LinkState State { get; private set; } = LinkState.Idle;
        public string Detail { get; private set; } = "";
        public Connection Current => Volatile.Read(ref current);
        public bool IsConnected => Current != null;

        public NetworkManager(Settings settings, byte[] masterKey, string localName, Action<Connection, byte[]> onMessage)
        {
            this.settings = settings.Clone();
            this.masterKey = masterKey;
            this.localName = localName;
            this.onMessage = onMessage;
        }

        public void Start()
        {
            if (running) return;
            running = true;
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
            worker = new Thread(settings.Role == Role.Host ? (ThreadStart)HostLoop : ClientLoop)
            {
                IsBackground = true,
                Name = "BetterMouse " + settings.Role,
            };
            worker.Start();
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            NetworkChange.NetworkAddressChanged -= OnAddressChanged;
            wake.Set();
            lock (gate) { try { listener?.Stop(); } catch { } }
            responder?.Dispose();
            Current?.CloseGracefully(AppInfo.Name + " stopped");
            worker?.Join(3000);
            SetStatus(LinkState.Idle, "Stopped");
        }

        public void Dispose() => Stop();

        public void Send(byte[] message) => Current?.Send(message);

        public void SendBulk(IFrameSource source)
        {
            var c = Current;
            if (c != null) c.SendBulk(source);
            else source.Dispose();
        }

        /// <summary>Drops the current link (if any) and reconnects right away.</summary>
        public void Kick()
        {
            Current?.Close("Reconnect requested");
            wake.Set();
        }

        void OnAddressChanged(object sender, EventArgs e)
        {
            Log.Info("Network addresses changed");
            wake.Set();
        }

        // ---------------------------------------------------------------- host

        void HostLoop()
        {
            while (running)
            {
                TcpListener l = null;
                try
                {
                    if (responder == null)
                    {
                        try { responder = new DiscoveryResponder(settings.Port, masterKey, ListenAddress); }
                        catch (Exception ex) { Log.Warn("LAN discovery responder unavailable: " + ex.Message); }
                    }

                    l = CreateListener();
                    l.Start(8);
                    lock (gate) listener = l;
                    Log.Info($"Listening on {l.LocalEndpoint}");
                    if (Current == null) SetStatus(LinkState.Waiting, $"Waiting for the other PC (port {settings.Port})");

                    while (running)
                    {
                        var client = l.AcceptTcpClient();
                        ThreadPool.QueueUserWorkItem(_ => Incoming(client));
                    }
                }
                catch (Exception ex)
                {
                    if (!running) break;
                    Log.Warn("Listener error: " + ex.Message);
                    if (Current == null)
                    {
                        var inUse = ex is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse;
                        SetStatus(LinkState.Error, inUse ? $"Port {settings.Port} is used by another program" : "Network error: " + ex.Message);
                    }
                    wake.WaitOne(2000);
                }
                finally
                {
                    lock (gate)
                    {
                        try { l?.Stop(); } catch { }
                        listener = null;
                    }
                }
            }
        }

        TcpListener CreateListener()
        {
            if (ListenAddress != null) return new TcpListener(ListenAddress, settings.Port);
            if (Socket.OSSupportsIPv6)
            {
                try
                {
                    var l = new TcpListener(IPAddress.IPv6Any, settings.Port);
                    l.Server.DualMode = true; // IPv4 + IPv6 on one socket
                    return l;
                }
                catch (Exception ex)
                {
                    Log.Info("Dual-mode listener unavailable, using IPv4 only: " + ex.Message);
                }
            }
            return new TcpListener(IPAddress.Any, settings.Port);
        }

        void Incoming(TcpClient client)
        {
            var from = Describe(client.Client.RemoteEndPoint as IPEndPoint);
            try
            {
                var conn = Connection.Establish(client, Role.Host, masterKey, localName);
                Activate(conn);
            }
            catch (AuthException)
            {
                Log.Warn($"Rejected {from}: security key mismatch");
                client.Close();
                if (Current == null) SetStatus(LinkState.AuthFailed, $"Security key does not match the PC at {from}");
            }
            catch (ProtocolException ex)
            {
                Log.Warn($"Rejected {from}: {ex.Message}");
                client.Close();
                if (Current == null) SetStatus(LinkState.Error, ex.Message);
            }
            catch (Exception ex)
            {
                Log.Info($"Handshake with {from} failed: {ex.Message}");
                client.Close();
            }
        }

        // ---------------------------------------------------------------- client

        void ClientLoop()
        {
            int failures = 0;
            while (running)
            {
                var conn = TryConnect(out bool authFailed, out string detail);
                if (!running)
                {
                    conn?.Close("Stopped");
                    break;
                }

                if (conn != null)
                {
                    failures = 0;
                    var address = conn.PeerAddress.ToString();
                    if (address != settings.LastGoodAddress)
                    {
                        settings.LastGoodAddress = address;
                        LastGoodAddressChanged?.Invoke(address);
                    }
                    Activate(conn);
                    // A network change also sets 'wake'; a link that survived it is kept.
                    while (running && !conn.IsClosed)
                        WaitHandle.WaitAny(new WaitHandle[] { conn.ClosedEvent, wake });
                    if (running) Thread.Sleep(200);
                    continue;
                }

                failures++;
                bool configProblem = authFailed || protocolFailure;
                int delay = configProblem ? AuthFailureBackoffMs : Math.Min(MaxBackoffMs, 250 << Math.Min(failures, 4));
                SetStatus(authFailed ? LinkState.AuthFailed : protocolFailure ? LinkState.Error : LinkState.Waiting, detail);
                wake.WaitOne(delay);
            }
        }

        Connection TryConnect(out bool authFailed, out string detail)
        {
            authFailed = false;
            protocolFailure = false;
            var tried = new List<IPEndPoint>();
            var candidates = new List<IPEndPoint>();
            var hostNames = new List<string>();

            foreach (var token in SplitAddresses(settings.PeerAddress))
            {
                if (TryParseEndPoint(token, settings.Port, out var ep)) AddUnique(candidates, ep);
                else hostNames.Add(token);
            }
            if (TryParseEndPoint(settings.LastGoodAddress, settings.Port, out var last)) AddUnique(candidates, last);

            if (State != LinkState.AuthFailed && State != LinkState.Error) // keep showing the real problem while retrying
            {
                var what = candidates.Count > 0 ? string.Join(", ", candidates.Select(c => c.Address)) : "the local network";
                SetStatus(LinkState.Waiting, "Connecting to " + what + "…");
            }

            // LAN discovery runs alongside the direct attempts, so a moved host is found quickly.
            Task<List<IPEndPoint>> discovery = null;
            if (settings.Discovery)
            {
                discovery = Task.Run(() => Discovery.Find(settings.Port, masterKey, DiscoveryTimeoutMs));
                discovery.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }

            // Host names: resolved with a short timeout so a dead DNS server can't stall us.
            foreach (var name in hostNames)
            {
                try
                {
                    var t = Dns.GetHostAddressesAsync(name);
                    if (t.Wait(1500))
                        foreach (var ip in t.Result.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
                            AddUnique(candidates, new IPEndPoint(ip, settings.Port));
                }
                catch (Exception ex)
                {
                    Log.Info($"Could not resolve '{name}': {ex.GetBaseException().Message}");
                }
            }

            string failure = null;
            var conn = DialAny(candidates, ref authFailed, ref failure);
            tried.AddRange(candidates);

            if (conn == null && discovery != null)
            {
                List<IPEndPoint> found;
                try { found = discovery.Wait(DiscoveryTimeoutMs + 1000) ? discovery.Result : new List<IPEndPoint>(); }
                catch { found = new List<IPEndPoint>(); }
                var fresh = found.Where(f => !tried.Contains(f)).ToList();
                if (fresh.Count > 0)
                {
                    Log.Info("LAN discovery found: " + string.Join(", ", fresh));
                    conn = DialAny(fresh, ref authFailed, ref failure);
                    tried.AddRange(fresh);
                }
            }

            if (conn != null)
            {
                detail = "";
                return conn;
            }

            if (failure != null) detail = failure;
            else if (tried.Count == 0) detail = settings.Discovery ? "Host not found on the local network. Retrying…" : "No host address set (open Settings)";
            else detail = "Host not reachable (" + string.Join(", ", tried.Select(t => t.Address)) + "). Retrying…";
            if (failure == null && NetUtil.AnyVpnUp())
                detail = "Host not reachable while a VPN is connected – tray menu > Network check";
            return null;
        }

        /// <summary>Dials every candidate at once; the first one to accept wins.</summary>
        Connection DialAny(List<IPEndPoint> endpoints, ref bool authFailed, ref string failure)
        {
            if (endpoints.Count == 0) return null;
            var attempts = new List<(IPEndPoint ep, TcpClient client, Task task)>();
            var locals = NetUtil.LocalIPv4();
            foreach (var ep in endpoints)
            {
                attempts.Add(Dial(ep, null));
                // A VPN whose networks overlap the home network (e.g. both 192.168.x) makes Windows
                // send traffic for the host into the tunnel. When the host is on the subnet of a
                // real Wi-Fi/Ethernet adapter, also dial from that adapter's address so the packets
                // stay on the local network. (A VPN that deliberately blocks local access still
                // blocks this; see NetworkCheck.)
                var route = NetUtil.RouteFor(ep.Address, locals);
                foreach (var source in NetUtil.OnLinkSources(ep.Address, locals))
                {
                    var owner = locals.First(l => l.Address.Equals(source));
                    if (route != null && route.InterfaceIndex == owner.InterfaceIndex) continue; // already the normal path
                    LogOnce($"route-{ep.Address}-{source}",
                        $"Windows routes {ep.Address} via \"{route?.AdapterName ?? "?"}\"; also dialing through \"{owner.AdapterName}\" ({source})");
                    attempts.Add(Dial(ep, source));
                }
            }

            var sw = Stopwatch.StartNew();
            var pending = attempts.ToList();
            (IPEndPoint ep, TcpClient client, Task task) winner = default;
            while (winner.client == null && pending.Count > 0 && running)
            {
                int remaining = DialTimeoutMs - (int)sw.ElapsedMilliseconds;
                if (remaining <= 0) break;
                int i = Task.WaitAny(pending.Select(p => p.task).ToArray(), remaining);
                if (i < 0) break;
                if (pending[i].task.Status == TaskStatus.RanToCompletion) winner = pending[i];
                pending.RemoveAt(i);
            }
            foreach (var a in attempts)
                if (a.client != winner.client)
                    try { a.client.Close(); } catch { }

            if (winner.client == null) return null;
            try
            {
                return Connection.Establish(winner.client, Role.Client, masterKey, localName);
            }
            catch (AuthException)
            {
                authFailed = true;
                failure = $"Security key does not match the host at {winner.ep.Address}";
            }
            catch (ProtocolException ex)
            {
                protocolFailure = true;
                failure = ex.Message;
            }
            catch (Exception ex)
            {
                failure = $"Handshake with {winner.ep.Address} failed: {ex.Message}";
            }
            Log.Warn(failure);
            try { winner.client.Close(); } catch { }
            return null;
        }

        static (IPEndPoint ep, TcpClient client, Task task) Dial(IPEndPoint ep, IPAddress source)
        {
            TcpClient c = null;
            Task t;
            try
            {
                c = source == null ? new TcpClient(ep.AddressFamily) : new TcpClient(new IPEndPoint(source, 0));
                t = c.ConnectAsync(ep.Address, ep.Port);
            }
            catch (Exception ex)
            {
                c = c ?? new TcpClient(ep.AddressFamily);
                t = Task.FromException(ex);
            }
            t.ContinueWith(x => { var _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return (ep, c, t);
        }

        readonly HashSet<string> loggedOnce = new HashSet<string>();

        void LogOnce(string key, string message)
        {
            lock (loggedOnce)
                if (!loggedOnce.Add(key)) return;
            Log.Info(message);
        }

        // ---------------------------------------------------------------- shared

        void Activate(Connection conn)
        {
            Connection old;
            lock (gate)
            {
                if (!running)
                {
                    conn.Close("Stopped");
                    return;
                }
                old = current;
                current = conn;
            }
            old?.Close("Replaced by a new connection from the same PC");
            conn.Start(onMessage, OnClosed);
            Log.Info($"Connected to {conn.PeerName} at {conn.PeerAddress}");
            SetStatus(LinkState.Connected, $"Connected to {conn.PeerName} ({conn.PeerAddress})");
            Connected?.Invoke(conn);
        }

        void OnClosed(Connection conn, string reason)
        {
            bool wasCurrent;
            lock (gate)
            {
                wasCurrent = current == conn;
                if (wasCurrent) current = null;
            }
            Log.Info($"Connection to {conn.PeerName} closed: {reason}");
            try { Disconnected?.Invoke(conn); }
            catch (Exception ex) { Log.Error("Disconnect handler failed", ex); }
            if (wasCurrent && running)
            {
                SetStatus(LinkState.Waiting, settings.Role == Role.Host
                    ? $"Connection lost ({reason}). Waiting for the other PC…"
                    : $"Connection lost ({reason}). Reconnecting…");
                wake.Set();
            }
        }

        void SetStatus(LinkState state, string detail)
        {
            State = state;
            Detail = detail;
            try { StatusChanged?.Invoke(state, detail); }
            catch (Exception ex) { Log.Error("Status handler failed", ex); }
        }

        static void AddUnique(List<IPEndPoint> list, IPEndPoint ep)
        {
            if (!list.Contains(ep)) list.Add(ep);
        }

        internal static IEnumerable<string> SplitAddresses(string text) =>
            (text ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());

        /// <summary>Accepts "1.2.3.4", "1.2.3.4:5000", "fe80::1%12", "[fe80::1%12]:5000".</summary>
        internal static bool TryParseEndPoint(string text, int defaultPort, out IPEndPoint ep)
        {
            ep = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            int port = defaultPort;
            string host = text;
            if (text.StartsWith("["))
            {
                int close = text.IndexOf(']');
                if (close < 0) return false;
                host = text.Substring(1, close - 1);
                var rest = text.Substring(close + 1);
                if (rest.StartsWith(":") && !int.TryParse(rest.Substring(1), out port)) return false;
            }
            else if (text.Count(c => c == ':') == 1)
            {
                int colon = text.IndexOf(':');
                host = text.Substring(0, colon);
                if (!int.TryParse(text.Substring(colon + 1), out port)) return false;
            }
            if (port < 1 || port > 65535) return false;
            if (!IPAddress.TryParse(host, out var ip)) return false;
            ep = new IPEndPoint(ip, port);
            return true;
        }

        static string Describe(IPEndPoint ep)
        {
            if (ep == null) return "unknown";
            var ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
            return ip.ToString();
        }
    }
}
