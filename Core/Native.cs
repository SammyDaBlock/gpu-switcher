using System.Runtime.InteropServices;
using System.Text;

namespace GpuSwitcher.Core;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct DXGI_ADAPTER_DESC1
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    public uint VendorId, DeviceId, SubSysId, Revision;
    public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
    public uint LuidLow;
    public int LuidHigh;
    public uint Flags;
}

[ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDXGIAdapter1
{
    // IDXGIObject
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    // IDXGIAdapter
    [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
    void GetDesc(); void CheckInterfaceSupport();
    // IDXGIAdapter1
    void GetDesc1(out DXGI_ADAPTER_DESC1 desc);
}

[ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDXGIFactory1
{
    // IDXGIObject
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    // IDXGIFactory
    void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
    // IDXGIFactory1
    [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
}

public record DxgiAdapterInfo(string Name, uint VendorId, uint DeviceId, uint SubSysId, long Luid, ulong VramBytes, int Outputs, bool Software);

static class Native
{
    // ---------- DXGI ----------
    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    public static List<DxgiAdapterInfo> EnumDxgiAdapters()
    {
        var list = new List<DxgiAdapterInfo>();
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factory) != 0) return list;
        try
        {
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
            {
                try
                {
                    adapter.GetDesc1(out var d);
                    int outputs = 0;
                    while (adapter.EnumOutputs((uint)outputs, out var o) == 0) { Marshal.Release(o); outputs++; }
                    long luid = ((long)d.LuidHigh << 32) | d.LuidLow;
                    list.Add(new DxgiAdapterInfo(d.Description, d.VendorId, d.DeviceId, d.SubSysId, luid,
                        (ulong)d.DedicatedVideoMemory, outputs, (d.Flags & 2) != 0));
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
        }
        finally { Marshal.ReleaseComObject(factory); }
        return list;
    }

    // ---------- CfgMgr32 (device enable/disable) ----------
    public const int CR_SUCCESS = 0;
    public const uint CM_PROB_DISABLED = 22;
    const uint DN_HAS_PROBLEM = 0x00000400;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Disable_DevNode(uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Enable_DevNode(uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Child(out uint child, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Sibling(out uint sibling, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, int bufferLen, uint flags);

    /// <summary>True if a real monitor (DISPLAY\... child device) is plugged into this GPU.
    /// Unlike DXGI outputs, this doesn't depend on which GPU our own process is set to render on.</summary>
    public static bool HasMonitors(string pnpId)
    {
        if (CM_Locate_DevNodeW(out var dev, pnpId, 0) != CR_SUCCESS) return false;
        var buf = new char[512];
        for (int cr = CM_Get_Child(out var child, dev, 0); cr == CR_SUCCESS; cr = CM_Get_Sibling(out child, child, 0))
        {
            if (CM_Get_Device_IDW(child, buf, buf.Length, 0) != CR_SUCCESS) continue;
            if (new string(buf).TrimEnd('\0').StartsWith(@"DISPLAY\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Non-persistent disable (a reboot brings the device back). Returns CONFIGRET.</summary>
    public static int DisableDevice(string pnpId)
    {
        int cr = CM_Locate_DevNodeW(out var dev, pnpId, 0);
        return cr != CR_SUCCESS ? cr : CM_Disable_DevNode(dev, 4 /* CM_DISABLE_UI_NOT_OK */);
    }

    public static int EnableDevice(string pnpId)
    {
        int cr = CM_Locate_DevNodeW(out var dev, pnpId, 0);
        return cr != CR_SUCCESS ? cr : CM_Enable_DevNode(dev, 0);
    }

    public static bool IsDeviceDisabled(string pnpId)
    {
        // CM_LOCATE_DEVNODE_PHANTOM (1) so we still find it while it's disabled
        if (CM_Locate_DevNodeW(out var dev, pnpId, 1) != CR_SUCCESS) return false;
        if (CM_Get_DevNode_Status(out var status, out var problem, dev, 0) != CR_SUCCESS) return false;
        return (status & DN_HAS_PROBLEM) != 0 && problem == CM_PROB_DISABLED;
    }

    // ---------- processes ----------
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref int size);

    public static string? GetProcessPath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // ---------- fast process list (Toolhelp: names + pids only, well under 1 ms) ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);

    public static List<(int Pid, string Name)> ProcessSnapshot()
    {
        var list = new List<(int, string)>(512);
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref e))
                do list.Add(((int)e.th32ProcessID, e.szExeFile)); while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    // ---------- launch un-elevated (with explorer's token) ----------
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? app, StringBuilder? cmd, uint flags,
        IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    /// <summary>PID of Explorer's shell (0 if the desktop isn't up yet).</summary>
    public static int GetShellProcessId()
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(shell, out int pid);
        return pid;
    }

    /// <summary>Starts a process as the normal (non-admin) desktop user, so relaunched apps don't inherit our admin rights.</summary>
    public static int LaunchUnelevated(string exe, string? commandLine, string? workingDir)
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero) throw new InvalidOperationException("Explorer isn't running.");
        GetWindowThreadProcessId(shell, out int explorerPid);
        var hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, explorerPid);
        if (hProc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            if (!OpenProcessToken(hProc, 0x0002 /* TOKEN_DUPLICATE */, out var tok)) throw new System.ComponentModel.Win32Exception();
            try
            {
                if (!DuplicateTokenEx(tok, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2, 1, out var primary))
                    throw new System.ComponentModel.Win32Exception();
                try
                {
                    var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                    var cmd = new StringBuilder(string.IsNullOrWhiteSpace(commandLine) ? $"\"{exe}\"" : commandLine);
                    if (!CreateProcessWithTokenW(primary, 0, exe, cmd, 0x00000400 /* CREATE_UNICODE_ENVIRONMENT */, IntPtr.Zero,
                            workingDir ?? Path.GetDirectoryName(exe), ref si, out var pi))
                        throw new System.ComponentModel.Win32Exception();
                    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                    return pi.dwProcessId;
                }
                finally { CloseHandle(primary); }
            }
            finally { CloseHandle(tok); }
        }
        finally { CloseHandle(hProc); }
    }
}
