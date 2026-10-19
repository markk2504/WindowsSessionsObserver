using SessionObserver.Protocol;

namespace SessionObserver.Service;

/// <summary>"Observer" section of appsettings.json.</summary>
public sealed class ObserverOptions
{
    public const string SectionName = "Observer";

    /// <summary>How often all sessions are re-enumerated to catch changes no notification reported.</summary>
    public int ReconcileIntervalSeconds { get; set; } = 30;

    /// <summary>Track session 0 (the non-interactive services session).</summary>
    public bool IncludeServicesSession { get; set; }
}

/// <summary>"EventStore" section of appsettings.json.</summary>
public sealed class EventStoreOptions
{
    public const string SectionName = "EventStore";

    /// <summary>Directory of the events-yyyyMMdd.jsonl files; empty means %ProgramData%\WindowsSessionsObserver.</summary>
    public string? Directory { get; set; }

    /// <summary>Number of recent events kept in memory and served to clients.</summary>
    public int RingSize { get; set; } = 5000;

    /// <summary>Event files older than this many days are deleted.</summary>
    public int RetentionDays { get; set; } = 30;

    public string ResolveDirectory() =>
        string.IsNullOrWhiteSpace(Directory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WindowsSessionsObserver")
            : Environment.ExpandEnvironmentVariables(Directory);
}

/// <summary>"Pipe" section of appsettings.json.</summary>
public sealed class PipeServerOptions
{
    public const string SectionName = "Pipe";

    public string PipeName { get; set; } = PipeNames.Default;

    /// <summary>Number of pipe instances waiting for connections at any time.</summary>
    public int ListenerCount { get; set; } = 4;

    /// <summary>Pushed events that may queue up for one subscriber before it is disconnected as too slow.</summary>
    public int MaxSubscriberBacklog { get; set; } = 1000;

    /// <summary>
    /// Accounts or groups allowed to connect in addition to SYSTEM and BUILTIN\Administrators,
    /// as names (e.g. "CONTOSO\Helpdesk") or SIDs (e.g. "S-1-5-32-555").
    /// </summary>
    public List<string> AllowedAccounts { get; set; } = [];
}
