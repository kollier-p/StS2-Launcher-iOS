#!/usr/bin/env python3
"""Generate the public FMOD/Spine GDExtension registrations for a game-free host.

No Steam assets or commercial game assemblies are needed. The pinned public
binaries come from bootstrap.py and are not staged in the repository.
"""
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[2]
IOS = ROOT / "ios"
FMOD = ROOT / "vendor/fmod-release/fmod/libs"
SPINE = ROOT / "vendor/spine-runtimes/spine-godot/bin"

FMOD_DESCRIPTOR = """[configuration]
entry_symbol="fmod_library_init"
compatibility_minimum="4.5"
[libraries]
macos.editor="res://addons/fmod/libs/macos/libGodotFmod.macos.template_release.framework"
macos.debug="res://addons/fmod/libs/macos/libGodotFmod.macos.template_release.framework"
macos.release="res://addons/fmod/libs/macos/libGodotFmod.macos.template_release.framework"
ios.release="res://addons/fmod/libs/ios/libGodotFmod.ios.template_release.xcframework"
[dependencies]
macos.editor={"libs/macos/libfmod.dylib":"", "libs/macos/libfmodstudio.dylib":""}
macos.debug={"libs/macos/libfmod.dylib":"", "libs/macos/libfmodstudio.dylib":""}
macos.release={"libs/macos/libfmod.dylib":"", "libs/macos/libfmodstudio.dylib":""}
ios.release={"libs/ios/libfmod_iphoneos.a":"", "libs/ios/libfmodstudio_iphoneos.a":""}
"""


def copy(source: Path, destination: Path) -> None:
    if not source.exists():
        raise SystemExit(f"Missing public extension dependency {source}; rerun scripts/ios/bootstrap.py")
    destination.parent.mkdir(parents=True, exist_ok=True)
    if source.is_dir():
        shutil.copytree(source, destination, dirs_exist_ok=True)
    else:
        shutil.copy2(source, destination)


def prepare() -> None:
    addons = IOS / "addons"
    if addons.is_symlink():
        raise SystemExit("Refusing to replace a symlinked addons folder")
    fmod = addons / "fmod"
    for name in ("libGodotFmod.macos.template_release.framework", "libfmod.dylib", "libfmodstudio.dylib"):
        copy(FMOD / "macos" / name, fmod / "libs/macos" / name)
    for name in ("libGodotFmod.ios.template_release.xcframework", "libfmod_iphoneos.a", "libfmodstudio_iphoneos.a"):
        copy(FMOD / "iOS" / name, fmod / "libs/ios" / name)
    (fmod / "fmod.gdextension").write_text(FMOD_DESCRIPTOR)

    spine = addons / "spine"
    descriptor = SPINE / "spine_godot_extension.gdextension"
    copy(descriptor, spine / descriptor.name)
    for platform, name in (
        ("macos", "libspine_godot.macos.template_release.framework"),
        ("ios", "libspine_godot.ios.template_release.framework"),
    ):
        copy(SPINE / platform / name, spine / platform / name)
    print("Prepared public FMOD and Spine extensions without Steam assets.")


if __name__ == "__main__":
    prepare()
