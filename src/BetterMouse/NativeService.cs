using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterMouse
{
    /// <summary>
    /// Win32 interop used only by the login-screen service and its agent: tokens, sessions,
    /// desktops and launching a process into another session. These need the service to run as
    /// LocalSystem (which has the required privileges); nothing here is reachable in normal mode.
    /// </summary>
    internal static class NativeService
    {
        // --- tokens ---------------------------------------------------------
        public const uint TOKEN_DUPLICATE = 0x0002;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        public const uint TOKEN_ADJUST_DEFAULT = 0x0080;
        public const uint TOKEN_ADJUST_SESSIONID = 0x0100;
        public const uint MAXIMUM_ALLOWED = 0x02000000;

        public enum SECURITY_IMPERSONATION_LEVEL { Anonymous, Identification, Impersonation, Delegation }
        public enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }
        public const int TokenSessionId = 12;

        // --- process creation ----------------------------------------------
        public const uint CREATE_NO_WINDOW = 0x08000000;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint CREATE_NEW_CONSOLE = 0x00000010;
        public const uint STARTF_USESHOWWINDOW = 0x00000001;
        public const short SW_HIDE = 0;

        // --- desktops -------------------------------------------------------
        public const uint GENERIC_ALL = 0x10000000;
        public const int UOI_NAME = 2;
        public const uint DESKTOP_RIGHTS =
            0x0001 /*READOBJECTS*/ | 0x0002 /*CREATEWINDOW*/ | 0x0004 /*CREATEMENU*/ |
            0x0008 /*HOOKCONTROL*/ | 0x0010 /*JOURNALRECORD*/ | 0x0020 /*JOURNALPLAYBACK*/ |
            0x0040 /*ENUMERATE*/ | 0x0080 /*WRITEOBJECTS*/ | 0x0100 /*SWITCHDESKTOP*/ |
            0x00020000 /*READ_CONTROL*/;

        public const uint INVALID_SESSION = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll")]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(IntPtr existing, uint desiredAccess, ref SECURITY_ATTRIBUTES attrs,
            SECURITY_IMPERSONATION_LEVEL level, TOKEN_TYPE type, out IntPtr newToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint value, int length);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUser(IntPtr token, string applicationName, string commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
            IntPtr environment, string currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool DestroyEnvironmentBlock(IntPtr env);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        public const uint STILL_ACTIVE = 259;
        public const uint WAIT_OBJECT_0 = 0;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetThreadDesktop(IntPtr desktop);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetUserObjectInformation(IntPtr obj, int index, byte[] info, int length, out int needed);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        /// <summary>Name of the desktop currently receiving input ("Default", "Winlogon", "Screen-saver"…), or null.</summary>
        public static string InputDesktopName()
        {
            var desk = OpenInputDesktop(0, false, DESKTOP_RIGHTS);
            if (desk == IntPtr.Zero) return null;
            try { return DesktopName(desk); }
            finally { CloseDesktop(desk); }
        }

        public static string DesktopName(IntPtr desk)
        {
            var buf = new byte[256];
            if (!GetUserObjectInformation(desk, UOI_NAME, buf, buf.Length, out int needed)) return null;
            var s = Encoding.Unicode.GetString(buf, 0, Math.Max(0, Math.Min(buf.Length, needed)));
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s.Substring(0, nul) : s;
        }
    }
}
