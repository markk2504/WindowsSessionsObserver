using SessionObserver.Protocol.Models;

namespace SessionObserver.Service.Interop;

/// <summary>Reads session information from the operating system. Replaced by a fake in tests.</summary>
public interface ISessionSource
{
    /// <summary>All sessions that currently exist, including listeners and session 0.</summary>
    IReadOnlyList<SessionInfo> EnumerateSessions();

    /// <summary>Current information for one session, or null when it does not exist.</summary>
    SessionInfo? QuerySession(int sessionId);
}
