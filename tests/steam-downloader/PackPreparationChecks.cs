using System.Security.Cryptography;
using System.Text;
using STS2MobileIos.Steam.Downloads.Preparation;

internal static class PackPreparationChecks
{
    private sealed record Entry(string Name, ulong Offset, byte[] Data);

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }

    private static void MakeSource(string file)
    {
        using var stream = File.Open(file, FileMode.Create);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("GDPC"));
        writer.Write(3u);
        writer.Write(4u);
        writer.Write(5u);
        writer.Write(1u);
        writer.Write(0u); // unencrypted, absolute offsets
        writer.Write(0ul); // base
        writer.Write(0ul); // directory offset at file position 32
        var entries = new List<Entry>();
        foreach (var (name, bytes) in new (string Name, byte[] Data)[] {
            (".godot/extension_list.cfg",
                Encoding.UTF8.GetBytes("res://addons/fmod/fmod.gdextension\nres://addons/sentry/sentry.gdextension\nres://addons/spine/spine_godot_extension.gdextension\n")),
            ("project.binary", Encoding.UTF8.GetBytes("test autoload/SentryInit end")),
            ("addons/sentry/sentry.gdextension", Encoding.UTF8.GetBytes("unwanted extension")),
        })
        {
            while ((stream.Position & 7) != 0) writer.Write((byte)0);
            ulong offset = (ulong)stream.Position;
            writer.Write(bytes);
            entries.Add(new Entry(name, offset, bytes));
        }
        long directory = stream.Position;
        writer.Write(entries.Count);
        foreach (var entry in entries)
        {
            byte[] name = Encoding.UTF8.GetBytes(entry.Name);
            byte[] padded = new byte[(name.Length + 3) & ~3];
            name.CopyTo(padded, 0);
            writer.Write(padded.Length);
            writer.Write(padded);
            writer.Write(entry.Offset);
            writer.Write((ulong)entry.Data.Length);
            writer.Write(MD5.HashData(entry.Data));
            writer.Write(0u);
        }
        stream.Position = 32;
        writer.Write((ulong)directory);
    }

    private static Dictionary<string, byte[]> ReadPrepared(string file)
    {
        using var stream = File.OpenRead(file);
        using var reader = new BinaryReader(stream);
        Assert(Encoding.ASCII.GetString(reader.ReadBytes(4)) == "GDPC", "Missing pack header");
        stream.Position = 32;
        long directory = (long)reader.ReadUInt64();
        stream.Position = directory;
        int count = reader.ReadInt32();
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int i = 0; i < count; ++i)
        {
            int nameLength = reader.ReadInt32();
            string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength)).TrimEnd('\0');
            long at = (long)reader.ReadUInt64();
            int length = checked((int)reader.ReadUInt64());
            byte[] hash = reader.ReadBytes(16);
            Assert(reader.ReadUInt32() == 0, "Encrypted resource was created");
            long after = stream.Position;
            stream.Position = at;
            byte[] bytes = reader.ReadBytes(length);
            Assert(MD5.HashData(bytes).SequenceEqual(hash), "Resource hash mismatch: " + name);
            result.Add(name, bytes);
            stream.Position = after;
        }
        Assert(stream.Position == stream.Length, "Unexpected bytes after directory");
        return result;
    }

    public static async Task RunAsync()
    {
        using var temp = new TempDirectory();
        string staging = Path.Combine(temp.Path, "download");
        Directory.CreateDirectory(Path.Combine(staging, "original"));
        string source = Path.Combine(staging, "original", "Slay the Spire 2.pck");
        MakeSource(source);
        byte[] originalBytes = File.ReadAllBytes(source);
        await File.WriteAllTextAsync(Path.Combine(staging, "original", "sts2.dll"),
            "a res://scenes/first.cs b res://scenes/second.cs c res://scenes/first.cs");
        string output = Path.Combine(temp.Path, "prepared", "StS2.pck");

        try
        {
            await GodotPackPreparation.PrepareAsync(staging, output, CancellationToken.None);
            throw new Exception("Unverified depot was accepted");
        }
        catch (InvalidDataException) { }

        await File.WriteAllTextAsync(Path.Combine(staging, "download-complete.json"), "{}");
        var receipt = await GodotPackPreparation.PrepareAsync(staging, output, CancellationToken.None);
        Assert(receipt.PlaceholderScripts == 2, "Script placeholder count is incorrect");
        Assert(File.ReadAllBytes(source).SequenceEqual(originalBytes), "Source pack was modified");
        var files = ReadPrepared(output);
        Assert(files.Count == 4, "Incorrect number of entries");
        Assert(!files.ContainsKey("addons/sentry/sentry.gdextension"), "Sentry extension remains");
        Assert(Encoding.UTF8.GetString(files[".godot/extension_list.cfg"]).Contains("fmod"),
            "FMOD extension removed");
        Assert(!Encoding.UTF8.GetString(files[".godot/extension_list.cfg"]).Contains("sentry"),
            "Reporting extension remains in extension list");
        Assert(Encoding.UTF8.GetString(files["project.binary"]).Contains("xutoload/SentryInit"),
            "Reporting autoload was not disabled");
        Assert(files["res://scenes/first.cs"].SequenceEqual(new byte[] { 10 }), "Script placeholder missing");
        Assert(files["res://scenes/second.cs"].SequenceEqual(new byte[] { 10 }), "Second script missing");

        // A cancelled conversion cannot overwrite a valid prepared pack.
        byte[] previous = File.ReadAllBytes(output);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await GodotPackPreparation.PrepareAsync(staging, output, cancelled.Token);
            throw new Exception("Cancelled preparation continued");
        }
        catch (OperationCanceledException) { }
        Assert(File.ReadAllBytes(output).SequenceEqual(previous), "Cancellation damaged prepared pack");

        Console.WriteLine("Godot pack preparation checks passed: unchanged source, Sentry removal, placeholders, hashes, atomic output.");
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sts2-pack-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
