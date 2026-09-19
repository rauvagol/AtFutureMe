using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AtFutureMe.Models;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

public partial class ConfigWindow : Window
{
    static readonly (RecurrenceKind Kind, string Label)[] Kinds =
    [
        (RecurrenceKind.Daily, "Daily"),
        (RecurrenceKind.EveryNDays, "Every N days"),
        (RecurrenceKind.Weekdays, "Weekdays (Mon–Fri)"),
        (RecurrenceKind.Weekly, "Weekly (chosen days)"),
        (RecurrenceKind.EveryNWeeks, "Every N weeks"),
        (RecurrenceKind.MonthlyDate, "Monthly on a date"),
        (RecurrenceKind.NthWeekday, "Nth weekday of the month"),
        (RecurrenceKind.Yearly, "Yearly"),
        (RecurrenceKind.AfterCompletion, "N days after I complete it"),
        (RecurrenceKind.AfterCompletionHours, "N hours after I complete it")
    ];

    static readonly string[] WeekdayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    static readonly Category TopLevelParent = new() { Id = "", Name = "(top level)", PathLabel = "(top level)" };
    static readonly Category NoFolder = new() { Id = "", Name = "(no folder)", PathLabel = "(no folder)" };

    sealed class ConfigTask
    {
        public string Kind { get; init; } = "";
        public string Id { get; init; } = "";
        public string Title { get; set; } = "";
        public string TypeLabel { get; set; } = "";
    }

    sealed class CompletedRow : INotifyPropertyChanged
    {
        string _title = "";
        DateTime _at;

        public string Kind { get; init; } = "";
        public string Id { get; init; } = "";
        public string TypeLabel { get; init; } = "";
        public string Folder { get; init; } = "";
        public string Title
        {
            get => _title;
            set { if (_title == value) return; _title = value; Notify(nameof(Title)); }
        }
        public DateTime CompletedAt
        {
            get => _at;
            set
            {
                if (_at == value)
                    return;
                _at = value;
                Notify(nameof(CompletedAt));
                Notify(nameof(WhenLabel));
            }
        }
        public string WhenLabel => CompletedAt.ToString("yyyy-MM-dd HH:mm");
        public string SubLabel => string.IsNullOrEmpty(Folder) ? TypeLabel : $"{TypeLabel} · {Folder}";
        public event PropertyChangedEventHandler? PropertyChanged;
        void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    sealed class LayoutNode : INotifyPropertyChanged
    {
        string _title = "";
        string _meta = "";
        bool _selected;

        public string Kind { get; init; } = "";
        public string Id { get; init; } = "";
        public string Title
        {
            get => _title;
            set { if (_title == value) return; _title = value; Notify(nameof(Title)); }
        }
        public string Meta
        {
            get => _meta;
            set { if (_meta == value) return; _meta = value; Notify(nameof(Meta)); }
        }
        public bool IsSelected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; Notify(nameof(IsSelected)); }
        }
        public bool IsFolder => Kind == "category";
        public int PinSlot { get; init; }
        public List<LayoutNode> Children { get; init; } = [];
        public event PropertyChangedEventHandler? PropertyChanged;
        void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    bool _loading;
    string? _layoutKind;
    string? _layoutId;
    string? _layoutAnchorKind;
    string? _layoutAnchorId;
    List<(string Kind, string Id)> _layoutSelection = [];

    public ConfigWindow()
    {
        _loading = true;
        InitializeComponent();
        foreach (var kind in Kinds)
            TodoKind.Items.Add(kind.Label);
        foreach (var name in WeekdayNames)
            TodoWeekday.Items.Add(name);
        TodoCheckUnit.Items.Add("Days");
        TodoCheckUnit.Items.Add("Weeks");
        EventCheckUnit.Items.Add("Days");
        EventCheckUnit.Items.Add("Weeks");
        TaskTypeBox.Items.Add("All");
        TaskTypeBox.Items.Add("To Do");
        TaskTypeBox.Items.Add("Recurring");
        TaskTypeBox.Items.Add("Scheduled");
        TaskTypeBox.Items.Add("Note");
        TaskTypeBox.SelectedIndex = 0;
        CompletedTypeBox.Items.Add("All");
        CompletedTypeBox.Items.Add("To Do");
        CompletedTypeBox.Items.Add("Recurring");
        CompletedTypeBox.Items.Add("Scheduled");
        CompletedTypeBox.SelectedIndex = 0;

        FillCheckRuleCombo(CategoryCheckRuleBox, false);
        FillCheckRuleCombo(OnceCheckRuleBox, true);
        FillCheckRuleCombo(TodoCheckRuleBox, true);
        FillCheckRuleCombo(EventCheckRuleBox, true);

        WireSettingColor(ColorTodoRow, (s, hex) => s.ColorTodo = hex);
        WireSettingColor(ColorRecurringRow, (s, hex) => s.ColorRecurring = hex);
        WireSettingColor(ColorScheduledRow, (s, hex) => s.ColorScheduled = hex);
        WireSettingColor(ColorDueRow, (s, hex) => s.ColorDue = hex);
        WireSettingColor(ColorLate1Row, (s, hex) => s.ColorLate1 = hex);
        WireSettingColor(ColorLate2Row, (s, hex) => s.ColorLate2 = hex);
        WireSettingColor(ColorLate3Row, (s, hex) => s.ColorLate3 = hex);
        WireSettingColor(ColorCompletedRow, (s, hex) => s.ColorCompleted = hex);
        WireClockColor(ClockColorRecurringRow, (s, hex) => s.ClockColorRecurring = hex);
        WireClockColor(ClockColorScheduledRow, (s, hex) => s.ClockColorScheduled = hex);
        WireClockColor(ClockColorDueRow, (s, hex) => s.ClockColorDue = hex);
        WireClockColor(ClockColorCaretRow, (s, hex) => s.ClockColorCaret = hex);
        WireClockColor(ClockColorRingRow, (s, hex) => s.ClockColorRing = hex);
        WireClockColor(ClockColorMajorTickRow, (s, hex) => s.ClockColorMajorTick = hex);
        WireClockColor(ClockColorMinorTickRow, (s, hex) => s.ClockColorMinorTick = hex);
        WireClockColor(ClockColorSecondHandRow, (s, hex) => s.ClockColorSecondHand = hex);
        WireClockColor(ClockColorHourHandRow, (s, hex) => s.ClockColorHourHand = hex);
        WireClockColor(ClockColorWedgeLineRow, (s, hex) => s.ClockColorWedgeLine = hex);
        WireClockColor(ClockColorTimeRow, (s, hex) => s.ClockColorTime = hex);
        WireClockColor(ClockColorTimeOutlineRow, (s, hex) => s.ClockColorTimeOutline = hex);
        WireClockColorOptional(ClockColorFaceRow, (s, hex) => s.ClockColorFace = hex ?? "");
        WireClockColor(ClockColorAttentionRow, (s, hex) => s.ClockColorAttention = hex);
        WireClockColor(ClockGearColorRow, (s, hex) => s.ClockGearColor = hex);
        WireClockColorOptional(ClockGearCompoundColorRow, (s, hex) => s.ClockGearCompoundColor = hex ?? "");
        WireClockColorOptional(ClockGearPlanetColorRow, (s, hex) => s.ClockGearPlanetColor = hex ?? "");
        WireClockColorOptional(ClockGearIdlerColorRow, (s, hex) => s.ClockGearIdlerColor = hex ?? "");
        WireClockColorOptional(ClockGearSunColorRow, (s, hex) => s.ClockGearSunColor = hex ?? "");
        WireClockColor(ClockGearCarriageColorRow, (s, hex) => s.ClockGearCarriageColor = hex);
        // auto model
        WireUsageColor(UsageColorClaudeRow, (s, hex) => s.UsageColorClaude = hex);
        WireUsageColor(UsageColorCursorRow, (s, hex) => s.UsageColorCursor = hex);
        WireUsageColor(UsageBoardColor5hRow, (s, hex) => s.UsageBoardColor5h = hex);
        WireUsageColor(UsageBoardColorWeeklyRow, (s, hex) => s.UsageBoardColorWeekly = hex);
        WireUsageColor(UsageBoardColorOpusRow, (s, hex) => s.UsageBoardColorOpus = hex);
        WireUsageColor(UsageBoardColorCursorRow, (s, hex) => s.UsageBoardColorCursor = hex);

        BindLists();
        LoadSettings();
        BindPairTargets();
        AppLayoutHideBox.Items.Add("Leave hide as-is");
        AppLayoutHideBox.Items.Add("Hide main overlay");
        AppLayoutHideBox.Items.Add("Hide all overlays");
        BindAppLayouts();
        AppHost.Instance.UiChanged += RefreshUpcomingScheduled;
        Closed += (_, _) =>
        {
            AppHost.Instance.UiChanged -= RefreshUpcomingScheduled;
            AppHost.Instance.RequestSave();
        };
        _loading = false;
    }

    public void FocusItem(string itemKind, string id)
    {
        _layoutKind = itemKind;
        _layoutId = id;
        MainTabs.SelectedIndex = 0;
        _layoutSelection = [(itemKind, id)];
        _layoutAnchorKind = itemKind;
        _layoutAnchorId = id;
        BindLayout();
        if (FindLayout(itemKind, id) is LayoutNode node)
            ApplyLayoutEditor(node);
    }

    void BindLists()
    {
        var data = AppHost.Instance.Data;
        var keep = TaskList.SelectedItem as ConfigTask;
        var catSel = (CategoryList.SelectedItem as Category)?.Id;
        var filter = TaskTypeBox.SelectedIndex;
        var rows = new List<ConfigTask>();
        if (filter is 0 or 1)
        {
            foreach (var t in data.Once)
                rows.Add(new ConfigTask { Kind = "once", Id = t.Id, Title = t.Title, TypeLabel = "To Do" });
        }
        if (filter is 0 or 2)
        {
            foreach (var t in data.Todos)
                rows.Add(new ConfigTask { Kind = "todo", Id = t.Id, Title = t.Title, TypeLabel = "Recurring" });
        }
        if (filter is 0 or 3)
        {
            foreach (var t in data.Events)
                rows.Add(new ConfigTask { Kind = "event", Id = t.Id, Title = t.Title, TypeLabel = "Scheduled" });
        }
        if (filter is 0 or 4)
        {
            foreach (var t in data.Notes)
                rows.Add(new ConfigTask
                {
                    Kind = "note",
                    Id = t.Id,
                    Title = t.Title,
                    TypeLabel = t.Hidden ? "Note (hidden)" : "Note"
                });
        }
        rows.Sort((a, b) => AppHost.Instance.CompareOverlayOrder(a.Kind, a.Id, a.Title, b.Kind, b.Id, b.Title));
        TaskList.ItemsSource = rows;
        CategoryTree.RefreshLabels(data.Categories);
        CategoryList.ItemsSource = CategoryTree.InOrder(data.Categories).ToList();
        FillCategoryCombo(OnceCategory);
        FillCategoryCombo(TodoCategory);
        FillCategoryCombo(EventCategory);
        FillCategoryCombo(NoteCategory);
        if (MainTabs.SelectedIndex == 0 && _layoutKind is "once" or "todo" or "event" or "note")
            SelectTask(_layoutKind, _layoutId ?? "");
        else if (keep != null)
            SelectTask(keep.Kind, keep.Id);
        else if (MainTabs.SelectedIndex != 0 && rows.Count > 0)
            TaskList.SelectedIndex = 0;
        else if (rows.Count == 0)
        {
            ShowTaskEditor(null);
            OnceEditor.IsEnabled = false;
            TodoEditor.IsEnabled = false;
            EventEditor.IsEnabled = false;
            NoteEditor.IsEnabled = false;
        }
        if (MainTabs.SelectedIndex == 0 && _layoutKind == "category" && _layoutId != null)
            CategoryList.SelectedItem = data.Categories.FirstOrDefault(c => c.Id == _layoutId);
        else if (catSel != null)
            CategoryList.SelectedItem = data.Categories.FirstOrDefault(c => c.Id == catSel);
        if (CategoryList.SelectedItem == null)
            SelectIndex(CategoryList, 0);
        BindLayout();
        BindCompleted();
    }

    void MainTabsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || e.Source != MainTabs || LayoutTree == null)
            return;
        AppHost.Instance.Data.Settings.ConfigTabIndex = MainTabs.SelectedIndex;
        AppHost.Instance.RequestSave();
        if (MainTabs.SelectedIndex == 0)
        {
            BindLayout();
            if (FindLayout(_layoutKind, _layoutId) is LayoutNode node)
                ApplyLayoutEditor(node);
            else
                ShowLayoutHint("Select a task or folder to edit it.");
        }
        else
        {
            ParkEditors();
            if (MainTabs.SelectedItem == CompletedTab)
                BindCompleted();
            else if (MainTabs.SelectedItem == IntegrationTab)
                BindPairTargets();
        }
    }

    void LayoutSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_loading || e.NewValue is not LayoutNode node)
            return;
        if (Keyboard.Modifiers is ModifierKeys.Shift or ModifierKeys.Control)
            return;
        SelectOnly(node);
        ApplySelectionUi();
    }

    void LayoutTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        if (IsInsideButton(e.OriginalSource as DependencyObject))
        {
            var clicked = NodeFromSource(e.OriginalSource as DependencyObject);
            if (clicked != null && !clicked.IsSelected)
            {
                SelectOnly(clicked);
                ApplySelectionUi();
            }
            return;
        }
        if (IsInsideExpander(e.OriginalSource as DependencyObject))
            return;
        var node = NodeFromSource(e.OriginalSource as DependencyObject);
        if (node == null)
            return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            SelectRange(node);
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ToggleSelect(node);
        else
            SelectOnly(node);
        e.Handled = true;
        ApplySelectionUi();
    }

    void LayoutTreeRightDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideExpander(e.OriginalSource as DependencyObject))
            return;
        var node = NodeFromSource(e.OriginalSource as DependencyObject);
        if (node == null)
            return;
        if (!node.IsSelected)
            SelectOnly(node);
        ApplySelectionUi();
    }

    static bool IsInsideButton(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is Button)
                return true;
            if (current is TreeViewItem)
                return false;
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    static bool IsInsideExpander(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is ToggleButton)
                return true;
            if (current is TreeViewItem)
                return false;
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    static LayoutNode? NodeFromSource(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is TreeViewItem item && item.DataContext is LayoutNode node)
                return node;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    void SelectOnly(LayoutNode node)
    {
        foreach (var item in WalkLayout())
            item.IsSelected = item.Kind == node.Kind && item.Id == node.Id;
        _layoutKind = node.Kind;
        _layoutId = node.Id;
        _layoutAnchorKind = node.Kind;
        _layoutAnchorId = node.Id;
        RememberLayoutSelection();
    }

    void ToggleSelect(LayoutNode node)
    {
        node.IsSelected = !node.IsSelected;
        _layoutKind = node.Kind;
        _layoutId = node.Id;
        _layoutAnchorKind = node.Kind;
        _layoutAnchorId = node.Id;
        RememberLayoutSelection();
    }

    void SelectRange(LayoutNode target)
    {
        if (_layoutAnchorKind == null || _layoutAnchorId == null)
        {
            SelectOnly(target);
            return;
        }

        var flat = WalkLayout().ToList();
        var anchor = FindLayout(_layoutAnchorKind, _layoutAnchorId) ?? target;
        var start = flat.FindIndex(n => n.Kind == anchor.Kind && n.Id == anchor.Id);
        var end = flat.FindIndex(n => n.Kind == target.Kind && n.Id == target.Id);
        if (start < 0)
            start = end;
        if (end < 0)
            return;
        var lo = Math.Min(start, end);
        var hi = Math.Max(start, end);
        for (var i = 0; i < flat.Count; i++)
            flat[i].IsSelected = i >= lo && i <= hi;
        _layoutKind = target.Kind;
        _layoutId = target.Id;
        RememberLayoutSelection();
    }

    void RememberLayoutSelection() =>
        _layoutSelection = WalkLayout().Where(n => n.IsSelected).Select(n => (n.Kind, n.Id)).ToList();

    List<LayoutNode> SelectedLayoutItems() =>
        WalkLayout().Where(n => n.IsSelected && n.Kind is "once" or "todo" or "event" or "note" or "category").ToList();

    void ApplySelectionUi()
    {
        var selected = SelectedLayoutItems();
        if (selected.Count > 1)
        {
            ParkEditors();
            ShowTaskEditor(null);
            ShowLayoutHint($"{selected.Count} items selected. Right-click and choose Move to send them to a folder.");
            return;
        }

        if (selected.Count == 1)
        {
            ApplyLayoutEditor(selected[0]);
            return;
        }

        if (FindLayout(_layoutKind, _layoutId) is LayoutNode node)
            ApplyLayoutEditor(node);
        else
        {
            ParkEditors();
            ShowTaskEditor(null);
            ShowLayoutHint("Select a task or folder to edit it.");
        }
    }

    void OpenMoveToFolder(object sender, RoutedEventArgs e)
    {
        var selected = SelectedLayoutItems();
        if (selected.Count == 0)
            return;
        var picker = new MoveToFolderWindow(StartFolderId(selected), id => CanMoveSelectionTo(selected, id))
        {
            Owner = this
        };
        if (picker.ShowDialog() == true)
            ApplyMoveToFolder(picker.ChosenId);
        else
            BindLists();
    }

    string StartFolderId(List<LayoutNode> selected)
    {
        var data = AppHost.Instance.Data;
        var parents = selected.Select(node =>
        {
            if (node.Kind == "category")
                return AppHost.Instance.ResolveCategoryId(
                    data.Categories.FirstOrDefault(c => c.Id == node.Id)?.ParentId);
            return AppHost.Instance.ResolveCategoryId(node.Kind switch
            {
                "once" => data.Once.FirstOrDefault(t => t.Id == node.Id)?.CategoryId,
                "todo" => data.Todos.FirstOrDefault(t => t.Id == node.Id)?.CategoryId,
                "event" => data.Events.FirstOrDefault(t => t.Id == node.Id)?.CategoryId,
                "note" => data.Notes.FirstOrDefault(t => t.Id == node.Id)?.CategoryId,
                _ => ""
            });
        }).Distinct().ToList();
        return parents.Count == 1 ? parents[0] : "";
    }

    static bool CanMoveSelectionTo(List<LayoutNode> selected, string targetId)
    {
        var all = AppHost.Instance.Data.Categories;
        var target = all.FirstOrDefault(c => c.Id == targetId);
        if (target == null)
            return true;
        foreach (var node in selected.Where(n => n.Kind == "category"))
        {
            var folder = all.FirstOrDefault(c => c.Id == node.Id);
            if (folder == null)
                continue;
            if (folder.Id == target.Id || CategoryTree.IsDescendant(all, folder, target))
                return false;
        }

        return true;
    }

    void ApplyMoveToFolder(string targetId)
    {
        var selected = SelectedLayoutItems();
        if (selected.Count == 0)
            return;
        var data = AppHost.Instance.Data;
        var target = data.Categories.FirstOrDefault(c => c.Id == targetId);
        foreach (var node in selected)
        {
            if (node.Kind is "once" or "todo" or "event" or "note")
            {
                switch (node.Kind)
                {
                    case "once":
                        var once = data.Once.FirstOrDefault(t => t.Id == node.Id);
                        if (once != null)
                            once.CategoryId = AppHost.Instance.ResolveCategoryId(targetId);
                        break;
                    case "todo":
                        var todo = data.Todos.FirstOrDefault(t => t.Id == node.Id);
                        if (todo != null)
                            todo.CategoryId = AppHost.Instance.ResolveCategoryId(targetId);
                        break;
                    case "event":
                        var ev = data.Events.FirstOrDefault(t => t.Id == node.Id);
                        if (ev != null)
                            ev.CategoryId = AppHost.Instance.ResolveCategoryId(targetId);
                        break;
                    case "note":
                        var note = data.Notes.FirstOrDefault(t => t.Id == node.Id);
                        if (note != null)
                            note.CategoryId = AppHost.Instance.ResolveCategoryId(targetId);
                        break;
                }
                continue;
            }

            if (node.Kind != "category")
                continue;
            var folder = data.Categories.FirstOrDefault(c => c.Id == node.Id);
            if (folder == null)
                continue;
            if (target == null || targetId.Length == 0)
                CategoryTree.PlaceRoot(data.Categories, folder);
            else if (CanMoveSelectionTo([node], targetId))
                CategoryTree.PlaceInside(data.Categories, folder, target);
        }

        _layoutSelection = selected.Select(n => (n.Kind, n.Id)).ToList();
        _layoutKind = selected[0].Kind;
        _layoutId = selected[0].Id;
        BindLists();
        ApplySelectionUi();
        FlushOverlay();
    }

    void ApplyLayoutEditor(LayoutNode node)
    {
        if (LayoutHint == null || LayoutEditorHost == null)
            return;
        if (node.Kind is "once" or "todo" or "event" or "note")
        {
            ShowLayoutHint(null);
            DockTaskEditors();
            _loading = true;
            if (TaskTypeBox.SelectedIndex != 0)
                TaskTypeBox.SelectedIndex = 0;
            _loading = false;
            if (TaskList.ItemsSource is not IEnumerable<ConfigTask> rows
                || rows.All(r => r.Kind != node.Kind || r.Id != node.Id))
            {
                _loading = true;
                BindLists();
                _loading = false;
            }
            SelectTask(node.Kind, node.Id);
            return;
        }

        if (node.Kind == "category")
        {
            ShowLayoutHint(null);
            DockCategoryEditor();
            var category = AppHost.Instance.Data.Categories.FirstOrDefault(c => c.Id == node.Id);
            if (category != null)
            {
                CategoryList.SelectedItem = category;
                LoadCategoryEditor();
            }
            return;
        }

        ParkEditors();
        ShowTaskEditor(null);
        ShowLayoutHint(node.Kind == "root"
            ? "Unfiled is not a folder — select a task to edit it."
            : "Select a task or folder to edit it.");
    }

    void ShowLayoutHint(string? text)
    {
        if (LayoutHint == null)
            return;
        LayoutHint.Text = text ?? "";
        LayoutHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    void DockTaskEditors()
    {
        ParkCategoryEditor();
        MoveTo(SharedTaskEditors, LayoutEditorHost);
    }

    void DockCategoryEditor()
    {
        ParkTaskEditors();
        MoveTo(CategoryEditor, LayoutEditorHost);
    }

    void ParkEditors()
    {
        ParkTaskEditors();
        ParkCategoryEditor();
    }

    void ParkTaskEditors() => MoveTo(SharedTaskEditors, TaskEditorHost);

    void ParkCategoryEditor() => MoveTo(CategoryEditor, CategoryEditorHost);

    static void MoveTo(FrameworkElement child, Panel? host)
    {
        if (host == null || ReferenceEquals(child.Parent, host))
            return;
        if (child.Parent is Panel current)
            current.Children.Remove(child);
        host.Children.Add(child);
    }

    void BindLayout()
    {
        if (LayoutTree == null)
            return;
        var keep = _layoutSelection.Count > 0
            ? _layoutSelection.ToList()
            : WalkLayout().Where(n => n.IsSelected).Select(n => (n.Kind, n.Id)).ToList();
        var roots = MixedChildren(null);

        var wasLoading = _loading;
        _loading = true;
        LayoutTree.ItemsSource = roots;
        if (keep.Count > 0)
            RestoreLayoutSelection(keep);
        else
            SelectLayout(_layoutKind, _layoutId);
        _loading = wasLoading;
    }

    void RestoreLayoutSelection(IReadOnlyList<(string Kind, string Id)> items)
    {
        foreach (var node in WalkLayout())
            node.IsSelected = items.Any(i => i.Kind == node.Kind && i.Id == node.Id);
        RememberLayoutSelection();
    }

    void SelectLayout(string? kind, string? id)
    {
        if (kind == null || id == null)
            return;
        foreach (var node in WalkLayout())
            node.IsSelected = node.Kind == kind && node.Id == id;
    }

    static void FillCheckRuleCombo(ComboBox box, bool forTask)
    {
        box.Items.Add(forTask ? "Use folder / Settings" : "Use parent / Settings");
        box.Items.Add("Keep visible when checked");
        box.Items.Add("Hide until reset");
        box.Items.Add("Hide until close to reset");
        box.Items.Add("Delete when checked");
    }

    static string? CheckRuleLabel(FolderCheckRule rule) =>
        rule switch
        {
            FolderCheckRule.KeepVisible => "keep on check",
            FolderCheckRule.HideUntilReset => "hide on check",
            FolderCheckRule.HideUntilLead => "reshow before reset",
            FolderCheckRule.DeleteOnCheck => "delete on check",
            _ => null
        };

    static string WithCheckRule(string meta, FolderCheckRule rule)
    {
        var extra = CheckRuleLabel(rule);
        return extra == null ? meta : $"{meta} · {extra}";
    }

    static IEnumerable<string> FolderRuleMeta(Category category)
    {
        if (category.Delayable)
            yield return "delayable";
        var rule = CheckRuleLabel(category.CheckRule);
        if (rule != null)
            yield return rule;
    }

    LayoutNode? FindLayout(string? kind, string? id) =>
        kind == null || id == null ? null : WalkLayout().FirstOrDefault(n => n.Kind == kind && n.Id == id);

    IEnumerable<LayoutNode> WalkLayout() =>
        LayoutTree.ItemsSource is IEnumerable<LayoutNode> roots ? WalkLayout(roots) : [];

    static IEnumerable<LayoutNode> WalkLayout(IEnumerable<LayoutNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in WalkLayout(node.Children))
                yield return child;
        }
    }

    static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.RegisterAttached(
        "ScrollOffset",
        typeof(double),
        typeof(ConfigWindow),
        new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is ScrollViewer scroll)
                scroll.ScrollToVerticalOffset((double)e.NewValue);
        }));

    void CenterLayoutOn(string? kind, string? id, double fromOffset, int tries = 0)
    {
        if (kind == null || id == null || LayoutTree == null)
            return;
        LayoutTree.Dispatcher.BeginInvoke(() =>
        {
            if (FindLayout(kind, id) is not LayoutNode node
                || FindTreeItem(LayoutTree, node) is not TreeViewItem item
                || FindVisualChild<ScrollViewer>(LayoutTree) is not ScrollViewer scroll)
            {
                if (tries < 4)
                    CenterLayoutOn(kind, id, fromOffset, tries + 1);
                return;
            }

            item.ApplyTemplate();
            item.UpdateLayout();
            scroll.BeginAnimation(ScrollOffsetProperty, null);
            scroll.ScrollToVerticalOffset(fromOffset);
            scroll.UpdateLayout();
            var header = item.Template.FindName("bd", item) as FrameworkElement ?? item;
            if (header.ActualHeight <= 0)
            {
                if (tries < 4)
                    CenterLayoutOn(kind, id, fromOffset, tries + 1);
                return;
            }

            var mid = header.TranslatePoint(new Point(0, header.ActualHeight / 2), scroll).Y;
            var target = scroll.VerticalOffset + mid - scroll.ViewportHeight / 2;
            var max = Math.Max(0, scroll.ExtentHeight - scroll.ViewportHeight);
            AnimateLayoutScroll(scroll, fromOffset, Math.Clamp(target, 0, max));
        }, DispatcherPriority.Loaded);
    }

    static void AnimateLayoutScroll(ScrollViewer scroll, double from, double to)
    {
        scroll.BeginAnimation(ScrollOffsetProperty, null);
        if (Math.Abs(to - from) < 0.5)
        {
            scroll.ScrollToVerticalOffset(to);
            return;
        }

        scroll.SetValue(ScrollOffsetProperty, from);
        scroll.ScrollToVerticalOffset(from);
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        anim.Completed += (_, _) =>
        {
            scroll.BeginAnimation(ScrollOffsetProperty, null);
            scroll.ScrollToVerticalOffset(to);
        };
        scroll.BeginAnimation(ScrollOffsetProperty, anim);
    }

    static TreeViewItem? FindTreeItem(ItemsControl parent, LayoutNode node)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem tvi)
                continue;
            if (item is LayoutNode match && match.Kind == node.Kind && match.Id == node.Id)
                return tvi;
            var nested = FindTreeItem(tvi, node);
            if (nested != null)
                return nested;
        }

        return null;
    }

    static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
                return hit;
            var nested = FindVisualChild<T>(child);
            if (nested != null)
                return nested;
        }

        return null;
    }

    void SyncLayoutNode(string kind, string id, string title, string meta)
    {
        if (FindLayout(kind, id) is not LayoutNode node)
            return;
        node.Title = title;
        node.Meta = meta;
    }

    LayoutNode FolderNode(Category category)
    {
        var extra = new List<string>();
        if (category.PoppedOut)
            extra.Add("popped out");
        if (category.Collapsed)
            extra.Add("collapsed");
        extra.AddRange(FolderRuleMeta(category));
        return new LayoutNode
        {
            Kind = "category",
            Id = category.Id,
            Title = category.Name,
            // auto model
            Meta = WithPin(string.Join(" · ", extra), category.PinSlot),
            PinSlot = category.PinSlot,
            Children = MixedChildren(category.Id)
        };
    }

    // auto model: always mix tasks + folders; Manual sort uses TaskOrder, else auto + pins
    List<LayoutNode> MixedChildren(string? parentId)
    {
        var folderId = parentId ?? "";
        var tasks = ItemsIn(folderId);
        var folders = CategoryTree.ChildrenOf(AppHost.Instance.Data.Categories, parentId)
            .Select(FolderNode)
            .ToList();
        var all = tasks.Concat(folders).ToList();
        all.Sort((a, b) => AppHost.Instance.CompareOverlayOrder(a.Kind, a.Id, a.Title, b.Kind, b.Id, b.Title));
        AppHost.ApplyPinnedOrder(all, n => n.PinSlot, n => n.Title);
        return all;
    }

    List<LayoutNode> ItemsIn(string categoryId)
    {
        var data = AppHost.Instance.Data;
        var rows = new List<LayoutNode>();
        foreach (var item in data.Once.Where(t => AppHost.Instance.ResolveCategoryId(t.CategoryId) == categoryId))
        {
            rows.Add(new LayoutNode
            {
                Kind = "once",
                Id = item.Id,
                Title = Blank(item.Title, "New to-do"),
                Meta = WithCheckRule(WithPin(item.CompletedAt != null ? "To Do · done" : "To Do", item.PinSlot), item.CheckRule),
                PinSlot = item.PinSlot
            });
        }

        foreach (var item in data.Todos.Where(t => AppHost.Instance.ResolveCategoryId(t.CategoryId) == categoryId))
        {
            rows.Add(new LayoutNode
            {
                Kind = "todo",
                Id = item.Id,
                Title = Blank(item.Title, "New recurring"),
                Meta = WithCheckRule(WithPin(item.Enabled ? "Recurring" : "Recurring · off", item.PinSlot), item.CheckRule),
                PinSlot = item.PinSlot
            });
        }

        foreach (var item in data.Events.Where(t => AppHost.Instance.ResolveCategoryId(t.CategoryId) == categoryId))
        {
            rows.Add(new LayoutNode
            {
                Kind = "event",
                Id = item.Id,
                Title = Blank(item.Title, "New scheduled"),
                Meta = WithCheckRule(WithPin(item.CompletedAt != null ? "Scheduled · done" : "Scheduled", item.PinSlot), item.CheckRule),
                PinSlot = item.PinSlot
            });
        }

        foreach (var item in data.Notes.Where(t => AppHost.Instance.ResolveCategoryId(t.CategoryId) == categoryId))
        {
            rows.Add(new LayoutNode
            {
                Kind = "note",
                Id = item.Id,
                Title = Blank(item.Title, string.IsNullOrWhiteSpace(item.Body) ? "Note" : item.Body),
                Meta = WithPin(item.Hidden ? "Note · hidden" : "Note", item.PinSlot),
                PinSlot = item.PinSlot
            });
        }

        // auto model: ordering applied in MixedChildren with folders
        return rows;
    }

    static string WithPin(string meta, int pin) =>
        pin > 0 ? $"{meta} · pin {pin}" : meta;

    static int ReadPin(TextBox? box) => ParseInt(box?.Text ?? "0", 0, 0, 99);

    static string Blank(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    static string? BlankToNull(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    void SelectTask(string kind, string id)
    {
        if (TaskList.ItemsSource is not IEnumerable<ConfigTask> rows)
            return;
        var match = rows.FirstOrDefault(r => r.Kind == kind && r.Id == id);
        if (match == null)
            return;
        TaskList.SelectedItem = match;
        TaskList.ScrollIntoView(match);
    }

    ConfigTask? SelectedTask()
    {
        if (TaskList.SelectedItem is ConfigTask row)
            return row;
        if (_layoutKind is "once" or "todo" or "event" or "note" && _layoutId != null)
            return new ConfigTask { Kind = _layoutKind, Id = _layoutId };
        return null;
    }

    Category? SelectedCategory()
    {
        if (CategoryList.SelectedItem is Category row)
            return row;
        if (_layoutKind == "category" && _layoutId != null)
            return AppHost.Instance.Data.Categories.FirstOrDefault(c => c.Id == _layoutId);
        return null;
    }

    string CurrentFolderId()
    {
        if (_layoutKind == "category")
            return _layoutId ?? "";
        if (_layoutKind == "root")
            return "";
        return SelectedTask() switch
        {
            { Kind: "once" } => AppHost.Instance.ResolveCategoryId(SelectedOnce()?.CategoryId),
            { Kind: "todo" } => AppHost.Instance.ResolveCategoryId(SelectedTodo()?.CategoryId),
            { Kind: "event" } => AppHost.Instance.ResolveCategoryId(SelectedEvent()?.CategoryId),
            { Kind: "note" } => AppHost.Instance.ResolveCategoryId(SelectedNote()?.CategoryId),
            _ => ""
        };
    }

    void LayoutKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteLayout(sender, e);
            e.Handled = true;
        }
    }

    void DeleteLayout(object sender, RoutedEventArgs e)
    {
        var selected = SelectedLayoutItems();
        if (selected.Count == 0)
        {
            if (_layoutKind is "once" or "todo" or "event" or "note")
                DeleteTask(sender, e);
            else if (_layoutKind == "category")
                DeleteCategory(sender, e);
            return;
        }

        var data = AppHost.Instance.Data;
        foreach (var node in selected.Where(n => n.Kind is "once" or "todo" or "event" or "note"))
        {
            switch (node.Kind)
            {
                case "once":
                    data.Once.RemoveAll(t => t.Id == node.Id);
                    break;
                case "todo":
                    data.Todos.RemoveAll(t => t.Id == node.Id);
                    break;
                case "event":
                    data.Events.RemoveAll(t => t.Id == node.Id);
                    break;
                case "note":
                    data.Notes.RemoveAll(t => t.Id == node.Id);
                    break;
            }

            AppHost.Instance.UntrackTask(node.Kind, node.Id);
        }

        foreach (var node in selected.Where(n => n.Kind == "category").ToList())
        {
            var item = data.Categories.FirstOrDefault(c => c.Id == node.Id);
            if (item == null)
                continue;
            var parent = CategoryTree.Parent(data.Categories, item);
            var fallback = parent?.Id ?? "";
            foreach (var once in data.Once.Where(t => t.CategoryId == item.Id))
                once.CategoryId = fallback;
            foreach (var todo in data.Todos.Where(t => t.CategoryId == item.Id))
                todo.CategoryId = fallback;
            foreach (var ev in data.Events.Where(t => t.CategoryId == item.Id))
                ev.CategoryId = fallback;
            foreach (var note in data.Notes.Where(t => t.CategoryId == item.Id))
                note.CategoryId = fallback;
            foreach (var child in CategoryTree.ChildrenOf(data.Categories, item.Id).ToList())
                child.ParentId = item.ParentId;
            AppHost.Instance.ForgetPairing(item.Id);
            // auto model
            AppHost.Instance.UntrackTask("category", item.Id);
            data.Categories.Remove(item);
        }

        CategoryTree.Rebuild(data.Categories);
        _layoutSelection.Clear();
        _layoutKind = null;
        _layoutId = null;
        BindLists();
        ParkEditors();
        ShowTaskEditor(null);
        ShowLayoutHint("Select a task or folder to edit it.");
        FlushOverlay();
    }

    void MoveLayoutUp(object sender, RoutedEventArgs e) => MoveLayout(-1);

    void MoveLayoutDown(object sender, RoutedEventArgs e) => MoveLayout(1);

    void MoveLayout(int delta)
    {
        var kind = _layoutKind;
        var id = _layoutId;
        if (kind == null || id == null)
            return;
        var from = FindVisualChild<ScrollViewer>(LayoutTree)?.VerticalOffset ?? 0;
        // auto model — tasks and folders share one sibling list under Manual sort
        if (kind is "once" or "todo" or "event" or "note" or "category")
            MoveAmongSiblings(kind, id, delta);
        CenterLayoutOn(kind, id, from);
    }

    void MoveAmongSiblings(string kind, string id, int delta)
    {
        string? parentId = null;
        if (kind == "category")
        {
            var folder = AppHost.Instance.Data.Categories.FirstOrDefault(c => c.Id == id);
            if (folder == null)
                return;
            parentId = folder.ParentId;
        }
        else
        {
            var folderId = CurrentFolderId();
            parentId = folderId.Length == 0 ? null : folderId;
        }

        var siblings = MixedChildren(parentId);
        var index = siblings.FindIndex(n => n.Kind == kind && n.Id == id);
        var next = index + delta;
        if (index < 0 || next < 0 || next >= siblings.Count)
            return;
        var other = siblings[next];
        // auto model: Layout ↑↓ sets a pin when Manual sort is off (works for folders and tasks)
        if (!AppHost.Instance.Data.Settings.ManualSort)
        {
            AppHost.Instance.SetPinSlot(kind, id, next + 1);
            BindLists();
            SelectLayout(kind, id);
            if (kind != "category")
                SelectTask(kind, id);
            else if (AppHost.Instance.Data.Categories.FirstOrDefault(c => c.Id == id) is Category pinnedCat)
                CategoryList.SelectedItem = pinnedCat;
            FlushOverlay();
            AppHost.Instance.RequestSave();
            return;
        }

        AppHost.Instance.SwapTaskOrder(kind, id, other.Kind, other.Id);
        if (kind == "category" && other.Kind == "category")
            CategoryTree.MoveSibling(AppHost.Instance.Data.Categories,
                AppHost.Instance.Data.Categories.First(c => c.Id == id), delta);
        else if (kind == other.Kind && kind is "once" or "todo" or "event" or "note")
        {
            var data = AppHost.Instance.Data;
            switch (kind)
            {
                case "once":
                    SwapById(data.Once, t => t.Id, id, other.Id);
                    break;
                case "todo":
                    SwapById(data.Todos, t => t.Id, id, other.Id);
                    break;
                case "event":
                    SwapById(data.Events, t => t.Id, id, other.Id);
                    break;
                case "note":
                    SwapById(data.Notes, t => t.Id, id, other.Id);
                    break;
            }
        }

        BindLists();
        SelectLayout(kind, id);
        if (kind != "category")
            SelectTask(kind, id);
        else if (AppHost.Instance.Data.Categories.FirstOrDefault(c => c.Id == id) is Category cat)
            CategoryList.SelectedItem = cat;
        FlushOverlay();
    }

    void MoveTaskAmongSiblings(int delta)
    {
        var task = SelectedTask();
        if (task == null)
            return;
        MoveAmongSiblings(task.Kind, task.Id, delta);
    }

    OnceItem? SelectedOnce()
    {
        var task = SelectedTask();
        return task?.Kind == "once"
            ? AppHost.Instance.Data.Once.FirstOrDefault(t => t.Id == task.Id)
            : null;
    }

    TodoItem? SelectedTodo()
    {
        var task = SelectedTask();
        return task?.Kind == "todo"
            ? AppHost.Instance.Data.Todos.FirstOrDefault(t => t.Id == task.Id)
            : null;
    }

    EventItem? SelectedEvent()
    {
        var task = SelectedTask();
        return task?.Kind == "event"
            ? AppHost.Instance.Data.Events.FirstOrDefault(t => t.Id == task.Id)
            : null;
    }

    NoteItem? SelectedNote()
    {
        var task = SelectedTask();
        return task?.Kind == "note"
            ? AppHost.Instance.Data.Notes.FirstOrDefault(t => t.Id == task.Id)
            : null;
    }

    void ShowTaskEditor(string? kind)
    {
        OnceEditor.Visibility = kind == "once" ? Visibility.Visible : Visibility.Collapsed;
        TodoEditor.Visibility = kind == "todo" ? Visibility.Visible : Visibility.Collapsed;
        EventEditor.Visibility = kind == "event" ? Visibility.Visible : Visibility.Collapsed;
        NoteEditor.Visibility = kind == "note" ? Visibility.Visible : Visibility.Collapsed;
    }

    static void SelectIndex(ListBox list, int index)
    {
        if (list.Items.Count == 0)
            return;
        list.SelectedIndex = Math.Clamp(index < 0 ? 0 : index, 0, list.Items.Count - 1);
    }

    void LoadSettings()
    {
        _loading = true;
        var s = AppHost.Instance.Data.Settings;
        ClickThroughBox.IsChecked = s.ClickThrough;
        WindowBorderBox.IsChecked = s.ShowWindowBorder;
        LoadTempHideDurations(s);
        TempHideCornerBox.SelectedIndex = (int)s.TempHideWarnCorner;
        LoadSettingsSections(s);
        if (MainTabs != null)
        {
            var tabCount = MainTabs.Items.Count;
            MainTabs.SelectedIndex = tabCount == 0
                ? 0
                : Math.Clamp(s.ConfigTabIndex, 0, tabCount - 1);
        }
        RefreshUpcomingScheduled();
        OpacitySlider.Value = s.Opacity;
        OpacityLabel.Text = $"{s.Opacity:P0}";
        DarknessSlider.Value = s.PanelDarkness;
        DarknessLabel.Text = $"{s.PanelDarkness:P0}";
        PanelDitherBox.IsChecked = s.PanelDither != false;
        DitherSlider.Value = s.PanelDitherStrength;
        DitherLabel.Text = $"{s.PanelDitherStrength:P0}";
        DitherSlider.IsEnabled = s.PanelDither != false;
        TextBackdropBox.IsChecked = s.TextBackdrop;
        BackdropSlider.Value = s.TextBackdropOpacity;
        BackdropLabel.Text = $"{s.TextBackdropOpacity:P0}";
        BackdropSlider.IsEnabled = s.TextBackdrop;
        FontSlider.Value = s.FontSize;
        FontLabel.Text = $"{s.FontSize:0}";
        BoldTextBox.IsChecked = s.BoldText;
        BindSettingColors(s);
        HideCompletedOnceBox.IsChecked = s.HideCompletedOnce;
        HideCompletedRecurringBox.IsChecked = s.HideCompletedRecurring;
        HideCompletedScheduledBox.IsChecked = s.HideCompletedScheduled;
        HideEmptyFoldersBox.IsChecked = s.HideEmptyFolders;
        FolderCountsBox.IsChecked = s.ShowFolderCounts;
        IndentSlider.Value = s.FolderIndent;
        IndentLabel.Text = $"{s.FolderIndent:0}";
        // auto model
        TopFolderIndentSlider.Value = s.TopFolderIndent;
        TopFolderIndentLabel.Text = $"{s.TopFolderIndent:0}";
        ContentIndentSlider.Value = s.FolderContentIndent;
        ContentIndentLabel.Text = $"{s.FolderContentIndent:0}";
        ManualSortBox.IsChecked = s.ManualSort;
        HorizonHoursBox.IsChecked = s.HorizonInHours;
        ApplyHorizonUnitUi();
        RecurringHorizonSlider.Value = s.RecurringHorizon;
        RecurringHorizonLabel.Text = HorizonAmountText(s.RecurringHorizon);
        HorizonSlider.Value = s.EventHorizonDays;
        HorizonLabel.Text = HorizonAmountText(s.EventHorizonDays);
        AutostartBox.IsChecked = s.Autostart;
        NotifyBox.IsChecked = s.Notifications;
        DataPath.Text = AppHost.Instance.Store.FilePath;
        // auto model
        LoadClockSettings(s);
        LoadUsageSettings(s);
    }

    // auto model
    sealed class ClockCategoryPick
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public bool IsChecked { get; set; }
    }

    // auto model
    void LoadClockSettings(AppSettings s)
    {
        if (ClockModuleBox == null || ClockHorizonBox == null || ClockHorizonUnitBox == null)
            return;
        ClockModuleBox.IsChecked = s.ShowClockModule;
        if (ClockLockModeBox != null)
            ClockLockModeBox.SelectedIndex = (int)s.ClockLockMode;
        if (ClockKeepFaceBox != null)
            ClockKeepFaceBox.IsChecked = s.ClockKeepFaceWhenLocked;
        LoadGearCatalogBox(s);
        RefreshPlanetSliders(s);
        LoadClockGearCarrierRates(s);
        if (ClockGearAutoCarrierBox != null)
            ClockGearAutoCarrierBox.IsChecked = s.ClockGearAutoCarrier != false;
        if (ClockGearDebugLabelsBox != null)
            ClockGearDebugLabelsBox.IsChecked = s.ClockGearDebugLabels;
        if (ClockGearDebugRingCenterBox != null)
            ClockGearDebugRingCenterBox.IsChecked = s.ClockGearDebugRingCenter;
        if (ClockGearHideCarouselBox != null)
            ClockGearHideCarouselBox.IsChecked = s.ClockGearHideCarousel;
        LoadClockGearAnimSpeed(s.ClockGearAnimSpeed);
        LoadClockGearOpacity(s.ClockGearOpacity);
        LoadClockGearCarriageStyle(s);
        if (ClockSecondHandBox != null)
            ClockSecondHandBox.IsChecked = s.ClockShowSecondHand;
        if (ClockHourHandBox != null)
            ClockHourHandBox.IsChecked = s.ClockShowHourHand;
        if (ClockTimeOutlineBox != null)
            ClockTimeOutlineBox.IsChecked = s.ClockTimeOutline;
        if (ClockTimeOutlineStrengthSlider != null)
        {
            ClockTimeOutlineStrengthSlider.Value = Math.Clamp(s.ClockTimeOutlineStrength, 1, 6);
            if (ClockTimeOutlineStrengthLabel != null)
                ClockTimeOutlineStrengthLabel.Text = $"{(int)ClockTimeOutlineStrengthSlider.Value}";
        }
        if (ClockTimePosSlider != null)
        {
            ClockTimePosSlider.Value = Math.Clamp(s.ClockTimePosition, 0, 1);
            if (ClockTimePosLabel != null)
                ClockTimePosLabel.Text = $"{ClockTimePosSlider.Value * 100:0}%";
        }
        // auto model
        if (ClockTimeFontSizeSlider != null)
        {
            var fs = Math.Clamp(s.ClockTimeFontSize <= 0 ? 13 : s.ClockTimeFontSize, 8, 48);
            ClockTimeFontSizeSlider.Value = fs;
            if (ClockTimeFontSizeLabel != null)
                ClockTimeFontSizeLabel.Text = $"{fs:0}";
        }
        ClockHorizonUnitBox.SelectedIndex = s.ClockHorizonInHours ? 0 : 1;
        var hours = s.ClockHorizonHours <= 0 ? 24 : s.ClockHorizonHours;
        var amount = s.ClockHorizonInHours ? hours : hours / 24.0;
        ClockHorizonBox.Text = amount.ToString("0.###", CultureInfo.CurrentCulture);
        if (ClockHourFormatBox != null)
            ClockHourFormatBox.SelectedIndex = s.ClockUse12Hour ? 1 : 0;
        if (ClockTickStepBox != null)
            ClockTickStepBox.SelectedIndex = (int)s.ClockTickStep;
        LoadClockTickLength(ClockTickLenHourSlider, ClockTickLenHourLabel, s.ClockTickLengthHour);
        LoadClockTickLength(ClockTickLenThirtySlider, ClockTickLenThirtyLabel, s.ClockTickLengthThirty);
        LoadClockTickLength(ClockTickLenFifteenSlider, ClockTickLenFifteenLabel, s.ClockTickLengthFifteen);
        LoadClockTickLength(ClockTickLenMinuteSlider, ClockTickLenMinuteLabel, s.ClockTickLengthMinute);
        LoadClockTickLength(ClockSecondHandLenSlider, ClockSecondHandLenLabel, s.ClockSecondHandLength);
        LoadClockTickLength(ClockHourHandLenSlider, ClockHourHandLenLabel, s.ClockHourHandLength);
        LoadClockTickLength(ClockWedgeWidthSlider, ClockWedgeWidthLabel, s.ClockWedgeWidth);
        if (ClockShowRecurringBox != null)
            ClockShowRecurringBox.IsChecked = s.ClockShowRecurring;
        if (ClockShowScheduledBox != null)
            ClockShowScheduledBox.IsChecked = s.ClockShowScheduled;
        if (ClockShowLabelsBox != null)
            ClockShowLabelsBox.IsChecked = s.ClockShowEventLabels;
        if (ClockAllCategoriesBox != null)
            ClockAllCategoriesBox.IsChecked = s.ClockAllCategories;
        BindClockColors(s);
        BindClockCategories(s);
        if (ClockCategoryList != null)
            ClockCategoryList.IsEnabled = !s.ClockAllCategories;
    }

    // auto model
    void BindClockColors(AppSettings s)
    {
        ClockColorRingRow?.Bind("Ring", s.ClockColorRing, s.ClockColorRing, false);
        ClockColorMajorTickRow?.Bind("Hour ticks", s.ClockColorMajorTick, s.ClockColorMajorTick, false);
        ClockColorMinorTickRow?.Bind("Minor ticks", s.ClockColorMinorTick, s.ClockColorMinorTick, false);
        ClockColorSecondHandRow?.Bind("Second hand", s.ClockColorSecondHand, s.ClockColorSecondHand, false);
        ClockColorHourHandRow?.Bind("Hour hand", s.ClockColorHourHand, s.ClockColorHourHand, false);
        ClockColorWedgeLineRow?.Bind("Exact-time line (wedge center)", s.ClockColorWedgeLine, s.ClockColorWedgeLine, false); // auto model
        ClockColorTimeRow?.Bind("Current time", s.ClockColorTime, s.ClockColorTime, false);
        ClockColorTimeOutlineRow?.Bind("Time outline", s.ClockColorTimeOutline, s.ClockColorTimeOutline, false);
        ClockColorCaretRow?.Bind("Now caret", s.ClockColorCaret, s.ClockColorCaret, false);
        ClockColorFaceRow?.Bind("Face fill (blank = match overlay panel)", "#202228", s.ClockColorFace, true);
        ClockColorAttentionRow?.Bind("Attention face tint", s.ClockColorAttention, s.ClockColorAttention, false);
        ClockGearColorRow?.Bind("Annulus / ring", s.ClockGearColor, s.ClockGearColor, false);
        ClockGearCompoundColorRow?.Bind(
            "Compound (blank = darkened ring)", s.ClockGearColor, s.ClockGearCompoundColor, true);
        ClockGearPlanetColorRow?.Bind(
            "Planet (blank = ring)", s.ClockGearColor, s.ClockGearPlanetColor, true);
        ClockGearIdlerColorRow?.Bind(
            "Idler (blank = ring)", s.ClockGearColor, s.ClockGearIdlerColor, true);
        ClockGearSunColorRow?.Bind(
            "Center sun (blank = darkened ring)", s.ClockGearColor, s.ClockGearSunColor, true);
        ClockGearCarriageColorRow?.Bind(
            "Carriage / frame", s.ClockGearCarriageColor, s.ClockGearCarriageColor, false);
        if (ClockAttentionTintBox != null)
            ClockAttentionTintBox.IsChecked = s.ClockAttentionTint;
        ClockColorRecurringRow?.Bind("Recurring wedge", s.ClockColorRecurring, s.ClockColorRecurring, false);
        ClockColorScheduledRow?.Bind("Scheduled wedge", s.ClockColorScheduled, s.ClockColorScheduled, false);
        ClockColorDueRow?.Bind("Due wedge", s.ClockColorDue, s.ClockColorDue, false);
    }

    // auto model
    void WireClockColor(ColorRow? row, Action<AppSettings, string> assign)
    {
        if (row == null)
            return;
        row.ColorCommitted += (_, _) =>
        {
            if (row.OverrideHex is not string hex)
                return;
            assign(AppHost.Instance.Data.Settings, hex);
            AppHost.Instance.SyncClockOverlay();
            AppHost.Instance.RequestSave();
        };
    }

    // auto model: allow clearing back to "follow panel"
    void WireClockColorOptional(ColorRow? row, Action<AppSettings, string?> assign)
    {
        if (row == null)
            return;
        row.ColorCommitted += (_, _) =>
        {
            assign(AppHost.Instance.Data.Settings, row.OverrideHex);
            AppHost.Instance.SyncClockOverlay();
            AppHost.Instance.RequestSave();
        };
    }

    // auto model
    void WireUsageColor(ColorRow? row, Action<AppSettings, string> assign)
    {
        if (row == null)
            return;
        row.ColorCommitted += (_, _) =>
        {
            if (row.OverrideHex is not string hex)
                return;
            assign(AppHost.Instance.Data.Settings, hex);
            AppHost.Instance.SyncUsageOverlay();
            AppHost.Instance.NotifyUsageUi();
            AppHost.Instance.RequestSave();
        };
    }

    // auto model
    void LoadUsageSettings(AppSettings s)
    {
        if (UsageModuleBox == null)
            return;
        UsageModuleBox.IsChecked = s.ShowUsageModule;
        if (UsageDockInRemindersBox != null)
            UsageDockInRemindersBox.IsChecked = s.UsageDockInReminders;
        if (UsageDockShowPercentBox != null)
            UsageDockShowPercentBox.IsChecked = s.UsageDockShowPercent;
        if (UsageDockPlainGrayBox != null)
            UsageDockPlainGrayBox.IsChecked = s.UsageDockPlainGray;
        if (UsageDockPlainGrayBarBox != null)
            UsageDockPlainGrayBarBox.IsChecked = s.UsageDockPlainGrayBar;
        if (UsageDockDimAtLimitBox != null)
            UsageDockDimAtLimitBox.IsChecked = s.UsageDockDimAtLimit;
        if (UsageDockWeekInherit5hDimBox != null)
            UsageDockWeekInherit5hDimBox.IsChecked = s.UsageDockWeekInherit5hDim;
        if (UsageLockModeBox != null)
            UsageLockModeBox.SelectedIndex = (int)s.UsageLockMode;
        if (UsageShowClaudeBox != null)
            UsageShowClaudeBox.IsChecked = s.UsageShowClaude;
        if (UsageShowCursorBox != null)
            UsageShowCursorBox.IsChecked = s.UsageShowCursor;
        if (UsageInjectRemindersBox != null)
            UsageInjectRemindersBox.IsChecked = s.UsageInjectReminders;
        if (UsageBoardSmartFilterBox != null)
            UsageBoardSmartFilterBox.IsChecked = s.UsageBoardSmartFilter;
        if (UsageShowOnClockBox != null)
            UsageShowOnClockBox.IsChecked = s.UsageShowOnClock;
        if (UsagePollSlider != null)
        {
            UsagePollSlider.Value = Math.Clamp(s.UsagePollMinutes, 5, 120);
            if (UsagePollLabel != null)
                UsagePollLabel.Text = $"{(int)UsagePollSlider.Value}m";
        }
        if (UsageSessionPctSlider != null)
        {
            UsageSessionPctSlider.Value = Math.Clamp(s.UsageFullSessionWeeklyPercent, 1, 50);
            if (UsageSessionPctLabel != null)
                UsageSessionPctLabel.Text = $"{UsageSessionPctSlider.Value:0}%";
        }
        if (UsageCushionSlider != null)
        {
            UsageCushionSlider.Value = Math.Clamp(s.UsageReminderCushion, 1, 5);
            if (UsageCushionLabel != null)
                UsageCushionLabel.Text = $"{UsageCushionSlider.Value:0.#}×";
        }
        LoadUsageLead(UsageLead5hSlider, UsageLead5hLabel, s.UsageAtLimitLeadHours5h, 0.25, 5);
        LoadUsageLead(UsageLeadWeeklySlider, UsageLeadWeeklyLabel, s.UsageAtLimitLeadHoursWeekly, 1, 168);
        LoadUsageLead(UsageLeadOpusSlider, UsageLeadOpusLabel, s.UsageAtLimitLeadHoursOpus, 1, 168);
        LoadUsageLead(UsageLeadCursorSlider, UsageLeadCursorLabel, s.UsageAtLimitLeadHoursCursor, 1, 168);
        LoadUsageLead(UsageClockLead5hSlider, UsageClockLead5hLabel, s.UsageClockAtLimitLeadHours5h, 0.25, 5);
        LoadUsageLead(UsageClockLeadWeeklySlider, UsageClockLeadWeeklyLabel, s.UsageClockAtLimitLeadHoursWeekly, 1, 168);
        LoadUsageLead(UsageClockLeadOpusSlider, UsageClockLeadOpusLabel, s.UsageClockAtLimitLeadHoursOpus, 1, 168);
        LoadUsageLead(UsageClockLeadCursorSlider, UsageClockLeadCursorLabel, s.UsageClockAtLimitLeadHoursCursor, 1, 168);
        UsageColorClaudeRow?.Bind("Claude accent", s.UsageColorClaude, s.UsageColorClaude, false);
        UsageColorCursorRow?.Bind("Cursor accent", s.UsageColorCursor, s.UsageColorCursor, false);
        if (UsageBoardColorMode5hBox != null)
            UsageBoardColorMode5hBox.SelectedIndex = (int)s.UsageBoardColorMode5h;
        if (UsageBoardColorModeWeeklyBox != null)
            UsageBoardColorModeWeeklyBox.SelectedIndex = (int)s.UsageBoardColorModeWeekly;
        if (UsageBoardColorModeOpusBox != null)
            UsageBoardColorModeOpusBox.SelectedIndex = (int)s.UsageBoardColorModeOpus;
        if (UsageBoardColorModeCursorBox != null)
            UsageBoardColorModeCursorBox.SelectedIndex = (int)s.UsageBoardColorModeCursor;
        UsageBoardColor5hRow?.Bind("Claude 5h fixed color", s.UsageBoardColor5h, s.UsageBoardColor5h, false);
        UsageBoardColorWeeklyRow?.Bind("Claude weekly fixed color", s.UsageBoardColorWeekly, s.UsageBoardColorWeekly, false);
        UsageBoardColorOpusRow?.Bind("Claude Opus fixed color", s.UsageBoardColorOpus, s.UsageBoardColorOpus, false);
        UsageBoardColorCursorRow?.Bind("Cursor fixed color", s.UsageBoardColorCursor, s.UsageBoardColorCursor, false);
        UpdateUsageBoardColorRowEnabled();
    }

    // auto model
    void UpdateUsageBoardColorRowEnabled()
    {
        SetUsageBoardColorRowEnabled(UsageBoardColor5hRow, UsageBoardColorMode5hBox);
        SetUsageBoardColorRowEnabled(UsageBoardColorWeeklyRow, UsageBoardColorModeWeeklyBox);
        SetUsageBoardColorRowEnabled(UsageBoardColorOpusRow, UsageBoardColorModeOpusBox);
        SetUsageBoardColorRowEnabled(UsageBoardColorCursorRow, UsageBoardColorModeCursorBox);
    }

    static void SetUsageBoardColorRowEnabled(ColorRow? row, ComboBox? modeBox)
    {
        if (row == null || modeBox == null)
            return;
        row.IsEnabled = modeBox.SelectedIndex == (int)UsageBoardColorMode.Fixed;
        row.Opacity = row.IsEnabled ? 1 : 0.45;
    }

    // auto model
    void UsageBoardColorModeChanged(object sender, EventArgs e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        if (UsageBoardColorMode5hBox?.SelectedIndex is >= 0 and var m5)
            s.UsageBoardColorMode5h = (UsageBoardColorMode)m5;
        if (UsageBoardColorModeWeeklyBox?.SelectedIndex is >= 0 and var mw)
            s.UsageBoardColorModeWeekly = (UsageBoardColorMode)mw;
        if (UsageBoardColorModeOpusBox?.SelectedIndex is >= 0 and var mo)
            s.UsageBoardColorModeOpus = (UsageBoardColorMode)mo;
        if (UsageBoardColorModeCursorBox?.SelectedIndex is >= 0 and var mc)
            s.UsageBoardColorModeCursor = (UsageBoardColorMode)mc;
        UpdateUsageBoardColorRowEnabled();
        AppHost.Instance.NotifyUsageUi();
        AppHost.Instance.RequestSave();
    }

    // auto model
    static void LoadUsageLead(Slider? slider, TextBlock? label, double hours, double min, double max)
    {
        if (slider == null)
            return;
        var v = Math.Clamp(hours <= 0 ? min : hours, min, max);
        slider.Value = v;
        if (label != null)
            label.Text = FormatUsageLead(v);
    }

    static string FormatUsageLead(double hours)
    {
        if (hours < 1)
            return $"{hours * 60:0}m";
        if (hours < 24)
            return $"{hours:0.#}h";
        return $"{hours / 24:0.#}d";
    }

    // auto model
    void UsageModuleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || UsageModuleBox == null)
            return;
        AppHost.Instance.Data.Settings.ShowUsageModule = UsageModuleBox.IsChecked == true;
        AppHost.Instance.SyncUsageOverlay();
        AppHost.Instance.RequestSave();
        if (UsageModuleBox.IsChecked == true)
            AppHost.Instance.Usage?.RequestRefreshSoon();
    }

    // auto model
    void UsageLockChanged(object sender, EventArgs e)
    {
        if (_loading || UsageLockModeBox == null || UsageLockModeBox.SelectedIndex < 0)
            return;
        AppHost.Instance.Data.Settings.UsageLockMode = (UsageLockMode)UsageLockModeBox.SelectedIndex;
        AppHost.Instance.SyncUsageOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void UsageFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        if (UsageShowClaudeBox != null)
            s.UsageShowClaude = UsageShowClaudeBox.IsChecked == true;
        if (UsageShowCursorBox != null)
            s.UsageShowCursor = UsageShowCursorBox.IsChecked == true;
        if (UsageInjectRemindersBox != null)
            s.UsageInjectReminders = UsageInjectRemindersBox.IsChecked == true;
        if (UsageBoardSmartFilterBox != null)
            s.UsageBoardSmartFilter = UsageBoardSmartFilterBox.IsChecked == true;
        if (UsageDockInRemindersBox != null)
            s.UsageDockInReminders = UsageDockInRemindersBox.IsChecked == true;
        if (UsageDockShowPercentBox != null)
            s.UsageDockShowPercent = UsageDockShowPercentBox.IsChecked == true;
        if (UsageDockPlainGrayBox != null)
            s.UsageDockPlainGray = UsageDockPlainGrayBox.IsChecked == true;
        if (UsageDockPlainGrayBarBox != null)
            s.UsageDockPlainGrayBar = UsageDockPlainGrayBarBox.IsChecked == true;
        if (UsageDockDimAtLimitBox != null)
            s.UsageDockDimAtLimit = UsageDockDimAtLimitBox.IsChecked == true;
        if (UsageDockWeekInherit5hDimBox != null)
            s.UsageDockWeekInherit5hDim = UsageDockWeekInherit5hDimBox.IsChecked == true;
        if (UsageShowOnClockBox != null)
            s.UsageShowOnClock = UsageShowOnClockBox.IsChecked == true;
        AppHost.Instance.SyncUsageOverlay();
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.NotifyUsageUi();
        AppHost.Instance.RequestSave();
        AppHost.Instance.Usage?.RequestRefreshSoon();
    }

    // auto model
    void UsageSmartKnobsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        if (UsageSessionPctSlider != null)
        {
            s.UsageFullSessionWeeklyPercent = Math.Clamp(Math.Round(UsageSessionPctSlider.Value), 1, 50);
            if (UsageSessionPctLabel != null)
                UsageSessionPctLabel.Text = $"{s.UsageFullSessionWeeklyPercent:0}%";
        }
        if (UsageCushionSlider != null)
        {
            s.UsageReminderCushion = Math.Clamp(Math.Round(UsageCushionSlider.Value * 2) / 2.0, 1, 5);
            if (UsageCushionLabel != null)
                UsageCushionLabel.Text = $"{s.UsageReminderCushion:0.#}×";
        }
        AppHost.Instance.NotifyUsageUi();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void UsageLeadChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        if (UsageLead5hSlider != null)
        {
            s.UsageAtLimitLeadHours5h = Math.Clamp(UsageLead5hSlider.Value, 0.25, 5);
            if (UsageLead5hLabel != null)
                UsageLead5hLabel.Text = FormatUsageLead(s.UsageAtLimitLeadHours5h);
        }
        if (UsageLeadWeeklySlider != null)
        {
            s.UsageAtLimitLeadHoursWeekly = Math.Clamp(Math.Round(UsageLeadWeeklySlider.Value), 1, 168);
            if (UsageLeadWeeklyLabel != null)
                UsageLeadWeeklyLabel.Text = FormatUsageLead(s.UsageAtLimitLeadHoursWeekly);
        }
        if (UsageLeadOpusSlider != null)
        {
            s.UsageAtLimitLeadHoursOpus = Math.Clamp(Math.Round(UsageLeadOpusSlider.Value), 1, 168);
            if (UsageLeadOpusLabel != null)
                UsageLeadOpusLabel.Text = FormatUsageLead(s.UsageAtLimitLeadHoursOpus);
        }
        if (UsageLeadCursorSlider != null)
        {
            s.UsageAtLimitLeadHoursCursor = Math.Clamp(Math.Round(UsageLeadCursorSlider.Value), 1, 168);
            if (UsageLeadCursorLabel != null)
                UsageLeadCursorLabel.Text = FormatUsageLead(s.UsageAtLimitLeadHoursCursor);
        }
        AppHost.Instance.NotifyUsageUi();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void UsageClockLeadChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        if (UsageClockLead5hSlider != null)
        {
            s.UsageClockAtLimitLeadHours5h = Math.Clamp(UsageClockLead5hSlider.Value, 0.25, 5);
            if (UsageClockLead5hLabel != null)
                UsageClockLead5hLabel.Text = FormatUsageLead(s.UsageClockAtLimitLeadHours5h);
        }
        if (UsageClockLeadWeeklySlider != null)
        {
            s.UsageClockAtLimitLeadHoursWeekly = Math.Clamp(Math.Round(UsageClockLeadWeeklySlider.Value), 1, 168);
            if (UsageClockLeadWeeklyLabel != null)
                UsageClockLeadWeeklyLabel.Text = FormatUsageLead(s.UsageClockAtLimitLeadHoursWeekly);
        }
        if (UsageClockLeadOpusSlider != null)
        {
            s.UsageClockAtLimitLeadHoursOpus = Math.Clamp(Math.Round(UsageClockLeadOpusSlider.Value), 1, 168);
            if (UsageClockLeadOpusLabel != null)
                UsageClockLeadOpusLabel.Text = FormatUsageLead(s.UsageClockAtLimitLeadHoursOpus);
        }
        if (UsageClockLeadCursorSlider != null)
        {
            s.UsageClockAtLimitLeadHoursCursor = Math.Clamp(Math.Round(UsageClockLeadCursorSlider.Value), 1, 168);
            if (UsageClockLeadCursorLabel != null)
                UsageClockLeadCursorLabel.Text = FormatUsageLead(s.UsageClockAtLimitLeadHoursCursor);
        }
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void UsagePollChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || UsagePollSlider == null)
            return;
        var minutes = (int)Math.Round(UsagePollSlider.Value);
        minutes = Math.Clamp(minutes, 5, 120);
        AppHost.Instance.Data.Settings.UsagePollMinutes = minutes;
        if (UsagePollLabel != null)
            UsagePollLabel.Text = $"{minutes}m";
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockLockChanged(object sender, EventArgs e)
    {
        if (_loading || ClockLockModeBox == null || ClockLockModeBox.SelectedIndex < 0)
            return;
        AppHost.Instance.Data.Settings.ClockLockMode = (ClockLockMode)ClockLockModeBox.SelectedIndex;
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void BindClockCategories(AppSettings s)
    {
        if (ClockCategoryList == null)
            return;
        CategoryTree.RefreshLabels(AppHost.Instance.Data.Categories);
        var selected = s.ClockCategoryIds?.ToHashSet(StringComparer.Ordinal) ?? [];
        var rows = new List<ClockCategoryPick>
        {
            new()
            {
                Id = "",
                Name = "(Unfiled)",
                IsChecked = selected.Contains("")
            }
        };
        foreach (var folder in AppHost.Instance.Data.Categories.OrderBy(c => c.PathLabel, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ClockCategoryPick
            {
                Id = folder.Id,
                Name = string.IsNullOrEmpty(folder.PathLabel) ? folder.Name : folder.PathLabel,
                IsChecked = selected.Contains(folder.Id)
            });
        }

        ClockCategoryList.ItemsSource = rows;
    }

    // auto model
    void ClockModuleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || ClockModuleBox == null)
            return;
        AppHost.Instance.Data.Settings.ShowClockModule = ClockModuleBox.IsChecked == true;
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockFilterChanged(object sender, EventArgs e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        if (ClockHourFormatBox != null)
            s.ClockUse12Hour = ClockHourFormatBox.SelectedIndex == 1;
        if (ClockTickStepBox != null && ClockTickStepBox.SelectedIndex >= 0)
            s.ClockTickStep = (ClockTickStep)ClockTickStepBox.SelectedIndex;
        if (ClockShowRecurringBox != null)
            s.ClockShowRecurring = ClockShowRecurringBox.IsChecked == true;
        if (ClockShowScheduledBox != null)
            s.ClockShowScheduled = ClockShowScheduledBox.IsChecked == true;
        if (ClockShowLabelsBox != null)
            s.ClockShowEventLabels = ClockShowLabelsBox.IsChecked == true;
        if (ClockKeepFaceBox != null)
            s.ClockKeepFaceWhenLocked = ClockKeepFaceBox.IsChecked == true;
        if (ClockAttentionTintBox != null)
            s.ClockAttentionTint = ClockAttentionTintBox.IsChecked == true;
        if (ClockGearDebugLabelsBox != null)
            s.ClockGearDebugLabels = ClockGearDebugLabelsBox.IsChecked == true;
        if (ClockGearDebugRingCenterBox != null)
            s.ClockGearDebugRingCenter = ClockGearDebugRingCenterBox.IsChecked == true;
        if (ClockGearHideCarouselBox != null)
            s.ClockGearHideCarousel = ClockGearHideCarouselBox.IsChecked == true;
        if (ClockSecondHandBox != null)
            s.ClockShowSecondHand = ClockSecondHandBox.IsChecked == true;
        if (ClockHourHandBox != null)
            s.ClockShowHourHand = ClockHourHandBox.IsChecked == true;
        if (ClockTimeOutlineBox != null)
            s.ClockTimeOutline = ClockTimeOutlineBox.IsChecked == true;
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockGearDriveModeChanged(object sender, EventArgs e)
    {
        if (_loading)
            return;
        ApplySelectedGearCombo(AppHost.Instance.Data.Settings);
        RefreshPlanetSliders(AppHost.Instance.Data.Settings);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    void LoadGearCatalogBox(AppSettings s)
    {
        if (ClockGearDriveModeBox == null)
            return;
        var prev = _loading;
        _loading = true;
        try
        {
            ClockGearDriveModeBox.Items.Clear();
            ClockGearDriveModeBox.Items.Add(new ComboBoxItem { Content = "Off", Tag = null });
            foreach (var combo in ClockGearGeometry.PrecomputedCombos())
                ClockGearDriveModeBox.Items.Add(new ComboBoxItem { Content = combo.Label, Tag = combo });
            ClockGearDriveModeBox.SelectedIndex = IndexOfGearCombo(s);
        }
        finally
        {
            _loading = prev;
        }
    }

    static int IndexOfGearCombo(AppSettings s)
    {
        if (s.ClockGearDriveMode == ClockGearDriveMode.Off)
            return 0;
        var combos = ClockGearGeometry.PrecomputedCombos();
        var stored = (s.ClockGearCompounds ?? []).Where(n => n > 0).ToArray();
        for (var i = 0; i < combos.Count; i++)
        {
            var c = combos[i];
            if (stored.Length == c.Compounds.Length && stored.SequenceEqual(c.Compounds))
                return i + 1;
        }
        if (ClockGearGeometry.TryGetProgression(s.ClockGearDriveMode, out var legacy))
        {
            var ncs = legacy.Compounds();
            for (var i = 0; i < combos.Count; i++)
            {
                if (combos[i].Compounds.SequenceEqual(ncs))
                    return i + 1;
            }
        }
        return 0;
    }

    void ApplySelectedGearCombo(AppSettings s)
    {
        if (ClockGearDriveModeBox?.SelectedItem is not ComboBoxItem item)
            return;
        if (item.Tag is ClockGearGeometry.GearCombo combo)
        {
            s.ClockGearDriveMode = ClockGearDriveMode.Concentric;
            s.ClockGearCompounds = [.. combo.Compounds];
            var packed = ClockGearGeometry.DensestStagePlanets(combo.Compounds, GearRim());
            s.ClockGearPlanetCounts = PadPlanetCounts(packed.Length > 0 ? packed : combo.StagePlanets);
            s.ClockGearPlanetOffsetsDeg = [0, 0, 0, 0, 0];
        }
        else
        {
            s.ClockGearDriveMode = ClockGearDriveMode.Off;
            s.ClockGearCompounds = [];
        }
    }

    static List<int> PadPlanetCounts(IReadOnlyList<int>? src)
    {
        var list = new List<int>();
        if (src != null)
        {
            foreach (var n in src)
                list.Add(Math.Clamp(n <= 0 ? 1 : n, 1, 12));
        }
        if (list.Count == 0)
            list.Add(1);
        while (list.Count < 5)
            list.Add(1);
        return list;
    }

    // auto model
    void LoadClockGearAnimSpeed(double speed)
    {
        if (ClockGearAnimSpeedSlider == null)
            return;
        var v = Math.Clamp(Math.Round(speed), 1, 1440);
        ClockGearAnimSpeedSlider.Value = v;
        if (ClockGearAnimSpeedLabel != null)
            ClockGearAnimSpeedLabel.Text = $"{v:0}×";
    }

    // auto model
    void ClockGearAnimSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ClockGearAnimSpeedSlider == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        s.ClockGearAnimSpeed = Math.Clamp(Math.Round(ClockGearAnimSpeedSlider.Value), 1, 1440);
        if (Math.Abs(ClockGearAnimSpeedSlider.Value - s.ClockGearAnimSpeed) > 0.01)
            ClockGearAnimSpeedSlider.Value = s.ClockGearAnimSpeed;
        if (ClockGearAnimSpeedLabel != null)
            ClockGearAnimSpeedLabel.Text = $"{s.ClockGearAnimSpeed:0}×";
        // Speed is read each frame — no face rebuild needed.
        AppHost.Instance.RequestSave();
    }

    void LoadClockGearOpacity(double opacity)
    {
        if (ClockGearOpacitySlider == null)
            return;
        var pct = Math.Clamp(Math.Round(Math.Clamp(opacity, 0.05, 1) * 100), 5, 100);
        ClockGearOpacitySlider.Value = pct;
        if (ClockGearOpacityLabel != null)
            ClockGearOpacityLabel.Text = $"{pct:0}%";
    }

    void ClockGearOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ClockGearOpacitySlider == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        var pct = Math.Clamp(Math.Round(ClockGearOpacitySlider.Value), 5, 100);
        if (Math.Abs(ClockGearOpacitySlider.Value - pct) > 0.01)
            ClockGearOpacitySlider.Value = pct;
        s.ClockGearOpacity = pct / 100.0;
        if (ClockGearOpacityLabel != null)
            ClockGearOpacityLabel.Text = $"{pct:0}%";
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    sealed class CarriageStageUi
    {
        public required StackPanel Root { get; init; }
        public required ComboBox KindBox { get; init; }
        public required TextBlock Hint { get; init; }
        public required TextBlock SweepTitle { get; init; }
        public required TextBlock SweepValue { get; init; }
        public required Slider Rail { get; init; }
        public required TextBlock RailValue { get; init; }
        public required Slider Rung { get; init; }
        public required TextBlock RungValue { get; init; }
        public required Slider Sweep { get; init; }
        public required ComboBox DirBox { get; init; }
        public required CheckBox PinsBox { get; init; }
        public required Slider Hub { get; init; }
        public required TextBlock HubValue { get; init; }
        public required StackPanel LinkPanel { get; init; }
        public required Slider Repeats { get; init; }
        public required TextBlock RepeatsValue { get; init; }
        public required Slider Stride { get; init; }
        public required TextBlock StrideValue { get; init; }
    }

    static readonly (ClockGearCarriageKind Kind, string Label, string Hint)[] CarriageKindChoices =
    [
        (ClockGearCarriageKind.Orbit, "Orbit rails",
            "Two rings with evenly spaced swept braces."),
        (ClockGearCarriageKind.PinLinks, "Pin links (dynamic)",
            "A bow from each compound pin to an idler pin. Repeats copy that link evenly between this planet and the next."),
        (ClockGearCarriageKind.Skeleton, "Skeleton",
            "Pin-to-pin bows only — no orbit rings."),
        (ClockGearCarriageKind.Cradles, "Planet cradles",
            "Filled C-arms behind each pair, plus the pin-link controls below."),
        (ClockGearCarriageKind.Truss, "Lattice truss",
            "X-bracing between the two orbit rails."),
        (ClockGearCarriageKind.Helix, "Helix ribbon",
            "Spiral ribbons winding between the two orbits."),
        (ClockGearCarriageKind.Fan, "Leaf spring",
            "Same pin-link template, with repeats filling the gap to the next planet.")
    ];

    CarriageStageUi[]? _carriageUi;

    static bool CarriageUsesPinLinks(ClockGearCarriageKind kind) =>
        kind is ClockGearCarriageKind.PinLinks or ClockGearCarriageKind.Skeleton
            or ClockGearCarriageKind.Cradles or ClockGearCarriageKind.Fan;

    static string CarriageSweepTitle(ClockGearCarriageKind kind) => kind switch
    {
        ClockGearCarriageKind.Helix => "Helix twist",
        ClockGearCarriageKind.PinLinks or ClockGearCarriageKind.Skeleton
            or ClockGearCarriageKind.Fan or ClockGearCarriageKind.Cradles => "Link bow",
        _ => "Brace sweep"
    };

    static string CarriageRepeatsText(int repeats) => repeats switch
    {
        0 => "none",
        1 => "1 mid",
        2 => "thirds",
        _ => $"{repeats} extras"
    };

    static string CarriageStrideText(int stride) => stride switch
    {
        0 => "same set",
        1 => "next planet",
        _ => $"skip {stride - 1}"
    };

    void LoadClockGearCarriageStyle(AppSettings s)
    {
        EnsureCarriageStageUi();
        if (_carriageUi == null)
            return;
        s.ClockGearCarriageStages ??= [];
        while (s.ClockGearCarriageStages.Count < 5)
        {
            var seed = s.ClockGearCarriageStages.Count > 0
                ? s.ClockGearCarriageStages[^1].Clone()
                : ClockGearCarriageStage.FromLegacy(s);
            s.ClockGearCarriageStages.Add(seed.Clamped());
        }
        var compounds = (s.ClockGearCompounds ?? []).Where(n => n > 0).ToArray();
        var live = s.ClockGearDriveMode != ClockGearDriveMode.Off && compounds.Length >= 1;
        var prev = _loading;
        _loading = true;
        try
        {
            for (var i = 0; i < _carriageUi.Length; i++)
            {
                var ui = _carriageUi[i];
                var on = live && i < Math.Max(compounds.Length, 1);
                ui.Root.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (!on)
                    continue;
                var st = ClockGearCarriageStage.At(s, i).Clamped();
                SelectCarriageKind(ui.KindBox, st.Kind);
                ui.Hint.Text = CarriageKindChoices.FirstOrDefault(c => c.Kind == st.Kind).Hint
                    ?? CarriageKindChoices[0].Hint;
                ui.Rail.Value = st.Rail;
                ui.RailValue.Text = $"{st.Rail:0.#}";
                ui.Rung.Value = st.Rung;
                ui.RungValue.Text = $"{st.Rung:0.#}";
                ui.Sweep.Value = st.SweepDeg;
                ui.SweepValue.Text = $"{st.SweepDeg:0}°";
                ui.SweepTitle.Text = CarriageSweepTitle(st.Kind);
                ui.DirBox.SelectedIndex = st.Reverse ? 1 : 0;
                ui.PinsBox.IsChecked = st.Pins;
                ui.Hub.Value = st.Hub;
                ui.HubValue.Text = st.Hub < 0.05 ? "off" : $"{st.Hub:0.#}";
                ui.Repeats.Value = st.LinkRepeats;
                ui.RepeatsValue.Text = CarriageRepeatsText(st.LinkRepeats);
                ui.Stride.Value = st.LinkStride;
                ui.StrideValue.Text = CarriageStrideText(st.LinkStride);
                ui.LinkPanel.Visibility = CarriageUsesPinLinks(st.Kind)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally
        {
            _loading = prev;
        }
    }

    void EnsureCarriageStageUi()
    {
        if (_carriageUi != null || ClockGearCarriageHost == null)
            return;
        _carriageUi = new CarriageStageUi[4];
        for (var i = 0; i < _carriageUi.Length; i++)
            _carriageUi[i] = BuildCarriageStageUi(i);
    }

    CarriageStageUi BuildCarriageStageUi(int index)
    {
        var comboStyle = (Style)FindResource("DarkCombo");
        var checkStyle = (Style)FindResource("DarkCheck");
        var root = new StackPanel { Margin = new Thickness(0, index == 0 ? 0 : 10, 0, 0) };
        root.Children.Add(new TextBlock
        {
            Text = $"Ring {index + 1}",
            Foreground = BrushHex("#9AA0A8"),
            Margin = new Thickness(0, 0, 0, 4)
        });

        var kindBox = new ComboBox
        {
            Style = comboStyle,
            MaxDropDownHeight = 360
        };
        foreach (var choice in CarriageKindChoices)
            kindBox.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Kind });
        kindBox.SelectedIndex = 0;
        kindBox.SelectionChanged += (_, _) => CarriageStageChanged(index);
        root.Children.Add(kindBox);

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushHex("#7A8088"),
            Margin = new Thickness(0, 4, 0, 0),
            Text = CarriageKindChoices[0].Hint
        };
        root.Children.Add(hint);

        var (rail, railValue) = AddCarriageSlider(root, "Rail thickness", 1, 12, 0.5, "4", index);
        var (rung, rungValue) = AddCarriageSlider(root, "Brace thickness", 1, 10, 0.5, "2.4", index);
        var (sweep, sweepValue, sweepTitle) = AddCarriageSliderTitled(root, "Brace sweep", 0, 45, 1, "18°", index);

        var dirBox = new ComboBox
        {
            Style = comboStyle,
            Margin = new Thickness(0, 8, 0, 0)
        };
        dirBox.Items.Add(new ComboBoxItem { Content = "Clockwise braces" });
        dirBox.Items.Add(new ComboBoxItem { Content = "Counter-clockwise braces" });
        dirBox.SelectedIndex = 0;
        dirBox.SelectionChanged += (_, _) => CarriageStageChanged(index);
        root.Children.Add(dirBox);

        var pins = new CheckBox
        {
            Style = checkStyle,
            Content = "Axle pins through gear centers",
            Margin = new Thickness(0, 8, 0, 0),
            IsChecked = true
        };
        pins.Checked += (_, _) => CarriageStageChanged(index);
        pins.Unchecked += (_, _) => CarriageStageChanged(index);
        root.Children.Add(pins);

        var (hub, hubValue) = AddCarriageSlider(root, "Hub reinforcement", 0, 14, 0.5, "off", index);

        var linkPanel = new StackPanel();
        var (repeats, repeatsValue) = AddCarriageSlider(linkPanel, "Link repeats", 0, 8, 1, "none", index);
        var (stride, strideValue) = AddCarriageSlider(linkPanel, "Link stride", 0, 6, 1, "same set", index);
        var linkHint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushHex("#7A8088"),
            Margin = new Thickness(0, 4, 0, 0),
            Text = "Repeats copy the pin link evenly between this planet and the next (1 = one in the middle, 2 = thirds). Stride 0 is own idler; 1+ braces to another planet’s idler."
        };
        linkPanel.Children.Add(linkHint);
        root.Children.Add(linkPanel);
        ClockGearCarriageHost!.Children.Add(root);

        return new CarriageStageUi
        {
            Root = root,
            KindBox = kindBox,
            Hint = hint,
            SweepTitle = sweepTitle,
            SweepValue = sweepValue,
            Rail = rail,
            RailValue = railValue,
            Rung = rung,
            RungValue = rungValue,
            Sweep = sweep,
            DirBox = dirBox,
            PinsBox = pins,
            Hub = hub,
            HubValue = hubValue,
            LinkPanel = linkPanel,
            Repeats = repeats,
            RepeatsValue = repeatsValue,
            Stride = stride,
            StrideValue = strideValue
        };
    }

    (Slider Slider, TextBlock Value) AddCarriageSlider(
        Panel host, string title, double min, double max, double tick, string initial, int index)
    {
        var (slider, value, _) = AddCarriageSliderTitled(host, title, min, max, tick, initial, index);
        return (slider, value);
    }

    (Slider Slider, TextBlock Value, TextBlock Title) AddCarriageSliderTitled(
        Panel host, string title, double min, double max, double tick, string initial, int index)
    {
        var value = new TextBlock
        {
            Foreground = BrushHex("#9AA0A8"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 48,
            Text = initial
        };
        DockPanel.SetDock(value, Dock.Right);
        var titleBlock = new TextBlock
        {
            Text = title,
            Foreground = BrushHex("#9AA0A8"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(value);
        row.Children.Add(titleBlock);
        host.Children.Add(row);
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = tick,
            IsSnapToTickEnabled = true,
            Value = min
        };
        slider.PreviewMouseLeftButtonDown += SliderGoToClick;
        slider.MouseMove += SliderDrag;
        slider.MouseLeftButtonUp += SliderEndDrag;
        slider.LostMouseCapture += SliderEndDrag;
        slider.ValueChanged += (_, _) => CarriageStageChanged(index);
        host.Children.Add(slider);
        return (slider, value, titleBlock);
    }

    static SolidColorBrush BrushHex(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex)!;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static void SelectCarriageKind(ComboBox box, ClockGearCarriageKind kind)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item && item.Tag is ClockGearCarriageKind k && k == kind)
            {
                box.SelectedIndex = i;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    void CarriageStageChanged(int index)
    {
        if (_loading || _carriageUi == null || index < 0 || index >= _carriageUi.Length)
            return;
        var s = AppHost.Instance.Data.Settings;
        s.ClockGearCarriageStages ??= [];
        while (s.ClockGearCarriageStages.Count < 5)
            s.ClockGearCarriageStages.Add(ClockGearCarriageStage.FromLegacy(s));
        var ui = _carriageUi[index];
        var kind = ClockGearCarriageKind.Orbit;
        if (ui.KindBox.SelectedItem is ComboBoxItem pick && pick.Tag is ClockGearCarriageKind tagged)
            kind = tagged;
        var st = s.ClockGearCarriageStages[index];
        st.Kind = kind;
        st.Rail = Math.Clamp(ui.Rail.Value, 1, 12);
        st.Rung = Math.Clamp(ui.Rung.Value, 1, 10);
        st.SweepDeg = Math.Clamp(Math.Round(ui.Sweep.Value), 0, 45);
        st.Reverse = ui.DirBox.SelectedIndex == 1;
        st.Pins = ui.PinsBox.IsChecked == true;
        st.Hub = Math.Clamp(ui.Hub.Value, 0, 14);
        st.LinkRepeats = (int)Math.Clamp(Math.Round(ui.Repeats.Value), 0, 8);
        st.LinkStride = (int)Math.Clamp(Math.Round(ui.Stride.Value), 0, 6);
        st.Clamped();
        ui.RailValue.Text = $"{st.Rail:0.#}";
        ui.RungValue.Text = $"{st.Rung:0.#}";
        ui.SweepValue.Text = $"{st.SweepDeg:0}°";
        ui.SweepTitle.Text = CarriageSweepTitle(kind);
        ui.HubValue.Text = st.Hub < 0.05 ? "off" : $"{st.Hub:0.#}";
        ui.RepeatsValue.Text = CarriageRepeatsText(st.LinkRepeats);
        ui.StrideValue.Text = CarriageStrideText(st.LinkStride);
        ui.Hint.Text = CarriageKindChoices.FirstOrDefault(c => c.Kind == kind).Hint
            ?? CarriageKindChoices[0].Hint;
        ui.LinkPanel.Visibility = CarriageUsesPinLinks(kind)
            ? Visibility.Visible : Visibility.Collapsed;
        s.ClockGearCarriageRail = s.ClockGearCarriageStages[0].Rail;
        s.ClockGearCarriageRung = s.ClockGearCarriageStages[0].Rung;
        s.ClockGearCarriageSweepDeg = s.ClockGearCarriageStages[0].SweepDeg;
        s.ClockGearCarriageReverse = s.ClockGearCarriageStages[0].Reverse;
        s.ClockGearCarriagePins = s.ClockGearCarriageStages[0].Pins;
        s.ClockGearCarriageHub = s.ClockGearCarriageStages[0].Hub;
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    void LoadClockGearCarrierRates(AppSettings s)
    {
        var rates = s.ClockGearCarrierDegPerMin ?? [0, 0, 0, 0, 0];
        while (rates.Count < 5)
            rates.Add(0);
        s.ClockGearCarrierDegPerMin = rates;
        var auto = s.ClockGearAutoCarrier != false;
        List<ClockGearGeometry.StageSolve>? diagStages = null;
        double[]? diagSecs = null;
        var diagOuter = 0.0;
        if (ClockGearGeometry.TryGetProgression(s, out var prog)
            && ClockGearGeometry.TrySolveTrain(
                prog, GearRim(), s.ClockGearPlanetCounts ?? [], s.ClockGearPlanetOffsetsDeg ?? [],
                s.ClockGearIdlerOffsetsDeg ?? [], out var stages, out _)
            && stages.Count > 0)
        {
            stages = ClockGearGeometry.WithDrawnCompounds(stages, s.ClockGearCompoundScale);
            var hours = s.ClockHorizonHours <= 0 ? 24 : s.ClockHorizonHours;
            diagOuter = 1.0 / ClockGearGeometry.FaceToSecondRatio(hours);
            diagStages = stages;
            if (auto)
            {
                diagSecs = ClockGearGeometry.SolveEqualCarrierSecScales(
                    stages, diagOuter, 1.0, s.ClockGearCarrierDirs,
                    Math.Max(0, s.ClockGearCarrierMinDegPerMin) / 360.0,
                    s.ClockGearCarrierPinned, s.ClockGearCarrierDegPerMin);
                for (var i = 0; i < diagSecs.Length && i < rates.Count; i++)
                    rates[i] = diagSecs[i] * 360.0;
            }
            else
            {
                diagSecs = new double[stages.Count];
                for (var i = 0; i < diagSecs.Length; i++)
                {
                    var dpm = i < rates.Count ? rates[i] : 0;
                    diagSecs[i] = Math.Abs(dpm) < 0.05 ? 0 : dpm / 360.0;
                }
            }
        }
        var compounds = (s.ClockGearCompounds ?? []).Where(n => n > 0).ToArray();
        var panels = new[]
        {
            ClockGearCarousel0, ClockGearCarousel1, ClockGearCarousel2, ClockGearCarousel3
        };
        var sliders = new[]
        {
            ClockGearCarrier0Slider, ClockGearCarrier1Slider,
            ClockGearCarrier2Slider, ClockGearCarrier3Slider
        };
        var labels = new[]
        {
            ClockGearCarrier0Label, ClockGearCarrier1Label,
            ClockGearCarrier2Label, ClockGearCarrier3Label
        };
        var dirBoxes = new[]
        {
            ClockGearCarrier0DirBox, ClockGearCarrier1DirBox,
            ClockGearCarrier2DirBox, ClockGearCarrier3DirBox
        };
        s.ClockGearCarrierDirs ??= [];
        while (s.ClockGearCarrierDirs.Count < 5)
            s.ClockGearCarrierDirs.Add(ClockGearCarrierDir.Auto);
        s.ClockGearCarrierPinned ??= [];
        while (s.ClockGearCarrierPinned.Count < 5)
            s.ClockGearCarrierPinned.Add(false);
        var unpinnedLive = 0;
        for (var i = 0; i < sliders.Length && i < compounds.Length; i++)
        {
            var dir = i < s.ClockGearCarrierDirs.Count
                ? s.ClockGearCarrierDirs[i] : ClockGearCarrierDir.Auto;
            if (s.ClockGearCarrierPinned[i] || dir == ClockGearCarrierDir.Still)
                continue;
            unpinnedLive++;
        }
        var pinBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED));
        var freeBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA8));
        var prev = _loading;
        _loading = true;
        try
        {
            if (ClockGearAutoCarrierBox != null)
                ClockGearAutoCarrierBox.IsChecked = auto;
            if (ClockGearCarrierMinSlider != null)
            {
                var min = Math.Clamp(s.ClockGearCarrierMinDegPerMin, 0, 360);
                s.ClockGearCarrierMinDegPerMin = min;
                ClockGearCarrierMinSlider.IsEnabled = auto;
                ClockGearCarrierMinSlider.Value = min;
                if (ClockGearCarrierMinLabel != null)
                    ClockGearCarrierMinLabel.Text = min < 0.5 ? "off" : FormatCarrierRate(min);
            }
            for (var i = 0; i < sliders.Length; i++)
            {
                var live = i < compounds.Length;
                var dir = i < s.ClockGearCarrierDirs.Count
                    ? s.ClockGearCarrierDirs[i] : ClockGearCarrierDir.Auto;
                var parked = live && (s.ClockGearCarrierPinned[i] || dir == ClockGearCarrierDir.Still);
                var leftover = auto && live && !parked && (compounds.Length <= 1 || unpinnedLive <= 1);
                if (panels[i] != null)
                    panels[i]!.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
                if (sliders[i] == null)
                    continue;
                var v = dir == ClockGearCarrierDir.Still ? 0 : rates[i];
                var span = Math.Max(720, Math.Ceiling(Math.Abs(v) + 1));
                sliders[i]!.Minimum = auto ? -span : -720;
                sliders[i]!.Maximum = auto ? span : 720;
                sliders[i]!.IsEnabled = live && (!auto || parked || !leftover);
                sliders[i]!.Value = auto ? v : Math.Clamp(Math.Round(v), -720, 720);
                if (labels[i] != null)
                {
                    labels[i]!.Text = FormatCarrierRate(auto ? v : sliders[i]!.Value);
                    labels[i]!.Foreground = auto && parked ? pinBrush : freeBrush;
                    labels[i]!.ToolTip = !auto ? null
                        : dir == ClockGearCarrierDir.Still ? "No direction (parked). Click to let Auto pick."
                        : parked ? "Locked speed. Click to let Auto pick."
                        : leftover ? "Computed from the sun lock."
                        : "Drag to lock this ring’s speed.";
                    labels[i]!.Cursor = auto && parked ? Cursors.Hand : Cursors.Arrow;
                }
                if (dirBoxes[i] != null)
                {
                    dirBoxes[i]!.Visibility = auto && live ? Visibility.Visible : Visibility.Collapsed;
                    dirBoxes[i]!.SelectedIndex = dir switch
                    {
                        ClockGearCarrierDir.Clockwise => 1,
                        ClockGearCarrierDir.Counterclockwise => 2,
                        ClockGearCarrierDir.Still => 3,
                        _ => 0
                    };
                    dirBoxes[i]!.IsEnabled = live && auto;
                }
            }
            if (ClockGearCarrierWarn != null)
            {
                var warn = diagStages != null && diagSecs != null
                    ? ClockGearGeometry.DiagnoseCarrierLocks(
                        diagStages, diagOuter, 1.0, diagSecs, auto,
                        s.ClockGearCarrierDirs,
                        Math.Max(0, s.ClockGearCarrierMinDegPerMin) / 360.0,
                        s.ClockGearCarrierPinned, s.ClockGearCarrierDegPerMin)
                    : null;
                ClockGearCarrierWarn.Text = warn ?? "";
                ClockGearCarrierWarn.Visibility = string.IsNullOrWhiteSpace(warn)
                    ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        finally
        {
            _loading = prev;
        }
    }

    static string FormatCarrierRate(double degPerMin)
    {
        if (Math.Abs(degPerMin) < 1e-6)
            return double.IsNegative(degPerMin) ? "−0°/m" : "+0°/m";
        if (Math.Abs(degPerMin) < 0.1)
            return $"{degPerMin:+0.###;-0.###}°/m";
        if (Math.Abs(degPerMin - Math.Round(degPerMin)) < 0.05)
            return $"{Math.Round(degPerMin):+0;-0}°/m";
        return $"{degPerMin:+0.0;-0.0}°/m";
    }

    void ClockGearAutoCarrierChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || ClockGearAutoCarrierBox == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        s.ClockGearAutoCarrier = ClockGearAutoCarrierBox.IsChecked == true;
        LoadClockGearCarrierRates(s);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    void ClockGearCarrierMinChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        var min = Math.Clamp(Math.Round(ClockGearCarrierMinSlider?.Value ?? 0), 0, 360);
        s.ClockGearCarrierMinDegPerMin = min;
        if (ClockGearCarrierMinLabel != null)
            ClockGearCarrierMinLabel.Text = min < 0.5 ? "off" : FormatCarrierRate(min);
        LoadClockGearCarrierRates(s);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    void ClockGearCarrierRateChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        var rates = s.ClockGearCarrierDegPerMin ?? [0.0, 0, 0, 0, 0];
        while (rates.Count < 5)
            rates.Add(0);
        s.ClockGearCarrierPinned ??= [];
        while (s.ClockGearCarrierPinned.Count < 5)
            s.ClockGearCarrierPinned.Add(false);
        var sliders = new[]
        {
            ClockGearCarrier0Slider, ClockGearCarrier1Slider,
            ClockGearCarrier2Slider, ClockGearCarrier3Slider
        };
        var labels = new[]
        {
            ClockGearCarrier0Label, ClockGearCarrier1Label,
            ClockGearCarrier2Label, ClockGearCarrier3Label
        };
        var idx = -1;
        for (var i = 0; i < sliders.Length; i++)
        {
            if (ReferenceEquals(sliders[i], sender))
                idx = i;
        }
        if (idx < 0)
            return;
        var v = Math.Clamp(Math.Round(sliders[idx]!.Value), -720, 720);
        if (Math.Abs(sliders[idx]!.Value - v) > 0.01)
            sliders[idx]!.Value = v;
        rates[idx] = v;
        s.ClockGearCarrierDegPerMin = rates;
        if (s.ClockGearAutoCarrier != false)
        {
            s.ClockGearCarrierDirs ??= [];
            while (s.ClockGearCarrierDirs.Count < 5)
                s.ClockGearCarrierDirs.Add(ClockGearCarrierDir.Auto);
            if (Math.Abs(v) < 0.5)
            {
                s.ClockGearCarrierDirs[idx] = ClockGearCarrierDir.Still;
                s.ClockGearCarrierPinned[idx] = false;
                rates[idx] = 0;
            }
            else
            {
                if (s.ClockGearCarrierDirs[idx] == ClockGearCarrierDir.Still)
                    s.ClockGearCarrierDirs[idx] = v < 0
                        ? ClockGearCarrierDir.Counterclockwise
                        : ClockGearCarrierDir.Clockwise;
                s.ClockGearCarrierPinned[idx] = true;
            }
            LoadClockGearCarrierRates(s);
        }
        else
        {
            for (var i = 0; i < sliders.Length; i++)
            {
                if (sliders[i] == null)
                    continue;
                var ri = Math.Clamp(Math.Round(sliders[i]!.Value), -720, 720);
                if (Math.Abs(sliders[i]!.Value - ri) > 0.01)
                    sliders[i]!.Value = ri;
                rates[i] = ri;
                if (labels[i] != null)
                    labels[i]!.Text = FormatCarrierRate(ri);
            }
        }
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    void ClockGearCarrierPinClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        if (s.ClockGearAutoCarrier == false)
            return;
        s.ClockGearCarrierPinned ??= [];
        while (s.ClockGearCarrierPinned.Count < 5)
            s.ClockGearCarrierPinned.Add(false);
        s.ClockGearCarrierDirs ??= [];
        while (s.ClockGearCarrierDirs.Count < 5)
            s.ClockGearCarrierDirs.Add(ClockGearCarrierDir.Auto);
        var labels = new[]
        {
            ClockGearCarrier0Label, ClockGearCarrier1Label,
            ClockGearCarrier2Label, ClockGearCarrier3Label
        };
        for (var i = 0; i < labels.Length; i++)
        {
            if (!ReferenceEquals(labels[i], sender))
                continue;
            var parked = s.ClockGearCarrierPinned[i]
                || (i < s.ClockGearCarrierDirs.Count
                    && s.ClockGearCarrierDirs[i] == ClockGearCarrierDir.Still);
            if (!parked)
                continue;
            s.ClockGearCarrierPinned[i] = false;
            if (i < s.ClockGearCarrierDirs.Count
                && s.ClockGearCarrierDirs[i] == ClockGearCarrierDir.Still)
                s.ClockGearCarrierDirs[i] = ClockGearCarrierDir.Auto;
            LoadClockGearCarrierRates(s);
            AppHost.Instance.SyncClockOverlay();
            AppHost.Instance.RequestSave();
            return;
        }
    }

    void ClockGearCarrierDirChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        if (s.ClockGearAutoCarrier == false)
            return;
        s.ClockGearCarrierDirs ??= [];
        while (s.ClockGearCarrierDirs.Count < 5)
            s.ClockGearCarrierDirs.Add(ClockGearCarrierDir.Auto);
        s.ClockGearCarrierPinned ??= [];
        while (s.ClockGearCarrierPinned.Count < 5)
            s.ClockGearCarrierPinned.Add(false);
        var rates = s.ClockGearCarrierDegPerMin ?? [0.0, 0, 0, 0, 0];
        while (rates.Count < 5)
            rates.Add(0);
        var boxes = new[]
        {
            ClockGearCarrier0DirBox, ClockGearCarrier1DirBox,
            ClockGearCarrier2DirBox, ClockGearCarrier3DirBox
        };
        for (var i = 0; i < boxes.Length; i++)
        {
            if (boxes[i] == null)
                continue;
            s.ClockGearCarrierDirs[i] = boxes[i]!.SelectedIndex switch
            {
                1 => ClockGearCarrierDir.Clockwise,
                2 => ClockGearCarrierDir.Counterclockwise,
                3 => ClockGearCarrierDir.Still,
                _ => ClockGearCarrierDir.Auto
            };
            if (s.ClockGearCarrierDirs[i] == ClockGearCarrierDir.Still)
            {
                rates[i] = 0;
                s.ClockGearCarrierPinned[i] = false;
            }
        }
        s.ClockGearCarrierDegPerMin = rates;
        LoadClockGearCarrierRates(s);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    void LoadClockGearCompoundScales(AppSettings s)
    {
        var scales = s.ClockGearCompoundScale ?? [1.0, 1, 1, 1, 1];
        while (scales.Count < 5)
            scales.Add(1);
        for (var i = 0; i < scales.Count; i++)
            scales[i] = scales[i] < 1.5 ? 1 : Math.Clamp(Math.Round(scales[i]), 1, 24);
        s.ClockGearCompoundScale = scales;
        var compounds = (s.ClockGearCompounds ?? []).Where(n => n > 0).ToArray();
        var panels = new[]
        {
            ClockGearSize0, ClockGearSize1, ClockGearSize2, ClockGearSize3
        };
        var sliders = new[]
        {
            ClockGearScale0Slider, ClockGearScale1Slider,
            ClockGearScale2Slider, ClockGearScale3Slider
        };
        var labels = new[]
        {
            ClockGearScale0Label, ClockGearScale1Label,
            ClockGearScale2Label, ClockGearScale3Label
        };

        var maxK = new int[4];
        Array.Fill(maxK, 1);
        if (ClockGearGeometry.TryGetProgression(s, out var prog)
            && ClockGearGeometry.TrySolveTrain(
                prog, GearRim(), s.ClockGearPlanetCounts ?? [], s.ClockGearPlanetOffsetsDeg ?? [],
                s.ClockGearIdlerOffsetsDeg ?? [], out var stages, out _))
        {
            for (var i = 0; i < maxK.Length && i < stages.Count; i++)
                maxK[i] = ClockGearGeometry.MaxCompoundMul(
                    stages[i].Layout, stages[i].Fit.Teeth, stages[i].PlanetCount,
                    stages[i].PlanetOffsetDeg);
        }

        var prev = _loading;
        _loading = true;
        try
        {
            for (var i = 0; i < sliders.Length; i++)
            {
                var live = i < compounds.Length;
                if (panels[i] != null)
                    panels[i]!.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
                if (sliders[i] == null)
                    continue;
                var cap = Math.Max(1, maxK[i]);
                sliders[i]!.Minimum = 1;
                sliders[i]!.Maximum = cap;
                sliders[i]!.IsEnabled = cap > 1;
                sliders[i]!.Visibility = cap > 1 ? Visibility.Visible : Visibility.Collapsed;
                var k = (int)Math.Clamp(Math.Round(scales[i]), 1, cap);
                scales[i] = k;
                sliders[i]!.Value = k;
                if (labels[i] != null)
                {
                    var nc = live ? compounds[i] * k : k;
                    var ni = 6 * k;
                    labels[i]!.Text = cap > 1 ? $"{k}×  {nc}T/{ni}T" : "1×";
                }
            }
        }
        finally
        {
            _loading = prev;
        }
        s.ClockGearCompoundScale = scales;
    }

    void ClockGearCompoundScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        var scales = s.ClockGearCompoundScale ?? [1.0, 1, 1, 1, 1];
        while (scales.Count < 5)
            scales.Add(1);
        var compounds = (s.ClockGearCompounds ?? []).Where(n => n > 0).ToArray();
        var sliders = new[]
        {
            ClockGearScale0Slider, ClockGearScale1Slider,
            ClockGearScale2Slider, ClockGearScale3Slider
        };
        var labels = new[]
        {
            ClockGearScale0Label, ClockGearScale1Label,
            ClockGearScale2Label, ClockGearScale3Label
        };
        for (var i = 0; i < sliders.Length; i++)
        {
            if (sliders[i] == null)
                continue;
            var cap = Math.Max(1, sliders[i]!.Maximum);
            var k = (int)Math.Clamp(Math.Round(sliders[i]!.Value), 1, cap);
            if (Math.Abs(sliders[i]!.Value - k) > 0.01)
                sliders[i]!.Value = k;
            scales[i] = k;
            if (labels[i] != null)
            {
                var nc = i < compounds.Length ? compounds[i] * k : k;
                labels[i]!.Text = $"{k}×  {nc}T/{6 * k}T";
            }
        }
        s.ClockGearCompoundScale = scales;
        LoadClockGearCarrierRates(s);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void LoadClockGearPlanetCounts(List<int>? counts) =>
        RefreshPlanetSliders(AppHost.Instance.Data.Settings);

    void CapPlanetSliders(int maxPlanets)
    {
        _ = maxPlanets;
    }

    void RefreshPlanetSliders(AppSettings s)
    {
        if (ClockGearFiddlePanel == null)
            return;
        var compounds = (s.ClockGearCompounds ?? []).Where(n => n > 0).ToArray();
        var on = s.ClockGearDriveMode != ClockGearDriveMode.Off && compounds.Length >= 2;
        ClockGearFiddlePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on)
            return;

        s.ClockGearPlanetCounts = PadPlanetCounts(s.ClockGearPlanetCounts);
        var sliders = new[]
        {
            ClockGearPlanetsGap0Slider, ClockGearPlanetsGap1Slider,
            ClockGearPlanetsGap2Slider, ClockGearPlanetsGap3Slider
        };
        var labels = new[]
        {
            ClockGearPlanetsGap0Label, ClockGearPlanetsGap1Label,
            ClockGearPlanetsGap2Label, ClockGearPlanetsGap3Label
        };
        var stages = new[]
        {
            ClockGearPlanetStage0, ClockGearPlanetStage1,
            ClockGearPlanetStage2, ClockGearPlanetStage3
        };

        var prev = _loading;
        _loading = true;
        try
        {
            var rim = GearRim();
            var current = s.ClockGearPlanetCounts;
            for (var i = 0; i < sliders.Length; i++)
            {
                var live = i < compounds.Length;
                if (stages[i] != null)
                    stages[i]!.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
                if (!live)
                    continue;
                var ticks = ClockGearGeometry.ValidStagePlanetCounts(compounds, rim, current, i);
                if (ticks.Count == 0)
                    ticks = [current[i]];
                var only = ticks.Count < 2;
                if (sliders[i] != null)
                {
                    sliders[i]!.Visibility = only ? Visibility.Collapsed : Visibility.Visible;
                    sliders[i]!.IsEnabled = !only;
                }
                if (only)
                {
                    current[i] = ticks[0];
                    if (labels[i] != null)
                        labels[i]!.Text = $"{ticks[0]}";
                    continue;
                }
                ApplyPlanetTicks(sliders[i], labels[i], ticks, current[i]);
                current[i] = (int)Math.Round(sliders[i]!.Value);
            }
        }
        finally
        {
            _loading = prev;
        }
        LoadClockGearCarrierRates(s);
        LoadClockGearCompoundScales(s);
        LoadClockGearCarriageStyle(s);
    }

    static double GearRim() => Math.Max(28, AppHost.Instance.ClockFaceRim);

    void ApplyGearSolve(AppSettings s)
    {
        EnsureIdlerOffsets(s);
        s.ClockGearPlanetCounts ??= [1, 1, 1, 1, 1];
        s.ClockGearPlanetOffsetsDeg ??= [0, 0, 0, 0, 0];
        s.ClockGearCarrierDegPerMin ??= [0, 0, 0, 0, 0];
        s.ClockGearCarrierDirs ??= [];
        s.ClockGearCarrierPinned ??= [];
        s.ClockGearCompoundScale ??= [1, 1, 1, 1, 1];
        while (s.ClockGearPlanetCounts.Count < 5)
            s.ClockGearPlanetCounts.Add(s.ClockGearPlanetCounts[^1]);
        while (s.ClockGearPlanetOffsetsDeg.Count < 5)
            s.ClockGearPlanetOffsetsDeg.Add(0);
        while (s.ClockGearCarrierDegPerMin.Count < 5)
            s.ClockGearCarrierDegPerMin.Add(0);
        while (s.ClockGearCarrierDirs.Count < 5)
            s.ClockGearCarrierDirs.Add(ClockGearCarrierDir.Auto);
        while (s.ClockGearCarrierPinned.Count < 5)
            s.ClockGearCarrierPinned.Add(false);
        while (s.ClockGearCompoundScale.Count < 5)
            s.ClockGearCompoundScale.Add(1);

        var prev = _loading;
        _loading = true;
        try
        {
            if (!ClockGearGeometry.TryGetProgression(s.ClockGearDriveMode, out var prog))
            {
                SetStageSliderEnabled(0, false);
                return;
            }

            var rim = GearRim();
            if (!ClockGearGeometry.TrySolveTrain(
                    prog, rim, s.ClockGearPlanetCounts, s.ClockGearPlanetOffsetsDeg,
                    s.ClockGearIdlerOffsetsDeg, out var stages, out _)
                || stages.Count == 0)
            {
                SetStageSliderEnabled(0, false);
                return;
            }

            for (var i = 0; i < 5; i++)
            {
                if (i < stages.Count)
                {
                    s.ClockGearPlanetCounts[i] = stages[i].PlanetCount;
                    s.ClockGearPlanetOffsetsDeg[i] = stages[i].PlanetOffsetDeg;
                    if (i < s.ClockGearIdlerOffsetsDeg.Count)
                        s.ClockGearIdlerOffsetsDeg[i] = stages[i].IdlerOffsetDeg;
                }
                else
                {
                    s.ClockGearPlanetCounts[i] = stages[^1].PlanetCount;
                }
            }
            s.ClockGearIdlerOffsetDeg = s.ClockGearIdlerOffsetsDeg[0];

            var planetSliders = new[]
            {
                ClockGearPlanetsGap0Slider, ClockGearPlanetsGap1Slider,
                ClockGearPlanetsGap2Slider, ClockGearPlanetsGap3Slider
            };
            var planetLabels = new[]
            {
                ClockGearPlanetsGap0Label, ClockGearPlanetsGap1Label,
                ClockGearPlanetsGap2Label, ClockGearPlanetsGap3Label
            };
            var offsetSliders = new[]
            {
                ClockGearPlanetOffset0Slider, ClockGearPlanetOffset1Slider,
                ClockGearPlanetOffset2Slider, ClockGearPlanetOffset3Slider
            };
            var offsetLabels = new[]
            {
                ClockGearPlanetOffset0Label, ClockGearPlanetOffset1Label,
                ClockGearPlanetOffset2Label, ClockGearPlanetOffset3Label
            };
            var idlerSliders = new[]
            {
                ClockGearIdlerOffsetSlider, ClockGearIdlerOffset1Slider,
                ClockGearIdlerOffset2Slider, ClockGearIdlerOffset3Slider
            };
            var idlerLabels = new[]
            {
                ClockGearIdlerOffsetLabel, ClockGearIdlerOffset1Label,
                ClockGearIdlerOffset2Label, ClockGearIdlerOffset3Label
            };

            for (var i = 0; i < planetSliders.Length; i++)
            {
                var live = i < prog.StageCount;
                if (planetSliders[i] != null)
                    planetSliders[i]!.IsEnabled = live;
                if (offsetSliders[i] != null)
                    offsetSliders[i]!.IsEnabled = live;
                if (!live)
                    continue;
                var ticks = ClockGearGeometry.CompletingPlanetCounts(
                    prog, rim, i, s.ClockGearPlanetCounts, s.ClockGearPlanetOffsetsDeg,
                    s.ClockGearIdlerOffsetsDeg);
                if (ticks.Count == 0)
                    ticks = [stages[Math.Min(i, stages.Count - 1)].PlanetCount];
                ApplyPlanetTicks(planetSliders[i], planetLabels[i], ticks, s.ClockGearPlanetCounts[i]);
                var period = ClockGearGeometry.PlanetOffsetPeriodDeg(s.ClockGearPlanetCounts[i]);
                if (offsetSliders[i] != null)
                {
                    offsetSliders[i]!.Maximum = period;
                    offsetSliders[i]!.Value = ClockGearGeometry.WrapOffsetDeg(
                        s.ClockGearPlanetOffsetsDeg[i], period);
                }
                if (offsetLabels[i] != null)
                    offsetLabels[i]!.Text = $"{s.ClockGearPlanetOffsetsDeg[i]:0}°";
            }

            for (var i = 0; i < idlerSliders.Length; i++)
            {
                var live = i < prog.StageCount && i < stages.Count;
                if (idlerSliders[i] != null)
                    idlerSliders[i]!.IsEnabled = live;
                if (!live)
                    continue;
                var st = stages[i];
                int? lockSun = i == prog.StageCount - 1 ? prog.LockedSunAt(i) : null;
                ApplyIdlerOffsetSlider(
                    idlerSliders[i], idlerLabels[i], st.IdlerOffsetDeg,
                    st.PlanetCount, st.PlanetOffsetDeg, st.Layout, lockSun);
            }
        }
        finally
        {
            _loading = prev;
        }
        LoadClockGearCarrierRates(s);
    }

    void SetStageSliderEnabled(int fromStage, bool enabled)
    {
        foreach (var sl in new[]
                 {
                     ClockGearPlanetsGap0Slider, ClockGearPlanetsGap1Slider,
                     ClockGearPlanetsGap2Slider, ClockGearPlanetsGap3Slider,
                     ClockGearPlanetOffset0Slider, ClockGearPlanetOffset1Slider,
                     ClockGearPlanetOffset2Slider, ClockGearPlanetOffset3Slider,
                     ClockGearIdlerOffsetSlider, ClockGearIdlerOffset1Slider,
                     ClockGearIdlerOffset2Slider, ClockGearIdlerOffset3Slider
                 })
        {
            if (sl != null)
                sl.IsEnabled = enabled;
        }
        _ = fromStage;
    }

    static void ApplyPlanetTicks(Slider? slider, TextBlock? label, List<int> ticks, int value)
    {
        if (slider == null)
            return;
        slider.Minimum = ticks.Min();
        slider.Maximum = ticks.Max();
        // Only assembled counts are seats (not every integer between min and max).
        slider.TickFrequency = Math.Max(1, ticks.Max() - ticks.Min() + 1);
        slider.Ticks = new DoubleCollection(ticks.Select(t => (double)t));
        slider.IsSnapToTickEnabled = true;
        var snapped = ticks.Contains(value)
            ? value
            : ticks.OrderBy(t => Math.Abs(t - value)).First();
        if (Math.Abs(slider.Value - snapped) > 0.01)
            slider.Value = snapped;
        if (label != null)
            label.Text = $"{snapped}";
    }

    static void LoadPlanetGap(Slider? slider, TextBlock? label, List<int>? counts, int index)
    {
        if (slider == null)
            return;
        var v = counts != null && index < counts.Count ? counts[index] : 3;
        v = Math.Clamp(v <= 0 ? 3 : v, 1, 12);
        slider.Value = v;
        if (label != null)
            label.Text = $"{v}";
    }

    static int ReadPlanetGap(Slider? slider, TextBlock? label)
    {
        var v = slider == null ? 3 : (int)Math.Round(Math.Clamp(slider.Value, 1, 12));
        if (slider != null && Math.Abs(slider.Value - v) > 0.01)
            slider.Value = v;
        if (label != null)
            label.Text = $"{v}";
        return v;
    }

    // auto model
    void LoadClockGearPlanetOffsets(List<double>? offsets)
    {
        var counts = AppHost.Instance.Data.Settings.ClockGearPlanetCounts;
        LoadPlanetOffset(ClockGearPlanetOffset0Slider, ClockGearPlanetOffset0Label, offsets, counts, 0);
        LoadPlanetOffset(ClockGearPlanetOffset1Slider, ClockGearPlanetOffset1Label, offsets, counts, 1);
        LoadPlanetOffset(ClockGearPlanetOffset2Slider, ClockGearPlanetOffset2Label, offsets, counts, 2);
        LoadPlanetOffset(ClockGearPlanetOffset3Slider, ClockGearPlanetOffset3Label, offsets, counts, 3);
    }

    static void LoadPlanetOffset(
        Slider? slider, TextBlock? label, List<double>? offsets, List<int>? counts, int index)
    {
        if (slider == null)
            return;
        var count = counts != null && index < counts.Count ? Math.Clamp(counts[index], 1, 12) : 3;
        var period = ClockGearGeometry.PlanetOffsetPeriodDeg(count);
        slider.Maximum = period;
        var v = offsets != null && index < offsets.Count ? offsets[index] : 0;
        v = ClockGearGeometry.WrapOffsetDeg(v, period);
        slider.Value = v;
        if (label != null)
            label.Text = $"{v:0}°";
    }

    // auto model
    void ClockGearPlanetCountChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        var compounds = (s.ClockGearCompounds ?? []).Where(n => n > 0).ToArray();
        var counts = PadPlanetCounts(s.ClockGearPlanetCounts);
        var sliders = new[]
        {
            ClockGearPlanetsGap0Slider, ClockGearPlanetsGap1Slider,
            ClockGearPlanetsGap2Slider, ClockGearPlanetsGap3Slider
        };
        var labels = new[]
        {
            ClockGearPlanetsGap0Label, ClockGearPlanetsGap1Label,
            ClockGearPlanetsGap2Label, ClockGearPlanetsGap3Label
        };
        for (var i = 0; i < sliders.Length && i < compounds.Length; i++)
        {
            if (sliders[i]?.Visibility == Visibility.Visible)
                counts[i] = ReadPlanetGap(sliders[i], labels[i]);
        }

        var rim = GearRim();
        if (sender is Slider moved)
        {
            var stage = Array.IndexOf(sliders, moved);
            if (stage >= 0 && stage < compounds.Length)
            {
                var ticks = ClockGearGeometry.ValidStagePlanetCounts(compounds, rim, counts, stage);
                if (ticks.Count > 0 && !ticks.Contains(counts[stage]))
                    counts[stage] = ticks.OrderBy(t => Math.Abs(t - counts[stage])).First();
            }
        }

        s.ClockGearPlanetCounts = counts;
        RefreshPlanetSliders(s);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    static double SyncOffsetSlider(
        Slider? slider, TextBlock? label, int planetCount, List<double>? offsets, int index)
    {
        var period = ClockGearGeometry.PlanetOffsetPeriodDeg(planetCount);
        var current = offsets != null && index < offsets.Count ? offsets[index] : 0;
        var v = ClockGearGeometry.WrapOffsetDeg(current, period);
        if (slider != null)
        {
            slider.Maximum = period;
            if (Math.Abs(slider.Value - v) > 0.01)
                slider.Value = v;
        }
        if (label != null)
            label.Text = $"{v:0}°";
        return v;
    }

    // auto model
    void ClockGearPlanetOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        var counts = s.ClockGearPlanetCounts;
        s.ClockGearPlanetOffsetsDeg =
        [
            ReadPlanetOffset(ClockGearPlanetOffset0Slider, ClockGearPlanetOffset0Label, CountAt(counts, 0)),
            ReadPlanetOffset(ClockGearPlanetOffset1Slider, ClockGearPlanetOffset1Label, CountAt(counts, 1)),
            ReadPlanetOffset(ClockGearPlanetOffset2Slider, ClockGearPlanetOffset2Label, CountAt(counts, 2)),
            ReadPlanetOffset(ClockGearPlanetOffset3Slider, ClockGearPlanetOffset3Label, CountAt(counts, 3))
        ];
        s.ClockGearIdlersPerCompound = 1;
        ResnapAllIdlerOffsets(s);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    static int CountAt(List<int>? counts, int index) =>
        counts != null && index < counts.Count ? Math.Clamp(counts[index], 1, 12) : 3;

    static double ReadPlanetOffset(Slider? slider, TextBlock? label, int planetCount)
    {
        var period = ClockGearGeometry.PlanetOffsetPeriodDeg(planetCount);
        var v = slider == null ? 0 : ClockGearGeometry.WrapOffsetDeg(Math.Round(slider.Value), period);
        if (slider != null)
        {
            if (Math.Abs(slider.Maximum - period) > 0.01)
                slider.Maximum = period;
            if (Math.Abs(slider.Value - v) > 0.01)
                slider.Value = v;
        }
        if (label != null)
            label.Text = $"{v:0}°";
        return v;
    }

    // auto model
    void LoadClockGearIdlers(List<double>? offsets)
    {
        EnsureIdlerOffsets(AppHost.Instance.Data.Settings);
        var s = AppHost.Instance.Data.Settings;
        ResnapAllIdlerOffsets(s);
    }

    static void EnsureIdlerOffsets(AppSettings s)
    {
        s.ClockGearIdlerOffsetsDeg ??= [0, 0, 0, 0, 0];
        while (s.ClockGearIdlerOffsetsDeg.Count < 5)
            s.ClockGearIdlerOffsetsDeg.Add(0);
    }

    void ResnapAllIdlerOffsets(AppSettings s) => ApplyGearSolve(s);

    static int PlanetCountGap0()
    {
        var counts = AppHost.Instance.Data.Settings.ClockGearPlanetCounts;
        return counts is { Count: > 0 } ? Math.Clamp(counts[0], 1, 12) : 3;
    }

    static double PlanetOffsetGap0()
    {
        var offs = AppHost.Instance.Data.Settings.ClockGearPlanetOffsetsDeg;
        var count = PlanetCountGap0();
        var raw = offs is { Count: > 0 } ? offs[0] : 0;
        return ClockGearGeometry.WrapOffsetDeg(raw, ClockGearGeometry.PlanetOffsetPeriodDeg(count));
    }

    void ApplyIdlerOffsetSlider(
        Slider? slider, TextBlock? label, double offsetDeg, int planetCount, double planetOffsetDeg,
        ClockGearGeometry.FaceMeshLayout? layout = null, int? lockSunTeeth = null)
    {
        if (slider == null)
            return;
        var lay = layout ?? ClockGearGeometry.LayoutFaceMesh(120);
        var ticks = ClockGearGeometry.ExactIdlerOffsetDegs(lay, planetCount, planetOffsetDeg, lockSunTeeth);
        slider.Minimum = ClockGearGeometry.IdlerOffsetMinDeg;
        slider.Maximum = ClockGearGeometry.IdlerOffsetMaxDeg;
        slider.Ticks = ticks.Count > 0 ? new DoubleCollection(ticks) : new DoubleCollection();
        if (ticks.Count == 0)
        {
            if (label != null)
            {
                var want = lockSunTeeth is int n ? $"{n}T " : "";
                label.Text = $"{want}no valid seat";
            }
            return;
        }
        offsetDeg = ClockGearGeometry.SnapIdlerOffsetDeg(
            lay, offsetDeg, planetCount, planetOffsetDeg, lockSunTeeth);
        if (Math.Abs(slider.Value - offsetDeg) > 0.001)
            slider.Value = offsetDeg;
        if (label != null)
            label.Text = FormatIdlerOffsetLabel(lay, offsetDeg, planetCount, planetOffsetDeg, lockSunTeeth);
    }

    static string FormatIdlerOffsetLabel(
        ClockGearGeometry.FaceMeshLayout layout,
        double offsetDeg, int planetCount, double planetOffsetDeg, int? lockSunTeeth = null)
    {
        var fit = ClockGearGeometry.FitInnerRing(
            layout, planetCount, planetOffsetDeg, offsetDeg, lockSunTeeth);
        var mark = fit.AssemblyOk ? "OK" : "BAD";
        return $"{offsetDeg:+0.#;-0.#;0}° → {fit.Teeth}T {mark}";
    }

    // auto model
    void ClockGearIdlerOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ClockGearIdlerOffsetSlider == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        EnsureIdlerOffsets(s);
        s.ClockGearIdlersPerCompound = 1;
        s.ClockGearIdlerOffsetsDeg[0] = ClockGearGeometry.ClampIdlerOffsetDeg(ClockGearIdlerOffsetSlider.Value);
        s.ClockGearIdlerOffsetDeg = s.ClockGearIdlerOffsetsDeg[0];
        ResnapAllIdlerOffsets(s);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockGearIdlerOffset1Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ClockGearIdlerOffset1Slider == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        EnsureIdlerOffsets(s);
        s.ClockGearIdlersPerCompound = 1;
        s.ClockGearIdlerOffsetsDeg[1] = ClockGearGeometry.ClampIdlerOffsetDeg(ClockGearIdlerOffset1Slider.Value);
        ResnapAllIdlerOffsets(s);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockGearIdlerOffset2Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ClockGearIdlerOffset2Slider == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        EnsureIdlerOffsets(s);
        s.ClockGearIdlersPerCompound = 1;
        s.ClockGearIdlerOffsetsDeg[2] = ClockGearGeometry.ClampIdlerOffsetDeg(ClockGearIdlerOffset2Slider.Value);
        ResnapAllIdlerOffsets(s);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockGearIdlerOffset3Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ClockGearIdlerOffset3Slider == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        EnsureIdlerOffsets(s);
        s.ClockGearIdlersPerCompound = 1;
        s.ClockGearIdlerOffsetsDeg[3] = ClockGearGeometry.ClampIdlerOffsetDeg(ClockGearIdlerOffset3Slider.Value);
        ResnapAllIdlerOffsets(s);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockTimeOutlineStrengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ClockTimeOutlineStrengthSlider == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        s.ClockTimeOutlineStrength = (int)Math.Round(ClockTimeOutlineStrengthSlider.Value);
        if (ClockTimeOutlineStrengthLabel != null)
            ClockTimeOutlineStrengthLabel.Text = $"{s.ClockTimeOutlineStrength}";
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    static void LoadClockTickLength(Slider? slider, TextBlock? label, double value)
    {
        if (slider == null)
            return;
        var snapped = SnapClockUiPercent(value, slider.Minimum, slider.Maximum);
        slider.Value = snapped;
        if (label != null)
            label.Text = $"{snapped * 100:0}%";
    }

    static double SnapClockUiPercent(double value, double min, double max)
    {
        var lo = (int)Math.Round(min * 100);
        var hi = (int)Math.Round(max * 100);
        var pct = (int)Math.Round(Math.Clamp(value, min, max) * 100);
        pct = Math.Clamp(pct, lo, hi);
        return pct / 100.0;
    }

    // auto model
    void ClockTickLengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        if (ClockTickLenHourSlider != null)
        {
            s.ClockTickLengthHour = SnapClockUiPercent(ClockTickLenHourSlider.Value, 0.01, 0.50);
            if (Math.Abs(ClockTickLenHourSlider.Value - s.ClockTickLengthHour) > 0.0001)
                ClockTickLenHourSlider.Value = s.ClockTickLengthHour;
            if (ClockTickLenHourLabel != null)
                ClockTickLenHourLabel.Text = $"{s.ClockTickLengthHour * 100:0}%";
        }
        if (ClockTickLenThirtySlider != null)
        {
            s.ClockTickLengthThirty = SnapClockUiPercent(ClockTickLenThirtySlider.Value, 0.01, 0.50);
            if (Math.Abs(ClockTickLenThirtySlider.Value - s.ClockTickLengthThirty) > 0.0001)
                ClockTickLenThirtySlider.Value = s.ClockTickLengthThirty;
            if (ClockTickLenThirtyLabel != null)
                ClockTickLenThirtyLabel.Text = $"{s.ClockTickLengthThirty * 100:0}%";
        }
        if (ClockTickLenFifteenSlider != null)
        {
            s.ClockTickLengthFifteen = SnapClockUiPercent(ClockTickLenFifteenSlider.Value, 0.01, 0.50);
            if (Math.Abs(ClockTickLenFifteenSlider.Value - s.ClockTickLengthFifteen) > 0.0001)
                ClockTickLenFifteenSlider.Value = s.ClockTickLengthFifteen;
            if (ClockTickLenFifteenLabel != null)
                ClockTickLenFifteenLabel.Text = $"{s.ClockTickLengthFifteen * 100:0}%";
        }
        if (ClockTickLenMinuteSlider != null)
        {
            s.ClockTickLengthMinute = SnapClockUiPercent(ClockTickLenMinuteSlider.Value, 0.01, 0.50);
            if (Math.Abs(ClockTickLenMinuteSlider.Value - s.ClockTickLengthMinute) > 0.0001)
                ClockTickLenMinuteSlider.Value = s.ClockTickLengthMinute;
            if (ClockTickLenMinuteLabel != null)
                ClockTickLenMinuteLabel.Text = $"{s.ClockTickLengthMinute * 100:0}%";
        }
        if (ClockSecondHandLenSlider != null)
        {
            s.ClockSecondHandLength = SnapClockUiPercent(ClockSecondHandLenSlider.Value, 0.01, 0.95);
            if (Math.Abs(ClockSecondHandLenSlider.Value - s.ClockSecondHandLength) > 0.0001)
                ClockSecondHandLenSlider.Value = s.ClockSecondHandLength;
            if (ClockSecondHandLenLabel != null)
                ClockSecondHandLenLabel.Text = $"{s.ClockSecondHandLength * 100:0}%";
        }
        if (ClockHourHandLenSlider != null)
        {
            s.ClockHourHandLength = SnapClockUiPercent(ClockHourHandLenSlider.Value, 0.01, 0.95);
            if (Math.Abs(ClockHourHandLenSlider.Value - s.ClockHourHandLength) > 0.0001)
                ClockHourHandLenSlider.Value = s.ClockHourHandLength;
            if (ClockHourHandLenLabel != null)
                ClockHourHandLenLabel.Text = $"{s.ClockHourHandLength * 100:0}%";
        }
        if (ClockWedgeWidthSlider != null)
        {
            s.ClockWedgeWidth = SnapClockUiPercent(ClockWedgeWidthSlider.Value, 0.01, 0.15);
            if (Math.Abs(ClockWedgeWidthSlider.Value - s.ClockWedgeWidth) > 0.0001)
                ClockWedgeWidthSlider.Value = s.ClockWedgeWidth;
            if (ClockWedgeWidthLabel != null)
                ClockWedgeWidthLabel.Text = $"{s.ClockWedgeWidth * 100:0}%";
        }
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockTimePosChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ClockTimePosSlider == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        s.ClockTimePosition = Math.Clamp(ClockTimePosSlider.Value, 0, 1);
        if (ClockTimePosLabel != null)
            ClockTimePosLabel.Text = $"{s.ClockTimePosition * 100:0}%";
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockTimeFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ClockTimeFontSizeSlider == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        s.ClockTimeFontSize = Math.Clamp(Math.Round(ClockTimeFontSizeSlider.Value), 8, 48);
        if (ClockTimeFontSizeLabel != null)
            ClockTimeFontSizeLabel.Text = $"{s.ClockTimeFontSize:0}";
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockAllCategoriesChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || ClockAllCategoriesBox == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        s.ClockAllCategories = ClockAllCategoriesBox.IsChecked == true;
        if (ClockCategoryList != null)
            ClockCategoryList.IsEnabled = !s.ClockAllCategories;
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockCategoryPickChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || ClockCategoryList?.ItemsSource is not IEnumerable<ClockCategoryPick> rows)
            return;
        var s = AppHost.Instance.Data.Settings;
        s.ClockCategoryIds = rows.Where(r => r.IsChecked).Select(r => r.Id).ToList();
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockHorizonUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ClockHorizonBox == null || ClockHorizonUnitBox == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        var hours = s.ClockHorizonHours <= 0 ? 24 : s.ClockHorizonHours;
        s.ClockHorizonInHours = ClockHorizonUnitBox.SelectedIndex == 0;
        var amount = s.ClockHorizonInHours ? hours : hours / 24.0;
        ClockHorizonBox.Text = amount.ToString("0.###", CultureInfo.CurrentCulture);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ClockHorizonCommit(object sender, RoutedEventArgs e) => CommitClockHorizon(true);

    // auto model
    void ClockHorizonKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitClockHorizon(true);
            e.Handled = true;
        }
    }

    // auto model
    void CommitClockHorizon(bool rewrite)
    {
        if (_loading || ClockHorizonBox == null || ClockHorizonUnitBox == null)
            return;
        var s = AppHost.Instance.Data.Settings;
        s.ClockHorizonInHours = ClockHorizonUnitBox.SelectedIndex == 0;
        double amount;
        if (s.ClockHorizonInHours)
            amount = ParseDouble(ClockHorizonBox.Text, 24, 1, 14 * 24);
        else
            amount = ParseDouble(ClockHorizonBox.Text, 1, 1.0 / 24, 14);
        s.ClockHorizonHours = s.ClockHorizonInHours ? amount : amount * 24;
        if (rewrite)
            ClockHorizonBox.Text = (s.ClockHorizonInHours ? s.ClockHorizonHours : s.ClockHorizonHours / 24.0)
                .ToString("0.###", CultureInfo.CurrentCulture);
        AppHost.Instance.SyncClockOverlay();
        AppHost.Instance.RequestSave();
    }

    void TaskFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TaskList == null)
            return;
        BindLists();
    }

    void Flush() => AppHost.Instance.Persist();

    void FlushOverlay() => AppHost.Instance.RequestSave();

    void CommitEdit(object sender)
    {
        if (sender is CheckBox)
            FlushOverlay();
        else
            Flush();
    }

    void AddTask(object sender, RoutedEventArgs e)
    {
        AppHost.Instance.OpenAddEntry(CurrentFolderId(), BindLists);
    }

    void LayoutAddItem(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayoutNode { Kind: "category", Id: var id } })
            AppHost.Instance.OpenAddEntry(id, BindLists);
    }

    void LayoutAddFolder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayoutNode { Kind: "category", Id: var id } })
            AppHost.Instance.OpenAddFolder(id, BindLists);
    }

    void DeleteTask(object sender, RoutedEventArgs e)
    {
        var task = SelectedTask();
        if (task == null)
            return;
        var parent = CurrentFolderId();
        var data = AppHost.Instance.Data;
        switch (task.Kind)
        {
            case "once":
                data.Once.RemoveAll(t => t.Id == task.Id);
                break;
            case "todo":
                data.Todos.RemoveAll(t => t.Id == task.Id);
                break;
            case "event":
                data.Events.RemoveAll(t => t.Id == task.Id);
                break;
            case "note":
                data.Notes.RemoveAll(t => t.Id == task.Id);
                break;
        }

        AppHost.Instance.UntrackTask(task.Kind, task.Id);
        if (parent.Length == 0)
        {
            _layoutKind = null;
            _layoutId = null;
        }
        else
        {
            _layoutKind = "category";
            _layoutId = parent;
        }
        BindLists();
        if (FindLayout(_layoutKind, _layoutId) is LayoutNode node)
            ApplyLayoutEditor(node);
        else
        {
            ParkEditors();
            ShowTaskEditor(null);
            ShowLayoutHint("Select a task or folder to edit it.");
        }
        FlushOverlay();
    }

    void MoveTaskUp(object sender, RoutedEventArgs e) => MoveCurrentTask(-1);

    void MoveTaskDown(object sender, RoutedEventArgs e) => MoveCurrentTask(1);

    void MoveCurrentTask(int delta)
    {
        var task = SelectedTask();
        if (task == null || TaskList.ItemsSource is not List<ConfigTask> rows)
            return;
        var index = rows.FindIndex(r => r.Kind == task.Kind && r.Id == task.Id);
        var next = index + delta;
        if (index < 0 || next < 0 || next >= rows.Count)
            return;
        var other = rows[next];
        AppHost.Instance.SwapTaskOrder(task.Kind, task.Id, other.Kind, other.Id);
        if (task.Kind == other.Kind)
        {
            var data = AppHost.Instance.Data;
            switch (task.Kind)
            {
                case "once":
                    SwapById(data.Once, t => t.Id, task.Id, other.Id);
                    break;
                case "todo":
                    SwapById(data.Todos, t => t.Id, task.Id, other.Id);
                    break;
                case "event":
                    SwapById(data.Events, t => t.Id, task.Id, other.Id);
                    break;
                case "note":
                    SwapById(data.Notes, t => t.Id, task.Id, other.Id);
                    break;
            }
        }

        BindLists();
        SelectTask(task.Kind, task.Id);
        FlushOverlay();
    }

    static void SwapById<T>(List<T> list, Func<T, string> idOf, string a, string b)
    {
        var ia = list.FindIndex(t => idOf(t) == a);
        var ib = list.FindIndex(t => idOf(t) == b);
        if (ia < 0 || ib < 0)
            return;
        (list[ia], list[ib]) = (list[ib], list[ia]);
    }

    static bool MoveById<T>(List<T> list, Func<T, string> idOf, string id, int delta)
    {
        var index = list.FindIndex(t => idOf(t) == id);
        var next = index + delta;
        if (index < 0 || next < 0 || next >= list.Count)
            return false;
        (list[index], list[next]) = (list[next], list[index]);
        return true;
    }

    void BindCompleted()
    {
        if (CompletedList == null)
            return;
        var keep = CompletedList.SelectedItem as CompletedRow;
        var filter = CompletedTypeBox?.SelectedIndex ?? 0;
        var data = AppHost.Instance.Data;
        var rows = new List<CompletedRow>();
        if (filter is 0 or 1)
        {
            foreach (var item in data.Once)
            {
                if (item.CompletedAt is DateTime at)
                    rows.Add(CompletedFrom("once", item.Id, item.Title, "To Do", item.CategoryId, at));
            }
        }
        if (filter is 0 or 2)
        {
            foreach (var item in data.Todos)
            {
                if (item.CompletedAt is DateTime at)
                    rows.Add(CompletedFrom("todo", item.Id, item.Title, "Recurring", item.CategoryId, at));
            }
        }
        if (filter is 0 or 3)
        {
            foreach (var item in data.Events)
            {
                if (item.CompletedAt is DateTime at)
                    rows.Add(CompletedFrom("event", item.Id, item.Title, "Scheduled", item.CategoryId, at));
            }
        }
        rows.Sort((a, b) => b.CompletedAt.CompareTo(a.CompletedAt));
        CompletedList.ItemsSource = rows;
        CompletedRow? next = null;
        if (keep != null)
            next = rows.FirstOrDefault(r => r.Kind == keep.Kind && r.Id == keep.Id);
        next ??= rows.Count > 0 ? rows[0] : null;
        CompletedList.SelectedItem = next;
        if (next == null)
            LoadCompletedEditor();
        var empty = rows.Count == 0;
        if (CompletedHint != null)
            CompletedHint.Visibility = Vis(empty);
        if (CompletedEditor != null)
            CompletedEditor.Visibility = Vis(!empty);
    }

    CompletedRow CompletedFrom(string kind, string id, string title, string type, string categoryId, DateTime at)
    {
        var folder = "";
        if (!string.IsNullOrEmpty(categoryId))
        {
            var cat = AppHost.Instance.Data.Categories.FirstOrDefault(c => c.Id == categoryId);
            folder = cat?.PathLabel ?? cat?.Name ?? "";
        }
        return new CompletedRow
        {
            Kind = kind,
            Id = id,
            Title = Blank(title, type),
            TypeLabel = type,
            Folder = folder,
            CompletedAt = at
        };
    }

    void CompletedFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        BindCompleted();
    }

    void CompletedSelected(object sender, SelectionChangedEventArgs e) => LoadCompletedEditor();

    void LoadCompletedEditor()
    {
        if (CompletedEditor == null)
            return;
        var wasLoading = _loading;
        _loading = true;
        if (CompletedList.SelectedItem is not CompletedRow row)
        {
            CompletedEditor.IsEnabled = false;
            CompletedEditor.Visibility = Visibility.Collapsed;
            if (CompletedHint != null)
                CompletedHint.Visibility = Visibility.Visible;
            _loading = wasLoading;
            return;
        }

        CompletedEditor.IsEnabled = true;
        CompletedEditor.Visibility = Visibility.Visible;
        if (CompletedHint != null)
            CompletedHint.Visibility = Visibility.Collapsed;
        CompletedTitle.Text = row.Title;
        CompletedType.Text = row.TypeLabel;
        CompletedFolder.Text = string.IsNullOrEmpty(row.Folder) ? "(no folder)" : row.Folder;
        var shown = row.CompletedAt > DateTime.Now ? DateTime.Now : row.CompletedAt;
        CompletedDate.DisplayDateEnd = DateTime.Today;
        CompletedDate.SelectedDate = shown.Date;
        CompletedTime.Text = shown.ToString("HH:mm");
        _loading = wasLoading;
    }

    void CompletedWhenChanged(object sender, EventArgs e) => ApplyCompletedWhen(false);

    void CompletedTimeLostFocus(object sender, RoutedEventArgs e) => ApplyCompletedWhen(true);

    void ApplyCompletedWhen(bool allowShortClock)
    {
        if (_loading || CompletedList.SelectedItem is not CompletedRow row)
            return;
        var date = CompletedDate.SelectedDate ?? row.CompletedAt.Date;
        if (!ApplyClockBox(CompletedTime, allowShortClock, false, out var clock)
            || !TimeSpan.TryParse(clock, out var time))
            return;

        var at = date.Date + time;
        var now = DateTime.Now;
        if (at > now)
        {
            at = now;
            _loading = true;
            CompletedDate.SelectedDate = at.Date;
            CompletedTime.Text = at.ToString("HH:mm");
            CompletedTime.CaretIndex = CompletedTime.Text.Length;
            _loading = false;
        }

        if (at == row.CompletedAt)
            return;
        if (!SetCompletedAt(row.Kind, row.Id, at))
            return;
        row.CompletedAt = at;
        FlushOverlay();
    }

    void CompletedSetNow(object sender, RoutedEventArgs e)
    {
        if (CompletedList.SelectedItem is not CompletedRow row)
            return;
        var at = DateTime.Now;
        if (!SetCompletedAt(row.Kind, row.Id, at))
            return;
        row.CompletedAt = at;
        _loading = true;
        CompletedDate.SelectedDate = at.Date;
        CompletedTime.Text = at.ToString("HH:mm");
        _loading = false;
        FlushOverlay();
    }

    void CompletedClear(object sender, RoutedEventArgs e)
    {
        if (CompletedList.SelectedItem is not CompletedRow row)
            return;
        if (!SetCompletedAt(row.Kind, row.Id, null))
            return;
        FlushOverlay();
        BindCompleted();
        BindLayout();
    }

    void CompletedDelete(object sender, RoutedEventArgs e)
    {
        if (CompletedList.SelectedItem is not CompletedRow row)
            return;
        AppHost.Instance.DeleteItem(row.Id, row.Kind);
        BindLists();
    }

    void CompletedListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;
        CompletedDelete(sender, e);
        e.Handled = true;
    }

    static bool SetCompletedAt(string kind, string id, DateTime? at)
    {
        if (at is DateTime when && when > DateTime.Now)
            at = DateTime.Now;
        var data = AppHost.Instance.Data;
        if (kind == "once")
        {
            var item = data.Once.FirstOrDefault(t => t.Id == id);
            if (item == null)
                return false;
            item.CompletedAt = at;
            return true;
        }
        if (kind == "todo")
        {
            var item = data.Todos.FirstOrDefault(t => t.Id == id);
            if (item == null)
                return false;
            item.CompletedAt = at;
            return true;
        }
        if (kind == "event")
        {
            var item = data.Events.FirstOrDefault(t => t.Id == id);
            if (item == null)
                return false;
            item.CompletedAt = at;
            return true;
        }
        return false;
    }

    void TaskSelected(object sender, SelectionChangedEventArgs e) => LoadSelectedTaskEditor();

    void LoadSelectedTaskEditor()
    {
        var kind = SelectedTask()?.Kind;
        ShowTaskEditor(kind);
        if (kind == "once")
            LoadOnceEditor();
        else if (kind == "todo")
            LoadTodoEditor();
        else if (kind == "event")
            LoadEventEditor();
        else if (kind == "note")
            LoadNoteEditor();
        else
        {
            OnceEditor.IsEnabled = false;
            TodoEditor.IsEnabled = false;
            EventEditor.IsEnabled = false;
            NoteEditor.IsEnabled = false;
        }
    }

    void LoadOnceEditor()
    {
        _loading = true;
        if (SelectedOnce() is not OnceItem item)
        {
            OnceEditor.IsEnabled = false;
            _loading = false;
            return;
        }

        OnceEditor.IsEnabled = true;
        OnceTitle.Text = item.Title;
        OnceUrl.Text = item.Url ?? "";
        SelectCategory(OnceCategory, item.CategoryId);
        OnceCompleted.IsChecked = item.CompletedAt != null;
        OncePin.Text = item.PinSlot.ToString();
        if (OnceHoldPin != null)
            OnceHoldPin.IsChecked = item.HoldPinWhenHidden;
        LoadItemCheckRule(item, OnceCheckRuleBox, OnceReshowRow, OnceReshowBox, OnceReshowUnitBox);
        BindItemColors(OnceColorHost, "once", false, 1, item.Colors, colors => item.Colors = colors);
        _loading = false;
    }

    void OnceFieldChanged(object sender, EventArgs e)
    {
        if (_loading || SelectedOnce() is not OnceItem item)
            return;
        item.Title = OnceTitle.Text.Trim().Length == 0 ? "New to-do" : OnceTitle.Text.Trim();
        item.Url = BlankToNull(OnceUrl.Text);
        var folder = OnceCategory.SelectedItem is Category onceCat ? onceCat.Id : "";
        var folderChanged = item.CategoryId != folder;
        item.CategoryId = folder;
        var complete = OnceCompleted.IsChecked == true;
        if (complete && item.CompletedAt == null)
            item.CompletedAt = DateTime.Now;
        else if (!complete)
            item.CompletedAt = null;
        var pin = ReadPin(OncePin);
        var pinChanged = item.PinSlot != pin;
        item.PinSlot = pin;
        var holdPin = OnceHoldPin?.IsChecked == true;
        var holdChanged = item.HoldPinWhenHidden != holdPin;
        item.HoldPinWhenHidden = holdPin;
        ReadItemCheckRule(item, OnceCheckRuleBox);
        UpdateReshowRow(OnceReshowRow, OnceCheckRuleBox);
        if (SelectedTask() is ConfigTask row)
            row.Title = item.Title;
        TaskList.Items.Refresh();
        if (folderChanged)
            BindLayout();
        else
            SyncLayoutNode("once", item.Id, item.Title,
                WithCheckRule(WithPin(complete ? "To Do · done" : "To Do", pin), item.CheckRule));
        if (pinChanged || holdChanged || ReferenceEquals(sender, OnceUrl) || ReferenceEquals(sender, OnceCheckRuleBox))
            FlushOverlay();
        else
            CommitEdit(sender);
    }

    void OnceCheckRuleChanged(object sender, SelectionChangedEventArgs e)
    {
        OnceFieldChanged(sender, e);
        UpdateReshowRow(OnceReshowRow, OnceCheckRuleBox);
    }

    void TodoCheckRuleChanged(object sender, SelectionChangedEventArgs e)
    {
        TodoFieldChanged(sender, e);
        UpdateReshowRow(TodoReshowRow, TodoCheckRuleBox);
    }

    void EventCheckRuleChanged(object sender, SelectionChangedEventArgs e)
    {
        EventFieldChanged(sender, e);
        UpdateReshowRow(EventReshowRow, EventCheckRuleBox);
    }

    static void ReadItemCheckRule(IHasCheckRule item, ComboBox box)
    {
        if (box.SelectedIndex >= 0)
            item.CheckRule = (FolderCheckRule)box.SelectedIndex;
    }

    void LoadItemCheckRule(IHasCheckRule item, ComboBox ruleBox, StackPanel reshowRow, TextBox reshowBox, ComboBox unitBox)
    {
        ruleBox.SelectedIndex = (int)item.CheckRule;
        FillReshowLead(reshowBox, unitBox, item);
        UpdateReshowRow(reshowRow, ruleBox);
    }

    static void UpdateReshowRow(StackPanel? row, ComboBox? ruleBox)
    {
        if (row == null || ruleBox == null)
            return;
        row.Visibility = ruleBox.SelectedIndex == (int)FolderCheckRule.HideUntilLead
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    void OnceReshowUnitChanged(object sender, SelectionChangedEventArgs e) =>
        ItemReshowUnitChanged(SelectedOnce(), OnceReshowBox, OnceReshowUnitBox);

    void TodoReshowUnitChanged(object sender, SelectionChangedEventArgs e) =>
        ItemReshowUnitChanged(SelectedTodo(), TodoReshowBox, TodoReshowUnitBox);

    void EventReshowUnitChanged(object sender, SelectionChangedEventArgs e) =>
        ItemReshowUnitChanged(SelectedEvent(), EventReshowBox, EventReshowUnitBox);

    void OnceReshowCommit(object sender, RoutedEventArgs e) => CommitItemReshow(SelectedOnce(), OnceReshowBox, OnceReshowUnitBox, true);
    void TodoReshowCommit(object sender, RoutedEventArgs e) => CommitItemReshow(SelectedTodo(), TodoReshowBox, TodoReshowUnitBox, true);
    void EventReshowCommit(object sender, RoutedEventArgs e) => CommitItemReshow(SelectedEvent(), EventReshowBox, EventReshowUnitBox, true);

    void OnceReshowKey(object sender, KeyEventArgs e) => DurationKey(e, () => CommitItemReshow(SelectedOnce(), OnceReshowBox, OnceReshowUnitBox, true));
    void TodoReshowKey(object sender, KeyEventArgs e) => DurationKey(e, () => CommitItemReshow(SelectedTodo(), TodoReshowBox, TodoReshowUnitBox, true));
    void EventReshowKey(object sender, KeyEventArgs e) => DurationKey(e, () => CommitItemReshow(SelectedEvent(), EventReshowBox, EventReshowUnitBox, true));

    void ItemReshowUnitChanged(IHasCheckRule? item, TextBox? box, ComboBox? unit)
    {
        if (_loading || item == null || box == null || unit == null)
            return;
        var hoursUnit = unit.SelectedIndex == 1;
        if (TryParseFolderReshow(box.Text, item.CheckReshowInHours, out var hours))
            item.CheckReshowHours = hours;
        item.CheckReshowInHours = hoursUnit;
        FillReshowLead(box, unit, item);
        FlushOverlay();
    }

    void CommitItemReshow(IHasCheckRule? item, TextBox? box, ComboBox? unit, bool rewrite)
    {
        if (_loading || item == null || box == null || unit == null)
            return;
        var hoursUnit = unit.SelectedIndex == 1;
        if (!TryParseFolderReshow(box.Text, hoursUnit, out var hours))
        {
            FillReshowLead(box, unit, item);
            return;
        }

        item.CheckReshowHours = hours;
        item.CheckReshowInHours = hoursUnit;
        if (rewrite)
            FillReshowLead(box, unit, item);
        FlushOverlay();
    }

    static void FillReshowLead(TextBox? box, ComboBox? unit, IHasCheckRule item)
    {
        if (box == null || unit == null)
            return;
        var hours = item.CheckReshowHours <= 0 ? 24 : item.CheckReshowHours;
        unit.SelectedIndex = item.CheckReshowInHours ? 1 : 0;
        var amount = item.CheckReshowInHours ? hours : hours / 24.0;
        box.Text = amount.ToString("0.###", CultureInfo.CurrentCulture);
    }

    static void FillOptionalHours(TextBox? box, ComboBox? unit, double hours, bool inHours)
    {
        if (box == null || unit == null)
            return;
        unit.SelectedIndex = inHours ? 1 : 0;
        var amount = inHours ? hours : hours / 24.0;
        box.Text = amount.ToString("0.###", CultureInfo.CurrentCulture);
    }

    static bool TryParseOptionalHours(string text, bool hours, out double hoursValue)
    {
        hoursValue = 0;
        var trimmed = text.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var amount)
            && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
            return false;
        if (amount < 0 || double.IsNaN(amount) || double.IsInfinity(amount))
            return false;
        hoursValue = RecurrenceEngine.ClampShowHours(hours ? amount : amount * 24, true);
        return true;
    }

    void EventShowBeforeUnitChanged(object sender, SelectionChangedEventArgs e) =>
        EventShowWindowUnitChanged(
            () => SelectedEvent()?.ShowBeforeHours ?? 0,
            hours => { if (SelectedEvent() is EventItem item) item.ShowBeforeHours = hours; },
            hours => { if (SelectedEvent() is EventItem item) item.ShowBeforeInHours = hours; },
            () => SelectedEvent()?.ShowBeforeInHours == true,
            EventShowBeforeBox, EventShowBeforeUnitBox);

    void EventShowAfterUnitChanged(object sender, SelectionChangedEventArgs e) =>
        EventShowWindowUnitChanged(
            () => SelectedEvent()?.ShowAfterCheckHours ?? 0,
            hours => { if (SelectedEvent() is EventItem item) item.ShowAfterCheckHours = hours; },
            hours => { if (SelectedEvent() is EventItem item) item.ShowAfterCheckInHours = hours; },
            () => SelectedEvent()?.ShowAfterCheckInHours == true,
            EventShowAfterBox, EventShowAfterUnitBox);

    void EventShowWindowUnitChanged(Func<double> getHours, Action<double> setHours, Action<bool> setUnit,
        Func<bool> getUnit, TextBox? box, ComboBox? unit)
    {
        if (_loading || SelectedEvent() == null || box == null || unit == null)
            return;
        var hoursUnit = unit.SelectedIndex == 1;
        if (TryParseOptionalHours(box.Text, getUnit(), out var hours))
            setHours(hours);
        setUnit(hoursUnit);
        FillOptionalHours(box, unit, getHours(), hoursUnit);
        FlushOverlay();
    }

    void EventShowBeforeCommit(object sender, RoutedEventArgs e) =>
        CommitEventShowWindow(true, EventShowBeforeBox, EventShowBeforeUnitBox,
            hours => { if (SelectedEvent() is EventItem item) item.ShowBeforeHours = hours; },
            unit => { if (SelectedEvent() is EventItem item) item.ShowBeforeInHours = unit; },
            () => SelectedEvent()?.ShowBeforeHours ?? 0,
            () => SelectedEvent()?.ShowBeforeInHours == true);

    void EventShowAfterCommit(object sender, RoutedEventArgs e) =>
        CommitEventShowWindow(true, EventShowAfterBox, EventShowAfterUnitBox,
            hours => { if (SelectedEvent() is EventItem item) item.ShowAfterCheckHours = hours; },
            unit => { if (SelectedEvent() is EventItem item) item.ShowAfterCheckInHours = unit; },
            () => SelectedEvent()?.ShowAfterCheckHours ?? 0,
            () => SelectedEvent()?.ShowAfterCheckInHours == true);

    void EventShowBeforeKey(object sender, KeyEventArgs e) =>
        DurationKey(e, () => EventShowBeforeCommit(sender, e));

    void EventShowAfterKey(object sender, KeyEventArgs e) =>
        DurationKey(e, () => EventShowAfterCommit(sender, e));

    void CommitEventShowWindow(bool rewrite, TextBox? box, ComboBox? unit,
        Action<double> setHours, Action<bool> setUnit, Func<double> getHours, Func<bool> getUnit)
    {
        if (_loading || SelectedEvent() == null || box == null || unit == null)
            return;
        var hoursUnit = unit.SelectedIndex == 1;
        if (!TryParseOptionalHours(box.Text, hoursUnit, out var hours))
        {
            FillOptionalHours(box, unit, getHours(), getUnit());
            return;
        }

        setHours(hours);
        setUnit(hoursUnit);
        if (rewrite)
            FillOptionalHours(box, unit, hours, hoursUnit);
        FlushOverlay();
    }

    void TodoKindChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateKindRows();
        TodoFieldChanged(sender, e);
    }

    void LoadTodoEditor()
    {
        _loading = true;
        if (SelectedTodo() is not TodoItem item)
        {
            TodoEditor.IsEnabled = false;
            _loading = false;
            return;
        }

        TodoEditor.IsEnabled = true;
        TodoTitle.Text = item.Title;
        TodoUrl.Text = item.Url ?? "";
        SelectCategory(TodoCategory, item.CategoryId);
        TodoEnabled.IsChecked = item.Enabled;
        // auto model
        TodoImportant.IsChecked = item.Important;
        FillOptionalHours(TodoImportantLeadBox, TodoImportantUnitBox, item.ImportantLeadHours <= 0 ? 1 : item.ImportantLeadHours, item.ImportantLeadInHours);
        if (TodoImportantLeadRow != null)
            TodoImportantLeadRow.Visibility = item.Important ? Visibility.Visible : Visibility.Collapsed;
        TodoUnknownDate.IsChecked = item.UnknownDate;
        TodoLastChecked.Text = "Last checked " + RecurrenceEngine.DescribeLastChecked(RecurrenceEngine.LastChecked(item), DateTime.Now);
        TodoKind.SelectedIndex = Math.Max(0, Array.FindIndex(Kinds, k => k.Kind == item.Recurrence.Kind));
        TodoTime.Text = item.Recurrence.Time;
        TodoInterval.Text = item.Recurrence.Kind == RecurrenceKind.AfterCompletionHours
            ? item.Recurrence.Interval.ToString("0.###", CultureInfo.CurrentCulture)
            : ((int)Math.Round(item.Recurrence.Interval)).ToString(CultureInfo.CurrentCulture);
        SetDays(item.Recurrence.Weekdays);
        TodoMonthDay.Text = item.Recurrence.MonthDay.ToString();
        TodoMonth.Text = item.Recurrence.Month.ToString();
        TodoNth.Text = item.Recurrence.Nth.ToString();
        TodoWeekday.SelectedIndex = Math.Clamp(item.Recurrence.Weekday, 0, 6);
        TodoSummary.Text = RecurrenceEngine.Describe(item.Recurrence);
        TodoPin.Text = item.PinSlot.ToString();
        if (TodoHoldPin != null)
            TodoHoldPin.IsChecked = item.HoldPinWhenHidden;
        LoadItemCheckRule(item, TodoCheckRuleBox, TodoReshowRow, TodoReshowBox, TodoReshowUnitBox);
        ShowCheckEvery(RecurrenceEngine.TodoCheckEveryDays(item), TodoCheckAmount, TodoCheckUnit);
        UpdateKindRows();
        BindItemColors(TodoColorHost, "todo", item.UnknownDate, RecurrenceEngine.TodoCheckEveryDays(item),
            item.Colors, colors => item.Colors = colors);
        _loading = false;
    }

    void TodoFieldChanged(object sender, EventArgs e)
    {
        if (_loading || SelectedTodo() is not TodoItem item)
            return;

        item.Title = TodoTitle.Text.Trim().Length == 0 ? "New recurring" : TodoTitle.Text.Trim();
        item.Url = BlankToNull(TodoUrl.Text);
        var folder = TodoCategory.SelectedItem is Category todoCat ? todoCat.Id : "";
        var folderChanged = item.CategoryId != folder;
        item.CategoryId = folder;
        item.Enabled = TodoEnabled.IsChecked == true;
        // auto model
        item.Important = TodoImportant.IsChecked == true;
        if (TodoImportantLeadRow != null)
            TodoImportantLeadRow.Visibility = item.Important ? Visibility.Visible : Visibility.Collapsed;
        var watch = TodoUnknownDate.IsChecked == true;
        if (watch && !item.UnknownDate)
            item.LastCheckedAt = DateTime.Now;
        item.UnknownDate = watch;
        TodoLastChecked.Text = "Last checked " + RecurrenceEngine.DescribeLastChecked(RecurrenceEngine.LastChecked(item), DateTime.Now);
        if (TodoKind.SelectedIndex >= 0)
            item.Recurrence.Kind = Kinds[TodoKind.SelectedIndex].Kind;
        if (ApplyClockBox(TodoTime, false, true, out var clock))
            item.Recurrence.Time = clock;
        // auto model
        item.Recurrence.Interval = item.Recurrence.Kind == RecurrenceKind.AfterCompletionHours
            ? ParseDouble(TodoInterval.Text, 1, 1.0 / 60, 8760)
            : ParseInt(TodoInterval.Text, 1, 1, 3650);
        item.Recurrence.Weekdays = ReadDays();
        item.Recurrence.MonthDay = ParseInt(TodoMonthDay.Text, 1, 1, 31);
        item.Recurrence.Month = ParseInt(TodoMonth.Text, 1, 1, 12);
        item.Recurrence.Nth = ParseInt(TodoNth.Text, 1, -1, 4);
        if (item.Recurrence.Nth == 0)
            item.Recurrence.Nth = 1;
        item.Recurrence.Weekday = Math.Max(0, TodoWeekday.SelectedIndex);
        item.Recurrence.Anchor ??= DateTime.Today;
        TodoSummary.Text = RecurrenceEngine.Describe(item.Recurrence);
        if (item.UnknownDate)
            item.CheckEveryDays = ReadCheckEvery(TodoCheckAmount, TodoCheckUnit);
        var pin = ReadPin(TodoPin);
        var pinChanged = item.PinSlot != pin;
        item.PinSlot = pin;
        var holdPin = TodoHoldPin?.IsChecked == true;
        var holdChanged = item.HoldPinWhenHidden != holdPin;
        item.HoldPinWhenHidden = holdPin;
        ReadItemCheckRule(item, TodoCheckRuleBox);
        UpdateReshowRow(TodoReshowRow, TodoCheckRuleBox);
        if (SelectedTask() is ConfigTask row)
            row.Title = item.Title;
        TaskList.Items.Refresh();
        if (folderChanged)
            BindLayout();
        else
            SyncLayoutNode("todo", item.Id, item.Title,
                WithCheckRule(WithPin(item.Enabled ? "Recurring" : "Recurring · off", pin), item.CheckRule));
        if (item.UnknownDate)
            RefreshItemColorLabels(TodoColorHost, "todo", true, RecurrenceEngine.TodoCheckEveryDays(item));
        if (pinChanged
            || holdChanged
            || ReferenceEquals(sender, TodoCheckAmount)
            || ReferenceEquals(sender, TodoCheckUnit)
            || ReferenceEquals(sender, TodoUrl)
            || ReferenceEquals(sender, TodoCheckRuleBox))
            FlushOverlay();
        else
            CommitEdit(sender);
    }

    void TodoTimeLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        ApplyClockBox(TodoTime, true, true, out _);
        TodoFieldChanged(sender, e);
    }

    void TodoWatchChanged(object sender, RoutedEventArgs e)
    {
        UpdateKindRows();
        TodoFieldChanged(sender, e);
        if (_loading || SelectedTodo() is not TodoItem item)
            return;
        BindItemColors(TodoColorHost, "todo", item.UnknownDate, RecurrenceEngine.TodoCheckEveryDays(item),
            item.Colors, colors => item.Colors = colors);
    }

    void MarkTodoCheckedNow(object sender, RoutedEventArgs e)
    {
        if (SelectedTodo() is not TodoItem item || !item.UnknownDate)
            return;
        AppHost.Instance.MarkCheckedNow("todo", item.Id);
        TodoLastChecked.Text = "Last checked " + RecurrenceEngine.DescribeLastChecked(RecurrenceEngine.LastChecked(item), DateTime.Now);
    }

    void UpdateKindRows()
    {
        var watch = TodoUnknownDate?.IsChecked == true;
        if (TodoWatchFields != null)
            TodoWatchFields.Visibility = Vis(watch);
        if (TodoScheduleFields != null)
            TodoScheduleFields.Visibility = Vis(!watch);
        if (watch)
            return;
        var kind = TodoKind.SelectedIndex >= 0 ? Kinds[TodoKind.SelectedIndex].Kind : RecurrenceKind.Daily;
        var hours = kind == RecurrenceKind.AfterCompletionHours;
        var interval = kind is RecurrenceKind.EveryNDays or RecurrenceKind.EveryNWeeks
            or RecurrenceKind.AfterCompletion or RecurrenceKind.AfterCompletionHours;
        var weekly = kind == RecurrenceKind.Weekly;
        var monthDay = kind is RecurrenceKind.MonthlyDate or RecurrenceKind.Yearly;
        var month = kind == RecurrenceKind.Yearly;
        var nth = kind == RecurrenceKind.NthWeekday;
        var weekday = kind is RecurrenceKind.NthWeekday or RecurrenceKind.EveryNWeeks;

        TimeRow.Visibility = Vis(!hours);
        IntervalRow.Visibility = Vis(interval);
        IntervalLabel.Text = hours ? "Hours" : kind == RecurrenceKind.AfterCompletion ? "Days" : "Every N";
        Grid.SetColumn(IntervalRow, hours ? 0 : 2);
        Grid.SetColumnSpan(IntervalRow, hours ? 3 : 1);
        WeekdaysLabel.Visibility = Vis(weekly);
        WeekdaysPanel.Visibility = Vis(weekly);
        MonthDayRow.Visibility = Vis(monthDay);
        MonthRow.Visibility = Vis(month);
        NthRow.Visibility = Vis(nth);
        WeekdayRow.Visibility = Vis(weekday);
    }

    void LoadEventEditor()
    {
        _loading = true;
        if (SelectedEvent() is not EventItem item)
        {
            EventEditor.IsEnabled = false;
            _loading = false;
            return;
        }

        EventEditor.IsEnabled = true;
        EventTitle.Text = item.Title;
        EventUrl.Text = item.Url ?? "";
        SelectCategory(EventCategory, item.CategoryId);
        EventUnknownDate.IsChecked = item.At == null;
        UpdateEventMode(item.At == null);
        EventLastChecked.Text = "Last checked " + RecurrenceEngine.DescribeLastChecked(RecurrenceEngine.LastChecked(item), DateTime.Now);
        EventDate.SelectedDate = item.At?.Date;
        EventTime.Text = item.At?.ToString("HH:mm") ?? "09:00";
        EventPin.Text = item.PinSlot.ToString();
        if (EventHoldPin != null)
            EventHoldPin.IsChecked = item.HoldPinWhenHidden;
        // auto model
        EventImportant.IsChecked = item.Important;
        FillOptionalHours(EventImportantLeadBox, EventImportantUnitBox, item.ImportantLeadHours <= 0 ? 1 : item.ImportantLeadHours, item.ImportantLeadInHours);
        if (EventImportantLeadRow != null)
            EventImportantLeadRow.Visibility = item.Important ? Visibility.Visible : Visibility.Collapsed;
        LoadItemCheckRule(item, EventCheckRuleBox, EventReshowRow, EventReshowBox, EventReshowUnitBox);
        FillOptionalHours(EventShowBeforeBox, EventShowBeforeUnitBox, item.ShowBeforeHours, item.ShowBeforeInHours);
        FillOptionalHours(EventShowAfterBox, EventShowAfterUnitBox, item.ShowAfterCheckHours, item.ShowAfterCheckInHours);
        ShowCheckEvery(RecurrenceEngine.EventCheckEveryDays(item), EventCheckAmount, EventCheckUnit);
        BindItemColors(EventColorHost, "event", item.At == null, RecurrenceEngine.EventCheckEveryDays(item),
            item.Colors, colors => item.Colors = colors);
        _loading = false;
    }

    void EventUnknownChanged(object sender, RoutedEventArgs e)
    {
        UpdateEventMode(EventUnknownDate.IsChecked == true);
        EventFieldChanged(sender, e);
        if (_loading || SelectedEvent() is not EventItem item)
            return;
        BindItemColors(EventColorHost, "event", item.At == null, RecurrenceEngine.EventCheckEveryDays(item),
            item.Colors, colors => item.Colors = colors);
    }

    void UpdateEventMode(bool watch)
    {
        if (EventWhenFields != null)
            EventWhenFields.Visibility = watch ? Visibility.Collapsed : Visibility.Visible;
        if (EventWatchFields != null)
            EventWatchFields.Visibility = watch ? Visibility.Visible : Visibility.Collapsed;
    }

    void MarkEventCheckedNow(object sender, RoutedEventArgs e)
    {
        if (SelectedEvent() is not EventItem item || item.At != null)
            return;
        AppHost.Instance.MarkCheckedNow("event", item.Id);
        EventLastChecked.Text = "Last checked " + RecurrenceEngine.DescribeLastChecked(RecurrenceEngine.LastChecked(item), DateTime.Now);
    }

    void EventTimeLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        ApplyClockBox(EventTime, true, false, out _);
        EventFieldChanged(sender, e);
    }

    void EventFieldChanged(object sender, EventArgs e)
    {
        if (_loading || SelectedEvent() is not EventItem item)
            return;
        item.Title = EventTitle.Text.Trim().Length == 0 ? "New scheduled" : EventTitle.Text.Trim();
        item.Url = BlankToNull(EventUrl.Text);
        var folder = EventCategory.SelectedItem is Category eventCat ? eventCat.Id : "";
        var folderChanged = item.CategoryId != folder;
        item.CategoryId = folder;
        if (EventUnknownDate.IsChecked == true)
        {
            if (item.At != null)
                item.LastCheckedAt = DateTime.Now;
            item.At = null;
            item.CheckEveryDays = ReadCheckEvery(EventCheckAmount, EventCheckUnit);
        }
        else
        {
            var date = EventDate.SelectedDate ?? DateTime.Today;
            var time = item.At?.TimeOfDay ?? TimeSpan.FromHours(9);
            if (ApplyClockBox(EventTime, false, false, out var clock))
                time = TimeSpan.Parse(clock);
            item.At = date.Date + time;
        }
        EventLastChecked.Text = "Last checked " + RecurrenceEngine.DescribeLastChecked(RecurrenceEngine.LastChecked(item), DateTime.Now);
        var pin = ReadPin(EventPin);
        var pinChanged = item.PinSlot != pin;
        item.PinSlot = pin;
        var holdPin = EventHoldPin?.IsChecked == true;
        var holdChanged = item.HoldPinWhenHidden != holdPin;
        item.HoldPinWhenHidden = holdPin;
        // auto model
        item.Important = EventImportant.IsChecked == true;
        if (EventImportantLeadRow != null)
            EventImportantLeadRow.Visibility = item.Important ? Visibility.Visible : Visibility.Collapsed;
        ReadItemCheckRule(item, EventCheckRuleBox);
        UpdateReshowRow(EventReshowRow, EventCheckRuleBox);
        if (SelectedTask() is ConfigTask row)
            row.Title = item.Title;
        TaskList.Items.Refresh();
        if (folderChanged)
            BindLayout();
        else
            SyncLayoutNode("event", item.Id, item.Title,
                WithCheckRule(WithPin(item.CompletedAt != null ? "Scheduled · done" : "Scheduled", pin), item.CheckRule));
        if (item.At == null)
            RefreshItemColorLabels(EventColorHost, "event", true, RecurrenceEngine.EventCheckEveryDays(item));
        if (pinChanged
            || holdChanged
            || ReferenceEquals(sender, EventCheckAmount)
            || ReferenceEquals(sender, EventCheckUnit)
            || ReferenceEquals(sender, EventUrl)
            || ReferenceEquals(sender, EventCheckRuleBox))
            FlushOverlay();
        else
            CommitEdit(sender);
    }

    void LoadNoteEditor()
    {
        _loading = true;
        if (SelectedNote() is not NoteItem item)
        {
            NoteEditor.IsEnabled = false;
            _loading = false;
            return;
        }

        NoteEditor.IsEnabled = true;
        NoteTitle.Text = item.Title;
        NoteUrl.Text = item.Url ?? "";
        SelectCategory(NoteCategory, item.CategoryId);
        NoteBody.Text = item.Body;
        NoteHidden.IsChecked = item.Hidden;
        NotePin.Text = item.PinSlot.ToString();
        if (NoteHoldPin != null)
            NoteHoldPin.IsChecked = item.HoldPinWhenHidden;
        _loading = false;
    }

    void NoteFieldChanged(object sender, EventArgs e)
    {
        if (_loading || SelectedNote() is not NoteItem item)
            return;
        item.Title = NoteTitle.Text.Trim().Length == 0 ? "Note" : NoteTitle.Text.Trim();
        item.Url = BlankToNull(NoteUrl.Text);
        var folder = NoteCategory.SelectedItem is Category noteCat ? noteCat.Id : "";
        var folderChanged = item.CategoryId != folder;
        item.CategoryId = folder;
        item.Body = NoteBody.Text;
        item.Hidden = NoteHidden.IsChecked == true;
        var pin = ReadPin(NotePin);
        var pinChanged = item.PinSlot != pin;
        item.PinSlot = pin;
        var holdPin = NoteHoldPin?.IsChecked == true;
        var holdChanged = item.HoldPinWhenHidden != holdPin;
        item.HoldPinWhenHidden = holdPin;
        if (SelectedTask() is ConfigTask row)
        {
            row.Title = item.Title;
            row.TypeLabel = item.Hidden ? "Note (hidden)" : "Note";
        }
        TaskList.Items.Refresh();
        if (folderChanged)
            BindLayout();
        else
            SyncLayoutNode("note", item.Id, item.Title, WithPin(item.Hidden ? "Note · hidden" : "Note", pin));
        if (pinChanged || holdChanged || ReferenceEquals(sender, NoteUrl))
            FlushOverlay();
        else
            CommitEdit(sender);
    }

    void RestoreNote(object sender, RoutedEventArgs e)
    {
        if (SelectedNote() is not NoteItem item)
            return;
        AppHost.Instance.RestoreNote(item.Id);
        NoteHidden.IsChecked = false;
        if (SelectedTask() is ConfigTask row)
            row.TypeLabel = "Note";
        TaskList.Items.Refresh();
        SyncLayoutNode("note", item.Id, item.Title, WithPin("Note", item.PinSlot));
    }

    void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || NotifyBox == null || BoldTextBox == null || TextBackdropBox == null
            || WindowBorderBox == null || ClickThroughBox == null || AutostartBox == null
            || PanelDitherBox == null
            || HideCompletedOnceBox == null || HideCompletedRecurringBox == null
            || HideCompletedScheduledBox == null || HideEmptyFoldersBox == null || FolderCountsBox == null
            || ManualSortBox == null)
            return;

        var host = AppHost.Instance;
        if (host.LayoutSceneActive && ReferenceEquals(sender, ClickThroughBox))
        {
            _loading = true;
            ClickThroughBox.IsChecked = host.Data.Settings.ClickThrough;
            _loading = false;
            return;
        }

        host.Data.Settings.Notifications = NotifyBox.IsChecked == true;
        host.Data.Settings.BoldText = BoldTextBox.IsChecked == true;
        host.Data.Settings.HideCompletedOnce = HideCompletedOnceBox.IsChecked == true;
        host.Data.Settings.HideCompletedRecurring = HideCompletedRecurringBox.IsChecked == true;
        host.Data.Settings.HideCompletedScheduled = HideCompletedScheduledBox.IsChecked == true;
        host.Data.Settings.HideCompleted = false;
        host.Data.Settings.HideEmptyFolders = HideEmptyFoldersBox.IsChecked == true;
        host.Data.Settings.ShowFolderCounts = FolderCountsBox.IsChecked == true;
        host.Data.Settings.ManualSort = ManualSortBox.IsChecked == true;
        host.Data.Settings.TextBackdrop = TextBackdropBox.IsChecked == true;
        host.Data.Settings.ShowWindowBorder = WindowBorderBox.IsChecked == true;
        host.Data.Settings.PanelDither = PanelDitherBox.IsChecked == true;
        BackdropSlider.IsEnabled = host.Data.Settings.TextBackdrop;
        DitherSlider.IsEnabled = host.Data.Settings.PanelDither != false;

        var clickThrough = ClickThroughBox.IsChecked == true;
        var autostart = AutostartBox.IsChecked == true;

        if (host.Data.Settings.ClickThrough != clickThrough)
            host.SetClickThrough(clickThrough);
        if (host.Data.Settings.Autostart != autostart)
            host.SetAutostart(autostart);
        else
            host.RequestSave();
    }

    void SliderGoToClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || e.LeftButton != MouseButtonState.Pressed)
            return;
        if (IsInsideThumb(e.OriginalSource as DependencyObject))
            return;

        ApplySliderPointer(slider, e);
        slider.CaptureMouse();
        e.Handled = true;
    }

    void SliderDrag(object sender, MouseEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsMouseCaptured)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            slider.ReleaseMouseCapture();
            return;
        }

        ApplySliderPointer(slider, e);
    }

    void SliderEndDrag(object sender, MouseEventArgs e)
    {
        if (sender is Slider slider && slider.IsMouseCaptured)
            slider.ReleaseMouseCapture();
    }

    static bool IsInsideThumb(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is Thumb)
                return true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    static void ApplySliderPointer(Slider slider, MouseEventArgs e)
    {
        var track = slider.Template?.FindName("PART_Track", slider) as Track;
        double value;
        if (track != null)
        {
            value = track.ValueFromPoint(e.GetPosition(track));
        }
        else
        {
            var pos = e.GetPosition(slider);
            var ratio = slider.Orientation == Orientation.Horizontal
                ? pos.X / Math.Max(1, slider.ActualWidth)
                : 1 - pos.Y / Math.Max(1, slider.ActualHeight);
            value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
            return;

        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
    }

    void DarknessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || DarknessLabel == null)
            return;
        AppHost.Instance.SetPanelDarkness(DarknessSlider.Value);
        DarknessLabel.Text = $"{DarknessSlider.Value:P0}";
    }

    void DitherChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || DitherLabel == null)
            return;
        AppHost.Instance.SetPanelDitherStrength(DitherSlider.Value);
        DitherLabel.Text = $"{DitherSlider.Value:P0}";
    }

    const double DurationMinSeconds = 1;
    const double DurationMaxSeconds = 72 * 3600;

    void LoadTempHideDurations(AppSettings s)
    {
        TempHideLeadUnitBox.SelectedIndex = s.TempHideLeadInHours ? 1 : 0;
        FillDurationBox(TempHideLeadBox, HoursAsSeconds(s.TempHideHours, 2), s.TempHideLeadInHours);
        TempHideMaxUnitBox.SelectedIndex = s.TempHideMaxInHours ? 1 : 0;
        FillDurationBox(TempHideMaxBox, HoursAsSeconds(s.TempHideMaxHours, 2), s.TempHideMaxInHours);
        TempHideWarnUnitBox.SelectedIndex = s.TempHideWarnInHours ? 1 : 0;
        FillDurationBox(TempHideWarnBox, s.TempHideWarnSeconds <= 0 ? 60 : s.TempHideWarnSeconds, s.TempHideWarnInHours);
    }

    static double HoursAsSeconds(double hours, double fallback) =>
        (hours <= 0 ? fallback : hours) * 3600;

    static void FillDurationBox(TextBox? box, double seconds, bool hours)
    {
        if (box == null)
            return;
        var amount = hours ? seconds / 3600.0 : seconds / 60.0;
        box.Text = amount.ToString("0.###", CultureInfo.CurrentCulture);
    }

    void TempHideLeadUnitChanged(object sender, SelectionChangedEventArgs e) =>
        ChangeDurationUnit(TempHideLeadBox, TempHideLeadUnitBox,
            () => AppHost.Instance.Data.Settings.TempHideLeadInHours,
            hours => AppHost.Instance.Data.Settings.TempHideLeadInHours = hours,
            () => HoursAsSeconds(AppHost.Instance.Data.Settings.TempHideHours, 2),
            seconds => AppHost.Instance.Data.Settings.TempHideHours = seconds / 3600);

    void TempHideMaxUnitChanged(object sender, SelectionChangedEventArgs e) =>
        ChangeDurationUnit(TempHideMaxBox, TempHideMaxUnitBox,
            () => AppHost.Instance.Data.Settings.TempHideMaxInHours,
            hours => AppHost.Instance.Data.Settings.TempHideMaxInHours = hours,
            () => HoursAsSeconds(AppHost.Instance.Data.Settings.TempHideMaxHours, 2),
            seconds => AppHost.Instance.Data.Settings.TempHideMaxHours = seconds / 3600);

    void TempHideWarnUnitChanged(object sender, SelectionChangedEventArgs e) =>
        ChangeDurationUnit(TempHideWarnBox, TempHideWarnUnitBox,
            () => AppHost.Instance.Data.Settings.TempHideWarnInHours,
            hours => AppHost.Instance.Data.Settings.TempHideWarnInHours = hours,
            () => AppHost.Instance.Data.Settings.TempHideWarnSeconds <= 0 ? 60 : AppHost.Instance.Data.Settings.TempHideWarnSeconds,
            seconds => AppHost.Instance.Data.Settings.TempHideWarnSeconds = seconds);

    void ChangeDurationUnit(TextBox? box, ComboBox? unit, Func<bool> getHours, Action<bool> setHours,
        Func<double> getSeconds, Action<double> setSeconds)
    {
        if (_loading || box == null || unit == null)
            return;
        var hours = unit.SelectedIndex == 1;
        if (TryParseDuration(box.Text, getHours(), out var seconds))
            setSeconds(seconds);
        setHours(hours);
        FillDurationBox(box, getSeconds(), hours);
        AppHost.Instance.RequestSave();
        RefreshUpcomingScheduled();
    }

    void TempHideLeadCommit(object sender, RoutedEventArgs e) => CommitLeadDuration(true);
    void TempHideMaxCommit(object sender, RoutedEventArgs e) => CommitMaxDuration(true);
    void TempHideWarnCommit(object sender, RoutedEventArgs e) => CommitWarnDuration(true);

    void TempHideLeadKey(object sender, KeyEventArgs e) => DurationKey(e, () => CommitLeadDuration(true));
    void TempHideMaxKey(object sender, KeyEventArgs e) => DurationKey(e, () => CommitMaxDuration(true));
    void TempHideWarnKey(object sender, KeyEventArgs e) => DurationKey(e, () => CommitWarnDuration(true));

    static void DurationKey(KeyEventArgs e, Action commit)
    {
        if (e.Key != Key.Enter)
            return;
        commit();
        e.Handled = true;
    }

    void CommitLeadDuration(bool rewrite) =>
        CommitHoursDuration(TempHideLeadBox, TempHideLeadUnitBox, rewrite,
            hours => AppHost.Instance.Data.Settings.TempHideLeadInHours = hours,
            seconds => AppHost.Instance.Data.Settings.TempHideHours = seconds / 3600,
            () => HoursAsSeconds(AppHost.Instance.Data.Settings.TempHideHours, 2));

    void CommitMaxDuration(bool rewrite) =>
        CommitHoursDuration(TempHideMaxBox, TempHideMaxUnitBox, rewrite,
            hours => AppHost.Instance.Data.Settings.TempHideMaxInHours = hours,
            seconds => AppHost.Instance.Data.Settings.TempHideMaxHours = seconds / 3600,
            () => HoursAsSeconds(AppHost.Instance.Data.Settings.TempHideMaxHours, 2));

    void CommitWarnDuration(bool rewrite) =>
        CommitHoursDuration(TempHideWarnBox, TempHideWarnUnitBox, rewrite,
            hours => AppHost.Instance.Data.Settings.TempHideWarnInHours = hours,
            seconds => AppHost.Instance.Data.Settings.TempHideWarnSeconds = seconds,
            () => AppHost.Instance.Data.Settings.TempHideWarnSeconds <= 0 ? 60 : AppHost.Instance.Data.Settings.TempHideWarnSeconds);

    void CommitHoursDuration(TextBox? box, ComboBox? unit, bool rewrite,
        Action<bool> setHours, Action<double> setSeconds, Func<double> getSeconds)
    {
        if (_loading || box == null || unit == null)
            return;
        var hours = unit.SelectedIndex == 1;
        if (!TryParseDuration(box.Text, hours, out var seconds))
        {
            FillDurationBox(box, getSeconds(), hours);
            return;
        }

        setSeconds(seconds);
        setHours(hours);
        if (rewrite)
            FillDurationBox(box, seconds, hours);
        AppHost.Instance.RequestSave();
        RefreshUpcomingScheduled();
    }

    static bool TryParseDuration(string text, bool hours, out double seconds)
    {
        seconds = 60;
        var trimmed = text.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var amount)
            && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
            return false;
        if (amount < 0 || double.IsNaN(amount) || double.IsInfinity(amount))
            return false;
        seconds = Math.Clamp(hours ? amount * 3600 : amount * 60, DurationMinSeconds, DurationMaxSeconds);
        return true;
    }

    static readonly string[] DefaultSettingsOpen = ["overlay", "temphide", "clock-options", "clock-aesthetics"];

    void LoadSettingsSections(AppSettings s)
    {
        var open = s.SettingsOpen ?? DefaultSettingsOpen.ToList();
        // First time after clock sections existed: default them open.
        if (!open.Exists(id => id.StartsWith("clock-", StringComparison.Ordinal)))
        {
            open.Add("clock-options");
            open.Add("clock-aesthetics");
        }
        ApplySectionOpen(SettingsOverlay, open);
        ApplySectionOpen(SettingsTempHide, open);
        ApplySectionOpen(SettingsLook, open);
        ApplySectionOpen(SettingsColors, open);
        ApplySectionOpen(SettingsList, open);
        ApplySectionOpen(SettingsSystem, open);
        ApplySectionOpen(ClockOptions, open);
        ApplySectionOpen(ClockAesthetics, open);
    }

    static void ApplySectionOpen(Expander? box, List<string> open)
    {
        if (box?.Tag is not string id)
            return;
        box.IsExpanded = open.Contains(id);
    }

    void SettingsSectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var s = AppHost.Instance.Data.Settings;
        s.SettingsOpen =
        [
            IdIfOpen(SettingsOverlay),
            IdIfOpen(SettingsTempHide),
            IdIfOpen(SettingsLook),
            IdIfOpen(SettingsColors),
            IdIfOpen(SettingsList),
            IdIfOpen(SettingsSystem),
            IdIfOpen(ClockOptions),
            IdIfOpen(ClockAesthetics)
        ];
        s.SettingsOpen.RemoveAll(string.IsNullOrEmpty);
        AppHost.Instance.RequestSave();
    }

    static string IdIfOpen(Expander? box) =>
        box is { IsExpanded: true, Tag: string id } ? id : "";

    void TempHideCornerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TempHideCornerBox == null)
            return;
        if (TempHideCornerBox.SelectedIndex >= 0)
            AppHost.Instance.Data.Settings.TempHideWarnCorner = (OverlayCorner)TempHideCornerBox.SelectedIndex;
        AppHost.Instance.RequestSave();
    }

    void RefreshUpcomingScheduled()
    {
        if (UpcomingScheduledList == null)
            return;
        var now = DateTime.Now;
        if (TempHideStatusText != null)
            TempHideStatusText.Text = AppHost.Instance.TempHideStatusText(now);
        var items = AppHost.Instance.UpcomingScheduled(now, 3);
        UpcomingScheduledList.Text = items.Count == 0
            ? "None important upcoming."
            : string.Join("\n", items.Select(item => $"{item.Title}  ·  {item.Remain}"));
    }

    void HorizonChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || HorizonLabel == null)
            return;
        var amount = (int)Math.Round(HorizonSlider.Value);
        AppHost.Instance.SetEventHorizonDays(amount);
        HorizonLabel.Text = HorizonAmountText(amount);
    }

    void RecurringHorizonChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || RecurringHorizonLabel == null)
            return;
        var amount = (int)Math.Round(RecurringHorizonSlider.Value);
        AppHost.Instance.SetRecurringHorizon(amount);
        RecurringHorizonLabel.Text = HorizonAmountText(amount);
    }

    void HorizonUnitChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || HorizonHoursBox == null)
            return;
        AppHost.Instance.SetHorizonInHours(HorizonHoursBox.IsChecked == true);
        ApplyHorizonUnitUi();
        if (RecurringHorizonLabel != null)
            RecurringHorizonLabel.Text = HorizonAmountText((int)Math.Round(RecurringHorizonSlider.Value));
        if (HorizonLabel != null)
            HorizonLabel.Text = HorizonAmountText((int)Math.Round(HorizonSlider.Value));
    }

    void ApplyHorizonUnitUi()
    {
        var hours = HorizonHoursBox?.IsChecked == true;
        var unit = hours ? "hours" : "days";
        var max = hours ? 336 : 180;
        if (RecurringHorizonCaption != null)
            RecurringHorizonCaption.Text = $"Only show Recurring within this many {unit} (0 = all)";
        if (HorizonCaption != null)
            HorizonCaption.Text = $"Only show Scheduled within this many {unit} (0 = all). A task can set its own window on Layout.";
        if (RecurringHorizonSlider != null)
            RecurringHorizonSlider.Maximum = max;
        if (HorizonSlider != null)
            HorizonSlider.Maximum = max;
    }

    static string HorizonAmountText(int amount) => amount <= 0 ? "All" : amount.ToString();

    void OpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || OpacityLabel == null)
            return;
        AppHost.Instance.SetOpacity(OpacitySlider.Value);
        OpacityLabel.Text = $"{OpacitySlider.Value:P0}";
    }

    void BackdropChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || BackdropLabel == null)
            return;
        AppHost.Instance.SetTextBackdropOpacity(BackdropSlider.Value);
        BackdropLabel.Text = $"{BackdropSlider.Value:P0}";
    }

    void FontChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || FontLabel == null)
            return;
        AppHost.Instance.Data.Settings.FontSize = FontSlider.Value;
        FontLabel.Text = $"{FontSlider.Value:0}";
        AppHost.Instance.RequestSave();
    }

    void WireSettingColor(ColorRow row, Action<AppSettings, string> assign)
    {
        row.ColorCommitted += (_, _) =>
        {
            if (row.OverrideHex is not string hex)
                return;
            assign(AppHost.Instance.Data.Settings, hex);
            AppHost.Instance.RequestSave();
            RefreshItemColorFallbacks();
        };
    }

    void BindSettingColors(AppSettings s)
    {
        ColorTodoRow.Bind("To Do — incomplete", s.ColorTodo, s.ColorTodo, false);
        ColorRecurringRow.Bind("Recurring — incomplete", s.ColorRecurring, s.ColorRecurring, false);
        ColorScheduledRow.Bind("Scheduled — upcoming", s.ColorScheduled, s.ColorScheduled, false);
        ColorDueRow.Bind("Scheduled — due", s.ColorDue, s.ColorDue, false);
        ColorLate1Row.Bind("Attention — 1 late", s.ColorLate1, s.ColorLate1, false);
        ColorLate2Row.Bind("Attention — 2 late", s.ColorLate2, s.ColorLate2, false);
        ColorLate3Row.Bind("Attention — 3+ late", s.ColorLate3, s.ColorLate3, false);
        ColorCompletedRow.Bind("Completed", s.ColorCompleted, s.ColorCompleted, false);
    }

    void BindItemColors(Panel? host, string kind, bool unknownDate, int checkEveryDays, TaskColors? existing, Action<TaskColors?> assign)
    {
        if (host == null)
            return;
        host.Children.Clear();
        var settings = AppHost.Instance.Data.Settings;
        var bag = existing ?? new TaskColors();
        foreach (var state in TaskPalette.States(kind, unknownDate))
        {
            var captured = state;
            var row = new ColorRow { Tag = captured };
            row.Bind(
                TaskPalette.Label(kind, unknownDate, captured, checkEveryDays),
                TaskPalette.Global(settings, kind, captured),
                bag.Get(captured),
                allowClear: true);
            row.ColorCommitted += (_, _) =>
            {
                bag.Set(captured, row.OverrideHex);
                assign(bag.HasAny ? bag : null);
                AppHost.Instance.RequestSave();
            };
            host.Children.Add(row);
        }
    }

    void RefreshItemColorLabels(Panel? host, string kind, bool unknownDate, int checkEveryDays)
    {
        if (host == null)
            return;
        foreach (var child in host.Children)
        {
            if (child is not ColorRow row || row.Tag is not TaskColorState state)
                continue;
            row.SetLabel(TaskPalette.Label(kind, unknownDate, state, checkEveryDays));
        }
    }

    void ShowCheckEvery(int days, TextBox amount, ComboBox unit)
    {
        var (n, weeks) = RecurrenceEngine.SplitCheckEvery(days);
        amount.Text = n.ToString();
        unit.SelectedIndex = weeks ? 1 : 0;
    }

    int ReadCheckEvery(TextBox amount, ComboBox unit)
    {
        var weeks = unit.SelectedIndex == 1;
        var n = ParseInt(amount.Text, 1, 1, weeks ? 520 : 3650);
        return RecurrenceEngine.CombineCheckEvery(n, weeks);
    }

    void RefreshItemColorFallbacks()
    {
        var settings = AppHost.Instance.Data.Settings;
        RefreshHost(OnceColorHost, "once", false, settings);
        RefreshHost(TodoColorHost, "todo", SelectedTodo()?.UnknownDate == true, settings);
        RefreshHost(EventColorHost, "event", SelectedEvent()?.At == null, settings);
    }

    static void RefreshHost(Panel? host, string kind, bool unknownDate, AppSettings settings)
    {
        if (host == null)
            return;
        foreach (var child in host.Children)
        {
            if (child is not ColorRow row || row.Tag is not TaskColorState state)
                continue;
            row.SetFallback(TaskPalette.Global(settings, kind, state));
        }
    }

    void IndentChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || IndentLabel == null)
            return;
        var indent = (int)Math.Round(IndentSlider.Value);
        AppHost.Instance.Data.Settings.FolderIndent = indent;
        IndentLabel.Text = indent.ToString();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void TopFolderIndentChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || TopFolderIndentLabel == null)
            return;
        var indent = (int)Math.Round(TopFolderIndentSlider.Value);
        AppHost.Instance.Data.Settings.TopFolderIndent = indent;
        TopFolderIndentLabel.Text = indent.ToString();
        AppHost.Instance.RequestSave();
    }

    // auto model
    void ContentIndentChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ContentIndentLabel == null)
            return;
        var indent = (int)Math.Round(ContentIndentSlider.Value);
        AppHost.Instance.Data.Settings.FolderContentIndent = indent;
        ContentIndentLabel.Text = indent.ToString();
        AppHost.Instance.RequestSave();
    }

    void OpenJson(object sender, RoutedEventArgs e)
    {
        var path = AppHost.Instance.Store.FilePath;
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    void OpenFolder(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(AppHost.Instance.Store.FilePath);
        if (dir != null)
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    // auto model
    void CreateBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = AppHost.Instance.CreateManualBackup();
            if (BackupStatus != null)
                BackupStatus.Text = "Backup saved: " + path;
            MessageBox.Show("Backup created:\n" + path, "AtFutureMe");
        }
        catch (Exception ex)
        {
            if (BackupStatus != null)
                BackupStatus.Text = "Backup failed: " + ex.Message;
            MessageBox.Show(ex.Message, "AtFutureMe backup failed");
        }
    }

    void AddCategory(object sender, RoutedEventArgs e)
    {
        var item = new Category { Name = "New category" };
        AppHost.Instance.Data.Categories.Add(item);
        CategoryTree.Rebuild(AppHost.Instance.Data.Categories);
        // auto model
        AppHost.Instance.TrackTask("category", item.Id);
        _layoutKind = "category";
        _layoutId = item.Id;
        BindLists();
        CategoryList.SelectedItem = item;
        if (FindLayout("category", item.Id) is LayoutNode node)
            ApplyLayoutEditor(node);
        FlushOverlay();
    }

    void AddSubcategory(object sender, RoutedEventArgs e)
    {
        if (SelectedCategory() is not Category parent)
        {
            AddCategory(sender, e);
            return;
        }

        var item = new Category { Name = "New category", ParentId = parent.Id };
        AppHost.Instance.Data.Categories.Add(item);
        CategoryTree.Rebuild(AppHost.Instance.Data.Categories);
        // auto model
        AppHost.Instance.TrackTask("category", item.Id);
        _layoutKind = "category";
        _layoutId = item.Id;
        BindLists();
        CategoryList.SelectedItem = item;
        if (FindLayout("category", item.Id) is LayoutNode created)
            ApplyLayoutEditor(created);
        FlushOverlay();
    }

    void DeleteCategory(object sender, RoutedEventArgs e)
    {
        if (SelectedCategory() is not Category item)
            return;
        var data = AppHost.Instance.Data;
        var parent = CategoryTree.Parent(data.Categories, item);
        var fallback = parent?.Id ?? "";
        foreach (var once in data.Once.Where(t => t.CategoryId == item.Id))
            once.CategoryId = fallback;
        foreach (var todo in data.Todos.Where(t => t.CategoryId == item.Id))
            todo.CategoryId = fallback;
        foreach (var ev in data.Events.Where(t => t.CategoryId == item.Id))
            ev.CategoryId = fallback;
        foreach (var note in data.Notes.Where(t => t.CategoryId == item.Id))
            note.CategoryId = fallback;
        foreach (var child in CategoryTree.ChildrenOf(data.Categories, item.Id).ToList())
            child.ParentId = item.ParentId;

        AppHost.Instance.ForgetPairing(item.Id);
        // auto model
        AppHost.Instance.UntrackTask("category", item.Id);
        data.Categories.Remove(item);
        CategoryTree.Rebuild(data.Categories);
        if (string.IsNullOrEmpty(fallback))
        {
            _layoutKind = null;
            _layoutId = null;
        }
        else
        {
            _layoutKind = "category";
            _layoutId = fallback;
        }
        BindLists();
        if (FindLayout(_layoutKind, _layoutId) is LayoutNode node)
            ApplyLayoutEditor(node);
        else
        {
            ParkEditors();
            ShowTaskEditor(null);
            ShowLayoutHint("Select a task or folder to edit it.");
        }
        FlushOverlay();
    }

    void MoveCategoryUp(object sender, RoutedEventArgs e) => MoveSelectedCategory(-1);

    void MoveCategoryDown(object sender, RoutedEventArgs e) => MoveSelectedCategory(1);

    void MoveSelectedCategory(int delta)
    {
        if (SelectedCategory() is not Category item)
            return;
        // auto model
        MoveAmongSiblings("category", item.Id, delta);
    }

    void CategorySelected(object sender, SelectionChangedEventArgs e) => LoadCategoryEditor();

    void LoadCategoryEditor()
    {
        _loading = true;
        if (SelectedCategory() is not Category item)
        {
            CategoryEditor.IsEnabled = false;
            _loading = false;
            return;
        }

        CategoryEditor.IsEnabled = true;
        CategoryName.Text = item.Name;
        if (CategoryPin != null)
            CategoryPin.Text = item.PinSlot.ToString();
        if (CategoryHoldPin != null)
            CategoryHoldPin.IsChecked = item.HoldPinWhenHidden;
        FillParentCombo(item);
        CategoryPopped.IsChecked = item.PoppedOut;
        CategoryDelayable.IsChecked = item.Delayable;
        CategoryCheckRuleBox.SelectedIndex = (int)item.CheckRule;
        FillFolderReshow(item);
        UpdateFolderReshowRow();
        CategoryPairLabel.Text = PairLabel(item);
        _loading = false;
    }

    static string PairLabel(Category item)
    {
        if (string.IsNullOrWhiteSpace(item.PairedExeName) && string.IsNullOrWhiteSpace(item.PairedExePath))
            return "Not paired";
        if (!string.IsNullOrWhiteSpace(item.PairedExePath))
            return string.IsNullOrWhiteSpace(item.PairedExeName)
                ? item.PairedExePath
                : $"{item.PairedExeName}\n{item.PairedExePath}";
        return item.PairedExeName ?? "Not paired";
    }

    void BindAppLayouts()
    {
        if (AppLayoutList == null)
            return;
        var keep = (AppLayoutList.SelectedItem as AppLayout)?.Id;
        var rows = AppHost.Instance.Layouts().ToList();
        AppLayoutList.ItemsSource = rows;
        AppLayoutList.SelectedItem = rows.FirstOrDefault(l => l.Id == keep) ?? rows.FirstOrDefault();
        LoadAppLayoutEditor();
    }

    void AddAppLayout(object sender, RoutedEventArgs e)
    {
        var picker = new PairProgramWindow(true) { Owner = this };
        if (picker.ShowDialog() != true || picker.Chosen == null)
            return;
        var layout = AppHost.Instance.FlagAppLayout(picker.Chosen.FilePath, picker.Chosen.Name);
        BindAppLayouts();
        AppLayoutList.SelectedItem = AppHost.Instance.Layouts().FirstOrDefault(l => l.Id == layout.Id);
    }

    void RemoveAppLayout(object sender, RoutedEventArgs e)
    {
        if (AppLayoutList.SelectedItem is not AppLayout layout)
            return;
        AppHost.Instance.RemoveAppLayout(layout.Id);
        BindAppLayouts();
    }

    void AppLayoutSelected(object sender, SelectionChangedEventArgs e) => LoadAppLayoutEditor();

    void LoadAppLayoutEditor()
    {
        if (AppLayoutEditor == null)
            return;
        _loading = true;
        if (AppLayoutList.SelectedItem is not AppLayout layout)
        {
            AppLayoutEditor.IsEnabled = false;
            AppLayoutHint.Visibility = Visibility.Visible;
            _loading = false;
            return;
        }

        AppLayoutEditor.IsEnabled = true;
        AppLayoutHint.Visibility = Visibility.Collapsed;
        AppLayoutHideBox.SelectedIndex = (int)layout.Hide;
        AppLayoutLockBox.IsChecked = layout.ClickThrough;
        _loading = false;
    }

    void AppLayoutFieldChanged(object sender, EventArgs e)
    {
        if (_loading || AppLayoutList.SelectedItem is not AppLayout layout)
            return;
        if (AppLayoutHideBox.SelectedIndex >= 0)
            layout.Hide = (AppLayoutHide)AppLayoutHideBox.SelectedIndex;
        layout.ClickThrough = AppLayoutLockBox.IsChecked == true;
        AppHost.Instance.UpdateAppLayout(layout);
    }

    void RefreshPairTargets(object sender, RoutedEventArgs e) => BindPairTargets();

    void BindPairTargets()
    {
        var host = AppHost.Instance;
        PairAvailableList.ItemsSource = ProcessWatch.ListPairTargets(host.Data.Settings.IgnoredPairTargets);
        PairHiddenList.ItemsSource = host.Data.Settings.IgnoredPairTargets
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    void HidePairTarget(object sender, RoutedEventArgs e) => HideSelectedPairTarget();

    void HidePairTarget(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
            HideSelectedPairTarget();
    }

    void HideSelectedPairTarget()
    {
        if (PairAvailableList.SelectedItem is not RunningExe exe)
            return;
        AppHost.Instance.HidePairTarget(exe.Name, exe.FilePath);
        BindPairTargets();
    }

    void RestorePairTarget(object sender, RoutedEventArgs e) => RestoreSelectedPairTarget();

    void RestorePairTarget(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
            RestoreSelectedPairTarget();
    }

    void RestoreSelectedPairTarget()
    {
        if (PairHiddenList.SelectedItem is not IgnoredPairTarget item)
            return;
        AppHost.Instance.RestorePairTarget(item.FilePath);
        BindPairTargets();
    }

    void PairCategoryProgram(object sender, RoutedEventArgs e)
    {
        if (SelectedCategory() is not Category item)
            return;
        var picker = new PairProgramWindow(item) { Owner = this };
        picker.ShowDialog();
        BindPairTargets();
        LoadCategoryEditor();
        FlushOverlay();
    }

    void ClearCategoryPair(object sender, RoutedEventArgs e)
    {
        if (SelectedCategory() is not Category item)
            return;
        item.PairedExePath = null;
        item.PairedExeName = null;
        item.PairMinimizeMain = false;
        AppHost.Instance.ForgetPairing(item.Id);
        Flush();
        LoadCategoryEditor();
        AppHost.Instance.RequestSave();
    }

    void CategoryFieldChanged(object sender, EventArgs e)
    {
        if (_loading || SelectedCategory() is not Category item)
            return;
        item.Name = CategoryName.Text.Trim().Length == 0 ? "New category" : CategoryName.Text.Trim();
        var pin = ReadPin(CategoryPin);
        var pinChanged = item.PinSlot != pin;
        item.PinSlot = pin;
        var holdPin = CategoryHoldPin?.IsChecked == true;
        var holdChanged = item.HoldPinWhenHidden != holdPin;
        item.HoldPinWhenHidden = holdPin;
        var parentId = CategoryParent.SelectedItem is Category parent && parent.Id.Length > 0
            ? parent.Id
            : null;
        var parentChanged = item.ParentId != parentId;
        item.ParentId = parentId;
        item.Delayable = CategoryDelayable.IsChecked == true;
        if (CategoryCheckRuleBox.SelectedIndex >= 0)
            item.CheckRule = (FolderCheckRule)CategoryCheckRuleBox.SelectedIndex;
        UpdateFolderReshowRow();
        var popped = CategoryPopped.IsChecked == true;
        if (item.PoppedOut != popped)
            AppHost.Instance.SetCategoryPoppedOut(item.Id, popped);
        else if (ReferenceEquals(sender, CategoryCheckRuleBox) || pinChanged || holdChanged)
            FlushOverlay();
        else
            Flush();
        if (parentChanged)
        {
            CategoryTree.Rebuild(AppHost.Instance.Data.Categories);
            BindLists();
            CategoryList.SelectedItem = item;
        }
        else
        {
            CategoryTree.RefreshLabels(AppHost.Instance.Data.Categories);
            CategoryList.Items.Refresh();
            var extra = new List<string>();
            if (item.PoppedOut)
                extra.Add("popped out");
            if (item.Collapsed)
                extra.Add("collapsed");
            extra.AddRange(FolderRuleMeta(item));
            SyncLayoutNode("category", item.Id, item.Name, WithPin(string.Join(" · ", extra), item.PinSlot));
            if (pinChanged)
                BindLists();
        }
    }

    void CategoryCheckRuleChanged(object sender, SelectionChangedEventArgs e)
    {
        CategoryFieldChanged(sender, e);
        UpdateFolderReshowRow();
    }

    void UpdateFolderReshowRow()
    {
        if (CategoryReshowRow == null)
            return;
        CategoryReshowRow.Visibility = CategoryCheckRuleBox.SelectedIndex == (int)FolderCheckRule.HideUntilLead
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    void FillFolderReshow(Category item)
    {
        if (CategoryReshowBox == null || CategoryReshowUnitBox == null)
            return;
        var hours = item.CheckReshowHours <= 0 ? 24 : item.CheckReshowHours;
        CategoryReshowUnitBox.SelectedIndex = item.CheckReshowInHours ? 1 : 0;
        var amount = item.CheckReshowInHours ? hours : hours / 24.0;
        CategoryReshowBox.Text = amount.ToString("0.###", CultureInfo.CurrentCulture);
    }

    void CategoryReshowUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedCategory() is not Category item || CategoryReshowBox == null || CategoryReshowUnitBox == null)
            return;
        var hoursUnit = CategoryReshowUnitBox.SelectedIndex == 1;
        if (TryParseFolderReshow(CategoryReshowBox.Text, item.CheckReshowInHours, out var hours))
            item.CheckReshowHours = hours;
        item.CheckReshowInHours = hoursUnit;
        FillFolderReshow(item);
        FlushOverlay();
    }

    void CategoryReshowCommit(object sender, RoutedEventArgs e) => CommitFolderReshow(true);

    void CategoryReshowKey(object sender, KeyEventArgs e) => DurationKey(e, () => CommitFolderReshow(true));

    void CommitFolderReshow(bool rewrite)
    {
        if (_loading || SelectedCategory() is not Category item || CategoryReshowBox == null || CategoryReshowUnitBox == null)
            return;
        var hoursUnit = CategoryReshowUnitBox.SelectedIndex == 1;
        if (!TryParseFolderReshow(CategoryReshowBox.Text, hoursUnit, out var hours))
        {
            FillFolderReshow(item);
            return;
        }

        item.CheckReshowHours = hours;
        item.CheckReshowInHours = hoursUnit;
        if (rewrite)
            FillFolderReshow(item);
        FlushOverlay();
    }

    static bool TryParseFolderReshow(string text, bool hours, out double hoursValue)
    {
        hoursValue = 24;
        var trimmed = text.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var amount)
            && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
            return false;
        if (amount < 0 || double.IsNaN(amount) || double.IsInfinity(amount))
            return false;
        hoursValue = Math.Clamp(hours ? amount : amount * 24, 1.0 / 60, 72 * 24);
        return true;
    }

    void FillParentCombo(Category item)
    {
        var all = AppHost.Instance.Data.Categories;
        CategoryTree.RefreshLabels(all);
        var options = new List<Category> { TopLevelParent };
        options.AddRange(CategoryTree.InOrder(all).Where(c =>
            c.Id != item.Id && !CategoryTree.IsDescendant(all, item, c)));
        CategoryParent.ItemsSource = options;
        CategoryParent.SelectedItem = options.FirstOrDefault(c => c.Id == (item.ParentId ?? ""))
                                      ?? TopLevelParent;
    }

    void FillCategoryCombo(ComboBox box)
    {
        var selectedId = (box.SelectedItem as Category)?.Id;
        var all = AppHost.Instance.Data.Categories;
        CategoryTree.RefreshLabels(all);
        var items = new List<Category> { NoFolder };
        items.AddRange(CategoryTree.InOrder(all));
        box.ItemsSource = items;
        SelectCategory(box, selectedId);
    }

    void SelectCategory(ComboBox box, string? categoryId)
    {
        if (box.ItemsSource is not IEnumerable<Category> items)
            return;
        box.SelectedItem = items.FirstOrDefault(c => c.Id == (categoryId ?? "")) ?? NoFolder;
    }

    void SetDays(List<int> days)
    {
        DaySun.IsChecked = days.Contains(0);
        DayMon.IsChecked = days.Contains(1);
        DayTue.IsChecked = days.Contains(2);
        DayWed.IsChecked = days.Contains(3);
        DayThu.IsChecked = days.Contains(4);
        DayFri.IsChecked = days.Contains(5);
        DaySat.IsChecked = days.Contains(6);
    }

    List<int> ReadDays()
    {
        var days = new List<int>();
        if (DaySun.IsChecked == true) days.Add(0);
        if (DayMon.IsChecked == true) days.Add(1);
        if (DayTue.IsChecked == true) days.Add(2);
        if (DayWed.IsChecked == true) days.Add(3);
        if (DayThu.IsChecked == true) days.Add(4);
        if (DayFri.IsChecked == true) days.Add(5);
        if (DaySat.IsChecked == true) days.Add(6);
        return days;
    }

    static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text, out var v) ? Math.Clamp(v, min, max) : fallback;

    // auto model
    static double ParseDouble(string text, double fallback, double min, double max)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v)
            && !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            return fallback;
        if (double.IsNaN(v) || double.IsInfinity(v))
            return fallback;
        return Math.Clamp(v, min, max);
    }

    // auto model
    void TodoImportantChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || SelectedTodo() is not TodoItem item)
            return;
        item.Important = TodoImportant.IsChecked == true;
        if (TodoImportantLeadRow != null)
            TodoImportantLeadRow.Visibility = item.Important ? Visibility.Visible : Visibility.Collapsed;
        FlushOverlay();
    }

    void EventImportantChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || SelectedEvent() is not EventItem item)
            return;
        item.Important = EventImportant.IsChecked == true;
        if (EventImportantLeadRow != null)
            EventImportantLeadRow.Visibility = item.Important ? Visibility.Visible : Visibility.Collapsed;
        FlushOverlay();
    }

    void TodoImportantUnitChanged(object sender, SelectionChangedEventArgs e) =>
        CommitImportantLead(SelectedTodo(), TodoImportantLeadBox, TodoImportantUnitBox, true);

    void EventImportantUnitChanged(object sender, SelectionChangedEventArgs e) =>
        CommitImportantLead(SelectedEvent(), EventImportantLeadBox, EventImportantUnitBox, true);

    void TodoImportantLeadCommit(object sender, RoutedEventArgs e) =>
        CommitImportantLead(SelectedTodo(), TodoImportantLeadBox, TodoImportantUnitBox, true);

    void EventImportantLeadCommit(object sender, RoutedEventArgs e) =>
        CommitImportantLead(SelectedEvent(), EventImportantLeadBox, EventImportantUnitBox, true);

    void TodoImportantLeadKey(object sender, KeyEventArgs e) =>
        DurationKey(e, () => TodoImportantLeadCommit(sender, e));

    void EventImportantLeadKey(object sender, KeyEventArgs e) =>
        DurationKey(e, () => EventImportantLeadCommit(sender, e));

    void CommitImportantLead(object? target, TextBox? box, ComboBox? unit, bool rewrite)
    {
        if (_loading || box == null || unit == null)
            return;
        var hoursUnit = unit.SelectedIndex == 1;
        if (!TryParseOptionalHours(box.Text, hoursUnit, out var hours) || hours <= 0)
            hours = hoursUnit ? 1 : 24;
        hours = Math.Clamp(hours, 1.0 / 60, 720 * 24);
        if (target is TodoItem todo)
        {
            todo.ImportantLeadHours = hours;
            todo.ImportantLeadInHours = hoursUnit;
        }
        else if (target is EventItem ev)
        {
            ev.ImportantLeadHours = hours;
            ev.ImportantLeadInHours = hoursUnit;
        }
        else
            return;
        if (rewrite)
            FillOptionalHours(box, unit, hours, hoursUnit);
        FlushOverlay();
    }

    bool ApplyClockBox(TextBox box, bool allowShort, bool emptyIsMidnight, out string clock)
    {
        clock = "00:00";
        if (string.IsNullOrWhiteSpace(box.Text))
            return emptyIsMidnight;
        if (!ClockText.TryFormat(box.Text, allowShort, out clock))
            return false;
        if (box.Text == clock)
            return true;
        var was = _loading;
        _loading = true;
        box.Text = clock;
        box.CaretIndex = clock.Length;
        _loading = was;
        return true;
    }
}
