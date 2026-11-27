using System.Diagnostics;
using System.Text.RegularExpressions;
using AtFutureMe.Models;

namespace AtFutureMe.Services;

public sealed record RunningExe(string Name, string FilePath)
{
    public override string ToString() => Name;
}

public readonly record struct RunningSnapshot(HashSet<string> Paths);

public static class ProcessWatch
{
    static readonly Regex TrailingVersion = new(
        @"[_\-]v?\d+(?:\.\d+)*[a-z0-9]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex VersionSegment = new(
        @"^v?\d+(?:\.\d+)*[a-z0-9\-]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly char[] DirSeps = ['\\', '/'];

    public static List<RunningExe> ListUserExes()
    {
        var byPath = new Dictionary<string, RunningExe>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, name) in EnumerateUserExes())
        {
            if (!byPath.ContainsKey(path))
                byPath[path] = new RunningExe(name, path);
        }

        return byPath.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<RunningExe> ListPairTargets(IEnumerable<IgnoredPairTarget> ignored, string? alwaysIncludePath = null)
    {
        var skip = new HashSet<string>(
            ignored.Select(t => t.FilePath).Where(p => !string.IsNullOrWhiteSpace(p))!,
            StringComparer.OrdinalIgnoreCase);
        return ListUserExes()
            .Where(e =>
                (!string.IsNullOrWhiteSpace(alwaysIncludePath) && PathsRelated(alwaysIncludePath, e.FilePath))
                || !skip.Contains(e.FilePath))
            .ToList();
    }

    static RunningSnapshot? _cached;
    static DateTime _cachedAt = DateTime.MinValue;

    public static RunningSnapshot Capture()
    {
        var now = DateTime.UtcNow;
        if (_cached is { } hit && now - _cachedAt < TimeSpan.FromMilliseconds(800))
            return hit;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _) in EnumerateUserExes())
            paths.Add(path);
        var snapshot = new RunningSnapshot(paths);
        _cached = snapshot;
        _cachedAt = now;
        return snapshot;
    }

    public static bool IsRunning(RunningSnapshot snapshot, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || snapshot.Paths.Count == 0)
            return false;
        if (snapshot.Paths.Contains(exePath))
            return true;
        foreach (var path in snapshot.Paths)
        {
            if (PathsRelated(exePath, path))
                return true;
        }

        return false;
    }

    public static bool IsRunning(string? exePath) =>
        IsRunning(Capture(), exePath);

    public static bool PathsRelated(string? pairedPath, string? runningPath)
    {
        if (string.IsNullOrWhiteSpace(pairedPath) || string.IsNullOrWhiteSpace(runningPath))
            return false;
        if (pairedPath.Equals(runningPath, StringComparison.OrdinalIgnoreCase))
            return true;
        var a = IdentityParts(pairedPath);
        var b = IdentityParts(runningPath);
        if (a.Length == 0 || a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].Equals(b[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    static string[] IdentityParts(string path)
    {
        var parts = path.Split(DirSeps, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return [];
        var file = FileStem(parts[^1]);
        var dirs = parts.Take(parts.Length - 1).Where(p => !VersionSegment.IsMatch(p));
        return [.. dirs, file];
    }

    static string FileStem(string fileName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return TrailingVersion.Replace(name, "");
    }

    static List<(string Path, string Name)> EnumerateUserExes()
    {
        var found = new List<(string, string)>();
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var self = Environment.ProcessPath;
        var session = Process.GetCurrentProcess().SessionId;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.SessionId != session)
                    continue;
                var path = NativeMethods.GetProcessImagePath(process.Id);
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                var name = System.IO.Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (!string.IsNullOrEmpty(windir) &&
                    path.StartsWith(windir, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (self != null && path.Equals(self, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (name.Equals("AtFutureMe.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                found.Add((path, name));
            }
            catch
            {
                // Access denied or process exited.
            }
            finally
            {
                process.Dispose();
            }
        }

        return found;
    }

    public static bool CategoryIsPaired(Category category) =>
        !string.IsNullOrWhiteSpace(category.PairedExePath);
}
