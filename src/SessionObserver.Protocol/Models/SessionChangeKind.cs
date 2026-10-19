namespace SessionObserver.Protocol.Models;

/// <summary>
/// Kind of session change. Values 1-11 mirror the WTS_SESSION_* notification codes
/// delivered with WM_WTSSESSION_CHANGE / SERVICE_CONTROL_SESSIONCHANGE.
/// </summary>
public enum SessionChangeKind
{
    Unknown = 0,
    ConsoleConnect = 1,
    ConsoleDisconnect = 2,
    RemoteConnect = 3,
    RemoteDisconnect = 4,
    SessionLogon = 5,
    SessionLogoff = 6,
    SessionLock = 7,
    SessionUnlock = 8,
    RemoteControl = 9,
    SessionCreate = 10,
    SessionTerminate = 11,

    /// <summary>A change detected by periodic reconciliation rather than by a notification.</summary>
    Reconciled = 100,
}
