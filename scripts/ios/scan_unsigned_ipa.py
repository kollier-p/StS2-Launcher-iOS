#!/usr/bin/env python3
"""Reject unsigned IPA artifacts containing game files or missing the JIT/Steam bootstrap.

The unsigned artifact is safe to distribute only if the player obtains their
commercial game themselves via SteamKit2 after authentication on-device.
"""
import argparse
from pathlib import Path
import plistlib
import sys
import zipfile

REQUIRED = (
    "Managed/STS2Jit.dll",
    "Managed/STS2JitSupport.dll",
    "Managed/STS2SteamBootstrap.dll",
    "Managed/STS2SteamDownloads.dll",
    "Managed/SteamKit2.dll",
    "Managed/System.Private.CoreLib.dll",
    "Managed/GodotSharp.dll",
)
FORBIDDEN = {"sts2.dll", "release_info.json", "slay the spire 2.pck",
             "steam_appid.txt", "0harmony.original.dll"}
MAX_PACK_BYTES = 32 * 1024 * 1024


def check(paths: set[str], sizes: dict[str, int], plist_data: bytes) -> None:
    info = plistlib.loads(plist_data)
    if not info.get("CFBundleIdentifier") or not info.get("CFBundleExecutable"):
        raise ValueError("Unsigned IPA has no valid bundle identifier or executable")
    roots = {part for path in paths for part in [path.split("/")[0]] if part}
    if len(roots) != 1:
        raise ValueError("Expected exactly one iOS app bundle")
    missing = [name for name in REQUIRED if name not in paths]
    if missing:
        raise ValueError("Unsigned JIT bootstrap is missing: " + ", ".join(missing))
    game = [path for path in paths if Path(path).name.lower() in FORBIDDEN]
    if game:
        raise ValueError("Commercial game files leaked into IPA: " + ", ".join(game))
    packs = [path for path in paths if path.lower().endswith(".pck") and sizes.get(path, 0) > MAX_PACK_BYTES]
    if packs:
        raise ValueError("Unexpected large content pack in IPA: " + ", ".join(packs))


def inspect_app(app: Path) -> None:
    if not app.is_dir() or not (app / "Info.plist").is_file():
        raise ValueError("Expected an Xcode-built .app with Info.plist")
    files = [file for file in app.rglob("*") if file.is_file()]
    paths = {str(file.relative_to(app)).replace("\\", "/") for file in files}
    sizes = {str(file.relative_to(app)).replace("\\", "/"): file.stat().st_size for file in files}
    check(paths, sizes, (app / "Info.plist").read_bytes())


def inspect_ipa(ipa: Path) -> None:
    with zipfile.ZipFile(ipa) as zipped:
        members = [item for item in zipped.infolist() if not item.is_dir()]
        apps = {item.filename.split("/")[1] for item in members
                if item.filename.startswith("Payload/") and item.filename.count("/") >= 2}
        if len(apps) != 1 or not next(iter(apps)).endswith(".app"):
            raise ValueError("Expected exactly one Payload/<name>.app")
        prefix = "Payload/" + apps.pop() + "/"
        names = {item.filename[len(prefix):] for item in members if item.filename.startswith(prefix)}
        sizes = {item.filename[len(prefix):]: item.file_size for item in members if item.filename.startswith(prefix)}
        if "Info.plist" not in names:
            raise ValueError("IPA has no app Info.plist")
        check(names, sizes, zipped.read(prefix + "Info.plist"))
        if zipped.testzip() is not None:
            raise ValueError("Unsigned IPA is corrupt")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--app", type=Path)
    group.add_argument("--ipa", type=Path)
    args = parser.parse_args()
    try:
        if args.app:
            inspect_app(args.app)
        else:
            inspect_ipa(args.ipa)
    except (ValueError, OSError, zipfile.BadZipFile) as error:
        print(f"Unsigned IPA rejected: {error}", file=sys.stderr)
        return 1
    print("Unsigned IPA checks passed: SteamKit2 and JIT bootstraps present; no game assets bundled.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
