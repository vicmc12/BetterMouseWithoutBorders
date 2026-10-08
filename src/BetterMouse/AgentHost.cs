using System;
using System.Collections.Concurrent;
using System.Threading;
using static BetterMouse.NativeService;

namespace BetterMouse
{
    /// <summary>
    /// The SYSTEM agent launched by the service into the active session. It is injection-only: it
    /// connects to the peer with the machine-wide settings and replays the received mouse and
    /// keyboard onto whatever desktop is currently receiving input — including the secure
    /// login/lock/UAC desktop, which only a SYSTEM process may reach. It never captures local
    /// input, shows UI or touches the clipboard. The service starts it only while the session is
    /// locked or at the login screen, and kills it on unlock. It relays the user's own keystrokes;
    /// it does not bypass the password.
    /// </summary>
    internal static class AgentHost
    {
        static readonly BlockingCollection<byte[]> inbound = new BlockingCollection<byte[]>(4096);
        static Injector injector;
        static NetworkManager net;

        public static int Run()
        {
            using (var mutex = new Mutex(true, @"Global\BetterMouseWithoutBorders-Agent", out bool first))
            {
                if (!first) { Log.Info("Agent already running; exiting"); return 0; }

                var settings = MachineSettings.Load();
                if (settings.SecurityKey.Trim().Length < 6)
                {
                    Log.Warn("Agent: machine settings missing or incomplete; exiting");
                    return 1;
                }

                Log.Info($"Agent starting as {Identity()} on desktop {InputDesktopName() ?? "?"}, role {settings.Role}");
                var masterKey = Crypto.DeriveMasterKey(settings.SecurityKey);
                injector = new Injector(SendToPeer, verifyEntry: false);
                var inject = new Thread(InjectLoop) { IsBackground = true, Name = "BMWB agent inject", Priority = ThreadPriority.Highest };
                inject.Start();

                net = new NetworkManager(settings, masterKey, Environment.MachineName, OnMessage);
                net.Connected += c => { injector.EndControl(); Log.Info("Agent connected to " + c.PeerName); };
                net.Disconnected += c => injector.EndControl();
                net.Start();

                // Run until the service terminates us (on unlock) or the machine shuts down.
                var forever = new ManualResetEvent(false);
                AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { net.Stop(); } catch { } Log.Flush(); };
                forever.WaitOne();
                return 0;
            }
        }

        static void OnMessage(Connection conn, byte[] m)
        {
            var type = (MsgType)m[0];
            if (type == MsgType.Enter || (type >= MsgType.MouseMove && type <= MsgType.Key))
                conn.KeepWarm();
            if (!inbound.IsAddingCompleted) inbound.TryAdd(m);
        }

        static void SendToPeer(byte[] message) => net?.Current?.Send(message);

        /// <summary>Owns the desktop attachment; everything that injects runs here.</summary>
        static void InjectLoop()
        {
            string attached = null;
            IntPtr deskHandle = IntPtr.Zero;
            while (true)
            {
                try
                {
                    // Follow the input desktop (Default ↔ Winlogon ↔ Screen-saver) without
                    // respawning: this thread has no windows or hooks, so it may re-attach freely.
                    var name = InputDesktopName();
                    if (name != null && name != attached)
                    {
                        var desk = OpenInputDesktop(0, false, DESKTOP_RIGHTS);
                        if (desk != IntPtr.Zero && SetThreadDesktop(desk))
                        {
                            if (deskHandle != IntPtr.Zero) CloseDesktop(deskHandle);
                            deskHandle = desk;
                            attached = name;
                            Log.Info("Agent now injecting on desktop " + name);
                        }
                        else if (desk != IntPtr.Zero)
                        {
                            CloseDesktop(desk);
                        }
                    }

                    if (inbound.TryTake(out var m, 150)) Dispatch(m);
                }
                catch (Exception ex)
                {
                    Log.Error("Agent inject", ex);
                    Thread.Sleep(50);
                }
            }
        }

        static void Dispatch(byte[] m)
        {
            var r = new PacketReader(m);
            switch ((MsgType)m[0])
            {
                case MsgType.Enter:
                {
                    var edge = (Edge)r.Byte();
                    injector.Enter(edge, r.Double());
                    break;
                }
                case MsgType.MouseMove:
                    injector.Move(r.Int32(), r.Int32());
                    break;
                case MsgType.MouseButton:
                    injector.Button((MouseButton)r.Byte(), r.Bool());
                    break;
                case MsgType.Wheel:
                    injector.Wheel(r.Int16(), r.Bool());
                    break;
                case MsgType.Key:
                {
                    var vk = r.UInt16();
                    var scan = r.UInt16();
                    var flags = r.Byte();
                    injector.Key(vk, scan, (flags & 1) != 0, (flags & 2) != 0);
                    break;
                }
                case MsgType.EndControl:
                    injector.EndControl();
                    break;
                // Leave/Layout/Activity/clipboard are not relevant to an injection-only agent.
            }
        }

        static string Identity()
        {
            try { return System.Security.Principal.WindowsIdentity.GetCurrent().Name; }
            catch { return "?"; }
        }
    }
}
