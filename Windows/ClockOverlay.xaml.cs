using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
    readonly List<Rect> _usedLabelRects = [];
    readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    readonly List<WedgeHotspot> _hotspots = [];
    readonly Popup _tipPopup = new()
    {
        AllowsTransparency = true,
        Placement = PlacementMode.Absolute,
        IsHitTestVisible = false,
        StaysOpen = true
    };
    readonly TextBlock _tipText = new()
    {
        Foreground = TipTextBrush,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 220
    };

    Line? _secondHand;
    Ellipse? _secondHub;
    readonly List<RotateTransform> _gearRots = [];
    readonly List<double> _gearSecScale = [];
    readonly List<double> _gearBaseDeg = [];
    bool _rendering;
    double _handCx;
    double _handCy;
    double _handRadius;
    double _gearHorizonHours = 24;
    string? _openTip;
    bool _buildingFace;

    sealed class WedgeHotspot
    {
        public required PathGeometry Geometry { get; init; }
        public required string Tip { get; init; }
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
        _tipPopup.Child = new Border
        {
            Background = TipBg,
            BorderBrush = TipBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = _tipText,
            IsHitTestVisible = false
        };
        LocationChanged += (_, _) => AppHost.Instance.ScheduleBoundsSave();
        SizeChanged += (_, _) =>
        {
            AppHost.Instance.ScheduleBoundsSave();
            Refresh();
        };
        AppHost.Instance.UiChanged += Refresh;
        _faceTick.Tick += (_, _) => RefreshFace(restageGears: false);
        Loaded += (_, _) =>
        {
            ApplyChrome();
            Refresh();
            StartMotion();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                StartMotion();
            else
            {
                StopMotion();
                HideTip();
            }
        };
        Closed += (_, _) =>
        {
            StopMotion();
            HideTip();
            AppHost.Instance.UiChanged -= Refresh;
        };
    }

    void StartMotion()
    {
        _faceTick.Start();
        if (_rendering)
            return;
        CompositionTarget.Rendering += OnRendering;
        _rendering = true;
    }

    void StopMotion()
    {
        _faceTick.Stop();
        if (!_rendering)
            return;
        CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible || !IsLoaded || _buildingFace)
            return;
        if (AppHost.Instance.Data.Settings.ClockShowSecondHand
            || AppHost.Instance.Data.Settings.ClockGearDriveMode != ClockGearDriveMode.Off)
            UpdateSecondHand(DateTime.Now);
        if (AppHost.Instance.Data.Settings.ClockGearDriveMode != ClockGearDriveMode.Off)
            UpdatePlanetaryDrive(DateTime.Now);
        UpdateHoverTip();
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
        }
    }

    void RefreshCore(bool restageGears)
    {
        var now = DateTime.Now;
        FaceFill.Children.Clear();
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
            && clockEvents.Any(e => e.HoursAhead <= 0.02
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
                _gearRots.Clear();
                _gearSecScale.Clear();
                _gearBaseDeg.Clear();
                AddConcentricPlanetary(cx, cy, rim, width, height, settings, now);
            }
        }
        else if (restageGears)
        {
            Gears.Children.Clear();
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
                var due = item.HoursAhead <= 0.02;
                var title = item.Title.Length > 20 ? item.Title[..19] + "…" : item.Title;
                TryPlaceEventLabel(cx, cy, rim, hours, spanHours, title,
                    BrushFromHex(TypeColorHex(settings, item.Kind, due, item.UsedPercent), 255),
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

    void DrawEventWedges(
        List<(string Title, double HoursAhead, string Kind, DateTime At, double? UsedPercent)> events,
        DateTime now,
        double spanHours,
        double cx,
        double cy,
        double rim,
        bool use12)
    {
        var settings = AppHost.Instance.Data.Settings;
        var clusterGap = Math.Max(0.012, 0.4 / Math.Max(spanHours, 1));
        var clusters = new List<List<(string Title, double HoursAhead, string Kind, DateTime At, double? UsedPercent)>>();
        foreach (var item in events.OrderBy(e => e.HoursAhead))
        {
            if (clusters.Count > 0
                && Math.Abs(item.HoursAhead - clusters[^1][^1].HoursAhead) <= clusterGap * spanHours)
                clusters[^1].Add(item);
            else
                clusters.Add([item]);
        }

        foreach (var cluster in clusters)
        {
            var hours = Math.Clamp(cluster.Average(c => c.HoursAhead), 0, spanHours);
            var due = cluster.Any(c => c.HoursAhead <= 0.02);
            var kinds = cluster.Select(c => c.Kind).Distinct().ToList();
            Brush fill;
            if (kinds.Count == 1)
            {
                fill = BrushFromHex(TypeColorHex(settings, kinds[0], due, cluster[0].UsedPercent), due ? (byte)0xC8 : (byte)0xA0);
            }
            else
            {
                var c0 = (Color)ColorConverter.ConvertFromString(TypeColorHex(settings, kinds[0], due, cluster[0].UsedPercent))!;
                var c1 = (Color)ColorConverter.ConvertFromString(TypeColorHex(settings, kinds[1], due, cluster.Count > 1 ? cluster[1].UsedPercent : null))!;
                c0.A = due ? (byte)0xC8 : (byte)0xA0;
                c1.A = due ? (byte)0xC8 : (byte)0xA0;
                fill = new LinearGradientBrush(c0, c1, 45);
                fill.Freeze();
            }

            var halfTurns = Math.Max(0.005, Math.Clamp(settings.ClockWedgeWidth, 0.01, 0.15) / 2.0);
            // Slightly widen stacked clusters so overlapping events stay readable.
            halfTurns += Math.Max(0, cluster.Count - 1) * 0.001;
            var centerTurn = hours / Math.Max(spanHours, 0.001);
            var a0 = (centerTurn - halfTurns) * Math.PI * 2;
            var a1 = (centerTurn + halfTurns) * Math.PI * 2;
            // Fill flush to the ring's inner edge; no stroke so tips meet the hub cleanly.
            var p0 = PolarPoint(cx, cy, rim, a0);
            var p1 = PolarPoint(cx, cy, rim, a1);

            var fig = new PathFigure
            {
                StartPoint = new Point(cx, cy),
                IsClosed = true,
                IsFilled = true
            };
            fig.Segments.Add(new LineSegment(p0, false));
            fig.Segments.Add(new ArcSegment
            {
                Point = p1,
                Size = new Size(rim, rim),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = a1 - a0 > Math.PI,
                IsStroked = false
            });
            fig.Segments.Add(new LineSegment(new Point(cx, cy), false));

            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();

            var tip = BuildClusterTip(cluster, use12);
            Face.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geo,
                Fill = fill,
                Stroke = null,
                StrokeThickness = 0,
                IsHitTestVisible = false
            });

            // Opaque radial lines mark each event's exact time down the wedge.
            var lineBrush = BrushFromHex(
                ColorText.NormalizeOr(settings.ClockColorWedgeLine, "#FFFFFF"), 255);
            foreach (var item in cluster)
            {
                var exactHours = Math.Clamp(item.HoursAhead, 0, spanHours);
                var lineTurn = exactHours / Math.Max(spanHours, 0.001) * Math.PI * 2;
                var tipPt = PolarPoint(cx, cy, rim, lineTurn);
                Face.Children.Add(new Line
                {
                    X1 = cx,
                    Y1 = cy,
                    X2 = tipPt.X,
                    Y2 = tipPt.Y,
                    Stroke = lineBrush,
                    StrokeThickness = 1.75,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false
                });
            }

            _hotspots.Add(new WedgeHotspot { Geometry = geo, Tip = tip });
        }
    }

    static string BuildClusterTip(
        List<(string Title, double HoursAhead, string Kind, DateTime At, double? UsedPercent)> cluster,
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
                .Append(TypeLabel(item.Kind))
                .Append(" · ")
                .Append(FormatClockTime(item.At, use12, withSeconds: false));
            // auto model
            var remain = RecurrenceEngine.FormatRemainingLong(item.At, now);
            if (item.HoursAhead <= 0.02 || string.Equals(remain, "now", StringComparison.Ordinal))
                sb.Append(" · now");
            else
                sb.Append(" · in ").Append(remain);
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

    // Returns Y of the chevron tip (for placing the time just under it).
    // Drawn on Marks so it stays above wedges and the second hand.
    void DrawNowMark(double cx, double cy, double rim, AppSettings settings)
    {
        var tipY = cy - rim + Math.Max(6, rim * 0.07);
        var baseY = cy - rim;
        var half = Math.Max(3.5, rim * 0.03);
        var fill = BrushFromHex(ColorText.NormalizeOr(settings.ClockColorCaret, FallbackCaret), 255);
        Marks.Children.Add(new Polygon
        {
            Points =
            [
                new Point(cx, tipY),
                new Point(cx - half, baseY),
                new Point(cx + half, baseY)
            ],
            Fill = fill,
            IsHitTestVisible = false
        });
    }

    void UpdateHoverTip()
    {
        // Don't tear down the tip while wedges are being rebuilt.
        if (_buildingFace)
            return;
        if (_hotspots.Count == 0 || !NativeMethods.TryGetCursorInWindow(this, out var winPt))
        {
            HideTip();
            return;
        }

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

        string? tip = null;
        for (var i = _hotspots.Count - 1; i >= 0; i--)
        {
            if (_hotspots[i].Geometry.FillContains(facePt))
            {
                tip = _hotspots[i].Tip;
                break;
            }
        }

        if (tip == null)
        {
            HideTip();
            return;
        }

        if (!NativeMethods.GetCursorPos(out var screen))
        {
            HideTip();
            return;
        }

        if (!string.Equals(_openTip, tip, StringComparison.Ordinal))
        {
            _tipText.Text = tip;
            _openTip = tip;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        _tipPopup.HorizontalOffset = screen.X / (dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX) + 14;
        _tipPopup.VerticalOffset = screen.Y / (dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY) + 18;
        if (!_tipPopup.IsOpen)
            _tipPopup.IsOpen = true;
    }

    void HideTip()
    {
        _openTip = null;
        if (_tipPopup.IsOpen)
            _tipPopup.IsOpen = false;
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

        double width = ft.Width;
        double height = ft.Height;
        var nearCenter = cy - height / 2;
        var left = cx - width / 2;
        var top = nearChevron + (nearCenter - nearChevron) * pos;

        if (settings.ClockTimeOutline)
        {
            var geo = ft.BuildGeometry(new Point(0, 0));
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

        var block = new TextBlock
        {
            Text = text,
            Foreground = fill,
            FontSize = fontSize,
            FontWeight = weight,
            FontFamily = SystemFonts.MessageFontFamily,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        Marks.Children.Add(block);
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
        var hourStride = ClockGearGeometry.FaceHourToothStride;
        var trainOp = Math.Clamp(settings.ClockGearOpacity, 0.05, 1);
        const byte solidA = 255;
        var ringHex = ColorText.NormalizeOr(settings.ClockGearColor, "#7A8088");
        var compoundHex = DrivePartHex(settings.ClockGearCompoundColor, DarkenHex(ringHex, 0.55));
        var planetHex = DrivePartHex(settings.ClockGearPlanetColor, ringHex);
        var idlerHex = DrivePartHex(settings.ClockGearIdlerColor, ringHex);
        var sunHex = DrivePartHex(settings.ClockGearSunColor, DarkenHex(ringHex, 0.72));
        var fill = BrushFromHex(ringHex, solidA);
        var innerDimFill = BrushFromHex(ringHex, solidA);
        var planetFill = BrushFromHex(planetHex, solidA);
        var compoundFill = BrushFromHex(compoundHex, solidA);
        var hourFill = BrushFromHex(
            ColorText.NormalizeOr(settings.ClockColorMajorTick, FallbackMajorTick), 0xCC);

        var ratio = ClockGearGeometry.FaceToSecondRatio(_gearHorizonHours);
        var ringSpeed = 1.0 / ratio;
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

        // Mask to the clock rim — compound gears may extend past it.
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

        Canvas MakeRingStack(bool dimRing)
        {
            var stack = new Canvas
            {
                Width = hostW,
                Height = hostH,
                Opacity = dimRing ? trainOp : 1,
                IsHitTestVisible = false,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false
            };
            gearLayer.Children.Add(stack);
            return stack;
        }

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
        var midClip = StageClip(rim, innerBand.OuterRoot);
        var backLayer = MakeCarrierLayer(midClip, c0, behind: false);

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
        AddCarriageTracks(
            backLayer, cx, cy, layout,
            seats0.ConvertAll(s => (s.Px, s.Py, s.Idlers)), carriageBrush, settings, 0);

        for (var i = 0; i < seats0.Count; i++)
        {
            var (px, py, _, _, alignDeg) = seats0[i];
            AddRotated(
                ClockGearGeometry.BuildCompoundGear(px, py, layout),
                compoundFill, planetSpin, alignDeg, px, py, host: backLayer);
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
            fill, ringSpeed, 0, cx, cy);

        AddRotated(
            ClockGearGeometry.BuildInternalHourToothAccents(
                cx, cy, layout, ringTeeth, hourStride),
            hourFill, ringSpeed, 0, cx, cy);

        var stack0 = MakeRingStack(innerFill != sunFill);
        AddRotated(
            ClockGearGeometry.BuildInnerRingExteriorTeeth(cx, cy, layout, inner),
            innerFill, innerSpin, innerAlignDeg, cx, cy, host: stack0);

        var frontLayer = MakeCarrierLayer(
            midClip, c0, behind: false, parent: stack0, dim: innerFill == sunFill);
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

        AddRotated(
            ClockGearGeometry.BuildInnerRingMaskCore(cx, cy, layout, inner),
            innerFill, innerSpin, innerAlignDeg, cx, cy, host: stack0);
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

        // Stage-2: carriage+compound behind already-drawn ring-1; idlers/planet on top.
        var stage2Clip = StageClip(innerBand.OuterRoot, inner2Band.OuterRoot);
        var backLayer2 = MakeCarrierLayer(stage2Clip, c1, behind: true);

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
        AddCarriageTracks(backLayer2, cx, cy, layout2, seats2, carriageBrush, settings, 1);

        for (var i = 0; i < planetCount1; i++)
        {
            var localAng = ClockGearGeometry.PlanetCarrierAngle(i, planetCount1, hostTeeth2, offset1Deg);
            var ang = localAng + frameDelta2;
            var px = cx + layout2.CarrierRadius * Math.Cos(ang);
            var py = cy + layout2.CarrierRadius * Math.Sin(ang);
            var alignRad = ClockGearGeometry.PlanetAlignAngle(localAng, planetTeeth2, hostTeeth2) + frameDelta2;
            var alignDeg = alignRad * deg;
            AddRotated(
                ClockGearGeometry.BuildCompoundGear(px, py, layout2),
                compoundFill, planetSpin2, alignDeg, px, py, host: backLayer2);
            if (showSpinMarks && spinMarkStroke != null)
            {
                var r = layout2.CompoundTipR;
                var diameter = new LineGeometry(new Point(px - r, py), new Point(px + r, py));
                diameter.Freeze();
                AddRotated(diameter, null, planetSpin2, alignDeg, px, py, spinMarkStroke, 1.4, backLayer2);
            }
        }

        var stack2 = MakeRingStack(inner2Fill != sunFill);
        AddRotated(
            ClockGearGeometry.BuildInnerRingExteriorTeeth(cx, cy, layout2, inner2),
            inner2Fill, inner2Spin, inner2AlignDeg, cx, cy, host: stack2);

        var frontLayer2 = MakeCarrierLayer(
            stage2Clip, c1, behind: false, parent: stack2, dim: inner2Fill == sunFill);
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

        AddRotated(
            ClockGearGeometry.BuildInnerRingMaskCore(cx, cy, layout2, inner2),
            inner2Fill, inner2Spin, inner2AlignDeg, cx, cy, host: stack2);
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

        var stage3Clip = StageClip(inner2Band.OuterRoot, inner3Band.OuterRoot);
        var backLayer3 = MakeCarrierLayer(stage3Clip, c2, behind: true);

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
        AddCarriageTracks(backLayer3, cx, cy, layout3, seats3, carriageBrush, settings, 2);

        for (var i = 0; i < planetCount2; i++)
        {
            var localAng = ClockGearGeometry.PlanetCarrierAngle(i, planetCount2, hostTeeth3, offset2Deg);
            var ang = localAng + frameDelta3;
            var px = cx + layout3.CarrierRadius * Math.Cos(ang);
            var py = cy + layout3.CarrierRadius * Math.Sin(ang);
            var alignRad = ClockGearGeometry.PlanetAlignAngle(localAng, planetTeeth3, hostTeeth3) + frameDelta3;
            var alignDeg = alignRad * deg;
            AddRotated(
                ClockGearGeometry.BuildCompoundGear(px, py, layout3),
                compoundFill, planetSpin3, alignDeg, px, py, host: backLayer3);
            if (showSpinMarks && spinMarkStroke != null)
            {
                var r = layout3.CompoundTipR;
                var diameter = new LineGeometry(new Point(px - r, py), new Point(px + r, py));
                diameter.Freeze();
                AddRotated(diameter, null, planetSpin3, alignDeg, px, py, spinMarkStroke, 1.4, backLayer3);
            }
        }

        var stack3 = MakeRingStack(inner3Fill != sunFill);
        AddRotated(
            ClockGearGeometry.BuildInnerRingExteriorTeeth(cx, cy, layout3, inner3),
            inner3Fill, inner3Spin, inner3AlignDeg, cx, cy, host: stack3);

        var frontLayer3 = MakeCarrierLayer(
            stage3Clip, c2, behind: false, parent: stack3, dim: inner3Fill == sunFill);
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

        AddRotated(
            ClockGearGeometry.BuildInnerRingMaskCore(cx, cy, layout3, inner3),
            inner3Fill, inner3Spin, inner3AlignDeg, cx, cy, host: stack3);
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
            var clipN = StageClip(prevBand.OuterRoot, band.OuterRoot);
            var backN = MakeCarrierLayer(clipN, cN, behind: true);
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
            AddCarriageTracks(backN, cx, cy, lay, seatsN, carriageBrush, settings, s);
            for (var i = 0; i < pCount; i++)
            {
                var localAng = ClockGearGeometry.PlanetCarrierAngle(i, pCount, lay.RingTeeth, pOff);
                var ang = localAng + frameN;
                var px = cx + lay.CarrierRadius * Math.Cos(ang);
                var py = cy + lay.CarrierRadius * Math.Sin(ang);
                var alignRad = ClockGearGeometry.PlanetAlignAngle(localAng, lay.PlanetTeeth, lay.RingTeeth) + frameN;
                var alignDegN = alignRad * deg;
                AddRotated(ClockGearGeometry.BuildCompoundGear(px, py, lay),
                    compoundFill, planetSpinN, alignDegN, px, py, host: backN);
            }
            var stackN = MakeRingStack(innerFillN != sunFill);
            AddRotated(
                ClockGearGeometry.BuildInnerRingExteriorTeeth(cx, cy, lay, fit),
                innerFillN, innerSpinN, innerAlignN, cx, cy, host: stackN);
            var frontN = MakeCarrierLayer(
                clipN, cN, behind: false, parent: stackN, dim: innerFillN == sunFill);
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
            AddRotated(
                ClockGearGeometry.BuildInnerRingMaskCore(cx, cy, lay, fit),
                innerFillN, innerSpinN, innerAlignN, cx, cy, host: stackN);
            extraFits.Add(fit);
            extraSpins.Add(innerSpinN);
            prevBand = band;
            prevFit = fit;
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

    static void AddCarriageTracks(
        Canvas host, double cx, double cy,
        ClockGearGeometry.FaceMeshLayout layout,
        IReadOnlyList<(double Px, double Py, (double X, double Y)[] Idlers)> seats,
        Brush brush, AppSettings settings, int stage)
    {
        if (settings.ClockGearHideCarousel || seats.Count == 0)
            return;
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
            or ClockGearCarriageKind.Fan;

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
                }
                WriteCircle(ctx, px, py, hubHole, clockwise: true);
                for (var j = 0; j < idlers.Length; j++)
                    WriteCircle(ctx, idlers[j].X, idlers[j].Y, idlerHole, clockwise: true);
            }
        }
        geo.Freeze();
        host.Children.Add(new Path
        {
            Data = geo,
            Fill = brush,
            IsHitTestVisible = false,
            SnapsToDevicePixels = false
        });
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
            _gearRots[i].Angle = _gearBaseDeg[i] + sec * _gearSecScale[i] * speed * deg;
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
