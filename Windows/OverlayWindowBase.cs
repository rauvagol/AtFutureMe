using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

public class OverlayWindowBase : Window
{
    const double HitPad = 3;
    // auto model
    const int WmMouseActivate = 0x0021;
    const int MaNoActivate = 3;

    public bool ForceClickThrough { get; protected set; }

    // auto model: true while a text field / menu on this overlay needs keyboard focus
    bool _allowActivate;
    bool _leaveFocusQueued;

    // Click-through follows the cursor, but a per-frame render callback keeps every
    // layered window repainting at the monitor refresh rate. Poll only when the
    // cursor actually moves.
    static readonly List<OverlayWindowBase> HitWindows = [];
    static readonly DispatcherTimer HitTimer = new() { Interval = TimeSpan.FromMilliseconds(32) };
    static bool HitTimerHooked;
    static int LastCursorX = int.MinValue;
    static int LastCursorY = int.MinValue;

    public OverlayWindowBase()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => RegisterHitWindow();
        Closed += (_, _) => UnregisterHitWindow();
        // auto model: take focus only for controls that need typing / menu navigation
        AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDownForFocus), true);
        AddHandler(PreviewGotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnPreviewGotKeyboardFocus), true);
        AddHandler(PreviewLostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnPreviewLostKeyboardFocus), true);
        AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler(OnContextMenuOpening), true);
        AddHandler(ContextMenuClosingEvent, new ContextMenuEventHandler(OnContextMenuClosing), true);
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        NativeMethods.ApplyOverlayStyles(this);
        ApplyClickThrough();
        // auto model
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    // auto model: refuse activation on click unless a text/menu focus session is open
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate && !_allowActivate)
        {
            handled = true;
            return (IntPtr)MaNoActivate;
        }

        return IntPtr.Zero;
    }

    void RegisterHitWindow()
    {
        if (!HitWindows.Contains(this))
            HitWindows.Add(this);
        if (!HitTimerHooked)
        {
            HitTimer.Tick += (_, _) => PollHitWindows();
            HitTimerHooked = true;
        }
        if (!HitTimer.IsEnabled)
            HitTimer.Start();
        ApplyClickThrough();
    }

    void UnregisterHitWindow()
    {
        HitWindows.Remove(this);
        if (HitWindows.Count == 0)
            HitTimer.Stop();
    }

    static void PollHitWindows()
    {
        if (HitWindows.Count == 0)
        {
            HitTimer.Stop();
            return;
        }

        if (!NativeMethods.GetCursorPos(out var pt))
            return;
        if (pt.X == LastCursorX && pt.Y == LastCursorY)
            return;
        LastCursorX = pt.X;
        LastCursorY = pt.Y;

        foreach (var window in HitWindows.ToArray())
        {
            if (window.IsVisible)
                window.ApplyClickThrough();
        }
    }

    public void ApplyClickThrough()
    {
        if (ForceClickThrough)
        {
            NativeMethods.SetClickThrough(this, true, allowActivate: false);
            return;
        }

        var host = AppHost.Instance;
        var pass = !host.OverlayDragActive
            && IsVisible
            && !HitInteractive();
        // auto model
        NativeMethods.SetClickThrough(this, pass, _allowActivate);
    }

    // auto model
    void OnPreviewMouseDownForFocus(object sender, MouseButtonEventArgs e)
    {
        if (ForceClickThrough || AppHost.Instance.Data.Settings.ClickThrough)
            return;
        if (NeedsKeyboardFocus(e.OriginalSource as DependencyObject))
            EnterFocusMode();
    }

    // auto model
    void OnPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (NeedsKeyboardFocus(e.NewFocus as DependencyObject))
            EnterFocusMode();
    }

    // auto model
    void OnPreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!NeedsKeyboardFocus(e.OldFocus as DependencyObject))
            return;
        if (NeedsKeyboardFocus(e.NewFocus as DependencyObject))
            return;
        ScheduleLeaveFocusMode();
    }

    // auto model
    void OnContextMenuOpening(object sender, ContextMenuEventArgs e) => EnterFocusMode();

    // auto model
    void OnContextMenuClosing(object sender, ContextMenuEventArgs e) => ScheduleLeaveFocusMode();

    // auto model
    void EnterFocusMode()
    {
        _leaveFocusQueued = false;
        if (_allowActivate)
        {
            if (!IsActive)
                Activate();
            return;
        }

        _allowActivate = true;
        ApplyClickThrough();
        Activate();
    }

    // auto model
    void ScheduleLeaveFocusMode()
    {
        if (!_allowActivate || _leaveFocusQueued)
            return;
        _leaveFocusQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _leaveFocusQueued = false;
            if (!_allowActivate)
                return;
            if (NeedsKeyboardFocus(Keyboard.FocusedElement as DependencyObject))
                return;
            if (HasOpenContextMenu(this))
                return;
            _allowActivate = false;
            ApplyClickThrough();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // auto model
    static bool NeedsKeyboardFocus(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is TextBox or RichTextBox or PasswordBox or ComboBox or DatePicker
                or TextBoxBase
                or Menu or MenuItem or System.Windows.Controls.ContextMenu)
                return true;
            if (current is Window)
                break;
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    // auto model
    static bool HasOpenContextMenu(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is not DependencyObject child)
                continue;
            if (child is System.Windows.Controls.ContextMenu { IsOpen: true })
                return true;
            if (HasOpenContextMenu(child))
                return true;
        }

        return false;
    }

    protected void ApplyPanelChrome(Border panel, Border? dither = null)
    {
        var host = AppHost.Instance;
        panel.Background = host.PanelBrush();
        panel.BorderBrush = host.PanelOutlineBrush();
        panel.BorderThickness = host.PanelBorderThickness();
        if (dither == null)
            return;
        dither.Background = host.DitherBrush();
        dither.Visibility = host.Data.Settings.PanelDither != false
            ? Visibility.Visible
            : Visibility.Collapsed;
        dither.CornerRadius = panel.CornerRadius;
    }

    protected void ApplyDragChrome(Border chromeBar, FrameworkElement actions,
        FrameworkElement title, RowDefinition chromeRow, FrameworkElement body)
    {
        var host = AppHost.Instance;
        var interactive = !host.Data.Settings.ClickThrough;
        var radius = PanelBorderRadius(chromeBar);
        chromeBar.VerticalAlignment = VerticalAlignment.Top;
        chromeBar.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(chromeBar, 0);
        Grid.SetRowSpan(chromeBar, 1);
        if (interactive)
        {
            chromeRow.Height = GridLength.Auto;
            chromeBar.Visibility = Visibility.Visible;
            chromeBar.Background = host.OpaquePanelBrush();
            chromeBar.Padding = new Thickness(10, 8, 10, 6);
            chromeBar.CornerRadius = new CornerRadius(radius, radius, 0, 0);
            body.Margin = new Thickness(10, 0, 10, 8);
        }
        else
        {
            chromeRow.Height = new GridLength(0);
            chromeBar.Visibility = Visibility.Collapsed;
            body.Margin = new Thickness(10, 8, 10, 8);
        }

        actions.Visibility = Visibility.Visible;
        title.Visibility = Visibility.Visible;
        if (title is TextBlock caption)
        {
            caption.TextTrimming = TextTrimming.CharacterEllipsis;
            caption.MaxWidth = double.PositiveInfinity;
        }
        chromeBar.MouseLeftButtonDown -= DragChromeBar;
        chromeBar.MouseLeftButtonDown += DragChromeBar;
        ApplyClickThrough();
    }

    void DragChromeBar(object sender, MouseButtonEventArgs e)
    {
        if (AppHost.Instance.Data.Settings.ClickThrough)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        if (IsInsideButton(e.OriginalSource as DependencyObject, sender as DependencyObject))
            return;

        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // Ignore if mouse is not down anymore.
        }
    }

    static bool IsInsideButton(DependencyObject? source, DependencyObject? stop)
    {
        var current = source;
        while (current != null && current != stop)
        {
            if (current is Button)
                return true;
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    static double PanelBorderRadius(FrameworkElement chromeBar)
    {
        var current = VisualTreeHelper.GetParent(chromeBar) as DependencyObject;
        while (current != null)
        {
            if (current is Border panel)
                return panel.CornerRadius.TopLeft;
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return 10;
    }

    public bool HitInteractive()
    {
        try
        {
            if (!NativeMethods.TryGetCursorInWindow(this, out var local))
                return false;
            if (local.X < -HitPad || local.Y < -HitPad
                || local.X > ActualWidth + HitPad || local.Y > ActualHeight + HitPad)
                return false;
            return HitsInteractive(this, local);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    bool HitsInteractive(DependencyObject root, Point windowPoint)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is not Visual child)
                continue;
            if (child is FrameworkElement fe && fe.IsVisible && IsInteractiveControl(fe)
                && ControlContains(fe, windowPoint))
                return true;
            if (HitsInteractive(child, windowPoint))
                return true;
        }

        return false;
    }

    bool ControlContains(FrameworkElement element, Point windowPoint)
    {
        try
        {
            if (element.ActualWidth < 1 || element.ActualHeight < 1)
                return false;
            var origin = element.TransformToAncestor(this).Transform(new Point(0, 0));
            var rect = new Rect(origin.X - HitPad, origin.Y - HitPad,
                element.ActualWidth + HitPad * 2, element.ActualHeight + HitPad * 2);
            return rect.Contains(windowPoint);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    bool IsInteractiveControl(FrameworkElement element)
    {
        if (!element.IsEnabled || element.Visibility != Visibility.Visible)
            return false;
        if (element is CheckBox)
            return true;
        if (Equals(element.Tag, "hit"))
            return true;
        if (AppHost.Instance.Data.Settings.ClickThrough)
            return false;
        if (element is OverlayItemRow or ResizeGrip or Button)
            return true;
        return Equals(element.Tag, "chrome") || Equals(element.Tag, "drop");
    }

    protected void DragIfUnlocked(MouseButtonEventArgs e)
    {
        if (AppHost.Instance.Data.Settings.ClickThrough)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        if (e.OriginalSource is DependencyObject source && IsChromeControl(source))
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Ignore if mouse is not down anymore.
        }
    }

    static bool IsChromeControl(DependencyObject source)
    {
        var current = source;
        while (current != null)
        {
            if (current is Button or CheckBox or ScrollViewer or TextBox or ComboBox or OverlayItemRow)
                return true;
            if (current is FrameworkElement element && Equals(element.Tag, "hit"))
                return true;
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
