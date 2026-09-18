using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using AtFutureMe.Models;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

// auto model: shared meters UI for floating Usage overlay and reminders dock
static class UsagePanelContent
{
    static readonly Dictionary<string, SolidColorBrush> Brushes = new(StringComparer.OrdinalIgnoreCase);
    const string PlainGray = "#9AA0A8";
    const string NameGray = "#E8EAED";
    const string TipBg = "#202228";
    const string TipBorder = "#3A3D45";

    // auto model: skip rebuild while hovering if nothing meaningful changed
    public static string CompactSignature(AppSettings s, UsageSnapshot? snap)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(s.ClickThrough ? 'L' : 'U')
            .Append(s.UsageDockPlainGray ? 'G' : 'T')
            .Append(s.UsageDockShowPercent ? 'P' : 'N')
            .Append(s.UsageShowClaude ? 'C' : '-')
            .Append(s.UsageShowCursor ? 'R' : '-');
        if (snap == null)
            return sb.Append("|null").ToString();
        sb.Append('|').Append(snap.FetchedAt.Ticks);
        if (snap.Claude?.FiveHour is { } f)
            sb.Append("|5:").Append(f.UsedPercent.ToString("0.#")).Append('@').Append(f.ResetsAt?.Ticks);
        if (snap.Claude?.SevenDay is { } w)
            sb.Append("|7:").Append(w.UsedPercent.ToString("0.#")).Append('@').Append(w.ResetsAt?.Ticks);
        if (snap.Claude?.SevenDayOpus is { } o)
            sb.Append("|o:").Append(o.UsedPercent.ToString("0.#")).Append('@').Append(o.ResetsAt?.Ticks);
        if (snap.Cursor is { } c)
            sb.Append("|c:").Append(c.TotalPercentUsed?.ToString("0.#")).Append('@').Append(c.BillingCycleEnd?.Ticks);
        return sb.ToString();
    }

    public static void Fill(StackPanel meters, bool compact = false)
    {
        meters.Children.Clear();
        var host = AppHost.Instance;
        var s = host.Data.Settings;
        var snap = host.Usage?.Latest;
        var now = DateTime.Now;
        var weight = host.OverlayChromeWeight();

        if (compact)
            Grid.SetIsSharedSizeScope(meters, true);

        if (snap == null)
        {
            AddMuted(meters, "Fetching usage…", compact);
            return;
        }

        if (s.UsageShowClaude)
        {
            if (!compact)
                AddHeader(meters, "Claude", ColorText.NormalizeOr(s.UsageColorClaude, "#D4A574"), weight);
            if (snap.ClaudeError != null && snap.Claude == null)
                AddMuted(meters, snap.ClaudeError, compact);
            else if (snap.Claude != null)
            {
                AddMeter(meters, compact ? "Claude 5h" : "5-hour session", snap.Claude.FiveHour, now, weight,
                    TimeSpan.FromHours(5), compact, s);
                AddMeter(meters, compact ? "Claude week" : "Weekly", snap.Claude.SevenDay, now, weight,
                    TimeSpan.FromDays(7), compact, s);
                if (snap.Claude.SevenDayOpus != null)
                    AddMeter(meters, compact ? "Claude Opus" : "Weekly Opus", snap.Claude.SevenDayOpus, now, weight,
                        TimeSpan.FromDays(7), compact, s);
                if (snap.ClaudeError != null)
                    AddMuted(meters, snap.ClaudeError, compact);
            }
            else
                AddMuted(meters, "No Claude data yet", compact);
        }

        if (s.UsageShowCursor)
        {
            if (!compact)
                AddHeader(meters, "Cursor", ColorText.NormalizeOr(s.UsageColorCursor, "#8AB4F8"), weight);
            if (snap.CursorError != null && snap.Cursor == null)
                AddMuted(meters, snap.CursorError, compact);
            else if (snap.Cursor != null)
            {
                var used = snap.Cursor.TotalPercentUsed ?? 0;
                var remain = Math.Clamp(100 - used, 0, 100);
                var reset = snap.Cursor.BillingCycleEnd is DateTime end
                    ? UsageTracker.FormatCountdown(end, now)
                    : "—";
                if (compact)
                {
                    var tip = snap.Cursor.BillingCycleStart is DateTime start
                        && snap.Cursor.BillingCycleEnd is DateTime cycleEnd
                            ? UsageTracker.FormatPaceHint(used, now, start, cycleEnd)
                            : null;
                    AddCompactRow(meters, "Cursor", used, reset, weight, s, tip);
                }
                else
                {
                    var sub = $"Resets in {reset}";
                    if (snap.Cursor.BillingCycleStart is DateTime start
                        && snap.Cursor.BillingCycleEnd is DateTime cycleEnd)
                    {
                        var pace = UsageTracker.FormatPaceHint(used, now, start, cycleEnd);
                        if (!string.IsNullOrEmpty(pace))
                            sub = $"{sub}\n{pace}";
                    }
                    AddMeterRow(meters, "Plan remaining", $"{remain:0}%", used, sub, weight);
                    if (!string.IsNullOrWhiteSpace(snap.Cursor.DisplayMessage))
                        AddMuted(meters, snap.Cursor.DisplayMessage!, compact);
                }

                if (snap.CursorError != null)
                    AddMuted(meters, snap.CursorError, compact);
            }
            else
                AddMuted(meters, "No Cursor data yet", compact);
        }

        if (!compact)
            AddMuted(meters, $"Updated {snap.FetchedAt:t}", compact);
    }

    static void AddHeader(StackPanel meters, string text, string hex, FontWeight weight)
    {
        meters.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brush(hex),
            FontWeight = weight,
            FontSize = Math.Max(12, AppHost.Instance.Data.Settings.FontSize),
            Margin = new Thickness(0, meters.Children.Count == 0 ? 0 : 10, 0, 4),
            IsHitTestVisible = false
        });
    }

    static void AddMuted(StackPanel meters, string text, bool compact)
    {
        meters.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brush(PlainGray),
            FontSize = compact ? 10 : 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, compact ? 1 : 2, 0, compact ? 1 : 2),
            IsHitTestVisible = false
        });
    }

    static void AddMeter(
        StackPanel meters, string label, UsageWindow? window, DateTime now, FontWeight weight,
        TimeSpan assumedLength, bool compact, AppSettings s)
    {
        if (window == null)
        {
            if (compact)
                AddCompactRow(meters, label, 0, "—", weight, s, null);
            else
                AddMeterRow(meters, label, "—", 0, "Not started", weight);
            return;
        }

        var reset = window.ResetsAt is DateTime at
            ? UsageTracker.FormatCountdown(at, now)
            : "—";

        if (compact)
        {
            var tip = UsageTracker.FormatWindowPaceHint(window, now, assumedLength);
            AddCompactRow(meters, label, window.UsedPercent, reset, weight, s, tip);
            return;
        }

        var remain = window.RemainingPercent;
        var sub = $"Resets in {reset}";
        var paceHint = UsageTracker.FormatWindowPaceHint(window, now, assumedLength);
        if (!string.IsNullOrEmpty(paceHint))
            sub = $"{sub}\n{paceHint}";
        AddMeterRow(meters, label, $"{remain:0}% left", window.UsedPercent, sub, weight);
    }

    // auto model: name | equal-length bar | optional % | time — shared columns across rows
    static void AddCompactRow(
        StackPanel meters, string label, double usedPercent, string reset, FontWeight weight, AppSettings s,
        string? tip)
    {
        var font = Math.Max(10, AppHost.Instance.Data.Settings.FontSize - 2);
        var tintHex = UsageTracker.UsageTintHex(usedPercent);
        var textHex = s.UsageDockPlainGray ? PlainGray : tintHex;
        var textBrush = Brush(textHex);
        var showPct = s.UsageDockShowPercent;
        var unlocked = !AppHost.Instance.Data.Settings.ClickThrough;

        var grid = new Grid
        {
            Margin = new Thickness(0, 2, 0, 2),
            Background = System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = unlocked
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
            SharedSizeGroup = "UsageDockName"
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        if (showPct)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto,
                SharedSizeGroup = "UsageDockPct"
            });
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
            SharedSizeGroup = "UsageDockTime",
            MinWidth = 52
        });

        var name = new TextBlock
        {
            Text = label,
            Foreground = Brush(NameGray),
            FontSize = font,
            FontWeight = weight,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            IsHitTestVisible = false
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var track = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(2),
            Background = Brush("#2A2E36"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 48,
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        var fillW = Math.Clamp(usedPercent, 0, 100) / 100.0;
        track.Child = new Grid
        {
            Children =
            {
                new Rectangle
                {
                    Fill = Brush(tintHex),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 1,
                    RadiusX = 2,
                    RadiusY = 2,
                    IsHitTestVisible = false
                }
            }
        };
        track.SizeChanged += (_, _) =>
        {
            if (track.Child is Grid g && g.Children[0] is Rectangle r)
                r.Width = Math.Max(0, track.ActualWidth * fillW);
        };
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);

        var col = 2;
        if (showPct)
        {
            var pct = new TextBlock
            {
                Text = $"{Math.Clamp(usedPercent, 0, 100):0}%",
                Foreground = textBrush,
                FontSize = font,
                FontWeight = weight,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                TextAlignment = TextAlignment.Right,
                IsHitTestVisible = false
            };
            Grid.SetColumn(pct, col++);
            grid.Children.Add(pct);
        }

        var time = new TextBlock
        {
            Text = reset,
            Foreground = textBrush,
            FontSize = font,
            FontWeight = weight,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            IsHitTestVisible = false
        };
        Grid.SetColumn(time, col);
        grid.Children.Add(time);

        if (unlocked && !string.IsNullOrWhiteSpace(tip))
        {
            // Keep tip near the cursor but ignore hits so moving down reaches the next row
            // (hit-testable tips sit over siblings and trap the pointer).
            var toolTip = new ToolTip
            {
                Background = Brush(TipBg),
                BorderBrush = Brush(TipBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                HasDropShadow = false,
                Placement = PlacementMode.Mouse,
                VerticalOffset = 14,
                HorizontalOffset = 8,
                IsHitTestVisible = false,
                Content = new TextBlock
                {
                    Text = tip,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 280,
                    Foreground = Brush(NameGray),
                    FontSize = Math.Max(11, font)
                }
            };
            grid.ToolTip = toolTip;
            ToolTipService.SetInitialShowDelay(grid, 350);
            ToolTipService.SetBetweenShowDelay(grid, 0);
            ToolTipService.SetShowDuration(grid, 60000);
        }

        meters.Children.Add(grid);
    }

    static void AddMeterRow(
        StackPanel meters, string label, string value, double usedPercent, string sub, FontWeight weight)
    {
        var font = Math.Max(11, AppHost.Instance.Data.Settings.FontSize - 1);
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 2), IsHitTestVisible = false };
        var valueBlock = new TextBlock
        {
            Text = value,
            Foreground = Brush(UsageTracker.UsageTintHex(usedPercent)),
            FontWeight = weight,
            FontSize = font,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(valueBlock, Dock.Right);
        row.Children.Add(valueBlock);
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brush(NameGray),
            FontSize = font,
            FontWeight = weight
        });
        meters.Children.Add(row);

        var track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Brush("#2A2E36"),
            Margin = new Thickness(0, 2, 0, 0),
            IsHitTestVisible = false,
            ClipToBounds = true
        };
        var fillW = Math.Clamp(usedPercent, 0, 100) / 100.0;
        track.Child = new Grid
        {
            Children =
            {
                new Rectangle
                {
                    Fill = Brush(UsageTracker.UsageTintHex(usedPercent)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 1,
                    RadiusX = 3,
                    RadiusY = 3
                }
            }
        };
        track.SizeChanged += (_, _) =>
        {
            if (track.Child is Grid g && g.Children[0] is Rectangle r)
                r.Width = Math.Max(0, track.ActualWidth * fillW);
        };
        meters.Children.Add(track);

        meters.Children.Add(new TextBlock
        {
            Text = sub,
            Foreground = Brush(PlainGray),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 4),
            IsHitTestVisible = false
        });
    }

    static SolidColorBrush Brush(string hex)
    {
        if (Brushes.TryGetValue(hex, out var cached))
            return cached;
        var color = (Color)ColorConverter.ConvertFromString(hex)!;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Brushes[hex] = brush;
        return brush;
    }
}
