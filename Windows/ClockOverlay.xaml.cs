using System.Globalization;
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
    bool _rendering;
    double _handCx;
    double _handCy;
    double _handRadius;
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
        _faceTick.Tick += (_, _) => Refresh();
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
        if (!IsVisible || !IsLoaded)
            return;
        if (AppHost.Instance.Data.Settings.ClockShowSecondHand)
            UpdateSecondHand(DateTime.Now);
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

    public void Refresh()
    {
        if (!IsLoaded)
            return;
        _buildingFace = true;
        try
        {
            RefreshCore();
        }
        finally
        {
            _buildingFace = false;
        }
    }

    void RefreshCore()
    {
        var now = DateTime.Now;
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

        // When locked/semi-locked, optionally keep a darkened disk so the face still reads.
        // Blank ClockColorFace follows the overlay panel brush (same as before face-color options).
        if (mode != ClockLockMode.Unlocked && settings.ClockKeepFaceWhenLocked)
        {
            Brush faceFill = ColorText.TryNormalize(settings.ClockColorFace, out var faceHex)
                ? BrushFromHex(faceHex, 0xF2)
                : host.PanelBrush();
            var disk = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = faceFill,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(disk, cx - radius);
            Canvas.SetTop(disk, cy - radius);
            Face.Children.Add(disk);
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

        DrawEventWedges(host.ClockEvents(now), now, spanHours, cx, cy, rim, use12);
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

        if (settings.ClockShowHourHand)
            DrawHourHand(cx, cy, rim, settings);

        if (settings.ClockShowSecondHand)
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
        var msIntoMinute = now.TimeOfDay.TotalMilliseconds % 60_000.0;
        if (msIntoMinute < 0)
            msIntoMinute += 60_000.0;
        var turn = msIntoMinute / 60_000.0 * (Math.PI * 2.0);
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
