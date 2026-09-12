using System.Windows;
using System.Windows.Controls;
using AtFutureMe.Models;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

public partial class AddEntryWindow : Window
{
    static readonly Category None = new() { Id = "", Name = "(no folder)", PathLabel = "(no folder)" };
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

    static readonly string[] WeekdayNames =
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    bool _loading = true;

    public bool FolderOnly { get; }

    public AddEntryWindow(string? categoryId, bool folderOnly = false)
    {
        FolderOnly = folderOnly;
        InitializeComponent();
        foreach (var kind in Kinds)
            RepeatBox.Items.Add(kind.Label);
        foreach (var name in WeekdayNames)
            RepeatWeekday.Items.Add(name);
        RepeatBox.SelectedIndex = 0;
        RepeatWeekday.SelectedIndex = 3;
        CheckEveryUnit.Items.Add("Days");
        CheckEveryUnit.Items.Add("Weeks");
        CheckEveryAmount.Text = "1";
        CheckEveryUnit.SelectedIndex = 1;
        TypeBox.Items.Add("To Do");
        TypeBox.Items.Add("Recurring");
        TypeBox.Items.Add("Scheduled");
        TypeBox.Items.Add("Note");
        EventDate.SelectedDate = DateTime.Today.AddDays(1);
        TypeBox.SelectedIndex = 0;
        if (FolderOnly)
        {
            Title = "Add folder";
            MinHeight = 200;
            Height = 260;
            TypeRow.Visibility = Visibility.Collapsed;
        }

        FillFolders(categoryId);
        _loading = false;
        UpdateTypeFields();
        UpdateRepeatRows();
        TitleBox.Focus();
    }

    void TypeChanged(object sender, SelectionChangedEventArgs e) => UpdateTypeFields();

    void UnknownDateChanged(object sender, RoutedEventArgs e) => UpdateTypeFields();

    void RepeatChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        if (ReferenceEquals(sender, RepeatTime))
            ApplyClockBox(RepeatTime, false, true, out _);
        UpdateRepeatRows();
    }

    void RepeatTimeLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        ApplyClockBox(RepeatTime, true, true, out _);
        UpdateRepeatRows();
    }

    void EventTimeChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        ApplyClockBox(EventTime, false, false, out _);
    }

    void EventTimeLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        ApplyClockBox(EventTime, true, false, out _);
    }

    void RepeatChanged(object sender, SelectionChangedEventArgs e) => RepeatChanged(sender, (RoutedEventArgs)e);

    void UpdateTypeFields()
    {
        if (EventFields == null || NoteFields == null || TodoHint == null || UnknownDateBox == null
            || CategoryLabel == null || TitleLabel == null || TodoFields == null)
            return;
        var once = TypeBox.SelectedIndex == 0;
        var todo = TypeBox.SelectedIndex == 1;
        var ev = TypeBox.SelectedIndex == 2;
        var folder = FolderOnly;
        var unknown = UnknownDateBox.IsChecked == true;
        CategoryLabel.Text = folder ? "Inside" : "Folder";
        TitleLabel.Text = folder ? "Name" : "Title";
        FillFolders((CategoryBox.SelectedItem as Category)?.Id);
        TodoFields.Visibility = todo ? Visibility.Visible : Visibility.Collapsed;
        UnknownDateBox.Visibility = ev && !folder ? Visibility.Visible : Visibility.Collapsed;
        EventFields.Visibility = ev && !unknown && !folder ? Visibility.Visible : Visibility.Collapsed;
        if (CheckEveryFields != null)
            CheckEveryFields.Visibility = ev && unknown && !folder ? Visibility.Visible : Visibility.Collapsed;
        NoteFields.Visibility = TypeBox.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (folder)
        {
            TodoHint.Text = "Creates a folder inside the selected folder, or at the top level.";
            TodoHint.Visibility = Visibility.Visible;
        }
        else if (once)
        {
            TodoHint.Text = "A checkbox on the overlay. Check it when you're done.";
            TodoHint.Visibility = Visibility.Visible;
        }
        else if (ev && unknown)
        {
            TodoHint.Text = "Tracks something coming eventually. Late colors step each time the check interval passes. Use ↻ to mark that you checked it now. When you know the date, set it from the overlay.";
            TodoHint.Visibility = Visibility.Visible;
        }
        else
        {
            TodoHint.Visibility = Visibility.Collapsed;
        }

        if (todo)
            UpdateRepeatRows();
    }

    void UpdateRepeatRows()
    {
        if (RepeatBox == null || RepeatSummary == null)
            return;
        var kind = RepeatBox.SelectedIndex >= 0 ? Kinds[RepeatBox.SelectedIndex].Kind : RecurrenceKind.Daily;
        var hours = kind == RecurrenceKind.AfterCompletionHours;
        var interval = kind is RecurrenceKind.EveryNDays or RecurrenceKind.EveryNWeeks
            or RecurrenceKind.AfterCompletion or RecurrenceKind.AfterCompletionHours;
        var weekly = kind == RecurrenceKind.Weekly;
        var monthDay = kind is RecurrenceKind.MonthlyDate or RecurrenceKind.Yearly;
        var month = kind == RecurrenceKind.Yearly;
        var nth = kind == RecurrenceKind.NthWeekday;
        var weekday = kind is RecurrenceKind.NthWeekday or RecurrenceKind.EveryNWeeks;
        TimeRow.Visibility = hours ? Visibility.Collapsed : Visibility.Visible;
        IntervalRow.Visibility = interval ? Visibility.Visible : Visibility.Collapsed;
        IntervalLabel.Text = hours ? "Hours" : kind == RecurrenceKind.AfterCompletion ? "Days" : "Every N";
        Grid.SetColumn(IntervalRow, hours ? 0 : 2);
        Grid.SetColumnSpan(IntervalRow, hours ? 3 : 1);
        WeekdaysLabel.Visibility = weekly ? Visibility.Visible : Visibility.Collapsed;
        WeekdaysPanel.Visibility = weekly ? Visibility.Visible : Visibility.Collapsed;
        MonthDayRow.Visibility = monthDay ? Visibility.Visible : Visibility.Collapsed;
        MonthRow.Visibility = month ? Visibility.Visible : Visibility.Collapsed;
        NthRow.Visibility = nth ? Visibility.Visible : Visibility.Collapsed;
        WeekdayRow.Visibility = weekday ? Visibility.Visible : Visibility.Collapsed;
        RepeatSummary.Text = RecurrenceEngine.Describe(ReadRecurrence());
    }

    Recurrence ReadRecurrence()
    {
        var kind = RepeatBox.SelectedIndex >= 0 ? Kinds[RepeatBox.SelectedIndex].Kind : RecurrenceKind.Daily;
        ClockText.TryParseFlexible(RepeatTime.Text, true, out var clock);
        var hours = kind == RecurrenceKind.AfterCompletionHours;
        var nth = ParseInt(RepeatNth.Text, 1, -1, 4);
        if (nth == 0)
            nth = 1;
        return new Recurrence
        {
            Kind = kind,
            Time = clock,
            Interval = hours
                ? ParseDouble(RepeatInterval.Text, 1, 1.0 / 60, 8760)
                : ParseInt(RepeatInterval.Text, 1, 1, 3650),
            Weekdays = ReadDays(),
            MonthDay = ParseInt(RepeatMonthDay.Text, 1, 1, 31),
            Month = ParseInt(RepeatMonth.Text, 1, 1, 12),
            Nth = nth,
            Weekday = Math.Max(0, RepeatWeekday.SelectedIndex),
            Anchor = DateTime.Today
        };
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

    static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text, out var v) ? Math.Clamp(v, min, max) : fallback;

    // auto model
    static double ParseDouble(string text, double fallback, double min, double max)
    {
        if (!double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out var v)
            && !double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v))
            return fallback;
        if (double.IsNaN(v) || double.IsInfinity(v))
            return fallback;
        return Math.Clamp(v, min, max);
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

    void FillFolders(string? keepId)
    {
        var folder = FolderOnly;
        None.Name = None.PathLabel = folder ? "(top level)" : "(no folder)";
        CategoryTree.RefreshLabels(AppHost.Instance.Data.Categories);
        CategoryBox.Items.Clear();
        CategoryBox.Items.Add(None);
        foreach (var category in CategoryTree.InOrder(AppHost.Instance.Data.Categories))
            CategoryBox.Items.Add(category);

        var keep = keepId != null && keepId.Length > 0
            ? AppHost.Instance.Data.Categories.FirstOrDefault(c => c.Id == keepId)
            : null;
        CategoryBox.SelectedItem = keep != null && CategoryBox.Items.Contains(keep) ? keep : None;
    }

    void OnCancel(object sender, RoutedEventArgs e) => Close();

    void OnAdd(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (FolderOnly)
        {
            var parent = CategoryBox.SelectedItem as Category;
            var parentId = parent != null && parent.Id.Length > 0 ? parent.Id : null;
            AppHost.Instance.AddSubcategory(parentId, title);
            Close();
            return;
        }

        var categoryId = CategoryBox.SelectedItem is Category category && category.Id.Length > 0
            ? category.Id
            : "";
        ApplyClockBox(EventTime, true, false, out var eventClock);
        AppHost.Instance.AddQuickEntry(
            TypeBox.SelectedIndex,
            categoryId,
            title,
            EventDate.SelectedDate,
            eventClock,
            NoteBody.Text,
            UnknownDateBox.IsChecked == true,
            TypeBox.SelectedIndex == 1 ? ReadRecurrence() : null,
            ReadCheckEvery());
        Close();
    }

    int ReadCheckEvery()
    {
        var weeks = CheckEveryUnit.SelectedIndex == 1;
        var n = ParseInt(CheckEveryAmount.Text, 1, 1, weeks ? 520 : 3650);
        return RecurrenceEngine.CombineCheckEvery(n, weeks);
    }
}
