# Reference-first launcher migration plan

Target: `kollier-p/StS2-Launcher-iOS` branch `feature/steamkit2-integration`.
Implementation reference only: `kollier-p/sts2-ios-launcher-public` branch
`feature/steam-workshop-browser`. Do not merge into or modify that repository.

## Verified differences

| Area | Target currently | Reference implementation |
| --- | --- | --- |
| Initial screen | `ios/jit/JitGate.mm` blocks Godot until `Documents/StS2.pck` has a build-pinned size | `STS2Mobile.Launcher/EntryScreen.cs` runs independently of downloaded game |
| Steam transfer | `STS2SteamDownloads` downloads a selected depot into staging from an in-game panel | `STS2Mobile.Steam` feeds a separate `GameInstaller` |
| Processing | `scripts/ios/pck_patch_sentry.py` and `pck_add_cs.py` run on a Mac | `STS2Mobile.GamePatcher` and `STS2Mobile.GamePack` process game inputs on-device |
| Boot | `RuntimeHost.mm` loads `sts2` and `STS2Jit` assemblies embedded in the IPA | `STS2Mobile.GameHost` supports launcher-to-game lifecycle |
| IPA | `.github/workflows/build-ipa.yml` currently requires self-hosted Mac, owned game and signing | Reference `.github/workflows/build-ipa.yml` uses hosted `xcode-27`, `--unsigned`, and a no-game-file scan |

## Required implementation sequence

1. **Decouple startup**: create a game-independent launcher scene and managed entrypoint that
   can run before `StS2.pck` or `sts2.dll` exists. Replace the hard content gate
   with a launcher-to-game transition. Retain JIT permission checks for game execution,
   not for displaying the launcher.
2. **Move Steam sign-in/download into launcher**: adapt the existing SteamKit2
   ownership/depot downloader and Keychain integration. Keep staged files under
   manifest-scoped directories, and never modify the active game while downloading.
3. **Port preparation pipeline**: adapt the reference's `GameAssemblyPreparation`,
   `GameAssemblyBuilder`, `GamePackPreparation` and `GamePackBuilder` for the
   target's Mono JIT runtime. Do not assume the reference's processed assemblies
   can be swapped into this host without validating Godot and managed runtime contracts.
4. **Safe activation and boot**: verify a complete compatible assembly/PCK pair,
   atomically mark the installation active, preserve the previous installation for
   rollback, and start the engine from the installed content. Never replace a
   currently loaded assembly or pack.
5. **Credential-free unsigned IPA**: once the IPA no longer embeds game assemblies,
   adapt the reference workflow's hosted macOS build, `--unsigned` export, no-game-file
   scanner, and artifact upload. It must build from a clean checkout without Steam
   files or Apple secrets. Verify the IPA contains the launcher and runtime but no
   proprietary game files. End-user sideloaders provide signing.

## Current status

Only the Steam depot staging portion of step 2 exists. The other steps are
**not implemented**. The target's current signed IPA workflow is a temporary
build pipeline and does not meet the intended credential-free requirement.
It must be replaced rather than presented as the final deliverable.

Do not claim first-launch boot, an unsigned artifact, or physical-device
compatibility until demonstrated by CI and a real device.
