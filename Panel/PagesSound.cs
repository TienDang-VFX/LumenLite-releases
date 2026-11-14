using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LumenLite.Panel;

/// <summary>Volume mixer with per-app output, output devices and the microphone.</summary>
internal sealed class SoundPage : Page
{
    private readonly Group _apps, _outputs, _mic;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly Dictionary<int, (Fader Fader, TextBlock Mute)> _rows = new();
    private string _appsSignature = "", _outputSignature = "", _micSignature = "";
    private Fader? _micFader;
    private TextBlock? _micMute;

    public SoundPage(PanelWindow host) : base(host)
    {
        _apps = Ui.Group(Title);
        _outputs = Ui.Group(L.T("Đầu ra"));
        _mic = Ui.Group(L.T("Micro")).Trailing(Ui.Keys(L.T("Ctrl+Alt+M: tắt/bật")));
        Add(_apps, _outputs, _mic);
        _timer.Tick += (_, _) => Refresh();
    }

    public override string Title => L.T("Trộn âm lượng");

    public override void Shown()
    {
        _appsSignature = _outputSignature = _micSignature = "";
        Refresh();
        _timer.Start();
    }

    public override void Hidden() => _timer.Stop();

    private List<AudioOutput> _outputList = new();

    private void Refresh()
    {
        try
        {
            _outputList = Audio.Outputs().OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            RefreshApps();
            RefreshOutputs();
            RefreshMic();
        }
        catch (Exception ex)
        {
            Store.Log("sound page failed: " + ex.Message);
        }
    }

    private void RefreshApps()
    {
        var apps = Audio.Apps().OrderBy(a => a.IsSystem).ThenBy(a => a.Pid).ToList();
        var signature = string.Join("|", apps.Select(a => a.Pid)) + "#" + string.Join("|", _outputList.Select(o => o.Id))
            + "#" + string.Join("|", apps.Select(a => AudioPolicy.GetOutput(a.Pid)));
        if (signature != _appsSignature)
        {
            _appsSignature = signature;
            _rows.Clear();
            _apps.Set(apps.Count == 0
                ? new UIElement[] { Ui.Note(L.T("Chưa có app nào phát âm thanh.")) }
                : apps.Select(AppRow).ToArray());
        }
        foreach (var app in apps)
        {
            if (!_rows.TryGetValue(app.Pid, out var row) || row.Fader.IsDragging)
                continue;
            row.Fader.Value = app.Level;
            row.Fader.Label = app.Muted ? L.T("Tắt") : null;
            row.Mute.Text = MuteGlyph(app.Muted);
        }
    }

    private static string MuteGlyph(bool muted) => muted ? "\uE74F" : "\uE767";

    private UIElement AppRow(AppVolume app)
    {
        var m = Ui.M;
        var name = L.T("Âm thanh hệ thống");
        BitmapSource? icon = null;
        if (!app.IsSystem)
        {
            (name, icon) = AppInfo.Of(app.Pid);
            if (!string.IsNullOrWhiteSpace(app.DisplayName) && !app.DisplayName.StartsWith('@'))
                name = app.DisplayName;
        }

        // Where this app plays, when Windows lets it be chosen per app.
        UIElement? output = null;
        string? hint = null;
        if (!app.IsSystem && AudioPolicy.Available)
        {
            var pinned = AudioPolicy.GetOutput(app.Pid);
            var current = pinned == null ? L.T("Mặc định") : _outputList.FirstOrDefault(o => o.Id == pinned)?.Name ?? L.T("Mặc định");
            var choices = new List<(string, Action)> { (L.T("Mặc định (theo Windows)"), () => SetAppOutput(app.Pid, null)) };
            choices.AddRange(_outputList.Select(o => (o.Name, (Action)(() => SetAppOutput(app.Pid, o.Id)))));
            output = Ui.Choice(current, choices, 150);
            hint = L.T("Đổi đầu ra của một app có hiệu lực khi app phát lại (nhiều app cần tạm dừng rồi phát tiếp).");
        }

        var pid = app.Pid;
        var fader = Ui.Slider(app.Level, level => Audio.SetAppVolume(pid, level));
        var muteGlyph = Ui.Glyph(MuteGlyph(app.Muted), 14, m.Secondary);
        var muted = app.Muted;
        var mute = m.Pressable(muteGlyph, () =>
        {
            muted = !muted;
            Audio.SetAppMute(pid, muted);
            muteGlyph.Text = MuteGlyph(muted);
            fader.Label = muted ? L.T("Tắt") : null;
        }, new Thickness(8, 4, 4, 4));
        _rows[app.Pid] = (fader, muteGlyph);

        var row = icon != null ? Ui.PictureRow(icon, name, hint, output) : Ui.Row(app.IsSystem ? "\uE7F4" : "\uE768", name, hint, output);
        return row.Below(Ui.Line(fader, mute));
    }

    private void SetAppOutput(int pid, string? deviceId)
    {
        if (!AudioPolicy.SetOutput(pid, deviceId))
            Host.Toast(L.T("Windows không cho đổi đầu ra của app này."));
        _appsSignature = "";
        Refresh();
    }

    private void RefreshOutputs()
    {
        var current = Audio.DefaultId();
        var signature = current + string.Join("|", _outputList.Select(o => o.Id)) + Store.S.LowerOnUnplug;
        if (signature == _outputSignature)
            return;
        _outputSignature = signature;
        var rows = new List<UIElement>();
        foreach (var output in _outputList)
        {
            var selected = output.Id == current;
            var glyph = output.FormFactor is AudioFormFactor.Headphones or AudioFormFactor.Headset ? "\uE7F6" : output.FormFactor == AudioFormFactor.Display ? "\uE7F4" : "\uE7F5";
            rows.Add(Ui.Row(glyph, output.Name, control: selected ? Ui.Glyph("\uE73E", 12, Ui.M.Accent) : null, onClick: () =>
            {
                Audio.SetDefault(output.Id);
                _outputSignature = "";
                Refresh();
            }).Current(selected));
        }
        rows.Add(Ui.Row("\uE7F6", L.T("Giảm âm lượng khi rút tai nghe"), L.T("Hạ về 25% để loa ngoài không phát quá to"), Ui.Switch(Store.S.LowerOnUnplug, on =>
        {
            Store.S.LowerOnUnplug = on;
            Store.SaveSoon();
        })));
        rows.Add(Ui.Row("\uE765", L.T("Chuyển nhanh"), control: Ui.Keys("Ctrl+Alt+O")));
        _outputs.Set(rows.ToArray());
    }

    private void RefreshMic()
    {
        var inputs = Audio.Inputs().OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var current = Audio.DefaultInputId();
        var signature = current + string.Join("|", inputs.Select(i => i.Id)) + Store.S.PreferredMic;
        if (signature != _micSignature)
        {
            _micSignature = signature;
            if (inputs.Count == 0)
            {
                _mic.Set(Ui.Note(L.T("Không có micro nào đang cắm.")));
                _micFader = null;
                return;
            }
            var preferredName = Store.S.PreferredMic is { } p ? inputs.FirstOrDefault(i => i.Id == p)?.Name ?? L.T("(đang rút)") : L.T("Không");
            var choices = new List<(string, Action)> { (L.T("Không ưu tiên (theo Windows)"), () => SetPreferred(null)) };
            choices.AddRange(inputs.Select(i => (i.Name, (Action)(() => SetPreferred(i.Id)))));

            _micMute = Ui.Glyph("\uE720", 15);
            var mute = Ui.M.Pressable(_micMute, () =>
            {
                if (Audio.MicVolume() is { } v)
                    Audio.SetMicMute(!v.Muted);
                RefreshMicLevel();
            }, new Thickness(5, 4, 9, 4));
            mute.Margin = new Thickness(-5, 0, 0, 0);
            mute.ToolTip = Ui.Tip(L.T("Tắt/bật micro"));
            _micFader = Ui.Slider(0, Audio.SetMicVolume);
            _mic.Set(
                Ui.Row(null, L.T("Micro ưu tiên"), L.T("Tự dùng micro này mỗi khi nó được cắm"), Ui.Choice(preferredName, choices, 170)),
                Ui.Row(null, L.T("Đang dùng"), control: Ui.Value(inputs.FirstOrDefault(i => i.Id == current)?.Name ?? "—")),
                Ui.Line(mute, _micFader));
        }
        RefreshMicLevel();
    }

    private void RefreshMicLevel()
    {
        if (_micFader == null || Audio.MicVolume() is not { } mic)
            return;
        _micFader.Value = mic.Level;
        _micFader.Label = mic.Muted ? L.T("Tắt") : null;
        _micMute!.Text = mic.Muted ? "\uEC54" : "\uE720";
        _micMute.Foreground = mic.Muted ? Ui.M.Red : Ui.M.Text;
    }

    private void SetPreferred(string? id)
    {
        Store.S.PreferredMic = id;
        Store.SaveSoon();
        if (id != null)
            Audio.SetDefault(id);
        _micSignature = "";
        Refresh();
    }
}

/// <summary>UniKey's typing mode, turning monitors on or off, and monitor brightness.</summary>
internal sealed class DevicesPage : Page
{
    private readonly Group _uniKey, _displays, _brightness;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<Fader> _brightnessFaders = new();
    private string _displaySignature = "";
    private bool? _lastMode;
    private bool _uniKeyBusy, _displayBusy;

    public DevicesPage(PanelWindow host) : base(host)
    {
        _uniKey = Ui.Group(Title);
        _displays = Ui.Group(L.T("Màn hình"));
        _brightness = Ui.Group(L.T("Độ sáng"));
        Add(_uniKey, _displays, _brightness);
        _timer.Tick += (_, _) =>
        {
            RefreshUniKey();
            RefreshDisplays();
        };
        WhileShown(() => Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged,
                   () => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged);
    }

    public override string Title => L.T("Thiết bị");

    private void OnDisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _displaySignature = "";
        RefreshDisplays();
        RefreshBrightness();
    });

    public override void Shown()
    {
        _displaySignature = "";
        RefreshUniKey();
        RefreshDisplays();
        RefreshBrightness();
        _timer.Start();
    }

    public override void Hidden() => _timer.Stop();

    // ---------- UniKey ----------

    private void RefreshUniKey()
    {
        if (_uniKeyBusy) return;
        _uniKeyBusy = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var running = false;
            bool? vietnamese = null;
            try
            {
                running = UniKey.IsRunning;
                if (running) vietnamese = UniKey.Vietnamese();
            }
            catch (Exception ex)
            {
                Store.Log("unikey read failed: " + ex.Message);
            }
            Dispatcher.BeginInvoke(() =>
            {
                _uniKeyBusy = false;
                ShowUniKey(running, vietnamese);
            });
        });
    }

    private void ShowUniKey(bool running, bool? vietnamese)
    {
        if (vietnamese != null) _lastMode = vietnamese;
        var mode = running ? vietnamese ?? _lastMode : null;
        if (!running)
        {
            _uniKey.Set(Ui.Row("\uE765", L.T("UniKey chưa chạy"), L.T("Mở UniKey để đổi kiểu gõ ở đây."), Ui.Switch(false, _ => RefreshUniKey())));
            return;
        }
        var title = mode switch { true => L.T("UniKey: Tiếng Việt"), false => "UniKey: English", null => L.T("Kiểu gõ UniKey") };
        var description = mode == null
            ? L.T("Biểu tượng UniKey đang ở khay ẩn; bấm để đổi, LumenLite sẽ biết kiểu mới.")
            : L.T("Bật là gõ tiếng Việt, tắt là gõ English. Giống bấm biểu tượng UniKey.");
        _uniKey.Set(Ui.Row("\uE765", title, description, Ui.Switch(mode == true, _ => Toggle())));
    }

    private void Toggle()
    {
        if (_uniKeyBusy) return;
        _uniKeyBusy = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool? vietnamese = null;
            try
            {
                vietnamese = UniKey.Toggle();
            }
            catch (Exception ex)
            {
                Store.Log("unikey toggle failed: " + ex.Message);
            }
            Dispatcher.BeginInvoke(() =>
            {
                _uniKeyBusy = false;
                ShowUniKey(true, vietnamese);
            });
        });
    }

    // ---------- monitors ----------

    private void RefreshDisplays()
    {
        if (_displayBusy) return;
        List<DisplayTarget> targets;
        try
        {
            targets = Displays.Targets();
        }
        catch (Exception ex)
        {
            Store.Log("display list failed: " + ex.Message);
            return;
        }
        var signature = string.Join("|", targets.Select(t => $"{t.Adapter}:{t.TargetId}:{t.Name}:{t.Active}:{t.Primary}"));
        if (signature == _displaySignature) return;
        _displaySignature = signature;

        var rows = new List<UIElement>();
        var active = targets.Count(t => t.Active);
        foreach (var (target, label) in Numbered(targets, t => t.Name))
        {
            var state = !target.Active ? L.T("Đang tắt") : target.Primary ? L.T("Màn hình chính") : L.T("Đang bật");
            var last = target.Active && active == 1;
            rows.Add(Ui.Row("\uE7F4", label, last ? state + L.T(" · không thể tắt màn hình cuối cùng") : null,
                Ui.Trail(Ui.Value(state), Ui.Switch(target.Active, on => SetDisplay(target, on)))).Disabled(last));
        }
        if (targets.Count == 0)
            rows.Add(Ui.Note(L.T("Không tìm thấy màn hình.")));
        rows.Add(Ui.Row("\uE713", L.T("Cài đặt màn hình của Windows"), control: Ui.Nav(), onClick: () =>
        {
            Host.HidePanel();
            CommandBar.Launch("ms-settings:display");
        }));
        _displays.Set(rows.ToArray());
    }

    /// <summary>Two monitors of the same model are told apart by a number after the name.</summary>
    private static IEnumerable<(T Item, string Label)> Numbered<T>(IReadOnlyList<T> items, Func<T, string> name)
    {
        var counts = items.GroupBy(name).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();
        foreach (var item in items)
        {
            var label = name(item);
            if (counts[label] > 1)
            {
                seen[label] = seen.GetValueOrDefault(label) + 1;
                label += $" {seen[label]}";
            }
            yield return (item, label);
        }
    }

    private void SetDisplay(DisplayTarget target, bool on)
    {
        _displayBusy = true;
        var error = 0;
        try
        {
            error = Displays.SetActive(target, on);
        }
        catch (Exception ex)
        {
            Store.Log("display toggle failed: " + ex.Message);
            error = -1;
        }
        finally
        {
            _displayBusy = false;
        }
        if (error != 0)
            Host.Toast(L.F($"Windows không {(on ? L.T("bật") : L.T("tắt"))} được {target.Name} (mã {error}). Thử trong Cài đặt màn hình."));
        _displaySignature = "";
        RefreshDisplays();
    }

    // ---------- brightness ----------

    private void RefreshBrightness()
    {
        if (_brightnessFaders.Any(s => s.IsDragging)) return;
        _brightnessFaders.Clear();
        _brightness.Set(Ui.Note(L.T("Đang hỏi màn hình…")));
        Brightness.Read(monitors => Dispatcher.BeginInvoke(() => ShowBrightness(monitors)));
    }

    private void ShowBrightness(List<BrightnessMonitor> monitors)
    {
        _brightnessFaders.Clear();
        if (monitors.Count == 0)
        {
            _brightness.Set(Ui.Note(L.T("Không màn hình nào nhận lệnh độ sáng. Bật DDC/CI trong menu của màn hình rời; màn hình laptop chưa hỗ trợ.")));
            return;
        }
        var rows = new List<UIElement>();
        foreach (var (monitor, label) in Numbered(monitors, m => m.Name))
        {
            var key = monitor.Key;
            var fader = Ui.Slider(monitor.Level, level => Brightness.Set(key, level), 190);
            _brightnessFaders.Add(fader);
            rows.Add(Ui.Row("\uE706", label, control: fader));
        }
        _brightness.Set(rows.ToArray());
    }
}
