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

    // auto model: 1 = first among siblings (tasks + folders), 0 = unpinned
    public int PinSlot { get; set; }

    // auto model: when hidden, still reserve this pin so later pins stay ahead of unpinned items
    public bool HoldPinWhenHidden { get; set; }

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

    // auto model: when hidden, still reserve this pin so later pins stay ahead of unpinned items
    public bool HoldPinWhenHidden { get; set; }

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
    // auto model
    public bool HoldPinWhenHidden { get; set; }
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
    // auto model
    public bool HoldPinWhenHidden { get; set; }

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
    // auto model
    public bool HoldPinWhenHidden { get; set; }
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
    /// <summary>Left inset for root-level folder blocks on the overlay.</summary>
    // auto model
    public int TopFolderIndent { get; set; }
    /// <summary>Left inset for tasks/notes inside a folder, so they read apart from root-level tasks under that folder.</summary>
    // auto model
    public int FolderContentIndent { get; set; } = 12;
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
    /// <summary>Last selected Configure tab index (Layout, Tasks, …).</summary>
    // auto model
    public int ConfigTabIndex { get; set; }
    [JsonIgnore]
    public bool TempHideMain { get; set; }

    [JsonIgnore]
    public DateTime? TempHideUntil { get; set; }
    public WindowBounds Reminders { get; set; } = new() { Width = 340, Height = 460 };
    // auto model: optional Now-at-top event clock
    public bool ShowClockModule { get; set; }
    public WindowBounds Clock { get; set; } = new() { Width = 280, Height = 300 };
    /// <summary>How far ahead the clock face covers, in hours.</summary>
    public double ClockHorizonHours { get; set; } = 24;
    /// <summary>True when the Clock tab edits the horizon in hours; false means days.</summary>
    public bool ClockHorizonInHours { get; set; } = true;
    /// <summary>When true, rim labels use 12-hour clock; otherwise 24-hour.</summary>
    public bool ClockUse12Hour { get; set; }
    /// <summary>Minor tick spacing on the rim.</summary>
    public ClockTickStep ClockTickStep { get; set; } = ClockTickStep.Hour;
    /// <summary>How far inward from the rim each tick class extends, as a fraction of radius (snapped to 1%).</summary>
    // auto model
    public double ClockTickLengthHour { get; set; } = 0.16;
    public double ClockTickLengthThirty { get; set; } = 0.12;
    public double ClockTickLengthFifteen { get; set; } = 0.10;
    public double ClockTickLengthMinute { get; set; } = 0.06;
    /// <summary>Second hand length as a fraction of rim radius (snapped to 1%).</summary>
    // auto model
    public double ClockSecondHandLength { get; set; } = 0.78;
    /// <summary>Task wedge angular width as a fraction of the full ring (snapped to 1%).</summary>
    // auto model
    public double ClockWedgeWidth { get; set; } = 0.02;
    public bool ClockShowEventLabels { get; set; }
    public bool ClockShowRecurring { get; set; } = true;
    public bool ClockShowScheduled { get; set; } = true;
    /// <summary>When locked, only the clock face shows (no chrome/panel) and clicks pass through.</summary>
    // auto model
    public ClockLockMode ClockLockMode { get; set; } = ClockLockMode.Locked;
    /// <summary>When locked/semi-locked, keep a darkened disk behind the face instead of a fully transparent window.</summary>
    // auto model
    public bool ClockKeepFaceWhenLocked { get; set; }
    /// <summary>Show the sweeping second hand (0 at top, clockwise).</summary>
    // auto model
    public bool ClockShowSecondHand { get; set; } = true;
    /// <summary>Show a fixed vertical hand pointing at Now (straight up).</summary>
    // auto model
    public bool ClockShowHourHand { get; set; }
    /// <summary>0 = time just under the Now chevron; 1 = near the center of the face.</summary>
    // auto model
    public double ClockTimePosition { get; set; }
    /// <summary>Font size for the current-time label on the clock face.</summary>
    // auto model
    public double ClockTimeFontSize { get; set; } = 13;
    /// <summary>Hour (Now) hand length as a fraction of rim radius (snapped to 1%).</summary>
    // auto model
    public double ClockHourHandLength { get; set; } = 0.55;
    // auto model: clock face + event colors (independent of overlay task colors)
    public string ClockColorRecurring { get; set; } = "#8AB4F8";
    public string ClockColorScheduled { get; set; } = "#FFD24D";
    public string ClockColorDue { get; set; } = "#5DDC8A";
    public string ClockColorCaret { get; set; } = "#8AB4F8";
    public string ClockColorRing { get; set; } = "#C5C9D0";
    public string ClockColorMajorTick { get; set; } = "#C5C9D0";
    public string ClockColorMinorTick { get; set; } = "#9AA0A8";
    public string ClockColorSecondHand { get; set; } = "#FF8A65";
    public string ClockColorHourHand { get; set; } = "#E8EAED";
    public string ClockColorWedgeLine { get; set; } = "#FFFFFF";
    public string ClockColorTime { get; set; } = "#E8EAED";
    public string ClockColorTimeOutline { get; set; } = "#000000";
    /// <summary>Draw an outline behind the current-time label.</summary>
    // auto model
    public bool ClockTimeOutline { get; set; }
    /// <summary>Outline thickness in device-independent pixels (1–6).</summary>
    // auto model
    public int ClockTimeOutlineStrength { get; set; } = 2;
    public string ClockColorFace { get; set; } = "";
    /// <summary>When true, fill the clock face with ClockColorAttention while a recurring/scheduled task is due.</summary>
    // auto model
    public bool ClockAttentionTint { get; set; }
    /// <summary>Face fill while a recurring or scheduled task needs attention (due / overdue).</summary>
    // auto model
    public string ClockColorAttention { get; set; } = "#FF8A65";

    // auto model: aesthetics — concentric planetary drive (face ↔ second hand)
    /// <summary>Off, or concentric multi-stage planetary underlay.</summary>
    public ClockGearDriveMode ClockGearDriveMode { get; set; } = ClockGearDriveMode.Off;
    /// <summary>Compound tooth counts for the selected catalog train (empty = Off / legacy enum).</summary>
    // auto model
    public List<int> ClockGearCompounds { get; set; } = [];
    /// <summary>Show RPM / ratio debug labels on the concentric gear scaffold.</summary>
    // auto model
    public bool ClockGearDebugLabels { get; set; }
    /// <summary>Draw a circle on each ring gear at its solid-rim midpoint (mask debug).</summary>
    // auto model
    public bool ClockGearDebugRingCenter { get; set; }
    /// <summary>Hide carriage rails, braces, and pins. Planets stay.</summary>
    // auto model
    public bool ClockGearHideCarousel { get; set; }
    /// <summary>Multiply planetary animation vs wall-clock (1–1440). Does not affect hands.</summary>
    // auto model
    public double ClockGearAnimSpeed { get; set; } = 1;
    /// <summary>Planet count per gap (outer→1st, 1st→2nd, 2nd→3rd, 3rd→sun). Length 4.</summary>
    // auto model
    public List<int> ClockGearPlanetCounts { get; set; } = [3, 3, 3, 3];
    /// <summary>Planet constellation rotation per gap in degrees (0–360). Length 4.</summary>
    // auto model
    public List<double> ClockGearPlanetOffsetsDeg { get; set; } = [0, 0, 0, 0];
    /// <summary>Looks-only carousel of each ring's planets (deg/min, signed).</summary>
    // auto model
    public List<double> ClockGearCarrierDegPerMin { get; set; } = [0, 0, 0, 0, 0];
    /// <summary>
    /// Null/true: equalize carousel orbit speed, keep outer face rate and
    /// center sun at 1 rpm. False: use ClockGearCarrierDegPerMin.
    /// </summary>
    // auto model
    public bool? ClockGearAutoCarrier { get; set; }
    /// <summary>Per-ring carousel direction when auto-carrier is on.</summary>
    // auto model
    public List<ClockGearCarrierDir> ClockGearCarrierDirs { get; set; } = [];
    /// <summary>Floor on auto carousel |ω| in deg/min (0 = no floor). Unlocked rings only.</summary>
    // auto model
    public double ClockGearCarrierMinDegPerMin { get; set; }
    /// <summary>When auto-carrier is on, lock this ring to ClockGearCarrierDegPerMin.</summary>
    // auto model
    public List<bool> ClockGearCarrierPinned { get; set; } = [];
    /// <summary>Integer compound/idler size multiplier per ring (1 = stock train).</summary>
    // auto model
    public List<double> ClockGearCompoundScale { get; set; } = [1, 1, 1, 1, 1];
    /// <summary>Always 1 — kept for save compat (2-idler pairs are not assemblable).</summary>
    // auto model
    public int ClockGearIdlersPerCompound { get; set; } = 1;
    /// <summary>Idler seat angle per gap (0° = toward center). Length 4; [0] is outer→1st.</summary>
    // auto model
    public List<double> ClockGearIdlerOffsetsDeg { get; set; } = [0, 0, 0, 0];
    /// <summary>Compat alias for <see cref="ClockGearIdlerOffsetsDeg"/>[0].</summary>
    // auto model
    public double ClockGearIdlerOffsetDeg
    {
        get => ClockGearIdlerOffsetsDeg is { Count: > 0 } ? ClockGearIdlerOffsetsDeg[0] : 0;
        set
        {
            ClockGearIdlerOffsetsDeg ??= [0, 0, 0, 0];
            while (ClockGearIdlerOffsetsDeg.Count < 4)
                ClockGearIdlerOffsetsDeg.Add(0);
            ClockGearIdlerOffsetsDeg[0] = value;
        }
    }
    public string ClockGearColor { get; set; } = "#7A8088";
    /// <summary>Compound fill. Blank = darkened ring color.</summary>
    // auto model
    public string ClockGearCompoundColor { get; set; } = "";
    /// <summary>Small planet pinion. Blank = ring color.</summary>
    // auto model
    public string ClockGearPlanetColor { get; set; } = "";
    /// <summary>Idler fill. Blank = ring color.</summary>
    // auto model
    public string ClockGearIdlerColor { get; set; } = "";
    /// <summary>Center sun (drives the second hand). Blank = darkened ring color.</summary>
    // auto model
    public string ClockGearSunColor { get; set; } = "";
    /// <summary>Opaque carrier rails and braces.</summary>
    // auto model
    public string ClockGearCarriageColor { get; set; } = "#C4A882";
    /// <summary>Per-annulus carriage frame. Empty = copy legacy scalars onto every ring.</summary>
    // auto model
    public List<ClockGearCarriageStage> ClockGearCarriageStages { get; set; } = [];
    /// <summary>Legacy rail stroke; used only to seed empty ClockGearCarriageStages.</summary>
    // auto model
    public double ClockGearCarriageRail { get; set; } = 4;
    /// <summary>Legacy brace stroke; used only to seed empty ClockGearCarriageStages.</summary>
    // auto model
    public double ClockGearCarriageRung { get; set; } = 2.4;
    /// <summary>Legacy brace sweep; used only to seed empty ClockGearCarriageStages.</summary>
    // auto model
    public double ClockGearCarriageSweepDeg { get; set; } = 18;
    /// <summary>Legacy brace direction; used only to seed empty ClockGearCarriageStages.</summary>
    // auto model
    public bool ClockGearCarriageReverse { get; set; }
    /// <summary>Legacy axle pins; used only to seed empty ClockGearCarriageStages.</summary>
    // auto model
    public bool? ClockGearCarriagePins { get; set; }
    /// <summary>Legacy hub reinforcement; used only to seed empty ClockGearCarriageStages.</summary>
    // auto model
    public double ClockGearCarriageHub { get; set; }
    /// <summary>0–1 train opacity. Outer drive teeth and center sun stay full strength.</summary>
    public double ClockGearOpacity { get; set; } = 0.42;
    /// <summary>Tooth radial depth as a fraction of that gear's pitch radius.</summary>
    public double ClockGearToothDepthFrac { get; set; } = 0.14;
    /// <summary>Fraction of each pitch angle taken by the tooth tip.</summary>
    public double ClockGearToothWidthFrac { get; set; } = 0.48;

    /// <summary>When true, every folder is included; otherwise only ClockCategoryIds.</summary>
    public bool ClockAllCategories { get; set; } = true;
    public List<string> ClockCategoryIds { get; set; } = [];

    // auto model: AI usage tracker overlay (Claude + Cursor)
    public bool ShowUsageModule { get; set; }
    public WindowBounds Usage { get; set; } = new() { Width = 280, Height = 320 };
    public UsageLockMode UsageLockMode { get; set; } = UsageLockMode.Locked;
    /// <summary>When true, show usage at the bottom of the reminders list instead of a floating window.</summary>
    // auto model
    public bool UsageDockInReminders { get; set; }
    /// <summary>Docked compact rows: use idle gray for % / time instead of usage tint.</summary>
    // auto model
    public bool UsageDockPlainGray { get; set; }
    /// <summary>Docked compact rows: use idle gray for the usage bar fill instead of usage tint.</summary>
    // auto model
    public bool UsageDockPlainGrayBar { get; set; }
    /// <summary>Docked compact rows: dim + strikethrough when a meter is fully used (unavailable until reset).</summary>
    // auto model
    public bool UsageDockDimAtLimit { get; set; }
    /// <summary>When dim-at-limit is on, Claude week also dims if Claude 5h is at 100%.</summary>
    // auto model
    public bool UsageDockWeekInherit5hDim { get; set; }
    /// <summary>Docked compact rows: show used percent before time-to-reset.</summary>
    // auto model
    public bool UsageDockShowPercent { get; set; } = true;
    /// <summary>Minutes between usage API polls (clamped 5–120).</summary>
    public int UsagePollMinutes { get; set; } = 15;
    public bool UsageShowClaude { get; set; } = true;
    public bool UsageShowCursor { get; set; } = true;
    /// <summary>Inject reset times onto the reminders board as scheduled-like rows.</summary>
    public bool UsageInjectReminders { get; set; } = true;
    /// <summary>When true, board rows only appear at/near limit or when remaining capacity may go unused before reset.</summary>
    // auto model
    public bool UsageBoardSmartFilter { get; set; } = true;
    /// <summary>Ballpark: one full 5h burn as a percent of the weekly bar (default 10).</summary>
    // auto model
    public double UsageFullSessionWeeklyPercent { get; set; } = 10;
    /// <summary>Multiplier for partial sessions when estimating alert horizon (default 2 → 30% left ≈ 30h).</summary>
    // auto model
    public double UsageReminderCushion { get; set; } = 2;
    /// <summary>When a meter is fully used, only show on the board within this many hours of reset.</summary>
    // auto model
    public double UsageAtLimitLeadHours5h { get; set; } = 1;
    public double UsageAtLimitLeadHoursWeekly { get; set; } = 12;
    public double UsageAtLimitLeadHoursOpus { get; set; } = 12;
    public double UsageAtLimitLeadHoursCursor { get; set; } = 24;
    /// <summary>When a meter is fully used, only show on the clock within this many hours of reset.</summary>
    // auto model
    public double UsageClockAtLimitLeadHours5h { get; set; } = 5;
    public double UsageClockAtLimitLeadHoursWeekly { get; set; } = 48;
    public double UsageClockAtLimitLeadHoursOpus { get; set; } = 48;
    public double UsageClockAtLimitLeadHoursCursor { get; set; } = 72;
    /// <summary>Show reset markers on the clock ring (usage-tinted wedges).</summary>
    public bool UsageShowOnClock { get; set; } = true;
    public string UsageColorClaude { get; set; } = "#D4A574";
    public string UsageColorCursor { get; set; } = "#8AB4F8";
    /// <summary>How each usage reset row is colored on the reminders board.</summary>
    // auto model
    public UsageBoardColorMode UsageBoardColorMode5h { get; set; } = UsageBoardColorMode.UsageTint;
    public UsageBoardColorMode UsageBoardColorModeWeekly { get; set; } = UsageBoardColorMode.UsageTint;
    public UsageBoardColorMode UsageBoardColorModeOpus { get; set; } = UsageBoardColorMode.UsageTint;
    public UsageBoardColorMode UsageBoardColorModeCursor { get; set; } = UsageBoardColorMode.UsageTint;
    public string UsageBoardColor5h { get; set; } = "#D4A574";
    public string UsageBoardColorWeekly { get; set; } = "#D4A574";
    public string UsageBoardColorOpus { get; set; } = "#D4A574";
    public string UsageBoardColorCursor { get; set; } = "#8AB4F8";

    public List<string> TaskOrder { get; set; } = [];
    public List<IgnoredPairTarget> IgnoredPairTargets { get; set; } = [];
    public DesktopLayout? Desktop { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UsageBoardColorMode
{
    /// <summary>Green→red by used percent.</summary>
    UsageTint,
    /// <summary>Fixed custom color.</summary>
    Fixed,
    /// <summary>Same default idle color as other scheduled rows.</summary>
    Default
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UsageLockMode
{
    Locked,
    SemiLocked,
    Unlocked
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClockLockMode
{
    Locked,
    SemiLocked,
    Unlocked
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClockTickStep
{
    Hour,
    ThirtyMinutes,
    FifteenMinutes,
    Minute
}

/// <summary>Clock face aesthetics gear drive — Off or a locked tooth progression.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClockGearCarriageKind
{
    /// <summary>Twin orbit rails with evenly spaced swept braces.</summary>
    Orbit,
    /// <summary>Orbit rails plus a bow from each compound pin to its idler pin.</summary>
    PinLinks,
    /// <summary>Pin-to-pin bows only — no orbit rings.</summary>
    Skeleton,
    /// <summary>Filled C-arms wrapping each compound/idler pair.</summary>
    Cradles,
    /// <summary>Orbit rails with X-lattice bracing between them.</summary>
    Truss,
    /// <summary>Helical ribbons winding between the two orbits.</summary>
    Helix,
    /// <summary>Several pin-to-pin bows fanned around each pair.</summary>
    Fan
}

/// <summary>Carriage look for one annulus.</summary>
// auto model
public sealed class ClockGearCarriageStage
{
    public ClockGearCarriageKind Kind { get; set; } = ClockGearCarriageKind.Orbit;
    public double Rail { get; set; } = 4;
    public double Rung { get; set; } = 2.4;
    public double SweepDeg { get; set; } = 18;
    public bool Reverse { get; set; }
    public bool Pins { get; set; } = true;
    public double Hub { get; set; }
    /// <summary>Extra copies of the pin-link template, evenly spaced to the next planet (0 = template only).</summary>
    public int LinkRepeats { get; set; }
    /// <summary>0 = own idler; 1 = next planet’s idler, wrapping around the ring.</summary>
    public int LinkStride { get; set; }

    public ClockGearCarriageStage Clone() => new()
    {
        Kind = Kind,
        Rail = Rail,
        Rung = Rung,
        SweepDeg = SweepDeg,
        Reverse = Reverse,
        Pins = Pins,
        Hub = Hub,
        LinkRepeats = LinkRepeats,
        LinkStride = LinkStride
    };

    public ClockGearCarriageStage Clamped()
    {
        if (!Enum.IsDefined(Kind))
            Kind = ClockGearCarriageKind.Orbit;
        Rail = Math.Clamp(Rail <= 0 ? 4 : Rail, 1, 12);
        Rung = Math.Clamp(Rung <= 0 ? 2.4 : Rung, 1, 10);
        SweepDeg = Math.Clamp(SweepDeg, 0, 45);
        Hub = Math.Clamp(Hub, 0, 14);
        LinkRepeats = Math.Clamp(LinkRepeats, 0, 8);
        LinkStride = Math.Clamp(LinkStride, 0, 6);
        return this;
    }

    public static ClockGearCarriageStage FromLegacy(AppSettings s) => new ClockGearCarriageStage
    {
        Kind = ClockGearCarriageKind.Orbit,
        Rail = s.ClockGearCarriageRail,
        Rung = s.ClockGearCarriageRung,
        SweepDeg = s.ClockGearCarriageSweepDeg,
        Reverse = s.ClockGearCarriageReverse,
        Pins = s.ClockGearCarriagePins != false,
        Hub = s.ClockGearCarriageHub
    }.Clamped();

    public static ClockGearCarriageStage At(AppSettings settings, int stage)
    {
        var list = settings.ClockGearCarriageStages;
        if (list != null && stage >= 0 && stage < list.Count && list[stage] != null)
            return list[stage];
        return FromLegacy(settings);
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClockGearCarrierDir
{
    Auto,
    Clockwise,
    Counterclockwise,
    /// <summary>Park this carousel (ω_c = 0).</summary>
    Still
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClockGearDriveMode
{
    Off,
    /// <summary>Legacy name: 96 → 72 → 36 (×10 / ×6 / ×6), first assemblable default.</summary>
    Concentric,
    Teeth144_96_64_32,
    Teeth144_108_72_36,
    Teeth144_120_90_60,
    Teeth144_120_80_60,
    Teeth144_144_108_72,
    Teeth144_144_120_80
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
