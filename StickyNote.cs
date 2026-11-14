using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace LumenLite;

/// <summary>
/// Scratchpad notes put out on the desktop as sticky notes. Each one is the same note as its Scratchpad tab (typing in
/// either shows in the other). A note can be dragged anywhere by its top bar, then pinned: pinned, it stays where it
/// is (no dragging or resizing) and floats above other windows. Place, size, colour and pin are kept between starts.
/// </summary>
internal static class StickyNotes
{
    private static readonly Dictionary<ScratchPad, StickyNote> Open = new();

    /// <summary>A note's text was changed by <c>sender</c> (a sticky note or the Scratchpad).</summary>
    public static event Action<ScratchPad, object>? Edited;

    /// <summary>Opens the Scratchpad on a note.</summary>
    public static Action<ScratchPad>? OpenInScratchpad { get; set; }

    public static void RaiseEdited(ScratchPad pad, object sender) => Edited?.Invoke(pad, sender);

    public static bool IsOnDesktop(ScratchPad pad) => Open.ContainsKey(pad);

    /// <summary>Shows the notes that were on the desktop when LumenLite last closed.</summary>
    public static void Restore()
    {
        foreach (var pad in Store.S.Pads.Where(p => p.OnDesktop).ToList())
            Show(pad, null);
    }

    /// <summary>Puts a note on the desktop (or brings it forward), at <paramref name="at"/> in WPF units when given.</summary>
    public static void Show(ScratchPad pad, Point? at)
    {
        var fresh = !Open.ContainsKey(pad);
        if (!Open.TryGetValue(pad, out var note))
        {
            note = new StickyNote(pad);
            note.Closed += (_, _) => Open.Remove(pad);
            Open[pad] = note;
        }
        if (at is { } p)
        {
            pad.Left = p.X - 40;
            pad.Top = p.Y - 14;
        }
        pad.OnDesktop = true;
        Store.SaveSoon();
        note.Place();
        note.Show();
        note.Activate();
        if (fresh)
            note.FocusEditor();
    }

    /// <summary>Takes a note off the desktop; it stays in the Scratchpad.</summary>
    public static void Remove(ScratchPad pad, bool forget = true)
    {
        if (Open.TryGetValue(pad, out var note))
            note.Close();
        if (forget)
        {
            pad.OnDesktop = false;
            Store.SaveSoon();
        }
    }

    /// <summary>Closes the windows at exit, leaving each note marked as on the desktop for the next start.</summary>
    public static void CloseAll()
    {
        foreach (var pad in Open.Keys.ToList())
            Remove(pad, forget: false);
    }

    /// <summary>The note colours, as background and text.</summary>
    public static readonly (string Name, Color Back, Color Text)[] Colors =
    {
        ("yellow", Color.FromRgb(0xFF, 0xF1, 0x9C), Color.FromRgb(0x2B, 0x26, 0x10)),
        ("green", Color.FromRgb(0xC8, 0xF0, 0xC0), Color.FromRgb(0x15, 0x2B, 0x14)),
        ("blue", Color.FromRgb(0xC4, 0xE0, 0xFF), Color.FromRgb(0x10, 0x20, 0x33)),
        ("pink", Color.FromRgb(0xFF, 0xCF, 0xE3), Color.FromRgb(0x33, 0x14, 0x22)),
        ("dark", Color.FromRgb(0x24, 0x24, 0x27), Color.FromRgb(0xEE, 0xEE, 0xEE)),
    };
}

internal sealed class StickyNote : Window
{
    private readonly ScratchPad _pad;
    private readonly TextBox _editor;
    private readonly TextBlock _title;
    private readonly TextBlock _pinGlyph;
    private readonly Border _card;
    private readonly Grid _header;
    private readonly List<TextBlock> _glyphs = new();
    private bool _loading;

    private const string PinGlyph = "", PinnedGlyph = "";

    public StickyNote(ScratchPad pad)
    {
        _pad = pad;
        Title = pad.Title;
        Icon = Branding.Icon;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = Theme.Font;
        MinWidth = 180;
        MinHeight = 120;
        Width = pad.Width ?? 260;
        Height = pad.Height ?? 240;

        _title = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
        _pinGlyph = Glyph(PinGlyph);

        _header = new Grid { Background = Brushes.Transparent, Height = 30, Cursor = Cursors.SizeAll };
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.Children.Add(_title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button(Glyph(""), L.T("Đổi màu"), NextColor));
        actions.Children.Add(Button(Glyph(""), L.T("Mở trong Scratchpad"), () => StickyNotes.OpenInScratchpad?.Invoke(_pad)));
        actions.Children.Add(Button(_pinGlyph, L.T("Ghim: giữ nguyên chỗ và nổi trên các cửa sổ khác"), TogglePin));
        actions.Children.Add(Button(Glyph("", 10), L.T("Gỡ khỏi màn hình (ghi chú vẫn còn trong Scratchpad)"), () => StickyNotes.Remove(_pad)));
        Grid.SetColumn(actions, 1);
        _header.Children.Add(actions);
        _header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                TogglePin();
                return;
            }
            if (!_pad.Pinned && e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
                SavePlace();
            }
        };

        _editor = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13.5,
            Padding = new Thickness(2),
            Text = pad.Text,
        };
        _editor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _pad.Text = _editor.Text;
            Store.SaveSoon();
            StickyNotes.RaiseEdited(_pad, this);
        };

        var body = new Grid { Margin = new Thickness(10, 4, 10, 10) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_header);
        Grid.SetRow(_editor, 1);
        body.Children.Add(_editor);


        _card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(10),
            Child = body,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.35, Direction = 270 },
        };
        Content = _card;

        StickyNotes.Edited += OnEdited;
        Closed += (_, _) => StickyNotes.Edited -= OnEdited;
        LocationChanged += (_, _) => SavePlace();
        SizeChanged += (_, _) => SavePlace();
        SourceInitialized += (_, _) =>
        {
            // A tool window: no taskbar button and no Alt+Tab entry, like the desktop widgets.
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            style |= Native.WS_EX_TOOLWINDOW;
            style &= ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(style));
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Keyboard.ClearFocus();
        };

        ApplyColor();
        ApplyPin();
        Refresh();
    }

    /// <summary>Puts the window where the note was left, or near the middle of the work area the first time.</summary>
    public void Place()
    {
        var area = SystemParameters.WorkArea;
        var left = _pad.Left ?? area.Left + (area.Width - Width) / 2 + Store.S.Pads.IndexOf(_pad) * 24;
        var top = _pad.Top ?? area.Top + area.Height / 4 + Store.S.Pads.IndexOf(_pad) * 24;
        // Kept on some screen: a note left on a monitor that is gone comes back to the main one.
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        left = Math.Clamp(left, virtualLeft, virtualLeft + SystemParameters.VirtualScreenWidth - 80);
        top = Math.Clamp(top, virtualTop, virtualTop + SystemParameters.VirtualScreenHeight - 40);
        Left = left;
        Top = top;
    }

    /// <summary>Puts the typing cursor at the end of the note.</summary>
    public void FocusEditor()
    {
        _editor.Focus();
        _editor.CaretIndex = _editor.Text.Length;
    }

    /// <summary>Shows the note's current title and text.</summary>
    public void Refresh()
    {
        _title.Text = _pad.Title;
        Title = _pad.Title;
        if (_editor.Text != _pad.Text)
        {
            _loading = true;
            var caret = _editor.CaretIndex;
            _editor.Text = _pad.Text;
            _editor.CaretIndex = Math.Min(caret, _editor.Text.Length);
            _loading = false;
        }
    }

    private void OnEdited(ScratchPad pad, object sender)
    {
        if (pad == _pad && sender != this)
            Refresh();
    }

    private void SavePlace()
    {
        if (!IsLoaded) return;
        _pad.Left = Left;
        _pad.Top = Top;
        _pad.Width = ActualWidth > 0 ? ActualWidth : Width;
        _pad.Height = ActualHeight > 0 ? ActualHeight : Height;
        Store.SaveSoon();
    }

    private void TogglePin()
    {
        _pad.Pinned = !_pad.Pinned;
        SavePlace();
        ApplyPin();
    }

    private void ApplyPin()
    {
        Topmost = _pad.Pinned;
        ResizeMode = _pad.Pinned ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        _header.Cursor = _pad.Pinned ? Cursors.Arrow : Cursors.SizeAll;
        _pinGlyph.Text = _pad.Pinned ? PinnedGlyph : PinGlyph;
        _pinGlyph.Opacity = _pad.Pinned ? 1 : 0.6;
    }

    private int ColorIndex()
    {
        var i = Array.FindIndex(StickyNotes.Colors, c => c.Name == _pad.Color);
        return i < 0 ? 0 : i;
    }

    private void NextColor()
    {
        _pad.Color = StickyNotes.Colors[(ColorIndex() + 1) % StickyNotes.Colors.Length].Name;
        Store.SaveSoon();
        StickyNotes.RaiseEdited(_pad, this);
        ApplyColor();
    }

    private void ApplyColor()
    {
        var (_, back, text) = StickyNotes.Colors[ColorIndex()];
        var foreground = new SolidColorBrush(text);
        _card.Background = new SolidColorBrush(back);
        _card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0));
        _card.BorderThickness = new Thickness(1);
        _title.Foreground = foreground;
        _editor.Foreground = foreground;
        _editor.CaretBrush = foreground;
        _editor.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x60, text.R, text.G, text.B));
        foreach (var glyph in _glyphs)
            glyph.Foreground = foreground;
    }

    private TextBlock Glyph(string glyph, double size = 12)
    {
        var block = new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _glyphs.Add(block);
        return block;
    }

    private static Border Button(TextBlock glyph, string tooltip, Action onClick)
    {
        var hover = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0));
        var button = new Border
        {
            Width = 26,
            Height = 24,
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = glyph,
        };
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return button;
    }
}
