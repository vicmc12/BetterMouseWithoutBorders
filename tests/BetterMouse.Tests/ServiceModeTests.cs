using System;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace BetterMouse.Tests
{
    public class ServiceModeTests
    {
        [Fact]
        public void MachineScopeSettingsRoundTrip()
        {
            var path = Path.Combine(Path.GetTempPath(), "bmwb-machine-" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                var s = new Settings { Role = Role.Client, Port = 15155, PeerAddress = "10.0.0.2", SecurityKey = "login-screen-key" };
                s.SaveTo(path, DataProtectionScope.LocalMachine);

                var back = Settings.LoadFrom(path, DataProtectionScope.LocalMachine);
                Assert.Equal(Role.Client, back.Role);
                Assert.Equal("10.0.0.2", back.PeerAddress);
                Assert.Equal("login-screen-key", back.SecurityKey);

                // The on-disk key is encrypted, not plain text.
                Assert.DoesNotContain("login-screen-key", File.ReadAllText(path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void UserScopedKeyFromAnotherUserDecryptsToEmptyNotGarbage()
        {
            // A settings file copied from another user/PC (user-scoped blob we can't decrypt) must
            // surface as an empty key, never throw or return garbage.
            var path = Path.Combine(Path.GetTempPath(), "bmwb-foreign-" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                File.WriteAllText(path, "Role=Client\nSecurityKey=dpapi:" + Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 }) + "\n");
                var s = Settings.LoadFrom(path, DataProtectionScope.CurrentUser);
                Assert.Equal("", s.SecurityKey);
                Assert.Equal(Role.Client, s.Role);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ServiceAndAgentUseDistinctArguments()
        {
            Assert.NotEqual(ServiceControl.ServiceArg, ServiceControl.AgentArg);
            Assert.StartsWith("--", ServiceControl.ServiceArg);
            Assert.StartsWith("--", ServiceControl.AgentArg);
            Assert.StartsWith("--", ServiceControl.InstallArg);
            Assert.StartsWith("--", ServiceControl.UninstallArg);
            Assert.Equal("BetterMouseWithoutBorders", ServiceControl.ServiceName);
        }

        [Fact]
        public void MachineSettingsLiveUnderProgramData()
        {
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppInfo.FileName);
            Assert.Equal(expected, MachineSettings.DataDirectory);
            Assert.EndsWith("service.ini", MachineSettings.FilePath);
        }

        [Fact]
        public void StatusTextIsOffWhenNotInstalled()
        {
            // On the dev machine the service isn't installed; this must not throw and reads "off".
            if (!ServiceControl.IsInstalled())
                Assert.Equal("off", ServiceControl.StatusText());
        }
    }
}
