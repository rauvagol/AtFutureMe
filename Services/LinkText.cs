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

        // Desktop shortcut Target: "C:\...\steam.exe" -applaunch 123  (or steam://…)
        if (TryQuotedCommand(raw, out start))
            return true;

        if (LooksLikePath(raw))
            return TryFile(FilePath(raw), out start);

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme.Length > 0)
        {
            if (uri.IsFile)
                return TryFile(uri.LocalPath, out start);

            // http/https and custom protocols (steam:, discord:, …)
            start = Shell(uri.AbsoluteUri);
            return true;
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

    static bool TryQuotedCommand(string raw, out ProcessStartInfo start)
    {
        start = null!;
        if (raw.Length < 2 || raw[0] != '"')
            return false;
        var end = raw.IndexOf('"', 1);
        if (end <= 1)
            return false;
        var file = raw[1..end].Trim();
        if (file.Length == 0 || !LooksLikePath(file))
            return false;
        var args = raw[(end + 1)..].Trim();
        start = new ProcessStartInfo(file)
        {
            UseShellExecute = true,
            Arguments = args
        };
        try
        {
            if (File.Exists(file))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(file));
                if (!string.IsNullOrEmpty(dir))
                    start.WorkingDirectory = dir;
            }
        }
        catch
        {
            // Still try to launch.
        }

        return true;
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
