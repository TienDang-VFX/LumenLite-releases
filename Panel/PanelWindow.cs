using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LumenLite.Panel;

/// <summary>A screen of the panel. Pages are kept and reused; Shown/Hidden start and stop their timers.</summary>
internal abstract class Page : StackPanel
{
    protected Page(PanelWindow host)
    {
        Host = host;
    }

    protected PanelWindow Host { get; }

    /// <summary>Heading next to the back arrow for sub-pages; the first group's label on a tab.</summary>
    public abstract string Title { get; }

    public virtual void Shown() { }

    public virtual void Hidden() { }

    /// <summary>Shows the part of this page a route names ("network" inside the System tab). Most pages have one part.</summary>
    public virtual void Go(string route) { }

    private readonly List<(Action Subscribe, Action Unsubscribe)> _live = new();
    private bool _subscribed;

    /// <summary>
    /// Hooks a static event only while the page is shown, so a closed panel is not
    /// kept alive by SystemMonitor and the like.
    /// </summary>
    protected void WhileShown(Action subscribe, Action unsubscribe) => _live.Add((subscribe, unsubscribe));

    protected void Add(params UIElement[] elements)
    {
        foreach (var element in elements)
            Children.Add(element);
    }

    internal virtual void Attach()
    {
        if (_subscribed) return;
        _subscribed = true;
        foreach (var (subscribe, _) in _live) subscribe();
    }

    internal virtual void Detach()
    {
        if (!_subscribed) return;
        _subscribed = false;
        foreach (var (_, unsubscribe) in _live) unsubscribe();
    }
}

/// <summary>Several pages under one tab, one after the other: sound then devices.</summary>
internal sealed class Joined : Page
{
    private readonly (string Route, Page Page)[] _parts;

    public Joined(PanelWindow host, params (string Route, Page Page)[] parts) : base(host)
    {
        _parts = parts;
        foreach (var (_, page) in parts)
            Children.Add(page);
    }

    public override string Title => _parts[0].Page.Title;

    public override void Shown()
    {
        foreach (var (_, page) in _parts) page.Shown();
    }

    public override void Hidden()
    {
        foreach (var (_, page) in _parts) page.Hidden();
    }

    /// <summary>Scrolls to the part the route names, once it has been laid out.</summary>
    public override void Go(string route)
    {
        var part = _parts.FirstOrDefault(p => p.Route == route).Page;
        if (part != null && part != _parts[0].Page)
            Dispatcher.BeginInvoke(() => part.BringIntoView(new Rect(0, 0, 1, 520)), DispatcherPriority.Loaded);
    }

    internal override void Attach()
    {
        foreach (var (_, page) in _parts) page.Attach();
    }

    internal override void Detach()
    {
        foreach (var (_, page) in _parts) page.Detach();
    }
}

/// <summary>Several pages under one tab, one at a time, with a segmented control at the top to switch.</summary>
internal sealed class Switched : Page
{
    private readonly (string Route, Page Page)[] _parts;
    private readonly ContentControl _segments = new() { Margin = new Thickness(0, 6, 0, 12), Focusable = false };
    private readonly ContentControl _view = new() { Focusable = false };
    private int _index;
    private bool _shown;

    public Switched(PanelWindow host, params (string Route, Page Page)[] parts) : base(host)
    {
        _parts = parts;
        Add(_segments, _view);
        Select(0);
    }

    private Page Current => _parts[_index].Page;

    public override string Title => Current.Title;

    public override void Shown()
    {
        _shown = true;
        Current.Shown();
    }

    public override void Hidden()
    {
        _shown = false;
        Current.Hidden();
    }

    public override void Go(string route)
    {
        var index = Array.FindIndex(_parts, p => p.Route == route);
        if (index >= 0 && index != _index)
            Select(index);
    }

    internal override void Attach() => Current.Attach();

    internal override void Detach() => Current.Detach();

    private void Select(int index)
    {
        if (_shown)
        {
            Current.Hidden();
            Current.Detach();
        }
        _index = index;
        _segments.Content = Ui.Views(_parts.Select((p, i) => (p.Page.Title, i == index, (Action)(() => { if (i != _index) Select(i); }))).ToList());
        _view.Content = Current;
        if (_shown)
        {
            Current.Attach();
            Current.Shown();
        }
    }
}

/// <summary>
/// LumenLite's control panel: one header line (the wordmark, or the back arrow on a sub-page, with Settings and
/// Quit at the right), scrolling groups of rows, and a row of tab icons along the bottom. It opens above the tray
/// and hides when it loses focus.
/// </summary>
internal sealed class PanelWindow : Window
{
    private const double PanelWidth = 400;
    /// <summary>Distance from the screen's edges, as Windows' own flyouts keep.</summary>
    private const double EdgeGap = 12;

    /// <summary>The tab numbers of the nine-tab panel, still accepted by "--tab N".</summary>
    private static readonly string[] LegacyTabs = { "sound", "devices", "system", "network", "storage", "apps", "agents", "tools", "toggles" };

    private readonly Material _m;
    private readonly Border _sheet;
    private readonly Grid _layers = new();
    private readonly UniformGrid _tabs = new() { Rows = 1 };
    private readonly ContentControl _content = new() { Focusable = false };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _wordmark;
    private readonly StackPanel _subHeader = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(-7, 0, 0, 0) };
    private readonly TextBlock _subTitle = Ui.Text("", 14, true);
    private readonly Grid _overlay = new() { Visibility = Visibility.Collapsed };

    /// <summary>A tab's page is only built the first time the tab is opened.</summary>
    private sealed class Tab
    {
        public required Func<Page> Create;
        public Page? Page;
        public required TextBlock Icon;
        public required Border Mark;
        public Page Get() => Page ??= Create();
    }

    private readonly List<Tab> _tabPages = new();
    /// <summary>Every name a screen can be opened by, and the tab it lives on.</summary>
    private readonly Dictionary<string, int> _routes = new();
    private readonly Stack<Page> _stack = new();
    private Page? _current;
    // Shared by every panel instance: a hidden panel is discarded and a new one built on the next open.
    private static DateTime _hiddenAt;
    private readonly DispatcherTimer _outsideClick = new() { Interval = TimeSpan.FromMilliseconds(120) };

    /// <summary>A mouse button is held with the pointer outside the panel.</summary>
    private bool MouseDownOutside()
    {
        if ((GetAsyncKeyState(0x01) & 0x8000) == 0 && (GetAsyncKeyState(0x02) & 0x8000) == 0 && (GetAsyncKeyState(0x04) & 0x8000) == 0)
            return false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetCursorPos(out var cursor) || !Native.GetWindowRect(hwnd, out var rect))
            return false;
        return cursor.X < rect.Left || cursor.X >= rect.Right || cursor.Y < rect.Top || cursor.Y >= rect.Bottom;
    }

    /// <summary>Alt (Alt+Tab), the Windows key or Esc: the user moved on with the keyboard.</summary>
    private static bool SwitchKeyDown() =>
        (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    public Action? OpenSettings { get; set; }

    /// <summary>Builds the panel again in the theme just chosen.</summary>
    public Action? Retheme { get; set; }

    /// <summary>Raised when the panel hides; the app then discards it to free its memory.</summary>
    public event Action? Dismissed;

    /// <summary>The app that was in front before the panel opened ("all but the current app").</summary>
    public int PreviousForegroundPid { get; private set; }
    public Action? Quit { get; set; }

    /// <summary>Whether Windows draws a blurred backdrop behind this panel (the snapshot tool wants to know).</summary>
    public bool OnGlass { get; }

    public PanelWindow()
    {
        Title = "LumenLite";
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = PanelWidth;
        SizeToContent = SizeToContent.Height;
        FontFamily = Theme.Font;
        UseLayoutRounding = true;
        OnGlass = Backdrop.Apply(this, Palette.Current.IsDark);
        _m = Material.Current;
        Foreground = _m.Text;
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr((style | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW));

        // One line: who this is (or where you are), and the two things that are not a tab.
        _wordmark = Ui.Mono("LUMENLITE", 11, _m.Text);
        _wordmark.FontWeight = FontWeights.SemiBold;
        _wordmark.Margin = new Thickness(4, 0, 0, 0);
        _subHeader.Children.Add(Ui.Icon("\uE72B", Back, size: 13));
        _subHeader.Children.Add(_subTitle);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -5, 0) };
        actions.Children.Add(Ui.Icon("\uE713", () => OpenSettings?.Invoke(), L.T("Cài đặt"), color: _m.Secondary));
        actions.Children.Add(Ui.Icon("\uE7E8", () => Confirm(L.T("Thoát LumenLite?"), L.T("Mọi widget, Dynamic Island và phím tắt sẽ tắt cho tới lần mở sau."), L.T("Thoát"), () => Quit?.Invoke()), L.T("Thoát"), color: _m.Secondary));
        var header = new Grid { Height = 30, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(_wordmark);
        header.Children.Add(_subHeader);
        header.Children.Add(actions);

        _scroll = new ScrollViewer
        {
            Content = _content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 640,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false,
        };

        // The panel sits on the bottom edge and grows upwards, so the tab bar lives at the
        // bottom: switching tabs then never moves it from under the mouse.
        var tabBar = new Border
        {
            BorderBrush = _m.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 5, 0, 0),
            Margin = new Thickness(0, 2, 0, 0),
            Child = _tabs,
        };

        var body = new StackPanel { Margin = new Thickness(12, 9, 12, 6) };
        body.Children.Add(header);
        body.Children.Add(_scroll);
        body.Children.Add(tabBar);

        _layers.Children.Add(body);
        _layers.Children.Add(_overlay);
        _sheet = _m.Frame(_layers);
        Content = _sheet;

        // Closes like a flyout: when the user clicks outside it or switches away with the
        // keyboard. Losing focus alone is not enough: Windows sometimes hands the focus back
        // to the previous window right after the panel opens, with nobody touching anything.
        Deactivated += (_, _) =>
        {
            if (_overlay.Visibility == Visibility.Visible || Mouse.Captured != null)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsActive && !HasOpenPopup() && (MouseDownOutside() || SwitchKeyDown()))
                    HidePanel();
            }, DispatcherPriority.Background);
        };
        // Clicks elsewhere also close it while it is not the active window (focus was taken away).
        _outsideClick.Tick += (_, _) =>
        {
            if (IsVisible && !IsActive && !HasOpenPopup() && _overlay.Visibility != Visibility.Visible && MouseDownOutside())
                HidePanel();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _outsideClick.Start();
            else _outsideClick.Stop();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (_overlay.Visibility == Visibility.Visible)
                    _overlay.Visibility = Visibility.Collapsed;
                else if (_stack.Count > 0)
                    Back();
                else
                    HidePanel();
                e.Handled = true;
            }
        };
        // After a size change the native window catches up a moment later; place it then.
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(Place, DispatcherPriority.Render);
    }

    /// <summary>
    /// Adds a tab; the first one added is shown first. It opens by its <paramref name="route"/>, and by each
    /// of <paramref name="parts"/>, which the page is then asked to bring up (<see cref="Page.Go"/>).
    /// </summary>
    public void AddTab(string route, string glyph, string title, Func<Page> create, params string[] parts)
    {
        var icon = Ui.Glyph(glyph, 16, _m.Secondary);
        icon.Margin = new Thickness(0, 9, 0, 11);
        // The selected tab: its icon in ink, over a short orange bar.
        var mark = new Border { Width = 14, Height = 2, Background = _m.Accent, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Hidden };
        var cell = new Grid();
        cell.Children.Add(icon);
        cell.Children.Add(mark);
        var index = _tabPages.Count;
        var button = _m.Pressable(cell, () => SelectTab(index), new Thickness(0));
        button.ToolTip = Ui.Tip(title);
        button.Margin = new Thickness(1, 0, 1, 0);
        _tabs.Children.Add(button);
        _tabs.Columns = _tabs.Children.Count;
        _tabPages.Add(new Tab { Create = create, Icon = icon, Mark = mark });
        _routes[route] = index;
        foreach (var part in parts)
            _routes[part] = index;
    }

    /// <summary>
    /// The route a "--tab" argument means: a name ("network"), or a tab number of the old nine-tab panel.
    /// Null when it is neither.
    /// </summary>
    public static string? RouteOf(string? argument)
    {
        argument = argument?.Trim().ToLowerInvariant() ?? "";
        if (int.TryParse(argument, out var number))
            return number >= 0 && number < LegacyTabs.Length ? LegacyTabs[number] : null;
        return LegacyTabs.Contains(argument) ? argument : null;
    }

    private void SelectTab(int index, string? route = null)
    {
        _stack.Clear();
        for (var i = 0; i < _tabPages.Count; i++)
        {
            var tab = _tabPages[i];
            var selected = i == index;
            tab.Icon.Foreground = selected ? _m.Text : _m.Secondary;
            tab.Mark.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        }
        var page = _tabPages[index].Get();
        if (route != null)
            page.Go(route);
        Show(page, sub: false);
    }

    /// <summary>Opens a sub-page with a back arrow.</summary>
    public void Push(Page page)
    {
        if (_current != null)
            _stack.Push(_current);
        Show(page, sub: true);
    }

    private void Back()
    {
        if (_stack.Count == 0)
            return;
        var previous = _stack.Pop();
        Show(previous, sub: _stack.Count > 0 || !_tabPages.Exists(t => t.Page == previous));
    }

    private void Show(Page page, bool sub)
    {
        if (_current != null)
        {
            _current.Hidden();
            _current.Detach();
        }
        _current = page;
        page.Attach();
        _subHeader.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
        _wordmark.Visibility = sub ? Visibility.Collapsed : Visibility.Visible;
        _subTitle.Text = page.Title;
        _content.Content = page;
        _scroll.ScrollToTop();
        page.Shown();
        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
    }

    /// <summary>Scrolls the current page to its end (the snapshot tool uses it to see the bottom of a long page).</summary>
    public void ScrollToEnd() => _scroll.ScrollToEnd();

    /// <summary>An in-panel confirmation sheet; a MessageBox would close the panel behind it.</summary>
    public void Confirm(string title, string message, string action, Action onConfirm, bool destructive = true)
    {
        _overlay.Children.Clear();
        _overlay.Background = _m.Scrim;
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(title, 16, true));
        var text = Ui.Note(message);
        text.FontSize = 13;
        text.Margin = new Thickness(0, 6, 0, 16);
        stack.Children.Add(text);
        var ok = Ui.Action(action, () =>
        {
            _overlay.Visibility = Visibility.Collapsed;
            onConfirm();
        }, destructive ? Tone.Destructive : Tone.Primary, wide: true);
        var buttons = Ui.Bar(Ui.Action(L.T("Hủy"), () => _overlay.Visibility = Visibility.Collapsed, wide: true), ok);
        buttons.Margin = new Thickness(0);
        stack.Children.Add(buttons);
        _overlay.Children.Add(new Border
        {
            // Opaque, whatever the sheet is made of: a question should not have the desktop showing through it.
            Background = _m.Menu,
            BorderBrush = _m.MenuRim,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        });
        _overlay.Visibility = Visibility.Visible;
    }

    /// <summary>Shows a short note at the bottom of the panel.</summary>
    public void Toast(string text)
    {
        var toast = new Border
        {
            Background = _m.Ink,
            CornerRadius = new CornerRadius(_m.ControlRadius),
            Padding = new Thickness(14, 8, 14, 9),
            Margin = new Thickness(30, 0, 30, 62),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = Ui.Note(text, _m.OnInk),
            IsHitTestVisible = false,
        };
        _layers.Children.Add(toast);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _layers.Children.Remove(toast);
        };
        timer.Start();
    }

    public void Toggle()
    {
        if (IsVisible)
            HidePanel();
        // A click on the tray icon first takes focus away (hiding the panel); don't reopen it at once.
        else if (DateTime.UtcNow - _hiddenAt > TimeSpan.FromMilliseconds(300))
            ShowPanel();
    }

    /// <summary>
    /// Opens the panel. <paramref name="route"/> names the screen: a tab ("sound", "system", "apps", "agents",
    /// "tools") or a part of one ("devices", "network", "storage", "toggles"). Without it the panel opens where
    /// it was.
    /// </summary>
    public void ShowPanel(string? route = null)
    {
        if (_tabPages.Count == 0)
            return;
        if (!IsVisible)
            PreviousForegroundPid = AppManager.ForegroundPid();
        if (route != null && _routes.TryGetValue(route, out var tab))
            SelectTab(tab, route);
        else if (_current == null)
            SelectTab(0);
        else
        {
            _current.Attach();
            _current.Shown();
        }
        Backdrop.ShowCloaked(this, Place);
        Activate();
        // On glass the backdrop cannot fade with the content: a fade there would show an empty sheet first.
        if (!OnGlass)
            _layers.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    public void HidePanel()
    {
        if (!IsVisible)
            return;
        _current?.Hidden();
        _current?.Detach();
        _overlay.Visibility = Visibility.Collapsed;
        _hiddenAt = DateTime.UtcNow;
        Hide();
        _stack.Clear();
        Dismissed?.Invoke();
    }

    /// <summary>Bottom-right of the main screen's work area, above the tray, in physical pixels.</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return;
        // Size from WPF's own layout, scaled to pixels: GetWindowRect can still report the old size.
        var width = (int)Math.Ceiling(ActualWidth * target.TransformToDevice.M11);
        var height = (int)Math.Ceiling(ActualHeight * target.TransformToDevice.M22);
        var gap = (int)Math.Round(EdgeGap * target.TransformToDevice.M11);
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        Native.SetWindowPos(hwnd, new IntPtr(-1), area.Right - width - gap, Math.Max(area.Top, area.Bottom - height - gap), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>A drop-down list is open. (A tooltip is a popup too, and does not count.)</summary>
    private static bool HasOpenPopup()
    {
        foreach (PresentationSource source in PresentationSource.CurrentSources)
        {
            if (source.RootVisual?.GetType().Name != "PopupRoot")
                continue;
            if ((source.RootVisual as FrameworkElement)?.Parent is not Popup popup)
                return true;
            if (popup.IsOpen && popup.Child is not System.Windows.Controls.ToolTip)
                return true;
        }
        return false;
    }
}
