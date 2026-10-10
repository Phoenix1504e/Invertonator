# Invertonator

**System-wide dark mode for Windows that websites cannot see.**

![Invertonator in action](screenshot.png)

**[⬇ Download the latest release](https://github.com/Phoenix1504e/Invertonator/releases/latest)** — single exe, no install, no .NET required.

## The problem

Dark Reader and every dark-mode extension work by **modifying the page** —
injected CSS, changed DOM, `prefers-color-scheme` signals. All of it is
observable by the website. A site can detect — and react to — your dark mode.

## The idea

Invertonator never touches the web layer.

It applies a color-inversion transform at the **OS display compositor** —
*after* the browser has rendered, *after* every API a page could query.
Your browser presents the byte-identical fingerprint of a plain light-mode
session. No injected CSS. No DOM changes. No signals. There is nothing
in the web layer to detect.

Then comes the trick: for every image on screen, Invertonator carves a
**hole** — a transparent overlay region running the *same inverse
transform*. Media passes through inversion twice and comes out in its
**original colors**, while everything around it stays dark.

> The macOS equivalent is Smart Invert, which relies on Apple-controlled
> framework integration unavailable on Windows. Invertonator reconstructs
> that capability from userland, using public APIs: the Windows
> Magnification API + UI Automation. Even privacy-hardened browsers
> (LibreWolf with `resistFingerprinting`, Tor Browser) cannot detect the
> dark mode — there is nothing in the web layer to detect.

## Modes

Press the mode hotkey (default `Ctrl+Alt+F6`) to cycle:

| Mode | What it does | Use it for |
|---|---|---|
| **Invert** | Full negative. Static media (images, thumbnails) and browser chrome get true-color holes. Video renders hue-inverted but smooth. | Browsing, reading |
| **Dim** | `out = dimLevel × in`: blacks stay black, everything else dims. No holes, zero overhead. | **Watching videos** — smooth, true colors, DRM-safe |
| **Off** | Identity | Daytime, screenshots |

## Features

- Fullscreen inversion — invisible to every website, by construction
- Self-canceling true-color holes over images and thumbnails
- Browser chrome (tabs, toolbars, sidebar) stays true-colored (Chromium browsers)
- Native click-through — media under holes is fully interactive
- Dim mode — blacks preserved, smooth video, DRM-safe
- Configurable hotkeys and settings (`settings.json`, auto-created)
- Tray menu, pierce mode, single-instance
- Built-in diagnostics: activity log + UIA tree dump

## Setup

**1.** Run `Invertonator.exe` — the screen inverts immediately.

**2.** Launch your browser with renderer accessibility enabled (this lets
Invertonator *see* media elements — it does not modify the browser):

```
chrome.exe  --force-renderer-accessibility
msedge.exe  --force-renderer-accessibility
opera.exe   --force-renderer-accessibility
```

**3.** Holes snap onto images as you browse.

## Configuration

All settings live in `settings.json`, **auto-created with defaults** next to
the exe on first run. Delete the file to reset.

```json
{
  "hotkeys": {
    "invert": "Ctrl+Alt+F5",
    "mode": "Ctrl+Alt+F6",
    "pierce": "Ctrl+Alt+F7",
    "quit": "Ctrl+Alt+F8",
    "dump": "Ctrl+Alt+F9"
  },
  "features": {
    "atMode": false,
    "dimLevel": 0.4
  }
}
```

- **Hotkeys** — modifiers `Ctrl`/`Shift`/`Alt`/`Win` + a key (`F1–F12`,
  `A–Z`, `0–9`, `NUM0–9`, `INSERT`, `DELETE`, `HOME`, `END`, `PGUP`, `PGDN`).
  Invalid or conflicting hotkeys are reported at startup; the tray menu
  always works regardless.
- **`atMode`** — announces the app as assistive technology system-wide.
  Leave off unless debugging; browsers respond to the launch flag instead.
- **`dimLevel`** — Dim mode brightness multiplier (`0.05–1.0`, default `0.4`).

## Default hotkeys

| Keys | Action |
|---|---|
| `Ctrl+Alt+F5` | Invert on/off |
| `Ctrl+Alt+F6` | Cycle screen mode (Invert → Dim → Off) |
| `Ctrl+Alt+F7` | Pierce mode (temporarily hide all holes) |
| `Ctrl+Alt+F8` | Quit (restores colors) |
| `Ctrl+Alt+F9` | Dump UIA tree to `%LOCALAPPDATA%\Invertonator\tree.txt` |

## Browser compatibility

| Browser | Media holes | Chrome holes | Notes |
|---|---|---|---|
| Chrome / Edge / Opera / Brave / Helium | ✅ | ✅ | Launch with the flag above |
| Firefox | ✅ | 🚧 v1.2 | Accessibility on by default |
| LibreWolf / Tor (RFP enabled) | ⚠️ | ⚠️ | Requires disabling `privacy.resistFingerprinting` — that flag resists Invertonator's accessibility queries (the dark mode itself still works, invisibly) |

## Building from source

```
git clone https://github.com/Phoenix1504e/Invertonator.git
cd Invertonator
dotnet build -c Release
```

Requires the .NET 8 SDK. Output: `bin/Release/net8.0-windows/Invertonator.exe`

## Known limitations

- **Video:** holes over playing video lag on integrated graphics (the
  magnifier re-captures its region every frame — a hard ceiling of this
  API). Video regions are therefore *not* holed — use **Dim mode** for
  video: smooth, true hues, DRM-safe.
- **Watch-page player** renders inverted in Invert mode — same reason, same
  answer (Dim mode).
- **Sidebar suggestion thumbnails** are not holed: YouTube exposes only the
  title link for sidebar cards (the thumbnail image is pruned from the
  accessibility tree), so there's no rect to preserve. Homepage grid holes
  work fully.
- **Popup menus** may render inverted.
- **DRM streams** (Netflix etc.) may capture black in hole mode — Dim mode
  handles them perfectly.

## How it compares

| | Dark Reader / extensions | NegativeScreen | macOS Smart Invert | **Invertonator** |
|---|---|---|---|---|
| Invisible to websites | ❌ | ✅ | ✅ | ✅ |
| Media in true colors | ✅ | ❌ | ✅ | ✅ |
| Works on Windows, any browser | ✅ | ✅ | ❌ | ✅ |
| Browser chrome preserved | ✅ | ❌ | ✅ | ✅ |
| No app cooperation needed | — | — | ❌ (Apple frameworks) | ✅ |

## Roadmap

- **v1.2** — Event-driven UIA (instant media discovery, near-zero idle
  CPU) · sidebar suggestion holes via richer tree access · Firefox
  chrome-hole support · auto-Dim on video detection
- **v2.0** — Research: compositor-level video handling (hardware overlay
  paths)

## Credits & thanks

- The [Magnification API](https://learn.microsoft.com/en-us/windows/win32/api/_magapi/) —
  the unsung hero
- [NegativeScreen](https://github.com/mlaily/NegativeScreen) — the pioneer
  of Windows screen inversion
- [FlaUI](https://github.com/FlaUI/FlaUI) — UI Automation for .NET
- Apple — for Smart Invert, the target we reverse-engineered from userland

## License

[MIT](LICENSE) — do whatever you want, just keep the notice.

---

*Built in one very long night of debugging, driven entirely by logs.*