using System.Runtime.InteropServices;

namespace Seasons;

// Win32 imports used by the rest of the project.
internal static unsafe class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int L, T, R, B;
        public readonly int W => R - L;
        public readonly int H => B - T;
        public readonly bool SameAs(in RECT o) => L == o.L && T == o.T && R == o.R && B == o.B;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    // Window styles
    public const uint WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000;
    public const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                      WS_EX_LAYERED = 0x80000, WS_EX_NOREDIRECTIONBITMAP = 0x200000, WS_EX_NOACTIVATE = 0x08000000;
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;

    // Messages
    public const uint WM_DESTROY = 0x0002, WM_DISPLAYCHANGE = 0x007E, WM_CONTEXTMENU = 0x007B,
                      WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_NULL = 0, WM_CLOSE = 0x0010, WM_APP = 0x8000;

    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_ASYNCWINDOWPOS = 0x4000;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    public const uint NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    public const uint MF_STRING = 0, MF_CHECKED = 8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;

    const string U = "user32.dll";

    [DllImport(U, SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport(U, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h,
                                                IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport(U)] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport(U)] public static extern int GetMessageW(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport(U)] public static extern int TranslateMessage(ref MSG msg);
    [DllImport(U)] public static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport(U)] public static extern void PostQuitMessage(int code);
    [DllImport(U)] public static extern int PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport(U)] public static extern int DestroyWindow(IntPtr h);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string s);
    [DllImport(U)] public static extern int ShowWindowAsync(IntPtr h, int cmd);
    [DllImport(U)] public static extern int ShowWindow(IntPtr h, int cmd);
    [DllImport(U)] public static extern int SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport(U)] public static extern int SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport(U)] public static extern int GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport(U)] public static extern int GetSystemMetrics(int i);
    [DllImport(U)] public static extern uint GetDpiForSystem();
    [DllImport(U)] public static extern int SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport(U)] public static extern int GetCursorPos(out POINT p);
    [DllImport(U)] public static extern IntPtr GetForegroundWindow();
    [DllImport(U)] public static extern int SetForegroundWindow(IntPtr h);
    [DllImport(U)] public static extern int IsWindowVisible(IntPtr h);
    [DllImport(U)] public static extern int IsIconic(IntPtr h);
    [DllImport(U)] public static extern int IsZoomed(IntPtr h);
    [DllImport(U)] public static extern int GetWindowRect(IntPtr h, out RECT r);
    [DllImport(U, EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport(U)] public static extern int GetClassNameW(IntPtr h, char* buf, int max);
    [DllImport(U)] public static extern int EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> cb, IntPtr l);
    [DllImport(U)] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport(U)] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport(U)] public static extern int GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
    [DllImport(U)] public static extern IntPtr CreatePopupMenu();
    [DllImport(U)] public static extern int DestroyMenu(IntPtr m);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern int AppendMenuW(IntPtr m, uint flags, UIntPtr id, string text);
    [DllImport(U)] public static extern int TrackPopupMenu(IntPtr m, uint flags, int x, int y, int r, IntPtr h, IntPtr rect);
    [DllImport(U)] public static extern IntPtr CreateIcon(IntPtr inst, int w, int h, byte planes, byte bpp, byte[] and, byte[] xor);
    [DllImport(U)] public static extern int DestroyIcon(IntPtr icon);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();

    // DirectX (ships with Windows)
    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels,
                                               uint numLevels, uint sdkVersion, out IntPtr device, out int level, out IntPtr context);
    [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory2(uint flags, ref Guid iid, out IntPtr factory);
    [DllImport("d2d1.dll")] public static extern int D2D1CreateFactory(int type, ref Guid iid, IntPtr options, out IntPtr factory);
    [DllImport("dcomp.dll")] public static extern int DCompositionCreateDevice(IntPtr dxgiDevice, ref Guid iid, out IntPtr device);

    public static string ClassName(IntPtr h)
    {
        char* buf = stackalloc char[80];
        int n = GetClassNameW(h, buf, 80);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    public static bool FrameBounds(IntPtr h, out RECT r)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out r, sizeof(RECT)) == 0 && r.W > 0 && r.H > 0) return true;
        return GetWindowRect(h, out r) != 0 && r.W > 0 && r.H > 0;
    }
}
