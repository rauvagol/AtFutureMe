using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AtFutureMe.Models;
using AtFutureMe.Services;
using AtFutureMe.ViewModels;
using AtFutureMe.Windows;
using Color = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.Brush;
using WinForms = System.Windows.Forms;

namespace AtFutureMe;

public sealed partial class AppHost : IDisposable
{
    public static AppHost Instance { get; private set; } = null!;

    public AppDataFile Data { get; }
    public DataStore Store { get; }

    public ObservableCollection<CategorySectionVm> DockedCategories { get; } = [];
    public ObservableCollection<OverlayItemVm> UncategorizedItems { get; } = [];
    // auto model
    public ObservableCollection<object> RootBoard { get; } = [];

    public event Action? UiChanged;

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _dueHighlight;
    readonly DispatcherTimer _saveBounds = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly HashSet<string> _notified = [];
    readonly Dictionary<string, MediaBrush> _brushes = [];
    readonly Dictionary<string, CategorySectionVm> _sections = [];
    readonly Dictionary<string, CategoryOverlay> _categoryWindows = [];

    WinForms.NotifyIcon? _tray;
    WinForms.ContextMenuStrip? _trayMenu;
    WinForms.ToolStripMenuItem? _overlayItem;
    WinForms.ToolStripMenuItem? _clickThroughItem;
    WinForms.ToolStripMenuItem? _autostartItem;
    RemindersOverlay? _reminders;
    ClockOverlay? _clock;
    UsageOverlay? _usage;
    TempHideCountdown? _countdown;
    ConfigWindow? _config;
    Icon? _trayIcon;
    Window? _trayActivator;
    bool _hiddenAll;
    bool _userHidMain;
    readonly HashSet<string> _hiddenWindows = [];
    readonly HashSet<string> _pairedWasRunning = [];
    readonly HashSet<string> _pairHidMain = [];
    readonly List<DeletedTask> _deleted = [];
    AddEntryWindow? _addWindow;
    SetEventWhenWindow? _setWhen;

    // auto model
    public UsageTracker? Usage { get; private set; }

    public bool OverlayDragActive => _overlayDrag;
    public const string MainOverlayId = "reminders";
    public const string ClockOverlayId = "clock";
    public const string UsageOverlayId = "usage";
    bool _disposed;
    bool _overlayDrag;

    public AppHost()
    {
        Instance = this;
        Store = new DataStore();
        Data = Store.Load();
        NormalizeSettings();
        CategoryTree.Sanitize(Data.Categories);
        if (EnsureCategories())
            Store.Save(Data);
        if (Data.Settings.Autostart != AutostartService.IsEnabled())
            AutostartService.SetEnabled(Data.Settings.Autostart);
    }

    public void Start()
    {
        CreateTray();
        _reminders = new RemindersOverlay();
        RestoreBounds(_reminders, Data.Settings.Reminders, defaultTopOffset: 48);
        RefreshOverlayItems();
        ApplyWindowChrome();
        DiscardSessionHide();
        ShowOverlays();
        Usage = new UsageTracker(Application.Current.Dispatcher);
        Usage.Changed += OnUsageChanged;
        Usage.Start();
        SyncClockOverlay();
        SyncUsageOverlay();
        EnsureDesktop();
        SnapshotPairings();
        SnapshotLayouts();

        _tick.Tick += (_, _) => Tick();
        _saveBounds.Tick += (_, _) =>
        {
            _saveBounds.Stop();
            Save();
        };
        _tick.Start();

        if (!Data.Settings.HasSeenWelcome)
        {
            Data.Settings.HasSeenWelcome = true;
            Save();
            _tray?.ShowBalloonTip(4000, "AtFutureMe",
                "Running in the notification area. Right-click the icon to configure.",
                WinForms.ToolTipIcon.Info);
        }
    }

    public void Save()
    {
        try
        {
            WriteStore(true);
        }
        catch (Exception ex)
        {
            App.Log("Save failed: " + ex);
        }
    }

    public void Persist()
    {
        try
        {
            WriteStore(false);
        }
        catch (Exception ex)
        {
            App.Log("Save failed: " + ex);
        }
    }

    void WriteStore(bool refreshUi)
    {
        CaptureBounds();
        OverlayLiveState? live = null;
        if (LayoutSceneActive)
        {
            live = RememberLiveOverlay();
            PersistDesktopFields();
        }
        else
            SnapshotDesktop();
        Store.Save(Data);
        if (live != null)
            RestoreLiveOverlay(live);
        if (refreshUi)
            UiChanged?.Invoke();
    }

    public void RequestSave()
    {
        Save();
        RefreshOverlayItems();
        ApplyWindowChrome();
        UpdateTrayChecks();
    }

    // auto model
    public string CreateManualBackup()
    {
        CaptureBounds();
        if (LayoutSceneActive)
            PersistDesktopFields();
        else
            SnapshotDesktop();
        return Store.CreateManualBackup(Data);
    }

    public void ToggleClickThrough()
    {
        if (LayoutSceneActive)
            return;
        Data.Settings.ClickThrough = !Data.Settings.ClickThrough;
        RequestSave();
    }

    public void SetClickThrough(bool value)
    {
        if (LayoutSceneActive)
            return;
        Data.Settings.ClickThrough = value;
        RequestSave();
    }

    public void SetCategoryPoppedOut(string categoryId, bool poppedOut)
    {
        var category = Data.Categories.FirstOrDefault(c => c.Id == categoryId);
        if (category == null)
            return;
        category.PoppedOut = poppedOut;
        if (!poppedOut)
            category.PairOpened = false;
        if (poppedOut)
            _hiddenWindows.Remove(categoryId);
        if (ActiveLayout() is AppLayout layout)
            FolderOverride(layout, categoryId).PoppedOut = poppedOut;
        RequestSave();
        ApplyOverlayVisibility();
    }

    public void OpenAddEntry(string? categoryId, Action? closed = null) =>
        OpenAddDialog(categoryId, false, closed);

    public void OpenAddFolder(string? parentId, Action? closed = null) =>
        OpenAddDialog(parentId, true, closed);

    void OpenAddDialog(string? categoryId, bool folderOnly, Action? closed)
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => OpenAddDialog(categoryId, folderOnly, closed));
            return;
        }

        try
        {
            if (_addWindow != null)
            {
                if (_addWindow.FolderOnly == folderOnly)
                {
                    _addWindow.Activate();
                    NativeMethods.SetForegroundWindow(new WindowInteropHelper(_addWindow).EnsureHandle());
                    return;
                }

                _addWindow.Close();
            }

            _addWindow = new AddEntryWindow(categoryId, folderOnly)
            {
                Topmost = true,
                ShowInTaskbar = true
            };
            _addWindow.Closed += (_, _) =>
            {
                _addWindow = null;
                closed?.Invoke();
            };
            _addWindow.Show();
            _addWindow.Activate();
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(_addWindow).EnsureHandle());
        }
        catch (Exception ex)
        {
            App.Log("OpenAddEntry failed: " + ex);
            MessageBox.Show(ex.Message, "AtFutureMe");
        }
    }

    public void AddQuickEntry(int type, string categoryId, string title, DateTime? date, string timeText, string body, bool dateUnknown, Recurrence? recurrence = null, int checkEveryDays = 0)
    {
        categoryId = ResolveCategoryId(categoryId);
        title = title.Trim();
        switch (type)
        {
            case 1:
                var schedule = recurrence?.Clone() ?? new Recurrence();
                schedule.Anchor ??= DateTime.Today;
                var todo = new TodoItem
                {
                    Title = title.Length == 0 ? "New recurring" : title,
                    CategoryId = categoryId,
                    Recurrence = schedule
                };
                Data.Todos.Add(todo);
                TrackTask("todo", todo.Id);
                break;
            case 2:
                DateTime? at = null;
                if (!dateUnknown && date != null)
                {
                    var when = date.Value.Date;
                    when += ClockText.ParseOr(timeText, TimeSpan.FromHours(9));
                    at = when;
                }
                var ev = new EventItem
                {
                    Title = title.Length == 0 ? "New scheduled" : title,
                    CategoryId = categoryId,
                    CreatedAt = DateTime.Now,
                    LastCheckedAt = dateUnknown ? DateTime.Now : null,
                    At = at,
                    CheckEveryDays = RecurrenceEngine.ClampCheckEveryDays(checkEveryDays <= 0 ? 7 : checkEveryDays)
                };
                Data.Events.Add(ev);
                TrackTask("event", ev.Id);
                break;
            case 3:
                var note = new NoteItem
                {
                    Title = title.Length == 0 ? "Note" : title,
                    Body = body,
                    CategoryId = categoryId
                };
                Data.Notes.Add(note);
                TrackTask("note", note.Id);
                break;
            default:
                var once = new OnceItem
                {
                    Title = title.Length == 0 ? "New to-do" : title,
                    CategoryId = categoryId,
                    CreatedAt = DateTime.Now
                };
                Data.Once.Add(once);
                TrackTask("once", once.Id);
                break;
        }

        RequestSave();
        ApplyOverlayVisibility();
    }

    public Category AddSubcategory(string? parentId, string name)
    {
        parentId = parentId != null && Data.Categories.Any(c => c.Id == parentId) ? parentId : null;
        name = name.Trim();
        var item = new Category
        {
            Name = name.Length == 0 ? "New category" : name,
            ParentId = parentId
        };
        Data.Categories.Add(item);
        CategoryTree.Rebuild(Data.Categories);
        // auto model
        TrackTask("category", item.Id);
        RequestSave();
        ApplyOverlayVisibility();
        return item;
    }

    public CategorySectionVm? SectionFor(string categoryId) =>
        _sections.GetValueOrDefault(categoryId);

    public string ResolveCategoryId(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && Data.Categories.Any(c => c.Id == preferred))
            return preferred!;
        return "";
    }

    public void SetOpacity(double opacity)
    {
        Data.Settings.Opacity = Math.Clamp(opacity, 0.15, 1);
        ApplyWindowChrome();
        Save();
        UiChanged?.Invoke();
    }

    public void SetPanelDarkness(double amount)
    {
        Data.Settings.PanelDarkness = Math.Clamp(amount, 0, 1);
        ApplyWindowChrome();
        Save();
        UiChanged?.Invoke();
    }

    public void SetEventHorizonDays(int days)
    {
        Data.Settings.EventHorizonDays = Math.Clamp(days, 0, 720);
        RequestSave();
    }

    public void SetRecurringHorizon(int amount)
    {
        Data.Settings.RecurringHorizon = Math.Clamp(amount, 0, 720);
        RequestSave();
    }

    public void SetHorizonInHours(bool hours)
    {
        Data.Settings.HorizonInHours = hours;
        RequestSave();
    }

    public void SetPanelDitherStrength(double amount)
    {
        Data.Settings.PanelDitherStrength = Math.Clamp(amount, 0, 1);
        Data.Settings.PanelDither = true;
        _dither = null;
        ApplyWindowChrome();
        Save();
        UiChanged?.Invoke();
    }

    public void SetTextBackdropOpacity(double opacity)
    {
        Data.Settings.TextBackdropOpacity = Math.Clamp(opacity, 0.15, 1);
        var brush = TextBackdropBrush();
        foreach (var item in OverlayItems())
            item.BackdropBrush = brush;
        Save();
    }

    IEnumerable<OverlayItemVm> OverlayItems()
    {
        foreach (var item in UncategorizedItems)
            yield return item;
        foreach (var section in _sections.Values)
        {
            foreach (var item in section.Items)
                yield return item;
        }
    }

    public void SetAutostart(bool enabled)
    {
        Data.Settings.Autostart = enabled;
        AutostartService.SetEnabled(enabled);
        Save();
        UpdateTrayChecks();
        UiChanged?.Invoke();
    }

    public void OpenConfig() => OpenConfigFor(null, null);

    public void OpenTaskLink(string? url)
    {
        if (!LinkText.TryStart(url, out var start))
            return;
        try
        {
            Process.Start(start);
        }
        catch (Exception ex)
        {
            App.Log("Open link failed: " + ex);
        }
    }

    public void OpenConfigFor(string? itemKind, string? id)
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => OpenConfigFor(itemKind, id));
            return;
        }

        try
        {
            if (_config is { IsVisible: true })
            {
                _config.Topmost = true;
                _config.Activate();
                if (itemKind != null && id != null)
                    _config.FocusItem(itemKind, id);
                NativeMethods.SetForegroundWindow(new WindowInteropHelper(_config).EnsureHandle());
                return;
            }

            _config = new ConfigWindow { Topmost = true };
            _config.Closed += (_, _) => _config = null;
            _config.Show();
            if (itemKind != null && id != null)
                _config.FocusItem(itemKind, id);
            _config.Activate();
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(_config).EnsureHandle());
        }
        catch (Exception ex)
        {
            App.Log("OpenConfig failed: " + ex);
            MessageBox.Show(ex.Message, "AtFutureMe");
        }
    }

    public void HideOverlay(string id)
    {
        _hiddenWindows.Add(id);
        if (id == MainOverlayId)
            _userHidMain = true;
        ApplyOverlayVisibility();
    }

    public void TempHideMain()
    {
        if (LayoutSceneActive)
            return;
        var state = ReadTempHideState(DateTime.Now);
        if (state != TempHideKind.CanHide)
            return;
        Data.Settings.TempHideMain = true;
        Data.Settings.TempHideUntil = DateTime.Now.Add(TempHideMax());
        RequestSave();
        ApplyOverlayVisibility();
        UpdateTempHideCountdown();
    }

    public TimeSpan TempHideLead() => HoursOrDefault(Data.Settings.TempHideHours, 2);

    public TimeSpan TempHideMax() => HoursOrDefault(Data.Settings.TempHideMaxHours, 2);

    public TimeSpan TempHideWarn()
    {
        var seconds = Data.Settings.TempHideWarnSeconds;
        if (seconds <= 0)
            seconds = 60;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 72 * 3600));
    }

    static TimeSpan HoursOrDefault(double hours, double fallback)
    {
        if (hours <= 0)
            hours = fallback;
        return TimeSpan.FromHours(Math.Clamp(hours, 1.0 / 3600, 72));
    }

    public enum TempHideKind
    {
        CanHide,
        HasActive,
        TooSoon,
        NoneUpcoming,
        Layout
    }

    public TempHideKind ReadTempHideState(DateTime now)
    {
        if (LayoutSceneActive)
            return TempHideKind.Layout;
        var items = UpcomingItems(now);
        // auto model
        if (items.Any(item => item.Active && HideAware(item)))
            return TempHideKind.HasActive;
        var next = items.FirstOrDefault(item => HideAware(item) && !item.Active);
        if (next.Title == null)
            return TempHideKind.CanHide;
        return next.At - now > next.Lead ? TempHideKind.CanHide : TempHideKind.TooSoon;
    }

    public bool TempHideWouldHold(DateTime now)
    {
        if (!Data.Settings.TempHideMain)
            return false;
        if (LayoutSceneActive)
            return false;
        if (Data.Settings.TempHideUntil is DateTime until && now >= until)
            return false;
        var items = UpcomingItems(now);
        // auto model
        if (items.Any(item => item.Active && HideAware(item)))
            return false;
        var next = items.FirstOrDefault(item => HideAware(item) && !item.Active);
        if (next.Title != null && next.At - now <= next.Lead)
            return false;
        return true;
    }

    public IReadOnlyList<(string Title, DateTime At, string Remain)> UpcomingScheduled(DateTime now, int take)
    {
        return UpcomingItems(now)
            .Where(HideAware)
            .Take(take)
            .Select(item => (item.Title, item.At, RemainLabel(item, now)))
            .ToList();
    }

    static string RemainLabel((string Title, DateTime At, bool Active, bool Delayable, bool Important, TimeSpan Lead, string Kind, string Id, string CategoryId) item, DateTime now)
    {
        var remain = item.Active ? "now" : RecurrenceEngine.FormatRemainingLong(item.At, now);
        if (item.Important)
            remain += " · important";
        else if (item.Delayable)
            remain += " · delayable";
        return remain;
    }

    // auto model
    static bool HideAware((string Title, DateTime At, bool Active, bool Delayable, bool Important, TimeSpan Lead, string Kind, string Id, string CategoryId) item) =>
        item.Important || !item.Delayable;

    List<(string Title, DateTime At, bool Active, bool Delayable, bool Important, TimeSpan Lead, string Kind, string Id, string CategoryId)> UpcomingItems(DateTime now)
    {
        var rows = new List<(string Title, DateTime At, bool Active, bool Delayable, bool Important, TimeSpan Lead, string Kind, string Id, string CategoryId)>();
        var defaultLead = TempHideLead();
        foreach (var item in Data.Once)
        {
            if (item.CompletedAt == null)
                rows.Add((item.Title, now, true, FolderIsDelayable(item.CategoryId), false, defaultLead, "once", item.Id, item.CategoryId));
        }

        foreach (var todo in Data.Todos)
        {
            if (!RecurrenceEngine.TryTimeline(todo, now, out var at, out var active))
                continue;
            // auto model
            var important = todo.Important;
            var lead = important ? ImportantLead(todo.ImportantLeadHours) : defaultLead;
            rows.Add((todo.Title, at, active, FolderIsDelayable(todo.CategoryId), important, lead, "todo", todo.Id, todo.CategoryId));
        }

        foreach (var ev in Data.Events)
        {
            if (ev.CompletedAt != null)
                continue;
            if (!RecurrenceEngine.TryTimeline(ev, now, out var at, out var active))
                continue;
            // auto model: important items still wake hide before they are eligible for the overlay
            var hidden = HideEventFromOverlay(ev, now);
            if (hidden && !ev.Important)
                continue;
            var important = ev.Important;
            var lead = important ? ImportantLead(ev.ImportantLeadHours) : defaultLead;
            var wakeAt = at;
            if (ev.At is DateTime when)
            {
                var window = EventShowBeforeWindow(ev);
                var appearAt = window > TimeSpan.Zero ? when - window : when;
                wakeAt = important ? appearAt : when;
                active = now >= when;
            }

            rows.Add((ev.Title, wakeAt, active, FolderIsDelayable(ev.CategoryId), important, lead, "event", ev.Id, ev.CategoryId));
        }

        // auto model: Claude / Cursor reset times as upcoming items (reminders board + wake)
        if (Data.Settings.UsageInjectReminders)
        {
            foreach (var reset in UsageTracker.BuildResetEvents(Usage?.Latest, Data.Settings, now))
            {
                if (!UsageTracker.ShouldShowOnRemindersBoard(reset, Data.Settings, now))
                    continue;
                rows.Add((reset.Title, reset.At, false, true, false, defaultLead, reset.Kind, reset.Id, ""));
            }
        }

        return rows
            .OrderBy(row => row.Active && HideAware(row) ? 0 : row.Active ? 1 : 2)
            .ThenBy(row => row.At)
            .ThenBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // auto model
    static TimeSpan ImportantLead(double hours)
    {
        if (hours <= 0)
            hours = 1;
        return TimeSpan.FromHours(Math.Clamp(hours, 1.0 / 60, 720 * 24));
    }

    bool FolderIsDelayable(string? categoryId)
    {
        var id = categoryId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(id) && guard++ < 64)
        {
            var folder = Data.Categories.FirstOrDefault(c => c.Id == id);
            if (folder == null)
                return false;
            if (folder.Delayable)
                return true;
            id = folder.ParentId;
        }

        return false;
    }

    TimeSpan EventShowBeforeWindow(EventItem ev)
    {
        if (ev.ShowBeforeHours > 0)
            return RecurrenceEngine.WindowFromHours(ev.ShowBeforeHours);
        var amount = Data.Settings.EventHorizonDays;
        if (amount <= 0)
            return TimeSpan.Zero;
        return Data.Settings.HorizonInHours ? TimeSpan.FromHours(amount) : TimeSpan.FromDays(amount);
    }

    bool HideEventFromOverlay(EventItem ev, DateTime now)
    {
        if (ev.CompletedAt != null)
            return false;
        if (ev.At is DateTime when)
            return RecurrenceEngine.BeyondWindow(when, now, EventShowBeforeWindow(ev));
        if (ev.ShowAfterCheckHours <= 0)
            return false;
        // Create sets LastCheckedAt to CreatedAt. Stay visible until a real ↻ / link check-in.
        if (ev.LastCheckedAt is not DateTime checkedAt
            || (checkedAt - ev.CreatedAt).Duration() < TimeSpan.FromSeconds(2))
            return false;
        var wait = RecurrenceEngine.WindowFromHours(ev.ShowAfterCheckHours);
        return now < checkedAt + wait;
    }

    public FolderCheckRule ResolvedCheckRule(string? categoryId, FolderCheckRule itemRule = FolderCheckRule.Inherit)
    {
        if (itemRule != FolderCheckRule.Inherit)
            return itemRule;
        var id = categoryId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(id) && guard++ < 64)
        {
            var folder = Data.Categories.FirstOrDefault(c => c.Id == id);
            if (folder == null)
                return FolderCheckRule.Inherit;
            if (folder.CheckRule != FolderCheckRule.Inherit)
                return folder.CheckRule;
            id = folder.ParentId;
        }

        return FolderCheckRule.Inherit;
    }

    TimeSpan FolderReshowLead(string? categoryId)
    {
        var id = categoryId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(id) && guard++ < 64)
        {
            var folder = Data.Categories.FirstOrDefault(c => c.Id == id);
            if (folder == null)
                break;
            if (folder.CheckRule == FolderCheckRule.HideUntilLead)
                return ClampReshowLead(folder.CheckReshowHours);

            if (folder.CheckRule != FolderCheckRule.Inherit)
                break;
            id = folder.ParentId;
        }

        return TimeSpan.FromHours(24);
    }

    static TimeSpan ClampReshowLead(double hours)
    {
        if (hours <= 0)
            hours = 24;
        return TimeSpan.FromHours(Math.Clamp(hours, 1.0 / 60, 72 * 24));
    }

    TimeSpan ReshowLead(string? categoryId, FolderCheckRule itemRule, double itemHours) =>
        itemRule == FolderCheckRule.HideUntilLead
            ? ClampReshowLead(itemHours)
            : FolderReshowLead(categoryId);

    bool HideCheckedOnOverlay(string kind, string? categoryId, FolderCheckRule itemRule, double itemHours,
        bool complete, DateTime? nextReset, DateTime now)
    {
        if (!complete)
            return false;
        switch (ResolvedCheckRule(categoryId, itemRule))
        {
            case FolderCheckRule.KeepVisible:
                return false;
            case FolderCheckRule.HideUntilReset:
            case FolderCheckRule.DeleteOnCheck:
                return true;
            case FolderCheckRule.HideUntilLead:
                if (nextReset is DateTime at && at - now <= ReshowLead(categoryId, itemRule, itemHours))
                    return false;
                return true;
            default:
                return kind switch
                {
                    "once" => Data.Settings.HideCompletedOnce,
                    "todo" => Data.Settings.HideCompletedRecurring,
                    "event" => Data.Settings.HideCompletedScheduled,
                    _ => false
                };
        }
    }

    public bool CanUndoDelete => _deleted.Count > 0;

    public int UndoDeleteCount => _deleted.Count;

    public string UndoDeleteTip =>
        CanUndoDelete
            ? UndoDeleteCount == 1 ? "Undo delete" : $"Undo delete ({UndoDeleteCount})"
            : "Undo delete — nothing to undo this session";

    public void UndoLastDelete()
    {
        if (_deleted.Count == 0)
            return;
        var entry = _deleted[^1];
        _deleted.RemoveAt(_deleted.Count - 1);
        switch (entry.Kind)
        {
            case "once" when entry.Item is OnceItem once:
                Data.Once.Add(once);
                RestoreTaskOrder("once", once.Id, entry.Order);
                break;
            case "todo" when entry.Item is TodoItem todo:
                Data.Todos.Add(todo);
                RestoreTaskOrder("todo", todo.Id, entry.Order);
                break;
            case "event" when entry.Item is EventItem ev:
                Data.Events.Add(ev);
                RestoreTaskOrder("event", ev.Id, entry.Order);
                break;
            default:
                return;
        }

        RequestSave();
    }

    void RememberDeleted(string kind, string id, object item)
    {
        _deleted.Add(new DeletedTask(kind, item, TaskOrderIndex(kind, id)));
    }

    void RestoreTaskOrder(string kind, string id, int index)
    {
        EnsureTaskOrder();
        var key = TaskKey(kind, id);
        Data.Settings.TaskOrder.Remove(key);
        if (index < 0 || index > Data.Settings.TaskOrder.Count)
            index = Data.Settings.TaskOrder.Count;
        Data.Settings.TaskOrder.Insert(index, key);
    }

    record DeletedTask(string Kind, object Item, int Order);

    public string TempHideLeadLabel() => HoursLabel(TempHideLead().TotalHours);

    public string TempHideMaxLabel() => HoursLabel(TempHideMax().TotalHours);

    public string TempHideWarnLabel() => CompactDuration(TempHideWarn());

    public static string HoursLabel(double hours)
    {
        if (hours < 1)
            return $"{Math.Round(hours * 60):0}m";
        if (Math.Abs(hours - Math.Round(hours)) < 0.05)
            return $"{hours:0}h";
        return $"{hours:0.##}h";
    }

    public static string CompactDuration(TimeSpan span)
    {
        var secs = Math.Max(0, (int)Math.Round(span.TotalSeconds));
        if (secs < 60)
            return $"{secs}s";
        var mins = secs / 60.0;
        if (mins < 60)
            return Math.Abs(mins - Math.Round(mins)) < 0.05 ? $"{mins:0}m" : $"{mins:0.#}m";
        return HoursLabel(span.TotalHours);
    }

    public static string CountdownClock(TimeSpan remain)
    {
        var secs = Math.Max(0, (int)Math.Floor(remain.TotalSeconds));
        if (remain > TimeSpan.Zero && secs == 0)
            secs = 1;
        if (secs >= 3600)
        {
            var hours = secs / 3600;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        if (secs >= 60)
        {
            var mins = secs / 60;
            return mins == 1 ? "1 minute" : $"{mins} minutes";
        }

        return secs == 1 ? "1 second" : $"{secs} seconds";
    }

    public string TempHideStatusText(DateTime now) =>
        ReadTempHideState(now) switch
        {
            TempHideKind.Layout => "App layout hide is active. Important tasks can still show the main overlay.",
            TempHideKind.HasActive => "Blocked — something important is already due. Hold the moon button on the overlay to highlight it.",
            TempHideKind.TooSoon => NextUpcomingTip(now, $"Blocked — next important item is already within {TempHideLeadLabel()}."),
            _ => NextUpcomingTip(now, $"Ready — hides until the next important item is within {TempHideLeadLabel()}, or for {TempHideMaxLabel()}, whichever is first.")
        };

    public string TempHideButtonTip(DateTime now)
    {
        var lead = TempHideLeadLabel();
        return ReadTempHideState(now) switch
        {
            TempHideKind.Layout => "App layout is hiding overlays. Important due items still show the main overlay.",
            TempHideKind.HasActive => "Something important is already due. Hold to highlight those items and their folders.",
            TempHideKind.TooSoon => NextUpcomingTip(now, $"Next upcoming item is already within {lead}."),
            _ => NextUpcomingTip(now, $"Hide until the next important item is within {lead}, or for at most {TempHideMaxLabel()}.")
        };
    }

    string NextUpcomingTip(DateTime now, string prefix)
    {
        var next = UpcomingItems(now).FirstOrDefault(item => !item.Active && !item.Delayable);
        if (next.Title == null)
            return prefix;
        var remain = RecurrenceEngine.FormatRemainingLong(next.At, now);
        return $"{prefix} ({next.Title} · {remain})";
    }

    public void SetDueHighlight(bool on)
    {
        _dueHighlight = on;
        ApplyDueHighlight();
    }

    // auto model: moon-hold nudge + static glow only when important is overriding a hide
    void ApplyDueHighlight()
    {
        var now = DateTime.Now;
        var moonItems = new HashSet<(string Kind, string Id)>();
        var moonFolders = new HashSet<string>(StringComparer.Ordinal);
        if (_dueHighlight)
        {
            foreach (var item in UpcomingItems(now).Where(item => item.Active && !item.Delayable))
            {
                var hiddenBy = VisibleCollapsedFolder(item.CategoryId);
                if (hiddenBy != null)
                    moonFolders.Add(hiddenBy);
                else
                    moonItems.Add((item.Kind, item.Id));
            }
        }

        var forceItems = new HashSet<(string Kind, string Id)>();
        var forceFolders = new HashSet<string>(StringComparer.Ordinal);
        // auto model: "doing X, should be doing Y" — only while hide is overridden
        if (MainForcedVisibleByImportant(now))
        {
            foreach (var item in UpcomingItems(now).Where(item => IsImportantAttention(item, now)))
            {
                var hiddenBy = VisibleCollapsedFolder(item.CategoryId);
                if (hiddenBy != null)
                    forceFolders.Add(hiddenBy);
                else
                    forceItems.Add((item.Kind, item.Id));
            }
        }

        const double moonGlow = 0.85;
        const double forceGlow = 0.55;

        foreach (var row in OverlayItems())
        {
            if (moonItems.Contains((row.ItemKind, row.Id)))
                row.AttentionGlow = moonGlow;
            else if (forceItems.Contains((row.ItemKind, row.Id)))
                row.AttentionGlow = forceGlow;
            else
                row.AttentionGlow = 0;
        }

        foreach (var section in _sections.Values)
        {
            if (moonFolders.Contains(section.Id))
                section.AttentionGlow = moonGlow;
            else if (forceFolders.Contains(section.Id))
                section.AttentionGlow = forceGlow;
            else
                section.AttentionGlow = 0;
        }
    }

    // auto model
    static bool IsImportantAttention(
        (string Title, DateTime At, bool Active, bool Delayable, bool Important, TimeSpan Lead, string Kind, string Id, string CategoryId) item,
        DateTime now) =>
        item.Important && (item.Active || item.At - now <= item.Lead);

    // auto model: true when important is why main is up despite layout/temp/tray hide
    bool MainForcedVisibleByImportant(DateTime now) =>
        ImportantForcesMainVisible(now) && MainWouldBeHiddenWithoutImportant(now);

    // auto model
    bool MainWouldBeHiddenWithoutImportant(DateTime now) =>
        _hiddenAll
        || _layoutHideAll
        || _hiddenWindows.Contains(MainOverlayId)
        || Data.Settings.TempHideMain && TempHideWouldHoldWithoutImportant(now);

    // auto model: temp-hide hold if Important did not wake the overlay
    bool TempHideWouldHoldWithoutImportant(DateTime now)
    {
        if (!Data.Settings.TempHideMain)
            return false;
        if (LayoutSceneActive)
            return false;
        if (Data.Settings.TempHideUntil is DateTime until && now >= until)
            return false;
        var items = UpcomingItems(now);
        if (items.Any(item => item.Active && !item.Delayable && !item.Important))
            return false;
        var next = items.FirstOrDefault(item => !item.Delayable && !item.Important && !item.Active);
        if (next.Title != null && next.At - now <= next.Lead)
            return false;
        return true;
    }

    string? VisibleCollapsedFolder(string? categoryId)
    {
        var chain = new List<string>();
        var id = categoryId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(id) && guard++ < 64)
        {
            var folder = Data.Categories.FirstOrDefault(c => c.Id == id);
            if (folder == null)
                break;
            chain.Add(id);
            if (folder.PoppedOut)
                break;
            id = folder.ParentId;
        }

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var folder = Data.Categories.FirstOrDefault(c => c.Id == chain[i]);
            if (folder?.Collapsed == true)
                return chain[i];
        }

        return null;
    }

    public void MinimizeToTray()
    {
        if (LayoutSceneActive)
            return;
        _hiddenAll = true;
        ApplyOverlayVisibility();
    }

    public void ShowOverlays()
    {
        if (LayoutSceneActive)
            return;
        DiscardSessionHide();
        ApplyOverlayVisibility();
        Persist();
        UpdateTrayChecks();
    }

    void DiscardSessionHide()
    {
        _hiddenAll = false;
        _userHidMain = false;
        Data.Settings.TempHideMain = false;
        Data.Settings.TempHideUntil = null;
        _hiddenWindows.Clear();
        _countdown?.Hide();
        if (Data.Settings.Desktop != null)
        {
            Data.Settings.Desktop.HiddenAll = false;
            Data.Settings.Desktop.UserHidMain = false;
        }
    }

    // auto model: important due / within lead shows main even when an app layout hid it
    bool ImportantForcesMainVisible(DateTime now) =>
        UpcomingItems(now).Any(item => IsImportantAttention(item, now));

    bool IsOverlayHidden(string id)
    {
        // auto model
        if (id == MainOverlayId && ImportantForcesMainVisible(DateTime.Now))
            return false;
        return _hiddenAll
            || _layoutHideAll
            || _hiddenWindows.Contains(id)
            || id == MainOverlayId && Data.Settings.TempHideMain && TempHideWouldHold(DateTime.Now);
    }

    void ApplyOverlayVisibility()
    {
        if (_reminders != null)
        {
            if (IsOverlayHidden(MainOverlayId))
                _reminders.Hide();
            else
            {
                _reminders.Show();
                _reminders.Topmost = false;
                _reminders.Topmost = true;
            }
        }

        SyncCategoryWindows();
        foreach (var window in _categoryWindows.Values.Where(w => w.IsVisible))
        {
            window.Topmost = false;
            window.Topmost = true;
        }
        // auto model
        SyncClockOverlay();
        SyncUsageOverlay();
        UpdateTempHideCountdown();
    }

    public double ClockHorizonHours()
    {
        var hours = Data.Settings.ClockHorizonHours;
        if (hours <= 0)
            hours = 24;
        return Math.Clamp(hours, 1, 14 * 24);
    }

    public string ClockHorizonLabel()
    {
        var hours = ClockHorizonHours();
        if (!Data.Settings.ClockHorizonInHours && hours >= 24 && Math.Abs(hours % 24) < 0.001)
        {
            var days = (int)Math.Round(hours / 24);
            return days == 1 ? "Next day" : $"Next {days} days";
        }

        if (hours >= 24 && Math.Abs(hours % 24) < 0.001)
        {
            var days = (int)Math.Round(hours / 24);
            return days == 1 ? "Next 24h" : $"Next {days * 24}h";
        }

        return hours == 1 ? "Next hour" : $"Next {hours:0.###}h";
    }

    public List<(string Title, double HoursAhead, string Kind, DateTime At, double? UsedPercent)> ClockEvents(DateTime now)
    {
        var span = ClockHorizonHours();
        var end = now.AddHours(span);
        var s = Data.Settings;
        var allowed = s.ClockAllCategories || s.ClockCategoryIds is not { Count: > 0 }
            ? null
            : s.ClockCategoryIds.ToHashSet(StringComparer.Ordinal);
        var rows = new List<(string Title, double HoursAhead, string Kind, DateTime At, double? UsedPercent)>();
        foreach (var item in UpcomingItems(now))
        {
            if (item.Kind == "todo" && !s.ClockShowRecurring)
                continue;
            if (item.Kind == "event" && !s.ClockShowScheduled)
                continue;
            // Usage markers come from BuildResetEvents below (independent of board filter).
            if (item.Kind.StartsWith("usage-", StringComparison.Ordinal))
                continue;
            if (item.Kind is not ("todo" or "event"))
                continue;
            if (allowed != null)
            {
                var folder = ResolveCategoryId(item.CategoryId);
                if (folder.Length == 0)
                {
                    if (!allowed.Contains(""))
                        continue;
                }
                else if (!ClockCategoryAllowed(folder, allowed))
                    continue;
            }

            DateTime at;
            double hoursAhead;
            if (item.Active)
            {
                at = item.At;
                hoursAhead = 0;
            }
            else if (item.At > now && item.At <= end)
            {
                at = item.At;
                hoursAhead = (at - now).TotalHours;
            }
            else
                continue;

            rows.Add((item.Title, Math.Max(0, hoursAhead), item.Kind, at, null));
        }

        // auto model: clock usage wedges use their own at-limit lead hours (not the board filter)
        if (s.UsageShowOnClock)
        {
            foreach (var reset in UsageTracker.BuildResetEvents(Usage?.Latest, s, now))
            {
                if (reset.At <= now || reset.At > end)
                    continue;
                if (!UsageTracker.ShouldShowOnClock(reset, s, now))
                    continue;
                rows.Add((reset.Title, (reset.At - now).TotalHours, reset.Kind, reset.At, reset.UsedPercent));
            }
        }

        return rows
            .OrderBy(r => r.HoursAhead)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(40)
            .ToList();
    }

    // auto model
    bool ClockCategoryAllowed(string categoryId, HashSet<string> allowed)
    {
        var id = categoryId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(id) && guard++ < 64)
        {
            if (allowed.Contains(id))
                return true;
            var folder = Data.Categories.FirstOrDefault(c => c.Id == id);
            if (folder == null)
                return false;
            id = folder.ParentId ?? "";
            if (id.Length == 0)
                return allowed.Contains("");
        }

        return false;
    }

    public void SyncClockOverlay()
    {
        if (!Data.Settings.ShowClockModule || _hiddenAll || _layoutHideAll)
        {
            if (_clock != null)
            {
                _clock.Hide();
                if (!Data.Settings.ShowClockModule)
                {
                    _clock.Close();
                    _clock = null;
                }
            }

            return;
        }

        if (_clock == null)
        {
            _clock = new ClockOverlay();
            RestoreBounds(_clock, Data.Settings.Clock, defaultTopOffset: 80, _reminders);
        }

        if (!_clock.IsVisible)
            _clock.Show();
        _clock.ApplyChrome();
        _clock.Refresh();
        _clock.Topmost = false;
        _clock.Topmost = true;
    }

    public void HideClockModule()
    {
        Data.Settings.ShowClockModule = false;
        SyncClockOverlay();
        RequestSave();
        UiChanged?.Invoke();
    }

    // auto model
    public void NotifyUsageUi()
    {
        SyncUsageOverlay();
        _usage?.Refresh();
        _reminders?.RefreshUsageDock();
        _clock?.Refresh();
        RefreshOverlayItems();
        UiChanged?.Invoke();
    }

    void OnUsageChanged() => NotifyUsageUi();

    public void SyncUsageOverlay()
    {
        var showModule = Data.Settings.ShowUsageModule && !_hiddenAll && !_layoutHideAll;
        var floating = showModule && !Data.Settings.UsageDockInReminders;

        if (!floating)
        {
            if (_usage != null)
            {
                _usage.Hide();
                if (!Data.Settings.ShowUsageModule || Data.Settings.UsageDockInReminders)
                {
                    _usage.Close();
                    _usage = null;
                }
            }
        }
        else
        {
            if (_usage == null)
            {
                _usage = new UsageOverlay();
                RestoreBounds(_usage, Data.Settings.Usage, defaultTopOffset: 120, _reminders);
            }

            if (!_usage.IsVisible)
                _usage.Show();
            _usage.ApplyChrome();
            _usage.Refresh();
            _usage.Topmost = false;
            _usage.Topmost = true;
        }

        _reminders?.RefreshUsageDock();
    }

    public void HideUsageModule()
    {
        Data.Settings.ShowUsageModule = false;
        SyncUsageOverlay();
        RequestSave();
        UiChanged?.Invoke();
    }

    void UpdateTempHideCountdown()
    {
        var now = DateTime.Now;
        if (_hiddenAll || TempHideReturnAt(now) is not DateTime wake || wake <= now || wake - now > TempHideWarn())
        {
            _countdown?.Hide();
            return;
        }

        _countdown ??= new TempHideCountdown();
        _countdown.ShowRemaining(wake - now, Data.Settings.TempHideWarnCorner, CountdownWorkArea);
    }

    DateTime? TempHideReturnAt(DateTime now)
    {
        if (!TempHideWouldHold(now))
            return null;
        DateTime? wake = Data.Settings.TempHideUntil;
        // auto model
        var next = UpcomingItems(now).FirstOrDefault(item => HideAware(item) && !item.Active);
        if (next.Title != null)
        {
            var byItem = next.At - next.Lead;
            if (wake == null || byItem < wake)
                wake = byItem;
        }

        return wake;
    }

    Rect CountdownWorkArea(Window visual)
    {
        var bounds = Data.Settings.Reminders;
        var x = bounds.Left ?? visual.Left;
        var y = bounds.Top ?? visual.Top;
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            var work = SystemParameters.WorkArea;
            x = work.Left + 40;
            y = work.Top + 40;
        }

        return WorkAreaFromPoint(visual, x, y);
    }

    public void DiscardHideAndSave()
    {
        DiscardSessionHide();
        Save();
    }

    public void Exit()
    {
        DiscardHideAndSave();
        System.Windows.Application.Current.Shutdown();
    }

    public void ToggleTodo(string id, bool isChecked)
    {
        var todo = Data.Todos.FirstOrDefault(t => t.Id == id);
        if (todo == null)
            return;
        if (isChecked && ResolvedCheckRule(todo.CategoryId, todo.CheckRule) == FolderCheckRule.DeleteOnCheck)
        {
            RememberDeleted("todo", todo.Id, todo);
            Data.Todos.Remove(todo);
            UntrackTask("todo", todo.Id);
            RequestSave();
            return;
        }

        todo.CompletedAt = isChecked ? DateTime.Now : null;
        RequestSave();
    }

    public void ToggleOnce(string id, bool isChecked)
    {
        var item = Data.Once.FirstOrDefault(t => t.Id == id);
        if (item == null)
            return;
        if (isChecked && ResolvedCheckRule(item.CategoryId, item.CheckRule) == FolderCheckRule.DeleteOnCheck)
        {
            RememberDeleted("once", item.Id, item);
            Data.Once.Remove(item);
            UntrackTask("once", item.Id);
            RequestSave();
            return;
        }

        item.CompletedAt = isChecked ? DateTime.Now : null;
        RequestSave();
    }

    public void ToggleEvent(string id, bool isChecked)
    {
        var ev = Data.Events.FirstOrDefault(e => e.Id == id);
        if (ev == null)
            return;
        if (isChecked && !EventCanComplete(ev, DateTime.Now))
            return;
        if (isChecked && ResolvedCheckRule(ev.CategoryId, ev.CheckRule) == FolderCheckRule.DeleteOnCheck)
        {
            RememberDeleted("event", ev.Id, ev);
            Data.Events.Remove(ev);
            UntrackTask("event", ev.Id);
            RequestSave();
            return;
        }

        ev.CompletedAt = isChecked ? DateTime.Now : null;
        RequestSave();
    }

    static bool EventCanComplete(EventItem ev, DateTime now) =>
        ev.At is DateTime when && now >= when;

    public void PromptSetEventWhen(string id, Window? owner)
    {
        var ev = Data.Events.FirstOrDefault(e => e.Id == id);
        if (ev == null || ev.At != null || ev.CompletedAt != null)
            return;
        if (_setWhen != null)
        {
            _setWhen.Bind(id);
            _setWhen.Activate();
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(_setWhen).EnsureHandle());
            return;
        }

        _setWhen = new SetEventWhenWindow(id)
        {
            Owner = owner,
            Topmost = true
        };
        _setWhen.Closed += (_, _) => _setWhen = null;
        _setWhen.Show();
        _setWhen.Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(_setWhen).EnsureHandle());
    }

    public bool SetEventWhen(string id, DateTime at)
    {
        var ev = Data.Events.FirstOrDefault(e => e.Id == id);
        if (ev == null || ev.CompletedAt != null)
            return false;
        ev.At = at;
        RequestSave();
        return true;
    }

    public void MarkCheckedNow(string kind, string id)
    {
        if (kind == "todo")
        {
            var todo = Data.Todos.FirstOrDefault(t => t.Id == id);
            if (todo == null || !todo.UnknownDate)
                return;
            todo.LastCheckedAt = DateTime.Now;
        }
        else if (kind == "event")
        {
            var ev = Data.Events.FirstOrDefault(e => e.Id == id);
            if (ev == null || ev.At != null)
                return;
            ev.LastCheckedAt = DateTime.Now;
        }
        else
            return;
        RequestSave();
    }

    public void HideNote(string id)
    {
        var note = Data.Notes.FirstOrDefault(n => n.Id == id);
        if (note == null)
            return;
        note.Hidden = true;
        RequestSave();
    }

    public void DeleteItem(string id, string kind)
    {
        var removed = kind switch
        {
            "once" => Data.Once.RemoveAll(t => t.Id == id) > 0,
            "todo" => Data.Todos.RemoveAll(t => t.Id == id) > 0,
            "event" => Data.Events.RemoveAll(t => t.Id == id) > 0,
            "note" => Data.Notes.RemoveAll(t => t.Id == id) > 0,
            _ => false
        };
        if (removed)
        {
            UntrackTask(kind, id);
            RequestSave();
        }
    }

    public void RestoreNote(string id)
    {
        var note = Data.Notes.FirstOrDefault(n => n.Id == id);
        if (note == null)
            return;
        note.Hidden = false;
        RequestSave();
    }

    public void ScheduleBoundsSave()
    {
        _saveBounds.Stop();
        _saveBounds.Start();
    }

    public SolidColorBrush PanelBrush()
    {
        var a = OpacityByte(Data.Settings.Opacity, 20);
        var rgb = PanelRgb();
        return CachedBrush($"panel:{a}:{rgb}", Color.FromArgb(a, rgb, rgb, rgb));
    }

    public SolidColorBrush OpaquePanelBrush()
    {
        var rgb = PanelRgb();
        return CachedBrush($"panel-opaque:{rgb}", Color.FromArgb(255, rgb, rgb, rgb));
    }

    byte PanelRgb()
    {
        var darkness = Math.Clamp(Data.Settings.PanelDarkness, 0, 1);
        return (byte)Math.Round(72 * (1 - darkness));
    }

    ImageBrush? _dither;
    double _ditherStrength = -1;

    public MediaBrush DitherBrush()
    {
        if (Data.Settings.PanelDither == false)
            return System.Windows.Media.Brushes.Transparent;
        var strength = Math.Clamp(Data.Settings.PanelDitherStrength, 0, 1);
        if (strength <= 0.01)
            return System.Windows.Media.Brushes.Transparent;
        if (_dither != null && Math.Abs(_ditherStrength - strength) < 0.005)
            return _dither;
        _ditherStrength = strength;
        _dither = BuildDither(strength);
        return _dither;
    }

    static readonly byte[,] BlueNoise16 = CreateBlueNoise(16);

    static byte[,] CreateBlueNoise(int n)
    {
        var used = new bool[n, n];
        var order = new byte[n, n];
        var total = n * n;
        for (var i = 0; i < total; i++)
        {
            var best = double.MaxValue;
            var bx = 0;
            var by = 0;
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    if (used[y, x])
                        continue;
                    double energy = 0;
                    for (var yy = 0; yy < n; yy++)
                    {
                        for (var xx = 0; xx < n; xx++)
                        {
                            if (!used[yy, xx])
                                continue;
                            var dx = Math.Min(Math.Abs(xx - x), n - Math.Abs(xx - x));
                            var dy = Math.Min(Math.Abs(yy - y), n - Math.Abs(yy - y));
                            energy += Math.Exp(-(dx * dx + dy * dy) / 3.6);
                        }
                    }

                    if (energy < best)
                    {
                        best = energy;
                        bx = x;
                        by = y;
                    }
                }
            }

            used[by, bx] = true;
            order[by, bx] = (byte)i;
        }

        return order;
    }

    static ImageBrush BuildDither(double strength)
    {
        const int size = 32;
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(
            size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        var maxA = 36 + strength * 20;
        var span = Math.Max(strength * 1.65, 0.05);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var a = BlueNoise16[y & 15, x & 15] / 255.0;
                var b = BlueNoise16[(y + 5) & 15, (x + 11) & 15] / 255.0;
                var rank = (a * 0.65) + (b * 0.35);
                var t = Math.Max(0, 1 - rank / span);
                var alpha = (byte)Math.Round(t * t * maxA);
                pixels[(y * size + x) * 4 + 3] = alpha;
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        return brush;
    }

    public SolidColorBrush PanelOutlineBrush()
    {
        if (!Data.Settings.ShowWindowBorder)
            return CachedBrush("border:off", Colors.Transparent);
        var a = OpacityByte(Data.Settings.Opacity, 20);
        return CachedBrush($"border:{a}", Color.FromArgb(a, 0, 0, 0));
    }

    public Thickness PanelBorderThickness() =>
        Data.Settings.ShowWindowBorder ? new Thickness(1) : new Thickness(0);

    public SolidColorBrush TextBackdropBrush()
    {
        var a = OpacityByte(Data.Settings.TextBackdropOpacity, 20);
        return CachedBrush($"textbd:{a}", Color.FromArgb(a, 0, 0, 0));
    }

    static byte OpacityByte(double opacity, byte min) =>
        (byte)Math.Clamp(Math.Round(opacity * 255), min, 255);

    SolidColorBrush CachedBrush(string key, Color color)
    {
        if (_brushes.TryGetValue(key, out var existing) && existing is SolidColorBrush solid)
            return solid;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushes[key] = brush;
        return brush;
    }

    void Tick()
    {
        if (TickTempHide() | TickPairings() | TickLayouts())
            Save();
        // auto model: re-evaluate temp hide and layout hide vs important wake every tick
        else if (Data.Settings.TempHideMain || LayoutSceneActive)
            ApplyOverlayVisibility();
        RefreshOverlayItems();
        UpdateTempHideCountdown();
        MaybeNotify();
    }

    bool TickTempHide()
    {
        if (!Data.Settings.TempHideMain)
            return false;
        var now = DateTime.Now;
        // auto model: only expire on max duration; near/due important items just unhide temporarily
        if (Data.Settings.TempHideUntil is not DateTime until || now < until)
            return false;
        Data.Settings.TempHideMain = false;
        Data.Settings.TempHideUntil = null;
        ApplyOverlayVisibility();
        UpdateTrayChecks();
        return true;
    }

    void SnapshotPairings()
    {
        _pairedWasRunning.Clear();
        if (!Data.Categories.Any(IsUtilityPair))
            return;
        var snapshot = ProcessWatch.Capture();
        foreach (var category in Data.Categories.Where(IsUtilityPair))
        {
            if (ProcessWatch.IsRunning(snapshot, category.PairedExePath))
                OnPairLaunch(category);
            else if (category.PairOpened)
                OnPairExit(category);
        }
    }

    public void RememberPairingBaseline(string categoryId)
    {
        var category = Data.Categories.FirstOrDefault(c => c.Id == categoryId);
        if (category == null || !ProcessWatch.CategoryIsPaired(category))
        {
            ForgetPairing(categoryId);
            return;
        }

        if (ProcessWatch.IsRunning(category.PairedExePath))
            OnPairLaunch(category);
        else
        {
            _pairedWasRunning.Remove(categoryId);
            SyncPairMinimize(category);
            ApplyOverlayVisibility();
        }
    }

    public void ForgetPairing(string categoryId)
    {
        _pairedWasRunning.Remove(categoryId);
        var category = Data.Categories.FirstOrDefault(c => c.Id == categoryId);
        if (category != null)
            category.PairOpened = false;
        if (_pairHidMain.Remove(categoryId))
            RestoreMainIfPairsAllow();
        ApplyOverlayVisibility();
    }

    public void SyncPairMinimizeMain(string categoryId)
    {
        var category = Data.Categories.FirstOrDefault(c => c.Id == categoryId);
        if (category == null)
            return;
        SyncPairMinimize(category);
        ApplyOverlayVisibility();
    }

    public bool IsPairTargetIgnored(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        return Data.Settings.IgnoredPairTargets.Any(t =>
            t.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    public void HidePairTarget(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsPairTargetIgnored(path))
            return;
        Data.Settings.IgnoredPairTargets.Add(new IgnoredPairTarget
        {
            Name = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileName(path) : name,
            FilePath = path
        });
        Save();
    }

    public void RestorePairTarget(string path)
    {
        Data.Settings.IgnoredPairTargets.RemoveAll(t =>
            t.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    bool TickPairings()
    {
        var paired = Data.Categories.Where(IsUtilityPair).ToList();
        if (paired.Count == 0)
        {
            var leftover = _pairedWasRunning.Count > 0 || _pairHidMain.Count > 0;
            _pairedWasRunning.Clear();
            _pairHidMain.Clear();
            if (leftover)
            {
                RestoreMainIfPairsAllow();
                ApplyOverlayVisibility();
            }
            return leftover;
        }

        var snapshot = ProcessWatch.Capture();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        foreach (var category in paired)
        {
            seen.Add(category.Id);
            var running = ProcessWatch.IsRunning(snapshot, category.PairedExePath);
            var was = _pairedWasRunning.Contains(category.Id);
            if (running && !was)
            {
                _pairedWasRunning.Add(category.Id);
                OnPairLaunch(category);
                changed = true;
            }
            else if (!running && was)
            {
                _pairedWasRunning.Remove(category.Id);
                OnPairExit(category);
                changed = true;
            }
        }

        foreach (var stale in _pairedWasRunning.Where(id => !seen.Contains(id)).ToList())
            _pairedWasRunning.Remove(stale);
        var hid = _pairHidMain.Count > 0;
        foreach (var stale in _pairHidMain.Where(id => !seen.Contains(id)).ToList())
            _pairHidMain.Remove(stale);
        if (hid && _pairHidMain.Count == 0)
        {
            RestoreMainIfPairsAllow();
            ApplyOverlayVisibility();
        }

        return changed;
    }

    void OnPairLaunch(Category category)
    {
        _pairedWasRunning.Add(category.Id);
        if (!category.PoppedOut)
        {
            category.PoppedOut = true;
            category.PairOpened = true;
        }

        _hiddenWindows.Remove(category.Id);
        SyncPairMinimize(category);
        ApplyOverlayVisibility();
    }

    void OnPairExit(Category category)
    {
        _pairedWasRunning.Remove(category.Id);
        if (category.PairOpened)
        {
            category.PoppedOut = false;
            category.PairOpened = false;
        }

        SyncPairMinimize(category);
        ApplyOverlayVisibility();
    }

    void SyncPairMinimize(Category category)
    {
        var running = _pairedWasRunning.Contains(category.Id);
        if (running && category.PairMinimizeMain && !_hiddenAll)
        {
            _pairHidMain.Add(category.Id);
            _hiddenWindows.Add(MainOverlayId);
            return;
        }

        if (_pairHidMain.Remove(category.Id))
            RestoreMainIfPairsAllow();
    }

    void RestoreMainIfPairsAllow()
    {
        if (_pairHidMain.Count > 0 || _hiddenAll || _userHidMain)
            return;
        _hiddenWindows.Remove(MainOverlayId);
    }

    static string TaskKey(string kind, string id) => $"{kind}:{id}";

    public void TrackTask(string kind, string id)
    {
        Data.Settings.TaskOrder ??= [];
        var key = TaskKey(kind, id);
        if (!Data.Settings.TaskOrder.Contains(key))
            Data.Settings.TaskOrder.Add(key);
    }

    public void UntrackTask(string kind, string id)
    {
        Data.Settings.TaskOrder?.Remove(TaskKey(kind, id));
    }

    public void MoveTaskOrder(string kind, string id, int delta)
    {
        EnsureTaskOrder();
        var key = TaskKey(kind, id);
        var index = Data.Settings.TaskOrder.IndexOf(key);
        var next = index + delta;
        if (index < 0 || next < 0 || next >= Data.Settings.TaskOrder.Count)
            return;
        (Data.Settings.TaskOrder[index], Data.Settings.TaskOrder[next]) = (Data.Settings.TaskOrder[next], Data.Settings.TaskOrder[index]);
    }

    public void SwapTaskOrder(string kindA, string idA, string kindB, string idB)
    {
        EnsureTaskOrder();
        var a = TaskKey(kindA, idA);
        var b = TaskKey(kindB, idB);
        var ia = Data.Settings.TaskOrder.IndexOf(a);
        var ib = Data.Settings.TaskOrder.IndexOf(b);
        if (ia < 0 || ib < 0)
            return;
        (Data.Settings.TaskOrder[ia], Data.Settings.TaskOrder[ib]) = (Data.Settings.TaskOrder[ib], Data.Settings.TaskOrder[ia]);
    }

    int TaskOrderIndex(string kind, string id)
    {
        EnsureTaskOrder();
        var index = Data.Settings.TaskOrder.IndexOf(TaskKey(kind, id));
        return index < 0 ? int.MaxValue : index;
    }

    void EnsureTaskOrder()
    {
        Data.Settings.TaskOrder ??= [];
        var known = new HashSet<string>(StringComparer.Ordinal);
        void AddAll(string kind, IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                var key = TaskKey(kind, id);
                known.Add(key);
                if (!Data.Settings.TaskOrder.Contains(key))
                    Data.Settings.TaskOrder.Add(key);
            }
        }

        AddAll("once", Data.Once.Select(t => t.Id));
        AddAll("todo", Data.Todos.Select(t => t.Id));
        AddAll("event", Data.Events.Select(t => t.Id));
        AddAll("note", Data.Notes.Select(t => t.Id));
        // auto model
        AddAll("category", Data.Categories.Select(c => c.Id));
        Data.Settings.TaskOrder.RemoveAll(key => !known.Contains(key));
    }

    public int CompareOverlayOrder(string kindA, string idA, string titleA, string kindB, string idB, string titleB)
    {
        if (Data.Settings.ManualSort)
        {
            var byOrder = TaskOrderIndex(kindA, idA).CompareTo(TaskOrderIndex(kindB, idB));
            return byOrder != 0 ? byOrder : string.Compare(titleA, titleB, StringComparison.OrdinalIgnoreCase);
        }

        // auto model: To Do (once) sorts after Recurring/Scheduled unless pinned
        var byKind = AutoSortTier(kindA).CompareTo(AutoSortTier(kindB));
        if (byKind != 0)
            return byKind;
        var byTime = OverlaySortAtFor(kindA, idA).CompareTo(OverlaySortAtFor(kindB, idB));
        return byTime != 0 ? byTime : string.Compare(titleA, titleB, StringComparison.OrdinalIgnoreCase);
    }

    // auto model: folders sit after notes unless pinned among siblings
    static int AutoSortTier(string kind) =>
        kind switch
        {
            "todo" or "event" => 0,
            "once" => 1,
            "note" => 2,
            "category" => 3,
            _ => 2
        };

    // auto model: tasks + folders share auto sort and pin slots; HoldPinWhenHidden ghosts keep slots
    List<object> BuildMixedAutoOrder(
        IReadOnlyList<OverlayItemVm> items,
        IReadOnlyList<CategorySectionVm> folders,
        string parentCategoryId)
    {
        var visibleTasks = items.Select(i => (i.ItemKind, i.Id)).ToHashSet();
        var visibleFolders = folders.Select(f => f.Id).ToHashSet();
        var rows = new List<(object? Entry, string Kind, string Id, string Title, int Pin, bool Ghost)>();
        foreach (var item in items)
            rows.Add((item, item.ItemKind, item.Id, item.Title, item.PinSlot, false));
        foreach (var folder in folders)
            rows.Add((folder, "category", folder.Id, folder.Name, folder.PinSlot, false));
        foreach (var ghost in TaskPinHoldGhosts(parentCategoryId, visibleTasks))
            rows.Add((null, ghost.ItemKind, ghost.Id, ghost.Title, ghost.PinSlot, true));
        foreach (var ghost in FolderPinHoldGhosts(parentCategoryId, visibleFolders))
            rows.Add((null, "category", ghost.Id, ghost.Name, ghost.PinSlot, true));
        rows.Sort((a, b) => CompareOverlayOrder(a.Kind, a.Id, a.Title, b.Kind, b.Id, b.Title));
        ApplyPinnedOrder(rows, r => r.Pin, r => r.Title);
        return rows.Where(r => !r.Ghost && r.Entry != null).Select(r => r.Entry!).ToList();
    }

    // auto model
    List<OverlayItemVm> TaskPinHoldGhosts(string categoryId, HashSet<(string Kind, string Id)> visible)
    {
        var ghosts = new List<OverlayItemVm>();
        void Add(string kind, string id, string title, int pin, bool hold)
        {
            if (!hold || pin <= 0 || visible.Contains((kind, id)))
                return;
            ghosts.Add(new OverlayItemVm
            {
                Id = id,
                ItemKind = kind,
                CategoryId = categoryId,
                Title = title,
                PinSlot = pin,
                IsPinHoldGhost = true
            });
        }

        foreach (var item in Data.Once)
        {
            if (ResolveCategoryId(item.CategoryId) != categoryId)
                continue;
            Add("once", item.Id, item.Title, item.PinSlot, item.HoldPinWhenHidden);
        }

        foreach (var item in Data.Todos)
        {
            if (ResolveCategoryId(item.CategoryId) != categoryId)
                continue;
            Add("todo", item.Id, item.Title, item.PinSlot, item.HoldPinWhenHidden);
        }

        foreach (var item in Data.Events)
        {
            if (ResolveCategoryId(item.CategoryId) != categoryId)
                continue;
            Add("event", item.Id, item.Title, item.PinSlot, item.HoldPinWhenHidden);
        }

        foreach (var item in Data.Notes)
        {
            if (ResolveCategoryId(item.CategoryId) != categoryId)
                continue;
            Add("note", item.Id, item.Title, item.PinSlot, item.HoldPinWhenHidden);
        }

        return ghosts;
    }

    // auto model
    List<CategorySectionVm> FolderPinHoldGhosts(string parentCategoryId, HashSet<string> visibleFolderIds)
    {
        string? parentId = parentCategoryId.Length == 0 ? null : parentCategoryId;
        var ghosts = new List<CategorySectionVm>();
        foreach (var category in CategoryTree.ChildrenOf(Data.Categories, parentId))
        {
            if (category.PoppedOut || !category.HoldPinWhenHidden || category.PinSlot <= 0)
                continue;
            if (visibleFolderIds.Contains(category.Id))
                continue;
            ghosts.Add(new CategorySectionVm
            {
                Id = category.Id,
                Name = category.Name,
                PinSlot = category.PinSlot
            });
        }

        return ghosts;
    }

    public void SetPinSlot(string kind, string id, int pin)
    {
        pin = Math.Clamp(pin, 0, 99);
        switch (kind)
        {
            case "category":
                if (Data.Categories.FirstOrDefault(c => c.Id == id) is Category cat)
                    cat.PinSlot = pin;
                break;
            case "once":
                if (Data.Once.FirstOrDefault(t => t.Id == id) is OnceItem once)
                    once.PinSlot = pin;
                break;
            case "todo":
                if (Data.Todos.FirstOrDefault(t => t.Id == id) is TodoItem todo)
                    todo.PinSlot = pin;
                break;
            case "event":
                if (Data.Events.FirstOrDefault(t => t.Id == id) is EventItem ev)
                    ev.PinSlot = pin;
                break;
            case "note":
                if (Data.Notes.FirstOrDefault(t => t.Id == id) is NoteItem note)
                    note.PinSlot = pin;
                break;
        }
    }

    DateTime OverlaySortAtFor(string kind, string id)
    {
        var now = DateTime.Now;
        switch (kind)
        {
            case "once":
                var once = Data.Once.FirstOrDefault(t => t.Id == id);
                return once == null ? DateTime.MaxValue : RecurrenceEngine.OverlaySortAt(once);
            case "todo":
                var todo = Data.Todos.FirstOrDefault(t => t.Id == id);
                if (todo == null)
                    return DateTime.MaxValue;
                return RecurrenceEngine.OverlaySortAt(todo, now, RecurrenceEngine.EvaluateTodo(todo, now));
            case "event":
                var ev = Data.Events.FirstOrDefault(t => t.Id == id);
                return ev == null ? DateTime.MaxValue : RecurrenceEngine.OverlaySortAt(ev);
            default:
                return DateTime.MaxValue;
        }
    }

    void SortOverlayRows(List<OverlayItemVm> rows, string categoryId)
    {
        // auto model: HoldPinWhenHidden ghosts reserve pin slots while hidden
        var visible = rows.Select(r => (r.ItemKind, r.Id)).ToHashSet();
        var combined = rows.Concat(TaskPinHoldGhosts(categoryId, visible)).ToList();
        combined.Sort((a, b) => CompareOverlayOrder(a.ItemKind, a.Id, a.Title, b.ItemKind, b.Id, b.Title));
        ApplyPinnedOrder(combined, r => r.PinSlot, r => r.Title);
        rows.Clear();
        rows.AddRange(combined.Where(r => !r.IsPinHoldGhost));
    }

    public static void ApplyPinnedOrder<T>(List<T> rows, Func<T, int> pinOf, Func<T, string> titleOf)
    {
        if (rows.Count == 0 || rows.All(r => pinOf(r) <= 0))
            return;
        var result = rows.Where(r => pinOf(r) <= 0).ToList();
        foreach (var group in rows.Where(r => pinOf(r) > 0).GroupBy(pinOf).OrderBy(g => g.Key))
        {
            var items = group.OrderBy(r => titleOf(r), StringComparer.OrdinalIgnoreCase).ToList();
            var index = Math.Clamp(group.Key - 1, 0, result.Count);
            result.InsertRange(index, items);
        }

        rows.Clear();
        rows.AddRange(result);
    }

    void RefreshOverlayItems()
    {
        var now = DateTime.Now;
        var interactive = !Data.Settings.ClickThrough;
        // auto model
        var inFolderIndent = Math.Clamp(Data.Settings.FolderContentIndent, 0, 40);
        var grouped = new Dictionary<string, List<OverlayItemVm>> { [""] = [] };
        foreach (var category in Data.Categories)
            grouped.TryAdd(category.Id, []);
        foreach (var item in Data.Once)
        {
            var complete = item.CompletedAt != null;
            if (HideCheckedOnOverlay("once", item.CategoryId, item.CheckRule, item.CheckReshowHours, complete, null, now))
                continue;
            var categoryId = ResolveCategoryId(item.CategoryId);
            if (!grouped.TryGetValue(categoryId, out var onceRows))
                onceRows = grouped[""];
            onceRows.Add(new OverlayItemVm
            {
                Id = item.Id,
                ItemKind = "once",
                CategoryId = categoryId,
                Title = item.Title,
                Url = item.Url,
                IsChecked = complete,
                ShowCheck = true,
                ShowCheckIn = false,
                ShowTrash = false,
                ShowDelete = false,
                Strikethrough = complete,
                Foreground = ItemColor("once", TaskPalette.FromOverdue(complete, 0), item.Colors),
                TitleWeight = OverlayWeight("once"),
                ShowBackdrop = Data.Settings.TextBackdrop,
                BackdropBrush = TextBackdropBrush(),
                SortAt = RecurrenceEngine.OverlaySortAt(item),
                PinSlot = item.PinSlot,
                ContentIndent = categoryId.Length == 0 ? 0 : inFolderIndent,
                CheckTip = "Mark complete"
            });
        }

        foreach (var todo in Data.Todos)
        {
            var state = RecurrenceEngine.EvaluateTodo(todo, now);
            if (!state.Visible)
                continue;
            if (HideCheckedOnOverlay("todo", todo.CategoryId, todo.CheckRule, todo.CheckReshowHours, state.IsComplete, state.NextChange, now))
                continue;
            if (RecurrenceEngine.HideRecurringByHorizon(todo, now, state,
                    Data.Settings.RecurringHorizon, Data.Settings.HorizonInHours))
                continue;
            var categoryId = ResolveCategoryId(todo.CategoryId);
            if (!grouped.TryGetValue(categoryId, out var todoRows))
                todoRows = grouped[""];
            todoRows.Add(new OverlayItemVm
            {
                Id = todo.Id,
                ItemKind = "todo",
                CategoryId = categoryId,
                Title = todo.Title,
                Url = todo.Url,
                Meta = RecurrenceEngine.FormatTodoMeta(todo, now, state),
                IsChecked = state.IsComplete,
                ShowCheck = true,
                ShowCheckIn = todo.UnknownDate,
                ShowTrash = false,
                ShowDelete = false,
                Strikethrough = state.IsComplete,
                Foreground = ItemColor("todo", TaskPalette.FromOverdue(state.IsComplete, state.OverdueCount), todo.Colors),
                TitleWeight = OverlayWeight("todo"),
                ShowBackdrop = Data.Settings.TextBackdrop,
                BackdropBrush = TextBackdropBrush(),
                SortAt = RecurrenceEngine.OverlaySortAt(todo, now, state),
                PinSlot = todo.PinSlot,
                ContentIndent = categoryId.Length == 0 ? 0 : inFolderIndent,
                CheckTip = "Mark complete"
            });
        }

        foreach (var ev in Data.Events)
        {
            var complete = ev.CompletedAt != null;
            if (HideCheckedOnOverlay("event", ev.CategoryId, ev.CheckRule, ev.CheckReshowHours, complete, null, now))
                continue;
            if (!complete && HideEventFromOverlay(ev, now))
                continue;
            var categoryId = ResolveCategoryId(ev.CategoryId);
            string meta;
            MediaBrush color;
            if (complete)
            {
                meta = ev.At != null ? RecurrenceEngine.FormatCountdown(ev.At.Value, now) : "";
                color = ItemColor("event", TaskColorState.Completed, ev.Colors);
            }
            else if (ev.At is DateTime at)
            {
                var due = now >= at;
                meta = RecurrenceEngine.FormatCountdown(at, now);
                color = ItemColor("event", due ? TaskColorState.Due : TaskColorState.Idle, ev.Colors);
            }
            else
            {
                if (ev.CreatedAt == default)
                    ev.CreatedAt = DateTime.Now;
                var last = RecurrenceEngine.LastChecked(ev);
                meta = RecurrenceEngine.FormatElapsed(last, now);
                color = ItemColor("event", TaskPalette.FromOverdue(false,
                    RecurrenceEngine.OverdueChecks(last, now, RecurrenceEngine.EventCheckEveryDays(ev))), ev.Colors);
            }
            (grouped.GetValueOrDefault(categoryId) ?? grouped[""]).Add(new OverlayItemVm
            {
                Id = ev.Id,
                ItemKind = "event",
                CategoryId = categoryId,
                Title = ev.Title,
                Url = ev.Url,
                Meta = meta,
                IsChecked = complete,
                ShowCheck = ev.At != null || complete,
                CheckEnabled = complete || EventCanComplete(ev, now),
                ShowCheckIn = ev.At == null && !complete,
                ShowSetWhen = ev.At == null && !complete,
                ShowTrash = false,
                ShowDelete = false,
                Strikethrough = complete,
                Foreground = color,
                TitleWeight = OverlayWeight("event"),
                ShowBackdrop = Data.Settings.TextBackdrop,
                BackdropBrush = TextBackdropBrush(),
                SortAt = RecurrenceEngine.OverlaySortAt(ev),
                PinSlot = ev.PinSlot,
                ContentIndent = categoryId.Length == 0 ? 0 : inFolderIndent,
                CheckTip = complete || EventCanComplete(ev, now)
                    ? "Mark complete"
                    : "Available after the scheduled time"
            });
        }

        // auto model: usage reset rows on the main board
        if (Data.Settings.UsageInjectReminders)
        {
            foreach (var reset in UsageTracker.BuildResetEvents(Usage?.Latest, Data.Settings, now))
            {
                if (!UsageTracker.ShouldShowOnRemindersBoard(reset, Data.Settings, now))
                    continue;
                var due = now >= reset.At;
                var tint = UsageTracker.BoardColorHex(reset, Data.Settings);
                grouped[""].Add(new OverlayItemVm
                {
                    Id = reset.Id,
                    ItemKind = reset.Kind,
                    CategoryId = "",
                    Title = reset.Title,
                    Meta = due
                        ? "now"
                        : $"{UsageTracker.FormatCountdown(reset.At, now)} · {Math.Clamp(100 - reset.UsedPercent, 0, 100):0}% left",
                    ShowCheck = false,
                    ShowTrash = false,
                    ShowDelete = false,
                    Foreground = Brush(tint),
                    TitleWeight = OverlayWeight("event"),
                    ShowBackdrop = Data.Settings.TextBackdrop,
                    BackdropBrush = TextBackdropBrush(),
                    SortAt = reset.At,
                    PinSlot = 0,
                    ContentIndent = 0
                });
            }
        }

        foreach (var note in Data.Notes.Where(n => !n.Hidden))
        {
            var categoryId = ResolveCategoryId(note.CategoryId);
            (grouped.GetValueOrDefault(categoryId) ?? grouped[""]).Add(new OverlayItemVm
            {
                Id = note.Id,
                ItemKind = "note",
                CategoryId = categoryId,
                Title = string.IsNullOrWhiteSpace(note.Title) ? note.Body : note.Title,
                Url = note.Url,
                Meta = string.IsNullOrWhiteSpace(note.Title) ? "" : note.Body,
                ShowCheck = false,
                ShowTrash = true,
                ShowDelete = false,
                Foreground = Brush("#DDDDDD"),
                TitleWeight = OverlayWeight("note"),
                ShowBackdrop = Data.Settings.TextBackdrop,
                BackdropBrush = TextBackdropBrush(),
                SortAt = DateTime.MaxValue,
                PinSlot = note.PinSlot,
                ContentIndent = categoryId.Length == 0 ? 0 : inFolderIndent
            });
        }

        foreach (var (categoryId, rows) in grouped)
            SortOverlayRows(rows, categoryId);

        CategoryTree.RefreshLabels(Data.Categories);
        var folderCounts = Data.Settings.ShowFolderCounts ? FolderUncompletedCounts(grouped) : null;

        var docked = new List<CategorySectionVm>();
        var liveIds = Data.Categories.Select(c => c.Id).ToHashSet();
        foreach (var stale in _sections.Keys.Where(id => !liveIds.Contains(id)).ToList())
            _sections.Remove(stale);

        foreach (var category in Data.Categories)
        {
            if (!_sections.TryGetValue(category.Id, out var section))
            {
                section = new CategorySectionVm { Id = category.Id };
                _sections[category.Id] = section;
            }

            section.Name = FolderOverlayName(category.Name, folderCounts, category.Id);
            section.PinSlot = category.PinSlot;
            section.ShowButtons = interactive;
            Sync(section.Items, grouped.GetValueOrDefault(category.Id) ?? []);
        }

        foreach (var category in Data.Categories.Where(c => c.PoppedOut))
            WireSection(category, 0);

        foreach (var category in Data.Categories.Where(c => CategoryTree.Parent(Data.Categories, c) == null && !c.PoppedOut))
        {
            WireSection(category, 0);
            var section = _sections[category.Id];
            if (ShowSection(section, interactive))
                docked.Add(section);
        }

        // auto model
        if (Data.Settings.ManualSort)
            docked = docked
                .OrderBy(s => TaskOrderIndex("category", s.Id))
                .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        for (var i = 0; i < docked.Count; i++)
        {
            if (i < DockedCategories.Count)
            {
                if (!ReferenceEquals(DockedCategories[i], docked[i]))
                    DockedCategories[i] = docked[i];
            }
            else
            {
                DockedCategories.Add(docked[i]);
            }
        }

        while (DockedCategories.Count > docked.Count)
            DockedCategories.RemoveAt(DockedCategories.Count - 1);

        Sync(UncategorizedItems, grouped.GetValueOrDefault("") ?? []);
        // auto model
        SyncRootBoard(UncategorizedItems, DockedCategories);
        SyncCategoryWindows();
        ApplyDueHighlight();
        UiChanged?.Invoke();
    }

    // auto model
    void SyncRootBoard(IReadOnlyList<OverlayItemVm> loose, IReadOnlyList<CategorySectionVm> folders)
    {
        var fresh = new List<object>();
        if (Data.Settings.ManualSort)
        {
            foreach (var key in Data.Settings.TaskOrder)
            {
                var sep = key.IndexOf(':');
                if (sep <= 0)
                    continue;
                var kind = key[..sep];
                var id = key[(sep + 1)..];
                if (kind == "category")
                {
                    var section = folders.FirstOrDefault(f => f.Id == id);
                    if (section != null)
                        fresh.Add(section);
                }
                else
                {
                    var item = loose.FirstOrDefault(t => t.ItemKind == kind && t.Id == id);
                    if (item != null)
                        fresh.Add(item);
                }
            }

            foreach (var item in loose)
            {
                if (!fresh.Contains(item))
                    fresh.Add(item);
            }

            foreach (var section in folders)
            {
                if (!fresh.Contains(section))
                    fresh.Add(section);
            }
        }
        else
        {
            // auto model
            fresh.AddRange(BuildMixedAutoOrder(loose, folders, ""));
        }

        SyncObjects(RootBoard, fresh);
    }

    static void SyncObjects(ObservableCollection<object> target, List<object> fresh)
    {
        for (var i = 0; i < fresh.Count; i++)
        {
            if (i < target.Count)
            {
                if (!ReferenceEquals(target[i], fresh[i]))
                    target[i] = fresh[i];
            }
            else
                target.Add(fresh[i]);
        }

        while (target.Count > fresh.Count)
            target.RemoveAt(target.Count - 1);
    }

    public void ToggleCategoryCollapsed(string id)
    {
        var category = Data.Categories.FirstOrDefault(c => c.Id == id);
        if (category == null)
            return;
        category.Collapsed = !category.Collapsed;
        if (ActiveLayout() is AppLayout layout)
            FolderOverride(layout, id).Collapsed = category.Collapsed;
        RequestSave();
    }

    void WireSection(Category category, int depth)
    {
        var section = _sections[category.Id];
        var kids = CategoryTree.ChildrenOf(Data.Categories, category.Id)
            .Where(c => !c.PoppedOut)
            .ToList();
        foreach (var kid in kids)
            WireSection(kid, depth + 1);

        var childSections = kids
            .Select(c => _sections[c.Id])
            .Where(s => ShowSection(s, !Data.Settings.ClickThrough))
            .ToList();
        // auto model
        if (Data.Settings.ManualSort)
            childSections = childSections
                .OrderBy(s => TaskOrderIndex("category", s.Id))
                .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        section.Depth = depth;
        // auto model: root folders use TopFolderIndent; nested folders step by FolderIndent
        section.Indent = Math.Clamp(
            depth == 0 ? Data.Settings.TopFolderIndent : Data.Settings.FolderIndent,
            0, 40);
        section.HasChildren = childSections.Count > 0 || section.Items.Count > 0;
        section.IsExpanded = !category.Collapsed;
        SyncSections(section.Children, childSections);
        SyncSectionEntries(section, childSections);
    }

    // auto model
    void SyncSectionEntries(CategorySectionVm section, List<CategorySectionVm> childSections)
    {
        var fresh = new List<object>();
        if (Data.Settings.ManualSort)
        {
            foreach (var key in Data.Settings.TaskOrder)
            {
                var sep = key.IndexOf(':');
                if (sep <= 0)
                    continue;
                var kind = key[..sep];
                var id = key[(sep + 1)..];
                if (kind == "category")
                {
                    var child = childSections.FirstOrDefault(c => c.Id == id);
                    if (child != null)
                        fresh.Add(child);
                }
                else
                {
                    var item = section.Items.FirstOrDefault(t => t.ItemKind == kind && t.Id == id);
                    if (item != null)
                        fresh.Add(item);
                }
            }

            foreach (var item in section.Items)
            {
                if (!fresh.Contains(item))
                    fresh.Add(item);
            }

            foreach (var child in childSections)
            {
                if (!fresh.Contains(child))
                    fresh.Add(child);
            }
        }
        else
        {
            // auto model
            fresh.AddRange(BuildMixedAutoOrder(section.Items.ToList(), childSections, section.Id));
        }

        SyncObjects(section.Entries, fresh);
    }

    static bool SectionVisible(CategorySectionVm section) =>
        section.Items.Count > 0 || section.Children.Any(SectionVisible);

    bool ShowSection(CategorySectionVm section, bool interactive)
    {
        if (SectionVisible(section))
            return true;
        return interactive && !Data.Settings.HideEmptyFolders;
    }

    static string FolderOverlayName(string name, Dictionary<string, int>? counts, string id) =>
        counts != null && counts.TryGetValue(id, out var n) ? $"{name} ({n})" : name;

    Dictionary<string, int> FolderUncompletedCounts(Dictionary<string, List<OverlayItemVm>> grouped)
    {
        var totals = new Dictionary<string, int>();
        int Walk(string id)
        {
            if (totals.TryGetValue(id, out var cached))
                return cached;
            totals[id] = 0;
            var n = (grouped.GetValueOrDefault(id) ?? [])
                .Count(item => item.ItemKind == "note" || !item.IsChecked);
            foreach (var child in CategoryTree.ChildrenOf(Data.Categories, id))
            {
                var childTotal = Walk(child.Id);
                if (!child.PoppedOut)
                    n += childTotal;
            }

            totals[id] = n;
            return n;
        }

        foreach (var category in Data.Categories)
            Walk(category.Id);
        return totals;
    }

    public void BeginOverlayDrag() => _overlayDrag = true;

    public void EndOverlayDrag()
    {
        _overlayDrag = false;
        foreach (var section in _sections.Values)
            section.DropHover = false;
    }

    public void SetCategoryDropHover(string? id)
    {
        foreach (var section in _sections.Values)
            section.DropHover = section.Id == id;
    }

    public void DropOnCategory(string targetId, OverlayDrag drag, string place)
    {
        if (drag.Kind == "category")
        {
            var item = Data.Categories.FirstOrDefault(c => c.Id == drag.Id);
            var target = Data.Categories.FirstOrDefault(c => c.Id == targetId);
            if (item == null || target == null)
                return;
            if (place == "before")
                CategoryTree.PlaceBeside(Data.Categories, item, target, false);
            else if (place == "after")
                CategoryTree.PlaceBeside(Data.Categories, item, target, true);
            else
                CategoryTree.PlaceInside(Data.Categories, item, target);
        }
        else
        {
            MoveItemToCategory(drag.Kind, drag.Id, targetId);
            return;
        }

        RequestSave();
        ApplyOverlayVisibility();
    }

    public void DropOnRoot(OverlayDrag drag)
    {
        if (drag.Kind != "category")
        {
            MoveItemToCategory(drag.Kind, drag.Id, "");
            return;
        }
        var item = Data.Categories.FirstOrDefault(c => c.Id == drag.Id);
        if (item == null)
            return;
        CategoryTree.PlaceRoot(Data.Categories, item);
        RequestSave();
        ApplyOverlayVisibility();
    }

    public void MoveItemToCategory(string kind, string id, string categoryId)
    {
        categoryId = ResolveCategoryId(categoryId);
        switch (kind)
        {
            case "once":
                var once = Data.Once.FirstOrDefault(t => t.Id == id);
                if (once != null)
                    once.CategoryId = categoryId;
                break;
            case "todo":
                var todo = Data.Todos.FirstOrDefault(t => t.Id == id);
                if (todo != null)
                    todo.CategoryId = categoryId;
                break;
            case "event":
                var ev = Data.Events.FirstOrDefault(t => t.Id == id);
                if (ev != null)
                    ev.CategoryId = categoryId;
                break;
            case "note":
                var note = Data.Notes.FirstOrDefault(t => t.Id == id);
                if (note != null)
                    note.CategoryId = categoryId;
                break;
        }

        RequestSave();
        ApplyOverlayVisibility();
    }

    static void SyncSections(ObservableCollection<CategorySectionVm> target, List<CategorySectionVm> fresh)
    {
        for (var i = 0; i < fresh.Count; i++)
        {
            if (i < target.Count)
            {
                if (!ReferenceEquals(target[i], fresh[i]))
                    target[i] = fresh[i];
            }
            else
            {
                target.Add(fresh[i]);
            }
        }

        while (target.Count > fresh.Count)
            target.RemoveAt(target.Count - 1);
    }

    static void Sync(ObservableCollection<OverlayItemVm> target, List<OverlayItemVm> fresh)
    {
        for (var i = 0; i < fresh.Count; i++)
        {
            if (i < target.Count && target[i].Id == fresh[i].Id && target[i].ItemKind == fresh[i].ItemKind)
            {
                var row = target[i];
                var src = fresh[i];
                row.Title = src.Title;
                row.Url = src.Url;
                row.Meta = src.Meta;
                row.IsChecked = src.IsChecked;
                row.ShowCheck = src.ShowCheck;
                row.CheckEnabled = src.CheckEnabled;
                row.ShowCheckIn = src.ShowCheckIn;
                row.ShowSetWhen = src.ShowSetWhen;
                row.ShowTrash = src.ShowTrash;
                row.ShowDelete = src.ShowDelete;
                row.Strikethrough = src.Strikethrough;
                row.Foreground = src.Foreground;
                row.TitleWeight = src.TitleWeight;
                row.ShowBackdrop = src.ShowBackdrop;
                row.BackdropBrush = src.BackdropBrush;
                row.CheckTip = src.CheckTip;
                row.AttentionGlow = src.AttentionGlow;
                row.ContentIndent = src.ContentIndent;
            }
            else if (i < target.Count)
            {
                target[i] = fresh[i];
            }
            else
            {
                target.Add(fresh[i]);
            }
        }

        while (target.Count > fresh.Count)
            target.RemoveAt(target.Count - 1);
    }

    void MaybeNotify()
    {
        if (!Data.Settings.Notifications || _tray == null)
            return;

        var now = DateTime.Now;
        foreach (var todo in Data.Todos)
        {
            var state = RecurrenceEngine.EvaluateTodo(todo, now);
            var key = $"todo:{todo.Id}:{state.OverdueCount}:{state.IsComplete}";
            if (todo.UnknownDate || !state.Visible || state.IsComplete || _notified.Contains(key))
                continue;
            if (state.OverdueCount == 0 && todo.CompletedAt != null)
                continue;
            if (state.OverdueCount > 0 || IsNewlyDue(todo, now))
            {
                _notified.Add(key);
                _tray.ShowBalloonTip(3500, "AtFutureMe", todo.Title, WinForms.ToolTipIcon.Info);
            }
        }

        foreach (var ev in Data.Events)
        {
            if (ev.CompletedAt != null || ev.At is not DateTime at || now < at)
                continue;
            var key = $"event:{ev.Id}";
            if (_notified.Contains(key))
                continue;
            _notified.Add(key);
            _tray.ShowBalloonTip(3500, "AtFutureMe", ev.Title, WinForms.ToolTipIcon.Info);
        }
    }

    static bool IsNewlyDue(TodoItem todo, DateTime now)
    {
        var prev = RecurrenceEngine.EvaluateTodo(todo, now.AddSeconds(-2));
        var cur = RecurrenceEngine.EvaluateTodo(todo, now);
        return cur.Visible && !cur.IsComplete && (!prev.Visible || prev.IsComplete);
    }

    void ApplyWindowChrome()
    {
        var resize = Data.Settings.ClickThrough ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        if (_reminders != null)
        {
            _reminders.ResizeMode = resize;
            _reminders.ApplyChrome();
        }

        if (_clock != null)
        {
            _clock.ResizeMode = resize;
            _clock.ApplyChrome();
        }

        if (_usage != null)
        {
            // Usage lock mode owns its own resize/chrome; still refresh click-through.
            _usage.ApplyChrome();
        }

        foreach (var window in _categoryWindows.Values)
        {
            window.ResizeMode = resize;
            window.ApplyChrome();
        }

        _reminders?.ApplyClickThrough();
        _clock?.ApplyClickThrough();
        _usage?.ApplyClickThrough();
        foreach (var window in _categoryWindows.Values)
            window.ApplyClickThrough();
        _countdown?.ApplyChrome();
        UpdateTempHideCountdown();
    }

    void SyncCategoryWindows()
    {
        var popped = Data.Categories.Where(c => c.PoppedOut).ToList();
        var ids = popped.Select(c => c.Id).ToHashSet();
        foreach (var id in _categoryWindows.Keys.Where(key => !ids.Contains(key)).ToList())
        {
            _categoryWindows[id].Close();
            _categoryWindows.Remove(id);
        }

        var cascade = 0;
        foreach (var category in popped)
        {
            if (!_categoryWindows.TryGetValue(category.Id, out var window))
            {
                window = new CategoryOverlay(category.Id);
                RestoreBounds(window, category.Bounds, 80, _reminders, cascade);
                if (category.Bounds.Left == null || category.Bounds.Top == null)
                    cascade++;
                _categoryWindows[category.Id] = window;
            }

            if (IsOverlayHidden(category.Id))
                window.Hide();
            else if (!window.IsVisible)
                window.Show();
            window.ApplyChrome();
        }
    }

    void CaptureBounds()
    {
        var layout = ActiveLayout();
        if (_reminders is { IsLoaded: true })
        {
            var read = ReadBounds(_reminders);
            Data.Settings.Reminders.Left = read.Left;
            Data.Settings.Reminders.Top = read.Top;
            Data.Settings.Reminders.Width = read.Width;
            Data.Settings.Reminders.Height = read.Height;
            if (layout != null)
            {
                var expected = layout.MainBounds ?? DesktopReminders();
                if (!WindowBounds.CloseTo(read, expected))
                    layout.MainBounds = read.Clone();
            }
        }

        // auto model
        if (_clock is { IsLoaded: true, IsVisible: true })
        {
            var read = ReadBounds(_clock);
            Data.Settings.Clock.Left = read.Left;
            Data.Settings.Clock.Top = read.Top;
            Data.Settings.Clock.Width = read.Width;
            Data.Settings.Clock.Height = read.Height;
        }

        // auto model
        if (_usage is { IsLoaded: true, IsVisible: true })
        {
            var read = ReadBounds(_usage);
            Data.Settings.Usage.Left = read.Left;
            Data.Settings.Usage.Top = read.Top;
            Data.Settings.Usage.Width = read.Width;
            Data.Settings.Usage.Height = read.Height;
        }

        foreach (var category in Data.Categories.Where(c => c.PoppedOut))
        {
            if (!_categoryWindows.TryGetValue(category.Id, out var window) || !window.IsLoaded)
                continue;
            var read = ReadBounds(window);
            CopyBounds(window, category.Bounds);
            if (layout == null || category.PairOpened)
                continue;
            var desk = DesktopFolder(category.Id)?.Bounds;
            var expected = layout.Folders.FirstOrDefault(f => f.Id == category.Id)?.Bounds ?? desk;
            if (expected == null || !WindowBounds.CloseTo(read, expected))
                FolderOverride(layout, category.Id).Bounds = read.Clone();
        }
    }

    static WindowBounds ReadBounds(Window window) => new()
    {
        Left = window.Left,
        Top = window.Top,
        Width = window.Width,
        Height = window.Height
    };

    static void CopyBounds(Window window, WindowBounds bounds)
    {
        bounds.Left = window.Left;
        bounds.Top = window.Top;
        bounds.Width = window.Width;
        bounds.Height = window.Height;
    }

    static void RestoreBounds(Window window, WindowBounds bounds, double defaultTopOffset, Window? near = null, int cascade = 0)
    {
        var width = bounds.Width > 80 ? bounds.Width : 340;
        var height = bounds.Height > 80 ? bounds.Height : 300;
        window.Width = width;
        window.Height = height;

        if (bounds.Left is double left && bounds.Top is double top &&
            !double.IsNaN(left) && !double.IsNaN(top) &&
            IsOnAnyScreen(window, left, top, width, height))
        {
            window.Left = left;
            window.Top = top;
            return;
        }

        if (near != null && !double.IsNaN(near.Left) && !double.IsNaN(near.Top))
        {
            PlaceNear(window, near, cascade);
            return;
        }

        var work = SystemParameters.WorkArea;
        window.Left = Math.Max(work.Left, work.Right - width - 24);
        window.Top = Math.Min(work.Bottom - height - 24, work.Top + defaultTopOffset);
    }

    static void PlaceNear(Window window, Window near, int cascade)
    {
        var width = window.Width;
        var height = window.Height;
        const double gap = 12;
        var work = WorkAreaFromPoint(near, near.Left + near.Width / 2, near.Top + 8);
        var top = near.Top + cascade * 36;
        var right = near.Left + near.Width + gap;
        var leftOf = near.Left - width - gap;
        double left;
        if (right + width <= work.Right + 8)
            left = right;
        else if (leftOf >= work.Left - 8)
            left = leftOf;
        else
            left = Math.Max(work.Left, work.Right - width - gap);

        window.Left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        window.Top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
    }

    static Rect WorkAreaFromPoint(Window visual, double x, double y)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        var area = WinForms.Screen.FromPoint(new System.Drawing.Point(
            (int)Math.Round(x * scaleX), (int)Math.Round(y * scaleY))).WorkingArea;
        return new Rect(area.X / scaleX, area.Y / scaleY, area.Width / scaleX, area.Height / scaleY);
    }

    static bool IsOnAnyScreen(Window visual, double left, double top, double width, double height)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        var rect = new Rectangle(
            (int)Math.Round(left * scaleX),
            (int)Math.Round(top * scaleY),
            (int)Math.Round(Math.Max(width, 40) * scaleX),
            (int)Math.Round(Math.Max(height, 40) * scaleY));
        return WinForms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect));
    }

    static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    void CreateTray()
    {
        _trayIcon = IconFactory.CreateTrayIcon();
        _overlayItem = new WinForms.ToolStripMenuItem("Hide overlay")
        {
            CheckOnClick = false
        };
        _overlayItem.Click += (_, _) => RunOnUi(ToggleOverlay);
        _clickThroughItem = new WinForms.ToolStripMenuItem("Lock overlay")
        {
            CheckOnClick = false
        };
        _clickThroughItem.Click += (_, _) => RunOnUi(ToggleClickThrough);
        _autostartItem = new WinForms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = false
        };
        _autostartItem.Click += (_, _) => RunOnUi(() => SetAutostart(!Data.Settings.Autostart));

        _trayMenu = new WinForms.ContextMenuStrip
        {
            ShowCheckMargin = true,
            ShowImageMargin = false
        };
        _trayMenu.Items.Add(_overlayItem);
        _trayMenu.Items.Add(new WinForms.ToolStripSeparator());
        _trayMenu.Items.Add(_clickThroughItem);
        _trayMenu.Items.Add(_autostartItem);
        _trayMenu.Items.Add(new WinForms.ToolStripSeparator());
        _trayMenu.Items.Add("Configure...", null, (_, _) => RunOnUi(OpenConfig));
        _trayMenu.Items.Add("Exit", null, (_, _) => RunOnUi(Exit));
        _trayMenu.Opening += (_, _) => UpdateTrayChecks();
        _trayMenu.Closed += (_, _) => CloseTrayMenu();

        _tray = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = "AtFutureMe"
        };
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right)
                System.Windows.Application.Current.Dispatcher.BeginInvoke(ShowTrayMenu, DispatcherPriority.Background);
            else if (e.Button == WinForms.MouseButtons.Left)
                RunOnUi(ShowFromTray);
        };
        _tray.DoubleClick += (_, _) => RunOnUi(OpenConfig);
        UpdateTrayChecks();
    }

    void ShowTrayMenu()
    {
        if (_trayMenu == null)
            return;

        UpdateTrayChecks();

        var pos = WinForms.Cursor.Position;
        EnsureTrayActivator(pos);
        if (_trayActivator != null)
        {
            var hwnd = new WindowInteropHelper(_trayActivator).Handle;
            NativeMethods.SetForegroundWindow(hwnd);
        }

        _trayMenu.Show(pos);
    }

    void EnsureTrayActivator(System.Drawing.Point pos)
    {
        if (_trayActivator == null)
        {
            _trayActivator = new Window
            {
                Width = 0,
                Height = 0,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = true,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize
            };
        }

        _trayActivator.Left = pos.X;
        _trayActivator.Top = pos.Y;
        _trayActivator.Show();
    }

    void CloseTrayMenu()
    {
        _trayActivator?.Hide();
    }

    void ShowFromTray()
    {
        if (LayoutSceneActive || !OverlayHidden)
            return;
        ShowOverlays();
    }

    void ToggleOverlay()
    {
        if (LayoutSceneActive)
            return;
        if (OverlayHidden)
            ShowOverlays();
        else
            MinimizeToTray();
    }

    bool OverlayHidden =>
        _hiddenAll || _hiddenWindows.Count > 0
        // auto model
        || (Data.Settings.TempHideMain && TempHideWouldHold(DateTime.Now));

    void UpdateTrayChecks()
    {
        var scene = LayoutSceneActive;
        if (_overlayItem != null)
        {
            _overlayItem.Enabled = !scene;
            _overlayItem.Text = OverlayHidden ? "Show overlay" : "Hide overlay";
        }
        if (_clickThroughItem != null)
        {
            _clickThroughItem.Enabled = !scene;
            _clickThroughItem.Checked = Data.Settings.ClickThrough;
        }
        if (_autostartItem != null)
            _autostartItem.Checked = Data.Settings.Autostart;
    }

    void NormalizeSettings()
    {
        Data.Settings.Opacity = Math.Clamp(Data.Settings.Opacity <= 0 ? 0.78 : Data.Settings.Opacity, 0.15, 1);
        Data.Settings.TextBackdropOpacity = Math.Clamp(
            Data.Settings.TextBackdropOpacity <= 0 ? 0.82 : Data.Settings.TextBackdropOpacity, 0.15, 1);
        Data.Settings.FontSize = Math.Clamp(Data.Settings.FontSize <= 0 ? 14 : Data.Settings.FontSize, 10, 28);
        Data.Settings.TextOutlineStrength = Math.Clamp(
            Data.Settings.TextOutlineStrength <= 0 ? 4 : Data.Settings.TextOutlineStrength, 1, 12);
        Data.Settings.PanelDarkness = Math.Clamp(Data.Settings.PanelDarkness, 0, 1);
        Data.Settings.PanelDither ??= true;
        Data.Settings.PanelDitherStrength = Math.Clamp(Data.Settings.PanelDitherStrength, 0, 1);
        Data.Settings.EventHorizonDays = Math.Clamp(Data.Settings.EventHorizonDays, 0, 720);
        Data.Settings.RecurringHorizon = Math.Clamp(Data.Settings.RecurringHorizon, 0, 720);
        if (Data.Settings.TempHideHours <= 0)
            Data.Settings.TempHideHours = 2;
        Data.Settings.TempHideHours = Math.Clamp(Data.Settings.TempHideHours, 1.0 / 3600, 72);
        if (Data.Settings.TempHideMaxHours <= 0)
            Data.Settings.TempHideMaxHours = 2;
        Data.Settings.TempHideMaxHours = Math.Clamp(Data.Settings.TempHideMaxHours, 1.0 / 3600, 72);
        if (Data.Settings.TempHideWarnSeconds <= 0)
            Data.Settings.TempHideWarnSeconds = 60;
        Data.Settings.TempHideWarnSeconds = Math.Clamp(Data.Settings.TempHideWarnSeconds, 1, 72 * 3600);
        if (!Enum.IsDefined(Data.Settings.TempHideWarnCorner))
            Data.Settings.TempHideWarnCorner = OverlayCorner.BottomRight;
        Data.Settings.TempHideMain = false;
        Data.Settings.TempHideUntil = null;
        if (Data.Settings.Desktop != null)
        {
            Data.Settings.Desktop.HiddenAll = false;
            Data.Settings.Desktop.UserHidMain = false;
        }
        Data.Settings.FolderIndent = Math.Clamp(Data.Settings.FolderIndent, 0, 40);
        // auto model
        Data.Settings.TopFolderIndent = Math.Clamp(Data.Settings.TopFolderIndent, 0, 40);
        if (Data.Settings.FolderContentIndent < 0)
            Data.Settings.FolderContentIndent = 12;
        Data.Settings.FolderContentIndent = Math.Clamp(Data.Settings.FolderContentIndent, 0, 40);
        // auto model
        if (Data.Settings.ClockHorizonHours <= 0)
            Data.Settings.ClockHorizonHours = 24;
        Data.Settings.ClockHorizonHours = Math.Clamp(Data.Settings.ClockHorizonHours, 1, 14 * 24);
        if (!Enum.IsDefined(Data.Settings.ClockTickStep))
            Data.Settings.ClockTickStep = ClockTickStep.Hour;
        Data.Settings.ClockTickLengthHour = SnapClockPercent(Data.Settings.ClockTickLengthHour, 0.01, 0.50);
        Data.Settings.ClockTickLengthThirty = SnapClockPercent(Data.Settings.ClockTickLengthThirty, 0.01, 0.50);
        Data.Settings.ClockTickLengthFifteen = SnapClockPercent(Data.Settings.ClockTickLengthFifteen, 0.01, 0.50);
        Data.Settings.ClockTickLengthMinute = SnapClockPercent(Data.Settings.ClockTickLengthMinute, 0.01, 0.50);
        Data.Settings.ClockSecondHandLength = SnapClockPercent(Data.Settings.ClockSecondHandLength, 0.01, 0.95);
        Data.Settings.ClockHourHandLength = SnapClockPercent(Data.Settings.ClockHourHandLength, 0.01, 0.95);
        Data.Settings.ClockWedgeWidth = SnapClockPercent(Data.Settings.ClockWedgeWidth, 0.01, 0.15);
        Data.Settings.ClockTimePosition = Math.Clamp(Data.Settings.ClockTimePosition, 0, 1);
        // auto model
        Data.Settings.ClockTimeFontSize = Math.Clamp(
            Data.Settings.ClockTimeFontSize <= 0 ? 13 : Data.Settings.ClockTimeFontSize, 8, 48);
        if (!Enum.IsDefined(Data.Settings.ClockLockMode))
            Data.Settings.ClockLockMode = ClockLockMode.Locked;
        Data.Settings.ClockColorRecurring = ColorText.NormalizeOr(Data.Settings.ClockColorRecurring, "#8AB4F8");
        Data.Settings.ClockColorScheduled = ColorText.NormalizeOr(Data.Settings.ClockColorScheduled, "#FFD24D");
        Data.Settings.ClockColorDue = ColorText.NormalizeOr(Data.Settings.ClockColorDue, ColorText.Due);
        Data.Settings.ClockColorCaret = ColorText.NormalizeOr(Data.Settings.ClockColorCaret, "#8AB4F8");
        Data.Settings.ClockColorRing = ColorText.NormalizeOr(Data.Settings.ClockColorRing, "#C5C9D0");
        Data.Settings.ClockColorMajorTick = ColorText.NormalizeOr(Data.Settings.ClockColorMajorTick, "#C5C9D0");
        Data.Settings.ClockColorMinorTick = ColorText.NormalizeOr(Data.Settings.ClockColorMinorTick, "#9AA0A8");
        Data.Settings.ClockColorSecondHand = ColorText.NormalizeOr(Data.Settings.ClockColorSecondHand, "#FF8A65");
        Data.Settings.ClockColorHourHand = ColorText.NormalizeOr(Data.Settings.ClockColorHourHand, "#E8EAED");
        Data.Settings.ClockColorWedgeLine = ColorText.NormalizeOr(Data.Settings.ClockColorWedgeLine, "#FFFFFF");
        Data.Settings.ClockColorTime = ColorText.NormalizeOr(Data.Settings.ClockColorTime, "#E8EAED");
        Data.Settings.ClockColorTimeOutline = ColorText.NormalizeOr(Data.Settings.ClockColorTimeOutline, "#000000");
        Data.Settings.ClockTimeOutlineStrength = Math.Clamp(
            Data.Settings.ClockTimeOutlineStrength <= 0 ? 2 : Data.Settings.ClockTimeOutlineStrength, 1, 6);
        // auto model
        if (!Enum.IsDefined(Data.Settings.UsageLockMode))
            Data.Settings.UsageLockMode = UsageLockMode.Locked;
        Data.Settings.UsagePollMinutes = Math.Clamp(
            Data.Settings.UsagePollMinutes <= 0 ? 15 : Data.Settings.UsagePollMinutes, 5, 120);
        Data.Settings.UsageFullSessionWeeklyPercent = Math.Clamp(
            Data.Settings.UsageFullSessionWeeklyPercent <= 0 ? 10 : Data.Settings.UsageFullSessionWeeklyPercent, 1, 100);
        Data.Settings.UsageReminderCushion = Math.Clamp(
            Data.Settings.UsageReminderCushion <= 0 ? 2 : Data.Settings.UsageReminderCushion, 1, 10);
        Data.Settings.UsageAtLimitLeadHours5h = ClampUsageLead(
            Data.Settings.UsageAtLimitLeadHours5h, 1);
        Data.Settings.UsageAtLimitLeadHoursWeekly = ClampUsageLead(
            Data.Settings.UsageAtLimitLeadHoursWeekly, 12);
        Data.Settings.UsageAtLimitLeadHoursOpus = ClampUsageLead(
            Data.Settings.UsageAtLimitLeadHoursOpus, 12);
        Data.Settings.UsageAtLimitLeadHoursCursor = ClampUsageLead(
            Data.Settings.UsageAtLimitLeadHoursCursor, 24);
        Data.Settings.UsageClockAtLimitLeadHours5h = ClampUsageLead(
            Data.Settings.UsageClockAtLimitLeadHours5h, 5);
        Data.Settings.UsageClockAtLimitLeadHoursWeekly = ClampUsageLead(
            Data.Settings.UsageClockAtLimitLeadHoursWeekly, 48);
        Data.Settings.UsageClockAtLimitLeadHoursOpus = ClampUsageLead(
            Data.Settings.UsageClockAtLimitLeadHoursOpus, 48);
        Data.Settings.UsageClockAtLimitLeadHoursCursor = ClampUsageLead(
            Data.Settings.UsageClockAtLimitLeadHoursCursor, 72);
        Data.Settings.UsageColorClaude = ColorText.NormalizeOr(Data.Settings.UsageColorClaude, "#D4A574");
        Data.Settings.UsageColorCursor = ColorText.NormalizeOr(Data.Settings.UsageColorCursor, "#8AB4F8");
        if (!Enum.IsDefined(Data.Settings.UsageBoardColorMode5h))
            Data.Settings.UsageBoardColorMode5h = UsageBoardColorMode.UsageTint;
        if (!Enum.IsDefined(Data.Settings.UsageBoardColorModeWeekly))
            Data.Settings.UsageBoardColorModeWeekly = UsageBoardColorMode.UsageTint;
        if (!Enum.IsDefined(Data.Settings.UsageBoardColorModeOpus))
            Data.Settings.UsageBoardColorModeOpus = UsageBoardColorMode.UsageTint;
        if (!Enum.IsDefined(Data.Settings.UsageBoardColorModeCursor))
            Data.Settings.UsageBoardColorModeCursor = UsageBoardColorMode.UsageTint;
        Data.Settings.UsageBoardColor5h = ColorText.NormalizeOr(Data.Settings.UsageBoardColor5h, "#D4A574");
        Data.Settings.UsageBoardColorWeekly = ColorText.NormalizeOr(Data.Settings.UsageBoardColorWeekly, "#D4A574");
        Data.Settings.UsageBoardColorOpus = ColorText.NormalizeOr(Data.Settings.UsageBoardColorOpus, "#D4A574");
        Data.Settings.UsageBoardColorCursor = ColorText.NormalizeOr(Data.Settings.UsageBoardColorCursor, "#8AB4F8");
        if (Data.Settings.Usage.Width < 160)
            Data.Settings.Usage.Width = 280;
        if (Data.Settings.Usage.Height < 140)
            Data.Settings.Usage.Height = 320;
        // Face fill: blank follows overlay panel. Migrate the brief default that changed backgrounds.
        if (string.IsNullOrWhiteSpace(Data.Settings.ClockColorFace)
            || (ColorText.TryNormalize(Data.Settings.ClockColorFace, out var face)
                && face.Equals("#202228", StringComparison.OrdinalIgnoreCase)))
            Data.Settings.ClockColorFace = "";
        else if (!ColorText.TryNormalize(Data.Settings.ClockColorFace, out _))
            Data.Settings.ClockColorFace = "";
        Data.Settings.ClockCategoryIds ??= [];
        if (Data.Settings.Clock.Width < 160)
            Data.Settings.Clock.Width = 280;
        if (Data.Settings.Clock.Height < 180)
            Data.Settings.Clock.Height = 300;
        FillTaskColors(Data.Settings);
        if (Data.Settings.HideCompleted)
        {
            Data.Settings.HideCompletedOnce = true;
            Data.Settings.HideCompletedRecurring = true;
            Data.Settings.HideCompletedScheduled = true;
            Data.Settings.HideCompleted = false;
        }
        Data.Settings.Reminders ??= new WindowBounds();
        Data.Settings.Clock ??= new WindowBounds { Width = 280, Height = 300 };
        Data.Settings.IgnoredPairTargets ??= [];
        Data.Settings.TaskOrder ??= [];
        Data.AppLayouts ??= [];
        Data.Categories ??= [];
        Data.Once ??= [];
        Data.Todos ??= [];
        Data.Events ??= [];
        Data.Notes ??= [];
        foreach (var folder in Data.Categories)
            NormalizeCheckRule(folder);
        foreach (var item in Data.Once)
            NormalizeCheckRule(item);
        foreach (var item in Data.Todos)
        {
            NormalizeCheckRule(item);
            // auto model
            if (item.ImportantLeadHours <= 0)
                item.ImportantLeadHours = 1;
            item.ImportantLeadHours = Math.Clamp(item.ImportantLeadHours, 1.0 / 60, 720 * 24);
            if (item.Recurrence.Interval <= 0)
                item.Recurrence.Interval = 1;
        }
        foreach (var item in Data.Events)
        {
            NormalizeCheckRule(item);
            item.ShowBeforeHours = RecurrenceEngine.ClampShowHours(item.ShowBeforeHours, true);
            item.ShowAfterCheckHours = RecurrenceEngine.ClampShowHours(item.ShowAfterCheckHours, true);
            // auto model
            if (item.ImportantLeadHours <= 0)
                item.ImportantLeadHours = 1;
            item.ImportantLeadHours = Math.Clamp(item.ImportantLeadHours, 1.0 / 60, 720 * 24);
        }
        MigrateHideMainPairs();
        EnsureTaskOrder();
    }

    // auto model: store clock size knobs as whole percents (e.g. 0.16 not 0.075191…)
    static double SnapClockPercent(double value, double min, double max)
    {
        var lo = (int)Math.Round(min * 100);
        var hi = (int)Math.Round(max * 100);
        if (value <= 0)
            value = min;
        var pct = (int)Math.Round(value * 100);
        pct = Math.Clamp(pct, lo, hi);
        return pct / 100.0;
    }

    // auto model
    static double ClampUsageLead(double value, double fallback) =>
        Math.Clamp(value <= 0 ? fallback : value, 0.25, 14 * 24);

    static void NormalizeCheckRule(IHasCheckRule item)
    {
        if (!Enum.IsDefined(item.CheckRule))
            item.CheckRule = FolderCheckRule.Inherit;
        if (item.CheckReshowHours <= 0)
            item.CheckReshowHours = 24;
        item.CheckReshowHours = Math.Clamp(item.CheckReshowHours, 1.0 / 60, 72 * 24);
    }

    bool EnsureCategories()
    {
        var changed = false;
        CategoryTree.Sanitize(Data.Categories);
        CategoryTree.RefreshLabels(Data.Categories);

        foreach (var item in Data.Once)
        {
            var id = ResolveCategoryId(item.CategoryId);
            if (item.CategoryId != id)
            {
                item.CategoryId = id;
                changed = true;
            }
        }

        foreach (var todo in Data.Todos)
        {
            var id = ResolveCategoryId(todo.CategoryId);
            if (todo.CategoryId != id)
            {
                todo.CategoryId = id;
                changed = true;
            }
        }

        foreach (var ev in Data.Events)
        {
            var id = ResolveCategoryId(ev.CategoryId);
            if (ev.CategoryId != id)
            {
                ev.CategoryId = id;
                changed = true;
            }
        }

        foreach (var note in Data.Notes)
        {
            var id = ResolveCategoryId(note.CategoryId);
            if (note.CategoryId != id)
            {
                note.CategoryId = id;
                changed = true;
            }
        }

        return changed;
    }

    FontWeight OverlayWeight(string kind) =>
        Data.Settings.BoldText
            ? FontWeights.Bold
            : kind == "note" ? FontWeights.SemiBold : FontWeights.Normal;

    public FontWeight OverlayChromeWeight() =>
        Data.Settings.BoldText ? FontWeights.Bold : FontWeights.SemiBold;

    MediaBrush ItemColor(string kind, TaskColorState state, TaskColors? colors) =>
        Brush(TaskPalette.Resolve(Data.Settings, kind, state, colors));

    static void FillTaskColors(AppSettings settings)
    {
        var idle = ColorText.NormalizeOr(settings.UncompletedColor, ColorText.Idle);
        settings.UncompletedColor = idle;
        settings.ColorTodo = ColorText.TryNormalize(settings.ColorTodo, out var todo) ? todo : idle;
        settings.ColorRecurring = ColorText.TryNormalize(settings.ColorRecurring, out var recurring) ? recurring : idle;
        settings.ColorScheduled = ColorText.TryNormalize(settings.ColorScheduled, out var scheduled) ? scheduled : idle;
        settings.ColorLate1 = ColorText.NormalizeOr(settings.ColorLate1, ColorText.Late1);
        settings.ColorLate2 = ColorText.NormalizeOr(settings.ColorLate2, ColorText.Late2);
        settings.ColorLate3 = ColorText.NormalizeOr(settings.ColorLate3, ColorText.Late3);
        settings.ColorDue = ColorText.NormalizeOr(settings.ColorDue, ColorText.Due);
        settings.ColorCompleted = ColorText.NormalizeOr(settings.ColorCompleted, ColorText.Completed);
    }

    MediaBrush Brush(string hex)
    {
        if (_brushes.TryGetValue(hex, out var cached))
            return cached;
        var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushes[hex] = brush;
        return brush;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _tick.Stop();
        _saveBounds.Stop();
        _addWindow?.Close();
        _setWhen?.Close();
        _countdown?.Close();
        _countdown = null;
        _clock?.Close();
        _clock = null;
        _usage?.Close();
        _usage = null;
        Usage?.Dispose();
        Usage = null;
        foreach (var window in _categoryWindows.Values.ToList())
            window.Close();
        _categoryWindows.Clear();
        _trayMenu?.Dispose();
        _trayActivator?.Close();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
    }
}
