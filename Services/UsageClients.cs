using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AtFutureMe.Models;

namespace AtFutureMe.Services;

// auto model
static class ClaudeUsageClient
{
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    const string BetaHeader = "oauth-2025-04-20";

    public static string CredentialsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".credentials.json");

    public static async Task<(ClaudeUsageInfo? Info, string? Error)> FetchAsync(
        HttpClient http, CancellationToken ct)
    {
        string token;
        try
        {
            token = ReadAccessToken();
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, "Claude unreachable: " + ex.Message);
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if ((int)resp.StatusCode == 429)
            return (null, "Claude usage rate-limited — will retry later");
        if (!resp.IsSuccessStatusCode)
            return (null, $"Claude usage HTTP {(int)resp.StatusCode}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return (new ClaudeUsageInfo
            {
                FiveHour = ParseWindow(root, "five_hour"),
                SevenDay = ParseWindow(root, "seven_day"),
                SevenDayOpus = ParseWindow(root, "seven_day_opus")
            }, null);
        }
        catch (Exception ex)
        {
            return (null, "Claude usage parse failed: " + ex.Message);
        }
    }

    static string ReadAccessToken()
    {
        var path = CredentialsPath;
        if (!File.Exists(path))
            throw new InvalidOperationException("Claude Code not signed in (.credentials.json missing)");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
            || !oauth.TryGetProperty("accessToken", out var tok)
            || tok.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(tok.GetString()))
            throw new InvalidOperationException("Claude Code OAuth token missing — run claude login");

        return tok.GetString()!;
    }

    static UsageWindow? ParseWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
            return null;

        double used = 0;
        if (el.TryGetProperty("utilization", out var util) && util.TryGetDouble(out var u))
            used = u;
        else if (el.TryGetProperty("used_percentage", out var pct) && pct.TryGetDouble(out var p))
            used = p;

        DateTime? resets = null;
        if (el.TryGetProperty("resets_at", out var r))
            resets = ParseReset(r);

        return new UsageWindow
        {
            UsedPercent = Math.Clamp(used, 0, 100),
            ResetsAt = resets
        };
    }

    static DateTime? ParseReset(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (string.IsNullOrWhiteSpace(s))
                return null;
            if (DateTimeOffset.TryParse(s, out var dto))
                return dto.LocalDateTime;
            if (long.TryParse(s, out var unixStr))
                return FromUnix(unixStr);
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var unix))
            return FromUnix(unix);

        return null;
    }

    static DateTime FromUnix(long value)
    {
        // seconds vs milliseconds
        if (value > 10_000_000_000L)
            return DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime;
        return DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime;
    }
}

// auto model
static class CursorUsageClient
{
    const string UsageUrl = "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage";

    public static string StateDbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor", "User", "globalStorage", "state.vscdb");

    public static async Task<(CursorUsageInfo? Info, string? Error)> FetchAsync(
        HttpClient http, CancellationToken ct)
    {
        string token;
        try
        {
            token = ReadAccessToken();
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, "Cursor unreachable: " + ex.Message);
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is 401 or 403)
            return (null, "Cursor auth expired — sign in to Cursor again");
        if (!resp.IsSuccessStatusCode)
            return (null, $"Cursor usage HTTP {(int)resp.StatusCode}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            double? pct = null;
            if (root.TryGetProperty("planUsage", out var plan)
                && plan.TryGetProperty("totalPercentUsed", out var t)
                && t.TryGetDouble(out var used))
                pct = Math.Clamp(used, 0, 100);

            string? msg = null;
            if (root.TryGetProperty("displayMessage", out var dm) && dm.ValueKind == JsonValueKind.String)
                msg = dm.GetString();

            return (new CursorUsageInfo
            {
                TotalPercentUsed = pct,
                BillingCycleStart = ParseCycle(root, "billingCycleStart"),
                BillingCycleEnd = ParseCycle(root, "billingCycleEnd"),
                DisplayMessage = msg
            }, null);
        }
        catch (Exception ex)
        {
            return (null, "Cursor usage parse failed: " + ex.Message);
        }
    }

    static string ReadAccessToken()
    {
        var path = StateDbPath;
        if (!File.Exists(path))
            throw new InvalidOperationException("Cursor state DB missing — open Cursor and sign in");

        // Read-only / immutable URI so a running Cursor WAL doesn't block us.
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
        }.ToString();

        try
        {
            return QueryToken(cs);
        }
        catch
        {
            var uri = $"Data Source=file:{path.Replace('\\', '/')}?mode=ro&immutable=1";
            return QueryToken(uri);
        }
    }

    static string QueryToken(string connectionString)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM ItemTable WHERE key = $k LIMIT 1";
        cmd.Parameters.AddWithValue("$k", "cursorAuth/accessToken");
        var raw = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Cursor access token missing — sign in to Cursor");

        raw = raw.Trim();
        if (raw.StartsWith('"') && raw.EndsWith('"'))
        {
            try { raw = JsonSerializer.Deserialize<string>(raw) ?? raw; }
            catch { /* keep raw */ }
        }

        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Cursor access token empty");
        return raw;
    }

    static DateTime? ParseCycle(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (string.IsNullOrWhiteSpace(s))
                return null;
            if (DateTimeOffset.TryParse(s, out var dto))
                return dto.LocalDateTime;
            if (long.TryParse(s, out var n))
                return FromUnix(n);
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var unix))
            return FromUnix(unix);
        return null;
    }

    static DateTime FromUnix(long value)
    {
        if (value > 10_000_000_000L)
            return DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime;
        return DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime;
    }
}
