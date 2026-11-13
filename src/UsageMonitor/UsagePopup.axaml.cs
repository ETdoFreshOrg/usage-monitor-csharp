using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UsageMonitor.Services;

namespace UsageMonitor;

public partial class UsagePopup : Window
{
    private enum PopupViewMode
    {
        Full,
        Compact,
    }

    private const double FullWidth = 572;
    private const double FullHeight = 450;
    // Keep the 90 px margin over the compact panel's Width in UsagePopup.axaml (324).
    private const double CompactWidth = 414;
    private const double CompactHeight = 124;

    private readonly Config _config;
    private readonly DispatcherTimer _systemRefreshTimer;
    private readonly DispatcherTimer _aiRefreshTimer;
    private readonly Bitmap _appBitmap;
    private PopupViewMode _viewMode = PopupViewMode.Full;
    private bool _allowClose;
    private bool _shutdownStarted;
    private bool _updateInProgress;

    // Drag state
    private bool _isDragging;
    private int _lastExternalFrontmostProcessId;
    private PixelPoint _dragStartScreenPoint;
    private PixelPoint _windowStartPosition;

    // Pinned bottom-right corner in screen pixels. When set, the window re-anchors
    // here on every Bounds change so async content growth (AI sections appearing,
    // view-mode switches) does not push the bottom edge past the taskbar.
    // Cleared the moment the user drags, so a dragged window stays put.
    private PixelPoint? _anchorBottomRight;
    private double _anchorScaling = 1;

    // Network tracking
    private long _lastBytesSent;
    private long _lastBytesReceived;
    private DateTime _lastNetworkCheck = DateTime.MinValue;

    // CPU tracking. Windows uses PerformanceCounter; Unix-like systems sample
    // cumulative kernel counters and calculate the delta between refreshes.
    private PerformanceCounter? _cpuCounter;
    private ulong _lastCpuTotal;
    private ulong _lastCpuIdle;
    private bool _hasCpuSample;
    private double _cpuPercent;
    private double _memoryPercent;
    private double _memoryUsedGB;
    private double _memoryTotalGB;

    public sealed record DriveToggle(string Key, string Label);

    private sealed class DriveDisplay
    {
        public required DriveInfo Drive { get; init; }
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required Grid FullRow { get; init; }
        public required Grid FullBar { get; init; }
        public required Border FullFill { get; init; }
        public required TextBlock FullUsage { get; init; }
        public required Grid CompactRow { get; init; }
        public required Border CompactFill { get; init; }
        public required TextBlock CompactPercent { get; init; }
        public required TextBlock CompactUsage { get; init; }
    }

    private readonly List<DriveDisplay> _driveDisplays = new();
    public IReadOnlyList<DriveToggle> DriveToggles { get; private set; } = Array.Empty<DriveToggle>();

    // Latest reset timestamps (driven by RefreshXxxAsync, read by UpdateCompactSummary)
    private DateTimeOffset? _codex5hReset, _codex24hReset, _codex7dReset;
    private DateTimeOffset? _codexSpark5hReset, _codexSpark7dReset;
    private DateTimeOffset? _codex2FiveHourReset, _codex2OneDayReset, _codex2SevenDayReset;
    private DateTimeOffset? _codex2SparkFiveHourReset, _codex2SparkSevenDayReset;
    private DateTimeOffset? _claude5hReset, _claude7dReset, _claudeDesignReset, _claudeFableReset;
    private DateTimeOffset? _claude2FiveHourReset, _claude2SevenDayReset, _claude2DesignReset, _claude2FableReset;
    private DateTimeOffset? _zai5hReset, _zaiMoReset;
    private double? _zai5hPercent, _zaiMoPercent;

    // Latest used/expected percent per AI window, used for compact-view rendering.
    // Codex windows are each optional (null = not reported, e.g. promax has no 5-hour window).
    private double? _codex5hUsed, _codex24hUsed, _codex7dUsed;
    private double? _codex5hExpected, _codex24hExpected, _codex7dExpected;
    private double? _codexSpark5hUsed, _codexSpark7dUsed;
    private double? _codexSpark5hExpected, _codexSpark7dExpected;
    private double? _codex2FiveHourUsed, _codex2OneDayUsed, _codex2SevenDayUsed;
    private double? _codex2FiveHourExpected, _codex2OneDayExpected, _codex2SevenDayExpected;
    private string? _codexPlan;
    private ResetCredits? _codexResets;
    private double? _codex2SparkFiveHourUsed, _codex2SparkSevenDayUsed;
    private double? _codex2SparkFiveHourExpected, _codex2SparkSevenDayExpected;
    private double? _claude5hUsed;
    private double _claude7dUsed;
    private double? _claude5hExpected, _claude7dExpected;
    private double? _claudeDesignUsed;
    private double? _claudeDesignExpected;
    private double? _claudeFableUsed;
    private double? _claudeFableExpected;
    private double? _claude2FiveHourUsed;
    private double _claude2SevenDayUsed;
    private double? _claude2FiveHourExpected, _claude2SevenDayExpected;
    private double? _claude2DesignUsed;
    private double? _claude2DesignExpected;
    private double? _claude2FableUsed;
    private double? _claude2FableExpected;
    private double? _zai5hExpected, _zaiMoExpected;

    // Per-bar state for target-aware rendering: used%, expected% (pace target), base color.
    private sealed class TargetBarState
    {
        public Grid Container = null!;
        public Border Fill = null!;
        public Border Tick = null!;
        public Color BaseColor;
        public double Used;
        public double? Expected;
        public bool Exhausted;
    }
    private readonly List<TargetBarState> _targetBars = new();
    private static readonly Color ExhaustedColor = Color.FromRgb(0x61, 0x61, 0x61);
    private readonly Dictionary<TextBlock, IBrush?> _textForegrounds = new();

    // Single source of truth: the usage-api aggregator.
    private UsageApiService? _usageApiService;

    // Auto-update checker
    private UpdateChecker? _updateChecker;
    private MemoryWatchdog? _memoryWatchdog;
    private bool _updateAvailable;

    // Last successful usage snapshot, retained so provider show/hide toggles can
    // re-render visibility immediately without waiting for the next poll.
    private UsageApiStatus? _lastStatus;
    private GateService? _gateService;
    private IReadOnlyList<GateRequest>? _lastGate;

    // Provider visibility toggles surfaced (in this order) in the tray "Providers" menu.
    public enum ProviderToggle { OpenAi, OpenRouter, Jev, Gate, Codex, Codex2, CodexSpark, Claude, Claude2, ClaudeDesign, ClaudeFable, Claude2Design, Zai, ZaiRequests }

    public static readonly IReadOnlyList<(ProviderToggle Key, string Label)> ProviderToggles = new[]
    {
        (ProviderToggle.OpenAi, "OpenAI"),
        (ProviderToggle.OpenRouter, "OpenRouter"),
        (ProviderToggle.Jev, "Jev Spend"),
        (ProviderToggle.Gate, "CPA Recent Models"),
        (ProviderToggle.Codex, "Codex #1"),
        (ProviderToggle.Codex2, "Codex #2"),
        (ProviderToggle.CodexSpark, "Codex Spark"),
        (ProviderToggle.Claude, "Claude"),
        (ProviderToggle.Claude2, "Claude2"),
        (ProviderToggle.ClaudeDesign, "Claude Design"),
        (ProviderToggle.ClaudeFable, "Claude Fable"),
        (ProviderToggle.Claude2Design, "Claude2 Design"),
        (ProviderToggle.Zai, "Z.ai Token Usage"),
        (ProviderToggle.ZaiRequests, "Z.ai Web/MCP Requests"),
    };

    public UsagePopup()
    {
        InitializeComponent();
        Icon = AppIcon.Create();
        _appBitmap = AppIcon.CreateBitmap();
        RestoreFullIcon.Source = _appBitmap;

        _config = Config.Load();
        InitializeDriveDisplays();

        // Wire up close button
        CloseButton.Click += (s, e) => HidePopup();
        CloseCompactButton.Click += (s, e) => HidePopup();
        CompactButton.Click += (s, e) => SetViewMode(PopupViewMode.Compact);
        RestoreFullButton.Click += (s, e) => SetViewMode(PopupViewMode.Full);
        UpdateButton.Click += async (_, _) => await ApplyUpdateAsync();
        UpdateButtonCompact.Click += async (_, _) => await ApplyUpdateAsync();
        UpdateModalCloseButton.Click += (_, _) => CloseUpdateModal();
        MonitorTitleText.PointerPressed += (_, _) => OpenUsageDashboard();
        WireRouterPanelLinks();
        WireProviderLinks();
        // Enable dragging from title bar area (works in all modes including compact)
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;

        // System stats refresh (every 2 seconds)
        _systemRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _systemRefreshTimer.Tick += SystemRefreshTimer_Tick;

        // AI credits refresh — fast, since usage-api is just a local aggregator with cached snapshots.
        var aiRefreshIntervalSeconds = Math.Max(1, _config.RefreshIntervalSeconds);
        _aiRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(aiRefreshIntervalSeconds) };
        _aiRefreshTimer.Tick += AiRefreshTimer_Tick;

        // Initialize CPU counter on Windows
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue(); // First call always returns 0, prime it
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize CPU counter: {ex.Message}");
            }
        }

        // Register target-aware bars so they re-render on resize.
        RegisterTargetBar(CodexPrimaryBar, CodexPrimaryBarFill, CodexPrimaryBarTick, Color.FromRgb(0x8B, 0xC3, 0x4A));
        RegisterTargetBar(CodexDailyBar, CodexDailyBarFill, CodexDailyBarTick, Color.FromRgb(0xCD, 0xDC, 0x39));
        RegisterTargetBar(CodexSecondaryBar, CodexSecondaryBarFill, CodexSecondaryBarTick, Color.FromRgb(0xB3, 0x9D, 0xDB));
        RegisterTargetBar(CodexSparkPrimaryBar, CodexSparkPrimaryBarFill, CodexSparkPrimaryBarTick, Color.FromRgb(0x4D, 0xD0, 0xE1));
        RegisterTargetBar(CodexSparkSecondaryBar, CodexSparkSecondaryBarFill, CodexSparkSecondaryBarTick, Color.FromRgb(0x4D, 0xB6, 0xAC));
        RegisterTargetBar(Codex2PrimaryBar, Codex2PrimaryBarFill, Codex2PrimaryBarTick, Color.FromRgb(0x64, 0xB5, 0xF6));
        RegisterTargetBar(Codex2DailyBar, Codex2DailyBarFill, Codex2DailyBarTick, Color.FromRgb(0x4F, 0xC3, 0xF7));
        RegisterTargetBar(Codex2SecondaryBar, Codex2SecondaryBarFill, Codex2SecondaryBarTick, Color.FromRgb(0x90, 0xCA, 0xF9));
        RegisterTargetBar(Codex2SparkPrimaryBar, Codex2SparkPrimaryBarFill, Codex2SparkPrimaryBarTick, Color.FromRgb(0x95, 0x75, 0xCD));
        RegisterTargetBar(Codex2SparkSecondaryBar, Codex2SparkSecondaryBarFill, Codex2SparkSecondaryBarTick, Color.FromRgb(0xB3, 0x9D, 0xDB));
        RegisterTargetBar(ClaudeCodePrimaryBar, ClaudeCodePrimaryBarFill, ClaudeCodePrimaryBarTick, Color.FromRgb(0xFF, 0x8A, 0x65));
        RegisterTargetBar(ClaudeCodeSecondaryBar, ClaudeCodeSecondaryBarFill, ClaudeCodeSecondaryBarTick, Color.FromRgb(0xFF, 0xB7, 0x4D));
        RegisterTargetBar(ClaudeDesignBar, ClaudeDesignBarFill, ClaudeDesignBarTick, Color.FromRgb(0xF4, 0x8F, 0xB1));
        RegisterTargetBar(ClaudeFableBar, ClaudeFableBarFill, ClaudeFableBarTick, Color.FromRgb(0xC2, 0x41, 0x0C));
        RegisterTargetBar(ClaudeCode2PrimaryBar, ClaudeCode2PrimaryBarFill, ClaudeCode2PrimaryBarTick, Color.FromRgb(0xFF, 0x8A, 0x65));
        RegisterTargetBar(ClaudeCode2SecondaryBar, ClaudeCode2SecondaryBarFill, ClaudeCode2SecondaryBarTick, Color.FromRgb(0xFF, 0xB7, 0x4D));
        RegisterTargetBar(ClaudeDesign2Bar, ClaudeDesign2BarFill, ClaudeDesign2BarTick, Color.FromRgb(0xF4, 0x8F, 0xB1));
        RegisterTargetBar(ClaudeFable2Bar, ClaudeFable2BarFill, ClaudeFable2BarTick, Color.FromRgb(0xC2, 0x41, 0x0C));
        RegisterTargetBar(ZaiTokenBar, ZaiTokenBarFill, ZaiTokenBarTick, Color.FromRgb(0xBA, 0x68, 0xC8));
        RegisterTargetBar(ZaiMonthlyBar, ZaiMonthlyBarFill, ZaiMonthlyBarTick, Color.FromRgb(0x7E, 0x57, 0xC2));

        // Initialize AI services
        InitializeAiServices();

        // Show build info
        BuildInfoText.Text = $"{BuildInfo.CommitSha} · {BuildInfo.BuildDate}";

        // Initialize auto-update checker
        _updateChecker = new UpdateChecker(_config.RepoPath);
        _updateChecker.UpdateDetected += (s, e) => Dispatcher.UIThread.Post(() =>
        {
            _updateAvailable = true;
            UpdateUpdateAffordances();
        });
        _updateChecker.Start();

        // Bounded memory no matter what leaks: relaunch past the configured limit.
        _memoryWatchdog = new MemoryWatchdog(_config.MemoryLimitMB);
        _memoryWatchdog.LimitExceeded += _ => Dispatcher.UIThread.Post(RestartForMemory);
        _memoryWatchdog.Start();

        SetViewMode(PopupViewMode.Compact);

        // Position near taskbar on first show only
        Opened += (s, e) =>
        {
            MakeWindowNonActivating();
            PinToTaskbarCorner();
            Dispatcher.UIThread.Post(ReanchorIfPinned, DispatcherPriority.Loaded);
        };

        // Re-anchor whenever the window resizes (e.g., async AI data revealing
        // sections, view-mode toggles), so the bottom edge never overlaps the taskbar.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                ReanchorIfPinned();
        };
    }

    private void PinToTaskbarCorner()
    {
        var screen = OperatingSystem.IsMacOS()
            && MacWindowPresenter.TryGetPointerPosition(out var pointer)
                ? Screens.ScreenFromPoint(pointer) ?? Screens.Primary
                : Screens.Primary;
        if (screen is null) return;

        var workArea = screen.WorkingArea;
        _anchorScaling = screen.Scaling;
        var margin = (int)(4 * _anchorScaling);
        _anchorBottomRight = new PixelPoint(
            workArea.X + workArea.Width - margin,
            workArea.Y + workArea.Height - margin);
        ReanchorIfPinned();
    }

    private void ReanchorIfPinned()
    {
        if (_anchorBottomRight is not PixelPoint anchor) return;
        var pixelW = (int)(Bounds.Width * _anchorScaling);
        var pixelH = (int)(Bounds.Height * _anchorScaling);
        Position = new PixelPoint(anchor.X - pixelW, anchor.Y - pixelH);
    }

    private void InitializeAiServices()
    {
        _usageApiService = new UsageApiService(_config.UsageApiUrl);
        if (!string.IsNullOrWhiteSpace(_config.UsageApiUrl))
            _gateService = new GateService(_config.UsageApiUrl);
        // Sections start hidden; the first successful refresh reveals whichever providers
        // returned data. NoKeysHint also flips off after the first successful response.
        NoKeysHint.IsVisible = false;
        ConfigPathText.Text = $"Source: {_config.UsageApiUrl}";
    }

    private void OpenUsageDashboard()
    {
        OpenUrl(string.IsNullOrWhiteSpace(_config.UsageApiUrl)
            ? "https://usage.etdofresh.com"
            : _config.UsageApiUrl.TrimEnd('/'));
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"Failed to open {url}: {ex.Message}");
        }
    }

    // A provider's name is the click target for that provider's own usage page.
    // Some labels already carry an explanatory tip, so the URL is appended to it
    // rather than replacing it.
    private static Cursor? _handCursor;

    private static void MakeLink(TextBlock label, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        // One shared native cursor instead of a new one per link (links are rebuilt with the cards).
        label.Cursor = _handCursor ??= new Cursor(StandardCursorType.Hand);
        var existing = ToolTip.GetTip(label) as string;
        ToolTip.SetTip(label, string.IsNullOrEmpty(existing) ? url : $"{existing}\n{url}");
        label.PointerPressed += (_, _) => OpenUrl(url);
    }

    // Every compact bar row is a Grid whose first child is its provider label, so
    // the rows become links without needing an x:Name on each one. The docked plan
    // label is skipped because it matches neither case.
    private void WireCompactRowLinks(Panel section, string? url)
    {
        foreach (var child in section.Children)
        {
            switch (child)
            {
                case StackPanel stack:
                    WireCompactRowLinks(stack, url);
                    break;
                case Grid row when row.Children.Count > 0 && row.Children[0] is TextBlock label:
                    MakeLink(label, url);
                    break;
            }
        }
    }

    private void WireProviderLinks()
    {
        MakeLink(CodexTitleText, _config.CodexUsageUrl);
        MakeLink(Codex2TitleText, _config.CodexUsageUrl);
        MakeLink(ClaudeTitleText, _config.ClaudeUsageUrl);
        MakeLink(ClaudeCode2TitleText, _config.ClaudeUsageUrl);
        MakeLink(ZaiTitleText, _config.ZaiUsageUrl);
        MakeLink(OpenRouterTitleText, _config.OpenRouterUsageUrl);
        MakeLink(JevTitleText, _config.JevUsageUrl);
        MakeLink(OpenAiTitleText, _config.OpenAiUsageUrl);

        WireCompactRowLinks(CodexCompactSection, _config.CodexUsageUrl);
        WireCompactRowLinks(Codex2CompactSection, _config.CodexUsageUrl);
        WireCompactRowLinks(ClaudeCompactSection, _config.ClaudeUsageUrl);
        WireCompactRowLinks(Claude2CompactSection, _config.ClaudeUsageUrl);
        WireCompactRowLinks(ZaiCompactSection, _config.ZaiUsageUrl);
        MakeLink(OpenRouterCompactLabel, _config.OpenRouterUsageUrl);
        MakeLink(JevCompactLabel, _config.JevUsageUrl);
        MakeLink(OpenAiCompactLabel, _config.OpenAiUsageUrl);
    }

    private void RegisterTargetBar(Grid container, Border fill, Border tick, Color baseColor)
    {
        var state = new TargetBarState
        {
            Container = container,
            Fill = fill,
            Tick = tick,
            BaseColor = baseColor,
        };
        _targetBars.Add(state);
        container.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                RenderTargetBar(state);
        };
    }

    private void SetTargetBar(Grid container, double used, double? expected)
    {
        var state = _targetBars.FirstOrDefault(s => ReferenceEquals(s.Container, container));
        if (state == null) return;
        state.Used = used;
        state.Expected = expected;
        RenderTargetBar(state);
    }

    private void SetTargetBarsExhausted(bool exhausted, params Grid[] containers)
    {
        foreach (var s in _targetBars.Where(s => containers.Contains(s.Container)))
        {
            s.Exhausted = exhausted;
            RenderTargetBar(s);
            // Full view: the bar's label shares its Grid row in column 0.
            if (s.Container.Parent is Grid g)
                foreach (var label in g.Children.OfType<TextBlock>().Where(t =>
                             Grid.GetRow(t) == Grid.GetRow(s.Container) && Grid.GetColumn(t) == 0 && Grid.GetColumnSpan(t) == 1))
                    SetTextExhausted(label, exhausted);
        }
    }

    // Greys a TextBlock (and any colored runs) while exhausted; restores its XAML color after.
    private void SetTextExhausted(TextBlock tb, bool exhausted)
    {
        if (!_textForegrounds.TryGetValue(tb, out var original))
            _textForegrounds[tb] = original = tb.Foreground;
        tb.Foreground = exhausted ? new SolidColorBrush(ExhaustedColor) : original;
        if (exhausted && tb.Inlines != null)
            foreach (var inline in tb.Inlines.Where(i => !KeepsOwnColor(i)))
                inline.ClearValue(TextElement.ForegroundProperty);
    }

    // Compact rows: bar fill sits in a bar Grid inside the row Grid holding label, %, and reset text.
    private void SetCompactRowExhausted(Border compactBar, bool exhausted)
    {
        if (compactBar.Parent?.Parent is not Grid row) return;
        foreach (var tb in row.Children.OfType<TextBlock>())
            SetTextExhausted(tb, exhausted);
    }

    private static void RenderTargetBar(TargetBarState s)
    {
        var w = s.Container.Bounds.Width;
        if (w <= 0) return;

        var used = Math.Clamp(s.Used, 0, 100);
        s.Fill.Width = used / 100.0 * w;
        s.Fill.Background = new SolidColorBrush(s.Exhausted ? ExhaustedColor : BlendOverColor(s.BaseColor, s.Expected, used));

        if (s.Expected is double exp && exp > 0 && exp < 100)
        {
            var x = exp / 100.0 * w;
            s.Tick.Margin = new Thickness(Math.Max(0, x - 1), 0, 0, 0);
            s.Tick.IsVisible = true;
        }
        else
        {
            s.Tick.IsVisible = false;
        }
    }

    private static void RenderCompactBar(Border fill, Border tick, Color baseColor, double used, double? expected, double width, bool exhausted)
    {
        var u = Math.Clamp(used, 0, 100);
        fill.Width = u / 100.0 * width;
        fill.Background = new SolidColorBrush(exhausted ? ExhaustedColor : BlendOverColor(baseColor, expected, u));
        if (expected is double exp && exp > 0 && exp < 100)
        {
            var x = exp / 100.0 * width;
            tick.Margin = new Thickness(Math.Max(0, x - 1), 0, 0, 0);
            tick.IsVisible = true;
        }
        else
        {
            tick.IsVisible = false;
        }
    }

    // Blend the base color toward yellow then red as used grows past the target.
    private static Color BlendOverColor(Color baseColor, double? expected, double used)
    {
        if (expected is not double exp || used <= exp) return baseColor;
        var headroom = Math.Max(1.0, 100.0 - exp);
        var t = Math.Clamp((used - exp) / headroom, 0, 1);
        var yellow = Color.FromRgb(0xFF, 0xEB, 0x3B);
        var red = Color.FromRgb(0xF4, 0x43, 0x36);
        return t < 0.5 ? Lerp(baseColor, yellow, t * 2) : Lerp(yellow, red, (t - 0.5) * 2);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    public void ShowPopup()
    {
        SetViewMode(PopupViewMode.Compact, anchorBottomRight: false);
        PinToTaskbarCorner();
        RememberFrontmostApplication();
        Show();
        MacWindowPresenter.BringToFront(this, relocate: true);
        DispatcherTimer.RunOnce(
            () => MacWindowPresenter.BringToFront(this),
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.Loaded);
        RefreshSystem();
        _systemRefreshTimer.Start();

        // Fetch AI credits immediately, then on timer
        _ = RefreshAiCreditsAsync();
        _aiRefreshTimer.Start();
    }

    public void HidePopup()
    {
        _systemRefreshTimer.Stop();
        _aiRefreshTimer.Stop();
        Hide();
    }

    // Idempotent pre-shutdown teardown. Called from the quit path, from the
    // lifetime's ShutdownRequested hook (Cmd+Q / logout / OS shutdown), or both.
    public void PrepareShutdown()
    {
        _allowClose = true;
        _systemRefreshTimer.Stop();
        _aiRefreshTimer.Stop();
        _usageApiService?.Dispose();
        _gateService?.Dispose();
        _updateChecker?.Dispose();
        _memoryWatchdog?.Dispose();
    }

    // A fresh process starts back at a normal footprint. Keep a hidden popup hidden.
    private void RestartForMemory()
    {
        if (_shutdownStarted) return;
        if (UpdateChecker.RestartApp(quiet: !IsVisible))
            ForceClose();
    }

    public void ForceClose()
    {
        // A double-activated Quit posts this twice; the second pass must not call
        // desktop.Shutdown() again (it would re-raise Exit and re-dispose the tray).
        if (_shutdownStarted) return;
        _shutdownStarted = true;

        PrepareShutdown();
        Close();
        // ShutdownMode is OnExplicitShutdown, so closing the last window is not
        // enough — end the lifetime here so both Quit and the update-restart
        // path fully exit the process instead of lingering with a live tray icon.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private async Task ApplyUpdateAsync()
    {
        if (_updateChecker == null || !_updateChecker.Enabled || _updateInProgress) return;
        _updateInProgress = true;
        UpdateButton.IsEnabled = false;
        UpdateButtonCompact.IsEnabled = false;
        ShowUpdateModal();
        try
        {
            var progress = new Progress<string>(status => UpdateModalStatus.Text = status);
            var success = await _updateChecker.ApplyUpdateAsync(progress);
            if (!success)
            {
                ShowUpdateFailure(_updateChecker.LastApplyError ?? "Update could not be completed.");
                return;
            }

            UpdateModalStatus.Text = "Restarting...";
            if (!_updateChecker.RestartScheduled && !UpdateChecker.RestartApp())
            {
                ShowUpdateFailure("Update installed, but automatic restart failed.");
                return;
            }
            ForceClose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Update apply failed: {ex.Message}");
            ShowUpdateFailure(ex.Message);
        }
        finally
        {
            // A successful update closes this window. If it failed, make the
            // visible update affordance available again for a later retry.
            UpdateButton.IsEnabled = true;
            UpdateButtonCompact.IsEnabled = true;
        }
    }

    private void ShowUpdateModal()
    {
        UpdateModalTitle.Text = "Updating Usage Monitor";
        UpdateModalStatus.Text = "Preparing update...";
        UpdateModalProgress.IsVisible = true;
        UpdateModalCloseButton.IsVisible = false;
        UpdateModalOverlay.IsVisible = true;
    }

    private void ShowUpdateFailure(string message)
    {
        UpdateModalTitle.Text = "Update failed";
        UpdateModalStatus.Text = message;
        UpdateModalProgress.IsVisible = false;
        UpdateModalCloseButton.IsVisible = true;
    }

    private void CloseUpdateModal()
    {
        UpdateModalOverlay.IsVisible = false;
        _updateInProgress = false;
    }

    // Shared by the automatic timer and the tray-menu command. The caller uses
    // the result for feedback; UpdateDetected still reveals the normal update
    // buttons whenever a newer build is found.
    public Task<UpdateChecker.CheckResult> CheckForUpdatesAsync() =>
        _updateChecker?.CheckAsync() ?? Task.FromResult(UpdateChecker.CheckResult.Disabled);

    private void PositionNearTaskbar(double? targetWidth = null, double? targetHeight = null)
    {
        if (Screens.Primary is { } screen)
        {
            var workArea = screen.WorkingArea;
            var scaling = screen.Scaling;
            var pixelWidth = (int)((targetWidth ?? Width) * scaling);
            var pixelHeight = (int)((targetHeight ?? Height) * scaling);
            var margin = (int)(4 * scaling);
            Position = new PixelPoint(
                workArea.X + workArea.Width - pixelWidth - margin,
                workArea.Y + workArea.Height - pixelHeight - margin
            );
        }
    }

    #region System Stats

    private void SystemRefreshTimer_Tick(object? sender, EventArgs e)
    {
        RefreshSystem();
        RefreshAvailabilityBadges(DateTimeOffset.Now);
    }

    private void RefreshSystem()
    {
        try
        {
            RememberFrontmostApplication();
            UpdateCpu();
            UpdateMemory();
            UpdateDisk();
            UpdateNetwork();
            UpdateUptime();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"System refresh error: {ex.Message}");
        }
    }

    private void UpdateCpu()
    {
        double cpuPercent = 0;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _cpuCounter != null)
        {
            try
            {
                cpuPercent = _cpuCounter.NextValue();
            }
            catch { }
        }
        else if (TryReadCpuTimes(out var total, out var idle))
        {
            if (_hasCpuSample && total > _lastCpuTotal)
            {
                var totalDelta = total - _lastCpuTotal;
                var idleDelta = idle >= _lastCpuIdle ? idle - _lastCpuIdle : 0;
                cpuPercent = Math.Clamp((1.0 - idleDelta / (double)totalDelta) * 100.0, 0, 100);
            }
            _lastCpuTotal = total;
            _lastCpuIdle = idle;
            _hasCpuSample = true;
        }

        _cpuPercent = Math.Clamp(cpuPercent, 0, 100);
        SetPlainBar(CpuBar, CpuBarFill, _cpuPercent);
        CpuPercentText.Text = $"{cpuPercent:F0}%";
        CpuDetailsText.Text = $"{Environment.ProcessorCount} logical cores";
        UpdateCompactSystemBars();
    }

    private void UpdateMemory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var status = new MEMORYSTATUSEX();
                status.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref status))
                {
                    double totalGB = status.ullTotalPhys / (1024.0 * 1024 * 1024);
                    double usedGB = (status.ullTotalPhys - status.ullAvailPhys) / (1024.0 * 1024 * 1024);
                    double percent = (usedGB / totalGB) * 100;

                    SetMemoryDisplay(usedGB, totalGB, percent);
                }
            }
            catch { }
        }
        else
        {
            if (TryReadUnixMemory(out var usedBytes, out var totalBytes) && totalBytes > 0)
            {
                var divisor = 1024.0 * 1024 * 1024;
                SetMemoryDisplay(usedBytes / divisor, totalBytes / divisor, usedBytes / (double)totalBytes * 100);
            }
        }
    }

    private void UpdateDisk()
    {
        try
        {
            foreach (var display in _driveDisplays)
            {
                var visible = _config.IsDriveVisible(display.Key);
                display.FullRow.IsVisible = visible;
                display.CompactRow.IsVisible = visible;
                if (!visible || !display.Drive.IsReady || display.Drive.TotalSize <= 0)
                    continue;

                var percent = Math.Clamp(
                    (display.Drive.TotalSize - display.Drive.AvailableFreeSpace) /
                    (double)display.Drive.TotalSize * 100.0, 0, 100);
                SetPlainBar(display.FullBar, display.FullFill, percent);
                display.CompactFill.Width = percent / 100.0 * 62.0;
                display.CompactPercent.Text = $"{percent:F0}%";
                var usedBytes = display.Drive.TotalSize - display.Drive.AvailableFreeSpace;
                display.FullUsage.Text = FormatDriveUsage(usedBytes, display.Drive.TotalSize, compact: false);
                display.CompactUsage.Text = FormatDriveUsage(usedBytes, display.Drive.TotalSize, compact: true);
            }
            DriveSection.IsVisible = _driveDisplays.Any(d => d.FullRow.IsVisible);
        }
        catch { }
    }

    private void SetMemoryDisplay(double usedGB, double totalGB, double percent)
    {
        _memoryPercent = Math.Clamp(percent, 0, 100);
        _memoryUsedGB = usedGB;
        _memoryTotalGB = totalGB;
        SetPlainBar(MemoryBar, MemoryBarFill, _memoryPercent);
        MemoryPercentText.Text = $"{_memoryPercent:F0}%";
        MemoryText.Text = $"{usedGB:F1} / {totalGB:F1} GB";
        UpdateCompactSystemBars();
    }

    private static void SetPlainBar(Grid container, Border fill, double percent)
    {
        var width = container.Bounds.Width;
        if (width > 0)
            fill.Width = Math.Clamp(percent, 0, 100) / 100.0 * width;
    }

    private void UpdateCompactSystemBars()
    {
        const double barWidth = 62.0;
        CpuCompactBar.Width = _cpuPercent / 100.0 * barWidth;
        MemoryCompactBar.Width = _memoryPercent / 100.0 * barWidth;
        CpuCompactText.Text = $"{_cpuPercent:F0}%";
        MemoryCompactText.Text = $"{_memoryPercent:F0}%";
        CpuCompactDetailsText.Text = $"{Environment.ProcessorCount}c";
        MemoryCompactDetailsText.Text = _memoryTotalGB > 0
            ? $"{_memoryUsedGB:F0}/{_memoryTotalGB:F0}G"
            : "—";
    }

    private void InitializeDriveDisplays()
    {
        var drives = DriveInfo.GetDrives()
            .Where(IsUserDrive)
            .OrderBy(d => d.Name == Path.DirectorySeparatorChar.ToString() ? 0 : 1)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DriveToggles = drives.Select(d => new DriveToggle(d.Name, GetDriveLabel(d))).ToArray();
        foreach (var drive in drives)
        {
            var label = GetDriveLabel(drive);
            var fullFill = NewBarFill();
            var fullBar = NewBarGrid(fullFill, stretch: true);
            var fullRow = new Grid { ColumnDefinitions = new ColumnDefinitions("84,*,106"), ClipToBounds = true };
            fullRow.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 8,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(fullBar, 1);
            fullRow.Children.Add(fullBar);
            var fullUsage = NewUsageText();
            Grid.SetColumn(fullUsage, 2);
            fullRow.Children.Add(fullUsage);

            var compactFill = NewBarFill();
            var compactBar = NewBarGrid(compactFill, stretch: false);
            var compactRow = new Grid { ColumnDefinitions = new ColumnDefinitions("58,68,42,*"), ClipToBounds = true };
            compactRow.Children.Add(new TextBlock
            {
                Text = CompactDriveLabel(label),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D)),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(compactBar, 1);
            compactRow.Children.Add(compactBar);
            var compactPercent = NewPercentText();
            Grid.SetColumn(compactPercent, 2);
            compactRow.Children.Add(compactPercent);
            var compactUsage = NewUsageText(fontSize: 7);
            Grid.SetColumn(compactUsage, 3);
            compactRow.Children.Add(compactUsage);

            FullDriveBarsHost.Children.Add(fullRow);
            CompactDriveBarsHost.Children.Add(compactRow);
            _driveDisplays.Add(new DriveDisplay
            {
                Drive = drive,
                Key = drive.Name,
                Label = label,
                FullRow = fullRow,
                FullBar = fullBar,
                FullFill = fullFill,
                FullUsage = fullUsage,
                CompactRow = compactRow,
                CompactFill = compactFill,
                CompactPercent = compactPercent,
                CompactUsage = compactUsage,
            });
        }
    }

    private static bool IsUserDrive(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady || drive.TotalSize <= 0)
                return false;
            if (OperatingSystem.IsWindows())
                return drive.DriveType is DriveType.Fixed or DriveType.Removable;
            if (OperatingSystem.IsMacOS())
                return drive.Name == "/" || drive.Name.StartsWith("/Volumes/", StringComparison.Ordinal);
            return drive.DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch { return false; }
    }

    private static string GetDriveLabel(DriveInfo drive)
    {
        if (drive.Name == "/") return "Macintosh HD";
        var trimmed = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? drive.Name : name;
    }

    private static string CompactDriveLabel(string label) => label.Length <= 8 ? label : label[..8];

    private static Border NewBarFill() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D)),
        CornerRadius = new CornerRadius(2),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        Width = 0,
    };

    private static Grid NewBarGrid(Border fill, bool stretch)
    {
        var grid = new Grid
        {
            Width = stretch ? double.NaN : 62,
            Height = 4,
            Margin = new Thickness(stretch ? 4 : 2, 0, 4, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = stretch
                ? Avalonia.Layout.HorizontalAlignment.Stretch
                : Avalonia.Layout.HorizontalAlignment.Left,
            ClipToBounds = true,
        };
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), CornerRadius = new CornerRadius(2) });
        grid.Children.Add(fill);
        return grid;
    }

    private static TextBlock NewPercentText() => new()
    {
        Text = "—",
        FontSize = 8,
        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xDA, 0xA6)),
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    private static TextBlock NewUsageText(double fontSize = 8) => new()
    {
        Text = "—",
        FontSize = fontSize,
        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xDA, 0xA6)),
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static string FormatDriveUsage(long usedBytes, long totalBytes, bool compact)
    {
        const double gibibyte = 1024d * 1024 * 1024;
        const double tebibyte = 1024d * 1024 * 1024 * 1024;
        if (totalBytes >= tebibyte)
            return compact
                ? $"{usedBytes / tebibyte:F1}/{totalBytes / tebibyte:F1}T"
                : $"{usedBytes / tebibyte:F1} / {totalBytes / tebibyte:F1} TB";

        return compact
            ? $"{usedBytes / gibibyte:F0}/{totalBytes / gibibyte:F0}G"
            : $"{usedBytes / gibibyte:F0} / {totalBytes / gibibyte:F0} GB";
    }

    public bool IsDriveVisible(string driveKey) => _config.IsDriveVisible(driveKey);

    public void SetDriveVisible(string driveKey, bool visible)
    {
        _config.SetDriveVisible(driveKey, visible);
        UpdateDisk();
        UpdateCompactSummary();
        InvalidateMeasure();
    }

    private static bool TryReadCpuTimes(out ulong total, out ulong idle)
    {
        total = idle = 0;
        if (OperatingSystem.IsMacOS())
        {
            var ticks = new uint[4];
            var count = (uint)ticks.Length;
            if (host_statistics(HostPort, HostCpuLoadInfo, ticks, ref count) != 0 || count < 4)
                return false;
            total = ticks.Aggregate<uint, ulong>(0, (sum, value) => sum + value);
            idle = ticks[CpuStateIdle];
            return total > 0;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var values = fields.Skip(1).Select(ulong.Parse).ToArray();
                total = values.Aggregate<ulong, ulong>(0, (sum, value) => sum + value);
                idle = values.ElementAtOrDefault(3) + values.ElementAtOrDefault(4);
                return total > 0;
            }
            catch { }
        }
        return false;
    }

    private static bool TryReadUnixMemory(out ulong usedBytes, out ulong totalBytes)
    {
        usedBytes = totalBytes = 0;
        if (OperatingSystem.IsMacOS())
        {
            var stats = new uint[64];
            var count = (uint)stats.Length;
            if (host_statistics64(HostPort, HostVmInfo64, stats, ref count) != 0 || count < 8)
                return false;
            var pageSize = (ulong)Environment.SystemPageSize;
            // Inactive pages are reclaimable cache, so count them as available.
            var freeBytes = (stats[0] + stats[2]) * pageSize;
            var total = (ulong)GetMacPhysicalMemory();
            if (total <= freeBytes) return false;
            totalBytes = total;
            usedBytes = total - freeBytes;
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var values = File.ReadLines("/proc/meminfo")
                    .Select(line => line.Split(':', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0], parts => ulong.Parse(parts[1].Trim().Split(' ')[0]) * 1024);
                totalBytes = values["MemTotal"];
                var available = values.TryGetValue("MemAvailable", out var value) ? value : values.GetValueOrDefault("MemFree");
                usedBytes = totalBytes - available;
                return totalBytes > 0;
            }
            catch { }
        }
        return false;
    }

    private static long GetMacPhysicalMemory()
    {
        nuint length = sizeof(long);
        return sysctlbyname("hw.memsize", out var memory, ref length, IntPtr.Zero, 0) == 0 ? memory : 0;
    }

    private const int HostCpuLoadInfo = 3;
    private const int HostVmInfo64 = 4;
    private const int CpuStateIdle = 2;

    // mach_host_self() hands out a new send-right reference on every call; take one and reuse it
    // rather than piling up references on the host port every 2 s.
    private static uint? _hostPort;
    private static uint HostPort => _hostPort ??= mach_host_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern uint mach_host_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int host_statistics(uint host, int flavor, [Out] uint[] info, ref uint count);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int host_statistics64(uint host, int flavor, [Out] uint[] info, ref uint count);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int sysctlbyname(string name, out long oldValue, ref nuint oldLength, IntPtr newValue, nuint newLength);

    private void UpdateNetwork()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            long totalSent = 0;
            long totalReceived = 0;

            foreach (var ni in interfaces)
            {
                var stats = ni.GetIPv4Statistics();
                totalSent += stats.BytesSent;
                totalReceived += stats.BytesReceived;
            }

            var now = DateTime.Now;
            if (_lastNetworkCheck > DateTime.MinValue)
            {
                var elapsed = (now - _lastNetworkCheck).TotalSeconds;
                if (elapsed > 0)
                {
                    double upSpeed = (totalSent - _lastBytesSent) / elapsed;
                    double downSpeed = (totalReceived - _lastBytesReceived) / elapsed;

                    NetworkUpText.Text = $"↑ {FormatSpeed(upSpeed)}";
                    NetworkDownText.Text = $"↓ {FormatSpeed(downSpeed)}";
                }
            }

            _lastBytesSent = totalSent;
            _lastBytesReceived = totalReceived;
            _lastNetworkCheck = now;
        }
        catch { }
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
            return $"{bytesPerSecond / (1024 * 1024):F1} MB/s";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:F1} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    private void UpdateUptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeText.Text = $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";
    }

    #endregion

    #region AI Credits

    private async void AiRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshAiCreditsAsync();
    }

    private async Task RefreshAiCreditsAsync()
    {
        if (_usageApiService == null) return;
        var gateTask = _gateService?.GetRecentAsync() ?? Task.FromResult<IReadOnlyList<GateRequest>?>(null);
        var status = await _usageApiService.GetStatusAsync();
        var gate = await gateTask;
        if (gate != null)
            Dispatcher.UIThread.Post(() => ApplyGate(gate));
        if (status == null) return;
        _lastStatus = status;

        Dispatcher.UIThread.Post(() =>
        {
            var tier = status.Staleness switch { "stale" => " (stale)", "little_stale" => " (a little stale)", _ => "" };
            var staleNames = status.StaleProviders is { Count: > 0 } keys
                ? " · Stale: " + string.Join(", ", keys.Select(ProviderLabel)) : "";
            ConfigPathText.Text = $"Source: {_config.UsageApiUrl} · Updated {FormatFetchedAt(status.OldestFetchedAt)}{tier}{staleNames}";
            ApplyOpenRouter(status.OpenRouter);
            ApplyOpenAi(status.OpenAi);
            ApplyJev(status.Jev);
            ApplyCodex(status.Codex);
            ApplyCodex2(status.Codex2);
            ApplyClaude(status.Claude);
            ApplyClaude2(status.Claude2);
            ApplyZai(status.Zai);
            ApplyAdditionalAccounts(status.AdditionalAccounts);
            ReflowAiGrid();
            UpdateCompactSummary();
        });
    }

    // Header links: "CPA" (orange) opens the CLI Proxy API itself and "CPA M" (blue) its
    // manager. Each only makes sense when its URL is configured; otherwise that link's
    // full and compact affordances stay hidden rather than opening a dead URL.
    private readonly record struct HeaderLink(string Text, string Tooltip, string? Url, Color Color)
    {
        public bool IsVisible => !string.IsNullOrWhiteSpace(Url);
    }

    private static HeaderLink ProxyHeaderLink(Config config) =>
        new("CPA", "Open CLI Proxy API", config.CliProxyApiUrl, ClaudeAccentColor);

    private static HeaderLink ManagerHeaderLink(Config config) =>
        new("CPA M", "Open CPA Manager Plus", config.NineRouterPanelUrl, CodexAccentColor);

    private void WireRouterPanelLinks()
    {
        WireHeaderLink(ProxyHeaderLink(_config), ProxyButton, ProxyButtonText, ProxyLinkCompact);
        WireHeaderLink(ManagerHeaderLink(_config), RouterButton, RouterButtonText, RouterLinkCompact);
    }

    private static void WireHeaderLink(HeaderLink link, Button button, TextBlock buttonText, TextBlock compact)
    {
        button.IsVisible = link.IsVisible;
        compact.IsVisible = link.IsVisible;
        buttonText.Text = compact.Text = link.Text;
        buttonText.Foreground = compact.Foreground = new SolidColorBrush(link.Color);
        if (!link.IsVisible) return;

        var url = link.Url!;
        var tip = $"{link.Tooltip}\n{url}";
        ToolTip.SetTip(button, tip);
        ToolTip.SetTip(compact, tip);
        button.Click += (_, _) => OpenUrl(url);
        compact.PointerPressed += (_, _) => OpenUrl(url);
    }

    public bool IsProviderVisible(ProviderToggle toggle) => toggle switch
    {
        ProviderToggle.OpenAi => _config.ShowOpenAi,
        ProviderToggle.OpenRouter => _config.ShowOpenRouter,
        ProviderToggle.Jev => _config.ShowJev,
        ProviderToggle.Gate => _config.ShowGate,
        ProviderToggle.Codex => _config.ShowCodex,
        ProviderToggle.Codex2 => _config.ShowCodex2,
        ProviderToggle.CodexSpark => _config.ShowCodexSpark,
        ProviderToggle.Claude => _config.ShowClaude,
        ProviderToggle.Claude2 => _config.ShowClaude2,
        ProviderToggle.ClaudeDesign => _config.ShowClaudeDesign,
        ProviderToggle.ClaudeFable => _config.ShowClaudeFable,
        ProviderToggle.Claude2Design => _config.ShowClaude2Design,
        ProviderToggle.Zai => _config.ShowZai,
        ProviderToggle.ZaiRequests => _config.ShowZaiRequests,
        _ => true,
    };

    public void SetProviderVisible(ProviderToggle toggle, bool visible)
    {
        switch (toggle)
        {
            case ProviderToggle.OpenAi: _config.ShowOpenAi = visible; break;
            case ProviderToggle.OpenRouter: _config.ShowOpenRouter = visible; break;
            case ProviderToggle.Jev: _config.ShowJev = visible; break;
            case ProviderToggle.Gate: _config.ShowGate = visible; break;
            case ProviderToggle.Codex: _config.ShowCodex = visible; break;
            case ProviderToggle.Codex2: _config.ShowCodex2 = visible; break;
            case ProviderToggle.CodexSpark: _config.ShowCodexSpark = visible; break;
            case ProviderToggle.Claude: _config.ShowClaude = visible; break;
            case ProviderToggle.Claude2: _config.ShowClaude2 = visible; break;
            case ProviderToggle.ClaudeDesign: _config.ShowClaudeDesign = visible; break;
            case ProviderToggle.ClaudeFable: _config.ShowClaudeFable = visible; break;
            case ProviderToggle.Claude2Design: _config.ShowClaude2Design = visible; break;
            case ProviderToggle.Zai: _config.ShowZai = visible; break;
            case ProviderToggle.ZaiRequests: _config.ShowZaiRequests = visible; break;
        }
        _config.Save();
        ReapplyProviderVisibility();
    }

    // Re-render section/bar visibility against the last snapshot so a menu toggle takes
    // effect immediately, even between polls. Safe to call from any thread.
    private void ReapplyProviderVisibility()
    {
        void Apply()
        {
            ApplyOpenRouter(_lastStatus?.OpenRouter);
            ApplyOpenAi(_lastStatus?.OpenAi);
            ApplyJev(_lastStatus?.Jev);
            ApplyGate(_lastGate);
            ApplyCodex(_lastStatus?.Codex);
            ApplyCodex2(_lastStatus?.Codex2);
            ApplyClaude(_lastStatus?.Claude);
            ApplyClaude2(_lastStatus?.Claude2);
            ApplyZai(_lastStatus?.Zai);
            ApplyAdditionalAccounts(_lastStatus?.AdditionalAccounts);
            ReflowAiGrid();
            UpdateCompactSummary();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    // The full-view AI cards live in a fixed 2-column grid. Repack the *visible* cards
    // top-left (and recompute their gutter margins) so hiding any subset never leaves an
    // interior hole. With all cards visible this reproduces the original cell layout.
    private void ReflowAiGrid()
    {
        // Account cards grouped by provider and sorted by priority, as in the compact list; Z.ai after Claude.
        var accountCards = SortAccounts(new (Control Card, AccountSortKey Key)[]
        {
            (CodexSection, FixedKey("codex", 0)), (Codex2Section, FixedKey("codex2", 1)),
            (ClaudeCodeSection, FixedKey("claude", 2)), (ClaudeCode2Section, FixedKey("claude2", 3)),
        }, c => c.Key).Select(c => c.Card);
        var sections = new Control[] { OpenRouterSection, OpenAiSection, JevSection }
            .Concat(accountCards).Append(ZaiSection);

        var slot = 0;
        foreach (var section in sections)
        {
            if (!section.IsVisible) continue;
            var row = slot / 2;
            var col = slot % 2;
            Grid.SetRow(section, row);
            Grid.SetColumn(section, col);
            section.Margin = new Thickness(
                col == 1 ? 3 : 0,   // left gutter for the right column
                row == 0 ? 0 : 3,   // top gap below the first row
                col == 0 ? 3 : 0,   // right gutter for the left column
                0);
            slot++;
        }
    }

    private void ApplyOpenRouter(OpenRouterBlock? r)
    {
        var show = r != null && _config.ShowOpenRouter;
        OpenRouterSection.IsVisible = show;
        if (!show || r == null) return;
        var remaining = r.LimitRemaining ?? (r.Limit.HasValue ? Math.Max(0, r.Limit.Value - r.Usage) : (double?)null);
        OpenRouterCreditsText.Text = remaining.HasValue
            ? $"${r.Usage:F2} used | ${remaining.Value:F2} left"
            : $"${r.Usage:F2} used";
        OpenRouterCompactText.Text = remaining.HasValue ? $"${remaining.Value:F2} left" : "—";
        OpenRouterRangeText.Text = r.IsFreeTier == true ? "Free tier" : "";
    }

    private void ApplyOpenAi(OpenAiBlock? o)
    {
        var show = o != null && _config.ShowOpenAi;
        OpenAiSection.IsVisible = show;
        if (!show || o == null) return;
        OpenAiCreditsText.Text = $"${o.SpendMonth:F2} this month";
    }

    private static string FormatSpend(double value) =>
        $"${value.ToString(value < 0.01 ? "F4" : value < 1 ? "F3" : "F2")}";

    // Shade ladders by tier: Anthropic oranges, OpenAI blues, brightest = strongest model.
    private static readonly Dictionary<string, Color> GateColors = new()
    {
        ["fable"] = Color.FromRgb(0xFF, 0xB7, 0x4D),
        ["opus"] = Color.FromRgb(0xFF, 0x8A, 0x3D),
        ["sonnet"] = Color.FromRgb(0xD8, 0x5F, 0x1A),
        ["haiku"] = Color.FromRgb(0x9A, 0x40, 0x10),
        ["astra"] = Color.FromRgb(0x90, 0xCA, 0xF9),
        ["sol"] = Color.FromRgb(0x42, 0x8F, 0xF0),
        ["terra"] = Color.FromRgb(0x1E, 0x5F, 0xC0),
        ["luna"] = Color.FromRgb(0x12, 0x3C, 0x80),
        ["glm"] = Color.FromRgb(0xBA, 0x68, 0xC8),
    };
    private static readonly Color GateOtherColor = Color.FromRgb(0x75, 0x75, 0x75);
    private static readonly Color GateErrorColor = Color.FromRgb(0xF4, 0x43, 0x36);
    private static readonly Color GateCcColor = Color.FromRgb(0xFF, 0x8A, 0x3D);
    private static readonly Color GateCxColor = Color.FromRgb(0x42, 0x8F, 0xF0);

    private static Color GateColor(string family) => GateColors.GetValueOrDefault(family, GateOtherColor);

    private static bool IsAnthropic(string family) => family is "fable" or "opus" or "sonnet" or "haiku";
    private static bool IsOpenAi(string family) => family is "astra" or "sol" or "terra" or "luna";

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // Last 20 9gate requests: a strip of ticks (oldest left, newest right; failures
    // get a red underline), then CC (Anthropic) / CX (OpenAI) counts.
    private void ApplyGate(IReadOnlyList<GateRequest>? reqs)
    {
        if (reqs != null) _lastGate = reqs;
        reqs = _lastGate;
        var show = reqs is { Count: > 0 } && _config.ShowGate;
        GateSection.IsVisible = show;
        GateCompactRow.IsVisible = show;
        if (!show) { _renderedGate = null; return; }
        // The strip is 2 x 20 freshly built squares with tooltips; skip the rebuild when nothing changed.
        if (_renderedGate != null && _renderedGate.SequenceEqual(reqs!)) return;
        _renderedGate = reqs;

        var last = reqs!.Take(GateSlots).Reverse().ToList();
        FillGateStrip(GateStrip, last, 1);
        FillGateStrip(GateCompactStrip, last, 1);

        var done = last.Where(r => !r.InProgress).ToList();
        last = done;
        var cc = last.Count(r => IsAnthropic(r.Family));
        var cx = last.Count(r => IsOpenAi(r.Family));
        var other = last.Count - cc - cx;
        var fails = last.Count(r => r.Outcome is not null and not "ok");
        foreach (var tb in new[] { GateTallyText, GateCompactTally })
        {
            tb.Inlines!.Clear();
            tb.Inlines.Add(new Run($"CC {cc}") { Foreground = new SolidColorBrush(GateCcColor) });
            tb.Inlines.Add(new Run($"  CX {cx}") { Foreground = new SolidColorBrush(GateCxColor) });
            if (other > 0)
                tb.Inlines.Add(new Run($"  +{other}") { Foreground = new SolidColorBrush(GateOtherColor) });
            if (fails > 0)
                tb.Inlines.Add(new Run($"  ✕{fails}") { Foreground = new SolidColorBrush(GateErrorColor) });
        }
        GateTitleText.Text = $"CPA · last {GateSlots}";
        var byModel = last.GroupBy(r => r.Family).OrderByDescending(g => g.Count()).ThenBy(g => g.Key);
        ToolTip.SetTip(GateCompactRow, string.Join("\n", byModel.Select(g => $"{Cap(g.Key)}: {g.Count()}")));
        ToolTip.SetTip(GateSection, ToolTip.GetTip(GateCompactRow));
    }

    private const int GateSlots = 20;
    private IReadOnlyList<GateRequest>? _renderedGate;
    private static readonly Color GateEmptyColor = Color.FromRgb(0x33, 0x33, 0x33);
    private static readonly Color GatePendingColor = Color.FromRgb(0x5A, 0x5A, 0x5A);

    // Always GateSlots squares: unused slots first (grey track), then oldest to newest.
    private static void FillGateStrip(UniformGrid strip, List<GateRequest> reqs, double gap)
    {
        strip.Children.Clear();
        for (var i = reqs.Count; i < GateSlots; i++)
            strip.Children.Add(new Border
            {
                Background = new SolidColorBrush(GateEmptyColor),
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(0, 0, gap, 0),
            });
        foreach (var r in reqs)
        {
            if (r.InProgress)
            {
                var pending = new Border
                {
                    Background = new SolidColorBrush(GatePendingColor),
                    CornerRadius = new CornerRadius(1),
                    Margin = new Thickness(0, 0, gap, 0),
                };
                ToolTip.SetTip(pending, "in progress" + (string.IsNullOrWhiteSpace(r.Title) ? "" : $"\n{r.Title}"));
                strip.Children.Add(pending);
                continue;
            }
            var failed = r.Outcome is not null and not "ok";
            var tick = new Border
            {
                Background = new SolidColorBrush(failed ? GateErrorColor : GateColor(r.Family)),
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(0, 0, gap, 0),
            };
            var when = r.StartedAt?.ToLocalTime().ToString("h:mm:ss tt") ?? "";
            var model = r.Resolved ?? r.Family;
            var effort = r.Effort != null ? $" · {r.Effort}" : "";
            var title = string.IsNullOrWhiteSpace(r.Title) ? "" : $"\n{r.Title}";
            var host = string.IsNullOrWhiteSpace(r.Host) ? "" : $" · {r.Host}";
            ToolTip.SetTip(tick, $"{model}{effort} · {r.Outcome ?? "?"}\n{when}{host}{title}");
            strip.Children.Add(tick);
        }
    }

    private void ApplyJev(JevBlock? j)
    {
        var show = j != null && _config.ShowJev;
        JevSection.IsVisible = show;
        if (!show || j == null) return;
        var text = $"1d {FormatSpend(j.Spend1Day)}  •  3d {FormatSpend(j.Spend3Days)}  •  7d {FormatSpend(j.Spend7Days)}";
        JevSpendText.Text = text;
        JevCompactText.Text = text;
    }

    private void ApplyCodex(CodexBlock? c)
    {
        // Held for ApplyCodex2, which runs next and owns the title text.
        _codexPlan = c?.PlanType;
        _codexResets = c?.Resets;
        var codexAccount = AccountInfoFor("codex");
        SetCompactPlan(CodexCompactPlan, _codexPlan, _codexResets, codexAccount?.Priority);
        SetCompactEmail(CodexCompactEmail, codexAccount);
        var show = c != null && _config.ShowCodex;
        CodexSection.IsVisible = show;
        ApplyCodexBalance(CodexBalanceText, CodexCompactBalanceRow, CodexCompactBalanceText, c?.CreditsBalance, "Codex CR");
        if (!show || c == null) return;
        SetCodexHeaderText(CodexCreditsText, c);
        (_codex5hUsed, _codex5hExpected, _codex5hReset) = ShowCodexWindow(c.Primary, Codex5hLabel, CodexPrimaryBar);
        (_codex24hUsed, _codex24hExpected, _codex24hReset) = ShowCodexWindow(c.OneDay, Codex24hLabel, CodexDailyBar);
        (_codex7dUsed, _codex7dExpected, _codex7dReset) = ShowCodexWindow(c.Secondary, Codex7dLabel, CodexSecondaryBar);

        // Spark (GPT-5.3-Codex-Spark) optional bars, gated by the Codex Spark toggle.
        var showSpark5h = c.SparkPrimary != null && _config.ShowCodexSpark;
        var showSpark7d = c.SparkSecondary != null && _config.ShowCodexSpark;
        CodexSpark5hLabel.IsVisible = showSpark5h;
        CodexSparkPrimaryBar.IsVisible = showSpark5h;
        CodexSpark7dLabel.IsVisible = showSpark7d;
        CodexSparkSecondaryBar.IsVisible = showSpark7d;

        if (showSpark5h && c.SparkPrimary is { } sp)
        {
            _codexSpark5hUsed = Math.Clamp(sp.UsedPercent, 0, 100);
            _codexSpark5hExpected = sp.ExpectedPercent;
            _codexSpark5hReset = sp.ResetsAt;
            SetTargetBar(CodexSparkPrimaryBar, _codexSpark5hUsed.Value, _codexSpark5hExpected);
        }
        else
        {
            _codexSpark5hUsed = null;
            _codexSpark5hExpected = null;
            _codexSpark5hReset = null;
        }

        if (showSpark7d && c.SparkSecondary is { } ss)
        {
            _codexSpark7dUsed = Math.Clamp(ss.UsedPercent, 0, 100);
            _codexSpark7dExpected = ss.ExpectedPercent;
            _codexSpark7dReset = ss.ResetsAt;
            SetTargetBar(CodexSparkSecondaryBar, _codexSpark7dUsed.Value, _codexSpark7dExpected);
        }
        else
        {
            _codexSpark7dUsed = null;
            _codexSpark7dExpected = null;
            _codexSpark7dReset = null;
        }

        var range = CodexResetSummary(c);
        if (showSpark5h || showSpark7d)
        {
            var sparkPct = _codexSpark5hUsed.HasValue && _codexSpark7dUsed.HasValue
                ? $"{_codexSpark5hUsed.Value:F0}% / {_codexSpark7dUsed.Value:F0}%"
                : _codexSpark5hUsed.HasValue
                    ? $"{_codexSpark5hUsed.Value:F0}%"
                    : $"{_codexSpark7dUsed!.Value:F0}%";
            range += (range.Length > 0 ? " • " : "") + $"Spark: {sparkPct}";
        }
        CodexRangeText.Text = range;
    }

    // The account-wide Codex windows OpenAI reports, shortest first, labelled by length. Any can be
    // missing, including the weekly one: an account is never hidden for lacking a particular window.
    internal static IEnumerable<(string Label, UsageWindow Window)> CodexAccountWindows(CodexBlock? c)
    {
        if (c?.Primary is { } five) yield return ("5h", five);
        if (c?.OneDay is { } day) yield return ("24h", day);
        if (c?.Secondary is { } week) yield return ("7d", week);
    }

    // Header percentages: shortest and longest window ("5% / 40% used"), or the only one ("12% used").
    private static void SetCodexHeaderText(TextBlock tb, CodexBlock c)
    {
        var windows = CodexAccountWindows(c).Select(w => w.Window).ToList();
        if (windows.Count >= 2)
            SetTwoUsedExpectedInlines(tb, Math.Clamp(windows[0].UsedPercent, 0, 100), windows[0].ExpectedPercent,
                Math.Clamp(windows[^1].UsedPercent, 0, 100), windows[^1].ExpectedPercent);
        else
        {
            SetUsedExpectedInlines(tb, windows.Count == 1 ? Math.Clamp(windows[0].UsedPercent, 0, 100) : null, windows.FirstOrDefault()?.ExpectedPercent);
            tb.Inlines!.Add(new Run(" used"));
        }
    }

    // "5h: in 3h 10m • 24h: in 20h 4m • 7d: Wed Nov 5": countdowns for the windows that reset within a day or so.
    internal static string CodexResetSummary(CodexBlock c) => string.Join(" • ", CodexAccountWindows(c).Select(w =>
        w.Label == "7d" ? $"7d: {FormatResetDate(w.Window.ResetsAt)}" : $"{w.Label}: in {FormatResetCountdown(w.Window.ResetsAt)}"));

    // Shows one full-view window row (label + bar) when the window is reported, hides it otherwise,
    // and returns what the compact view needs.
    private (double? Used, double? Expected, DateTimeOffset? Reset) ShowCodexWindow(UsageWindow? w, TextBlock label, Grid bar)
    {
        label.IsVisible = w != null;
        bar.IsVisible = w != null;
        if (w == null) return (null, null, null);
        var used = Math.Clamp(w.UsedPercent, 0, 100);
        SetTargetBar(bar, used, w.ExpectedPercent);
        return (used, w.ExpectedPercent, w.ResetsAt);
    }

    private void ApplyCodex2(CodexBlock? c)
    {
        // Absence is intentional: no auth/no successful data means no card or
        // compact rows. The primary labels gain "1" only while account #2 is visible.
        var show = c != null && _config.ShowCodex2;
        Codex2Section.IsVisible = show;
        var codex2Account = AccountInfoFor("codex2");
        SetTitleWithPlan(CodexTitleText, show ? "Codex #1" : "Codex", _codexPlan, _codexResets, AccountInfoFor("codex"));
        SetTitleWithPlan(Codex2TitleText, "Codex #2", c?.PlanType, c?.Resets, codex2Account);
        SetCompactPlan(Codex2CompactPlan, c?.PlanType, c?.Resets, codex2Account?.Priority);
        SetCompactEmail(Codex2CompactEmail, codex2Account);
        CodexCompactFiveHourLabelText.Text = show ? "Codex1 5h" : "Codex 5h";
        CodexCompactOneDayLabelText.Text = show ? "Codex1 24h" : "Codex 24h";
        CodexCompactSevenDayLabelText.Text = show ? "Codex1 7d" : "Codex 7d";
        CodexCompactBalanceLabel.Text = show ? "Codex1 CR" : "Codex CR";
        ApplyCodexBalance(Codex2BalanceText, Codex2CompactBalanceRow, Codex2CompactBalanceText, c?.CreditsBalance, "Codex2 CR");

        if (!show || c == null)
        {
            (_codex2FiveHourUsed, _codex2FiveHourExpected, _codex2FiveHourReset) = ShowCodexWindow(null, Codex2FiveHourLabel, Codex2PrimaryBar);
            (_codex2OneDayUsed, _codex2OneDayExpected, _codex2OneDayReset) = ShowCodexWindow(null, Codex2OneDayLabel, Codex2DailyBar);
            (_codex2SevenDayUsed, _codex2SevenDayExpected, _codex2SevenDayReset) = ShowCodexWindow(null, Codex2SevenDayLabel, Codex2SecondaryBar);
            _codex2SparkFiveHourUsed = null;
            _codex2SparkSevenDayUsed = null;
            _codex2SparkFiveHourExpected = null;
            _codex2SparkSevenDayExpected = null;
            _codex2SparkFiveHourReset = null;
            _codex2SparkSevenDayReset = null;
            Codex2SparkFiveHourLabel.IsVisible = false;
            Codex2SparkPrimaryBar.IsVisible = false;
            Codex2SparkSevenDayLabel.IsVisible = false;
            Codex2SparkSecondaryBar.IsVisible = false;
            return;
        }

        SetCodexHeaderText(Codex2CreditsText, c);
        (_codex2FiveHourUsed, _codex2FiveHourExpected, _codex2FiveHourReset) = ShowCodexWindow(c.Primary, Codex2FiveHourLabel, Codex2PrimaryBar);
        (_codex2OneDayUsed, _codex2OneDayExpected, _codex2OneDayReset) = ShowCodexWindow(c.OneDay, Codex2OneDayLabel, Codex2DailyBar);
        (_codex2SevenDayUsed, _codex2SevenDayExpected, _codex2SevenDayReset) = ShowCodexWindow(c.Secondary, Codex2SevenDayLabel, Codex2SecondaryBar);

        var showSparkFiveHour = c.SparkPrimary != null && _config.ShowCodexSpark;
        var showSparkSevenDay = c.SparkSecondary != null && _config.ShowCodexSpark;
        Codex2SparkFiveHourLabel.IsVisible = showSparkFiveHour;
        Codex2SparkPrimaryBar.IsVisible = showSparkFiveHour;
        Codex2SparkSevenDayLabel.IsVisible = showSparkSevenDay;
        Codex2SparkSecondaryBar.IsVisible = showSparkSevenDay;

        if (showSparkFiveHour && c.SparkPrimary is { } sparkFiveHour)
        {
            _codex2SparkFiveHourUsed = Math.Clamp(sparkFiveHour.UsedPercent, 0, 100);
            _codex2SparkFiveHourExpected = sparkFiveHour.ExpectedPercent;
            _codex2SparkFiveHourReset = sparkFiveHour.ResetsAt;
            SetTargetBar(Codex2SparkPrimaryBar, _codex2SparkFiveHourUsed.Value, _codex2SparkFiveHourExpected);
        }
        else
        {
            _codex2SparkFiveHourUsed = null;
            _codex2SparkFiveHourExpected = null;
            _codex2SparkFiveHourReset = null;
        }

        if (showSparkSevenDay && c.SparkSecondary is { } sparkSevenDay)
        {
            _codex2SparkSevenDayUsed = Math.Clamp(sparkSevenDay.UsedPercent, 0, 100);
            _codex2SparkSevenDayExpected = sparkSevenDay.ExpectedPercent;
            _codex2SparkSevenDayReset = sparkSevenDay.ResetsAt;
            SetTargetBar(Codex2SparkSecondaryBar, _codex2SparkSevenDayUsed.Value, _codex2SparkSevenDayExpected);
        }
        else
        {
            _codex2SparkSevenDayUsed = null;
            _codex2SparkSevenDayExpected = null;
            _codex2SparkSevenDayReset = null;
        }

        var range = CodexResetSummary(c);
        if (showSparkFiveHour || showSparkSevenDay)
        {
            var sparkPercent = _codex2SparkFiveHourUsed.HasValue && _codex2SparkSevenDayUsed.HasValue
                ? $"{_codex2SparkFiveHourUsed.Value:F0}% / {_codex2SparkSevenDayUsed.Value:F0}%"
                : _codex2SparkFiveHourUsed.HasValue
                    ? $"{_codex2SparkFiveHourUsed.Value:F0}%"
                    : $"{_codex2SparkSevenDayUsed!.Value:F0}%";
            range += (range.Length > 0 ? " • " : "") + $"Spark: {sparkPercent}";
        }
        Codex2RangeText.Text = range;
    }

    private void ApplyClaude(ClaudeBlock? c)
    {
        var show = c != null && _config.ShowClaude;
        ClaudeCodeSection.IsVisible = show;
        if (!show || c == null) return;
        var claudeAccount = AccountInfoFor("claude");
        SetTitleWithPlan(ClaudeTitleText, "Claude", c.SubscriptionType, c.Resets, claudeAccount);
        SetCompactEmail(ClaudeCompactEmail, claudeAccount);
        // Only some Claude accounts have a 5-hour limit; the 5h rows show just for those.
        var fiveHour = c.FiveHour is { } fw ? Math.Clamp(fw.UsedPercent, 0, 100) : (double?)null;
        if (fiveHour.HasValue)
            SetTwoUsedExpectedInlines(ClaudeCodeCreditsText, fiveHour.Value, c.FiveHour!.ExpectedPercent, c.SevenDay.UsedPercent, c.SevenDay.ExpectedPercent);
        else
            SetUsedExpectedInlines(ClaudeCodeCreditsText, c.SevenDay.UsedPercent, c.SevenDay.ExpectedPercent);
        _claude5hUsed = fiveHour;
        _claude7dUsed = c.SevenDay.UsedPercent;
        _claude5hExpected = c.FiveHour?.ExpectedPercent;
        _claude7dExpected = c.SevenDay.ExpectedPercent;
        ClaudeCode5hLabel.IsVisible = fiveHour.HasValue;
        ClaudeCodePrimaryBar.IsVisible = fiveHour.HasValue;
        if (fiveHour.HasValue)
            SetTargetBar(ClaudeCodePrimaryBar, fiveHour.Value, _claude5hExpected);
        SetTargetBar(ClaudeCodeSecondaryBar, _claude7dUsed, _claude7dExpected);
        _claude5hReset = c.FiveHour?.ResetsAt;
        _claude7dReset = c.SevenDay.ResetsAt;

        var showDesign = c.SevenDayDesign != null && _config.ShowClaudeDesign;
        ClaudeDesignLabel.IsVisible = showDesign;
        ClaudeDesignBar.IsVisible = showDesign;
        if (showDesign && c.SevenDayDesign is { } d)
        {
            _claudeDesignUsed = Math.Clamp(d.UsedPercent, 0, 100);
            _claudeDesignExpected = d.ExpectedPercent;
            _claudeDesignReset = d.ResetsAt;
            SetTargetBar(ClaudeDesignBar, _claudeDesignUsed.Value, _claudeDesignExpected);
        }
        else
        {
            _claudeDesignUsed = null;
            _claudeDesignExpected = null;
            _claudeDesignReset = null;
        }

        var showFable = c.SevenDayFable != null && _config.ShowClaudeFable;
        ClaudeFableLabel.IsVisible = showFable;
        ClaudeFableBar.IsVisible = showFable;
        if (showFable && c.SevenDayFable is { } f)
        {
            _claudeFableUsed = Math.Clamp(f.UsedPercent, 0, 100);
            _claudeFableExpected = f.ExpectedPercent;
            _claudeFableReset = f.ResetsAt;
            SetTargetBar(ClaudeFableBar, _claudeFableUsed.Value, _claudeFableExpected);
        }
        else
        {
            _claudeFableUsed = null;
            _claudeFableExpected = null;
            _claudeFableReset = null;
        }

        EnsurePlanSlots(ClaudeCompactPlan, ("5h", ClaudeCompact5hRow), ("7d", ClaudeCompact7dRow),
            ("Des", ClaudeCompactDesignRow), ("Fab", ClaudeCompactFableRow));
        SetClaudeCompactPlan(ClaudeCompactPlan, c.SubscriptionType, c.Resets, claudeAccount?.Priority,
            ClaudeRowKinds(c.FiveHour != null, showDesign, showFable));

        var range = (c.FiveHour != null ? $"5h: in {FormatResetCountdown(c.FiveHour.ResetsAt)} • " : "")
            + $"7d: {FormatResetDate(c.SevenDay.ResetsAt)}";
        if (_claudeDesignUsed.HasValue)
            range += $" • Des: {_claudeDesignUsed.Value:F0}%";
        if (_claudeFableUsed.HasValue)
            range += $" • Fab: {_claudeFableUsed.Value:F0}%";
        ClaudeCodeRangeText.Text = range;
    }

    private void ApplyClaude2(ClaudeBlock? c)
    {
        // AND-gate: the second account renders only when the server exposes
        // providers.claude2 AND the local config flag approves it.
        var show = c != null && _config.ShowClaude2;
        ClaudeCode2Section.IsVisible = show;
        var claude2Account = AccountInfoFor("claude2");
        SetTitleWithPlan(ClaudeCode2TitleText, "Claude 2", c?.SubscriptionType, c?.Resets, claude2Account);
        SetCompactEmail(Claude2CompactEmail, claude2Account);
        if (!show)
        {
            // Clear all claude2 state so the compact view never renders stale data.
            _claude2FiveHourUsed = null;
            _claude2SevenDayUsed = 0;
            _claude2FiveHourExpected = null;
            _claude2SevenDayExpected = null;
            _claude2FiveHourReset = null;
            _claude2SevenDayReset = null;
            _claude2DesignUsed = null;
            _claude2DesignExpected = null;
            _claude2DesignReset = null;
            _claude2FableUsed = null;
            _claude2FableExpected = null;
            _claude2FableReset = null;
            ClaudeDesign2Label.IsVisible = false;
            ClaudeDesign2Bar.IsVisible = false;
            ClaudeFable2Label.IsVisible = false;
            ClaudeFable2Bar.IsVisible = false;
            ClaudeCode25hLabel.IsVisible = false;
            ClaudeCode2PrimaryBar.IsVisible = false;
            return;
        }
        var fiveHour = c!.FiveHour is { } fw ? Math.Clamp(fw.UsedPercent, 0, 100) : (double?)null;
        if (fiveHour.HasValue)
            SetTwoUsedExpectedInlines(ClaudeCode2CreditsText, fiveHour.Value, c.FiveHour!.ExpectedPercent, c.SevenDay.UsedPercent, c.SevenDay.ExpectedPercent);
        else
            SetUsedExpectedInlines(ClaudeCode2CreditsText, c.SevenDay.UsedPercent, c.SevenDay.ExpectedPercent);
        _claude2FiveHourUsed = fiveHour;
        _claude2SevenDayUsed = c.SevenDay.UsedPercent;
        _claude2FiveHourExpected = c.FiveHour?.ExpectedPercent;
        _claude2SevenDayExpected = c.SevenDay.ExpectedPercent;
        ClaudeCode25hLabel.IsVisible = fiveHour.HasValue;
        ClaudeCode2PrimaryBar.IsVisible = fiveHour.HasValue;
        if (fiveHour.HasValue)
            SetTargetBar(ClaudeCode2PrimaryBar, fiveHour.Value, _claude2FiveHourExpected);
        SetTargetBar(ClaudeCode2SecondaryBar, _claude2SevenDayUsed, _claude2SevenDayExpected);
        _claude2FiveHourReset = c.FiveHour?.ResetsAt;
        _claude2SevenDayReset = c.SevenDay.ResetsAt;

        var showDesign = c.SevenDayDesign != null && _config.ShowClaude2Design;
        ClaudeDesign2Label.IsVisible = showDesign;
        ClaudeDesign2Bar.IsVisible = showDesign;
        if (showDesign && c.SevenDayDesign is { } d)
        {
            _claude2DesignUsed = Math.Clamp(d.UsedPercent, 0, 100);
            _claude2DesignExpected = d.ExpectedPercent;
            _claude2DesignReset = d.ResetsAt;
            SetTargetBar(ClaudeDesign2Bar, _claude2DesignUsed.Value, _claude2DesignExpected);
        }
        else
        {
            _claude2DesignUsed = null;
            _claude2DesignExpected = null;
            _claude2DesignReset = null;
        }

        var showFable = c.SevenDayFable != null && _config.ShowClaudeFable;
        ClaudeFable2Label.IsVisible = showFable;
        ClaudeFable2Bar.IsVisible = showFable;
        if (showFable && c.SevenDayFable is { } f)
        {
            _claude2FableUsed = Math.Clamp(f.UsedPercent, 0, 100);
            _claude2FableExpected = f.ExpectedPercent;
            _claude2FableReset = f.ResetsAt;
            SetTargetBar(ClaudeFable2Bar, _claude2FableUsed.Value, _claude2FableExpected);
        }
        else
        {
            _claude2FableUsed = null;
            _claude2FableExpected = null;
            _claude2FableReset = null;
        }

        EnsurePlanSlots(Claude2CompactPlan, ("5h", Claude2Compact5hRow), ("7d", Claude2Compact7dRow),
            ("Des", Claude2CompactDesignRow), ("Fab", Claude2CompactFableRow));
        SetClaudeCompactPlan(Claude2CompactPlan, c.SubscriptionType, c.Resets, claude2Account?.Priority,
            ClaudeRowKinds(c.FiveHour != null, showDesign, showFable));

        var range = (c.FiveHour != null ? $"5h: in {FormatResetCountdown(c.FiveHour.ResetsAt)} • " : "")
            + $"7d: {FormatResetDate(c.SevenDay.ResetsAt)}";
        if (_claude2DesignUsed.HasValue)
            range += $" • Des: {_claude2DesignUsed.Value:F0}%";
        if (_claude2FableUsed.HasValue)
            range += $" • Fab: {_claude2FableUsed.Value:F0}%";
        ClaudeCode2RangeText.Text = range;
    }

    private void ApplyZai(ZaiBlock? z)
    {
        var showTokens = z?.FiveHour != null && _config.ShowZai;
        var showRequests = z?.Monthly != null && _config.ShowZaiRequests;
        var show = showTokens || showRequests;
        ZaiSection.IsVisible = show;
        SetTitleWithPlan(ZaiTitleText, "Z.ai", z?.Level);
        ZaiCompactPlan.Text = FormatPlan(z?.Level) ?? "";
        _zai5hPercent = showTokens ? z!.FiveHour?.UsedPercent : null;
        _zai5hReset = showTokens ? z!.FiveHour?.ResetsAt : null;
        _zai5hExpected = showTokens ? z!.FiveHour?.ExpectedPercent : null;
        _zaiMoPercent = showRequests ? z!.Monthly?.UsedPercent : null;
        _zaiMoReset = showRequests ? z!.Monthly?.ResetsAt : null;
        _zaiMoExpected = showRequests ? z!.Monthly?.ExpectedPercent : null;

        ZaiTokenLabel.IsVisible = showTokens;
        ZaiTokenBar.IsVisible = showTokens;
        if (showTokens)
            SetTargetBar(ZaiTokenBar, z!.FiveHour!.UsedPercent, _zai5hExpected);

        ZaiRequestsLabel.IsVisible = showRequests;
        ZaiMonthlyBar.IsVisible = showRequests;
        if (showRequests)
            SetTargetBar(ZaiMonthlyBar, z!.Monthly!.UsedPercent, _zaiMoExpected);
        ZaiDetailText.IsVisible = showRequests && z!.MonthlyCurrent.HasValue && z.MonthlyLimit.HasValue;
        ZaiDetailText.Text = ZaiDetailText.IsVisible
            ? $"{z!.MonthlyCurrent:N0} / {z.MonthlyLimit:N0} Web/MCP requests this month"
            : "";

        if (!show || z == null)
        {
            ZaiCreditsText.Text = "—";
            ZaiResetText.Text = "";
            return;
        }

        ZaiCreditsText.Inlines!.Clear();
        bool zaiAnyHeader = false;
        if (_zai5hPercent.HasValue)
        {
            ZaiCreditsText.Inlines.Add(new Run($"5h {_zai5hPercent.Value:F0}%"));
            if (_zai5hExpected.HasValue)
                ZaiCreditsText.Inlines.Add(ExpectedRun($" {_zai5hExpected.Value:F0}%", ZaiCreditsText.FontSize, _zai5hPercent.Value, _zai5hExpected));
            zaiAnyHeader = true;
        }
        if (_zaiMoPercent.HasValue)
        {
            if (zaiAnyHeader) ZaiCreditsText.Inlines.Add(new Run(" • "));
            ZaiCreditsText.Inlines.Add(new Run($"Requests {_zaiMoPercent.Value:F0}%"));
            if (_zaiMoExpected.HasValue)
                ZaiCreditsText.Inlines.Add(ExpectedRun($" {_zaiMoExpected.Value:F0}%", ZaiCreditsText.FontSize, _zaiMoPercent.Value, _zaiMoExpected));
            zaiAnyHeader = true;
        }
        if (!zaiAnyHeader) ZaiCreditsText.Inlines.Add(new Run("Connected"));
        else ZaiCreditsText.Inlines.Add(new Run(" used"));

        var resetParts = new List<string>();
        if (_zai5hReset.HasValue) resetParts.Add($"5h: in {FormatResetCountdown(_zai5hReset)}");
        if (_zaiMoReset.HasValue) resetParts.Add($"Requests: {FormatResetDate(_zaiMoReset)}");
        ZaiResetText.Text = string.Join(" • ", resetParts);
    }

    private void UpdateUpdateAffordances()
    {
        UpdateButton.IsVisible = _updateAvailable && _viewMode == PopupViewMode.Full;
        UpdateButtonCompact.IsVisible = _updateAvailable && _viewMode == PopupViewMode.Compact;
    }

    private void SetViewMode(PopupViewMode mode, bool anchorBottomRight = true)
    {
        // Capture current bottom-right pixel position before resizing
        var scaling = Screens.Primary?.Scaling ?? 1;
        var oldPixelW = (int)(Bounds.Width * scaling);
        var oldPixelH = (int)(Bounds.Height * scaling);
        var bottomRight = new PixelPoint(Position.X + oldPixelW, Position.Y + oldPixelH);

        _viewMode = mode;

        FullView.IsVisible = mode == PopupViewMode.Full;
        CompactView.IsVisible = mode == PopupViewMode.Compact;

        CompactButton.IsVisible = mode == PopupViewMode.Full;
        ProxyButton.IsVisible = mode == PopupViewMode.Full && ProxyHeaderLink(_config).IsVisible;
        RouterButton.IsVisible = mode == PopupViewMode.Full && ManagerHeaderLink(_config).IsVisible;
        CloseButton.IsVisible = mode == PopupViewMode.Full;
        UpdateUpdateAffordances();

        // Full mode: outer border visible; compact/icon: transparent wrapper
        if (mode == PopupViewMode.Full)
        {
            OuterBorder.Background = Avalonia.Media.Brush.Parse("#E6181818");
            OuterBorder.BorderThickness = new Thickness(1);
            OuterBorder.Padding = new Thickness(12);
            OuterBorder.CornerRadius = new CornerRadius(18);
            SizeToContent = SizeToContent.Height;
            Width = FullWidth;
        }
        else
        {
            OuterBorder.Background = Avalonia.Media.Brushes.Transparent;
            OuterBorder.BorderThickness = new Thickness(0);
            OuterBorder.Padding = new Thickness(0);
            OuterBorder.CornerRadius = new CornerRadius(0);
            SizeToContent = SizeToContent.WidthAndHeight;
        }

        UpdateCompactSummary();
        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();

        if (anchorBottomRight)
        {
            // Reposition so bottom-right stays at the same spot
            Dispatcher.UIThread.Post(() =>
            {
                var s = Screens.Primary?.Scaling ?? 1;
                var newPixelW = (int)(Bounds.Width * s);
                var newPixelH = (int)(Bounds.Height * s);
                Position = new PixelPoint(
                    bottomRight.X - newPixelW,
                    bottomRight.Y - newPixelH);
            }, DispatcherPriority.Loaded);
        }
    }

    private void UpdateCompactSummary()
    {
        // Exhausted limits grey out: an account-wide window (5h/7d) greys every bar of
        // that account; a model-scoped window (Spark, Design, Fable) greys only its own.
        static bool Full(double? v) => v >= 99.5;
        var codexOut = Full(_codex5hUsed) || Full(_codex24hUsed) || Full(_codex7dUsed);
        var codexSparkOut = codexOut || Full(_codexSpark5hUsed) || Full(_codexSpark7dUsed);
        var codex2Out = Full(_codex2FiveHourUsed) || Full(_codex2OneDayUsed) || Full(_codex2SevenDayUsed);
        var codex2SparkOut = codex2Out || Full(_codex2SparkFiveHourUsed) || Full(_codex2SparkSevenDayUsed);
        var claudeOut = Full(_claude5hUsed) || Full(_claude7dUsed);
        var claudeDesignOut = claudeOut || Full(_claudeDesignUsed);
        var claudeFableOut = claudeOut || Full(_claudeFableUsed);
        var claude2Out = Full(_claude2FiveHourUsed) || Full(_claude2SevenDayUsed);
        var claude2DesignOut = claude2Out || Full(_claude2DesignUsed);
        var claude2FableOut = claude2Out || Full(_claude2FableUsed);
        var zaiOut = Full(_zai5hPercent);
        var zaiMoOut = zaiOut || Full(_zaiMoPercent);
        SetTargetBarsExhausted(codexOut, CodexPrimaryBar, CodexDailyBar, CodexSecondaryBar);
        SetTargetBarsExhausted(codexSparkOut, CodexSparkPrimaryBar, CodexSparkSecondaryBar);
        SetTargetBarsExhausted(codex2Out, Codex2PrimaryBar, Codex2DailyBar, Codex2SecondaryBar);
        SetTargetBarsExhausted(codex2SparkOut, Codex2SparkPrimaryBar, Codex2SparkSecondaryBar);
        SetTargetBarsExhausted(claudeOut, ClaudeCodePrimaryBar, ClaudeCodeSecondaryBar);
        SetTargetBarsExhausted(claudeDesignOut, ClaudeDesignBar);
        SetTargetBarsExhausted(claudeFableOut, ClaudeFableBar);
        SetTargetBarsExhausted(claude2Out, ClaudeCode2PrimaryBar, ClaudeCode2SecondaryBar);
        SetTargetBarsExhausted(claude2DesignOut, ClaudeDesign2Bar);
        SetTargetBarsExhausted(claude2FableOut, ClaudeFable2Bar);
        SetTargetBarsExhausted(zaiOut, ZaiTokenBar);
        SetTargetBarsExhausted(zaiMoOut, ZaiMonthlyBar);

        if (OpenRouterCompactSection == null)
            return;

        // System one-liners
        UpdateCompactSystemBars();
        UptimeCompactText.Text = UptimeText.Text;
        NetCompactText.Text = $"{NetworkUpText.Text}  {NetworkDownText.Text}";

        // AI one-liners
        OpenRouterCompactSection.IsVisible = OpenRouterSection.IsVisible;
        OpenAiCompactRow.IsVisible = OpenAiSection.IsVisible;
        JevCompactRow.IsVisible = JevSection.IsVisible;
        CodexCompactSection.IsVisible = CodexSection.IsVisible;
        Codex2CompactSection.IsVisible = Codex2Section.IsVisible;
        ClaudeCompactSection.IsVisible = ClaudeCodeSection.IsVisible;
        Claude2CompactSection.IsVisible = ClaudeCode2Section.IsVisible;
        ZaiCompactSection.IsVisible = ZaiSection.IsVisible;
        ZaiCompactTokenRow.IsVisible = _zai5hPercent.HasValue && ZaiSection.IsVisible;
        ZaiCompactRequestsRow.IsVisible = _zaiMoPercent.HasValue && ZaiSection.IsVisible;

        if (string.IsNullOrWhiteSpace(OpenRouterCompactText.Text))
            OpenRouterCompactText.Text = "—";

        OpenAiCompactText.Text = OpenAiCreditsText.Text ?? "—";

        const double barWidth = 62.0;
        CodexCompact5hRow.IsVisible = _codex5hUsed.HasValue;
        if (_codex5hUsed.HasValue)
        {
            RenderCompactBar(CodexCompact5hBar, CodexCompact5hTick, Color.FromRgb(0x8B, 0xC3, 0x4A), _codex5hUsed.Value, _codex5hExpected, barWidth, codexOut);
            SetUsedExpectedInlines(CodexCompact5hPct, _codex5hUsed, _codex5hExpected);
        }
        CodexCompact24hRow.IsVisible = _codex24hUsed.HasValue;
        if (_codex24hUsed.HasValue)
        {
            RenderCompactBar(CodexCompact24hBar, CodexCompact24hTick, Color.FromRgb(0xCD, 0xDC, 0x39), _codex24hUsed.Value, _codex24hExpected, barWidth, codexOut);
            SetUsedExpectedInlines(CodexCompact24hPct, _codex24hUsed, _codex24hExpected);
        }
        CodexCompact7dRow.IsVisible = _codex7dUsed.HasValue;
        if (_codex7dUsed.HasValue)
        {
            RenderCompactBar(CodexCompact7dBar, CodexCompact7dTick, Color.FromRgb(0xB3, 0x9D, 0xDB), _codex7dUsed.Value, _codex7dExpected, barWidth, codexOut);
            SetUsedExpectedInlines(CodexCompact7dPct, _codex7dUsed, _codex7dExpected);
        }
        CodexCompact5hReset.Text = _codex5hReset.HasValue ? $"in {FormatResetCountdown(_codex5hReset)}" : "";
        CodexCompact24hReset.Text = _codex24hReset.HasValue ? $"in {FormatResetCountdown(_codex24hReset)}" : "";
        CodexCompact7dReset.Text = _codex7dReset.HasValue ? FormatResetDate(_codex7dReset) : "";

        CodexCompactSpark5hRow.IsVisible = _codexSpark5hUsed.HasValue;
        if (_codexSpark5hUsed.HasValue)
        {
            RenderCompactBar(CodexCompactSpark5hBar, CodexCompactSpark5hTick, Color.FromRgb(0x4D, 0xD0, 0xE1), _codexSpark5hUsed.Value, _codexSpark5hExpected, barWidth, codexSparkOut);
            SetUsedExpectedInlines(CodexCompactSpark5hPct, _codexSpark5hUsed, _codexSpark5hExpected);
            CodexCompactSpark5hReset.Text = _codexSpark5hReset.HasValue ? $"in {FormatResetCountdown(_codexSpark5hReset)}" : "";
        }
        CodexCompactSpark7dRow.IsVisible = _codexSpark7dUsed.HasValue;
        if (_codexSpark7dUsed.HasValue)
        {
            RenderCompactBar(CodexCompactSpark7dBar, CodexCompactSpark7dTick, Color.FromRgb(0x4D, 0xB6, 0xAC), _codexSpark7dUsed.Value, _codexSpark7dExpected, barWidth, codexSparkOut);
            SetUsedExpectedInlines(CodexCompactSpark7dPct, _codexSpark7dUsed, _codexSpark7dExpected);
            CodexCompactSpark7dReset.Text = _codexSpark7dReset.HasValue ? FormatResetDate(_codexSpark7dReset) : "";
        }

        Codex2CompactFiveHourRow.IsVisible = _codex2FiveHourUsed.HasValue;
        Codex2CompactOneDayRow.IsVisible = _codex2OneDayUsed.HasValue;
        Codex2CompactSevenDayRow.IsVisible = _codex2SevenDayUsed.HasValue;
        if (Codex2Section.IsVisible)
        {
            if (_codex2FiveHourUsed.HasValue)
            {
                RenderCompactBar(Codex2CompactFiveHourBar, Codex2CompactFiveHourTick, Color.FromRgb(0x64, 0xB5, 0xF6), _codex2FiveHourUsed.Value, _codex2FiveHourExpected, barWidth, codex2Out);
                SetUsedExpectedInlines(Codex2CompactFiveHourPercent, _codex2FiveHourUsed, _codex2FiveHourExpected);
                Codex2CompactFiveHourReset.Text = _codex2FiveHourReset.HasValue ? $"in {FormatResetCountdown(_codex2FiveHourReset)}" : "";
            }
            if (_codex2OneDayUsed.HasValue)
            {
                RenderCompactBar(Codex2CompactOneDayBar, Codex2CompactOneDayTick, Color.FromRgb(0x4F, 0xC3, 0xF7), _codex2OneDayUsed.Value, _codex2OneDayExpected, barWidth, codex2Out);
                SetUsedExpectedInlines(Codex2CompactOneDayPercent, _codex2OneDayUsed, _codex2OneDayExpected);
                Codex2CompactOneDayReset.Text = _codex2OneDayReset.HasValue ? $"in {FormatResetCountdown(_codex2OneDayReset)}" : "";
            }
            if (_codex2SevenDayUsed.HasValue)
            {
                RenderCompactBar(Codex2CompactSevenDayBar, Codex2CompactSevenDayTick, Color.FromRgb(0x90, 0xCA, 0xF9), _codex2SevenDayUsed.Value, _codex2SevenDayExpected, barWidth, codex2Out);
                SetUsedExpectedInlines(Codex2CompactSevenDayPercent, _codex2SevenDayUsed, _codex2SevenDayExpected);
                Codex2CompactSevenDayReset.Text = _codex2SevenDayReset.HasValue ? FormatResetDate(_codex2SevenDayReset) : "";
            }
        }

        Codex2CompactSparkFiveHourRow.IsVisible = _codex2SparkFiveHourUsed.HasValue && Codex2Section.IsVisible;
        if (_codex2SparkFiveHourUsed.HasValue && Codex2Section.IsVisible)
        {
            RenderCompactBar(Codex2CompactSparkFiveHourBar, Codex2CompactSparkFiveHourTick, Color.FromRgb(0x95, 0x75, 0xCD), _codex2SparkFiveHourUsed.Value, _codex2SparkFiveHourExpected, barWidth, codex2SparkOut);
            SetUsedExpectedInlines(Codex2CompactSparkFiveHourPercent, _codex2SparkFiveHourUsed, _codex2SparkFiveHourExpected);
            Codex2CompactSparkFiveHourReset.Text = _codex2SparkFiveHourReset.HasValue ? $"in {FormatResetCountdown(_codex2SparkFiveHourReset)}" : "";
        }
        Codex2CompactSparkSevenDayRow.IsVisible = _codex2SparkSevenDayUsed.HasValue && Codex2Section.IsVisible;
        if (_codex2SparkSevenDayUsed.HasValue && Codex2Section.IsVisible)
        {
            RenderCompactBar(Codex2CompactSparkSevenDayBar, Codex2CompactSparkSevenDayTick, Color.FromRgb(0xB3, 0x9D, 0xDB), _codex2SparkSevenDayUsed.Value, _codex2SparkSevenDayExpected, barWidth, codex2SparkOut);
            SetUsedExpectedInlines(Codex2CompactSparkSevenDayPercent, _codex2SparkSevenDayUsed, _codex2SparkSevenDayExpected);
            Codex2CompactSparkSevenDayReset.Text = _codex2SparkSevenDayReset.HasValue ? FormatResetDate(_codex2SparkSevenDayReset) : "";
        }

        ClaudeCompact5hRow.IsVisible = _claude5hUsed.HasValue;
        if (_claude5hUsed.HasValue)
        {
            RenderCompactBar(ClaudeCompact5hBar, ClaudeCompact5hTick, Color.FromRgb(0xFF, 0x8A, 0x65), _claude5hUsed.Value, _claude5hExpected, barWidth, claudeOut);
            SetUsedExpectedInlines(ClaudeCompact5hPct, _claude5hUsed, _claude5hExpected);
            ClaudeCompact5hReset.Text = _claude5hReset.HasValue ? $"in {FormatResetCountdown(_claude5hReset)}" : "";
        }
        RenderCompactBar(ClaudeCompact7dBar, ClaudeCompact7dTick, Color.FromRgb(0xFF, 0xB7, 0x4D), _claude7dUsed, _claude7dExpected, barWidth, claudeOut);
        SetUsedExpectedInlines(ClaudeCompact7dPct, _claude7dUsed, _claude7dExpected);
        ClaudeCompact7dReset.Text = _claude7dReset.HasValue ? FormatResetDate(_claude7dReset) : "";

        ClaudeCompactDesignRow.IsVisible = _claudeDesignUsed.HasValue;
        if (_claudeDesignUsed.HasValue)
        {
            RenderCompactBar(ClaudeCompactDesignBar, ClaudeCompactDesignTick, Color.FromRgb(0xF4, 0x8F, 0xB1), _claudeDesignUsed.Value, _claudeDesignExpected, barWidth, claudeDesignOut);
            SetUsedExpectedInlines(ClaudeCompactDesignPct, _claudeDesignUsed, _claudeDesignExpected);
            ClaudeCompactDesignReset.Text = _claudeDesignReset.HasValue ? FormatResetDate(_claudeDesignReset) : "";
        }

        ClaudeCompactFableRow.IsVisible = _claudeFableUsed.HasValue;
        if (_claudeFableUsed.HasValue)
        {
            RenderCompactBar(ClaudeCompactFableBar, ClaudeCompactFableTick, Color.FromRgb(0xE2, 0x70, 0x3A), _claudeFableUsed.Value, _claudeFableExpected, barWidth, claudeFableOut);
            SetUsedExpectedInlines(ClaudeCompactFablePct, _claudeFableUsed, _claudeFableExpected);
            ClaudeCompactFableReset.Text = _claudeFableReset.HasValue ? FormatResetDate(_claudeFableReset) : "";
        }

        // Second Claude account — fields are cleared whenever the section hides,
        // but keep the visibility guard for symmetry and safety.
        Claude2CompactDesignRow.IsVisible = _claude2DesignUsed.HasValue && ClaudeCode2Section.IsVisible;
        Claude2CompactFableRow.IsVisible = _claude2FableUsed.HasValue && ClaudeCode2Section.IsVisible;
        Claude2Compact5hRow.IsVisible = _claude2FiveHourUsed.HasValue && ClaudeCode2Section.IsVisible;
        if (ClaudeCode2Section.IsVisible)
        {
            if (_claude2FiveHourUsed.HasValue)
            {
                RenderCompactBar(Claude2Compact5hBar, Claude2Compact5hTick, Color.FromRgb(0xFF, 0x8A, 0x65), _claude2FiveHourUsed.Value, _claude2FiveHourExpected, barWidth, claude2Out);
                SetUsedExpectedInlines(Claude2Compact5hPct, _claude2FiveHourUsed, _claude2FiveHourExpected);
                Claude2Compact5hReset.Text = _claude2FiveHourReset.HasValue ? $"in {FormatResetCountdown(_claude2FiveHourReset)}" : "";
            }
            RenderCompactBar(Claude2Compact7dBar, Claude2Compact7dTick, Color.FromRgb(0xFF, 0xB7, 0x4D), _claude2SevenDayUsed, _claude2SevenDayExpected, barWidth, claude2Out);
            SetUsedExpectedInlines(Claude2Compact7dPct, _claude2SevenDayUsed, _claude2SevenDayExpected);
            Claude2Compact7dReset.Text = _claude2SevenDayReset.HasValue ? FormatResetDate(_claude2SevenDayReset) : "";

            if (_claude2DesignUsed.HasValue)
            {
                RenderCompactBar(Claude2CompactDesignBar, Claude2CompactDesignTick, Color.FromRgb(0xF4, 0x8F, 0xB1), _claude2DesignUsed.Value, _claude2DesignExpected, barWidth, claude2DesignOut);
                SetUsedExpectedInlines(Claude2CompactDesignPct, _claude2DesignUsed, _claude2DesignExpected);
                Claude2CompactDesignReset.Text = _claude2DesignReset.HasValue ? FormatResetDate(_claude2DesignReset) : "";
            }

            if (_claude2FableUsed.HasValue)
            {
                RenderCompactBar(Claude2CompactFableBar, Claude2CompactFableTick, Color.FromRgb(0xE2, 0x70, 0x3A), _claude2FableUsed.Value, _claude2FableExpected, barWidth, claude2FableOut);
                SetUsedExpectedInlines(Claude2CompactFablePct, _claude2FableUsed, _claude2FableExpected);
                Claude2CompactFableReset.Text = _claude2FableReset.HasValue ? FormatResetDate(_claude2FableReset) : "";
            }
        }

        if (_zai5hPercent.HasValue)
        {
            RenderCompactBar(ZaiCompact5hBar, ZaiCompact5hTick, Color.FromRgb(0xBA, 0x68, 0xC8), _zai5hPercent.Value, _zai5hExpected, barWidth, zaiOut);
            SetUsedExpectedInlines(ZaiCompact5hPct, _zai5hPercent, _zai5hExpected);
            ZaiCompact5hReset.Text = _zai5hReset.HasValue ? $"in {FormatResetCountdown(_zai5hReset)}" : "";
        }
        if (_zaiMoPercent.HasValue)
        {
            RenderCompactBar(ZaiCompactMoBar, ZaiCompactMoTick, Color.FromRgb(0x7E, 0x57, 0xC2), _zaiMoPercent.Value, _zaiMoExpected, barWidth, zaiMoOut);
            SetUsedExpectedInlines(ZaiCompactMoPct, _zaiMoPercent, _zaiMoExpected);
            ZaiCompactMoReset.Text = _zaiMoReset.HasValue ? FormatResetDate(_zaiMoReset) : "";
        }

        foreach (var (text, reset) in new[]
                 {
                     (CodexCompact5hReset, _codex5hReset), (CodexCompact24hReset, _codex24hReset), (CodexCompact7dReset, _codex7dReset),
                     (CodexCompactSpark5hReset, _codexSpark5hReset), (CodexCompactSpark7dReset, _codexSpark7dReset),
                     (Codex2CompactFiveHourReset, _codex2FiveHourReset), (Codex2CompactOneDayReset, _codex2OneDayReset), (Codex2CompactSevenDayReset, _codex2SevenDayReset),
                     (Codex2CompactSparkFiveHourReset, _codex2SparkFiveHourReset), (Codex2CompactSparkSevenDayReset, _codex2SparkSevenDayReset),
                     (ClaudeCompact5hReset, _claude5hReset), (ClaudeCompact7dReset, _claude7dReset),
                     (ClaudeCompactDesignReset, _claudeDesignReset), (ClaudeCompactFableReset, _claudeFableReset),
                     (Claude2Compact5hReset, _claude2FiveHourReset), (Claude2Compact7dReset, _claude2SevenDayReset),
                     (Claude2CompactDesignReset, _claude2DesignReset), (Claude2CompactFableReset, _claude2FableReset),
                     (ZaiCompact5hReset, _zai5hReset), (ZaiCompactMoReset, _zaiMoReset),
                 })
            ToolTip.SetTip(text, reset.HasValue ? $"Resets {FormatCentralTime(reset)}" : null);

        SetResetTimesTooltip(CodexRangeText, ("5h", _codex5hReset), ("24h", _codex24hReset), ("7d", _codex7dReset),
            ("Spark 5h", _codexSpark5hReset), ("Spark 7d", _codexSpark7dReset));
        SetResetTimesTooltip(Codex2RangeText, ("5h", _codex2FiveHourReset), ("24h", _codex2OneDayReset), ("7d", _codex2SevenDayReset),
            ("Spark 5h", _codex2SparkFiveHourReset), ("Spark 7d", _codex2SparkSevenDayReset));
        SetResetTimesTooltip(ClaudeCodeRangeText, ("5h", _claude5hReset), ("7d", _claude7dReset),
            ("Design", _claudeDesignReset), ("Fable", _claudeFableReset));
        SetResetTimesTooltip(ClaudeCode2RangeText, ("5h", _claude2FiveHourReset), ("7d", _claude2SevenDayReset),
            ("Design", _claude2DesignReset), ("Fable", _claude2FableReset));
        SetResetTimesTooltip(ZaiResetText, ("5h", _zai5hReset), ("Requests", _zaiMoReset));

        // Grey text last, after percentages were rebuilt above.
        SetTextExhausted(CodexCreditsText, codexOut);
        SetTextExhausted(Codex2CreditsText, codex2Out);
        SetTextExhausted(ClaudeCodeCreditsText, claudeOut);
        SetTextExhausted(ClaudeCode2CreditsText, claude2Out);
        SetTextExhausted(ZaiCreditsText, zaiOut);
        foreach (var (bar, flag) in new[]
                 {
                     (CodexCompact5hBar, codexOut), (CodexCompact24hBar, codexOut), (CodexCompact7dBar, codexOut),
                     (CodexCompactSpark5hBar, codexSparkOut), (CodexCompactSpark7dBar, codexSparkOut),
                     (Codex2CompactFiveHourBar, codex2Out), (Codex2CompactOneDayBar, codex2Out), (Codex2CompactSevenDayBar, codex2Out),
                     (Codex2CompactSparkFiveHourBar, codex2SparkOut), (Codex2CompactSparkSevenDayBar, codex2SparkOut),
                     (ClaudeCompact5hBar, claudeOut), (ClaudeCompact7dBar, claudeOut),
                     (ClaudeCompactDesignBar, claudeDesignOut), (ClaudeCompactFableBar, claudeFableOut),
                     (Claude2Compact5hBar, claude2Out), (Claude2Compact7dBar, claude2Out),
                     (Claude2CompactDesignBar, claude2DesignOut), (Claude2CompactFableBar, claude2FableOut),
                     (ZaiCompact5hBar, zaiOut), (ZaiCompactMoBar, zaiMoOut),
                 })
            SetCompactRowExhausted(bar, flag);

        // Visibility of the fixed blocks is settled above, so the provider labels can follow it.
        OrderCompactAccounts();
    }

    private static void ApplyCodexBalance(TextBlock full, Grid row, TextBlock compact, string? balance, string label)
    {
        var text = decimal.TryParse(balance, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("0.##", CultureInfo.InvariantCulture)
            : balance;
        full.IsVisible = row.IsVisible = !string.IsNullOrWhiteSpace(text);
        full.Text = $"{label}  {text}";
        compact.Text = text;
    }

    private static void SetResetTimesTooltip(TextBlock text, params (string Label, DateTimeOffset? At)[] resets)
    {
        var tip = string.Join("\n", resets.Where(r => r.At.HasValue)
            .Select(r => $"{r.Label}: {FormatCentralTime(r.At)}"));
        ToolTip.SetTip(text, tip.Length > 0 ? tip : null);
    }

    private static string FormatUsedExpected(double? used, double? expected)
    {
        if (!used.HasValue) return "—";
        return expected.HasValue
            ? $"{used.Value:F0}% {expected.Value:F0}%"
            : $"{used.Value:F0}%";
    }

    private static string FormatExpectedShort(double? expected)
    {
        return expected.HasValue ? $"{expected.Value:F0}%" : "—";
    }

    private static Color ExpectedTint(double used, double? expected)
    {
        if (expected is not double exp || used <= exp) return Color.FromRgb(0xC8, 0xE6, 0xC9); // light green
        var headroom = Math.Max(1.0, 100.0 - exp);
        var t = Math.Clamp((used - exp) / headroom, 0, 1);
        if (t <= 0.33) return Color.FromRgb(0xFF, 0xF5, 0x9D); // light yellow
        if (t <= 0.66) return Color.FromRgb(0xFF, 0xCC, 0x80); // light orange
        return Color.FromRgb(0xEF, 0x9A, 0x9A);                 // light red
    }

    private static Run ExpectedRun(string text, double baseFontSize, double used, double? expected)
    {
        return new Run(text)
        {
            Foreground = new SolidColorBrush(ExpectedTint(used, expected)),
            FontSize = Math.Max(6, baseFontSize - 2),
        };
    }

    // The plan tier is metadata, not a measurement, so it trails the provider
    // name in the sub-label gray rather than competing with the percentages.
    private static void SetTitleWithPlan(TextBlock tb, string title, string? plan, ResetCredits? resets = null,
        AccountInfo? account = null, DateTimeOffset? now = null)
    {
        tb.Inlines!.Clear();
        tb.Inlines.Add(new Run(title));
        var label = FormatPlan(plan);
        if (label != null)
            tb.Inlines.Add(new Run($"  {label}")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize = Math.Max(6, tb.FontSize - 2),
            });
        var badge = ResetBadge(resets);
        if (badge != null)
            tb.Inlines.Add(new ResetBadgeRun($"  {badge}") { FontSize = Math.Max(6, tb.FontSize - 2) });
        var priority = FormatPriority(account?.Priority);
        if (priority != null)
            tb.Inlines.Add(new Run($"  {priority}")
            {
                Foreground = new SolidColorBrush(PriorityColor),
                FontSize = Math.Max(6, tb.FontSize - 2),
            });
        if (!string.IsNullOrWhiteSpace(account?.Email) || HasAvailability(account) || account?.Renewal != null || account?.Expiry != null)
        {
            tb.Inlines.Add(new LineBreak());
            AddAccountLine(tb.Inlines, account!.Email, account, Math.Max(6, tb.FontSize - 2), now ?? DateTimeOffset.Now);
        }
        var tips = new[] { AvailabilityTooltip(account), ExpiryTooltip(account?.Expiry), RenewalTooltip(account?.Renewal), ResetTooltip(resets), DataAgeTooltip(account, now) }.Where(t => t != null);
        ToolTip.SetTip(tb, tips.Any() ? string.Join("\n", tips) : null);
    }

    // Email, then when the account can next be used: "live" when no account-wide window is full,
    // else a countdown to the latest reset among the full ones ("4h 58m", "2d 3h"). How old the
    // numbers are lives in the tooltip.
    private static void AddAccountLine(Avalonia.Controls.Documents.InlineCollection inlines, string? email,
        AccountInfo? account, double fontSize, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(email))
            inlines.Add(new Run(email) { Foreground = new SolidColorBrush(EmailColor), FontSize = fontSize, FontWeight = FontWeight.Normal });
        // Separators only between items on this line (the full-view title has other inlines before it).
        var empty = string.IsNullOrWhiteSpace(email);
        if (HasAvailability(account))
        {
            var badge = new AvailabilityBadge(empty ? "" : " · ", account!.Blocked, account.AvailableAt);
            empty = false;
            var run = new Run(badge.Text(now))
                { Foreground = new SolidColorBrush(AvailabilityColor(account)), FontSize = fontSize, FontWeight = FontWeight.Normal };
            AvailabilityBadges.AddOrUpdate(run, badge);
            inlines.Add(run);
        }
        // An account that will not renew shows its end date in place of the renewal.
        if (FormatExpiry(account?.Expiry) is { } expiry)
            inlines.Add(new Run((empty ? "" : " · ") + expiry)
            {
                Foreground = new SolidColorBrush(account!.Expiry!.DaysUntil is <= ExpirySoonDays ? ExpirySoonColor : RenewalSoonColor),
                FontSize = fontSize, FontWeight = FontWeight.SemiBold,
            });
        else if (FormatRenewal(account?.Renewal) is { } renewal)
            inlines.Add(new Run((empty ? "" : " · ") + renewal)
            {
                Foreground = new SolidColorBrush(account!.Renewal!.DaysUntil is <= RenewalSoonDays ? RenewalSoonColor : EmailColor),
                FontSize = fontSize, FontWeight = FontWeight.Normal,
            });
    }

    // Next billing date, entered by hand on usage-api: "renews Dec 2 · 28d"; amber in its last few days.
    private const int RenewalSoonDays = 3;
    private static readonly Color RenewalSoonColor = Color.FromRgb(0xFF, 0xB7, 0x4D);

    // Subscription end (usage-api's expiry): "expires Dec 9 · 26d", amber; red in its last week and once passed.
    private const int ExpirySoonDays = 7;
    private static readonly Color ExpirySoonColor = Color.FromRgb(0xF4, 0x43, 0x36);

    private static string? FormatExpiry(Expiry? x)
    {
        if (x == null) return null;
        var date = x.ExpiresAt.ToString("MMM d", CultureInfo.InvariantCulture);
        return x.DaysUntil switch
        {
            < 0 => $"expired {date}",
            0 => $"expires {date} · today",
            { } d => $"expires {date} · {d}d",
            null => $"expires {date}",
        };
    }

    private static string? ExpiryTooltip(Expiry? x)
    {
        if (x == null) return null;
        var days = x.DaysUntil switch { null => "", < 0 and var d => $" ({-d} days ago)", 0 => " (today)", 1 => " (in 1 day)", { } d => $" (in {d} days)" };
        var source = x.Source == "provider" ? " · will not renew (reported by the provider)" : x.Source == "manual" ? " · entered by hand" : "";
        return $"{(x.DaysUntil < 0 ? "Expired" : "Expires")} {x.ExpiresAt.ToString("ddd MMM d, yyyy", CultureInfo.InvariantCulture)}{days}{source}";
    }

    private static string? FormatRenewal(Renewal? r)
    {
        if (r == null) return null;
        var date = r.RenewsAt.ToString("MMM d", CultureInfo.InvariantCulture);
        var days = r.DaysUntil switch { null => "", 0 => " · today", { } d => $" · {d}d" };
        return $"renews {date}{days}";
    }

    private static string? RenewalTooltip(Renewal? r)
    {
        if (r == null) return null;
        var cycle = string.IsNullOrWhiteSpace(r.BillingCycle) ? "" : $", {r.BillingCycle}";
        var days = r.DaysUntil switch { null => "", 0 => " (today)", 1 => " (in 1 day)", { } d => $" (in {d} days)" };
        var source = r.Source == "manual" ? " · entered by hand" : "";
        return $"Renews {r.RenewsAt.ToString("ddd MMM d, yyyy", CultureInfo.InvariantCulture)}{days}{cycle}{source}";
    }

    // Shown once the account has numbers (or is known to be blocked); hidden for an account with no data yet.
    private static bool HasAvailability(AccountInfo? account) => account is { FetchedAt: not null } or { Blocked: true };

    private sealed record AvailabilityBadge(string Prefix, bool Blocked, DateTimeOffset? AvailableAt)
    {
        public string Text(DateTimeOffset now) => Prefix + FormatAvailability(Blocked, AvailableAt, now);
    }

    // Every badge Run on screen, so the countdowns tick down between usage-api refreshes without a re-render.
    // Weak keys: runs from replaced cards drop out on their own.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Run, AvailabilityBadge> AvailabilityBadges = new();

    private static void RefreshAvailabilityBadges(DateTimeOffset now)
    {
        foreach (var (run, badge) in AvailabilityBadges)
        {
            var text = badge.Text(now);
            if (run.Text != text) run.Text = text;
        }
    }

    // A window counts as full where the bars already grey out (shown as 100%).
    private const double FullPercent = 99.5;

    // Account-wide windows only: per-model limits (Spark, Fable, Design) don't block the whole account.
    // A full 24-hour Codex window blocks the account like a 5-hour or 7-day one.
    private static IEnumerable<UsageWindow?> AccountWideWindows(CodexBlock? codex, ClaudeBlock? claude) =>
        new[] { codex?.Primary, codex?.OneDay, codex?.Secondary, claude?.FiveHour, claude?.SevenDay };

    private static AccountInfo WithAvailability(AccountInfo info, IEnumerable<UsageWindow?> accountWide)
    {
        var full = accountWide.Where(w => w is { UsedPercent: >= FullPercent }).Select(w => w!).ToList();
        if (full.Count == 0) return info with { Blocked = false, AvailableAt = null };
        // Usable again only once every full window has reset; an unknown reset leaves the time unknown.
        var availableAt = full.Any(w => w.ResetsAt == null) ? null : full.Max(w => w.ResetsAt);
        return info with { Blocked = true, AvailableAt = availableAt };
    }

    private static string FormatAvailability(bool blocked, DateTimeOffset? availableAt, DateTimeOffset now)
    {
        if (!blocked) return "live";
        if (availableAt is not { } at) return "out";
        var left = at - now;
        // The window has reset; the next refresh shows the new numbers.
        if (left <= TimeSpan.Zero) return "live";
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d {left.Hours}h";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes}m";
        return left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes}m" : "<1m";
    }

    private static readonly Color LiveColor = Color.FromRgb(0x81, 0xC7, 0x84);
    private static readonly Color BlockedBadgeColor = Color.FromRgb(0xE5, 0x73, 0x73);

    // Blocked counts down in red. "live" is green, but amber/grey when usage-api says its numbers are getting old.
    private static Color AvailabilityColor(AccountInfo account) => account.Blocked ? BlockedBadgeColor : account.Staleness switch
    {
        "stale" => StaleBadgeColor,
        "little_stale" => Color.FromRgb(0xFF, 0xB7, 0x4D),
        _ => LiveColor,
    };
    private static readonly Color StaleBadgeColor = Color.FromRgb(0x9E, 0x9E, 0x9E);

    private static string? AvailabilityTooltip(AccountInfo? account)
    {
        if (account is not { Blocked: true }) return HasAvailability(account) ? "Usable now" : null;
        return account.AvailableAt is { } at ? $"Out of usage until {FormatCentralTime(at)}" : "Out of usage (reset time unknown)";
    }

    private static string? FormatDataAge(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t) return null;
        var age = now - t;
        if (age < TimeSpan.FromMinutes(2)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromHours(48)) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private static string? DataAgeTooltip(AccountInfo? account, DateTimeOffset? now = null)
    {
        if (account?.FetchedAt is not { } at) return null;
        var how = account.DataSource switch
        {
            "passive" => "seen in CPA request headers",
            "active" => "checked with the usage endpoint",
            _ => "updated",
        };
        var tier = account.Staleness switch { "stale" => " (stale)", "little_stale" => " (a little stale)", _ => "" };
        return $"Data {FormatDataAge(at, now ?? DateTimeOffset.Now)}{tier}: {how} {FormatCentralTime(at)}";
    }

    // Which login an account is and the routing priority CLIProxyAPI gives it
    // ("P10"), so the monitor's rows can be matched to the CPA dashboard cards.
    private static readonly Color PriorityColor = Color.FromRgb(0xB3, 0x9D, 0xDB);
    private static readonly Color EmailColor = Color.FromRgb(0x77, 0x77, 0x77);

    private AccountInfo? AccountInfoFor(string key)
    {
        if (_lastStatus?.Accounts is not { } accounts || !accounts.TryGetValue(key, out var info)) return null;
        return FixedAccountWindows(key) is { } windows ? WithAvailability(info, windows) : info;
    }

    // Account-wide windows of the four built-in accounts; null for any other key.
    private IEnumerable<UsageWindow?>? FixedAccountWindows(string key) => key switch
    {
        "codex" => AccountWideWindows(_lastStatus?.Codex, null),
        "codex2" => AccountWideWindows(_lastStatus?.Codex2, null),
        "claude" => AccountWideWindows(null, _lastStatus?.Claude),
        "claude2" => AccountWideWindows(null, _lastStatus?.Claude2),
        _ => null,
    };

    private static string? FormatPriority(double? priority) =>
        priority is { } p ? "P" + p.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : null;

    // Gmail is the common case, so its domain is dropped; other domains stay so
    // etgarcia@uno.edu and etgarcia@my.uno.edu remain distinguishable.
    private static string? ShortEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var e = email.Trim();
        return e.EndsWith("@gmail.com", StringComparison.OrdinalIgnoreCase) ? e[..^"@gmail.com".Length] : e;
    }

    // Small grey line heading an account's compact rows: short email and data age; hidden when both are unknown.
    private static void SetCompactEmail(TextBlock tb, AccountInfo? account, DateTimeOffset? now = null)
    {
        tb.Inlines!.Clear();
        AddAccountLine(tb.Inlines, ShortEmail(account?.Email), account, tb.FontSize, now ?? DateTimeOffset.Now);
        tb.IsVisible = tb.Inlines.Count > 0;
        var tips = new[] { string.IsNullOrWhiteSpace(account?.Email) ? null : account.Email, AvailabilityTooltip(account),
                ExpiryTooltip(account?.Expiry), RenewalTooltip(account?.Renewal), DataAgeTooltip(account, now) }
            .Where(t => t != null);
        ToolTip.SetTip(tb, tips.Any() ? string.Join("\n", tips) : null);
    }

    // Available limit resets (Claude "Reset for free", Codex reset credits); redeemed on the provider's site.
    private static readonly Color ResetBadgeColor = Color.FromRgb(0x4D, 0xD0, 0xE1);

    // A "↻N" reset badge. It stays cyan even when the rest of its account is greyed out (exhausted or
    // stale), so the resets that would bring the account back are always easy to spot.
    internal sealed class ResetBadgeRun : Run
    {
        public ResetBadgeRun(string text) : base(text) => Foreground = new SolidColorBrush(ResetBadgeColor);
        protected override Type StyleKeyOverride => typeof(Run);
    }

    // Runs that keep their own color when their text is greyed: reset badges and availability countdowns.
    internal static bool KeepsOwnColor(Inline inline) =>
        inline is ResetBadgeRun || (inline is Run run && AvailabilityBadges.TryGetValue(run, out _));

    // Full-view title: Claude's weekly resets as "↻N", its Fable-only ones as "Fab↻N"; one "↻N" otherwise.
    private static string? ResetBadge(ResetCredits? r)
    {
        if (r == null) return null;
        var parts = new List<string>();
        if (r.SevenDayCount > 0) parts.Add($"↻{r.SevenDayCount}");
        if (r.FableCount > 0) parts.Add($"Fab↻{r.FableCount}");
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    private static string? ResetTooltip(ResetCredits? r)
    {
        if (r == null) return null;
        static string N(int n, string what) => $"{n} {what}reset{(n == 1 ? "" : "s")}";
        var s = !r.IsSplit ? N(r.AvailableCount, "limit ") + " available"
            : r.AvailableCount == 0 ? "No limit resets available"
            : string.Join(", ", new[] {
                r.WeeklyCount > 0 ? N(r.WeeklyCount.Value, "weekly ") : null,
                r.FableCount > 0 ? N(r.FableCount, "Fable ") : null,
                r.OtherCount > 0 ? N(r.OtherCount, "unrecognized ") + " (counted on 7d)" : null,
            }.Where(p => p != null)) + " available";
        if (r.NextExpiresAt.HasValue) s += $", expires {FormatCentralTime(r.NextExpiresAt)}";
        if (r.CheckedAt.HasValue) s += $"\nChecked {FormatCentralTime(r.CheckedAt)}";
        return s;
    }

    // Claude's compact plan column holds one line per usage row ("5h", "7d", "Fab", "Des"), each as tall
    // as its row, so a reset count sits beside the limit it clears: weekly resets on the 7d line, Fable
    // resets on the Fab line. Slots are made once per column; a trailing slot takes what has no row.
    internal static void EnsurePlanSlots(StackPanel column, params (string Kind, Control Row)[] rows)
    {
        if (column.Children.Count > 0) return;
        foreach (var (kind, row) in rows)
        {
            var slot = PlanSlot(kind);
            row.GetObservable(Visual.BoundsProperty).Subscribe(new Avalonia.Reactive.AnonymousObserver<Rect>(b =>
            {
                if (b.Height > 0) slot.Height = b.Height;
            }));
            column.Children.Add(slot);
        }
        column.Children.Add(PlanSlot("extra"));
    }

    internal static string[] ClaudeRowKinds(bool fiveHour, bool design, bool fable) =>
        new[] { fiveHour ? "5h" : null, "7d", design ? "Des" : null, fable ? "Fab" : null }.OfType<string>().ToArray();

    private static TextBlock PlanSlot(string kind) => new()
    {
        Tag = kind, FontSize = 8, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Foreground = Brush.Parse("#888888"),
    };

    // visibleKinds: the rows this account shows, so the plan name goes on the first visible line.
    internal static void SetClaudeCompactPlan(StackPanel column, string? plan, ResetCredits? resets, double? priority,
        IReadOnlyCollection<string> visibleKinds)
    {
        var slots = column.Children.OfType<TextBlock>().ToList();
        var lines = slots.Where(s => s.Tag is string k && (k == "extra" || visibleKinds.Contains(k))).ToList();
        var extra = slots.First(s => (string?)s.Tag == "extra");
        foreach (var slot in slots)
        {
            slot.Inlines!.Clear();
            slot.IsVisible = false;
        }
        TextBlock Line(string kind) => lines.FirstOrDefault(s => (string?)s.Tag == kind) ?? extra;
        void Add(TextBlock slot, string text, Color? color = null)
        {
            if (slot.Inlines!.Count > 0) text = " " + text;
            slot.Inlines.Add(color is { } c ? new Run(text) { Foreground = new SolidColorBrush(c) } : new Run(text));
        }
        void AddBadge(TextBlock slot, string text) =>
            slot.Inlines!.Add(new ResetBadgeRun(slot.Inlines.Count > 0 ? " " + text : text));
        var first = lines[0];
        var label = FormatPlan(plan);
        if (label != null) Add(first, label);
        if (resets is { SevenDayCount: > 0 }) AddBadge(Line("7d"), $"↻{resets.SevenDayCount}");
        if (resets is { FableCount: > 0 })
        {
            var fab = Line("Fab");
            AddBadge(fab, fab == extra ? $"Fab↻{resets.FableCount}" : $"↻{resets.FableCount}");
        }
        var p = FormatPriority(priority);
        if (p != null) Add(lines.Count > 2 ? lines[1] : extra, p, PriorityColor);
        foreach (var slot in lines) slot.IsVisible = slot.Inlines!.Count > 0 || slot != extra;
        ToolTip.SetTip(column, ResetTooltip(resets));
    }

    // Compact plan column: reset badge to the right of the plan.
    // Priority follows; the column wraps rather than clipping when all three don't fit.
    private static void SetCompactPlan(TextBlock tb, string? plan, ResetCredits? resets, double? priority = null)
    {
        tb.TextWrapping = TextWrapping.Wrap;
        tb.Inlines!.Clear();
        tb.Inlines.Add(new Run(FormatPlan(plan) ?? ""));
        var badge = ResetBadge(resets);
        if (badge != null)
            tb.Inlines.Add(new ResetBadgeRun($" {badge}"));
        var p = FormatPriority(priority);
        if (p != null)
            tb.Inlines.Add(new Run($" {p}") { Foreground = new SolidColorBrush(PriorityColor) });
        ToolTip.SetTip(tb, ResetTooltip(resets));
    }

    // Providers disagree on casing: usage-api sends Claude's tier already
    // display-ready ("Max 20x"), while Codex and Z.ai send raw enum values
    // ("pro", "max"). Anything already carrying capitals is left untouched.
    private static string? FormatPlan(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Replace('_', ' ');
        if (s.Any(char.IsUpper)) return s;
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static void SetUsedExpectedInlines(TextBlock tb, double? used, double? expected)
    {
        tb.Inlines!.Clear();
        if (!used.HasValue)
        {
            tb.Inlines.Add(new Run("—"));
            return;
        }
        tb.Inlines.Add(new Run($"{used.Value:F0}%"));
        if (expected.HasValue)
        {
            tb.Inlines.Add(ExpectedRun($" {expected.Value:F0}%", tb.FontSize, used.Value, expected));
        }
    }

    private static void SetTwoUsedExpectedInlines(TextBlock tb, double a, double? aExp, double b, double? bExp)
    {
        tb.Inlines!.Clear();
        tb.Inlines.Add(new Run($"{a:F0}%"));
        if (aExp.HasValue)
            tb.Inlines.Add(ExpectedRun($" ({aExp.Value:F0}%)", tb.FontSize, a, aExp));
        tb.Inlines.Add(new Run($" / {b:F0}%"));
        if (bExp.HasValue)
            tb.Inlines.Add(ExpectedRun($" ({bExp.Value:F0}%)", tb.FontSize, b, bExp));
        tb.Inlines.Add(new Run(" used"));
    }

    private static string FormatResetCountdown(DateTimeOffset? resetAt)
    {
        if (!resetAt.HasValue) return "—";
        var remaining = resetAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "now";
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"{remaining.Minutes}m";
    }

    private static string FormatResetDate(DateTimeOffset? resetAt)
    {
        if (!resetAt.HasValue) return "—";
        try
        {
            var central = GetCentralTimeZone();
            var converted = TimeZoneInfo.ConvertTime(resetAt.Value, central);
            return $"{converted:ddd MMM d}";
        }
        catch
        {
            return $"{resetAt.Value.ToLocalTime():ddd MMM d}";
        }
    }
    #endregion

    private static string FormatCodexPlan(string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType))
            return "Plan unknown";

        var normalized = planType.Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized);
    }

    // "claude3" -> "Claude #3", "codex" -> "Codex", "openrouter" -> "Openrouter".
    private static string ProviderLabel(string key)
    {
        var digits = key.Length - key.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Length;
        var name = key[..(key.Length - digits)];
        name = name.Length == 0 ? key : char.ToUpperInvariant(name[0]) + name[1..];
        return digits == 0 ? name : $"{name} #{key[^digits..]}";
    }

    private static string FormatFetchedAt(DateTimeOffset? fetchedAt)
    {
        return FormatCentralTime(fetchedAt);
    }

    private static string EnsureUsedSuffix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "—";

        return text.Contains("used", StringComparison.OrdinalIgnoreCase)
            ? text
            : $"{text} used";
    }

    private static string FormatCentralTime(DateTimeOffset? value)
    {
        if (!value.HasValue)
            return "unknown";

        try
        {
            var central = GetCentralTimeZone();
            var converted = TimeZoneInfo.ConvertTime(value.Value, central);
            var isDst = central.IsDaylightSavingTime(converted.DateTime);
            var suffix = isDst ? "CDT" : "CST";
            return $"{converted:MMM d h:mm tt} {suffix}";
        }
        catch
        {
            return $"{value.Value.ToLocalTime():MMM d h:mm tt}";
        }
    }

    private static TimeZoneInfo GetCentralTimeZone()
    {
        try
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time")
                : TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }

    private static string FormatTokenCount(long totalTokens)
    {
        if (totalTokens >= 1_000_000)
            return $"{totalTokens / 1_000_000.0:F2}M";
        if (totalTokens >= 1_000)
            return $"{totalTokens / 1_000.0:F1}K";
        return totalTokens.ToString("N0");
    }

    #region Drag Support

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_updateInProgress) return;
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            if (e.Source is Control sourceControl)
            {
                if (sourceControl is Button)
                    return;

                if (sourceControl.GetVisualAncestors().OfType<Button>().Any())
                    return;
            }

            if (CloseButton.IsPointerOver || CompactButton.IsPointerOver
                || CloseCompactButton.IsPointerOver || UpdateButton.IsPointerOver || UpdateButtonCompact.IsPointerOver)
                return;

            var pos = point.Position;
            RememberFrontmostApplication();
            _isDragging = true;
            _anchorBottomRight = null;
            var screenPos = this.PointToScreen(pos);
            _dragStartScreenPoint = new PixelPoint((int)screenPos.X, (int)screenPos.Y);
            _windowStartPosition = Position;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging)
        {
            var currentPos = e.GetPosition(this);
            var screenPos = this.PointToScreen(currentPos);
            var currentScreenPoint = new PixelPoint((int)screenPos.X, (int)screenPos.Y);

            Position = new PixelPoint(
                _windowStartPosition.X + (currentScreenPoint.X - _dragStartScreenPoint.X),
                _windowStartPosition.Y + (currentScreenPoint.Y - _dragStartScreenPoint.Y)
            );
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            var processId = _lastExternalFrontmostProcessId;
            DispatcherTimer.RunOnce(
                () => MacWindowPresenter.ActivateApplication(processId),
                TimeSpan.FromMilliseconds(50),
                DispatcherPriority.Loaded);
        }
    }

    private void RememberFrontmostApplication()
    {
        var processId = MacWindowPresenter.GetFrontmostApplicationProcessId();
        if (processId > 0 && processId != Environment.ProcessId)
            _lastExternalFrontmostProcessId = processId;
    }

    #endregion

    #region Non-Activating Window (Windows)

    private void MakeWindowNonActivating()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero) return;

                var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting non-activating: {ex.Message}");
            }
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    #endregion

    #region Windows Memory API

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    #endregion

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Hide-on-close applies only to user-initiated closes. App- or OS-driven
        // closes (Cmd+Q, logout, macOS system shutdown) must never be vetoed —
        // cancelling those makes macOS report "UsageMonitor interrupted shutdown"
        // and hangs the whole machine's shutdown until the OS force-kills us.
        if (!_allowClose && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            HidePopup();
        }
        else
        {
            _systemRefreshTimer.Stop();
            _aiRefreshTimer.Stop();
        }
        base.OnClosing(e);
    }
}
