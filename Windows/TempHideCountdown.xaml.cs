using System.Windows;
using AtFutureMe.Models;

namespace AtFutureMe.Windows;

public partial class TempHideCountdown : OverlayWindowBase
{
    public TempHideCountdown()
    {
        ForceClickThrough = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -2000;
        Top = -2000;
        InitializeComponent();
        ResizeMode = ResizeMode.NoResize;
    }

    public void ShowRemaining(TimeSpan remain, OverlayCorner corner, Func<Window, Rect> workOf)
    {
        RemainText.Text = AppHost.CountdownClock(remain);
        ApplyChrome();
        if (!IsVisible)
            Show();
        UpdateLayout();
        Place(corner, workOf(this));
        Topmost = false;
        Topmost = true;
        ApplyClickThrough();
    }

    public void ApplyChrome()
    {
        var host = AppHost.Instance;
        ApplyPanelChrome(PanelBorder, DitherLayer);
        RemainText.FontWeight = host.OverlayChromeWeight();
        RemainText.FontSize = Math.Max(host.Data.Settings.FontSize + 2, 16);
        ApplyClickThrough();
    }

    void Place(OverlayCorner corner, Rect work)
    {
        const double margin = 16;
        var width = ActualWidth > 1 ? ActualWidth : Width;
        var height = ActualHeight > 1 ? ActualHeight : Height;
        var left = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
            ? work.Left + margin
            : work.Right - width - margin;
        var top = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight
            ? work.Top + margin
            : work.Bottom - height - margin;
        Left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        Top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
    }
}
