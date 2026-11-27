using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using AtFutureMe.Models;
using AtFutureMe;

namespace AtFutureMe.Services;

public sealed class DiscordAlert
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string Context { get; init; } = "";
    public DateTime When { get; init; }
    /// <summary>Optional per-sender color hex when a name-color rule matches.</summary>
    public string? Color { get; init; }
}

public sealed class DiscordAlertService : IDisposable
{
    static readonly string[] ProcessNames =
    [
        "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment", "vesktop", "Vesktop"
    ];

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    static readonly TimeSpan HeartbeatLimit = TimeSpan.FromSeconds(25);

    readonly Dispatcher _dispatcher;
    readonly List<DiscordAlert> _alerts = [];
    readonly HashSet<string> _prioritySoundedIds = new(StringComparer.Ordinal);
    DispatcherTimer? _poll;
    bool _disposed;
    bool _unread;
    string _seenKey = "";
    DateTime _lastPrioritySoundUtc = DateTime.MinValue;
    DateTime _priorityUnreadSinceUtc = DateTime.MinValue;
    DateTime _discordProbeAt = DateTime.MinValue;
    bool _discordRunning;
    bool _vencordRunning;
    bool _vencordCheckable;

    public DiscordAlertService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public IReadOnlyList<DiscordAlert> Alerts => _alerts;
    public bool HasUnread => _unread;
    public bool PluginMissing { get; private set; } = true;
    public string UnreadLabel { get; private set; } = "";
    public IReadOnlyList<string> UnreadNames { get; private set; } = [];
    /// <summary>Resolved cue color for current unread (named sender, else null → use default).</summary>
    public string? UnreadCueColor { get; private set; }
    /// <summary>Matched name-color rule for current unread, if any.</summary>
    public DiscordNameColor? UnreadNameStyle { get; private set; }
    public string MissingLabel { get; private set; } = "";
    public string Status { get; private set; } = "Starting…";
    public event Action? Changed;

    public static string InboxPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AtFutureMe",
            "discord-inbox.json");

    public void Start()
    {
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        Poll();
    }

    public void RefreshNow() => Poll();

    public void SetPollInterval(TimeSpan interval)
    {
        if (_poll == null)
            return;
        if (interval < TimeSpan.FromSeconds(1))
            interval = TimeSpan.FromSeconds(1);
        if (_poll.Interval == interval)
            return;
        _poll.Interval = interval;
    }

    public string BuildDebugReport()
    {
        var host = AppHost.Instance;
        var s = host.Data.Settings;
        var lines = new List<string>();
        var discordRunning = DiscordIsRunning(out var vencordRunning, out var vencordCheckable);
        TryReadBridge(out var bridge);
        var fileExists = File.Exists(InboxPath);
        var live = BridgeLive(bridge);
        var unseen = bridge?.Events.Where(e => !e.Seen).OrderByDescending(e => e.WhenUtc).ToList() ?? [];
        var all = bridge?.Events.OrderByDescending(e => e.WhenUtc).ToList() ?? [];
        var clockWanted = DiscordShowsClock(s);
        var windowWanted = DiscordShowsWindow(s);
        var reasons = new List<string>();

        if (!s.ShowDiscordModule)
            reasons.Add("Watch Discord inbox is off, so no cue or window will show.");
        if (clockWanted && !s.ShowClockModule)
            reasons.Add("Alerts are set to use the clock, but the clock module is off.");
        else if (clockWanted && !host.ClockOverlayIsUp)
            reasons.Add("The clock overlay is not up (hidden with the main overlay, or not shown).");
        if (windowWanted && !host.DiscordOverlayIsUp)
            reasons.Add("The Discord window overlay is not up (hidden with the main overlay, or not shown).");
        if (!fileExists)
            reasons.Add("No inbox file yet. The Vencord plugin has never written one, or Discord hasn’t been started with AtFutureMe Inbox enabled.");
        else if (bridge is null)
            reasons.Add("Inbox file exists but couldn’t be parsed.");
        else if (!live)
            reasons.Add(bridge.Running
                ? "Inbox heartbeat is stale, so AtFutureMe treats the plugin as offline."
                : "Inbox file says the plugin is not running (Discord closed or the plugin was disabled).");
            else if (unseen.Count == 0)
        {
            if (all.Count == 0)
                reasons.Add("Plugin is alive but has no stored DMs. Either none arrived, or they were filtered before being written.");
            else
                reasons.Add("Recent DMs are in the file, but every one is marked seen. That used to happen when Discord auto-acked a selected DM while minimized. New messages stay unread unless Discord is the active window.");
        }

        var discordFocused = DiscordIsForeground();
        if (live && unseen.Count > 0 && discordFocused)
            reasons.Add("There is unread inbox mail, but Discord is the active window, so the cue is held.");

        if (live && unseen.Count > 0 && s.ShowDiscordModule)
        {
            if (clockWanted && s.ShowClockModule && host.ClockOverlayIsUp && s.DiscordClockSender == DiscordClockSender.Off)
                reasons.Add("Unread exists and the clock cue should still pulse/tint/dot, but sender text is Hidden — you won’t see a name.");
            if (!clockWanted && !windowWanted)
                reasons.Add("Unread exists, but neither clock nor window display is enabled.");
        }

        var wouldShow = s.ShowDiscordModule
            && live
            && unseen.Count > 0
            && !discordFocused
            && (clockWanted && s.ShowClockModule && host.ClockOverlayIsUp
                || windowWanted && host.DiscordOverlayIsUp);

        lines.Add(wouldShow
            ? "Would alert now: YES — " + string.Join(", ", UniqueSenderNames(unseen))
            : "Would alert now: NO");
        if (reasons.Count > 0)
        {
            lines.Add("");
            lines.Add("Why:");
            foreach (var reason in reasons)
                lines.Add("• " + reason);
        }
        else if (wouldShow)
        {
            lines.Add("");
            lines.Add("Why:");
            lines.Add("• Unread inbox names are queued and a display surface is up. If you still didn’t notice it, check cue opacity, clock size, or another window covering the overlay.");
        }

        lines.Add("");
        lines.Add("Settings");
        lines.Add("• Watch inbox: " + OnOff(s.ShowDiscordModule));
        lines.Add("• Show alerts: " + DisplayName(s.DiscordAlertDisplay));
        lines.Add("• Clock module: " + OnOff(s.ShowClockModule) + (host.ClockOverlayIsUp ? ", overlay up" : ", overlay not up"));
        lines.Add("• Clock cue: " + CueName(s.DiscordClockCue));
        lines.Add("• Sender text: " + SenderName(s.DiscordClockSender) + (s.DiscordClockNameOnly ? " (name only)" : ""));
        lines.Add("• Discord window: " + (host.DiscordOverlayIsUp ? "up" : "not up"));
        lines.Add("• Main overlay put away: " + (host.MainOverlayPutAway ? "yes" : "no"));

        lines.Add("");
        lines.Add("Discord / Vencord");
        lines.Add("• Discord process: " + (discordRunning ? "running" : "not running"));
        lines.Add("• Discord is the active window: " + (discordFocused ? "yes — cue held" : "no"));
        if (discordRunning)
        {
            lines.Add(vencordCheckable
                ? "• Vencord on this install: " + (vencordRunning ? "yes" : "NO — stock Discord")
                : "• Vencord on this install: couldn’t inspect the process");
        }

        lines.Add("");
        lines.Add("Inbox file");
        lines.Add("• " + InboxPath);
        if (!fileExists)
            lines.Add("• Missing");
        else
        {
            try
            {
                var written = File.GetLastWriteTime(InboxPath);
                lines.Add("• Last write: " + written.ToString("h:mm:ss tt"));
            }
            catch
            {
                lines.Add("• Last write: unknown");
            }
            if (bridge is null)
                lines.Add("• Parse: failed");
            else
            {
                var beat = ToUtc(bridge.HeartbeatUtc);
                var age = DateTime.UtcNow - beat;
                var ageText = age.TotalSeconds < 90
                    ? $"{Math.Max(0, (int)age.TotalSeconds)}s ago"
                    : $"{age.TotalMinutes:0.0} min ago";
                lines.Add("• Plugin flag: " + (bridge.Running ? "running" : "stopped"));
                lines.Add("• Heartbeat: " + ageText + (live ? " (live)" : " (STALE)"));
                lines.Add("• Events: " + all.Count + " stored, " + unseen.Count + " unseen");
                if (bridge.Debug?.Settings != null)
                {
                    var ds = bridge.Debug.Settings;
                    lines.Add("• Plugin filters: spam " + OnOff(ds.IgnoreSpam)
                        + ", message requests " + OnOff(ds.IgnoreMessageRequests)
                        + ", blocked " + OnOff(ds.IgnoreBlocked));
                }
            }
        }

        if (all.Count > 0)
        {
            lines.Add("");
            lines.Add("Stored DMs (newest first)");
            foreach (var item in all.Take(8))
            {
                var name = DisplayName(item.User, item.UserId);
                var when = LocalWhen(item.WhenUtc);
                var flag = item.Seen ? "seen — no alert" : "UNREAD";
                var preview = string.IsNullOrWhiteSpace(item.Preview) ? "" : "  " + Short(item.Preview, 80);
                lines.Add($"• {when}  {name}  {item.Kind}  {flag}{preview}");
            }
        }

        var decisions = bridge?.Debug?.Decisions;
        if (decisions is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("Plugin decisions (why a DM was kept or dropped)");
            foreach (var item in decisions.Take(12))
            {
                var when = LocalWhen(item.WhenUtc);
                var who = string.IsNullOrWhiteSpace(item.User) ? "" : "  " + item.User;
                var preview = string.IsNullOrWhiteSpace(item.Preview) ? "" : "  " + Short(item.Preview, 60);
                lines.Add($"• {when}  {DecisionText(item.Action, item.Reason)}{who}{preview}");
            }
        }
        else
        {
            lines.Add("");
            lines.Add("Plugin decisions: none in the file yet. Reload Discord once after this AtFutureMe Inbox plugin update to start recording skip reasons (spam, blocked, DM was open). After that, this log updates live — no restart.");
        }

        lines.Add("");
        lines.Add("If a DM never appears above, it was a server message, your own message, or the plugin wasn’t running when it arrived.");
        return string.Join(Environment.NewLine, lines);
    }

    static bool DiscordShowsClock(AppSettings settings) =>
        settings.ShowDiscordModule && settings.DiscordAlertDisplay != DiscordAlertDisplay.Window;

    static bool DiscordShowsWindow(AppSettings settings) =>
        settings.ShowDiscordModule && settings.DiscordAlertDisplay != DiscordAlertDisplay.Clock;

    static string OnOff(bool value) => value ? "on" : "off";

    static string DisplayName(DiscordAlertDisplay value) => value switch
    {
        DiscordAlertDisplay.Clock => "clock face",
        DiscordAlertDisplay.Both => "window and clock",
        _ => "own window"
    };

    static string CueName(DiscordClockCue value) => value switch
    {
        DiscordClockCue.TintFace => "solid tint",
        DiscordClockCue.PulseRing => "pulse ring",
        DiscordClockCue.RimDot => "dot on the rim",
        _ => "pulse face"
    };

    static string SenderName(DiscordClockSender value) => value switch
    {
        DiscordClockSender.OnFace => "on the face",
        DiscordClockSender.Below => "below the clock",
        _ => "hidden"
    };

    static string DecisionText(string action, string reason)
    {
        var key = (string.IsNullOrWhiteSpace(action) ? "" : action.Trim() + ":") + (reason ?? "").Trim();
        return key switch
        {
            "keep:stored-unread" => "kept as unread — should alert",
            "keep:stored-seen-you-were-looking" => "kept as seen — Discord was focused on that DM",
            "keep:stored-seen-dm-was-open" => "kept as seen — that DM was already open",
            "seen:you-opened-this-dm" => "marked seen — you opened that DM",
            "seen:discord-acked-channel" => "marked seen — Discord acked the channel while focused",
            "info:ignored-discord-acked-channel-discord-not-focused" => "ignored Discord ack — Discord was not the active window",
            "info:ignored-you-opened-this-dm-discord-not-focused" => "ignored channel select — Discord was not the active window",
            "ignore:blocked-or-ignored" => "dropped — blocked or ignored (plugin filter on)",
            "ignore:spam-channel" => "dropped — Discord marked the DM as spam",
            "ignore:spam-user" => "dropped — Discord marked the sender as spam",
            "ignore:spammer-flag" => "dropped — sender has Discord’s spammer flag",
            "ignore:message-request" => "dropped — message request (plugin filter on)",
            "ignore:channel-not-in-cache" => "dropped — Discord didn’t have that channel loaded",
            "ignore:no-current-user" => "dropped — Discord user wasn’t ready yet",
            "info:plugin-started" => "plugin started",
            "info:plugin-stopped" => "plugin stopped",
            _ => (action + " " + reason).Trim()
        };
    }

    static DateTime ToUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return value.ToUniversalTime();
    }

    static string LocalWhen(DateTime when)
    {
        if (when == default)
            return "unknown time";
        var local = when.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(when, DateTimeKind.Utc).ToLocalTime()
            : when.ToLocalTime();
        return local.ToString("h:mm:ss tt");
    }

    static string Short(string message, int max)
    {
        var n = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return n.Length <= max ? n : n[..(max - 1)] + "…";
    }

    void Poll()
    {
        if (_disposed)
            return;
        try
        {
            var wasMissing = PluginMissing;
            var wasUnread = _unread;
            var wasUnreadLabel = UnreadLabel;
            var wasMissingLabel = MissingLabel;
            if (!TryReadBridge(out var bridge) || !BridgeLive(bridge))
            {
                if (DateTime.UtcNow - _discordProbeAt > TimeSpan.FromSeconds(15))
                {
                    _discordRunning = DiscordIsRunning(out _vencordRunning, out _vencordCheckable);
                    _discordProbeAt = DateTime.UtcNow;
                }
                var discordRunning = _discordRunning;
                var vencordRunning = _vencordRunning;
                var vencordCheckable = _vencordCheckable;
                PluginMissing = true;
                _unread = false;
                UnreadLabel = "";
                UnreadNames = [];
                UnreadCueColor = null;
                UnreadNameStyle = null;
                if (!discordRunning)
                {
                    MissingLabel = "Discord isn’t running";
                    SetStatus("Discord isn’t running.");
                }
                else if (vencordCheckable && !vencordRunning)
                {
                    MissingLabel = "Vencord isn’t running";
                    SetStatus("Discord is running, but Vencord isn’t injected. Patch Discord from your Vencord source build.");
                }
                else if (vencordRunning)
                {
                    MissingLabel = "Inbox plugin not running";
                    SetStatus("Vencord is running, but AtFutureMe Inbox isn’t. Enable it in Vencord → Plugins, then restart Discord.");
                }
                else
                {
                    MissingLabel = "Inbox plugin not running";
                    SetStatus("Couldn’t confirm Vencord. Enable AtFutureMe Inbox in Vencord → Plugins, then restart Discord.");
                }
                if (wasMissing != PluginMissing || wasUnread != _unread || wasMissingLabel != MissingLabel)
                    Raise();
                return;
            }

            PluginMissing = false;
            MissingLabel = "";
            SyncAlerts(bridge!);
            var unseen = bridge!.Events
                .Where(e => !e.Seen)
                .OrderByDescending(e => e.WhenUtc)
                .ToList();
            var discordFocused = DiscordIsForeground();
            _unread = unseen.Count > 0 && !discordFocused;
            if (_unread)
            {
                UnreadNames = UniqueSenderNames(unseen);
                UnreadLabel = string.Join("\n", UnreadNames);
                UnreadNameStyle = ResolveUnreadNameStyle(unseen);
                UnreadCueColor = UnreadNameStyle != null
                    && ColorText.TryNormalize(UnreadNameStyle.Color, out var cue)
                    ? cue
                    : null;
                SetStatus(UnreadNames.Count == 1
                    ? $"New inbox message from {UnreadNames[0]}."
                    : $"{unseen.Count} unread inbox messages.");
            }
            else
            {
                UnreadNames = [];
                UnreadLabel = "";
                UnreadCueColor = null;
                UnreadNameStyle = null;
                SetStatus(unseen.Count > 0 && discordFocused
                    ? "Discord is the active window — inbox cue held."
                    : "Watching your Discord inbox.");
            }
            MaybePlayPrioritySound(unseen, discordFocused);
            if (wasMissing != PluginMissing || wasUnread != _unread || wasUnreadLabel != UnreadLabel)
                Raise();
        }
        catch (Exception ex)
        {
            PluginMissing = true;
            UnreadLabel = "";
            UnreadNames = [];
            UnreadCueColor = null;
            UnreadNameStyle = null;
            MissingLabel = "Inbox plugin not running";
            SetStatus("Could not read Discord inbox: " + Short(ex.Message));
        }
    }

    void MaybePlayPrioritySound(List<InboxEvent> unseen, bool discordFocused)
    {
        var s = AppHost.Instance.Data.Settings;
        if (!s.ShowDiscordModule)
            return;
        var priorities = (s.DiscordPriorityNames ?? [])
            .Select(n => n.Trim().TrimStart('@'))
            .Where(n => n.Length > 0)
            .ToList();
        if (priorities.Count == 0)
            return;

        var matching = unseen.Where(e => MatchesPriority(e, priorities)).ToList();
        var liveIds = matching.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        _prioritySoundedIds.RemoveWhere(id => !liveIds.Contains(id));

        if (discordFocused || matching.Count == 0)
        {
            if (matching.Count == 0)
            {
                _lastPrioritySoundUtc = DateTime.MinValue;
                _priorityUnreadSinceUtc = DateTime.MinValue;
            }
            return;
        }

        if (_priorityUnreadSinceUtc == DateTime.MinValue)
            _priorityUnreadSinceUtc = DateTime.UtcNow;

        var volume = Math.Clamp(s.DiscordPriorityVolume, 0, 1);
        var sound = s.DiscordPrioritySound;
        var mode = s.EffectiveDiscordPriorityRepeatMode();
        if (mode == 0)
        {
            var fresh = matching.Where(e => !_prioritySoundedIds.Contains(e.Id)).ToList();
            if (fresh.Count == 0)
                return;
            foreach (var item in fresh)
                _prioritySoundedIds.Add(item.Id);
            _lastPrioritySoundUtc = DateTime.UtcNow;
            AlertSound.Play(sound, volume);
            return;
        }

        var interval = Math.Clamp(s.DiscordPriorityIntervalMinutes <= 0 ? 5 : s.DiscordPriorityIntervalMinutes, 0.5, 24 * 60);
        if (mode == 2)
        {
            var grace = Math.Clamp(s.DiscordPriorityGraceMinutes <= 0 ? 10 : s.DiscordPriorityGraceMinutes, 0.5, 24 * 60);
            if (DateTime.UtcNow - _priorityUnreadSinceUtc < TimeSpan.FromMinutes(grace))
                return;
        }

        if (_lastPrioritySoundUtc != DateTime.MinValue
            && DateTime.UtcNow - _lastPrioritySoundUtc < TimeSpan.FromMinutes(interval))
            return;
        _lastPrioritySoundUtc = DateTime.UtcNow;
        foreach (var item in matching)
            _prioritySoundedIds.Add(item.Id);
        AlertSound.Play(sound, volume);
    }

    static bool MatchesPriority(InboxEvent item, List<string> priorities)
    {
        var name = (item.User ?? "").Trim();
        var id = (item.UserId ?? "").Trim();
        var display = DisplayName(item.User, item.UserId);
        foreach (var priority in priorities)
        {
            if (MatchesNameToken(priority, name, id, display))
                return true;
        }

        return false;
    }

    static DiscordNameColor? ResolveUnreadNameStyle(List<InboxEvent> unseen)
    {
        var colors = AppHost.Instance.Data.Settings.DiscordNameColors;
        if (colors == null || colors.Count == 0)
            return null;
        foreach (var item in unseen)
        {
            var match = MatchNameColorEntry(item, colors);
            if (match != null)
                return match;
        }

        return null;
    }

    static DiscordNameColor? MatchNameColorEntry(InboxEvent item, List<DiscordNameColor> colors)
    {
        var name = (item.User ?? "").Trim();
        var id = (item.UserId ?? "").Trim();
        var display = DisplayName(item.User, item.UserId);
        foreach (var entry in colors)
        {
            var token = (entry.Name ?? "").Trim().TrimStart('@');
            if (token.Length == 0)
                continue;
            if (!MatchesNameToken(token, name, id, display))
                continue;
            return entry;
        }

        return null;
    }

    static string? MatchNameColor(InboxEvent item, List<DiscordNameColor> colors)
    {
        var entry = MatchNameColorEntry(item, colors);
        if (entry == null)
            return null;
        // Overlay title prefers text color, then cue.
        if (ColorText.TryNormalize(entry.TextColor, out var text))
            return text;
        return ColorText.TryNormalize(entry.Color, out var cue) ? cue : null;
    }

    /// <summary>Match a configured name/id against raw username, display alias, or user id.</summary>
    public static bool MatchesNameToken(string token, string? user, string? userId, string? display = null)
    {
        var name = (user ?? "").Trim();
        var id = (userId ?? "").Trim();
        var shown = (display ?? "").Trim();
        if (shown.Length == 0 && (name.Length > 0 || id.Length > 0))
            shown = DisplayName(name, id);
        var key = token.Trim().TrimStart('@');
        if (key.Length == 0)
            return false;
        return key.Equals(name, StringComparison.OrdinalIgnoreCase)
            || key.Equals(name.TrimStart('@'), StringComparison.OrdinalIgnoreCase)
            || (shown.Length > 0 && key.Equals(shown, StringComparison.OrdinalIgnoreCase))
            || (id.Length > 0 && key.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    void SyncAlerts(InboxBridgeFile bridge)
    {
        var colors = AppHost.Instance.Data.Settings.DiscordNameColors ?? [];
        var next = bridge.Events
            .Select(e => new DiscordAlert
            {
                Id = e.Id,
                Title = DisplayName(e.User, e.UserId),
                Body = e.Preview,
                Context = e.Kind == "group" ? "Group DM" : "Direct message",
                When = e.WhenUtc.ToLocalTime(),
                Color = MatchNameColor(e, colors)
            })
            .ToList();
        var key = string.Join("|", next.Select(a =>
            a.Id
            + (bridge.Events.First(e => e.Id == a.Id).Seen ? "s" : "u")
            + (a.Color ?? "")));
        if (key == _seenKey)
            return;
        _seenKey = key;
        _alerts.Clear();
        _alerts.AddRange(next);
        Raise();
    }

    static bool TryReadBridge(out InboxBridgeFile? bridge)
    {
        bridge = null;
        var path = InboxPath;
        if (!File.Exists(path))
            return false;
        try
        {
            var json = File.ReadAllText(path);
            bridge = JsonSerializer.Deserialize<InboxBridgeFile>(json, JsonOptions);
            return bridge != null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static bool BridgeLive(InboxBridgeFile? bridge)
    {
        if (bridge is null || !bridge.Running)
            return false;
        var beat = bridge.HeartbeatUtc;
        if (beat.Kind == DateTimeKind.Unspecified)
            beat = DateTime.SpecifyKind(beat, DateTimeKind.Utc);
        return DateTime.UtcNow - beat.ToUniversalTime() <= HeartbeatLimit;
    }

    static List<string> UniqueSenderNames(List<InboxEvent> unseen)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var item in unseen)
        {
            var name = DisplayName(item.User, item.UserId);
            var key = item.UserId.Length > 0 ? "id:" + item.UserId : "n:" + name;
            if (!seen.Add(key))
                continue;
            names.Add(name);
        }
        return names;
    }

    static bool DiscordIsRunning(out bool vencordRunning, out bool vencordCheckable)
    {
        vencordRunning = false;
        vencordCheckable = false;
        var running = false;
        foreach (var name in ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length == 0)
                    continue;
                running = true;
                if (name.Contains("vesktop", StringComparison.OrdinalIgnoreCase))
                {
                    vencordRunning = true;
                    vencordCheckable = true;
                    return true;
                }
                foreach (var process in processes)
                {
                    var exe = TryProcessPath(process);
                    if (string.IsNullOrWhiteSpace(exe))
                        continue;
                    vencordCheckable = true;
                    if (InstallLooksLikeVencord(Path.GetDirectoryName(exe) ?? ""))
                    {
                        vencordRunning = true;
                        return true;
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
        return running;
    }

    static bool DiscordIsForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (!NativeMethods.WindowLooksOnScreen(hwnd))
            return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            foreach (var expected in ProcessNames)
            {
                if (name.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    static string? TryProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    static bool InstallLooksLikeVencord(string exeDir)
    {
        var resources = Path.Combine(exeDir, "resources");
        if (!Directory.Exists(resources))
            return false;
        if (File.Exists(Path.Combine(resources, "_app.asar")))
            return true;
        var appDir = Path.Combine(resources, "app");
        if (!Directory.Exists(appDir))
            return false;
        foreach (var file in new[] { "index.js", "package.json" })
        {
            var path = Path.Combine(appDir, file);
            if (!File.Exists(path))
                continue;
            try
            {
                var text = File.ReadAllText(path);
                if (text.Contains("Vencord", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // ignore unreadable patch files
            }
        }
        return false;
    }

    void SetStatus(string status)
    {
        if (Status == status)
            return;
        Status = status;
        Raise();
    }

    void Raise()
    {
        if (_disposed)
            return;
        if (_dispatcher.CheckAccess())
            Changed?.Invoke();
        else
            _dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    public static string DisplayName(string? user, string? userId)
    {
        var name = (user ?? "").Trim();
        var id = (userId ?? "").Trim();
        var aliases = AppHost.Instance.Data.Settings.DiscordNameAliases;
        if (aliases != null)
        {
            foreach (var alias in aliases)
            {
                var from = (alias.From ?? "").Trim().TrimStart('@');
                var to = (alias.To ?? "").Trim();
                if (from.Length == 0 || to.Length == 0)
                    continue;
                if (from.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || from.Equals(name.TrimStart('@'), StringComparison.OrdinalIgnoreCase)
                    || (id.Length > 0 && from.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    return to;
            }
        }

        return name.Length > 0 ? name : "Unknown";
    }

    static string Short(string message)
    {
        var n = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return n.Length <= 160 ? n : n[..157] + "…";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _poll?.Stop();
        _poll = null;
    }

    sealed class InboxBridgeFile
    {
        public bool Running { get; set; }
        public DateTime HeartbeatUtc { get; set; }
        public List<InboxEvent> Events { get; set; } = [];
        public InboxDebug? Debug { get; set; }
    }

    sealed class InboxDebug
    {
        public InboxDebugSettings? Settings { get; set; }
        public List<InboxDecision> Decisions { get; set; } = [];
    }

    sealed class InboxDebugSettings
    {
        public bool IgnoreSpam { get; set; }
        public bool IgnoreMessageRequests { get; set; }
        public bool IgnoreBlocked { get; set; }
    }

    sealed class InboxDecision
    {
        public DateTime WhenUtc { get; set; }
        public string Action { get; set; } = "";
        public string Reason { get; set; } = "";
        public string User { get; set; } = "";
        public string Preview { get; set; } = "";
    }

    sealed class InboxEvent
    {
        public string Id { get; set; } = "";
        public string User { get; set; } = "";
        public string UserId { get; set; } = "";
        public string Preview { get; set; } = "";
        public string Kind { get; set; } = "dm";
        public bool Seen { get; set; }
        public DateTime WhenUtc { get; set; }
    }
}
