using System.Runtime.InteropServices;

namespace Seasons;

// Rendering: Direct2D draws into a transparent swap chain, DirectComposition puts it on the window.
// No COM wrapper library: methods are called through their vtable index
// (the numbers come from the Windows SDK headers).
internal sealed unsafe class Gfx : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] public struct ColorF { public float R, G, B, A; }
    [StructLayout(LayoutKind.Sequential)] public struct Point2F { public float X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Ellipse { public float X, Y, Rx, Ry; }

    [StructLayout(LayoutKind.Sequential)]
    struct SwapChainDesc1
    {
        public uint Width, Height;
        public int Format, Stereo;
        public uint SampleCount, SampleQuality, BufferUsage, BufferCount;
        public int Scaling, SwapEffect, AlphaMode;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapProperties1
    {
        public int Format, AlphaMode;
        public float DpiX, DpiY;
        public int Options;
        public IntPtr ColorContext;
    }

    static Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    static Guid IID_IDXGIFactory2 = new("50C83A1C-E072-4C48-87B0-3630FA36A6D0");
    static Guid IID_IDXGISurface = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");
    static Guid IID_ID2D1Factory1 = new("BB12D362-DAEE-4B9A-AA1D-14BA401CFA1F");
    static Guid IID_IDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");

    const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    const int DXGI_STATUS_OCCLUDED = 0x087A0001;

    // Static on purpose: geometries depend on it and have to survive a device loss.
    static IntPtr s_d2dFactory;

    IntPtr _d3d, _d3dCtx, _dxgiDevice, _dxgiFactory, _swap, _d2dDevice, _dc, _bitmap, _brush;
    IntPtr _dcompDevice, _dcompTarget, _dcompVisual;

    static void** Vt(IntPtr p) => *(void***)p;

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
    }

    static void Release(ref IntPtr p)
    {
        if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; }
    }

    public static void ReleaseGeometry(ref IntPtr g) => Release(ref g);

    public Gfx(IntPtr hwnd, int width, int height, ColorF snowColor)
    {
        try
        {
            if (s_d2dFactory == IntPtr.Zero)
                Check(Native.D2D1CreateFactory(0 /* mono-thread */, ref IID_ID2D1Factory1, IntPtr.Zero, out s_d2dFactory), "D2D1CreateFactory");

            // GPU if possible, WARP (software) otherwise
            const uint BGRA = 0x20, SDK = 7;
            int hr = Native.D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, BGRA, IntPtr.Zero, 0, SDK, out _d3d, out _, out _d3dCtx);
            if (hr < 0) hr = Native.D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, BGRA, IntPtr.Zero, 0, SDK, out _d3d, out _, out _d3dCtx);
            Check(hr, "D3D11CreateDevice");
            Check(Marshal.QueryInterface(_d3d, ref IID_IDXGIDevice, out _dxgiDevice), "IDXGIDevice");

            // Composition swap chain with premultiplied alpha
            Check(Native.CreateDXGIFactory2(0, ref IID_IDXGIFactory2, out _dxgiFactory), "CreateDXGIFactory2");
            var desc = new SwapChainDesc1
            {
                Width = (uint)width, Height = (uint)height,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleCount = 1,
                BufferUsage = 0x20,   // RENDER_TARGET_OUTPUT
                BufferCount = 2,
                Scaling = 0,          // STRETCH
                SwapEffect = 3,       // FLIP_SEQUENTIAL
                AlphaMode = 1,        // PREMULTIPLIED
            };
            IntPtr swap;
            Check(((delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, SwapChainDesc1*, IntPtr, IntPtr*, int>)Vt(_dxgiFactory)[24])
                  (_dxgiFactory, _d3d, &desc, IntPtr.Zero, &swap), "CreateSwapChainForComposition");
            _swap = swap;

            // D2D context drawing into the back buffer
            IntPtr d2dDevice, dc, surface, bitmap, brush;
            Check(((delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, IntPtr*, int>)Vt(s_d2dFactory)[17])
                  (s_d2dFactory, _dxgiDevice, &d2dDevice), "ID2D1Factory1.CreateDevice");
            _d2dDevice = d2dDevice;
            Check(((delegate* unmanaged[MemberFunction]<IntPtr, int, IntPtr*, int>)Vt(_d2dDevice)[4])
                  (_d2dDevice, 0, &dc), "CreateDeviceContext");
            _dc = dc;

            Guid iidSurface = IID_IDXGISurface;
            Check(((delegate* unmanaged[MemberFunction]<IntPtr, uint, Guid*, IntPtr*, int>)Vt(_swap)[9])
                  (_swap, 0, &iidSurface, &surface), "GetBuffer");
            var props = new BitmapProperties1
            {
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                AlphaMode = 1,        // PREMULTIPLIED
                DpiX = 96, DpiY = 96,
                Options = 1 | 2,      // TARGET | CANNOT_DRAW
            };
            hr = ((delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, BitmapProperties1*, IntPtr*, int>)Vt(_dc)[62])
                 (_dc, surface, &props, &bitmap);
            Marshal.Release(surface);
            Check(hr, "CreateBitmapFromDxgiSurface");
            _bitmap = bitmap;
            ((delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, void>)Vt(_dc)[74])(_dc, _bitmap);          // SetTarget
            ((delegate* unmanaged[MemberFunction]<IntPtr, float, float, void>)Vt(_dc)[51])(_dc, 96f, 96f);   // SetDpi: work in physical pixels

            Check(((delegate* unmanaged[MemberFunction]<IntPtr, ColorF*, void*, IntPtr*, int>)Vt(_dc)[8])
                  (_dc, &snowColor, null, &brush), "CreateSolidColorBrush");
            _brush = brush;

            // Hook the swap chain up to the window
            IntPtr target, visual;
            Check(Native.DCompositionCreateDevice(_dxgiDevice, ref IID_IDCompositionDevice, out _dcompDevice), "DCompositionCreateDevice");
            Check(((delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, int, IntPtr*, int>)Vt(_dcompDevice)[6])
                  (_dcompDevice, hwnd, 1, &target), "CreateTargetForHwnd");
            _dcompTarget = target;
            Check(((delegate* unmanaged[MemberFunction]<IntPtr, IntPtr*, int>)Vt(_dcompDevice)[7])
                  (_dcompDevice, &visual), "CreateVisual");
            _dcompVisual = visual;
            Check(((delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, int>)Vt(_dcompVisual)[15])(_dcompVisual, _swap), "SetContent");
            Check(((delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, int>)Vt(_dcompTarget)[3])(_dcompTarget, _dcompVisual), "SetRoot");
            Check(((delegate* unmanaged[MemberFunction]<IntPtr, int>)Vt(_dcompDevice)[3])(_dcompDevice), "Commit");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Begin()
    {
        ((delegate* unmanaged[MemberFunction]<IntPtr, void>)Vt(_dc)[48])(_dc);                 // BeginDraw
        ColorF clear = default;                                                                 // transparent
        ((delegate* unmanaged[MemberFunction]<IntPtr, ColorF*, void>)Vt(_dc)[47])(_dc, &clear); // Clear
    }

    public void Dot(float x, float y, float radius, float opacity)
    {
        ((delegate* unmanaged[MemberFunction]<IntPtr, float, void>)Vt(_brush)[4])(_brush, opacity);            // SetOpacity
        var e = new Ellipse { X = x, Y = y, Rx = radius, Ry = radius };
        ((delegate* unmanaged[MemberFunction]<IntPtr, Ellipse*, IntPtr, void>)Vt(_dc)[21])(_dc, &e, _brush);   // FillEllipse
    }

    public void Fill(IntPtr geometry, float opacity)
    {
        ((delegate* unmanaged[MemberFunction]<IntPtr, float, void>)Vt(_brush)[4])(_brush, opacity);
        ((delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, IntPtr, IntPtr, void>)Vt(_dc)[23])(_dc, geometry, _brush, IntPtr.Zero); // FillGeometry
    }

    // Returns false when the device is lost and has to be recreated.
    public bool End(out bool occluded)
    {
        occluded = false;
        int hr = ((delegate* unmanaged[MemberFunction]<IntPtr, ulong*, ulong*, int>)Vt(_dc)[49])(_dc, null, null);  // EndDraw
        if (hr < 0) return false;
        hr = ((delegate* unmanaged[MemberFunction]<IntPtr, uint, uint, int>)Vt(_swap)[8])(_swap, 1, 0);             // Present (vsync)
        if (hr == DXGI_STATUS_OCCLUDED) occluded = true;
        return hr >= 0;
    }

    // Closed filled polygon, used for the snow layers.
    public static IntPtr CreatePolygon(Point2F* pts, int count)
    {
        if (s_d2dFactory == IntPtr.Zero || count < 3) return IntPtr.Zero;
        IntPtr geo, sink;
        if (((delegate* unmanaged[MemberFunction]<IntPtr, IntPtr*, int>)Vt(s_d2dFactory)[10])(s_d2dFactory, &geo) < 0) return IntPtr.Zero; // CreatePathGeometry
        if (((delegate* unmanaged[MemberFunction]<IntPtr, IntPtr*, int>)Vt(geo)[17])(geo, &sink) < 0) { Marshal.Release(geo); return IntPtr.Zero; } // Open
        ((delegate* unmanaged[MemberFunction]<IntPtr, Point2F, int, void>)Vt(sink)[5])(sink, pts[0], 0);              // BeginFigure (filled)
        ((delegate* unmanaged[MemberFunction]<IntPtr, Point2F*, uint, void>)Vt(sink)[6])(sink, pts + 1, (uint)(count - 1)); // AddLines
        ((delegate* unmanaged[MemberFunction]<IntPtr, int, void>)Vt(sink)[8])(sink, 1);                               // EndFigure (closed)
        int hr = ((delegate* unmanaged[MemberFunction]<IntPtr, int>)Vt(sink)[9])(sink);                               // Close
        Marshal.Release(sink);
        if (hr < 0) { Marshal.Release(geo); return IntPtr.Zero; }
        return geo;
    }

    public void Dispose()
    {
        if (_dc != IntPtr.Zero)
            ((delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, void>)Vt(_dc)[74])(_dc, IntPtr.Zero);
        Release(ref _dcompVisual);
        Release(ref _dcompTarget);
        Release(ref _dcompDevice);
        Release(ref _brush);
        Release(ref _bitmap);
        Release(ref _dc);
        Release(ref _d2dDevice);
        Release(ref _swap);
        Release(ref _dxgiFactory);
        Release(ref _dxgiDevice);
        Release(ref _d3dCtx);
        Release(ref _d3d);
    }
}
