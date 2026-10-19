using System.Text.Json.Serialization;

namespace SessionObserver.Protocol.Models;

/// <summary>A single recorded session change.</summary>
public sealed record SessionEvent
{
    /// <summary>Monotonically increasing number assigned by the service; survives restarts.</summary>
    public long Sequence { get; init; }

    public DateTimeOffset Timestamp { get; init; }
    public SessionChangeKind Kind { get; init; }
    public int SessionId { get; init; }

    /// <summary>The user the event concerns (for a logoff, the user who logged off).</summary>
    public string? UserName { get; init; }
    public string? Domain { get; init; }
    public string? ClientName { get; init; }
    public string? ClientAddress { get; init; }

    /// <summary>Human-readable detail, used mainly for <see cref="SessionChangeKind.Reconciled"/> events.</summary>
    public string? Detail { get; init; }

    /// <summary>State of the session after the change; null when the session no longer exists.</summary>
    public SessionInfo? Session { get; init; }

    [JsonIgnore]
    public string QualifiedUserName => SessionInfo.FormatUser(Domain, UserName);
}
