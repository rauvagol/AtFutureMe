using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AtFutureMe.Models;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

public partial class MiniClockOverlay : OverlayWindowBase
{
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly DispatcherTimer _hideTick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<(TextBlock Remain, DateTime At)> _times = [];
    string _signature = "";
    DateTime? _hideUntil;
    TextBlock? _hideLine;
    double _hideSecondsArmed = -1;

    public MiniClockOverlay()
    {
        InitializeComponent();
        LocationChanged += (_, _) => AppHost.Instance.ScheduleBoundsSave();
        SizeChanged += (_, _) => AppHost.Instance.ScheduleBoundsSave();
        AppHost.Instance.UiChanged += Refresh;
        if (AppHost.Instance.Discord != null)
            AppHost.Instance.Discord.Changed += Refresh;
        _tick.Tick += (_, _) => RefreshTimes();
        _hideTick.Tick += (_, _) => TickHideCountdown();
        Loaded += (_, _) =>
        {
            ApplyChrome();
            Refresh();
            _tick.Start();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Refresh();
                _tick.Start();
            }
            else
            {
                _tick.Stop();
                StopHideCountdown();
            }
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            StopHideCountdown();
            AppHost.Instance.UiChanged -= Refresh;
            if (AppHost.Instance.Discord != null)
                AppHost.Instance.Discord.Changed -= Refresh;
        };
    }

    public void ApplyChrome()
    {
        var host = AppHost.Instance;
        var mode = host.Data.Settings.ClockLockMode;
        ForceClickThrough = mode == ClockLockMode.Locked;
        ResizeMode = mode == ClockLockMode.Unlocked
            ? ResizeMode.CanResizeWithGrip
            : ResizeMode.NoResize;
        var lookahead = AppHost.MiniClockLookaheadText(host.Data.Settings.MiniClockHours, shortForm: true);

        if (mode == ClockLockMode.Unlocked)
        {
            ApplyDragChrome(ChromeBar, ChromeActions, ChromeTitle, ChromeRow, BodyHost);
            ChromeTitle.Text = $"Next {lookahead}";
            ChromeTitle.FontWeight = host.OverlayChromeWeight();
            ApplyPanelChrome(PanelBorder, DitherLayer);
            ForceClickThrough = false;
            BodyHost.Margin = new Thickness(12, 4, 12, 12);
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
            BodyHost.Margin = new Thickness(12, 10, 12, 12);
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
        var host = AppHost.Instance;
        var now = DateTime.Now;
        var rows = host.MiniClockRows(now);
        var discord = host.MiniClockDiscordText();
        var signature = discord + "\n" + string.Join("\n", rows.Select(row => row.Kind + ":" + row.Id + ":" + row.Title));
        if (signature == _signature)
        {
            if (rows.Count == 0 && discord.Length == 0)
                TickHideCountdown();
            else
                RefreshTimes();
            return;
        }

        _signature = signature;
        _times.Clear();
        BodyHost.Children.Clear();
        _hideLine = null;
        var font = Math.Max(12, host.Data.Settings.FontSize);
        var weight = host.OverlayChromeWeight();
        var text = Freeze(ColorText.NormalizeOr(host.Data.Settings.ClockColorTime, "#E8EAED"));
        var dim = Freeze("#9AA0A8");

        if (discord.Length > 0)
        {
            StopHideCountdown();
            BodyHost.Children.Add(new TextBlock
            {
                Text = discord,
                Foreground = Freeze(ColorText.NormalizeOr(host.Data.Settings.DiscordClockTextColor, "#E8EAED")),
                FontSize = font,
                FontWeight = weight,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        if (rows.Count == 0 && discord.Length == 0)
        {
            var lookahead = AppHost.MiniClockLookaheadText(host.Data.Settings.MiniClockHours, shortForm: false);
            BodyHost.Children.Add(new TextBlock
            {
                Text = $"Nothing in the next {lookahead}",
                Foreground = dim,
                FontSize = font
            });
            ArmHideCountdown(host, font, dim);
            return;
        }

        StopHideCountdown();
        foreach (var row in rows)
        {
            var line = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock
            {
                Text = row.Title,
                Foreground = text,
                FontSize = font,
                FontWeight = FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var remain = new TextBlock
            {
                Text = RemainText(row.At, now),
                Foreground = dim,
                FontSize = font,
                FontWeight = weight
            };
            Grid.SetColumn(remain, 1);
            line.Children.Add(title);
            line.Children.Add(remain);
            BodyHost.Children.Add(line);
            _times.Add((remain, row.At));
        }
    }

    void ArmHideCountdown(AppHost host, double font, Brush dim)
    {
        var seconds = Math.Clamp(host.Data.Settings.MiniClockHideSeconds, 0, 600);
        if (seconds <= 0)
        {
            StopHideCountdown();
            return;
        }

        if (_hideUntil == null || Math.Abs(_hideSecondsArmed - seconds) > 0.01)
        {
            _hideUntil = DateTime.Now.AddSeconds(seconds);
            _hideSecondsArmed = seconds;
        }

        _hideLine = new TextBlock
        {
            Text = HideCountdownText(),
            Foreground = dim,
            FontSize = Math.Max(11, font - 1),
            Margin = new Thickness(0, 6, 0, 0)
        };
        BodyHost.Children.Add(_hideLine);
        if (!_hideTick.IsEnabled)
            _hideTick.Start();
        TickHideCountdown();
    }

    void TickHideCountdown()
    {
        if (_hideUntil == null)
        {
            StopHideCountdown();
            return;
        }

        var left = (_hideUntil.Value - DateTime.Now).TotalSeconds;
        if (left <= 0)
        {
            StopHideCountdown();
            AppHost.Instance.HideMiniUntilAttention();
            return;
        }

        if (_hideLine != null)
            _hideLine.Text = HideCountdownText();
    }

    string HideCountdownText()
    {
        if (_hideUntil == null)
            return "";
        var left = Math.Max(1, (int)Math.Ceiling((_hideUntil.Value - DateTime.Now).TotalSeconds));
        return left == 1 ? "Hiding in 1 second" : $"Hiding in {left} seconds";
    }

    void StopHideCountdown()
    {
        _hideTick.Stop();
        _hideUntil = null;
        _hideSecondsArmed = -1;
        _hideLine = null;
    }

    void RefreshTimes()
    {
        if (!IsLoaded)
            return;
        var now = DateTime.Now;
        foreach (var (remain, at) in _times)
            remain.Text = RemainText(at, now);
    }

    static string RemainText(DateTime at, DateTime now) =>
        at <= now ? "now" : RecurrenceEngine.FormatRemainingLong(at, now);

    static SolidColorBrush Freeze(string hex)
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
            BodyHost.Cursor = Cursors.Arrow;
    }

    void OnBodyDrag(object sender, MouseButtonEventArgs e)
    {
        var mode = AppHost.Instance.Data.Settings.ClockLockMode;
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
        if (AppHost.Instance.Data.Settings.ClockLockMode == ClockLockMode.Unlocked)
            DragIfUnlocked(e);
    }

    void OnHide(object sender, RoutedEventArgs e) => AppHost.Instance.HideClockModule();
}
