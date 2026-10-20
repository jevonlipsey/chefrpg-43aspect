using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

// keeps the always-on hud inside the centred 4:3 view without touching the game's
// hierarchy every frame.
//
// the world camera is already pillarboxed by letterboxer + urp's pixel perfect blit.
// screen space overlay canvases still span the whole window, so anything anchored to
// a screen edge parks in the pillars on a wide display. we only move a rect when it
// actually crosses the view line:
//   - the clock is pinned to the top right once per canvas scale, then held inside
//   - the side key prompts live under a slot we own, because keyinputtooltip resets
//     their anchoredPosition to zero; moving the slot means the game's reset is free
//   - a full hierarchy walk runs every 30 frames (or on resize/scene change), not
//     every frame. rects that needed a shift go on a short tracked list and only
//     those few are re-measured on later frames.
// no per-frame canvas dirtying: an element already inside the view is measured and
// left alone, and the walk cadence is amortised instead of paid on every frame.
//
// every shift is reversible. a resize or scene change puts each moved rect back at its
// authored position before the caches are dropped, because a shift measured against a
// transient frame (the 720p flip while the container settles) would otherwise stay
// baked into the hud after the frame returns to 4:3.
//
// the display policy (fullscreen / windowed / persisted size) is the other half of this
// class: the pure decisions are in DisplayPolicy.cs, the unity calls are below.
public static partial class ChefRPG43SafeArea
{
    private const float CLOCK_MARGIN_Y = 8f;
    private const int SCENE_CHECK_FRAMES = 30;
    private const int FULL_SCAN_FRAMES = 30;
    private const int TRACKED_FRAMES = 4;
    private const int HUD_RESOLVE_FRAMES = 120;
    private const int HUD_RERESOLVE_FRAMES = 60;
    private const int MAX_TRACKED = 96;
    private const int MAX_SHIFTS = 6;
    private const int WATCHDOG_FRAMES = 120;   // ~2s at 60fps between display checks
    private const int CLOCK_LOG_FRAMES = 4;

    private static readonly Vector3[] corners = new Vector3[4];
    // authored anchoredPosition of every rect we have moved, recorded on its first shift
    private static readonly Dictionary<RectTransform, Vector2> origPos = new Dictionary<RectTransform, Vector2>();
    private static readonly List<RectTransform> tracked = new List<RectTransform>(MAX_TRACKED);
    private static readonly HashSet<Transform> trackedSet = new HashSet<Transform>();
    private static readonly Dictionary<Transform, int> shiftCount = new Dictionary<Transform, int>();
    private static readonly Dictionary<Transform, int> pending = new Dictionary<Transform, int>();
    private static readonly HashSet<Transform> givenUp = new HashSet<Transform>();
    private static readonly List<RectTransform> rectBuf = new List<RectTransform>(2048);

    private static Canvas[] canvases;
    private static int lastW, lastH;
    private static int lastScene = int.MinValue;
    private static int frameStamp = -1;
    private static int sceneTick, fullScanTick, hudTick;
    private static int hudReresolve;
    private static bool alive;
    private static bool viewLogged;
    private static int shiftLogs = 16;
    private static int shifts;

    private static int curW, curH;

    private static Transform clock;
    private static Transform prompts;
    private static Transform promptsSlot;
    private static float clockScale = -1f;
    private static int clockPinW, clockPinH;
    private static int clockLogWait;          // frames until the clock rect is logged; 0 = nothing pending
    private static int clockRectLogs = 8;

    // the clock's authored layout, so a resize can undo the pin before re-pinning it
    private static Transform clockOrigOf;
    private static Vector2 clockOrigMin, clockOrigMax, clockOrigPivot, clockOrigPos;

    // display policy state. fullscreen until a windowed choice says otherwise.
    private static bool wantWindowed;
    private static int wantW, wantH;
    private static int gap;                 // frames until the next watchdog pass; 0 runs now
    private static bool wantInit;
    private static int persistW, persistH, persistMode;
    private static int displayLogs = 24;

    private static bool hudSearched;
    private static bool matteSearched;
    private static UnityEngine.Object matte;
    private static GameObject upperBar;
    private static GameObject lowerBar;

    private static bool ppSearched;
    private static Component ppc;
    private static bool ppCropEnforced;

    // one render-path dump a couple of seconds into the first scenes: every camera, its
    // pixel-perfect settings, the root canvases, and any ui graphic over the top/bottom
    // strips. this is how 'Camera Letterbox' was found; kept so a regression shows up.
    private const int RENDER_DUMP_FRAMES = 120;
    private static int renderDumpWait;      // frames until the render dump; 0 = nothing pending
    private static int renderDumps = 3;

    private static bool shotChecked;
    private static bool shotEnabled;

    public static void ClampCanvasesTo43()
    {
        int w = Screen.width;
        int h = Screen.height;
        if (w <= 0 || h <= 0) return;

        int frame = Time.frameCount;
        if (frame == frameStamp) return;       // update + lateupdate callers, run once
        frameStamp = frame;
        curW = w;
        curH = h;

        CaptureProbe(frame);

        if (!alive)
        {
            alive = true;
            lastW = w;
            lastH = h;
            LogView(w, h);
            renderDumpWait = RENDER_DUMP_FRAMES;
        }
        if (w != lastW || h != lastH)
        {
            lastW = w;
            lastH = h;
            ResetSceneCaches();
            fullScanTick = 0;
        }

        if (--sceneTick <= 0)
        {
            sceneTick = SCENE_CHECK_FRAMES;
            int handle = SceneManager.GetActiveScene().handle;
            if (handle != lastScene)
            {
                lastScene = handle;
                ResetSceneCaches();
                fullScanTick = 0;
            }
        }

        // 4:3 view centred in the window. on a 4:3 panel bar is 0 and the view is full.
        float bar = ((float)w - (float)h * 4f / 3f) * 0.5f;
        if (bar < 1f) bar = 0f;
        float viewLeft = bar;
        float viewRight = (float)w - bar;
        if (!viewLogged)
        {
            viewLogged = true;
            Debug.Log("[ChefRPG43] view " + w + "x" + h + " -> 4:3 [" + viewLeft.ToString("F0") + "," + viewRight.ToString("F0") + "]");
        }

        // a scene unload kills the cached hud transforms, but the handle poll only runs
        // every 30 frames. a null check on a dead object is free, so start a short
        // re-resolve window the moment that trips and stop it as soon as both roots are
        // alive again. bounded so a scene without a clock never pays per-frame finds.
        // the clock / side-prompt lookups only feed the pillar path below
        if (bar >= 1f)
        {
            if (clock != null && prompts != null)
            {
                hudReresolve = 0;
            }
            else if (hudSearched && hudReresolve < HUD_RERESOLVE_FRAMES)
            {
                if (hudReresolve == 0) fullScanTick = 0;   // whole hierarchy is new: rescan now
                hudReresolve++;
                hudTick = 0;
            }

            if (--hudTick <= 0)
            {
                hudTick = HUD_RESOLVE_FRAMES;
                ResolveHud(w, h);
            }
        }

        // on a 4:3 frame there are no pillars: nothing can sit beside the view, and the
        // generic containment only ever fought the game there (it dragged the gamepad
        // cursor slot, slide-in panels and title text, and the measuring cost frames
        // under emulation). the edge layout below does the 4:3 work once per scene.
        bool pillars = bar >= 1f;
        Layout43(pillars);
        KeepParked();
        HideMatteBars();
        if ((frame & 31) == 0) EnforcePpRefs();
        if (renderDumpWait > 0 && --renderDumpWait == 0) DumpRenderPath();
        if (!pillars) return;

        PinClockIfNeeded(w, h, viewRight);
        LogClockRect();
        ContainPinned(clock, null, viewLeft, viewRight);
        ContainPinned(prompts, promptsSlot, viewLeft, viewRight);

        if (--fullScanTick <= 0)
        {
            fullScanTick = FULL_SCAN_FRAMES;
            FullScan(viewLeft, viewRight);
        }
        if ((frame & (TRACKED_FRAMES - 1)) == 0 && tracked.Count > 0)
            CheckTracked(viewLeft, viewRight);
    }

    // ---- 4:3 layout -------------------------------------------------------------------
    //
    // the hud is authored against a centred 640x360 box. on a 4:3 frame the canvas is
    // 640x480 units, so everything on the box's top or bottom edge floats 60 units
    // (120px at 2x) in from the screen edge, and 'Camera Letterbox' - a 1300x520 frame
    // with a 16:9 hole, the last child of Main User Interface - paints black over
    // everything outside the box. that frame is the device's black bars.
    //
    // turn the frame off, move edge-pinned hud out to the real edges by whole canvas
    // units (pixel perfect at any integer scale), and stretch the map panel to the
    // frame height. only the containers below are touched: menus and build mode lay
    // their parts out relative to each other and stay exactly as authored.
    private static readonly string[] HUD_FRAMES =
    {
        "In Game UI (ON)", "Consumables Slots", "Festivals", "Restaurant Gameplay UI (ON)", "Key Input Parent (ON)"
    };
    private const float BOX_W = 640f;
    private const float BOX_H = 360f;
    private const float EDGE_SNAP = 12f;       // units from the box edge that still count as "on" it
    private const int LAYOUT_FRAMES = 60;

    // where the layout put each rect, so a game reset back to the authored spot is redone
    private static readonly Dictionary<RectTransform, Vector2> layoutWant = new Dictionary<RectTransform, Vector2>();
    private static readonly Dictionary<Transform, Vector3> scaleOrig = new Dictionary<Transform, Vector3>();
    private static bool layoutDone;
    private static int layoutTick;
    private static int layoutLogs = 24;
    private static UnityEngine.UI.Graphic letterboxFrame;

    private static void Layout43(bool pillars)
    {
        if (--layoutTick > 0) return;
        layoutTick = LAYOUT_FRAMES;
        try
        {
            if (!layoutDone) ApplyLayout43(pillars);
            else KeepLayout43();
        }
        catch (System.Exception e)
        {
            if (layoutLogs > 0)
            {
                layoutLogs--;
                Debug.Log("[ChefRPG43] layout error " + e.GetType().Name + ": " + e.Message);
            }
        }
    }

    private static void ApplyLayout43(bool pillars)
    {
        GameObject ui = GameObject.Find("Main User Interface");
        if (ui == null) return;                 // not loaded yet: retry next tick
        RectTransform root = ui.transform as RectTransform;
        if (root == null) return;
        layoutDone = true;

        Transform frame = ui.transform.Find("Camera Letterbox");
        letterboxFrame = frame != null ? frame.GetComponent<UnityEngine.UI.Graphic>() : null;
        if (letterboxFrame != null) letterboxFrame.enabled = false;

        float cw = root.rect.width;
        float ch = root.rect.height;
        float extraX = Mathf.Max(0f, Mathf.Floor((cw - BOX_W) * 0.5f));
        float extraY = Mathf.Max(0f, Mathf.Floor((ch - BOX_H) * 0.5f));
        int moved = 0;

        if (extraX >= 1f || extraY >= 1f)
        {
            // the canvas root is itself a box frame: 'Consumables Slots' (the hotbar icons)
            // sits there, beside its frame 'In Game UI/Consumables UI'
            for (int i = 0; i < ui.transform.childCount; i++)
            {
                Transform c = ui.transform.GetChild(i);
                if (c != frame && MoveToEdge(c as RectTransform, extraX, extraY)) moved++;
            }

            for (int f = 0; f < HUD_FRAMES.Length; f++)
            {
                Transform container = FindDeep(ui.transform, HUD_FRAMES[f], 2);
                if (container == null) continue;
                for (int i = 0; i < container.childCount; i++)
                {
                    Transform c = container.GetChild(i);
                    if (pillars && c == clock) continue;     // the pillar path pins the clock itself
                    if (MoveToEdge(c as RectTransform, extraX, extraY)) moved++;
                }
            }

            // the map is a 640x360 panel; stretched to the frame height on request
            Transform map = FindDeep(ui.transform, "Minimap (OFF)", 2);
            if (map != null && extraY >= 1f)
            {
                Vector3 s;
                if (!scaleOrig.TryGetValue(map, out s))
                {
                    s = map.localScale;
                    scaleOrig[map] = s;
                }
                map.localScale = new Vector3(s.x, s.y * ch / BOX_H, s.z);
            }

            // the scene-entry fade is a fixed 640x360 canvas. while a scene loads the
            // camera stops drawing, so with the frame gone the strips outside the fade
            // kept the previous scene's last image. size the fade to the whole canvas.
            RectTransform fade = FindDeep(ui.transform, "Entrance Scene Fade In", 2) as RectTransform;
            if (fade != null)
            {
                if (!sizeOrig.ContainsKey(fade)) sizeOrig[fade] = fade.sizeDelta;
                fade.sizeDelta = new Vector2(cw, ch);
            }

            CollectParked(ui.transform, extraY);
        }

        if (layoutLogs > 0)
        {
            layoutLogs--;
            Debug.Log("[ChefRPG43] layout canvas=" + cw.ToString("F0") + "x" + ch.ToString("F0")
                + " extra=(" + extraX.ToString("F0") + "," + extraY.ToString("F0") + ") letterbox="
                + (letterboxFrame != null ? "off" : "missing") + " moved=" + moved + " parked=" + parked.Count);
        }
    }

    // ---- parked panels ------------------------------------------------------------------
    //
    // the game hides slide-in panels (festival leaderboards, the money popup, results)
    // by parking them just outside the 640x360 box - off screen at 16:9, but on screen in
    // a 4:3 frame. most are driven by an Animator that rewrites the position every frame,
    // so a one-off move does not stick. instead, after animation (LetterBoxer.LateUpdate)
    // each parked panel is pushed out by the canvas overhang, eased in over the first few
    // units past the box edge so a slide-in reaches its authored on-screen spot without a
    // jump. only the topmost parked rect of a branch is tracked; its children follow.
    private const float PARK_RAMP = 4f;        // units past the box edge to reach the full push
    private const int MAX_PARKED = 48;

    // parallel lists, not a nested class: the patcher clones only this top-level type
    // into the game, and a nested type would stay bound to SafeHelper.dll
    private static readonly List<RectTransform> parked = new List<RectTransform>();
    private static readonly List<Vector2> parkedWritten = new List<Vector2>();  // what we last wrote, to tell our push from a rewrite
    private static readonly List<float> parkedPush = new List<float>();         // the push currently baked into it
    private static readonly Dictionary<RectTransform, Vector2> sizeOrig = new Dictionary<RectTransform, Vector2>();
    private static float parkExtraY;
    private static float parkBoxHalf;           // box half-height in screen pixels
    private static int parkLogs = 12;

    private static void CollectParked(Transform root, float extraY)
    {
        parked.Clear();
        parkedWritten.Clear();
        parkedPush.Clear();
        parkExtraY = extraY;
        Canvas canvas = root.GetComponent<Canvas>();
        float scale = canvas != null ? canvas.scaleFactor : 1f;
        parkBoxHalf = BOX_H * 0.5f * scale;
        WalkParked(root, Screen.height * 0.5f, 0);
    }

    // ui the game positions itself every frame (cursor, dragged item, tooltips) can sit
    // anywhere; it is never "parked", so its branches are skipped outright
    private static readonly string[] PARK_SKIP = { "Tooltips UI", "Virtual Mouse", "Gamepad Tools", "Draggable Item" };

    private static void WalkParked(Transform parent, float midY, int depth)
    {
        for (int i = 0; i < parent.childCount && parked.Count < MAX_PARKED; i++)
        {
            Transform c = parent.GetChild(i);
            if (depth == 0 && System.Array.IndexOf(PARK_SKIP, c.name) >= 0) continue;
            RectTransform rt = c as RectTransform;
            if (rt != null && c != letterboxFrame?.transform && rt.rect.width > 1f && rt.rect.height > 1f
                && rt.rect.height * rt.lossyScale.y < parkBoxHalf * 2f)
            {
                float minX, maxX, minY, maxY;
                Measure(rt, null, out minX, out maxX, out minY, out maxY);
                bool below = maxY <= midY - parkBoxHalf + 0.5f;
                bool above = minY >= midY + parkBoxHalf - 0.5f;
                if (below || above)
                {
                    // only slide-in panels, i.e. animated ones; a static rect outside the
                    // box is something the game placed there on purpose
                    if (!AnimatedNear(c)) continue;
                    parked.Add(rt);
                    parkedWritten.Add(rt.anchoredPosition);
                    parkedPush.Add(0f);
                    if (parkLogs > 0)
                    {
                        parkLogs--;
                        Debug.Log("[ChefRPG43] parked panel '" + c.name + "' y=[" + minY.ToString("F0") + "," + maxY.ToString("F0") + "]");
                    }
                    continue;                  // children ride along with it
                }
            }
            if (depth < 6) WalkParked(c, midY, depth + 1);
        }
    }

    // an Animator on the rect or one of its two parents (the canvas root excluded)
    private static bool AnimatedNear(Transform t)
    {
        for (int up = 0; up < 3 && t != null && t.parent != null; up++, t = t.parent)
            if (t.GetComponent("Animator") != null) return true;   // by name: AnimationModule is not referenced
        return false;
    }

    // takes the push back out, for a resize or scene change
    private static void RestoreParked()
    {
        for (int i = 0; i < parked.Count; i++)
        {
            RectTransform rt = parked[i];
            if (rt == null) continue;
            Vector2 cur = rt.anchoredPosition;
            if ((cur - parkedWritten[i]).sqrMagnitude < 0.0001f) rt.anchoredPosition = new Vector2(cur.x, cur.y - parkedPush[i]);
        }
        parked.Clear();
        parkedWritten.Clear();
        parkedPush.Clear();
        foreach (KeyValuePair<RectTransform, Vector2> e in sizeOrig)
        {
            if (e.Key != null) e.Key.sizeDelta = e.Value;
        }
        sizeOrig.Clear();
    }

    // runs every frame after animation; a handful of rects at most
    private static void KeepParked()
    {
        if (parked.Count == 0 || parkExtraY < 1f) return;
        float midY = Screen.height * 0.5f;
        for (int i = parked.Count - 1; i >= 0; i--)
        {
            RectTransform rt = parked[i];
            if (rt == null)
            {
                parked.RemoveAt(i);
                parkedWritten.RemoveAt(i);
                parkedPush.RemoveAt(i);
                continue;
            }
            if (!rt.gameObject.activeInHierarchy) continue;

            Vector2 cur = rt.anchoredPosition;
            // our push is still in place unless something (usually an Animator) rewrote it
            float baseY = (cur - parkedWritten[i]).sqrMagnitude < 0.0001f ? cur.y - parkedPush[i] : cur.y;
            Transform parent = rt.parent;
            float pxPerUnit = parent != null ? parent.lossyScale.y : 1f;
            if (pxPerUnit <= 0.0001f) continue;

            float minX, maxX, minY, maxY;
            Measure(rt, null, out minX, out maxX, out minY, out maxY);
            float shiftPx = (baseY - cur.y) * pxPerUnit;   // measure at the unpushed spot
            minY += shiftPx;
            maxY += shiftPx;

            float push = 0f;
            float boxBottom = midY - parkBoxHalf;
            float boxTop = midY + parkBoxHalf;
            if (maxY < boxBottom)
                push = -parkExtraY * Mathf.Clamp01((boxBottom - maxY) / (PARK_RAMP * pxPerUnit));
            else if (minY > boxTop)
                push = parkExtraY * Mathf.Clamp01((minY - boxTop) / (PARK_RAMP * pxPerUnit));

            Vector2 want = new Vector2(cur.x, baseY + push);
            if ((want - cur).sqrMagnitude > 0.0001f) rt.anchoredPosition = want;
            parkedWritten[i] = want;
            parkedPush[i] = push;
        }
    }

    // a centre-anchored rect whose edge sits on the 640x360 box edge moves out by the
    // canvas overhang on that side. full-height panels are left where they are.
    private static bool MoveToEdge(RectTransform rt, float extraX, float extraY)
    {
        if (rt == null) return false;
        Vector2 half = new Vector2(0.5f, 0.5f);
        if (rt.anchorMin != half || rt.anchorMax != half) return false;
        Vector2 size = rt.sizeDelta;
        if (size.y >= 300f) return false;

        Vector2 pos;
        if (!origPos.TryGetValue(rt, out pos)) pos = rt.anchoredPosition;
        Vector2 piv = rt.pivot;
        float top = pos.y + (1f - piv.y) * size.y;
        float bottom = pos.y - piv.y * size.y;
        float right = pos.x + (1f - piv.x) * size.x;
        float left = pos.x - piv.x * size.x;

        float dx = 0f, dy = 0f;
        if (Mathf.Abs(top - BOX_H * 0.5f) <= EDGE_SNAP) dy = extraY;
        else if (Mathf.Abs(bottom + BOX_H * 0.5f) <= EDGE_SNAP) dy = -extraY;
        if (Mathf.Abs(right - BOX_W * 0.5f) <= EDGE_SNAP) dx = extraX;
        else if (Mathf.Abs(left + BOX_W * 0.5f) <= EDGE_SNAP) dx = -extraX;
        if (dx == 0f && dy == 0f) return false;

        if (!origPos.ContainsKey(rt)) origPos[rt] = pos;
        Vector2 want = new Vector2(pos.x + dx, pos.y + dy);
        rt.anchoredPosition = want;
        layoutWant[rt] = want;
        if (layoutLogs > 0)
        {
            layoutLogs--;
            Debug.Log("[ChefRPG43] layout moved '" + rt.name + "' by " + dx.ToString("F0") + "," + dy.ToString("F0"));
        }
        return true;
    }

    // cheap steady-state check: the frame stays off, and a rect the game put back at its
    // authored spot goes back to the edge. a rect the game is animating elsewhere is left.
    private static void KeepLayout43()
    {
        if (letterboxFrame != null && letterboxFrame.enabled) letterboxFrame.enabled = false;
        if (layoutWant.Count == 0) return;
        List<RectTransform> dead = null;
        foreach (KeyValuePair<RectTransform, Vector2> e in layoutWant)
        {
            RectTransform rt = e.Key;
            if (rt == null)
            {
                if (dead == null) dead = new List<RectTransform>();
                dead.Add(rt);
                continue;
            }
            Vector2 orig;
            if (origPos.TryGetValue(rt, out orig) && (rt.anchoredPosition - orig).sqrMagnitude < 0.01f)
                rt.anchoredPosition = e.Value;
        }
        if (dead != null)
            for (int i = 0; i < dead.Count; i++) layoutWant.Remove(dead[i]);
    }

    // breadth-first by name, inactive objects included, at most `depth` levels down
    private static Transform FindDeep(Transform root, string name, int depth)
    {
        for (int i = 0; i < root.childCount; i++)
        {
            Transform c = root.GetChild(i);
            if (c.name == name) return c;
        }
        if (depth <= 1) return null;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDeep(root.GetChild(i), name, depth - 1);
            if (found != null) return found;
        }
        return null;
    }

    // put every rect we moved back where the game authored it. runs before the caches
    // are dropped on a resize or scene change: the scan that follows measures the new
    // frame from a clean slate, so a shift taken during a transient size cannot outlive
    // it. every entry is null-checked, because a scene change destroys most of them.
    private static void RestoreShifts()
    {
        foreach (KeyValuePair<RectTransform, Vector2> e in origPos)
        {
            RectTransform rt = e.Key;
            if (rt != null) rt.anchoredPosition = e.Value;
        }
        origPos.Clear();
        layoutWant.Clear();
        foreach (KeyValuePair<Transform, Vector3> e in scaleOrig)
        {
            if (e.Key != null) e.Key.localScale = e.Value;
        }
        scaleOrig.Clear();
        RestoreParked();

        RectTransform c = clock as RectTransform;
        if (c != null && clockOrigOf == clock)
        {
            c.anchorMin = clockOrigMin;
            c.anchorMax = clockOrigMax;
            c.pivot = clockOrigPivot;
            c.anchoredPosition = clockOrigPos;
        }
        clockOrigOf = null;
    }

    // a resize or scene change invalidates every cached transform and offset
    private static void ResetSceneCaches()
    {
        RestoreShifts();
        canvases = null;
        tracked.Clear();
        trackedSet.Clear();
        shiftCount.Clear();
        pending.Clear();
        givenUp.Clear();
        clock = null;
        prompts = null;
        promptsSlot = null;
        clockScale = -1f;
        hudSearched = false;
        matteSearched = false;
        matte = null;
        upperBar = null;
        lowerBar = null;
        ppSearched = false;
        ppc = null;
        ppCropEnforced = false;
        layoutDone = false;
        layoutTick = 0;
        renderDumpWait = RENDER_DUMP_FRAMES;
        viewLogged = false;
        clockLogWait = 0;
        clockPinW = 0;
        clockPinH = 0;
        hudTick = 0;        // re-resolve the hud on the next frame, not 120 frames later
        hudReresolve = 0;   // fresh dead-reference window for the new scene
    }

    private static void LogView(int w, int h)
    {
        float bar = ((float)w - (float)h * 4f / 3f) * 0.5f;
        if (bar < 1f) bar = 0f;
        Debug.Log("[ChefRPG43] clamp alive screen=" + w + "x" + h + " view=[" + bar.ToString("F0") + "," + ((float)w - bar).ToString("F0") + "]");
        Debug.Log("[ChefRPG43] display full=" + Screen.fullScreen + " mode=" + Screen.fullScreenMode
            + " sys=" + Display.main.systemWidth + "x" + Display.main.systemHeight);
    }

    // opt-in backbuffer captures: create chefrpg43_shots in persistentDataPath
    // before launching. this bypasses the compositor, so it shows exactly what
    // unity blits (including any pixel-perfect bars) even when x11grab is black.
    private static void CaptureProbe(int frame)
    {
        if (frame < 1 || frame > 600) return;
        if (!shotChecked)
        {
            shotChecked = true;
            string marker = Application.persistentDataPath + "/chefrpg43_shots";
            shotEnabled = System.IO.File.Exists(marker);
            Debug.Log("[ChefRPG43] probe path=" + Application.persistentDataPath + " marker=" + shotEnabled + " frame=" + frame);
        }
        if (!shotEnabled) return;
        // steamapi init can eat the whole run before frame 30, so take the first
        // frames unconditionally and then thin out. the first real frame is the one
        // that shows whether the pixel-perfect blit filled the panel.
        bool due = frame <= 10
            || (frame <= 60 && frame % 5 == 0)
            || (frame <= 300 && frame % 30 == 0)
            || frame == 600;
        if (!due) return;
        string path = Application.persistentDataPath + "/chefrpg43_live_" + frame + ".png";
        // the game quits on the steamapi failure before an async screenshot reaches
        // disk, so read the backbuffer and write the png in the same frame.
        try
        {
            Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
            if (tex == null)
            {
                Debug.Log("[ChefRPG43] shot null " + frame);
                return;
            }
            int tw = tex.width;
            int th = tex.height;
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.Destroy(tex);
            if (png == null || png.Length == 0)
            {
                Debug.Log("[ChefRPG43] shot encode failed " + frame);
                return;
            }
            System.IO.File.WriteAllBytes(path, png);
            Debug.Log("[ChefRPG43] shot " + frame + " " + tw + "x" + th + " bytes=" + png.Length + " full=" + Screen.fullScreen);
        }
        catch (System.Exception e)
        {
            Debug.Log("[ChefRPG43] shot error " + frame + " " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static void FullScan(float viewLeft, float viewRight)
    {
        canvases = Object.FindObjectsOfType<Canvas>();
        if (canvases == null) return;
        for (int i = 0; i < canvases.Length; i++)
        {
            Canvas canvas = canvases[i];
            if (canvas == null || !canvas.isRootCanvas) continue;
            if (canvas.renderMode == RenderMode.WorldSpace) continue;
            Walk(canvas.transform, CamFor(canvas, null), viewLeft, viewRight);
        }
    }

    private static void Walk(Transform parent, Camera cam, float viewLeft, float viewRight)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (!child.gameObject.activeInHierarchy) continue;   // inactive ui is not drawn
            RectTransform rt = child as RectTransform;
            if (rt == null)
            {
                Walk(child, cam, viewLeft, viewRight);
                continue;
            }

            // the pinned elements are owned by the dedicated pass; letting the generic
            // walk gate or shift their rects would fight it and can double-displace them.
            if (child == clock || child == promptsSlot) continue;

            Canvas nested = child.GetComponent<Canvas>();
            Camera nextCam = nested != null ? CamFor(nested, cam) : cam;

            // a shifted ancestor takes its children with it, so stop there
            if (Contain(rt, nextCam, viewLeft, viewRight)) continue;
            Walk(child, nextCam, viewLeft, viewRight);
        }
    }

    private static Camera CamFor(Canvas canvas, Camera fallback)
    {
        if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
        if (canvas.renderMode == RenderMode.WorldSpace) return fallback;
        return canvas.worldCamera != null ? canvas.worldCamera : fallback;
    }

    private static bool Contain(RectTransform rt, Camera cam, float viewLeft, float viewRight)
    {
        if (rt == null) return false;
        int taken;
        if (shiftCount.TryGetValue(rt, out taken) && taken >= MAX_SHIFTS) return true;

        float minX, maxX, minY, maxY;
        Measure(rt, cam, out minX, out maxX, out minY, out maxY);
        if (OffScreen(minX, maxX, minY, maxY)) return false;   // parked off screen: leave animations alone

        float dx, dy;
        bool crossing = Overhang(minX, maxX, minY, maxY, viewLeft, viewRight, out dx, out dy);
        if (!Gate(rt, crossing)) return false;
        if (!Shift(rt, cam, dx, dy)) return false;
        Track(rt);
        return true;
    }

    // entirely outside the window on either axis: an animation parked off screen
    private static bool OffScreen(float minX, float maxX, float minY, float maxY)
    {
        return maxX <= 0f || minX >= (float)curW || maxY <= 0f || minY >= (float)curH;
    }

    // how far a rect has to move to get back inside the view, per axis. x is bounded
    // by the centred 4:3 view, y by the window. an axis the rect spans edge to edge is a
    // background or a group, and is left alone on that axis.
    private static bool Overhang(float minX, float maxX, float minY, float maxY,
        float viewLeft, float viewRight, out float dx, out float dy)
    {
        dx = AxisOverhang(minX, maxX, viewLeft, viewRight);
        dy = AxisOverhang(minY, maxY, 0f, (float)curH);
        return dx != 0f || dy != 0f;
    }

    private static float AxisOverhang(float min, float max, float lo, float hi)
    {
        if (max - min >= hi - lo - 1f) return 0f;
        if (max > hi + 0.75f) return hi - max;
        if (min < lo - 0.75f) return lo - min;
        return 0f;
    }

    // a rect has to stay outside for two scans (~0.5s) before we move it, so a
    // slide-in animation is not frozen halfway through
    private static bool Gate(Transform t, bool crossing)
    {
        if (givenUp.Contains(t)) return false;
        if (!crossing)
        {
            pending.Remove(t);
            return false;
        }

        int frames;
        pending.TryGetValue(t, out frames);
        frames++;
        pending[t] = frames;
        if (frames < 2) return false;
        pending.Remove(t);
        return true;
    }

    private static void CheckTracked(float viewLeft, float viewRight)
    {
        for (int i = tracked.Count - 1; i >= 0; i--)
        {
            RectTransform rt = tracked[i];
            if (rt == null)
            {
                tracked.RemoveAt(i);
                continue;
            }
            if (!rt.gameObject.activeInHierarchy) continue;

            Canvas canvas = rt.GetComponentInParent<Canvas>();
            Camera cam = CamFor(canvas, null);
            float minX, maxX, minY, maxY;
            Measure(rt, cam, out minX, out maxX, out minY, out maxY);
            if (OffScreen(minX, maxX, minY, maxY)) continue;

            float dx, dy;
            bool crossing = Overhang(minX, maxX, minY, maxY, viewLeft, viewRight, out dx, out dy);
            if (!Gate(rt, crossing)) continue;
            Shift(rt, cam, dx, dy);
        }
    }

    private static void Track(RectTransform rt)
    {
        if (rt == null || tracked.Count >= MAX_TRACKED) return;
        if (trackedSet.Add(rt)) tracked.Add(rt);
    }

    private static void Measure(RectTransform rt, Camera cam, out float minX, out float maxX, out float minY, out float maxY)
    {
        rt.GetWorldCorners(corners);
        if (cam != null)
        {
            for (int i = 0; i < 4; i++)
                corners[i] = RectTransformUtility.WorldToScreenPoint(cam, corners[i]);
        }
        minX = corners[0].x;
        maxX = corners[0].x;
        minY = corners[0].y;
        maxY = corners[0].y;
        for (int i = 1; i < 4; i++)
        {
            float x = corners[i].x;
            float y = corners[i].y;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
    }

    private static void MeasureUnion(Transform root, Camera cam, out float minX, out float maxX, out float minY, out float maxY)
    {
        minX = float.MaxValue;
        maxX = float.MinValue;
        minY = float.MaxValue;
        maxY = float.MinValue;
        rectBuf.Clear();
        root.GetComponentsInChildren(false, rectBuf);
        for (int i = 0; i < rectBuf.Count; i++)
        {
            RectTransform rt = rectBuf[i];
            if (rt == null) continue;
            rt.GetWorldCorners(corners);
            for (int j = 0; j < 4; j++)
            {
                float x, y;
                if (cam == null)
                {
                    x = corners[j].x;
                    y = corners[j].y;
                }
                else
                {
                    Vector3 p = cam.WorldToScreenPoint(corners[j]);
                    if (p.z <= 0f) continue;
                    x = p.x;
                    y = p.y;
                }
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (minX > maxX || minY > maxY)
        {
            minX = 0f;
            maxX = 0f;
            minY = 0f;
            maxY = 0f;
        }
    }

    private static bool Shift(RectTransform rt, Camera cam, float dx, float dy)
    {
        Transform parent = rt.parent;
        if (parent == null) return false;

        float localX = 0f;
        float localY = 0f;
        bool converted = false;
        RectTransform parentRect = parent as RectTransform;
        if (parentRect != null)
        {
            Vector2 pivotScreen = RectTransformUtility.WorldToScreenPoint(cam, rt.position);
            Vector2 before, after;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, pivotScreen, cam, out before)
                && RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, new Vector2(pivotScreen.x + dx, pivotScreen.y + dy), cam, out after))
            {
                localX = after.x - before.x;
                localY = after.y - before.y;
                converted = true;
            }
        }
        if (!converted)
        {
            Vector3 scale = parent.lossyScale;
            if (scale.x <= 0.0001f || scale.y <= 0.0001f) return false;
            localX = dx / scale.x;
            localY = dy / scale.y;
        }

        // remember where the game put it, once, so a resize can put it back
        Vector2 pos = rt.anchoredPosition;
        if (!origPos.ContainsKey(rt)) origPos[rt] = pos;
        pos.x = pos.x + localX;
        pos.y = pos.y + localY;
        rt.anchoredPosition = pos;
        shifts++;

        int taken;
        shiftCount.TryGetValue(rt, out taken);
        taken++;
        shiftCount[rt] = taken;

        if (taken >= MAX_SHIFTS)
        {
            givenUp.Add(rt);
            trackedSet.Remove(rt);
            tracked.Remove(rt);
            if (shiftLogs > 0)
            {
                shiftLogs--;
                Debug.Log("[ChefRPG43] parked '" + rt.name + "' (game repositions it every frame)");
            }
            return true;
        }

        if (taken == 1 && shiftLogs > 0)
        {
            shiftLogs--;
            Debug.Log("[ChefRPG43] contained '" + rt.name + "' by " + dx.ToString("F1") + "," + dy.ToString("F1") + "px");
        }
        return true;
    }

    // the clock is built inside the centred design box, so on a wide window its top
    // right corner parks in the right pillar. pin it to the top right of the 4:3 view
    // once per canvas scale and window size; the game never writes this rect, so this is
    // a one-time write and containment below is then a no-op steady state.
    private static void PinClockIfNeeded(int w, int h, float viewRight)
    {
        if (clock == null) return;
        if (!clock.gameObject.activeInHierarchy) return;
        RectTransform rt = clock as RectTransform;
        if (rt == null) return;

        Canvas canvas = clock.GetComponentInParent<Canvas>();
        if (canvas == null) return;
        float scale = canvas.scaleFactor;
        bool anchored = rt.anchorMin == new Vector2(1f, 1f) && rt.anchorMax == new Vector2(1f, 1f)
            && rt.pivot == new Vector2(1f, 1f);
        if (anchored && Mathf.Abs(clockScale - scale) < 0.0001f && clockPinW == w && clockPinH == h) return;

        RectTransform parent = rt.parent as RectTransform;
        if (parent == null) return;

        // keep the authored layout the first time, so a resize can undo the pin
        if (clockOrigOf != clock)
        {
            clockOrigOf = clock;
            clockOrigMin = rt.anchorMin;
            clockOrigMax = rt.anchorMax;
            clockOrigPivot = rt.pivot;
            clockOrigPos = rt.anchoredPosition;
        }

        rt.anchorMin = new Vector2(1f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);

        Vector2 target = new Vector2(viewRight, (float)h - CLOCK_MARGIN_Y);
        Vector2 local;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, target, null, out local)) return;
        rt.anchoredPosition = local - parent.rect.max;
        clockScale = scale;
        clockPinW = w;
        clockPinH = h;
        clockLogWait = CLOCK_LOG_FRAMES;

        if (shiftLogs > 0)
        {
            shiftLogs--;
            Debug.Log("[ChefRPG43] pinned clock to top right (" + rt.anchoredPosition.x.ToString("F0") + "," + rt.anchoredPosition.y.ToString("F0") + ")");
        }
    }

    // where the clock actually ended up, a few frames after each pin so the canvas has
    // had time to rescale. the next device log can then confirm the clock is inside
    // [0,w]x[0,h] instead of inferring it from a screenshot.
    private static void LogClockRect()
    {
        if (clockLogWait <= 0 || clockRectLogs <= 0 || clock == null) return;
        if (--clockLogWait > 0) return;
        RectTransform rt = clock as RectTransform;
        if (rt == null) return;

        Canvas canvas = clock.GetComponentInParent<Canvas>();
        float minX, maxX, minY, maxY;
        Measure(rt, CamFor(canvas, null), out minX, out maxX, out minY, out maxY);
        clockRectLogs--;
        Debug.Log("[ChefRPG43] ui clock rect x=[" + minX.ToString("F0") + "," + maxX.ToString("F0")
            + "] y=[" + minY.ToString("F0") + "," + maxY.ToString("F0") + "] screen=" + curW + "x" + curH);
    }

    private static void ContainPinned(Transform measure, Transform move, float viewLeft, float viewRight)
    {
        if (measure == null || !measure.gameObject.activeInHierarchy) return;
        if (givenUp.Contains(move != null ? move : measure)) return;
        RectTransform mrt = measure as RectTransform;
        RectTransform rt = (move != null ? move : measure) as RectTransform;
        if (mrt == null || rt == null) return;

        Canvas canvas = measure.GetComponentInParent<Canvas>();
        Camera cam = CamFor(canvas, null);
        float minX, maxX, minY, maxY;
        MeasureUnion(measure, cam, out minX, out maxX, out minY, out maxY);

        if (OffScreen(minX, maxX, minY, maxY)) return;

        // a group that already spans the whole frame (side prompts on a match-width
        // canvas) cannot be slid as one rect: shifting it by the right-edge overhang
        // pushes the left edge into the other pillar. contain the individual prompt
        // rects instead - ScaleSideInputs only rewrites the group root, so moving the
        // children is stable across the game's own reset.
        bool spansX = maxX - minX >= viewRight - viewLeft - 1f;
        bool outsideX = maxX > viewRight + 0.75f || minX < viewLeft - 0.75f;
        if (spansX && outsideX)
        {
            ContainChildren(move != null ? move : measure, cam, viewLeft, viewRight);
            return;
        }

        float dx, dy;
        if (!Overhang(minX, maxX, minY, maxY, viewLeft, viewRight, out dx, out dy)) return;
        Shift(rt, cam, dx, dy);
    }

    // top-down containment for a group too wide to move in one piece: the first
    // rect that crosses the view is shifted and its subtree is left alone, because
    // moving an ancestor moves the children with it.
    private static void ContainChildren(Transform parent, Camera cam, float viewLeft, float viewRight)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (!child.gameObject.activeInHierarchy) continue;
            RectTransform rt = child as RectTransform;
            if (rt == null)
            {
                ContainChildren(child, cam, viewLeft, viewRight);
                continue;
            }
            if (givenUp.Contains(child)) continue;

            float minX, maxX, minY, maxY;
            Measure(rt, cam, out minX, out maxX, out minY, out maxY);
            if (OffScreen(minX, maxX, minY, maxY)) continue;

            float dx, dy;
            if (Overhang(minX, maxX, minY, maxY, viewLeft, viewRight, out dx, out dy))
            {
                if (Shift(rt, cam, dx, dy)) continue;
            }
            ContainChildren(child, cam, viewLeft, viewRight);
        }
    }

    // keyinputtooltip.ScaleSideInputs resets anchoredPosition to zero whenever it runs.
    // give the panel a slot we own; moving the slot survives the game's reset and costs
    // one write per resolution change instead of one per frame.
    private static void EnsurePromptsSlot()
    {
        if (prompts == null) return;
        RectTransform child = prompts as RectTransform;
        if (child == null) return;

        Transform current = prompts.parent;
        if (current != null && current.name == "ChefRPG43 HUD Slot")
        {
            promptsSlot = current;
            return;
        }

        RectTransform parent = current as RectTransform;
        if (parent == null) return;

        GameObject go = new GameObject("ChefRPG43 HUD Slot", typeof(RectTransform));
        RectTransform slot = go.GetComponent<RectTransform>();
        slot.SetParent(parent, false);
        slot.SetSiblingIndex(prompts.GetSiblingIndex());
        slot.anchorMin = child.anchorMin;
        slot.anchorMax = child.anchorMax;
        slot.pivot = child.pivot;
        slot.anchoredPosition = child.anchoredPosition;
        slot.sizeDelta = child.sizeDelta;
        slot.localScale = Vector3.one;

        child.SetParent(slot, false);
        child.anchorMin = Vector2.zero;
        child.anchorMax = Vector2.one;
        child.pivot = new Vector2(0.5f, 0.5f);
        child.anchoredPosition = Vector2.zero;
        child.offsetMin = Vector2.zero;
        child.offsetMax = Vector2.zero;

        promptsSlot = slot;
        if (shiftLogs > 0)
        {
            shiftLogs--;
            Debug.Log("[ChefRPG43] side prompts -> owned slot");
        }
    }

    private static void ResolveHud(int w, int h)
    {
        if (!hudSearched)
        {
            hudSearched = true;
            clock = FindByName("Time UI");
            prompts = FindSideInputs();
            EnsurePromptsSlot();
        }
        if (clock == null) clock = FindByName("Time UI");
        if (prompts == null)
        {
            prompts = FindSideInputs();
            EnsurePromptsSlot();
        }
        // a scene that loaded before the bar/matte or the camera existed has to be
        // able to find them later, or the 16:9 bars stay visible forever.
        if (matte == null) matteSearched = false;
        if (ppc == null) ppSearched = false;
    }

    private static Transform FindByName(string name)
    {
        GameObject go = GameObject.Find(name);
        return go != null ? go.transform : null;
    }

    private static Transform FindSideInputs()
    {
        try
        {
            System.Type t = System.Type.GetType("KeyInputTooltip, Assembly-CSharp");
            if (t != null)
            {
                UnityEngine.Object component = UnityEngine.Object.FindObjectOfType(t);
                if (component != null)
                {
                    FieldInfo f = t.GetField("SideKeyInputs", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null)
                    {
                        GameObject go = f.GetValue(component) as GameObject;
                        if (go != null) return go.transform;
                    }
                }
            }
        }
        catch (System.Exception)
        {
        }

        GameObject found = GameObject.Find("SideKeyInputs");
        return found != null ? found.transform : null;
    }

    // letterboxing's two bars are authored for a 16:9 frame, so on a 4:3 view they sit
    // inside the picture. hide the objects once per scene and keep them hidden if a
    // cutscene turns them back on.
    private static void HideMatteBars()
    {
        if (!matteSearched)
        {
            matteSearched = true;
            try
            {
                System.Type t = System.Type.GetType("Letterboxing, Assembly-CSharp");
                if (t == null) return;
                matte = UnityEngine.Object.FindObjectOfType(t);
                if (matte == null) return;
                FieldInfo u = t.GetField("UpperImage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                FieldInfo l = t.GetField("LowerImage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (u != null) upperBar = u.GetValue(matte) as GameObject;
                if (l != null) lowerBar = l.GetValue(matte) as GameObject;
            }
            catch (System.Exception)
            {
                return;
            }
        }

        if (upperBar != null && upperBar.activeSelf) upperBar.SetActive(false);
        if (lowerBar != null && lowerBar.activeSelf) lowerBar.SetActive(false);
    }

    // safety net for a serialized 16:9 reference (640x360 / 800x450). keep the
    // authored height for crisp integer zoom, but make the ratio 4:3.
    private static void EnforcePpRefs()
    {
        if (!ppSearched)
        {
            ppSearched = true;
            Camera cam = Camera.main;
            if (cam != null) ppc = cam.GetComponent("PixelPerfectCamera");
            if (ppc == null)
            {
                System.Type t = System.Type.GetType("UnityEngine.Experimental.Rendering.Universal.PixelPerfectCamera, Unity.RenderPipelines.Universal.Runtime");
                if (t != null) ppc = UnityEngine.Object.FindObjectOfType(t) as Component;
            }
        }
        if (ppc == null) return;

        if (!ppCropEnforced)
        {
            ppCropEnforced = true;
            SetBoolProp(ppc, "cropFrameX", true);
            SetBoolProp(ppc, "cropFrameY", true);
            SetBoolProp(ppc, "stretchFill", true);
        }

        RebasePp(ppc);
    }

    // keep the authored height for crisp integer zoom, but make the ratio 4:3
    private static void RebasePp(Component pp)
    {
        int rx, ry;
        if (!GetIntProp(pp, "refResolutionX", out rx)) return;
        if (!GetIntProp(pp, "refResolutionY", out ry)) return;
        if (ry <= 0 || rx * 3 == ry * 4) return;

        int newY = ry * 4 / 3;
        if (newY == ry) newY = ry + 1;
        int newX = newY * 4 / 3;
        SetIntProp(pp, "refResolutionX", newX);
        SetIntProp(pp, "refResolutionY", newY);
        if (shiftLogs > 0)
        {
            shiftLogs--;
            Debug.Log("[ChefRPG43] pp reference " + rx + "x" + ry + " -> " + newX + "x" + newY
                + " on '" + pp.name + "' (" + pp.GetType().Name + ")");
        }
    }

    // every camera, every pixel-perfect component on it (urp's and the 2d package's) and
    // every root canvas, so one device log names the layer that draws the 16:9 band
    private static void DumpRenderPath()
    {
        if (renderDumps <= 0) return;
        renderDumps--;
        try
        {
            Debug.Log("[ChefRPG43] render screen=" + Screen.width + "x" + Screen.height
                + " full=" + Screen.fullScreen + " mode=" + Screen.fullScreenMode);

            Camera[] cams = Camera.allCameras;
            for (int i = 0; i < cams.Length; i++)
            {
                Camera c = cams[i];
                if (c == null) continue;
                RenderTexture tt = c.targetTexture;
                Debug.Log("[ChefRPG43] render cam '" + c.name + "' depth=" + c.depth + " rect=" + c.rect
                    + " pix=" + c.pixelRect + " tex=" + (tt != null ? tt.name + " " + tt.width + "x" + tt.height : "screen")
                    + " clear=" + c.clearFlags + " ortho=" + c.orthographicSize);

                Component[] comps = c.GetComponents<Component>();
                for (int j = 0; j < comps.Length; j++)
                {
                    Component pp = comps[j];
                    if (pp == null || pp.GetType().Name != "PixelPerfectCamera") continue;
                    int rx, ry, ppu;
                    GetIntProp(pp, "refResolutionX", out rx);
                    GetIntProp(pp, "refResolutionY", out ry);
                    GetIntProp(pp, "assetsPPU", out ppu);
                    Behaviour b = pp as Behaviour;
                    Debug.Log("[ChefRPG43] render   pp " + pp.GetType().FullName
                        + " enabled=" + (b != null && b.enabled) + " ref=" + rx + "x" + ry + " ppu=" + ppu
                        + " cropX=" + GetBoolProp(pp, "cropFrameX") + " cropY=" + GetBoolProp(pp, "cropFrameY")
                        + " stretch=" + GetBoolProp(pp, "stretchFill") + " upscale=" + GetBoolProp(pp, "upscaleRT"));
                }
            }

            Canvas[] all = Object.FindObjectsOfType<Canvas>();
            for (int i = 0; i < all.Length; i++)
            {
                Canvas cv = all[i];
                if (cv == null || !cv.isRootCanvas || !cv.isActiveAndEnabled) continue;
                Debug.Log("[ChefRPG43] render canvas '" + cv.name + "' mode=" + cv.renderMode
                    + " cam=" + (cv.worldCamera != null ? cv.worldCamera.name : "null")
                    + " order=" + cv.sortingOrder + " pix=" + cv.pixelRect + " scale=" + cv.scaleFactor.ToString("F3"));
            }

            Debug.Log("[ChefRPG43] render matte found=" + (matte != null)
                + " upper=" + (upperBar != null ? upperBar.activeSelf.ToString() : "null")
                + " lower=" + (lowerBar != null ? lowerBar.activeSelf.ToString() : "null"));
            DumpEdgeGraphics();
        }
        catch (System.Exception e)
        {
            Debug.Log("[ChefRPG43] render dump error " + e.GetType().Name + ": " + e.Message);
        }
    }

    // every camera and canvas reports the full 4:3 frame, yet the device shows black over
    // the top and bottom 120px. anything drawing that has to be a visible ui graphic
    // spanning most of the width across one of those strips, so list exactly those.
    private static void DumpEdgeGraphics()
    {
        int w = Screen.width;
        int h = Screen.height;
        float strip = h * 0.125f;   // 120px on the 960-tall panel
        int budget = 40;
        UnityEngine.UI.Graphic[] gs = Object.FindObjectsOfType<UnityEngine.UI.Graphic>();
        for (int i = 0; i < gs.Length && budget > 0; i++)
        {
            UnityEngine.UI.Graphic g = gs[i];
            if (g == null || !g.isActiveAndEnabled || g.color.a < 0.5f) continue;
            Canvas canvas = g.canvas;
            if (canvas == null || canvas.renderMode == RenderMode.WorldSpace) continue;

            float minX, maxX, minY, maxY;
            Measure(g.rectTransform, CamFor(canvas, null), out minX, out maxX, out minY, out maxY);
            if (maxX - minX < w * 0.8f) continue;
            bool top = maxY > h - strip + 1f && minY < h - 1f;
            bool bottom = minY < strip - 1f && maxY > 1f;
            if (!top && !bottom) continue;

            budget--;
            Transform t = g.transform;
            string path = t.name;
            for (int up = 0; up < 3 && t.parent != null; up++)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            UnityEngine.UI.Image img = g as UnityEngine.UI.Image;
            Canvas root = canvas.rootCanvas;
            Debug.Log("[ChefRPG43] render edge " + (top ? "top" : "") + (bottom ? "bottom" : "") + " '" + path + "' "
                + g.GetType().Name + " color=" + g.color + " sprite=" + (img != null && img.sprite != null ? img.sprite.name : "none")
                + " x=[" + minX.ToString("F0") + "," + maxX.ToString("F0") + "] y=[" + minY.ToString("F0") + "," + maxY.ToString("F0") + "]"
                + " canvas='" + (root != null ? root.name : "?") + "' order=" + (root != null ? root.sortingOrder : 0)
                + " depth=" + g.depth);
        }
        if (budget == 40) Debug.Log("[ChefRPG43] render edge none: no visible ui graphic covers the top/bottom strips");
    }

    private static bool GetBoolProp(object o, string name)
    {
        try
        {
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) return false;
            object v = p.GetValue(o, null);
            return v is bool && (bool)v;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    private static bool GetIntProp(object o, string name, out int value)
    {
        value = 0;
        try
        {
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) return false;
            value = System.Convert.ToInt32(p.GetValue(o, null));
            return true;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    private static bool SetIntProp(object o, string name, int value)
    {
        try
        {
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null || !p.CanWrite) return false;
            p.SetValue(o, value, null);
            return true;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    private static bool SetBoolProp(object o, string name, bool value)
    {
        try
        {
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null || !p.CanWrite) return false;
            p.SetValue(o, value, null);
            return true;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    // ---- display policy: the unity calls behind DisplayPolicy.cs ----------------------
    //
    // fullscreen is borderless at the display's own size; windowed is a 4:3 window. the
    // game's settings panel calls ApplyFullscreen / ApplyWindowed instead of
    // Screen.SetResolution, and DisplayWatchdog (LetterBoxer.Update, plus the first title
    // frame) keeps the screen where the last choice put it.

    // the display's native size, with the same fallbacks the old il used when the
    // container reports nothing sensible
    private static void SysSize(out int sw, out int sh)
    {
        sw = Display.main.systemWidth;
        sh = Display.main.systemHeight;
        if (sw < 320) sw = 1280;
        if (sh < 240) sh = 960;
    }

    public static void ApplyFullscreen()
    {
        int sw, sh;
        SysSize(out sw, out sh);
        wantInit = true;
        wantWindowed = false;
        wantW = sw;
        wantH = sh;
        // no Screen.fullScreen = true first: that alone made the container switch to an
        // exclusive 16:9 mode and stay there
        Screen.SetResolution(sw, sh, FullScreenMode.FullScreenWindow);
        Persist(sw, sh, MODE_FULLSCREEN_WINDOW);
        LogDisplay("fullscreen " + sw + "x" + sh);
    }

    public static void ApplyWindowed(int w, int h)
    {
        int sw, sh, cw, ch;
        SysSize(out sw, out sh);
        Coerce43(w, h, sw, sh, out cw, out ch);
        wantInit = true;
        wantWindowed = true;
        wantW = cw;
        wantH = ch;
        Screen.SetResolution(cw, ch, FullScreenMode.Windowed);
        Persist(cw, ch, MODE_WINDOWED);
        LogDisplay("windowed " + cw + "x" + ch + " (asked " + w + "x" + h + ")");
    }

    // the picker's list: only 4:3 sizes that fit the display, replacing Screen.resolutions
    public static Resolution[] Resolutions43()
    {
        Resolution[] src = Screen.resolutions;
        int[] heights = new int[src.Length];
        int rate = 60;
        for (int i = 0; i < src.Length; i++)
        {
            heights[i] = src[i].height;
            rate = src[i].refreshRate;
        }

        int sw, sh;
        SysSize(out sw, out sh);
        int[] flat = Modes43(heights, sw, sh);
        Resolution[] list = new Resolution[flat.Length / 2];
        for (int i = 0; i < list.Length; i++)
        {
            list[i].width = flat[i * 2];
            list[i].height = flat[i * 2 + 1];
            list[i].refreshRate = rate;
        }
        return list;
    }

    // a saved 1280x720 becomes 960x720, so the panel label and the window agree on 4:3
    public static Resolution Coerce43(Resolution r)
    {
        int sw, sh, ow, oh;
        SysSize(out sw, out sh);
        Coerce43(r.width, r.height, sw, sh, out ow, out oh);
        r.width = ow;
        r.height = oh;
        return r;
    }

    // every ~2s: ask the engine for whatever the last choice was, but only when the screen
    // has drifted from it (a stale 16:9 window, a container that clamped the first
    // request, the player dragging a window corner). a screen that is already right costs
    // nothing. gap starts at zero, so the first call - the title scene's Awake - runs now.
    public static void DisplayWatchdog()
    {
        if (--gap > 0) return;
        gap = WATCHDOG_FRAMES;

        int sw, sh;
        SysSize(out sw, out sh);
        if (!wantInit) InitWantFromPrefs(sw, sh);

        int rw, rh, mode;
        if (!NextRequest(wantWindowed, wantW, wantH, Screen.width, Screen.height, Screen.fullScreen,
                sw, sh, out rw, out rh, out mode))
            return;

        Screen.SetResolution(rw, rh, (FullScreenMode)mode);
        if (mode == MODE_WINDOWED)
        {
            wantW = rw;
            wantH = rh;
        }
        Persist(rw, rh, mode);
        LogDisplay("watchdog " + (mode == MODE_WINDOWED ? "windowed " : "fullscreen ") + rw + "x" + rh
            + " (screen " + Screen.width + "x" + Screen.height + " full=" + Screen.fullScreen + ")");
    }

    // before the settings panel has run, trust the mode unity restored from the previous
    // session so a windowed boot is not flipped to fullscreen for a few frames. the panel's
    // own LoadSettings overrides this the moment the title scene loads.
    private static void InitWantFromPrefs(int sw, int sh)
    {
        wantInit = true;
        try
        {
            if (PlayerPrefs.GetInt("Screenmanager Fullscreen mode", MODE_FULLSCREEN_WINDOW) == MODE_WINDOWED)
            {
                int pw, ph;
                Coerce43(0, PlayerPrefs.GetInt("Screenmanager Resolution Height", 0), sw, sh, out pw, out ph);
                wantWindowed = true;
                wantW = pw;
                wantH = ph;
            }
        }
        catch (System.Exception)
        {
        }
    }

    // insurance for a killed session. unity writes its Screenmanager prefs itself on a
    // clean quit, but the container may kill the game instead, and then a windowed choice
    // or size change made this session is lost. writing the same keys on every change
    // (the device registry shows unity reads exactly these names) means the next boot
    // starts from the last choice rather than from an older clean exit.
    private static void Persist(int w, int h, int mode)
    {
        if (w == persistW && h == persistH && mode == persistMode) return;
        try
        {
            PlayerPrefs.SetInt("Screenmanager Resolution Width", w);
            PlayerPrefs.SetInt("Screenmanager Resolution Height", h);
            PlayerPrefs.SetInt("Screenmanager Fullscreen mode", mode);
            PlayerPrefs.SetInt("Screenmanager Resolution Use Native", 0);
            PlayerPrefs.Save();
            persistW = w;
            persistH = h;
            persistMode = mode;
        }
        catch (System.Exception)
        {
        }
    }

    private static void LogDisplay(string what)
    {
        if (displayLogs <= 0) return;
        displayLogs--;
        Debug.Log("[ChefRPG43] display " + what);
    }
}
