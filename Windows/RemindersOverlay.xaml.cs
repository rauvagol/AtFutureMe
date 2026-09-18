using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AtFutureMe.ViewModels;

namespace AtFutureMe.Windows;

public partial class RemindersOverlay : OverlayWindowBase
{
    static readonly SolidColorBrush SnoozeLive = FreezeRgb(0xC5, 0xC9, 0xD0);
    static readonly SolidColorBrush SnoozeMuted = FreezeRgb(0x66, 0x6A, 0x70);

    static SolidColorBrush FreezeRgb(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public RemindersOverlay()
    {
        InitializeComponent();
        BoardList.ItemsSource = AppHost.Instance.RootBoard;
        LocationChanged += (_, _) => AppHost.Instance.ScheduleBoundsSave();
        SizeChanged += (_, _) => AppHost.Instance.ScheduleBoundsSave();
        AppHost.Instance.UiChanged += ApplyChrome;
        Loaded += (_, _) => ApplyChrome();
        Closed += (_, _) =>
        {
            StopDueHighlight();
            AppHost.Instance.UiChanged -= ApplyChrome;
        };
    }

    public void ApplyChrome()
    {
        var host = AppHost.Instance;
        ApplyDragChrome(ChromeBar, ChromeActions, ChromeTitle, ChromeRow, BodyScroll);
        BoardList.FontSize = host.Data.Settings.FontSize;
        BoardList.FontWeight = host.OverlayChromeWeight();
        EmptyHint.FontWeight = host.OverlayChromeWeight();
        ChromeTitle.FontWeight = host.OverlayChromeWeight();
        var now = DateTime.Now;
        var state = host.ReadTempHideState(now);
        SnoozeButton.IsEnabled = state is AppHost.TempHideKind.CanHide or AppHost.TempHideKind.HasActive;
        var color = state == AppHost.TempHideKind.CanHide ? SnoozeLive : SnoozeMuted;
        SnoozeButton.Foreground = color;
        SnoozeGlyph.Foreground = color;
        SnoozeBlock.Stroke = color;
        SnoozeBlock.Visibility = state == AppHost.TempHideKind.HasActive
            ? Visibility.Visible
            : Visibility.Collapsed;
        SnoozeButton.ToolTip = host.TempHideButtonTip(now);
        UndoButton.IsEnabled = host.CanUndoDelete;
        UndoButton.ToolTip = host.UndoDeleteTip;
        ApplyPanelChrome(PanelBorder, DitherLayer);
        EmptyHint.Visibility = host.RootBoard.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshUsageDock();
    }

    // auto model
    string? _usageDockSig;

    public void RefreshUsageDock()
    {
        if (UsageDock == null || UsageDockMeters == null)
            return;
        var host = AppHost.Instance;
        var s = host.Data.Settings;
        var show = s.ShowUsageModule && s.UsageDockInReminders;
        if (!show)
        {
            UsageDock.Visibility = Visibility.Collapsed;
            UsageDockMeters.Children.Clear();
            _usageDockSig = null;
            return;
        }

        UsageDock.Visibility = Visibility.Visible;
        var sig = UsagePanelContent.CompactSignature(s, host.Usage?.Latest);
        if (sig == _usageDockSig && UsageDockMeters.Children.Count > 0)
            return;
        _usageDockSig = sig;
        UsagePanelContent.Fill(UsageDockMeters, compact: true);
    }

    void OnWindowDrag(object sender, MouseButtonEventArgs e) => DragIfUnlocked(e);

    void OnMinimize(object sender, RoutedEventArgs e) => AppHost.Instance.HideOverlay(AppHost.MainOverlayId);

    bool _pressHighlight;

    void OnTempHideDown(object sender, MouseButtonEventArgs e)
    {
        if (AppHost.Instance.ReadTempHideState(DateTime.Now) != AppHost.TempHideKind.HasActive)
            return;
        _pressHighlight = true;
        AppHost.Instance.SetDueHighlight(true);
        SnoozeButton.CaptureMouse();
        e.Handled = true;
    }

    void OnTempHideUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressHighlight)
            StopDueHighlight();
    }

    void OnTempHideLostCapture(object sender, MouseEventArgs e)
    {
        if (_pressHighlight)
            StopDueHighlight();
    }

    void StopDueHighlight()
    {
        _pressHighlight = false;
        AppHost.Instance.SetDueHighlight(false);
        if (SnoozeButton.IsMouseCaptured)
            SnoozeButton.ReleaseMouseCapture();
    }

    void OnTempHide(object sender, RoutedEventArgs e) => AppHost.Instance.TempHideMain();

    void OnClose(object sender, RoutedEventArgs e) => AppHost.Instance.Exit();

    void OnAdd(object sender, RoutedEventArgs e) => AppHost.Instance.OpenAddEntry(null);

    void OnAddFolder(object sender, RoutedEventArgs e) => AppHost.Instance.OpenAddFolder(null);

    void OnOpenConfig(object sender, RoutedEventArgs e) => AppHost.Instance.OpenConfig();

    void OnUndoDelete(object sender, RoutedEventArgs e) => AppHost.Instance.UndoLastDelete();

    void OnRootDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(OverlayDrag)) is OverlayDrag { Kind: "category" }
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    void OnRootDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(OverlayDrag)) is OverlayDrag drag)
            AppHost.Instance.DropOnRoot(drag);
        e.Handled = true;
    }
}
