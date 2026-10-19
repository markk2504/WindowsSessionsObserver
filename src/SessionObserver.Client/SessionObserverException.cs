namespace SessionObserver.Client;

public enum SessionObserverErrorKind
{
    /// <summary>The pipe does not exist or does not answer: the service is not running.</summary>
    ServiceUnavailable,

    /// <summary>The pipe refused the connection: the caller is not an administrator (or not elevated).</summary>
    AccessDenied,

    /// <summary>Client and service speak different protocol versions.</summary>
    ProtocolMismatch,

    /// <summary>The service answered with an error.</summary>
    ServerError,

    /// <summary>The connection was lost or is not established.</summary>
    ConnectionLost,

    /// <summary>The service did not answer in time.</summary>
    Timeout,

    /// <summary>The service sent something the client did not understand.</summary>
    ProtocolError,
}

public sealed class SessionObserverException : Exception
{
    public SessionObserverException(SessionObserverErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public SessionObserverErrorKind Kind { get; }
}
