using System.Reflection;
using log4net;
using log4net.Config;
using log4net.Repository;

namespace SessionObserver.Logging;

/// <summary>
/// Configures log4net for an executable so it writes &lt;exename&gt;.log (all severities) and
/// &lt;exename&gt;error.log (warnings and errors) to the shared log directory.
/// Call <see cref="Initialize"/> first thing in Main and <see cref="Shutdown"/> on exit.
/// </summary>
public static class LoggingSetup
{
    public const string ConfigFileName = "log4net.config";
    public const string LogDirProperty = "LogDir";
    public const string ExeNameProperty = "ExeName";

    private const string EmbeddedConfigName = "SessionObserver.Logging.log4net.config";

    private static readonly object Sync = new();
    private static bool _exceptionHandlersHooked;

    /// <summary>%ProgramData%\WindowsSessionsObserver\Logs</summary>
    public static string DefaultLogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WindowsSessionsObserver",
        "Logs");

    /// <summary>Directory the log files are written to, once initialized.</summary>
    public static string? LogDirectory { get; private set; }

    /// <summary>Base name of the log files, once initialized.</summary>
    public static string? ExeName { get; private set; }

    /// <summary>Full path of the configuration file in use, or null when the embedded default is used.</summary>
    public static string? ConfigFilePath { get; private set; }

    private static ILoggerRepository Repository => LogManager.GetRepository(typeof(LoggingSetup).Assembly);

    /// <param name="exeName">Base name of the log files; defaults to the running executable's name.</param>
    /// <param name="logDirectory">Directory for the log files; defaults to <see cref="DefaultLogDirectory"/>.</param>
    public static void Initialize(string? exeName = null, string? logDirectory = null)
    {
        lock (Sync)
        {
            exeName = string.IsNullOrWhiteSpace(exeName) ? GetDefaultExeName() : exeName;
            logDirectory = string.IsNullOrWhiteSpace(logDirectory) ? DefaultLogDirectory : logDirectory;

            try
            {
                Directory.CreateDirectory(logDirectory);
            }
            catch (Exception ex)
            {
                // log4net reports its own errors on the console/debug output; make the cause visible there too.
                System.Diagnostics.Trace.TraceError($"Cannot create log directory '{logDirectory}': {ex.Message}");
            }

            LogDirectory = logDirectory;
            ExeName = exeName;
            GlobalContext.Properties[LogDirProperty] = logDirectory;
            GlobalContext.Properties[ExeNameProperty] = exeName;

            ILoggerRepository repository = Repository;
            repository.ResetConfiguration();

            string externalConfig = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
            if (File.Exists(externalConfig))
            {
                ConfigFilePath = externalConfig;
                XmlConfigurator.ConfigureAndWatch(repository, new FileInfo(externalConfig));
            }
            else
            {
                ConfigFilePath = null;
                using Stream stream = typeof(LoggingSetup).Assembly.GetManifestResourceStream(EmbeddedConfigName)
                    ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedConfigName}' is missing.");
                XmlConfigurator.Configure(repository, stream);
            }

            HookUnhandledExceptions();

            LogManager.GetLogger(typeof(LoggingSetup)).Info(
                $"Logging initialized for '{exeName}' (version {GetEntryVersion()}, PID {Environment.ProcessId}, " +
                $"user {Environment.UserDomainName}\\{Environment.UserName}); directory '{logDirectory}', " +
                $"configuration '{ConfigFilePath ?? "embedded default"}'.");
        }
    }

    /// <summary>Flushes and closes all appenders.</summary>
    public static void Shutdown()
    {
        lock (Sync)
        {
            if (ExeName is not null)
            {
                LogManager.GetLogger(typeof(LoggingSetup)).Info($"'{ExeName}' shutting down.");
            }
            LogManager.Shutdown();
        }
    }

    private static void HookUnhandledExceptions()
    {
        if (_exceptionHandlersHooked)
        {
            return;
        }
        _exceptionHandlersHooked = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            ILog log = LogManager.GetLogger(typeof(LoggingSetup));
            log.Fatal($"Unhandled exception (terminating: {e.IsTerminating}).", e.ExceptionObject as Exception);
            if (e.IsTerminating)
            {
                LogManager.Flush(5000);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogManager.GetLogger(typeof(LoggingSetup)).Error("Unobserved task exception.", e.Exception);
        };
    }

    private static string GetDefaultExeName()
    {
        string? path = Environment.ProcessPath;
        string? name = string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(name) ? "SessionObserver" : name;
    }

    private static string GetEntryVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
