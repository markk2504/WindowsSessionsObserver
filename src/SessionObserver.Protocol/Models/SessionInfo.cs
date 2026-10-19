using System.Text.Json.Serialization;

namespace SessionObserver.Protocol.Models;

/// <summary>Snapshot of a Windows session as reported by the WTS API.</summary>
public sealed record SessionInfo
{
    public int SessionId { get; init; }
    public string? UserName { get; init; }
    public string? Domain { get; init; }
    public SessionState State { get; init; }
    public string? WinStationName { get; init; }
    public string? ClientName { get; init; }
    public string? ClientAddress { get; init; }
    public bool IsRemote { get; init; }

    /// <summary>True when locked, false when unlocked, null when Windows does not report it.</summary>
    public bool? IsLocked { get; init; }

    public DateTimeOffset? LogonTime { get; init; }
    public DateTimeOffset? ConnectTime { get; init; }
    public DateTimeOffset? DisconnectTime { get; init; }
    public DateTimeOffset? LastInputTime { get; init; }

    /// <summary>DOMAIN\user, user, or an empty string when nobody is logged on.</summary>
    [JsonIgnore]
    public string QualifiedUserName => FormatUser(Domain, UserName);

    public static string FormatUser(string? domain, string? userName) =>
        string.IsNullOrEmpty(userName) ? string.Empty
        : string.IsNullOrEmpty(domain) ? userName
        : $@"{domain}\{userName}";
}
