using System.Windows;
using System.Windows.Threading;

namespace AtFutureMe.Windows;

public partial class DiscordDebugWindow : Window
{
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public DiscordDebugWindow()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => RefreshLog();
        Loaded += (_, _) =>
        {
            RefreshLog();
            _tick.Start();
        };
        Closed += (_, _) => _tick.Stop();
    }

    public void RefreshLog()
    {
        if (!IsLoaded)
            return;
        var report = AppHost.Instance.Discord?.BuildDebugReport() ?? "Discord watcher isn’t started.";
        if (LogBox.Text == report)
            return;
        var pinEnd = LogBox.IsFocused
            && LogBox.CaretIndex >= Math.Max(0, LogBox.Text.Length - 2);
        LogBox.Text = report;
        if (pinEnd)
            LogBox.CaretIndex = report.Length;
    }
}
