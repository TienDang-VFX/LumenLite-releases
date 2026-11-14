using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace LumenLite.Panel;

/// <summary>What to delete for good; each category says what it is, so nothing is ticked blind.</summary>
internal sealed class CleanerPage : Page
{
    private readonly Group _list = Ui.Group(null);
    private readonly ActionButton _cleanButton;
    private readonly HashSet<string> _selected = new() { "temp", "browsers", "crash", "shaders" };
    private List<CleanCategory> _categories = Cleaner.Categories();
    private bool _busy;

    public CleanerPage(PanelWindow host) : base(host)
    {
        _cleanButton = Ui.Action(L.T("Dọn"), Clean, Tone.Primary, "\uE899", wide: true);
        _cleanButton.ToolTip = Ui.Tip(L.T("File đang được dùng sẽ được bỏ qua. Trình duyệt đang mở có thể giữ lại một phần cache."));
        Add(_list,
            Ui.Bar(_cleanButton),
            Ui.Group(L.T("Tự động"), Ui.Row(null, L.T("Tự dọn mỗi tuần"), L.T("File tạm của bạn và báo lỗi, chạy nền khi mở LumenLite"), Ui.Switch(Store.S.CleanerAuto, on =>
            {
                Store.S.CleanerAuto = on;
                Store.SaveSoon();
                Cleaner.RunScheduled();
            }))));
    }

    public override string Title => L.T("Dọn dẹp");

    public override void Shown() => Scan();

    private async void Scan()
    {
        if (_busy) return;
        _busy = true;
        _list.Set(Ui.Note(L.T("Đang quét…")));
        SetButton(L.T("Đang quét…"), false);
        _categories = Cleaner.Categories();
        await Task.Run(() => Cleaner.Measure(_categories));
        _busy = false;
        Draw();
    }

    private void Draw()
    {
        var m = Ui.M;
        _list.Set(_categories.Select(category =>
        {
            var id = category.Id;
            var size = Ui.Value(category.Files == 0 ? L.T("Sạch") : SystemMonitor.Size(category.Bytes), category.Files == 0 ? m.Secondary : m.Text);
            var pick = Ui.Switch(_selected.Contains(id), on =>
            {
                if (on) _selected.Add(id); else _selected.Remove(id);
                UpdateTotal();
            });
            // The description stays in view: it is what will be deleted.
            return (UIElement)Ui.Row(category.Glyph, category.Title, control: Ui.Trail(size, pick)).Detail(category.Description);
        }).ToArray());
        UpdateTotal();
    }

    private void UpdateTotal()
    {
        var total = _categories.Where(c => _selected.Contains(c.Id)).Sum(c => c.Bytes);
        SetButton(total == 0 ? L.T("Không có gì để dọn") : L.F($"Dọn {SystemMonitor.Size(total)}"), total > 0);
    }

    private void SetButton(string text, bool enabled)
    {
        _cleanButton.Text = text;
        _cleanButton.IsEnabled = enabled;
    }

    private void Clean()
    {
        var chosen = _categories.Where(c => _selected.Contains(c.Id) && c.Files > 0).ToList();
        var total = chosen.Sum(c => c.Bytes);
        Host.Confirm(L.F($"Xóa {SystemMonitor.Size(total)}?"), L.T("Các file này bị xóa hẳn, không vào Thùng rác: ") + string.Join(", ", chosen.Select(c => c.Title.ToLowerInvariant())) + ".", L.T("Xóa"), async () =>
        {
            SetButton(L.T("Đang dọn…"), false);
            var freed = await Task.Run(() => Cleaner.Clean(chosen));
            Host.Toast(L.F($"Đã giải phóng {SystemMonitor.Size(freed)}."));
            Scan();
        });
    }
}

internal sealed class MessagingPage : Page
{
    private readonly ContentControl _ages = new() { Focusable = false };
    private readonly StackPanel _list = new();
    private int _days = 90;

    public MessagingPage(PanelWindow host) : base(host)
    {
        Add(Ui.Group(L.T("Chọn file cũ hơn"), _ages), _list);
        // Stays visible: it is what makes the button below safe to press.
        var note = Ui.Note(L.T("File được chuyển vào Thùng rác, nên vẫn khôi phục được."));
        note.Margin = new Thickness(4, 0, 4, 4);
        Add(note);
    }

    public override string Title => L.T("File từ app nhắn tin");

    public override void Shown() => Load();

    private string Age => _days == 365 ? L.T("1 năm") : _days + L.T(" ngày");

    private async void Load()
    {
        _ages.Content = Ui.Segments(new[] { 30, 90, 180, 365 }.Select(days => (days == 365 ? L.T("1 năm") : L.F($"{days} ngày"), days == _days, (Action)(() =>
        {
            _days = days;
            Load();
        }))).ToList());
        _list.Children.Clear();
        _list.Children.Add(Ui.Group(null, Ui.Note(L.T("Đang quét…"))));
        var folders = await Task.Run(() => Cleaner.MessagingFolders(_days));
        _list.Children.Clear();
        if (folders.Count == 0)
            _list.Children.Add(Ui.Group(null, Ui.Note(L.T("Không thấy thư mục nhận file của Zalo, Telegram, Messenger, Discord hay Viber."))));
        foreach (var folder in folders)
        {
            var recycle = Ui.Action(L.T("Bỏ file cũ"), () => Host.Confirm(L.F($"Chuyển {folder.OldFiles} file vào Thùng rác?"),
                L.F($"{SystemMonitor.Size(folder.OldBytes)} file {folder.App} cũ hơn {Age} trong {folder.Path}."), L.T("Chuyển"), async () =>
                {
                    var moved = await Task.Run(() => Cleaner.RecycleOld(folder.Path, _days));
                    Host.Toast(L.F($"Đã chuyển {moved} file vào Thùng rác."));
                    Load();
                }));
            recycle.IsEnabled = folder.OldFiles > 0;
            _list.Children.Add(Ui.Group(folder.App,
                Ui.Row("\uE838", L.T("Mở thư mục"), folder.Path, Ui.Nav(), () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder.Path}\"") { UseShellExecute = true })),
                Ui.Row("\uE74D", L.F($"Cũ hơn {Age}"), control: Ui.Trail(Ui.Value(folder.OldFiles == 0 ? L.T("Không có") : $"{SystemMonitor.Size(folder.OldBytes)} · {folder.OldFiles} file"), recycle)))
                .Trailing(Ui.Value($"{SystemMonitor.Size(folder.Bytes)} · {folder.Files} file")));
        }
    }
}

/// <summary>Open apps to quit, plus updates, the uninstaller and ports.</summary>
internal sealed class AppsPage : Page
{
    private readonly Group _apps;

    public AppsPage(PanelWindow host) : base(host)
    {
        _apps = Ui.Group(Title);
        Add(_apps,
            Ui.Bar(Ui.Action(L.T("Thoát tất cả"), () => QuitMany(exceptFront: false), glyph: "\uE711", wide: true),
                   Ui.Action(L.T("Trừ app đang dùng"), () => QuitMany(exceptFront: true), glyph: "\uE8BB", wide: true)),
            Ui.Group(L.T("Quản lý app"),
                Ui.Row("\uE895", L.T("Cập nhật app"), L.T("Tìm bản mới cho app đã cài qua winget, cập nhật từng app hoặc tất cả"), Ui.Nav(), () => Host.Push(new UpdatesPage(Host))),
                Ui.Row("\uE74D", L.T("Gỡ cài đặt"), L.T("Gỡ app rồi dọn thư mục nó để lại trong AppData và ProgramData"), Ui.Nav(), () => Host.Push(new UninstallPage(Host))),
                Ui.Row("\uE968", L.T("Cổng mạng"), L.T("Xem tiến trình nào đang mở port và dừng nó"), Ui.Nav(), () => Host.Push(new PortsPage(Host)))));
    }

    public override string Title => L.T("Ứng dụng đang mở");

    public override void Shown() => Load();

    private List<RunningApp> _running = new();

    private void Load()
    {
        _running = AppManager.Running();
        if (_running.Count == 0)
        {
            _apps.Set(Ui.Note(L.T("Không có app nào đang mở cửa sổ.")));
            return;
        }
        _apps.Set(_running.Select(app =>
        {
            var front = app.Pid == Host.PreviousForegroundPid;
            return (UIElement)Ui.PictureRow(AppInfo.Of(app.Pid).Icon, front ? app.Name + L.T("  · đang dùng") : app.Name, control: Ui.Action(L.T("Thoát"), () =>
            {
                AppManager.Quit(app);
                Host.Toast(L.F($"Đã yêu cầu {app.Name} đóng."));
                Delay(Load);
            })).Current(front);
        }).ToArray());
    }

    private void QuitMany(bool exceptFront)
    {
        var targets = _running.Where(a => !exceptFront || a.Pid != Host.PreviousForegroundPid).ToList();
        if (targets.Count == 0) return;
        Host.Confirm(exceptFront ? L.F($"Thoát {targets.Count} app, giữ app đang dùng?") : L.F($"Thoát {targets.Count} app?"),
            L.T("Mỗi app được yêu cầu đóng như khi bấm dấu X, nên app nào có việc chưa lưu sẽ hỏi bạn trước."), L.T("Thoát"), () =>
            {
                foreach (var app in targets)
                    AppManager.Quit(app);
                Delay(Load);
            });
    }

    private static async void Delay(Action action)
    {
        await Task.Delay(1200);
        action();
    }
}

internal sealed class UpdatesPage : Page
{
    private readonly Group _list = Ui.Group(null);
    private readonly ActionButton _all;

    public UpdatesPage(PanelWindow host) : base(host)
    {
        _all = Ui.Action(L.T("Cập nhật tất cả"), () => Host.Confirm(L.T("Cập nhật tất cả?"), L.T("winget sẽ tải và cài từng bản mới trong một cửa sổ riêng. Một số app cần bạn bấm đồng ý quyền admin."), L.T("Cập nhật"), () => AppManager.Upgrade(null), destructive: false),
            Tone.Primary, "\uE895", wide: true);
        Add(_list, Ui.Bar(_all, Ui.Action(L.T("Kiểm tra lại"), Load, glyph: "\uE72C", wide: true)));
    }

    public override string Title => L.T("Cập nhật app");

    public override void Shown() => Load();

    private async void Load()
    {
        _list.Title = null;
        _all.IsEnabled = false;
        if (!AppManager.WingetAvailable)
        {
            _list.Set(Ui.Note(L.T("Máy chưa có winget. Cài \"App Installer\" từ Microsoft Store để dùng mục này.")));
            return;
        }
        _list.Set(Ui.Note(L.T("Đang hỏi winget (có thể mất 10–30 giây)…")));
        List<UpgradeItem> items;
        try
        {
            items = await AppManager.UpgradesAsync();
        }
        catch (Exception ex)
        {
            _list.Set(Ui.Note(L.T("Không kiểm tra được: ") + ex.Message));
            return;
        }
        if (items.Count == 0)
        {
            _list.Set(Ui.Note(L.T("Mọi app winget biết đều đã mới nhất.")));
            return;
        }
        _list.Title = L.F($"{items.Count} app có bản mới");
        // The versions stay in view: they are what the button will change.
        _list.Set(items.Select(item => (UIElement)Ui.Row(null, item.Name, control: Ui.Action(L.T("Cập nhật"), () => AppManager.Upgrade(item.Id), Tone.Primary))
            .Detail($"{item.Version}  →  {item.Available}")).ToArray());
        _all.IsEnabled = true;
    }
}

internal sealed class UninstallPage : Page
{
    private readonly Group _list = Ui.Group(null);
    private readonly TextBlock _count = Ui.Note("");
    private readonly TextBox _search;
    private List<InstalledApp> _apps = new();

    public UninstallPage(PanelWindow host) : base(host)
    {
        var (box, input) = Ui.Search(L.T("Tìm app đã cài…"));
        _search = input;
        _search.TextChanged += (_, _) => Draw();
        _count.Margin = new Thickness(4, 0, 4, 4);
        Add(box, _list, _count);
    }

    public override string Title => L.T("Gỡ cài đặt");

    public override async void Shown()
    {
        _count.Text = "";
        _list.Set(Ui.Note(L.T("Đang đọc danh sách app…")));
        _apps = await Task.Run(AppManager.Installed);
        Draw();
        _search.Focus();
    }

    private void Draw()
    {
        var query = _search.Text.Trim();
        var matches = _apps.Where(a => query.Length == 0 || a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (a.Publisher?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)).ToList();
        _list.Set(matches.Take(60).Select(Row).ToArray());
        _list.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _count.Text = matches.Count > 60 ? L.F($"Còn {matches.Count - 60} app nữa, gõ tên để tìm.") : matches.Count == 0 ? L.T("Không có app nào khớp.") : $"{matches.Count} app";
    }

    private UIElement Row(InstalledApp app)
    {
        var icon = app.Icon is { Length: > 0 } path && File.Exists(path) ? AppInfo.FileIcon(path) : null;
        // Publisher, version and size tell two apps of one name apart: in the tooltip, with the size also in view.
        var details = string.Join(" · ", new[] { app.Publisher, app.Version, app.SizeBytes > 0 ? SystemMonitor.Size(app.SizeBytes) : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var remove = Ui.Action(L.T("Gỡ"), () => Uninstall(app), Tone.Destructive);
        return Ui.PictureRow(icon, app.Name, details, app.SizeBytes > 0 ? Ui.Trail(Ui.Value(SystemMonitor.Size(app.SizeBytes)), remove) : remove);
    }

    private void Uninstall(InstalledApp app)
    {
        Host.Confirm(L.F($"Gỡ {app.Name}?"), L.T("Trình gỡ của chính app sẽ mở ra. Gỡ xong, LumenLite tìm thư mục nó để lại và hỏi bạn có muốn bỏ vào Thùng rác không."), L.T("Gỡ"), async () =>
        {
            Process? process;
            try
            {
                process = AppManager.StartUninstall(app);
            }
            catch (Exception ex)
            {
                Host.Toast(L.T("Không mở được trình gỡ: ") + ex.Message);
                return;
            }
            if (process == null) return;
            await process.WaitForExitAsync();
            await Task.Delay(1500);
            _apps = await Task.Run(AppManager.Installed);
            Draw();
            if (_apps.Any(a => a.Key == app.Key && a.Name == app.Name))
                return; // still installed: cancelled or failed
            var leftovers = await Task.Run(() => AppManager.Leftovers(app));
            if (leftovers.Count == 0)
            {
                Host.Toast(L.F($"Đã gỡ {app.Name}, không còn thư mục sót lại."));
                return;
            }
            Host.ShowPanel();
            Host.Confirm(L.F($"Dọn {leftovers.Count} thư mục còn sót?"), string.Join("\n", leftovers), L.T("Bỏ vào Thùng rác"), () =>
            {
                var removed = 0;
                foreach (var folder in leftovers)
                {
                    try
                    {
                        Cleaner.RecycleFolder(folder);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Store.Log("leftover remove failed: " + ex.Message);
                    }
                }
                Host.Toast(L.F($"Đã chuyển {removed} thư mục vào Thùng rác."));
            });
        });
    }
}

internal sealed class PortsPage : Page
{
    private readonly Group _list = Ui.Group(null);

    public PortsPage(PanelWindow host) : base(host)
    {
        Add(_list, Ui.Bar(Ui.Action(L.T("Làm mới"), Load, glyph: "\uE72C", wide: true)));
    }

    public override string Title => L.T("Cổng mạng đang mở");

    public override void Shown() => Load();

    private void Load()
    {
        List<ListeningPort> ports;
        try
        {
            ports = AppManager.Ports();
        }
        catch (Exception ex)
        {
            _list.Set(Ui.Note(L.T("Không đọc được: ") + ex.Message));
            return;
        }
        if (ports.Count == 0)
        {
            _list.Set(Ui.Note(L.T("Không có cổng nào đang lắng nghe.")));
            return;
        }
        _list.Set(ports.GroupBy(p => (p.Port, p.Pid)).Select(group =>
        {
            var port = group.First();
            var stop = Ui.Action(L.T("Dừng"), () => Host.Confirm(L.F($"Dừng {port.Process}?"), L.F($"Tiến trình PID {port.Pid} đang mở cổng {port.Port} sẽ bị buộc tắt ngay, việc chưa lưu sẽ mất."), L.T("Dừng"), () =>
            {
                try
                {
                    Process.GetProcessById(port.Pid).Kill(entireProcessTree: true);
                    Host.Toast(L.F($"Đã dừng {port.Process}."));
                }
                catch (Exception ex)
                {
                    Host.Toast(L.T("Không dừng được (có thể cần quyền admin): ") + ex.Message);
                }
                Load();
            }), Tone.Destructive);
            stop.IsEnabled = port.Pid > 4;
            var number = Ui.Mono(port.Port.ToString(), 13);
            number.Width = 46;
            return (UIElement)Ui.LeadRow(number, port.Process, $"{string.Join(", ", group.Select(p => p.Protocol).Distinct())} · {port.Address} · PID {port.Pid}", stop);
        }).ToArray());
    }
}
