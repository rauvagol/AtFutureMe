using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AtFutureMe;

public partial class App : System.Windows.Application
{
    Mutex? _mutex;
    AppHost? _host;

    static string LogPath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        "atfutureme.log");

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failure.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log("Dispatcher: " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("Unhandled: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("Task: " + args.Exception);
            args.SetObserved();
        };

        try
        {
            Log("Starting");
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            System.Windows.Forms.Application.EnableVisualStyles();

            base.OnStartup(e);

            _mutex = new Mutex(true, @"Local\AtFutureMe.SingleInstance", out var created);
            if (!created)
            {
                _mutex.Dispose();
                _mutex = null;
                MessageBox.Show("AtFutureMe is already running. Use the notification area icon.",
                    "AtFutureMe", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            _host = new AppHost();
            _host.Start();
            Log("Started");
        }
        catch (Exception ex)
        {
            Log("Startup failed: " + ex);
            MessageBox.Show(ex.ToString(), "AtFutureMe failed to start");
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.DiscardHideAndSave();
        _host?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    void IgnoreBringIntoView(object sender, RequestBringIntoViewEventArgs e) =>
        e.Handled = true;

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _host?.DiscardHideAndSave();
        base.OnSessionEnding(e);
    }
}
