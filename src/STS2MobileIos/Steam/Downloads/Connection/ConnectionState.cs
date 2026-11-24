namespace STS2MobileIos.Steam.Downloads.Connection;

public enum ConnectionState
{
    Idle,
    Connecting,
    Connected,
    Draining,
    Backoff,
}
