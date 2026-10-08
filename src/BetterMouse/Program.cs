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
            Log.Init(Settings.DataDirectory);

            // Elevated helper started by the "Allow through Windows Firewall" button (host PC only).
            if (args.Contains(Firewall.ElevatedArgument, StringComparer.OrdinalIgnoreCase))
            {
                int code = Firewall.ApplyRules();
                Log.Flush();
                return code;
            }

            using (var mutex = new Mutex(true, @"Local\BetterMouseWithoutBorders", out bool first))
            {
                if (!first)
                {
                    MessageBox.Show("BetterMouse is already running – look for the mouse icon in the system tray.",
                        "BetterMouse", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

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
                    MessageBox.Show("BetterMouse stopped because of an error:\n\n" + ex.Message + "\n\nLog: " + Log.FilePath,
                        "BetterMouse", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }
}
