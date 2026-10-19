using System.Text.Json.Serialization;
using SessionObserver.Protocol.Models;

namespace SessionObserver.Protocol.Messages;

/// <summary>
/// Base type of every message exchanged over the pipe. The "$type" discriminator selects the concrete type.
/// Requests carry a <see cref="RequestId"/> that the matching response echoes back; pushes have none.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PingRequest), "ping")]
[JsonDerivedType(typeof(PongResponse), "pong")]
[JsonDerivedType(typeof(GetSessionsRequest), "getSessions")]
[JsonDerivedType(typeof(SessionsResult), "sessions")]
[JsonDerivedType(typeof(GetEventsRequest), "getEvents")]
[JsonDerivedType(typeof(EventsResult), "events")]
[JsonDerivedType(typeof(SubscribeRequest), "subscribe")]
[JsonDerivedType(typeof(SubscribedResponse), "subscribed")]
[JsonDerivedType(typeof(EventPush), "event")]
[JsonDerivedType(typeof(ErrorResponse), "error")]
public abstract record ProtocolMessage
{
    public string? RequestId { get; init; }
}

/// <summary>Handshake: checks liveness and protocol compatibility.</summary>
public sealed record PingRequest : ProtocolMessage
{
    public int ClientProtocolVersion { get; init; } = ProtocolVersion.Current;
}

public sealed record PongResponse : ProtocolMessage
{
    public int ServerProtocolVersion { get; init; }
    public string? ServerVersion { get; init; }
    public string? MachineName { get; init; }
    public DateTimeOffset ServerTime { get; init; }
}

/// <summary>Requests the sessions that currently exist on the machine.</summary>
public sealed record GetSessionsRequest : ProtocolMessage;

public sealed record SessionsResult : ProtocolMessage
{
    public IReadOnlyList<SessionInfo> Sessions { get; init; } = [];
}

/// <summary>
/// Requests recorded events in ascending sequence order.
/// With <see cref="SinceSequence"/>: the oldest <see cref="Max"/> events after that sequence (for gap filling).
/// Without it: the newest <see cref="Max"/> events. <c>Max = 0</c> returns only <see cref="EventsResult.LatestSequence"/>.
/// </summary>
public sealed record GetEventsRequest : ProtocolMessage
{
    public long? SinceSequence { get; init; }
    public int Max { get; init; } = 500;
}

public sealed record EventsResult : ProtocolMessage
{
    public IReadOnlyList<SessionEvent> Events { get; init; } = [];

    /// <summary>Sequence of the newest event the service has recorded (0 when none).</summary>
    public long LatestSequence { get; init; }
}

/// <summary>Asks the service to push every new event on this connection as an <see cref="EventPush"/>.</summary>
public sealed record SubscribeRequest : ProtocolMessage;

public sealed record SubscribedResponse : ProtocolMessage
{
    /// <summary>Latest sequence at the moment of subscription; every later event will be pushed.</summary>
    public long LatestSequence { get; init; }
}

/// <summary>Unsolicited message carrying a new event to a subscribed connection.</summary>
public sealed record EventPush : ProtocolMessage
{
    public required SessionEvent Event { get; init; }
}

public sealed record ErrorResponse : ProtocolMessage
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>Well-known values of <see cref="ErrorResponse.Code"/>.</summary>
public static class ErrorCodes
{
    public const string UnsupportedRequest = "unsupported_request";
    public const string InvalidRequest = "invalid_request";
    public const string VersionMismatch = "version_mismatch";
    public const string InternalError = "internal_error";
}
