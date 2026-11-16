using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static Seasons.Native;

namespace Seasons;

internal static unsafe class Program
{
    const string AppName = "Seasons";
    const string SettingsKey = @"Software\Seasons";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const uint TrayMessage = WM_APP + 1;
    const int CmdPause = 1, CmdLow = 2, CmdMedium = 3, CmdStartup = 4, CmdQuit = 5, CmdWallpaper = 6, CmdOverlay = 7;

    static IntPtr s_overlay, s_tray, s_icon;
    static uint s_taskbarCreated;

    // Shared between the UI thread (icon, menu) and the render thread
    static volatile bool s_paused, s_quit, s_displayChanged, s_densityChanged;
    static volatile int s_density;   // 0 = low, 1 = medium
    static volatile int s_mode;      // 0 = wallpaper only, 1 = on top of windows

    [STAThread]
    static int Main()
    {
        using var mutex = new Mutex(true, "Seasons.SingleInstance", out bool first);
        if (!first) return 0;

        SetProcessDpiAwarenessContext(new IntPtr(-4));   // Per-Monitor V2, in case the manifest is ignored
        LoadSettings();
        RemoveOldStartupEntry();

        IntPtr inst = GetModuleHandleW(null);
        IntPtr clsName = Marshal.StringToHGlobalUni("SeasonsOverlayWindow");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            hInstance = inst,
            lpszClassName = clsName,
        };
        if (RegisterClassExW(ref wc) == 0) return Fail("Could not create the window.");

        // Invisible window that receives the tray icon clicks
        s_tray = CreateWindowExW(WS_EX_TOOLWINDOW, "SeasonsOverlayWindow", AppName, WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);

        // The overlay: topmost, transparent, click-through, hidden from the taskbar and Alt+Tab
        VirtualScreen(out int x, out int y, out int w, out int h);
        s_overlay = CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "SeasonsOverlayWindow", AppName, WS_POPUP, x, y, w, h, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (s_tray == IntPtr.Zero || s_overlay == IntPtr.Zero) return Fail("Could not create the window.");

        s_icon = MakeIcon();
        s_taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        AddTrayIcon();

        var render = new Thread(RenderLoop) { IsBackground = true, Name = "Seasons.Render" };
        render.Start();

        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        s_quit = true;
        render.Join(1500);
        RemoveTrayIcon();
        if (s_icon != IntPtr.Zero) DestroyIcon(s_icon);
        return 0;
    }

    static int Fail(string message)
    {
        MessageBoxW(IntPtr.Zero, message, AppName, 0x10);
        return 1;
    }

    static void VirtualScreen(out int x, out int y, out int w, out int h)
    {
        // 1 px margin, otherwise Windows treats the overlay as a fullscreen app
        // (same trick PowerToys uses)
        x = GetSystemMetrics(SM_XVIRTUALSCREEN) + 1; y = GetSystemMetrics(SM_YVIRTUALSCREEN) + 1;
        w = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN) - 2); h = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN) - 2);
    }

    // --- Rendering ---

    static void RenderLoop()
    {
        float scale = Math.Max(1f, GetDpiForSystem() / 96f);
        var snow = new Snow(scale);
        Gfx gfx = null;
        bool shown = false, fullscreen = false;
        int failures = 0;
        long lastTicks = Stopwatch.GetTimestamp(), lastCheck = 0, lastTop = 0;
        double toSeconds = 1.0 / Stopwatch.Frequency;

        while (!s_quit)
        {
            long now = Stopwatch.GetTimestamp();

            if ((now - lastCheck) * toSeconds > 0.25)
            {
                lastCheck = now;
                fullscreen = Snow.FullscreenAppActive(s_overlay);
            }

            // Paused or fullscreen app: hide everything and stop drawing
            if (s_paused || fullscreen)
            {
                if (shown) { ShowWindowAsync(s_overlay, SW_HIDE); shown = false; }
                Thread.Sleep(200);
                lastTicks = Stopwatch.GetTimestamp();
                continue;
            }

            if (gfx == null || s_displayChanged)
            {
                s_displayChanged = false;
                gfx?.Dispose();
                gfx = null;
                VirtualScreen(out int x, out int y, out int w, out int h);
                SetWindowPos(s_overlay, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
                try
                {
                    gfx = new Gfx(s_overlay, w, h, Snow.SnowColor);
                    failures = 0;
                }
                catch (Exception e)
                {
                    if (++failures >= 5)
                    {
                        MessageBoxW(IntPtr.Zero, "Graphics could not be started.\n\n" + e.Message, AppName, 0x10);
                        PostMessageW(s_tray, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        return;
                    }
                    Thread.Sleep(1500);
                    continue;
                }
                snow.Resize(x, y, w, h, s_density);
                s_densityChanged = false;
                lastTicks = Stopwatch.GetTimestamp();
            }

            if (s_densityChanged) { s_densityChanged = false; snow.SetDensity(s_density); }

            if (!shown)
            {
                ShowWindowAsync(s_overlay, SW_SHOWNOACTIVATE);
                shown = true;
                lastTop = 0;
            }
            if ((now - lastTop) * toSeconds > 2.0)
            {
                // Other topmost windows can end up in front, so push ourselves back on top
                lastTop = now;
                SetWindowPos(s_overlay, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            }

            float dt = (float)Math.Min(0.05, (now - lastTicks) * toSeconds);
            lastTicks = now;

            snow.Wallpaper = s_mode == 0;
            snow.Update(dt, s_overlay, s_tray);
            if (!snow.Render(gfx, out bool occluded))
            {
                // Device lost (wake from sleep, driver update...): rebuild everything
                gfx.Dispose();
                gfx = null;
                Thread.Sleep(500);
                continue;
            }
            if (occluded) { Thread.Sleep(250); continue; }

            // Present normally waits for vsync, if it didn't, sync on the DWM instead
            if ((Stopwatch.GetTimestamp() - now) * toSeconds < 0.004) DwmFlush();
        }

        gfx?.Dispose();
    }

    // --- Windows and tray icon ---

    [UnmanagedCallersOnly]
    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TrayMessage && hwnd == s_tray)
        {
            uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
            if (ev == WM_RBUTTONUP || ev == WM_LBUTTONUP || ev == WM_CONTEXTMENU) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == WM_DISPLAYCHANGE) { s_displayChanged = true; return IntPtr.Zero; }
        if (msg == s_taskbarCreated && s_taskbarCreated != 0 && hwnd == s_tray) { AddTrayIcon(); return IntPtr.Zero; }
        if (msg == WM_CLOSE && hwnd == s_tray) { DestroyWindow(hwnd); return IntPtr.Zero; }
        if (msg == WM_DESTROY && hwnd == s_tray) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    static NOTIFYICONDATAW TrayData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = s_tray,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = TrayMessage,
        hIcon = s_icon,
        szTip = AppName,
        szInfo = "",
        szInfoTitle = "",
    };

    static void AddTrayIcon() { var d = TrayData(); Shell_NotifyIconW(NIM_ADD, ref d); }
    static void RemoveTrayIcon() { var d = TrayData(); Shell_NotifyIconW(NIM_DELETE, ref d); }

    static void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu(), density = CreatePopupMenu(), mode = CreatePopupMenu();
        AppendMenuW(mode, MF_STRING | (s_mode == 0 ? MF_CHECKED : 0), CmdWallpaper, "Wallpaper only");
        AppendMenuW(mode, MF_STRING | (s_mode == 1 ? MF_CHECKED : 0), CmdOverlay, "On top of windows");
        AppendMenuW(density, MF_STRING | (s_density == 0 ? MF_CHECKED : 0), CmdLow, "Low");
        AppendMenuW(density, MF_STRING | (s_density == 1 ? MF_CHECKED : 0), CmdMedium, "Medium");
        AppendMenuW(menu, MF_STRING | (s_paused ? MF_CHECKED : 0), CmdPause, "Pause");
        AppendMenuW(menu, MF_POPUP, (UIntPtr)(nuint)mode, "Display");
        AppendMenuW(menu, MF_POPUP, (UIntPtr)(nuint)density, "Density");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING | (StartupEnabled() ? MF_CHECKED : 0), CmdStartup, "Start with Windows");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING, CmdQuit, "Quit");

        GetCursorPos(out POINT p);
        SetForegroundWindow(s_tray);     // otherwise the menu won't close when clicking elsewhere
        int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON, p.X, p.Y, 0, s_tray, IntPtr.Zero);
        PostMessageW(s_tray, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);               // also destroys the submenus

        switch (cmd)
        {
            case CmdPause: s_paused = !s_paused; break;
            case CmdLow: SetDensity(0); break;
            case CmdMedium: SetDensity(1); break;
            case CmdWallpaper: SetMode(0); break;
            case CmdOverlay: SetMode(1); break;
            case CmdStartup: SetStartup(!StartupEnabled()); break;
            case CmdQuit: DestroyWindow(s_tray); break;
        }
    }

    // --- Settings, stored in HKCU ---

    static void LoadSettings()
    {
        try
        {
            using RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey);
            s_density = k?.GetValue("Density") is int v && v == 1 ? 1 : 0;
            s_mode = k?.GetValue("Mode") is int m && m == 1 ? 1 : 0;
        }
        catch { s_density = 0; s_mode = 0; }
    }

    static void SetDensity(int value)
    {
        s_density = value;
        s_densityChanged = true;
        try
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsKey);
            k?.SetValue("Density", value, RegistryValueKind.DWord);
        }
        catch { }
    }

    static void SetMode(int value)
    {
        s_mode = value;
        try
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsKey);
            k?.SetValue("Mode", value, RegistryValueKind.DWord);
        }
        catch { }
    }

    // Older builds were called "Neige" and "Saisons": clean up their startup entries
    static void RemoveOldStartupEntry()
    {
        try
        {
            using RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            k?.DeleteValue("Neige", false);
            k?.DeleteValue("Saisons", false);
        }
        catch { }
    }

    static bool StartupEnabled()
    {
        try
        {
            using RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(AppName) is string;
        }
        catch { return false; }
    }

    static void SetStartup(bool enable)
    {
        try
        {
            using RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (enable) k.SetValue(AppName, "\"" + Environment.ProcessPath + "\"");
            else k.DeleteValue(AppName, false);
        }
        catch { }
    }

    // --- Icon: a snowflake drawn in code, no .ico file needed ---

    static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy), 0f, 1f);
        float ex = px - (ax + t * dx), ey = py - (ay + t * dy);
        return MathF.Sqrt(ex * ex + ey * ey);
    }

    static IntPtr MakeIcon()
    {
        const int n = 32;
        var xor = new byte[n * n * 4];
        var and = new byte[n * n / 8];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float px = x + 0.5f - n / 2f, py = y + 0.5f - n / 2f;
                float d = 99f;
                for (int arm = 0; arm < 6; arm++)
                {
                    float ang = arm * MathF.PI / 3f + MathF.PI / 2f;
                    float ux = MathF.Cos(ang), uy = MathF.Sin(ang);
                    d = MathF.Min(d, SegmentDistance(px, py, 0, 0, ux * 13f, uy * 13f));
                    for (int side = -1; side <= 1; side += 2)
                    {
                        float a2 = ang + side * 0.9f;
                        d = MathF.Min(d, SegmentDistance(px, py, ux * 7.5f, uy * 7.5f,
                                                         ux * 7.5f + MathF.Cos(a2) * 4.5f, uy * 7.5f + MathF.Sin(a2) * 4.5f));
                    }
                }
                float alpha = Math.Clamp(1.6f - d, 0f, 1f);
                int i = (y * n + x) * 4;
                xor[i] = 0xF0; xor[i + 1] = 0xE6; xor[i + 2] = 0xDE;      // B, G, R
                xor[i + 3] = (byte)(alpha * 255f);
            }
        }
        return CreateIcon(GetModuleHandleW(null), n, n, 1, 32, and, xor);
    }
}
