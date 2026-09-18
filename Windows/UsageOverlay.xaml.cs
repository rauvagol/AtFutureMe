using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AtFutureMe.Models;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

// auto model
public partial class UsageOverlay : OverlayWindowBase
{
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };

    public UsageOverlay()
    {
        InitializeComponent();
        LocationChanged += (_, _) => AppHost.Instance.ScheduleBoundsSave();
        SizeChanged += (_, _) => AppHost.Instance.ScheduleBoundsSave();
        AppHost.Instance.UiChanged += Refresh;
        if (AppHost.Instance.Usage != null)
            AppHost.Instance.Usage.Changed += Refresh;
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
            if (AppHost.Instance.Usage != null)
                AppHost.Instance.Usage.Changed -= Refresh;
        };
    }

    public void ApplyChrome()
    {
        var host = AppHost.Instance;
        var mode = host.Data.Settings.UsageLockMode;
        ForceClickThrough = mode == UsageLockMode.Locked;
        ResizeMode = mode == UsageLockMode.Unlocked
            ? ResizeMode.CanResizeWithGrip
            : ResizeMode.NoResize;

        if (mode == UsageLockMode.Unlocked)
        {
            ApplyDragChrome(ChromeBar, ChromeActions, ChromeTitle, ChromeRow, BodyHost);
            ChromeTitle.Text = "AI usage";
            ChromeTitle.FontWeight = host.OverlayChromeWeight();
            ApplyPanelChrome(PanelBorder, DitherLayer);
            ForceClickThrough = false;
            BodyHost.Margin = new Thickness(10, 6, 10, 10);
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
            if (mode == UsageLockMode.SemiLocked)
            {
                BodyHost.Cursor = Cursors.SizeAll;
                BodyHost.PreviewMouseLeftButtonDown -= OnBodyDrag;
                BodyHost.PreviewMouseLeftButtonDown += OnBodyDrag;
            }
            else
            {
                BodyHost.Cursor = Cursors.Arrow;
                BodyHost.PreviewMouseLeftButtonDown -= OnBodyDrag;
            }
        }

        ApplyClickThrough();
    }

    public void Refresh()
    {
        if (!IsLoaded)
            return;
        UsagePanelContent.Fill(Meters);
        ApplyClickThrough();
    }

    void OnBodyDrag(object sender, MouseButtonEventArgs e)
    {
        if (AppHost.Instance.Data.Settings.UsageLockMode != UsageLockMode.SemiLocked
            || e.LeftButton != MouseButtonState.Pressed)
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
        if (AppHost.Instance.Data.Settings.UsageLockMode == UsageLockMode.Unlocked)
            DragIfUnlocked(e);
    }

    void OnHide(object sender, RoutedEventArgs e) => AppHost.Instance.HideUsageModule();

    void OnRefresh(object sender, RoutedEventArgs e)
    {
        AppHost.Instance.Usage?.RequestRefreshSoon();
        UsagePanelContent.Fill(Meters);
    }
}
