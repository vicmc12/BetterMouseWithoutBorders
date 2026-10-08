using System;
using System.IO;
using System.Security.Cryptography;

namespace BetterMouse
{
    /// <summary>
    /// Machine-wide copy of the settings for the login-screen service and its agent, which run as
    /// SYSTEM and cannot read a user's %APPDATA%. Lives in %ProgramData% and encrypts the key with
    /// the LocalMachine DPAPI scope so SYSTEM can read it. Written by the elevated installer and
    /// kept in sync by the tray app while the service is installed.
    /// </summary>
    internal static class MachineSettings
    {
        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppInfo.FileName);

        public static string FilePath => Path.Combine(DataDirectory, "service.ini");

        public static bool Exists => File.Exists(FilePath);

        public static Settings Load() => Settings.LoadFrom(FilePath, DataProtectionScope.LocalMachine);

        public static void Save(Settings s)
        {
            Directory.CreateDirectory(DataDirectory);
            s.SaveTo(FilePath, DataProtectionScope.LocalMachine);
        }

        public static void Delete()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }
    }
}
