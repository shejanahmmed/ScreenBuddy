using System.Threading;
using System.Windows.Forms;

namespace ScreenBuddyTray;

static class Program
{
    private const string MutexName = "ScreenBuddy_TrayApp_SingleInstance_v1";

    [STAThread]
    static void Main()
    {
        // Log all unhandled exceptions (UI thread and worker threads)
        Application.ThreadException += (sender, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (sender, e) => LogCrash(e.ExceptionObject as System.Exception);

        try
        {
            using var mutex = new Mutex(true, MutexName, out bool isNewInstance);

            if (!isNewInstance)
            {
                MessageBox.Show(
                    "ScreenBuddy is already running.",
                    "ScreenBuddy Already Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using var streamManager = new StreamManager();
            Application.Run(new MainForm(streamManager));
        }
        catch (System.Exception ex)
        {
            LogCrash(ex);
            throw;
        }
    }

    private static void LogCrash(System.Exception? ex)
    {
        if (ex == null) return;
        try
        {
            string path = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt");
            System.IO.File.WriteAllText(path, ex.ToString());
        }
        catch { }
    }
}
