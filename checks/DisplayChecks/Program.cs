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
Console.WriteLine("PASS: credit balances, row visibility, reset times (CDT/CST), tooltip clearing, account email/priority, availability badge + ticking, 24h badge, data age tooltip");

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
