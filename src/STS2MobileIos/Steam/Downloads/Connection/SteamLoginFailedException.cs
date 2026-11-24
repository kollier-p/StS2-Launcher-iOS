using SteamKit2;

namespace STS2MobileIos.Steam.Downloads.Connection;

public sealed class SteamLoginFailedException(EResult result) : InvalidOperationException($"Login failed: {result}")
{
    public EResult Result { get; } = result;
}
