namespace SessionObserver.Protocol.Models;

/// <summary>Connection state of a session. Values mirror WTS_CONNECTSTATE_CLASS.</summary>
public enum SessionState
{
    Active = 0,
    Connected = 1,
    ConnectQuery = 2,
    Shadow = 3,
    Disconnected = 4,
    Idle = 5,
    Listen = 6,
    Reset = 7,
    Down = 8,
    Init = 9,
}
