using log4net;
using SessionObserver.Logging;

namespace SessionObserver.Viewer;

internal static class Program
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(Program));

    [STAThread]
    private static void Main()
    {
        LoggingSetup.Initialize();
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) =>
            {
                Log.Error("Unhandled UI exception.", e.Exception);
                MessageBox.Show(e.Exception.Message, "Windows Sessions Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            ApplicationConfiguration.Initialize();

            // Placeholder until MainForm is implemented.
            using var form = new Form
            {
                Text = "Windows Sessions Viewer",
                StartPosition = FormStartPosition.CenterScreen,
                Size = new Size(1000, 700),
            };
            Application.Run(form);
        }
        finally
        {
            LoggingSetup.Shutdown();
        }
    }
}
