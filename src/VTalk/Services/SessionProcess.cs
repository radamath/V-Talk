using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VTalk.Services;

/// <summary>
/// SYSTEM (session 0) sürecinden aktif konsol oturumunda etkileşimli süreç başlatır.
/// ScheduledTask Interactive / explorer start session-0'da kalır veya sessizce başarısız olur.
/// </summary>
internal static class SessionProcess
{
    public static bool TryStartInActiveSession(string exePath, string arguments, out string detail)
    {
        detail = "";
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            detail = "exe yok";
            return false;
        }

        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFFu)
        {
            detail = "aktif konsol oturumu yok";
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            detail = "WTSQueryUserToken=" + Marshal.GetLastWin32Error();
            return false;
        }

        var env = IntPtr.Zero;
        var primary = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation,
                    TokenPrimary, out primary))
            {
                detail = "DuplicateTokenEx=" + Marshal.GetLastWin32Error();
                return false;
            }

            CreateEnvironmentBlock(out env, primary, false);

            var desktop = "winsta0\\default";
            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = desktop,
            };
            var cmd = new StringBuilder();
            cmd.Append('"').Append(exePath).Append('"');
            if (!string.IsNullOrWhiteSpace(arguments))
                cmd.Append(' ').Append(arguments);

            var workDir = Path.GetDirectoryName(exePath) ?? Environment.SystemDirectory;
            if (!CreateProcessAsUser(primary, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT, env, workDir, ref si, out var pi))
            {
                detail = "CreateProcessAsUser=" + Marshal.GetLastWin32Error();
                return false;
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            detail = $"ok session={sessionId} pid={pi.dwProcessId}";
            return true;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            CloseHandle(userToken);
        }
    }

    private const uint TOKEN_ALL_ACCESS = 0x000F01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
