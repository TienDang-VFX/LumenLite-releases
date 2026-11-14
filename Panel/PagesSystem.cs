using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace LumenLite.Panel;

/// <summary>Usage with history graphs, memory pressure, battery, uptime, alerts and the Island readout.</summary>
internal sealed class SystemPage : Page
{
    private readonly Meter _cpu = new("CPU", graph: true);
    private readonly Meter _gpu = new("GPU", graph: true);
    private readonly Meter _battery = new("Pin");
    private readonly Meter _memory = new(L.T("Áp lực RAM"), bar: false, graph: true);
    private readonly Row _uptime = Ui.Row("\uE823", "");
    private readonly Group _hardware;
    private readonly ContentControl _islandMetrics = new() { Focusable = false };
    private bool _hasBattery;

    public SystemPage(PanelWindow host) : base(host)
    {
        _hardware = Ui.Group(L.T("Mức sử dụng phần cứng"), _cpu, _gpu);
        Add(_hardware,
            Ui.Group(L.T("Bộ nhớ"), _memory, _uptime),
            Ui.Group(L.T("Cảnh báo"),
                Alert(L.T("CPU tải cao"), L.T("Trên 90% liên tục 1 phút"), Store.S.AlertCpu, on => Store.S.AlertCpu = on),
                Alert(L.T("Thiếu RAM"), L.T("Dùng trên 92% bộ nhớ"), Store.S.AlertMemory, on => Store.S.AlertMemory = on),
                Alert(L.T("Ổ C sắp đầy"), L.T("Còn dưới 10 GB hoặc dưới 5%"), Store.S.AlertDisk, on => Store.S.AlertDisk = on),
                Alert(L.T("Pin yếu"), L.T("Còn 20% và không cắm sạc"), Store.S.AlertBattery, on => Store.S.AlertBattery = on))
                .Hint(L.T("Cảnh báo hiện trên Dynamic Island và thông báo Windows, mỗi loại tối đa 30 phút một lần. Windows không cho đọc nhiệt độ hay tốc độ quạt nếu không cài driver riêng.")),
            Ui.Group(L.T("Chỉ số trên Dynamic Island"), _islandMetrics)
                .Hint(L.T("Hiện ngay trên viên Island lúc rảnh, giống chỉ số trên thanh menu của Mac.")));
        DrawIslandMetrics();

        WhileShown(() => { SystemMonitor.Sampled += Show; SystemMonitor.Watch(true); },
                   () => { SystemMonitor.Sampled -= Show; SystemMonitor.Watch(false); });
    }

    public override string Title => L.T("Hệ thống");

    public override void Shown()
    {
        if (SystemMonitor.Last is { } s)
            Show(s);
    }

    private static Row Alert(string title, string when, bool on, Action<bool> set) => Ui.Row(null, title, when, Ui.Switch(on, value =>
    {
        set(value);
        Store.SaveSoon();
    }));

    private void DrawIslandMetrics() =>
        _islandMetrics.Content = Ui.Segments(new[] { ("off", L.T("Tắt")), ("system", "CPU · RAM"), ("network", L.T("Mạng ↓↑")) }
            .Select(o => (o.Item2, Store.S.IslandMetrics == o.Item1, (Action)(() =>
            {
                Store.S.IslandMetrics = o.Item1;
                Store.SaveSoon();
                DrawIslandMetrics();
            }))).ToList());

    private void Show(SystemSample s)
    {
        var m = Ui.M;
        _cpu.Update(s.Cpu / 100, $"{s.Cpu:0}%", SystemMonitor.Cpu);
        _gpu.Update((s.Gpu ?? 0) / 100, s.Gpu is { } g ? $"{g:0}%" : "—", SystemMonitor.Gpu);
        if (s.Battery is { } battery)
        {
            // A desktop has no battery: its meter only joins the group once a reading says there is one.
            if (!_hasBattery)
            {
                _hasBattery = true;
                _hardware.Set(_cpu, _gpu, _battery);
            }
            _battery.Update(battery, $"{battery * 100:0}%" + (s.Charging ? " ⚡" : ""), fill: m.Green);
        }
        var percent = s.MemoryTotal == 0 ? 0 : 100.0 * s.MemoryUsed / s.MemoryTotal;
        var (label, color, soft) = percent switch
        {
            < 75 => (L.T("Bình thường"), m.Green, m.GreenSoft),
            < 90 => ("Cao", m.Amber, m.AmberSoft),
            _ => (L.T("Rất cao"), m.Red, m.RedSoft),
        };
        _memory.Badge(label, color, soft);
        _memory.Update(percent / 100, $"{s.MemoryUsed / 1073741824.0:0.0} GB / {s.MemoryTotal / 1073741824.0:0} GB", SystemMonitor.Memory);
        _uptime.Title = L.T("Máy đã chạy ") + SystemMonitor.Uptime();
    }
}

/// <summary>Live traffic, addresses and a speed test.</summary>
internal sealed class NetworkPage : Page
{
    private readonly Meter _down = new(L.T("Tải xuống"), bar: false, graph: true, fixedMax: false);
    private readonly Meter _up = new(L.T("Tải lên"), bar: false);
    private readonly TextBlock _ip = Ui.Value(""), _sessionDown = Ui.Value(""), _sessionUp = Ui.Value("");
    private readonly Readout _ping = new("\uE916", L.T("Độ trễ"));
    private readonly Readout _speedDown = new("\uE896", L.T("Xuống"));
    private readonly Readout _speedUp = new("\uE898", L.T("Lên"));
    // Stays visible: it says what the test costs before it runs, then how it went.
    private readonly TextBlock _speedStatus = Ui.Note(L.T("Đo qua speed.cloudflare.com, tốn khoảng 50–150 MB dữ liệu."));
    private readonly ActionButton _speedButton;
    private CancellationTokenSource? _speedCancel;

    public NetworkPage(PanelWindow host) : base(host)
    {
        _ping.Value = _speedDown.Value = _speedUp.Value = "—";
        _speedButton = Ui.Action(L.T("Bắt đầu đo"), RunSpeedTest, Tone.Primary, "\uE768", wide: true);
        Add(Ui.Group(null,
                _down,
                _up,
                Ui.Row(null, L.T("IP nội bộ"), control: _ip),
                Ui.Row(null, L.T("Đã tải xuống (từ khi mở app)"), control: _sessionDown),
                Ui.Row(null, L.T("Đã tải lên (từ khi mở app)"), control: _sessionUp)),
            Ui.Group(L.T("Đo tốc độ mạng"), Readout.Strip(_ping, _speedDown, _speedUp), _speedStatus, _speedButton));

        WhileShown(() => { SystemMonitor.Sampled += Show; SystemMonitor.Watch(true); },
                   () => { SystemMonitor.Sampled -= Show; SystemMonitor.Watch(false); });
    }

    public override string Title => L.T("Mạng");

    public override void Shown()
    {
        if (SystemMonitor.Last is { } s)
            Show(s);
    }

    private void Show(SystemSample s)
    {
        _down.Update(0, SystemMonitor.Speed(s.DownloadPerSecond), SystemMonitor.Download);
        _up.Update(0, SystemMonitor.Speed(s.UploadPerSecond));
        _ip.Text = s.LocalIp ?? L.T("Không có mạng");
        _sessionDown.Text = SystemMonitor.Size(SystemMonitor.SessionDown);
        _sessionUp.Text = SystemMonitor.Size(SystemMonitor.SessionUp);
    }

    private async void RunSpeedTest()
    {
        if (_speedCancel != null)
        {
            _speedCancel.Cancel();
            return;
        }
        _speedCancel = new CancellationTokenSource();
        SetButton("\uE71A", L.T("Dừng"));
        _ping.Value = _speedDown.Value = _speedUp.Value = "…";
        try
        {
            var progress = new Progress<string>(text => _speedStatus.Text = text);
            var result = await SpeedTest.RunAsync(progress, _speedCancel.Token);
            _ping.Value = $"{result.PingMs:0} ms";
            _speedDown.Value = $"{result.DownloadMbps:0}";
            _speedUp.Value = $"{result.UploadMbps:0}";
            _speedStatus.Text = L.F($"Tải xuống {result.DownloadMbps:0} Mbps, tải lên {result.UploadMbps:0} Mbps (đơn vị ô: Mbps). Đo lúc {DateTime.Now:HH:mm}.");
        }
        catch (OperationCanceledException)
        {
            _speedStatus.Text = L.T("Đã dừng.");
            _ping.Value = _speedDown.Value = _speedUp.Value = "—";
        }
        catch (Exception ex)
        {
            _speedStatus.Text = L.T("Không đo được: ") + ex.Message;
            _ping.Value = _speedDown.Value = _speedUp.Value = "—";
        }
        finally
        {
            _speedCancel = null;
            SetButton("\uE768", L.T("Đo lại"));
        }
    }

    private void SetButton(string glyph, string text)
    {
        _speedButton.Glyph = glyph;
        _speedButton.Text = text;
    }
}

/// <summary>Drives with their free space, plus the cleaner and messaging downloads.</summary>
internal sealed class StoragePage : Page
{
    private readonly Group _drives = Ui.Group(null);
    private readonly TextBlock _bin = Ui.Value("");

    public StoragePage(PanelWindow host) : base(host)
    {
        Add(_drives,
            Ui.Group(L.T("Dọn dẹp"),
                Ui.Row("\uE899", L.T("Dọn dẹp"), L.T("File tạm, cache trình duyệt, cache đồ họa và báo lỗi"), Ui.Nav(), () => Host.Push(new CleanerPage(Host))),
                Ui.Row("\uE8BD", L.T("File từ app nhắn tin"), L.T("Zalo, Telegram, Messenger… gom ảnh và file nhận được qua thời gian"), Ui.Nav(), () => Host.Push(new MessagingPage(Host))),
                Ui.Row("\uE74D", L.T("Thùng rác"), control: _bin, onClick: () =>
                {
                    QuickActions.EmptyRecycleBin(IntPtr.Zero);
                    Shown();
                })));
    }

    public override string Title => L.T("Lưu trữ");

    public override void Shown()
    {
        var m = Ui.M;
        _drives.Set(DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable).Select(drive =>
        {
            var fraction = (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize;
            var name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? L.T("Ổ đĩa") : drive.VolumeLabel;
            var meter = new Meter($"{name} ({drive.Name.TrimEnd('\\')})");
            meter.Update(fraction, L.F($"còn {SystemMonitor.Size(drive.AvailableFreeSpace)} / {SystemMonitor.Size(drive.TotalSize)}"),
                fill: fraction > 0.9 ? m.Red : fraction > 0.75 ? m.Amber : null);
            return (UIElement)meter;
        }).ToArray());
        var (count, bytes) = QuickActions.RecycleBin();
        _bin.Text = count == 0 ? L.T("Trống") : L.F($"{count} mục · {SystemMonitor.Size(bytes)} · bấm để dọn");
    }
}
