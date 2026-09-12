namespace AtFutureMe.Services;

static class ClockText
{
    public static bool TryFormat(string text, bool allowShort, out string clock)
    {
        clock = "00:00";
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var raw = text.Trim();
        var colon = raw.IndexOf(':');
        if (colon >= 0)
        {
            var left = raw[..colon];
            var right = raw[(colon + 1)..];
            if (right.Length != 2 || !int.TryParse(left, out var hour) || !int.TryParse(right, out var minute))
                return false;
            if (hour is < 0 or > 23 || minute is < 0 or > 59)
                return false;
            clock = $"{hour:D2}:{minute:D2}";
            return true;
        }

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 4)
        {
            var hour = int.Parse(digits[..2]);
            var minute = int.Parse(digits[2..]);
            if (hour > 23 || minute > 59)
                return false;
            clock = $"{hour:D2}:{minute:D2}";
            return true;
        }

        if (allowShort && digits.Length == 3)
        {
            var hour = int.Parse(digits[..1]);
            var minute = int.Parse(digits[1..]);
            if (hour > 23 || minute > 59)
                return false;
            clock = $"{hour:D2}:{minute:D2}";
            return true;
        }

        return false;
    }

    public static bool TryParseFlexible(string text, bool emptyIsMidnight, out string clock)
    {
        clock = "00:00";
        if (string.IsNullOrWhiteSpace(text))
            return emptyIsMidnight;
        return TryFormat(text, true, out clock);
    }

    public static TimeSpan ParseOr(string text, TimeSpan fallback)
    {
        if (!TryFormat(text, true, out var clock) || !TimeSpan.TryParse(clock, out var time))
            return fallback;
        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            return fallback;
        return time;
    }
}
