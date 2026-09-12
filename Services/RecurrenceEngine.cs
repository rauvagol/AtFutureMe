using AtFutureMe.Models;

namespace AtFutureMe.Services;

public readonly record struct TodoEvaluation(
    bool Visible,
    bool IsComplete,
    int OverdueCount,
    DateTime? NextChange);

public static class RecurrenceEngine
{
    public static TimeSpan GetTime(Recurrence r)
    {
        if (TimeSpan.TryParse(r.Time, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
            return t;
        return TimeSpan.Zero;
    }

    public static DateTime Combine(DateTime date, TimeSpan time) => date.Date + time;

    public static TodoEvaluation EvaluateTodo(TodoItem todo, DateTime now)
    {
        if (!todo.Enabled)
            return new TodoEvaluation(false, false, 0, null);

        if (todo.UnknownDate)
        {
            if (todo.CompletedAt != null)
                return new TodoEvaluation(true, true, 0, null);
            var checks = OverdueChecks(LastChecked(todo), now, TodoCheckEveryDays(todo));
            return new TodoEvaluation(true, false, checks, null);
        }

        if (todo.Recurrence.Kind == RecurrenceKind.AfterCompletionHours)
            return EvaluateAfterCompletionHours(todo, now);
        if (todo.Recurrence.Kind == RecurrenceKind.AfterCompletion)
            return EvaluateAfterCompletion(todo, now);

        var firstDue = FirstDue(todo);
        var nextAfterNow = OnOrAfter(todo, now.AddTicks(1));

        if (now < firstDue)
            return new TodoEvaluation(true, false, 0, firstDue);

        var periodStart = OnOrBefore(todo, now) ?? firstDue;
        if (periodStart < FirstPeriodStart(todo))
            periodStart = FirstPeriodStart(todo);

        var complete = todo.CompletedAt is DateTime done && done >= periodStart;
        var overdue = 0;
        if (!complete)
        {
            DateTime missedFrom;
            if (todo.CompletedAt is DateTime lastDone)
            {
                missedFrom = OnOrAfter(todo, lastDone.AddTicks(1));
            }
            else
            {
                missedFrom = FirstPeriodStart(todo);
            }

            overdue = CountOccurrences(todo, missedFrom, periodStart);
        }

        return new TodoEvaluation(true, complete, Math.Max(0, overdue), nextAfterNow);
    }

    public static bool TryTimeline(TodoItem todo, DateTime now, out DateTime at, out bool active)
    {
        at = default;
        active = false;
        var state = EvaluateTodo(todo, now);
        if (!todo.Enabled || !state.Visible)
            return false;
        if (todo.UnknownDate)
        {
            if (state.IsComplete || state.OverdueCount <= 0)
                return false;
            at = now;
            active = true;
            return true;
        }

        if (state.IsComplete)
        {
            if (state.NextChange is not DateTime next)
                return false;
            at = next;
            return true;
        }

        if (todo.Recurrence.Kind is RecurrenceKind.AfterCompletion or RecurrenceKind.AfterCompletionHours)
        {
            at = now;
            active = true;
            return true;
        }

        var first = FirstDue(todo);
        if (now < first)
        {
            at = first;
            return true;
        }

        at = now;
        active = true;
        return true;
    }

    public static bool TryTimeline(EventItem ev, DateTime now, out DateTime at, out bool active)
    {
        at = default;
        active = false;
        if (ev.CompletedAt != null)
            return false;
        if (ev.At is DateTime when)
        {
            if (when <= now)
            {
                at = now;
                active = true;
            }
            else
                at = when;
            return true;
        }

        if (OverdueChecks(LastChecked(ev), now, EventCheckEveryDays(ev)) <= 0)
            return false;
        at = now;
        active = true;
        return true;
    }

    public static string FormatCountdown(DateTime at, DateTime now)
    {
        var t = at - now;
        if (t.TotalSeconds <= 0)
            return "now";
        if (t.TotalHours >= 48)
            return $"{(int)t.TotalDays}d";
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h";
        if (t.TotalMinutes >= 10)
            return $"{(int)t.TotalMinutes}m";
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    public static string FormatRemainingLong(DateTime at, DateTime now)
    {
        var t = at - now;
        if (t.TotalSeconds <= 0)
            return "now";
        if (t.TotalHours >= 48)
            return t.Hours == 0 ? $"{(int)t.TotalDays}d" : $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1)
            return t.Minutes == 0 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}m";
        return "now";
    }

    public static string FormatElapsed(DateTime since, DateTime now) =>
        $"+{DaysSince(since, now)}d";

    public static string FormatTodoMeta(TodoItem todo, DateTime now, TodoEvaluation state)
    {
        if (todo.UnknownDate)
            return FormatElapsed(LastChecked(todo), now);
        if (state.IsComplete && state.NextChange is DateTime next)
            return FormatCountdown(next, now);
        return "";
    }

    public const double MinShowHours = 1.0 / 60;
    // TimeSpan.FromHours throws above this. Not a product cap.
    public static readonly double MaxShowHours = TimeSpan.MaxValue.TotalHours - 24;

    public static double ClampShowHours(double hours, bool allowZero)
    {
        if (double.IsNaN(hours) || double.IsInfinity(hours) || hours < 0)
            return allowZero ? 0 : MinShowHours;
        if (allowZero && hours == 0)
            return 0;
        return Math.Clamp(hours, MinShowHours, MaxShowHours);
    }

    public static TimeSpan WindowFromHours(double hours) =>
        TimeSpan.FromHours(ClampShowHours(hours, false));

    public static bool BeyondHorizon(DateTime at, DateTime now, int amount, bool hours)
    {
        if (amount <= 0 || at <= now)
            return false;
        var limit = hours ? now.AddHours(amount) : now.AddDays(amount);
        return at > limit;
    }

    public static bool BeyondWindow(DateTime at, DateTime now, TimeSpan window)
    {
        if (window <= TimeSpan.Zero || at <= now)
            return false;
        return at - now > window;
    }

    public static bool HideRecurringByHorizon(TodoItem todo, DateTime now, TodoEvaluation state, int amount, bool hours)
    {
        if (amount <= 0 || todo.UnknownDate || !state.Visible || state.IsComplete)
            return false;
        DateTime? at = null;
        if (state.OverdueCount == 0)
        {
            var first = FirstDue(todo);
            if (now < first)
                at = first;
        }

        return at is DateTime when && BeyondHorizon(when, now, amount, hours);
    }

    public static int DaysSince(DateTime since, DateTime now) =>
        Math.Max(0, (now.Date - since.Date).Days);

    public static int ClampCheckEveryDays(int days) => Math.Clamp(days <= 0 ? 1 : days, 1, 3650);

    public static int TodoCheckEveryDays(TodoItem todo) =>
        ClampCheckEveryDays(todo.CheckEveryDays <= 0 ? 1 : todo.CheckEveryDays);

    public static int EventCheckEveryDays(EventItem ev) =>
        ClampCheckEveryDays(ev.CheckEveryDays <= 0 ? 7 : ev.CheckEveryDays);

    public static int OverdueChecks(DateTime since, DateTime now, int everyDays) =>
        DaysSince(since, now) / ClampCheckEveryDays(everyDays);

    public static (int Amount, bool Weeks) SplitCheckEvery(int days)
    {
        days = ClampCheckEveryDays(days);
        if (days % 7 == 0)
            return (days / 7, true);
        return (days, false);
    }

    public static int CombineCheckEvery(int amount, bool weeks)
    {
        amount = Math.Max(1, amount);
        return ClampCheckEveryDays(weeks ? amount * 7 : amount);
    }

    public static DateTime LastChecked(TodoItem todo) =>
        todo.LastCheckedAt ?? todo.CreatedAt;

    public static DateTime LastChecked(EventItem ev) =>
        ev.LastCheckedAt ?? ev.CreatedAt;

    public static DateTime OverlaySortAt(TodoItem todo, DateTime now, TodoEvaluation state)
    {
        if (todo.UnknownDate)
            return state.IsComplete ? DateTime.MaxValue.AddTicks(-1) : LastChecked(todo);
        if (state.IsComplete || !state.Visible)
            return state.NextChange ?? DateTime.MaxValue;
        if (state.OverdueCount > 0)
            return now.Date.AddDays(-state.OverdueCount);
        return OnOrBefore(todo, now) ?? now;
    }

    public static DateTime OverlaySortAt(EventItem ev) =>
        ev.CompletedAt != null
            ? DateTime.MaxValue.AddTicks(-2)
            : ev.At ?? LastChecked(ev);

    public static DateTime OverlaySortAt(OnceItem item) =>
        item.CompletedAt != null
            ? DateTime.MaxValue.AddTicks(-2)
            : item.CreatedAt == default ? DateTime.Now : item.CreatedAt;

    public static string DescribeLastChecked(DateTime at, DateTime now)
    {
        var days = DaysSince(at, now);
        if (days == 0)
            return $"today at {at:h:mm tt}";
        if (days == 1)
            return $"yesterday at {at:h:mm tt}";
        return $"{days} days ago ({at:MMM d})";
    }

    public static string Describe(Recurrence r)
    {
        var time = FormatTime(GetTime(r));
        var n = Math.Max(1, r.Interval);
        return r.Kind switch
        {
            RecurrenceKind.Daily => $"Daily at {time}",
            RecurrenceKind.EveryNDays => n == 1 ? $"Daily at {time}" : $"Every {n} days at {time}",
            RecurrenceKind.Weekdays => $"Weekdays at {time}",
            RecurrenceKind.Weekly => $"Weekly ({DescribeDays(r.Weekdays)}) at {time}",
            RecurrenceKind.EveryNWeeks => $"Every {n} weeks on {DayName(r.Weekday)} at {time}",
            RecurrenceKind.MonthlyDate => $"Monthly on the {Ordinal(r.MonthDay)} at {time}",
            RecurrenceKind.NthWeekday => $"{NthLabel(r.Nth)} {DayName(r.Weekday)} at {time}",
            RecurrenceKind.Yearly => $"Yearly on {MonthName(r.Month)} {r.MonthDay} at {time}",
            RecurrenceKind.AfterCompletion => n == 1 ? $"1 day after completion at {time}" : $"{n} days after completion at {time}",
            RecurrenceKind.AfterCompletionHours => n == 1 ? "1 hour after completion" : $"{n} hours after completion",
            _ => "Recurring"
        };
    }

    static TodoEvaluation EvaluateAfterCompletion(TodoItem todo, DateTime now)
    {
        var interval = Math.Max(1, todo.Recurrence.Interval);
        var time = GetTime(todo.Recurrence);

        DateTime currentDue;
        if (todo.CompletedAt is DateTime done)
        {
            currentDue = NextDueAfterCompletion(done, interval, time);
            if (now < currentDue)
                return new TodoEvaluation(true, true, 0, currentDue);

            var overdue = 0;
            var cursor = currentDue;
            var safety = 0;
            while (cursor.AddDays(interval) <= now && safety++ < 10000)
            {
                overdue++;
                cursor = cursor.AddDays(interval);
            }

            var next = cursor.AddDays(interval);
            return new TodoEvaluation(true, false, overdue, next);
        }

        currentDue = todo.CreatedAt;
        var missed = 0;
        var start = Combine(currentDue, time);
        if (start > currentDue)
            start = currentDue;

        var tick = Combine(todo.CreatedAt.Date, time);
        if (tick > todo.CreatedAt)
            tick = tick.AddDays(-interval);
        if (tick < todo.CreatedAt.Date.AddYears(-10))
            tick = todo.CreatedAt;

        var periodStart = todo.CreatedAt;
        var nextBoundary = periodStart.Date.AddDays(interval).Date + time;
        if (nextBoundary <= todo.CreatedAt)
            nextBoundary = todo.CreatedAt.AddDays(interval);

        var edge = nextBoundary;
        var guard = 0;
        while (edge <= now && guard++ < 10000)
        {
            missed++;
            edge = edge.AddDays(interval);
        }

        return new TodoEvaluation(true, false, missed, edge);
    }

    static TodoEvaluation EvaluateAfterCompletionHours(TodoItem todo, DateTime now)
    {
        var hours = Math.Max(1, todo.Recurrence.Interval);
        var interval = TimeSpan.FromHours(hours);

        if (todo.CompletedAt is DateTime done)
        {
            var currentDue = done + interval;
            if (now < currentDue)
                return new TodoEvaluation(true, true, 0, currentDue);

            return SpanOverdue(currentDue, now, interval);
        }

        return SpanOverdue(todo.CreatedAt, now, interval);
    }

    static TodoEvaluation SpanOverdue(DateTime due, DateTime now, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            interval = TimeSpan.FromHours(1);

        var overdue = (int)Math.Max(0, (now - due).Ticks / interval.Ticks);
        var next = due + TimeSpan.FromTicks(interval.Ticks * (overdue + 1L));
        return new TodoEvaluation(true, false, overdue, next);
    }

    static DateTime NextDueAfterCompletion(DateTime completedAt, int intervalDays, TimeSpan time)
    {
        var date = completedAt.Date.AddDays(intervalDays);
        var due = date + time;
        if (due <= completedAt)
            due = due.AddDays(1);
        return due;
    }

    static DateTime FirstDue(TodoItem todo)
    {
        var fromStartOfDay = OnOrAfter(todo, todo.CreatedAt.Date);
        if (fromStartOfDay.Date == todo.CreatedAt.Date)
            return fromStartOfDay;
        return OnOrAfter(todo, todo.CreatedAt);
    }

    static DateTime FirstPeriodStart(TodoItem todo) => FirstDue(todo);

    public static DateTime OnOrAfter(TodoItem todo, DateTime atOrAfter)
    {
        var r = todo.Recurrence;
        var time = GetTime(r);
        var interval = Math.Max(1, r.Interval);

        switch (r.Kind)
        {
            case RecurrenceKind.Daily:
                return NextDaily(atOrAfter, time);
            case RecurrenceKind.EveryNDays:
                return NextEveryNDays(atOrAfter, time, interval, r.Anchor ?? todo.CreatedAt);
            case RecurrenceKind.Weekdays:
                return NextWeekly(atOrAfter, time, [1, 2, 3, 4, 5]);
            case RecurrenceKind.Weekly:
                var days = r.Weekdays.Count > 0 ? r.Weekdays : [1];
                return NextWeekly(atOrAfter, time, days);
            case RecurrenceKind.EveryNWeeks:
                return NextEveryNWeeks(atOrAfter, time, interval, r.Weekday, r.Anchor ?? todo.CreatedAt);
            case RecurrenceKind.MonthlyDate:
                return NextMonthlyDate(atOrAfter, time, r.MonthDay);
            case RecurrenceKind.NthWeekday:
                return NextNthWeekday(atOrAfter, time, r.Nth, r.Weekday);
            case RecurrenceKind.Yearly:
                return NextYearly(atOrAfter, time, r.Month, r.MonthDay);
            default:
                return NextDaily(atOrAfter, time);
        }
    }

    public static DateTime? OnOrBefore(TodoItem todo, DateTime atOrBefore)
    {
        var r = todo.Recurrence;
        var time = GetTime(r);
        var interval = Math.Max(1, r.Interval);

        DateTime? previous = r.Kind switch
        {
            RecurrenceKind.Daily => PrevDaily(atOrBefore, time),
            RecurrenceKind.EveryNDays => PrevEveryNDays(atOrBefore, time, interval, r.Anchor ?? todo.CreatedAt),
            RecurrenceKind.Weekdays => PrevWeekly(atOrBefore, time, [1, 2, 3, 4, 5]),
            RecurrenceKind.Weekly => PrevWeekly(atOrBefore, time, r.Weekdays.Count > 0 ? r.Weekdays : [1]),
            RecurrenceKind.EveryNWeeks => PrevEveryNWeeks(atOrBefore, time, interval, r.Weekday, r.Anchor ?? todo.CreatedAt),
            RecurrenceKind.MonthlyDate => PrevMonthlyDate(atOrBefore, time, r.MonthDay),
            RecurrenceKind.NthWeekday => PrevNthWeekday(atOrBefore, time, r.Nth, r.Weekday),
            RecurrenceKind.Yearly => PrevYearly(atOrBefore, time, r.Month, r.MonthDay),
            _ => PrevDaily(atOrBefore, time)
        };

        return previous;
    }

    static DateTime PrevDaily(DateTime atOrBefore, TimeSpan time)
    {
        var cand = Combine(atOrBefore, time);
        return cand <= atOrBefore ? cand : cand.AddDays(-1);
    }

    static DateTime PrevEveryNDays(DateTime atOrBefore, TimeSpan time, int interval, DateTime anchor)
    {
        var start = Combine(anchor, time);
        if (atOrBefore < start)
            return start;
        var days = (atOrBefore.Date - start.Date).TotalDays;
        var steps = (int)Math.Floor(days / interval);
        var cand = start.AddDays(steps * interval);
        while (cand > atOrBefore)
            cand = cand.AddDays(-interval);
        while (cand.AddDays(interval) <= atOrBefore)
            cand = cand.AddDays(interval);
        return cand;
    }

    static DateTime? PrevWeekly(DateTime atOrBefore, TimeSpan time, IReadOnlyList<int> weekdays)
    {
        var set = weekdays.ToHashSet();
        for (var i = 0; i < 14; i++)
        {
            var day = atOrBefore.Date.AddDays(-i);
            if (!set.Contains((int)day.DayOfWeek))
                continue;
            var cand = Combine(day, time);
            if (cand <= atOrBefore)
                return cand;
        }

        return null;
    }

    static DateTime PrevEveryNWeeks(DateTime atOrBefore, TimeSpan time, int intervalWeeks, int weekday, DateTime anchor)
    {
        var first = NextEveryNWeeks(anchor, time, intervalWeeks, weekday, anchor);
        var spanWeeks = Math.Max(1, intervalWeeks) * 7;
        if (atOrBefore < first)
            return first;
        var days = (atOrBefore.Date - first.Date).TotalDays;
        var steps = (int)Math.Floor(days / spanWeeks);
        var cand = first.AddDays(steps * spanWeeks);
        while (cand > atOrBefore)
            cand = cand.AddDays(-spanWeeks);
        while (cand.AddDays(spanWeeks) <= atOrBefore)
            cand = cand.AddDays(spanWeeks);
        return cand;
    }

    static DateTime PrevMonthlyDate(DateTime atOrBefore, TimeSpan time, int monthDay)
    {
        var cand = NextMonthlyDate(new DateTime(atOrBefore.Year, atOrBefore.Month, 1).AddMonths(-2), time, monthDay);
        var next = NextMonthlyDate(cand.AddTicks(1), time, monthDay);
        while (next <= atOrBefore)
        {
            cand = next;
            next = NextMonthlyDate(cand.AddTicks(1), time, monthDay);
        }

        return cand <= atOrBefore ? cand : NextMonthlyDate(atOrBefore, time, monthDay);
    }

    static DateTime PrevNthWeekday(DateTime atOrBefore, TimeSpan time, int nth, int weekday)
    {
        var start = new DateTime(atOrBefore.Year, atOrBefore.Month, 1).AddMonths(-2);
        var cand = NextNthWeekday(start, time, nth, weekday);
        var next = NextNthWeekday(cand.AddTicks(1), time, nth, weekday);
        while (next <= atOrBefore)
        {
            cand = next;
            next = NextNthWeekday(cand.AddTicks(1), time, nth, weekday);
        }

        return cand <= atOrBefore ? cand : NextNthWeekday(atOrBefore, time, nth, weekday);
    }

    static DateTime PrevYearly(DateTime atOrBefore, TimeSpan time, int month, int monthDay)
    {
        var cand = NextYearly(new DateTime(atOrBefore.Year - 1, 1, 1), time, month, monthDay);
        var next = NextYearly(cand.AddTicks(1), time, month, monthDay);
        while (next <= atOrBefore)
        {
            cand = next;
            next = NextYearly(cand.AddTicks(1), time, month, monthDay);
        }

        return cand <= atOrBefore ? cand : NextYearly(atOrBefore, time, month, monthDay);
    }

    static int CountOccurrences(TodoItem todo, DateTime fromInclusive, DateTime toExclusive)
    {
        if (fromInclusive >= toExclusive)
            return 0;

        var count = 0;
        var cursor = OnOrAfter(todo, fromInclusive);
        var safety = 0;
        while (cursor < toExclusive && safety++ < 20000)
        {
            count++;
            var next = OnOrAfter(todo, cursor.AddTicks(1));
            if (next <= cursor)
                break;
            cursor = next;
        }

        return count;
    }

    static DateTime NextDaily(DateTime atOrAfter, TimeSpan time)
    {
        var cand = Combine(atOrAfter, time);
        return cand >= atOrAfter ? cand : cand.AddDays(1);
    }

    static DateTime NextEveryNDays(DateTime atOrAfter, TimeSpan time, int interval, DateTime anchor)
    {
        var start = Combine(anchor, time);
        if (atOrAfter <= start)
            return start;

        var days = (atOrAfter.Date - start.Date).TotalDays;
        var steps = (int)Math.Floor(days / interval);
        var cand = start.AddDays(steps * interval);
        while (cand < atOrAfter)
            cand = cand.AddDays(interval);
        return cand;
    }

    static DateTime NextWeekly(DateTime atOrAfter, TimeSpan time, IReadOnlyList<int> weekdays)
    {
        var set = weekdays.ToHashSet();
        for (var i = 0; i < 14; i++)
        {
            var day = atOrAfter.Date.AddDays(i);
            if (!set.Contains((int)day.DayOfWeek))
                continue;
            var cand = Combine(day, time);
            if (cand >= atOrAfter)
                return cand;
        }

        return Combine(atOrAfter.Date.AddDays(7), time);
    }

    static DateTime NextEveryNWeeks(DateTime atOrAfter, TimeSpan time, int intervalWeeks, int weekday, DateTime anchor)
    {
        weekday = ClampDay(weekday);
        var anchorDay = Combine(anchor, time);
        var delta = (weekday - (int)anchorDay.DayOfWeek + 7) % 7;
        var first = Combine(anchorDay.Date.AddDays(delta), time);
        if (first < Combine(anchor, time))
            first = first.AddDays(7);

        var spanWeeks = intervalWeeks * 7;
        if (atOrAfter <= first)
            return first;

        var days = (atOrAfter.Date - first.Date).TotalDays;
        var steps = (int)Math.Floor(days / spanWeeks);
        var cand = first.AddDays(steps * spanWeeks);
        while (cand < atOrAfter)
            cand = cand.AddDays(spanWeeks);
        return cand;
    }

    static DateTime NextMonthlyDate(DateTime atOrAfter, TimeSpan time, int monthDay)
    {
        monthDay = Math.Clamp(monthDay, 1, 31);
        for (var m = 0; m < 36; m++)
        {
            var month = new DateTime(atOrAfter.Year, atOrAfter.Month, 1).AddMonths(m);
            var day = Math.Min(monthDay, DateTime.DaysInMonth(month.Year, month.Month));
            var cand = Combine(new DateTime(month.Year, month.Month, day), time);
            if (cand >= atOrAfter)
                return cand;
        }

        return atOrAfter.AddMonths(1);
    }

    static DateTime NextNthWeekday(DateTime atOrAfter, TimeSpan time, int nth, int weekday)
    {
        weekday = ClampDay(weekday);
        for (var m = 0; m < 36; m++)
        {
            var month = new DateTime(atOrAfter.Year, atOrAfter.Month, 1).AddMonths(m);
            var cand = Combine(NthWeekdayInMonth(month.Year, month.Month, nth, (DayOfWeek)weekday), time);
            if (cand >= atOrAfter)
                return cand;
        }

        return atOrAfter.AddMonths(1);
    }

    static DateTime NextYearly(DateTime atOrAfter, TimeSpan time, int month, int monthDay)
    {
        month = Math.Clamp(month, 1, 12);
        monthDay = Math.Clamp(monthDay, 1, 31);
        for (var y = 0; y < 8; y++)
        {
            var year = atOrAfter.Year + y;
            var day = Math.Min(monthDay, DateTime.DaysInMonth(year, month));
            var cand = Combine(new DateTime(year, month, day), time);
            if (cand >= atOrAfter)
                return cand;
        }

        return atOrAfter.AddYears(1);
    }

    static DateTime NthWeekdayInMonth(int year, int month, int nth, DayOfWeek dow)
    {
        if (nth < 0)
        {
            var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            var back = ((int)last.DayOfWeek - (int)dow + 7) % 7;
            return last.AddDays(-back);
        }

        nth = Math.Clamp(nth, 1, 4);
        var first = new DateTime(year, month, 1);
        var forward = ((int)dow - (int)first.DayOfWeek + 7) % 7;
        var day = 1 + forward + (nth - 1) * 7;
        var max = DateTime.DaysInMonth(year, month);
        if (day > max)
            day = 1 + forward + (nth - 2) * 7;
        return new DateTime(year, month, Math.Clamp(day, 1, max));
    }

    static int ClampDay(int weekday) => Math.Clamp(weekday, 0, 6);

    public static string FormatTime(TimeSpan time)
    {
        var dt = DateTime.Today + time;
        return dt.ToString("h:mm tt");
    }

    static string DescribeDays(List<int> days)
    {
        if (days.Count == 0)
            return "no days";
        return string.Join(", ", days.OrderBy(d => d).Select(DayName));
    }

    static string DayName(int d) => ((DayOfWeek)ClampDay(d)).ToString()[..3];

    static string MonthName(int m) => new DateTime(2000, Math.Clamp(m, 1, 12), 1).ToString("MMM");

    static string Ordinal(int n)
    {
        var v = Math.Clamp(n, 1, 31);
        if (v is 11 or 12 or 13) return $"{v}th";
        return (v % 10) switch
        {
            1 => $"{v}st",
            2 => $"{v}nd",
            3 => $"{v}rd",
            _ => $"{v}th"
        };
    }

    static string NthLabel(int nth) => nth < 0 ? "Last" : nth switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        4 => "4th",
        _ => $"{nth}th"
    };
}
