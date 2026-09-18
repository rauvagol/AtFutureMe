using System.Net.Http;
using System.Windows.Threading;
using AtFutureMe.Models;

namespace AtFutureMe.Services;

// auto model
public sealed class UsageTracker : IDisposable
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly Dispatcher _dispatcher;
    readonly object _gate = new();
    CancellationTokenSource? _loopCts;
    Task? _loop;
    DateTime _claudeBackoffUntil = DateTime.MinValue;
    DateTime _nextPoll = DateTime.MinValue;

    public UsageSnapshot? Latest { get; private set; }
    public event Action? Changed;

    public UsageTracker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        if (_loop != null)
            return;
        _loopCts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_loopCts.Token));
    }

    public void RequestRefreshSoon()
    {
        lock (_gate)
            _nextPoll = DateTime.UtcNow;
    }

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Keep last-good snapshot; retry on next interval.
            }

            var minutes = 15;
            try
            {
                minutes = Math.Clamp(AppHost.Instance.Data.Settings.UsagePollMinutes, 5, 120);
            }
            catch { /* host may be disposing */ }

            DateTime waitUntil;
            lock (_gate)
            {
                _nextPoll = DateTime.UtcNow.AddMinutes(minutes);
                waitUntil = _nextPoll;
            }

            while (!ct.IsCancellationRequested)
            {
                DateTime target;
                lock (_gate)
                    target = _nextPoll;
                var delay = target - DateTime.UtcNow;
                if (delay <= TimeSpan.Zero)
                    break;
                var slice = delay > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay;
                try { await Task.Delay(slice, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                // waitUntil unused except to stabilize; re-check _nextPoll each slice
                _ = waitUntil;
            }
        }
    }

    async Task PollOnceAsync(CancellationToken ct)
    {
        var settings = AppHost.Instance.Data.Settings;
        ClaudeUsageInfo? claude = Latest?.Claude;
        CursorUsageInfo? cursor = Latest?.Cursor;
        string? claudeErr = null;
        string? cursorErr = null;

        if (settings.UsageShowClaude)
        {
            if (DateTime.UtcNow < _claudeBackoffUntil)
                claudeErr = Latest?.ClaudeError ?? "Claude usage backing off after rate limit";
            else
            {
                var (info, err) = await ClaudeUsageClient.FetchAsync(_http, ct).ConfigureAwait(false);
                if (info != null)
                {
                    claude = info;
                    claudeErr = null;
                }
                else
                {
                    claudeErr = err;
                    if (err != null && err.Contains("rate-limited", StringComparison.OrdinalIgnoreCase))
                        _claudeBackoffUntil = DateTime.UtcNow.AddMinutes(30);
                }
            }
        }

        if (settings.UsageShowCursor)
        {
            var (info, err) = await CursorUsageClient.FetchAsync(_http, ct).ConfigureAwait(false);
            if (info != null)
            {
                cursor = info;
                cursorErr = null;
            }
            else
                cursorErr = err;
        }

        var snap = new UsageSnapshot
        {
            FetchedAt = DateTime.Now,
            Claude = settings.UsageShowClaude ? claude : null,
            Cursor = settings.UsageShowCursor ? cursor : null,
            ClaudeError = settings.UsageShowClaude ? claudeErr : null,
            CursorError = settings.UsageShowCursor ? cursorErr : null
        };

        Latest = snap;
        try
        {
            _ = _dispatcher.BeginInvoke(() => Changed?.Invoke());
        }
        catch
        {
            // Dispatcher may be shutting down.
        }
    }

    public static List<UsageResetEvent> BuildResetEvents(UsageSnapshot? snap, AppSettings settings, DateTime now)
    {
        var list = new List<UsageResetEvent>();
        if (snap == null)
            return list;

        if (settings.UsageShowClaude && snap.Claude != null)
        {
            AddWindow(list, "usage-claude-5h", "Claude 5h reset", snap.Claude.FiveHour, now);
            AddWindow(list, "usage-claude-week", "Claude weekly reset", snap.Claude.SevenDay, now);
            AddWindow(list, "usage-claude-opus", "Claude Opus weekly reset", snap.Claude.SevenDayOpus, now);
        }

        if (settings.UsageShowCursor && snap.Cursor?.BillingCycleEnd is DateTime end && end > now)
        {
            list.Add(new UsageResetEvent
            {
                Id = "usage-cursor-billing",
                Kind = "usage-cursor",
                Title = "Cursor billing reset",
                At = end,
                UsedPercent = snap.Cursor.TotalPercentUsed ?? 0
            });
        }

        return list;
    }

    /// <summary>
    /// Board visibility with smart filter:
    /// - At/near limit: only within per-meter lead hours of reset.
    /// - Otherwise (weekly/cursor): when hours-until-reset &lt;= (remaining% / fullSession%) * 5h * cushion.
    /// - 5h not-at-limit: when mostly spent or nearly over.
    /// </summary>
    // auto model
    public static bool ShouldShowOnRemindersBoard(UsageResetEvent reset, AppSettings settings, DateTime now)
    {
        if (!settings.UsageBoardSmartFilter)
            return true;

        var hoursLeft = (reset.At - now).TotalHours;
        if (hoursLeft <= 0)
            return true;

        var used = Math.Clamp(reset.UsedPercent, 0, 100);
        if (used >= 99.5)
            return hoursLeft <= AtLimitLeadHours(reset, settings, forClock: false);

        var remaining = 100 - used;

        // Short session window: only surface when this 5h block is mostly spent or almost over.
        if (IsFiveHour(reset))
            return used >= 80 || hoursLeft <= 2;

        var sessionPct = Math.Clamp(
            settings.UsageFullSessionWeeklyPercent <= 0 ? 10 : settings.UsageFullSessionWeeklyPercent,
            1, 100);
        var cushion = Math.Clamp(
            settings.UsageReminderCushion <= 0 ? 2 : settings.UsageReminderCushion,
            1, 10);
        var fullSessionsLeft = remaining / sessionPct;
        var alertHorizonHours = fullSessionsLeft * 5.0 * cushion;
        return hoursLeft <= alertHorizonHours;
    }

    /// <summary>
    /// Clock wedge visibility for at-limit meters uses separate lead hours from the board.
    /// Not-at-limit usage markers still show whenever they fall inside the clock horizon.
    /// </summary>
    // auto model
    public static bool ShouldShowOnClock(UsageResetEvent reset, AppSettings settings, DateTime now)
    {
        if (!settings.UsageShowOnClock)
            return false;

        var hoursLeft = (reset.At - now).TotalHours;
        if (hoursLeft <= 0)
            return true;

        var used = Math.Clamp(reset.UsedPercent, 0, 100);
        if (used >= 99.5)
            return hoursLeft <= AtLimitLeadHours(reset, settings, forClock: true);

        return true;
    }

    static bool IsFiveHour(UsageResetEvent reset) =>
        reset.Id == "usage-claude-5h" || reset.Kind == "usage-claude-5h";

    static double AtLimitLeadHours(UsageResetEvent reset, AppSettings settings, bool forClock)
    {
        double hours = reset.Id switch
        {
            "usage-claude-5h" => forClock
                ? settings.UsageClockAtLimitLeadHours5h
                : settings.UsageAtLimitLeadHours5h,
            "usage-claude-week" => forClock
                ? settings.UsageClockAtLimitLeadHoursWeekly
                : settings.UsageAtLimitLeadHoursWeekly,
            "usage-claude-opus" => forClock
                ? settings.UsageClockAtLimitLeadHoursOpus
                : settings.UsageAtLimitLeadHoursOpus,
            "usage-cursor-billing" => forClock
                ? settings.UsageClockAtLimitLeadHoursCursor
                : settings.UsageAtLimitLeadHoursCursor,
            _ when reset.Kind.StartsWith("usage-cursor", StringComparison.Ordinal)
                => forClock ? settings.UsageClockAtLimitLeadHoursCursor : settings.UsageAtLimitLeadHoursCursor,
            _ when reset.Kind.Contains("opus", StringComparison.OrdinalIgnoreCase)
                => forClock ? settings.UsageClockAtLimitLeadHoursOpus : settings.UsageAtLimitLeadHoursOpus,
            _ when reset.Kind.Contains("week", StringComparison.OrdinalIgnoreCase)
                => forClock ? settings.UsageClockAtLimitLeadHoursWeekly : settings.UsageAtLimitLeadHoursWeekly,
            _ => forClock ? settings.UsageClockAtLimitLeadHoursWeekly : settings.UsageAtLimitLeadHoursWeekly
        };
        if (hours <= 0)
            hours = forClock ? 5 : 1;
        return Math.Clamp(hours, 0.25, 14 * 24);
    }

    static void AddWindow(List<UsageResetEvent> list, string id, string title, UsageWindow? window, DateTime now)
    {
        if (window?.ResetsAt is not DateTime at || at <= now)
            return;
        list.Add(new UsageResetEvent
        {
            Id = id,
            Kind = id.StartsWith("usage-claude", StringComparison.Ordinal) ? id : "usage-claude",
            Title = title,
            At = at,
            UsedPercent = window.UsedPercent
        });
    }

    /// <summary>Green (0% used) → red (100% used).</summary>
    public static string UsageTintHex(double usedPercent)
    {
        var t = Math.Clamp(usedPercent, 0, 100) / 100.0;
        // #5DDC8A → #FF5A5A
        var r = (int)Math.Round(0x5D + (0xFF - 0x5D) * t);
        var g = (int)Math.Round(0xDC + (0x5A - 0xDC) * t);
        var b = (int)Math.Round(0x8A + (0x5A - 0x8A) * t);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    // auto model
    public static string BoardColorHex(UsageResetEvent reset, AppSettings settings)
    {
        var (mode, fixedHex, fallback) = BoardColorSettings(reset, settings);
        return mode switch
        {
            UsageBoardColorMode.Fixed => ColorText.NormalizeOr(fixedHex, fallback),
            UsageBoardColorMode.Default => ColorText.Idle,
            _ => UsageTintHex(reset.UsedPercent)
        };
    }

    static (UsageBoardColorMode Mode, string FixedHex, string Fallback) BoardColorSettings(
        UsageResetEvent reset, AppSettings settings) =>
        reset.Id switch
        {
            "usage-claude-5h" => (settings.UsageBoardColorMode5h, settings.UsageBoardColor5h, "#D4A574"),
            "usage-claude-week" => (settings.UsageBoardColorModeWeekly, settings.UsageBoardColorWeekly, "#D4A574"),
            "usage-claude-opus" => (settings.UsageBoardColorModeOpus, settings.UsageBoardColorOpus, "#D4A574"),
            "usage-cursor-billing" => (settings.UsageBoardColorModeCursor, settings.UsageBoardColorCursor, "#8AB4F8"),
            _ when reset.Kind.StartsWith("usage-cursor", StringComparison.Ordinal)
                => (settings.UsageBoardColorModeCursor, settings.UsageBoardColorCursor, "#8AB4F8"),
            _ when reset.Kind.Contains("opus", StringComparison.OrdinalIgnoreCase)
                => (settings.UsageBoardColorModeOpus, settings.UsageBoardColorOpus, "#D4A574"),
            _ when reset.Kind.Contains("week", StringComparison.OrdinalIgnoreCase)
                => (settings.UsageBoardColorModeWeekly, settings.UsageBoardColorWeekly, "#D4A574"),
            _ => (settings.UsageBoardColorMode5h, settings.UsageBoardColor5h, "#D4A574")
        };

    public static string FormatCountdown(DateTime at, DateTime now)
    {
        var span = at - now;
        if (span <= TimeSpan.Zero)
            return "now";
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    /// <summary>
    /// Project when the bar hits 100% at the current average burn rate over [start, now].
    /// Returns null when there isn't enough signal yet.
    /// </summary>
    // auto model
    public static string? FormatPaceHint(
        double usedPercent, DateTime now, DateTime periodStart, DateTime periodEnd)
    {
        if (periodEnd <= periodStart || now >= periodEnd)
            return null;

        var used = Math.Clamp(usedPercent, 0, 100);
        if (used >= 99.5)
            return "Limit reached — waiting on reset";

        var elapsed = now - periodStart;
        var period = periodEnd - periodStart;
        if (elapsed.TotalMinutes < 15 || elapsed < TimeSpan.FromTicks(period.Ticks / 40))
            return "Too early to project pace";

        if (used < 1)
            return "Barely started — pace looks fine";

        // Time to burn the remaining % at the average rate so far.
        var remain = 100 - used;
        var hoursToExhaust = elapsed.TotalHours * (remain / used);
        var exhaustAt = now.AddHours(hoursToExhaust);
        var early = periodEnd - exhaustAt;

        if (early.TotalHours >= 1)
            return $"Slow down — on pace to empty ~{FormatDuration(early)} early";
        if (early.TotalHours <= -1)
            return $"On pace to finish with ~{FormatDuration(-early)} spare";
        return "On pace for the reset";
    }

    // auto model
    public static string? FormatWindowPaceHint(UsageWindow? window, DateTime now, TimeSpan assumedLength)
    {
        if (window?.ResetsAt is not DateTime end || end <= now)
            return null;
        var start = end - assumedLength;
        if (start > now)
            start = now - TimeSpan.FromMinutes(1);
        return FormatPaceHint(window.UsedPercent, now, start, end);
    }

    static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = -span;
        if (span.TotalDays >= 1.5)
        {
            var days = span.TotalDays;
            // "a week and a half" style for roughly 7–14d ranges
            if (days >= 6.5 && days < 7.75)
                return "a week";
            if (days >= 9.5 && days < 12)
                return "a week and a half";
            if (days >= 13 && days < 16)
                return "about 2 weeks";
            return days >= 2 ? $"{days:0.#} days" : "1 day";
        }

        if (span.TotalHours >= 1)
            return span.Minutes == 0
                ? $"{(int)span.TotalHours}h"
                : $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    public void Dispose()
    {
        _loopCts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _loopCts?.Dispose();
        _http.Dispose();
    }
}
