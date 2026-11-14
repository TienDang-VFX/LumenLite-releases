using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LumenLite.Panel;
using LumenLite.Widgets;

namespace LumenLite;

public sealed class App : Application
{
    private readonly List<WidgetWindow> _widgets = new();
    private Tray? _tray;
    private IslandWindow? _island;
    private Background? _background;
    private PanelWindow? _panel;
    private CommandBar? _commandBar;
    private Scratchpad? _scratchpad;

    private const string ShowSignal = "LumenLite.ShowPanel";
    private const string TabFile = "open-tab.txt";

    /// <summary>
    /// Starting LumenLite again while it runs opens the panel. "--tab NAME" picks the screen: sound, devices, system,
    /// network, storage, apps, agents, tools or toggles (the numbers 0-8 of the old nine-tab panel still work).
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // A copy started by an update waits for the one it replaces to exit.
        Updater.WaitForPrevious(args);
        // "--notes-sync owner/repo": written down first, for whichever copy ends up running.
        var notesRequest = NotesSync.Request.Save(args);
        // Started inside a terminal's or an agent's job, LumenLite would go when that tool closes: run outside it.
        if (Snapshot.Folder(args) == null && Detach.Relaunched(args))
            return;
        using var mutex = new Mutex(true, "LumenLite.SingleInstance", out var first);
        if (!first)
        {
            try
            {
                var tab = Array.IndexOf(args, "--tab") is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : "";
                System.IO.Directory.CreateDirectory(Store.Folder);
                if (!notesRequest)
                    System.IO.File.WriteAllText(System.IO.Path.Combine(Store.Folder, TabFile), tab);
                // Lets the running copy bring its panel to the front (Windows only allows that when asked).
                AllowSetForegroundWindow(-1);
                if (EventWaitHandle.TryOpenExisting(ShowSignal, out var signal))
                    signal.Set();
            }
            catch
            {
            }
            return;
        }
        if (Snapshot.Folder(args) != null)
            Palette.Override = Snapshot.Theme(args);
        Backdrop.Off = Array.IndexOf(args, "--no-glass") >= 0;
        new App { _snapshot = Snapshot.Folder(args) }.Run();
    }

    private string? _snapshot;

    /// <summary>Saves every screen as a PNG in the snapshot folder, then quits.</summary>
    private async void TakeSnapshots(string folder)
    {
        try
        {
            await Snapshot.Settle(1500);
            // Each tab, then the parts of a tab that a route opens: the other System views, and the lower half of
            // the two joined tabs. The panel is thrown away whenever it loses focus: take whichever copy is current.
            var routes = new[] { "sound", "devices", "system", "network", "storage", "apps", "agents", "tools", "toggles" };
            for (var i = 0; i < routes.Length; i++)
            {
                Panel.ShowPanel(routes[i]);
                await Snapshot.Settle();
                Snapshot.Save(Panel, folder, $"panel-{i + 1}-{routes[i]}", Panel.OnGlass);
            }
            Panel.ShowPanel("sound");
            Panel.OpenSettings?.Invoke();
            await Snapshot.Settle();
            Snapshot.Save(Panel, folder, "panel-settings", Panel.OnGlass);
            Panel.ScrollToEnd();
            await Snapshot.Settle(300);
            Snapshot.Save(Panel, folder, "panel-settings-end", Panel.OnGlass);
            Panel.ShowPanel("storage");
            Panel.Push(new CleanerPage(Panel));
            await Snapshot.Settle(9000);
            Snapshot.Save(Panel, folder, "panel-cleaner", Panel.OnGlass);
            Panel.Confirm(L.T("Thoát LumenLite?"), L.T("Mọi widget, Dynamic Island và phím tắt sẽ tắt cho tới lần mở sau."), L.T("Thoát"), () => { });
            await Snapshot.Settle(300);
            Snapshot.Save(Panel, folder, "panel-confirm", Panel.OnGlass);
            Panel.HidePanel();
            foreach (var (mode, name) in new[] { ("idle", "island-idle"), ("expanded", "island-expanded"), ("tools", "island-tools"), ("agents", "island-agents"), ("calendar", "island-calendar"), ("message", "island-message") })
            {
                _island!.Enabled = true;
                _island.SnapshotMode(mode);
                await Snapshot.Settle();
                Snapshot.Save(_island, folder, name);
            }
            foreach (var widget in _widgets)
            {
                widget.Show();
                await Snapshot.Settle();
                Snapshot.Save(widget, folder, "widget-" + widget.Id);
            }
            Bar.Open();
            await Snapshot.Settle();
            Snapshot.Save(Bar, folder, "commandbar", Bar.OnGlass);
            Bar.Hide();
            Pad.Open();
            await Snapshot.Settle();
            Snapshot.Save(Pad, folder, "scratchpad", Pad.OnGlass);
            OpenRadial(toggle: false);
            await Snapshot.Settle();
            if (_radial != null)
                Snapshot.Save(_radial, folder, "radial");
        }
        catch (Exception ex)
        {
            Store.Log("snapshot failed: " + ex);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "error.txt"), ex.ToString());
        }
        Shutdown();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private void ListenForSecondStart()
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
        var thread = new Thread(() =>
        {
            while (signal.WaitOne())
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // Started again with --notes-sync: set the sync up, without opening the panel.
                    if (NotesSync.Request.Pending)
                    {
                        NotesSync.Request.RunPending();
                        return;
                    }
                    string? route = null;
                    try
                    {
                        var path = System.IO.Path.Combine(Store.Folder, TabFile);
                        if (System.IO.File.Exists(path))
                            route = PanelWindow.RouteOf(System.IO.File.ReadAllText(path));
                    }
                    catch
                    {
                    }
                    // No tab given: behave like clicking the tray icon (open, or close when open).
                    if (route == null)
                        Panel.Toggle();
                    else
                        Panel.ShowPanel(route);
                });
            }
        }) { IsBackground = true, Name = "LumenLite second start" };
        thread.Start();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;
        Store.Load();
        L.Init(Store.S.Language);
        // Drawing on the GPU keeps the animations smooth: on the CPU every frame of a transparent window, its shadows
        // included, is drawn and copied pixel by pixel. Software drawing saves the driver's per-process memory (tens
        // of MB on NVIDIA) and stays available as a setting for machines short on memory.
        if (Store.S.SoftwareRendering)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Store.Log($"start {Updater.CurrentText} pid {Environment.ProcessId} from {Environment.ProcessPath}");
        // Whatever ends LumenLite leaves a line: an exception no handler caught, or Windows ending the session.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Store.Log("crash: " + ev.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Store.Log("unobserved task error: " + ev.Exception.GetBaseException().Message);
            ev.SetObserved();
        };
        SessionEnding += (_, ev) => Store.Log("Windows session ending: " + ev.ReasonSessionEnding);
        Autostart.Migrate();
        SystemMonitor.Start();
        AgentMonitor.Start();
        // The Notes widget is gone; its text moves into a Scratchpad tab.
        if (!string.IsNullOrWhiteSpace(Store.S.Notes))
        {
            Store.S.Pads.Add(new ScratchPad { Title = L.T("Ghi chú cũ"), Text = Store.S.Notes });
            Store.S.Notes = "";
            Store.SaveSoon();
        }
        // The weekly clean-up waits a few minutes rather than adding disk work to the sign-in.
        var cleanLater = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        cleanLater.Tick += (_, _) =>
        {
            cleanLater.Stop();
            Cleaner.RunScheduled();
        };
        cleanLater.Start();

        // Default layout: a column on the right of the primary screen, notes beside it.
        var area = SystemParameters.WorkArea;
        var x = area.Right - 380;
        _widgets.Add(new WidgetWindow("launcher", "Launcher", new LauncherView(), new Size(340, 210), new Point(x, area.Top + 344), resizable: true));

        foreach (var widget in _widgets)
        {
            if (Store.Widget(widget.Id).Visible)
                widget.Show();
        }

        _island = new IslandWindow();
        _island.AlwaysShow = Store.S.IslandAlwaysShow;
        // The island names a screen ("sound", "devices", "agents"…); the panel finds the tab it lives on.
        _island.OpenPanel = route => Panel.ShowPanel(route);
        _island.OpenCommandBar = () => Bar.Open();
        AgentOpen.Unknown += text => _island?.Notify("", text, 4);
        _island.OpenRadial = OpenRadial;
        _island.OpenNotes = () => Pad.Open();
        StickyNotes.OpenInScratchpad = pad => Pad.Open(pad);
        StickyNotes.Restore();
        NotesSync.Start();
        NotesSync.Request.RunPending();
        _island.Enabled = Store.S.Island;
        _background = new Background(_island);
        // The panel, Command Bar and Scratchpad are built the first time they are opened.
        _background.OpenPanel = () => Panel.Toggle();
        _background.OpenCommandBar = () => Bar.Open();
        _background.OpenRadial = OpenRadial;
        _middle = new MiddleHold
        {
            Open = () => OpenRadial(toggle: false),
            Release = () => _radial?.ReleaseSelect(),
            Enabled = Store.S.RadialMiddleHold,
        };
        _tray = new Tray(_widgets, _island, _background, Quit, () => Panel.Toggle());
        Memory.Start();
        Updater.Start();
        Updater.Ready += version =>
        {
            var text = L.F($"Đã tải LumenLite {version.Major}.{version.Minor}.{version.Build}: khởi động lại để dùng (Cài đặt hoặc Command Bar)");
            _island?.Notify("\uE777", text, 8);
            _tray?.Balloon(text);
        };
        ListenForSecondStart();
        if (_snapshot != null)
            TakeSnapshots(_snapshot);
        AgentMonitor.Finished += session =>
        {
            if (Store.S.AgentAlerts)
                _island.Notify("\uE73E", $"{AgentMonitor.Name(session.Provider)} xong: {session.Title}", 5);
        };
        SystemMonitor.Alert += (glyph, text) =>
        {
            _island.Notify(glyph, text, 5);
            _tray?.Balloon(text);
        };
    }

    private PanelWindow Panel => _panel ??= BuildPanel(_background!, _island!);
    private CommandBar Bar => _commandBar ??= BuildCommandBar();

    /// <summary>A hidden panel is thrown away so its pages, icons and render buffers are freed.</summary>
    private void DiscardPanel(PanelWindow panel)
    {
        if (_panel != panel) return;
        _panel = null;
        panel.Close();
        Memory.TrimSoon();
    }

    private CommandBar BuildCommandBar()
    {
        var bar = new CommandBar(Commands);
        bar.IsVisibleChanged += (_, _) =>
        {
            if (bar.IsVisible || _commandBar != bar) return;
            _commandBar = null;
            bar.Dispatcher.BeginInvoke(bar.Close);
            Memory.TrimSoon();
        };
        return bar;
    }
    /// <summary>The Scratchpad is kept between uses; one built for another theme is replaced while it is hidden.</summary>
    private Scratchpad Pad
    {
        get
        {
            if (_scratchpad is { IsVisible: false } old && old.BuiltFor != Palette.Current)
            {
                old.Discard();
                _scratchpad = null;
            }
            return _scratchpad ??= new Scratchpad();
        }
    }

    /// <summary>
    /// The ring: the AI Agents page at the top, then each RustDesk machine, with
    /// the Island's own marks (the spark; a PC or a Mac).
    /// </summary>
    private IReadOnlyList<RadialAction> RadialItems()
    {
        var items = new List<RadialAction>
        {
            new("agents", "✦", "AI Agents", () => _island?.ShowAgents(), Theme.Text, Symbol: true),
        };
        if (RustDesk.Available)
        {
            foreach (var peer in RustDesk.List().Take(7))
            {
                var id = peer.Id;
                var mac = peer.Platform.Contains("Mac", StringComparison.OrdinalIgnoreCase);
                var name = peer.Host.Length > 0 ? peer.Host : peer.Id;
                items.Add(new("rustdesk:" + id, mac ? "\uE7F8" : "\uE7F4", peer.Showing ? name + L.T(" · đang xem") : name, () =>
                {
                    // Pressed again while that screen is in front: back to the desktop.
                    var minimized = RustDesk.Toggle(id);
                    _island?.Notify(mac ? "\uE7F8" : "\uE7F4", minimized ? "\u0110\u00E3 thu nh\u1ECF RustDesk" : "RustDesk: " + name);
                }, Theme.Text, Active: peer.Showing));
            }
        }
        return items;
    }

    private RadialMenu? _radial;
    private MiddleHold? _middle;

    /// <summary>The shortcut toggles the ring; holding the middle button only ever opens it.</summary>
    private void OpenRadial() => OpenRadial(toggle: true);

    private void OpenRadial(bool toggle)
    {
        if (_radial != null)
        {
            if (toggle) _radial.Close();
            return;
        }
        _radial = new RadialMenu(RadialItems());
        _radial.Closed += (_, _) =>
        {
            _radial = null;
            Memory.TrimSoon();
        };
        _radial.Open();
    }

    private PanelWindow BuildPanel(Background background, IslandWindow island)
    {
        var panel = new PanelWindow();
        // Five tabs. Each opens by its own name, and by the names of the screens that were folded into it.
        panel.AddTab("sound", "\uE9E9", L.T("Âm thanh"), () => new Joined(panel, ("sound", new SoundPage(panel)), ("devices", new DevicesPage(panel))), "devices");
        panel.AddTab("system", "\uE9D9", L.T("Hệ thống"), () => new Switched(panel, ("system", new SystemPage(panel)), ("network", new NetworkPage(panel)), ("storage", new StoragePage(panel))), "network", "storage");
        panel.AddTab("apps", "\uE71D", L.T("Ứng dụng"), () => new AppsPage(panel));
        panel.AddTab("agents", "\uE99A", "AI agent", () => new AgentsPage(panel));
        panel.AddTab("tools", "\uE90F", L.T("Tiện ích"), () => new Joined(panel,
            ("tools", new ToolsPage(panel, () => Bar.Open(), () => Pad.Open(), island, background, () => panel.Push(new RadialPage(panel, RadialItems, background.RadialHotkey, on =>
            {
                Store.S.RadialMiddleHold = on;
                Store.SaveSoon();
                if (_middle != null) _middle.Enabled = on;
            })))),
            ("toggles", new TogglesPage(panel))), "toggles");
        panel.OpenSettings = () => panel.Push(new SettingsPage(panel, _widgets, background));
        // The panel takes its colours when it is built: a new theme shows by building it again, back on Settings.
        panel.Retheme = () =>
        {
            panel.HidePanel();
            Dispatcher.BeginInvoke(() =>
            {
                Panel.ShowPanel();
                Panel.OpenSettings?.Invoke();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        panel.Quit = Quit;
        // Closing waits a moment, so a click on the tray icon that hid the panel can still be told apart from a fresh open.
        panel.Dismissed += () => panel.Dispatcher.BeginInvoke(() => DiscardPanel(panel), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        return panel;
    }

    /// <summary>Everything LumenLite can do, for the Command Bar.</summary>
    private IEnumerable<CommandItem> Commands()
    {
        var panel = Panel;
        CommandItem Item(string title, string subtitle, string glyph, Action run) => new(title, subtitle, L.T("Lệnh"), glyph, run);
        CommandItem Settings(string title, string uri, string glyph) => new(title, L.T("Cài đặt Windows"), "Windows", glyph, () => CommandBar.Launch(uri));
        yield return Item(L.T("Mở bảng điều khiển LumenLite"), L.T("Âm thanh, thiết bị, hệ thống, mạng, ứng dụng…"), "\uF0E2", () => panel.ShowPanel());
        yield return Item("Scratchpad", L.T("Ghi chú nhanh nhiều tab"), "\uE70B", () => Pad.Open());
        yield return Item(L.T("Đồng bộ ghi chú với GitHub"), NotesSync.Active ? NotesSync.Status : L.T("Thiết lập repo và token"), "", () =>
        {
            if (NotesSync.Active) _ = NotesSync.SyncAsync();
            else { panel.ShowPanel(); panel.Push(new NotesSyncPage(panel)); }
        });
        yield return Item(L.T("Ghi chú mới trên màn hình"), L.T("Một ghi chú dán, kéo đi đâu cũng được rồi ghim lại"), "\uE7C4", NewStickyNote);
        yield return Item(L.T("UniKey, màn hình, độ sáng"), L.T("Đổi kiểu gõ, bật/tắt màn hình, chỉnh độ sáng"), "", () => panel.ShowPanel("devices"));
        yield return Item(L.T("Trộn âm lượng"), L.T("Âm lượng và đầu ra riêng từng app"), "\uE9E9", () => panel.ShowPanel("sound"));
        yield return Item(L.T("Khóa máy"), "Win + L", "\uE72E", QuickActions.Lock);
        yield return Item(L.T("Tắt màn hình"), L.T("Rê chuột hoặc bấm phím để bật lại"), "\uE7F4", () => QuickActions.ScreensOff(IntPtr.Zero));
        yield return Item(L.T("Dọn thùng rác"), L.T("Windows hỏi lại trước khi xóa"), "\uE74D", () => QuickActions.EmptyRecycleBin(IntPtr.Zero));
        yield return Item(L.T("Ẩn/hiện icon desktop"), "", "\uE8FC", QuickActions.ToggleDesktopIcons);
        yield return Item(L.T("Đổi giao diện sáng/tối"), QuickActions.LightTheme() ? L.T("Đang sáng") : L.T("Đang tối"), "\uE793", () => QuickActions.SetLightTheme(!QuickActions.LightTheme()));
        yield return Item(L.T("Tắt/bật micro"), "Ctrl+Alt+M", "\uE720", () => _background!.ToggleMic());
        yield return Item(L.T("Chuyển loa/tai nghe"), "Ctrl+Alt+O", "\uE7F5", () => _background!.NextOutput());
        yield return Item("AI agent", L.T("Token và phiên Claude Code"), "\uE99A", () => panel.ShowPanel("agents"));
        yield return Item(L.T("Hệ thống"), L.T("CPU, GPU, RAM, cảnh báo"), "\uE9D9", () => panel.ShowPanel("system"));
        yield return Item(L.T("Đo tốc độ mạng"), L.T("Ping, tải xuống, tải lên"), "\uE774", () => panel.ShowPanel("network"));
        yield return Item(L.T("Dọn dẹp ổ đĩa"), L.T("File tạm, cache trình duyệt, báo lỗi"), "\uE899", () => { panel.ShowPanel("storage"); panel.Push(new CleanerPage(panel)); });
        yield return Item(L.T("Dọn file app nhắn tin"), "Zalo, Telegram, Messenger…", "\uE8BD", () => { panel.ShowPanel("storage"); panel.Push(new MessagingPage(panel)); });
        yield return Item(L.T("Thoát app"), L.T("Thoát một app, tất cả, hoặc tất cả trừ app đang dùng"), "\uE711", () => panel.ShowPanel("apps"));
        yield return Item(L.T("Cập nhật app"), L.T("Qua winget"), "\uE895", () => { panel.ShowPanel("apps"); panel.Push(new UpdatesPage(panel)); });
        yield return Item(L.T("Gỡ cài đặt app"), L.T("Kèm dọn thư mục còn sót"), "\uE74D", () => { panel.ShowPanel("apps"); panel.Push(new UninstallPage(panel)); });
        yield return Item(L.T("Cổng mạng đang mở"), L.T("Port và tiến trình"), "\uE968", () => { panel.ShowPanel("apps"); panel.Push(new PortsPage(panel)); });
        yield return Updater.Status == Updater.State.Ready
            ? Item(L.T("Khởi động lại để cập nhật LumenLite"), Updater.Message, "\uE777", () => Updater.Restart(Quit))
            : Item(L.T("Kiểm tra cập nhật LumenLite"), L.F($"Đang dùng {Updater.CurrentText}"), "\uE777", () => _ = Updater.CheckAsync());
        yield return Item(L.T("Lau phím (Cleaning Mode)"), L.T("Khóa bàn phím để lau"), "\uE765", CleaningMode.Start);
        yield return Item(L.T("Chỉnh sửa bố cục widget"), L.T("Kéo để di chuyển, Esc để xong"), "\uE70F", () => WidgetWindow.SetEditing(true));
        yield return Settings("Bluetooth", "ms-settings:bluetooth", "\uE702");
        yield return Settings("Wi-Fi", "ms-settings:network-wifi", "\uE701");
        yield return Settings(L.T("Âm thanh (Windows)"), "ms-settings:sound", "\uE767");
        yield return Settings(L.T("Màn hình (Windows)"), "ms-settings:display", "\uE7F4");
        yield return Settings(L.T("Cài đặt Windows"), "ms-settings:", "\uE713");
        yield return Settings(L.T("Ứng dụng đã cài (Windows)"), "ms-settings:appsfeatures", "\uE71D");
        foreach (var peer in RustDesk.List().Take(6))
        {
            var id = peer.Id;
            yield return new CommandItem("RustDesk: " + (peer.Host.Length > 0 ? peer.Host : peer.Id), $"{peer.Id} · {peer.User} · {peer.Platform}" + (peer.Showing ? L.T(" · đang xem") : ""), "RustDesk", "\uE703", () => RustDesk.Toggle(id));
        }
        yield return new CommandItem("Task Manager", L.T("Quản lý tác vụ"), "Windows", "\uE9D9", () => CommandBar.Launch("taskmgr.exe"));
        yield return new CommandItem(L.T("Thư mục Downloads"), "", L.T("Thư mục"), "\uE896", () => CommandBar.Launch(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")));
    }

    private static void NewStickyNote()
    {
        var pad = new ScratchPad { Title = L.F($"Ghi chú {Store.S.Pads.Count + 1}") };
        Store.S.Pads.Add(pad);
        NotesSync.EnsureIds();
        StickyNotes.Show(pad, null);
        NotesSync.Soon();
    }

    /// <summary>Lets a widget's content scroll when the user makes it smaller than its lists.</summary>
    private static ScrollViewer Scrolling(FrameworkElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    private void Quit()
    {
        Store.Log("quit requested: " + new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name);
        Store.SaveNow();
        WidgetWindow.Exiting = true;
        _tray?.Dispose();
        _middle?.Dispose();
        _panel?.Close();
        _commandBar?.Close();
        _scratchpad?.Close();
        StickyNotes.CloseAll();
        CleaningMode.Stop();
        _background?.Dispose();
        _island?.Close();
        foreach (var widget in _widgets)
            widget.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Store.Log($"exit {e.ApplicationExitCode}");
        Store.SaveNow();
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A widget error is logged instead of taking every widget down with it.
        Store.Log("unhandled: " + e.Exception);
        e.Handled = true;
    }
}
