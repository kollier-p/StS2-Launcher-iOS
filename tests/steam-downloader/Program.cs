using System.Security.Cryptography;
using SteamKit2.CDN;
using STS2MobileIos.Steam.Downloads.Depot;

static class Checks
{
    private sealed class FakeSource(byte[] content) : IDepotChunkSource
    {
        public List<ulong> Reads { get; } = [];
        public Func<DepotManifest.ChunkData, Exception?>? Fail { get; set; }

        public Task<int> ReadAsync(DepotManifest.ChunkData chunk, byte[] destination, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Reads.Add(chunk.Offset);
            if (Fail?.Invoke(chunk) is { } error) throw error;
            var length = (int)chunk.UncompressedLength;
            content.AsSpan((int)chunk.Offset, length).CopyTo(destination);
            return Task.FromResult(length);
        }
    }

    private static DepotManifest.FileData ManifestFile(byte[] bytes)
    {
        var result = new DepotManifest.FileData { TotalSize = (ulong)bytes.Length, Chunks = [] };
        for (int at = 0; at < bytes.Length; at += 1024)
        {
            var block = bytes.AsSpan(at, Math.Min(1024, bytes.Length - at));
            result.Chunks.Add(new DepotManifest.ChunkData(
                SHA1.HashData(block), DepotChunk.AdlerHash(block),
                (ulong)at, (uint)block.Length / 2, (uint)block.Length));
        }
        result.FileHash = SHA1.HashData(bytes);
        return result;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    public static async Task Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sts2-steam-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] original = RandomNumberGenerator.GetBytes(8192);
            var content = new FakeSource(original);
            string file = Path.Combine(directory, "nested", "sts2.bin");
            var depotFile = new DepotFile(ManifestFile(original), file,
                Convert.ToHexStringLower(SHA256.HashData(original)));
            var downloader = new DepotDownload(content, [depotFile], workers: 2);
            var first = await downloader.RunAsync(CancellationToken.None);
            Require(first.DownloadedChunks == 8, "Initial transfer missed chunks");
            Require(File.ReadAllBytes(file).SequenceEqual(original), "File content mismatch");
            Require(!File.Exists(file + DepotDownload.PartialSuffix), "Partial file not renamed");

            content.Reads.Clear();
            var second = await new DepotDownload(content, [depotFile]).RunAsync(CancellationToken.None);
            Require(second.ReusedFiles == 1 && content.Reads.Count == 0, "Verified file was downloaded again");

            var damaged = (byte[])original.Clone();
            damaged[10] ^= 1;
            File.WriteAllBytes(file, damaged);
            content.Reads.Clear();
            await new DepotDownload(content, [depotFile], workers: 2).RunAsync(CancellationToken.None);
            Require(File.ReadAllBytes(file).SequenceEqual(original), "Damaged file was not repaired");
            Require(content.Reads.Count == 8, "Damaged complete file was trusted");

            File.Delete(file);
            File.WriteAllBytes(file + DepotDownload.PartialSuffix, original[..4096]);
            content.Reads.Clear();
            var resumed = await new DepotDownload(content, [depotFile], workers: 2).RunAsync(CancellationToken.None);
            Require(resumed.ReusedChunks == 4 && content.Reads.Count == 4, "Partial download did not resume");
            Require(File.ReadAllBytes(file).SequenceEqual(original), "Resumed file failed verification");

            File.Delete(file);
            content.Reads.Clear();
            using var cancelled = new CancellationTokenSource();
            content.Fail = _ =>
            {
                cancelled.Cancel();
                return new OperationCanceledException(cancelled.Token);
            };
            try
            {
                await new DepotDownload(content, [depotFile], workers: 1).RunAsync(cancelled.Token);
                throw new Exception("Cancellation did not propagate");
            }
            catch (OperationCanceledException) { }
            Require(File.Exists(file + DepotDownload.PartialSuffix), "Cancelled download deleted resumable partial");

            Require(SteamDepot.IsUsable(true, "cache1-tyo3.steamcontent.com", 443, false, false, [], 7),
                "Allowed Steam HTTPS CDN rejected");
            Require(!SteamDepot.IsUsable(true, "steampipe.akamaized.net", 443, false, false, [], 7),
                "Unapproved CDN was accepted");
            Require(!SteamDepot.IsUsable(true, "cache1-tyo3.steamcontent.com", 443, true, false, [], 7),
                "Steam proxy route was accepted");

            Console.WriteLine("Steam downloader checks passed: hashes, resume, corruption, cancellation and CDN policy.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

