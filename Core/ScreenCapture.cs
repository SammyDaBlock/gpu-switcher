using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GpuSwitcher.Core;

/// <summary>
/// Grabs a square of one monitor as a 32x32 RGB image. Windows.Graphics.Capture (what Discord / OBS / Medal use)
/// hands frames to a D3D device on the monitors' GPU, which also does the downscaling (mipmaps = box filter);
/// the CPU only averages a tiny 32-63 px image. Unlike Desktop Duplication this works while GPU Switcher itself is
/// set to the other GPU.
/// </summary>
public sealed unsafe class ScreenCapture : IDisposable
{
    const uint BGRA = 87; // DXGI_FORMAT_B8G8R8A8_UNORM
    const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    static readonly Guid IID_Factory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    static readonly Guid IID_DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    static readonly Guid IID_Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    static readonly Guid IID_CaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, in Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, in Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(in Guid iid);
    }

    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);
    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels,
        uint sdkVersion, out IntPtr device, out int level, out IntPtr context);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct TexDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, Misc; }
    [StructLayout(LayoutKind.Sequential)]
    struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)]
    struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }

    readonly (int X, int Y, int W, int H) _monitor;
    readonly ICollection<long> _gpuLuids;
    IntPtr _device, _context, _mipTex, _srv, _staging;
    IDirect3DDevice? _winrtDevice;
    GraphicsCaptureItem? _item;
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    Windows.Graphics.SizeInt32 _poolSize;
    int _mipSize, _stagingSize;
    long _retryAt;

    /// <param name="monitor">The monitor's bounds in screen pixels.</param>
    /// <param name="gpuLuids">The GPU to capture on (the one the monitors are plugged into); empty = any.</param>
    public ScreenCapture((int X, int Y, int W, int H) monitor, ICollection<long> gpuLuids)
    {
        _monitor = monitor;
        _gpuLuids = gpuLuids;
    }

    static IntPtr Slot(IntPtr obj, int i) => (*(IntPtr**)obj)[i];
    static void Release(ref IntPtr p) { if (p != IntPtr.Zero) Marshal.Release(p); p = IntPtr.Zero; }

    /// <summary>
    /// Fills rgb (32*32*3) from the square region (fractions of the monitor; null = middle square).
    /// Returns false when the screen hasn't changed since the last call (or capture isn't possible right now).
    /// </summary>
    public bool Grab(CoverRect? region, byte[] rgb)
    {
        if (_pool == null && !Init()) return false;

        using var frame = _pool!.TryGetNextFrame();
        if (frame == null) return false; // nothing new on screen

        // monitor resolution changed: resize the pool, use the next frame
        if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
        {
            _poolSize = frame.ContentSize;
            _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, _poolSize);
            return false;
        }

        int monW = _poolSize.Width, monH = _poolSize.Height;
        int size, x, y;
        if (region is { } r)
        {
            size = Math.Max(32, (int)Math.Min(r.W * monW, r.H * monH));
            x = (int)(r.X * monW); y = (int)(r.Y * monH);
        }
        else { size = Math.Min(monW, monH); x = (monW - size) / 2; y = (monH - size) / 2; }
        size = Math.Min(size, Math.Min(monW, monH));
        x = Math.Clamp(x, 0, monW - size); y = Math.Clamp(y, 0, monH - size);

        if (_mipSize != size && !MakeMipTexture(size)) return false;
        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var tex = access.GetInterface(IID_Texture2D);
        try
        {
            var box = new Box { Left = (uint)x, Top = (uint)y, Right = (uint)(x + size), Bottom = (uint)(y + size), Back = 1 };
            CopyRegion(_mipTex, 0, tex, 0, &box);
        }
        finally { Marshal.Release(tex); }

        ((delegate* unmanaged<IntPtr, IntPtr, void>)Slot(_context, 54))(_context, _srv); // GenerateMips

        // smallest mip that's still >= 32 px
        int level = 0, m = _mipSize;
        while (m / 2 >= 32) { m /= 2; level++; }
        if (_stagingSize != m && !MakeStaging(m)) return false;
        CopyRegion(_staging, 0, _mipTex, (uint)level, null);

        Mapped map;
        if (((delegate* unmanaged<IntPtr, IntPtr, uint, int, uint, Mapped*, int>)Slot(_context, 14))(_context, _staging, 0, 1 /* READ */, 0, &map) < 0) return false;
        try { Average((byte*)map.Data, (int)map.RowPitch, m, rgb); }
        finally { ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Slot(_context, 15))(_context, _staging, 0); }
        return true;
    }

    void CopyRegion(IntPtr dst, uint dstSub, IntPtr src, uint srcSub, Box* box) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)Slot(_context, 46))(_context, dst, dstSub, 0, 0, 0, src, srcSub, box);

    /// <summary>m x m BGRA -> 32x32 RGB, each output pixel = average of the source pixels it covers.</summary>
    static void Average(byte* src, int pitch, int m, byte[] rgb)
    {
        for (int oy = 0; oy < 32; oy++)
        {
            int y0 = oy * m / 32, y1 = Math.Max(y0 + 1, (oy + 1) * m / 32);
            for (int ox = 0; ox < 32; ox++)
            {
                int x0 = ox * m / 32, x1 = Math.Max(x0 + 1, (ox + 1) * m / 32);
                int r = 0, g = 0, b = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    var row = src + sy * pitch;
                    for (int sx = x0; sx < x1; sx++) { b += row[sx * 4]; g += row[sx * 4 + 1]; r += row[sx * 4 + 2]; n++; }
                }
                int o = (oy * 32 + ox) * 3;
                rgb[o] = (byte)(r / n); rgb[o + 1] = (byte)(g / n); rgb[o + 2] = (byte)(b / n);
            }
        }
    }

    bool MakeMipTexture(int size)
    {
        Release(ref _srv); Release(ref _mipTex); _mipSize = 0;
        // MipLevels 0 = full chain; render target + shader resource + GENERATE_MIPS so the GPU can build it
        var d = new TexDesc { Width = (uint)size, Height = (uint)size, MipLevels = 0, ArraySize = 1, Format = BGRA, SampleCount = 1, BindFlags = 0x8 | 0x20, Misc = 0x1 };
        IntPtr tex, srv;
        if (((delegate* unmanaged<IntPtr, TexDesc*, IntPtr, IntPtr*, int>)Slot(_device, 5))(_device, &d, IntPtr.Zero, &tex) < 0) return Fail("mip texture");
        if (((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Slot(_device, 7))(_device, tex, IntPtr.Zero, &srv) < 0) { Marshal.Release(tex); return Fail("mip view"); }
        _mipTex = tex; _srv = srv; _mipSize = size;
        return true;
    }

    bool MakeStaging(int m)
    {
        Release(ref _staging); _stagingSize = 0;
        var d = new TexDesc { Width = (uint)m, Height = (uint)m, MipLevels = 1, ArraySize = 1, Format = BGRA, SampleCount = 1, Usage = 3 /* STAGING */, CpuAccess = 0x20000 /* READ */ };
        IntPtr tex;
        if (((delegate* unmanaged<IntPtr, TexDesc*, IntPtr, IntPtr*, int>)Slot(_device, 5))(_device, &d, IntPtr.Zero, &tex) < 0) return Fail("staging texture");
        _staging = tex; _stagingSize = m;
        return true;
    }

    bool Fail(string what) { Log.Write($"Pixel mirror: couldn't create the {what}"); return false; }

    bool Init()
    {
        if (Environment.TickCount64 < _retryAt) return false;
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("screen capture isn't supported on this Windows");
            if (!CreateDevice()) throw new InvalidOperationException("couldn't create a D3D11 device");

            var hmon = MonitorFromPoint(new POINT { X = _monitor.X + _monitor.W / 2, Y = _monitor.Y + _monitor.H / 2 }, 2 /* NEAREST */);
            var interop = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem").AsInterface<IGraphicsCaptureItemInterop>();
            var itemPtr = interop.CreateForMonitor(hmon, IID_CaptureItem);
            try { _item = GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }

            _poolSize = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, _poolSize);
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            // Windows 11: no yellow "being captured" border around the monitor (desktop apps get this without asking)
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) _session.IsBorderRequired = false;
            _session.StartCapture();
            Log.Write($"Pixel mirror: capturing {_poolSize.Width}x{_poolSize.Height}");
            _retryAt = 0;
            return true;
        }
        catch (Exception ex)
        {
            // lock screen / UAC / unplugged monitor: try again in 3 s, quietly
            if (_retryAt == 0) Log.Write($"Pixel mirror: can't capture ({ex.Message}), retrying");
            Reset();
            _retryAt = Environment.TickCount64 + 3000;
            return false;
        }
    }

    /// <summary>D3D11 device on the monitors' GPU (falls back to the first GPU), plus its WinRT wrapper for the capture API.</summary>
    bool CreateDevice()
    {
        if (CreateDXGIFactory1(IID_Factory1, out var factory) < 0) return false;
        try
        {
            IntPtr pick = IntPtr.Zero;
            for (uint a = 0; ; a++)
            {
                IntPtr adapter;
                if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(factory, 12))(factory, a, &adapter) == DXGI_ERROR_NOT_FOUND) break;
                var raw = stackalloc byte[Marshal.SizeOf<DXGI_ADAPTER_DESC1>()];
                ((delegate* unmanaged<IntPtr, byte*, int>)Slot(adapter, 10))(adapter, raw); // GetDesc1
                var d = Marshal.PtrToStructure<DXGI_ADAPTER_DESC1>((IntPtr)raw);
                long luid = ((long)d.LuidHigh << 32) | d.LuidLow;
                if (pick == IntPtr.Zero && (_gpuLuids.Count == 0 || _gpuLuids.Contains(luid))) pick = adapter;
                else Marshal.Release(adapter);
            }
            try
            {
                // adapter given -> D3D_DRIVER_TYPE_UNKNOWN (0); null adapter -> HARDWARE (1). BGRA support flag.
                if (D3D11CreateDevice(pick, pick == IntPtr.Zero ? 1 : 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out _device, out _, out _context) < 0) return false;
            }
            finally { if (pick != IntPtr.Zero) Marshal.Release(pick); }
        }
        finally { Marshal.Release(factory); }

        var iid = IID_DxgiDevice;
        if (Marshal.QueryInterface(_device, ref iid, out var dxgi) < 0) return false;
        try
        {
            if (CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable) < 0) return false;
            try { _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
            finally { Marshal.Release(inspectable); }
        }
        finally { Marshal.Release(dxgi); }
        return true;
    }

    void Reset()
    {
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        try { _winrtDevice?.Dispose(); } catch { }
        _session = null; _pool = null; _item = null; _winrtDevice = null;
        Release(ref _staging); Release(ref _srv); Release(ref _mipTex); Release(ref _context); Release(ref _device);
        _mipSize = _stagingSize = 0;
    }

    public void Dispose() => Reset();
}
