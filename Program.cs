// Invertonator — v1.0
//
//   System-wide dark mode for Windows that websites cannot see.
//
//   Modes:   F6 cycles the SCREEN transform: Invert → Dim → Off (F5 = on/off)
//            Invert — full negative; STATIC media (images, thumbnails) and
//                     browser chrome get true-color holes. Video renders
//                     hue-inverted but SMOOTH (no capture machinery).
//            Dim    — out = 0.4·in: blacks stay black, whites go dark.
//                     THE WATCH MODE: true hues, zero hole machinery,
//                     works with DRM video. No holes.
//            Off    — identity
//
//   DESIGN NOTE (v1.0): video/player holes were removed deliberately.
//   The windowed magnifier re-captures + re-renders its source region
//   every refresh — a real per-frame CPU cost that makes video lag on
//   integrated graphics, unfixable by tuning. Product split:
//     Invert = reading/browsing mode (static holes only)
//     Dim    = watching mode (smooth, true-hue, DRM-safe)
//
//   PUMP:    dedicated thread @ 10ms, timeBeginPeriod(1) resolution,
//            Highest priority. All remaining holes are STATIC media.
//   WALK:    ONE unified UIA walk per cycle @ 25ms. Chrome rects are
//            CACHED (invalidated on window move/resize).
//   HOLES:   image (named only — ads are unnamed) · graphic ·
//            link (photo-shaped only) · player by name — NO, player and
//            video passes are DISABLED in v1.0 (see design note) ·
//            chrome containers
//   CLICKS:  native (WS_EX_LAYERED + WS_EX_TRANSPARENT hosts)
//   DUMP:    F9 → UIA tree → tree.txt (warm-up poke, 4000-node cap)
//   AT MODE: SPI_SETSCREENREADER at startup (Chromium completes its tree)
//
// Hotkeys: Ctrl+Alt+F5 invert on/off · F6 screen mode · F7 pierce ·
//          F8 quit · F9 tree dump
// NOTE: browsers need --force-renderer-accessibility for web-content holes.

using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AutomationElement = FlaUI.Core.AutomationElements.AutomationElement;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Invertonator;

internal static class Logger
{
    private static readonly string LogPath =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Invertonator", "log.txt");

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}\r\n");
        }
        catch { }
    }
}

internal static class Program
{
    private static Mutex? _single;

    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        _single = new Mutex(true, @"Local\Invertonator.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Invertonator is already running (check the tray).", "Invertonator");
            return;
        }
        GC.KeepAlive(_single);

        Application.Run(new InvertonatorContext());
    }
}

internal sealed class InvertonatorContext : ApplicationContext
{
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const uint HOTKEY_MODS = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
    private const int HK_TOGGLE = 1, HK_MODE = 2, HK_PIERCE = 3, HK_QUIT = 4, HK_DUMP = 5;
    private const uint VK_F5 = 0x74, VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78;

    private const uint SPI_SETSCREENREADER = 0x004B;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    private readonly MagEngine _engine = new();
    private HotkeyForm? _hotkeys;
    private NotifyIcon? _tray;
    private ToolStripMenuItem _miToggle = null!, _miMode = null!, _miPierce = null!;
    private readonly List<string> _failures = new();
    private MediaTracker? _tracker;

    public InvertonatorContext()
    {
        try { Init(); }
        catch (Exception ex)
        {
            Logger.Log("UNHANDLED during startup: " + ex);
            try { _engine.SetInvert(false); } catch { }   // never leave the user inverted
            MessageBox.Show(ex.Message, "Invertonator — startup crash");
            Environment.Exit(1);
        }
    }

    private void Init()
    {
        Logger.Log("=== starting Invertonator v1.0 ===");

        // Announce as assistive technology: Chromium checks the Windows
        // screen-reader flag and builds the COMPLETE web-content tree.
        SpiSetScreenReader(true);
        Logger.Log("startup: SPI_SETSCREENREADER set");

        if (!_engine.Initialize())
            Fatal("MagInitialize failed (Magnification.dll missing?).");
        if (!_engine.SetInvert(true))
            Fatal("Fullscreen color effect failed. Close Windows Magnifier (Win+Esc) if running.");
        Logger.Log("startup: screen mode OK");

        BuildTray();

        _hotkeys = new HotkeyForm();
        _hotkeys.HotkeyPressed += OnHotkey;

        TryRegister(HK_TOGGLE, VK_F5, "Ctrl+Alt+F5");
        TryRegister(HK_MODE,   VK_F6, "Ctrl+Alt+F6");
        TryRegister(HK_PIERCE, VK_F7, "Ctrl+Alt+F7");
        TryRegister(HK_QUIT,   VK_F8, "Ctrl+Alt+F8");
        TryRegister(HK_DUMP,   VK_F9, "Ctrl+Alt+F9");

        // Hotkey status: log always, dialog only on failure.
        if (_failures.Count > 0)
        {
            Logger.Log("startup report: FAILED " + string.Join("; ", _failures));
            MessageBox.Show(
                "Some hotkeys couldn't be registered (owned by another app):\n\n" +
                string.Join("\n", _failures) +
                "\n\nUse the tray menu instead, or quit the conflicting app and restart.",
                "Invertonator — hotkey conflict");
        }
        else
        {
            Logger.Log("startup report: all hotkeys registered OK");
        }

        _tracker = new MediaTracker(_engine);
        Logger.Log("startup: tracker running");

        UpdateUi();
        Logger.Log("startup: complete");
    }

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    private static void SpiSetScreenReader(bool on) =>
        SystemParametersInfo(SPI_SETSCREENREADER, on ? 1u : 0u, (IntPtr)(on ? 1 : 0),
            SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

    // Uses icon.ico if present next to the exe; falls back to the system
    // shield icon so the build never breaks over a missing asset.
    private static Icon LoadAppIcon()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch { }
        return SystemIcons.Shield;
    }

    private void BuildTray()
    {
        _miToggle = new ToolStripMenuItem("Invert: ON");
        _miToggle.Click += (_, _) => { _engine.ToggleInvert(); UpdateUi(); };

        _miMode = new ToolStripMenuItem("Screen mode: Invert");
        _miMode.Click += (_, _) => CycleMode();

        _miPierce = new ToolStripMenuItem("Pierce mode: OFF");
        _miPierce.Click += (_, _) => TogglePierce();

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitApp();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _miToggle, _miMode, _miPierce, new ToolStripSeparator(), exit
        });

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Invertonator",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => { _engine.ToggleInvert(); UpdateUi(); };
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HK_TOGGLE: _engine.ToggleInvert(); UpdateUi(); break;
            case HK_MODE:   CycleMode(); break;
            case HK_PIERCE: TogglePierce(); break;
            case HK_DUMP:   _tracker?.RequestDump(); break;
            case HK_QUIT:   ExitApp(); break;
        }
    }

    private void CycleMode()
    {
        _miMode.Text = "Screen mode: " + _engine.CycleScreenMode();
        UpdateUi();
    }

    private void TogglePierce()
    {
        bool hidden = _engine.SetPierce(!_engine.Pierce);
        _miPierce.Text = hidden ? "Pierce mode: ON (holes hidden)" : "Pierce mode: OFF";
    }

    private void UpdateUi()
    {
        _miToggle.Text = $"Invert: {(_engine.Inverted ? "ON" : "OFF")}  (Ctrl+Alt+F5)";
        _tray!.Text = $"Invertonator — {_engine.ModeName}";
    }

    private void TryRegister(int id, uint vk, string name)
    {
        try
        {
            _hotkeys!.Register(id, HOTKEY_MODS, vk);
            Logger.Log($"Registered {name} OK");
        }
        catch (Win32Exception ex)
        {
            _failures.Add($"{name}  (Win32 error {ex.NativeErrorCode})");
            Logger.Log($"FAILED {name}: Win32 error {ex.NativeErrorCode}");
        }
    }

    private void ExitApp()
    {
        _tracker?.Dispose();
        _tray!.Visible = false;
        _hotkeys?.Dispose();
        _engine.Dispose();
        Application.Exit();
    }

    private static void Fatal(string msg)
    {
        Logger.Log("FATAL: " + msg);
        MessageBox.Show(msg, "Invertonator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(1);
    }
}

// =====================================================================
// MediaTracker: ONE unified UIA walk per cycle → rects posted to UI thread
// =====================================================================
internal sealed class MediaTracker : IDisposable
{
    private sealed record Found(Rectangle Rect, int Area, string Desc, bool Dyn);

    private const int WALK_INTERVAL_MS = 25;     // 40Hz discovery cadence
    private const int MIN_SIZE = 48;
    private const int MAX_HOLES = 12;            // 4 chrome + up to 8 media
    private const double MAX_MEDIA_MONITOR_FRAC = 0.85;
    private const double MIN_AR = 0.15, MAX_AR = 6.5;
    private const int MIN_AREA_HARD = 96 * 96;
    private const int DUMP_MAX_NODES = 4000;
    private const string PLAYER_NAME = "YouTube Video Player";

    private static readonly string TreePath =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Invertonator", "tree.txt");

    private readonly MagEngine _engine;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _dumpRequested;

    private string _lastSignature = "";
    private int _lastHoleCount = -1;
    private IntPtr _lastHwnd = IntPtr.Zero;
    private readonly HashSet<string> _loggedRejects = new();

    // Chrome-rect cache: valid while the same browser window occupies the
    // same screen rect. Window move/resize → invalidate → re-collect once.
    private IntPtr _chromeHwnd = IntPtr.Zero;
    private Rectangle _chromeWinRect = Rectangle.Empty;
    private List<Rectangle> _chromeCache = new();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public MediaTracker(MagEngine engine)
    {
        _engine = engine;
        _ui = SynchronizationContext.Current!;
        var token = _cts.Token;
        Task.Run(() => WalkLoop(token), token);
    }

    public void RequestDump() => _dumpRequested = true;

    private async Task WalkLoop(CancellationToken token)
    {
        // COM UIA objects are thread-affine — created and used on THIS thread.
        using var automation = new UIA3Automation();

        while (!token.IsCancellationRequested)
        {
            if (_dumpRequested)
            {
                _dumpRequested = false;
                DumpTree(automation);
            }

            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd != _lastHwnd)
                {
                    _lastHwnd = hwnd;
                    _loggedRejects.Clear();   // new window → fresh ledger
                }

                if (hwnd == IntPtr.Zero)
                {
                    Post(Array.Empty<Found>());
                    await Task.Delay(WALK_INTERVAL_MS, token);
                    continue;
                }

                var root = automation.FromHandle(hwnd);

                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(MonitorFromWindow(hwnd, 1 /* MONITOR_DEFAULTTONEAREST */), ref mi);
                var monRect = new Rectangle(
                    mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top);

                // ---- MEDIA: one unified walk (classified inside Collect) ----
                var found = new List<Found>();
                var seen = new HashSet<string>();

                // v1.0: video + player elements are NOT collected (design
                // note in header) — their condition legs are omitted.
                var mediaCond = new OrCondition(
                    automation.ConditionFactory.ByControlType(ControlType.Image),
                    automation.ConditionFactory.ByControlType(ControlType.Hyperlink),
                    new PropertyCondition(automation.PropertyLibrary.Element.LocalizedControlType, "graphic"));

                Collect(found, seen, root, mediaCond, monRect);

                // Containment dedupe: rect inside another rect → redundant.
                var kept = new List<Found>();
                foreach (var f in found.OrderByDescending(x => x.Area))
                    if (!kept.Any(k => k.Rect.Contains(f.Rect)))
                        kept.Add(f);

                kept.Sort((a, b) => b.Area.CompareTo(a.Area));

                // ---- CHROME: cached; invalidated on window move/resize ----
                var winRect = root.BoundingRectangle;
                if (hwnd != _chromeHwnd || winRect != _chromeWinRect)
                {
                    _chromeHwnd = hwnd;
                    _chromeWinRect = winRect;
                    _chromeCache = CollectChrome(root, automation, monRect);
                }
                var chromeRects = _chromeCache.Where(r => monRect.IntersectsWith(r)).ToList();

                var mediaSlots = Math.Max(0, MAX_HOLES - chromeRects.Count);
                var final = kept.Take(mediaSlots).ToList();
                foreach (var cr in chromeRects)
                    final.Insert(0, new Found(cr, cr.Width * cr.Height, "[chrome] browser UI (chrome-hole)", Dyn: false));

                Post(final);
            }
            catch (Exception ex)
            {
                Logger.Log($"tracker error: {ex.Message}");
            }

            await Task.Delay(WALK_INTERVAL_MS, token);
        }
    }

    private void Post(IList<Found> items)
    {
        // RECTS ONLY — names excluded (hover previews churn at same rects).
        var signature = string.Join("|", items.Select(x => $"{x.Rect.X},{x.Rect.Y},{x.Rect.Width},{x.Rect.Height}"));
        if (signature == _lastSignature && items.Count == _lastHoleCount) return;
        _lastSignature = signature;

        if (items.Count != _lastHoleCount)
        {
            Logger.Log($"tracker: hole count {(_lastHoleCount < 0 ? "" : _lastHoleCount + " -> ")}{items.Count}");
            _lastHoleCount = items.Count;
        }
        foreach (var it in items)
            Logger.Log($"  hole {it.Rect.Width}x{it.Rect.Height} @({it.Rect.X},{it.Rect.Y})  {it.Desc}{(it.Dyn ? " [dyn]" : "")}");

        var rects = items.Select(x => x.Rect).ToList();
        var dyns  = items.Select(x => x.Dyn).ToList();
        _ui.Post(_ => _engine.SetHoles(rects, dyns), null);
    }

    private void Collect(List<Found> found, HashSet<string> seen, AutomationElement root,
        ConditionBase cond, Rectangle monRect)
    {
        var matches = root.FindAll(TreeScope.Descendants, cond);
        foreach (var el in matches)
        {
            try
            {
                var r = el.BoundingRectangle;
                if (r.Width < MIN_SIZE || r.Height < MIN_SIZE) continue;
                if (!monRect.IntersectsWith(r)) continue;

                string name = "", lct = "";
                try { name = el.Properties.Name.ValueOrDefault ?? ""; } catch { }
                try { lct = el.Properties.LocalizedControlType?.ValueOrDefault ?? ""; } catch { }
                var nameShort = name.Length > 40 ? name[..40] : name;

                // ---- classify once, from ground truth ----
                string tag;
                if (lct == "graphic") tag = "graphic-pass";
                else if (lct == "hyperlink" || lct == "link") tag = "link-pass";
                else tag = "image-pass";

                double ar = (double)r.Width / r.Height;
                if (ar < MIN_AR || ar > MAX_AR)
                { RejectLog($"AR {ar:F2} X", r, lct, nameShort); continue; }

                // Ads/decoration: content images carry alt text; unnamed
                // full-width images on YouTube are ad banners.
                if (tag == "image-pass" && string.IsNullOrEmpty(name))
                { RejectLog("image unnamed (ad/decor)", r, lct, nameShort); continue; }

                // Title links are text-shaped (AR 5–8); thumbnails are
                // photo-shaped (≤3). Never hole text.
                if (tag == "link-pass" && ar > 3.0)
                { RejectLog("link text-shaped", r, lct, nameShort); continue; }

                if (r.Width * r.Height > monRect.Width * monRect.Height * MAX_MEDIA_MONITOR_FRAC)
                { RejectLog($"too big (> {MAX_MEDIA_MONITOR_FRAC:P0} of monitor)", r, lct, nameShort); continue; }

                if (r.Width * r.Height < MIN_AREA_HARD)
                { RejectLog($"too small (< {MIN_AREA_HARD}px^2)", r, lct, nameShort); continue; }

                var key = $"{r.X},{r.Y},{r.Width},{r.Height}";
                if (!seen.Add(key)) continue;

                // v1.0: all remaining holes are static media → never dynamic.
                found.Add(new Found(r, r.Width * r.Height, $"[{lct}] \"{nameShort}\" ({tag})", false));
            }
            catch { /* element vanished mid-walk */ }
        }
    }

    // Browser chrome rects (tabs/toolbar/bookmarks/sidebar) — they get holes
    // so the UI shows TRUE colors while the page stays dark. A chrome-named
    // element INSIDE "Page container" is web content and is excluded.
    private List<Rectangle> CollectChrome(AutomationElement root, UIA3Automation automation, Rectangle monRect)
    {
        var rects = new List<Rectangle>();
        try
        {
            var pageRect = Rectangle.Empty;
            var pcMatches = root.FindAll(TreeScope.Descendants,
                automation.ConditionFactory.ByName("Page container"));
            foreach (var pc in pcMatches)
            {
                try
                {
                    var pr = pc.BoundingRectangle;
                    if (pr.Width > 0) { pageRect = pr; break; }
                }
                catch { }
            }

            var chromeCond = new OrCondition(
                automation.ConditionFactory.ByName("Top bar container"),
                automation.ConditionFactory.ByName("Navigation"),
                automation.ConditionFactory.ByName("Bookmarks bar"),
                automation.ConditionFactory.ByName("Browser sidebar"));

            var matches = root.FindAll(TreeScope.Descendants, chromeCond);
            foreach (var el in matches)
            {
                try
                {
                    var r = el.BoundingRectangle;
                    if (r.Width < 20 || r.Height < 20) continue;
                    if (!monRect.IntersectsWith(r)) continue;
                    if (pageRect != Rectangle.Empty && pageRect.Contains(r)) continue;
                    rects.Add(r);
                }
                catch { /* vanished mid-walk */ }
            }
        }
        catch { }
        return rects;
    }

    private void RejectLog(string reason, Rectangle r, string lct, string name)
    {
        var key = $"{reason}|{r.X},{r.Y},{r.Width},{r.Height}";
        if (!_loggedRejects.Add(key)) return;
        if (_loggedRejects.Count > 300) _loggedRejects.Clear();
        Logger.Log($"  rejected {reason}: {r.Width}x{r.Height} @({r.X},{r.Y}) [{lct}] \"{name}\"");
    }

    private void DumpTree(UIA3Automation automation)
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { Logger.Log("[tree] no foreground window"); return; }
            var root = automation.FromHandle(hwnd);

            // Warm-up: wake Chromium's renderer tree (same as tracker does).
            try
            {
                root.FindAll(TreeScope.Descendants,
                    automation.ConditionFactory.ByControlType(ControlType.Image));
                Thread.Sleep(1500);
            }
            catch { }

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromWindow(hwnd, 1), ref mi);
            var monRect = new Rectangle(
                mi.rcMonitor.Left, mi.rcMonitor.Top,
                mi.rcMonitor.Right - mi.rcMonitor.Left,
                mi.rcMonitor.Bottom - mi.rcMonitor.Top);

            int count = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var writer = new StreamWriter(TreePath, append: false);
            writer.WriteLine($"tree dump {DateTime.Now:HH:mm:ss} — window {R2S(root.BoundingRectangle)}");
            Walk(root, "");
            Logger.Log($"[tree] dump written to tree.txt ({count} nodes, {sw.ElapsedMilliseconds}ms)");

            void Walk(AutomationElement el, string indent)
            {
                if (count >= DUMP_MAX_NODES) return;
                Rectangle r = Rectangle.Empty;
                string lct = "?", name = "";
                try { r = el.BoundingRectangle; } catch { return; }
                try { lct = el.Properties.LocalizedControlType?.ValueOrDefault ?? "?"; } catch { }
                try { name = el.Properties.Name.ValueOrDefault ?? ""; } catch { }

                if (r.Width <= 0 || r.Height <= 0) return;
                if (!monRect.IntersectsWith(r)) return;

                count++;
                if (name.Length > 30) name = name[..30];
                writer.WriteLine($"[tree]{indent} [{lct}] \"{name}\" {r.Width}x{r.Height} @({r.X},{r.Y})");

                var kids = el.FindAllChildren();
                foreach (var k in kids)
                    Walk(k, indent + "  ");
            }
        }
        catch (Exception ex)
        {
            Logger.Log("[tree] dump failed: " + ex.Message);
        }
    }

    private static string R2S(Rectangle r) => $"{r.Width}x{r.Height} @({r.X},{r.Y})";

    public void Dispose() => _cts.Cancel();
}

// =====================================================================
// MagEngine: fullscreen transform (Invert/Dim/Off) + tiered hole pump
//   Pump @ 10ms, timeBeginPeriod(1), Highest priority.
//   v1.0: all holes are static media → refreshed on the STATIC cadence
//   (~2Hz). Dynamic tier retained in code for v1.1 experiments.
// =====================================================================
internal sealed class MagEngine : IDisposable
{
    public enum ScreenMode { Invert, Dim, Off }

    private sealed class Hole
    {
        public HostForm Host = null!;
        public IntPtr Mag;
        public MagSubclass Sub = null!;
        public RECT Source;
        public bool Dynamic;
    }

    private const string MagnifierClass = "Magnifier";

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    private const int WS_DISABLED = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020,
                      WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000,
                      WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 2;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private const int MAX_POOLED = 16;
    private const int PUMP_MS = 10;            // 100Hz budget (dynamic tier: v1.1)
    private const int STATIC_EVERY = 50;       // static holes refresh ~2Hz
    private const float DIM_LEVEL = 0.4f;

    private static readonly float[] InvertMatrix =
    {
        -1,  0,  0, 0, 0,
         0, -1,  0, 0, 0,
         0,  0, -1, 0, 0,
         0,  0,  0, 1, 0,
         1,  1,  1, 0, 1,
    };

    private static readonly float[] DimMatrix =
    {
        DIM_LEVEL, 0,        0,        0, 0,
        0,        DIM_LEVEL, 0,        0, 0,
        0,        0,        DIM_LEVEL, 0, 0,
        0,        0,        0,        1, 0,
        0,        0,        0,        0, 1,
    };

    private static readonly float[] Identity5x5 =
    {
        1,0,0,0,0,  0,1,0,0,0,  0,0,1,0,0,  0,0,0,1,0,  0,0,0,0,1,
    };

    private static readonly float[] Identity3x3 = { 1,0,0, 0,1,0, 0,0,1 };

    private bool _initialized;
    private ScreenMode _mode = ScreenMode.Invert;
    private bool _invertOn = true;
    private bool _pierce;
    private readonly List<Hole> _holes = new();
    private readonly object _holesLock = new();

    private Thread? _pumpThread;
    private volatile bool _pumpRun;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("Magnification.dll")] private static extern bool MagInitialize();
    [DllImport("Magnification.dll")] private static extern bool MagUninitialize();
    [DllImport("Magnification.dll")] private static extern bool MagSetFullscreenColorEffect(float[] fx);
    [DllImport("Magnification.dll")] private static extern bool MagSetWindowSource(IntPtr hwnd, RECT source);
    [DllImport("Magnification.dll")] private static extern bool MagSetWindowTransform(IntPtr hwnd, float[] transform);
    [DllImport("Magnification.dll")] private static extern bool MagSetColorEffect(IntPtr hwnd, float[] fx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string? windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint ms);

    public MagEngine()
    {
        StartPump();
    }

    public bool Inverted => _invertOn && _mode != ScreenMode.Off;
    public bool Pierce => _pierce;
    public string ModeName => _invertOn ? _mode.ToString() : "Off";

    public bool Initialize()
    {
        if (_initialized) return true;
        _initialized = MagInitialize();
        return _initialized;
    }

    private bool ApplyMatrix()
    {
        float[] m = !_invertOn || _mode == ScreenMode.Off ? Identity5x5
                  : _mode == ScreenMode.Dim              ? DimMatrix
                  :                                        InvertMatrix;
        return MagSetFullscreenColorEffect((float[])m.Clone());
    }

    public bool SetInvert(bool on)
    {
        _invertOn = on;
        var ok = ApplyMatrix();
        return ok;
    }

    public void ToggleInvert() => SetInvert(!_invertOn);

    public string CycleScreenMode()
    {
        _mode = _mode switch
        {
            ScreenMode.Invert => ScreenMode.Dim,
            ScreenMode.Dim    => ScreenMode.Off,
            _                 => ScreenMode.Invert,
        };
        ApplyMatrix();
        Logger.Log($"screen mode: {_mode}");
        return _mode.ToString();
    }

    public bool SetPierce(bool on)
    {
        _pierce = on;
        return true;
    }

    // ==== THE pump: 10ms ticks, tiered refresh, highest priority ====
    private void StartPump()
    {
        _pumpRun = true;
        _pumpThread = new Thread(() =>
        {
            // Thread.Sleep(16) jitters to 15–32ms without this — the hidden
            // cause of choppy video. 1ms resolution + Highest priority gives
            // the pump a real, stable cadence that wins scheduling fights
            // against the UIA walk.
            timeBeginPeriod(1);
            try
            {
                int tick = 0;
                while (_pumpRun)
                {
                    try
                    {
                        bool wantVisible = _invertOn && _mode == ScreenMode.Invert && !_pierce;
                        tick++;

                        Hole[] snapshot;
                        lock (_holesLock) snapshot = _holes.ToArray();

                        foreach (var h in snapshot)
                        {
                            if (!h.Host.IsHandleCreated) continue;

                            if (wantVisible && !h.Host.Visible)
                                ShowWindow(h.Host.Handle, SW_SHOWNOACTIVATE);
                            else if (!wantVisible && h.Host.Visible)
                                ShowWindow(h.Host.Handle, SW_HIDE);

                            if (wantVisible && (h.Dynamic || tick % STATIC_EVERY == 0))
                                MagSetWindowSource(h.Mag, h.Source);
                        }
                    }
                    catch { /* pool mutated mid-tick — next tick */ }
                    Thread.Sleep(PUMP_MS);
                }
            }
            finally
            {
                timeEndPeriod(1);
            }
        })
        { IsBackground = true, Priority = ThreadPriority.Highest };
        _pumpThread.Start();
    }

    // ================= holes =================

    /// Geometry + dynamic flags; visibility is the pump's job. UI thread.
    public void SetHoles(IList<Rectangle> rects, IList<bool>? dyn = null)
    {
        lock (_holesLock)
        {
            if (_mode != ScreenMode.Invert || !_invertOn)
            {
                ParkAll();
                return;
            }

            for (int i = 0; i < rects.Count && i < MAX_POOLED; i++)
            {
                Hole h;
                if (i < _holes.Count)
                {
                    h = _holes[i];
                }
                else
                {
                    var created = CreateHole();
                    if (created == null) break;
                    h = created;
                    _holes.Add(h);
                }

                h.Dynamic = dyn != null && i < dyn.Count && dyn[i];

                var r = rects[i];
                var src = new RECT { Left = r.X, Top = r.Y, Right = r.X + r.Width, Bottom = r.Y + r.Height };
                bool moved = src.Left != h.Source.Left || src.Top != h.Source.Top ||
                             src.Right != h.Source.Right || src.Bottom != h.Source.Bottom;

                if (moved)
                {
                    h.Source = src;
                    MoveWindow(h.Host.Handle, r.X, r.Y, r.Width, r.Height, true);
                    MoveWindow(h.Mag, 0, 0, r.Width, r.Height, true);
                    MagSetWindowSource(h.Mag, src);
                }
            }

            for (int i = rects.Count; i < _holes.Count; i++)
            {
                var h = _holes[i];
                if (h.Source.Right != 0 || h.Source.Bottom != 0)
                {
                    h.Source = default;
                    MoveWindow(h.Host.Handle, -10000, -10000, 8, 8, true);
                }
            }
        }
    }

    private void ParkAll()
    {
        foreach (var h in _holes)
        {
            if (h.Source.Right != 0 || h.Source.Bottom != 0)
            {
                h.Source = default;
                if (h.Host.IsHandleCreated)
                    MoveWindow(h.Host.Handle, -10000, -10000, 8, 8, true);
            }
        }
    }

    private Hole? CreateHole()
    {
        var host = new HostForm { Bounds = new Rectangle(-10000, -10000, 8, 8) };
        host.Show();

        SetLayeredWindowAttributes(host.Handle, 0, 255, LWA_ALPHA);

        var mag = CreateWindowEx(WS_EX_TRANSPARENT, MagnifierClass, null, WS_CHILD | WS_VISIBLE,
            0, 0, 8, 8, host.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (mag == IntPtr.Zero) { host.Dispose(); return null; }

        var sub = new MagSubclass();
        sub.AssignHandle(mag);

        MagSetWindowTransform(mag, (float[])Identity3x3.Clone());
        MagSetColorEffect(mag, (float[])InvertMatrix.Clone());   // permanently Invert

        return new Hole { Host = host, Mag = mag, Sub = sub, Source = default, Dynamic = false };
    }

    public void Dispose()
    {
        _pumpRun = false;
        try { _pumpThread?.Join(300); } catch { }
        MagSetFullscreenColorEffect((float[])Identity5x5.Clone());
        lock (_holesLock)
        {
            foreach (var h in _holes)
            {
                try { h.Sub.ReleaseHandle(); } catch { }
                try { h.Host.Dispose(); } catch { }
            }
            _holes.Clear();
        }
        if (_initialized) MagUninitialize();
    }

    // Host: layered + transparent → native click-through
    private sealed class HostForm : Form
    {
        public HostForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            Text = "Invertonator hole";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= WS_DISABLED;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)(-1); return; }
            base.WndProc(ref m);
        }
    }
}

internal sealed class MagSubclass : NativeWindow
{
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)(-1); return; }
        base.WndProc(ref m);
    }
}

internal sealed class HotkeyForm : Form
{
    public const int WM_HOTKEY = 0x0312;
    public event Action<int>? HotkeyPressed;

    public HotkeyForm()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        _ = Handle;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Register(int id, uint mods, uint vk)
    {
        if (!RegisterHotKey(Handle, id, mods, vk))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Hotkey id {id} is taken.");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) HotkeyPressed?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            for (int id = 1; id <= 5; id++) UnregisterHotKey(Handle, id);
        base.Dispose(disposing);
    }
}