using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;

namespace LumenLite;

/// <summary>
/// One shared sampler for the panel, the Dynamic Island readout and the alerts.
/// Samples every second on a background thread and raises Sampled on the UI thread.
/// </summary>
internal static class SystemMonitor
{
    public const int HistoryLength = 60;

    // Opened on the first sample, on a background thread: the performance counters (GPU engines
    // especially) take a moment to set up and must not hold up start-up.
    private static SystemStats? _stats;
    private static SystemStats Stats => _stats ??= new SystemStats();
    private static Timer? _timer;
    private static Dispatcher? _dispatcher;
    private static int _busy;

    public static SystemSample? Last { get; private set; }
    public static readonly Queue<double> Cpu = new(), Gpu = new(), Memory = new(), Download = new(), Upload = new();
    public static long SessionDown { get; private set; }
    public static long SessionUp { get; private set; }

    public static event Action<SystemSample>? Sampled;

    /// <summary>Raised with an icon and a message when a threshold is crossed.</summary>
    public static event Action<string, string>? Alert;

    // Alert bookkeeping
    private static int _cpuHighSeconds;
    private static readonly Dictionary<string, DateTime> LastAlert = new();

    private const int FastInterval = 1000, IdleInterval = 5000;
    private static int _watchers;
    private static int _interval = IdleInterval;

    /// <summary>
    /// Something on screen shows live numbers (a panel page, the Island readout):
    /// sample every second, GPU included. Otherwise every 5 s, enough for alerts.
    /// </summary>
    public static void Watch(bool on)
    {
        _watchers = Math.Max(0, _watchers + (on ? 1 : -1));
        Retime();
    }

    public static bool IslandWatching
    {
        set
        {
            if (value == _islandWatching) return;
            _islandWatching = value;
            Watch(value);
        }
    }

    private static bool _islandWatching;

    private static void Retime()
    {
        var interval = _watchers > 0 ? FastInterval : IdleInterval;
        if (interval == _interval) return;
        _interval = interval;
        _timer?.Change(interval == FastInterval ? 0 : interval, interval);
    }

    public static void Start()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        // The first sample waits for Windows to finish starting; the performance counters are opened then.
        _timer ??= new Timer(_ => Tick(), null, 20000, IdleInterval);
    }

    private static void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            SystemSample sample;
            lock (Stats)
                sample = Stats.Sample(gpu: _watchers > 0);
            _dispatcher?.BeginInvoke(() => Publish(sample));
        }
        catch (Exception ex)
        {
            Store.Log("monitor sample failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static void Publish(SystemSample s)
    {
        Last = s;
        Push(Cpu, s.Cpu);
        Push(Gpu, s.Gpu ?? 0);
        Push(Memory, s.MemoryTotal == 0 ? 0 : 100.0 * s.MemoryUsed / s.MemoryTotal);
        Push(Download, s.DownloadPerSecond);
        Push(Upload, s.UploadPerSecond);
        SessionDown += (long)s.DownloadPerSecond;
        SessionUp += (long)s.UploadPerSecond;
        CheckAlerts(s);
        Sampled?.Invoke(s);
    }

    private static void Push(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > HistoryLength)
            queue.Dequeue();
    }

    private static void CheckAlerts(SystemSample s)
    {
        var step = _interval / 1000;
        var before = _cpuHighSeconds;
        _cpuHighSeconds = s.Cpu >= 90 ? _cpuHighSeconds + step : 0;
        if (Store.S.AlertCpu && before < 60 && _cpuHighSeconds >= 60)
            Raise("cpu", "", L.T("CPU đã chạy trên 90% suốt 1 phút"));

        var memory = s.MemoryTotal == 0 ? 0 : 100.0 * s.MemoryUsed / s.MemoryTotal;
        if (Store.S.AlertMemory && memory >= 92)
            Raise("memory", "", L.F($"RAM gần đầy: {memory:0}% đang dùng"));

        if (Store.S.AlertDisk && s.DiskFree is { } free && s.DiskTotal is { } total && total > 0
            && (free < 10L * 1024 * 1024 * 1024 || free < total / 20))
            Raise("disk", "", L.F($"Ổ C sắp đầy: còn {free / 1073741824.0:0.0} GB"));

        if (Store.S.AlertBattery && s.Battery is { } battery && !s.Charging && battery <= 0.2)
            Raise("battery", "", L.F($"Pin yếu: còn {battery * 100:0}%"));
    }

    /// <summary>Each kind of alert shows at most once every 30 minutes.</summary>
    private static void Raise(string kind, string glyph, string text)
    {
        if (LastAlert.TryGetValue(kind, out var when) && DateTime.UtcNow - when < TimeSpan.FromMinutes(30))
            return;
        LastAlert[kind] = DateTime.UtcNow;
        Alert?.Invoke(glyph, text);
    }

    public static string Uptime()
    {
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return up.TotalDays >= 1 ? L.F($"{(int)up.TotalDays} ngày {up.Hours} giờ") : L.F($"{up.Hours} giờ {up.Minutes} phút");
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1048576 => $"{bytesPerSecond / 1048576:0.0} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:0} KB/s",
        _ => $"{bytesPerSecond:0} B/s",
    };

    public static string Size(double bytes) => bytes switch
    {
        >= 1099511627776 => $"{bytes / 1099511627776:0.0} TB",
        >= 1073741824 => $"{bytes / 1073741824:0.0} GB",
        >= 1048576 => $"{bytes / 1048576:0} MB",
        >= 1024 => $"{bytes / 1024:0} KB",
        _ => $"{bytes:0} B",
    };
}
