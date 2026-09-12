namespace AtFutureMe.Services;

static class ColorText
{
    public const string Idle = "#B3B3B3";
    public const string Late1 = "#FFFFFF";
    public const string Late2 = "#FFD24D";
    public const string Late3 = "#FF5A5A";
    public const string Due = "#5DDC8A";
    public const string Completed = "#888888";

    public static bool TryNormalize(string? text, out string hex)
    {
        hex = Idle;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var raw = text.Trim().TrimStart('#');
        if (raw.Length is not (3 or 6) || !raw.All(Uri.IsHexDigit))
            return false;
        if (raw.Length == 3)
            raw = string.Concat(raw.Select(c => $"{c}{c}"));
        hex = "#" + raw.ToUpperInvariant();
        return true;
    }

    public static string NormalizeOr(string? text, string fallback) =>
        TryNormalize(text, out var hex) ? hex : fallback;
}
