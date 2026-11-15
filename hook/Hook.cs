// GpuSwitcherHook.exe: started by Windows instead of a hooked launcher (Image File Execution Options "Debugger"),
// e.g. when Epic starts FortniteLauncher.exe. GPU Switcher sees this process, turns the extra GPU off and then
// removes the hook: that removal is the go signal, so the real launcher (and its anti-cheat) start with the GPU
// already off and nothing changes under them. Built with the C# compiler that ships with Windows (build.cmd).
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

static class Hook
{
    const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\";

    static int Main()
    {
        // Windows runs: "<this exe>" <the original command line>, passed on byte for byte
        string rest = SkipArg(Environment.CommandLine);
        string exe = FirstArg(rest);
        if (exe.Length == 0) return 1;
        string name = Path.GetFileName(exe);

        // wait for GPU Switcher to take the hook away (only if it's running; 20 s cap so a stuck app never blocks the game)
        var sw = Stopwatch.StartNew();
        while (Hooked(name) && Process.GetProcessesByName("GpuSwitcher").Length > 0 && sw.ElapsedMilliseconds < 20000)
            Thread.Sleep(50);

        // still hooked (GPU Switcher closed or stuck): start it as a debuggee and let go at once,
        // the one way Windows skips the hook, so the game still starts, just without force mode
        bool viaDebug = Hooked(name);
        var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
        PROCESS_INFORMATION pi;
        if (!CreateProcess(exe, rest, IntPtr.Zero, IntPtr.Zero, false, viaDebug ? DEBUG_ONLY_THIS_PROCESS : 0, IntPtr.Zero, null, ref si, out pi))
            return Marshal.GetLastWin32Error();
        if (viaDebug)
        {
            var ev = new byte[256];
            if (WaitForDebugEvent(ev, 10000))
            {
                if (BitConverter.ToInt32(ev, 0) == CREATE_PROCESS_DEBUG_EVENT) CloseHandle((IntPtr)BitConverter.ToInt64(ev, 16)); // its hFile
                ContinueDebugEvent(BitConverter.ToInt32(ev, 4), BitConverter.ToInt32(ev, 8), DBG_CONTINUE);
            }
            DebugSetProcessKillOnExit(false);
            DebugActiveProcessStop(pi.dwProcessId);
        }
        CloseHandle(pi.hThread);
        // stay alive like the real launcher so whoever started it (Epic) sees it run and exit
        WaitForSingleObject(pi.hProcess, uint.MaxValue);
        uint code;
        GetExitCodeProcess(pi.hProcess, out code);
        return (int)code;
    }

    static bool Hooked(string name)
    {
        using (var k = Registry.LocalMachine.OpenSubKey(Ifeo + name))
        {
            var v = k == null ? null : k.GetValue("Debugger") as string;
            return v != null && v.IndexOf("GpuSwitcherHook", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // Windows' rule for the program name: quoted up to the next quote, else up to the first space/tab
    static int ArgEnd(string s)
    {
        if (s.StartsWith("\"")) { int q = s.IndexOf('"', 1); return q < 0 ? s.Length : q + 1; }
        int i = s.IndexOfAny(new[] { ' ', '\t' });
        return i < 0 ? s.Length : i;
    }
    static string SkipArg(string s) { s = s.TrimStart(); return s.Substring(ArgEnd(s)).TrimStart(' ', '\t'); }
    static string FirstArg(string s) { return s.Substring(0, ArgEnd(s)).Trim('"'); }

    const uint DEBUG_ONLY_THIS_PROCESS = 0x2, DBG_CONTINUE = 0x00010002;
    const int CREATE_PROCESS_DEBUG_EVENT = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool WaitForDebugEvent(byte[] ev, uint ms);
    [DllImport("kernel32.dll")] static extern bool ContinueDebugEvent(int pid, int tid, uint status);
    [DllImport("kernel32.dll")] static extern bool DebugActiveProcessStop(int pid);
    [DllImport("kernel32.dll")] static extern bool DebugSetProcessKillOnExit(bool kill);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
