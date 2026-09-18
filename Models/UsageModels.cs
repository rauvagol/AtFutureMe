namespace AtFutureMe.Models;

// auto model
public sealed class UsageSnapshot
{
    public DateTime FetchedAt { get; init; } = DateTime.Now;
    public ClaudeUsageInfo? Claude { get; init; }
    public CursorUsageInfo? Cursor { get; init; }
    public string? ClaudeError { get; init; }
    public string? CursorError { get; init; }
}

// auto model
public sealed class ClaudeUsageInfo
{
    public UsageWindow? FiveHour { get; init; }
    public UsageWindow? SevenDay { get; init; }
    public UsageWindow? SevenDayOpus { get; init; }
}

// auto model
public sealed class CursorUsageInfo
{
    public double? TotalPercentUsed { get; init; }
    public DateTime? BillingCycleStart { get; init; }
    public DateTime? BillingCycleEnd { get; init; }
    public string? DisplayMessage { get; init; }
}

// auto model
public sealed class UsageWindow
{
    /// <summary>Percent already consumed in [0, 100].</summary>
    public double UsedPercent { get; init; }
    public DateTime? ResetsAt { get; init; }

    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

// auto model: synthetic reset row for board / clock / upcoming
public sealed class UsageResetEvent
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime At { get; init; }
    public double UsedPercent { get; init; }
}
