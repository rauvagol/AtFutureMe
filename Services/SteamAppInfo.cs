using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AtFutureMe.Services;

/// <summary>
/// Looks up Steam app metadata so DLC/music IDs can resolve to their base game for library/news/run links.
/// </summary>
static class SteamAppInfo
{
    static readonly HttpClient Http = CreateClient();
    static readonly ConcurrentDictionary<string, string> NavIdCache = new(StringComparer.Ordinal);
    static readonly Regex NavAppId = new(
        @"^steam://(?:rungameid|appnews|nav/games/details)/(\d+)/?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AtFutureMe/1.0");
        return http;
    }

    /// <summary>
    /// For storefront keep the given id. For run/news/library, map DLC (and similar) to the base game.
    /// </summary>
    public static async Task<string> ResolveForFormatAsync(string appId, string format, CancellationToken ct = default)
    {
        appId = appId.Trim();
        if (format is "store" or null)
            return appId;
        if (format is not ("rungameid" or "appnews" or "library"))
            return appId;
        return await ResolveBaseAppIdAsync(appId, ct).ConfigureAwait(false);
    }

    public static async Task<string> ResolveBaseAppIdAsync(string appId, CancellationToken ct = default)
    {
        appId = appId.Trim();
        if (appId.Length == 0 || !appId.All(char.IsDigit))
            return appId;
        if (NavIdCache.TryGetValue(appId, out var cached))
            return cached;

        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var resolved = ReadBaseAppId(doc.RootElement, appId);
            NavIdCache[appId] = resolved;
            return resolved;
        }
        catch (Exception ex)
        {
            App.Log($"Steam app lookup failed for {appId}: {ex.Message}");
            return appId;
        }
    }

    /// <summary>
    /// Rewrites run/news/library steam:// links so a DLC app id becomes its base game. Store links unchanged.
    /// </summary>
    public static async Task<string?> RewriteNavUrlAsync(string? url, CancellationToken ct = default)
    {
        var raw = url?.Trim();
        if (string.IsNullOrEmpty(raw))
            return raw;
        var match = NavAppId.Match(raw);
        if (!match.Success)
            return raw;
        var id = match.Groups[1].Value;
        var baseId = await ResolveBaseAppIdAsync(id, ct).ConfigureAwait(false);
        if (baseId == id)
            return raw;
        var prefix = raw[..match.Groups[1].Index];
        return prefix + baseId;
    }

    static string ReadBaseAppId(JsonElement root, string appId)
    {
        if (!root.TryGetProperty(appId, out var entry))
            return appId;
        if (!entry.TryGetProperty("success", out var ok) || !ok.GetBoolean())
            return appId;
        if (!entry.TryGetProperty("data", out var data))
            return appId;

        // Base games are type "game" (and similar) without needing a parent.
        if (data.TryGetProperty("type", out var typeEl))
        {
            var type = typeEl.GetString();
            if (type is "game" or "application" or "tool" or "demo")
                return appId;
        }

        if (!data.TryGetProperty("fullgame", out var full) || full.ValueKind != JsonValueKind.Object)
            return appId;
        if (!full.TryGetProperty("appid", out var parentEl))
            return appId;
        var parent = parentEl.ValueKind switch
        {
            JsonValueKind.String => parentEl.GetString(),
            JsonValueKind.Number => parentEl.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(parent) ? appId : parent.Trim();
    }
}
