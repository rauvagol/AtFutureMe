using System.Diagnostics;
using System.IO;

namespace AtFutureMe.Services;

static class LinkText
{
    public static bool TryStart(string? text, out ProcessStartInfo start)
    {
        start = null!;
        var raw = Unwrap(text);
        if (raw.Length == 0)
            return false;
        raw = Environment.ExpandEnvironmentVariables(raw);

        if (LooksLikePath(raw))
            return TryFile(FilePath(raw), out start);

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https")
            {
                start = Shell(uri.AbsoluteUri);
                return true;
            }

            if (uri.IsFile)
                return TryFile(uri.LocalPath, out start);
            return false;
        }

        if (Uri.TryCreate("https://" + raw, UriKind.Absolute, out var web)
            && web.Scheme == Uri.UriSchemeHttps)
        {
            start = Shell(web.AbsoluteUri);
            return true;
        }

        return false;
    }

    static string Unwrap(string? text)
    {
        var raw = text?.Trim() ?? "";
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1].Trim();
        return raw;
    }

    static bool LooksLikePath(string raw) =>
        raw.StartsWith(@"\\", StringComparison.Ordinal)
        || raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        || raw.Length >= 3
            && char.IsAsciiLetter(raw[0])
            && raw[1] == ':'
            && raw[2] is '\\' or '/';

    static string FilePath(string raw)
    {
        if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && uri.IsFile)
            return uri.LocalPath;
        return raw;
    }

    static bool TryFile(string path, out ProcessStartInfo start)
    {
        start = Shell(path);
        try
        {
            if (File.Exists(path))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir))
                    start.WorkingDirectory = dir;
            }
        }
        catch
        {
            // Still try to open whatever they entered.
        }

        return true;
    }

    static ProcessStartInfo Shell(string fileName) =>
        new(fileName) { UseShellExecute = true };
}
