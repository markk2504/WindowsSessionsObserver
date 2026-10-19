namespace SessionObserver.Client;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
}

public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStateChangedEventArgs(ConnectionState state, SessionObserverException? error)
    {
        State = state;
        Error = error;
    }

    public ConnectionState State { get; }

    /// <summary>The most recent connection failure, when there is one.</summary>
    public SessionObserverException? Error { get; }
}
