using log4net;
using SessionObserver.Logging;

namespace SessionObserver.Test;

internal static class Program
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(Program));

    private static int Main(string[] args)
    {
        LoggingSetup.Initialize();
        try
        {
            Log.Info($"Started with arguments: {string.Join(' ', args)}");
            PrintUsage();
            return args.Length == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error("Unexpected error.", ex);
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            LoggingSetup.Shutdown();
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  sessionobservertest list                  Show the current sessions.");
        Console.WriteLine("  sessionobservertest history [--last N]    Show recent session events.");
        Console.WriteLine("  sessionobservertest watch [--json]        Stream live session events until Ctrl+C.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --pipe <name>                             Connect to a pipe other than the default.");
        Console.WriteLine();
        Console.WriteLine("The commands are not implemented yet.");
    }
}
