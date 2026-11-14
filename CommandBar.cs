using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LumenLite.Panel;

namespace LumenLite;

/// <summary>One result line in the Command Bar.</summary>
internal sealed record CommandItem(string Title, string Subtitle, string Kind, string Glyph, Action Run, string? IconSource = null, int Score = 0);

/// <summary>
/// One field that finds and runs things: apps (including Store apps), LumenLite's
/// own actions, Windows settings pages, recent files, a calculator, unit
/// conversions and emoji. Opens with a shortcut, closes on Esc or when it loses focus.
/// </summary>
internal sealed class CommandBar : Window
{
    private readonly TextBox _input;
    private readonly StackPanel _results = new();
    private readonly TextBlock _hint = Ui.Mono("", 10.5, Ui.M.Secondary);
    private readonly Func<IEnumerable<CommandItem>> _commands;
    private List<CommandItem> _items = new();
    private int _selected;
    private List<(string Name, string Launch)> _apps = new();
    private List<(string Name, string Path)> _recent = new();
    private static readonly Dictionary<string, BitmapSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether Windows draws a blurred backdrop behind the bar (the snapshot tool wants to know).</summary>
    public bool OnGlass { get; }

    public CommandBar(Func<IEnumerable<CommandItem>> commands)
    {
        _commands = commands;
        Title = "LumenLite Command Bar";
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 652;
        SizeToContent = SizeToContent.Height;
        FontFamily = Theme.Font;
        OnGlass = Backdrop.Apply(this, Palette.Current.IsDark);
        var m = Material.Current;
        Foreground = m.Text;
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr((style | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW));

        var (box, input) = Ui.Search(L.T("Tìm app, lệnh, phép tính, đổi đơn vị, emoji…"), 18, bare: true);
        _input = input;

        var stack = new StackPanel();
        stack.Children.Add(box);
        // The results are on a group of their own, so they stay readable whatever shows through the glass.
        var results = m.Raised(new ScrollViewer { Content = _results, MaxHeight = 430, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }, new Thickness(4));
        results.Margin = new Thickness(8, 8, 8, 0);
        stack.Children.Add(results);
        _hint.Margin = new Thickness(16, 6, 16, 10);
        _hint.Text = L.T("↑↓ chọn · Enter mở · Esc đóng");
        stack.Children.Add(_hint);

        Content = m.Frame(stack);

        _input.TextChanged += (_, _) => Search();
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => Hide();
        SizeChanged += (_, _) => Place();
    }

    public void Open()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        _input.Text = "";
        Search();
        Backdrop.ShowCloaked(this, Place);
        Activate();
        _input.Focus();
        Keyboard.Focus(_input);
        // The app list is read in the background and refreshed each time.
        Task.Run(() =>
        {
            var apps = LoadApps();
            var recent = LoadRecent();
            Dispatcher.BeginInvoke(() =>
            {
                _apps = apps;
                _recent = recent;
                if (IsVisible) Search();
            });
        });
    }

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var rect))
            return;
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var width = rect.Right - rect.Left;
        Native.SetWindowPos(hwnd, new IntPtr(-1), area.Left + (area.Width - width) / 2, area.Top + area.Height / 5, 0, 0, Native.SWP_NOSIZE);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Down:
                Select(_selected + 1);
                e.Handled = true;
                break;
            case Key.Up:
                Select(_selected - 1);
                e.Handled = true;
                break;
            case Key.Enter:
                Run(_selected);
                e.Handled = true;
                break;
        }
    }

    private void Run(int index)
    {
        if (index < 0 || index >= _items.Count)
            return;
        Hide();
        try
        {
            _items[index].Run();
        }
        catch (Exception ex)
        {
            Store.Log("command failed: " + ex.Message);
        }
    }

    private void Select(int index)
    {
        if (_items.Count == 0) return;
        _selected = Math.Clamp(index, 0, _items.Count - 1);
        for (var i = 0; i < _results.Children.Count; i++)
            ((Border)_results.Children[i]).Background = i == _selected ? Ui.M.AccentSoft : Brushes.Transparent;
        ((FrameworkElement)_results.Children[_selected]).BringIntoView();
    }

    // ---------- search ----------

    private void Search()
    {
        var query = _input.Text.Trim();
        var items = new List<CommandItem>();
        if (query.Length > 0)
        {
            items.AddRange(Calculate(query));
            items.AddRange(Convert(query));
            items.AddRange(Emoji(query));
            items.AddRange(Match(_commands(), query));
            items.AddRange(_apps.Select(a => Scored(new CommandItem(a.Name, L.T("Ứng dụng"), "App", "", () => Launch(a.Launch), a.Launch), query)).Where(i => i.Score > 0));
            items.AddRange(_recent.Select(r => Scored(new CommandItem(r.Name, r.Path, L.T("File gần đây"), "", () => Launch(r.Path), r.Path), query)).Where(i => i.Score > 0).Select(i => i with { Score = i.Score - 15 }));
            if (Directory.Exists(query) || File.Exists(query))
                items.Add(new CommandItem(query, L.T("Mở đường dẫn này"), L.T("Đường dẫn"), "", () => Launch(query), Score: 95));
            items = items.OrderByDescending(i => i.Score).Take(9).ToList();
            items.Add(new CommandItem(L.F($"Tìm \"{query}\" trên Google"), L.T("Mở trình duyệt"), "Web", "",
                () => Launch("https://www.google.com/search?q=" + Uri.EscapeDataString(query))));
        }
        else
        {
            items.AddRange(_commands().Take(6));
        }
        _items = items;
        Draw();
    }

    private static IEnumerable<CommandItem> Match(IEnumerable<CommandItem> source, string query) =>
        source.Select(item => Scored(item, query)).Where(i => i.Score > 0);

    /// <summary>Starts-with beats word-starts-with beats contains; accents are ignored.</summary>
    private static CommandItem Scored(CommandItem item, string query)
    {
        var name = Fold(item.Title);
        var keys = Fold(item.Title + " " + item.Subtitle);
        var q = Fold(query);
        var score = name.StartsWith(q) ? 90
            : name.Split(' ', '-', '_', '.').Any(w => w.StartsWith(q)) ? 75
            : name.Contains(q) ? 60
            : Initials(name).StartsWith(q) && q.Length >= 2 ? 55
            : keys.Contains(q) ? 40 : 0;
        if (score > 0 && item.Kind == L.T("Lệnh")) score += 3;
        return item with { Score = score };
    }

    private static string Initials(string text) => new(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]).ToArray());

    /// <summary>Lower-case without Vietnamese accents, so "khoa may" finds "Khóa máy".</summary>
    public static string Fold(string text)
    {
        var normalized = text.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        return builder.ToString();
    }

    private void Draw()
    {
        _results.Children.Clear();
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var index = i;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = item.IconSource != null ? LoadIcon(item.IconSource) : null;
            grid.Children.Add(icon != null
                ? new Image { Source = icon, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left }
                : item.Kind == "Emoji" ? new TextBlock { Text = item.Glyph, FontSize = 22, FontFamily = new FontFamily("Segoe UI Emoji"), VerticalAlignment = VerticalAlignment.Center }
                : Ui.Glyph(item.Glyph, 16, Ui.M.Text));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Ui.Text(item.Title, 14.5, true));
            if (item.Subtitle.Length > 0)
                text.Children.Add(Ui.Text(item.Subtitle, 11.5, false, Ui.M.Secondary));
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var kind = Ui.Mono(item.Kind.ToUpperInvariant(), 10, Ui.M.Secondary);
            kind.Margin = new Thickness(10, 0, 4, 0);
            Grid.SetColumn(kind, 2);
            grid.Children.Add(kind);
            var row = new Border { Child = grid, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(Ui.M.ControlRadius), Cursor = Cursors.Hand };
            row.MouseEnter += (_, _) => Select(index);
            row.MouseLeftButtonUp += (_, _) => Run(index);
            _results.Children.Add(row);
        }
        _selected = 0;
        if (_items.Count > 0)
            Select(0);
    }

    // ---------- calculator ----------

    private static readonly Regex MathLike = new(@"^[\d\s\.,\+\-\*/\(\)%\^x×÷:]+$", RegexOptions.Compiled);

    private static IEnumerable<CommandItem> Calculate(string query)
    {
        if (!MathLike.IsMatch(query) || !query.Any(c => "+-*/^x×÷%:".Contains(c)) || !query.Any(char.IsDigit))
            yield break;
        double value;
        try
        {
            value = new Calculator(query).Evaluate();
        }
        catch
        {
            yield break;
        }
        if (double.IsNaN(value) || double.IsInfinity(value))
            yield break;
        var text = Math.Abs(value) >= 1e15 ? value.ToString("E6", CultureInfo.InvariantCulture) : Math.Round(value, 10).ToString("#,0.##########", CultureInfo.GetCultureInfo("vi-VN"));
        var plain = Math.Round(value, 10).ToString(CultureInfo.InvariantCulture);
        yield return new CommandItem("= " + text, L.T("Enter để copy kết quả"), L.T("Máy tính"), "", () => CopyText(plain), Score: 100);
    }

    /// <summary>A small arithmetic parser: + - * / ^ %, brackets; "," works as a decimal point.</summary>
    private sealed class Calculator
    {
        private readonly string _s;
        private int _i;

        public Calculator(string text) => _s = text.Replace(" ", "").Replace('×', '*').Replace('x', '*').Replace('÷', '/').Replace(':', '/').Replace(',', '.');

        public double Evaluate()
        {
            var value = Sum();
            if (_i != _s.Length) throw new FormatException();
            return value;
        }

        private double Sum()
        {
            var value = Product();
            while (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-'))
                value = _s[_i++] == '+' ? value + Product() : value - Product();
            return value;
        }

        private double Product()
        {
            var value = Power();
            while (_i < _s.Length && (_s[_i] == '*' || _s[_i] == '/'))
                value = _s[_i++] == '*' ? value * Power() : value / Power();
            return value;
        }

        private double Power()
        {
            var value = Unary();
            if (_i < _s.Length && _s[_i] == '^')
            {
                _i++;
                return Math.Pow(value, Power());
            }
            return value;
        }

        private double Unary()
        {
            if (_i < _s.Length && _s[_i] == '-') { _i++; return -Unary(); }
            if (_i < _s.Length && _s[_i] == '+') { _i++; return Unary(); }
            var value = Atom();
            while (_i < _s.Length && _s[_i] == '%') { _i++; value /= 100; }
            return value;
        }

        private double Atom()
        {
            if (_i < _s.Length && _s[_i] == '(')
            {
                _i++;
                var inner = Sum();
                if (_i >= _s.Length || _s[_i] != ')') throw new FormatException();
                _i++;
                return inner;
            }
            var start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (start == _i) throw new FormatException();
            return double.Parse(_s[start.._i], CultureInfo.InvariantCulture);
        }
    }

    // ---------- units ----------

    private static readonly Dictionary<string, (string Group, double Factor, string Label)> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mm"] = ("len", 0.001, "mm"), ["cm"] = ("len", 0.01, "cm"), ["m"] = ("len", 1, "m"), ["km"] = ("len", 1000, "km"),
        ["in"] = ("len", 0.0254, "inch"), ["inch"] = ("len", 0.0254, "inch"), ["ft"] = ("len", 0.3048, "ft"), ["yd"] = ("len", 0.9144, "yd"), ["mi"] = ("len", 1609.344, "dặm"), ["dặm"] = ("len", 1609.344, "dặm"),
        ["mg"] = ("mass", 0.000001, "mg"), ["g"] = ("mass", 0.001, "g"), ["kg"] = ("mass", 1, "kg"), ["t"] = ("mass", 1000, "tấn"), ["tấn"] = ("mass", 1000, "tấn"),
        ["lb"] = ("mass", 0.45359237, "lb"), ["lbs"] = ("mass", 0.45359237, "lb"), ["oz"] = ("mass", 0.028349523125, "oz"),
        ["b"] = ("data", 1, "B"), ["kb"] = ("data", 1024, "KB"), ["mb"] = ("data", 1048576, "MB"), ["gb"] = ("data", 1073741824, "GB"), ["tb"] = ("data", 1099511627776, "TB"),
        ["ml"] = ("vol", 0.001, "ml"), ["l"] = ("vol", 1, "lít"), ["lít"] = ("vol", 1, "lít"), ["gal"] = ("vol", 3.785411784, "gallon"), ["cup"] = ("vol", 0.2365882365, "cup"),
        ["km/h"] = ("speed", 1 / 3.6, "km/h"), ["kmh"] = ("speed", 1 / 3.6, "km/h"), ["m/s"] = ("speed", 1, "m/s"), ["mph"] = ("speed", 0.44704, "mph"),
        ["c"] = ("temp", 0, "°C"), ["°c"] = ("temp", 0, "°C"), ["f"] = ("temp", 0, "°F"), ["°f"] = ("temp", 0, "°F"), ["k"] = ("temp", 0, "K"),
        ["m2"] = ("area", 1, "m²"), ["km2"] = ("area", 1e6, "km²"), ["ha"] = ("area", 10000, "ha"), ["acre"] = ("area", 4046.8564224, "acre"),
    };

    private static readonly Regex Conversion = new(@"^(-?[\d\.,]+)\s*([a-zA-Z°/²2ặấí]+)(?:\s*(?:to|sang|ra|->|=|in)\s*([a-zA-Z°/²2ặấí]+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IEnumerable<CommandItem> Convert(string query)
    {
        var m = Conversion.Match(query);
        if (!m.Success || !Units.TryGetValue(m.Groups[2].Value, out var from))
            yield break;
        if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            yield break;
        IEnumerable<(string Key, (string Group, double Factor, string Label) Unit)> targets = m.Groups[3].Success && Units.TryGetValue(m.Groups[3].Value, out var to)
            ? new[] { (m.Groups[3].Value, to) }
            : Units.Where(u => u.Value.Group == from.Group && u.Value.Label != from.Label).GroupBy(u => u.Value.Label).Select(g => (g.First().Key, g.First().Value)).Take(4);
        foreach (var (_, unit) in targets)
        {
            if (unit.Group != from.Group) continue;
            var value = from.Group == "temp" ? Temperature(amount, from.Label, unit.Label) : amount * from.Factor / unit.Factor;
            var text = $"{value.ToString(Math.Abs(value) >= 100 ? "#,0.##" : "0.####", CultureInfo.GetCultureInfo("vi-VN"))} {unit.Label}";
            yield return new CommandItem($"{m.Groups[1].Value} {from.Label} = {text}", L.T("Enter để copy"), L.T("Đổi đơn vị"), "",
                () => CopyText(value.ToString(CultureInfo.InvariantCulture)), Score: 98);
        }
    }

    private static double Temperature(double value, string from, string to)
    {
        var celsius = from switch { "°F" => (value - 32) * 5 / 9, "K" => value - 273.15, _ => value };
        return to switch { "°F" => celsius * 9 / 5 + 32, "K" => celsius + 273.15, _ => celsius };
    }

    // ---------- emoji ----------

    private static readonly (string Emoji, string Keys)[] EmojiTable =
    {
        ("😀", "cuoi smile grin happy vui"), ("😂", "cuoi ra nuoc mat laugh joy lol haha"), ("🤣", "cuoi lan rofl"), ("😊", "blush vui smile"), ("😍", "yeu love heart eyes thich"),
        ("😘", "hon kiss"), ("😎", "ngau cool sunglasses"), ("🤔", "suy nghi think hmm"), ("😴", "ngu sleep"), ("😭", "khoc cry sob"), ("😢", "buon sad tear"),
        ("😡", "gian angry rage"), ("😱", "so scream shock"), ("🥰", "yeu thuong love"), ("😅", "sweat cuoi ngai"), ("🙃", "upside nguoc"), ("😉", "wink nhay mat"),
        ("🥳", "party tiec chuc mung"), ("🤯", "mind blown"), ("😬", "grimace"), ("🙄", "eye roll"), ("😇", "angel thien than"), ("🤗", "hug om"),
        ("👍", "like ok tot thumbs up"), ("👎", "dislike thumbs down"), ("👏", "clap vo tay"), ("🙏", "pray cam on thanks please"), ("👋", "wave chao hi bye"),
        ("💪", "strong manh muscle"), ("👀", "eyes nhin"), ("✌️", "peace victory"), ("🤝", "handshake bat tay"), ("👌", "ok perfect"),
        ("❤️", "tim heart love do"), ("💔", "broken heart tan vo"), ("💯", "100 hundred"), ("🔥", "fire lua hot chay"), ("✨", "sparkle lap lanh"), ("⭐", "star sao"),
        ("🎉", "tada party chuc mung celebrate"), ("🎂", "cake banh sinh nhat birthday"), ("🎁", "gift qua"), ("✅", "check xong done tick"), ("❌", "cross x sai no"),
        ("⚠️", "warning canh bao"), ("❓", "question hoi"), ("💡", "idea y tuong bulb"), ("📌", "pin ghim"), ("📎", "clip kep"), ("📝", "note ghi chu memo"),
        ("📅", "calendar lich"), ("⏰", "alarm bao thuc clock"), ("☕", "coffee ca phe"), ("🍜", "pho mi noodle"), ("🍕", "pizza"), ("🍺", "beer bia"), ("🍚", "com rice"),
        ("🚀", "rocket ten lua launch"), ("💻", "laptop may tinh computer"), ("📱", "phone dien thoai"), ("🎵", "music nhac"), ("🎮", "game"), ("📷", "camera anh"),
        ("☀️", "sun nang mat troi"), ("🌧️", "rain mua"), ("❄️", "snow tuyet lanh"), ("🌙", "moon trang dem"), ("🌈", "rainbow cau vong"), ("🌸", "flower hoa"),
        ("🐶", "dog cho"), ("🐱", "cat meo"), ("🐷", "pig heo lon"), ("🐔", "chicken ga"), ("🦄", "unicorn"), ("🙈", "monkey khi"),
        ("🇻🇳", "vietnam viet nam co"), ("💰", "money tien"), ("📈", "chart tang"), ("📉", "chart giam"), ("🏠", "home nha"), ("🚗", "car xe oto"), ("✈️", "plane may bay"),
    };

    private static IEnumerable<CommandItem> Emoji(string query)
    {
        var q = Fold(query.TrimStart(':'));
        if (q.Length < 2)
            yield break;
        var count = 0;
        foreach (var (emoji, keys) in EmojiTable)
        {
            if (!keys.Split(' ').Any(k => k.StartsWith(q)))
                continue;
            yield return new CommandItem(emoji + "  " + keys.Split(' ')[0], L.T("Enter để copy emoji"), "Emoji", emoji, () => CopyText(emoji), Score: query.StartsWith(':') ? 97 : 30);
            if (++count == 4) yield break;
        }
    }

    // ---------- apps and files ----------

    /// <summary>Every app in the Start menu, desktop and Store alike, through shell:AppsFolder.</summary>
    private static List<(string Name, string Launch)> LoadApps()
    {
        var result = new List<(string, string)>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application")!;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string path = item.Path;
                if (string.IsNullOrWhiteSpace(name) || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Gỡ cài đặt"))
                    continue;
                result.Add((name, @"shell:AppsFolder\" + path));
            }
        }
        catch (Exception ex)
        {
            Store.Log("app list failed: " + ex.Message);
        }
        return result.DistinctBy(a => a.Item1).ToList();
    }

    private static List<(string Name, string Path)> LoadRecent()
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(150)
                .Select(f => (Path.GetFileNameWithoutExtension(f.Name), f.FullName)).ToList();
        }
        catch
        {
            return new List<(string, string)>();
        }
    }

    public static void Launch(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Store.Log("copy failed: " + ex.Message);
        }
    }

    // ---------- icons ----------

    private static BitmapSource? LoadIcon(string source)
    {
        if (IconCache.TryGetValue(source, out var cached))
            return cached;
        BitmapSource? icon = null;
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            if (SHCreateItemFromParsingName(source, IntPtr.Zero, ref iid, out var factory) == 0)
            {
                // SIIGBF_RESIZETOFIT | SIIGBF_ICONONLY-ish: a 32 px icon with transparency.
                if (factory.GetImage(new SIZE { cx = 32, cy = 32 }, 0x0, out var hbitmap) == 0)
                {
                    icon = FromHBitmap(hbitmap);
                    DeleteObject(hbitmap);
                }
                Marshal.ReleaseComObject(factory);
            }
        }
        catch
        {
        }
        IconCache[source] = icon;
        return icon;
    }

    /// <summary>Copies a 32-bit premultiplied bitmap keeping its alpha (CreateBitmapSourceFromHBitmap drops it).</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        var bitmap = new BITMAP();
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), ref bitmap) == 0 || bitmap.bmBits == IntPtr.Zero || bitmap.bmBitsPixel != 32)
        {
            var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }
        var stride = bitmap.bmWidthBytes;
        var pixels = new byte[stride * bitmap.bmHeight];
        // DIB sections are stored bottom-up; copy rows in reverse.
        for (var y = 0; y < bitmap.bmHeight; y++)
            Marshal.Copy(bitmap.bmBits + (bitmap.bmHeight - 1 - y) * stride, pixels, y * stride, stride);
        var source = BitmapSource.Create(bitmap.bmWidth, bitmap.bmHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, ref BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
