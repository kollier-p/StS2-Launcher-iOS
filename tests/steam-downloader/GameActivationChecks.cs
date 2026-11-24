using STS2MobileIos.Steam.Downloads.Preparation;

internal static class GameActivationChecks
{
    private static void Assert(bool condition, string text)
    {
        if (!condition) throw new Exception(text);
    }

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "sts2-activation-" + Guid.NewGuid().ToString("N"));
        string docs = Path.Combine(root, "Documents");
        string staged = Path.Combine(root, "steam");
        ulong manifest = 12345678;
        string installed = Path.Combine(docs, "steam-game-installs", manifest.ToString());
        string prepared = Path.Combine(installed, "StS2.pck");
        Directory.CreateDirectory(Path.Combine(staged, "original"));
        Directory.CreateDirectory(installed);
        try
        {
            File.WriteAllText(Path.Combine(staged, "download-complete.json"), "{}");
            File.WriteAllBytes(Path.Combine(staged, "original", "sts2.dll"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(staged, "original", "0Harmony.dll"), [4, 5, 6]);
            File.WriteAllBytes(Path.Combine(staged, "original", "SmartFormat.dll"), [7, 8, 9]);
            File.WriteAllBytes(Path.Combine(staged, "SmartFormat.dll"), [7, 8, 9]);
            File.WriteAllBytes(prepared, [10, 11, 12, 13]);
            void Adapt(string original, string destination)
            {
                Assert(Path.GetFileName(original) == "0Harmony.dll", "Wrong Harmony assembly adapted");
                File.WriteAllBytes(destination, [42, 43]);
            }

            var ready = await GameActivation.ActivateAsync(staged, docs, manifest,
                prepared, CancellationToken.None, Adapt);
            Assert(File.Exists(Path.Combine(docs, "StS2.pck")), "Prepared pack not published");
            Assert(File.Exists(Path.Combine(docs, GameActivation.ReadyFileName)), "Ready marker not published");
            Assert(ready.Manifest == manifest && ready.PackBytes == 4, "Ready marker doesn't identify game");
            Assert(File.ReadAllBytes(Path.Combine(installed, "Managed", "0Harmony.dll"))
                .SequenceEqual(new byte[] { 42, 43 }), "Harmony detour adapter not used");
            Assert(File.ReadAllBytes(Path.Combine(staged, "original", "0Harmony.dll"))
                .SequenceEqual(new byte[] { 4, 5, 6 }), "Steam original Harmony modified");
            Assert(File.Exists(Path.Combine(installed, "Managed", "SmartFormat.dll")),
                "Overlapping Steam assemblies could not be activated");

            // A crash after moving the managed directory and pack but before
            // writing the ready marker must resume only when the old bytes match.
            File.Delete(Path.Combine(docs, GameActivation.ReadyFileName));
            File.WriteAllBytes(prepared, [10, 11, 12, 13]);
            await GameActivation.ActivateAsync(staged, docs, manifest,
                prepared, CancellationToken.None, Adapt);
            Assert(File.Exists(Path.Combine(docs, GameActivation.ReadyFileName)),
                "Interrupted activation could not resume");

            File.Delete(Path.Combine(docs, GameActivation.ReadyFileName));
            File.WriteAllBytes(prepared, [55, 56, 57, 58]);
            try
            {
                await GameActivation.ActivateAsync(staged, docs, manifest,
                    prepared, CancellationToken.None, Adapt);
                throw new Exception("Mismatched existing game pack was accepted");
            }
            catch (InvalidOperationException) { }
            Assert(File.ReadAllBytes(Path.Combine(docs, "StS2.pck"))
                .SequenceEqual(new byte[] { 10, 11, 12, 13 }), "Untrusted pack replaced existing game");
            Console.WriteLine("Game activation checks passed: managed integrity, Harmony adaptation, crash resume and safe replacement.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
