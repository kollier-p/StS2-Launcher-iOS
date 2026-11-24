# SteamKit2 first-launch installation and Mono JIT game boot (experimental)

Branch: `feature/steamkit2-integration`. This is a **source-tested prototype**.
A full unsigned iOS IPA build and physical-device Steam sign-in/download/boot
have not yet been verified. Do not call it a finished or supported release.

## Architecture

The unsigned IPA contains the Godot host, patched Mono 9 JIT, managed
SteamKit2 login/downloader, Godot PCK preparer, live Harmony mod hooks, FMOD/
Spine iOS extensions, and public .NET libraries. **It does not contain
Slay the Spire 2 game files**. The `STS2GameStub` project's fake `sts2.dll`
exists only at compile time, and is removed from the packaged app.

First launch is deliberately handled by UIKit **before** Godot's game PCK is
loaded, because the original `JitGate` cannot initialize the game engine with
a missing PCK. The user must enable JIT for the app using a compatible debugger
or JIT tool. The gate then calls `sts2_bootstrap_start`, which initializes the
same patched Mono runtime that the game uses.

The managed bootstrap:

1. Shows a SteamKit2 QR code for approval with the Steam app on another device
   (or reuses the existing Keychain refresh token)
2. Obtains an ownership ticket for Steam app `2868840` and retrieves the
   current stable macOS ARM64 depot `2868842`
3. Downloads the original PCK and **all** managed DLLs, with checksum verification
   and resumable partial chunks under `Documents/steam-game-downloads/<manifest>`
4. Copies the original PCK, removes its desktop Sentry extension and autoload,
   and adds C# script placeholders at the exact `res://` resource paths
5. Copies the original managed assemblies to the manifest-specific install
   directory, and adapts the tested Harmony 2.4.2 DLL to call the patched
   Mono JIT executable-memory alias and native method-detour backend
6. Atomically publishes `Documents/StS2.pck` and, **last**, a
   `Documents/StS2.ready.json` installation marker

The app then asks to be **fully closed and reopened**. JIT must be enabled
again. The gate validates the ready marker and prepared PCK before asking
Godot to load `user://StS2.pck`. `RuntimeHost` adds the installed original
`sts2.dll` and adapted `0Harmony.dll` to the managed runtime assembly
search path and runs the usual `STS2Jit.Entry.Start` Harmony patches.

These steps are wired in source but **have not yet demonstrated a complete
real-device game boot**.

## Dynamic mods are required

This target deliberately keeps the original **JIT-enabled** Mono runtime.
Harmony must be able to patch managed game methods while it is running.
Unlike the reference's no-JIT executable, code mods are **not** limited to
patches woven in ahead of time. The original game's mod manager and
`Documents/mods` scan redirection stay in `STS2Jit/Entry.cs`.

The unsigned game-free build currently includes a **minimal** set of mobile
Harmony platform/audio hooks to avoid compiling the full UI against a private
game binary. The older local Mac build still has the complete mobile/cloud/LAN
UI. Expanded hook compatibility will require device validation.

Mod code is trusted code with app privileges. Only install mods from
developers you trust. The current first-launch pipeline checks the tested
Harmony DLL's SHA-256 and refuses unknown variants rather than applying
potentially dangerous JIT detour patches.

## Unsigned GitHub Actions IPA

The [Build unsigned SteamKit2 JIT IPA](../.github/workflows/build-ipa.yml)
workflow uses a GitHub-hosted Apple Silicon macOS/Xcode runner. It needs **no
Apple developer team ID, no signing certificate, and no Steam game installation
or account credentials**. It builds from public source and pinned public
build tools:

```sh
python3 scripts/ios/bootstrap.py
python3 scripts/ios/build.py runtime
python3 scripts/ios/build.py build-shell
python3 scripts/ios/scan_unsigned_ipa.py --app .cache/xcode-jit/Build/Products/Release-iphoneos/StS2.app
python3 scripts/ios/package_ipa.py --app .cache/xcode-jit/Build/Products/Release-iphoneos/StS2.app --output dist/StS2-JIT-unsigned.ipa --unsigned
python3 scripts/ios/scan_unsigned_ipa.py --ipa dist/StS2-JIT-unsigned.ipa
```

The workflow then checks the SHA-256 checksum and uploads the
`StS2-JIT-unsigned.ipa` and its `.sha256` file as a run artifact **if
all steps succeed**. The artifact is unsigned; iOS will not run it until the
player signs it with their own Apple ID via a sideloading tool.

For a local unsigned build, create `ios/config.local.json` with a dummy
`team_id` (e.g. `0000000000`) and a stable `bundle_id`; no signing
identity is needed. Full Xcode is still required. Keep the same bundle ID
when updating to preserve installed game, saves and Keychain data.

## Safety and testing

- Steam game bytes and credentials are never checked into this source tree
  or included in the unsigned IPA.
- The launcher checks Steam ownership before requesting depot content.
- The downloader verifies Steam chunks and completed files. Interrupted
  transfers are resumable from checked chunks.
- The preparer works from a source copy, uses bounded index parsing, and
  publishes a complete PCK atomically.
- Activation verifies any leftover unmarked game content byte-for-byte before
  resuming, and refuses mismatched or unrelated installed files.
- The native runtime verifies executable-memory/JIT permission before running
  Mono, and the game-specific Harmony adapter only modifies known matching DLLs.
- CI tests cover Steam depot integrity/recovery, synthetic pack preparation,
  game activation, JIT source contracts, and unsigned IPA packaging.

```sh
dotnet build src/STS2SteamBootstrap/STS2SteamBootstrap.csproj -c Release
dotnet build src/STS2Jit/STS2Jit.csproj -c Release -p:UseGameStub=true
dotnet run --project tests/steam-downloader/SteamDownloaderTests.csproj -c Release
python3 -m unittest discover -s scripts/ios -p 'test_unsigned_artifact.py'
```

### Still not validated

A source compile is **not** evidence that the native unsigned IPA boots on
the device. Pending: successful macOS hosted build artifact, actual Steam
authentication and depot transfer, compatibility against Steam's current
stable game build, native Godot/FM0D/Spine startup, and a live Harmony detour
from a managed mod on physical iPhone/iPad hardware. iOS restricts JIT; an
external JIT-enabling tool/debugger is still required at each launch.
Device storage usage and network backgrounding also need stress testing.

This implementation adapts the MIT-licensed SteamKit2 depot/network layer
from the [reference project](https://github.com/kollier-p/sts2-ios-launcher-public/tree/feature/steam-workshop-browser)
without importing its no-runtime-JIT limitation or modifying that repository.
