using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AtFutureMe.Services;

/// <summary>
/// When a list link points at an exe or a Steam game that is already running,
/// focus that window instead of starting another copy.
/// </summary>
static class LinkFocus
{
    static readonly Regex RunGameId = new(
        @"steam://rungameid/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex AppLaunch = new(
        @"-applaunch\s+(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex QuotedPath = new(
        "\"path\"\\s+\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex InstallDir = new(
        "\"installdir\"\\s+\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryActivate(string? url)
    {
        var raw = Unwrap(url);
        if (raw.Length == 0)
            return false;
        raw = Environment.ExpandEnvironmentVariables(raw);

        if (TrySteamAppId(raw, out var appId))
            return TryActivateSteamApp(appId);
        if (TryExePath(raw, out var exe))
            return TryActivateExe(exe);
        return false;
    }

    static bool TryActivateSteamApp(string appId)
    {
        var dir = FindSteamAppDirectory(appId);
        if (dir == null)
            return false;
        var ids = ProcessesUnder(dir);
        if (ids.Count == 0)
            return false;
        return ActivateMainWindow(NativeMethods.ListAppWindows(ids), null);
    }

    static bool TryActivateExe(string exePath)
    {
        var ids = ProcessesMatching(exePath);
        if (ids.Count == 0)
            return false;
        return ActivateMainWindow(NativeMethods.ListAppWindows(ids), null);
    }

    /// <summary>
    /// Focus the window the user picked for this item. Title wins when that
    /// process has more than one window; otherwise the largest window of that exe.
    /// </summary>
    public static bool TryActivateChosen(string? exePath, string? title)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return false;
        var ids = ProcessesMatching(exePath);
        if (ids.Count == 0)
            return false;
        return ActivateMainWindow(NativeMethods.ListAppWindows(ids), title);
    }

    static bool ActivateMainWindow(List<NativeMethods.AppWindow> windows, string? wantedTitle)
    {
        if (windows.Count == 0)
            return false;
        var playable = windows.Where(w => !IsAnticheatSplash(w)).ToList();
        var pool = playable.Count > 0 ? playable : windows;
        var wanted = wantedTitle?.Trim() ?? "";
        NativeMethods.AppWindow best = default;
        var bestArea = long.MinValue;
        var titled = false;
        foreach (var window in pool)
        {
            var titleHit = wanted.Length > 0
                && string.Equals(window.Title.Trim(), wanted, StringComparison.OrdinalIgnoreCase);
            if (titled && !titleHit)
                continue;
            if (titleHit && !titled)
            {
                titled = true;
                bestArea = long.MinValue;
            }
            if (window.Area <= bestArea)
                continue;
            bestArea = window.Area;
            best = window;
        }

        if (best.Handle == IntPtr.Zero)
            return false;
        NativeMethods.ForceForeground(best.Handle);
        return true;
    }

    static bool IsAnticheatSplash(NativeMethods.AppWindow window)
    {
        var title = window.Title;
        if (title.Contains("anti-cheat", StringComparison.OrdinalIgnoreCase)
            || title.Contains("anticheat", StringComparison.OrdinalIgnoreCase)
            || title.Contains("battleye", StringComparison.OrdinalIgnoreCase))
            return true;
        if (NativeMethods.ClassLooksLikeSplash(window.Handle))
            return true;
        var path = NativeMethods.GetProcessImagePath(window.ProcessId);
        var name = Path.GetFileNameWithoutExtension(path ?? "");
        return name.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase)
            || name.Contains("BattlEye", StringComparison.OrdinalIgnoreCase)
            || name.Equals("BEService", StringComparison.OrdinalIgnoreCase)
            || name.Equals("start_protected_game", StringComparison.OrdinalIgnoreCase);
    }

    public readonly record struct FocusChoice(string Title, string ExePath, string ExeName);

    public static List<FocusChoice> ListChoices()
    {
        var self = Environment.ProcessPath;
        var choices = new List<FocusChoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in NativeMethods.ListOpenWindows())
        {
            if (string.IsNullOrWhiteSpace(window.Title))
                continue;
            var path = NativeMethods.GetProcessImagePath(window.ProcessId);
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (self != null && path.Equals(self, StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Path.GetFileName(path);
            if (name.Equals("AtFutureMe.exe", StringComparison.OrdinalIgnoreCase))
                continue;
            var key = path + "\n" + window.Title;
            if (!seen.Add(key))
                continue;
            choices.Add(new FocusChoice(window.Title.Trim(), path, name));
        }

        choices.Sort((a, b) =>
        {
            var byName = string.Compare(a.ExeName, b.ExeName, StringComparison.OrdinalIgnoreCase);
            return byName != 0
                ? byName
                : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });
        return choices;
    }

    static bool TrySteamAppId(string raw, out string appId)
    {
        var run = RunGameId.Match(raw);
        if (run.Success)
        {
            appId = run.Groups[1].Value;
            return true;
        }

        if (raw.Length > 2 && raw[0] == '"')
        {
            var end = raw.IndexOf('"', 1);
            if (end > 1)
            {
                var file = raw[1..end];
                var name = Path.GetFileName(file);
                if (name.Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var launch = AppLaunch.Match(raw);
                    if (launch.Success)
                    {
                        appId = launch.Groups[1].Value;
                        return true;
                    }
                }
            }
        }

        appId = "";
        return false;
    }

    static bool TryExePath(string raw, out string path)
    {
        path = "";
        if (raw.Length > 2 && raw[0] == '"')
        {
            var end = raw.IndexOf('"', 1);
            if (end <= 1)
                return false;
            raw = raw[1..end].Trim();
        }

        if (!raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!(raw.StartsWith(@"\\", StringComparison.Ordinal)
              || (raw.Length >= 3 && char.IsAsciiLetter(raw[0]) && raw[1] == ':' && raw[2] is '\\' or '/')))
            return false;
        path = raw;
        return true;
    }

    static string? FindSteamAppDirectory(string appId)
    {
        foreach (var lib in SteamLibraries())
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest))
                continue;
            string text;
            try
            {
                text = File.ReadAllText(manifest);
            }
            catch
            {
                continue;
            }

            var match = InstallDir.Match(text);
            if (!match.Success)
                continue;
            var folder = match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal);
            var full = Path.Combine(lib, "steamapps", "common", folder);
            if (Directory.Exists(full))
                return full;
        }

        return null;
    }

    static IEnumerable<string> SteamLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steam = SteamInstallPath();
        if (!string.IsNullOrWhiteSpace(steam) && seen.Add(steam))
            yield return steam;
        if (string.IsNullOrWhiteSpace(steam))
            yield break;

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            yield break;
        string text;
        try
        {
            text = File.ReadAllText(vdf);
        }
        catch
        {
            yield break;
        }

        foreach (Match match in QuotedPath.Matches(text))
        {
            var path = match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal).Replace('/', '\\');
            if (Directory.Exists(path) && seen.Add(path))
                yield return path;
        }
    }

    static string? SteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("InstallPath") as string ?? key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                path = path.Replace('/', '\\');
                if (Directory.Exists(path))
                    return path;
            }
        }
        catch
        {
            // Fall through to the default install folder.
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    static HashSet<int> ProcessesUnder(string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd('\\', '/') + "\\";
        var ids = new HashSet<int>();
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
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;
                var name = Path.GetFileName(path);
                if (name.Equals("steam.exe", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("steamservice.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                ids.Add(process.Id);
            }
            catch
            {
                // Process exited or access denied.
            }
            finally
            {
                process.Dispose();
            }
        }

        return ids;
    }

    static HashSet<int> ProcessesMatching(string exePath)
    {
        var ids = new HashSet<int>();
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.SessionId != session)
                    continue;
                var path = NativeMethods.GetProcessImagePath(process.Id);
                if (ProcessWatch.PathsRelated(exePath, path))
                    ids.Add(process.Id);
            }
            catch
            {
                // Process exited or access denied.
            }
            finally
            {
                process.Dispose();
            }
        }

        return ids;
    }

    static string Unwrap(string? text)
    {
        var raw = text?.Trim() ?? "";
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1].Trim();
        return raw;
    }
}

