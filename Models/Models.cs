using System.Text.Json.Serialization;

namespace AtFutureMe.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecurrenceKind
{
    Daily,
    EveryNDays,
    Weekdays,
    Weekly,
    EveryNWeeks,
    MonthlyDate,
    NthWeekday,
    Yearly,
    AfterCompletion,
    AfterCompletionHours
}

public sealed class Recurrence
{
    public RecurrenceKind Kind { get; set; } = RecurrenceKind.Daily;

    /// <summary>Clock time as HH:mm (local). Empty means midnight.</summary>
    public string Time { get; set; } = "00:00";

    /// <summary>N for every-N-days, every-N-weeks, N days after completion, or N hours after completion (fractional hours allowed).</summary>
    // auto model
    public double Interval { get; set; } = 1;

    /// <summary>0 = Sunday … 6 = Saturday. Used by Weekly / Weekdays / EveryNWeeks / NthWeekday.</summary>
    public List<int> Weekdays { get; set; } = [];

    /// <summary>Day of month 1–31 for MonthlyDate / Yearly.</summary>
    public int MonthDay { get; set; } = 1;

    /// <summary>1–4, or -1 for last, for NthWeekday.</summary>
    public int Nth { get; set; } = 1;

    /// <summary>Single weekday 0–6 for NthWeekday / EveryNWeeks.</summary>
    public int Weekday { get; set; } = 3;

    /// <summary>1–12 for Yearly.</summary>
    public int Month { get; set; } = 1;

    /// <summary>Anchor date for EveryNDays / EveryNWeeks (local date).</summary>
    public DateTime? Anchor { get; set; }

    public Recurrence Clone() => new()
    {
        Kind = Kind,
        Time = Time,
        Interval = Interval,
        Weekdays = [.. Weekdays],
        MonthDay = MonthDay,
        Nth = Nth,
        Weekday = Weekday,
        Month = Month,
        Anchor = Anchor
    };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FolderCheckRule
{
    Inherit,
    KeepVisible,
    HideUntilReset,
    HideUntilLead,
    DeleteOnCheck
}

public interface IHasCheckRule
{
    FolderCheckRule CheckRule { get; set; }
    double CheckReshowHours { get; set; }
    bool CheckReshowInHours { get; set; }
}

public sealed class Category : IHasCheckRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New category";
    public string? ParentId { get; set; }
    public bool Collapsed { get; set; }
    public bool PoppedOut { get; set; }
    public bool PairOpened { get; set; }
    public string? PairedExePath { get; set; }
    public string? PairedExeName { get; set; }
    public bool PairMinimizeMain { get; set; }

    /// <summary>Due items in this folder (and nested folders) don't block temporary hide.</summary>
    public bool Delayable { get; set; }

    /// <summary>What happens on the overlay when an item in this folder is checked. Nested folders inherit unless they set their own rule.</summary>
    public FolderCheckRule CheckRule { get; set; } = FolderCheckRule.Inherit;

    /// <summary>Hours before reset to reshow a checked item when CheckRule is HideUntilLead.</summary>
    public double CheckReshowHours { get; set; } = 24;

    /// <summary>True when the folder editor shows the reshow lead in hours; false means days.</summary>
    public bool CheckReshowInHours { get; set; }

    public WindowBounds Bounds { get; set; } = new() { Width = 340, Height = 320 };

    [JsonIgnore]
    public string TreeLabel { get; set; } = "";

    [JsonIgnore]
    public string PathLabel { get; set; } = "";

    public override string ToString() => string.IsNullOrEmpty(PathLabel) ? Name : PathLabel;
}

public sealed class TodoItem : IHasCheckRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New recurring";
    public string? Url { get; set; }
    public string CategoryId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public int PinSlot { get; set; }

    /// <summary>Coming eventually with no known date. Overlay tracks days since last check.</summary>
    public bool UnknownDate { get; set; }

    /// <summary>Days between check-ins for unknown-date items. 0 or less means daily.</summary>
    public int CheckEveryDays { get; set; } = 1;
    public Recurrence Recurrence { get; set; } = new();
    public FolderCheckRule CheckRule { get; set; } = FolderCheckRule.Inherit;
    public double CheckReshowHours { get; set; } = 24;
    public bool CheckReshowInHours { get; set; }

    /// <summary>When true, forces the overlay out of temporary hide this long before the item is due, until it is completed.</summary>
    // auto model
    public bool Important { get; set; }
    public double ImportantLeadHours { get; set; } = 1;
    public bool ImportantLeadInHours { get; set; } = true;

    public TaskColors? Colors { get; set; }
}

public sealed class OnceItem : IHasCheckRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New to-do";
    public string? Url { get; set; }
    public string CategoryId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public int PinSlot { get; set; }
    public FolderCheckRule CheckRule { get; set; } = FolderCheckRule.Inherit;
    public double CheckReshowHours { get; set; } = 24;
    public bool CheckReshowInHours { get; set; }
    public TaskColors? Colors { get; set; }
}

public sealed class EventItem : IHasCheckRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New scheduled";
    public string? Url { get; set; }
    public string CategoryId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? At { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int PinSlot { get; set; }

    /// <summary>Days between check-ins when there is no date. 0 or less means weekly.</summary>
    public int CheckEveryDays { get; set; } = 7;
    public FolderCheckRule CheckRule { get; set; } = FolderCheckRule.Inherit;
    public double CheckReshowHours { get; set; } = 24;
    public bool CheckReshowInHours { get; set; }

    /// <summary>Hours before At to show on the overlay. 0 uses Settings.</summary>
    public double ShowBeforeHours { get; set; }
    public bool ShowBeforeInHours { get; set; }

    /// <summary>Hours after last check before an unknown-date event appears. 0 means always show.</summary>
    public double ShowAfterCheckHours { get; set; }
    public bool ShowAfterCheckInHours { get; set; }

    /// <summary>When true, forces the overlay out of temporary hide this long before the item is due, until it is completed.</summary>
    // auto model
    public bool Important { get; set; }
    public double ImportantLeadHours { get; set; } = 1;
    public bool ImportantLeadInHours { get; set; } = true;

    public TaskColors? Colors { get; set; }
}

public sealed class NoteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Note";
    public string? Url { get; set; }
    public string Body { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public bool Hidden { get; set; }
    public int PinSlot { get; set; }
}

public sealed class WindowBounds
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 420;
    public WindowBounds Clone() => new()
    {
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height
    };

    public static bool CloseTo(WindowBounds? a, WindowBounds? b)
    {
        if (a == null && b == null)
            return true;
        if (a == null || b == null)
            return false;
        return Near(a.Left, b.Left) && Near(a.Top, b.Top) && Near(a.Width, b.Width) && Near(a.Height, b.Height);
    }

    static bool Near(double? a, double? b) =>
        a is double x && b is double y ? Math.Abs(x - y) < 0.5 : a == null && b == null;

    static bool Near(double a, double b) => Math.Abs(a - b) < 0.5;
}

public sealed class AppSettings
{
    public double Opacity { get; set; } = 0.78;
    public double PanelDarkness { get; set; } = 0.75;
    public bool? PanelDither { get; set; }
    public double PanelDitherStrength { get; set; } = 0.42;
    public bool ClickThrough { get; set; } = true;
    public bool Autostart { get; set; }
    public bool Notifications { get; set; }
    public bool HasSeenWelcome { get; set; }
    public double FontSize { get; set; } = 14;
    public bool BoldText { get; set; }
    public string UncompletedColor { get; set; } = "#B3B3B3";
    public string ColorTodo { get; set; } = "";
    public string ColorRecurring { get; set; } = "";
    public string ColorScheduled { get; set; } = "";
    public string ColorLate1 { get; set; } = "";
    public string ColorLate2 { get; set; } = "";
    public string ColorLate3 { get; set; } = "";
    public string ColorDue { get; set; } = "";
    public string ColorCompleted { get; set; } = "";
    public bool TextBackdrop { get; set; }
    public double TextBackdropOpacity { get; set; } = 0.82;
    public bool ShowWindowBorder { get; set; } = true;
    public bool TextOutline { get; set; } = true;
    public double TextOutlineStrength { get; set; } = 4;
    public bool HideCompleted { get; set; }
    public bool HideCompletedOnce { get; set; }
    public bool HideCompletedRecurring { get; set; }
    public bool HideCompletedScheduled { get; set; }
    public bool HideEmptyFolders { get; set; }
    public bool ShowFolderCounts { get; set; }
    public int FolderIndent { get; set; } = 14;
    public bool ManualSort { get; set; }
    public int EventHorizonDays { get; set; }
    public int RecurringHorizon { get; set; }
    public bool HorizonInHours { get; set; }
    public double TempHideHours { get; set; } = 2;
    public bool TempHideLeadInHours { get; set; } = true;
    public double TempHideMaxHours { get; set; } = 2;
    public bool TempHideMaxInHours { get; set; } = true;
    public double TempHideWarnSeconds { get; set; } = 60;
    public bool TempHideWarnInHours { get; set; }
    public OverlayCorner TempHideWarnCorner { get; set; } = OverlayCorner.BottomRight;
    public List<string>? SettingsOpen { get; set; }
    [JsonIgnore]
    public bool TempHideMain { get; set; }

    [JsonIgnore]
    public DateTime? TempHideUntil { get; set; }
    public WindowBounds Reminders { get; set; } = new() { Width = 340, Height = 460 };
    public List<string> TaskOrder { get; set; } = [];
    public List<IgnoredPairTarget> IgnoredPairTargets { get; set; } = [];
    public DesktopLayout? Desktop { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppLayoutHide
{
    Unchanged,
    Main,
    All
}

public sealed class DesktopLayout
{
    public bool ClickThrough { get; set; }
    [JsonIgnore]
    public bool HiddenAll { get; set; }
    [JsonIgnore]
    public bool UserHidMain { get; set; }
    public WindowBounds Reminders { get; set; } = new() { Width = 340, Height = 460 };
    public List<DesktopFolderState> Folders { get; set; } = [];
}

public sealed class DesktopFolderState
{
    public string Id { get; set; } = "";
    public bool PoppedOut { get; set; }
    public bool Collapsed { get; set; }
    public WindowBounds Bounds { get; set; } = new() { Width = 340, Height = 320 };
}

public sealed class AppLayout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ExePath { get; set; } = "";
    public string ExeName { get; set; } = "";
    public AppLayoutHide Hide { get; set; } = AppLayoutHide.Unchanged;
    public bool ClickThrough { get; set; }
    public WindowBounds? MainBounds { get; set; }
    public List<AppLayoutFolder> Folders { get; set; } = [];
}

public sealed class AppLayoutFolder
{
    public string Id { get; set; } = "";
    public bool? PoppedOut { get; set; }
    public bool? Collapsed { get; set; }
    public WindowBounds? Bounds { get; set; }
}

public enum TaskColorState
{
    Idle,
    Late1,
    Late2,
    Late3,
    Due,
    Completed
}

public sealed class TaskColors
{
    public string? Idle { get; set; }
    public string? Late1 { get; set; }
    public string? Late2 { get; set; }
    public string? Late3 { get; set; }
    public string? Due { get; set; }
    public string? Completed { get; set; }

    public string? Get(TaskColorState state) => state switch
    {
        TaskColorState.Idle => Idle,
        TaskColorState.Late1 => Late1,
        TaskColorState.Late2 => Late2,
        TaskColorState.Late3 => Late3,
        TaskColorState.Due => Due,
        TaskColorState.Completed => Completed,
        _ => null
    };

    public void Set(TaskColorState state, string? hex)
    {
        switch (state)
        {
            case TaskColorState.Idle: Idle = hex; break;
            case TaskColorState.Late1: Late1 = hex; break;
            case TaskColorState.Late2: Late2 = hex; break;
            case TaskColorState.Late3: Late3 = hex; break;
            case TaskColorState.Due: Due = hex; break;
            case TaskColorState.Completed: Completed = hex; break;
        }
    }

    public bool HasAny =>
        !string.IsNullOrWhiteSpace(Idle)
        || !string.IsNullOrWhiteSpace(Late1)
        || !string.IsNullOrWhiteSpace(Late2)
        || !string.IsNullOrWhiteSpace(Late3)
        || !string.IsNullOrWhiteSpace(Due)
        || !string.IsNullOrWhiteSpace(Completed);
}

public sealed class IgnoredPairTarget
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
}

public sealed class AppDataFile
{
    public AppSettings Settings { get; set; } = new();
    public List<Category> Categories { get; set; } = [];
    public List<OnceItem> Once { get; set; } = [];
    public List<TodoItem> Todos { get; set; } = [];
    public List<EventItem> Events { get; set; } = [];
    public List<NoteItem> Notes { get; set; } = [];
    public List<AppLayout> AppLayouts { get; set; } = [];
}
