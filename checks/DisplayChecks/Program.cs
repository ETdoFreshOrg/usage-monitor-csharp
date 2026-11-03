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
Call("SetTitleWithPlan", title, "Claude", "Max 20x", null, new UsageMonitor.Services.AccountInfo("etdofresh@gmail.com", 10));
Check(title.Inlines!.Text == "Claude  Max 20x  P10\netdofresh@gmail.com", $"Full title wrong: {title.Inlines.Text}");
var email = new TextBlock();
Call("SetCompactEmail", email, "etdofresh@gmail.com");
Check(email.IsVisible && email.Text == "etdofresh", "Compact email header wrong");
Call("SetCompactEmail", email, (string?)null);
Check(!email.IsVisible, "Empty compact email header still visible");
Console.WriteLine("PASS: credit balances, row visibility, reset times (CDT/CST), tooltip clearing, account email/priority");

using var accountJson = System.Text.Json.JsonDocument.Parse("""
{"codex3":{"account":{"id":"third","name":"Third Account"},"data":{"secondary":{"used_percent":35},"credits_balance":"0"}},
 "claude12":{"account":{"id":"twelfth","name":"Twelfth Account"},"data":{"seven_day":{"utilization":3}}},
 "codex2":{"data":{"secondary":{"used_percent":1}}},"openai":{"data":{}}}
""");
var parsedAccounts = (IReadOnlyList<UsageMonitor.Services.UsageAccount>)typeof(UsageMonitor.Services.UsageApiService)
    .GetMethod("ParseAdditionalAccounts", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { accountJson.RootElement })!;
Check(parsedAccounts.Count == 2, "Dynamic parser included legacy or unrelated providers");
Check(parsedAccounts[0].Id == "third" && parsedAccounts[0].Codex?.CreditsBalance == "0", "Codex #3 identity or credits missing");
Check(parsedAccounts[1].Claude?.SevenDay.UsedPercent == 3, "Claude #12 usage missing");
Console.WriteLine("PASS: arbitrary Codex/Claude accounts, identity, credits, legacy compatibility");

if (Environment.GetEnvironmentVariable("USAGE_CHECK_LIVE_URL") is { Length: > 0 } liveUrl)
{
    using var service = new UsageMonitor.Services.UsageApiService(liveUrl);
    var status = await service.GetStatusAsync();
    Check(status?.Codex != null && status.Claude != null, "Live primary accounts missing");
    Check(status!.AdditionalAccounts?.Any(a => a.Key == "codex3" && a.Codex != null) == true, "Live Codex #3 missing");
    Console.WriteLine("PASS: live monitor API client reads primary accounts and Codex #3");
    using var recentService = new UsageMonitor.Services.GateService(liveUrl);
    var recent = await recentService.GetRecentAsync();
    Check(recent is { Count: > 0 and <= 20 }, "CPA recent-model strip did not receive recorded requests");
    Console.WriteLine("PASS: live CPA recent-model strip receives at most 20 requests");
}
