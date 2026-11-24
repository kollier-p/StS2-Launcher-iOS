# On-device Steam game-file downloads (first milestone)

This feature is under development on `feature/steamkit2-integration`.
It downloads owned **Slay the Spire 2** game inputs directly from Steam to the iPad
or iPhone and verifies the downloaded bytes. It does **not yet** prepare a playable
PCK or permit a clean first launch without the existing Mac preparation process.

## Using the downloader

1. Install and launch the existing JIT build with a prepared game as described in
   [Build and install](building.md). This limitation will be addressed in the next milestone.
2. In the running game, open **iOS > Steam Cloud** (the panel title now also mentions
   game files). Tap **Connect Steam**, scan the QR code with the Steam app and approve
   the login. A saved Keychain session can also be restored.
3. Tap **Download owned game files from Steam**. The app checks your ownership
   ticket using SteamKit2, looks up the current **stable** (`public`) manifest
   for app 2868840 / macOS ARM64 depot 2868842, and downloads its required files.
4. Watch the status for progress. **Cancel download** or closing the panel stops the
   operation; return and download again to resume. Do not force-quit while files
   are being written if you can avoid it.
5. After completion, the checked files and `download-complete.json` live in
   **Files > On My iPad > StS2 JIT > steam-game-downloads > <manifest ID>**.
   Only a completely verified download has the completion marker.

The downloader fetches the same game inputs selected by the reference project's
`GameLayout`: release information, the original Godot PCK, the game assembly,
the reporting SDK assemblies when present, and the Steamworks/SmartFormat DLLs.
It does not download unnecessary desktop executables or other depots.

## Integrity and privacy

- A SteamKit2 connection uses the Steam refresh token already held in the
  device-only iOS Keychain. The token and login secrets are not stored in the
  staging folder or included in download receipts.
- An ownership ticket is required before requesting the protected depot key.
- Only HTTPS content servers whose hostnames meet the port/domain policy are
  selected. No redirect or alternate Steam CDN URL is followed by the downloader.
- Every downloaded chunk is checked using the Steam manifest checksum, and every
  completed file is checked using its manifest SHA-1. A failed file is not
  published as complete.
- Downloads write only into `steam-game-downloads/<manifest ID>/`, preserving
  `Documents/StS2.pck`, active managed assemblies, save profiles and mods.
  A different game manifest uses a different directory.
- Interrupted downloads leave `.partial` files. Subsequent runs verify and
  reuse intact chunks, fetch missing ones and remove partials only after
  successful whole-file verification. No game assets are checked into Git.

Downloading roughly a gigabyte or more requires free storage and a reliable
connection. The current UI does not calculate free space ahead of time; an
insufficient-space error can leave a resumable partial download.

## What's still needed

The existing iOS host's startup gate requires a pre-prepared, version-pinned
`Documents/StS2.pck`, and the app build embeds managed game assemblies copied
from a Mac. To make downloads usable at **first launch**, the bootstrap and
managed runtime need to start an independent launcher without those inputs, and
on-device PCK preparation, compatibility checking and safe activation must be
added. That work is **not** part of this first downloader commit.

Steam Cloud imports still use the original HTTPS/WebSocket implementation.
This feature does not add Cloud uploads, Workshop browsing or an unsigned IPA
build workflow.

## Developer checks

The downloader is kept in an independent project to make its network and chunk
code testable without a copy of the commercial game:

```sh
dotnet build src/STS2SteamDownloads/STS2SteamDownloads.csproj -c Release
dotnet run --project tests/steam-downloader/SteamDownloaderTests.csproj -c Release
```

The offline tests exercise completed-download reuse, bad-file repair, partial
chunk reuse, cancellation and the CDN allowlist without reaching Steam.
Actual login, ownership, CDN connectivity and on-device file sharing still
require a physical device and a real account that owns the game.

The initial depot implementation was adapted from the separate
[reference launcher](https://github.com/kollier-p/sts2-ios-launcher-public/tree/feature/steam-workshop-browser),
under its MIT licence. Only the *target* repository is changed.
