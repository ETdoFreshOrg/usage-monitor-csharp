using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UsageMonitor.Services;

/// <summary>
/// Guards against the app growing without bound. In Nov 2026 a build grew ~470 MB/hour
/// (about 2,000 new VM regions an hour) until it reached 22 GB and took the Mac Studio
/// down. Every minute this samples the process's own physical footprint and VM region
/// count (the same numbers vmmap and Jetsam report). It logs them to usage.log hourly,
/// and as soon as growth crosses a warning level, so a recurrence leaves evidence. Past
/// the hard limit for two samples in a row it raises <see cref="LimitExceeded"/> so the
/// app can relaunch itself instead of starving the machine.
/// </summary>
public sealed class MemoryWatchdog : IDisposable
{
    public readonly record struct Sample(long FootprintBytes, int RegionCount, long GcHeapBytes, int ThreadCount);

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LogEvery = TimeSpan.FromHours(1);

    private readonly long _limitBytes;
    private readonly Timer _timer;
    private DateTime _lastLog = DateTime.MinValue;
    private long _nextWarnBytes;
    private int _overLimitStreak;
    private int _fired;

    /// <summary>Raised once, from a timer thread, after two consecutive samples over the limit.</summary>
    public event Action<Sample>? LimitExceeded;

    public MemoryWatchdog(int limitMb)
    {
        _limitBytes = limitMb > 0 ? limitMb * 1024L * 1024 : 0;
        _nextWarnBytes = _limitBytes > 0 ? _limitBytes / 2 : 1024L * 1024 * 1024;
        _timer = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(30), Interval);

    /// <summary>Two consecutive samples over a positive limit; a zero or negative limit disables it.</summary>
    public static bool ShouldRestart(int consecutiveOverLimit, long limitBytes) => limitBytes > 0 && consecutiveOverLimit >= 2;

    public static string Format(Sample s) =>
        $"memory: footprint {s.FootprintBytes / 1048576} MB, {s.RegionCount} VM regions, " +
        $"GC heap {s.GcHeapBytes / 1048576} MB, {s.ThreadCount} threads";

    private void Tick()
    {
        try
        {
            var s = Read();
            var now = DateTime.UtcNow;
            if (now - _lastLog >= LogEvery || s.FootprintBytes >= _nextWarnBytes)
            {
                AppLog.WriteLine(Format(s));
                _lastLog = now;
                // Re-warn each time growth adds another quarter of the limit (or 256 MB without one).
                while (s.FootprintBytes >= _nextWarnBytes)
                    _nextWarnBytes += _limitBytes > 0 ? _limitBytes / 4 : 256L * 1024 * 1024;
            }

            _overLimitStreak = _limitBytes > 0 && s.FootprintBytes > _limitBytes ? _overLimitStreak + 1 : 0;
            if (ShouldRestart(_overLimitStreak, _limitBytes) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                AppLog.WriteLine($"Memory limit {_limitBytes / 1048576} MB exceeded, relaunching. {Format(s)}");
                LimitExceeded?.Invoke(s);
            }
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"Memory watchdog sample failed: {ex.Message}");
        }
    }

    public static Sample Read()
    {
        long footprint;
        var regions = 0;
        if (!OperatingSystem.IsMacOS() || !TryReadMacVmInfo(out footprint, out regions))
        {
            using var p = Process.GetCurrentProcess();
            footprint = OperatingSystem.IsWindows() ? p.PrivateMemorySize64 : p.WorkingSet64;
        }
        using var self = Process.GetCurrentProcess();
        return new Sample(footprint, regions, GC.GetTotalMemory(false), self.Threads.Count);
    }

    // task_vm_info_data_t: phys_footprint is the 64-bit field at byte offset 144 and
    // region_count the 32-bit field at offset 8 (see <mach/task_info.h>).
    private const int TaskVmInfo = 22;
    private const int PhysFootprintOffset = 144;
    private const int RegionCountOffset = 8;
    private static uint? _taskSelf;

    private static bool TryReadMacVmInfo(out long footprint, out int regions)
    {
        footprint = 0;
        regions = 0;
        var buffer = new int[128];
        var count = (uint)buffer.Length;
        if (task_info(TaskSelf(), TaskVmInfo, buffer, ref count) != 0 || count * 4 < PhysFootprintOffset + 8)
            return false;
        var bytes = MemoryMarshal.AsBytes(buffer.AsSpan());
        footprint = BitConverter.ToInt64(bytes.Slice(PhysFootprintOffset, 8));
        regions = BitConverter.ToInt32(bytes.Slice(RegionCountOffset, 4));
        return footprint > 0;
    }

    // mach_task_self() is a macro over the libSystem global mach_task_self_; reading it
    // takes no new port reference (unlike the task_self_trap call).
    private static uint TaskSelf()
    {
        if (_taskSelf is { } port) return port;
        var lib = NativeLibrary.Load("/usr/lib/libSystem.dylib");
        port = (uint)Marshal.ReadInt32(NativeLibrary.GetExport(lib, "mach_task_self_"));
        _taskSelf = port;
        return port;
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int task_info(uint task, int flavor, [Out] int[] info, ref uint count);

    public void Dispose() => _timer.Dispose();
}
