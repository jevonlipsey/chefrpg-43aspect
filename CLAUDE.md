# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

Run everything from the repo root. `verify/Program.cs` resolves `original` and `verify/remote/win-refs` as relative paths, so running from elsewhere breaks it.

```bash
# patch + install (stdlib launcher, no .NET SDK needed if a prebuilt patcher exists; falls back to `dotnet run`)
python3 patch.py "/path/to/Chef RPG_Data/Managed"                # backs up to .bak, installs the patched dll
python3 patch.py "/path/to/Chef RPG_Data/Managed" --no-install   # only writes Assembly-CSharp-43.dll next to the input

# or call the patcher directly: patcher <inputDll> [managedDir] [outputDll]   (or: patcher <ManagedDir>)
# with managedDir it runs the full verifier, without it a reference-free subset; writes output only if verified
# exit codes: 0 ok, 1 verify failed/error, 3 already patched, 4 not Chef RPG's dll
dotnet run --project patcher -- Assembly-CSharp.dll "/path/to/Managed" Assembly-CSharp-43.dll

# verify a patched dll. this is the only test suite; gate every deploy on it.
dotnet run --project verify/Verify.csproj -- Assembly-CSharp-43.dll
# expected: exit 0 and "checks: 406 passed, 0 failed, 0 skipped"

# release builds of the patcher, one runtime at a time (parallel publishes share patcher/obj)
dotnet publish patcher -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o dist/mac
dotnet publish patcher -c Release -r osx-x64   --self-contained -p:PublishSingleFile=true -o dist/mac-x64
dotnet publish patcher -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -o dist/win
dotnet publish patcher -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist/linux

# browser patcher: the same patcher compiled to .NET WASM, built into web/dist/ (deployed by .github/workflows/pages.yml)
bash tools/build_web.sh
# release gate for the web build: browser output must be byte-identical to the CLI's. needs your own game dlls, so not in CI.
node web/test/parity.mjs
```

`web/` holds the browser patcher (`web/wasm/PatcherWasm.csproj`, `web/test/parity.mjs`); `web/dist/` is build output and gitignored. `.github/workflows/pages.yml` deploys it and `release.yml` builds the four self-contained patchers on `v*` tags. Neither builds `SafeHelper.dll` (committed, prebuilt).

There is no linter and no per-test selector. The verify harness runs all checks in one pass. To investigate a single failing check, read the check in `verify/IlVerifier.cs`, `verify/SemanticVerifier.cs`, or `verify/Simulator.cs` and run the harness again.

`patcher/safehelper/` is a separate netstandard2.0 project (`SafeHelper.csproj`). Its output `SafeHelper.dll` is embedded into the patcher as a resource (`patcher/patcher.csproj`). The patcher does not rebuild it, so after changing `ChefRPG43SafeArea.cs` or `DisplayPolicy.cs` you must do both steps, then re-patch from the pristine input (a stale embedded copy is a known past failure):

```bash
dotnet build -c Release patcher/safehelper/SafeHelper.csproj
cp patcher/safehelper/bin/Release/netstandard2.0/SafeHelper.dll patcher/safehelper/SafeHelper.dll
```

Patch from pristine inputs only: `original/Assembly-CSharp.dll` for the mac build, `verify/remote/Assembly-CSharp-droiddeck-bak.dll` for the Windows/device build (the managed dir only feeds the full verifier's resolver; patching itself needs no Unity assemblies). The Windows input needs `--refs verify/remote/win-refs` for a full verify from the mac Managed dir.

## Architecture

The tool rewrites the game's IL. It doesn't ship game code, and it needs a legally owned `Assembly-CSharp.dll` as input. The pipeline is:

1. **Patch** (`patcher/Program.cs`, Mono.Cecil). Loads `Assembly-CSharp.dll` with no assembly resolver: the Unity members it emits are built by hand in `BuildCoreRefs`, scoped to the module's own `UnityEngine.CoreModule` reference, so the same code runs in the browser. `PatchApi.Patch` (in-memory) runs the patch and then the verifier, and returns no output unless it passes. It rewrites a fixed set of game methods and injects helper calls. The main edits are the fullscreen and windowed paths in `SettingsPanel`, the `LetterBoxer` camera and watchdog, `CameraFollow` pixel-perfect rebasing, `Formulas` screen-position math, tooltip and `UILineRenderer` aspect literals, and canvas repointing to a 4:3 UI camera. Per-method edits are described in `README.md`.
2. **Helper** (`patcher/safehelper/`). The HUD-containment and display-policy logic lives in `SafeHelper.dll`, which the patcher embeds as a resource, rather than in hand-written IL. `ChefRPG43SafeArea` is a `partial` class split in two files. `ChefRPG43SafeArea.cs` holds the Unity-facing code: reversible HUD containment on x and y, the `Apply*`/`Resolutions43`/`DisplayWatchdog` wrappers, and `PlayerPrefs` persistence. `DisplayPolicy.cs` holds the pure integer decisions (`Coerce43`, `Modes43`, `NextRequest`). It has no usings and no `System.Math`, because `verify/Verify.csproj` compiles that same file so the simulator exercises the shipped code, and because a CoreLib import breaks Unity's Mono. The patcher clones the helper type into the game assembly (`InjectSafeAreaHelper`, including its exception handlers) **before** `ApplyDisplayPolicy` wires `SettingsPanel`, `StartMenuManager.Awake` and `LetterBoxer.Update` to it. Check how `patcher/Program.cs` wires it in before changing its interface.
3. **Self-check.** After writing, the patcher re-reads its own output and exits non-zero if any branch target no longer lands on an instruction. Cecil stores short branches as signed bytes and can overflow them after insertions, so this check catches that class of bug. The patcher widens branches to avoid it.
4. **Verify** (`verify/`). Three layers run in sequence from `Program.cs`:
   - `IlVerifier`: decodes every method body, checks branch targets and stack balance, checks that each imported member is on an allowed list, and fails any body that has a `leave` but no exception handlers (stripped try/catch).
   - `SemanticVerifier`: checks that patch intent holds, such as windowed mode keeping its original body and calling `ApplyWindowed`, the picker using `Resolutions43`, and the cloned helper keeping the same exception handlers as the embedded `SafeHelper.dll`.
   - `Simulator`: a numeric model of the camera and HUD logic (including a resize round-trip and y containment), plus display-policy scenarios that drive the real `DisplayPolicy.cs` against an engine model with end-of-frame resolution changes, a 16:9-clamping container and kill/reboot persistence.

   The member whitelist is the guard that matters most. A reference that resolves against the patcher's own .NET runtime, instead of Unity's Mono, passes the build and fails at JIT on device. `IlVerifier` rejects any `System.Private.CoreLib` reference for this reason.

### Hardcoded paths

`DEV_MANAGED` in `patcher/Program.cs` and `UNITY_MANAGED` in `verify/Program.cs` point at the mac Steam install under the current user's home folder. The `HintPath`s in `patcher/safehelper/SafeHelper.csproj` use the same location through `$(ChefManaged)`; pass `-p:ChefManaged=<Managed dir>` to build the helper elsewhere. Only the zero-argument dev default and the standalone verifier read these paths, and the verifier skips a directory that doesn't exist, so on another machine it degrades rather than failing outright.

### Device and deploy

The Windows build runs under Wine / Proton on a Retroid Pocket Nova (hostname `droiddeck` in ssh). Its Proton prefix has no .NET runtime, so patch and verify happen on the mac and the result gets pushed over ssh. `README.md` has the exact push sequence (write `.new`, check sha256, then `mv`). Don't replace the live dll without the hash check. Access, device paths, launching and stopping the game, and how to run a device test are in `CLAUDE.local.md`, which is gitignored and holds personal device details.

`verify/remote/` holds device-side captures and is gitignored. `verify/remote/win-refs` supplies Windows-only references so that Windows dlls resolve during verify.

### Directories you should not edit

- `original/`, `orig/`: pristine copies of the game dll. They're gitignored and are the baseline for diffs.
- `decomp*/`: decompiler output, reference only and gitignored.
- `dist/`: published binaries, gitignored. Rebuild with the publish commands above.
- `Assembly-CSharp-43*.dll`, `Assembly-CSharp.dll` at the root: build outputs, gitignored.

### Version history

Changes are versioned in commit messages and tags. The public repo (github.com/jevonlipsey/chefrpg-43aspect) starts at v5.7 as a single fresh commit, because the earlier history contained a patched game dll. The v5.4–v5.6 history is not in this repo; it lives only in a local backup outside the repo. The 4:3 approach has changed several times. In particular, v5.4 failed because of the CoreLib reference described above, and the URP patch was removed after it broke rendering. Check `git log` and the notes here before reintroducing an approach.

### Publishing

The repo is public. Never commit a game assembly (original or patched), decompiler output, device logs or screenshots; `.gitignore` blocks every `.dll` except `patcher/safehelper/SafeHelper.dll`, and that helper is built with `DebugType=none` and a path map so no local paths end up inside it. Releases ship only the self-contained patcher binaries (`release.yml`, on a `v*` tag), never a patched dll. GitHub Pages serves the web patcher from `pages.yml` on pushes to `main`.
