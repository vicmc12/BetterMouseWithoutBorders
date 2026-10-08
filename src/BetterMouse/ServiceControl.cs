using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Windows.Forms;

namespace BetterMouse
{
    /// <summary>
    /// Installs, removes and queries the optional login-screen service. The service runs as
    /// LocalSystem (that is the whole point: only SYSTEM may reach the login/lock/UAC desktop),
    /// so install and remove need administrator rights and go through an elevated copy of the exe.
    /// Normal mouse sharing never touches any of this.
    /// </summary>
    internal static class ServiceControl
    {
        public const string ServiceName = "BetterMouseWithoutBorders";
        public const string DisplayName = "Better Mouse Without Borders (login screen)";
        const string Description = "Lets Better Mouse Without Borders relay your keyboard and mouse to this PC's " +
                                   "login, lock and UAC screens. You still type your own password; it is not bypassed.";

        public const string ServiceArg = "--service";
        public const string AgentArg = "--agent";
        public const string InstallArg = "--install-service";
        public const string UninstallArg = "--uninstall-service";
        const string FromArg = "--from=";

        static string ExePath => Application.ExecutablePath;

        public static bool IsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static bool IsInstalled()
        {
            try { return ServiceController.GetServices().Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase)); }
            catch { return false; }
        }

        public static bool IsRunning()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                    return sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending;
            }
            catch { return false; }
        }

        public static string StatusText()
        {
            if (!IsInstalled()) return "off";
            return IsRunning() ? "on" : "installed but not running";
        }

        // ---------------------------------------------------------------- UI side (not elevated)

        /// <summary>Elevates and installs. The current settings are copied to the machine store first.</summary>
        public static bool Enable(out string message)
        {
            // Pass the user's settings file; the elevated copy re-reads and re-encrypts it machine-wide.
            int code = RunElevated(InstallArg + " \"" + FromArg + Settings.FilePath + "\"", out message);
            switch (code)
            {
                case 0: message = "Login-screen control is on. You can now type your password on this PC from the other one."; return true;
                case 2: message = "Needs administrator rights (the prompt was cancelled)."; return false;
                case 3: message = "Could not read this PC's settings. Open Settings, make sure the security key is set, click OK, then try again.\n" +
                                  "(If you approved the prompt with a different admin account, approve it with your own account instead.)"; return false;
                default: message = message ?? "Could not install the service. See the log (tray > Open log)."; return false;
            }
        }

        public static bool Disable(out string message)
        {
            int code = RunElevated(UninstallArg, out message);
            if (code == 0) { message = "Login-screen control is off."; return true; }
            if (code == 2) { message = "Needs administrator rights (the prompt was cancelled)."; return false; }
            message = message ?? "Could not remove the service. See the log.";
            return false;
        }

        static int RunElevated(string arguments, out string message)
        {
            message = null;
            try
            {
                var psi = new ProcessStartInfo(ExePath, arguments) { UseShellExecute = true, Verb = "runas" };
                using (var p = Process.Start(psi))
                {
                    if (p == null) { message = "Could not start the elevated helper."; return 1; }
                    p.WaitForExit(60000);
                    return p.HasExited ? p.ExitCode : 1;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return 2; // user cancelled the UAC prompt
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return 1;
            }
        }

        // ---------------------------------------------------------------- elevated worker

        public static int InstallElevated(string[] args)
        {
            if (!IsAdmin()) return 2;
            try
            {
                var from = args.FirstOrDefault(a => a.StartsWith(FromArg, StringComparison.OrdinalIgnoreCase))?.Substring(FromArg.Length);
                var settings = from != null && System.IO.File.Exists(from)
                    ? Settings.LoadFrom(from, System.Security.Cryptography.DataProtectionScope.CurrentUser)
                    : Settings.Load();
                if (settings.SecurityKey.Trim().Length < 6)
                {
                    Log.Warn("Service install: settings incomplete or key not readable from " + from);
                    return 3;
                }
                MachineSettings.Save(settings);
                Log.Info("Service install: wrote machine settings to " + MachineSettings.FilePath);
                if (!Install(out var err)) { Log.Error("Service install failed: " + err); return 4; }
                SetFailureActions();
                StartService();
                Log.Info("Login-screen service installed and started");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Service install", ex);
                return 4;
            }
            finally { Log.Flush(); }
        }

        public static int UninstallElevated()
        {
            if (!IsAdmin()) return 2;
            try
            {
                StopService();
                if (!Delete(out var err)) Log.Warn("Service delete: " + err);
                MachineSettings.Delete();
                Log.Info("Login-screen service removed");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Service uninstall", ex);
                return 4;
            }
            finally { Log.Flush(); }
        }

        // ---------------------------------------------------------------- SCM

        static bool Install(out string error)
        {
            error = null;
            var scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) { error = "OpenSCManager " + Marshal.GetLastWin32Error(); return false; }
            try
            {
                var binPath = "\"" + ExePath + "\" " + ServiceArg;
                var svc = CreateService(scm, ServiceName, DisplayName, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS,
                    SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, binPath, null, IntPtr.Zero, null, null, null);
                if (svc == IntPtr.Zero)
                {
                    int e = Marshal.GetLastWin32Error();
                    if (e == ERROR_SERVICE_EXISTS) return true;
                    error = "CreateService " + e;
                    return false;
                }
                try { SetDescription(svc); } catch { }
                CloseServiceHandle(svc);
                return true;
            }
            finally { CloseServiceHandle(scm); }
        }

        static bool Delete(out string error)
        {
            error = null;
            var scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) { error = "OpenSCManager " + Marshal.GetLastWin32Error(); return false; }
            try
            {
                var svc = OpenService(scm, ServiceName, SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero)
                {
                    int e = Marshal.GetLastWin32Error();
                    if (e == ERROR_SERVICE_DOES_NOT_EXIST) return true;
                    error = "OpenService " + e;
                    return false;
                }
                try
                {
                    var status = new SERVICE_STATUS();
                    ControlService(svc, SERVICE_CONTROL_STOP, ref status);
                    if (!DeleteService(svc)) { error = "DeleteService " + Marshal.GetLastWin32Error(); return false; }
                    return true;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        static void SetDescription(IntPtr svc)
        {
            var desc = new SERVICE_DESCRIPTION { lpDescription = Description };
            var p = Marshal.AllocHGlobal(Marshal.SizeOf(desc));
            try { Marshal.StructureToPtr(desc, p, false); ChangeServiceConfig2(svc, SERVICE_CONFIG_DESCRIPTION, p); }
            finally { Marshal.FreeHGlobal(p); }
        }

        static void StartService()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Service start: " + ex.Message); }
        }

        static void StopService()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped && sc.CanStop)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                }
            }
            catch (Exception ex) { Log.Info("Service stop: " + ex.Message); }
        }

        /// <summary>Restart the service if it ever crashes.</summary>
        static void SetFailureActions()
        {
            try
            {
                var psi = new ProcessStartInfo("sc.exe",
                    $"failure \"{ServiceName}\" reset= 86400 actions= restart/2000/restart/5000/restart/5000")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); }
            }
            catch { }
        }

        const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        const uint SERVICE_ALL_ACCESS = 0xF01FF;
        const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
        const uint SERVICE_AUTO_START = 2;
        const uint SERVICE_ERROR_NORMAL = 1;
        const uint SERVICE_CONTROL_STOP = 1;
        const uint SERVICE_CONFIG_DESCRIPTION = 1;
        const int ERROR_SERVICE_EXISTS = 1073;
        const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;

        [StructLayout(LayoutKind.Sequential)]
        struct SERVICE_STATUS { public uint t, cs, ca, we, sse, cp, wh; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SERVICE_DESCRIPTION { [MarshalAs(UnmanagedType.LPWStr)] public string lpDescription; }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenSCManager(string machine, string database, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateService(IntPtr scm, string name, string display, uint access, uint type,
            uint startType, uint errorControl, string binaryPath, string loadOrderGroup, IntPtr tagId,
            string dependencies, string serviceStartName, string password);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool ControlService(IntPtr service, uint control, ref SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, IntPtr info);
    }
}
