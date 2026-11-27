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

    public static string Lerp(string fromHex, string toHex, double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (!TryRgb(fromHex, out var fr, out var fg, out var fb))
            return NormalizeOr(toHex, Idle);
        if (!TryRgb(toHex, out var tr, out var tg, out var tb))
            return NormalizeOr(fromHex, Idle);
        var r = (int)Math.Round(fr + (tr - fr) * t);
        var g = (int)Math.Round(fg + (tg - fg) * t);
        var b = (int)Math.Round(fb + (tb - fb) * t);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    static bool TryRgb(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (!TryNormalize(hex, out var n) || n.Length != 7)
            return false;
        r = Convert.ToByte(n[1..3], 16);
        g = Convert.ToByte(n[3..5], 16);
        b = Convert.ToByte(n[5..7], 16);
        return true;
    }
}
