using log4net;
using SessionObserver.Logging;
using SessionObserver.Service;
using SessionObserver.Service.Interop;
using SessionObserver.Service.Storage;

LoggingSetup.Initialize();
ILog log = LogManager.GetLogger(typeof(Program));

try
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(options => options.ServiceName = "WindowsSessionsObserver");

    // Only start/stop failures go to the Windows Event Log; everything else is logged through log4net.
    builder.Logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>(level => level >= LogLevel.Warning);

    builder.Services.Configure<ObserverOptions>(builder.Configuration.GetSection(ObserverOptions.SectionName));
    builder.Services.Configure<EventStoreOptions>(builder.Configuration.GetSection(EventStoreOptions.SectionName));
    builder.Services.Configure<PipeServerOptions>(builder.Configuration.GetSection(PipeServerOptions.SectionName));

    builder.Services.AddSingleton<ISessionSource, WtsSessionSource>();
    builder.Services.AddSingleton<EventStore>();

    using IHost host = builder.Build();
    log.Info("Service host starting.");
    await host.RunAsync();
    log.Info("Service host stopped.");
    return 0;
}
catch (Exception ex)
{
    log.Fatal("Service host terminated unexpectedly.", ex);
    return 1;
}
finally
{
    LoggingSetup.Shutdown();
}
