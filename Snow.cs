using System.Runtime.InteropServices;
using static Seasons.Native;

namespace Seasons;

// The whole winter simulation: flakes, wind, mouse, snow resting on windows and on the ground.
// Runs on the render thread. Coordinates are physical pixels.
internal sealed unsafe class Snow
{
    // --- Settings (sizes are for a 100 % scaled screen) ---
    public static readonly Gfx.ColorF SnowColor = new() { R = 0xD6 / 255f, G = 0xDC / 255f, B = 0xE6 / 255f, A = 1f };
    const float MouseRadius = 80f;        // how far the mouse pushes flakes
    const float CapMax = 6f;              // snow thickness on a window
    const float CapFillSeconds = 40f;     // time for a window to get fully covered
    const float GroundMax = 14f;          // snow thickness on the ground (taskbar)
    const float GroundPile = 3f;          // a pile on the ground can reach GroundMax * 3
    const float CapPile = 2.2f;           // same thing on windows
    const float GroundFillSeconds = 60f;
    const float CapOpacity = 0.86f;
    const float LandChance = 0.55f;
    const int MaxDebris = 3000;
    static readonly float[] WindFactor = { 0.5f, 0.8f, 1.1f };

    struct Flake
    {
        public float X, Y, R, Vy, Op, Phase, SwayAmp, SwayFreq, Pvx, Pvy;
        public byte Layer;   // 0 = far (small, slow, blurry), 2 = near
    }

    // Falling snow. With Mass > 0 it settles wherever it lands (Mass = area in px²),
    // with Mass = 0 it's just a puff that fades out.
    struct Debris { public float X, Y, Vx, Vy, Life, R, Op, Mass; public IntPtr Skip; }

    struct Win { public IntPtr H; public RECT R; public bool Host, Ground; }

    sealed class Cap
    {
        public IntPtr Hwnd;
        public RECT Rect;
        public int X0, Bw, N, Gen;
        public float[] H = Array.Empty<float>(), MaxH = Array.Empty<float>();
        public List<int> Vis = new();          // visible parts of the top edge, as [start, end) pairs
        public List<IntPtr> Geos = new();
        public bool Dirty, Full, Unsettled;
        public bool Ground;                    // true for the taskbar
        public float Limit;                    // max pile height
        public float Settle, DirtyTimer;
    }

    readonly Random _rng = new();
    readonly float _s;                 // DPI scale (1 = 100 %)
    readonly float _capPx, _groundPx, _capLimit, _groundLimit;
    float[] _scratch = new float[256];

    // true: flakes only on the wallpaper, false: on top of everything
    public bool Wallpaper = true;
    POINT _prevCur;
    int _curDx;
    Flake[] _flakes = Array.Empty<Flake>();
    int _count;
    readonly List<Debris> _debris = new();
    readonly Dictionary<IntPtr, Cap> _caps = new();
    readonly List<IntPtr> _gone = new();
    readonly List<int> _tmpA = new(), _tmpB = new();
    Gfx.Point2F[] _pts = new Gfx.Point2F[512];
    int _ox, _oy, _w, _h, _gen, _frame;
    float _wind, _windTarget, _windTimer;

    static readonly List<Win> s_wins = new();
    static IntPtr s_skip1, s_skip2;
    static int s_minW, s_minH;

    public Snow(float scale)
    {
        _s = scale;
        _capPx = CapMax * scale;
        _groundPx = GroundMax * scale;
        _capLimit = _capPx * CapPile;
        _groundLimit = _groundPx * GroundPile;
    }

    float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    // --- Flakes ---

    public void Resize(int originX, int originY, int width, int height, int density)
    {
        _ox = originX; _oy = originY; _w = width; _h = height;
        ClearCaps();
        _debris.Clear();
        _count = 0;
        SetDensity(density);
    }

    public void SetDensity(int density)
    {
        // roughly 1 flake per 36,000 px² on low, 18,000 on medium
        float logicalArea = (float)_w * _h / (_s * _s);
        int target = Math.Clamp((int)(logicalArea / (density == 0 ? 36000f : 18000f)), 20, 1200);
        if (_flakes.Length < target) Array.Resize(ref _flakes, target);
        for (int i = _count; i < target; i++) Spawn(ref _flakes[i], true);
        _count = target;
    }

    void Spawn(ref Flake f, bool anywhere)
    {
        double p = _rng.NextDouble();
        float d;
        if (p < 0.45) { f.Layer = 0; d = Rand(1.0f, 1.8f); f.Vy = Rand(15, 23); f.Op = Rand(0.30f, 0.42f); }
        else if (p < 0.80) { f.Layer = 1; d = Rand(1.8f, 2.8f); f.Vy = Rand(23, 35); f.Op = Rand(0.40f, 0.56f); }
        else { f.Layer = 2; d = Rand(2.8f, 4.0f); f.Vy = Rand(35, 52); f.Op = Rand(0.55f, 0.70f); }
        f.R = d * 0.5f * _s;
        f.Vy *= _s;
        f.SwayAmp = Rand(4, 14) * _s;
        f.SwayFreq = Rand(0.4f, 1.1f);
        f.Phase = Rand(0, MathF.Tau);
        f.Pvx = f.Pvy = 0;
        f.X = Rand(0, _w);
        f.Y = anywhere ? Rand(0, _h) : Rand(-60, -6) * _s;
    }

    public void Update(float dt, IntPtr overlay, IntPtr tray)
    {
        if (_frame++ % (Wallpaper ? 2 : 3) == 0) UpdateWindows(overlay, tray);

        // The wind picks a new target every few seconds and eases towards it
        _windTimer -= dt;
        if (_windTimer <= 0) { _windTarget = Rand(-16, 16) * _s; _windTimer = Rand(5, 12); }
        _wind += (_windTarget - _wind) * MathF.Min(1f, dt * 0.35f);

        GetCursorPos(out POINT cur);
        _curDx = cur.X - _prevCur.X;
        _prevCur = cur;
        float mx = cur.X - _ox, my = cur.Y - _oy;
        float radius = MouseRadius * _s, radius2 = radius * radius;
        float damp = MathF.Exp(-2.2f * dt);
        float maxPush = 150f * _s;

        for (int i = 0; i < _count; i++)
        {
            ref Flake f = ref _flakes[i];
            float dx = f.X - mx, dy = f.Y - my;
            float d2 = dx * dx + dy * dy;
            if (d2 < radius2)
            {
                // Push away, stronger close to the cursor
                float d = MathF.Sqrt(d2) + 0.001f;
                float k = 1f - d / radius;
                float a = 850f * _s * k * k * dt / d;
                f.Pvx += dx * a; f.Pvy += dy * a;
                float pv = MathF.Sqrt(f.Pvx * f.Pvx + f.Pvy * f.Pvy);
                if (pv > maxPush) { f.Pvx *= maxPush / pv; f.Pvy *= maxPush / pv; }
            }

            f.Phase += f.SwayFreq * dt;
            float prevY = f.Y;
            f.X += (_wind * WindFactor[f.Layer] + f.SwayAmp * MathF.Sin(f.Phase) + f.Pvx) * dt;
            f.Y += (f.Vy + f.Pvy) * dt;
            f.Pvx *= damp; f.Pvy *= damp;

            if (f.X < -20) f.X += _w + 40; else if (f.X > _w + 20) f.X -= _w + 40;
            if (f.Y > _h + 8) { Spawn(ref f, false); continue; }

            if (f.Layer > 0 && f.Y > prevY && TryLand(f.X + _ox, prevY + _oy, f.Y + _oy))
                Spawn(ref f, false);
        }

        UpdateCaps(dt, cur);

        // Falling snow
        float gravity = 420f * _s, terminal = 480f * _s;
        for (int i = _debris.Count - 1; i >= 0; i--)
        {
            Debris p = _debris[i];
            float prevY = p.Y;
            p.Vy = MathF.Min(terminal, p.Vy + gravity * dt);
            p.X += (p.Vx + _wind * 0.5f) * dt;
            p.Y += p.Vy * dt;
            p.Vx *= damp;
            bool done;
            if (p.Mass > 0) done = p.Y > _h + 8 || LandDebris(in p, prevY);     // settles on whatever is below
            else { p.Life -= dt; done = p.Life <= 0 || p.Y > _h + 8; }
            if (done)
            {
                _debris[i] = _debris[^1];
                _debris.RemoveAt(_debris.Count - 1);
            }
            else _debris[i] = p;
        }
    }

    // --- Resting snow ---

    static bool InVisible(Cap c, float x)
    {
        var v = c.Vis;
        for (int i = 0; i + 1 < v.Count; i += 2)
            if (x >= v[i] && x < v[i + 1]) return true;
        return false;
    }

    bool TryLand(float sx, float prevSy, float sy)
    {
        foreach (Cap c in _caps.Values)
        {
            if (c.N == 0 || c.Settle > 0) continue;
            int top = c.Rect.T;
            if (sy < top - c.Limit - 1 || prevSy > top) continue;
            int k = (int)((sx - c.X0) / c.Bw);
            if (k < 0 || k >= c.N || !InVisible(c, sx)) continue;
            float surface = top - c.H[k];
            if (prevSy < surface && sy >= surface)
            {
                if (_rng.NextDouble() > LandChance) return false;
                // Small bump where the flake lands
                for (int j = Math.Max(0, k - 3); j <= Math.Min(c.N - 1, k + 3); j++)
                {
                    float dd = j - k;
                    float limit = c.Ground ? c.Limit : c.MaxH[j] * 1.12f;
                    c.H[j] = MathF.Min(limit, c.H[j] + 0.8f * _s * MathF.Exp(-dd * dd / 3f));
                }
                c.Dirty = true;
                return true;
            }
        }
        return false;
    }

    // Drops the snow on the first surface it hits (a window or the ground).
    bool LandDebris(in Debris p, float prevY)
    {
        float sx = p.X + _ox, sy = p.Y + _oy, psy = prevY + _oy;
        foreach (Cap c in _caps.Values)
        {
            if (c.N == 0 || c.Hwnd == p.Skip) continue;
            int top = c.Rect.T;
            if (sy < top - c.Limit - 1 || psy > top) continue;
            int k = (int)MathF.Floor((sx - c.X0) / c.Bw);
            if (k < 0 || k >= c.N || !InVisible(c, sx)) continue;
            float surface = top - c.H[k];
            if (psy <= surface && sy >= surface)
            {
                float dh = p.Mass / c.Bw;
                int a = Math.Max(0, k - 1), b = Math.Min(c.N - 1, k + 1);
                c.H[a] = MathF.Min(c.Limit, c.H[a] + dh * 0.25f);
                c.H[k] = MathF.Min(c.Limit, c.H[k] + dh * 0.5f);
                c.H[b] = MathF.Min(c.Limit, c.H[b] + dh * 0.25f);
                c.Dirty = true; c.Unsettled = true;
                return true;
            }
        }
        return false;
    }

    // Slopes that are too steep slide sideways. Returns true if anything moved.
    static bool Relax(Cap c)
    {
        bool moved = false;
        float maxDiff = c.Bw * 0.8f;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int k = 0; k + 1 < c.N; k++)
            {
                float d = c.H[k] - c.H[k + 1];
                if (d > maxDiff) { float m = (d - maxDiff) * 0.5f; c.H[k] -= m; c.H[k + 1] += m; moved = true; }
                else if (d < -maxDiff) { float m = (-d - maxDiff) * 0.5f; c.H[k] += m; c.H[k + 1] -= m; moved = true; }
            }
        }
        return moved;
    }

    void UpdateCaps(float dt, POINT cur)
    {
        float grow = dt / CapFillSeconds;
        foreach (Cap c in _caps.Values)
        {
            if (c.N == 0) continue;
            if (c.Ground) { UpdateGround(c, dt, cur); continue; }
            if (c.Unsettled)
            {
                c.Unsettled = Relax(c);
                c.Dirty = true;
            }
            if (c.Settle > 0) { c.Settle -= dt; continue; }

            // Slow build-up with an uneven profile
            if (!c.Full)
            {
                bool any = false;
                for (int k = 0; k < c.N; k++)
                {
                    if (c.H[k] < c.MaxH[k]) { c.H[k] = MathF.Min(c.MaxH[k], c.H[k] + grow * c.MaxH[k]); any = true; }
                }
                c.Full = !any;
                c.DirtyTimer += dt;
                if (c.DirtyTimer > 0.3f) { c.DirtyTimer = 0; c.Dirty = true; }
            }

            // Moving the mouse over the edge knocks the snow off
            int top = c.Rect.T;
            if (cur.Y >= top - c.Limit - 6 * _s && cur.Y <= top + 4 * _s && InVisible(c, cur.X))
            {
                float reach = 16f * _s;
                int k0 = Math.Max(0, (int)((cur.X - reach - c.X0) / c.Bw));
                int k1 = Math.Min(c.N - 1, (int)((cur.X + reach - c.X0) / c.Bw));
                for (int k = k0; k <= k1; k++)
                {
                    if (c.H[k] < 0.35f * _s) continue;
                    float x = c.X0 + (k + 0.5f) * c.Bw;
                    // It isn't lost: it falls and lands further down
                    AddDebris(x - _ox, top - c.H[k] * 0.5f - _oy, (x - cur.X) * 2f + Rand(-12, 12) * _s, Rand(-25, 15) * _s,
                              c.H[k] * 0.95f * c.Bw, c.Hwnd);
                    c.H[k] *= 0.05f;
                    c.Dirty = true; c.Full = false; c.Unsettled = true;
                }
            }
        }
    }

    // The ground: snow builds up, the mouse pushes it around, piles slowly settle.
    void UpdateGround(Cap c, float dt, POINT cur)
    {
        float grow = dt / GroundFillSeconds, melt = 0.05f * _s * dt;
        bool slow = false, fast = false;
        for (int k = 0; k < c.N; k++)
        {
            if (c.H[k] < c.MaxH[k]) { c.H[k] = MathF.Min(c.MaxH[k], c.H[k] + grow * c.MaxH[k]); slow = true; }
            else if (c.H[k] > c.MaxH[k]) { c.H[k] = MathF.Max(c.MaxH[k], c.H[k] - melt); slow = true; }   // settling
        }

        int top = c.Rect.T;
        if (cur.Y >= top - c.Limit - 4 * _s && cur.Y <= top + 10 * _s && InVisible(c, cur.X))
        {
            float level = MathF.Max(0f, top - cur.Y - 3 * _s);     // only clear what's above the cursor
            float reach = 9f * _s;
            int k0 = Math.Max(0, (int)((cur.X - reach - c.X0) / c.Bw));
            int k1 = Math.Min(c.N - 1, (int)((cur.X + reach - c.X0) / c.Bw));
            int moving = Math.Sign(_curDx);
            for (int k = k0; k <= k1; k++)
            {
                float excess = c.H[k] - level;
                if (excess <= 0.2f * _s) continue;
                float x = c.X0 + (k + 0.5f) * c.Bw;
                int dir = moving != 0 ? moving : (x >= cur.X ? 1 : -1);
                c.H[k] = level;
                fast = true;

                // Pushed snow piles up in front of the cursor
                int kt = dir > 0 ? k1 + 1 + _rng.Next(0, 4) : k0 - 1 - _rng.Next(0, 4);
                for (int guard = 0; guard < 60 && excess > 0.01f && kt >= 0 && kt < c.N; guard++, kt += dir)
                {
                    float room = c.Limit - c.H[kt];
                    if (room <= 0) continue;
                    float put = MathF.Min(room, excess);
                    c.H[kt] += put;
                    excess -= put;
                }
                if (_rng.NextDouble() < 0.10)
                    AddDebris(x - _ox, top - level - _oy, dir * Rand(20, 70) * _s, -Rand(25, 70) * _s);   // a bit of powder
            }
        }

        if (Relax(c)) fast = true;
        c.Unsettled = false;

        if (fast) c.Dirty = true;
        else if (slow)
        {
            c.DirtyTimer += dt;
            if (c.DirtyTimer > 0.3f) { c.DirtyTimer = 0; c.Dirty = true; }
        }
    }

    void AddDebris(float x, float y, float vx, float vy, float mass = 0f, IntPtr skip = default)
    {
        if (_debris.Count >= MaxDebris) return;
        _debris.Add(new Debris
        {
            X = x, Y = y, Vx = vx, Vy = vy, Mass = mass, Skip = skip,
            Life = Rand(1.5f, 2.6f), R = Rand(0.7f, 1.5f) * _s, Op = Rand(0.45f, 0.7f),
        });
    }

    // Window moved. The snow doesn't follow the window, it stays where it was on screen.
    // If the window goes up or sideways, whatever is still above it stays, the rest falls.
    // If it goes down, everything falls and lands back on it.
    void Move(Cap c, in RECT r)
    {
        RECT old = c.Rect;
        int oldX0 = c.X0, oldN = c.N, oldBw = c.Bw;
        if (_scratch.Length < oldN) _scratch = new float[oldN + 64];
        Array.Copy(c.H, _scratch, oldN);
        bool down = r.T > old.T;

        Layout(c, r);
        for (int k = 0; k < oldN; k++)
        {
            float h = _scratch[k];
            if (h <= 0.05f) continue;
            float x = oldX0 + (k + 0.5f) * oldBw;
            if (!down)
            {
                int nk = (int)MathF.Floor((x - c.X0) / c.Bw);
                if (nk >= 0 && nk < c.N) { c.H[nk] = MathF.Min(c.Limit, c.H[nk] + h); continue; }   // still above the window
            }
            if (h > 0.3f * _s && InVisible(c, x))
                AddDebris(x - _ox, old.T - h * 0.5f - _oy, Rand(-4, 4) * _s, 0f, h * oldBw);
        }
        c.Unsettled = true;
    }

    // Window closed or minimized: all its snow falls.
    void DropAll(Cap c)
    {
        for (int k = 0; k < c.N; k++)
        {
            if (c.H[k] <= 0.3f * _s) continue;
            float x = c.X0 + (k + 0.5f) * c.Bw;
            if (InVisible(c, x))
                AddDebris(x - _ox, c.Rect.T - c.H[k] * 0.5f - _oy, Rand(-10, 10) * _s, Rand(-5, 20) * _s, c.H[k] * c.Bw, c.Hwnd);
        }
        Array.Clear(c.H);
        c.Full = false;
        c.Dirty = true;
    }

    void Layout(Cap c, in RECT r)
    {
        c.Rect = r;
        int inset = c.Ground ? 0 : (int)MathF.Round(7 * _s);   // Windows 11 rounded corners
        float peak = c.Ground ? _groundPx : _capPx;
        c.Limit = c.Ground ? _groundLimit : _capLimit;
        c.Bw = Math.Max(2, (int)MathF.Round(3 * _s));
        c.X0 = r.L + inset;
        int n = Math.Max(0, (r.W - 2 * inset) / c.Bw);
        if (n != c.N)
        {
            c.N = n;
            c.H = new float[n];
            c.MaxH = new float[n];
            // Three sine waves at different frequencies are enough for a natural-looking edge
            float p1 = Rand(0, MathF.Tau), p2 = Rand(0, MathF.Tau), p3 = Rand(0, MathF.Tau);
            float f1 = MathF.Tau / (90 * _s), f2 = MathF.Tau / (37 * _s), f3 = MathF.Tau / (13 * _s);
            for (int k = 0; k < n; k++)
            {
                float x = k * c.Bw;
                float noise = 0.5f + 0.28f * MathF.Sin(x * f1 + p1) + 0.15f * MathF.Sin(x * f2 + p2) + 0.07f * MathF.Sin(x * f3 + p3);
                c.MaxH[k] = peak * (0.42f + 0.58f * noise);
            }
        }
        else Array.Clear(c.H);
        c.Full = false;
        c.Settle = c.Ground ? 0f : 1.0f;      // no new snow while the window is moving
        c.Dirty = true;
    }

    void ReleaseGeos(Cap c)
    {
        for (int i = 0; i < c.Geos.Count; i++) { IntPtr g = c.Geos[i]; Gfx.ReleaseGeometry(ref g); }
        c.Geos.Clear();
    }

    void ClearCaps()
    {
        foreach (Cap c in _caps.Values) ReleaseGeos(c);
        _caps.Clear();
    }

    void Rebuild(Cap c)
    {
        ReleaseGeos(c);
        c.Dirty = false;
        if (c.N == 0) return;
        float baseY = c.Rect.T - _oy;
        float taperLen = 8f * _s;
        if (_pts.Length < c.N + 4) _pts = new Gfx.Point2F[c.N + 64];

        for (int i = 0; i + 1 < c.Vis.Count; i += 2)
        {
            int a = c.Vis[i], b = c.Vis[i + 1];
            int k0 = Math.Max(0, (a - c.X0) / c.Bw), k1 = Math.Min(c.N - 1, (b - c.X0) / c.Bw);
            int n = 0;
            float peak = 0;
            _pts[n++] = new Gfx.Point2F { X = a - _ox, Y = baseY };
            for (int k = k0; k <= k1; k++)
            {
                float x = c.X0 + (k + 0.5f) * c.Bw;
                if (x <= a || x >= b) continue;
                float t = c.Ground ? 1f : Math.Clamp(MathF.Min(x - a, b - x) / taperLen, 0f, 1f);
                float h = c.H[k] * t * t * (3f - 2f * t);      // rounded ends
                if (h > peak) peak = h;
                _pts[n++] = new Gfx.Point2F { X = x - _ox, Y = baseY - h };
            }
            _pts[n++] = new Gfx.Point2F { X = b - _ox, Y = baseY };
            if (peak < 0.3f || n < 3) continue;
            fixed (Gfx.Point2F* p = _pts)
            {
                IntPtr g = Gfx.CreatePolygon(p, n);
                if (g != IntPtr.Zero) c.Geos.Add(g);
            }
        }
    }

    // --- Window tracking ---

    [UnmanagedCallersOnly]
    static int EnumProc(IntPtr h, IntPtr l)
    {
        if (h == s_skip1 || h == s_skip2) return 1;
        if (IsWindowVisible(h) == 0 || IsIconic(h) != 0) return 1;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return 1;

        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_LAYERED) != 0)
        {
            if ((ex & WS_EX_TRANSPARENT) != 0) return 1;                                   // other click-through overlays
            if (GetLayeredWindowAttributes(h, out _, out byte alpha, out uint fl) != 0 && (fl & 2) != 0 && alpha == 0) return 1;
        }

        char* buf = stackalloc char[80];
        int len = GetClassNameW(h, buf, 80);
        var cls = new ReadOnlySpan<char>(buf, Math.Max(0, len));
        if (cls.SequenceEqual("Progman") || cls.SequenceEqual("WorkerW")) return 1;        // the desktop itself

        if (!FrameBounds(h, out RECT r) || r.W < 8 || r.H < 8) return 1;

        bool taskbar = cls.SequenceEqual("Shell_TrayWnd") || cls.SequenceEqual("Shell_SecondaryTrayWnd");
        bool host = taskbar
                    || (IsZoomed(h) == 0
                        && (ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) == 0
                        && r.W >= s_minW && r.H >= s_minH);
        s_wins.Add(new Win { H = h, R = r, Host = host, Ground = taskbar });
        return 1;
    }

    static void Subtract(List<int> src, List<int> dst, int a, int b)
    {
        dst.Clear();
        for (int i = 0; i + 1 < src.Count; i += 2)
        {
            int s = src[i], e = src[i + 1];
            if (b <= s || a >= e) { dst.Add(s); dst.Add(e); continue; }
            if (a > s) { dst.Add(s); dst.Add(a); }
            if (b < e) { dst.Add(b); dst.Add(e); }
        }
    }

    void UpdateWindows(IntPtr overlay, IntPtr tray)
    {
        s_skip1 = overlay; s_skip2 = tray;
        s_minW = (int)(140 * _s); s_minH = (int)(50 * _s);
        s_wins.Clear();
        EnumWindows(&EnumProc, IntPtr.Zero);       // in Z order, topmost first
        _gen++;

        int capStrip = (int)MathF.Ceiling(_capLimit) + 1, groundStrip = (int)MathF.Ceiling(_groundLimit) + 1;
        int room = (int)MathF.Ceiling(_capPx) + 1;
        int minSeg = (int)(18 * _s);

        for (int i = 0; i < s_wins.Count; i++)
        {
            Win w = s_wins[i];
            if (!w.Host) continue;
            int strip = w.Ground ? groundStrip : capStrip;
            // Window touching the top of the screen: no room for snow
            if (MonitorFromPoint(new POINT { X = (w.R.L + w.R.R) / 2, Y = w.R.T - room }, 0) == IntPtr.Zero) continue;

            if (!_caps.TryGetValue(w.H, out Cap c))
            {
                c = new Cap { Hwnd = w.H, Ground = w.Ground };
                _caps[w.H] = c;
                Layout(c, w.R);
            }
            else if (!c.Rect.SameAs(w.R))
            {
                Move(c, w.R);
            }
            c.Gen = _gen;

            // Remove the parts of the edge hidden by windows above.
            // In wallpaper mode the ground is hidden by any window.
            int last = Wallpaper && c.Ground ? s_wins.Count : i;
            List<int> cur = _tmpA, other = _tmpB;
            cur.Clear();
            if (c.N > 0) { cur.Add(c.X0); cur.Add(c.X0 + c.N * c.Bw); }
            for (int j = 0; j < last && cur.Count > 0; j++)
            {
                if (j == i) continue;
                RECT o = s_wins[j].R;
                if (o.T <= w.R.T && o.B > w.R.T - strip && o.R > w.R.L && o.L < w.R.R)
                {
                    Subtract(cur, other, o.L - 2, o.R + 2);
                    (cur, other) = (other, cur);
                }
            }
            other.Clear();
            for (int k = 0; k + 1 < cur.Count; k += 2)
                if (cur[k + 1] - cur[k] >= minSeg) { other.Add(cur[k]); other.Add(cur[k + 1]); }

            bool same = other.Count == c.Vis.Count;
            for (int k = 0; same && k < other.Count; k++) same = other[k] == c.Vis[k];
            if (!same)
            {
                c.Vis.Clear();
                c.Vis.AddRange(other);
                c.Dirty = true;
            }
        }

        // Windows that are gone (closed, minimized, maximized): their snow falls
        _gone.Clear();
        foreach (Cap c in _caps.Values) if (c.Gen != _gen) _gone.Add(c.Hwnd);
        foreach (IntPtr h in _gone)
        {
            Cap c = _caps[h];
            DropAll(c);
            ReleaseGeos(c);
            _caps.Remove(h);
        }
    }

    // --- Drawing ---

    // Wallpaper mode: 0 if the flake is in front of a window, with a short fade at the edge.
    float Visibility(float sx, float sy)
    {
        if (!Wallpaper) return 1f;
        float edge = 3f * _s, m = edge;
        for (int i = 0; i < s_wins.Count; i++)
        {
            RECT r = s_wins[i].R;
            float d = MathF.Max(MathF.Max(r.L - sx, sx - r.R), MathF.Max(r.T - sy, sy - r.B));
            if (d <= 0) return 0f;
            if (d < m) m = d;
        }
        return m / edge;
    }

    public bool Render(Gfx g, out bool occluded)
    {
        g.Begin();

        foreach (Cap c in _caps.Values)
        {
            if (c.Dirty) Rebuild(c);
            for (int i = 0; i < c.Geos.Count; i++) g.Fill(c.Geos[i], CapOpacity);
        }

        for (int i = 0; i < _count; i++)
        {
            ref Flake f = ref _flakes[i];
            float op = f.Op * Visibility(f.X + _ox, f.Y + _oy);
            if (op <= 0.01f) continue;
            if (f.Layer == 0)
            {
                // Cheap blur: a faint halo plus a small dot
                g.Dot(f.X, f.Y, f.R * 2.1f, op * 0.30f);
                g.Dot(f.X, f.Y, f.R * 0.9f, op * 0.75f);
            }
            else g.Dot(f.X, f.Y, f.R, op);
        }

        for (int i = 0; i < _debris.Count; i++)
        {
            Debris p = _debris[i];
            g.Dot(p.X, p.Y, p.R, p.Mass > 0 ? p.Op : p.Op * MathF.Min(1f, p.Life / 0.5f));
        }

        return g.End(out occluded);
    }

    // --- Fullscreen ---

    public static bool FullscreenAppActive(IntPtr overlay)
    {
        if (SHQueryUserNotificationState(out int state) == 0 && (state == 3 || state == 4)) return true;   // exclusive fullscreen game or presentation mode

        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == overlay || IsWindowVisible(fg) == 0) return false;

        char* buf = stackalloc char[80];
        int len = GetClassNameW(fg, buf, 80);
        var cls = new ReadOnlySpan<char>(buf, Math.Max(0, len));
        if (cls.SequenceEqual("Progman") || cls.SequenceEqual("WorkerW") || cls.StartsWith("Shell_")
            || cls.SequenceEqual("XamlExplorerHostIslandWindow") || cls.SequenceEqual("Windows.UI.Core.CoreWindow")
            || cls.SequenceEqual("MultitaskingViewFrame"))
            return false;

        if (!FrameBounds(fg, out RECT r)) return false;
        var mi = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        if (GetMonitorInfoW(MonitorFromWindow(fg, 2), ref mi) == 0) return false;
        RECT m = mi.rcMonitor;
        if (r.L > m.L || r.T > m.T || r.R < m.R || r.B < m.B) return false;

        // A plain maximized window (with its title bar) doesn't count
        long style = GetWindowLongPtr(fg, GWL_STYLE).ToInt64();
        bool maximizedWithCaption = IsZoomed(fg) != 0 && (style & WS_CAPTION) == WS_CAPTION;
        return !maximizedWithCaption;
    }
}
