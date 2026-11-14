using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LumenLite;

/// <summary>
/// The Island's extra pages, laid out like Vorssaint's: a grid of tool tiles, and
/// an AI-agents page made of dark cards. Both open inside the expanded Island and
/// have a back arrow in the header.
/// </summary>
public sealed partial class IslandWindow
{
    private const string ViewTools = "tools", ViewAgents = "agents", ViewCalendar = "calendar", ViewRemote = "remote";

    // The display's tokens under the names these pages use. Amber and red are status only: a limit running low,
    // a session waiting for the person.
    private static readonly Brush TileBrush = Material.Display.Group;
    private static readonly Brush TileHover = Material.Display.GroupHover;
    private static readonly Brush TileBorder = Material.Display.Rim;
    private static readonly Brush Red = Material.Display.Red;
    private static readonly Brush Amber = Material.Display.Amber;
    private static readonly Brush TrackBrush = Material.Display.Track;
    private static readonly Brush Faint = Material.Display.Secondary;

    private string _view = "";
    private readonly UniformGrid _tools = new() { Columns = 4, Margin = new Thickness(-4, -2, -4, 0) };
    private readonly ScrollViewer _agents = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Height = 322 };
    private readonly StackPanel _agentsContent = new();
    private readonly StackPanel _remote = new();
    private int _remoteCount;
    // RustDesk machines on the main page, under the controls and under the player.
    private readonly UniformGrid _stripControls = new() { Rows = 1, Margin = new Thickness(-4, 8, -4, 0) };
    private readonly UniformGrid _stripPlayer = new() { Rows = 1, Margin = new Thickness(-4, 10, -4, 0) };
    private int _stripCount;
    private Border? _back;

    public Action? OpenCommandBar { get; set; }
    public Action? OpenRadial { get; set; }

    private bool IsCalendar => _view == ViewCalendar;

    /// <summary>Opens the Island on its AI Agents page, as the radial menu's spark does.</summary>
    public void ShowAgents() => ShowView(ViewAgents);

    /// <summary>Switches the open Island to a page ("" is the player or controls).</summary>
    private void ShowView(string view)
    {
        _view = view;
        if (view == ViewCalendar)
            _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        SetMode(Mode.Expanded);
        Animate(HeightProperty, ExpandedHeight());
        UpdateTiles();
    }

    private void BuildViews()
    {
        // Back arrow at the left of the header, shown on every page but the main one.
        if (_heading.Parent is Grid header)
        {
            _back = Material.Display.Pressable(Material.Display.Glyph("", 12), () => ShowView(""), new Thickness(6, 3, 6, 3));
            _back.HorizontalAlignment = HorizontalAlignment.Left;
            _back.VerticalAlignment = VerticalAlignment.Center;
            _back.Margin = new Thickness(-6, 0, 0, 0);
            header.Children.Add(_back);
        }

        GridTile("", "RustDesk", L.T("Đổi máy"), () => ShowView(ViewRemote));
        GridTile("", L.T("Âm lượng"), L.T("Từng app"), () => OpenPanel?.Invoke("sound"));
        GridTile("", L.T("Đang phát"), null, () => ShowView(""));
        GridTile("", L.T("Ghi chú"), null, () => OpenNotes?.Invoke());
        GridTile("", L.T("Tải về"), null, () => CommandBar.Launch(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")));
        GridTile("", L.T("Hệ thống"), null, () => OpenPanel?.Invoke("system"));
        GridTile("", L.T("Tiện ích"), null, () => OpenPanel?.Invoke("tools"));
        GridTile("", L.T("Lịch"), null, () => ShowView(ViewCalendar));
        GridTile("", "Command Bar", null, () => OpenCommandBar?.Invoke());
        GridTile("", L.T("Menu vòng tròn"), null, () => OpenRadial?.Invoke());
        GridTile("", L.T("Thiết bị"), null, () => OpenPanel?.Invoke("devices"));
        GridTile("✦", "AI Agents", null, () => ShowView(ViewAgents), symbol: true);
        Grid.SetRow(_tools, 1);
        _expanded.Children.Add(_tools);

        _agents.Content = _agentsContent;
        Grid.SetRow(_agents, 1);
        _expanded.Children.Add(_agents);
        Grid.SetRow(_remote, 1);
        _expanded.Children.Add(_remote);

        _controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_stripControls, 2);
        Grid.SetColumnSpan(_stripControls, 2);
        _controls.Children.Add(_stripControls);
        _player.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_stripPlayer, 2);
        Grid.SetColumnSpan(_stripPlayer, 2);
        _player.Children.Add(_stripPlayer);

        AgentMonitor.Updated += () =>
        {
            if (_mode == Mode.Expanded && _view == ViewAgents)
                DrawAgents();
        };
    }

    /// <summary>One square tile of the tool grid.</summary>
    private void GridTile(string glyph, string label, string? detail, Action onClick, bool symbol = false) =>
        _tools.Children.Add(new Tile(Material.Display, glyph, label, onClick, detail, roomy: true, symbol: symbol) { Height = 92 });

    /// <summary>Shows the parts of the open Island that belong to the current page.</summary>
    private void ApplyView()
    {
        var main = _view == "";
        _heading.Text = (_view switch
        {
            ViewCalendar => L.T("Lịch"),
            ViewTools => L.T("Tiện ích"),
            ViewAgents => "AI Agents",
            ViewRemote => "RustDesk",
            _ => _hasMedia ? L.T("Đang phát") : L.T("Điều khiển"),
        }).ToUpperInvariant();
        _heading.Margin = new Thickness(main ? 0 : 22, 0, 0, 0);
        if (_back != null) _back.Visibility = main ? Visibility.Collapsed : Visibility.Visible;
        _player.Visibility = main && _hasMedia ? Visibility.Visible : Visibility.Collapsed;
        _controls.Visibility = main && !_hasMedia ? Visibility.Visible : Visibility.Collapsed;
        _calendar.Visibility = _view == ViewCalendar ? Visibility.Visible : Visibility.Collapsed;
        _tools.Visibility = _view == ViewTools ? Visibility.Visible : Visibility.Collapsed;
        _agents.Visibility = _view == ViewAgents ? Visibility.Visible : Visibility.Collapsed;
        _remote.Visibility = _view == ViewRemote ? Visibility.Visible : Visibility.Collapsed;
        if (_view == ViewRemote) DrawRemote();
        if (main) DrawStrips();
        if (_view == ViewCalendar) DrawCalendar();
        if (_view == ViewAgents)
        {
            _agents.ScrollToTop();
            DrawAgents();
        }
    }

    private double ViewHeight() => _view switch
    {
        ViewTools => 64 + 3 * 100,
        ViewAgents => 400,
        ViewRemote => 92 + Math.Max(1, _remoteCount) * 66,
        _ => -1,
    };

    // ---------- RustDesk on the main page ----------

    /// <summary>Extra height the RustDesk row adds to the main page.</summary>
    private double StripHeight() => _stripCount > 0 ? 50 : 0;

    private void DrawStrips()
    {
        var peers = RustDesk.Available ? RustDesk.List().Take(3).ToList() : new System.Collections.Generic.List<RemotePeer>();
        _stripCount = peers.Count;
        foreach (var strip in new[] { _stripControls, _stripPlayer })
        {
            strip.Children.Clear();
            strip.Visibility = peers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            strip.Columns = Math.Max(1, peers.Count);
            foreach (var peer in peers)
                strip.Children.Add(StripButton(peer));
        }
    }

    /// <summary>A machine as a pill: click to bring up RustDesk on that machine.</summary>
    private Border StripButton(RemotePeer peer)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mac = peer.Platform.Contains("Mac", StringComparison.OrdinalIgnoreCase);
        var icon = Material.Display.Glyph(mac ? "\uE7F8" : "\uE7F4", 14);
        icon.Foreground = peer.Showing ? Material.Display.Accent : Material.Display.Secondary;
        icon.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(icon);
        var name = new TextBlock
        {
            Text = peer.Host.Length > 0 ? peer.Host : peer.Id,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var pill = new Border
        {
            Height = 38,
            Margin = new Thickness(4, 0, 4, 0),
            Padding = new Thickness(12, 0, 10, 0),
            CornerRadius = new CornerRadius(Material.Display.ControlRadius),
            Background = TileBrush,
            BorderBrush = peer.Showing ? Material.Display.Accent : TileBorder,
            BorderThickness = new Thickness(1),
            Child = row,
            Cursor = Cursors.Hand,
            ToolTip = $"RustDesk · {peer.Id}" + (peer.User.Length > 0 ? $" · {peer.User}" : "") + L.T(" · bấm để mở, bấm lần nữa để thu nhỏ"),
        };
        pill.MouseEnter += (_, _) => pill.Background = TileHover;
        pill.MouseLeave += (_, _) => pill.Background = TileBrush;
        var id = peer.Id;
        pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try
            {
                // Pressed again while that screen is in front: RustDesk is minimized, back to the desktop.
                RustDesk.Toggle(id);
            }
            catch (Exception ex)
            {
                Store.Log("rustdesk connect failed: " + ex.Message);
            }
            // Collapse so the RustDesk window is not covered; the row marks the new machine at once, and again once
            // RustDesk has switched, in case the island is still open.
            SetMode(Wanted());
            if (_mode == Mode.Expanded && _view == "") DrawStrips();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_mode == Mode.Expanded && _view == "") DrawStrips();
            };
            timer.Start();
        };
        return pill;
    }

    // ---------- RustDesk page ----------

    private void DrawRemote()
    {
        _remote.Children.Clear();
        if (!RustDesk.Available)
        {
            _remoteCount = 1;
            _remote.Children.Add(Card(new TextBlock { Text = L.T("Chưa thấy RustDesk trên máy này."), FontSize = 13, Foreground = Material.Display.Secondary }));
            return;
        }
        var peers = RustDesk.List().Take(5).ToList();
        _remoteCount = peers.Count;
        foreach (var peer in peers)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var mac = peer.Platform.Contains("Mac", StringComparison.OrdinalIgnoreCase);
            var icon = Material.Display.Glyph(mac ? "\uE7F8" : "\uE7F4", 20);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.Foreground = peer.Showing ? Material.Display.Accent : Material.Display.Text;
            grid.Children.Add(icon);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = peer.Host.Length > 0 ? peer.Host : peer.Id, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { peer.Id, peer.User, peer.Platform }.Where(x => x.Length > 0)), FontSize = 11.5, Foreground = Faint, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var state = new Border
            {
                CornerRadius = new CornerRadius(Material.Display.KeyRadius),
                Padding = new Thickness(9, 3, 9, 4),
                Background = peer.Showing ? Material.Display.AccentSoft : TrackBrush,
                Child = new TextBlock { Text = peer.Showing ? L.T("Đang xem") : L.T("Chuyển"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = peer.Showing ? Material.Display.Accent : Material.Display.Text },
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(state, 2);
            grid.Children.Add(state);
            var card = Card(grid, new Thickness(0, 0, 0, 8));
            card.Padding = new Thickness(14, 10, 14, 10);
            card.Cursor = Cursors.Hand;
            card.MouseEnter += (_, _) => card.Background = TileHover;
            card.MouseLeave += (_, _) => card.Background = TileBrush;
            var id = peer.Id;
            card.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    RustDesk.Toggle(id);
                }
                catch (Exception ex)
                {
                    Store.Log("rustdesk connect failed: " + ex.Message);
                }
                // The window title changes once RustDesk has switched tabs.
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (_view == ViewRemote) DrawRemote();
                };
                timer.Start();
            };
            _remote.Children.Add(card);
        }
        if (peers.Count == 0)
        {
            _remoteCount = 1;
            _remote.Children.Add(Card(new TextBlock { Text = L.T("RustDesk chưa lưu máy nào."), FontSize = 13, Foreground = Material.Display.Secondary }));
        }
        var open = Material.Display.Pressable(new TextBlock { Text = L.T("Mở RustDesk  ›"), FontSize = 12.5, Foreground = Material.Display.Accent }, RustDesk.OpenApp, new Thickness(8, 2, 8, 4));
        open.HorizontalAlignment = HorizontalAlignment.Center;
        _remote.Children.Add(open);
    }

}
