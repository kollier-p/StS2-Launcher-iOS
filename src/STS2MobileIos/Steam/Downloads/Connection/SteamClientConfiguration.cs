using SteamKit2;
using STS2MobileIos.Steam.Downloads.Network;

namespace STS2MobileIos.Steam.Downloads.Connection;

public static class SteamClientConfiguration
{
    /// <summary>WebSocket connections to policy-approved servers, with every HTTP request going through the Steam network policy.</summary>
    public static SteamConfiguration Create() => SteamConfiguration.Create(builder => builder
        .WithProtocolTypes(ProtocolTypes.WebSocket)
        .WithServerListProvider(new PolicyServerListProvider())
        // SteamKit must not bypass the provider with its own unfiltered directory lookup.
        .WithDirectoryFetch(false)
        .WithHttpClientFactory(_ => SteamHttpClient.Create()));
}
