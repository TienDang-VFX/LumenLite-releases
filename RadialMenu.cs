using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LumenLite;

/// <summary>
/// A button on the radial menu, drawn like its counterpart on the Island: the
/// same mark (a Segoe Fluent glyph, or a symbol-font character such as the AI
/// spark) in the same off-white. <paramref name="Active"/> rings it, as the Island
/// marks the RustDesk machine on screen now.
/// </summary>
internal sealed record RadialAction(string Id, string Glyph, string Label, Action Run, Brush Tint, bool Symbol = false, bool Active = false);

/// <summary>
/// A ring of up to eight buttons that opens around the mouse pointer. Point
/// towards a button and click (or let go of the shortcut keys) to run it;
/// Esc, a click in the middle or a click elsewhere closes it.
/// </summary>
internal sealed class RadialMenu : Window
{
    private const double Size = 400, Ring = 132, ButtonSize = 66, CenterSize = 104, DeadZone = 46;

    private readonly Canvas _canvas = new() { Width = Size, Height = Size };
    private readonly TextBlock _centerLabel = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Material.Display.Text, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = CenterSize - 18 };
    private readonly List<(RadialAction Action, Border Button, TextBlock Icon)> _items = new();
    // The display's colours: black knobs with a hairline rim; the one pointed at turns orange.
    private static readonly Brush Tile = Material.Display.Fill;
    private static readonly Brush TileBorder = Material.Display.Rim;
    private static readonly Brush Picked = Material.Display.Accent;
    private static readonly Brush Faint = Material.Display.Secondary;
    private readonly ScaleTransform _zoom = new(0.7, 0.7, Size / 2, Size / 2);
    private int _hover = -1;
    private bool _closing;
    private DateTime _openedAt;

    public RadialMenu(IReadOnlyList<RadialAction> actions)
    {
        Title = "LumenLite Radial";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = Size;
        Height = Size;
        FontFamily = Theme.Font;

        // A faint disc catches the mouse everywhere inside the ring.
        var disc = new Ellipse { Width = Size - 8, Height = Size - 8, Fill = Palette.B(0x01, 0, 0, 0) };
        Canvas.SetLeft(disc, 4);
        Canvas.SetTop(disc, 4);
        _canvas.Children.Add(disc);

        var center = new Border
        {
            Width = CenterSize,
            Height = CenterSize,
            CornerRadius = new CornerRadius(CenterSize / 2),
            Background = Tile,
            BorderBrush = TileBorder,
            BorderThickness = new Thickness(1),
            Child = _centerLabel,
        };
        _centerLabel.VerticalAlignment = VerticalAlignment.Center;
        _centerLabel.HorizontalAlignment = HorizontalAlignment.Center;
        Canvas.SetLeft(center, (Size - CenterSize) / 2);
        Canvas.SetTop(center, (Size - CenterSize) / 2);
        _canvas.Children.Add(center);

        var list = actions.Take(8).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var angle = -Math.PI / 2 + i * 2 * Math.PI / list.Count;
            var icon = list[i].Symbol
                ? new TextBlock { Text = list[i].Glyph, FontSize = 24, FontFamily = new FontFamily("Segoe UI Symbol"), Foreground = list[i].Tint, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : Material.Display.Glyph(list[i].Glyph, 22, list[i].Tint);
            var button = new Border
            {
                Width = ButtonSize,
                Height = ButtonSize,
                CornerRadius = new CornerRadius(ButtonSize / 2),
                Background = Tile,
                BorderBrush = TileBorder,
                BorderThickness = new Thickness(1),
                Child = icon,
                ToolTip = list[i].Label,
            };
            Canvas.SetLeft(button, Size / 2 + Math.Cos(angle) * Ring - ButtonSize / 2);
            Canvas.SetTop(button, Size / 2 + Math.Sin(angle) * Ring - ButtonSize / 2);
            _canvas.Children.Add(button);
            _items.Add((list[i], button, icon));
        }
        Highlight(-1);

        _canvas.RenderTransform = _zoom;
        Content = _canvas;

        MouseMove += (_, e) => Highlight(Pick(e.GetPosition(_canvas)));
        MouseLeftButtonUp += (_, e) =>
        {
            var index = Pick(e.GetPosition(_canvas));
            if (index >= 0) Run(index);
            else Close();
        };
        MouseRightButtonUp += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is >= Key.D1 and <= Key.D8 && e.Key - Key.D1 < _items.Count) Run(e.Key - Key.D1);
        };
        // Quick gesture: hold the shortcut, point at a button, let go.
        PreviewKeyUp += (_, e) =>
        {
            if (_hover >= 0 && DateTime.UtcNow - _openedAt > TimeSpan.FromMilliseconds(150) && e.Key is Key.R or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.System)
                Run(_hover);
        };
        Deactivated += (_, _) => { if (!_closing) Close(); };
        Closing += (_, _) => _closing = true;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr((style | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW));
            CenterOnCursor(hwnd);
        };
    }

    /// <summary>The trigger (middle button) was let go: run what is pointed at, or close.</summary>
    public void ReleaseSelect()
    {
        if (_hover >= 0)
            Run(_hover);
        else if (!_closing)
            Close();
    }

    public void Open()
    {
        _openedAt = DateTime.UtcNow;
        Show();
        Activate();
        var grow = new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } };
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    }

    /// <summary>The button in the direction of the pointer, or -1 in the middle.</summary>
    private int Pick(Point point)
    {
        if (_items.Count == 0) return -1;
        var dx = point.X - Size / 2;
        var dy = point.Y - Size / 2;
        if (Math.Sqrt(dx * dx + dy * dy) < DeadZone) return -1;
        var angle = Math.Atan2(dy, dx) + Math.PI / 2; // 0 at the top, clockwise
        if (angle < 0) angle += 2 * Math.PI;
        var step = 2 * Math.PI / _items.Count;
        return (int)Math.Round(angle / step) % _items.Count;
    }

    private void Highlight(int index)
    {
        _hover = index;
        for (var i = 0; i < _items.Count; i++)
        {
            var (action, button, icon) = _items[i];
            var on = i == index;
            // Pointed at: filled orange; the machine on screen now keeps an orange rim.
            button.Background = on ? Picked : Tile;
            button.BorderBrush = on || action.Active ? Material.Display.Accent : TileBorder;
            button.BorderThickness = new Thickness(action.Active && !on ? 2 : 1);
            icon.Foreground = on ? Material.Display.OnAccent : action.Tint;
        }
        _centerLabel.Text = index >= 0 ? _items[index].Action.Label : _items.Count > 0 ? L.T("Chọn máy hoặc AI") : L.T("RustDesk chưa lưu máy nào");
        _centerLabel.Foreground = index >= 0 ? Material.Display.Text : Faint;
    }

    private void Run(int index)
    {
        var action = _items[index].Action;
        Close();
        try
        {
            action.Run();
        }
        catch (Exception ex)
        {
            Store.Log($"radial '{action.Id}' failed: " + ex.Message);
        }
    }

    /// <summary>Centres the ring on the pointer, kept fully on the pointer's screen.</summary>
    private void CenterOnCursor(IntPtr hwnd)
    {
        if (!GetCursorPos(out var cursor) || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return;
        var side = (int)Math.Round(Size * target.TransformToDevice.M11);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea;
        var x = Math.Clamp(cursor.X - side / 2, screen.Left, screen.Right - side);
        var y = Math.Clamp(cursor.Y - side / 2, screen.Top, screen.Bottom - side);
        Native.SetWindowPos(hwnd, new IntPtr(-1), x, y, 0, 0, Native.SWP_NOSIZE);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
