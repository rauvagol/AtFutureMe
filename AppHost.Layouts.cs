using AtFutureMe.Models;
using AtFutureMe.Services;

namespace AtFutureMe;

public sealed partial class AppHost
{
    readonly List<string> _layoutOrder = [];
    readonly HashSet<string> _layoutRunning = [];
    bool _layoutHideAll;

    public bool LayoutSceneActive => ActiveLayout() != null;

    public AppLayout? ActiveLayout()
    {
        for (var i = _layoutOrder.Count - 1; i >= 0; i--)
        {
            var layout = Data.AppLayouts.FirstOrDefault(l => l.Id == _layoutOrder[i]);
            if (layout != null)
                return layout;
        }

        return null;
    }

    public bool HasAppLayout(string? exePath) =>
        !string.IsNullOrWhiteSpace(exePath)
        && Data.AppLayouts.Any(l => ProcessWatch.PathsRelated(l.ExePath, exePath));

    public IEnumerable<AppLayout> Layouts() =>
        Data.AppLayouts.OrderBy(l => l.ExeName, StringComparer.OrdinalIgnoreCase);

    public AppLayout AddAppLayout(string exePath, string exeName)
    {
        var existing = Data.AppLayouts.FirstOrDefault(l => ProcessWatch.PathsRelated(l.ExePath, exePath));
        if (existing != null)
            return existing;
        var layout = new AppLayout
        {
            ExePath = exePath,
            ExeName = string.IsNullOrWhiteSpace(exeName) ? System.IO.Path.GetFileName(exePath) : exeName,
            Hide = AppLayoutHide.Unchanged,
            ClickThrough = DesktopLock()
        };
        Data.AppLayouts.Add(layout);
        return layout;
    }

    public AppLayout FlagAppLayout(string exePath, string exeName)
    {
        var layout = AddAppLayout(exePath, exeName);
        RememberLayoutBaseline(layout);
        RequestSave();
        return layout;
    }

    public void RemoveAppLayout(string id)
    {
        Data.AppLayouts.RemoveAll(l => l.Id == id);
        _layoutOrder.RemoveAll(x => x == id);
        _layoutRunning.Remove(id);
        ApplyScene();
        RequestSave();
    }

    public void UpdateAppLayout(AppLayout layout)
    {
        CaptureBounds();
        if (ReferenceEquals(layout, ActiveLayout()))
            ApplyScene();
        RequestSave();
    }

    void EnsureDesktop()
    {
        Data.AppLayouts ??= [];
        if (Data.Settings.Desktop == null)
            SnapshotDesktop();
    }

    void MigrateHideMainPairs()
    {
        foreach (var category in Data.Categories.Where(c =>
                     c.PairMinimizeMain && !string.IsNullOrWhiteSpace(c.PairedExePath)).ToList())
        {
            var layout = AddAppLayout(category.PairedExePath!, category.PairedExeName ?? "");
            layout.Hide = AppLayoutHide.Main;
            var folder = FolderOverride(layout, category.Id);
            folder.PoppedOut = true;
            category.PairMinimizeMain = false;
            category.PairedExePath = null;
            category.PairedExeName = null;
            category.PairOpened = false;
        }
    }

    void SnapshotDesktop()
    {
        Data.Settings.Desktop = new DesktopLayout
        {
            ClickThrough = Data.Settings.ClickThrough,
            HiddenAll = _hiddenAll,
            UserHidMain = _userHidMain,
            Reminders = Data.Settings.Reminders.Clone(),
            Folders = Data.Categories.Select(c => new DesktopFolderState
            {
                Id = c.Id,
                PoppedOut = c.PoppedOut && !c.PairOpened,
                Collapsed = c.Collapsed,
                Bounds = c.Bounds.Clone()
            }).ToList()
        };
    }

    WindowBounds DesktopReminders() =>
        Data.Settings.Desktop?.Reminders ?? Data.Settings.Reminders;

    bool DesktopLock() =>
        Data.Settings.Desktop?.ClickThrough ?? Data.Settings.ClickThrough;

    DesktopFolderState? DesktopFolder(string id) =>
        Data.Settings.Desktop?.Folders.FirstOrDefault(f => f.Id == id);

    void RestoreDesktopToLive()
    {
        var desk = Data.Settings.Desktop;
        if (desk == null)
        {
            SnapshotDesktop();
            desk = Data.Settings.Desktop!;
        }

        Data.Settings.ClickThrough = desk.ClickThrough;
        Data.Settings.Reminders = desk.Reminders.Clone();
        _hiddenAll = desk.HiddenAll;
        _userHidMain = desk.UserHidMain;
        _layoutHideAll = false;
        _hiddenWindows.Clear();
        if (_userHidMain)
            _hiddenWindows.Add(MainOverlayId);

        foreach (var category in Data.Categories)
        {
            var saved = desk.Folders.FirstOrDefault(f => f.Id == category.Id);
            if (saved == null)
            {
                category.PoppedOut = false;
                category.Collapsed = false;
                continue;
            }

            category.PoppedOut = saved.PoppedOut;
            category.Collapsed = saved.Collapsed;
            category.Bounds = saved.Bounds.Clone();
            category.PairOpened = false;
        }
    }

    static AppLayoutFolder FolderOverride(AppLayout layout, string id)
    {
        var folder = layout.Folders.FirstOrDefault(f => f.Id == id);
        if (folder != null)
            return folder;
        folder = new AppLayoutFolder { Id = id };
        layout.Folders.Add(folder);
        return folder;
    }

    void ApplyLayoutToLive(AppLayout layout)
    {
        if (layout.MainBounds != null)
            Data.Settings.Reminders = layout.MainBounds.Clone();
        Data.Settings.ClickThrough = layout.ClickThrough;
        foreach (var category in Data.Categories)
        {
            var over = layout.Folders.FirstOrDefault(f => f.Id == category.Id);
            if (over == null)
                continue;
            if (over.PoppedOut is bool popped)
                category.PoppedOut = popped;
            if (over.Collapsed is bool collapsed)
                category.Collapsed = collapsed;
            if (over.Bounds != null)
                category.Bounds = over.Bounds.Clone();
        }

        _layoutHideAll = layout.Hide == AppLayoutHide.All;
        if (layout.Hide == AppLayoutHide.Main)
            _hiddenWindows.Add(MainOverlayId);
        else if (layout.Hide == AppLayoutHide.Unchanged)
        {
            // desktop hide already restored
        }
        else if (layout.Hide == AppLayoutHide.All)
            _hiddenWindows.Clear();
    }

    void ApplyAdditivePops()
    {
        foreach (var category in Data.Categories.Where(IsUtilityPair))
        {
            if (!_pairedWasRunning.Contains(category.Id))
                continue;
            if (!category.PoppedOut)
            {
                category.PoppedOut = true;
                category.PairOpened = true;
            }

            _hiddenWindows.Remove(category.Id);
        }
    }

    bool IsUtilityPair(Category category) =>
        ProcessWatch.CategoryIsPaired(category) && !HasAppLayout(category.PairedExePath);

    void ApplyScene()
    {
        RestoreDesktopToLive();
        if (ActiveLayout() is AppLayout layout)
            ApplyLayoutToLive(layout);
        ApplyAdditivePops();
        ApplyWindowChrome();
        ApplyOverlayVisibility();
        ApplyLiveWindowPositions();
        UpdateTrayChecks();
    }

    void ApplyLiveWindowPositions()
    {
        if (_reminders is { IsLoaded: true })
            RestoreBounds(_reminders, Data.Settings.Reminders, 48);
        foreach (var category in Data.Categories.Where(c => c.PoppedOut))
        {
            if (_categoryWindows.TryGetValue(category.Id, out var window) && window.IsLoaded)
                RestoreBounds(window, category.Bounds, 80, _reminders);
        }
    }

    bool TickLayouts()
    {
        var layouts = Data.AppLayouts.ToList();
        if (layouts.Count == 0)
        {
            var leftover = _layoutRunning.Count > 0 || _layoutOrder.Count > 0;
            _layoutRunning.Clear();
            _layoutOrder.Clear();
            if (leftover)
            {
                ApplyScene();
                return true;
            }

            return false;
        }

        var snapshot = ProcessWatch.Capture();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        var wasActive = ActiveLayout()?.Id;
        foreach (var layout in layouts)
        {
            seen.Add(layout.Id);
            var running = ProcessWatch.IsRunning(snapshot, layout.ExePath);
            var was = _layoutRunning.Contains(layout.Id);
            if (running && !was)
            {
                if (_layoutRunning.Count == 0)
                    SnapshotDesktop();
                _layoutRunning.Add(layout.Id);
                _layoutOrder.Remove(layout.Id);
                _layoutOrder.Add(layout.Id);
                changed = true;
            }
            else if (!running && was)
            {
                _layoutRunning.Remove(layout.Id);
                _layoutOrder.Remove(layout.Id);
                changed = true;
            }
        }

        foreach (var stale in _layoutRunning.Where(id => !seen.Contains(id)).ToList())
        {
            _layoutRunning.Remove(stale);
            _layoutOrder.Remove(stale);
            changed = true;
        }

        if (changed && ActiveLayout()?.Id != wasActive)
            ApplyScene();
        return changed;
    }

    void SnapshotLayouts()
    {
        _layoutRunning.Clear();
        _layoutOrder.Clear();
        if (Data.AppLayouts.Count == 0)
            return;
        var snapshot = ProcessWatch.Capture();
        var any = false;
        foreach (var layout in Data.AppLayouts)
        {
            if (!ProcessWatch.IsRunning(snapshot, layout.ExePath))
                continue;
            if (!any)
            {
                SnapshotDesktop();
                any = true;
            }

            _layoutRunning.Add(layout.Id);
            _layoutOrder.Add(layout.Id);
        }

        if (any)
            ApplyScene();
    }

    void RememberLayoutBaseline(AppLayout layout)
    {
        if (ProcessWatch.IsRunning(layout.ExePath))
        {
            if (_layoutRunning.Count == 0)
                SnapshotDesktop();
            _layoutRunning.Add(layout.Id);
            _layoutOrder.Remove(layout.Id);
            _layoutOrder.Add(layout.Id);
            ApplyScene();
        }
    }

    void PersistDesktopFields()
    {
        if (Data.Settings.Desktop == null)
            SnapshotDesktop();
        var desk = Data.Settings.Desktop!;
        Data.Settings.ClickThrough = desk.ClickThrough;
        Data.Settings.Reminders = desk.Reminders.Clone();
        foreach (var category in Data.Categories)
        {
            var saved = desk.Folders.FirstOrDefault(f => f.Id == category.Id);
            if (saved == null)
            {
                category.PoppedOut = false;
                category.Collapsed = false;
                category.PairOpened = false;
                continue;
            }
            category.PoppedOut = saved.PoppedOut;
            category.Collapsed = saved.Collapsed;
            category.Bounds = saved.Bounds.Clone();
            category.PairOpened = false;
        }
    }

    OverlayLiveState RememberLiveOverlay()
    {
        return new OverlayLiveState(
            Data.Settings.ClickThrough,
            Data.Settings.Reminders.Clone(),
            _hiddenAll,
            _userHidMain,
            _layoutHideAll,
            [.. _hiddenWindows],
            Data.Categories.ToDictionary(c => c.Id, c => new FolderLiveState(
                c.PoppedOut, c.Collapsed, c.PairOpened, c.Bounds.Clone())));
    }

    void RestoreLiveOverlay(OverlayLiveState live)
    {
        Data.Settings.ClickThrough = live.ClickThrough;
        Data.Settings.Reminders = live.Reminders;
        _hiddenAll = live.HiddenAll;
        _userHidMain = live.UserHidMain;
        _layoutHideAll = live.LayoutHideAll;
        _hiddenWindows.Clear();
        foreach (var id in live.HiddenWindows)
            _hiddenWindows.Add(id);
        foreach (var category in Data.Categories)
        {
            if (!live.Folders.TryGetValue(category.Id, out var state))
                continue;
            category.PoppedOut = state.PoppedOut;
            category.Collapsed = state.Collapsed;
            category.PairOpened = state.PairOpened;
            category.Bounds = state.Bounds;
        }
    }

    record FolderLiveState(bool PoppedOut, bool Collapsed, bool PairOpened, WindowBounds Bounds);

    record OverlayLiveState(
        bool ClickThrough,
        WindowBounds Reminders,
        bool HiddenAll,
        bool UserHidMain,
        bool LayoutHideAll,
        List<string> HiddenWindows,
        Dictionary<string, FolderLiveState> Folders);
}
