// the 4:3 display policy as pure integer functions, so the exact code that ships in
// the game assembly is also compiled into verify/ and driven by the simulator.
//
// no usings on purpose. nothing here may touch UnityEngine (verify has no unity), and
// nothing may touch System.Math: the cloned helper is imported into the game assembly
// and a stray Math call binds to the patcher's own runtime (System.Private.CoreLib),
// which unity's mono cannot load - the v5.4 failure. integer arithmetic only.
//
// the unity-facing half (state, SetResolution, PlayerPrefs, the watchdog) lives in
// ChefRPG43SafeArea.cs.
public static partial class ChefRPG43SafeArea
{
    // smallest window height we hand out. the game's ui is authored around 640x480.
    private const int MIN_HEIGHT_43 = 480;
    private const int MAX_MODES_43 = 10;   // the resolution picker has ten buttons

    // FullScreenMode values, spelled out so this file needs no unity types
    public const int MODE_FULLSCREEN_WINDOW = 1;
    public const int MODE_WINDOWED = 3;

    // width for a 4:3 frame of height h, rounded to the nearest even number so a
    // window never lands on an odd pixel column. 700 -> 934, 960 -> 1280, 720 -> 960.
    private static int Wide43(int h)
    {
        return ((h * 4 + 3) / 6) * 2;
    }

    // keeps the requested height and derives a 4:3 width from it. the height is held
    // to [480, sysH]; if the width then overflows the display, both sides are scaled
    // down from the display width instead.
    public static void Coerce43(int w, int h, int sysW, int sysH, out int ow, out int oh)
    {
        // w is accepted so callers can pass a whole Resolution through, but 4:3 is
        // decided by the height alone
        int height = h < MIN_HEIGHT_43 ? MIN_HEIGHT_43 : h;
        if (height > sysH) height = sysH;
        if (height < 2) height = 2;
        int width = Wide43(height);
        if (width > sysW)
        {
            width = sysW & ~1;
            height = (width * 3 + 2) / 4;
        }
        ow = width;
        oh = height;
    }

    // every 4:3 size the picker may offer, as a flat w0,h0,w1,h1... array in ascending
    // order. the heights come from the 16:9 list the engine reports (so the picker
    // keeps the same steps), plus the usual 4:3 heights and the largest size that fits.
    // capped at ten entries, keeping the largest.
    public static int[] Modes43(int[] heights, int sysW, int sysH)
    {
        int cap = (heights == null ? 0 : heights.Length) + 5;
        int[] hs = new int[cap];
        int n = 0;

        if (heights != null)
        {
            for (int i = 0; i < heights.Length; i++)
                n = AddHeight(hs, n, heights[i], sysW, sysH);
        }
        n = AddHeight(hs, n, 480, sysW, sysH);
        n = AddHeight(hs, n, 600, sysW, sysH);
        n = AddHeight(hs, n, 768, sysW, sysH);
        n = AddHeight(hs, n, 960, sysW, sysH);

        // the largest 4:3 frame the display can hold, e.g. 1772x1329 on a 16:10 mac
        int topW, topH;
        Coerce43(0, sysH, sysW, sysH, out topW, out topH);
        n = AddHeight(hs, n, topH, sysW, sysH);

        // insertion sort, ascending
        for (int i = 1; i < n; i++)
        {
            int v = hs[i];
            int j = i - 1;
            while (j >= 0 && hs[j] > v)
            {
                hs[j + 1] = hs[j];
                j--;
            }
            hs[j + 1] = v;
        }

        int start = n > MAX_MODES_43 ? n - MAX_MODES_43 : 0;
        int[] flat = new int[(n - start) * 2];
        for (int i = start; i < n; i++)
        {
            int ow, oh;
            Coerce43(0, hs[i], sysW, sysH, out ow, out oh);
            flat[(i - start) * 2] = ow;
            flat[(i - start) * 2 + 1] = oh;
        }
        return flat;
    }

    // adds a candidate height once, if a 4:3 frame of that height fits the display
    private static int AddHeight(int[] hs, int n, int h, int sysW, int sysH)
    {
        if (h < MIN_HEIGHT_43 || h > sysH || Wide43(h) > sysW) return n;
        for (int i = 0; i < n; i++)
            if (hs[i] == h) return n;
        if (n >= hs.Length) return n;
        hs[n] = h;
        return n + 1;
    }

    // |lhs - rhs| * 200 <= lhs + rhs  <=>  the two aspects agree within 0.5%
    private static bool SameAspect(int aw, int ah, int bw, int bh)
    {
        long lhs = (long)aw * bh;
        long rhs = (long)bw * ah;
        long diff = lhs - rhs;
        if (diff < 0) diff = -diff;
        return diff * 200 <= lhs + rhs;
    }

    // one watchdog tick. returns true when the engine has to be asked for something and
    // fills rw/rh/mode with the request. steady state returns false, so a window that is
    // already right costs no SetResolution calls at all.
    //   fullscreen wanted: borderless at the display's own size. anything else (a window,
    //     or a frame whose aspect is off the display's) is wrong.
    //   windowed wanted: a decorated 4:3 window. fullscreen is wrong, and so is a window
    //     that is not 4:3 - the user dragging a corner re-requests at the height they
    //     chose rather than snapping back to the old size.
    public static bool NextRequest(bool wantWindowed, int wantW, int wantH, int scrW, int scrH,
        bool isFull, int sysW, int sysH, out int rw, out int rh, out int mode)
    {
        rw = 0;
        rh = 0;
        mode = 0;
        if (scrW <= 0 || scrH <= 0 || sysW <= 0 || sysH <= 0) return false;

        if (!wantWindowed)
        {
            if (isFull && SameAspect(scrW, scrH, sysW, sysH)) return false;
            rw = sysW;
            rh = sysH;
            mode = MODE_FULLSCREEN_WINDOW;
            return true;
        }

        if (!isFull && SameAspect(scrW, scrH, 4, 3)) return false;
        // coming back from fullscreen the screen height is the display's, not the
        // window the player picked, so fall back to the remembered size
        int h = (isFull && wantH > 0) ? wantH : scrH;
        Coerce43(0, h, sysW, sysH, out rw, out rh);
        mode = MODE_WINDOWED;
        return true;
    }
}
