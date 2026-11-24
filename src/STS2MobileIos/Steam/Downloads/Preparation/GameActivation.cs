using System.Security.Cryptography;
using System.Text.Json;

namespace STS2MobileIos.Steam.Downloads.Preparation;

/// <summary>
/// Publishes a completely prepared, Steam-verified game to the native startup
/// gate. The ready marker is always the final write; neither an interrupted
/// download nor an interrupted preparation can be mistaken for an installation.
/// Game/code assets are not loaded by this class.
/// </summary>
public static class GameActivation
{
    public const string ReadyFileName = "StS2.ready.json";
    public sealed record Ready(ulong Manifest, long PackBytes, string AssemblySha256);

    public static async Task<Ready> ActivateAsync(string staging, string documents, ulong manifest,
        string preparedPack, CancellationToken cancellation)
    {
        documents = Path.GetFullPath(documents);
        string manifestString = manifest.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string install = Path.Combine(documents, "steam-game-installs", manifestString);
        string installedManaged = Path.Combine(install, "Managed");
        string targetPack = Path.Combine(documents, "StS2.pck");
        string marker = Path.Combine(documents, ReadyFileName);

        // An update/switch requires a separate transaction with rollback;
        // first-launch setup must never replace a working existing game.
        if (File.Exists(marker))
        {
            Ready existing = await ReadReadyAsync(marker, cancellation).ConfigureAwait(false)
                ?? throw new InvalidDataException("An existing game marker is damaged.");
            if (existing.Manifest != manifest)
                throw new InvalidOperationException("A different game build is already installed. Updating is not yet supported.");
            if (!File.Exists(targetPack) || new FileInfo(targetPack).Length != existing.PackBytes)
                throw new InvalidDataException("The installed game pack does not match its completion marker.");
            return existing;
        }
        if (!File.Exists(Path.Combine(staging, "download-complete.json")))
            throw new InvalidDataException("Steam did not verify the downloaded depot.");
        string assembly = Path.Combine(staging, "original", "sts2.dll");
        if (!File.Exists(assembly) || !File.Exists(preparedPack))
            throw new FileNotFoundException("The downloaded game is not fully prepared.");
        string expectedPrepared = Path.Combine(install, "StS2.pck");
        if (!string.Equals(Path.GetFullPath(preparedPack), Path.GetFullPath(expectedPrepared),
                StringComparison.Ordinal))
            throw new InvalidDataException("Prepared game pack was not in its manifest-specific staging folder.");
        if (File.Exists(targetPack) && !File.Exists(marker))
            throw new InvalidOperationException("Unmarked game content already exists. Back it up before installing.");

        // Build the managed directory without exposing an incomplete set to Mono.
        Directory.CreateDirectory(install);
        string managedTmp = Path.Combine(install, "Managed.preparing");
        if (Directory.Exists(managedTmp)) Directory.Delete(managedTmp, recursive: true);
        Directory.CreateDirectory(managedTmp);
        try
        {
            foreach (string directory in new[] { Path.Combine(staging, "original"), staging })
            {
                foreach (string source in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    cancellation.ThrowIfCancellationRequested();
                    string file = Path.GetFileName(source);
                    if (file != file.Trim() || file.Contains('/') || file.Contains('\\'))
                        throw new InvalidDataException("The depot contains an invalid assembly name.");
                    string target = Path.Combine(managedTmp, file);
                    File.Copy(source, target, overwrite: false);
                    // File copying can fail silently if the disk fills. Hash the
                    // installed DLL before publishing its directory.
                    byte[] hash = await Task.Run(() => SHA256.HashData(File.ReadAllBytes(source)),
                        cancellation).ConfigureAwait(false);
                    byte[] copied = await Task.Run(() => SHA256.HashData(File.ReadAllBytes(target)),
                        cancellation).ConfigureAwait(false);
                    if (!hash.AsSpan().SequenceEqual(copied))
                        throw new InvalidDataException("A game assembly changed during copying.");
                }
            }

            if (!File.Exists(Path.Combine(managedTmp, "sts2.dll")) ||
                !File.Exists(Path.Combine(managedTmp, "0Harmony.dll")))
                throw new InvalidDataException("The Steam depot is missing required game or Harmony code.");
            if (Directory.Exists(installedManaged))
                throw new InvalidOperationException("An uncommitted managed game installation exists.");
            Directory.Move(managedTmp, installedManaged);
            cancellation.ThrowIfCancellationRequested();

            var ready = new Ready(manifest, new FileInfo(preparedPack).Length,
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(assembly, cancellation).ConfigureAwait(false))));
            // A same-filesystem atomic rename exposes a complete PCK, never a
            // partially copied one. Publish the marker *after* this rename.
            File.Move(preparedPack, targetPack, overwrite: false);
            string metadataTmp = marker + ".preparing";
            await File.WriteAllTextAsync(metadataTmp, JsonSerializer.Serialize(ready), cancellation).ConfigureAwait(false);
            File.Move(metadataTmp, marker, overwrite: false);
            return ready;
        }
        finally
        {
            if (Directory.Exists(managedTmp)) Directory.Delete(managedTmp, recursive: true);
        }
    }

    public static async Task<Ready?> ReadReadyAsync(string path, CancellationToken cancellation = default)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var input = File.OpenRead(path);
            var receipt = await JsonSerializer.DeserializeAsync<Ready>(input, cancellationToken: cancellation)
                .ConfigureAwait(false);
            return receipt is { Manifest: > 0, PackBytes: > 0 } &&
                receipt.AssemblySha256.Length == 64 ? receipt : null;
        }
        catch (Exception error) when (error is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
