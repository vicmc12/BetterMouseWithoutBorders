using System;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Windows.Forms;

namespace BetterMouse
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool Has(string a) => args.Contains(a, StringComparer.OrdinalIgnoreCase);

            // Login-screen service and its SYSTEM agent run as LocalSystem, so they log to the
            // machine-wide folder and never init WinForms or the per-user mutex.
            if (Has(ServiceControl.ServiceArg))
            {
                Log.Init(MachineSettings.DataDirectory);
                try { ServiceHost.Run(); } catch (Exception ex) { Log.Error("Service host", ex); Log.Flush(); return 1; }
                return 0;
            }
            if (Has(ServiceControl.AgentArg))
            {
                Log.Init(MachineSettings.DataDirectory);
                try { return AgentHost.Run(); } catch (Exception ex) { Log.Error("Agent host", ex); Log.Flush(); return 1; }
            }
            if (Has(ServiceControl.InstallArg)) { Log.Init(MachineSettings.DataDirectory); return ServiceControl.InstallElevated(args); }
            if (Has(ServiceControl.UninstallArg)) { Log.Init(MachineSettings.DataDirectory); return ServiceControl.UninstallElevated(); }

            var migrated = Settings.MigrateFromLegacyName(); // before anything reads settings or opens the log
            Log.Init(Settings.DataDirectory);
            if (migrated != null) Log.Info("Renamed to " + AppInfo.Name + ": copied " + migrated);

            // Elevated helper started by the "Allow through Windows Firewall" button (host PC only).
            if (Has(Firewall.ElevatedArgument))
            {
                int code = Firewall.ApplyRules();
                Log.Flush();
                return code;
            }

            using (var mutex = new Mutex(true, @"Local\BetterMouseWithoutBorders", out bool first))
            {
                if (!first)
                {
                    MessageBox.Show(AppInfo.Name + " is already running – look for the mouse icon in the system tray.",
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                if (Autostart.MigrateFromLegacyName()) Log.Info("Start-with-Windows entry renamed to " + AppInfo.Name);
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Error("UI thread exception", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

                try
                {
                    Application.Run(new TrayApp());
                }
                catch (Exception ex)
                {
                    Log.Error("Fatal", ex);
                    Log.Flush();
                    MessageBox.Show(AppInfo.Name + " stopped because of an error:\n\n" + ex.Message + "\n\nLog: " + Log.FilePath,
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }
}
