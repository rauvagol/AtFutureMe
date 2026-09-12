using AtFutureMe.Models;

namespace AtFutureMe.Services;

static class TaskPalette
{
    public static TaskColorState[] States(string kind, bool unknownDate)
    {
        if (kind == "once")
            return [TaskColorState.Idle, TaskColorState.Completed];
        if (kind == "event" && !unknownDate)
            return [TaskColorState.Idle, TaskColorState.Due, TaskColorState.Completed];
        return
        [
            TaskColorState.Idle,
            TaskColorState.Late1,
            TaskColorState.Late2,
            TaskColorState.Late3,
            TaskColorState.Completed
        ];
    }

    public static string Label(string kind, bool unknownDate, TaskColorState state, int checkEveryDays = 1)
    {
        var waiting = unknownDate;
        var step = RecurrenceEngine.ClampCheckEveryDays(checkEveryDays);
        return state switch
        {
            TaskColorState.Idle when kind == "once" => "Incomplete",
            TaskColorState.Idle when kind == "todo" && waiting => "Just checked",
            TaskColorState.Idle when kind == "todo" => "Incomplete (not late)",
            TaskColorState.Idle when kind == "event" && waiting => "Just checked",
            TaskColorState.Idle => "Upcoming",
            TaskColorState.Late1 when waiting => MissedCheckLabel(1, step, false),
            TaskColorState.Late1 => "1 missed cycle",
            TaskColorState.Late2 when waiting => MissedCheckLabel(2, step, false),
            TaskColorState.Late2 => "2 missed cycles",
            TaskColorState.Late3 when waiting => MissedCheckLabel(3, step, true),
            TaskColorState.Late3 => "3+ missed cycles",
            TaskColorState.Due => "Due",
            TaskColorState.Completed => "Completed",
            _ => state.ToString()
        };
    }

    static string MissedCheckLabel(int n, int step, bool plus)
    {
        var count = plus ? $"{n}+" : n.ToString();
        if (step == 1)
            return n == 1 && !plus ? "1 day since check" : $"{count} days since check";
        if (step == 7)
            return n == 1 && !plus ? "1 week since check" : $"{count} weeks since check";
        return n == 1 && !plus ? "1 missed check" : $"{count} missed checks";
    }

    public static string Global(AppSettings settings, string kind, TaskColorState state)
    {
        var hex = state switch
        {
            TaskColorState.Idle when kind == "once" => settings.ColorTodo,
            TaskColorState.Idle when kind == "todo" => settings.ColorRecurring,
            TaskColorState.Idle => settings.ColorScheduled,
            TaskColorState.Late1 => settings.ColorLate1,
            TaskColorState.Late2 => settings.ColorLate2,
            TaskColorState.Late3 => settings.ColorLate3,
            TaskColorState.Due => settings.ColorDue,
            TaskColorState.Completed => settings.ColorCompleted,
            _ => ColorText.Idle
        };
        var fallback = state switch
        {
            TaskColorState.Late1 => ColorText.Late1,
            TaskColorState.Late2 => ColorText.Late2,
            TaskColorState.Late3 => ColorText.Late3,
            TaskColorState.Due => ColorText.Due,
            TaskColorState.Completed => ColorText.Completed,
            _ => ColorText.Idle
        };
        return ColorText.NormalizeOr(hex, fallback);
    }

    public static string Resolve(AppSettings settings, string kind, TaskColorState state, TaskColors? item)
    {
        if (ColorText.TryNormalize(item?.Get(state), out var hex))
            return hex;
        return Global(settings, kind, state);
    }

    public static TaskColorState FromOverdue(bool complete, int overdue)
    {
        if (complete)
            return TaskColorState.Completed;
        return overdue switch
        {
            0 => TaskColorState.Idle,
            1 => TaskColorState.Late1,
            2 => TaskColorState.Late2,
            _ => TaskColorState.Late3
        };
    }
}
