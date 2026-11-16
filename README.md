# Seasons: desktop animations for Windows

Seasonal effects on the Windows desktop, a bit like what macOS does, but more low-key.
Free, open source, no ads, no internet connection.

| Season | Status |
|---|---|
| Winter: snow | available |
| Autumn: leaves | planned |
| Spring | planned |
| Summer | planned |

## Winter

Small snowflakes drift down slowly, pile up on top of your windows and on the taskbar, and you can
push them around with the mouse.

- Discreet flakes (light blue-grey, 1 to 4 px) on three depth layers, with a bit of wind.
- The mouse pushes flakes away as it passes, no clicking needed.
- Snow builds up in a thin layer on top of windows, and handles overlapping windows.
- Snow also builds up on the ground, on top of the taskbar. You can push it with the mouse to make piles.
- Moving the mouse over the top edge of a window knocks its snow off, and it lands further down.
- When you move a window, the snow behaves roughly like real snow: whatever is no longer supported
  falls, and if you drag the window down, the snow falls back onto it.
- Two display modes:
  - **Wallpaper only** (default): flakes stay behind your windows.
  - **On top of windows**: flakes fall in front of everything.
- Hides itself when an app goes fullscreen (games, videos).
- Clicks go straight through: it never gets in the way.

## Usage

A snowflake icon shows up next to the clock (it may be hidden behind the small `^` arrow).
Click it to open the menu:

- **Pause**: hides the effect without closing the app
- **Display**: wallpaper only, or on top of windows
- **Density**: low or medium
- **Start with Windows**
- **Quit**

Settings are remembered between runs.

## Installation

### Option 1: download the .exe

A ready-to-use `Seasons.exe` is available in the **Releases** section of this repository.
It's a single file with nothing to install: put it anywhere and double-click it.

### Option 2: build it yourself

You need the .NET 8 SDK (free, from Microsoft):

```
winget install Microsoft.DotNet.SDK.8
```

Then, in the project folder:

```
dotnet publish -c Release -o dist
```

or just double-click `build.bat`. The app ends up in `dist\Seasons.exe`.

The first build needs internet access to download the .NET components.
The app itself never connects to anything.

## Windows blocks the app?

That's expected. The .exe isn't signed (code signing certificates are expensive), so Windows doesn't
know who made it and plays it safe.

**SmartScreen** ("Windows protected your PC"): click **More info**, then **Run anyway**.

**Smart App Control** ("Smart App Control blocked an app that may be unsafe"): there is no "run anyway"
button. Turn it off just long enough to launch Seasons once, then turn it back on:

1. Install pending Windows updates first (on older builds, Smart App Control can't be turned back on
   without resetting the PC)
2. Open **Windows Security** → **App & browser control** → **Smart App Control settings**
3. Select **Off**
4. Launch `Seasons.exe`
5. Go back to the same place and set Smart App Control back to **On**

Seasons keeps working afterwards with Smart App Control turned on.

If it ever gets blocked again (after a Windows update, for example), just repeat these steps.

> Don't turn Smart App Control off through the registry: it can end up blocking all your apps.

### VirusTotal says it's a trojan

A few antivirus engines (usually 3 or 4 out of about 70) flag the .exe with generic names such as
`Malicious` or `confidence_60%`. These are common false positives for recent, unsigned .NET apps that
bundle their runtime into a single file.

If in doubt, the easiest thing is to read the code (it's short) and build the .exe yourself.

## Customizing

The visual settings are constants at the top of `Snow.cs`:

| Constant | What it does |
|---|---|
| `SnowColor` | flake color (`#D6DCE6` by default) |
| `MouseRadius` | how far the mouse pushes flakes away |
| `CapMax` / `CapFillSeconds` | thickness and build-up speed on windows |
| `GroundMax` / `GroundFillSeconds` | thickness and build-up speed on the ground |
| `GroundPile` | max height of the piles you make with the mouse |

Rebuild after changing them.

## How it works

- C# / .NET 8, no external dependencies.
- A transparent fullscreen window, always on top, that lets clicks through.
- Rendering with Direct2D + DirectComposition (the GPU does the work, the CPU stays mostly idle).
- Window positions come from `EnumWindows` and `DwmGetWindowAttribute`.

| File | Contents |
|---|---|
| `Program.cs` | startup, tray icon and menu, render loop |
| `Snow.cs` | the snow (winter) |
| `Gfx.cs` | Direct2D rendering |
| `Native.cs` | Windows API calls |

## Known limitations

- Made for Windows 10 / 11, 64-bit.
- With several monitors at different scaling levels, flakes won't be exactly the same size everywhere.
- Fullscreen detection only looks at the active window.
- In wallpaper mode, a maximized window hides all the snow, including the snow on the ground.

## License

MIT, see [LICENSE](LICENSE).

## Disclaimer

This is a personal hobby project, provided as is, without any warranty.
Use it at your own risk: the author is not responsible for any damage, data loss
or other issue caused by using it. See the [LICENSE](LICENSE) for the full terms.
