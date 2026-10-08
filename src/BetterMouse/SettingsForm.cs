using System;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BetterMouse
{
    internal sealed class SettingsForm : Form
    {
        readonly float scale;
        readonly LayoutPicker layoutPicker;
        readonly Label sideLabel;
        readonly RadioButton hostRadio, clientRadio;
        readonly TextBox addressBox, keyBox;
        readonly CheckBox discoveryCheck, showKeyCheck, edgeCheck, clipCheck, filesCheck, autostartCheck, activityCheck;
        readonly NumericUpDown portBox, maxMbBox;
        readonly Button firewallButton;
        readonly Label firewallStatus;

        public Settings Result { get; private set; }
        public bool StartWithWindows { get; private set; }

        public SettingsForm(Settings current, bool firstRun, string otherName = null)
        {
            Text = "BetterMouse – Settings";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(S(14));
            Icon = Icons.Create(Icons.Online);
            int wrap = S(470);

            // Two columns so the window fits a 1080p screen even at 150 % scaling.
            var root = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            var left = Column();
            var right = Column();

            if (firstRun)
            {
                var intro = Note("Run BetterMouse on both PCs with the same security key. " +
                    "Make the PC where you have admin rights the Host, and the other PC the Client. " +
                    "Everything stays on your local network – no internet needed.", S(980), bold: false);
                root.Controls.Add(intro);
                root.SetColumnSpan(intro, 2);
            }
            root.Controls.Add(left);
            root.Controls.Add(right);

            var ips = LocalAddresses();
            var lines = new[] { $"This PC: {Environment.MachineName}" }
                .Concat(ips.Length == 0 ? new[] { "No network connection" }
                                        : ips.Select((a, i) => (i == 0 ? "IP address:  " : "                     ") + a))
                .ToArray();
            var thisPc = new TextBox
            {
                ReadOnly = true,
                Multiline = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Lines = lines,
                Width = wrap,
                Height = (Font.Height + S(1)) * lines.Length + S(2),
                Margin = new Padding(S(3), S(6), S(3), S(6)),
                TabStop = false,
            };
            left.Controls.Add(thisPc);

            // --- connection
            var conn = Group("Connection", out var connBody);
            hostRadio = new RadioButton { Text = "Host – waits for the other PC to connect (use on the PC where you are admin)", AutoSize = true };
            clientRadio = new RadioButton { Text = "Client – connects to the host (no admin rights needed)", AutoSize = true };
            connBody.Controls.Add(hostRadio);
            connBody.Controls.Add(clientRadio);

            addressBox = new TextBox { Width = S(230), Text = current.PeerAddress };
            connBody.Controls.Add(Row(Label("Host IP address:"), addressBox, Hint("e.g. 192.168.1.20")));
            discoveryCheck = new CheckBox { Text = "If it doesn't answer, search the local network for the host", AutoSize = true, Checked = current.Discovery };
            connBody.Controls.Add(discoveryCheck);

            portBox = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = current.Port, Width = S(80) };
            connBody.Controls.Add(Row(Label("Port:"), portBox, Hint("same on both PCs")));

            keyBox = new TextBox { Width = S(200), Text = current.SecurityKey, UseSystemPasswordChar = true };
            var generate = new Button { Text = "Generate", AutoSize = true };
            generate.Click += (s, e) => { keyBox.Text = Crypto.GenerateSecurityKey(); showKeyCheck.Checked = true; };
            showKeyCheck = new CheckBox { Text = "Show", AutoSize = true };
            showKeyCheck.CheckedChanged += (s, e) => keyBox.UseSystemPasswordChar = !showKeyCheck.Checked;
            connBody.Controls.Add(Row(Label("Security key:"), keyBox, generate, showKeyCheck));
            connBody.Controls.Add(Hint("Type exactly the same key on both PCs (at least 6 characters). It encrypts everything sent between them."));
            left.Controls.Add(conn);

            // --- screens
            var screens = Group("Where is the other PC?", out var screensBody);
            screensBody.Controls.Add(Hint("Click the spot where the other PC's screen sits on your desk, seen from this PC. " +
                                          "The other PC updates itself to match automatically."));
            layoutPicker = new LayoutPicker
            {
                Side = current.PeerSide,
                ThisName = Environment.MachineName,
                OtherName = string.IsNullOrEmpty(otherName) ? "Other PC" : otherName,
                Size = new Size(S(470), S(215)),
                BackColor = SystemColors.Window,
                Margin = new Padding(S(3), S(4), S(3), S(4)),
            };
            sideLabel = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(S(3), 0, S(3), S(4)) };
            layoutPicker.SideChanged += (s, e) => UpdateSideLabel();
            screensBody.Controls.Add(layoutPicker);
            screensBody.Controls.Add(sideLabel);
            UpdateSideLabel();
            edgeCheck = new CheckBox { Text = "Switch PCs by moving the mouse past that edge", AutoSize = true, Checked = current.EdgeSwitching };
            screensBody.Controls.Add(edgeCheck);
            screensBody.Controls.Add(Hint("Hotkeys (always on): Ctrl+Alt+F1 = this PC, Ctrl+Alt+F2 = other PC."));
            right.Controls.Add(screens);

            // --- clipboard
            var clip = Group("Clipboard", out var clipBody);
            clipCheck = new CheckBox { Text = "Share the clipboard (text and images)", AutoSize = true, Checked = current.ShareClipboard };
            clipBody.Controls.Add(clipCheck);
            filesCheck = new CheckBox { Text = "Also share copied files, up to", AutoSize = true, Checked = current.ShareFiles };
            maxMbBox = new NumericUpDown { Minimum = 1, Maximum = 4096, Value = current.MaxClipboardMB, Width = S(70) };
            clipBody.Controls.Add(Row(filesCheck, maxMbBox, Label("MB per copy")));
            clipCheck.CheckedChanged += (s, e) => UpdateEnabled();
            left.Controls.Add(clip);

            // --- windows
            var win = Group("Windows", out var winBody);
            autostartCheck = new CheckBox { Text = "Start BetterMouse when I sign in to Windows", AutoSize = true, Checked = Autostart.IsEnabled || firstRun };
            winBody.Controls.Add(autostartCheck);
            activityCheck = new CheckBox
            {
                Text = "Keep this PC active while I'm working on the other PC",
                AutoSize = true,
                Checked = current.MirrorActivity,
            };
            winBody.Controls.Add(activityCheck);
            winBody.Controls.Add(Hint("Teams stays \"Available\" and the screen doesn't lock or sleep while you use the other PC. " +
                                      "Stops when you stop: step away and both PCs go idle normally."));
            firewallButton = new Button { Text = "Allow through Windows Firewall…", AutoSize = true };
            firewallButton.Click += (s, e) => ConfigureFirewall();
            firewallStatus = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
            winBody.Controls.Add(Row(firewallButton, firewallStatus));
            winBody.Controls.Add(Hint("Host only, asks for admin once. Lets the client in on every network type, " +
                                      "including when the internet is down or Windows calls the network \"Public\"."));
            right.Controls.Add(win);

            // --- buttons
            var ok = new Button { Text = "OK", DialogResult = DialogResult.None, Width = S(90), Height = S(28) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = S(90), Height = S(28) };
            ok.Click += (s, e) => Accept();
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, S(10), 0, 0),
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            root.Controls.Add(buttons);
            root.SetColumnSpan(buttons, 2);

            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(root);

            hostRadio.Checked = current.Role == Role.Host;
            clientRadio.Checked = current.Role == Role.Client;
            hostRadio.CheckedChanged += (s, e) => UpdateEnabled();
            UpdateEnabled();
            RefreshFirewallStatus();
        }

        int S(int px) => (int)Math.Round(px * scale);

        void UpdateSideLabel()
        {
            var side = layoutPicker.Side;
            sideLabel.Text = side == Edge.Top || side == Edge.Bottom
                ? $"Move the mouse off the {(side == Edge.Top ? "top" : "bottom")} of this screen to reach {layoutPicker.OtherName}."
                : $"Move the mouse off the {side.Describe()} edge of this screen to reach {layoutPicker.OtherName}.";
        }

        void UpdateEnabled()
        {
            bool client = clientRadio.Checked;
            addressBox.Enabled = client;
            discoveryCheck.Enabled = client;
            firewallButton.Enabled = !client;
            filesCheck.Enabled = clipCheck.Checked;
            maxMbBox.Enabled = clipCheck.Checked;
        }

        void RefreshFirewallStatus()
        {
            firewallStatus.Text = "checking…";
            Task.Run(() => Firewall.RuleLooksPresent()).ContinueWith(t =>
            {
                if (IsDisposed) return;
                firewallStatus.Text = t.Status == TaskStatus.RanToCompletion && t.Result ? "✔ rule is in place" : "not set up yet";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ConfigureFirewall()
        {
            Cursor = Cursors.WaitCursor;
            bool ok = Firewall.ConfigureWithElevation(out var message);
            Cursor = Cursors.Default;
            MessageBox.Show(this, message, "BetterMouse", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshFirewallStatus();
        }

        void Accept()
        {
            var key = keyBox.Text.Trim();
            if (key.Length < 6)
            {
                MessageBox.Show(this, "The security key must have at least 6 characters. Click Generate for a strong one, then type the same key on the other PC.",
                    "BetterMouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                keyBox.Focus();
                return;
            }
            var address = addressBox.Text.Trim();
            if (clientRadio.Checked && address.Length == 0 && !discoveryCheck.Checked)
            {
                MessageBox.Show(this, "Enter the host PC's IP address (shown at the top of Settings on the host), or enable the local network search.",
                    "BetterMouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                addressBox.Focus();
                return;
            }
            var names = NetworkManager.SplitAddresses(address).Where(a => !NetworkManager.TryParseEndPoint(a, 1, out _)).ToList();
            if (clientRadio.Checked && names.Count > 0)
            {
                var answer = MessageBox.Show(this,
                    $"\"{string.Join(", ", names)}\" is a name, not an IP address. Names need DNS, which may stop working when the internet is down.\n\n" +
                    "An IP address is more reliable. Keep the name anyway?",
                    "BetterMouse", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) { addressBox.Focus(); return; }
            }

            var s = new Settings
            {
                Role = clientRadio.Checked ? Role.Client : Role.Host,
                PeerAddress = address,
                Discovery = discoveryCheck.Checked,
                Port = (int)portBox.Value,
                SecurityKey = key,
                PeerSide = layoutPicker.Side,
                EdgeSwitching = edgeCheck.Checked,
                MirrorActivity = activityCheck.Checked,
                ShareClipboard = clipCheck.Checked,
                ShareFiles = filesCheck.Checked,
                MaxClipboardMB = (int)maxMbBox.Value,
            };
            Result = s;
            StartWithWindows = autostartCheck.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        // ------------------------------------------------------------ layout helpers

        FlowLayoutPanel Column() => new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, S(8), 0),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };

        GroupBox Group(string title, out FlowLayoutPanel body)
        {
            var box = new GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(S(505), 0), // same width for every box in a column
                Padding = new Padding(S(8), S(4), S(8), S(6)),
                Margin = new Padding(0, S(6), 0, 0),
            };
            body = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
            };
            box.Controls.Add(body);
            return box;
        }

        FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
            };
            foreach (var c in controls)
            {
                if (c is Label) c.Margin = new Padding(S(3), S(6), S(3), 0);
                row.Controls.Add(c);
            }
            return row;
        }

        static Label Label(string text) => new Label { Text = text, AutoSize = true };

        Label Hint(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(S(470), 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(S(3), S(2), S(3), S(4)),
        };

        Label Note(string text, int width, bool bold) => new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Font = bold ? new Font(Font, FontStyle.Bold) : Font,
            Margin = new Padding(S(3), 0, S(3), S(6)),
        };

        /// <summary>"192.168.1.20  (Ethernet)" lines, real LAN adapters first, VPN/virtual ones last.</summary>
        static string[] LocalAddresses()
        {
            string[] virtualHints = { "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "wsl", "tap", "tun", "vpn", "tailscale", "zerotier", "wireguard", "loopback", "bluetooth" };
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n =>
                    {
                        var props = n.GetIPProperties();
                        var text = (n.Name + " " + n.Description).ToLowerInvariant();
                        bool isVirtual = virtualHints.Any(text.Contains);
                        bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                        int rank = isVirtual ? 2 : hasGateway ? 0 : 1;
                        return props.UnicastAddresses
                            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                            .Select(a => (rank, line: $"{a.Address}   ({n.Name}{(isVirtual ? ", virtual/VPN" : "")})"));
                    })
                    .OrderBy(x => x.rank)
                    .Select(x => x.line)
                    .Distinct()
                    .ToArray();
            }
            catch
            {
                return new string[0];
            }
        }
    }
}
