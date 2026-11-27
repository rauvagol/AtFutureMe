using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AtFutureMe.Models;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

public partial class DiscordOverlay : OverlayWindowBase
{
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };

    public DiscordOverlay()
    {
        InitializeComponent();
        LocationChanged += (_, _) => AppHost.Instance.ScheduleBoundsSave();
        SizeChanged += (_, _) => AppHost.Instance.ScheduleBoundsSave();
        AppHost.Instance.UiChanged += Refresh;
        if (AppHost.Instance.Discord != null)
            AppHost.Instance.Discord.Changed += Refresh;
        _tick.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            ApplyChrome();
            Refresh();
            _tick.Start();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                _tick.Start();
            else
                _tick.Stop();
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            AppHost.Instance.UiChanged -= Refresh;
            if (AppHost.Instance.Discord != null)
                AppHost.Instance.Discord.Changed -= Refresh;
        };
    }

    public void ApplyChrome()
    {
        var host = AppHost.Instance;
        var mode = host.Data.Settings.DiscordLockMode;
        ForceClickThrough = mode == ClockLockMode.Locked;
        ResizeMode = mode == ClockLockMode.Unlocked
            ? ResizeMode.CanResizeWithGrip
            : ResizeMode.NoResize;

        if (mode == ClockLockMode.Unlocked)
        {
            ApplyDragChrome(ChromeBar, ChromeActions, ChromeTitle, ChromeRow, BodyHost);
            ChromeTitle.Text = "Discord";
            ChromeTitle.FontWeight = host.OverlayChromeWeight();
            ApplyPanelChrome(PanelBorder, DitherLayer);
            ForceClickThrough = false;
            BodyHost.Margin = new Thickness(10, 6, 10, 10);
            PanelBorder.Tag = "hit";
            EnableBodyDrag(true);
        }
        else
        {
            ChromeRow.Height = new GridLength(0);
            ChromeBar.Visibility = Visibility.Collapsed;
            ChromeBar.Height = 0;
            ChromeBar.Padding = new Thickness(0);
            PanelBorder.Background = host.PanelBrush();
            PanelBorder.BorderBrush = Brushes.Transparent;
            PanelBorder.BorderThickness = new Thickness(0);
            PanelBorder.Padding = new Thickness(0);
            if (DitherLayer != null)
            {
                DitherLayer.Visibility = Visibility.Collapsed;
                DitherLayer.Background = Brushes.Transparent;
            }
            BodyHost.Margin = new Thickness(10, 10, 10, 10);
            ResizeMode = ResizeMode.NoResize;
            if (mode == ClockLockMode.SemiLocked)
            {
                PanelBorder.Tag = "hit";
                EnableBodyDrag(true);
            }
            else
            {
                PanelBorder.Tag = null;
                EnableBodyDrag(false);
            }
        }

        ApplyClickThrough();
    }

    public void Refresh()
    {
        if (!IsLoaded)
            return;
        Alerts.Children.Clear();
        var discord = AppHost.Instance.Discord;
        var items = discord?.Alerts ?? [];
        if (discord?.PluginMissing == true)
            Alerts.Children.Add(WarningBanner(discord.Status));
        else if (discord?.HasUnread == true)
            Alerts.Children.Add(UnreadBanner(discord.Status));
        else
            Alerts.Children.Add(Hint(discord?.Status ?? "Watching Discord."));
        foreach (var alert in items)
            Alerts.Children.Add(AlertBlock(alert));
        ApplyClickThrough();
    }

    static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("#9AA0A8"),
        FontSize = Math.Max(11, AppHost.Instance.Data.Settings.FontSize - 2),
        Margin = new Thickness(0, 0, 0, 10)
    };

    static TextBlock UnreadBanner(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("#FF8A80"),
        FontWeight = FontWeights.SemiBold,
        FontSize = Math.Max(13, AppHost.Instance.Data.Settings.FontSize),
        Margin = new Thickness(0, 0, 0, 10)
    };

    static TextBlock WarningBanner(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("#FFB74D"),
        FontWeight = FontWeights.SemiBold,
        FontSize = Math.Max(12, AppHost.Instance.Data.Settings.FontSize - 1),
        Margin = new Thickness(0, 0, 0, 10)
    };

    static FrameworkElement AlertBlock(DiscordAlert alert)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(new TextBlock
        {
            Text = alert.Title,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(alert.Color ?? "#E8EAED"),
            FontWeight = FontWeights.SemiBold
        });
        if (alert.Body.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = alert.Body,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("#C5C9D0"),
                Margin = new Thickness(0, 2, 0, 0)
            });
        }
        var meta = alert.Context.Length > 0
            ? $"{alert.Context} · {WhenText(alert.When)}"
            : WhenText(alert.When);
        stack.Children.Add(new TextBlock
        {
            Text = meta,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#7A8088"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        });
        return stack;
    }

    static string WhenText(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1)
            return "just now";
        if (span.TotalHours < 1)
            return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24)
            return when.ToString("h:mm tt");
        return when.ToString("MMM d h:mm tt");
    }

    static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    void EnableBodyDrag(bool on)
    {
        BodyHost.PreviewMouseLeftButtonDown -= OnBodyDrag;
        if (on)
        {
            BodyHost.Cursor = Cursors.SizeAll;
            BodyHost.PreviewMouseLeftButtonDown += OnBodyDrag;
        }
        else
        {
            BodyHost.Cursor = Cursors.Arrow;
        }
    }

    void OnBodyDrag(object sender, MouseButtonEventArgs e)
    {
        var mode = AppHost.Instance.Data.Settings.DiscordLockMode;
        if (mode == ClockLockMode.Locked || e.LeftButton != MouseButtonState.Pressed)
            return;
        var host = AppHost.Instance;
        host.BeginOverlayDrag();
        ApplyClickThrough();
        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException) { }
        finally
        {
            host.EndOverlayDrag();
            ApplyClickThrough();
        }
    }

    void OnWindowDrag(object sender, MouseButtonEventArgs e)
    {
        if (AppHost.Instance.Data.Settings.DiscordLockMode == ClockLockMode.Unlocked)
            DragIfUnlocked(e);
    }

    void OnHide(object sender, RoutedEventArgs e) => AppHost.Instance.HideDiscordModule();
}
