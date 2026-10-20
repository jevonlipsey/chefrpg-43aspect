Chef RPG 4:3 Aspect Ratio Patch - read this first
=================================================

This zip contains the patcher only. It does NOT contain any game files. You patch your own
legally owned copy of Chef RPG's Assembly-CSharp.dll. Nothing is uploaded or downloaded.

Contents: the patcher program (patcher.exe on Windows, patcher elsewhere), patch.py,
INSTALL.md (full guide, Android handhelds, troubleshooting) and this file.

Find the game's Managed folder first
------------------------------------
  Windows : ...\steamapps\common\Chef RPG\Chef RPG_Data\Managed
  Linux   : ~/.local/share/Steam/steamapps/common/Chef RPG/Chef RPG_Data/Managed
  macOS   : ~/Library/Application Support/Steam/steamapps/common/Chef RPG/ChefRPG.app/Contents/Resources/Data/Managed
            (right-click ChefRPG.app > Show Package Contents > Contents/Resources/Data/Managed)
In Steam: right-click Chef RPG > Manage > Browse local files.

Windows
-------
1. Drag Assembly-CSharp.dll (from Chef RPG_Data\Managed) onto patcher.exe.
   Dragging the whole Managed folder onto patcher.exe works too and runs the full check.
2. A window shows the progress and waits for Enter when it is done. It writes
   Assembly-CSharp-43.dll next to the original and never touches the original itself.
3. In the Managed folder: rename Assembly-CSharp.dll to Assembly-CSharp.dll.bak (your backup),
   then rename Assembly-CSharp-43.dll to Assembly-CSharp.dll.
If Windows SmartScreen warns about an unknown app, choose More info > Run anyway.

macOS / Linux
-------------
  chmod +x patcher
  ./patcher "/path/to/Managed"
macOS may refuse to open a downloaded program; run this once, then try again:
  xattr -d com.apple.quarantine patcher
This writes Assembly-CSharp-43.dll in the Managed folder. Back up Assembly-CSharp.dll
(as Assembly-CSharp.dll.bak), then rename the -43 file to Assembly-CSharp.dll.

Easiest on a desktop with Python 3 (patch.py must stay next to the patcher):
  python3 patch.py "/path/to/Managed"
This patches, verifies, backs up the original to Assembly-CSharp.dll.bak and installs.
Add --no-install to only write Assembly-CSharp-43.dll.

No computer handy, or on a phone / Android handheld?
  Web patcher (runs in your browser, nothing is uploaded):
  https://jevonlipsey.github.io/chefrpg-43aspect/

Safe by design
--------------
The patcher verifies its own output and writes nothing if any check fails, so a failed run
never leaves a broken file. Always keep the Assembly-CSharp.dll.bak backup. After a Chef RPG
update, Steam replaces the file: patch the new original again. If a run says "already patched",
restore the .bak (or Steam > Properties > Installed Files > Verify integrity of game files).

Problems: https://github.com/jevonlipsey/chefrpg-43aspect/issues
