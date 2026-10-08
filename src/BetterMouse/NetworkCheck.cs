using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BetterMouse
{
    /// <summary>
    /// Plain-language answer to "why can't the PCs see each other?" (no admin needed):
    /// adapters, which adapter Windows routes the host through, direct connection tests
    /// (normal route and through each local adapter), and a verdict.
    /// </summary>
    internal static class NetworkCheck
    {
        enum Result { Ok, Refused, TimedOut, Failed }

        public static string Run(Settings s, NetworkManager net)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"BetterMouse {AppInfo.Version} network check, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"This PC: {Environment.MachineName} ({s.Role}), port {s.Port}");
            var conn = net?.Current;
            sb.AppendLine(conn != null ? $"Status: connected to {conn.PeerName} at {conn.PeerAddress}" : "Status: " + (net?.Detail ?? "not running"));
            if (conn != null)
            {
                sb.AppendLine(conn.LastRttMs < 0
                    ? "Round trip: not measured yet (needs BetterMouse 1.2 on both PCs)"
                    : $"Round trip: {conn.LastRttMs} ms now, worst {conn.WorstRttMs} ms since connecting " +
                      (conn.WorstRttMs >= 150 ? "(spikes: expect brief cursor stalls; Wi-Fi power saving or a busy network)" : "(good)"));
            }
            sb.AppendLine();

            var locals = NetUtil.LocalIPv4();
            bool vpnUp = NetUtil.AnyVpnUp(locals);
            sb.AppendLine("Network adapters:");
            foreach (var l in locals)
            {
                var tag = NetUtil.LooksLikeVpn(l.AdapterName, l.AdapterDescription) ? "  [VPN]" : l.IsVirtualOrVpn ? "  [virtual]" : "";
                sb.AppendLine($"  {l.Address}/{l.PrefixLength,-3} {l.AdapterName} ({l.AdapterDescription}){tag}");
            }
            if (locals.Count == 0) sb.AppendLine("  none – this PC has no network connection");
            sb.AppendLine();

            if (s.Role == Role.Host)
            {
                bool rule = Firewall.RuleLooksPresent();
                sb.AppendLine("Firewall rule for BetterMouse: " + (rule ? "present (all network types)" : "MISSING"));
                sb.AppendLine();
                sb.AppendLine("Verdict:");
                if (conn != null) sb.AppendLine("  Working: the other PC is connected.");
                else if (!rule) sb.AppendLine("  Add the firewall rule (tray menu > Allow through Windows Firewall), then let the other PC reconnect.");
                else sb.AppendLine("  This PC is listening. Run the Network check on the other PC (the client) to see why it can't get here.");
                return sb.ToString();
            }

            var targets = new List<IPAddress>();
            foreach (var token in NetworkManager.SplitAddresses(s.PeerAddress))
                if (NetworkManager.TryParseEndPoint(token, s.Port, out var ep) && !targets.Contains(ep.Address)) targets.Add(ep.Address);
            if (NetworkManager.TryParseEndPoint(s.LastGoodAddress, s.Port, out var last) && !targets.Contains(last.Address)) targets.Add(last.Address);
            if (targets.Count == 0)
            {
                sb.AppendLine("No host IP address is set (Settings), so only the LAN search can find the host.");
                return sb.ToString();
            }

            bool anyOk = false, anyRefused = false, rescuedByAdapter = false, routedIntoVpn = false;
            foreach (var target in targets.Where(t => t.AddressFamily == AddressFamily.InterNetwork))
            {
                sb.AppendLine($"Host {target}:");
                var route = NetUtil.RouteFor(target, locals);
                bool routeIsVpn = route != null && NetUtil.LooksLikeVpn(route.AdapterName, route.AdapterDescription ?? "");
                routedIntoVpn |= routeIsVpn;
                sb.AppendLine($"  Windows sends traffic for it through: {route?.AdapterName ?? "unknown"}{(routeIsVpn ? "  <- the VPN" : "")}");
                var onLink = NetUtil.OnLinkSources(target, locals);
                sb.AppendLine(onLink.Count > 0
                    ? "  Same local network as: " + string.Join(", ", onLink.Select(a => $"{locals.First(l => l.Address.Equals(a)).AdapterName} ({a})"))
                    : "  Not on the same subnet as any Wi-Fi/Ethernet adapter of this PC");

                var normal = TryConnect(target, s.Port, null, out var normalText);
                sb.AppendLine("  Connect (normal route): " + normalText);
                anyOk |= normal == Result.Ok;
                anyRefused |= normal == Result.Refused;
                foreach (var source in onLink)
                {
                    var name = locals.First(l => l.Address.Equals(source)).AdapterName;
                    var r = TryConnect(target, s.Port, source, out var text);
                    sb.AppendLine($"  Connect through {name}: {text}");
                    anyOk |= r == Result.Ok;
                    anyRefused |= r == Result.Refused;
                    if (r == Result.Ok && normal != Result.Ok) rescuedByAdapter = true;
                }
                sb.AppendLine("  Ping: " + Ping(target) + " (many PCs don't answer ping; that alone is fine)");
                sb.AppendLine();
            }

            sb.AppendLine("Verdict:");
            if (anyOk && rescuedByAdapter)
            {
                sb.AppendLine("  Works. Your VPN claims your home network's addresses for its own tunnel (its networks");
                sb.AppendLine("  overlap with yours), but the host answers through your local adapter, and BetterMouse");
                sb.AppendLine("  now connects that way automatically.");
            }
            else if (anyOk)
            {
                sb.AppendLine("  The host is reachable. If BetterMouse still doesn't connect, check that both PCs use");
                sb.AppendLine("  the same security key and port (the tray icon turns red on a key mismatch).");
            }
            else if (anyRefused)
            {
                sb.AppendLine($"  The host PC answered, but nothing is listening on port {s.Port}: start BetterMouse on");
                sb.AppendLine("  the host and make sure both PCs use the same port.");
            }
            else if (vpnUp)
            {
                sb.AppendLine("  Nothing reaches your home network while the VPN is connected. The VPN is set to block");
                sb.AppendLine("  the local network (FortiClient: \"exclusive routing\", or \"local LAN access\" turned off).");
                sb.AppendLine("  That is a security setting of the VPN, usually controlled by your company's IT;");
                sb.AppendLine("  BetterMouse doesn't try to get around it. Options:");
                sb.AppendLine("   - ask IT whether your VPN profile can allow local LAN access / split tunnelling;");
                sb.AppendLine("   - if you created this VPN connection yourself, look for a local-LAN-access option in it;");
                sb.AppendLine("   - otherwise BetterMouse reconnects by itself a few seconds after the VPN disconnects.");
                if (routedIntoVpn)
                    sb.AppendLine("  (Windows also routes the host's address into the VPN, and dialing through the local adapter was blocked too.)");
            }
            else
            {
                sb.AppendLine("  The host can't be reached. Check the IP address (the host's Settings shows it), that both");
                sb.AppendLine("  PCs are on the same network, and that the host has its firewall rule");
                sb.AppendLine("  (host tray menu > Allow through Windows Firewall).");
            }
            return sb.ToString();
        }

        static Result TryConnect(IPAddress target, int port, IPAddress source, out string text)
        {
            TcpClient c = null;
            try
            {
                c = source == null ? new TcpClient(target.AddressFamily) : new TcpClient(new IPEndPoint(source, 0));
                var t = c.ConnectAsync(target, port);
                if (!t.Wait(2500)) { text = "no answer (timed out)"; return Result.TimedOut; }
                text = "OK";
                return Result.Ok;
            }
            catch (Exception ex)
            {
                var se = ex.GetBaseException() as SocketException;
                if (se?.SocketErrorCode == SocketError.ConnectionRefused) { text = "refused (PC is there, BetterMouse not listening)"; return Result.Refused; }
                if (se?.SocketErrorCode == SocketError.TimedOut) { text = "no answer (timed out)"; return Result.TimedOut; }
                text = "failed: " + ex.GetBaseException().Message;
                return Result.Failed;
            }
            finally
            {
                try { c?.Close(); } catch { }
            }
        }

        static string Ping(IPAddress target)
        {
            try
            {
                using (var p = new Ping())
                {
                    var r = p.Send(target, 1000);
                    return r.Status == IPStatus.Success ? $"reply in {r.RoundtripTime} ms" : r.Status.ToString();
                }
            }
            catch (Exception ex)
            {
                return "failed: " + ex.GetBaseException().Message;
            }
        }

        /// <summary>Shows the check in a window with Copy / Run again.</summary>
        public static void ShowDialog(Func<Settings> settings, Func<NetworkManager> net)
        {
            var form = new Form
            {
                Text = "BetterMouse – Network check",
                Font = SystemFonts.MessageBoxFont,
                StartPosition = FormStartPosition.CenterScreen,
                Icon = Icons.Create(Icons.Online),
                ShowInTaskbar = true,
            };
            using (var g = form.CreateGraphics())
                form.Size = new Size((int)(860 * g.DpiX / 96f), (int)(600 * g.DpiY / 96f));
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5f),
                BackColor = SystemColors.Window,
            };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            var copy = new Button { Text = "Copy", AutoSize = true };
            var again = new Button { Text = "Run again", AutoSize = true };
            close.Click += (s, e) => form.Close();
            copy.Click += (s, e) => { try { Clipboard.SetText(box.Text); } catch { } };
            buttons.Controls.AddRange(new Control[] { close, copy, again });
            form.Controls.Add(box);
            form.Controls.Add(buttons);
            form.CancelButton = close;

            void Start()
            {
                box.Text = "Checking… (takes a few seconds)";
                again.Enabled = false;
                Task.Run(() => Run(settings(), net())).ContinueWith(t =>
                {
                    if (form.IsDisposed) return;
                    box.Text = (t.Status == TaskStatus.RanToCompletion ? t.Result : "Check failed: " + t.Exception?.GetBaseException().Message)
                        .Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                    box.Select(0, 0);
                    again.Enabled = true;
                    Log.Info("Network check:" + Environment.NewLine + box.Text);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }

            again.Click += (s, e) => Start();
            form.Shown += (s, e) => Start();
            form.FormClosed += (s, e) => form.Dispose();
            form.Show();
        }
    }
}
