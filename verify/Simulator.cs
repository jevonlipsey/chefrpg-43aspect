using System;
using System.Collections.Generic;
using System.Linq;

namespace ChefRPG43.Verify;

/// numeric twin of the runtime path. it uses the same integer zoom and aspect-fit
/// formulas as urp's PixelPerfectCameraInternal and the same 4:3 matte as the
/// patched LetterBoxer, then runs the hud containment cadence for 100 frames and a
/// scene transition. no unity types, so the numbers are exact and reviewable.
///
/// the display half is different: it drives ChefRPG43SafeArea.NextRequest / Coerce43 /
/// Modes43 from DisplayPolicy.cs, the very file that ships in the game assembly, against
/// a small engine model (end-of-frame resolution changes, a container that clamps to
/// 16:9 modes, prefs that survive a kill). only the thin unity wrappers around those
/// functions (state, SetResolution, PlayerPrefs) are modelled here rather than compiled.
public sealed class Simulator
{
    private const double ASPECT = 4.0 / 3.0;
    private const int ASSETS_PPU = 16;

    private readonly Checks checks;

    public Simulator(Checks checks)
    {
        this.checks = checks;
    }

    private sealed record Resolution(string Name, int W, int H, int SysW, int SysH);

    private static readonly Resolution[] Cases =
    {
        new("droiddeck 1280x960 (4:3)", 1280, 960, 1280, 960),
        new("mac points 2056x1329 (16:10)", 2056, 1329, 2056, 1329),
        new("retina 4112x2580 (1.594)", 4112, 2580, 2056, 1290),
        new("mac backing 4112x2658 (16:10)", 4112, 2658, 2056, 1329),
        new("window 1280x720 (16:9)", 1280, 720, 1280, 720),
        new("fullhd 1920x1080 (16:9)", 1920, 1080, 1920, 1080),
        new("ultrawide 3440x1440 (21:9)", 3440, 1440, 3440, 1440),
        new("4:3 1024x768", 1024, 768, 1024, 768),
    };

    public void Run()
    {
        checks.Section("camera simulation");
        foreach (var c in Cases)
        foreach (var (rw, rh) in new[] { (640, 480), (800, 600) })
            CheckCamera(c, rw, rh);

        checks.Section("hud containment simulation");
        foreach (var c in Cases)
            CheckHud(c);

        checks.Section("hud resize round-trip and y containment");
        CheckHudRoundTrip("quick 960 -> 720 -> 960 (frames 30/60)", 60);
        CheckHudRoundTrip("long 960 -> 720 -> 960 (frames 30/120)", 120);
        CheckHudRoundTrip("mac 1329 -> 720 -> 1329", 120, baseW: 2056, baseH: 1329);
        CheckYContainment(1280, 960);
        CheckYContainment(2056, 1329);

        checks.Section("fullscreen retry simulation");
        CheckRetry("4:3 panel", 1280, 960, 1280, 960);
        CheckRetry("4:3 windowed", 1280, 960, 1280, 960, fullScreen: false);
        CheckRetry("retina 16:10", 4112, 2658, 2056, 1329);
        CheckRetry("wide window asks for native 4:3", 1280, 720, 1280, 960);
        CheckRetry("fullhd window asks for native 4:3", 1920, 1080, 1280, 960);
        CheckRetry("wide window reverts to 720", 1280, 720, 1280, 960, revertAt: 240);

        checks.Section("display policy simulation");
        CheckPolicyFunctions();
        CheckBootPersisted720();
        CheckWindowedStaysWindowed();
        CheckUserResize();
        CheckContainerClamp();
        CheckMacFullscreen();
        CheckKillAndReboot();
        CheckModes43();
    }

    private void CheckCamera(Resolution c, int rw, int rh)
    {
        var lb = LetterBoxerRect(c.W, c.H);
        var ppc = PixelPerfect(c.W, c.H, rw, rh);

        double lbW = lb.width * c.W;
        double lbH = lb.height * c.H;
        double lbX = lb.x * c.W;
        double lbY = lb.y * c.H;
        string tag = $"{c.Name} ref {rw}x{rh}";

        checks.Check(Math.Abs(ppc.FinalW / ppc.FinalH - ASPECT) < 1e-6,
            $"{tag}: pixel-perfect view is 4:3 ({ppc.FinalW:F2}x{ppc.FinalH:F2})");
        checks.Check(Math.Abs(lbW / lbH - ASPECT) < 1e-6,
            $"{tag}: LetterBoxer rect is 4:3 ({lbW:F1}x{lbH:F1})");
        checks.Check(Math.Abs(lbW - ppc.FinalW) <= 1.001 && Math.Abs(lbH - ppc.FinalH) <= 1.001,
            $"{tag}: LetterBoxer and pixel-perfect agree within 1px");
        checks.Check(ppc.X >= 0 && ppc.Y >= 0 && ppc.X + ppc.FinalW <= c.W + 0.001 && ppc.Y + ppc.FinalH <= c.H + 0.001,
            $"{tag}: view rect lies inside the screen");
        checks.Check(Math.Abs(ppc.TopBar) <= 0.5 && Math.Abs(ppc.BottomBar) <= 0.5,
            $"{tag}: no top/bottom cinematic bar over the 4:3 view");
        checks.Check(Math.Abs(lbX - ppc.X) <= 1.001 && Math.Abs(lbY - ppc.Y) <= 1.001,
            $"{tag}: centered at the same origin");
        checks.Check(ppc.OrthoSize > 0, $"{tag}: orthographic size positive ({ppc.OrthoSize:F4})");
        checks.Check(ppc.OffscreenW > 0 && ppc.OffscreenH > 0,
            $"{tag}: offscreen render target valid ({ppc.OffscreenW}x{ppc.OffscreenH})");
    }

    private void CheckHud(Resolution c)
    {
        var run = new HudRun(c.W, c.H, sceneChangeAt: 50, frames: 100);
        run.Execute();

        var clock = run.Get("clock");
        var left = run.Get("prompt-left");
        var right = run.Get("prompt-right");
        var tooltip = run.Get("tooltip");
        string tag = c.Name;

        checks.Check(run.Inside(clock, run.ViewLeft, run.ViewRight) &&
                     run.Inside(left, run.ViewLeft, run.ViewRight) &&
                     run.Inside(right, run.ViewLeft, run.ViewRight),
            $"{tag}: pinned clock and side prompts inside 4:3 by frame 5");
        checks.Check(run.Inside(clock, run.ViewLeft, run.ViewRight) &&
                     run.Inside(left, run.ViewLeft, run.ViewRight) &&
                     run.Inside(right, run.ViewLeft, run.ViewRight),
            $"{tag}: pinned hud stays inside after a scene transition");
        checks.Check(run.Inside(tooltip, run.ViewLeft, run.ViewRight),
            $"{tag}: edge tooltip pulled inside after the two-scan gate");
        checks.Check(run.SteadyFrameWrites(82, 99) == 0,
            $"{tag}: zero canvas writes per frame once settled (frames 82-99)");
        checks.Check(run.MaxWritesInFrame <= 6,
            $"{tag}: bounded rect writes in any single frame ({run.MaxWritesInFrame})");
        checks.Check(run.BackgroundWrites == 0,
            $"{tag}: full-width fade/matte backgrounds are never moved");
        checks.Check(run.MaxShiftsOf("clock") <= 2 && run.MaxShiftsOf("prompt-left") <= 2 && run.MaxShiftsOf("prompt-right") <= 2,
            $"{tag}: pinned elements are not rewritten every frame");
        // a scene load replaces the hud objects; the dead-reference window re-resolves
        // on the next frame, so containment lands by the frame after activation. the
        // 30-frame scene poll and 60-frame window are the slow paths, still well
        // inside "no visible drift".
        checks.Check(run.SceneChangeInsideByFrame(51, 60),
            $"{tag}: hud is contained within 10 frames of a scene transition");

        var formulas = FormulaMap(c.W, c.H);
        checks.Check(Math.Abs(formulas.cornerX - run.ViewLeft) <= 1.0,
            $"{tag}: Formulas corner offset matches the 4:3 view left edge");
        checks.Check(Math.Abs(formulas.cornerY) <= 0.001,
            $"{tag}: Formulas has no vertical offset on a wide screen");
        checks.Check(formulas.MapsViewEdges(run.ViewLeft, run.ViewRight),
            $"{tag}: Formulas maps the 4:3 view edges onto the design frame");
    }

    // the device log showed 1280x960 -> 1280x720 -> 1280x960. during the 720 phase the
    // old helper shifted hud rects by -95..-160px and never gave them back, so the hud sat
    // displaced (clock cut off) once the frame returned to 4:3. the run here must end in
    // exactly the state an undisturbed run reaches, and the 720 phase must really have
    // displaced something or the test proves nothing.
    private void CheckHudRoundTrip(string name, int backAt, int baseW = 1280, int baseH = 960)
    {
        const int frames = 260;
        int midH = 720;
        var baseline = new HudRun(baseW, baseH, sceneChangeAt: -1, frames: frames);
        baseline.Execute();
        var run = new HudRun(baseW, baseH, sceneChangeAt: -1, frames: frames,
            resizes: new[] { (30, 1280, midH), (backAt, baseW, baseH) });
        run.Execute();

        checks.Check(run.MaxDisplacementBetween(35, backAt - 1) > 1.0,
            $"{name}: the 720 phase displaces the hud (max {run.MaxDisplacementBetween(35, backAt - 1):F0} canvas px), so the round-trip is a real test");
        var diffs = run.OwnDifferences(baseline);
        checks.Check(diffs.Count == 0,
            diffs.Count == 0
                ? $"{name}: every element ends at the position an undisturbed run reaches"
                : $"{name}: elements left displaced after the round-trip: {string.Join(", ", diffs.Take(6))}");
        var clock = run.Get("clock");
        var left = run.Get("prompt-left");
        var right = run.Get("prompt-right");
        checks.Check(run.InsideScreen(clock) && run.InsideScreen(left) && run.InsideScreen(right),
            $"{name}: clock and prompts end inside [0,w]x[0,h]");
        checks.Check(run.Inside(clock, run.ViewLeft, run.ViewRight) &&
                     run.Inside(left, run.ViewLeft, run.ViewRight) &&
                     run.Inside(right, run.ViewLeft, run.ViewRight),
            $"{name}: clock and prompts end inside the 4:3 view");
        checks.Check(run.SteadyFrameWrites(frames - 60, frames - 1) == 0,
            $"{name}: zero canvas writes per frame once settled again");
    }

    private void CheckYContainment(int w, int h)
    {
        var run = new HudRun(w, h, sceneChangeAt: -1, frames: 120, withEdgeItems: true);
        run.Execute();
        var banner = run.Get("banner");
        var footer = run.Get("footer");
        checks.Check(run.InsideY(banner) && run.InsideY(footer),
            $"{w}x{h}: elements authored past the top and bottom edge are pulled inside the window");
        checks.Check(run.MaxShiftsOf("banner") <= 2 && run.MaxShiftsOf("footer") <= 2,
            $"{w}x{h}: y containment settles in at most two shifts per element");
        checks.Check(run.SteadyFrameWrites(90, 119) == 0,
            $"{w}x{h}: y containment is quiet once settled");
    }

    private void CheckRetry(string name, int screenW, int screenH, int sysW, int sysH,
        bool fullScreen = true, int revertAt = -1)
    {
        var run = new RetryRun(screenW, screenH, sysW, sysH, frames: 400, fullScreen, revertAt);
        run.Execute();
        int expected = 0;
        if (!fullScreen) expected++;
        if (Math.Abs((double)screenW / screenH - (double)sysW / sysH) > 0.005) expected++;
        if (revertAt >= 0) expected++;
        checks.Check(run.Requests.Count == expected,
            $"{name}: asks for the native size {expected} time(s) ({run.Requests.Count})");
        checks.Check(run.Requests.All(r => r.W == sysW && r.H == sysH),
            $"{name}: retry only requests the native display size");
        checks.Check(run.Requests.All(r => (double)r.W / r.H <= 1.78 + 1e-6),
            $"{name}: retry never selects a wider-than-16:9 mode");
    }

    private static LetterBox LetterBoxerRect(int w, int h)
    {
        double ratio = 4.0 / 3.0;
        double fit = (double)w / h / ratio;
        if (fit < 1.0)
            return new LetterBox(0, (1 - fit) / 2, 1, fit);
        double width = 1.0 / fit;
        return new LetterBox((1 - width) / 2, 0, width, 1);
    }

    private static PpcResult PixelPerfect(int w, int h, int rw, int rh)
    {
        int zoom = Math.Max(1, Math.Min(h / rh, w / rw));
        int offW = zoom * rw;
        int offH = zoom * rh;
        double screenAspect = (double)w / h;
        double refAspect = (double)rw / rh;
        double finalW, finalH, x, y;
        if (screenAspect > refAspect)
        {
            finalH = h;
            finalW = h * refAspect;
            x = (w - (int)finalW) / 2.0;
            y = 0;
        }
        else
        {
            finalW = w;
            finalH = w / refAspect;
            y = (h - (int)finalH) / 2.0;
            x = 0;
        }
        double ortho = rh * 0.5 / ASSETS_PPU;
        return new PpcResult(zoom, offW, offH, x, y, finalW, finalH, ortho,
            (h - finalH) / 2.0, (h - finalH) / 2.0);
    }

    // mirrors the patched Formulas il exactly: GetActualScreenSize returns
    // (h*4/3, h); LocateScreenPosition subtracts the pillar offset on wide screens
    // and the letterbox offset on narrow ones; GetCornerOffset returns that offset.
    private static FormulaResult FormulaMap(int w, int h) => new(w, h);

    private sealed record LetterBox(double x, double y, double width, double height);
    private sealed record PpcResult(int Zoom, int OffscreenW, int OffscreenH, double X, double Y,
        double FinalW, double FinalH, double OrthoSize, double TopBar, double BottomBar);

    private sealed class FormulaResult
    {
        private const double LIMIT = 4.0 / 3.0 + 1e-6;

        private readonly int w, h;

        public double cornerX { get; }
        public double cornerY { get; }

        public FormulaResult(int w, int h)
        {
            this.w = w;
            this.h = h;
            double actualW = ActualW();
            bool narrow = (double)w / h < LIMIT;
            cornerX = narrow ? 0 : (w - actualW) / 2;
            cornerY = narrow ? (h - h) / 2 : 0;
        }

        private double ActualW() => h * 4.0 / 3.0;

        public double LocateX(double x)
        {
            if ((double)w / h > LIMIT) x -= (w - ActualW()) / 2;
            return x;
        }

        public bool MapsViewEdges(double viewLeft, double viewRight)
        {
            double left = LocateX(viewLeft);
            double right = LocateX(viewRight);
            if ((double)w / h > LIMIT)
                return Math.Abs(left) <= 1e-6 && Math.Abs(right - ActualW()) <= 1e-6;
            return Math.Abs(left - viewLeft) <= 1e-6 && Math.Abs(right - viewRight) <= 1e-6;
        }
    }

    // ------------------------------------------------------------------------------
    // display policy: the shipped DisplayPolicy.cs against an engine model
    // ------------------------------------------------------------------------------

    private static readonly (int W, int H)[] Container16x9 =
        { (720, 400), (800, 450), (1024, 576), (1152, 648), (1280, 720) };

    /// the engine and the container around the game. SetResolution only takes effect at
    /// the end of the frame (Screen.width/height are stale until then), the container
    /// answers the first few requests with a 16:9 mode it already exposes, and the
    /// Screenmanager prefs are only written by Persist: a killed session writes nothing.
    private sealed class EngineModel
    {
        public readonly int SysW, SysH;
        public int ScreenW, ScreenH;
        public bool IsFull;
        public int PrefW, PrefH, PrefMode;
        public int Clamped;                       // requests the container still answers with 16:9
        public readonly List<(int W, int H, int Mode, int Frame, string Source)> Requests = new();
        private (int W, int H, int Mode)? pending;

        public EngineModel(int sysW, int sysH, int prefW, int prefH, int prefMode, int clamped = 0)
        {
            SysW = sysW; SysH = sysH;
            PrefW = prefW; PrefH = prefH; PrefMode = prefMode;
            Clamped = clamped;
            // unity restores the persisted size and mode at boot
            ScreenW = prefW; ScreenH = prefH; IsFull = prefMode != ChefRPG43SafeArea.MODE_WINDOWED;
        }

        public void SetResolution(int w, int h, int mode, int frame, string source)
        {
            Requests.Add((w, h, mode, frame, source));
            pending = (w, h, mode);
        }

        public void Persist(int w, int h, int mode)
        {
            PrefW = w; PrefH = h; PrefMode = mode;
        }

        public void EndFrame()
        {
            if (pending is not { } p) return;
            pending = null;
            int w = p.W, h = p.H;
            if (Clamped > 0)
            {
                Clamped--;
                (w, h) = Container16x9.Where(m => m.H <= p.H).DefaultIfEmpty(Container16x9[0]).Last();
            }
            else
            {
                if (w > SysW) w = SysW;
                if (h > SysH) h = SysH;
            }
            ScreenW = w; ScreenH = h;
            IsFull = p.Mode != ChefRPG43SafeArea.MODE_WINDOWED;
        }
    }

    /// the unity wrappers around the policy, as the game runs them: ApplyFullscreen /
    /// ApplyWindowed from the settings panel, DisplayWatchdog from StartMenuManager.Awake
    /// and LetterBoxer.Update, LoadSettings at the title scene.
    private sealed class GameModel
    {
        private readonly EngineModel engine;
        private bool wantWindowed;
        private int wantW, wantH, gap;
        private bool wantInit;
        private int persistW, persistH, persistMode;
        public int Frame;

        public GameModel(EngineModel engine) { this.engine = engine; }

        public void ApplyFullscreen(string source)
        {
            wantInit = true;
            wantWindowed = false;
            wantW = engine.SysW; wantH = engine.SysH;
            engine.SetResolution(engine.SysW, engine.SysH, ChefRPG43SafeArea.MODE_FULLSCREEN_WINDOW, Frame, source);
            PersistIfChanged(engine.SysW, engine.SysH, ChefRPG43SafeArea.MODE_FULLSCREEN_WINDOW);
        }

        public void ApplyWindowed(int w, int h, string source)
        {
            ChefRPG43SafeArea.Coerce43(w, h, engine.SysW, engine.SysH, out int cw, out int ch);
            wantInit = true;
            wantWindowed = true;
            wantW = cw; wantH = ch;
            engine.SetResolution(cw, ch, ChefRPG43SafeArea.MODE_WINDOWED, Frame, source);
            PersistIfChanged(cw, ch, ChefRPG43SafeArea.MODE_WINDOWED);
        }

        public void Watchdog()
        {
            if (--gap > 0) return;
            gap = 120;
            if (!wantInit)
            {
                wantInit = true;
                if (engine.PrefMode == ChefRPG43SafeArea.MODE_WINDOWED)
                {
                    ChefRPG43SafeArea.Coerce43(0, engine.PrefH, engine.SysW, engine.SysH, out wantW, out wantH);
                    wantWindowed = true;
                }
            }
            if (!ChefRPG43SafeArea.NextRequest(wantWindowed, wantW, wantH, engine.ScreenW, engine.ScreenH, engine.IsFull,
                    engine.SysW, engine.SysH, out int rw, out int rh, out int mode))
                return;
            engine.SetResolution(rw, rh, mode, Frame, "watchdog");
            if (mode == ChefRPG43SafeArea.MODE_WINDOWED) { wantW = rw; wantH = rh; }
            PersistIfChanged(rw, rh, mode);
        }

        // SettingsPanel.LoadSettings after the patch: coerce the saved size, then the
        // game's own branch picks fullscreen or the windowed call
        public void LoadSettings(int savedW, int savedH, bool savedWindowed, bool mac)
        {
            int curW = savedW, curH = savedH;
            if (curW < Container16x9[0].W || curH < Container16x9[0].H || curW < 640 || curH < 360)
            {
                (curW, curH) = Container16x9[^1];
                savedWindowed = false;
            }
            ChefRPG43SafeArea.Coerce43(curW, curH, engine.SysW, engine.SysH, out curW, out curH);
            if (!savedWindowed || mac) ApplyFullscreen("apply");
            else ApplyWindowed(curW, curH, "apply");
        }

        private void PersistIfChanged(int w, int h, int mode)
        {
            if (w == persistW && h == persistH && mode == persistMode) return;
            persistW = w; persistH = h; persistMode = mode;
            engine.Persist(w, h, mode);
        }
    }

    private sealed class Session
    {
        public readonly EngineModel Engine;
        public readonly GameModel Game;
        public readonly List<(int W, int H, bool Full)> Trace = new();
        private readonly int awakeAt, loadAt;
        private readonly bool savedWindowed, mac;
        private readonly int savedW, savedH;
        private readonly Dictionary<int, Action<EngineModel, GameModel>> events;

        public Session(EngineModel engine, int savedW, int savedH, bool savedWindowed, bool mac = false,
            int awakeAt = 1, int loadAt = 5, Dictionary<int, Action<EngineModel, GameModel>>? events = null)
        {
            Engine = engine;
            Game = new GameModel(engine);
            this.savedW = savedW; this.savedH = savedH; this.savedWindowed = savedWindowed; this.mac = mac;
            this.awakeAt = awakeAt; this.loadAt = loadAt;
            this.events = events ?? new();
        }

        public void Run(int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                Game.Frame = f;
                Trace.Add((Engine.ScreenW, Engine.ScreenH, Engine.IsFull));
                if (events.TryGetValue(f, out var ev)) ev(Engine, Game);
                if (f == awakeAt) Game.Watchdog();             // StartMenuManager.Awake
                if (f == loadAt) Game.LoadSettings(savedW, savedH, savedWindowed, mac);   // Character.Start
                Game.Watchdog();                               // LetterBoxer.Update
                Engine.EndFrame();
            }
        }

        public int WatchdogRequestsAfter(int frame) =>
            Engine.Requests.Count(r => r.Source == "watchdog" && r.Frame > frame);
        public int RequestsAfter(int frame) => Engine.Requests.Count(r => r.Frame > frame);
        public bool AllWindowed(int from, int to) =>
            Enumerable.Range(from, to - from + 1).All(i => Trace[i].W == 960 && Trace[i].H == 720 && !Trace[i].Full);
    }

    private void CheckPolicyFunctions()
    {
        void Coerce(string name, int w, int h, int sysW, int sysH, int ew, int eh)
        {
            ChefRPG43SafeArea.Coerce43(w, h, sysW, sysH, out int ow, out int oh);
            checks.Check(ow == ew && oh == eh, $"Coerce43 {name}: {w}x{h} on {sysW}x{sysH} -> {ow}x{oh} (expected {ew}x{eh})");
        }
        Coerce("saved 16:9 becomes 4:3", 1280, 720, 1280, 960, 960, 720);
        Coerce("even 4:3 width for an odd height", 1000, 700, 1280, 960, 934, 700);
        Coerce("native 4:3 stays", 1280, 960, 1280, 960, 1280, 960);
        Coerce("height held to the display", 1920, 1080, 1280, 960, 1280, 960);
        Coerce("height floor is 480", 640, 360, 1280, 960, 640, 480);
        Coerce("width overflow scales both down", 0, 960, 1000, 960, 1000, 750);
        Coerce("16:10 mac keeps the tallest 4:3", 2056, 1329, 2056, 1329, 1772, 1329);

        // steady state: zero requests, in both modes, including a rounding-edge window
        bool Steady(bool windowed, int sw, int sh, bool full) =>
            !ChefRPG43SafeArea.NextRequest(windowed, sw, sh, sw, sh, full, 1280, 960, out _, out _, out _);
        checks.Check(Steady(false, 1280, 960, true), "NextRequest: fullscreen at native size asks for nothing");
        checks.Check(Steady(true, 960, 720, false), "NextRequest: a 4:3 window asks for nothing while windowed is wanted");
        checks.Check(Steady(true, 934, 700, false), "NextRequest: an even 934x700 window is steady");
        checks.Check(ChefRPG43SafeArea.NextRequest(true, 960, 720, 960, 720, true, 1280, 960, out int rw, out int rh, out int mode)
                     && (rw, rh, mode) == (960, 720, ChefRPG43SafeArea.MODE_WINDOWED),
            "NextRequest: windowed wanted while fullscreen asks for the remembered 960x720 window");
        checks.Check(ChefRPG43SafeArea.NextRequest(false, 1280, 960, 960, 720, false, 1280, 960, out rw, out rh, out mode)
                     && (rw, rh, mode) == (1280, 960, ChefRPG43SafeArea.MODE_FULLSCREEN_WINDOW),
            "NextRequest: fullscreen wanted while windowed asks for native borderless");
    }

    // an earlier session left 1280x720 in the prefs; the panel is 1280x960
    private void CheckBootPersisted720()
    {
        var s = new Session(new EngineModel(1280, 960, 1280, 720, ChefRPG43SafeArea.MODE_FULLSCREEN_WINDOW),
            savedW: 1280, savedH: 720, savedWindowed: false);
        s.Run(400);
        var t = s.Trace;
        checks.Check(t[0] == (1280, 720, true), "boot with persisted 1280x720 starts where unity left it (setup sanity)");
        checks.Check(t[2] == (1280, 960, true),
            "boot with persisted 1280x720: native 1280x960 fullscreen by the first watchdog tick after StartMenuManager.Awake");
        checks.Check(s.RequestsAfter(10) == 0,
            $"boot with persisted 1280x720: zero requests in steady state ({s.RequestsAfter(10)})");
        checks.Check(s.Engine.Requests.All(r => (r.W, r.H) == (1280, 960)),
            "boot with persisted 1280x720: never asks for a 16:9 size");
    }

    // the v5.6 regression: windowed was rewritten to fullscreen and the watchdog forced
    // FullScreenWindow whenever Screen.fullScreen was false, so no window lived 2 seconds
    private void CheckWindowedStaysWindowed()
    {
        var s = new Session(new EngineModel(1280, 960, 1280, 720, ChefRPG43SafeArea.MODE_FULLSCREEN_WINDOW),
            savedW: 1280, savedH: 720, savedWindowed: true);
        s.Run(400);
        checks.Check(s.Trace[8] == (960, 720, false),
            "windowed at saved 1280x720 becomes a 960x720 window");
        checks.Check(s.AllWindowed(8, 399),
            "windowed at saved 1280x720 stays a 960x720 window for 400 frames (regression: old watchdog forced fullscreen)");
        checks.Check(s.RequestsAfter(10) == 0,
            $"windowed at saved 1280x720: zero requests once settled ({s.RequestsAfter(10)})");

        // the player switches windowed -> fullscreen -> windowed from the settings panel
        var s2 = new Session(new EngineModel(1280, 960, 1280, 960, ChefRPG43SafeArea.MODE_FULLSCREEN_WINDOW),
            savedW: 1280, savedH: 960, savedWindowed: false,
            events: new()
            {
                [100] = (_, g) => g.ApplyWindowed(960, 720, "apply"),
                [250] = (_, g) => g.ApplyFullscreen("apply"),
                [400] = (_, g) => g.ApplyWindowed(1024, 768, "apply"),
            });
        s2.Run(700);
        checks.Check(s2.Trace[101] == (960, 720, false) && s2.Trace[249] == (960, 720, false),
            "windowed button: a 960x720 window that survives until the player leaves it");
        checks.Check(s2.Trace[251] == (1280, 960, true) && s2.Trace[399] == (1280, 960, true),
            "fullscreen button: back to native borderless and it stays");
        checks.Check(s2.Trace[401] == (1024, 768, false) && s2.Trace[699] == (1024, 768, false),
            "picking 1024x768 windowed: a 1024x768 window that stays");
        checks.Check(s2.WatchdogRequestsAfter(0) == 0,
            "windowed/fullscreen switching never needs the watchdog to correct it");
    }

    private void CheckUserResize()
    {
        var s = new Session(new EngineModel(1280, 960, 1280, 960, ChefRPG43SafeArea.MODE_WINDOWED),
            savedW: 960, savedH: 720, savedWindowed: true,
            events: new() { [200] = (e, _) => { e.ScreenW = 1000; e.ScreenH = 700; } });
        s.Run(700);
        var afterDrag = s.Engine.Requests.Where(r => r.Frame >= 200).ToList();
        checks.Check(afterDrag.Count == 1 && (afterDrag[0].W, afterDrag[0].H, afterDrag[0].Mode) == (934, 700, ChefRPG43SafeArea.MODE_WINDOWED),
            $"dragging a window to 1000x700 is re-requested as 934x700 (4:3, even) once ({afterDrag.Count} request(s))");
        checks.Check(s.Trace[699] == (934, 700, false),
            "the resized window ends at 934x700 and stays windowed");
        checks.Check(s.RequestsAfter(afterDrag.Count > 0 ? afterDrag[0].Frame : 0) == 0,
            "after the correction the window is steady (no further requests)");
    }

    private void CheckContainerClamp()
    {
        var s = new Session(new EngineModel(1280, 960, 1280, 720, ChefRPG43SafeArea.MODE_FULLSCREEN_WINDOW, clamped: 3),
            savedW: 1280, savedH: 720, savedWindowed: false);
        s.Run(500);
        var r = s.Engine.Requests;
        checks.Check(r.All(x => (x.W, x.H) == (1280, 960) && x.W * 9 != x.H * 16),
            "container clamping the first 3 requests: every request is the native 4:3 size, never 16:9");
        checks.Check(r.Count == 4,
            $"container clamping the first 3 requests: retries until it sticks, on the 4th request ({r.Count})");
        checks.Check(s.Trace[499] == (1280, 960, true),
            "container clamping the first 3 requests: ends at native fullscreen");
        checks.Check(s.RequestsAfter(r.Count > 0 ? r[^1].Frame : 0) == 0,
            "container clamping the first 3 requests: silent after it sticks");
    }

    private void CheckMacFullscreen()
    {
        var s = new Session(new EngineModel(2056, 1329, 2056, 1329, ChefRPG43SafeArea.MODE_FULLSCREEN_WINDOW),
            savedW: 1280, savedH: 720, savedWindowed: false, mac: true);
        s.Engine.ScreenW = 4112; s.Engine.ScreenH = 2658;   // retina backing store
        s.Run(400);
        checks.Check(s.Engine.Requests.Count == 1 && s.Engine.Requests[0].Source == "apply"
                     && (s.Engine.Requests[0].W, s.Engine.Requests[0].H) == (2056, 1329),
            $"mac 2056x1329: native fullscreen requested once by the panel, watchdog stays quiet ({s.Engine.Requests.Count} request(s))");
        checks.Check(s.RequestsAfter(10) == 0, "mac 2056x1329: zero requests in steady state");

        var hud = new HudRun(2056, 1329, sceneChangeAt: -1, frames: 100);
        hud.Execute();
        checks.Check(hud.Inside(hud.Get("clock"), hud.ViewLeft, hud.ViewRight)
                     && hud.Inside(hud.Get("prompt-left"), hud.ViewLeft, hud.ViewRight)
                     && hud.Inside(hud.Get("prompt-right"), hud.ViewLeft, hud.ViewRight),
            "mac 2056x1329: the 4:3 pillarbox keeps the hud inside the view");
    }

    // the container kills the game, so unity never writes its own prefs; only Persist does
    private void CheckKillAndReboot()
    {
        var first = new Session(new EngineModel(1280, 960, 1280, 720, ChefRPG43SafeArea.MODE_FULLSCREEN_WINDOW),
            savedW: 1280, savedH: 720, savedWindowed: false);
        first.Run(60);                                          // ...then killed, nothing else is written
        var e1 = first.Engine;
        var reboot = new Session(new EngineModel(1280, 960, e1.PrefW, e1.PrefH, e1.PrefMode),
            savedW: 1280, savedH: 960, savedWindowed: false);
        reboot.Run(300);
        checks.Check(reboot.Trace[0] == (1280, 960, true),
            "kill then reboot after fullscreen: boots straight into native 1280x960");
        checks.Check(reboot.WatchdogRequestsAfter(-1) == 0,
            "kill then reboot after fullscreen: the watchdog has nothing to correct");

        var w1 = new Session(new EngineModel(1280, 960, 1280, 960, ChefRPG43SafeArea.MODE_FULLSCREEN_WINDOW),
            savedW: 1280, savedH: 960, savedWindowed: false,
            events: new() { [30] = (_, g) => g.ApplyWindowed(960, 720, "apply") });
        w1.Run(80);
        var e2 = w1.Engine;
        // the panel saved windowed 960x720 to the game's own save file before the kill
        var reboot2 = new Session(new EngineModel(1280, 960, e2.PrefW, e2.PrefH, e2.PrefMode),
            savedW: 960, savedH: 720, savedWindowed: true);
        reboot2.Run(400);
        checks.Check(reboot2.Trace[0] == (960, 720, false),
            "kill then reboot after windowed: boots straight into the 960x720 window");
        checks.Check(reboot2.AllWindowed(0, 399),
            "kill then reboot after windowed: never flips to fullscreen on the way up");
        checks.Check(reboot2.WatchdogRequestsAfter(-1) == 0,
            "kill then reboot after windowed: the watchdog has nothing to correct");
    }

    private void CheckModes43()
    {
        int[] device = Container16x9.Select(m => m.H).ToArray();
        var flat = ChefRPG43SafeArea.Modes43(device, 1280, 960);
        var modes = Enumerable.Range(0, flat.Length / 2).Select(i => (W: flat[i * 2], H: flat[i * 2 + 1])).ToList();
        checks.Check(modes.Count > 0 && modes.Count <= 10, $"Modes43 on the device list: 1..10 entries ({modes.Count})");
        checks.Check(modes.All(m => m.W * 3 == m.H * 4), "Modes43 on the device list: every entry is exactly 4:3");
        checks.Check(modes.All(m => m.W <= 1280 && m.H <= 960), "Modes43 on the device list: nothing larger than the display");
        checks.Check(modes.Distinct().Count() == modes.Count, "Modes43 on the device list: entries are distinct");
        checks.Check(modes.Zip(modes.Skip(1)).All(p => p.First.H < p.Second.H),
            "Modes43 on the device list: ascending");
        checks.Check(modes.Contains((1280, 960)) && modes.Contains((960, 720)),
            "Modes43 on the device list: includes native 1280x960 and 960x720");

        int[] many = Enumerable.Range(0, 40).Select(i => 400 + i * 40).ToArray();
        var big = ChefRPG43SafeArea.Modes43(many, 2056, 1329);
        var bigModes = Enumerable.Range(0, big.Length / 2).Select(i => (W: big[i * 2], H: big[i * 2 + 1])).ToList();
        checks.Check(bigModes.Count == 10, $"Modes43 on a dense list is capped at the picker's 10 buttons ({bigModes.Count})");
        checks.Check(bigModes[^1] == (1772, 1329) && bigModes.All(m => m.W <= 2056 && m.H <= 1329),
            "Modes43 on a 2056x1329 display keeps the largest 4:3 frame (1772x1329)");
    }

    private sealed class RetryRun
    {
        private readonly int frames;
        private int screenW, screenH;
        private readonly int sysW, sysH;
        private readonly int revertAt;
        private bool fullScreen;
        public List<(int W, int H)> Requests { get; } = new();

        public RetryRun(int w, int h, int sw, int sh, int frames, bool fullScreen, int revertAt)
        {
            screenW = w; screenH = h; sysW = sw; sysH = sh; this.frames = frames;
            this.fullScreen = fullScreen; this.revertAt = revertAt;
        }

        // the real policy decides; this only supplies the cadence and applies the result
        public void Execute()
        {
            int gap = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                if (frame == revertAt)
                {
                    screenW = 1280;
                    screenH = 720;
                    fullScreen = false;
                }
                if (++gap < 120) continue;
                gap = 0;
                if (ChefRPG43SafeArea.NextRequest(false, sysW, sysH, screenW, screenH, fullScreen,
                        sysW, sysH, out int rw, out int rh, out int mode))
                {
                    Requests.Add((rw, rh));
                    screenW = rw;
                    screenH = rh;
                    fullScreen = mode != ChefRPG43SafeArea.MODE_WINDOWED;
                }
            }
        }
    }

    // ------------------------------------------------------------------------------
    // hud containment model
    // ------------------------------------------------------------------------------

    private sealed class HudElement
    {
        public string Name { get; }
        public HudRun Run { get; }
        public double AuthL, AuthR, AuthB, AuthT;   // authored rect at the current window size
        public double OwnX, OwnY;                   // our shift, in canvas units (anchoredPosition)
        public HudElement? Parent;
        public bool GivenUp;
        public List<HudElement> Children { get; } = new();

        public HudElement(HudRun run, string name) { Run = run; Name = name; }

        // a shifted ancestor takes its children with it, like unity
        private double EffX => OwnX + (Parent?.EffX ?? 0);
        private double EffY => OwnY + (Parent?.EffY ?? 0);
        public double Left => AuthL + EffX * Run.Scale;
        public double Right => AuthR + EffX * Run.Scale;
        public double Bottom => AuthB + EffY * Run.Scale;
        public double Top => AuthT + EffY * Run.Scale;
        public double Width => Right - Left;
        public double Height => Top - Bottom;
    }

    /// models ClampCanvasesTo43's cadence against a window-anchored hud. the world
    /// view keeps [viewLeft, viewRight] on x and the window on y; the clock is pinned to
    /// its top right, the side prompt group is too wide to slide so its children move
    /// individually, and generic elements only move after two full-scan sightings. every
    /// shift is an anchoredPosition offset in canvas units that survives a resize (so it
    /// scales with the window, which is how a transient size poisons the hud) and is undone
    /// by RestoreShifts on a resize or scene change.
    private sealed class HudRun
    {
        private const int FULL_SCAN_FRAMES = 30;
        private const int TRACKED_FRAMES = 4;
        private const int HUD_RESOLVE_FRAMES = 120;
        private const int MAX_SHIFTS = 6;

        private int w, h;
        private readonly int sceneChangeAt, frames;
        private readonly bool withEdgeItems;
        private readonly (int Frame, int W, int H)[] resizes;

        private readonly List<HudElement> all = new();
        private readonly List<int> writesByFrame = new();
        private readonly Dictionary<int, bool> containedAt = new();
        private readonly Dictionary<int, double> displacement = new();
        private readonly HashSet<HudElement> tracked = new();
        private readonly HashSet<HudElement> shifted = new();
        private readonly Dictionary<HudElement, int> pending = new();
        private readonly Dictionary<HudElement, int> shiftCount = new();

        private HudElement clock = null!, promptLeft = null!, promptRight = null!;
        private HudElement promptsGroup = null!, fade = null!, matteTop = null!, tooltip = null!;

        private int fullScanTick, frame;
        private bool needResolve = true;
        private bool barsActive;
        private bool clockPinned;

        public double Scale => w / 640.0;
        public double ViewLeft { get; private set; }
        public double ViewRight { get; private set; }
        public int MaxWritesInFrame { get; private set; }
        public int BackgroundWrites { get; private set; }

        public HudRun(int w, int h, int sceneChangeAt, int frames,
            (int Frame, int W, int H)[]? resizes = null, bool withEdgeItems = false)
        {
            this.w = w; this.h = h; this.sceneChangeAt = sceneChangeAt; this.frames = frames;
            this.resizes = resizes ?? Array.Empty<(int, int, int)>();
            this.withEdgeItems = withEdgeItems;
            UpdateView();
            BuildScene();
        }

        private void UpdateView()
        {
            double bar = (w - h * ASPECT) * 0.5;
            if (bar < 1) bar = 0;
            ViewLeft = bar;
            ViewRight = w - bar;
        }

        public HudElement Get(string name) => all.First(e => e.Name == name);

        public bool Inside(HudElement e, double left, double right) =>
            e.Left >= left - 0.75 && e.Right <= right + 0.75;

        public bool InsideY(HudElement e) => e.Bottom >= -0.75 && e.Top <= h + 0.75;
        public bool InsideScreen(HudElement e) => Inside(e, 0, w) && InsideY(e);

        public int SteadyFrameWrites(int from, int to) => writesByFrame.Skip(from).Take(to - from + 1).Sum();

        public int MaxShiftsOf(string name) =>
            shiftCount.TryGetValue(Get(name), out var n) ? n : 0;

        public bool SceneChangeInsideByFrame(int from, int to)
        {
            for (int f = from; f <= to; f++)
                if (!containedAt.TryGetValue(f, out var contained) || !contained)
                    return false;
            return true;
        }

        // largest anchoredPosition offset any element carried between two frames
        public double MaxDisplacementBetween(int from, int to) =>
            displacement.Where(kv => kv.Key >= from && kv.Key <= to).Select(kv => kv.Value).DefaultIfEmpty(0).Max();

        public List<string> OwnDifferences(HudRun other)
        {
            var list = new List<string>();
            foreach (var e in all)
            {
                var o = other.Get(e.Name);
                if (Math.Abs(e.OwnX - o.OwnX) > 1e-6 || Math.Abs(e.OwnY - o.OwnY) > 1e-6)
                    list.Add($"{e.Name}({e.OwnX:F1},{e.OwnY:F1} vs {o.OwnX:F1},{o.OwnY:F1})");
            }
            return list;
        }

        // authored rects for the current window size. a scene load replaces every hud
        // object and they spawn at their window anchors; a resize re-lays them out at the
        // new size but keeps whatever offset we put on them (anchoredPosition persists).
        private void Layout()
        {
            double s = Scale;
            double clockW = 150 * s, clockH = 40 * s, promptW = 120 * s, promptH = 125 * s;
            double tooltipW = 200 * s, tooltipH = 70 * s;
            Set(clock, w - clockW, w, h - 8 - clockH, h - 8);
            Set(promptLeft, 0, promptW, h / 2.0 - promptH, h / 2.0 + promptH);
            Set(promptRight, w - promptW, w, h / 2.0 - promptH, h / 2.0 + promptH);
            // the group's union spans the window on a match-width canvas, which is why
            // it can never be slid as a single rect
            Set(promptsGroup, 0, w, h / 2.0 - promptH, h / 2.0 + promptH);
            Set(Get("dialogue"), (w - Math.Min(600 * s, w * 0.9)) / 2, (w + Math.Min(600 * s, w * 0.9)) / 2, h * 0.1, h * 0.3);
            Set(fade, 0, w, 0, h);
            Set(matteTop, 0, w, h * 0.9, h);
            Set(tooltip, w * 0.82, w * 0.82 + tooltipW, h * 0.55, h * 0.55 + tooltipH);
            if (withEdgeItems)
            {
                // a banner hanging 40px over the top edge and a footer 30px under the bottom
                Set(Get("banner"), w / 2.0 - 100 * s, w / 2.0 + 100 * s, h - 60, h + 40);
                Set(Get("footer"), w / 2.0 - 100 * s, w / 2.0 + 100 * s, -30, 50);
            }
        }

        private static void Set(HudElement e, double l, double r, double b, double t)
        {
            e.AuthL = l; e.AuthR = r; e.AuthB = b; e.AuthT = t;
        }

        private void BuildScene()
        {
            all.Clear();
            tracked.Clear();
            shifted.Clear();
            pending.Clear();
            shiftCount.Clear();

            HudElement Add(string name)
            {
                var e = new HudElement(this, name);
                all.Add(e);
                return e;
            }

            clock = Add("clock");
            promptLeft = Add("prompt-left");
            promptRight = Add("prompt-right");
            promptsGroup = Add("prompts");
            promptsGroup.Children.Add(promptLeft);
            promptsGroup.Children.Add(promptRight);
            promptLeft.Parent = promptsGroup;
            promptRight.Parent = promptsGroup;
            Add("dialogue");
            fade = Add("fade");
            matteTop = Add("matte-top");
            tooltip = Add("tooltip");
            if (withEdgeItems)
            {
                Add("banner");
                Add("footer");
            }
            Layout();

            barsActive = true;
            clockPinned = false;
            needResolve = true;
            fullScanTick = 0;   // whole hierarchy is new: rescan now
        }

        // ResetSceneCaches for a resize: put every shifted rect back at its authored
        // position (and un-pin the clock), then drop the caches so the new frame is
        // measured from a clean slate
        private int RestoreShifts()
        {
            int writes = shifted.Count + (clockPinned ? 1 : 0);
            foreach (var e in shifted) { e.OwnX = 0; e.OwnY = 0; e.GivenUp = false; }
            clock.OwnX = 0; clock.OwnY = 0; clock.GivenUp = false;
            foreach (var e in all) e.GivenUp = false;
            shifted.Clear();
            tracked.Clear();
            pending.Clear();
            shiftCount.Clear();
            clockPinned = false;
            needResolve = true;
            fullScanTick = 0;
            return writes;
        }

        public void Execute()
        {
            for (frame = 0; frame < frames; frame++)
            {
                int writes = 0;
                if (frame == sceneChangeAt)
                    BuildScene();
                foreach (var rs in resizes)
                {
                    if (rs.Frame != frame) continue;
                    w = rs.W; h = rs.H;
                    UpdateView();
                    Layout();
                    writes += RestoreShifts();
                }

                // resolve is a no-op cost after the roots are cached
                if (needResolve || frame % HUD_RESOLVE_FRAMES == 0)
                {
                    needResolve = false;
                    // ResolveHud re-finds both roots and arms the prompt slot
                }

                PinClock(ref writes);
                ContainPinned(clock, ref writes);
                ContainPinned(promptsGroup, ref writes);

                if (barsActive)
                {
                    barsActive = false;
                    writes++;   // one SetActive(false) per scene
                }

                if (--fullScanTick <= 0)
                {
                    fullScanTick = FULL_SCAN_FRAMES;
                    FullScan(ref writes);
                }
                if ((frame & (TRACKED_FRAMES - 1)) == 0)
                    CheckTracked(ref writes);

                writesByFrame.Add(writes);
                MaxWritesInFrame = Math.Max(MaxWritesInFrame, writes);
                displacement[frame] = all.Select(e => Math.Max(Math.Abs(e.OwnX), Math.Abs(e.OwnY))).DefaultIfEmpty(0).Max();
                containedAt[frame] = Inside(clock, ViewLeft, ViewRight)
                    && Inside(promptLeft, ViewLeft, ViewRight)
                    && Inside(promptRight, ViewLeft, ViewRight);
            }
        }

        // pinned to the top right of the 4:3 view, once per scene/window size;
        // the game never writes this rect
        private void PinClock(ref int writes)
        {
            if (clock == null || clockPinned) return;
            clock.OwnX = (ViewRight - clock.AuthR) / Scale;
            clock.OwnY = (h - 8 - clock.AuthT) / Scale;
            clockPinned = true;
            shifted.Add(clock);
            shiftCount[clock] = 1;
            writes++;
        }

        private bool OffScreen(double minX, double maxX, double minY, double maxY) =>
            maxX <= 0 || minX >= w || maxY <= 0 || minY >= h;

        private static double AxisOverhang(double min, double max, double lo, double hi)
        {
            if (max - min >= hi - lo - 1) return 0;
            if (max > hi + 0.75) return hi - max;
            if (min < lo - 0.75) return lo - min;
            return 0;
        }

        private bool Overhang(double minX, double maxX, double minY, double maxY, out double dx, out double dy)
        {
            dx = AxisOverhang(minX, maxX, ViewLeft, ViewRight);
            dy = AxisOverhang(minY, maxY, 0, h);
            return dx != 0 || dy != 0;
        }

        private void ContainPinned(HudElement e, ref int writes)
        {
            if (e == null || e.GivenUp) return;
            var (minX, maxX, minY, maxY) = Union(e);
            if (OffScreen(minX, maxX, minY, maxY)) return;

            // a group that already spans the 4:3 view cannot be slid in one piece
            // without pushing its other edge into the opposite pillar.
            bool spansX = maxX - minX >= ViewRight - ViewLeft - 1;
            bool outsideX = maxX > ViewRight + 0.75 || minX < ViewLeft - 0.75;
            if (spansX && outsideX)
            {
                ContainChildren(e, ref writes);
                return;
            }

            if (!Overhang(minX, maxX, minY, maxY, out var dx, out var dy)) return;
            Move(e, dx, dy, ref writes);
        }

        // MeasureUnion over the subtree: leaves use their own rect, groups the hull
        private static (double minX, double maxX, double minY, double maxY) Union(HudElement e)
        {
            if (e.Children.Count == 0) return (e.Left, e.Right, e.Bottom, e.Top);
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var child in e.Children)
            {
                var (a, b, c, d) = Union(child);
                minX = Math.Min(minX, a);
                maxX = Math.Max(maxX, b);
                minY = Math.Min(minY, c);
                maxY = Math.Max(maxY, d);
            }
            if (minX > maxX || minY > maxY) return (0, 0, 0, 0);
            return (minX, maxX, minY, maxY);
        }

        // first rect that crosses the view is shifted and its subtree is left alone,
        // because moving an ancestor moves the children with it
        private void ContainChildren(HudElement parent, ref int writes)
        {
            foreach (var child in parent.Children)
            {
                if (child.GivenUp) continue;
                if (OffScreen(child.Left, child.Right, child.Bottom, child.Top)) continue;

                if (Overhang(child.Left, child.Right, child.Bottom, child.Top, out var dx, out var dy))
                {
                    Move(child, dx, dy, ref writes);
                    continue;
                }
                ContainChildren(child, ref writes);
            }
        }

        // generic walk: full width fade/matte stay put, off-screen stays put,
        // and a rect must be outside on two scans before it is moved. the prompt
        // group's subtree belongs to the pinned pass, like the owned slot in the helper.
        private void FullScan(ref int writes)
        {
            foreach (var e in all)
            {
                if (e == clock || e == promptsGroup || e.Children.Count > 0) continue;   // owned by the pinned pass
                if (e.Parent == promptsGroup) continue;
                Contain(e, ref writes);
            }
        }

        private void Contain(HudElement e, ref int writes)
        {
            if (e.GivenUp) return;
            if (OffScreen(e.Left, e.Right, e.Bottom, e.Top)) return;   // parked off screen: animations

            bool crossing = Overhang(e.Left, e.Right, e.Bottom, e.Top, out var dx, out var dy);
            if (!Gate(e, crossing)) return;
            Move(e, dx, dy, ref writes);
        }

        private bool Gate(HudElement e, bool crossing)
        {
            if (!crossing)
            {
                pending.Remove(e);
                return false;
            }
            pending.TryGetValue(e, out var count);
            pending[e] = ++count;
            if (count < 2) return false;
            pending.Remove(e);
            return true;
        }

        private void CheckTracked(ref int writes)
        {
            foreach (var e in tracked.ToList())
            {
                if (e.GivenUp) continue;
                if (OffScreen(e.Left, e.Right, e.Bottom, e.Top)) continue;
                bool crossing = Overhang(e.Left, e.Right, e.Bottom, e.Top, out var dx, out var dy);
                if (!Gate(e, crossing)) continue;
                Move(e, dx, dy, ref writes);
            }
        }

        private void Move(HudElement e, double dx, double dy, ref int writes)
        {
            // one anchoredPosition write moves the whole subtree, like unity
            e.OwnX += dx / Scale;
            e.OwnY += dy / Scale;
            shifted.Add(e);
            writes++;
            if (e.Name == "fade" || e.Name == "matte-top") BackgroundWrites++;

            shiftCount.TryGetValue(e, out var count);
            shiftCount[e] = ++count;
            if (count >= MAX_SHIFTS)
            {
                e.GivenUp = true;
                tracked.Remove(e);
            }
            else
            {
                tracked.Add(e);
            }
        }
    }
}
