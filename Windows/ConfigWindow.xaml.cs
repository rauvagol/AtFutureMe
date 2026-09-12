using System.ComponentModel;
using System.Diagnostics;
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
            Meta = string.Join(" · ", extra),
            // auto model
            Children = MixedChildren(category.Id)
        };
    }

    // auto model
    List<LayoutNode> MixedChildren(string? parentId)
    {
        var folderId = parentId ?? "";
        var tasks = ItemsIn(folderId);
        var folders = CategoryTree.ChildrenOf(AppHost.Instance.Data.Categories, parentId)
            .Select(FolderNode)
            .ToList();
        if (!AppHost.Instance.Data.Settings.ManualSort)
            return tasks.Concat(folders).ToList();

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

        rows.Sort((a, b) => AppHost.Instance.CompareOverlayOrder(a.Kind, a.Id, a.Title, b.Kind, b.Id, b.Title));
        AppHost.ApplyPinnedOrder(rows, n => n.PinSlot, n => n.Title);
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
        AppHost.Instance.Data.Settings.ManualSort = true;
        if (ManualSortBox != null)
            ManualSortBox.IsChecked = true;
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
        if (pinChanged || ReferenceEquals(sender, OnceUrl) || ReferenceEquals(sender, OnceCheckRuleBox))
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
        if (pinChanged || ReferenceEquals(sender, NoteUrl))
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

    static readonly string[] DefaultSettingsOpen = ["overlay", "temphide"];

    void LoadSettingsSections(AppSettings s)
    {
        var open = s.SettingsOpen ?? DefaultSettingsOpen.ToList();
        ApplySectionOpen(SettingsOverlay, open);
        ApplySectionOpen(SettingsTempHide, open);
        ApplySectionOpen(SettingsLook, open);
        ApplySectionOpen(SettingsColors, open);
        ApplySectionOpen(SettingsList, open);
        ApplySectionOpen(SettingsSystem, open);
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
            IdIfOpen(SettingsSystem)
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
        else if (ReferenceEquals(sender, CategoryCheckRuleBox))
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
            SyncLayoutNode("category", item.Id, item.Name, string.Join(" · ", extra));
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
