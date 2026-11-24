using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SteamKit2;
using SteamKit2.Authentication;
using STS2MobileIos.Steam.Downloads;
using STS2MobileIos.Steam.Downloads.Connection;
using STS2MobileIos.Steam.Downloads.Preparation;

namespace STS2SteamBootstrap;

/// <summary>
/// Runs before Godot and before the game is on disk. Mono is initialized by
/// RuntimeHost only after iOS JIT permission has been established; this entry
/// does not load game code and never patches or invokes its managed assemblies.
/// </summary>
public static class Entry
{
    [DllImport("__Internal", EntryPoint = "sts2_launcher_status")]
    private static extern void NativeStatus([MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    [DllImport("__Internal", EntryPoint = "sts2_launcher_qr")]
    private static extern void NativeQr([MarshalAs(UnmanagedType.LPUTF8Str)] string url);

    [DllImport("__Internal", EntryPoint = "sts2_launcher_ready")]
    private static extern void NativeReady();

    [DllImport("__Internal", EntryPoint = "sts2_keychain_read")]
    private static extern IntPtr NativeReadCredential();

    [DllImport("__Internal", EntryPoint = "sts2_keychain_write")]
    private static extern int NativeWriteCredential([MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("__Internal", EntryPoint = "sts2_free")]
    private static extern void NativeFree(IntPtr value);

    private sealed record Login(string Account, string Token, string SteamId);
    private static int _active;

    [UnmanagedCallersOnly]
    public static int Begin()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) return 0;
        _ = Task.Run(RunAsync);
        return 0;
    }

    private static async Task RunAsync()
    {
        try
        {
            string documents = Environment.GetEnvironmentVariable("STS2_DOCUMENTS")
                ?? throw new InvalidOperationException("iOS game documents folder is not configured.");
            Directory.CreateDirectory(documents);
            Status("Checking your Steam login...");
            Login login = ReadLogin() ?? await QrLoginAsync().ConfigureAwait(false);
            Status("Verifying that this account owns Slay the Spire 2...");
            var progress = new Progress<SteamGameDownloader.Progress>(p =>
            {
                if (p.Total > 0)
                    Status($"Downloading game files: {100.0 * p.Done / p.Total:F1}% ({p.Done / 1048576:N0} / {p.Total / 1048576:N0} MiB)");
                else Status(p.Phase + "...");
            });
            string root = Path.Combine(documents, "steam-game-downloads");
            SteamGameDownloader.Receipt downloaded = await SteamGameDownloader.DownloadAsync(
                login.Account, login.Token, root, progress, CancellationToken.None).ConfigureAwait(false);

            // Keep the active game untouched until all depot chunks are checked,
            // the pack has been prepared, and the managed files are ready.
            Status("Preparing the game for iOS. Keep this app open...");
            var result = await GodotPackPreparation.PrepareAsync(downloaded.Directory,
                Path.Combine(documents, "steam-game-installs",
                    downloaded.Manifest.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "StS2.pck"), CancellationToken.None, new Progress<string>(Status))
                .ConfigureAwait(false);

            Status("Installing verified game assemblies...");
            await GameActivation.ActivateAsync(downloaded.Directory, documents,
                downloaded.Manifest, result.OutputPack, CancellationToken.None).ConfigureAwait(false);
            Status("The game is ready. Close this app fully, reopen it, and enable JIT to play with mods.");
            NativeReady();
        }
        catch (Exception error)
        {
            // No refresh token, signed Steam URL or exception dump is sent to
            // the native view. A user can retry after closing and reopening.
            string description = error switch
            {
                UnauthorizedAccessException => "Steam did not confirm that this account owns the game.",
                InvalidDataException => "The downloaded game files could not be prepared for this launcher version.",
                IOException => "Storage or file access failed. Free space and reopen to resume.",
                TimeoutException => "Steam did not respond. Reopen to try again.",
                _ => "Game setup stopped (" + error.GetType().Name + "). Reopen to retry."
            };
            Status(description);
        }
        finally { Volatile.Write(ref _active, 0); }
    }

    private static Login? ReadLogin()
    {
        IntPtr pointer = NativeReadCredential();
        if (pointer == IntPtr.Zero) return null;
        try
        {
            string? json = Marshal.PtrToStringUTF8(pointer);
            if (string.IsNullOrEmpty(json)) return null;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string account = root.GetProperty("account").GetString() ?? "";
            string refresh = root.GetProperty("refresh").GetString() ?? "";
            string steamId = root.TryGetProperty("steamid", out var id) ? id.GetString() ?? "" : "";
            return account.Length > 0 && refresh.Length > 0 ? new Login(account, refresh, steamId) : null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
        finally { NativeFree(pointer); }
    }

    private static async Task<Login> QrLoginAsync()
    {
        Status("Connecting to Steam for QR sign-in...");
        using var client = new QrSignInClient();
        await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
        QrAuthSession session = await client.Client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
        {
            DeviceFriendlyName = "StS2 iOS JIT",
            IsPersistentSession = true
        }).ConfigureAwait(false);
        void ShowQr() => NativeQr(session.ChallengeURL);
        session.ChallengeURLChanged = ShowQr;
        ShowQr();
        Status("Scan the QR code using the Steam app on another device, then approve sign-in.");
        AuthPollResult approved = await session.PollingWaitForResultAsync(CancellationToken.None).ConfigureAwait(false);
        string steamId = Subject(approved.RefreshToken);
        var credentials = JsonSerializer.Serialize(new
        {
            account = approved.AccountName,
            refresh = approved.RefreshToken,
            steamid = steamId,
        });
        if (NativeWriteCredential(credentials) != 0)
            throw new IOException("Could not securely store your Steam login in Keychain.");
        NativeQr("");
        return new Login(approved.AccountName, approved.RefreshToken, steamId);
    }

    private static string Subject(string jwt)
    {
        string[] fields = jwt.Split('.');
        if (fields.Length != 3) throw new InvalidDataException("Steam login token format is unknown.");
        string base64 = fields[1].Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
        using var data = JsonDocument.Parse(Convert.FromBase64String(base64));
        string id = data.RootElement.GetProperty("sub").GetString() ?? "";
        if (id.Length != 17 || !ulong.TryParse(id, out _))
            throw new InvalidDataException("Steam returned an invalid account identifier.");
        return id;
    }

    private static void Status(string message) => NativeStatus(message);

    private sealed class QrSignInClient : IDisposable
    {
        private readonly SteamClient _steam = new(SteamClientConfiguration.Create());
        private readonly CancellationTokenSource _end = new();
        private readonly Task _pump;
        private readonly TaskCompletionSource _connected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public QrSignInClient()
        {
            var callbacks = new CallbackManager(_steam);
            callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => _connected.TrySetResult());
            callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ =>
                _connected.TrySetException(new IOException("Steam connection closed.")));
            _pump = Task.Run(() =>
            {
                while (!_end.IsCancellationRequested)
                    callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
            });
        }

        public SteamClient Client => _steam;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            _steam.Connect();
            await _connected.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _end.Cancel();
            _steam.Disconnect();
            try { _pump.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _end.Dispose();
        }
    }
}
