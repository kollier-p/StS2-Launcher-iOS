using System.Text.Json;
using SteamKit2;
using SteamKit2.CDN;
using STS2MobileIos.Steam.Downloads.Abstractions;
using STS2MobileIos.Steam.Downloads.Connection;
using STS2MobileIos.Steam.Downloads.Depot;

namespace STS2MobileIos.Steam.Downloads;

/// <summary>
/// Retrieves a Steam-owned copy of the macOS ARM64 game inputs into a separate Documents
/// directory. This does NOT overwrite the active PCK or replace assemblies loaded by Mono.
/// Downloading and installing the prepared game are deliberately different operations.
/// </summary>
public static class SteamGameDownloader
{
    public const uint AppId = 2868840;
    public const uint MacArm64DepotId = 2868842;
    public const string StableBranch = "public";

    // The same inputs selected by the working reference project's GameLayout.
    // Never build filesystem paths from untrusted depot entry names.
    private const string Resources = "SlayTheSpire2.app/Contents/Resources/";
    private const string Assemblies = Resources + "data_sts2_macos_arm64/";

    private sealed record RequiredFile(string DepotPath, string LocalPath, bool Optional = false);

    private static readonly RequiredFile[] Required =
    [
        new(Resources + "release_info.json", "release_info.json"),
        new(Resources + "Slay the Spire 2.pck", "original/Slay the Spire 2.pck"),
        new(Assemblies + "sts2.dll", "original/sts2.dll"),
        new(Assemblies + "Sentry.dll", "original/Sentry.dll", true),
        new(Assemblies + "Sentry.Godot.dll", "original/Sentry.Godot.dll", true),
        new(Assemblies + "Steamworks.NET.dll", "Steamworks.NET.dll"),
        new(Assemblies + "SmartFormat.dll", "SmartFormat.dll"),
        new(Assemblies + "SmartFormat.ZString.dll", "SmartFormat.ZString.dll")
    ];

    public sealed record Progress(string Phase, long Done = 0, long Total = 0);
    public sealed record Receipt(string Directory, ulong Manifest, uint Build, long Bytes, int DownloadedChunks, int ReusedChunks, int ReusedFiles);

    private sealed class QuietLogger : ILogger
    {
        // No account credentials, signed CDN URLs or token-bearing exceptions are logged.
        public void Info(string tag, string message, Exception? exception = null) { }
        public void Warn(string tag, string message, Exception? exception = null) { }
        public void Error(string tag, string message, Exception? exception = null) { }
    }

    /// <summary>
    /// Requires a previously approved Steam QR sign-in (saved in the device Keychain). Ownership
    /// is verified with an app ticket before a manifest or depot key is requested.
    /// Good chunks in .partial files survive cancellation and are rechecked on the next run.
    /// </summary>
    public static Task<Receipt> DownloadAsync(SteamLogin login, string stagingRoot, IProgress<Progress>? progress, CancellationToken cancellation) =>
        Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(login.AccountName) || string.IsNullOrWhiteSpace(login.RefreshToken))
                throw new InvalidOperationException("Connect your Steam account before downloading game files.");
            cancellation.ThrowIfCancellationRequested();
            using var connection = new SteamConnection(login.AccountName, login.RefreshToken, new QuietLogger());

            progress?.Report(new Progress("Checking Steam ownership"));
            SteamApps.AppOwnershipTicketCallback ownership =
                await connection.Apps.GetAppOwnershipTicket(AppId);
            cancellation.ThrowIfCancellationRequested();
            if (ownership.Result != EResult.OK || ownership.AppID != AppId || ownership.Ticket is not { Length: > 0 })
                throw new UnauthorizedAccessException("This Steam account could not prove ownership of Slay the Spire 2.");

            progress?.Report(new Progress("Finding the stable game build"));
            var builds = await SteamBuilds.ReadAsync(connection, AppId, MacArm64DepotId, [StableBranch], cancellation);
            if (!builds.TryGetValue(StableBranch, out var build) || build == null)
                throw new InvalidDataException("Steam did not provide a downloadable stable macOS game build.");

            progress?.Report(new Progress("Getting the game's file list"));
            using var depot = await SteamDepot.OpenAsync(connection, AppId, MacArm64DepotId,
                build.ManifestId, StableBranch, cancellation);
            string destination = Path.Combine(stagingRoot, build.ManifestId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var files = Plan(depot.Manifest, destination);

            // A completion marker must never describe files that may be under repair.
            string marker = Path.Combine(destination, "download-complete.json");
            Directory.CreateDirectory(destination);
            if (File.Exists(marker)) File.Delete(marker);
            progress?.Report(new Progress("Checking previously downloaded chunks"));
            var result = await new DepotDownload(depot, files,
                (done, total) => progress?.Report(new Progress("Downloading game files", done, total)))
                .RunAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();

            // The marker is written only after all manifest hashes were checked, and then atomically moved.
            var receipt = new Receipt(destination, build.ManifestId, build.BuildId,
                result.Bytes, result.DownloadedChunks, result.ReusedChunks, result.ReusedFiles);
            string temporary = marker + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(receipt) + "\n");
            File.Move(temporary, marker, overwrite: true);
            progress?.Report(new Progress("Game files downloaded and verified", result.Bytes, result.Bytes));
            return receipt;
        }, cancellation);

    /// <summary>A manifest-controlled plan with fixed sandbox-relative output paths.</summary>
    public static IReadOnlyList<DepotFile> Plan(DepotManifest manifest, string directory)
    {
        if (manifest.Files == null)
            throw new InvalidDataException("Steam returned a file list without files.");

        var indexed = manifest.Files.ToDictionary(
            file => file.FileName.Replace('\\', '/'), StringComparer.Ordinal);
        var plan = new List<DepotFile>();
        foreach (var file in Required)
        {
            if (!indexed.TryGetValue(file.DepotPath, out var depotFile))
            {
                if (file.Optional) continue;
                throw new InvalidDataException("Steam's depot does not contain a required game file: " + file.DepotPath);
            }
            // Only fixed paths, never file.FileName, become local file names.
            plan.Add(new DepotFile(depotFile,
                Path.Combine(directory, file.LocalPath.Replace('/', Path.DirectorySeparatorChar)), null));
        }
        return plan.OrderBy(file => file.Source.TotalSize).ToArray();
    }
}
