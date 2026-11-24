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
        string preparedPack, CancellationToken cancellation, Action<string, string>? adaptHarmony = null)
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
        // A prior launch may have crashed after publishing the pack but
        // before the marker. In that case the previous pack is accepted only
        // when it hashes exactly like this verified preparation. Unrelated
        // user-provided packs are never silently replaced.
        bool priorPack = File.Exists(targetPack);
        if (priorPack && !await IdenticalAsync(targetPack, preparedPack, cancellation).ConfigureAwait(false))
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
                    // The Steam layout stores a few DLLs both in its managed
                    // directory and as separately requested top-level files.
                    // They must be byte-identical; never let duplicate names
                    // silently shadow a different assembly.
                    byte[] hash = await Task.Run(() => SHA256.HashData(File.ReadAllBytes(source)),
                        cancellation).ConfigureAwait(false);
                    if (!File.Exists(target))
                        File.Copy(source, target, overwrite: false);
                    byte[] copied = await Task.Run(() => SHA256.HashData(File.ReadAllBytes(target)),
                        cancellation).ConfigureAwait(false);
                    if (!hash.AsSpan().SequenceEqual(copied))
                        throw new InvalidDataException("Two Steam game assemblies share a name but have different contents.");
                }
            }

            if (!File.Exists(Path.Combine(managedTmp, "sts2.dll")) ||
                !File.Exists(Path.Combine(managedTmp, "0Harmony.dll")))
                throw new InvalidDataException("The Steam depot is missing required game or Harmony code.");
            // This is the critical runtime-Harmony step: patch the downloaded
            // game-matched Harmony copy to use sts2_jit_patch, while retaining
            // the untouched Steam original in the depot staging folder.
            adaptHarmony?.Invoke(Path.Combine(staging, "original", "0Harmony.dll"),
                Path.Combine(managedTmp, "0Harmony.dll"));
            if (Directory.Exists(installedManaged))
            {
                // Resume a crash between directory publication and the ready
                // marker. Only reuse the directory when every file, including
                // rewritten Harmony, is byte-for-byte identical.
                var pending = Directory.EnumerateFiles(managedTmp, "*.dll").ToList();
                var existing = Directory.EnumerateFiles(installedManaged, "*.dll").ToList();
                if (pending.Count != existing.Count)
                    throw new InvalidDataException("Uncommitted managed installation differs; manual recovery required.");
                foreach (var candidate in pending)
                {
                    string prior = Path.Combine(installedManaged, Path.GetFileName(candidate));
                    if (!File.Exists(prior) ||
                        !await IdenticalAsync(candidate, prior, cancellation).ConfigureAwait(false))
                        throw new InvalidDataException("Uncommitted managed installation differs; manual recovery required.");
                }
            }
            else Directory.Move(managedTmp, installedManaged);
            cancellation.ThrowIfCancellationRequested();

            var ready = new Ready(manifest, new FileInfo(preparedPack).Length,
                Convert.ToHexStringLower(await HashFileAsync(assembly, cancellation).ConfigureAwait(false)));
            // A same-filesystem atomic rename exposes a complete PCK. If an
            // interrupted previous activation already published these exact
            // bytes, leave them in place; only now publish the ready marker.
            if (!priorPack) File.Move(preparedPack, targetPack, overwrite: false);
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

    private static Task<byte[]> HashFileAsync(string path, CancellationToken cancellation) =>
        Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            return SHA256.HashData(stream);
        }, cancellation);

    private static async Task<bool> IdenticalAsync(string left, string right, CancellationToken cancellation)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        byte[] a = await HashFileAsync(left, cancellation).ConfigureAwait(false);
        byte[] b = await HashFileAsync(right, cancellation).ConfigureAwait(false);
        return a.AsSpan().SequenceEqual(b);
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
