using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageMonitor.Services;

public record UsageWindow(
    double UsedPercent,
    double? ExpectedPercent,
    double? Slack,
    DateTimeOffset? ResetsAt
);

// Claude splits AvailableCount by the limit a reset clears: WeeklyCount the account-wide weekly limit,
// FableCount only the Fable weekly limit, OtherCount a kind usage-api does not recognize. WeeklyCount is
// null for Codex and for an older usage-api that sends only the total.
public record ResetCredits(int AvailableCount, DateTimeOffset? NextExpiresAt, DateTimeOffset? CheckedAt = null,
    int? WeeklyCount = null, int FableCount = 0, int OtherCount = 0)
{
    public bool IsSplit => WeeklyCount.HasValue;
    // Shown on the 7d line: weekly resets plus unrecognized ones, which are never dropped. Without a split
    // (older usage-api, Codex) the whole count, as before.
    public int SevenDayCount => WeeklyCount is { } weekly ? weekly + OtherCount : AvailableCount;
}
// FiveHour is null for accounts without a 5-hour limit (or with no open session); hide it then.
public record ClaudeBlock(UsageWindow? FiveHour, UsageWindow SevenDay, UsageWindow? SevenDayDesign, UsageWindow? SevenDayFable, string? SubscriptionType, ResetCredits? Resets);
// Codex account-wide windows, each filed by its length: Primary is the 5-hour one, OneDay the 24-hour one,
// Secondary the 7-day one. Any can be null (promax has no 5-hour window; a 24-hour one is rare), and the
// account still shows with whichever windows it has.
public record CodexBlock(UsageWindow? Primary, UsageWindow? Secondary, UsageWindow? SparkPrimary, UsageWindow? SparkSecondary, string? PlanType, string? CreditsBalance, ResetCredits? Resets, UsageWindow? OneDay = null);
public record ZaiBlock(UsageWindow? FiveHour, UsageWindow? Monthly, long? MonthlyCurrent, long? MonthlyLimit, string? Level);
public record OpenRouterBlock(double Usage, double? Limit, double? LimitRemaining, bool? IsFreeTier);
public record OpenAiBlock(double SpendToday, double SpendMonth, string Currency);
public record JevBlock(double Spend1Day, double Spend3Days, double Spend7Days, string Currency);
// Staleness/FetchedAt/Error come from the provider entry itself, so a card can say
// "stale since …" when usage-api keeps serving an account's last good numbers.
public record UsageAccount(string Key, string Id, string Name, ClaudeBlock? Claude, CodexBlock? Codex,
    string? Staleness = null, DateTimeOffset? FetchedAt = null, string? Error = null, double? Priority = null);
// Who an account is: the email usage-api names it by and CLIProxyAPI's routing priority (null when unset),
// plus how current its numbers are: FetchedAt is when they were true, DataSource is "passive" (seen in
// CPA request headers) or "active" (usage-endpoint check), Staleness is usage-api's tier for them.
// Blocked/AvailableAt are filled in by the popup from the account's windows: Blocked when an
// account-wide window (5h or 7d) is full, AvailableAt the latest reset among the full ones (null = unknown).
// Renewal is the next subscription billing date entered by hand on usage-api (null when unset).
// Expiry is set when the subscription ends instead of renewing (null while it renews).
public record AccountInfo(string? Email, double? Priority, DateTimeOffset? FetchedAt = null,
    string? DataSource = null, string? Staleness = null, bool Blocked = false, DateTimeOffset? AvailableAt = null,
    Renewal? Renewal = null, Expiry? Expiry = null);

// usage-api's `renewal`: a calendar date in America/Chicago plus its day count, cycle and source.
public record Renewal(DateOnly RenewsAt, string? BillingCycle, int? DaysUntil, string? Source);

// usage-api's `expiry`: the last day of a subscription that will not renew (America/Chicago), days left
// (negative once passed) and where it came from: "provider" (ChatGPT, for Codex) or "manual".
public record Expiry(DateOnly ExpiresAt, int? DaysUntil, string? Source);

public record UsageApiStatus(
    DateTimeOffset Timestamp,
    ClaudeBlock? Claude,
    ClaudeBlock? Claude2,
    CodexBlock? Codex,
    CodexBlock? Codex2,
    ZaiBlock? Zai,
    OpenRouterBlock? OpenRouter,
    OpenAiBlock? OpenAi,
    JevBlock? Jev,
    // Oldest per-provider fetchedAt and worst usage-api staleness tier
    // ("fresh" | "little_stale" | "stale"); null from older usage-api builds.
    DateTimeOffset? OldestFetchedAt = null,
    string? Staleness = null,
    IReadOnlyList<UsageAccount>? AdditionalAccounts = null,
    // Provider keys (e.g. "claude3") whose data usage-api marks "stale". They are left
    // out of OldestFetchedAt/Staleness so one frozen account doesn't make the whole
    // footer read "Updated 30h ago"; the footer names them instead.
    IReadOnlyList<string>? StaleProviders = null,
    // Identity of every Claude/Codex account keyed like the providers ("claude", "codex2", ...).
    IReadOnlyDictionary<string, AccountInfo>? Accounts = null
);

public class UsageApiService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _url;
    private string? _lastLoggedProblem;

    // Console output goes to /dev/null when the app relaunches itself, so fetch problems
    // also go to usage.log, once per distinct message rather than every refresh tick.
    private void LogProblem(string? message)
    {
        if (message == _lastLoggedProblem) return;
        _lastLoggedProblem = message;
        if (message != null) Console.WriteLine(message);
        AppLog.WriteLine(message ?? "usage-api: recovered");
    }

    public UsageApiService(string baseUrl)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _url = baseUrl.TrimEnd('/') + "/api/usage";
    }

    public async Task<UsageApiStatus?> GetStatusAsync()
    {
        try
        {
            using var resp = await _http.GetAsync(_url);
            if (!resp.IsSuccessStatusCode)
            {
                LogProblem($"usage-api HTTP {(int)resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ts = ReadDateTimeOffset(root, "timestamp") ?? DateTimeOffset.Now;
            var providers = root.TryGetProperty("providers", out var p) ? p : default;

            DateTimeOffset? oldest = null;
            string? worst = null;
            string[] rank = ["fresh", "little_stale", "stale"];
            var stale = new List<string>();
            if (providers.ValueKind == JsonValueKind.Object)
                foreach (var prov in providers.EnumerateObject())
                {
                    if (prov.Value.ValueKind != JsonValueKind.Object) continue;
                    var tier = ReadString(prov.Value, "staleness");
                    if (tier == "stale") { stale.Add(prov.Name); continue; }
                    // Claude/Codex accounts show their own data age per group, and their slow backup
                    // checks would otherwise make the footer read "Updated 25m ago" most of the time.
                    if (prov.Value.TryGetProperty("account", out var acct) && acct.ValueKind == JsonValueKind.Object) continue;
                    var at = ReadDateTimeOffset(prov.Value, "fetchedAt");
                    if (at is { } a && (oldest == null || a < oldest)) oldest = a;
                    if (tier != null && Array.IndexOf(rank, tier) > Array.IndexOf(rank, worst)) worst = tier;
                }
            LogProblem(stale.Count == 0 ? null : "usage-api stale providers: " + string.Join(", ", stale.Select(k =>
                $"{k} ({ReadString(providers.GetProperty(k), "error") ?? "no error"})")));

            return new UsageApiStatus(
                ts,
                ParseClaude(GetData(providers, "claude")),
                ParseClaude(GetData(providers, "claude2")),
                ParseCodex(GetData(providers, "codex")),
                ParseCodex(GetData(providers, "codex2")),
                ParseZai(GetData(providers, "zai")),
                ParseOpenRouter(GetData(providers, "openrouter")),
                ParseOpenAi(GetData(providers, "openai")),
                ParseJev(GetData(providers, "jev")),
                oldest,
                worst,
                ParseAdditionalAccounts(providers),
                stale,
                ParseAccountInfo(providers)
            );
        }
        catch (Exception ex)
        {
            LogProblem($"usage-api error: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private static IReadOnlyDictionary<string, AccountInfo> ParseAccountInfo(JsonElement providers)
    {
        var info = new Dictionary<string, AccountInfo>();
        if (providers.ValueKind != JsonValueKind.Object) return info;
        foreach (var entry in providers.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object
                || !entry.Value.TryGetProperty("account", out var a) || a.ValueKind != JsonValueKind.Object) continue;
            info[entry.Name] = new AccountInfo(ReadString(a, "name"), ReadDouble(a, "priority"),
                ReadDateTimeOffset(entry.Value, "fetchedAt"), ReadString(entry.Value, "dataSource"),
                ReadString(entry.Value, "staleness"), Renewal: ParseRenewal(entry.Value), Expiry: ParseExpiry(entry.Value));
        }
        return info;
    }

    private static Expiry? ParseExpiry(JsonElement entry)
    {
        if (!entry.TryGetProperty("expiry", out var x) || x.ValueKind != JsonValueKind.Object) return null;
        if (!DateOnly.TryParseExact(ReadString(x, "expires_at"), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date)) return null;
        var days = ReadDouble(x, "days_until_expiry");
        return new Expiry(date, days is { } d ? (int)d : null, ReadString(x, "source"));
    }

    private static Renewal? ParseRenewal(JsonElement entry)
    {
        if (!entry.TryGetProperty("renewal", out var r) || r.ValueKind != JsonValueKind.Object) return null;
        if (!DateOnly.TryParseExact(ReadString(r, "renews_at"), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date)) return null;
        var days = ReadDouble(r, "days_until_renewal");
        return new Renewal(date, ReadString(r, "billing_cycle"), days is { } d ? (int)d : null, ReadString(r, "source"));
    }

    private static IReadOnlyList<UsageAccount> ParseAdditionalAccounts(JsonElement providers)
    {
        var accounts = new List<UsageAccount>();
        if (providers.ValueKind != JsonValueKind.Object) return accounts;
        foreach (var entry in providers.EnumerateObject())
        {
            var family = entry.Name.StartsWith("codex", StringComparison.Ordinal) ? "codex"
                : entry.Name.StartsWith("claude", StringComparison.Ordinal) ? "claude" : null;
            if (family == null || !int.TryParse(entry.Name[family.Length..], out var number) || number < 3) continue;
            var metadata = entry.Value.TryGetProperty("account", out var a) ? a : default;
            accounts.Add(new UsageAccount(entry.Name, ReadString(metadata, "id") ?? entry.Name,
                ReadString(metadata, "name") ?? entry.Name,
                family == "claude" ? ParseClaude(GetData(providers, entry.Name)) : null,
                family == "codex" ? ParseCodex(GetData(providers, entry.Name)) : null,
                ReadString(entry.Value, "staleness"), ReadDateTimeOffset(entry.Value, "fetchedAt"),
                ReadString(entry.Value, "error"), ReadDouble(metadata, "priority")));
        }
        return accounts.OrderBy(a => a.Key.StartsWith("codex") ? 0 : 1)
            .ThenBy(a => int.Parse(a.Key[(a.Key.StartsWith("codex") ? 5 : 6)..])).ToArray();
    }

    private static JsonElement GetData(JsonElement providers, string key)
    {
        if (providers.ValueKind != JsonValueKind.Object) return default;
        if (!providers.TryGetProperty(key, out var entry)) return default;
        if (!entry.TryGetProperty("data", out var data)) return default;
        return data.ValueKind == JsonValueKind.Null ? default : data;
    }

    private static UsageWindow ParseWindow(JsonElement el, string usedKey)
    {
        return new UsageWindow(
            ReadDouble(el, usedKey) ?? 0,
            ReadDouble(el, "expected_percent"),
            ReadDouble(el, "slack"),
            ReadDateTimeOffset(el, "resets_at")
        );
    }

    private static ClaudeBlock? ParseClaude(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("seven_day", out var seven) || seven.ValueKind != JsonValueKind.Object)
            return null;

        UsageWindow? design = null;
        if (data.TryGetProperty("seven_day_design", out var des) && des.ValueKind == JsonValueKind.Object)
            design = ParseWindow(des, "utilization");

        // Per-model weekly limit, same shape as Design. Absent for accounts that
        // have never run Fable, so it stays null and the bar simply never shows.
        UsageWindow? fable = null;
        if (data.TryGetProperty("seven_day_fable", out var fab) && fab.ValueKind == JsonValueKind.Object)
            fable = ParseWindow(fab, "utilization");

        // Only some Claude accounts have a 5-hour window; usage-api sends null for the rest.
        UsageWindow? fiveHour = null;
        if (data.TryGetProperty("five_hour", out var five) && five.ValueKind == JsonValueKind.Object)
            fiveHour = ParseWindow(five, "utilization");

        return new ClaudeBlock(
            fiveHour,
            ParseWindow(seven, "utilization"),
            design,
            fable,
            ReadString(data, "subscription_type"),
            ParseResetCredits(data)
        );
    }

    // Same "reset_credits" shape for Claude and Codex; null when the server has none.
    private static ResetCredits? ParseResetCredits(JsonElement data)
    {
        if (!data.TryGetProperty("reset_credits", out var rc) || rc.ValueKind != JsonValueKind.Object)
            return null;
        var count = (int)(ReadInt64(rc, "available_count") ?? 0);
        var weekly = ReadInt64(rc, "weekly_available") is { } w ? (int)w : (int?)null;
        var fable = (int)(ReadInt64(rc, "fable_available") ?? 0);
        var other = (int)(ReadInt64(rc, "other_available") ?? 0);
        if (other > 0) LogUnknownResets(other, rc);
        return new ResetCredits(count, ReadDateTimeOffset(rc, "next_expires_at"), ReadDateTimeOffset(data, "reset_credits_checked_at"),
            weekly, fable, other);
    }

    // Resets of a kind usage-api could not classify are shown on the 7d line; note them (once per label set).
    private static readonly HashSet<string> LoggedUnknownResets = new();
    internal static Action<string> UnknownResetLog = AppLog.WriteLine; // checks capture it instead of the real log
    private static void LogUnknownResets(int count, JsonElement rc)
    {
        var labels = new List<string>();
        if (rc.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Array)
            foreach (var c in credits.EnumerateArray())
                if (ReadString(c, "type") == "other")
                    labels.Add(ReadString(c, "grant_id") ?? ReadString(c, "label") ?? "?");
        var key = string.Join(", ", labels.Distinct());
        lock (LoggedUnknownResets)
            if (!LoggedUnknownResets.Add(key)) return;
        UnknownResetLog($"Reset credits of unknown kind ({count}) shown on the 7d line: {(key.Length > 0 ? key : "no detail")}");
    }

    // Which Codex slot a window of this length belongs to; the same cut-offs as usage-api
    // (up to 12 h is the 5-hour window, up to 3 days the 24-hour one, longer the weekly one).
    internal static string? CodexWindowSlot(double? windowMinutes) => windowMinutes switch
    {
        null or <= 0 => null,
        <= 12 * 60 => "five_hour",
        <= 3 * 24 * 60 => "one_day",
        _ => "seven_day",
    };

    private static CodexBlock? ParseCodex(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;

        // usage-api sends five_hour / one_day / seven_day (primary / secondary are older aliases of the
        // 5-hour and 7-day ones). Each window is filed by its own length again, so an older usage-api that
        // still puts a 24-hour window under five_hour or primary reads correctly too.
        var windows = new Dictionary<string, UsageWindow>();
        foreach (var (field, slot) in new[] { ("five_hour", "five_hour"), ("one_day", "one_day"), ("seven_day", "seven_day"),
                     ("primary", "five_hour"), ("secondary", "seven_day") })
        {
            if (!data.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Object) continue;
            windows.TryAdd(CodexWindowSlot(ReadDouble(el, "window_minutes")) ?? slot, ParseWindow(el, "used_percent"));
        }
        // No account-wide window at all means there is nothing to show yet.
        if (windows.Count == 0) return null;

        UsageWindow? sparkPri = null, sparkSec = null;
        if (data.TryGetProperty("additional", out var add) && add.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in add.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var name = ReadString(entry, "name") ?? "";
                var feat = ReadString(entry, "metered_feature") ?? "";
                if (!name.Contains("spark", StringComparison.OrdinalIgnoreCase) &&
                    !feat.Equals("codex_bengalfox", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (entry.TryGetProperty("primary", out var ep) && ep.ValueKind == JsonValueKind.Object)
                    sparkPri = ParseWindow(ep, "used_percent");
                if (entry.TryGetProperty("secondary", out var es) && es.ValueKind == JsonValueKind.Object)
                    sparkSec = ParseWindow(es, "used_percent");
                break;
            }
        }

        return new CodexBlock(
            windows.GetValueOrDefault("five_hour"),
            windows.GetValueOrDefault("seven_day"),
            sparkPri,
            sparkSec,
            ReadString(data, "plan_type"),
            ReadString(data, "credits_balance"),
            ParseResetCredits(data),
            windows.GetValueOrDefault("one_day")
        );
    }

    private static ZaiBlock? ParseZai(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        UsageWindow? fiveHour = null;
        if (data.TryGetProperty("five_hour", out var five) && five.ValueKind == JsonValueKind.Object)
            fiveHour = ParseWindow(five, "used_percent");

        UsageWindow? monthly = null;
        long? monthlyCurrent = null, monthlyLimit = null;
        if (data.TryGetProperty("monthly", out var mo) && mo.ValueKind == JsonValueKind.Object)
        {
            monthly = ParseWindow(mo, "used_percent");
            monthlyCurrent = ReadInt64(mo, "current");
            monthlyLimit = ReadInt64(mo, "limit");
        }

        return new ZaiBlock(fiveHour, monthly, monthlyCurrent, monthlyLimit, ReadString(data, "level"));
    }

    private static OpenRouterBlock? ParseOpenRouter(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        return new OpenRouterBlock(
            ReadDouble(data, "usage") ?? 0,
            ReadDouble(data, "limit"),
            ReadDouble(data, "limit_remaining"),
            ReadBool(data, "is_free_tier")
        );
    }

    private static OpenAiBlock? ParseOpenAi(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        return new OpenAiBlock(
            ReadDouble(data, "spend_today") ?? 0,
            ReadDouble(data, "spend_month") ?? 0,
            ReadString(data, "currency") ?? "USD"
        );
    }

    private static JevBlock? ParseJev(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        return new JevBlock(
            ReadDouble(data, "spend_1d") ?? 0,
            ReadDouble(data, "spend_3d") ?? 0,
            ReadDouble(data, "spend_7d") ?? 0,
            ReadString(data, "currency") ?? "USD"
        );
    }

    private static double? ReadDouble(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    }

    private static long? ReadInt64(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;
    }

    private static bool? ReadBool(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    private static string? ReadString(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement el, string key)
    {
        var s = ReadString(el, key);
        return DateTimeOffset.TryParse(s, out var t) ? t : null;
    }
}
