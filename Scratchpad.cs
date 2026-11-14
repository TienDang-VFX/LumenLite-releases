using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LumenLite.Panel;

namespace LumenLite;

/// <summary>
/// Floating notes in tabs with a Markdown preview, saved as you type
/// (in settings.json, next to the Notes widget's text).
/// </summary>
internal sealed class Scratchpad : Window
{
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _editor;
    private readonly FlowDocumentScrollViewer _preview = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, IsToolBarVisible = false };
    private readonly TextBlock _previewLabel = Ui.Text(L.T("Xem trước"), 12, false, Ui.M.Secondary);
    private readonly TextBlock _pinGlyph = Ui.Glyph("", 13, Ui.M.Secondary);
    private int _active;
    private ScratchPad? _activePad;
    private readonly Border _history;
    private bool _loading;
    private bool _discarded;

    /// <summary>Whether Windows draws a blurred backdrop behind the window (the snapshot tool wants to know).</summary>
    public bool OnGlass { get; }

    /// <summary>The palette this window's colours were taken from.</summary>
    public Palette BuiltFor { get; } = Palette.Current;

    /// <summary>Closes the window for good (closing normally only hides it).</summary>
    public void Discard()
    {
        _discarded = true;
        Close();
    }

    public Scratchpad()
    {
        Title = "Scratchpad";
        Icon = Branding.Icon;
        // On glass the window has a real (invisible) sizing edge; the transparent fallback has none, so it shows a grip.
        ResizeMode = Backdrop.Available ? ResizeMode.CanResize : ResizeMode.CanResizeWithGrip;
        Width = 496;
        Height = 436;
        MinWidth = 296;
        MinHeight = 196;
        FontFamily = Theme.Font;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        OnGlass = Backdrop.Apply(this, Palette.Current.IsDark, resizeBorder: 6);
        if (Store.S.Pads.Count == 0)
            Store.S.Pads.Add(new ScratchPad { Title = L.T("Ghi chú 1"), Text = L.T("# Scratchpad\n\nGõ **Markdown** ở đây: `- danh sách`, `## tiêu đề`, `[link](https://…)`.\nTự lưu khi bạn gõ.\n") });

        _editor = new TextBox
        {
            Background = Brushes.Transparent,
            Foreground = Ui.M.Text,
            CaretBrush = Ui.M.Accent,
            SelectionBrush = Ui.M.Accent,
            BorderThickness = new Thickness(0),
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 14,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"),
            Padding = new Thickness(4),
        };
        _editor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            Store.S.Pads[_active].Text = _editor.Text;
            Store.SaveSoon();
            StickyNotes.RaiseEdited(Store.S.Pads[_active], this);
        };
        StickyNotes.Edited += OnStickyEdited;
        NotesSync.ListChanged += OnNotesListChanged;
        Closed += (_, _) =>
        {
            StickyNotes.Edited -= OnStickyEdited;
            NotesSync.ListChanged -= OnNotesListChanged;
        };

        var header = new Grid { Margin = new Thickness(12, 10, 10, 6), Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Ui.M.Pressable(Ui.Glyph("", 13), AddPad, new Thickness(8, 5, 8, 5)));
        actions.Children.Add(Ui.M.Pressable(_previewLabel, TogglePreview, new Thickness(8, 5, 8, 5)));
        var toDesktop = Ui.M.Pressable(Ui.Glyph("", 13), () => StickyNotes.Show(Store.S.Pads[_active], null), new Thickness(8, 5, 8, 5));
        toDesktop.ToolTip = L.T("Dán ghi chú này lên màn hình (hoặc kéo tab ra ngoài cửa sổ)");
        actions.Children.Add(toDesktop);
        _history = Ui.M.Pressable(Ui.Glyph("", 13), () =>
        {
            if (NotesSync.HistoryUrl(Store.S.Pads[_active]) is { } url) CommandBar.Launch(url);
        }, new Thickness(8, 5, 8, 5));
        _history.ToolTip = L.T("Lịch sử ghi chú này trên GitHub");
        actions.Children.Add(_history);
        var pin = Ui.M.Pressable(_pinGlyph, () =>
        {
            Topmost = !Topmost;
            _pinGlyph.Foreground = Topmost ? Ui.M.Accent : Ui.M.Secondary;
        }, new Thickness(8, 5, 8, 5));
        pin.ToolTip = L.T("Ghim nổi trên các cửa sổ khác");
        actions.Children.Add(pin);
        actions.Children.Add(Ui.M.Pressable(Ui.Glyph("", 11, Ui.M.Secondary), Hide, new Thickness(8, 5, 8, 5)));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1)
                DragMove();
        };

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(header);
        var page = new Grid();
        page.Children.Add(_editor);
        page.Children.Add(_preview);
        // The note is on a group of its own, so the text stays readable whatever shows through the glass.
        var content = Ui.M.Raised(page, new Thickness(10, 8, 6, 8));
        content.Margin = new Thickness(10, 2, 10, 10);
        Grid.SetRow(content, 2);
        body.Children.Add(content);

        Content = Ui.M.Frame(body);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Hide();
            if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control) { AddPad(); e.Handled = true; }
            if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control) { TogglePreview(); e.Handled = true; }
        };
        Closing += (_, e) =>
        {
            if (!WidgetWindow.Exiting && !_discarded)
            {
                e.Cancel = true;
                Hide();
            }
        };
        Select(0);
        IsVisibleChanged += (_, _) => { if (!IsVisible) Memory.TrimSoon(); };
    }

    public void Open()
    {
        Backdrop.ShowCloaked(this);
        Activate();
        if (_preview.Visibility != Visibility.Visible)
            _editor.Focus();
    }

    /// <summary>Opens the Scratchpad on the given note.</summary>
    public void Open(ScratchPad pad)
    {
        var index = Store.S.Pads.IndexOf(pad);
        if (index >= 0)
            Select(index);
        Open();
    }

    private void OnStickyEdited(ScratchPad pad, object sender)
    {
        if (sender == this || Store.S.Pads.IndexOf(pad) != _active || _editor.Text == pad.Text)
            return;
        _loading = true;
        var caret = _editor.CaretIndex;
        _editor.Text = pad.Text;
        _editor.CaretIndex = Math.Min(caret, _editor.Text.Length);
        _loading = false;
        if (_preview.Visibility == Visibility.Visible)
            _preview.Document = Markdown(_editor.Text);
    }

    private void OnNotesListChanged()
    {
        if (Store.S.Pads.Count == 0)
            Store.S.Pads.Add(new ScratchPad { Title = L.F($"Ghi chú {1}") });
        var index = _activePad == null ? -1 : Store.S.Pads.IndexOf(_activePad);
        if (index < 0)
            Select(Math.Min(_active, Store.S.Pads.Count - 1));
        else
        {
            _active = index;
            DrawTabs();
        }
    }

    private void AddPad()
    {
        Store.S.Pads.Add(new ScratchPad { Title = L.F($"Ghi chú {Store.S.Pads.Count + 1}") });
        NotesSync.EnsureIds();
        Store.SaveSoon();
        NotesSync.Soon();
        Select(Store.S.Pads.Count - 1);
        _editor.Focus();
    }

    private void Select(int index)
    {
        _active = Math.Clamp(index, 0, Store.S.Pads.Count - 1);
        _activePad = Store.S.Pads[_active];
        _history.Visibility = NotesSync.Active ? Visibility.Visible : Visibility.Collapsed;
        _loading = true;
        _editor.Text = Store.S.Pads[_active].Text;
        _loading = false;
        if (_preview.Visibility == Visibility.Visible)
            _preview.Document = Markdown(_editor.Text);
        DrawTabs();
    }

    private void DrawTabs()
    {
        _tabs.Children.Clear();
        for (var i = 0; i < Store.S.Pads.Count; i++)
        {
            var index = i;
            var pad = Store.S.Pads[i];
            var selected = i == _active;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var title = Ui.Text(pad.Title, 12.5, selected, selected ? Ui.M.Text : Ui.M.Secondary);
            title.MaxWidth = 130;
            row.Children.Add(title);
            if (Store.S.Pads.Count > 1)
            {
                var close = Ui.M.Pressable(Ui.Glyph("", 8, Ui.M.Secondary), () => ClosePad(index), new Thickness(5, 3, 3, 3), 4);
                close.Margin = new Thickness(6, 0, -4, 0);
                row.Children.Add(close);
            }
            var tab = Ui.M.Pressable(row, () => Select(index), new Thickness(10, 5, 10, 5), Ui.M.ControlRadius, selected ? Ui.M.Inset : null);
            tab.Margin = new Thickness(0, 0, 4, 0);
            tab.ToolTip = L.T("Bấm đúp để đổi tên");
            tab.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                    Rename(index, tab);
            };
            DragOut(tab, pad);
            _tabs.Children.Add(tab);
        }
    }

    private void Rename(int index, Border tab)
    {
        var box = new TextBox
        {
            Text = Store.S.Pads[index].Title,
            Background = Ui.M.Inset,
            Foreground = Ui.M.Text,
            CaretBrush = Ui.M.Text,
            BorderThickness = new Thickness(0),
            FontSize = 12.5,
            MinWidth = 80,
        };
        void Commit()
        {
            var name = box.Text.Trim();
            if (name.Length > 0)
                Store.S.Pads[index].Title = name;
            Store.SaveSoon();
            StickyNotes.RaiseEdited(Store.S.Pads[index], this);
            DrawTabs();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { Commit(); e.Handled = true; }
        };
        box.LostFocus += (_, _) => Commit();
        tab.Child = box;
        box.Focus();
        box.SelectAll();
    }

    private void ClosePad(int index)
    {
        var pad = Store.S.Pads[index];
        if (pad.Text.Trim().Length > 0 && MessageBox.Show(this, L.F($"Xóa ghi chú \"{pad.Title}\"?"), "Scratchpad", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        StickyNotes.Remove(pad);
        NotesSync.Forget(pad);
        Store.S.Pads.RemoveAt(index);
        Store.SaveSoon();
        Select(Math.Min(_active, Store.S.Pads.Count - 1));
    }

    /// <summary>
    /// Dragging a tab out of the window and letting go puts that note on the desktop as a sticky note, where the
    /// mouse was released.
    /// </summary>
    private void DragOut(Border tab, ScratchPad pad)
    {
        Point? pressed = null;
        var dragging = false;
        tab.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = e.GetPosition(this);
            dragging = false;
        };
        tab.PreviewMouseMove += (_, e) =>
        {
            if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed)
            {
                pressed = null;
                return;
            }
            var now = e.GetPosition(this);
            if (!dragging && (Math.Abs(now.X - start.X) > 12 || Math.Abs(now.Y - start.Y) > 12))
            {
                dragging = true;
                tab.CaptureMouse();
                tab.Cursor = Cursors.SizeAll;
            }
        };
        tab.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var wasDragging = dragging;
            pressed = null;
            dragging = false;
            tab.Cursor = null;
            if (!wasDragging)
                return;
            tab.ReleaseMouseCapture();
            e.Handled = true;
            var inside = e.GetPosition(this);
            if (inside.X >= 0 && inside.Y >= 0 && inside.X <= ActualWidth && inside.Y <= ActualHeight)
                return;
            // The drop point in screen units: the window's own place plus where the mouse is relative to it.
            StickyNotes.Show(pad, new Point(Left + inside.X, Top + inside.Y));
        };
    }

    private void TogglePreview()
    {
        var show = _preview.Visibility != Visibility.Visible;
        _preview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _editor.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _previewLabel.Text = show ? L.T("Sửa") : L.T("Xem trước");
        _previewLabel.Foreground = show ? Ui.M.Accent : Ui.M.Secondary;
        if (show)
            _preview.Document = Markdown(_editor.Text);
        else
            _editor.Focus();
    }

    // ---------- a small Markdown renderer ----------

    private static FlowDocument Markdown(string text)
    {
        var doc = new FlowDocument { FontFamily = Theme.Font, FontSize = 14, Foreground = Ui.M.Text, PagePadding = new Thickness(2), Background = Brushes.Transparent };
        List? list = null;
        Paragraph? code = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```"))
            {
                if (code == null)
                {
                    code = new Paragraph { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, Background = Ui.M.Inset, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 4, 0, 8) };
                    doc.Blocks.Add(code);
                }
                else
                    code = null;
                list = null;
                continue;
            }
            if (code != null)
            {
                if (code.Inlines.Count > 0) code.Inlines.Add(new LineBreak());
                code.Inlines.Add(new Run(line));
                continue;
            }
            var bullet = Regex.Match(line, @"^\s*([-*+]|\d+\.)\s+(.*)$");
            if (bullet.Success)
            {
                var numbered = char.IsDigit(bullet.Groups[1].Value[0]);
                if (list == null || (list.MarkerStyle == TextMarkerStyle.Decimal) != numbered)
                {
                    list = new List { MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(20, 0, 0, 0) };
                    doc.Blocks.Add(list);
                }
                var item = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                var content = bullet.Groups[2].Value;
                if (content.StartsWith("[ ] ") || content.StartsWith("[x] "))
                {
                    item.Inlines.Add(new Run(content[1] == 'x' ? "☑ " : "☐ "));
                    content = content[4..];
                }
                AddInlines(item, content);
                list.ListItems.Add(new ListItem(item));
                continue;
            }
            list = null;
            if (line.Length == 0)
                continue;
            if (Regex.IsMatch(line, @"^\s*(---+|\*\*\*+)\s*$"))
            {
                doc.Blocks.Add(new BlockUIContainer(new Border { Height = 1, Background = Ui.M.Divider, Margin = new Thickness(0, 6, 0, 6) }));
                continue;
            }
            var heading = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                paragraph.FontSize = level switch { 1 => 24, 2 => 19, 3 => 16, _ => 14.5 };
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, level == 1 ? 2 : 8, 0, 6);
                AddInlines(paragraph, heading.Groups[2].Value);
            }
            else if (line.StartsWith(">"))
            {
                paragraph.BorderBrush = Ui.M.Track;
                paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                paragraph.Padding = new Thickness(10, 2, 0, 2);
                paragraph.Foreground = Ui.M.Secondary;
                AddInlines(paragraph, line.TrimStart('>', ' '));
            }
            else
                AddInlines(paragraph, line);
            doc.Blocks.Add(paragraph);
        }
        return doc;
    }

    private static readonly Regex InlinePattern = new(@"(\*\*(.+?)\*\*|__(.+?)__|\*(.+?)\*|_(.+?)_|`(.+?)`|\[(.+?)\]\((.+?)\)|~~(.+?)~~)", RegexOptions.Compiled);

    private static void AddInlines(Paragraph paragraph, string text)
    {
        var last = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > last)
                paragraph.Inlines.Add(new Run(text[last..m.Index]));
            if (m.Groups[2].Success || m.Groups[3].Success)
                paragraph.Inlines.Add(new Bold(new Run(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value)));
            else if (m.Groups[4].Success || m.Groups[5].Success)
                paragraph.Inlines.Add(new Italic(new Run(m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value)));
            else if (m.Groups[6].Success)
                paragraph.Inlines.Add(new Run(m.Groups[6].Value) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Background = Ui.M.Inset, FontSize = 12.5 });
            else if (m.Groups[7].Success)
            {
                var url = m.Groups[8].Value;
                var link = new Hyperlink(new Run(m.Groups[7].Value)) { Foreground = Ui.M.Accent, Cursor = Cursors.Hand };
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    link.Click += (_, _) => CommandBar.Launch(uri.AbsoluteUri);
                paragraph.Inlines.Add(link);
            }
            else if (m.Groups[9].Success)
                paragraph.Inlines.Add(new Run(m.Groups[9].Value) { TextDecorations = TextDecorations.Strikethrough });
            last = m.Index + m.Length;
        }
        if (last < text.Length)
            paragraph.Inlines.Add(new Run(text[last..]));
    }
}
