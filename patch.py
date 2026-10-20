#!/usr/bin/env python3
"""
chef rpg 4:3 aspect ratio patcher (launcher)

runs the mono.cecil patcher against your legally owned Assembly-CSharp.dll.
no copyrighted binaries are distributed here. python 3.8+, stdlib only; the
.net sdk is NOT needed when a prebuilt patcher is available.

usage:
  python3 patch.py "/path/to/Chef RPG_Data/Managed"                patch + install
  python3 patch.py "/path/to/Chef RPG_Data/Managed" --no-install   only write Assembly-CSharp-43.dll
  python3 patch.py "/path/to/Chef RPG_Data/Managed" --dotnet       force `dotnet run --project patcher`

install mode (default):
  1. patches Assembly-CSharp.dll into a temp file (the patcher verifies it and
     only writes the file if every check passed)
  2. copies the original to Assembly-CSharp.dll.bak (an existing .bak is never overwritten)
  3. moves the patched temp file over Assembly-CSharp.dll
  a failed run leaves Assembly-CSharp.dll untouched.

patcher lookup order:
  $CHEFRPG43_PATCHER (path to a patcher binary; for testing / power users),
  ./patcher[.exe] next to this script (release zip layout),
  dist/<mac|mac-x64|win|linux>/patcher[.exe] for this machine,
  then `dotnet run --project patcher` if dotnet is on PATH.

patcher exit codes: 0 ok, 1 verify failed / error, 3 already patched,
4 not Chef RPG's dll.
"""

import hashlib
import os
import platform
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
WEB_URL = "https://jevonlipsey.github.io/chefrpg-43aspect/"
RELEASES_URL = "https://github.com/jevonlipsey/chefrpg-43aspect/releases"


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def rid_dir():
    system = platform.system()
    machine = platform.machine().lower()
    if system == "Darwin":
        if machine in ("arm64", "aarch64"):
            return "mac"
        if machine in ("x86_64", "amd64"):
            return "mac-x64"
    elif system == "Windows":
        if machine in ("amd64", "x86_64"):
            return "win"
    elif system == "Linux":
        if machine in ("x86_64", "amd64"):
            return "linux"
    return None


def find_prebuilt():
    override = os.environ.get("CHEFRPG43_PATCHER")
    if override:
        if os.path.isfile(override):
            return os.path.abspath(override)
        print(f"[!] CHEFRPG43_PATCHER='{override}' is not a file.")
        sys.exit(1)
    exe = "patcher.exe" if platform.system() == "Windows" else "patcher"
    candidates = [os.path.join(HERE, exe)]
    d = rid_dir()
    if d:
        candidates.append(os.path.join(HERE, "dist", d, exe))
    for c in candidates:
        if os.path.isfile(c) and not stale(c):
            return c
    return None


def stale(binary):
    """in a source checkout, dist/ is gitignored build output and can lag the code.
    an old patcher there once skipped the already-patched check and double-patched,
    so a binary older than any patcher/verify source is ignored (dotnet run instead)."""
    newest = 0.0
    for d in ("patcher", "verify"):
        for root, dirs, files in os.walk(os.path.join(HERE, d)):
            dirs[:] = [x for x in dirs if x not in ("bin", "obj", "remote")]
            for f in files:
                if f.endswith((".cs", ".csproj")) or f == "SafeHelper.dll":
                    newest = max(newest, os.path.getmtime(os.path.join(root, f)))
    if newest > os.path.getmtime(binary):
        print(f"[!] ignoring {binary}: older than the patcher sources (rebuild it with dotnet publish).")
        return True
    return False


def patcher_command(force_dotnet):
    """returns (command prefix, description)."""
    if not force_dotnet:
        prebuilt = find_prebuilt()
        if prebuilt:
            if os.name != "nt" and not os.access(prebuilt, os.X_OK):
                try:
                    os.chmod(prebuilt, os.stat(prebuilt).st_mode | 0o111)
                except OSError:
                    pass
            return [prebuilt], prebuilt
    patcher_dir = os.path.join(HERE, "patcher")
    if shutil.which("dotnet") and os.path.isdir(patcher_dir):
        return ["dotnet", "run", "--project", patcher_dir, "--"], "dotnet run --project patcher"
    if force_dotnet:
        print("[!] --dotnet needs the .net sdk (`dotnet` on PATH) and the patcher/ source folder.")
    else:
        print("[!] no patcher found for this machine and no .net sdk to build one.")
        print("    easiest: use the web patcher in any browser (nothing is uploaded):")
        print(f"      {WEB_URL}")
        print("    or download a patcher binary for your os from the releases page:")
        print(f"      {RELEASES_URL}")
    sys.exit(1)


def remove_quiet(path):
    try:
        os.remove(path)
    except OSError:
        pass


def explain(rc):
    if rc == 3:
        print("[!] this Assembly-CSharp.dll is already patched (nothing to do).")
        print("    to start over, restore Assembly-CSharp.dll.bak if you have one, or let steam")
        print("    restore the original: steam > chef rpg > properties > installed files >")
        print("    verify integrity of game files. then run this again.")
    elif rc == 4:
        print("[!] that is not chef rpg's Assembly-CSharp.dll (or it is a version this patch")
        print("    does not know). check you pointed at the right Managed folder; if the game")
        print("    just updated, the patch may need a new release.")
    elif rc == 1:
        print("[!] patch or verification failed. nothing was installed.")
        print("    if the game just updated, wait for a new release or open an issue with the output above.")
    else:
        print(f"[!] patcher exited with code {rc}. nothing was installed.")


def main():
    print("==================================================")
    print("      chef rpg 4:3 aspect ratio patcher")
    print("==================================================")

    args = sys.argv[1:]
    install = True
    force_dotnet = False
    paths = []
    for a in args:
        if a == "--no-install":
            install = False
        elif a == "--dotnet":
            force_dotnet = True
        elif a in ("-h", "--help"):
            print(__doc__)
            sys.exit(0)
        elif a.startswith("--"):
            print(f"[!] unknown option {a}")
            sys.exit(1)
        else:
            paths.append(a)

    if len(paths) != 1:
        print('[!] usage: python3 patch.py "/path/to/Chef RPG_Data/Managed" [--no-install] [--dotnet]')
        sys.exit(1)

    managed_dir = os.path.abspath(paths[0])
    input_file = os.path.join(managed_dir, "Assembly-CSharp.dll")
    final_file = os.path.join(managed_dir, "Assembly-CSharp-43.dll")
    tmp_file = os.path.join(managed_dir, "Assembly-CSharp-43.dll.tmp")
    backup_file = input_file + ".bak"

    if not os.path.exists(input_file):
        print(f"[!] error: '{input_file}' not found.")
        sys.exit(1)

    cmd, how = patcher_command(force_dotnet)
    out_file = tmp_file if install else final_file

    print(f"[*] managed dir: {managed_dir}")
    print(f"[*] patcher:     {how}")
    print(f"[*] mode:        {'install (backs up, then replaces Assembly-CSharp.dll)' if install else 'write Assembly-CSharp-43.dll only'}")

    remove_quiet(out_file)
    try:
        res = subprocess.run(cmd + [input_file, managed_dir, out_file])
    except OSError as e:
        print(f"[!] could not run the patcher: {e}")
        if platform.system() == "Darwin":
            print("    on macos a downloaded binary may be blocked; try:")
            print(f"      xattr -d com.apple.quarantine '{cmd[0]}'")
        sys.exit(1)
    rc = res.returncode

    if rc != 0:
        remove_quiet(out_file)
        explain(rc)
        sys.exit(rc if rc > 0 else 1)
    if not os.path.isfile(out_file) or os.path.getsize(out_file) == 0:
        remove_quiet(out_file)
        print("[!] the patcher reported success but wrote no output. nothing was installed.")
        sys.exit(1)

    if not install:
        print(f"[+] done: {final_file}")
        print(f"    sha256 {sha256(final_file)}")
        print("    rename it over Assembly-CSharp.dll (keep a backup of the original).")
        return

    try:
        if os.path.exists(backup_file):
            print(f"[*] backup already exists, keeping it: {backup_file}")
        else:
            shutil.copy2(input_file, backup_file)
            print(f"[*] backed up original: {backup_file}")
        os.replace(out_file, input_file)
    except OSError as e:
        remove_quiet(out_file)
        print(f"[!] could not install the patched dll: {e}")
        print("    Assembly-CSharp.dll was not changed (is the game running, or the folder read-only?).")
        sys.exit(1)

    print(f"[+] installed: {input_file}")
    print(f"    sha256 {sha256(input_file)}")
    print(f"    to undo: copy {os.path.basename(backup_file)} back over Assembly-CSharp.dll")
    print("    (or steam > verify integrity of game files). re-run this after each game update.")


if __name__ == "__main__":
    main()
