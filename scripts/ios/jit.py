#!/usr/bin/env python3
"""Mono JIT runtime and app backend for build.py."""

import hashlib
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import selectors
import time
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[2]
CACHE = ROOT / ".cache"
SOURCE = ROOT / "vendor/dotnet-runtime-jit"
BUILD = CACHE / "jit-mono-build"
PACK = CACHE / "jit-runtime-pack/runtimes/ios-arm64"
DERIVED = CACHE / "xcode-jit"
APP = DERIVED / "Build/Products/Release-iphoneos/StS2.app"
DOTNET = ROOT / ".tools/dotnet/dotnet"
VERSION = "9.0.20"
COMMIT = "3879076d9a06ce098d37c3882fb1845a6627335b"
PACK_SHA256 = "ccb189791773d4ca77cee3dd03e28dc2a9b6231b479e1e44e28088a378692b3f"


def run(*args, **kwargs):
    print("+ " + " ".join(str(arg) for arg in args), flush=True)
    return subprocess.run([str(arg) for arg in args], check=True, **kwargs)


def signing_identity(app, cfg):
    if cfg.get("sign_identity"):
        return cfg["sign_identity"]
    details = subprocess.run(["codesign", "--display", "--verbose=4", str(app)],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    for line in details.stderr.splitlines():
        if line.startswith("Authority=Apple Development:"):
            return line.removeprefix("Authority=")
    raise SystemExit("Could not identify the development certificate; set sign_identity in ios/config.local.json")


def native_libraries():
    paths = [BUILD / "mono/mini" / name for name in (
        "libmonosgen-2.0.a", "libmono-component-marshal-ilgen-static.a",
        "libmono-component-debugger-stub-static.a", "libmono-component-hot_reload-stub-static.a",
        "libmono-component-diagnostics_tracing-stub-static.a")]
    if not all(path.is_file() for path in paths):
        raise SystemExit("Build the runtime first: python3 scripts/ios/build.py runtime")
    return paths


def prepare_adapters(managed):
    run(DOTNET, "build", ROOT / "src/STS2JitPrepare", "-c", "Release", cwd=ROOT)
    prepare = ROOT / "src/STS2JitPrepare/bin/Release/net9.0/STS2JitPrepare.dll"
    for mode, source in (("corelib", PACK / "native/System.Private.CoreLib.dll"),
                         ("harmony", ROOT / "upstream/game-refs/0Harmony.dll")):
        run(DOTNET, prepare, mode, source, managed / "STS2JitSupport.dll", managed / source.name)


def copy_to_device(source, destination, cfg):
    run("xcrun", "devicectl", "device", "copy", "to", "--device", cfg["device"],
        "--source", source, "--destination", destination,
        "--domain-type", "appDataContainer", "--domain-identifier", cfg["bundle_id"])


def install_app(app, cfg):
    info = app / "Info.plist"
    if not info.is_file():
        raise SystemExit("Build the app before installing it")
    if plistlib.loads(info.read_bytes()).get("CFBundleIdentifier") != cfg["bundle_id"]:
        raise SystemExit("App bundle ID does not match config; rebuild before installing")
    run("xcrun", "devicectl", "device", "install", "app", "--device", cfg["device"], app)


def patch_runtime():
    patch = ROOT / "ios/jit/mono-ios-jit.patch"
    reverse = subprocess.run(["git", "apply", "--reverse", "--check", str(patch)],
        cwd=SOURCE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if reverse.returncode != 0:
        check = subprocess.run(["git", "apply", "--check", str(patch)], cwd=SOURCE)
        if check.returncode != 0:
            raise SystemExit("Runtime source differs from the supported patch; checkout left unchanged")
        run("git", "apply", patch, cwd=SOURCE)
    header = ROOT / "ios/jit/MonoJitMemory.h"
    destination = SOURCE / "src/mono/mono/utils/sts2-ios-jit-memory.h"
    if destination.exists() and destination.read_bytes() != header.read_bytes():
        raise SystemExit("Runtime alias header differs; review it before rebuilding")
    shutil.copy2(header, destination)


def runtime():
    CACHE.mkdir(exist_ok=True)
    if not SOURCE.exists():
        run("git", "clone", "--depth", "1", "--branch", "v" + VERSION,
            "https://github.com/dotnet/runtime.git", SOURCE)
    actual = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=SOURCE, text=True).strip()
    if actual != COMMIT:
        raise SystemExit("Unexpected runtime revision; existing checkout left unchanged")
    patch_runtime()
    archive = CACHE / "downloads" / f"microsoft.netcore.app.runtime.mono.ios-arm64.{VERSION}.nupkg"
    archive.parent.mkdir(exist_ok=True)
    if not archive.exists():
        urllib.request.urlretrieve("https://api.nuget.org/v3-flatcontainer/"
            f"microsoft.netcore.app.runtime.mono.ios-arm64/{VERSION}/{archive.name}", archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != PACK_SHA256:
        raise SystemExit("Runtime package checksum mismatch")
    with zipfile.ZipFile(archive) as zipped:
        zipped.extractall(CACHE / "jit-runtime-pack")
    generated = SOURCE / "artifacts/obj"
    generated.mkdir(parents=True, exist_ok=True)
    (generated / "runtime_version.h").write_text("\n".join(
        f"#define Runtime{kind}{part} {number}" for kind, parts in (
            ("File", (("MajorVersion", 9), ("MinorVersion", 0), ("BuildVersion", 20), ("RevisionVersion", 0))),
            ("Product", (("MajorVersion", 9), ("MinorVersion", 0), ("PatchVersion", 20))))
        for part, number in parts) + "\n")
    (generated / "_version.c").write_text(
        f'static char sccsid[] __attribute__((used)) = "@(#)Version {VERSION} @Commit: {COMMIT}";\n')
    run("cmake", "-S", SOURCE / "src/mono", "-B", BUILD, "-G", "Ninja",
        "-DCMAKE_SYSTEM_NAME=iOS", "-DCMAKE_OSX_ARCHITECTURES=arm64",
        "-DCMAKE_OSX_DEPLOYMENT_TARGET=16.0", "-DCMAKE_BUILD_TYPE=Release",
        "-DCLR_CMAKE_HOST_ARCH=arm64", "-DDISABLE_JIT=OFF", "-DDISABLE_AOT=ON",
        "-DDISABLE_REFLECTION_EMIT=OFF", "-DDISABLE_EXECUTABLES=ON",
        "-DDISABLE_EVENTPIPE=ON", "-DDISABLE_DEBUGGER_AGENT=ON", "-DDISABLE_LLDB=ON",
        "-DSTATIC_COMPONENTS=ON", "-DDISABLE_LINK_STATIC_COMPONENTS=ON",
        "-DDISABLE_SHARED_LIBS=ON", "-DENABLE_LAZY_GC_THREAD_CREATION=OFF",
        "-DENABLE_ICALL_EXPORT=ON", "-DGC_SUSPEND=preemptive")
    run("cmake", "--build", BUILD, "-j", "8", "--target", "monosgen-static",
        "mono-component-marshal-ilgen-static", "mono-component-debugger-stub-static",
        "mono-component-hot_reload-stub-static", "mono-component-diagnostics_tracing-stub-static")


def validate_inputs(steam):
    native_libraries()
    content = CACHE / "StS2.pck"
    metadata = CACHE / "content-input.json"
    references = ROOT / "upstream/game-refs/sts2.dll"
    assembly = steam / "Contents/Resources/data_sts2_macos_arm64/sts2.dll"
    source_pack = steam / "Contents/Resources/Slay the Spire 2.pck"
    if not all(path.is_file() for path in (content, metadata, references, assembly, source_pack,
                                          steam / "Contents/Resources/release_info.json")):
        raise SystemExit("Run build.py prepare and content using the same Steam game before building")
    expected = json.loads(metadata.read_text())
    digest = hashlib.sha256(assembly.read_bytes()).hexdigest()
    if (hashlib.sha256(references.read_bytes()).hexdigest() != digest
            or expected.get("assembly_sha256") != digest
            or expected.get("source_pack_bytes") != source_pack.stat().st_size):
        raise SystemExit("Steam game, prepared references and content differ; rerun prepare and content")


def build_game(cfg, steam, host):
    from prepare_xcode import prepare

    libraries = native_libraries()
    framework = host / "StS2/dylibs/sts2.framework"
    framework.mkdir(parents=True, exist_ok=True)
    sdk = subprocess.check_output(["xcrun", "--sdk", "iphoneos", "--show-sdk-path"], text=True).strip()
    run("xcrun", "clang++", "-dynamiclib", "-arch", "arm64", "-isysroot", sdk,
        "-miphoneos-version-min=16.0", "-std=c++17", "-fobjc-arc", "-O2",
        "-I" + str(PACK / "native/include/mono-2.0"), ROOT / "ios/jit/RuntimeHost.mm",
        ROOT / "ios/jit/MonoJitMemory.mm",
        *["-Wl,-force_load," + str(library) for library in libraries],
        "-framework", "Foundation", "-framework", "Security", "-lz", "-liconv",
        "-Wl,-install_name,@rpath/sts2.framework/sts2", "-o", framework / "sts2")
    prepare(host, cfg, (CACHE / "StS2.pck").stat().st_size)
    run("xcodebuild", "-project", host / "StS2.xcodeproj", "-scheme", "StS2",
        "-configuration", "Release", "-destination", "generic/platform=iOS",
        "-derivedDataPath", DERIVED, "-allowProvisioningUpdates",
        "-allowProvisioningDeviceRegistration", "DEVELOPMENT_TEAM=" + cfg["team_id"], "build")
    app = APP
    identity = signing_identity(app, cfg)
    shutil.copy2(steam / "Contents/Resources/release_info.json", app / "release_info.json")
    managed = app / "Managed"
    if managed.exists():
        shutil.rmtree(managed)
    managed.mkdir()
    for directory in (PACK / "lib/net9.0", ROOT / "upstream/game-refs"):
        for assembly in directory.glob("*.dll"):
            shutil.copy2(assembly, managed / assembly.name)
    for name in ("STS2JitSupport", "STS2Jit", "STS2SteamBootstrap"):
        # The standalone SteamKit2 bootstrap and its transitive dependencies
        # must be available before any game files have been downloaded.
        run(DOTNET, "build", ROOT / "src" / name, "-c", "Release", "-o", managed)
    prepare_adapters(managed)
    for library in (PACK / "native").glob("libSystem.*.dylib"):
        destination = app / "Frameworks" / library.name
        shutil.copy2(library, destination)
        run("codesign", "--force", "--sign", identity, destination)
    entitlements = DERIVED / "Build/Intermediates.noindex/StS2.build/Release-iphoneos/StS2.build/StS2.app.xcent"
    run("codesign", "--force", "--sign", identity, "--entitlements", entitlements, app)



def build_shell_unsigned(cfg, host):
    """Build an unsigned, game-free iOS shell that downloads the user's Steam game.

    The Mono JIT runtime remains exactly the patched alias-memory backend used
    by signed builds. Compile-only API stubs are never distributed.
    """
    from prepare_xcode import prepare

    libraries = native_libraries()
    framework = host / "StS2/dylibs/sts2.framework"
    framework.mkdir(parents=True, exist_ok=True)
    sdk = subprocess.check_output(["xcrun", "--sdk", "iphoneos", "--show-sdk-path"], text=True).strip()
    run("xcrun", "clang++", "-dynamiclib", "-arch", "arm64", "-isysroot", sdk,
        "-miphoneos-version-min=16.0", "-std=c++17", "-fobjc-arc", "-O2",
        "-I" + str(PACK / "native/include/mono-2.0"),
        ROOT / "ios/jit/RuntimeHost.mm", ROOT / "ios/jit/MonoJitMemory.mm",
        *["-Wl,-force_load," + str(library) for library in libraries],
        "-framework", "Foundation", "-framework", "Security", "-lz", "-liconv",
        "-Wl,-install_name,@rpath/sts2.framework/sts2", "-o", framework / "sts2")
    prepare(host, cfg, 0)  # No embedded or pre-prepared game content.
    run("xcodebuild", "-project", host / "StS2.xcodeproj", "-scheme", "StS2",
        "-configuration", "Release", "-destination", "generic/platform=iOS",
        "-derivedDataPath", DERIVED, "CODE_SIGNING_ALLOWED=NO",
        "CODE_SIGNING_REQUIRED=NO", "STRIP_STYLE=non-global",
        "DEVELOPMENT_TEAM=", "build")
    app = APP
    if not (app / "Info.plist").is_file():
        raise SystemExit("Unsigned Xcode build did not create StS2.app")

    managed = app / "Managed"
    if managed.exists():
        shutil.rmtree(managed)
    managed.mkdir()
    runtime = PACK / "lib/net9.0"
    if not runtime.is_dir():
        raise SystemExit("Pinned iOS Mono framework assembly pack is missing")
    for assembly in runtime.glob("*.dll"):
        shutil.copy2(assembly, managed / assembly.name)
    for name in ("STS2JitSupport", "STS2Jit", "STS2SteamBootstrap"):
        command = [DOTNET, "build", ROOT / "src" / name, "-c", "Release", "-o", managed]
        if name == "STS2Jit":
            command.append("-p:UseGameStub=true")
        run(*command)

    # The Godot API is a public NuGet dependency, not a game asset. In some
    # MSBuild configurations ProjectReference Private=false means this DLL is
    # resolved for compilation but omitted from the -o output; copy the
    # matching restored package explicitly if necessary.
    if not (managed / "GodotSharp.dll").is_file():
        godot_packages = Path.home() / ".nuget/packages/godotsharp/4.5.1/lib"
        candidates = sorted(godot_packages.glob("net*/GodotSharp.dll"))
        if len(candidates) != 1:
            raise SystemExit("Expected one public GodotSharp 4.5.1 NuGet assembly; restore the game-free hooks first")
        shutil.copy2(candidates[0], managed / "GodotSharp.dll")

    # The public compile-time 'sts2' stub is never installed. The authentic
    # assembly is downloaded with SteamKit2 after account ownership checks.
    (managed / "sts2.dll").unlink(missing_ok=True)
    # The original app's adapter knows how to make CoreLib report executable
    # dynamic-code support to Harmony on the patched Mono backend. Unlike the
    # Mac game build, there is no Harmony to adapt yet; Steam supplies it later.
    run(DOTNET, "build", ROOT / "src/STS2JitPrepare", "-c", "Release")
    prepare_adapter = ROOT / "src/STS2JitPrepare/bin/Release/net9.0/STS2JitPrepare.dll"
    run(DOTNET, prepare_adapter, "corelib", PACK / "native/System.Private.CoreLib.dll",
        managed / "STS2JitSupport.dll", managed / "System.Private.CoreLib.dll")

    for library in (PACK / "native").glob("libSystem.*.dylib"):
        destination = app / "Frameworks" / library.name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(library, destination)

    required = ("STS2Jit.dll", "STS2JitSupport.dll", "STS2SteamBootstrap.dll",
                "STS2SteamDownloads.dll", "SteamKit2.dll", "Mono.Cecil.dll",
                "System.Private.CoreLib.dll", "GodotSharp.dll")
    missing = [name for name in required if not (managed / name).is_file()]
    if missing:
        raise SystemExit("Unsigned host is missing managed runtime dependencies: " + ", ".join(missing))
    forbidden = [name for name in ("sts2.dll", "release_info.json", "Slay the Spire 2.pck",
                                   "StS2.pck") if (managed / name).exists() or (app / name).exists()]
    if forbidden:
        raise SystemExit("Game asset leaked into unsigned IPA build: " + ", ".join(forbidden))
    print(f"Game-free unsigned iOS host built at {app}; live Mono JIT and SteamKit2 retained.")

def install_game(cfg, push_content=False, mods=None):
    if push_content and not (CACHE / "StS2.pck").is_file():
        raise SystemExit("Prepare content first with scripts/ios/build.py content")
    if mods is not None and not mods.is_dir():
        raise SystemExit("--mods must name a directory containing mod folders")
    install_app(APP, cfg)
    if push_content:
        copy_to_device(CACHE / "StS2.pck", "Documents/StS2.pck", cfg)
    if mods is not None:
        copy_to_device(mods, "Documents/mods", cfg)
    print("Installed StS2 JIT. Enable JIT with build.py launch.")


def launch(cfg, pid=None):
    if pid is None:
        result = CACHE / "jit-launch.json"
        run("xcrun", "devicectl", "device", "process", "launch", "--device", cfg["device"],
            "--start-stopped", "--terminate-existing", "--json-output", result, cfg["bundle_id"])
        pid = json.loads(result.read_text())["result"]["process"]["processIdentifier"]
    debugger = subprocess.Popen(["xcrun", "lldb", "-o", "settings set use-color false",
        "-o", "settings set show-statusline false", "-o", "device select " + cfg["device"],
        "-o", f"device process attach -p {pid}"], stdin=subprocess.PIPE,
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    output = b""
    selector = selectors.DefaultSelector()
    selector.register(debugger.stdout, selectors.EVENT_READ)
    deadline = time.monotonic() + 90
    progress = time.monotonic() + 30
    try:
        while time.monotonic() < deadline:
            if time.monotonic() >= progress:
                print("Waiting for the iPad debugger connection...", flush=True)
                progress = time.monotonic() + 30
            if not selector.select(1):
                continue
            chunk = os.read(debugger.stdout.fileno(), 8192)
            if not chunk: break
            print(chunk.decode(errors="replace"), end="", flush=True)
            output += chunk
            if f"Process {pid} stopped".encode() in output:
                debugger.stdin.write(b"process detach\nquit\n")
                debugger.stdin.flush()
                remainder = debugger.communicate(timeout=15)[0]
                print(remainder.decode(errors="replace"), end="")
                if b"detached" not in remainder:
                    raise SystemExit("Debugger did not confirm detachment")
                print(f"JIT enabled for process {pid}. The debugger is detached.")
                return
        raise SystemExit("Could not enable JIT; check device connection and debugger output")
    finally:
        selector.close()
        if debugger.poll() is None:
            debugger.terminate()
            debugger.wait(timeout=10)
