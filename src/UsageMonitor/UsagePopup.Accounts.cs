using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UsageMonitor.Services;

namespace UsageMonitor;

public partial class UsagePopup
{
    public event Action? AccountsChanged;
    private IReadOnlyList<UsageAccount> _additionalAccounts = Array.Empty<UsageAccount>();
    public IReadOnlyList<UsageAccount> AdditionalAccountToggles => _additionalAccounts;
    public bool IsAccountVisible(string id) => _config.AccountVisibility == null || !_config.AccountVisibility.TryGetValue(id, out var show) || show;

    public void SetAccountVisible(string id, bool visible)
    {
        (_config.AccountVisibility ??= new())[id] = visible;
        _config.Save();
        ApplyAdditionalAccounts(_additionalAccounts);
    }

    private void ApplyAdditionalAccounts(IReadOnlyList<UsageAccount>? accounts)
    {
        accounts ??= Array.Empty<UsageAccount>();
        var changed = !_additionalAccounts.Select(a => (a.Id, a.Name)).SequenceEqual(accounts.Select(a => (a.Id, a.Name)));
        _additionalAccounts = accounts;
        // Rebuilding tears down and recreates every account card (controls, text layouts,
        // tooltips, cursors) on each poll. Only do it when what the cards show has changed.
        var renderKey = AccountsRenderKey(accounts, DateTimeOffset.Now);
        if (renderKey == _accountsRenderKey)
        {
            OrderCompactAccounts();
            if (changed) AccountsChanged?.Invoke();
            return;
        }
        _accountsRenderKey = renderKey;
        AdditionalAccountsFull.Children.Clear();
        foreach (var old in _extraCompactKeys.Keys) AccountsCompact.Children.Remove(old);
        _extraCompactKeys.Clear();
        // Same order as the compact list: Codex then Claude, each by CPA priority (highest first).
        var ordered = SortAccounts(accounts.Select((a, i) => (Account: a,
            Key: new AccountSortKey(ProviderRank(a.Key), AccountInfoFor(a.Key)?.Priority ?? a.Priority, AccountEmail(a), 100 + i))), x => x.Key);
        foreach (var (account, sortKey) in ordered)
        {
            if (!IsAccountVisible(account.Id)) continue;
            var codex = account.Key.StartsWith("codex", StringComparison.Ordinal);
            var number = account.Key[(codex ? 5 : 6)..];
            var label = (codex ? "Codex" : "Claude") + number;
            var windows = new List<(string Label, UsageWindow Window)>();
            if (account.Codex is { } cx)
            {
                windows.AddRange(CodexAccountWindows(cx));
                if (_config.ShowCodexSpark && cx.SparkPrimary != null) windows.Add(("Sp5h", cx.SparkPrimary));
                if (_config.ShowCodexSpark && cx.SparkSecondary != null) windows.Add(("Sp7d", cx.SparkSecondary));
            }
            if (account.Claude is { } cc)
            {
                if (cc.FiveHour != null) windows.Add(("5h", cc.FiveHour));
                windows.Add(("7d", cc.SevenDay));
                if (_config.ShowClaudeFable && cc.SevenDayFable != null) windows.Add(("Fab", cc.SevenDayFable));
                if (_config.ShowClaudeDesign && cc.SevenDayDesign != null) windows.Add(("Des", cc.SevenDayDesign));
            }
            if (windows.Count == 0) continue;
            // usage-api keeps serving an account's last good numbers when its fetches fail;
            // grey those out and say how old they are instead of passing them off as live.
            var stale = account.Staleness == "stale";
            var exhausted = !stale && windows.Any(w => w.Window.UsedPercent >= 100);
            var color = stale ? StaleColor : exhausted ? ExhaustedColor : (codex ? CodexAccentColor : ClaudeAccentColor);
            var plan = account.Codex?.PlanType ?? account.Claude?.SubscriptionType;
            var resets = account.Codex?.Resets ?? account.Claude?.Resets;
            var full = new StackPanel { Spacing = 3 };
            var title = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(color) };
            // usage-api falls back to the key ("claude3") when it has no account name; that is not an email.
            var email = AccountEmail(account);
            var info = WithAvailability(AccountInfoFor(account.Key) is { } known ? known with { Email = email }
                : new AccountInfo(email, account.Priority, account.FetchedAt, null, account.Staleness),
                AccountWideWindows(account.Codex, account.Claude));
            SetTitleWithPlan(title, (codex ? "Codex #" : "Claude #") + number, plan, resets, info);
            ToolTip.SetTip(title, string.Join("\n", new[] { account.Name, AvailabilityTooltip(info), ExpiryTooltip(info.Expiry), RenewalTooltip(info.Renewal), ResetTooltip(resets), DataAgeTooltip(info) }.Where(t => t != null)));
            full.Children.Add(title);
            MakeLink(title, codex ? _config.CodexUsageUrl : _config.ClaudeUsageUrl);
            var compact = new DockPanel { LastChildFill = true };
            var divider = new Border { Height = 1, Background = Brush.Parse("#2A2A2A"), Margin = new Thickness(0, 1, 0, 3) };
            DockPanel.SetDock(divider, Dock.Top);
            compact.Children.Add(divider);
            var emailText = new TextBlock { FontSize = 7, Foreground = new SolidColorBrush(EmailColor),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 1) };
            SetCompactEmail(emailText, info);
            DockPanel.SetDock(emailText, Dock.Top);
            compact.Children.Add(emailText);
            // Codex: one plan line. Claude: one line per row, so weekly resets sit on 7d and Fable ones on Fab.
            Control planColumn;
            TextBlock? planText = null;
            StackPanel? planSlots = null;
            if (codex)
            {
                planText = new TextBlock { Width = 58, FontSize = 8, TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 0, 0), Foreground = Brush.Parse("#888888") };
                SetCompactPlan(planText, plan, resets, account.Priority);
                planColumn = planText;
            }
            else planColumn = planSlots = new StackPanel { Width = 58, Spacing = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(planColumn, Dock.Right);
            compact.Children.Add(planColumn);
            var rows = new StackPanel { Spacing = 1 };
            compact.Children.Add(rows);
            ToolTip.SetTip(compact, account.Name);
            var rowControls = new List<(string Kind, Control Row)>();
            foreach (var (name, window) in windows)
            {
                var row = AccountUsageRow(label + " " + name, window, color);
                rowControls.Add((name, row));
                rows.Children.Add(row);
                full.Children.Add(AccountUsageRow(name, window, color));
            }
            if (planSlots != null)
            {
                EnsurePlanSlots(planSlots, rowControls.ToArray());
                SetClaudeCompactPlan(planSlots, plan, resets, account.Priority, rowControls.Select(r => r.Kind).ToArray());
            }
            WireCompactRowLinks(rows, codex ? _config.CodexUsageUrl : _config.ClaudeUsageUrl);
            if (stale)
            {
                var note = "Stale since " + FormatFetchedAt(account.FetchedAt);
                var tip = account.Error ?? "usage-api could not refresh this account";
                // The rest of the account is greyed; its refresh note stays bright so it stands out.
                var compactNote = new TextBlock { Text = label + " " + note, FontSize = 8, Foreground = new SolidColorBrush(StaleNoteColor) };
                var fullNote = new TextBlock { Text = note, FontSize = 8, Foreground = new SolidColorBrush(StaleNoteColor), TextWrapping = TextWrapping.Wrap };
                ToolTip.SetTip(compactNote, tip);
                ToolTip.SetTip(fullNote, tip);
                rows.Children.Add(compactNote);
                full.Children.Add(fullNote);
            }
            var balance = account.Codex?.CreditsBalance;
            if (decimal.TryParse(balance, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount))
                balance = amount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(balance))
            {
                rows.Children.Add(new TextBlock { Text = label + " CR  " + balance, FontSize = 9, Foreground = new SolidColorBrush(color) });
                full.Children.Add(new TextBlock { Text = "Credits  " + balance, FontSize = 9, Foreground = new SolidColorBrush(color) });
            }
            if (exhausted)
                foreach (var text in new[] { title, planText }.Concat(planSlots?.Children.OfType<TextBlock>() ?? Enumerable.Empty<TextBlock>()).OfType<TextBlock>())
                {
                    text.Foreground = new SolidColorBrush(color);
                    if (text.Inlines != null)
                        // Reset badges and the availability countdown keep their own colors so they stay readable.
                        foreach (var inline in text.Inlines.Where(i => !KeepsOwnColor(i)))
                            inline.ClearValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty);
                }
            _extraCompactKeys[compact] = sortKey;
            AccountsCompact.Children.Add(compact);
            AdditionalAccountsFull.Children.Add(new Border { Child = full, Background = Brush.Parse("#222222"),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(8) });
        }
        if (AdditionalAccountsFull.Parent is Control fullHost) fullHost.IsVisible = AdditionalAccountsFull.Children.Count > 0;
        OrderCompactAccounts();
        if (changed) AccountsChanged?.Invoke();
    }

    private string? _accountsRenderKey;

    // Everything the additional-account cards are built from. Relative times in them ("in 4h 30m",
    // "Stale since 3m ago") are minute-resolution, so the minute is part of the key; the countdown
    // badges tick every 2 s on their own (RefreshAvailabilityBadges) without a rebuild.
    internal string AccountsRenderKey(IReadOnlyList<UsageAccount> accounts, DateTimeOffset now) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            Accounts = accounts,
            Info = accounts.Select(a => AccountInfoFor(a.Key)),
            Visible = accounts.Select(a => IsAccountVisible(a.Id)),
            _config.ShowCodexSpark,
            _config.ShowClaudeFable,
            _config.ShowClaudeDesign,
            _config.CodexUsageUrl,
            _config.ClaudeUsageUrl,
            Minute = now.ToUnixTimeSeconds() / 60,
        });

    // usage-api falls back to the key ("claude3") when it has no account name; that is not an email.
    private static string? AccountEmail(UsageAccount account) => account.Name == account.Key ? null : account.Name;

    // Compact account blocks built here, with their place in the provider/priority order.
    private readonly Dictionary<Control, AccountSortKey> _extraCompactKeys = new();

    internal sealed record AccountSortKey(int Provider, double? Priority, string? Email, int Seq);

    private static int ProviderRank(string key) =>
        key.StartsWith("codex", StringComparison.Ordinal) ? 0 : key.StartsWith("claude", StringComparison.Ordinal) ? 1 : 2;

    // Provider (Codex, Claude, Z.ai), then CPA priority highest first (CPA routes to it first),
    // unset priority last, then email, then the original order.
    internal static IEnumerable<T> SortAccounts<T>(IEnumerable<T> items, Func<T, AccountSortKey> key) =>
        items.OrderBy(i => key(i).Provider)
            .ThenBy(i => key(i).Priority.HasValue ? 0 : 1)
            .ThenByDescending(i => key(i).Priority ?? 0)
            .ThenBy(i => key(i).Email ?? "\uffff", StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => key(i).Seq);

    private AccountSortKey FixedKey(string key, int seq) =>
        new(ProviderRank(key), AccountInfoFor(key)?.Priority, AccountInfoFor(key)?.Email, seq);

    private TextBlock?[] _groupLabels = new TextBlock?[3];
    private static readonly (string Text, string Color)[] GroupLabelStyle =
        { ("Codex", "#8BC34A"), ("Claude", "#FF8A65"), ("Z.ai", "#BA68C8") };

    /// Puts every compact account block in provider order, each provider under a small label.
    private void OrderCompactAccounts()
    {
        if (AccountsCompact == null) return;
        var blocks = new List<(Control Block, AccountSortKey Key)>
        {
            (CodexCompactSection, FixedKey("codex", 0)), (Codex2CompactSection, FixedKey("codex2", 1)),
            (ClaudeCompactSection, FixedKey("claude", 2)), (Claude2CompactSection, FixedKey("claude2", 3)),
            (ZaiCompactSection, new AccountSortKey(2, null, null, 4)),
        };
        blocks.AddRange(_extraCompactKeys.Select(kv => (kv.Key, kv.Value)));
        var desired = new List<Control>();
        foreach (var group in SortAccounts(blocks, b => b.Key).GroupBy(b => b.Key.Provider))
        {
            var label = _groupLabels[group.Key] ??= new TextBlock
            {
                Text = GroupLabelStyle[group.Key].Text, FontSize = 8, FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse(GroupLabelStyle[group.Key].Color), Opacity = 0.8, Margin = new Thickness(0, 3, 0, -1),
            };
            label.IsVisible = group.Any(b => b.Block.IsVisible);
            desired.Add(label);
            desired.AddRange(group.Select(b => b.Block));
        }
        if (AccountsCompact.Children.SequenceEqual(desired)) return;
        AccountsCompact.Children.Clear();
        AccountsCompact.Children.AddRange(desired);
    }

    private static readonly Color StaleColor = Color.Parse("#777777");
    private static readonly Color StaleNoteColor = Color.FromRgb(0xFF, 0xB7, 0x4D);
    // Account-card accents, shared with the header's CPA (orange) and CPA M (blue) links.
    private static readonly Color CodexAccentColor = Color.Parse("#64B5F6");
    private static readonly Color ClaudeAccentColor = Color.Parse("#FFB74D");

    private static Grid AccountUsageRow(string label, UsageWindow window, Color color)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("68,68,42,*"), MinHeight = 13 };
        row.Children.Add(new TextBlock { Text = label, FontSize = 9, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
        var bar = new Grid { Width = 62, Height = 4, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        bar.Children.Add(new Border { Background = Brush.Parse("#333333"), CornerRadius = new CornerRadius(2) });
        bar.Children.Add(new Border { Background = new SolidColorBrush(color), Width = 62 * Math.Clamp(window.UsedPercent, 0, 100) / 100,
            HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) });
        if (window.ExpectedPercent is { } expected)
            bar.Children.Add(new Border { Background = Brushes.White, Width = 2, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(60 * Math.Clamp(expected, 0, 100) / 100, 0, 0, 0) });
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);
        var percent = new TextBlock { FontSize = 8, Foreground = new SolidColorBrush(color), TextAlignment = TextAlignment.Right };
        SetUsedExpectedInlines(percent, window.UsedPercent, color == ExhaustedColor ? null : window.ExpectedPercent);
        Grid.SetColumn(percent, 2);
        row.Children.Add(percent);
        // Windows that reset within a day or so count down; weekly ones show the date.
        var reset = new TextBlock { Text = label.EndsWith("5h") || label.EndsWith("24h") ? FormatResetCountdown(window.ResetsAt) : FormatResetDate(window.ResetsAt),
            FontSize = 8, Foreground = color == ExhaustedColor ? new SolidColorBrush(color) : Brush.Parse("#888888"), TextAlignment = TextAlignment.Right };
        SetResetTimesTooltip(reset, ("Resets", window.ResetsAt));
        Grid.SetColumn(reset, 3);
        row.Children.Add(reset);
        return row;
    }
}
