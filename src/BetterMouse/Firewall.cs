using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;

namespace BetterMouse
{
    /// <summary>
    /// Host PC only (the one where you are admin): one inbound "allow" rule for BetterMouse.exe
    /// on ALL network profiles. Windows' own pop-up only allows "Private" networks, and a network
    /// that loses internet or is a direct cable can turn into "Public"/"Unidentified" - which is
    /// a classic reason a KVM tool stops working when the internet drops.
    /// The client PC never needs a rule: it only makes outgoing connections.
    /// </summary>
    internal static class Firewall
    {
        public const string RuleName = AppInfo.Name;
        const string LegacyRuleName = AppInfo.LegacyFileName;
        public const string ElevatedArgument = "--configure-firewall";

        static string ExePath => Application.ExecutablePath;

        /// <summary>Starts an elevated copy of this exe (one UAC prompt) that writes the rule.</summary>
        public static bool ConfigureWithElevation(out string message)
        {
            try
            {
                var psi = new ProcessStartInfo(ExePath, ElevatedArgument) { UseShellExecute = true, Verb = "runas" };
                using (var p = Process.Start(psi))
                {
                    if (p == null) { message = "Could not start the elevated helper."; return false; }
                    p.WaitForExit(30000);
                    if (p.ExitCode == 0)
                    {
                        message = "Firewall rule added. The other PC can now connect on any network type (Private, Public, cable, Wi-Fi).";
                        return true;
                    }
                    message = "netsh reported an error (exit code " + p.ExitCode + "). See the log for details.";
                    return false;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                message = "Cancelled. Without the rule, Windows Firewall may block the other PC.";
                return false;
            }
            catch (Exception ex)
            {
                message = "Could not configure the firewall: " + ex.Message;
                return false;
            }
        }

        /// <summary>Runs inside the elevated helper process.</summary>
        public static int ApplyRules()
        {
            var exe = ExePath;
            // Remove our old rules (also the pre-rename "BetterMouse" one, which points at the old
            // exe) and any "block" rules Windows created when its pop-up was cancelled.
            Netsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
            Netsh($"advfirewall firewall delete rule name=\"{LegacyRuleName}\"");
            Netsh($"advfirewall firewall delete rule name=all program=\"{exe}\"");
            return Netsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any description=\"{AppInfo.Name}: mouse/keyboard sharing on the local network\"");
        }

        /// <summary>Best-effort check, works without admin.</summary>
        public static bool RuleLooksPresent()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\" verbose")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return p.ExitCode == 0 && output.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        static int Netsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    Log.Info($"netsh {args} -> {p.ExitCode}: {output.Trim()}");
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Log.Error("netsh failed", ex);
                return -1;
            }
        }
    }
}
