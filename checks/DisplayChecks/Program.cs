using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using UsageMonitor;

// Run with: dotnet run --project checks/DisplayChecks
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
object? Call(string name, params object?[] args) =>
    typeof(UsagePopup).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var full = new TextBlock();
var row = new Grid();
var compact = new TextBlock();
foreach (var (balance, expected) in new (string?, string?)[]
         {
             ("100.0000000000", "100"), ("12.345", "12.35"), ("0", "0"),
             ("unlimited", "unlimited"), (null, null), ("", ""),
         })
{
    Call("ApplyCodexBalance", full, row, compact, balance, "Codex CR");
    Check(compact.Text == expected, $"Wrong credit balance for {balance}");
    Check(row.IsVisible == !string.IsNullOrWhiteSpace(expected), "Wrong balance row visibility");
    Check(full.IsVisible == row.IsVisible, "Full/compact balance visibility differs");
    if (row.IsVisible) Check(full.Text == $"Codex CR  {expected}", "Full balance missing label");
}

var summer = DateTimeOffset.Parse("2026-10-09T01:00:00Z");
var winter = DateTimeOffset.Parse("2026-12-09T01:00:00Z");
Check((string?)Call("FormatCentralTime", summer) == "Oct 8 8:00 PM CDT", "Summer reset time wrong");
Check((string?)Call("FormatCentralTime", winter) == "Dec 8 7:00 PM CST", "Winter reset time wrong");
Call("SetResetTimesTooltip", full, new (string, DateTimeOffset?)[] { ("7d", summer), ("5h", null) });
Check((string?)ToolTip.GetTip(full) == "7d: Oct 8 8:00 PM CDT", "Reset tooltip wrong");
Call("SetResetTimesTooltip", full, new (string, DateTimeOffset?)[] { ("7d", null) });
Check(ToolTip.GetTip(full) == null, "Missing reset left stale tooltip");
Check((string?)Call("ShortEmail", "etdofresh@gmail.com") == "etdofresh", "Gmail domain not dropped");
Check((string?)Call("ShortEmail", "etgarcia@my.uno.edu") == "etgarcia@my.uno.edu", "Non-Gmail domain dropped");
Check(Call("ShortEmail", (string?)null) == null, "Missing email not null");
Check((string?)Call("FormatPriority", (double?)10) == "P10", "Priority label wrong");
Check(Call("FormatPriority", (double?)null) == null, "Unset priority shown");
var plan = new TextBlock();
Call("SetCompactPlan", plan, "max", null, (double?)9);
Check(plan.Inlines!.Count == 2 && plan.Inlines.Text == "Max P9", $"Compact plan with priority wrong: {plan.Inlines.Text}");
Call("SetCompactPlan", plan, "max", null, null);
Check(plan.Inlines.Text == "Max", "Unset priority left in compact plan");
var title = new TextBlock { FontSize = 10 };
var now = DateTimeOffset.Parse("2026-11-03T20:00:00Z");
Call("SetTitleWithPlan", title, "Claude", "Max 20x", null, new UsageMonitor.Services.AccountInfo("etdofresh@gmail.com", 10), now);
Check(title.Inlines!.Text == "Claude  Max 20x  P10\netdofresh@gmail.com", $"Full title wrong: {title.Inlines.Text}");
Call("SetTitleWithPlan", title, "Claude", "Max 20x", null,
    new UsageMonitor.Services.AccountInfo("etdofresh@gmail.com", 10, now.AddSeconds(-40), "passive", "fresh"), now);
Check(title.Inlines!.Text == "Claude  Max 20x  P10\netdofresh@gmail.com · live", $"Full title availability wrong: {title.Inlines.Text}");
var email = new TextBlock { FontSize = 7 };
Call("SetCompactEmail", email, new UsageMonitor.Services.AccountInfo("etdofresh@gmail.com", null), now);
Check(email.IsVisible && email.Inlines!.Text == "etdofresh", $"Compact email header wrong: {email.Inlines?.Text}");
Call("SetCompactEmail", email, null, now);
Check(!email.IsVisible, "Empty compact email header still visible");

// The header badge is availability, not data age: usable accounts read "live" however old their numbers are,
// and the age moves to the tooltip.
Call("SetCompactEmail", email, new UsageMonitor.Services.AccountInfo("a@uno.edu", null, now.AddMinutes(-4), "active", "fresh"), now);
Check(email.Inlines!.Text == "a@uno.edu · live", $"Usable account not live: {email.Inlines.Text}");
Check(((string?)ToolTip.GetTip(email))!.Contains("Data 4m ago: checked with the usage endpoint"), $"Data age missing from tooltip: {ToolTip.GetTip(email)}");
Call("SetCompactEmail", email, new UsageMonitor.Services.AccountInfo(null, null, now.AddMinutes(-50), "active", "little_stale"), now);
Check(email.IsVisible && email.Inlines!.Text == "live", "Badge without email wrong");
Check(((Avalonia.Controls.Documents.Run)email.Inlines![0]).Foreground is Avalonia.Media.SolidColorBrush { Color: var c } && c.R == 0xFF,
    "Little-stale live badge not amber");

UsageMonitor.Services.UsageWindow W(double used, DateTimeOffset? reset) => new(used, null, null, reset);
UsageMonitor.Services.AccountInfo Avail(params UsageMonitor.Services.UsageWindow?[] windows) =>
    (UsageMonitor.Services.AccountInfo)typeof(UsagePopup).GetMethod("WithAvailability", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { new UsageMonitor.Services.AccountInfo("x@uno.edu", null, now, "passive", "fresh"), windows })!;
string Badge(UsageMonitor.Services.AccountInfo info, DateTimeOffset at)
{
    Call("SetCompactEmail", email, info, at);
    return email.Inlines!.Text!;
}
var fiveReset = now.AddHours(4).AddMinutes(58);
var weekReset = now.AddDays(2).AddHours(3).AddMinutes(10);
Check(Badge(Avail(W(40, fiveReset), W(60, weekReset)), now) == "x@uno.edu · live", "Usable account not live");
Check(Badge(Avail(W(100, fiveReset), W(60, weekReset)), now) == "x@uno.edu · 4h 58m", "5h-full countdown wrong");
Check(Badge(Avail(W(40, fiveReset), W(100, weekReset)), now) == "x@uno.edu · 2d 3h", "7d-full countdown wrong");
Check(Badge(Avail(W(100, fiveReset), W(100, weekReset)), now) == "x@uno.edu · 2d 3h", "Both full should wait for the later reset");
Check(Badge(Avail(null, W(99.6, now.AddMinutes(37))), now) == "x@uno.edu · 37m", "Codex with no 5h window wrong");
Check(Badge(Avail(W(100, null), W(10, weekReset)), now) == "x@uno.edu · out", "Unknown reset should read out");
Check(((string?)ToolTip.GetTip(email))!.Contains("Out of usage (reset time unknown)"), "Unknown-reset tooltip missing");
Check(Badge(Avail(W(100, fiveReset), W(60, weekReset)), fiveReset.AddSeconds(1)) == "x@uno.edu · live", "Passed reset should read live");
// Per-model windows (Fable, Spark, Design) are not passed as account-wide, so they never block.
var codexBlock = new UsageMonitor.Services.CodexBlock(W(10, fiveReset), W(20, weekReset), W(100, fiveReset), W(100, weekReset), null, null, null);
var claudeBlock = new UsageMonitor.Services.ClaudeBlock(null, W(30, weekReset), W(100, weekReset), W(100, weekReset), null, null);
var wide = (IEnumerable<UsageMonitor.Services.UsageWindow?>)typeof(UsagePopup).GetMethod("AccountWideWindows", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, new object?[] { codexBlock, claudeBlock })!;
Check(Badge(Avail(wide.ToArray()), now) == "x@uno.edu · live", "Per-model full window blocked the account");
Check(((string?)ToolTip.GetTip(email))!.Contains("Usable now"), "Usable tooltip missing");

// A full 24-hour Codex window blocks the account until it resets.
var dayReset = now.AddHours(20).AddMinutes(5);
var dayOnly = new UsageMonitor.Services.CodexBlock(null, null, null, null, null, null, null, W(100, dayReset));
var dayWide = (IEnumerable<UsageMonitor.Services.UsageWindow?>)typeof(UsagePopup).GetMethod("AccountWideWindows", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, new object?[] { dayOnly, null })!;
Check(Badge(Avail(dayWide.ToArray()), now) == "x@uno.edu · 20h 5m", "Full 24-hour window should count down to its reset");
var dayWithWeek = new UsageMonitor.Services.CodexBlock(null, W(100, weekReset), null, null, null, null, null, W(100, dayReset));
dayWide = (IEnumerable<UsageMonitor.Services.UsageWindow?>)typeof(UsagePopup).GetMethod("AccountWideWindows", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, new object?[] { dayWithWeek, null })!;
Check(Badge(Avail(dayWide.ToArray()), now) == "x@uno.edu · 2d 3h", "Full 24-hour and 7-day windows should wait for the later reset");
var labels = ((IEnumerable<(string Label, UsageMonitor.Services.UsageWindow Window)>)Call("CodexAccountWindows",
    new UsageMonitor.Services.CodexBlock(W(1, fiveReset), W(2, weekReset), null, null, null, null, null, W(3, dayReset)))!).Select(w => w.Label);
Check(string.Join(",", labels) == "5h,24h,7d", $"Codex window rows wrong: {string.Join(",", labels)}");
var summaryReset = DateTimeOffset.Now.AddHours(20).AddMinutes(5).AddSeconds(30);
Check((string?)Call("CodexResetSummary", new UsageMonitor.Services.CodexBlock(null, null, null, null, null, null, null, W(5, summaryReset))) == "24h: in 20h 5m",
    "24-hour reset summary wrong");

// Countdowns tick between refreshes without a re-render.
Call("SetCompactEmail", email, Avail(W(100, fiveReset), W(60, weekReset)), now);
Call("RefreshAvailabilityBadges", now.AddHours(1));
Check(email.Inlines!.Text == "x@uno.edu · 3h 58m", $"Badge did not tick: {email.Inlines.Text}");
Call("RefreshAvailabilityBadges", now.AddHours(4).AddMinutes(57).AddSeconds(30));
Check(email.Inlines!.Text == "x@uno.edu · <1m", $"Badge final minute wrong: {email.Inlines.Text}");
// Renewal date trails the badge on the email line; full date in the tooltip; nothing when unset.
var renewal = new UsageMonitor.Services.Renewal(new DateOnly(2026, 12, 2), "monthly", 28, "manual");
Call("SetCompactEmail", email, new UsageMonitor.Services.AccountInfo("etdofreshai@gmail.com", 9, now, "passive", "fresh", Renewal: renewal), now);
Check(email.Inlines!.Text == "etdofreshai · live · renews Dec 2 · 28d", $"Renewal on email line wrong: {email.Inlines.Text}");
Check(((string?)ToolTip.GetTip(email))!.Contains("Renews Wed Dec 2, 2026 (in 28 days), monthly · entered by hand"), $"Renewal tooltip wrong: {ToolTip.GetTip(email)}");
Call("SetCompactEmail", email, new UsageMonitor.Services.AccountInfo("a@uno.edu", null, now, Renewal: renewal with { DaysUntil = 2 }), now);
Check(((Avalonia.Controls.Documents.Run)email.Inlines![^1]).Foreground is Avalonia.Media.SolidColorBrush { Color: var soon } && soon.R == 0xFF && soon.G == 0xB7,
    "Renewal within 3 days not amber");
Call("SetCompactEmail", email, new UsageMonitor.Services.AccountInfo("a@uno.edu", null, now, Renewal: renewal with { DaysUntil = 0 }), now);
Check(email.Inlines!.Text == "a@uno.edu · live · renews Dec 2 · today", $"Renewal today wrong: {email.Inlines.Text}");
Call("SetCompactEmail", email, new UsageMonitor.Services.AccountInfo("a@uno.edu", null, now), now);
Check(email.Inlines!.Text == "a@uno.edu · live" && !((string?)ToolTip.GetTip(email))!.Contains("Renews"), "Null renewal still shown");
Call("SetTitleWithPlan", title, "Claude", "Max 20x", null, new UsageMonitor.Services.AccountInfo(null, null, Renewal: renewal), now);
Check(title.Inlines!.Text == "Claude  Max 20x\nrenews Dec 2 · 28d", $"Title renewal without email wrong: {title.Inlines.Text}");
// A subscription that will not renew shows its end date in place of the renewal: amber, red in its last week.
var expiry = new UsageMonitor.Services.Expiry(new DateOnly(2026, 12, 9), 26, "provider");
Call("SetCompactEmail", email, new UsageMonitor.Services.AccountInfo("etdofresh+dev@gmail.com", null, now, Renewal: renewal, Expiry: expiry), now);
Check(email.Inlines!.Text == "etdofresh+dev · live · expires Dec 9 · 26d", $"Expiry on email line wrong: {email.Inlines.Text}");
Check(((Avalonia.Controls.Documents.Run)email.Inlines![^1]).Foreground is Avalonia.Media.SolidColorBrush { Color: var amber } && amber.R == 0xFF && amber.G == 0xB7,
    "Expiry more than a week out not amber");
Check(((string?)ToolTip.GetTip(email))!.Contains("Expires Wed Dec 9, 2026 (in 26 days) · will not renew"), $"Expiry tooltip wrong: {ToolTip.GetTip(email)}");
Call("SetCompactEmail", email, new UsageMonitor.Services.AccountInfo("a@uno.edu", null, now, Expiry: expiry with { DaysUntil = 3, Source = "manual" }), now);
Check(((Avalonia.Controls.Documents.Run)email.Inlines![^1]).Foreground is Avalonia.Media.SolidColorBrush { Color: var red } && red.R == 0xF4 && red.G == 0x43,
    "Expiry within a week not red");
Check(((string?)ToolTip.GetTip(email))!.Contains("entered by hand"), "Manual expiry tooltip wrong");
Call("SetCompactEmail", email, new UsageMonitor.Services.AccountInfo("a@uno.edu", null, now, Expiry: expiry with { DaysUntil = -2 }), now);
Check(email.Inlines!.Text == "a@uno.edu · live · expired Dec 9", $"Past expiry wrong: {email.Inlines.Text}");
Console.WriteLine("PASS: credit balances, row visibility, reset times (CDT/CST), tooltip clearing, account email/priority, availability badge + ticking, 24h badge, data age tooltip, renewal date, expiry date");

using var accountJson = System.Text.Json.JsonDocument.Parse("""
{"codex3":{"account":{"id":"third","name":"Third Account"},"data":{"secondary":{"used_percent":35},"credits_balance":"0"}},
 "claude12":{"account":{"id":"twelfth","name":"Twelfth Account"},"data":{"five_hour":null,"seven_day":{"utilization":3}}},
 "claude13":{"account":{"id":"thirteenth","name":"Thirteenth Account"},"data":{"five_hour":{"utilization":99,"expected_percent":40,"resets_at":"2026-11-03T23:30:00Z"},"seven_day":{"utilization":73}}},
 "codex4":{"account":{"id":"fourth","name":"Daily Account"},"data":{"plan_type":"pro","five_hour":null,"seven_day":null,"primary":null,"secondary":null,
   "one_day":{"used_percent":12,"window_minutes":1440,"expected_percent":30,"resets_at":"2026-11-05T03:47:01Z"}}},
 "codex5":{"account":{"id":"fifth","name":"Old API"},"data":{"five_hour":{"used_percent":7,"window_minutes":1440,"resets_at":"2026-11-05T03:47:01Z"},
   "primary":{"used_percent":7,"window_minutes":1440,"resets_at":"2026-11-05T03:47:01Z"},"seven_day":null,"secondary":null}},
 "codex6":{"account":{"id":"sixth","name":"No windows"},"data":{"five_hour":null,"seven_day":null}},
 "codex2":{"data":{"secondary":{"used_percent":1}}},"openai":{"data":{}}}
""");
var parsedAccounts = (IReadOnlyList<UsageMonitor.Services.UsageAccount>)typeof(UsageMonitor.Services.UsageApiService)
    .GetMethod("ParseAdditionalAccounts", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { accountJson.RootElement })!;
Check(parsedAccounts.Count == 6, $"Dynamic parser included legacy or unrelated providers ({parsedAccounts.Count})");
Check(parsedAccounts[0].Id == "third" && parsedAccounts[0].Codex?.CreditsBalance == "0", "Codex #3 identity or credits missing");
Check(parsedAccounts[0].Codex is { Secondary.UsedPercent: 35, Primary: null, OneDay: null }, "Legacy secondary-only Codex not read as 7d");
// A Codex account with only a 24-hour window is shown, not dropped for lacking a weekly window.
Check(parsedAccounts[1].Codex is { Primary: null, Secondary: null, OneDay: { UsedPercent: 12, ExpectedPercent: 30 } } day
      && day.OneDay!.ResetsAt == DateTimeOffset.Parse("2026-11-05T03:47:01Z"), "24-hour-only Codex account missing or misread");
// usage-api builds from before one_day put that window under five_hour/primary; its length files it as 24h.
Check(parsedAccounts[2].Codex is { Primary: null, Secondary: null, OneDay.UsedPercent: 7 }, "Old-API 24-hour window read as 5h");
Check(parsedAccounts[3].Codex == null, "Codex account with no windows should have no block");
Check(parsedAccounts[4].Claude?.SevenDay.UsedPercent == 3, "Claude #12 usage missing");
Check(parsedAccounts[4].Claude?.FiveHour == null, "Claude #12 without a 5-hour window got one");
Check(parsedAccounts[5].Claude?.FiveHour is { UsedPercent: 99, ExpectedPercent: 40 } fh
      && fh.ResetsAt == DateTimeOffset.Parse("2026-11-03T23:30:00Z"), "Claude #13 5-hour window missing");
Console.WriteLine("PASS: arbitrary Codex/Claude accounts, identity, credits, Claude 5h only where reported, Codex 24h-only accounts, legacy compatibility");

using var renewalJson = System.Text.Json.JsonDocument.Parse("""
{"claude":{"account":{"name":"etdofreshai@gmail.com","priority":9},"renewal":{"renews_at":"2026-12-02","billing_cycle":"monthly","days_until_renewal":28,"source":"manual"}},
 "codex":{"account":{"name":"etdofresh@gmail.com"},"renewal":null,"expiry":null},
 "codex2":{"account":{"name":"x@example.com"},"renewal":{"renews_at":"soon"},"expiry":{"expires_at":"2026-12-09","days_until_expiry":26,"source":"provider","ends_at":"2026-12-09T06:15:18.000Z"}}}
""");
var parsedInfo = (IReadOnlyDictionary<string, UsageMonitor.Services.AccountInfo>)typeof(UsageMonitor.Services.UsageApiService)
    .GetMethod("ParseAccountInfo", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { renewalJson.RootElement })!;
Check(parsedInfo["claude"].Renewal == new UsageMonitor.Services.Renewal(new DateOnly(2026, 12, 2), "monthly", 28, "manual"), "Renewal not parsed");
Check(parsedInfo["codex"].Renewal == null && parsedInfo["codex2"].Renewal == null, "Null or malformed renewal not ignored");
Check(parsedInfo["codex2"].Expiry == new UsageMonitor.Services.Expiry(new DateOnly(2026, 12, 9), 26, "provider")
    && parsedInfo["codex"].Expiry == null && parsedInfo["claude"].Expiry == null, "Expiry not parsed");
Console.WriteLine("PASS: renewal and expiry parsed from usage-api, null/malformed ignored");

// Accounts are grouped by provider (Codex, Claude, Z.ai) and sorted by CPA priority, highest first;
// unset priority goes last and ties fall back to email. ET's accounts as of 2026-11-04.
var keyType = typeof(UsagePopup).GetNestedType("AccountSortKey", BindingFlags.NonPublic)!;
object Key(int provider, double? priority, string? mail, int seq, bool blocked = false, DateTimeOffset? availableAt = null) =>
    Activator.CreateInstance(keyType, provider, priority, mail, seq, blocked, availableAt)!;
var sample = new (string Name, object Key)[]
{
    ("codex1 P1", Key(0, 1, "etdofresh@gmail.com", 0)),
    ("codex2 P5", Key(0, 5, "etdofresh+dev@gmail.com", 1)),
    ("claude P9", Key(1, 9, "etdofreshai@gmail.com", 2)),
    ("zai", Key(2, null, null, 4)),
    ("codex3 P5", Key(0, 5, "etdofreshai@gmail.com", 100)),
    ("claude3 P8", Key(1, 8, "etgarcia@uno.edu", 101)),
    ("claude4 P8", Key(1, 8, "etdofresh@gmail.com", 102)),
    ("claude5 P10", Key(1, 10, "etgarcia@my.uno.edu", 103)),
    ("claude6 unset", Key(1, null, "aaa@example.com", 104)),
};
var item = System.Linq.Expressions.Expression.Parameter(typeof((string, object)));
var selector = System.Linq.Expressions.Expression.Lambda(
    System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(item, "Item2"), keyType), item).Compile();
string[] SortNames((string, object)[] items) =>
    ((IEnumerable<(string, object)>)typeof(UsagePopup).GetMethod("SortAccounts", BindingFlags.NonPublic | BindingFlags.Static)!
        .MakeGenericMethod(typeof((string, object))).Invoke(null, new object[] { items, selector })!).Select(x => x.Item1).ToArray();
var sorted = SortNames(sample);
var expectedOrder = new[] { "codex2 P5", "codex3 P5", "codex1 P1", "claude5 P10", "claude P9", "claude4 P8", "claude3 P8", "claude6 unset", "zai" };
Check(sorted.SequenceEqual(expectedOrder), "Account order wrong: " + string.Join(", ", sorted));
Console.WriteLine("PASS: accounts grouped by provider, highest priority first, ties by email, unset priority last, Z.ai after Claude");

// Within a provider: live accounts first by priority, then used-up ones by soonest usable again
// (unknown reset last). ET's accounts as of 2026-11-13.
DateTimeOffset Nov(int day) => new(2026, 11, day, 12, 0, 0, TimeSpan.FromHours(-6));
var states = new (string Name, object Key)[]
{
    ("codex1 live P5", Key(0, 5, "etdofresh@gmail.com", 0)),
    ("codex2 out Nov15 P3", Key(0, 3, "etdofresh+dev@gmail.com", 1, true, Nov(15))),
    ("codex3 out Nov14 P1", Key(0, 1, "etdofreshai@gmail.com", 100, true, Nov(14))),
    ("claude6 out Nov14 P10", Key(1, 10, "etgarcia@lsuneworleans.edu", 105, true, Nov(14))),
    ("claude5 out Nov19 P9", Key(1, 9, "etgarcia@my.uno.edu", 104, true, Nov(19))),
    ("claude7 out Nov19 P8", Key(1, 8, "etdofreshai@gmail.com", 106, true, Nov(19))),
    ("claude3 live P7", Key(1, 7, "etgarcia@uno.edu", 102)),
    ("claude4 out Nov17 P5", Key(1, 5, "etdofresh@gmail.com", 103, true, Nov(17))),
    ("claude9 out unknown P20", Key(1, 20, "x@example.com", 107, true, null)),
    ("claude8 live P2", Key(1, 2, "y@example.com", 108)),
    ("zai", Key(2, null, null, 4)),
};
var stateOrder = SortNames(states);
var expectedStates = new[] { "codex1 live P5", "codex3 out Nov14 P1", "codex2 out Nov15 P3",
    "claude3 live P7", "claude8 live P2", "claude6 out Nov14 P10", "claude4 out Nov17 P5",
    "claude5 out Nov19 P9", "claude7 out Nov19 P8", "claude9 out unknown P20", "zai" };
Check(stateOrder.SequenceEqual(expectedStates), "Live/used-up order wrong: " + string.Join(", ", stateOrder));
Console.WriteLine("PASS: live accounts first by priority, then used-up accounts by soonest reset, within each provider");

// Header links: orange "CPA" opens the CLI Proxy API itself, blue "CPA M" its manager (the old CPA link).
object? Link(string name, Config config) =>
    typeof(UsagePopup).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { config });
T Prop<T>(object? o, string name) => (T)o!.GetType().GetProperty(name)!.GetValue(o)!;
var defaults = new Config();
var proxy = Link("ProxyHeaderLink", defaults);
var manager = Link("ManagerHeaderLink", defaults);
Check(Prop<string>(proxy, "Text") == "CPA" && Prop<string>(manager, "Text") == "CPA M", "Header link labels wrong");
Check(Prop<string>(proxy, "Url") == "http://etzminisforumx1pro.lan:8317/", $"CPA should open the CLI Proxy API: {Prop<string>(proxy, "Url")}");
Check(Prop<string>(manager, "Url") == "http://etzminisforumx1pro.lan:18317/management.html#/accounts", "CPA M should open the manager the old CPA link opened");
Check(Prop<Avalonia.Media.Color>(proxy, "Color") == Avalonia.Media.Color.Parse("#FFB74D"), "CPA should use the Claude orange");
Check(Prop<Avalonia.Media.Color>(manager, "Color") == Avalonia.Media.Color.Parse("#64B5F6"), "CPA M should use the Codex blue");
foreach (var url in new[] { Prop<string>(proxy, "Url"), Prop<string>(manager, "Url") })
{
    var uri = new Uri(url);
    Check(uri.UserInfo == "" && uri.Query == "", $"Header link carries credentials or query parameters: {url}");
}
Check(Prop<bool>(proxy, "IsVisible") && Prop<bool>(manager, "IsVisible"), "Configured header links hidden");
var custom = new Config { CliProxyApiUrl = "https://cpa.example.test/", ProxyDashboardUrl = " " };
Check(Prop<string>(Link("ProxyHeaderLink", custom), "Url") == "https://cpa.example.test/", "CliProxyApiUrl config not used");
Check(!Prop<bool>(Link("ManagerHeaderLink", custom), "IsVisible"), "Blank manager URL should hide CPA M");
Check(!Prop<bool>(Link("ProxyHeaderLink", new Config { CliProxyApiUrl = "" }), "IsVisible"), "Blank proxy URL should hide CPA");
var roundTrip = System.Text.Json.JsonSerializer.Deserialize<Config>("{\"CliProxyApiUrl\":\"http://h:1/\"}")!;
Check(roundTrip.CliProxyApiUrl == "http://h:1/" && roundTrip.ProxyDashboardUrl == defaults.ProxyDashboardUrl, "CliProxyApiUrl not read from config.json");
Check(System.Text.Json.JsonSerializer.Deserialize<Config>("{}")!.CliProxyApiUrl == defaults.CliProxyApiUrl, "Old config.json without CliProxyApiUrl lost the default");
Console.WriteLine("PASS: header CPA (CLI Proxy API, orange) and CPA M (manager, blue) links, configurable, hidden when blank, no credentials");

// Reset credits: Claude's weekly resets on the 7d line, Fable-only ones on the Fab line.
UsageMonitor.Services.ResetCredits ParseResets(string json) =>
    (UsageMonitor.Services.ResetCredits)typeof(UsageMonitor.Services.UsageApiService)
        .GetMethod("ParseResetCredits", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { System.Text.Json.JsonDocument.Parse(json).RootElement })!;
var resetLog = new List<string>();
typeof(UsageMonitor.Services.UsageApiService).GetField("UnknownResetLog", BindingFlags.NonPublic | BindingFlags.Static)!
    .SetValue(null, (Action<string>)resetLog.Add);
var split = ParseResets("{\"reset_credits\":{\"available_count\":3,\"weekly_available\":1,\"fable_available\":2,\"other_available\":0,\"next_expires_at\":\"2026-12-10T22:00:00+00:00\"}}");
Check(split.IsSplit && split.SevenDayCount == 1 && split.FableCount == 2, $"Split resets misread: {split}");
var old = ParseResets("{\"reset_credits\":{\"available_count\":2,\"next_expires_at\":null}}");
Check(!old.IsSplit && old.SevenDayCount == 2 && old.FableCount == 0, $"Older usage-api count not kept on 7d: {old}");
var unknown = ParseResets("{\"reset_credits\":{\"available_count\":3,\"weekly_available\":1,\"fable_available\":0,\"other_available\":2}}");
Check(unknown.SevenDayCount == 3 && unknown.FableCount == 0, "Unknown resets not shown on the 7d line");
ParseResets("{\"reset_credits\":{\"available_count\":3,\"weekly_available\":1,\"fable_available\":0,\"other_available\":2}}");
Check(resetLog.Count == 1 && resetLog[0].Contains("unknown kind (2)"), $"Unknown resets not logged exactly once: {string.Join(" / ", resetLog)}");
Check(split.FableCount == 2 && resetLog.Count == 1, "Known resets logged as unknown");

Dictionary<string, string> PlanLines(UsageMonitor.Services.ResetCredits? r, double? priority, params string[] kinds)
{
    var column = new StackPanel();
    var rows = new[] { "5h", "7d", "Fab", "Des" }.Select(k => (k, (Control)new Grid())).ToArray();
    Call("EnsurePlanSlots", column, rows);
    Call("SetClaudeCompactPlan", column, "Max 20x", r, priority, kinds);
    return column.Children.OfType<TextBlock>().Where(t => t.IsVisible).ToDictionary(t => (string)t.Tag!, t => t.Inlines!.Text ?? "");
}
string Show(Dictionary<string, string> d) => string.Join(" | ", d.Select(kv => $"{kv.Key}={kv.Value}"));
var lines = PlanLines(split, 10, "7d", "Fab");
Check(lines["7d"] == "Max 20x ↻1" && lines["Fab"] == "↻2 P10" && !lines.ContainsKey("extra"), $"Split plan column wrong: {Show(lines)}");
lines = PlanLines(split, null, "5h", "7d", "Fab");
Check(lines["5h"] == "Max 20x" && lines["7d"] == "↻1" && lines["Fab"] == "↻2", $"Weekly reset not on 7d under a 5h row: {Show(lines)}");
lines = PlanLines(split with { WeeklyCount = 0 }, 3, "7d", "Fab");
Check(lines["7d"] == "Max 20x" && lines["Fab"] == "↻2 P3", $"Zero weekly resets not hidden: {Show(lines)}");
lines = PlanLines(split with { FableCount = 0, AvailableCount = 1 }, 3, "7d", "Fab");
Check(lines["7d"] == "Max 20x ↻1" && lines["Fab"] == "P3", $"Zero Fable resets not hidden: {Show(lines)}");
lines = PlanLines(old, 9, "7d", "Fab");
Check(lines["7d"] == "Max 20x ↻2" && lines["Fab"] == "P9", $"Older usage-api fallback wrong: {Show(lines)}");
lines = PlanLines(split, null, "7d");
Check(lines["7d"] == "Max 20x ↻1" && lines["extra"] == "Fab↻2", $"Fable reset lost when the Fab row is hidden: {Show(lines)}");
lines = PlanLines(null, null, "7d", "Fab");
Check(lines["7d"] == "Max 20x" && lines["Fab"] == "" && !lines.ContainsKey("extra"), $"No-reset plan column wrong: {Show(lines)}");
Check((string?)Call("ResetBadge", split) == "↻1 Fab↻2" && (string?)Call("ResetBadge", old) == "↻2"
    && Call("ResetBadge", split with { WeeklyCount = 0, FableCount = 0, AvailableCount = 0 }) == null, "Full-view reset badge wrong");
var tip = (string)Call("ResetTooltip", split)!;
Check(tip.StartsWith("1 weekly reset, 2 Fable resets available"), $"Reset tooltip wrong: {tip}");
Check(((string)Call("ResetTooltip", unknown)!).Contains("2 unrecognized resets (counted on 7d)"), "Unknown resets missing from tooltip");
Check(((string)Call("ResetTooltip", old)!).StartsWith("2 limit resets available"), "Older usage-api tooltip changed");
Console.WriteLine("PASS: Claude weekly resets on the 7d line, Fable resets on the Fab line, unknown on 7d, older usage-api unchanged");

// Greying an exhausted account keeps its "↻N" reset badges cyan in every place they are drawn.
bool KeepsCyan(TextBlock tb)
{
    tb.Foreground = Avalonia.Media.Brushes.Gray;
    foreach (var inline in tb.Inlines!.Where(i => !(bool)Call("KeepsOwnColor", i)!))
        inline.ClearValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty);
    var badges = tb.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Where(r => r.Text?.Contains('↻') == true).ToList();
    return badges.Count > 0 && badges.All(i => i.Foreground is Avalonia.Media.ISolidColorBrush { Color: var c }
        && c == Avalonia.Media.Color.FromRgb(0x4D, 0xD0, 0xE1));
}
var cyanPlan = new TextBlock();
Call("SetCompactPlan", cyanPlan, "prolite", split, (double?)1);
var cyanTitle = new TextBlock { FontSize = 10 };
Call("SetTitleWithPlan", cyanTitle, "Codex #3", "prolite", split, null, now);
var cyanColumn = new StackPanel();
Call("EnsurePlanSlots", cyanColumn, new[] { "7d", "Fab" }.Select(k => (k, (Control)new Grid())).ToArray());
Call("SetClaudeCompactPlan", cyanColumn, "Max 20x", split, (double?)8, new[] { "7d", "Fab" });
Check(KeepsCyan(cyanPlan) && KeepsCyan(cyanTitle)
    && cyanColumn.Children.OfType<TextBlock>().Where(t => t.Inlines!.Text?.Contains('↻') == true).All(KeepsCyan),
    "Reset badge greyed out with its exhausted account");
Console.WriteLine("PASS: reset badges stay cyan when their account is greyed out");

// Memory watchdog: relaunch only after two consecutive samples over a positive limit.
const long limit = 1536L * 1024 * 1024;
Check(!UsageMonitor.Services.MemoryWatchdog.ShouldRestart(1, limit), "Watchdog fired on a single spike");
Check(UsageMonitor.Services.MemoryWatchdog.ShouldRestart(2, limit), "Watchdog ignored a sustained overrun");
Check(!UsageMonitor.Services.MemoryWatchdog.ShouldRestart(5, 0), "Watchdog fired with the limit disabled");
var mem = UsageMonitor.Services.MemoryWatchdog.Read();
Check(mem.FootprintBytes is > 1024 * 1024 and < 64L * 1024 * 1024 * 1024, $"Implausible footprint {mem.FootprintBytes}");
if (OperatingSystem.IsMacOS())
{
    Check(mem.RegionCount > 100, $"VM region count not read: {mem.RegionCount}");
    // Must agree with what vmmap reports as the physical footprint (within 25% / 16 MB).
    var vmmap = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/vmmap",
        $"--summary {Environment.ProcessId}") { RedirectStandardOutput = true, RedirectStandardError = true })!;
    var summary = vmmap.StandardOutput.ReadToEnd();
    vmmap.WaitForExit();
    var line = summary.Split('\n').FirstOrDefault(l => l.StartsWith("Physical footprint:"));
    if (line != null)
    {
        var parts = line.Split(':')[1].Trim();
        var value = double.Parse(parts[..^1], CultureInfo.InvariantCulture);
        var bytes = (long)(value * parts[^1] switch { 'K' => 1024d, 'M' => 1048576d, 'G' => 1073741824d, _ => 1d });
        var sampled = UsageMonitor.Services.MemoryWatchdog.Read().FootprintBytes;
        Check(Math.Abs(sampled - bytes) < Math.Max(16L * 1024 * 1024, bytes / 4), $"Footprint {sampled} disagrees with vmmap {bytes}");
    }
}
Console.WriteLine($"PASS: memory watchdog restart rule and self-sampling ({UsageMonitor.Services.MemoryWatchdog.Format(mem)})");

if (Environment.GetEnvironmentVariable("USAGE_CHECK_LIVE_URL") is { Length: > 0 } liveUrl)
{
    using var service = new UsageMonitor.Services.UsageApiService(liveUrl);
    var status = await service.GetStatusAsync();
    Check(status?.Codex != null && status.Claude != null, "Live primary accounts missing");
    Console.WriteLine("Live Codex #1 windows: " + string.Join(", ", ((IEnumerable<(string Label, UsageMonitor.Services.UsageWindow Window)>)Call("CodexAccountWindows", status!.Codex)!)
        .Select(w => $"{w.Label} {w.Window.UsedPercent}%")));
    Check(status!.AdditionalAccounts?.Any(a => a.Key == "codex3" && a.Codex != null) == true, "Live Codex #3 missing");
    Console.WriteLine("PASS: live monitor API client reads primary accounts and Codex #3");
    using var recentService = new UsageMonitor.Services.GateService(liveUrl);
    var recent = await recentService.GetRecentAsync();
    Check(recent is { Count: > 0 and <= 20 }, "CPA recent-model strip did not receive recorded requests");
    Console.WriteLine("PASS: live CPA recent-model strip receives at most 20 requests");
}
