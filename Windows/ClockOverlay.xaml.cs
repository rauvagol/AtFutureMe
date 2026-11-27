using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AtFutureMe.Models;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

// auto model: Now-at-top clock with type wedges, inset Now mark, and hover tips through click-through
public partial class ClockOverlay : OverlayWindowBase
{
    static readonly SolidColorBrush TipBg = Freeze(Color.FromArgb(0xF2, 0x20, 0x22, 0x28));
    static readonly SolidColorBrush TipBorder = Freeze(Color.FromRgb(0x3A, 0x3D, 0x45));
    static readonly SolidColorBrush TipTextBrush = Freeze(Color.FromRgb(0xE8, 0xEA, 0xED));

    const double RingStroke = 2.0;

    // Distinct defaults (also used when settings are empty).
    const string FallbackRecurring = "#8AB4F8";
    const string FallbackScheduled = "#FFD24D";
    const string FallbackDue = "#5DDC8A";
    const string FallbackCaret = "#8AB4F8";
    const string FallbackRing = "#C5C9D0";
    const string FallbackMajorTick = "#C5C9D0";
    const string FallbackMinorTick = "#9AA0A8";
    const string FallbackSecond = "#FF8A65";
    const string FallbackTime = "#E8EAED";
    const string FallbackFace = "#202228";

    readonly DispatcherTimer _faceTick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _motion = new() { Interval = TimeSpan.FromMilliseconds(100) };
    DateTime _lastFaceBuild = DateTime.MinValue;
    int _faceMinuteKey = -1;
    readonly List<Rect> _usedLabelRects = [];
    readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    readonly List<WedgeHotspot> _hotspots = [];
    readonly List<UIElement> _wedgeLines = [];
    WedgeLayerCompositor? _wedgeBlend;
    readonly Window _tipWindow;
    readonly Border _tipChrome;
    readonly StackPanel _tipBody = new() { MaxWidth = 280 };
    int _tipCursorX = int.MinValue;
    int _tipCursorY = int.MinValue;
    bool _tipVisible;
    bool _tipWarmed;
    bool _tipClosing;

    Line? _secondHand;
    Ellipse? _secondHub;
    UIElement? _discordCue;
    bool _discordCuePulses;
    double _discordCuePeak = 0.7;
    UIElement? _lastChanceCue;
    bool _lastChanceCuePulses;
    double _lastChanceCuePeak = 0.7;
    readonly List<RotateTransform> _gearRots = [];
    readonly List<double> _gearSecScale = [];
    readonly List<double> _gearBaseDeg = [];
    bool _motionOn;
    double _handCx;
    double _handCy;
    double _handRadius;
    double _gearHorizonHours = 24;
    string? _openTip;
    bool _buildingFace;

    sealed class WedgeHotspot
    {
        public required double Cx { get; init; }
        public required double Cy { get; init; }
        public required double Rim { get; init; }
        public required double A0 { get; init; }
        public required double A1 { get; init; }
        public double Inner { get; init; }
        public bool Cutout { get; init; }
        public required string Tip { get; init; }

        public bool Contains(Point p)
        {
            var dx = p.X - Cx;
            var dy = p.Y - Cy;
            var r2 = dx * dx + dy * dy;
            if (r2 > Rim * Rim)
                return false;
            // Polar(): x = cx + r·sin(t), y = cy − r·cos(t) → t = atan2(dx, −dy)
            var t = Math.Atan2(dx, -dy);
            var a0 = A0;
            var a1 = A1;
            while (t < a0)
                t += Math.PI * 2;
            while (t > a0 + Math.PI * 2)
                t -= Math.PI * 2;
            if (t > a1)
                return false;
            var inner = WedgeInnerRadius(a0, a1, Rim, Inner, Cutout, t);
            return r2 + 0.01 >= inner * inner;
        }
    }

    static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public ClockOverlay()
    {
        InitializeComponent();
        // Dedicated tip HWND: warmed once while the clock is visible, then only
        // opacity/position change on hover — avoids Popup HWND churn (busy cursor)
        // and stays outside the clipped clock window.
        _tipChrome = new Border
        {
            Background = TipBg,
            BorderBrush = TipBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = _tipBody,
            IsHitTestVisible = false
        };
        _tipWindow = new Window
        {
            Content = _tipChrome,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            Focusable = false,
            IsHitTestVisible = false,
            Opacity = 0,
            Left = -32000,
            Top = -32000
        };
        // Owner is set in WarmTipHost after this window has been shown — setting it
        // in the ctor throws (clock HWND does not exist yet).
        _tipWindow.Closing += (_, e) =>
        {
            if (!_tipClosing)
                e.Cancel = true;
        };
        LocationChanged += (_, _) => AppHost.Instance.ScheduleBoundsSave();
        SizeChanged += (_, _) =>
        {
            AppHost.Instance.ScheduleBoundsSave();
            Refresh();
        };
        AppHost.Instance.UiChanged += OnUiChanged;
        _faceTick.Tick += (_, _) => OnFaceTick();
        _motion.Tick += (_, _) => OnMotion();
        Loaded += (_, _) =>
        {
            WarmTipHost();
            ApplyChrome();
            Refresh();
            StartMotion();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                WarmTipHost();
                StartMotion();
            }
            else
            {
                StopMotion();
                SleepTipHost();
            }
        };
        Closed += (_, _) =>
        {
            StopMotion();
            _tipClosing = true;
            HideTip();
            try { _tipWindow.Close(); } catch { /* already closing */ }
            AppHost.Instance.UiChanged -= OnUiChanged;
        };
    }

    /// <summary>
    /// Create tip HWND once per clock-visible session (off-screen, opacity 0).
    /// Styles applied once; later hover only moves + fades — no Show/Hide churn.
    /// </summary>
    void WarmTipHost()
    {
        if (_tipWarmed || !IsLoaded || !IsVisible)
            return;
        // Owner requires this window to have been shown already.
        if (_tipWindow.Owner != this)
            _tipWindow.Owner = this;
        if (!_tipWindow.IsVisible)
            _tipWindow.Show();
        NativeMethods.ConfigureTipWindow(_tipWindow);
        _tipWindow.Opacity = 0;
        _tipWindow.Left = -32000;
        _tipWindow.Top = -32000;
        _tipWarmed = true;
        _tipVisible = false;
    }

    /// <summary>Fully hide tip host when the clock module is hidden (rare path).</summary>
    void SleepTipHost()
    {
        HideTip();
        if (_tipWindow.IsVisible)
            _tipWindow.Hide();
        _tipWarmed = false;
    }

    public void RefreshDiscordCue()
    {
        if (!IsLoaded || !IsVisible)
            return;
        RefreshFace(restageGears: false);
    }

    void OnUiChanged() => RefreshFace(restageGears: false);

    void OnFaceTick()
    {
        var now = DateTime.Now;
        var minuteKey = now.Hour * 60 + now.Minute;
        var minuteChanged = minuteKey != _faceMinuteKey;
        // Wedge motion is sub-pixel for most horizons; rebuilding the face every
        // second was the steady CPU cost. The minute flip still updates the time.
        if (!minuteChanged && (now - _lastFaceBuild).TotalSeconds < 15)
            return;
        _faceMinuteKey = minuteKey;
        RefreshFace(restageGears: false);
    }

    void StartMotion()
    {
        _faceTick.Start();
        UpdateMotionRate();
        if (_motionOn)
            return;
        _motion.Start();
        _motionOn = true;
    }

    void StopMotion()
    {
        _faceTick.Stop();
        if (!_motionOn)
            return;
        _motion.Stop();
        _motionOn = false;
    }

    void UpdateMotionRate()
    {
        var settings = AppHost.Instance.Data.Settings;
        var animate = settings.ClockShowSecondHand
            || settings.ClockGearDriveMode != ClockGearDriveMode.Off
            || _discordCuePulses
            || _lastChanceCuePulses;
        var interval = animate ? TimeSpan.FromMilliseconds(33) : TimeSpan.FromMilliseconds(100);
        if (_motion.Interval != interval)
            _motion.Interval = interval;
    }

    void OnMotion()
    {
        if (!IsVisible || !IsLoaded || _buildingFace)
            return;
        var now = DateTime.Now;
        var settings = AppHost.Instance.Data.Settings;
        if (settings.ClockShowSecondHand || settings.ClockGearDriveMode != ClockGearDriveMode.Off)
            UpdateSecondHand(now);
        if (settings.ClockGearDriveMode != ClockGearDriveMode.Off)
            UpdatePlanetaryDrive(now);
        UpdateHoverTip();
        UpdateFaceCuePulses(now);
    }

    public void ApplyChrome()
    {
        var host = AppHost.Instance;
        var mode = host.Data.Settings.ClockLockMode;
        // auto model: lock mode is independent of the main overlay lock
        ForceClickThrough = mode == ClockLockMode.Locked;
        ResizeMode = mode == ClockLockMode.Unlocked
            ? ResizeMode.CanResizeWithGrip
            : ResizeMode.NoResize;

        if (mode == ClockLockMode.Unlocked)
        {
            ApplyDragChrome(ChromeBar, ChromeActions, ChromeTitle, ChromeRow, FaceHost);
            ChromeTitle.Text = host.ClockHorizonLabel();
            ChromeTitle.FontWeight = host.OverlayChromeWeight();
            ApplyPanelChrome(PanelBorder, DitherLayer);
            ForceClickThrough = false;
        }
        else
        {
            // Locked + semi-locked: identical chrome-less layout so the face can sit flush at the top.
            ChromeRow.Height = new GridLength(0);
            ChromeBar.Visibility = Visibility.Collapsed;
            ChromeBar.Height = 0;
            ChromeBar.Padding = new Thickness(0);
            PanelBorder.Background = Brushes.Transparent;
            PanelBorder.BorderBrush = Brushes.Transparent;
            PanelBorder.BorderThickness = new Thickness(0);
            PanelBorder.Padding = new Thickness(0);
            if (DitherLayer != null)
            {
                DitherLayer.Visibility = Visibility.Collapsed;
                DitherLayer.Background = Brushes.Transparent;
            }
            FaceHost.Margin = new Thickness(0);
            // No resize grip chrome.
            ResizeMode = ResizeMode.NoResize;
        }

        ApplyClickThrough();
    }

    public void Refresh() => RefreshFace(restageGears: true);

    void RefreshFace(bool restageGears)
    {
        if (!IsLoaded)
            return;
        _buildingFace = true;
        try
        {
            RefreshCore(restageGears);
        }
        finally
        {
            _buildingFace = false;
            _lastFaceBuild = DateTime.Now;
            UpdateMotionRate();
            // Force tip hit-test next frame after hotspots change.
            _tipCursorX = int.MinValue;
        }
    }

    void RefreshCore(bool restageGears)
    {
        var now = DateTime.Now;
        FaceFill.Children.Clear();
        Cues.Children.Clear();
        Face.Children.Clear();
        Marks.Children.Clear();
        _usedLabelRects.Clear();
        _hotspots.Clear();
        // Keep tip open across face rebuilds — closing it every tick caused flicker.

        var width = FaceHost.ActualWidth;
        var height = FaceHost.ActualHeight;
        if (width < 40 || height < 40)
            return;

        var host = AppHost.Instance;
        var settings = host.Data.Settings;
        var mode = settings.ClockLockMode;
        ForceClickThrough = mode == ClockLockMode.Locked;
        var spanHours = host.ClockHorizonHours();
        var radius = Math.Min(width, height) * 0.38;
        var font = Math.Max(10, settings.FontSize - 2);
        // auto model
        var timeFont = Math.Clamp(settings.ClockTimeFontSize <= 0 ? 13 : settings.ClockTimeFontSize, 8, 48);
        var weight = host.OverlayChromeWeight();
        var use12 = settings.ClockUse12Hour;
        var rim = radius - RingStroke / 2.0;
        host.ClockFaceRim = rim;
        var cx = width / 2;
        // Top-align when chrome-less so the face can sit against the top of the monitor.
        var cy = mode == ClockLockMode.Unlocked
            ? height / 2
            : radius + 8;
        if (cy + radius + 8 > height)
            cy = height / 2;
        _handCx = cx;
        _handCy = cy;
        _handRadius = rim;

        var clockEvents = host.ClockEvents(now);
        // auto model: optional face tint while a recurring/scheduled task is due
        var attention = settings.ClockAttentionTint
            && clockEvents.Any(e => ClockItemIsDue(e)
                && (e.Kind is "todo" or "event"));
        var showFace = attention
            || (mode != ClockLockMode.Unlocked && settings.ClockKeepFaceWhenLocked);
        if (showFace)
        {
            Brush faceFill;
            if (attention)
            {
                faceFill = BrushFromHex(
                    ColorText.NormalizeOr(settings.ClockColorAttention, "#FF8A65"), 0xF2);
            }
            else if (ColorText.TryNormalize(settings.ClockColorFace, out var faceHex))
                faceFill = BrushFromHex(faceHex, 0xF2);
            else
                faceFill = host.PanelBrush();

            var disk = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = faceFill,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(disk, cx - radius);
            Canvas.SetTop(disk, cy - radius);
            FaceFill.Children.Add(disk);
        }

        if (settings.ClockGearDriveMode != ClockGearDriveMode.Off)
        {
            _gearHorizonHours = spanHours;
            if (restageGears)
            {
                Gears.Children.Clear();
                OuterDrive.Children.Clear();
                HourAccents.Children.Clear();
                _gearRots.Clear();
                _gearSecScale.Clear();
                _gearBaseDeg.Clear();
                AddConcentricPlanetary(cx, cy, rim, width, height, settings, now);
            }
        }
        else if (restageGears)
        {
            Gears.Children.Clear();
            OuterDrive.Children.Clear();
            HourAccents.Children.Clear();
            _gearRots.Clear();
            _gearSecScale.Clear();
            _gearBaseDeg.Clear();
        }

        var ring = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = BrushFromHex(ColorText.NormalizeOr(settings.ClockColorRing, FallbackRing), 0xCC),
            StrokeThickness = RingStroke,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(ring, cx - radius);
        Canvas.SetTop(ring, cy - radius);
        Face.Children.Add(ring);

        DrawEventWedges(clockEvents, now, spanHours, cx, cy, rim, use12);
        if (settings.ClockGearDriveMode == ClockGearDriveMode.Off)
            DrawClockTicks(now, spanHours, cx, cy, rim, settings);

        var chevronTipY = cy - rim + Math.Max(6, rim * 0.07);

        if (settings.ClockShowEventLabels)
        {
            foreach (var item in host.ClockEvents(now))
            {
                var hours = Math.Clamp(item.HoursAhead, 0, spanHours);
                var title = item.Title.Length > 20 ? item.Title[..19] + "…" : item.Title;
                TryPlaceEventLabel(cx, cy, rim, hours, spanHours, title,
                    BrushFromHex(MarkColorHex(settings, item, now), 255),
                    font - 1, weight);
            }
        }

        if (settings.ClockShowHourHand || settings.ClockGearDriveMode != ClockGearDriveMode.Off)
            DrawHourHand(cx, cy, rim, settings);

        if (settings.ClockShowSecondHand || settings.ClockGearDriveMode != ClockGearDriveMode.Off)
        {
            EnsureSecondHand(settings);
            UpdateSecondHand(now);
        }
        else
        {
            Hands.Children.Clear();
            _secondHand = null;
            _secondHub = null;
        }

        // Time sits on Marks (above Face + Hands) so hands never cut the outline/fill.
        var timeText = FormatClockTime(now, use12, withSeconds: false);
        var timeBrush = BrushFromHex(ColorText.NormalizeOr(settings.ClockColorTime, FallbackTime), 255);
        var pos = Math.Clamp(settings.ClockTimePosition, 0, 1);
        PlaceCurrentTime(timeText, timeBrush, timeFont, weight, cx, cy, chevronTipY + 3, pos, settings); // auto model
        DrawNowMark(cx, cy, rim, settings);

        // Semi-lock / unlocked: invisible hit plate so the face can be dragged without layout chrome.
        if (mode is ClockLockMode.SemiLocked or ClockLockMode.Unlocked)
        {
            var hit = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = Brushes.Transparent,
                Tag = "hit",
                Cursor = mode == ClockLockMode.SemiLocked ? Cursors.SizeAll : Cursors.Arrow
            };
            Canvas.SetLeft(hit, cx - radius);
            Canvas.SetTop(hit, cy - radius);
            hit.MouseLeftButtonDown += OnFaceDrag;
            Face.Children.Add(hit);
        }

        ApplyDiscordCue(settings, cx, cy, radius);
        ApplyLastChanceCue(settings, clockEvents, now, cx, cy, radius);
        ApplyDiscordSender(settings, cx, cy, radius);
        ApplyClickThrough();
    }

    void OnFaceDrag(object sender, MouseButtonEventArgs e)
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
        catch (InvalidOperationException)
        {
            // Ignore if mouse is not down anymore.
        }
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

    static bool ClockItemIsDue(
        (string Title, double HoursAhead, double? DueByHoursAhead, string Kind, DateTime At, DateTime? DueBy, double? UsedPercent, double Opacity) item) =>
        (item.DueByHoursAhead ?? item.HoursAhead) <= 0.02;

    static double WedgeTypeMax(AppSettings settings, string kind, bool due)
    {
        var typeOp = due
            ? settings.ClockWedgeOpacityDue
            : string.Equals(kind, "event", StringComparison.Ordinal)
                ? settings.ClockWedgeOpacityScheduled
                : settings.ClockWedgeOpacityRecurring;
        return Math.Clamp(typeOp <= 0 ? (due ? 0.78 : 0.63) : typeOp, 0.05, 1);
    }

    static byte ItemAlpha(double itemOpacity) =>
        (byte)Math.Clamp(Math.Round((itemOpacity <= 0 ? 1 : Math.Clamp(itemOpacity, 0.05, 1)) * 255), 1, 255);

    void DrawEventWedges(
        List<(string Title, double HoursAhead, double? DueByHoursAhead, string Kind, DateTime At, DateTime? DueBy, double? UsedPercent, double Opacity)> events,
        DateTime now,
        double spanHours,
        double cx,
        double cy,
        double rim,
        bool use12)
    {
        var settings = AppHost.Instance.Data.Settings;
        // X is the configured wedge angle. Same-color marks closer than X+1° share one
        // fill so opacity never stacks; X+1° apart leaves a 1° gap.
        var wedgeTurns = Math.Clamp(settings.ClockWedgeWidth, 0.01, 0.15);
        var halfTurns = wedgeTurns * 0.5;
        var splitTurns = wedgeTurns + (1.0 / 360.0);
        var span = Math.Max(spanHours, 0.001);

        var bands = new List<(string Title, double HoursAhead, double? DueByHoursAhead, string Kind, DateTime At, DateTime? DueBy, double? UsedPercent, double Opacity)>();
        var points = new List<(string Title, double HoursAhead, double? DueByHoursAhead, string Kind, DateTime At, DateTime? DueBy, double? UsedPercent, double Opacity)>();
        foreach (var item in events)
        {
            if (ShowsAsTimeBand(item, settings))
                bands.Add(item);
            else
                points.Add(item);
        }

        // Furthest end underneath, nearest end on top, so a closer window darkens
        // the one that runs past it instead of the later color covering the nearer one.
        var layers = new List<(double FarHours, Action Draw)>();
        foreach (var item in bands)
            layers.Add((item.DueByHoursAhead ?? item.HoursAhead, () =>
                DrawOneClockMark(item, spanHours, cx, cy, rim, use12, settings)));

        foreach (var colorGroup in points.GroupBy(item => TypeColorHex(
                     settings, item.Kind, ClockItemIsDue(item), item.UsedPercent)))
        {
            var sorted = colorGroup.OrderBy(c => c.HoursAhead).ToList();
            var clusters = new List<List<(string Title, double HoursAhead, double? DueByHoursAhead, string Kind, DateTime At, DateTime? DueBy, double? UsedPercent, double Opacity)>>();
            foreach (var item in sorted)
            {
                if (clusters.Count == 0
                    || (item.HoursAhead - clusters[^1][^1].HoursAhead) / span >= splitTurns)
                    clusters.Add([item]);
                else
                    clusters[^1].Add(item);
            }

            foreach (var cluster in clusters)
            {
                // Already due at now: each item is its own layer so those opacities add.
                if (cluster.All(c => c.HoursAhead <= 0.02))
                {
                    foreach (var item in cluster)
                        layers.Add((0, () => DrawOneClockMark(item, spanHours, cx, cy, rim, use12, settings)));
                    continue;
                }

                var times = new List<double>();
                foreach (var hours in cluster.Select(c => c.HoursAhead).OrderBy(h => h))
                {
                    if (times.Count == 0 || hours - times[^1] > 1.0 / 3600.0)
                        times.Add(hours);
                }

                var minH = times[0];
                var maxH = times[^1];
                var a0 = (minH / span - halfTurns) * Math.PI * 2;
                var a1 = (maxH / span + halfTurns) * Math.PI * 2;
                if (a1 - a0 > Math.PI * 2 - 0.01)
                    a1 = a0 + Math.PI * 2 - 0.01;
                LimitWedgeToHorizon(ref a0, ref a1);
                var opacity = Math.Clamp(cluster.Max(c => c.Opacity <= 0 ? 1 : c.Opacity), 0.05, 1);
                var typeMax = WedgeTypeMax(settings, cluster[0].Kind, cluster.All(ClockItemIsDue));
                var alpha = ItemAlpha(opacity);
                var fill = BrushFromHex(colorGroup.Key, alpha);
                var tip = BuildClusterTip(cluster, use12);
                var lineTimes = times.ToList();
                var profile = WedgeProfileStart(settings);
                layers.Add((maxH, () =>
                {
                    BeginWedgeLayer();
                    AddWedgeFill(cx, cy, rim, a0, a1, fill, profile, typeMax);
                    EndWedgeLayer();
                    AddWedgeLines(lineTimes, spanHours, cx, cy, rim, a0, a1, settings, alpha, typeMax, profile);
                    _hotspots.Add(MakeWedgeHotspot(cx, cy, rim, a0, a1, tip, profile));
                }));
            }
        }

        _wedgeLines.Clear();
        _wedgeBlend = settings.ClockWedgeBlend == ClockWedgeBlend.Normal
            ? null
            : WedgeLayerCompositor.TryCreate(Face, settings.ClockWedgeBlend);
        foreach (var layer in layers.OrderByDescending(layer => layer.FarHours))
            layer.Draw();
        FinishWedgeBlend();
    }

    void BeginWedgeLayer() => _wedgeBlend?.BeginLayer();

    void EndWedgeLayer() => _wedgeBlend?.EndLayer();

    void FinishWedgeBlend()
    {
        if (_wedgeBlend == null)
            return;
        Face.Children.Add(_wedgeBlend.CreateImage());
        foreach (var line in _wedgeLines)
            Face.Children.Add(line);
        _wedgeLines.Clear();
        _wedgeBlend = null;
    }

    static bool ShowsAsTimeBand(
        (string Title, double HoursAhead, double? DueByHoursAhead, string Kind, DateTime At, DateTime? DueBy, double? UsedPercent, double Opacity) item,
        AppSettings settings)
    {
        var windowOpen = item.HoursAhead <= 0.02;
        return item.DueByHoursAhead is double endHours
            && endHours > item.HoursAhead + 0.001
            && item.DueBy is DateTime
            && item.DueBy > item.At
            && (!settings.ClockWindowStartOnly || windowOpen);
    }

    void DrawOneClockMark(
        (string Title, double HoursAhead, double? DueByHoursAhead, string Kind, DateTime At, DateTime? DueBy, double? UsedPercent, double Opacity) item,
        double spanHours,
        double cx,
        double cy,
        double rim,
        bool use12,
        AppSettings settings)
    {
        var now = DateTime.Now;
        var due = ClockItemIsDue(item);
        var opacity = Math.Clamp(item.Opacity <= 0 ? 1 : item.Opacity, 0.05, 1);
        var alpha = ItemAlpha(opacity);
        var typeMax = WedgeTypeMax(settings, item.Kind, due);
        var tip = BuildClusterTip([item], use12);

        // Start-only: future windows get a classic start mark; once the window is open, show the band.
        var windowOpen = item.HoursAhead <= 0.02;
        var showBand = item.DueByHoursAhead is double endHours
            && endHours > item.HoursAhead + 0.001
            && item.DueBy is DateTime
            && item.DueBy > item.At
            && (!settings.ClockWindowStartOnly || windowOpen);

        BeginWedgeLayer();
        if (showBand && item.DueByHoursAhead is double bandEnd && item.DueBy is DateTime bandDue)
        {
            var startH = Math.Clamp(item.HoursAhead, 0, spanHours);
            var endH = Math.Clamp(bandEnd, 0, spanHours);
            var a0 = startH / Math.Max(spanHours, 0.001) * Math.PI * 2;
            var a1 = endH / Math.Max(spanHours, 0.001) * Math.PI * 2;
            if (a1 <= a0 + 0.0001)
            {
                var half = Math.Max(0.005, Math.Clamp(settings.ClockWedgeWidth, 0.01, 0.15) / 2.0);
                var mid = startH / Math.Max(spanHours, 0.001);
                a0 = (mid - half) * Math.PI * 2;
                a1 = (mid + half) * Math.PI * 2;
            }

            LimitWedgeToHorizon(ref a0, ref a1);

            if (settings.ClockWindowShowGradient)
            {
                AddWindowGradientFill(
                    cx, cy, rim, a0, a1, startH, endH,
                    item.At, bandDue, now, settings, alpha, typeMax);
            }
            else
            {
                var hex = MarkColorHex(settings, item, now);
                var bandMax = WindowProgressOpacity(settings, item.At, bandDue, now, typeMax);
                var profile = WindowProgressProfile(settings, item.At, bandDue, now);
                AddWedgeFill(cx, cy, rim, a0, a1, BrushFromHex(hex, alpha), profile, bandMax);
            }

            EndWedgeLayer();
            AddWedgeLines([startH, endH], spanHours, cx, cy, rim, a0, a1, settings, alpha, typeMax,
                WedgeProfileStart(settings), WedgeProfileEnd(settings), item.At, bandDue, now);
            _hotspots.Add(MakeWedgeHotspot(cx, cy, rim, a0, a1, tip,
                WindowHitProfile(settings)));
            return;
        }

        // Classic: wedge at start time only (urgency color still uses due-by when set).
        var hexPoint = MarkColorHex(settings, item, now);
        var hours = Math.Clamp(item.HoursAhead, 0, spanHours);
        var halfTurns = Math.Max(0.005, Math.Clamp(settings.ClockWedgeWidth, 0.01, 0.15) / 2.0);
        var centerTurn = hours / Math.Max(spanHours, 0.001);
        var fa0 = (centerTurn - halfTurns) * Math.PI * 2;
        var fa1 = (centerTurn + halfTurns) * Math.PI * 2;
        LimitWedgeToHorizon(ref fa0, ref fa1);
        var pointProfile = WedgeProfileStart(settings);
        AddWedgeFill(cx, cy, rim, fa0, fa1, BrushFromHex(hexPoint, alpha), pointProfile, typeMax);
        EndWedgeLayer();
        AddWedgeLines([hours], spanHours, cx, cy, rim, fa0, fa1, settings, alpha, typeMax, pointProfile);
        _hotspots.Add(MakeWedgeHotspot(cx, cy, rim, fa0, fa1, tip, pointProfile));
    }

    /// <summary>
    /// Paint urgency along the visible band as a true angle→color sweep.
    /// WPF has no ConicGradientBrush, so this rasterizes one WriteableBitmap:
    /// each pixel’s color/opacity/fade comes from its angle (and radius), with no slices.
    /// </summary>
    Brush AddWindowGradientFill(
        double cx, double cy, double rim, double a0, double a1,
        double startH, double endH,
        DateTime windowStart, DateTime dueBy, DateTime now,
        AppSettings settings, byte alpha, double typeFallback)
    {
        var openFallback = ColorText.NormalizeOr(settings.ClockWindowOpenColor, "#5DDC8A");
        var visibleFrom = now.AddHours(Math.Min(startH, endH));
        var visibleTo = now.AddHours(Math.Max(startH, endH));
        if (visibleTo <= visibleFrom)
        {
            var hex = WindowProgressColorHex(settings, windowStart, dueBy, now, openFallback);
            var op = WindowProgressOpacity(settings, windowStart, dueBy, now, typeFallback);
            var profile = WindowProgressProfile(settings, windowStart, dueBy, now);
            var solid = BrushFromHex(hex, ScaleAlpha(alpha, op));
            AddWedgeFill(cx, cy, rim, a0, a1, solid, profile, 1);
            return solid;
        }

        var dipW = Face.ActualWidth;
        var dipH = Face.ActualHeight;
        if (dipW < 2 || dipH < 2 || rim < 2)
            return BrushFromHex(openFallback, alpha);

        var bmp = BuildConicWindowBitmap(
            dipW, dipH, cx, cy, rim, a0, a1,
            visibleFrom, visibleTo, windowStart, dueBy,
            settings, alpha, typeFallback, openFallback);
        PresentConicWindowBitmap(bmp, dipW, dipH);
        return BrushFromHex(openFallback, alpha);
    }

    void PresentConicWindowBitmap(WriteableBitmap bmp, double dipW, double dipH)
    {
        var brush = new ImageBrush(bmp)
        {
            Viewbox = new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, dipW, dipH),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
            TileMode = TileMode.None
        };
        brush.Freeze();
        var geometry = new RectangleGeometry(new Rect(0, 0, dipW, dipH));
        geometry.Freeze();
        if (_wedgeBlend is { LayerOpen: true })
        {
            _wedgeBlend.Draw(geometry, brush, null);
            return;
        }

        Face.Children.Add(new System.Windows.Shapes.Path
        {
            Data = geometry,
            Fill = brush,
            Stroke = null,
            IsHitTestVisible = false
        });
    }

    WriteableBitmap BuildConicWindowBitmap(
        double dipW, double dipH,
        double cx, double cy, double rim,
        double a0, double a1,
        DateTime visibleFrom, DateTime visibleTo,
        DateTime windowStart, DateTime dueBy,
        AppSettings settings, byte alpha, double typeFallback, string openFallback)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var dpiX = dpi.PixelsPerInchX;
        var dpiY = dpi.PixelsPerInchY;
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        var width = Math.Max(1, (int)Math.Ceiling(dipW * scaleX));
        var height = Math.Max(1, (int)Math.Ceiling(dipH * scaleY));
        var pixels = new byte[width * height * 4];
        var span = a1 - a0;
        if (span <= 1e-9)
            span = 1e-9;

        const int stops = 256;
        var colors = new Color[stops + 1];
        var profiles = new WedgeProfile[stops + 1];
        var spanTicks = (visibleTo - visibleFrom).Ticks;
        for (var i = 0; i <= stops; i++)
        {
            var u = i / (double)stops;
            var at = spanTicks <= 0
                ? visibleFrom
                : visibleFrom + TimeSpan.FromTicks((long)(spanTicks * u));
            var hex = WindowProgressColorHex(settings, windowStart, dueBy, at, openFallback);
            var op = WindowProgressOpacity(settings, windowStart, dueBy, at, typeFallback);
            colors[i] = ColorFromHex(hex, ScaleAlpha(alpha, op));
            profiles[i] = WindowProgressProfile(settings, windowStart, dueBy, at);
        }

        for (var y = 0; y < height; y++)
        {
            var ly = (y + 0.5) / scaleY;
            var dy = ly - cy;
            for (var x = 0; x < width; x++)
            {
                var lx = (x + 0.5) / scaleX;
                var dx = lx - cx;
                var r = Math.Sqrt(dx * dx + dy * dy);
                if (r > rim + 0.75 || r < 0.25)
                    continue;

                // Match Polar(): x = sin(t), y = -cos(t)
                var turn = Math.Atan2(dx, -dy);
                var u = AngleToBandU(turn, a0, span);
                if (u < 0)
                    continue;

                var stop = u * stops;
                var i0 = Math.Clamp((int)stop, 0, stops);
                var i1 = Math.Min(stops, i0 + 1);
                var frac = stop - i0;
                var color = LerpColor(colors[i0], colors[i1], frac);
                var profile = LerpWedgeProfile(profiles[i0], profiles[i1], frac);
                var fade = RadialFadeFactor(r / rim, profile);
                if (fade <= 0.001)
                    continue;

                var edge = HorizonEdgeFade(turn);
                var a = (byte)Math.Clamp(Math.Round(color.A * fade * edge), 0, 255);
                if (a == 0)
                    continue;
                // Premultiply for Pbgra32
                var o = (y * width + x) * 4;
                pixels[o] = (byte)Math.Round(color.B * (a / 255.0));
                pixels[o + 1] = (byte)Math.Round(color.G * (a / 255.0));
                pixels[o + 2] = (byte)Math.Round(color.R * (a / 255.0));
                pixels[o + 3] = a;
            }
        }

        var bmp = new WriteableBitmap(width, height, dpiX, dpiY, PixelFormats.Pbgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>0–1 if <paramref name="turn"/> lies on the clockwise band starting at a0; else -1.</summary>
    static double AngleToBandU(double turn, double a0, double span)
    {
        var t = turn - a0;
        while (t < 0)
            t += Math.PI * 2;
        while (t >= Math.PI * 2)
            t -= Math.PI * 2;
        if (t > span + 1e-6)
            return -1;
        return Math.Clamp(t / span, 0, 1);
    }

    static Color LerpColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t),
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>Same radial falloff as <see cref="WedgeFadeMask"/>, plus inner cutout / truncation.</summary>
    static double RadialFadeFactor(double fracRim, WedgeProfile profile)
    {
        fracRim = Math.Clamp(fracRim, 0, 1.5);
        var inner = Math.Clamp(profile.Inner, 0, 0.80);
        var cutout = profile.Mode == ClockWedgeInnerMode.Cutout;
        if (cutout && fracRim < inner)
            return 0;
        if (!cutout && inner > 0.5 && fracRim < inner)
            return 0;

        var fade = Math.Clamp(profile.Fade, 0, 1);
        var reach = Math.Clamp(profile.FadeReach, 0, 1);
        var solidAt = Math.Max(reach, inner);
        if (fade <= 0.001)
            return 1;

        double curve;
        if (fracRim <= inner)
            curve = 1 - fade;
        else if (fracRim >= solidAt)
            curve = 1;
        else
        {
            var t = (fracRim - inner) / Math.Max(1e-6, solidAt - inner);
            curve = (1 - fade) + fade * t;
        }

        return Math.Clamp(curve, 0, 1);
    }

    static byte ScaleAlpha(byte alpha, double opacity) =>
        (byte)Math.Clamp(Math.Round(alpha * Math.Clamp(opacity, 0.05, 1)), 1, 255);

    Color ColorFromHex(string hex, byte alpha)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            color.A = alpha;
            return color;
        }
        catch
        {
            return Color.FromArgb(alpha, 0xE8, 0xEA, 0xED);
        }
    }

    readonly record struct WedgeProfile(
        double Inner, double Fade, double FadeReach, ClockWedgeInnerMode Mode);

    static WedgeProfile WedgeProfileStart(AppSettings settings) => new(
        Math.Clamp(settings.ClockWedgeInner, 0, 0.80),
        Math.Clamp(settings.ClockWedgeFade, 0, 1),
        Math.Clamp(settings.ClockWedgeFadeReach, 0, 1),
        Enum.IsDefined(settings.ClockWedgeInnerMode)
            ? settings.ClockWedgeInnerMode
            : ClockWedgeInnerMode.Vertex);

    static WedgeProfile WedgeProfileEnd(AppSettings settings) => new(
        Math.Clamp(settings.ClockWedgeInnerEnd, 0, 0.80),
        Math.Clamp(settings.ClockWedgeFadeEnd, 0, 1),
        Math.Clamp(settings.ClockWedgeFadeReachEnd, 0, 1),
        Enum.IsDefined(settings.ClockWedgeInnerModeEnd)
            ? settings.ClockWedgeInnerModeEnd
            : WedgeProfileStart(settings).Mode);

    static WedgeProfile WindowHitProfile(AppSettings settings)
    {
        var start = WedgeProfileStart(settings);
        var end = WedgeProfileEnd(settings);
        return new(
            Math.Min(start.Inner, end.Inner),
            Math.Max(start.Fade, end.Fade),
            Math.Max(start.FadeReach, end.FadeReach),
            start.Inner <= end.Inner ? start.Mode : end.Mode);
    }

    static WedgeProfile LerpWedgeProfile(WedgeProfile a, WedgeProfile b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new(
            a.Inner + (b.Inner - a.Inner) * t,
            a.Fade + (b.Fade - a.Fade) * t,
            a.FadeReach + (b.FadeReach - a.FadeReach) * t,
            t < 0.5 ? a.Mode : b.Mode);
    }

    static WedgeProfile WindowProgressProfile(
        AppSettings settings, DateTime start, DateTime dueBy, DateTime now) =>
        LerpWedgeProfile(WedgeProfileStart(settings), WedgeProfileEnd(settings),
            WindowProgressAmount(
                settings, start, dueBy, now,
                settings.ClockWedgeEndWarnPercent <= 0
                    ? (settings.ClockWindowWarnPercent <= 0 ? 0.5 : settings.ClockWindowWarnPercent)
                    : settings.ClockWedgeEndWarnPercent,
                settings.ClockWedgeEndLastHours <= 0
                    ? (settings.ClockWindowLastHours <= 0 ? 1 : settings.ClockWindowLastHours)
                    : settings.ClockWedgeEndLastHours));

    void AddWedgeFill(
        double cx, double cy, double rim, double a0, double a1, Brush fill,
        WedgeProfile profile, double typeMax = 1)
    {
        var fadeStart = HorizonTurn - HorizonFade;
        if (a1 <= fadeStart || a1 <= a0)
        {
            PaintWedge(cx, cy, rim, a0, a1, fill, profile, typeMax);
            return;
        }

        var solidEnd = Math.Max(a0, fadeStart);
        if (solidEnd > a0 + 1e-4)
            PaintWedge(cx, cy, rim, a0, solidEnd, fill, profile, typeMax);

        const int steps = 10;
        var span = a1 - solidEnd;
        if (span <= 1e-5)
            return;
        for (var i = 0; i < steps; i++)
        {
            var s0 = solidEnd + span * i / steps;
            var s1 = solidEnd + span * (i + 1) / steps;
            var opacity = HorizonEdgeFade((s0 + s1) * 0.5);
            if (opacity <= 0.02)
                continue;
            PaintWedge(cx, cy, rim, s0, s1, ScaleFillAlpha(fill, opacity), profile, typeMax);
        }
    }

    void PaintWedge(
        double cx, double cy, double rim, double a0, double a1, Brush fill,
        WedgeProfile profile, double typeMax)
    {
        var inner = profile.Inner * rim;
        var cutout = profile.Mode == ClockWedgeInnerMode.Cutout;
        var geometry = WedgeGeometry(cx, cy, rim, a0, a1, inner, cutout);
        var mask = WedgeFadeMask(cx, cy, rim, profile, typeMax);
        if (_wedgeBlend is { LayerOpen: true })
        {
            _wedgeBlend.Draw(geometry, fill, mask);
            return;
        }
        Face.Children.Add(new System.Windows.Shapes.Path
        {
            Data = geometry,
            Fill = fill,
            OpacityMask = mask,
            Stroke = null,
            StrokeThickness = 0,
            IsHitTestVisible = false
        });
    }

    static Brush ScaleFillAlpha(Brush fill, double opacity)
    {
        if (opacity >= 0.999)
            return fill;
        if (fill is SolidColorBrush solid)
        {
            var c = solid.Color;
            var scaled = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(Math.Round(c.A * opacity), 0, 255),
                c.R, c.G, c.B));
            scaled.Freeze();
            return scaled;
        }

        var clone = fill.CloneCurrentValue();
        clone.Opacity *= opacity;
        if (clone.CanFreeze)
            clone.Freeze();
        return clone;
    }

    static RadialGradientBrush? WedgeFadeMask(
        double cx, double cy, double rim, WedgeProfile profile, double maxOpacity = 1)
    {
        var fade = Math.Clamp(profile.Fade, 0, 1);
        var cap = Math.Clamp(maxOpacity, 0.05, 1);
        if (fade <= 0.001 && cap >= 0.999)
            return null;
        var start = Math.Clamp(profile.Inner, 0, 0.80);
        var reach = Math.Clamp(profile.FadeReach, 0, 1);
        var solidAt = Math.Max(reach, start);
        byte StopAlpha(double curve) =>
            (byte)Math.Clamp(Math.Round(255 * Math.Min(curve, cap)), 0, 255);
        var innerA = StopAlpha(1 - fade);
        var outerA = StopAlpha(1);
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(cx, cy),
            GradientOrigin = new Point(cx, cy),
            RadiusX = Math.Max(1, rim),
            RadiusY = Math.Max(1, rim)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(innerA, 255, 255, 255), 0));
        if (start > 0.001)
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(innerA, 255, 255, 255), start));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(outerA, 255, 255, 255), solidAt));
        if (solidAt < 0.999)
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(outerA, 255, 255, 255), 1));
        brush.Freeze();
        return brush;
    }

    static WedgeHotspot MakeWedgeHotspot(
        double cx, double cy, double rim, double a0, double a1, string tip, WedgeProfile profile) =>
        new()
        {
            Cx = cx, Cy = cy, Rim = rim, A0 = a0, A1 = a1, Tip = tip,
            Inner = profile.Inner * rim,
            Cutout = profile.Mode == ClockWedgeInnerMode.Cutout
        };

    const double HorizonTurn = Math.PI * 2;
    /// <summary>Keep the arc end just off the now ray so the sweep cannot wrap.</summary>
    static readonly double HorizonSeam = 0.08 * Math.PI / 180.0;
    /// <summary>Short ease-out into the now line, about 3°.</summary>
    static readonly double HorizonFade = 3.0 * Math.PI / 180.0;

    /// <summary>
    /// 1 away from the now line, 0 on it. Only the last <see cref="HorizonFade"/>
    /// before a full turn is affected.
    /// </summary>
    static double HorizonEdgeFade(double angle)
    {
        var t = angle;
        while (t < 0)
            t += HorizonTurn;
        while (t >= HorizonTurn)
            t -= HorizonTurn;
        var before = HorizonTurn - t;
        if (before >= HorizonFade)
            return 1;
        var u = before / HorizonFade;
        return u * u * (3 - 2 * u);
    }

    /// <summary>
    /// A full horizon turn lands back on the now ray. Keep the wedge on the
    /// future side of that ray so a mark near the far edge cannot read as current.
    /// </summary>
    static void LimitWedgeToHorizon(ref double a0, ref double a1)
    {
        var limit = HorizonTurn - HorizonSeam;
        if (a0 < 0)
            a0 = 0;
        if (a1 > limit)
            a1 = limit;
        if (a0 > a1)
            a0 = Math.Max(0, a1 - HorizonSeam);
    }

    static PathGeometry WedgeGeometry(
        double cx, double cy, double rim, double a0, double a1, double inner, bool cutout)
    {
        var p0 = PolarPoint(cx, cy, rim, a0);
        var p1 = PolarPoint(cx, cy, rim, a1);
        var large = a1 - a0 > Math.PI;
        var fig = new PathFigure { IsClosed = true, IsFilled = true };
        if (inner <= 0.5)
        {
            fig.StartPoint = new Point(cx, cy);
            fig.Segments.Add(new LineSegment(p0, false));
            fig.Segments.Add(new ArcSegment
            {
                Point = p1,
                Size = new Size(rim, rim),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = large,
                IsStroked = false
            });
            fig.Segments.Add(new LineSegment(new Point(cx, cy), false));
        }
        else if (cutout)
        {
            var i0 = PolarPoint(cx, cy, inner, a0);
            var i1 = PolarPoint(cx, cy, inner, a1);
            fig.StartPoint = i0;
            fig.Segments.Add(new LineSegment(p0, false));
            fig.Segments.Add(new ArcSegment
            {
                Point = p1,
                Size = new Size(rim, rim),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = large,
                IsStroked = false
            });
            fig.Segments.Add(new LineSegment(i1, false));
            fig.Segments.Add(new ArcSegment
            {
                Point = i0,
                Size = new Size(inner, inner),
                SweepDirection = SweepDirection.Counterclockwise,
                IsLargeArc = large,
                IsStroked = false
            });
        }
        else
        {
            var mid = (a0 + a1) * 0.5;
            fig.StartPoint = PolarPoint(cx, cy, inner, mid);
            fig.Segments.Add(new LineSegment(p0, false));
            fig.Segments.Add(new ArcSegment
            {
                Point = p1,
                Size = new Size(rim, rim),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = large,
                IsStroked = false
            });
        }
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    /// <summary>Radius where the wedge's inner edge crosses this angle.</summary>
    static double WedgeInnerRadius(double a0, double a1, double rim, double inner, bool cutout, double angle)
    {
        if (inner <= 0.5)
            return 0;
        if (cutout)
            return inner;
        var mid = (a0 + a1) * 0.5;
        var edge = angle <= mid ? a0 : a1;
        var ax = inner * Math.Sin(mid);
        var ay = inner * Math.Cos(mid);
        var bx = rim * Math.Sin(edge);
        var by = rim * Math.Cos(edge);
        var cA = ax * Math.Cos(angle) - ay * Math.Sin(angle);
        var cB = bx * Math.Cos(angle) - by * Math.Sin(angle);
        var den = cA - cB;
        if (Math.Abs(den) < 1e-8)
            return inner;
        var s = Math.Clamp(cA / den, 0, 1);
        var px = (1 - s) * ax + s * bx;
        var py = (1 - s) * ay + s * by;
        return Math.Sqrt(px * px + py * py);
    }

    void AddWedgeLines(
        IEnumerable<double> hoursAhead,
        double spanHours,
        double cx,
        double cy,
        double rim,
        double a0,
        double a1,
        AppSettings settings,
        byte alpha,
        double typeMax,
        WedgeProfile profile,
        WedgeProfile? endProfile = null,
        DateTime? windowStart = null,
        DateTime? dueBy = null,
        DateTime? now = null)
    {
        var lineBrush = BrushFromHex(
            ColorText.NormalizeOr(settings.ClockColorWedgeLine, "#FFFFFF"), alpha);
        var hoursList = hoursAhead.Distinct().OrderBy(h => h).ToList();
        for (var i = 0; i < hoursList.Count; i++)
        {
            var hours = hoursList[i];
            WedgeProfile lineProfile;
            if (endProfile is { } end
                && windowStart is DateTime ws
                && dueBy is DateTime db
                && now is DateTime n
                && hoursList.Count >= 2)
            {
                lineProfile = i == 0
                    ? profile
                    : i == hoursList.Count - 1
                        ? end
                        : WindowProgressProfile(settings, ws, db, n.AddHours(hours));
            }
            else
                lineProfile = profile;

            var inner = lineProfile.Inner * rim;
            var cutout = lineProfile.Mode == ClockWedgeInnerMode.Cutout;
            var lineMask = WedgeFadeMask(cx, cy, rim, lineProfile, typeMax);
            var exactHours = Math.Clamp(hours, 0, spanHours);
            var lineTurn = exactHours / Math.Max(spanHours, 0.001) * Math.PI * 2;
            var turnLo = Math.Min(a0, a1);
            var turnHi = Math.Max(a0, a1);
            if (lineTurn < turnLo || lineTurn > turnHi)
                lineTurn = Math.Clamp(lineTurn, turnLo, turnHi);
            var lineFade = HorizonEdgeFade(lineTurn);
            var atSide = !cutout && inner > 0.5
                && (Math.Abs(lineTurn - a0) < 1e-3 || Math.Abs(lineTurn - a1) < 1e-3);
            Point startPt;
            if (atSide)
                startPt = PolarPoint(cx, cy, inner, (a0 + a1) * 0.5);
            else
            {
                var startR = WedgeInnerRadius(a0, a1, rim, inner, cutout, lineTurn);
                if (cutout && inner > 0.5)
                    startR = Math.Min(rim - 1, startR + 1);
                startPt = PolarPoint(cx, cy, startR, lineTurn);
            }
            var tipR = cutout && inner > 0.5 ? rim - 1 : rim;
            var tipPt = PolarPoint(cx, cy, tipR, lineTurn);
            var line = new Line
            {
                X1 = startPt.X,
                Y1 = startPt.Y,
                X2 = tipPt.X,
                Y2 = tipPt.Y,
                Stroke = lineBrush,
                StrokeThickness = 1.75,
                StrokeStartLineCap = cutout ? PenLineCap.Flat : PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = lineFade,
                OpacityMask = lineMask,
                IsHitTestVisible = false
            };
            if (_wedgeBlend != null)
                _wedgeLines.Add(line);
            else
                Face.Children.Add(line);
        }
    }

    static string BuildClusterTip(
        List<(string Title, double HoursAhead, double? DueByHoursAhead, string Kind, DateTime At, DateTime? DueBy, double? UsedPercent, double Opacity)> cluster,
        bool use12)
    {
        var now = DateTime.Now;
        var sb = new StringBuilder();
        foreach (var item in cluster)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(item.Title)
                .Append("\n  ")
                .Append(TypeLabel(item.Kind));
            if (item.DueBy is DateTime dueBy)
            {
                sb.Append(" · ")
                    .Append(FormatClockTime(item.At, use12, withSeconds: false))
                    .Append(" – ")
                    .Append(FormatClockTime(dueBy, use12, withSeconds: false));
                if (ClockItemIsDue(item))
                    sb.Append(" · due");
                else if (item.HoursAhead <= 0.02)
                    sb.Append(" · window open · ends in ").Append(RecurrenceEngine.FormatRemainingLong(dueBy, now));
                else
                    sb.Append(" · in ").Append(RecurrenceEngine.FormatRemainingLong(item.At, now));
            }
            else
            {
                sb.Append(" · ")
                    .Append(FormatClockTime(item.At, use12, withSeconds: false));
                var remain = RecurrenceEngine.FormatRemainingLong(item.At, now);
                if (item.HoursAhead <= 0.02 || string.Equals(remain, "now", StringComparison.Ordinal))
                    sb.Append(" · now");
                else
                    sb.Append(" · in ").Append(remain);
            }

            if (item.UsedPercent is double used)
                sb.Append(" · ").Append($"{Math.Clamp(100 - used, 0, 100):0}% left");
        }

        return sb.ToString();
    }

    static string TypeLabel(string kind) =>
        kind switch
        {
            "todo" => "Recurring",
            "event" => "Scheduled",
            "once" => "To Do",
            "usage-claude-5h" => "Claude 5h",
            "usage-claude-week" => "Claude weekly",
            "usage-claude-opus" => "Claude Opus",
            "usage-cursor" => "Cursor",
            _ when kind.StartsWith("usage-", StringComparison.Ordinal) => "AI usage",
            _ => kind
        };

    // auto model: clock-tab colors, independent of overlay task colors
    static string TypeColorHex(AppSettings settings, string kind, bool due, double? usedPercent = null)
    {
        if (kind.StartsWith("usage-", StringComparison.Ordinal))
        {
            if (usedPercent is double u)
                return UsageTracker.UsageTintHex(u);
            if (kind.StartsWith("usage-cursor", StringComparison.Ordinal))
                return ColorText.NormalizeOr(settings.UsageColorCursor, "#8AB4F8");
            return ColorText.NormalizeOr(settings.UsageColorClaude, "#D4A574");
        }

        if (due)
            return ColorText.NormalizeOr(settings.ClockColorDue, FallbackDue);

        return kind switch
        {
            "todo" => ColorText.NormalizeOr(settings.ClockColorRecurring, FallbackRecurring),
            "event" => ColorText.NormalizeOr(settings.ClockColorScheduled, FallbackScheduled),
            _ => ColorText.NormalizeOr(settings.ClockColorRecurring, FallbackRecurring)
        };
    }

    /// <summary>
    /// Color for a due-by window: open color until warn%, then lerp to last-chance,
    /// full last-chance color in the final N hours (and after due-by).
    /// Before the window opens, returns <paramref name="beforeOpen"/>.
    /// </summary>
    static string WindowProgressColorHex(
        AppSettings settings, DateTime start, DateTime dueBy, DateTime now, string beforeOpen)
    {
        var open = ColorText.NormalizeOr(settings.ClockWindowOpenColor, "#5DDC8A");
        var last = ColorText.NormalizeOr(settings.ClockWindowLastColor, "#FF5A5A");
        if (now < start)
            return beforeOpen;
        if (now >= dueBy)
            return last;

        var warnFrac = Math.Clamp(
            settings.ClockWindowWarnPercent <= 0 ? 0.5 : settings.ClockWindowWarnPercent, 0.05, 0.95);
        var lastHours = Math.Clamp(
            settings.ClockWindowLastHours <= 0 ? 1 : settings.ClockWindowLastHours, 0.05, 24 * 14);
        var duration = (dueBy - start).TotalHours;
        if (duration <= 0)
            return last;

        var warnAt = start.AddHours(duration * warnFrac);
        var lastAt = dueBy.AddHours(-lastHours);
        if (lastAt < start)
            lastAt = start;

        if (now < warnAt)
            return open;
        if (now >= lastAt || lastAt <= warnAt)
            return last;

        var t = (now - warnAt).TotalHours / (lastAt - warnAt).TotalHours;
        return ColorText.Lerp(open, last, t);
    }

    /// <summary>
    /// 0 before warn, 1 at last-chance / due-by.
    /// Optional warn/last overrides are used for the wedge-end shape schedule.
    /// </summary>
    static double WindowProgressAmount(
        AppSettings settings, DateTime start, DateTime dueBy, DateTime now,
        double? warnPercent = null, double? lastHoursOverride = null)
    {
        if (now < start)
            return 0;
        if (now >= dueBy)
            return 1;

        var warnFrac = Math.Clamp(
            warnPercent ?? (settings.ClockWindowWarnPercent <= 0 ? 0.5 : settings.ClockWindowWarnPercent),
            0.05, 0.95);
        var lastHours = Math.Clamp(
            lastHoursOverride ?? (settings.ClockWindowLastHours <= 0 ? 1 : settings.ClockWindowLastHours),
            0.05, 24 * 14);
        var duration = (dueBy - start).TotalHours;
        if (duration <= 0)
            return 1;

        var warnAt = start.AddHours(duration * warnFrac);
        var lastAt = dueBy.AddHours(-lastHours);
        if (lastAt < start)
            lastAt = start;

        if (now < warnAt)
            return 0;
        if (now >= lastAt || lastAt <= warnAt)
            return 1;

        return Math.Clamp((now - warnAt).TotalHours / (lastAt - warnAt).TotalHours, 0, 1);
    }

    /// <summary>
    /// Opacity for a due-by window: open opacity until warn%, then lerp to last-chance,
    /// full last opacity in the final N hours (and after due-by). Same timing as color.
    /// </summary>
    static double WindowProgressOpacity(
        AppSettings settings, DateTime start, DateTime dueBy, DateTime now, double beforeOpen)
    {
        var open = Math.Clamp(
            settings.ClockWindowOpenOpacity <= 0 ? beforeOpen : settings.ClockWindowOpenOpacity, 0.05, 1);
        var last = Math.Clamp(
            settings.ClockWindowLastOpacity <= 0 ? beforeOpen : settings.ClockWindowLastOpacity, 0.05, 1);
        if (now < start)
            return Math.Clamp(beforeOpen, 0.05, 1);
        var t = WindowProgressAmount(settings, start, dueBy, now);
        return open + (last - open) * t;
    }

    static string MarkColorHex(
        AppSettings settings,
        (string Title, double HoursAhead, double? DueByHoursAhead, string Kind, DateTime At, DateTime? DueBy, double? UsedPercent, double Opacity) item,
        DateTime now)
    {
        var due = ClockItemIsDue(item);
        var fallback = TypeColorHex(settings, item.Kind, due, item.UsedPercent);
        if (item.DueBy is not DateTime dueBy || dueBy <= item.At)
            return fallback;
        return WindowProgressColorHex(settings, item.At, dueBy, now, fallback);
    }

    // Drawn on Marks so they stay above wedges and the second hand.
    void DrawNowMark(double cx, double cy, double rim, AppSettings settings)
    {
        var inset = Math.Max(6, rim * 0.07);
        var half = Math.Max(3.5, rim * 0.03);
        var fill = BrushFromHex(ColorText.NormalizeOr(settings.ClockColorCaret, FallbackCaret), 255);
        // Same caret at Now (top) and the three quarter turns.
        for (var i = 0; i < 4; i++)
        {
            var turn = i * Math.PI * 0.5;
            var tip = PolarPoint(cx, cy, rim - inset, turn);
            var bx = cx + rim * Math.Sin(turn);
            var by = cy - rim * Math.Cos(turn);
            var tx = -Math.Cos(turn) * half;
            var ty = -Math.Sin(turn) * half;
            Marks.Children.Add(new Polygon
            {
                Points =
                [
                    tip,
                    new Point(bx + tx, by + ty),
                    new Point(bx - tx, by - ty)
                ],
                Fill = fill,
                IsHitTestVisible = false
            });
        }
    }

    void UpdateHoverTip()
    {
        // Don't tear down the tip while wedges are being rebuilt.
        if (_buildingFace)
            return;
        if (!_tipWarmed || _hotspots.Count == 0 || !NativeMethods.TryGetCursorInWindow(this, out var winPt))
        {
            HideTip();
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursorPx))
        {
            HideTip();
            return;
        }

        // Same pixel: skip hit-test / layout. Still re-assert z-order so the topmost
        // clock cannot cover an already-visible tip.
        if (cursorPx.X == _tipCursorX && cursorPx.Y == _tipCursorY)
        {
            if (_tipVisible)
                NativeMethods.RaiseTipZOrder(_tipWindow);
            return;
        }
        _tipCursorX = cursorPx.X;
        _tipCursorY = cursorPx.Y;

        Point facePt;
        try
        {
            facePt = TranslatePoint(winPt, Face);
        }
        catch (InvalidOperationException)
        {
            HideTip();
            return;
        }

        var tips = new List<string>();
        foreach (var spot in _hotspots)
        {
            if (!spot.Contains(facePt))
                continue;
            if (tips.Exists(t => string.Equals(t, spot.Tip, StringComparison.Ordinal)))
                continue;
            tips.Add(spot.Tip);
        }

        if (tips.Count == 0)
        {
            HideTip();
            return;
        }

        var tip = tips.Count == 1 ? tips[0] : string.Join("\n\n", tips);
        if (!string.Equals(_openTip, tip, StringComparison.Ordinal))
        {
            ApplyTipText(tip);
            _openTip = tip;
        }

        _tipChrome.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _tipChrome.DesiredSize;

        var area = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point(cursorPx.X, cursorPx.Y)).WorkingArea;
        var preferRight = cursorPx.X < area.Left + area.Width * 0.5;
        var preferDown = cursorPx.Y < area.Top + area.Height * 0.5;

        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        var tipW = size.Width * sx;
        var tipH = size.Height * sy;
        var gapX = 14 * sx;
        var gapY = 18 * sy;

        // Prefer toward monitor center — clamp to the working area; do not flip.
        var px = preferRight ? cursorPx.X + gapX : cursorPx.X - tipW - gapX * (8.0 / 14.0);
        var py = preferDown ? cursorPx.Y + gapY : cursorPx.Y - tipH - gapY * (8.0 / 18.0);
        px = Math.Clamp(px, area.Left, Math.Max(area.Left, area.Right - tipW));
        py = Math.Clamp(py, area.Top, Math.Max(area.Top, area.Bottom - tipH));

        _tipWindow.Left = px / sx;
        _tipWindow.Top = py / sy;
        _tipWindow.Opacity = 1;
        // Z-order only (no exstyle/FRAMECHANGED) — keeps tip above this Topmost clock.
        NativeMethods.RaiseTipZOrder(_tipWindow);
        _tipVisible = true;
    }

    void HideTip()
    {
        _openTip = null;
        _tipCursorX = int.MinValue;
        _tipCursorY = int.MinValue;
        if (!_tipVisible && _tipWindow.Opacity <= 0)
            return;
        _tipBody.Children.Clear();
        _tipWindow.Opacity = 0;
        _tipWindow.Left = -32000;
        _tipWindow.Top = -32000;
        _tipVisible = false;
    }

    /// <summary>
    /// Tip text is "title\n  details" per item (blank line between hotspot groups).
    /// Render as title + indented wrapping details so wrap keeps the indent.
    /// </summary>
    void ApplyTipText(string tip)
    {
        _tipBody.Children.Clear();
        string? title = null;
        var details = new StringBuilder();
        var gapBeforeNext = false;

        void Flush()
        {
            if (title == null && details.Length == 0)
                return;
            if (gapBeforeNext && _tipBody.Children.Count > 0)
                _tipBody.Children.Add(new FrameworkElement { Height = 8 });
            gapBeforeNext = false;

            if (!string.IsNullOrEmpty(title))
            {
                _tipBody.Children.Add(new TextBlock
                {
                    Text = title,
                    Foreground = TipTextBrush,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 280
                });
            }

            if (details.Length > 0)
            {
                _tipBody.Children.Add(new TextBlock
                {
                    Text = details.ToString(),
                    Foreground = TipTextBrush,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 2, 0, 0),
                    MaxWidth = 268
                });
            }

            title = null;
            details.Clear();
        }

        foreach (var raw in tip.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0)
            {
                Flush();
                gapBeforeNext = true;
                continue;
            }

            if (raw.StartsWith("  ", StringComparison.Ordinal) || raw.StartsWith('\t'))
            {
                var part = raw.TrimStart();
                if (details.Length > 0)
                    details.Append(' ');
                details.Append(part);
                continue;
            }

            // New item title — flush previous title/details first.
            Flush();
            title = raw;
        }

        Flush();
    }

    void PlaceCurrentTime(
        string text, Brush fill, double fontSize, FontWeight weight,
        double cx, double cy, double nearChevron, double pos, AppSettings settings)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(
            SystemFonts.MessageFontFamily, FontStyles.Normal, weight, FontStretches.Normal);
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White,
            dpi);

        double height = ft.Height;
        var nearCenter = cy - height / 2;
        var top = nearChevron + (nearCenter - nearChevron) * pos;
        var geo = ft.BuildGeometry(new Point(0, 0));
        geo.Freeze();

        if (settings.ClockTimeOutline)
        {
            // Widen the glyph edges into an outline ring, then paint the fill on top.
            // (Stroking the same path with Fill makes thick outlines blob into solid shapes.)
            var strength = Math.Clamp(settings.ClockTimeOutlineStrength, 1, 6);
            var outlineBrush = BrushFromHex(
                ColorText.NormalizeOr(settings.ClockColorTimeOutline, "#000000"), 255);
            var pen = new Pen(outlineBrush, strength)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            var outlineGeo = geo.GetWidenedPathGeometry(pen);
            outlineGeo.Freeze();
            var left = CanvasLeftCentered(cx, geo, outlineGeo);

            var outlinePath = new System.Windows.Shapes.Path
            {
                Data = outlineGeo,
                Fill = outlineBrush,
                Stroke = null,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(outlinePath, left);
            Canvas.SetTop(outlinePath, top);
            Marks.Children.Add(outlinePath);

            var fillPath = new System.Windows.Shapes.Path
            {
                Data = geo,
                Fill = fill,
                Stroke = null,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(fillPath, left);
            Canvas.SetTop(fillPath, top);
            Marks.Children.Add(fillPath);
            return;
        }

        var leftPlain = CanvasLeftCentered(cx, geo);
        var fillOnly = new System.Windows.Shapes.Path
        {
            Data = geo,
            Fill = fill,
            Stroke = null,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(fillOnly, leftPlain);
        Canvas.SetTop(fillOnly, top);
        Marks.Children.Add(fillOnly);
    }

    /// <summary>Canvas.Left so the ink (and optional outline) is centered on <paramref name="cx"/>.</summary>
    static double CanvasLeftCentered(double cx, Geometry geo, Geometry? outline = null)
    {
        var bounds = geo.Bounds;
        if (outline != null && !outline.Bounds.IsEmpty)
            bounds = Rect.Union(bounds, outline.Bounds);
        if (bounds.IsEmpty || bounds.Width <= 0)
            return cx;
        return cx - (bounds.X + bounds.Width * 0.5);
    }

    // Fixed vertical Now hand — tapered needle pointing straight up.
    void DrawHourHand(double cx, double cy, double rim, AppSettings settings)
    {
        var frac = Math.Clamp(settings.ClockHourHandLength, 0.01, 0.95);
        var len = rim * frac;
        var fill = BrushFromHex(ColorText.NormalizeOr(settings.ClockColorHourHand, "#E8EAED"), 255);
        // Slightly darker outline for edge definition on any face color.
        var outline = BrushFromHex(ColorText.NormalizeOr(settings.ClockColorHourHand, "#E8EAED"), 0x55);

        var hubOuter = Math.Max(8.0, rim * 0.07);
        var hubInner = hubOuter * 0.45;
        var tipY = cy - len;
        var neckY = cy - len * 0.22;
        // Seat the blade on the hub so there's no gap at the pivot.
        var baseY = cy - hubOuter * 0.15;
        var halfBase = Math.Max(3.2, rim * 0.028);
        var halfNeck = Math.Max(1.4, rim * 0.012);

        // Soft under-glow / outline (slightly larger).
        var outlinePoly = new Polygon
        {
            Points =
            [
                new Point(cx, tipY - 1),
                new Point(cx - halfNeck - 0.8, neckY),
                new Point(cx - halfBase - 1.1, baseY),
                new Point(cx, cy),
                new Point(cx + halfBase + 1.1, baseY),
                new Point(cx + halfNeck + 0.8, neckY)
            ],
            Fill = outline,
            IsHitTestVisible = false
        };
        Face.Children.Add(outlinePoly);

        // Main tapered blade — tapers into the hub center.
        var blade = new Polygon
        {
            Points =
            [
                new Point(cx, tipY),
                new Point(cx - halfNeck, neckY),
                new Point(cx - halfBase, baseY),
                new Point(cx, cy),
                new Point(cx + halfBase, baseY),
                new Point(cx + halfNeck, neckY)
            ],
            Fill = fill,
            IsHitTestVisible = false
        };
        Face.Children.Add(blade);

        // Hub: outer ring + inner cap so it reads as a proper hand pivot.
        var ring = new Ellipse
        {
            Width = hubOuter,
            Height = hubOuter,
            Stroke = fill,
            StrokeThickness = Math.Max(1.4, hubOuter * 0.12),
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Face.Children.Add(ring);
        Canvas.SetLeft(ring, cx - hubOuter / 2);
        Canvas.SetTop(ring, cy - hubOuter / 2);

        var cap = new Ellipse
        {
            Width = hubInner,
            Height = hubInner,
            Fill = fill,
            IsHitTestVisible = false
        };
        Face.Children.Add(cap);
        Canvas.SetLeft(cap, cx - hubInner / 2);
        Canvas.SetTop(cap, cy - hubInner / 2);
    }

    void EnsureSecondHand(AppSettings settings)
    {
        var brush = BrushFromHex(ColorText.NormalizeOr(settings.ClockColorSecondHand, FallbackSecond), 255);
        if (_secondHand != null && _secondHub != null && Hands.Children.Contains(_secondHand))
        {
            _secondHand.Stroke = brush;
            _secondHub.Fill = brush;
            return;
        }

        Hands.Children.Clear();
        _secondHand = new Line
        {
            Stroke = brush,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        _secondHub = new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = brush,
            IsHitTestVisible = false
        };
        Hands.Children.Add(_secondHand);
        Hands.Children.Add(_secondHub);
    }

    void UpdateSecondHand(DateTime now)
    {
        if (_secondHand == null || _secondHub == null || _handRadius < 1)
            return;
        // auto model: standard clock angles — :00 ↑, :15 →, :30 ↓, :45 ← (clockwise from 12)
        // Use ms-into-minute so :00.000 is exactly vertical (Sin=0, Cos=1 → Y decreases).
        var turn = SecondHandTurn(now);
        var handFrac = Math.Clamp(AppHost.Instance.Data.Settings.ClockSecondHandLength, 0.01, 0.95);
        var len = _handRadius * handFrac;
        var sin = Math.Sin(turn);
        var cos = Math.Cos(turn);
        _secondHand.X1 = _handCx;
        _secondHand.Y1 = _handCy;
        _secondHand.X2 = _handCx + len * sin;
        _secondHand.Y2 = _handCy - len * cos;
        Canvas.SetLeft(_secondHub, _handCx - 2.5);
        Canvas.SetTop(_secondHub, _handCy - 2.5);
    }

        // auto model: radians clockwise from 12, matching the second hand.
        // Unwrapped so a 1/1440 ring does not snap back 0.25° at :00.
    static double SecondHandTurn(DateTime now) =>
        now.TimeOfDay.TotalMinutes * (Math.PI * 2.0);

    /// <summary>
    /// Concentric compound planets under the face, locked to the selected tooth progression.
    /// Clipped to the clock rim so compound bodies that spill past the ring are masked off.
    /// Planet Angle = align(carrier) + ω_p·t so placement drives mesh phase.
    /// </summary>
    // auto model
    void AddConcentricPlanetary(
        double cx, double cy, double rim, double hostW, double hostH,
        AppSettings settings, DateTime now)
    {
        _gearRots.Clear();
        _gearSecScale.Clear();
        _gearBaseDeg.Clear();

        if (!ClockGearGeometry.TryGetProgression(settings, out var prog))
            return;

        var wantCounts = settings.ClockGearPlanetCounts ?? [];
        var wantOffs = settings.ClockGearPlanetOffsetsDeg ?? [];
        var wantIdlers = settings.ClockGearIdlerOffsetsDeg ?? [];
        if (!ClockGearGeometry.TrySolveTrain(
                prog, rim, wantCounts, wantOffs, wantIdlers, out var solves, out var assembleWhy)
            || solves.Count < prog.StageCount)
        {
            ShowProgressionFail(cx, cy, rim, settings, $"{prog.Label}: {assembleWhy}");
            return;
        }

        solves = ClockGearGeometry.WithDrawnCompounds(solves, settings.ClockGearCompoundScale);

        var layout = solves[0].Layout;
        var planetCount = solves[0].PlanetCount;
        var offsetDeg = solves[0].PlanetOffsetDeg;
        var idlerOffsetDeg = solves[0].IdlerOffsetDeg;
        var inner = solves[0].Fit;
        layout = ClockGearGeometry.ApplyCompoundMul(
            layout, inner.Teeth, planetCount, offsetDeg, idlerOffsetDeg,
            CompoundMulAt(settings, 0), out idlerOffsetDeg);
        var ringTeeth = ClockGearGeometry.FaceRingTeeth;
        var planetTeeth = ClockGearGeometry.FacePlanetTeeth;
        var hourStride = ClockGearGeometry.HourAccentStride(ringTeeth, _gearHorizonHours);
        var trainOp = Math.Clamp(settings.ClockGearOpacity, 0.05, 1);
        var ringOp = Math.Clamp(settings.ClockGearRingOpacity, 0.05, 1);
        const byte solidA = 255;
        var ringHex = ColorText.NormalizeOr(settings.ClockGearColor, "#7A8088");
        var compoundHex = DrivePartHex(settings.ClockGearCompoundColor, DarkenHex(ringHex, 0.55));
        var planetHex = DrivePartHex(settings.ClockGearPlanetColor, ringHex);
        var idlerHex = DrivePartHex(settings.ClockGearIdlerColor, ringHex);
        var sunHex = DrivePartHex(settings.ClockGearSunColor, DarkenHex(ringHex, 0.72));
        var fill = BrushFromHex(ringHex, solidA);
        // Same color as the outer rim teeth; intermediate stacks use ClockGearRingOpacity.
        var innerDimFill = BrushFromHex(ringHex, solidA);
        var planetFill = BrushFromHex(planetHex, solidA);
        var compoundFill = BrushFromHex(compoundHex, solidA);
        ClockGearCompoundStage CompoundAt(int stage) =>
            ClockGearCompoundStage.At(settings, stage);
        var hourFill = BrushFromHex(
            ColorText.NormalizeOr(settings.ClockColorMajorTick, FallbackMajorTick), 0xCC);

        var ringSpeed = ClockGearGeometry.FaceRingSecScale(_gearHorizonHours);
        var carrierSecs = settings.ClockGearAutoCarrier != false
            ? ClockGearGeometry.SolveEqualCarrierSecScales(
                solves, ringSpeed, 1.0, settings.ClockGearCarrierDirs,
                Math.Max(0, settings.ClockGearCarrierMinDegPerMin) / 360.0,
                settings.ClockGearCarrierPinned, settings.ClockGearCarrierDegPerMin)
            : ManualCarrierSecs(settings, solves.Count);
        double C(int stage) =>
            stage >= 0 && stage < carrierSecs.Length
                ? carrierSecs[stage]
                : CarrierSec(settings, stage);
        var carrierWarn = ClockGearGeometry.DiagnoseCarrierLocks(
            solves, ringSpeed, 1.0, carrierSecs,
            settings.ClockGearAutoCarrier != false,
            settings.ClockGearCarrierDirs,
            Math.Max(0, settings.ClockGearCarrierMinDegPerMin) / 360.0,
            settings.ClockGearCarrierPinned, settings.ClockGearCarrierDegPerMin);
        void PaintCarrierWarn()
        {
            if (!string.IsNullOrWhiteSpace(carrierWarn))
                DrawCarrierWarn(cx, cy, layout, settings, carrierWarn);
        }
        var c0 = C(0);
        var carriageBrush = BrushFromHex(
            ColorText.NormalizeOr(settings.ClockGearCarriageColor, "#C4A882"), solidA);
        // Carrier frame: planets/idlers use relative ω (mid-layer adds ω_c).
        // Sun is not on that layer, so it takes the inertial rate.
        CarrierFrameSpins(
            ringSpeed, c0, ringTeeth, planetTeeth,
            layout.CompoundTeeth, layout.IdlerTeeth, inner.Teeth,
            layout.MeshCompoundTeeth, layout.MeshIdlerTeeth,
            lockSunRpm: prog.StageCount == 1,
            out var planetSpin, out var idlerSpin, out var innerSpin);
        var compoundTeeth = layout.MeshCompoundTeeth;
        var idlerTeeth = layout.MeshIdlerTeeth;

        var idlerCount = 1;
        var idlerFill = BrushFromHex(idlerHex, solidA);
        var innerBand = ClockGearGeometry.ResolveInnerRingBand(inner);
        var sunFill = BrushFromHex(sunHex, solidA);
        var innerFill = prog.StageCount == 1 ? sunFill : innerDimFill;

        // Mid-train under alerts; plain outer teeth on OuterDrive under Face wedges;
        // colored hour accents on HourAccents above the wedges.
        var clip = new EllipseGeometry(new Point(cx, cy), rim, rim);
        clip.Freeze();
        var gearLayer = new Canvas
        {
            Width = hostW,
            Height = hostH,
            Clip = clip,
            IsHitTestVisible = false,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        Gears.Children.Add(gearLayer);

        var outerLayer = new Canvas
        {
            Width = hostW,
            Height = hostH,
            Clip = clip,
            IsHitTestVisible = false,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        OuterDrive.Children.Add(outerLayer);

        var hourLayer = new Canvas
        {
            Width = hostW,
            Height = hostH,
            Clip = clip,
            IsHitTestVisible = false,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        HourAccents.Children.Add(hourLayer);

        Path AddRotated(
            Geometry geo, Brush? fillBrush, double secScale, double baseDeg,
            double rotCx, double rotCy, Brush? strokeBrush = null, double strokeThickness = 0,
            Canvas? host = null)
        {
            host ??= gearLayer;
            var rot = new RotateTransform(baseDeg, rotCx, rotCy);
            var path = new Path
            {
                Data = geo,
                Fill = fillBrush,
                Stroke = strokeBrush,
                StrokeThickness = strokeThickness,
                RenderTransform = rot,
                IsHitTestVisible = false,
                SnapsToDevicePixels = false
            };
            host.Children.Add(path);
            _gearRots.Add(rot);
            _gearSecScale.Add(secScale);
            _gearBaseDeg.Add(baseDeg);
            return path;
        }

        void AddCompound(
            Geometry[] parts, Brush fill, double secScale, double baseDeg,
            double rotCx, double rotCy, Canvas host)
        {
            foreach (var part in parts)
                AddRotated(part, fill, secScale, baseDeg, rotCx, rotCy, host: host);
        }

        Canvas MakeCarrierLayer(
            Geometry clipGeo, double carrierSec, bool behind,
            Canvas? parent = null, bool dim = true)
        {
            var layer = new Canvas
            {
                Width = hostW,
                Height = hostH,
                Clip = clipGeo,
                Opacity = dim ? trainOp : 1,
                IsHitTestVisible = false,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false
            };
            parent ??= gearLayer;
            if (behind)
                parent.Children.Insert(0, layer);
            else
                parent.Children.Add(layer);
            SpinCarrier(layer, cx, cy, carrierSec);
            return layer;
        }

        // Clip frame: tooth-hole mask that spins with a ring. Nest carrier layers inside
        // with relative secScale so compounds keep inertial spin while holes track the ring.
        Canvas MakeClipFrame(
            Geometry clipGeo, double spinSec, bool behind, Canvas? parent = null)
        {
            var layer = new Canvas
            {
                Width = hostW,
                Height = hostH,
                Clip = clipGeo,
                IsHitTestVisible = false,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false
            };
            parent ??= gearLayer;
            if (behind)
                parent.Children.Insert(0, layer);
            else
                parent.Children.Add(layer);
            SpinCarrier(layer, cx, cy, spinSec);
            return layer;
        }

        Canvas MakeCarrierOn(
            Canvas parent, double relativeSec, bool dim = true)
        {
            var layer = new Canvas
            {
                Width = hostW,
                Height = hostH,
                Opacity = dim ? trainOp : 1,
                IsHitTestVisible = false,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false
            };
            parent.Children.Add(layer);
            SpinCarrier(layer, cx, cy, relativeSec);
            return layer;
        }

        Canvas MakeRingStack()
        {
            // Stack stays Opacity 1 so planets/idlers parented here can use trainOp
            // without multiplying with ClockGearRingOpacity. Dim ring paths instead.
            var stack = new Canvas
            {
                Width = hostW,
                Height = hostH,
                Opacity = 1,
                IsHitTestVisible = false,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false
            };
            gearLayer.Children.Add(stack);
            return stack;
        }

        void DimRingPath(UIElement path, bool dimRing)
        {
            if (dimRing)
                path.Opacity = ringOp;
        }

        // Annulus: draw only for innerR ≤ r ≤ outerR.
        Geometry StageClip(double outerR, double innerR = 0)
        {
            outerR = Math.Max(2, outerR);
            innerR = Math.Max(0, innerR);
            if (innerR < 2 || innerR + 1.5 >= outerR)
            {
                var g = new EllipseGeometry(new Point(cx, cy), outerR, outerR);
                g.Freeze();
                return g;
            }
            var clip = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(cx, cy), outerR, outerR),
                new EllipseGeometry(new Point(cx, cy), innerR, innerR));
            clip.Freeze();
            return clip;
        }

        Geometry ExcludeSolid(Geometry allow, Geometry solid)
        {
            var g = new CombinedGeometry(GeometryCombineMode.Exclude, allow, solid);
            g.Freeze();
            return g;
        }

        Geometry RotateGeo(Geometry geo, double baseDeg)
        {
            if (Math.Abs(baseDeg) < 1e-9)
                return geo;
            var clone = geo.Clone();
            clone.Transform = new RotateTransform(baseDeg, cx, cy);
            clone.Freeze();
            return clone;
        }

        var deg = 180.0 / Math.PI;
        var showSpinMarks = settings.ClockGearDebugLabels;
        var spinMarkStroke = showSpinMarks
            ? BrushFromHex(ColorText.NormalizeOr(settings.ClockColorMajorTick, FallbackMajorTick), 0xEE)
            : null;

        // Split hollow ring: exterior teeth under idlers; solid rim once on top.
        var refAng = ClockGearGeometry.PlanetCarrierAngle(0, planetCount, ringTeeth, offsetDeg);
        var refAlign = ClockGearGeometry.PlanetAlignAngle(refAng, planetTeeth, ringTeeth);
        var refSeat = ClockGearGeometry.IdlerSeatAngle(refAng, 0, idlerCount, idlerOffsetDeg);
        var refIdlerAlign = ClockGearGeometry.IdlerAlignAngle(
            refSeat, refAlign, compoundTeeth, idlerTeeth);
        var refPx = cx + layout.CarrierRadius * Math.Cos(refAng);
        var refPy = cy + layout.CarrierRadius * Math.Sin(refAng);
        var refIx = refPx + layout.IdlerMeshRadius * Math.Cos(refSeat);
        var refIy = refPy + layout.IdlerMeshRadius * Math.Sin(refSeat);
        var idlerToSun = Math.Atan2(cy - refIy, cx - refIx);
        var innerAlignDeg = ClockGearGeometry.IdlerAlignAngle(
            idlerToSun, refIdlerAlign, idlerTeeth, inner.Teeth) * deg;

        // Painter: carriage + large compound, then rings, then idlers/planet, then sun.
        // Compounds sit in the mid annulus (including tooth bands) but Exclude the solid
        // tooth/rim outlines; clip frames spin with each ring so gaps stay open while
        // solid teeth mask. Relative carrier spin keeps compound ω_inertial = c0.
        var midAllow = StageClip(rim, innerBand.OuterRoot);
        var outerToothSolid = ClockGearGeometry.BuildInternalRing(cx, cy, layout, ringTeeth);
        var outerPunch = ExcludeSolid(midAllow, outerToothSolid);
        var hollowExtSolid = RotateGeo(
            ClockGearGeometry.BuildInnerRingExteriorTeeth(cx, cy, layout, inner),
            innerAlignDeg);
        var outerClipFrame = MakeClipFrame(outerPunch, ringSpeed, behind: false);
        var hollowExtFrame = MakeClipFrame(
            ExcludeSolid(StageClip(rim), hollowExtSolid),
            innerSpin - ringSpeed, behind: false, parent: outerClipFrame);
        var backLayer = MakeCarrierOn(hollowExtFrame, c0 - innerSpin);

        var seats0 = new List<(double Px, double Py, (double X, double Y)[] Idlers, double Ang, double AlignDeg)>(planetCount);
        for (var i = 0; i < planetCount; i++)
        {
            var ang = ClockGearGeometry.PlanetCarrierAngle(i, planetCount, ringTeeth, offsetDeg);
            var px = cx + layout.CarrierRadius * Math.Cos(ang);
            var py = cy + layout.CarrierRadius * Math.Sin(ang);
            var alignRad = ClockGearGeometry.PlanetAlignAngle(ang, planetTeeth, ringTeeth);
            var idlerPts = new (double X, double Y)[idlerCount];
            for (var j = 0; j < idlerCount; j++)
            {
                var seatJ = ClockGearGeometry.IdlerSeatAngle(ang, j, idlerCount, idlerOffsetDeg);
                idlerPts[j] = (
                    px + layout.IdlerMeshRadius * Math.Cos(seatJ),
                    py + layout.IdlerMeshRadius * Math.Sin(seatJ));
            }
            seats0.Add((px, py, idlerPts, ang, alignRad * deg));
        }
        var carriage0 = AddCarriageTracks(
            backLayer, cx, cy, layout,
            seats0.ConvertAll(s => (s.Px, s.Py, s.Idlers)), carriageBrush, settings, 0);

        for (var i = 0; i < seats0.Count; i++)
        {
            var (px, py, _, _, alignDeg) = seats0[i];
            AddCompound(
                ClockGearGeometry.BuildCompoundGear(px, py, layout, CompoundAt(0)),
                compoundFill, planetSpin, alignDeg, px, py, backLayer);
            if (showSpinMarks && spinMarkStroke != null)
            {
                var r = layout.CompoundTipR;
                var diameter = new LineGeometry(new Point(px - r, py), new Point(px + r, py));
                diameter.Freeze();
                AddRotated(diameter, null, planetSpin, alignDeg, px, py, spinMarkStroke, 1.4, backLayer);
            }
        }

        AddRotated(
            ClockGearGeometry.BuildInternalRing(cx, cy, layout, ringTeeth),
            fill, ringSpeed, 0, cx, cy, host: outerLayer);

        AddRotated(
            ClockGearGeometry.BuildInternalHourToothAccents(
                cx, cy, layout, ringTeeth, hourStride),
            hourFill, ringSpeed, 0, cx, cy, host: hourLayer);

        var dimRing0 = innerFill != sunFill;
        var stack0 = MakeRingStack();
        DimRingPath(AddRotated(
            ClockGearGeometry.BuildInnerRingExteriorTeeth(cx, cy, layout, inner),
            innerFill, innerSpin, innerAlignDeg, cx, cy, host: stack0), dimRing0);

        var frontLayer = MakeCarrierLayer(
            StageClip(rim, innerBand.OuterRoot), c0, behind: false, parent: stack0, dim: true);
        if (carriage0 != null)
            AddCarriageUnderIdlers(frontLayer, carriage0,
                seats0.ConvertAll(s => (s.Px, s.Py, s.Idlers)), layout.IdlerTipR, carriageBrush);
        for (var i = 0; i < seats0.Count; i++)
        {
            var (px, py, idlerPts, ang, alignDeg) = seats0[i];
            var alignRad = alignDeg * (Math.PI / 180.0);
            for (var j = 0; j < idlerCount; j++)
            {
                var seat = ClockGearGeometry.IdlerSeatAngle(ang, j, idlerCount, idlerOffsetDeg);
                var ix = idlerPts[j].X;
                var iy = idlerPts[j].Y;
                var idlerAlignDeg = ClockGearGeometry.IdlerAlignAngle(
                    seat, alignRad, compoundTeeth, idlerTeeth) * deg;
                AddRotated(
                    ClockGearGeometry.BuildIdlerGear(ix, iy, layout),
                    idlerFill, idlerSpin, idlerAlignDeg, ix, iy, host: frontLayer);
            }

            AddRotated(
                ClockGearGeometry.BuildPlanetGear(px, py, layout, planetTeeth),
                planetFill, planetSpin, alignDeg, px, py, host: frontLayer);
        }
        AddCarriagePins(
            frontLayer, layout,
            seats0.ConvertAll(s => (s.Px, s.Py, s.Idlers)), carriageBrush, settings, 0);

        DimRingPath(AddRotated(
            ClockGearGeometry.BuildInnerRingMaskCore(cx, cy, layout, inner),
            innerFill, innerSpin, innerAlignDeg, cx, cy, host: stack0), dimRing0);
        if (showSpinMarks && spinMarkStroke != null)
        {
            var diameter = new LineGeometry(
                new Point(cx - inner.TipR, cy), new Point(cx + inner.TipR, cy));
            diameter.Freeze();
            AddRotated(diameter, null, innerSpin, innerAlignDeg, cx, cy, spinMarkStroke, 1.4);
        }

        // --- Stage 2: host = first inner's interior; locked compound + sun teeth ---
        var planetCount1 = solves[1].PlanetCount;
        var offset1Deg = solves[1].PlanetOffsetDeg;
        var layout2 = solves[1].Layout;
        var twoStage = prog.StageCount == 2;
        var idlerOffset1Deg = solves[1].IdlerOffsetDeg;
        var inner2 = solves[1].Fit;
        layout2 = ClockGearGeometry.ApplyCompoundMul(
            layout2, inner2.Teeth, planetCount1, offset1Deg, idlerOffset1Deg,
            CompoundMulAt(settings, 1), out idlerOffset1Deg);
        var inner2Band = ClockGearGeometry.ResolveInnerRingBand(inner2);
        var compoundTeeth2 = layout2.MeshCompoundTeeth;
        var idlerTeeth2 = layout2.MeshIdlerTeeth;
        var planetTeeth2 = layout2.PlanetTeeth;
        var hostTeeth2 = layout2.RingTeeth;
        var c1 = C(1);
        CarrierFrameSpins(
            innerSpin, c1, hostTeeth2, planetTeeth2,
            layout2.CompoundTeeth, layout2.IdlerTeeth, inner2.Teeth,
            layout2.MeshCompoundTeeth, layout2.MeshIdlerTeeth,
            lockSunRpm: twoStage,
            out var planetSpin2, out var idlerSpin2, out var inner2Spin);
        var inner2Fill = prog.StageCount == 2 ? sunFill : innerDimFill;

        // Map PlanetCarrierAngle (tip0 @ -π/2) into stage-1 interior tooth frame.
        var hostPitch2 = Math.PI * 2.0 / hostTeeth2;
        var frameDelta2 = innerAlignDeg * (Math.PI / 180.0) + Math.PI / 2.0 + hostPitch2 * 0.5;

        var local0 = ClockGearGeometry.PlanetCarrierAngle(0, planetCount1, hostTeeth2, offset1Deg);
        var world0 = local0 + frameDelta2;
        var align0 = ClockGearGeometry.PlanetAlignAngle(local0, planetTeeth2, hostTeeth2) + frameDelta2;
        var seat0 = ClockGearGeometry.IdlerSeatAngle(world0, 0, 1, idlerOffset1Deg);
        var idlerAlign0 = ClockGearGeometry.IdlerAlignAngle(
            seat0, align0, compoundTeeth2, idlerTeeth2);
        var p0x = cx + layout2.CarrierRadius * Math.Cos(world0);
        var p0y = cy + layout2.CarrierRadius * Math.Sin(world0);
        var i0x = p0x + layout2.IdlerMeshRadius * Math.Cos(seat0);
        var i0y = p0y + layout2.IdlerMeshRadius * Math.Sin(seat0);
        var idlerToSun2 = Math.Atan2(cy - i0y, cx - i0x);
        var inner2AlignDeg = ClockGearGeometry.IdlerAlignAngle(
            idlerToSun2, idlerAlign0, idlerTeeth2, inner2.Teeth) * deg;

        // Stage-2: radial band includes tooth valleys; punch host interior teeth + next
        // exterior crowns (clip frames track each ring). Solid rims stay outside the band.
        var radial2 = StageClip(innerBand.InnerRoot, inner2Band.OuterRoot);
        var hostInteriorSolid = new CombinedGeometry(
            GeometryCombineMode.Intersect,
            RotateGeo(
                ClockGearGeometry.BuildInnerRingMaskCore(cx, cy, layout, inner),
                innerAlignDeg),
            new EllipseGeometry(new Point(cx, cy), innerBand.InnerRoot, innerBand.InnerRoot));
        hostInteriorSolid.Freeze();
        var hostPunch2 = ExcludeSolid(radial2, hostInteriorSolid);
        var nextExt2 = RotateGeo(
            ClockGearGeometry.BuildInnerRingExteriorTeeth(cx, cy, layout2, inner2),
            inner2AlignDeg);
        var hostClip2 = MakeClipFrame(hostPunch2, innerSpin, behind: true);
        var nextClip2 = MakeClipFrame(
            ExcludeSolid(StageClip(rim), nextExt2),
            inner2Spin - innerSpin, behind: false, parent: hostClip2);
        var backLayer2 = MakeCarrierOn(nextClip2, c1 - inner2Spin);

        var seats2 = new List<(double Px, double Py, (double X, double Y)[] Idlers)>(planetCount1);
        for (var i = 0; i < planetCount1; i++)
        {
            var localAng = ClockGearGeometry.PlanetCarrierAngle(i, planetCount1, hostTeeth2, offset1Deg);
            var ang = localAng + frameDelta2;
            var px = cx + layout2.CarrierRadius * Math.Cos(ang);
            var py = cy + layout2.CarrierRadius * Math.Sin(ang);
            var seat = ClockGearGeometry.IdlerSeatAngle(ang, 0, 1, idlerOffset1Deg);
            var ix = px + layout2.IdlerMeshRadius * Math.Cos(seat);
            var iy = py + layout2.IdlerMeshRadius * Math.Sin(seat);
            seats2.Add((px, py, [(ix, iy)]));
        }
        var carriage2 = AddCarriageTracks(backLayer2, cx, cy, layout2, seats2, carriageBrush, settings, 1);

        for (var i = 0; i < planetCount1; i++)
        {
            var localAng = ClockGearGeometry.PlanetCarrierAngle(i, planetCount1, hostTeeth2, offset1Deg);
            var ang = localAng + frameDelta2;
            var px = cx + layout2.CarrierRadius * Math.Cos(ang);
            var py = cy + layout2.CarrierRadius * Math.Sin(ang);
            var alignRad = ClockGearGeometry.PlanetAlignAngle(localAng, planetTeeth2, hostTeeth2) + frameDelta2;
            var alignDeg = alignRad * deg;
            AddCompound(
                ClockGearGeometry.BuildCompoundGear(px, py, layout2, CompoundAt(1)),
                compoundFill, planetSpin2, alignDeg, px, py, backLayer2);
            if (showSpinMarks && spinMarkStroke != null)
            {
                var r = layout2.CompoundTipR;
                var diameter = new LineGeometry(new Point(px - r, py), new Point(px + r, py));
                diameter.Freeze();
                AddRotated(diameter, null, planetSpin2, alignDeg, px, py, spinMarkStroke, 1.4, backLayer2);
            }
        }

        var dimRing2 = inner2Fill != sunFill;
        var stack2 = MakeRingStack();
        DimRingPath(AddRotated(
            ClockGearGeometry.BuildInnerRingExteriorTeeth(cx, cy, layout2, inner2),
            inner2Fill, inner2Spin, inner2AlignDeg, cx, cy, host: stack2), dimRing2);

        var frontLayer2 = MakeCarrierLayer(
            StageClip(innerBand.InnerRoot, inner2Band.OuterRoot), c1, behind: false,
            parent: stack2, dim: true);
        if (carriage2 != null)
            AddCarriageUnderIdlers(frontLayer2, carriage2, seats2, layout2.IdlerTipR, carriageBrush);
        for (var i = 0; i < planetCount1; i++)
        {
            var localAng = ClockGearGeometry.PlanetCarrierAngle(i, planetCount1, hostTeeth2, offset1Deg);
            var ang = localAng + frameDelta2;
            var px = cx + layout2.CarrierRadius * Math.Cos(ang);
            var py = cy + layout2.CarrierRadius * Math.Sin(ang);
            var alignRad = ClockGearGeometry.PlanetAlignAngle(localAng, planetTeeth2, hostTeeth2) + frameDelta2;
            var alignDeg = alignRad * deg;
            var seat = ClockGearGeometry.IdlerSeatAngle(ang, 0, 1, idlerOffset1Deg);
            var ix = px + layout2.IdlerMeshRadius * Math.Cos(seat);
            var iy = py + layout2.IdlerMeshRadius * Math.Sin(seat);
            var idlerAlignDeg2 = ClockGearGeometry.IdlerAlignAngle(
                seat, alignRad, compoundTeeth2, idlerTeeth2) * deg;
            AddRotated(
                ClockGearGeometry.BuildIdlerGear(ix, iy, layout2),
                idlerFill, idlerSpin2, idlerAlignDeg2, ix, iy, host: frontLayer2);
            AddRotated(
                ClockGearGeometry.BuildPlanetGear(px, py, layout2, planetTeeth2),
                planetFill, planetSpin2, alignDeg, px, py, host: frontLayer2);
        }
        AddCarriagePins(frontLayer2, layout2, seats2, carriageBrush, settings, 1);

        DimRingPath(AddRotated(
            ClockGearGeometry.BuildInnerRingMaskCore(cx, cy, layout2, inner2),
            inner2Fill, inner2Spin, inner2AlignDeg, cx, cy, host: stack2), dimRing2);
        if (showSpinMarks && spinMarkStroke != null)
        {
            var diameter = new LineGeometry(
                new Point(cx - inner2.TipR, cy), new Point(cx + inner2.TipR, cy));
            diameter.Freeze();
            AddRotated(diameter, null, inner2Spin, inner2AlignDeg, cx, cy, spinMarkStroke, 1.4);
        }

        if (twoStage)
        {
            if (showSpinMarks)
            {
                DrawGearReadout(cx, cy, layout, settings,
                [
                    $"{prog.Label}  last {inner2.Teeth}T locked ×1440",
                    $"inner1  {inner.Teeth}T  ×{inner.StageRatioFromOuter:0.##}  {(inner.AssemblyOk ? "OK" : inner.FailReason)}",
                    $"inner2  {inner2.Teeth}T  ×{inner2.StageRatioFromOuter:0.##}  {(inner2.AssemblyOk ? "OK" : inner2.FailReason)} locked",
                    $"cumul ×{inner.StageRatioFromOuter * inner2.StageRatioFromOuter:0.##}"
                ]);
                var anim = Math.Clamp(settings.ClockGearAnimSpeed, 1, 1440);
                DrawRpmReadout(cx, cy, layout, settings,
                [
                    $"outer ring: {FormatRpm(ringSpeed * anim)}",
                    $"1st inner ring: {FormatRpm(innerSpin * anim)}",
                    $"center (2nd ring): {FormatRpm(inner2Spin * anim)}"
                ]);
            }
            UpdatePlanetaryDrive(now);
            PaintCarrierWarn();
            return;
        }

        // --- Stage 3: host = 2nd inner's interior ---
        var planetCount2 = solves[2].PlanetCount;
        var offset2Deg = solves[2].PlanetOffsetDeg;
        var layout3 = solves[2].Layout;
        var idlerOffset2Deg = solves[2].IdlerOffsetDeg;
        var inner3 = solves[2].Fit;
        layout3 = ClockGearGeometry.ApplyCompoundMul(
            layout3, inner3.Teeth, planetCount2, offset2Deg, idlerOffset2Deg,
            CompoundMulAt(settings, 2), out idlerOffset2Deg);
        var inner3Band = ClockGearGeometry.ResolveInnerRingBand(inner3);
        var compoundTeeth3 = layout3.MeshCompoundTeeth;
        var idlerTeeth3 = layout3.MeshIdlerTeeth;
        var planetTeeth3 = layout3.PlanetTeeth;
        var hostTeeth3 = layout3.RingTeeth;
        var c2 = C(2);
        CarrierFrameSpins(
            inner2Spin, c2, hostTeeth3, planetTeeth3,
            layout3.CompoundTeeth, layout3.IdlerTeeth, inner3.Teeth,
            layout3.MeshCompoundTeeth, layout3.MeshIdlerTeeth,
            lockSunRpm: prog.StageCount == 3,
            out var planetSpin3, out var idlerSpin3, out var inner3Spin);
        var inner3Fill = prog.StageCount == 3 ? sunFill : innerDimFill;

        var hostPitch3 = Math.PI * 2.0 / hostTeeth3;
        var frameDelta3 = inner2AlignDeg * (Math.PI / 180.0) + Math.PI / 2.0 + hostPitch3 * 0.5;

        var local30 = ClockGearGeometry.PlanetCarrierAngle(0, planetCount2, hostTeeth3, offset2Deg);
        var world30 = local30 + frameDelta3;
        var align30 = ClockGearGeometry.PlanetAlignAngle(local30, planetTeeth3, hostTeeth3) + frameDelta3;
        var seat30 = ClockGearGeometry.IdlerSeatAngle(world30, 0, 1, idlerOffset2Deg);
        var idlerAlign30 = ClockGearGeometry.IdlerAlignAngle(
            seat30, align30, compoundTeeth3, idlerTeeth3);
        var p30x = cx + layout3.CarrierRadius * Math.Cos(world30);
        var p30y = cy + layout3.CarrierRadius * Math.Sin(world30);
        var i30x = p30x + layout3.IdlerMeshRadius * Math.Cos(seat30);
        var i30y = p30y + layout3.IdlerMeshRadius * Math.Sin(seat30);
        var idlerToSun3 = Math.Atan2(cy - i30y, cx - i30x);
        var inner3AlignDeg = ClockGearGeometry.IdlerAlignAngle(
            idlerToSun3, idlerAlign30, idlerTeeth3, inner3.Teeth) * deg;

        var radial3 = StageClip(inner2Band.InnerRoot, inner3Band.OuterRoot);
        var hostInteriorSolid3 = new CombinedGeometry(
            GeometryCombineMode.Intersect,
            RotateGeo(
                ClockGearGeometry.BuildInnerRingMaskCore(cx, cy, layout2, inner2),
                inner2AlignDeg),
            new EllipseGeometry(new Point(cx, cy), inner2Band.InnerRoot, inner2Band.InnerRoot));
        hostInteriorSolid3.Freeze();
        var hostPunch3 = ExcludeSolid(radial3, hostInteriorSolid3);
        var nextExt3 = RotateGeo(
            ClockGearGeometry.BuildInnerRingExteriorTeeth(cx, cy, layout3, inner3),
            inner3AlignDeg);
        var hostClip3 = MakeClipFrame(hostPunch3, inner2Spin, behind: true);
        var nextClip3 = MakeClipFrame(
            ExcludeSolid(StageClip(rim), nextExt3),
            inner3Spin - inner2Spin, behind: false, parent: hostClip3);
        var backLayer3 = MakeCarrierOn(nextClip3, c2 - inner3Spin);

        var seats3 = new List<(double Px, double Py, (double X, double Y)[] Idlers)>(planetCount2);
        for (var i = 0; i < planetCount2; i++)
        {
            var localAng = ClockGearGeometry.PlanetCarrierAngle(i, planetCount2, hostTeeth3, offset2Deg);
            var ang = localAng + frameDelta3;
            var px = cx + layout3.CarrierRadius * Math.Cos(ang);
            var py = cy + layout3.CarrierRadius * Math.Sin(ang);
            var seat = ClockGearGeometry.IdlerSeatAngle(ang, 0, 1, idlerOffset2Deg);
            var ix = px + layout3.IdlerMeshRadius * Math.Cos(seat);
            var iy = py + layout3.IdlerMeshRadius * Math.Sin(seat);
            seats3.Add((px, py, [(ix, iy)]));
        }
        var carriage3 = AddCarriageTracks(backLayer3, cx, cy, layout3, seats3, carriageBrush, settings, 2);

        for (var i = 0; i < planetCount2; i++)
        {
            var localAng = ClockGearGeometry.PlanetCarrierAngle(i, planetCount2, hostTeeth3, offset2Deg);
            var ang = localAng + frameDelta3;
            var px = cx + layout3.CarrierRadius * Math.Cos(ang);
            var py = cy + layout3.CarrierRadius * Math.Sin(ang);
            var alignRad = ClockGearGeometry.PlanetAlignAngle(localAng, planetTeeth3, hostTeeth3) + frameDelta3;
            var alignDeg = alignRad * deg;
            AddCompound(
                ClockGearGeometry.BuildCompoundGear(px, py, layout3, CompoundAt(2)),
                compoundFill, planetSpin3, alignDeg, px, py, backLayer3);
            if (showSpinMarks && spinMarkStroke != null)
            {
                var r = layout3.CompoundTipR;
                var diameter = new LineGeometry(new Point(px - r, py), new Point(px + r, py));
                diameter.Freeze();
                AddRotated(diameter, null, planetSpin3, alignDeg, px, py, spinMarkStroke, 1.4, backLayer3);
            }
        }

        var dimRing3 = inner3Fill != sunFill;
        var stack3 = MakeRingStack();
        DimRingPath(AddRotated(
            ClockGearGeometry.BuildInnerRingExteriorTeeth(cx, cy, layout3, inner3),
            inner3Fill, inner3Spin, inner3AlignDeg, cx, cy, host: stack3), dimRing3);

        var frontLayer3 = MakeCarrierLayer(
            StageClip(inner2Band.InnerRoot, inner3Band.OuterRoot), c2, behind: false,
            parent: stack3, dim: true);
        if (carriage3 != null)
            AddCarriageUnderIdlers(frontLayer3, carriage3, seats3, layout3.IdlerTipR, carriageBrush);
        for (var i = 0; i < planetCount2; i++)
        {
            var localAng = ClockGearGeometry.PlanetCarrierAngle(i, planetCount2, hostTeeth3, offset2Deg);
            var ang = localAng + frameDelta3;
            var px = cx + layout3.CarrierRadius * Math.Cos(ang);
            var py = cy + layout3.CarrierRadius * Math.Sin(ang);
            var alignRad = ClockGearGeometry.PlanetAlignAngle(localAng, planetTeeth3, hostTeeth3) + frameDelta3;
            var alignDeg = alignRad * deg;
            var seat = ClockGearGeometry.IdlerSeatAngle(ang, 0, 1, idlerOffset2Deg);
            var ix = px + layout3.IdlerMeshRadius * Math.Cos(seat);
            var iy = py + layout3.IdlerMeshRadius * Math.Sin(seat);
            var idlerAlignDeg3 = ClockGearGeometry.IdlerAlignAngle(
                seat, alignRad, compoundTeeth3, idlerTeeth3) * deg;
            AddRotated(
                ClockGearGeometry.BuildIdlerGear(ix, iy, layout3),
                idlerFill, idlerSpin3, idlerAlignDeg3, ix, iy, host: frontLayer3);
            AddRotated(
                ClockGearGeometry.BuildPlanetGear(px, py, layout3, planetTeeth3),
                planetFill, planetSpin3, alignDeg, px, py, host: frontLayer3);
        }
        AddCarriagePins(frontLayer3, layout3, seats3, carriageBrush, settings, 2);

        DimRingPath(AddRotated(
            ClockGearGeometry.BuildInnerRingMaskCore(cx, cy, layout3, inner3),
            inner3Fill, inner3Spin, inner3AlignDeg, cx, cy, host: stack3), dimRing3);
        if (showSpinMarks && spinMarkStroke != null)
        {
            var diameter = new LineGeometry(
                new Point(cx - inner3.TipR, cy), new Point(cx + inner3.TipR, cy));
            diameter.Freeze();
            AddRotated(diameter, null, inner3Spin, inner3AlignDeg, cx, cy, spinMarkStroke, 1.4);
        }

        var extraFits = new List<ClockGearGeometry.InnerRingFit>();
        var extraSpins = new List<double>();
        var extraOk = true;
        var prevBand = inner3Band;
        var prevFit = inner3;
        var prevLay = layout3;
        var prevSpin = inner3Spin;
        var prevAlign = inner3AlignDeg;
        for (var s = 3; s < prog.StageCount && extraOk; s++)
        {
            var lay = solves[s].Layout;
            var pCount = solves[s].PlanetCount;
            var pOff = solves[s].PlanetOffsetDeg;
            var idlerOff = solves[s].IdlerOffsetDeg;
            var fit = solves[s].Fit;
            lay = ClockGearGeometry.ApplyCompoundMul(
                lay, fit.Teeth, pCount, pOff, idlerOff,
                CompoundMulAt(settings, s), out idlerOff);
            var band = ClockGearGeometry.ResolveInnerRingBand(fit);
            var cN = C(s);
            CarrierFrameSpins(
                prevSpin, cN, lay.RingTeeth, lay.PlanetTeeth,
                lay.CompoundTeeth, lay.IdlerTeeth, fit.Teeth,
                lay.MeshCompoundTeeth, lay.MeshIdlerTeeth,
                lockSunRpm: s == prog.StageCount - 1,
                out var planetSpinN, out var idlerSpinN, out var innerSpinN);
            var hostPitchN = Math.PI * 2.0 / lay.RingTeeth;
            var frameN = prevAlign * (Math.PI / 180.0) + Math.PI / 2.0 + hostPitchN * 0.5;
            var localN = ClockGearGeometry.PlanetCarrierAngle(0, pCount, lay.RingTeeth, pOff);
            var worldN = localN + frameN;
            var alignN = ClockGearGeometry.PlanetAlignAngle(localN, lay.PlanetTeeth, lay.RingTeeth) + frameN;
            var seatN = ClockGearGeometry.IdlerSeatAngle(worldN, 0, 1, idlerOff);
            var idlerAlignN = ClockGearGeometry.IdlerAlignAngle(
                seatN, alignN, lay.MeshCompoundTeeth, lay.MeshIdlerTeeth);
            var pnx = cx + lay.CarrierRadius * Math.Cos(worldN);
            var pny = cy + lay.CarrierRadius * Math.Sin(worldN);
            var inx = pnx + lay.IdlerMeshRadius * Math.Cos(seatN);
            var iny = pny + lay.IdlerMeshRadius * Math.Sin(seatN);
            var idlerToSunN = Math.Atan2(cy - iny, cx - inx);
            var innerAlignN = ClockGearGeometry.IdlerAlignAngle(
                idlerToSunN, idlerAlignN, lay.MeshIdlerTeeth, fit.Teeth) * deg;
            var innerFillN = s == prog.StageCount - 1 ? sunFill : innerDimFill;
            var radialN = StageClip(prevBand.InnerRoot, band.OuterRoot);
            var hostInteriorSolidN = new CombinedGeometry(
                GeometryCombineMode.Intersect,
                RotateGeo(
                    ClockGearGeometry.BuildInnerRingMaskCore(cx, cy, prevLay, prevFit),
                    prevAlign),
                new EllipseGeometry(new Point(cx, cy), prevBand.InnerRoot, prevBand.InnerRoot));
            hostInteriorSolidN.Freeze();
            var hostPunchN = ExcludeSolid(radialN, hostInteriorSolidN);
            var nextExtN = RotateGeo(
                ClockGearGeometry.BuildInnerRingExteriorTeeth(cx, cy, lay, fit),
                innerAlignN);
            var hostClipN = MakeClipFrame(hostPunchN, prevSpin, behind: true);
            var nextClipN = MakeClipFrame(
                ExcludeSolid(StageClip(rim), nextExtN),
                innerSpinN - prevSpin, behind: false, parent: hostClipN);
            var backN = MakeCarrierOn(nextClipN, cN - innerSpinN);
            var seatsN = new List<(double Px, double Py, (double X, double Y)[] Idlers)>(pCount);
            for (var i = 0; i < pCount; i++)
            {
                var localAng = ClockGearGeometry.PlanetCarrierAngle(i, pCount, lay.RingTeeth, pOff);
                var ang = localAng + frameN;
                var px = cx + lay.CarrierRadius * Math.Cos(ang);
                var py = cy + lay.CarrierRadius * Math.Sin(ang);
                var seat = ClockGearGeometry.IdlerSeatAngle(ang, 0, 1, idlerOff);
                var ix = px + lay.IdlerMeshRadius * Math.Cos(seat);
                var iy = py + lay.IdlerMeshRadius * Math.Sin(seat);
                seatsN.Add((px, py, [(ix, iy)]));
            }
            var carriageN = AddCarriageTracks(backN, cx, cy, lay, seatsN, carriageBrush, settings, s);
            for (var i = 0; i < pCount; i++)
            {
                var localAng = ClockGearGeometry.PlanetCarrierAngle(i, pCount, lay.RingTeeth, pOff);
                var ang = localAng + frameN;
                var px = cx + lay.CarrierRadius * Math.Cos(ang);
                var py = cy + lay.CarrierRadius * Math.Sin(ang);
                var alignRad = ClockGearGeometry.PlanetAlignAngle(localAng, lay.PlanetTeeth, lay.RingTeeth) + frameN;
                var alignDegN = alignRad * deg;
                AddCompound(ClockGearGeometry.BuildCompoundGear(px, py, lay, CompoundAt(s)),
                    compoundFill, planetSpinN, alignDegN, px, py, backN);
            }
            var dimRingN = innerFillN != sunFill;
            var stackN = MakeRingStack();
            DimRingPath(AddRotated(
                ClockGearGeometry.BuildInnerRingExteriorTeeth(cx, cy, lay, fit),
                innerFillN, innerSpinN, innerAlignN, cx, cy, host: stackN), dimRingN);
            var frontN = MakeCarrierLayer(
                StageClip(prevBand.InnerRoot, band.OuterRoot), cN, behind: false,
                parent: stackN, dim: true);
            if (carriageN != null)
                AddCarriageUnderIdlers(frontN, carriageN, seatsN, lay.IdlerTipR, carriageBrush);
            for (var i = 0; i < pCount; i++)
            {
                var localAng = ClockGearGeometry.PlanetCarrierAngle(i, pCount, lay.RingTeeth, pOff);
                var ang = localAng + frameN;
                var px = cx + lay.CarrierRadius * Math.Cos(ang);
                var py = cy + lay.CarrierRadius * Math.Sin(ang);
                var alignRad = ClockGearGeometry.PlanetAlignAngle(localAng, lay.PlanetTeeth, lay.RingTeeth) + frameN;
                var alignDegN = alignRad * deg;
                var seat = ClockGearGeometry.IdlerSeatAngle(ang, 0, 1, idlerOff);
                var ix = px + lay.IdlerMeshRadius * Math.Cos(seat);
                var iy = py + lay.IdlerMeshRadius * Math.Sin(seat);
                var idlerAlignDegN = ClockGearGeometry.IdlerAlignAngle(
                    seat, alignRad, lay.MeshCompoundTeeth, lay.MeshIdlerTeeth) * deg;
                AddRotated(ClockGearGeometry.BuildIdlerGear(ix, iy, lay),
                    idlerFill, idlerSpinN, idlerAlignDegN, ix, iy, host: frontN);
                AddRotated(ClockGearGeometry.BuildPlanetGear(px, py, lay, lay.PlanetTeeth),
                    planetFill, planetSpinN, alignDegN, px, py, host: frontN);
            }
            AddCarriagePins(frontN, lay, seatsN, carriageBrush, settings, s);
            DimRingPath(AddRotated(
                ClockGearGeometry.BuildInnerRingMaskCore(cx, cy, lay, fit),
                innerFillN, innerSpinN, innerAlignN, cx, cy, host: stackN), dimRingN);
            extraFits.Add(fit);
            extraSpins.Add(innerSpinN);
            prevBand = band;
            prevFit = fit;
            prevLay = lay;
            prevSpin = innerSpinN;
            prevAlign = innerAlignN;
        }
        if (!extraOk)
        {
            PaintCarrierWarn();
            UpdatePlanetaryDrive(now);
            return;
        }

        if (settings.ClockGearDebugRingCenter)
        {
            // Solid-rim midpoints only (outer internal ring + hollow first inner).
            var ringCenterStroke = BrushFromHex(
                ColorText.NormalizeOr(settings.ClockColorMajorTick, FallbackMajorTick), 0xEE);
            var outerMidR = Math.Max(2, (layout.RingOuter + layout.RootR) * 0.5);
            var outerMid = new EllipseGeometry(new Point(cx, cy), outerMidR, outerMidR);
            outerMid.Freeze();
            gearLayer.Children.Add(new Path
            {
                Data = outerMid,
                Stroke = ringCenterStroke,
                StrokeThickness = 1.25,
                Fill = null,
                IsHitTestVisible = false
            });

            var innerMidR = Math.Max(2, innerBand.SolidMidR);
            var innerMid = new EllipseGeometry(new Point(cx, cy), innerMidR, innerMidR);
            innerMid.Freeze();
            gearLayer.Children.Add(new Path
            {
                Data = innerMid,
                Stroke = ringCenterStroke,
                StrokeThickness = 1.25,
                Fill = null,
                IsHitTestVisible = false
            });
        }

        if (showSpinMarks)
        {
            var lastFit = extraFits.Count > 0 ? extraFits[^1] : inner3;
            var lastSpin = extraSpins.Count > 0 ? extraSpins[^1] : inner3Spin;
            var cumul = inner.StageRatioFromOuter * inner2.StageRatioFromOuter * inner3.StageRatioFromOuter;
            foreach (var f in extraFits)
                cumul *= f.StageRatioFromOuter;
            var read = new List<string>
            {
                $"{prog.Label}  last {lastFit.Teeth}T locked ×1440",
                $"inner1  {inner.Teeth}T  ×{inner.StageRatioFromOuter:0.##}  {(inner.AssemblyOk ? "OK" : inner.FailReason)}",
                $"inner2  {inner2.Teeth}T  ×{inner2.StageRatioFromOuter:0.##}  {(inner2.AssemblyOk ? "OK" : inner2.FailReason)}",
                $"inner3  {inner3.Teeth}T  ×{inner3.StageRatioFromOuter:0.##}  {(inner3.AssemblyOk ? "OK" : inner3.FailReason)}"
            };
            for (var i = 0; i < extraFits.Count; i++)
            {
                var f = extraFits[i];
                var lockMark = i == extraFits.Count - 1 ? " locked" : "";
                read.Add($"inner{i + 4}  {f.Teeth}T  ×{f.StageRatioFromOuter:0.##}  {(f.AssemblyOk ? "OK" : f.FailReason)}{lockMark}");
            }
            if (extraFits.Count == 0)
                read[^1] += " locked";
            read.Add($"cumul ×{cumul:0.##}");
            DrawGearReadout(cx, cy, layout, settings, [.. read]);

            var anim = Math.Clamp(settings.ClockGearAnimSpeed, 1, 1440);
            var rpm = new List<string>
            {
                $"outer ring: {FormatRpm(ringSpeed * anim)}",
                $"1st inner ring: {FormatRpm(innerSpin * anim)}",
                $"2nd inner ring: {FormatRpm(inner2Spin * anim)}",
                $"3rd inner ring: {FormatRpm(inner3Spin * anim)}"
            };
            for (var i = 0; i < extraSpins.Count; i++)
                rpm.Add($"{i + 4}th inner ring: {FormatRpm(extraSpins[i] * anim)}");
            rpm.Add($"center: {FormatRpm(lastSpin * anim)}");
            DrawRpmReadout(cx, cy, layout, settings, [.. rpm]);
        }

        PaintCarrierWarn();
        UpdatePlanetaryDrive(now);
    }

    void DrawGearReadout(
        double cx, double cy, ClockGearGeometry.FaceMeshLayout layout,
        AppSettings settings, string[] lines)
    {
        var font = Math.Max(9, settings.FontSize - 3);
        var brush = BrushFromHex("#C5C9D0", 0xEE);
        var dim = BrushFromHex("#9AA0A8", 0xCC);
        for (var i = 0; i < lines.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = lines[i],
                Foreground = i == 0 ? brush : dim,
                FontSize = font,
                FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                IsHitTestVisible = false
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, cx - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, cy + layout.CarrierRadius * 0.12 + i * (font + 4));
            Marks.Children.Add(tb);
        }
    }

    void DrawRpmReadout(
        double cx, double cy, ClockGearGeometry.FaceMeshLayout layout,
        AppSettings settings, string[] lines)
    {
        var font = Math.Max(9, settings.FontSize - 3);
        var brush = BrushFromHex("#C5C9D0", 0xEE);
        for (var i = 0; i < lines.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = lines[i],
                Foreground = brush,
                FontSize = font,
                FontWeight = FontWeights.SemiBold,
                IsHitTestVisible = false
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, cx + layout.RingOuter * 0.12);
            Canvas.SetTop(tb, cy - layout.RingOuter * 0.52 + i * (font + 6));
            Marks.Children.Add(tb);
        }
    }

    void DrawCarrierWarn(
        double cx, double cy, ClockGearGeometry.FaceMeshLayout layout,
        AppSettings settings, string message)
    {
        var font = Math.Max(9, settings.FontSize - 3);
        var tb = new TextBlock
        {
            Text = message,
            Foreground = BrushFromHex(
                ColorText.NormalizeOr(settings.ClockColorAttention, "#FF8A65"), 0xEE),
            FontSize = font,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Max(120, layout.RingOuter * 1.6),
            IsHitTestVisible = false
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, cx - tb.DesiredSize.Width / 2);
        Canvas.SetTop(tb, cy + layout.RingOuter * 0.58);
        Marks.Children.Add(tb);
    }

    void ShowProgressionFail(double cx, double cy, double rim, AppSettings settings, string message)
    {
        var font = Math.Max(9, settings.FontSize - 3);
        var tb = new TextBlock
        {
            Text = message,
            Foreground = BrushFromHex("#FF8A65", 0xEE),
            FontSize = font,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Max(80, rim * 1.4),
            IsHitTestVisible = false
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, cx - tb.DesiredSize.Width / 2);
        Canvas.SetTop(tb, cy + rim * 0.08);
        Marks.Children.Add(tb);
    }

    /// <summary>Signed RPM from gear secScale (rev per SecondHandTurn radian-cycle × anim).</summary>
    static string FormatRpm(double rpm)
    {
        var a = Math.Abs(rpm);
        var neg = double.IsNegative(rpm);
        if (a < 1e-12)
            return neg ? "−0 rpm" : "+0 rpm";
        var sign = neg ? "−" : "";
        // Prefer exact unit-fraction when it matches (e.g. 1/1440).
        var inv = 1.0 / a;
        var rounded = Math.Round(inv);
        if (rounded >= 2 && Math.Abs(inv - rounded) < 0.02)
            return $"{sign}1/{rounded:0} rpm";
        if (a >= 0.1)
            return $"{sign}{a:0.####} rpm";
        if (a >= 0.001)
            return $"{sign}{a:0.######} rpm";
        return $"{sign}{a:0.###e0} rpm";
    }

    static int CompoundMulAt(AppSettings settings, int stage) =>
        ClockGearGeometry.CompoundMulFromScale(settings.ClockGearCompoundScale, stage);

    static double[] ManualCarrierSecs(AppSettings settings, int count)
    {
        var secs = new double[Math.Max(0, count)];
        for (var i = 0; i < secs.Length; i++)
            secs[i] = CarrierSec(settings, i);
        return secs;
    }

    static double CarrierSec(AppSettings settings, int stage)
    {
        var rates = settings.ClockGearCarrierDegPerMin;
        var dpm = rates != null && stage >= 0 && stage < rates.Count ? rates[stage] : 0;
        if (Math.Abs(dpm) < 0.05)
            return 0;
        return dpm / 360.0;
    }

    /// <summary>
    /// Willis / carrier-frame rates. planetRel and idlerRel are drawn on a layer
    /// that already rotates at carrierSec; sunInertial is world-frame (sun is centered).
    /// Intermediate inner rings use drawn (k-scaled) teeth so a sized-up idler
    /// still drives that ring. The last sun stays kinematic (1 rpm). In that
    /// last stage the idler is locked to the sun so it does not chew the ring.
    /// </summary>
    static void CarrierFrameSpins(
        double hostInertial, double carrierSec,
        int hostTeeth, int planetTeeth, int compoundTeeth, int idlerTeeth, int sunTeeth,
        int meshCompoundTeeth, int meshIdlerTeeth,
        bool lockSunRpm,
        out double planetRel, out double idlerRel, out double sunInertial)
    {
        hostTeeth = Math.Max(1, hostTeeth);
        planetTeeth = Math.Max(1, planetTeeth);
        compoundTeeth = Math.Max(1, compoundTeeth);
        idlerTeeth = Math.Max(1, idlerTeeth);
        sunTeeth = Math.Max(1, sunTeeth);
        var visNc = Math.Max(1, meshCompoundTeeth > 0 ? meshCompoundTeeth : compoundTeeth);
        var visNi = Math.Max(1, meshIdlerTeeth > 0 ? meshIdlerTeeth : idlerTeeth);
        planetRel = (hostTeeth / (double)planetTeeth) * (hostInertial - carrierSec);
        if (lockSunRpm)
        {
            sunInertial = carrierSec + planetRel * (compoundTeeth / (double)sunTeeth);
            idlerRel = -(sunInertial - carrierSec) * (sunTeeth / (double)visNi);
        }
        else
        {
            idlerRel = -planetRel * (visNc / (double)visNi);
            sunInertial = carrierSec + planetRel * (visNc / (double)sunTeeth);
        }
    }

    void SpinCarrier(Canvas layer, double cx, double cy, double carrierSec)
    {
        if (Math.Abs(carrierSec) < 1e-12)
            return;
        var rot = new RotateTransform(0, cx, cy);
        layer.RenderTransform = rot;
        _gearRots.Add(rot);
        _gearSecScale.Add(carrierSec);
        _gearBaseDeg.Add(0);
    }

    static Geometry? AddCarriageTracks(
        Canvas host, double cx, double cy,
        ClockGearGeometry.FaceMeshLayout layout,
        IReadOnlyList<(double Px, double Py, (double X, double Y)[] Idlers)> seats,
        Brush brush, AppSettings settings, int stage)
    {
        if (settings.ClockGearHideCarousel || seats.Count == 0)
            return null;
        var style = ClockGearCarriageStage.At(settings, stage);
        var stroke = Math.Clamp(style.Rail, 1, 12);
        var rungHalf = Math.Clamp(style.Rung, 1, 10) * 0.5;
        var compoundOrbit = Math.Max(2, layout.CarrierRadius);
        var idlerOrbit = compoundOrbit;
        if (seats[0].Idlers.Length > 0)
        {
            var (ix0, iy0) = seats[0].Idlers[0];
            idlerOrbit = Math.Sqrt((ix0 - cx) * (ix0 - cx) + (iy0 - cy) * (iy0 - cy));
        }
        var hubHole = Math.Max(2.2, Math.Max(layout.CompoundRootR, layout.PlanetRootR));
        var idlerHole = Math.Max(2.2, layout.IdlerRootR);
        var pad = Math.Clamp(style.Hub, 0, 14);
        var innerR = Math.Min(compoundOrbit, idlerOrbit);
        var outerR = Math.Max(compoundOrbit, idlerOrbit);
        var split = Math.Abs(idlerOrbit - compoundOrbit) > stroke * 0.6;
        var kind = style.Kind;
        var rails = kind is ClockGearCarriageKind.Orbit or ClockGearCarriageKind.PinLinks
            or ClockGearCarriageKind.Truss or ClockGearCarriageKind.Helix
            or ClockGearCarriageKind.Fan or ClockGearCarriageKind.Bars;

        var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geo.Open())
        {
            if (rails)
            {
                WriteAnnulus(ctx, cx, cy, compoundOrbit, stroke);
                if (split)
                    WriteAnnulus(ctx, cx, cy, idlerOrbit, stroke);
            }

            switch (kind)
            {
                case ClockGearCarriageKind.PinLinks:
                case ClockGearCarriageKind.Skeleton:
                case ClockGearCarriageKind.Fan:
                    WriteSeatPinLinks(ctx, cx, cy, seats, style.SweepDeg, style.Reverse, rungHalf,
                        style.LinkRepeats, style.LinkStride);
                    break;
                case ClockGearCarriageKind.Cradles:
                    WriteCradles(ctx, cx, cy, innerR, outerR, seats, style.SweepDeg, style.Reverse);
                    WriteSeatPinLinks(ctx, cx, cy, seats, Math.Min(18, style.SweepDeg), style.Reverse, rungHalf,
                        style.LinkRepeats, style.LinkStride);
                    break;
                case ClockGearCarriageKind.Truss:
                    WriteTruss(ctx, cx, cy, innerR, outerR, layout.Module, style.SweepDeg, style.Reverse, rungHalf);
                    WriteSeatPinLinks(ctx, cx, cy, seats, 0, false, rungHalf * 0.85, 0, 0);
                    break;
                case ClockGearCarriageKind.Helix:
                    WriteHelixRibbons(ctx, cx, cy, innerR, outerR, style.SweepDeg, style.Reverse, rungHalf);
                    break;
                case ClockGearCarriageKind.Bars:
                    WriteBars(ctx, cx, cy, innerR, outerR, stroke, seats, style, rungHalf);
                    break;
                default:
                    WriteOrbitBraces(ctx, cx, cy, innerR, outerR, stroke, layout.Module,
                        style.SweepDeg, style.Reverse, rungHalf);
                    break;
            }

            for (var i = 0; i < seats.Count; i++)
            {
                var (px, py, idlers) = seats[i];
                if (pad > 0.05)
                {
                    WriteCircle(ctx, px, py, hubHole + pad, clockwise: false);
                    for (var j = 0; j < idlers.Length; j++)
                        WriteCircle(ctx, idlers[j].X, idlers[j].Y, idlerHole + pad, clockwise: false);
                    // CW circle is a hole in the pad disk. Alone (pad == 0) it fills a
                    // solid carriage platter the size of the compound, so every cutout
                    // shows carriage color.
                    WriteCircle(ctx, px, py, hubHole, clockwise: true);
                    for (var j = 0; j < idlers.Length; j++)
                        WriteCircle(ctx, idlers[j].X, idlers[j].Y, idlerHole, clockwise: true);
                }
            }
        }
        geo.Freeze();

        // Idlers live on a later translucent layer, so this frame would show through
        // the metal. Drop only the tip disk here; the front layer redraws the frame
        // inside that disk and the gear paints over it, leaving the tooth gaps open.
        Geometry data = geo;
        var cover = Math.Max(0.5, layout.IdlerTipR);
        for (var i = 0; i < seats.Count; i++)
        {
            var idlers = seats[i].Idlers;
            for (var j = 0; j < idlers.Length; j++)
            {
                var hole = new EllipseGeometry(new Point(idlers[j].X, idlers[j].Y), cover, cover);
                hole.Freeze();
                data = new CombinedGeometry(GeometryCombineMode.Exclude, data, hole);
            }
        }
        data.Freeze();
        host.Children.Add(new Path
        {
            Data = data,
            Fill = brush,
            IsHitTestVisible = false,
            SnapsToDevicePixels = false
        });
        return geo;
    }

    static void AddCarriageUnderIdlers(
        Canvas host, Geometry carriage,
        IReadOnlyList<(double Px, double Py, (double X, double Y)[] Idlers)> seats,
        double tipR, Brush brush)
    {
        var clip = new GeometryGroup { FillRule = FillRule.Nonzero };
        tipR = Math.Max(0.5, tipR);
        for (var i = 0; i < seats.Count; i++)
        {
            var idlers = seats[i].Idlers;
            for (var j = 0; j < idlers.Length; j++)
                clip.Children.Add(new EllipseGeometry(new Point(idlers[j].X, idlers[j].Y), tipR, tipR));
        }
        if (clip.Children.Count == 0)
            return;
        clip.Freeze();
        host.Children.Add(new Path
        {
            Data = carriage,
            Clip = clip,
            Fill = brush,
            IsHitTestVisible = false,
            SnapsToDevicePixels = false
        });
    }

    static void WriteBars(
        StreamGeometryContext ctx, double cx, double cy,
        double innerR, double outerR, double rail,
        IReadOnlyList<(double Px, double Py, (double X, double Y)[] Idlers)> seats,
        ClockGearCarriageStage style, double half)
    {
        if (seats.Count == 0 || outerR - innerR < 1.2)
            return;
        var count = Math.Clamp(style.BarCount <= 0 ? 4 : style.BarCount, 1, 12);
        var between = Math.Clamp(style.BarBetween, 0, 8);
        var lines = count * (between + 1);
        var step = Math.PI * 2.0 / lines;
        var phase = BarPhase(cx, cy, seats, style.BarOrigin);
        // Flat ends buried in the rails. Rounded caps ended on the rail center
        // and the shared fill punched a semicircle out of each joint.
        var innerEdge = Math.Max(0.5, innerR - rail * 0.5);
        var outerEdge = outerR + rail * 0.5;
        for (var k = 0; k < lines; k++)
        {
            var ang = phase + k * step;
            WriteFlatBar(ctx, GearPolar(cx, cy, innerEdge, ang), GearPolar(cx, cy, outerEdge, ang), half);
        }
    }

    static void WriteFlatBar(StreamGeometryContext ctx, Point a, Point b, double half)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.5)
            return;
        var nx = -dy / len * half;
        var ny = dx / len * half;
        ctx.BeginFigure(new Point(a.X + nx, a.Y + ny), true, true);
        ctx.LineTo(new Point(b.X + nx, b.Y + ny), true, false);
        ctx.LineTo(new Point(b.X - nx, b.Y - ny), true, false);
        ctx.LineTo(new Point(a.X - nx, a.Y - ny), true, false);
    }

    static double BarPhase(
        double cx, double cy,
        IReadOnlyList<(double Px, double Py, (double X, double Y)[] Idlers)> seats,
        ClockGearCarriageBarOrigin origin)
    {
        var here = seats[0];
        var next = seats[1 % seats.Count];
        var large = Math.Atan2(here.Py - cy, here.Px - cx);
        var (sx, sy) = SmallCenter(here);
        var small = Math.Atan2(sy - cy, sx - cx);
        var nextLarge = Math.Atan2(next.Py - cy, next.Px - cx);
        var (nsx, nsy) = SmallCenter(next);
        var nextSmall = Math.Atan2(nsy - cy, nsx - cx);
        return origin switch
        {
            ClockGearCarriageBarOrigin.SmallCenter => small,
            ClockGearCarriageBarOrigin.MidLarge => MidAngle(large, nextLarge),
            ClockGearCarriageBarOrigin.MidSmall => MidAngle(small, nextSmall),
            _ => large
        };
    }

    static double MidAngle(double a0, double a1)
    {
        var d = a1 - a0;
        while (d <= -Math.PI)
            d += Math.PI * 2;
        while (d > Math.PI)
            d -= Math.PI * 2;
        return a0 + d * 0.5;
    }

    /// <summary>
    /// Idler center. The planet pinion is coaxial with the large compound, so the
    /// small center that can sit apart from it is the idler.
    /// </summary>
    static (double X, double Y) SmallCenter(
        (double Px, double Py, (double X, double Y)[] Idlers) seat)
    {
        if (seat.Idlers.Length == 0)
            return (seat.Px, seat.Py);
        return (seat.Idlers[0].X, seat.Idlers[0].Y);
    }

    static void WriteOrbitBraces(
        StreamGeometryContext ctx, double cx, double cy,
        double innerR, double outerR, double stroke, double module,
        double sweepDeg, bool reverse, double half)
    {
        if (outerR - innerR <= stroke)
            return;
        var midR = (innerR + outerR) * 0.5;
        var spacing = Math.Max(11.0, module * 4.0);
        var braceCount = Math.Clamp((int)Math.Round(2 * Math.PI * midR / spacing), 10, 36);
        var step = Math.PI * 2.0 / braceCount;
        var lean = sweepDeg * (Math.PI / 180.0);
        if (reverse)
            lean = -lean;
        for (var k = 0; k < braceCount; k++)
        {
            var a0 = k * step;
            var a1 = a0 + lean;
            var p0 = GearPolar(cx, cy, innerR, a0);
            var p1 = GearPolar(cx, cy, outerR, a1);
            var ctrl = GearPolar(cx, cy, midR + (outerR - innerR) * 0.12, a0 + lean * 0.5);
            WriteRibbon(ctx, p0, ctrl, p1, half);
        }
    }

    static void WriteTruss(
        StreamGeometryContext ctx, double cx, double cy,
        double innerR, double outerR, double module,
        double sweepDeg, bool reverse, double half)
    {
        if (outerR - innerR < 2)
            return;
        var midR = (innerR + outerR) * 0.5;
        var spacing = Math.Max(16.0, module * 5.5);
        var bays = Math.Clamp((int)Math.Round(2 * Math.PI * midR / spacing), 6, 18);
        var step = Math.PI * 2.0 / bays;
        var lean = sweepDeg * (Math.PI / 180.0) * 0.35;
        if (reverse)
            lean = -lean;
        for (var k = 0; k < bays; k++)
        {
            var a0 = k * step + lean;
            var a1 = (k + 1) * step + lean;
            var inner0 = GearPolar(cx, cy, innerR, a0);
            var inner1 = GearPolar(cx, cy, innerR, a1);
            var outer0 = GearPolar(cx, cy, outerR, a0);
            var outer1 = GearPolar(cx, cy, outerR, a1);
            var mid = GearPolar(cx, cy, midR, a0 + step * 0.5);
            WriteRibbon(ctx, inner0, mid, outer1, half);
            WriteRibbon(ctx, outer0, mid, inner1, half);
        }
    }

    static void WriteHelixRibbons(
        StreamGeometryContext ctx, double cx, double cy,
        double innerR, double outerR, double sweepDeg, bool reverse, double half)
    {
        if (outerR - innerR < 2)
            return;
        var sign = reverse ? -1.0 : 1.0;
        var total = Math.PI * 2.0 + sweepDeg * (Math.PI / 180.0) * 4.0;
        const int starts = 2;
        const int segs = 20;
        for (var s = 0; s < starts; s++)
        {
            var aBase = s * Math.PI;
            Point? prev = null;
            for (var k = 0; k <= segs; k++)
            {
                var t = k / (double)segs;
                var a = aBase + sign * t * total;
                var r = innerR + (outerR - innerR) * t;
                var p = GearPolar(cx, cy, r, a);
                if (prev != null)
                {
                    var midT = (k - 0.5) / segs;
                    var ctrl = GearPolar(cx, cy,
                        innerR + (outerR - innerR) * midT,
                        aBase + sign * midT * total);
                    WriteRibbon(ctx, prev.Value, ctrl, p, half);
                }
                prev = p;
            }
        }
    }

    static void WriteCradles(
        StreamGeometryContext ctx, double cx, double cy,
        double innerR, double outerR,
        IReadOnlyList<(double Px, double Py, (double X, double Y)[] Idlers)> seats,
        double sweepDeg, bool reverse)
    {
        if (outerR - innerR < 2)
            return;
        var pad = sweepDeg * (Math.PI / 180.0);
        if (reverse)
            pad = -pad;
        for (var i = 0; i < seats.Count; i++)
        {
            var (px, py, idlers) = seats[i];
            if (idlers.Length == 0)
                continue;
            var ac = Math.Atan2(py - cy, px - cx);
            for (var j = 0; j < idlers.Length; j++)
            {
                var ai = Math.Atan2(idlers[j].Y - cy, idlers[j].X - cx);
                var span = WrapPi(ai - ac);
                var a0 = ac - Math.Sign(span == 0 ? 1 : span) * Math.Abs(pad) * 0.35;
                var a1 = ai + Math.Sign(span == 0 ? 1 : span) * Math.Abs(pad) * 0.65;
                if (pad < 0)
                    (a0, a1) = (a1, a0);
                WriteAnnularSector(ctx, cx, cy, innerR - 0.6, outerR + 0.6, a0, a1);
            }
        }
    }

    static void WriteSeatPinLinks(
        StreamGeometryContext ctx, double cx, double cy,
        IReadOnlyList<(double Px, double Py, (double X, double Y)[] Idlers)> seats,
        double sweepDeg, bool reverse, double half, int repeats, int stride)
    {
        var n = seats.Count;
        if (n == 0)
            return;
        repeats = Math.Clamp(repeats, 0, 8);
        stride = Math.Clamp(stride, 0, 6);
        if (n <= 1)
            stride = 0;
        else
            stride %= n;

        for (var i = 0; i < n; i++)
        {
            var src = seats[i];
            var dst = seats[(i + stride) % n];
            for (var j = 0; j < dst.Idlers.Length; j++)
                WritePinLink(ctx, src.Px, src.Py, dst.Idlers[j].X, dst.Idlers[j].Y, sweepDeg, reverse, half);
        }

        if (repeats <= 0)
            return;
        var fullTurn = n == 1 ? (reverse ? -Math.PI * 2 : Math.PI * 2) : 0.0;
        for (var i = 0; i < n; i++)
        {
            var next = (i + 1) % n;
            var src0 = seats[i];
            var src1 = seats[next];
            var dst0 = seats[(i + stride) % n];
            var dst1 = seats[(next + stride) % n];
            var idlers = Math.Min(dst0.Idlers.Length, dst1.Idlers.Length);
            if (idlers == 0)
                continue;
            for (var k = 1; k <= repeats; k++)
            {
                var t = k / (double)(repeats + 1);
                var px = LerpOrbit(cx, cy, src0.Px, src0.Py, src1.Px, src1.Py, t, fullTurn);
                for (var j = 0; j < idlers; j++)
                {
                    var id = LerpOrbit(
                        cx, cy,
                        dst0.Idlers[j].X, dst0.Idlers[j].Y,
                        dst1.Idlers[j].X, dst1.Idlers[j].Y,
                        t, fullTurn);
                    WritePinLink(ctx, px.X, px.Y, id.X, id.Y, sweepDeg, reverse, half);
                }
            }
        }
    }

    static Point LerpOrbit(
        double cx, double cy, double x0, double y0, double x1, double y1, double t, double fullTurn)
    {
        var a0 = Math.Atan2(y0 - cy, x0 - cx);
        var a1 = Math.Atan2(y1 - cy, x1 - cx);
        var r0 = Math.Sqrt((x0 - cx) * (x0 - cx) + (y0 - cy) * (y0 - cy));
        var r1 = Math.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy));
        var da = a1 - a0;
        while (da <= -Math.PI)
            da += Math.PI * 2;
        while (da > Math.PI)
            da -= Math.PI * 2;
        if (Math.Abs(da) < 1e-8 && Math.Abs(fullTurn) > 0)
            da = fullTurn;
        return GearPolar(cx, cy, r0 + (r1 - r0) * t, a0 + da * t);
    }

    static void WritePinLink(
        StreamGeometryContext ctx,
        double ax, double ay, double bx, double by,
        double sweepDeg, bool reverse, double half)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 0.8)
            return;
        var nx = -dy / dist;
        var ny = dx / dist;
        if (reverse)
        {
            nx = -nx;
            ny = -ny;
        }
        var bow = dist * Math.Sin(sweepDeg * (Math.PI / 180.0));
        var ctrl = new Point(
            (ax + bx) * 0.5 + nx * bow,
            (ay + by) * 0.5 + ny * bow);
        WriteRibbon(ctx, new Point(ax, ay), ctrl, new Point(bx, by), half);
    }

    static void WriteAnnularSector(
        StreamGeometryContext ctx, double cx, double cy,
        double inner, double outer, double a0, double a1)
    {
        inner = Math.Max(0.6, inner);
        outer = Math.Max(inner + 0.8, outer);
        var delta = WrapPi(a1 - a0);
        if (Math.Abs(delta) < 0.04)
            return;
        if (delta < 0)
        {
            (a0, a1) = (a1, a0);
            delta = -delta;
        }
        var large = delta > Math.PI;
        var out0 = GearPolar(cx, cy, outer, a0);
        var out1 = GearPolar(cx, cy, outer, a1);
        var in1 = GearPolar(cx, cy, inner, a1);
        var in0 = GearPolar(cx, cy, inner, a0);
        ctx.BeginFigure(out0, true, true);
        ctx.ArcTo(out1, new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
        ctx.LineTo(in1, true, false);
        ctx.ArcTo(in0, new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
    }

    static Point GearPolar(double cx, double cy, double r, double a) =>
        new(cx + r * Math.Cos(a), cy + r * Math.Sin(a));

    static double WrapPi(double a)
    {
        while (a > Math.PI)
            a -= Math.PI * 2;
        while (a < -Math.PI)
            a += Math.PI * 2;
        return a;
    }

    static void AddCarriagePins(
        Canvas host, ClockGearGeometry.FaceMeshLayout layout,
        IReadOnlyList<(double Px, double Py, (double X, double Y)[] Idlers)> seats,
        Brush brush, AppSettings settings, int stage)
    {
        var style = ClockGearCarriageStage.At(settings, stage);
        if (settings.ClockGearHideCarousel || !style.Pins || seats.Count == 0)
            return;
        var pinR = Math.Clamp(layout.Module * 0.9, 1.35, 3.2);
        for (var i = 0; i < seats.Count; i++)
        {
            var (px, py, idlers) = seats[i];
            AddAxlePin(host, px, py, pinR, brush);
            for (var j = 0; j < idlers.Length; j++)
                AddAxlePin(host, idlers[j].X, idlers[j].Y, pinR, brush);
        }
    }

    static void AddAxlePin(Canvas host, double x, double y, double r, Brush brush)
    {
        var pin = new Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            Fill = brush,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(pin, x - r);
        Canvas.SetTop(pin, y - r);
        host.Children.Add(pin);
    }

    static void WriteAnnulus(StreamGeometryContext ctx, double cx, double cy, double r, double stroke)
    {
        var half = Math.Max(0.7, stroke * 0.5);
        WriteCircle(ctx, cx, cy, r + half, clockwise: false);
        WriteCircle(ctx, cx, cy, Math.Max(0.4, r - half), clockwise: true);
    }

    static void WriteCircle(StreamGeometryContext ctx, double cx, double cy, double r, bool clockwise)
    {
        r = Math.Max(0.5, r);
        var dir = clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        ctx.BeginFigure(new Point(cx + r, cy), true, true);
        ctx.ArcTo(new Point(cx - r, cy), new Size(r, r), 0, false, dir, true, false);
        ctx.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, dir, true, false);
    }

    static void WriteRibbon(StreamGeometryContext ctx, Point a, Point c, Point b, double half)
    {
        half = Math.Max(0.8, half);
        const int samples = 10;
        var pts = new Point[samples + 1];
        for (var i = 0; i <= samples; i++)
        {
            var t = i / (double)samples;
            var u = 1.0 - t;
            pts[i] = new Point(
                u * u * a.X + 2 * u * t * c.X + t * t * b.X,
                u * u * a.Y + 2 * u * t * c.Y + t * t * b.Y);
        }
        var left = new Point[samples + 1];
        var right = new Point[samples + 1];
        for (var i = 0; i <= samples; i++)
        {
            var i0 = Math.Max(0, i - 1);
            var i1 = Math.Min(samples, i + 1);
            var dx = pts[i1].X - pts[i0].X;
            var dy = pts[i1].Y - pts[i0].Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6)
            {
                left[i] = right[i] = pts[i];
                continue;
            }
            var nx = -dy / len * half;
            var ny = dx / len * half;
            left[i] = new Point(pts[i].X + nx, pts[i].Y + ny);
            right[i] = new Point(pts[i].X - nx, pts[i].Y - ny);
        }
        ctx.BeginFigure(left[0], true, true);
        for (var i = 1; i <= samples; i++)
            ctx.LineTo(left[i], true, false);
        ctx.ArcTo(right[samples], new Size(half, half), 0, false, SweepDirection.Clockwise, true, false);
        for (var i = samples - 1; i >= 0; i--)
            ctx.LineTo(right[i], true, false);
        ctx.ArcTo(left[0], new Size(half, half), 0, false, SweepDirection.Clockwise, true, false);
    }

    void UpdatePlanetaryDrive(DateTime now)
    {
        if (_gearRots.Count == 0
            || _gearRots.Count != _gearSecScale.Count
            || _gearRots.Count != _gearBaseDeg.Count)
            return;
        var sec = SecondHandTurn(now);
        var speed = Math.Clamp(AppHost.Instance.Data.Settings.ClockGearAnimSpeed, 1, 1440);
        var deg = 180.0 / Math.PI;
        for (var i = 0; i < _gearRots.Count; i++)
        {
            var angle = _gearBaseDeg[i] + sec * _gearSecScale[i] * speed * deg;
            if (Math.Abs(_gearRots[i].Angle - angle) < 0.02)
                continue;
            _gearRots[i].Angle = angle;
        }
    }

    void DrawClockTicks(DateTime now, double spanHours, double cx, double cy, double rim, AppSettings settings)
    {
        var majorBrush = BrushFromHex(ColorText.NormalizeOr(settings.ClockColorMajorTick, FallbackMajorTick), 0xCC);
        var minorBrush = BrushFromHex(ColorText.NormalizeOr(settings.ClockColorMinorTick, FallbackMinorTick), 0x66);
        var step = TickStep(settings.ClockTickStep);
        var lenHour = Math.Clamp(settings.ClockTickLengthHour, 0.01, 0.50);
        var lenThirty = Math.Clamp(settings.ClockTickLengthThirty, 0.01, 0.50);
        var lenFifteen = Math.Clamp(settings.ClockTickLengthFifteen, 0.01, 0.50);
        var lenMinute = Math.Clamp(settings.ClockTickLengthMinute, 0.01, 0.50);
        var start = AlignDown(now, step);
        if (start > now.AddMilliseconds(-1))
            start = start.Add(-step);
        var end = now.AddHours(spanHours);

        for (var t = start; t <= end.Add(step); t = t.Add(step))
        {
            var ahead = (t - now).TotalHours;
            if (ahead < -step.TotalHours / 60.0)
                continue;
            if (ahead > spanHours + 0.001)
                break;

            var (inward, major) = TickClass(t, lenHour, lenThirty, lenFifteen, lenMinute);
            var inner = rim * (1.0 - inward);
            var (x1, y1) = PointOnRing(cx, cy, inner, ahead, spanHours);
            var (x2, y2) = PointOnRing(cx, cy, rim, ahead, spanHours);
            Face.Children.Add(new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = major ? majorBrush : minorBrush,
                StrokeThickness = major ? 2 : 1,
                IsHitTestVisible = false
            });
        }
    }

    // auto model: pick length by the strongest class this instant belongs to
    static (double Inward, bool Hour) TickClass(
        DateTime t, double lenHour, double lenThirty, double lenFifteen, double lenMinute)
    {
        if (t.Minute == 0 && t.Second == 0)
            return (lenHour, true);
        if (t.Minute % 30 == 0)
            return (lenThirty, false);
        if (t.Minute % 15 == 0)
            return (lenFifteen, false);
        return (lenMinute, false);
    }

    void ApplyDiscordCue(AppSettings settings, double cx, double cy, double radius)
    {
        _discordCue = null;
        _discordCuePulses = false;
        if (!AppHost.DiscordShowsClockCue(settings))
            return;
        var discord = AppHost.Instance.Discord;
        if (discord == null)
            return;

        if (discord.PluginMissing && settings.DiscordMissingClockCue != DiscordMissingClockCue.Off)
        {
            DrawFaceCue(
                MapMissingCue(settings.DiscordMissingClockCue),
                ColorText.NormalizeOr(settings.DiscordMissingClockColor, "#FFB74D"),
                CuePeak(settings.DiscordMissingClockOpacity, 0.7),
                Math.Clamp(settings.DiscordClockRingInset <= 0 ? 0.08 : settings.DiscordClockRingInset, 0.04, 0.55),
                cx, cy, radius,
                ref _discordCue, ref _discordCuePulses, ref _discordCuePeak);
            return;
        }

        if (discord.HasUnread)
        {
            DrawFaceCue(
                settings.DiscordClockCue,
                ColorText.NormalizeOr(
                    discord.UnreadCueColor ?? settings.DiscordClockColor,
                    "#5865F2"),
                CuePeak(settings.DiscordClockOpacity, 0.7),
                Math.Clamp(settings.DiscordClockRingInset <= 0 ? 0.08 : settings.DiscordClockRingInset, 0.04, 0.55),
                cx, cy, radius,
                ref _discordCue, ref _discordCuePulses, ref _discordCuePeak);
        }
    }

    void ApplyLastChanceCue(
        AppSettings settings,
        List<(string Title, double HoursAhead, double? DueByHoursAhead, string Kind, DateTime At, DateTime? DueBy, double? UsedPercent, double Opacity)> clockEvents,
        DateTime now,
        double cx, double cy, double radius)
    {
        _lastChanceCue = null;
        _lastChanceCuePulses = false;
        if (settings.ClockLastChanceCue == ClockLastChanceCue.Off)
            return;
        if (!clockEvents.Any(e => ClockItemInLastChance(e, settings, now)))
            return;

        DrawFaceCue(
            MapLastChanceCue(settings.ClockLastChanceCue),
            ColorText.NormalizeOr(settings.ClockWindowLastColor, "#FF5A5A"),
            0.7,
            0.12,
            cx, cy, radius,
            ref _lastChanceCue, ref _lastChanceCuePulses, ref _lastChanceCuePeak);
    }

    static bool ClockItemInLastChance(
        (string Title, double HoursAhead, double? DueByHoursAhead, string Kind, DateTime At, DateTime? DueBy, double? UsedPercent, double Opacity) item,
        AppSettings settings,
        DateTime now)
    {
        if (item.Kind is not ("todo" or "event"))
            return false;
        if (item.DueBy is not DateTime dueBy || dueBy <= item.At)
            return false;
        if (now < item.At)
            return false;
        var lastHours = Math.Clamp(
            settings.ClockWindowLastHours <= 0 ? 1 : settings.ClockWindowLastHours, 0.05, 24 * 14);
        var lastAt = dueBy.AddHours(-lastHours);
        if (lastAt < item.At)
            lastAt = item.At;
        return now >= lastAt;
    }

    static DiscordClockCue MapMissingCue(DiscordMissingClockCue cue) =>
        cue switch
        {
            DiscordMissingClockCue.TintFace => DiscordClockCue.TintFace,
            DiscordMissingClockCue.PulseRing => DiscordClockCue.PulseRing,
            DiscordMissingClockCue.RimDot => DiscordClockCue.RimDot,
            _ => DiscordClockCue.PulseFace
        };

    static DiscordClockCue MapLastChanceCue(ClockLastChanceCue cue) =>
        cue switch
        {
            ClockLastChanceCue.TintFace => DiscordClockCue.TintFace,
            ClockLastChanceCue.PulseRing => DiscordClockCue.PulseRing,
            ClockLastChanceCue.RimDot => DiscordClockCue.RimDot,
            _ => DiscordClockCue.PulseFace
        };

    void DrawFaceCue(
        DiscordClockCue cue, string hex, double peak, double ringInset,
        double cx, double cy, double radius,
        ref UIElement? target, ref bool pulses, ref double peakStore)
    {
        var brush = BrushFromHex(hex, 0xFF);
        peakStore = peak;
        pulses = cue != DiscordClockCue.TintFace;
        switch (cue)
        {
            case DiscordClockCue.TintFace:
            {
                var disk = new Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    Fill = brush,
                    Opacity = peak,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(disk, cx - radius);
                Canvas.SetTop(disk, cy - radius);
                Cues.Children.Add(disk);
                break;
            }
            case DiscordClockCue.PulseRing:
            {
                var thickness = Math.Clamp(ringInset, 0.04, 0.55) * radius;
                thickness = Math.Max(3, thickness);
                var mid = Math.Max(4, radius - thickness / 2);
                var ring = new Ellipse
                {
                    Width = mid * 2,
                    Height = mid * 2,
                    Stroke = brush,
                    StrokeThickness = thickness,
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(ring, cx - mid);
                Canvas.SetTop(ring, cy - mid);
                Cues.Children.Add(ring);
                target = ring;
                break;
            }
            case DiscordClockCue.RimDot:
            {
                var size = Math.Max(9, radius * 0.13);
                var dot = new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = brush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, cx - size / 2);
                Canvas.SetTop(dot, cy - radius - size * 0.15);
                Cues.Children.Add(dot);
                target = dot;
                break;
            }
            default:
            {
                var disk = new Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    Fill = brush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(disk, cx - radius);
                Canvas.SetTop(disk, cy - radius);
                Cues.Children.Add(disk);
                target = disk;
                break;
            }
        }

        UpdateFaceCuePulses(DateTime.Now);
    }

    void ApplyDiscordSender(AppSettings settings, double cx, double cy, double radius)
    {
        DiscordSenderHost.Children.Clear();
        DiscordSenderRow.Height = new GridLength(0);

        var discord = AppHost.Instance.Discord;
        string text;
        DiscordClockSender place;
        string colorHex;
        string? outlineHex = null;
        var outlineStrength = 2;
        double? fontOverride = null;
        if (AppHost.DiscordShowsClockCue(settings)
            && discord?.PluginMissing == true
            && settings.DiscordMissingClockCue != DiscordMissingClockCue.Off
            && settings.DiscordMissingClockLabel != DiscordClockSender.Off
            && !string.IsNullOrWhiteSpace(discord.MissingLabel))
        {
            text = discord.MissingLabel;
            place = settings.DiscordMissingClockLabel;
            colorHex = ColorText.NormalizeOr(settings.DiscordMissingClockColor, "#FFB74D");
        }
        else if (AppHost.DiscordShowsClockCue(settings)
            && discord?.HasUnread == true
            && settings.DiscordClockSender != DiscordClockSender.Off
            && !string.IsNullOrWhiteSpace(discord.UnreadLabel))
        {
            var names = discord.UnreadNames.Count > 0
                ? discord.UnreadNames
                : discord.UnreadLabel.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            text = string.Join("\n", names.Select(name =>
                settings.DiscordClockNameOnly ? name : "New message from " + name));
            place = settings.DiscordClockSender;
            var style = discord.UnreadNameStyle;
            colorHex = ColorText.NormalizeOr(
                (style != null && ColorText.TryNormalize(style.TextColor, out var textColor)
                    ? textColor
                    : null)
                ?? settings.DiscordClockTextColor,
                "#E8EAED");
            if (style != null && ColorText.TryNormalize(style.OutlineColor, out var outline))
            {
                outlineHex = outline;
                outlineStrength = Math.Clamp(style.OutlineStrength <= 0 ? 2 : style.OutlineStrength, 1, 6);
            }
            if (style != null && style.TextSize > 0)
                fontOverride = Math.Clamp(style.TextSize, 8, 48);
        }
        else
            return;

        var brush = BrushFromHex(colorHex, 0xFF);
        var font = fontOverride ?? Math.Max(11, settings.FontSize - 1);
        Brush? outlineBrush = outlineHex == null ? null : BrushFromHex(outlineHex, 0xFF);

        if (place == DiscordClockSender.OnFace)
        {
            var maxWidth = Math.Max(80, radius * 1.55);
            PlaceSenderText(
                text, brush, outlineBrush, outlineStrength, font, maxWidth,
                Marks, cx, hostTop: null, faceCy: cy, faceRadius: radius);
            return;
        }

        DiscordSenderRow.Height = GridLength.Auto;
        var belowWidth = Math.Max(80, FaceHost.ActualWidth - 16);
        DiscordSenderHost.Width = belowWidth;
        DiscordSenderHost.HorizontalAlignment = HorizontalAlignment.Center;
        PlaceSenderText(
            text, brush, outlineBrush, outlineStrength, font, belowWidth,
            DiscordSenderHost, cx: belowWidth / 2, hostTop: 0, faceCy: null, faceRadius: null);
    }

    /// <summary>
    /// Sender name(s). Outline matches <see cref="PlaceCurrentTime"/>: widen glyph
    /// edges with a round pen, fill that ribbon, then paint the fill glyph on top.
    /// Each line is centered on <paramref name="cx"/> by ink bounds (not advance width).
    /// </summary>
    void PlaceSenderText(
        string text, Brush fill, Brush? outline, int strength, double fontSize, double maxWidth,
        Canvas host, double cx, double? hostTop, double? faceCy, double? faceRadius)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(
            SystemFonts.MessageFontFamily,
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);
        Pen? pen = null;
        if (outline != null)
        {
            pen = new Pen(outline, Math.Clamp(strength, 1, 6))
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var geos = new List<(Geometry Geo, Geometry? Outline, double Height)>();
        double totalHeight = 0;
        var lineGap = Math.Max(4, fontSize * 0.28);
        foreach (var line in lines)
        {
            // Left-align at (0,0); horizontal centering uses geometry bounds below.
            var ft = new FormattedText(
                line.Length == 0 ? " " : line,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.White,
                dpi)
            {
                MaxTextWidth = maxWidth,
                Trimming = TextTrimming.None
            };
            var geo = ft.BuildGeometry(new Point(0, 0));
            geo.Freeze();
            Geometry? outlineGeo = null;
            if (pen != null)
            {
                outlineGeo = geo.GetWidenedPathGeometry(pen);
                outlineGeo.Freeze();
            }
            geos.Add((geo, outlineGeo, ft.Height));
            totalHeight += ft.Height + lineGap;
        }

        if (geos.Count > 0)
            totalHeight -= lineGap;

        double top;
        if (hostTop is double fixedTop)
            top = fixedTop;
        else
        {
            var cy = faceCy ?? 0;
            var radius = faceRadius ?? 0;
            top = cy + radius * 0.22;
            if (top + totalHeight > cy + radius - 4)
                top = Math.Max(cy - radius * 0.1, cy + radius - totalHeight - 4);
        }

        var pad = pen == null ? 0 : Math.Clamp(strength, 1, 6);
        if (ReferenceEquals(host, DiscordSenderHost))
            DiscordSenderHost.Height = Math.Max(0, totalHeight + pad);

        var y = top;
        foreach (var (geo, outlineGeo, height) in geos)
        {
            var lineLeft = CanvasLeftCentered(cx, geo, outlineGeo);
            if (outlineGeo != null && outline != null)
            {
                var outlinePath = new System.Windows.Shapes.Path
                {
                    Data = outlineGeo,
                    Fill = outline,
                    Stroke = null,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(outlinePath, lineLeft);
                Canvas.SetTop(outlinePath, y);
                host.Children.Add(outlinePath);
            }

            var fillPath = new System.Windows.Shapes.Path
            {
                Data = geo,
                Fill = fill,
                Stroke = null,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(fillPath, lineLeft);
            Canvas.SetTop(fillPath, y);
            host.Children.Add(fillPath);
            y += height + lineGap;
        }
    }

    void UpdateFaceCuePulses(DateTime now)
    {
        var wave = 0.5 + 0.5 * Math.Sin(now.TimeOfDay.TotalSeconds * (Math.PI * 2 / 1.4));
        var opacityFactor = 0.38 + 0.62 * wave;
        if (_discordCue != null && _discordCuePulses)
            _discordCue.Opacity = _discordCuePeak * opacityFactor;
        if (_lastChanceCue != null && _lastChanceCuePulses)
            _lastChanceCue.Opacity = _lastChanceCuePeak * opacityFactor;
    }

    static double CuePeak(double opacity, double fallback) =>
        Math.Clamp(opacity <= 0 ? fallback : opacity, 0.05, 1);

    SolidColorBrush BrushFromHex(string hex, byte alpha)
    {
        var key = $"{hex}:{alpha}";
        if (_brushes.TryGetValue(key, out var cached))
            return cached;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            color.A = alpha;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _brushes[key] = brush;
            return brush;
        }
        catch
        {
            return TipTextBrush;
        }
    }

    static string DrivePartHex(string? stored, string inherit)
    {
        if (!string.IsNullOrWhiteSpace(stored) && ColorText.TryNormalize(stored, out var hex))
            return hex;
        return ColorText.NormalizeOr(inherit, "#7A8088");
    }

    // auto model
    static string DarkenHex(string hex, double factor)
    {
        factor = Math.Clamp(factor, 0.15, 1);
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex)!;
            return $"#{(byte)(c.R * factor):X2}{(byte)(c.G * factor):X2}{(byte)(c.B * factor):X2}";
        }
        catch
        {
            return "#3A3E44";
        }
    }

    static TimeSpan TickStep(ClockTickStep step) =>
        step switch
        {
            ClockTickStep.Minute => TimeSpan.FromMinutes(1),
            ClockTickStep.FifteenMinutes => TimeSpan.FromMinutes(15),
            ClockTickStep.ThirtyMinutes => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromHours(1)
        };

    static DateTime AlignDown(DateTime now, TimeSpan step)
    {
        if (step >= TimeSpan.FromHours(1))
            return new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind);
        var mins = Math.Max(1, (int)step.TotalMinutes);
        var aligned = now.Minute / mins * mins;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, aligned, 0, now.Kind);
    }

    static string FormatClockTime(DateTime time, bool use12, bool withSeconds)
    {
        if (use12)
            return time.ToString(withSeconds ? "h:mm:ss tt" : "h:mm tt", CultureInfo.CurrentCulture);
        return time.ToString(withSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.CurrentCulture);
    }

    void TryPlaceEventLabel(double cx, double cy, double radius, double hoursAhead, double spanHours,
        string text, Brush brush, double fontSize, FontWeight weight)
    {
        for (var i = 0; i < 5; i++)
        {
            var r = radius * (1.38 + i * 0.1);
            if (TryPlaceCenteredLabel(cx, cy, r, hoursAhead, spanHours, text, brush, fontSize, weight))
                return;
        }
    }

    bool TryPlaceCenteredLabel(double cx, double cy, double radius, double hoursAhead, double spanHours,
        string text, Brush brush, double fontSize, FontWeight weight)
    {
        var (x, y) = PointOnRing(cx, cy, radius, hoursAhead, spanHours);
        var block = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = fontSize,
            FontWeight = weight,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 100,
            IsHitTestVisible = false
        };
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = block.DesiredSize.Width;
        var h = block.DesiredSize.Height;
        var dx = x - cx;
        var dy = y - cy;
        double left = Math.Abs(dx) < 8 ? x - w / 2 : dx >= 0 ? x + 2 : x - w - 2;
        double top = Math.Abs(dy) < 8 ? y - h / 2 : dy >= 0 ? y + 2 : y - h - 2;
        var rect = new Rect(left, top, w, h);
        foreach (var used in _usedLabelRects)
        {
            if (Rect.Intersect(used, rect) is { Width: > 1, Height: > 1 })
                return false;
        }

        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        Face.Children.Add(block);
        RememberLabel(left, top, w, h);
        return true;
    }

    void RememberLabel(double left, double top, double w, double h) =>
        _usedLabelRects.Add(new Rect(left - 2, top - 2, w + 4, h + 4));

    static (double X, double Y) PointOnRing(double cx, double cy, double radius, double hoursAhead, double spanHours)
    {
        var span = Math.Max(spanHours, 0.001);
        var turn = hoursAhead / span * Math.PI * 2;
        return Polar(cx, cy, radius, turn);
    }

    static (double X, double Y) Polar(double cx, double cy, double radius, double turn) =>
        (cx + radius * Math.Sin(turn), cy - radius * Math.Cos(turn));

    static Point PolarPoint(double cx, double cy, double radius, double turn)
    {
        var (x, y) = Polar(cx, cy, radius, turn);
        return new Point(x, y);
    }

    void OnHide(object sender, RoutedEventArgs e) => AppHost.Instance.HideClockModule();
}
