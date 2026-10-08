using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace BetterMouse
{
    internal enum Role : byte { Host = 1, Client = 2 }

    internal enum Edge : byte { Left = 0, Right = 1, Top = 2, Bottom = 3 }

    internal static class EdgeExtensions
    {
        public static string Describe(this Edge e)
        {
            switch (e)
            {
                case Edge.Left: return "left";
                case Edge.Right: return "right";
                case Edge.Top: return "above";
                default: return "below";
            }
        }

        /// <summary>
        /// Keeps both PCs' "other PC is on my …" settings mirror images of each other.
        /// Returns the side this PC should switch to, or null to keep its own (newer) choice.
        /// On a tie the host's layout wins, so two fresh installs (both "right") agree at once.
        /// </summary>
        public static Edge? SyncLayout(Edge mine, long myStamp, Role myRole, Edge theirs, long theirStamp)
        {
            var mirrored = theirs.Opposite();
            if (mirrored == mine) return null;
            if (theirStamp > myStamp) return mirrored;
            if (theirStamp == myStamp && myRole == Role.Client) return mirrored;
            return null;
        }

        public static Edge Opposite(this Edge e)
        {
            switch (e)
            {
                case Edge.Left: return Edge.Right;
                case Edge.Right: return Edge.Left;
                case Edge.Top: return Edge.Bottom;
                default: return Edge.Top;
            }
        }

        public static bool IsHorizontal(this Edge e) => e == Edge.Left || e == Edge.Right;
    }

    internal sealed class Settings
    {
        public const int DefaultPort = 15155;
        const string FileName = "BetterMouse.ini";
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BetterMouse settings v1");

        public Role Role = Role.Host;
        public int Port = DefaultPort;
        /// <summary>Client only: host IP address(es) or name(s), comma separated.</summary>
        public string PeerAddress = "";
        /// <summary>Client only: find the host with a LAN broadcast when the address does not answer.</summary>
        public bool Discovery = true;
        /// <summary>Which side of this PC's screen leads to the other PC.</summary>
        public Edge PeerSide = Edge.Right;
        /// <summary>When PeerSide was last chosen by the user (UTC ticks); the newer choice wins when the PCs sync.</summary>
        public long LayoutChangedUtc;
        public string SecurityKey = "";
        public bool EdgeSwitching = true;
        /// <summary>
        /// While the user is really active on the other PC, keep this PC from going idle (Teams
        /// "Away", screen saver, auto-lock). Stops as soon as the user stops.
        /// </summary>
        public bool MirrorActivity = true;
        public bool ShareClipboard = true;
        public bool ShareFiles = true;
        public int MaxClipboardMB = 100;
        /// <summary>Client only: the address that worked last time (tried first on reconnect).</summary>
        public string LastGoodAddress = "";

        public bool IsComplete =>
            SecurityKey.Trim().Length >= 6 &&
            (Role == Role.Host || Discovery || PeerAddress.Trim().Length > 0);

        /// <summary>
        /// Portable mode if BetterMouse.ini sits next to the exe; otherwise %APPDATA%\BetterMouse.
        /// Either way, writable without admin rights.
        /// </summary>
        public static string DataDirectory
        {
            get
            {
                var exeDir = AppDomain.CurrentDomain.BaseDirectory;
                if (File.Exists(Path.Combine(exeDir, FileName))) return exeDir;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterMouse");
            }
        }

        public static string FilePath => Path.Combine(DataDirectory, FileName);

        public static bool FileExists => File.Exists(FilePath);

        public Settings Clone() => (Settings)MemberwiseClone();

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                s.Role = Enum<Role>(values, "Role", s.Role);
                s.Port = Int(values, "Port", s.Port, 1, 65535);
                s.PeerAddress = Str(values, "PeerAddress", s.PeerAddress);
                s.Discovery = Bool(values, "Discovery", s.Discovery);
                s.PeerSide = Enum<Edge>(values, "PeerSide", s.PeerSide);
                s.LayoutChangedUtc = values.TryGetValue("LayoutChangedUtc", out var lc) && long.TryParse(lc, out var t) ? t : 0;
                s.EdgeSwitching = Bool(values, "EdgeSwitching", s.EdgeSwitching);
                s.MirrorActivity = Bool(values, "MirrorActivity", s.MirrorActivity);
                s.ShareClipboard = Bool(values, "ShareClipboard", s.ShareClipboard);
                s.ShareFiles = Bool(values, "ShareFiles", s.ShareFiles);
                s.MaxClipboardMB = Int(values, "MaxClipboardMB", s.MaxClipboardMB, 1, 4096);
                s.LastGoodAddress = Str(values, "LastGoodAddress", s.LastGoodAddress);
                s.SecurityKey = UnprotectKey(Str(values, "SecurityKey", ""));
            }
            catch (Exception ex)
            {
                Log.Error("Could not read settings", ex);
            }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(DataDirectory);
            var sb = new StringBuilder();
            sb.AppendLine("# BetterMouse settings. Edit through the tray icon > Settings.");
            sb.AppendLine("Role=" + Role);
            sb.AppendLine("Port=" + Port);
            sb.AppendLine("PeerAddress=" + PeerAddress.Trim());
            sb.AppendLine("Discovery=" + Discovery);
            sb.AppendLine("PeerSide=" + PeerSide);
            sb.AppendLine("LayoutChangedUtc=" + LayoutChangedUtc);
            sb.AppendLine("EdgeSwitching=" + EdgeSwitching);
            sb.AppendLine("MirrorActivity=" + MirrorActivity);
            sb.AppendLine("ShareClipboard=" + ShareClipboard);
            sb.AppendLine("ShareFiles=" + ShareFiles);
            sb.AppendLine("MaxClipboardMB=" + MaxClipboardMB);
            sb.AppendLine("LastGoodAddress=" + LastGoodAddress);
            sb.AppendLine("SecurityKey=" + ProtectKey(SecurityKey));
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }

        /// <summary>The key is encrypted for the current Windows user (DPAPI) before it touches the disk.</summary>
        static string ProtectKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(blob);
        }

        static string UnprotectKey(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored; // hand-written plain key
            try
            {
                var blob = Convert.FromBase64String(stored.Substring(6));
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                Log.Warn("Stored security key could not be decrypted (settings copied from another user/PC?): " + ex.Message);
                return "";
            }
        }

        static string Str(Dictionary<string, string> v, string k, string def) => v.TryGetValue(k, out var s) ? s : def;

        static bool Bool(Dictionary<string, string> v, string k, bool def) =>
            v.TryGetValue(k, out var s) && bool.TryParse(s, out var b) ? b : def;

        static int Int(Dictionary<string, string> v, string k, int def, int min, int max) =>
            v.TryGetValue(k, out var s) && int.TryParse(s, out var i) && i >= min && i <= max ? i : def;

        static T Enum<T>(Dictionary<string, string> v, string k, T def) where T : struct =>
            v.TryGetValue(k, out var s) && System.Enum.TryParse<T>(s, true, out var e) && System.Enum.IsDefined(typeof(T), e) ? e : def;
    }

    /// <summary>"Start with Windows" through the per-user Run key (no admin needed).</summary>
    internal static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "BetterMouse";

        static string Command => "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --autostart";

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        public static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, Command);
                else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
            }
        }
    }
}
