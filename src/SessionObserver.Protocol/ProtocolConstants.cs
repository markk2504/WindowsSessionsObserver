namespace SessionObserver.Protocol;

public static class PipeNames
{
    /// <summary>Name of the pipe the service listens on (\\.\pipe\WindowsSessionsObserver).</summary>
    public const string Default = "WindowsSessionsObserver";
}

public static class ProtocolVersion
{
    /// <summary>Bumped on any incompatible change to the messages or framing.</summary>
    public const int Current = 1;
}

/// <summary>Raised for malformed, oversized or truncated messages.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}
