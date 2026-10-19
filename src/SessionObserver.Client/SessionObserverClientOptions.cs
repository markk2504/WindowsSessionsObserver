using SessionObserver.Protocol;

namespace SessionObserver.Client;

public sealed class SessionObserverClientOptions
{
    /// <summary>Machine running the service; "." for the local machine.</summary>
    public string ServerName { get; set; } = ".";

    public string PipeName { get; set; } = PipeNames.Default;

    /// <summary>How long a single connection attempt waits for the pipe.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a request waits for its response.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Reconnect automatically (with exponential backoff) after a connection is lost.</summary>
    public bool AutoReconnect { get; set; } = true;

    public TimeSpan ReconnectMinDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
}
