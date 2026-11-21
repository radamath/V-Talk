# SYSTEM oturumundan aktif konsol kullanicisinda V-Talk'i --show ile baslatir.
param(
    [string]$Exe = "$env:ProgramFiles\V-Talk\VTalk.exe",
    [string]$Log = "$env:ProgramData\VTalk\update\pending\apply.log"
)
$ErrorActionPreference = 'Stop'
function W([string]$m) {
    try { Add-Content -LiteralPath $Log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff') + ' ' + $m) } catch {}
}
if (-not (Test-Path -LiteralPath $Exe)) { W 'launch: exe yok'; exit 0 }

$src = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class VTalkLaunch {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  struct STARTUPINFO {
    public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
    public int dwX,dwY,dwXSize,dwYSize,dwXCountChars,dwYCountChars,dwFillAttribute,dwFlags;
    public short wShowWindow,cbReserved2; public IntPtr lpReserved2,hStdInput,hStdOutput,hStdError;
  }
  [StructLayout(LayoutKind.Sequential)]
  struct PROCESS_INFORMATION { public IntPtr hProcess,hThread; public int dwProcessId,dwThreadId; }
  [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
  [DllImport("wtsapi32.dll", SetLastError=true)] static extern bool WTSQueryUserToken(uint id, out IntPtr token);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool DuplicateTokenEx(IntPtr t, uint a, IntPtr attr, int il, int tt, out IntPtr p);
  [DllImport("userenv.dll", SetLastError=true)] static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr t, bool inherit);
  [DllImport("userenv.dll", SetLastError=true)] static extern bool DestroyEnvironmentBlock(IntPtr env);
  [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern bool CreateProcessAsUser(IntPtr t, string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  public static string Start(string exe, string args) {
    uint sid = WTSGetActiveConsoleSessionId();
    if (sid == 0xFFFFFFFF) return "no-session";
    if (!WTSQueryUserToken(sid, out var user)) return "token="+Marshal.GetLastWin32Error();
    IntPtr env = IntPtr.Zero, primary = IntPtr.Zero;
    try {
      if (!DuplicateTokenEx(user, 0x000F01FF, IntPtr.Zero, 2, 1, out primary)) return "dup="+Marshal.GetLastWin32Error();
      CreateEnvironmentBlock(out env, primary, false);
      var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = "winsta0\\default" };
      var cmd = new StringBuilder("\"" + exe + "\" " + args);
      var dir = System.IO.Path.GetDirectoryName(exe);
      if (!CreateProcessAsUser(primary, null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x400, env, dir, ref si, out var pi))
        return "create="+Marshal.GetLastWin32Error();
      CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
      return "ok pid="+pi.dwProcessId;
    } finally {
      if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
      if (primary != IntPtr.Zero) CloseHandle(primary);
      CloseHandle(user);
    }
  }
}
'@

try { Add-Type -TypeDefinition $src -Language CSharp -ErrorAction Stop } catch {
    if ($_.Exception.Message -notmatch 'already exists') { W ("launch Add-Type: " + $_.Exception.Message); exit 1 }
}
$r = [VTalkLaunch]::Start($Exe, '--show')
W ("launch: " + $r)
if ($r -notlike 'ok*') { exit 1 }
exit 0
