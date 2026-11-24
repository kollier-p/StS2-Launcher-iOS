using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace STS2MobileIos.Steam.Downloads.Preparation;

/// <summary>
/// Converts a verified Steam Godot GDPC v3 pack to the layout this iOS Mono JIT
/// host consumes. Input is never modified; output is published only after all
/// indexes, script placeholders and source assets have been checked.
/// </summary>
public static class GodotPackPreparation
{
    private const uint Magic = 0x43504447; // GDPC, little-endian
    private const uint Format = 3;
    private const int MaxEntries = 250_000;
    private const int MaxPathBytes = 16_384;
    private const int MaxScripts = 20_000;
    private const int MaxModifiedResourceBytes = 16 * 1024 * 1024;
    private static readonly byte[] Placeholder = [(byte)'\n'];
    private static readonly byte[] SentryAutoload = Encoding.UTF8.GetBytes("autoload/SentryInit");
    private static readonly byte[] DisabledAutoload = Encoding.UTF8.GetBytes("xutoload/SentryInit");
    private static readonly Regex ScriptPaths = new(
        @"res://[A-Za-z0-9_/.]+\.cs", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class FileEntry
    {
        public required byte[] Name { get; init; }
        public required string Path { get; init; }
        public ulong Offset { get; set; }
        public ulong Length { get; set; }
        public required byte[] Md5 { get; set; }
        public uint Flags { get; set; }
    }

    private sealed record PackIndex(
        ulong DirectoryStart, ulong FilesBase, bool Relative, IReadOnlyList<FileEntry> Entries);

    public sealed record Result(string SourcePack, string OutputPack, int OriginalEntries, int PlaceholderScripts, long Size);

    /// <summary>
    /// The original pack and sts2.dll come from the SteamKit-verified depot, as
    /// <c>original/Slay the Spire 2.pck</c> and <c>original/sts2.dll</c>.
    /// A crash leaves at most an untrusted .preparing file. The final output is
    /// replaced using an atomic rename only after complete verification.
    /// </summary>
    public static async Task<Result> PrepareAsync(
        string downloadedFolder, string outputPack, CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        string pack = Path.Combine(downloadedFolder, "original", "Slay the Spire 2.pck");
        string gameAssembly = Path.Combine(downloadedFolder, "original", "sts2.dll");
        if (!File.Exists(pack) || !File.Exists(gameAssembly))
            throw new FileNotFoundException("Steam game files have not finished downloading.");
        // A completion marker is only written after Steam validates every file's
        // full hash. A partial Steam download must never be prepared.
        if (!File.Exists(Path.Combine(downloadedFolder, "download-complete.json")))
            throw new InvalidDataException("The Steam download has not been verified.");

        var scripts = ReadScriptPaths(gameAssembly);
        cancellationToken.ThrowIfCancellationRequested();

        outputPack = Path.GetFullPath(outputPack);
        string temporary = outputPack + ".preparing";
        if (string.Equals(Path.GetFullPath(pack), outputPack, StringComparison.Ordinal) ||
            string.Equals(Path.GetFullPath(pack), temporary, StringComparison.Ordinal))
            throw new ArgumentException("Do not overwrite Steam's original downloaded pack.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPack)!);
        progress?.Report("Checking the original game pack");
        PackIndex index;
        await using (var source = new FileStream(pack, FileMode.Open, FileAccess.Read, FileShare.Read,
                         1024 * 1024, FileOptions.SequentialScan))
            index = ReadIndex(source);

        // The verified source remains intact. A fresh output is needed even after
        // an interrupted preparation, which can leave partially changed indexes.
        try
        {
            await using (var source = new FileStream(pack, FileMode.Open, FileAccess.Read, FileShare.Read,
                             1024 * 1024, FileOptions.SequentialScan))
            await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite,
                             FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            {
                progress?.Report("Copying original game assets");
                await source.CopyToAsync(target, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Disabling the desktop reporting extension");
                var entries = index.Entries.ToList();
                RewriteReportingEntries(target, index, entries);
                progress?.Report("Binding game script resources");
                int added = AddPlaceholders(target, index, entries, scripts, cancellationToken);
                WriteIndex(target, index, entries);
                target.Flush(flushToDisk: true);
                cancellationToken.ThrowIfCancellationRequested();

                // Verify the newly indexed pack without loading the huge payload.
                var verification = ReadIndex(target);
                if (verification.Entries.Count != entries.Count ||
                    verification.Entries.Any(entry => entry.Path == "addons/sentry/sentry.gdextension"))
                    throw new InvalidDataException("The prepared pack's file index is invalid.");
            }

            // Rename inside the same directory/filesystem. No consumer may open
            // .preparing as a game. On success, the complete file appears at once.
            File.Move(temporary, outputPack, overwrite: true);
            return new Result(pack, outputPack, index.Entries.Count,
                scripts.Count(script => !index.Entries.Any(entry => entry.Path == script)),
                new FileInfo(outputPack).Length);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            throw;
        }
    }

    private static HashSet<string> ReadScriptPaths(string assembly)
    {
        // The managed game binary is small compared to the pack. Scan only its
        // UTF-8 embedded res:// paths; do not load or execute the game's DLL.
        var bytes = File.ReadAllBytes(assembly);
        var embedded = Encoding.Latin1.GetString(bytes);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in ScriptPaths.Matches(embedded))
        {
            string script = match.Value[6..];
            if (script.StartsWith('/') || script.Split('/').Any(s => s is "." or ".." or ""))
                throw new InvalidDataException("The game assembly listed an unsafe script path.");
            result.Add(script);
            if (result.Count > MaxScripts)
                throw new InvalidDataException("Too many script paths in this game build.");
        }
        if (result.Count == 0)
            throw new InvalidDataException("This game build has no recognizable script paths.");
        return result;
    }

    private static PackIndex ReadIndex(Stream stream)
    {
        if (!stream.CanSeek) throw new InvalidDataException("Game pack is not seekable.");
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic || reader.ReadUInt32() != Format)
            throw new InvalidDataException("Only unencrypted Godot GDPC v3 packs are supported.");
        reader.ReadBytes(12); // engine major, minor and patch
        uint flags = reader.ReadUInt32();
        if ((flags & 1) != 0) throw new InvalidDataException("Encrypted game packs are not supported.");
        ulong filesBase = reader.ReadUInt64();
        ulong directoryStart = reader.ReadUInt64();
        if (directoryStart < 40 || directoryStart >= (ulong)stream.Length)
            throw new InvalidDataException("Invalid pack directory offset.");
        stream.Position = checked((long)directoryStart);
        int count = reader.ReadInt32();
        if (count < 1 || count > MaxEntries)
            throw new InvalidDataException("Invalid pack entry count.");
        var result = new List<FileEntry>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int size = reader.ReadInt32();
            if (size < 1 || size > MaxPathBytes || size > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid game resource name.");
            byte[] name = reader.ReadBytes(size);
            string path = Encoding.UTF8.GetString(name).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path) || path.StartsWith('/') ||
                path.Contains('\\') || path.Split('/').Any(s => s == ".."))
                throw new InvalidDataException("Duplicate or unsafe game pack resource name.");
            ulong offset = reader.ReadUInt64(), length = reader.ReadUInt64();
            byte[] hash = reader.ReadBytes(16);
            uint entryFlags = reader.ReadUInt32();
            if (hash.Length != 16 || (entryFlags & 1) != 0)
                throw new InvalidDataException("Unsupported or encrypted pack resource.");
            ulong actual = checked(offset + (((flags & 2) != 0) ? filesBase : 0UL));
            if (actual > directoryStart || length > directoryStart - actual)
                throw new InvalidDataException("Resource points outside the game's file-data area.");
            result.Add(new FileEntry { Name = name, Path = path, Offset = offset, Length = length, Md5 = hash, Flags = entryFlags });
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Expected the resource index at the end of the game pack.");
        return new PackIndex(directoryStart, filesBase, (flags & 2) != 0, result);
    }

    private static byte[] ReadResource(Stream stream, PackIndex index, FileEntry entry)
    {
        if (entry.Length > MaxModifiedResourceBytes)
            throw new InvalidDataException("Unexpectedly large resource metadata in game pack.");
        stream.Position = checked((long)(entry.Offset + (index.Relative ? index.FilesBase : 0UL)));
        byte[] data = new byte[checked((int)entry.Length)];
        stream.ReadExactly(data);
        return data;
    }

    private static void RewriteReportingEntries(Stream stream, PackIndex index, List<FileEntry> entries)
    {
        var extension = entries.SingleOrDefault(e => e.Path == ".godot/extension_list.cfg")
            ?? throw new InvalidDataException("Game pack has no extension listing.");
        var project = entries.SingleOrDefault(e => e.Path == "project.binary")
            ?? throw new InvalidDataException("Game pack has no project settings.");
        var sentry = entries.SingleOrDefault(e => e.Path == "addons/sentry/sentry.gdextension")
            ?? throw new InvalidDataException("Game pack has no expected Sentry extension.");

        byte[] original = ReadResource(stream, index, extension);
        using var filtered = new MemoryStream();
        int start = 0;
        for (int i = 0; i <= original.Length; i++)
        {
            if (i < original.Length && original[i] != (byte)'\n') continue;
            int length = i - start;
            if (length > 0 &&
                !Encoding.UTF8.GetString(original, start, length).Contains("sentry", StringComparison.OrdinalIgnoreCase))
            {
                filtered.Write(original, start, length);
                filtered.WriteByte((byte)'\n');
            }
            start = i + 1;
        }
        byte[] replacement = filtered.ToArray();
        if (replacement.Length > original.Length)
            throw new InvalidDataException("Reporting removal would grow the extension listing.");
        string listing = Encoding.UTF8.GetString(replacement);
        if (!listing.Contains("fmod", StringComparison.OrdinalIgnoreCase) ||
            !listing.Contains("spine", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected game extension list; refusing to patch.");
        WriteResource(stream, index, extension, replacement);

        byte[] projectData = ReadResource(stream, index, project);
        int pos = projectData.AsSpan().IndexOf(SentryAutoload);
        if (pos >= 0)
        {
            DisabledAutoload.CopyTo(projectData.AsSpan(pos));
            WriteResource(stream, index, project, projectData);
        }

        entries.Remove(sentry);
    }

    private static void WriteResource(Stream stream, PackIndex index, FileEntry entry, byte[] data)
    {
        if ((ulong)data.Length > entry.Length)
            throw new InvalidDataException("Patch exceeds resource's original allocation.");
        stream.Position = checked((long)(entry.Offset + (index.Relative ? index.FilesBase : 0UL)));
        stream.Write(data);
        entry.Length = checked((ulong)data.Length);
        entry.Md5 = MD5.HashData(data);
    }

    private static int AddPlaceholders(Stream stream, PackIndex index, List<FileEntry> entries, HashSet<string> scripts, CancellationToken ct)
    {
        var existing = new HashSet<string>(entries.Select(e => e.Path), StringComparer.Ordinal);
        stream.Position = checked((long)index.DirectoryStart);
        int added = 0;
        foreach (string path in scripts.OrderBy(s => s, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!existing.Add(path)) continue;
            Align(stream);
            ulong position = checked((ulong)stream.Position);
            stream.Write(Placeholder);
            byte[] rawName = Encoding.UTF8.GetBytes(path);
            byte[] paddedName = new byte[(rawName.Length + 3) & ~3];
            rawName.CopyTo(paddedName, 0);
            entries.Add(new FileEntry
            {
                Name = paddedName, Path = path,
                Offset = index.Relative ? checked(position - index.FilesBase) : position,
                Length = 1, Md5 = MD5.HashData(Placeholder), Flags = 0
            });
            added++;
        }
        Align(stream);
        return added;
    }

    private static void Align(Stream stream)
    {
        while ((stream.Position & 7) != 0) stream.WriteByte(0);
    }

    private static void WriteIndex(Stream stream, PackIndex index, List<FileEntry> entries)
    {
        long newIndex = stream.Position;
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(entries.Count);
        foreach (var entry in entries)
        {
            writer.Write(entry.Name.Length);
            writer.Write(entry.Name);
            writer.Write(entry.Offset);
            writer.Write(entry.Length);
            writer.Write(entry.Md5);
            writer.Write(entry.Flags);
        }
        stream.SetLength(stream.Position);
        stream.Position = 32; // Godot GDPC v3 header's directory-offset field.
        writer.Write((ulong)newIndex);
    }
}
