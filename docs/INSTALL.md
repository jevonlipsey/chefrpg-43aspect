# Installing the Chef RPG 4:3 patch

This patch makes Chef RPG render at a true 4:3 aspect ratio (no top/bottom bars on a 4:3 screen such as the Retroid Pocket Nova). You patch **your own copy** of the game's `Assembly-CSharp.dll`; nothing from the game is downloaded or shared. This is an unofficial fan tool, not affiliated with or endorsed by World 2 Studio Inc.

## Quick summary

- The patched file **replaces** `Assembly-CSharp.dll` in the game's `Managed` folder.
- **Always keep a backup** of the original (`Assembly-CSharp.dll.bak`).
- **Re-patch after every game update.** Steam updates overwrite the file, and the patcher refuses a file that is already patched, so always patch the fresh original.

## 1. Get the patched dll

Pick one. All three produce the same patch.

### a) Web patcher (any device, including your phone)

1. Open the web patcher: <https://jevonlipsey.github.io/chefrpg-43aspect/>
2. Drop in (or pick) your `Assembly-CSharp.dll`.
3. Wait for it to patch and verify. Everything runs in your browser; the file is not uploaded.
4. Download the patched `Assembly-CSharp.dll` and put it in the `Managed` folder (see [section 2](#2-where-the-managed-folder-is)).

The page reports something like "N checks passed, K skipped". The browser version runs the checks that do not need Unity's own libraries; the skipped ones need them. The desktop patcher below runs the full set when you point it at the `Managed` folder.

### b) Desktop patcher (Windows, macOS, Linux)

1. Download the zip for your system from the Releases page: <https://github.com/jevonlipsey/chefrpg-43aspect/releases>

| System | Asset |
|---|---|
| Windows | `chefrpg-43aspect-<version>-windows-x64.zip` |
| macOS, Apple silicon | `chefrpg-43aspect-<version>-macos-arm64.zip` |
| macOS, Intel | `chefrpg-43aspect-<version>-macos-x64.zip` |
| Linux / Steam Deck | `chefrpg-43aspect-<version>-linux-x64.zip` |

2. Unzip it (each zip also has `patch.py`, this guide and a `README-FIRST.txt`).

**Windows: drag and drop.** Drag `Assembly-CSharp.dll` from the game's `Managed` folder onto `patcher.exe` (dragging the whole `Managed` folder works too and runs the full verification). A window shows the progress and waits for Enter when it is done. It writes `Assembly-CSharp-43.dll` next to the original and never changes the original itself. Then, in the `Managed` folder:

1. Rename `Assembly-CSharp.dll` to `Assembly-CSharp.dll.bak` (your backup).
2. Rename `Assembly-CSharp-43.dll` to `Assembly-CSharp.dll`.

If the run ends with an explanation instead (already patched, not Chef RPG's dll, or verification failed), nothing was written; see [Troubleshooting](#6-troubleshooting). If Windows SmartScreen warns about an unknown app, choose **More info**, then **Run anyway**.

**Terminal (all systems).** Open a terminal / command prompt in the unzipped folder and run it against the `Managed` folder:

| System | Command |
|---|---|
| Windows | `patcher.exe "C:\Program Files (x86)\Steam\steamapps\common\Chef RPG\Chef RPG_Data\Managed"` |
| macOS (Apple silicon) | `./patcher "$HOME/Library/Application Support/Steam/steamapps/common/Chef RPG/ChefRPG.app/Contents/Resources/Data/Managed"` |
| macOS (Intel) | same as above, using the Intel build |
| Linux / Steam Deck | `./patcher "$HOME/.local/share/Steam/steamapps/common/Chef RPG/Chef RPG_Data/Managed"` |

If macOS says the file cannot be opened, run `xattr -d com.apple.quarantine ./patcher` once. On Linux run `chmod +x patcher` if needed.

With the `Managed` folder as the argument the patcher runs the **full verification** and only writes `Assembly-CSharp-43.dll` next to the original if every check passed. Back up `Assembly-CSharp.dll` as `Assembly-CSharp.dll.bak`, then rename the `-43` file to `Assembly-CSharp.dll`.

### c) `python3 patch.py` (easiest on a desktop)

From the release zip (or a clone of this repo):

```
python3 patch.py "/path/to/Chef RPG_Data/Managed"
```

This runs the patcher, verifies the result, **backs up the original to `Assembly-CSharp.dll.bak`** (an existing backup is never overwritten) and then installs the patched file. Add `--no-install` to only write `Assembly-CSharp-43.dll` and leave the original alone.

## 2. Where the Managed folder is

| Platform | Folder |
|---|---|
| DroidDeck (internal proot environment) | `/root/.local/share/Steam/steamapps/common/Chef RPG/Chef RPG_Data/Managed/` |
| Windows (Steam) | `C:\Program Files (x86)\Steam\steamapps\common\Chef RPG\Chef RPG_Data\Managed\` |
| Linux / Steam Deck (Windows build under Proton) | `~/.local/share/Steam/steamapps/common/Chef RPG/Chef RPG_Data/Managed/` |
| macOS | `~/Library/Application Support/Steam/steamapps/common/Chef RPG/ChefRPG.app/Contents/Resources/Data/Managed/` |

Easiest way to open it: in Steam, right-click Chef RPG, then **Manage**, then **Browse local files**.

**macOS is different:** the game is an app bundle. Right-click `ChefRPG.app` and choose **Show Package Contents**, then go to `Contents/Resources/Data/Managed/`. (The folder is not called `Chef RPG_Data` there.)

## 3. Android handhelds

Both apps below run the Windows game inside their own Linux/Wine environment, so the game's files are usually **not** in a normal Android folder. The steps here are what could be confirmed from public sources; anything else is marked **(unconfirmed)**. Your app's version may differ.

The easiest route on Android is the web patcher on the phone itself:

1. Copy `Assembly-CSharp.dll` out of the game's `Chef RPG_Data/Managed/` folder to somewhere your browser can pick it (for example `Download/`). Keep this copy as your backup.
2. Open the web patcher (<https://jevonlipsey.github.io/chefrpg-43aspect/>) in the phone's browser and pick that file.
3. Download the patched file.
4. Copy it back over the original in `Chef RPG_Data/Managed/`.

Or patch on a PC and copy the patched file over (any file transfer you like).

### DroidDeck

DroidDeck runs Valve's ARM64 Steam client inside an in-app proot Linux runtime, with Windows games running through Proton. See the [DroidDeck site](https://droiddeck.app/) and [this overview](https://heldgames.com/guides/steam-on-android-handheld-options). Be careful to use the right app: there is an unrelated project of the same name that is a Stream Deck controller.

Getting at the game's files:

- Inside DroidDeck's Linux environment (e.g. via its desktop terminal or file manager), the Steam library is located at:
  `/root/.local/share/Steam/steamapps/common/Chef RPG/Chef RPG_Data/Managed/Assembly-CSharp.dll`
- Version 0.3.2 added the ability to browse DroidDeck's Linux filesystem from its File Manager, and 0.4.0 added a desktop mode with a file manager and terminal (per the [release coverage](https://retrohandhelds.gg/droiddeck-brings-steamos-to-android-in-an-app/) and the [DroidDeck site](https://droiddeck.app/)).
- Copy `Assembly-CSharp.dll` out to your Android storage or patch it directly in desktop mode.
- The game's logs (and `chefrpg43.txt`) are inside the Proton prefix:
  `/root/.local/share/Steam/steamapps/compatdata/1796790/pfx/drive_c/users/steamuser/AppData/LocalLow/World 2 Studio Inc/Chef RPG/`

Display settings (the settings cog): **Resolution** and **16:9 game fit**. See [Display settings](#4-display-settings) below. These names come from testing on a Retroid Pocket Nova; other DroidDeck versions may label them differently.

### GameNative

[GameNative](https://github.com/utkarshdalal/GameNative) runs games you own from Steam, Epic, GOG and Amazon through Wine with Box64 (see this [overview](https://heldgames.com/guides/steam-on-android-handheld-options)). Setup guide: [retrohandhelds.gg](https://retrohandhelds.gg/gamenative-setup-guide/).

Getting at the game's files, from [a GitHub discussion on file access](https://github.com/utkarshdalal/GameNative/discussions/237) (users' reports, no maintainer answer there):

- GameNative stores containers in the app's private data directory. Since Android 13 a normal file manager cannot open it. **Exposing it is an open request; whether your version allows it is unconfirmed.**
- Users report an **Open container** action that gives access to the container's Windows-style drives, with games on the `A:` drive on one user's device. **The exact menu is unconfirmed — check your app's file manager / docs.**
- Rooted devices (or a Shizuku-style file manager) can reach app data directly. **(unconfirmed for this use)**
- GameNative 1.0.0 has "Legacy" and "Modern" builds that differ in drive access and where external storage is ([coverage](https://pocket-gaming.org/2026/06/10/gamenative-1-0-0/)), so paths differ by build. **(unconfirmed which you have)**

Display and resolution settings for GameNative containers: **unconfirmed — check your app's container settings.** The goal is the same as below: give the game a 4:3 screen (for a 4:3 handheld, the panel's native size) and do not letterbox it to 16:9.

## 4. Display settings

All platforms:

1. In the game: **Settings → Fullscreen.** This uses borderless fullscreen at the screen's own size. **Windowed** gives a 4:3 window.
2. On a wider screen (a Mac, or a 16:9 PC monitor) the game shows true 4:3 with black bars at the sides. That is expected, not a bug.

On a 4:3 handheld the container must hand the game a 4:3 surface, or bars will remain:

| Setting (DroidDeck) | Value |
|---|---|
| Resolution | `Match screen (1280x960)` on the Retroid Pocket Nova |
| 16:9 game fit | `Stretch` (off). `Keep proportions` letterboxes the game into a 16:9 box |

Both apply on the **next session**: fully exit and relaunch the container app **and** the game after changing them.

Fallback if the game still starts at the wrong size: add the Steam launch options

```
-screen-width 1280 -screen-height 960 -window-mode borderless
```

## 5. Checking it worked

- The HUD (clock, key prompts) is inside the picture and there are no top/bottom bars on a 4:3 screen.
- `Player.log` contains lines with `CHEFRPG43`.
- A file `chefrpg43.txt` exists in the game's data folder (Unity's persistent data path, the same folder as the logs below on Windows).

| Platform | `Player.log` |
|---|---|
| Windows | `%USERPROFILE%\AppData\LocalLow\World 2 Studio Inc\Chef RPG\Player.log` |
| macOS | `~/Library/Logs/World 2 Studio Inc/Chef RPG/Player.log` |
| Wine / Proton | inside the prefix, `C:\users\steamuser\AppData\LocalLow\World 2 Studio Inc\Chef RPG\Player.log` |

`chefrpg43.txt` is written to the persistent data path: `...\LocalLow\World 2 Studio Inc\Chef RPG\` on Windows and Wine, `~/Library/Application Support/World 2 Studio Inc/Chef RPG/` on macOS (Unity's usual layout; unconfirmed on a real Mac, so search for `chefrpg43.txt` if it is not there).

## 6. Troubleshooting

| Problem | What to do |
|---|---|
| Black bars remain on a 4:3 screen | Almost always the container, not the game. Set the container's resolution to the panel's native size and turn 16:9 fit to Stretch (section 4), then fully relaunch both apps. Check the `[ChefRPG43] render cam 'Main Camera'` line in `Player.log`: a `pix=` rect covering the whole screen means any bars are the container's. |
| Game will not start / black screen | Restore `Assembly-CSharp.dll.bak` over `Assembly-CSharp.dll`. If the problem is gone, the patch does not suit your setup; open an issue with your `Player.log`. |
| "already patched" (exit code 3) | The file in the folder is already patched. Restore the original (`.bak`, or Steam **Verify integrity of game files**) and patch that. |
| Verification fails / "game changed" | The game was probably updated and the patch no longer matches. Nothing is written when verification fails, so your install is safe. Wait for a new release or open an issue. |
| "not Chef RPG's dll" (exit code 4) | You picked the wrong file or folder. On macOS use the path inside `ChefRPG.app` (section 2). |
| It stopped working after a game update | Steam replaced the file. Patch the new original again. |

## 7. Uninstall

Copy `Assembly-CSharp.dll.bak` back over `Assembly-CSharp.dll`, or in Steam right-click Chef RPG, then **Properties → Installed Files → Verify integrity of game files**.
