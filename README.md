# Chef RPG 4:3 Aspect Ratio Patch

Native 4:3 aspect ratio patch for **Chef RPG**, aimed at the **Retroid Pocket Nova** (4.5" 4:3 AMOLED, Windows build via Wine / ARM64 Proton) and any other 4:3 or non-16:9 display.

The patcher rewrites `Assembly-CSharp.dll` at the IL level with [Mono.Cecil](https://github.com/jbevain/cecil). **No copyrighted game code is redistributed** — the tool ships only the patch logic and modifies your own legally owned copy.

> Unofficial fan project, not affiliated with or endorsed by World 2 Studio Inc. Chef RPG and its files belong to them. The code here is MIT licensed ([LICENSE](LICENSE)).

## Install (players)

You patch your own copy of the game's `Assembly-CSharp.dll`; nothing from the game is shipped here.

- **Web patcher** (any browser, including your phone): <https://jevonlipsey.github.io/chefrpg-43aspect/> — drop in your dll, download the patched one. Runs locally, nothing is uploaded.
- **Desktop patcher** (no .NET needed): download `chefrpg-43aspect-<version>-windows-x64.zip`, `-macos-arm64.zip`, `-macos-x64.zip` or `-linux-x64.zip` from the [Releases page](https://github.com/jevonlipsey/chefrpg-43aspect/releases). **On Windows, drag `Assembly-CSharp.dll` (from `Chef RPG_Data\Managed`) onto `patcher.exe`**, then rename: the original to `Assembly-CSharp.dll.bak` and the new `Assembly-CSharp-43.dll` to `Assembly-CSharp.dll`. On macOS / Linux run `./patcher "/path/to/Managed"`. The releases contain only the patcher, never a game file.
- **`python3 patch.py "/path/to/Chef RPG_Data/Managed"`**: patches, verifies, backs up the original to `Assembly-CSharp.dll.bak` and installs.

Step-by-step instructions, per-platform folder locations, Android handheld notes (DroidDeck, GameNative), display settings and troubleshooting are in **[docs/INSTALL.md](docs/INSTALL.md)**.

## Why the game letterboxes

Two separate things pin the game to 16:9:

1. **`LetterBoxer`** (game code) hardcodes a 16:9 matte (`x = 16, y = 9`) and shrinks `cam.rect` every `Update` to fit a 16:9 box. On a 4:3 panel that *is* the top/bottom black bars.
2. **Fullscreen resolution.** `SettingsPanel.FullscreenResolution()` calls `Screen.SetResolution(Screen.resolutions[last])`. The last entry is a 16:9 mode, so on a 4:3 panel the whole 16:9 framebuffer gets shown with bars — and the Unity UI canvas, which fills that framebuffer, spills its buttons into the black area.

Fixing only the camera made this worse: cropping to 4:3 *inside* a 16:9 framebuffer produces a double box. The fix is to make the framebuffer 4:3 in the first place.

### If the panel still shows bars: the container, not the game

On a 4:3 panel the game can only fill the screen if the OS/container hands it a 4:3 surface. In
DroidDeck's Display settings the container reported `1280x960` while the game's own framebuffer was
`1280x720`, and every mode the game could choose from was 16:9 too. No in-game change can beat that.

Fix it in DroidDeck first — the display settings cog has **Resolution** (`Match screen (1280x960)` /
720p / 1080p / …) and **16:9 game fit** (`Keep proportions` / `Stretch`). On the Retroid Pocket Nova
(native **1280x960**, 4:3):

- Resolution → **Match screen (1280x960)**
- 16:9 game fit → **off / Stretch**. *Keep proportions* letterboxes the game into a 16:9 box, which
  is the black bars — it is the right setting for an unpatched 16:9 game, and the wrong one for a
  game that renders 4:3 itself.

Both apply on the *next* session, so fully exit and relaunch DroidDeck and the game after changing
them. The game will then see a 1280x960 display and the patch fills the panel. The runtime dump
below records the camera's real rect, so you can tell a container-side letterbox (camera rect already
covers the whole screen) from a game-side one.


## What the patch changes (all in `Assembly-CSharp.dll`)

1. **Fullscreen resolution** — `SettingsPanel.FullscreenResolution()` switches from exclusive
   fullscreen (which requests a display-mode change; the container answers with a 16:9 mode) to
   **borderless** fullscreen at the panel's own size, via the injected helper's `ApplyFullscreen()`:
   `Screen.SetResolution(Display.main.systemWidth, Display.main.systemHeight, FullScreenMode.FullScreenWindow)`.
   The `Screen.fullScreen = true` that used to run first is gone — that call alone was enough to
   move the container into an exclusive 16:9 mode. The method keeps writing the diagnostic to
   `<persistentDataPath>/chefrpg43.txt` describing what the display reports.
   `LoadSettings()` coerces the saved resolution to 4:3 (a saved 1280x720 becomes 960x720) so the
   panel label and the window agree; it no longer forces `WindowedMode = false`.
2. **Display watchdog** — containers only expose extra Xwayland modes *after* a client asks for
   them, so the boot-time `SetResolution` can land before the 4:3 mode exists and Unity silently
   keeps the 16:9 mode it clamped to. `ChefRPG43SafeArea.DisplayWatchdog()` runs from
   `LetterBoxer.Update` (present in every scene) and from the head of `StartMenuManager.Awake`
   (the first title frame, before `LoadSettings`). Every ~2 seconds (120 frames) it compares the
   screen with what the last fullscreen/windowed choice asked for and re-requests it only if they
   differ, so a screen that is already right costs no `SetResolution` calls at all. The policy
   (`DisplayPolicy.cs`) is plain integer code; the verifier compiles that same file and drives it.
3. **`LetterBoxer`** — `PerformSizing()` becomes `cam.rect = (0, 0, 1, 1)` and
   `AddLetterBoxingCamera()` becomes a no-op; `onUpdate = true` is enforced in `Awake` so the
   watchdog is never disabled by game logic.
4. **`CameraFollow`** — pixel-perfect reference resolutions are re-based to 4:3
   (`640x360` → `640x480` zoom-in, `800x450` → `800x600` zoom-out) and left **fixed**; with
   `cropFrameX/Y` + `stretchFill` on, the pixel-perfect blit aspect-fits the 4:3 render into the
   framebuffer: on the 1280x960 panel it fills edge to edge, on a wider screen (mac) it shows true
   4:3 pillarboxed instead of widening the world view. SafeHelper enforces `cropFrameX/Y = true` and
   `stretchFill = true` on the active PixelPerfectCamera component once per scene.
5. **Windowed mode is a real 4:3 window** — `WindowedMode()` and `SetResolutionValue()` keep their
   original bodies (button sprites, label, popup close); only their raw
   `Screen.SetResolution(w, h, false)` becomes `ApplyWindowed(w, h)`, as does the `LoadSettings()`
   windowed branch. `ApplyWindowed` coerces to the nearest 4:3 size (height kept, width even,
   fitted to the display), asks for `FullScreenMode.Windowed`, and tells the watchdog that a window
   is what the player wants, so the watchdog stops forcing fullscreen. If the player drags a corner
   to a non-4:3 size, the watchdog re-requests a 4:3 window at the height they chose. (v5.6
   redirected these to `FullscreenMode()`, which is why the Windowed button did nothing.)
   `ChangeResolution()` lists `Resolutions43()` instead of `Screen.resolutions`, so the picker
   shows only 4:3 sizes that fit the display (at most the picker's 10 buttons).
   The settings panel hides the Windowed button on the Mac player (`Application.platform == 1`),
   so the windowed path is exercised on Windows/Wine builds.
6. **`Formulas`** — `GetActualScreenSize()` now returns the largest 4:3 rect inside the screen
   (`Screen.height*4/3, Screen.height`), and `LocateScreenPosition()` / `GetCornerOffset()` remap
   positions into that view (the original 16:9 math had a dead integer-division aspect compare).
   Without this, the virtual cursor and HUD tooltips anchor to the pillars.
7. **Tooltips / dialogue / `UILineRenderer`** — hardcoded `9f/16f` and `1.7777778f`
   aspect literals become `3f/4f` / `4f/3f`.
8. **UI containment** — `ChefRPG43SafeArea` (injected from the embedded `SafeHelper.dll`, run from
   `LetterBoxer.LateUpdate`) keeps the always-on HUD inside the centred 4:3 view: the clock is pinned
   to the top right, the side key prompts live under an owned slot, and any other rect that stays
   outside for two scans is nudged back, on x (the 4:3 view) and on y (the window). Every shift is
   reversible: a resize or scene change restores each moved rect to its authored position before
   the caches are dropped, so a transient size (the 960 -> 720 -> 960 flip seen in an earlier
   device log) cannot leave the HUD displaced afterwards. Each pin also logs the clock's screen
   rect (`[ChefRPG43] ui clock rect ...`) so a log shows whether it is inside the window. The
   helper's try/catch blocks are carried over when it is cloned into the game assembly.
9. **Persisted size** — `ApplyFullscreen`/`ApplyWindowed` (and watchdog requests) also write Unity's
   own `Screenmanager Resolution Width/Height`, `Screenmanager Fullscreen mode` and
   `Screenmanager Resolution Use Native` PlayerPrefs. Unity normally writes these on a clean quit; if the
   container kills the game instead, a size or windowed choice made that session would be lost. This
   is insurance for that case. If the game ever boots at the wrong size anyway, the
   Steam launch options `-screen-width 1280 -screen-height 960 -window-mode borderless` set it before
   any game code runs.

Nothing outside `Assembly-CSharp.dll` is touched. (An earlier attempt also patched
`Unity.RenderPipelines.Universal.Runtime.dll`; that broke rendering and has been removed.)

## Usage

```
patcher <inputDll> [managedDir] [outputDll]
patcher <managedDir>            # input = <managedDir>/Assembly-CSharp.dll, output = <managedDir>/Assembly-CSharp-43.dll
```

- `managedDir` is optional. It supplies the Unity assemblies; they bind by name, so any Unity
  install's `Managed` folder works. **With it the patcher runs the full verifier** (IL, semantics,
  simulation, helper integrity). **Without it** it runs a reference-free subset of the checks (this
  is what the browser patcher does).
- The output file is written **only if verification passed**. A failed run leaves nothing behind.
- Exit codes: `0` ok, `1` verification failed or error, `3` input is already patched,
  `4` input is not Chef RPG's `Assembly-CSharp.dll`.

```bash
# windows
dist\win\patcher.exe "C:\path\to\Chef RPG_Data\Managed"
# macos (apple silicon / intel)
dist/mac/patcher "/path/to/Chef RPG_Data/Managed"
dist/mac-x64/patcher "/path/to/Chef RPG_Data/Managed"
# linux
dist/linux/patcher "/path/to/Chef RPG_Data/Managed"
# explicit paths
patcher Assembly-CSharp.dll "/path/to/Chef RPG_Data/Managed" Assembly-CSharp-43.dll
```

`patch.py` is a stdlib-only launcher (Python 3.8+, no .NET SDK needed). It finds a prebuilt patcher
for the host (`./patcher[.exe]` next to the script, or `dist/<mac|mac-x64|win|linux>/`), falling
back to `dotnet run --project patcher` if the SDK is installed, or pointing you at the web patcher
and Releases page otherwise. By default it patches into a temp file, and only if the patcher exits 0
it copies the original to `Assembly-CSharp.dll.bak` (never overwriting an existing one) and
replaces `Assembly-CSharp.dll`, printing the sha256. `--no-install` just writes
`Assembly-CSharp-43.dll` next to the original; `--dotnet` forces the SDK route.
`CHEFRPG43_PATCHER=/path/to/patcher` overrides binary discovery.

After writing, the patcher re-reads its own output and aborts with a non-zero exit code if any branch
target no longer lands on an instruction. Mono.Cecil stores short branches as a single signed
byte and will happily overflow it when you insert code in front of a branch target, which
produces a body that jits to garbage. Widening the branches and verifying the result keeps that
class of bug out of a released binary.

## Web patcher

The same Cecil patcher and verifier, compiled to .NET WebAssembly (`web/wasm/PatcherWasm.csproj`),
run entirely in the browser: the user drops in `Assembly-CSharp.dll`, it is patched and verified
locally, and the patched dll is downloaded. **Nothing is uploaded**, and no game code is hosted.

- Built by `bash tools/build_web.sh` into `web/dist/` (`index.html` + `_framework/`).
- Deployed to GitHub Pages by `.github/workflows/pages.yml` on pushes to `main` that touch
  `web/`, `patcher/`, `verify/` or the build script. The embedded `SafeHelper.dll` is committed, so
  CI needs no game files.
- Without Unity's assemblies the browser runs the reference-free subset of the checks, and reports
  "N checks passed, K skipped".
- Local release gate: `node web/test/parity.mjs` checks that the browser output is byte-identical to
  the CLI's. It needs your own game dlls, so it does not run in CI.

## Verification

`verify/` is a standalone harness that re-checks a patched dll before it ships:

- **IL** — every method body decodes, every branch target lands on an instruction, stack
  heights balance at each instruction, and every imported member resolves and is on the
  allowed list.
- **Semantics** — patch-intent checks: the fullscreen override only ever asks for the native
  display size, `PerformSizing` becomes a no-op, the aspect literals became `3/4`, canvases
  are repointed at the 4:3 UI camera, and `refResolution` rebasing stayed 4:3.
- **Simulation** — a numeric twin of the camera/HUD logic runs 100 frames per configuration
  (`1280x960`, `2056x1329`, `4112x2580`, `4112x2658`, `1280x720`, `3440x1440`, `1024x768`)
  with a scene transition at frame 50. It asserts the camera rect stays 4:3, pinned HUD stays
  inside the 4:3 view, full-width fade/matte assets are never moved, and steady state costs zero
  canvas writes per frame. Further runs resize the window 960 -> 720 -> 960 and require the HUD to end
  exactly where an undisturbed run ends, and pull elements authored past the top/bottom edge back
  inside. The display half compiles `patcher/safehelper/DisplayPolicy.cs` itself and drives it
  against an engine model (resolution changes land at end of frame, the container clamps the first
  requests to 16:9 modes, prefs survive a kill): boot from a persisted 1280x720, a windowed
  save staying windowed for 400 frames, a dragged window re-requested as 4:3, a clamping container,
  the mac fullscreen case, kill-and-reboot, and the 4:3 mode list. Only the thin Unity wrappers
  (state, `SetResolution`, `PlayerPrefs`) are modelled rather than compiled.
- **Helper integrity** — any method with a `leave` but no exception handlers fails (try/catch
  stripped), and the cloned helper must have the same handlers, per method, as the embedded
  `patcher/safehelper/SafeHelper.dll`.

The member whitelist is the part that earned its keep: v5.4 imported `System.Math.Abs(long)`,
and Cecil recorded the reference against the patcher's own runtime — `System.Private.CoreLib,
9.0.0.0` — which Unity 2019's Mono cannot resolve. The affected methods failed at first JIT,
which is why v5.4 booted in 16:9 with the HUD spilling again. The abs is now inlined as
negate-and-branch, the harness rejects any CoreLib reference, and `--verify` lists the
assembly-ref versions of its own output.

```bash
dotnet run --project verify/Verify.csproj -- Assembly-CSharp-43.dll
```

Exit code 0 with `checks: 406 passed, 0 failed, 0 skipped` (and a sha256 of the dll) means it is safe to
install. Gate every deploy on it.

## Install on the Retroid Pocket Nova

1. Back up `Chef RPG_Data/Managed/Assembly-CSharp.dll`.
2. Run `python3 patch.py` (or the patcher) against that `Managed` folder, or copy a patched
   `Assembly-CSharp.dll` over from the web patcher / another machine.
3. If you previously installed the broken URP variant, **restore the original
   `Unity.RenderPipelines.Universal.Runtime.dll`** (delete the `-43` one).
4. Launch via Wine / ARM64 Proton.

### Patching a Windows build from another machine

The device's Proton prefix has no .NET runtime and `scp` is disabled, so the Windows dll is
patched off-device and pushed over ssh:

```bash
# 1. take the pristine windows dll (the device's .bak, or a backup you kept)
# 2. patch it locally - the resolution source can be ANY Unity install's Managed folder,
#    because the Unity assemblies are all version 0.0.0.0 and refs bind by name:
patcher "Assembly-CSharp.dll" "/path/to/Chef RPG_Data/Managed" "Assembly-CSharp-43.dll"
#    (the patcher already verifies and writes nothing on failure; this re-checks independently)
# 3. verify before shipping
dotnet run --project verify/Verify.csproj -- Assembly-CSharp-43.dll
# 4. push to the device (.new + hash check, then mv, so a partial transfer can never
#    replace the live dll):
ssh droiddeck 'cat > ".../Managed/Assembly-CSharp.dll.new"' < Assembly-CSharp-43.dll
ssh droiddeck 'sha256sum ".../Managed/Assembly-CSharp.dll.new"'   # must match step 3
ssh droiddeck 'chmod 600 ".../Managed/Assembly-CSharp.dll.new" && \
               mv -f ".../Managed/Assembly-CSharp.dll.new" ".../Managed/Assembly-CSharp.dll"'
```

To verify the patch loaded, `Player.log` and `<persistentDataPath>/chefrpg43.txt` now contain
`CHEFRPG43` breadcrumbs:

- once per boot: `sysW` / `sysH` (the display size), `scrW` / `scrH` (the framebuffer *before* the
  patch asks for a change), `newScrW` / `newScrH` (immediately after, so usually stale — Unity
  applies the resize at end of frame), plus a one-shot `lateScrW` / `lateScrH` / `lateFull` taken on
  the first settings-panel frame.
- every 300 frames: the live world camera — `camName`, `camDepth`, `camScrW` / `camScrH`,
  `camTexture`, `camRectX` / `camRectY` / `camRectW` / `camRectH` (the camera's actual viewport in
  pixels), `camOrtho`, and the pixel-perfect component's `ppRefW` / `ppRefH` / `ppAssets` /
  `ppCropX` / `ppCropY` / `ppStretch` / `ppUpscale` / `ppSnap`. A `camRect` that covers the whole
  screen means the game is filling its frame and any remaining bars are the container's; a
  `camScrH` of 720 means the game was handed a 16:9 frame.

## Build from source

The HUD/display helper is a separate project that the patcher embeds as a resource. After
changing `patcher/safehelper/ChefRPG43SafeArea.cs` or `DisplayPolicy.cs`, rebuild it **and copy the
result over the embedded file** — the patcher does not rebuild it, and a stale copy is how a patched
dll once shipped without its helper changes:

```bash
dotnet build -c Release patcher/safehelper/SafeHelper.csproj
cp patcher/safehelper/bin/Release/netstandard2.0/SafeHelper.dll patcher/safehelper/SafeHelper.dll
```

```bash
dotnet publish patcher -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o dist/mac
dotnet publish patcher -c Release -r osx-x64   --self-contained -p:PublishSingleFile=true -o dist/mac-x64
dotnet publish patcher -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -o dist/win
dotnet publish patcher -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist/linux
```

Publish the runtimes one at a time — parallel `dotnet publish` runs share `patcher/obj` and can
clobber each other's intermediate output.
