namespace DongGfx.Core.Models;

/// <summary>Connection state of the Deriv WebSocket client.</summary>
public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error
}