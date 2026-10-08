using System;
using System.ServiceProcess;
using System.Threading;
using static BetterMouse.NativeService;

namespace BetterMouse
{
    /// <summary>
    /// The login-screen service (LocalSystem). It never touches the desktop itself — a Session-0
    /// service can't. Instead it keeps one SYSTEM <c>--agent</c> process running in the active
    /// console session on the secure desktop, but only while that session is at the login screen
    /// or locked. When a user is logged in and unlocked, it stops the agent and the ordinary
    /// no-admin tray app takes over. Lock ↔ unlock hands control between the two.
    /// </summary>
    internal sealed class ServiceHost : ServiceBase
    {
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool running;
        volatile bool secure = true; // at boot the console is at the login screen
        Thread worker;

        IntPtr agentProcess = IntPtr.Zero;
        uint agentSession = INVALID_SESSION;

        public ServiceHost()
        {
            ServiceName = ServiceControl.ServiceName;
            CanHandleSessionChangeEvent = true;
            CanShutdown = true;
            CanStop = true;
            AutoLog = false;
        }

        public static void Run() => ServiceBase.Run(new ServiceHost());

        protected override void OnStart(string[] args)
        {
            running = true;
            Log.Info("Login-screen service starting (" + AppInfo.Name + " " + AppInfo.Version + ")");
            worker = new Thread(Loop) { IsBackground = true, Name = "BMWB service" };
            worker.Start();
        }

        protected override void OnStop() => Shutdown("stop");
        protected override void OnShutdown() => Shutdown("shutdown");

        void Shutdown(string why)
        {
            if (!running) return;
            running = false;
            Log.Info("Login-screen service stopping (" + why + ")");
            wake.Set();
            worker?.Join(4000);
            lock (gate) KillAgent();
            Log.Flush();
        }

        protected override void OnSessionChange(SessionChangeDescription change)
        {
            switch (change.Reason)
            {
                case SessionChangeReason.SessionLock:
                case SessionChangeReason.SessionLogoff:
                case SessionChangeReason.ConsoleDisconnect:
                    secure = true;
                    break;
                case SessionChangeReason.SessionUnlock:
                case SessionChangeReason.SessionLogon:
                    secure = false;
                    break;
            }
            Log.Info($"Session change {change.Reason} (session {change.SessionId}); secure={secure}");
            wake.Set();
        }

        void Loop()
        {
            while (running)
            {
                try { Reconcile(); }
                catch (Exception ex) { Log.Error("Service loop", ex); }
                wake.WaitOne(1000); // event-driven, with a 1 s safety poll (agent crash, session switch)
            }
        }

        void Reconcile()
        {
            lock (gate)
            {
                uint sid = WTSGetActiveConsoleSessionId();
                bool wantAgent = running && secure && sid != INVALID_SESSION && sid != 0;

                if (!AgentAlive())
                {
                    if (agentProcess != IntPtr.Zero) { CloseHandle(agentProcess); agentProcess = IntPtr.Zero; agentSession = INVALID_SESSION; }
                }

                if (wantAgent)
                {
                    if (!AgentAlive() || agentSession != sid)
                    {
                        if (AgentAlive()) KillAgent();
                        LaunchAgent(sid);
                    }
                }
                else if (AgentAlive())
                {
                    KillAgent();
                }
            }
        }

        bool AgentAlive()
        {
            if (agentProcess == IntPtr.Zero) return false;
            return GetExitCodeProcess(agentProcess, out uint code) && code == STILL_ACTIVE;
        }

        void KillAgent()
        {
            if (agentProcess == IntPtr.Zero) return;
            Log.Info("Stopping agent in session " + agentSession);
            try { TerminateProcess(agentProcess, 0); } catch { }
            CloseHandle(agentProcess);
            agentProcess = IntPtr.Zero;
            agentSession = INVALID_SESSION;
        }

        /// <summary>
        /// Launch the agent as SYSTEM into <paramref name="sid"/> on the secure desktop. Uses a copy
        /// of the service's own token with the session id changed (LocalSystem has the privileges
        /// for this); the agent re-attaches to whatever desktop is current, so "Winlogon" here is
        /// just a safe starting point.
        /// </summary>
        void LaunchAgent(uint sid)
        {
            IntPtr token = IntPtr.Zero, dup = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(),
                        TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID,
                        out token))
                {
                    Log.Error("Agent launch: OpenProcessToken " + Marshal32());
                    return;
                }
                var sa = new SECURITY_ATTRIBUTES { nLength = System.Runtime.InteropServices.Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)) };
                if (!DuplicateTokenEx(token, MAXIMUM_ALLOWED, ref sa, SECURITY_IMPERSONATION_LEVEL.Impersonation, TOKEN_TYPE.TokenPrimary, out dup))
                {
                    Log.Error("Agent launch: DuplicateTokenEx " + Marshal32());
                    return;
                }
                uint session = sid;
                if (!SetTokenInformation(dup, TokenSessionId, ref session, sizeof(uint)))
                {
                    Log.Error("Agent launch: SetTokenInformation " + Marshal32());
                    return;
                }

                var si = new STARTUPINFO();
                si.cb = System.Runtime.InteropServices.Marshal.SizeOf(typeof(STARTUPINFO));
                si.lpDesktop = "WinSta0\\Winlogon";
                si.dwFlags = (int)STARTF_USESHOWWINDOW;
                si.wShowWindow = SW_HIDE;

                IntPtr env = IntPtr.Zero;
                bool haveEnv = CreateEnvironmentBlock(out env, dup, false);
                uint flags = CREATE_NO_WINDOW | (haveEnv ? CREATE_UNICODE_ENVIRONMENT : 0);
                var cmd = "\"" + System.Windows.Forms.Application.ExecutablePath + "\" " + ServiceControl.AgentArg;

                bool ok = CreateProcessAsUser(dup, System.Windows.Forms.Application.ExecutablePath, cmd,
                    IntPtr.Zero, IntPtr.Zero, false, flags, haveEnv ? env : IntPtr.Zero, null, ref si, out var pi);
                if (haveEnv) DestroyEnvironmentBlock(env);

                if (!ok)
                {
                    Log.Error("Agent launch: CreateProcessAsUser " + Marshal32());
                    return;
                }
                CloseHandle(pi.hThread);
                agentProcess = pi.hProcess;
                agentSession = sid;
                Log.Info("Agent launched in session " + sid + " (pid " + pi.dwProcessId + ")");
            }
            catch (Exception ex)
            {
                Log.Error("Agent launch", ex);
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                if (dup != IntPtr.Zero) CloseHandle(dup);
            }
        }

        static string Marshal32() => "error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error();
    }
}
