using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LumenLite.Widgets;

public sealed class LauncherView : DockPanel
{
    private readonly WrapPanel _tiles = new();
    private readonly TextBlock _hint = new()
    {
        Text = L.T("Kéo thả app, thư mục, file hoặc link vào đây"),
        Foreground = Material.Display.Secondary,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private static List<LaunchItem> Items => Store.S.Launcher ??= DefaultItems();

    public LauncherView()
    {
        var header = Material.Display.Caption("Launcher");
        header.Margin = new Thickness(0, 0, 0, 8);
        SetDock(header, Dock.Top);
        Children.Add(header);

        var panel = new StackPanel();
        panel.Children.Add(_tiles);
        panel.Children.Add(_hint);
        Children.Add(new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        // A transparent background makes the empty space accept drops too.
        Background = Brushes.Transparent;
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text)
                ? DragDropEffects.Link
                : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += OnDrop;

        Rebuild();
    }

    private static List<LaunchItem> DefaultItems()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return new List<LaunchItem>
        {
            new() { Name = "File Explorer", Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") },
            new() { Name = "Downloads", Target = downloads },
            new() { Name = L.T("Cài đặt"), Target = "ms-settings:" },
        };
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths)
                Add(DisplayName(path), path);
        }
        else if (e.Data.GetData(DataFormats.Text) is string text && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Add(uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host, uri.ToString());
        }
        e.Handled = true;
    }

    private void Add(string name, string target)
    {
        if (Items.Exists(i => string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase)))
            return;
        Items.Add(new LaunchItem { Name = name, Target = target });
        Store.SaveSoon();
        Rebuild();
    }

    private void Rebuild()
    {
        _tiles.Children.Clear();
        foreach (var item in Items)
            _tiles.Children.Add(CreateTile(item));
        _hint.Visibility = Items.Count < 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement CreateTile(LaunchItem item)
    {
        var stack = new StackPanel { Width = 64 };
        UIElement icon = LoadIcon(item.Target) is { } image
            ? new Image { Source = image, Width = 32, Height = 32 }
            : Material.Display.Glyph("", 26); // globe
        stack.Children.Add(new Border { Height = 34, Child = icon });
        stack.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 30,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var tile = Material.Display.Pressable(stack, () => Launch(item), new Thickness(6));
        tile.Margin = new Thickness(0, 0, 4, 4);
        tile.ToolTip = item.Target;

        var menu = new ContextMenu();
        var open = new MenuItem { Header = L.T("Mở vị trí") , IsEnabled = File.Exists(item.Target) || Directory.Exists(item.Target) };
        open.Click += (_, _) => Start("explorer.exe", $"/select,\"{item.Target}\"");
        var remove = new MenuItem { Header = L.T("Xóa khỏi launcher") };
        remove.Click += (_, _) =>
        {
            Items.Remove(item);
            Store.SaveSoon();
            Rebuild();
        };
        menu.Items.Add(open);
        menu.Items.Add(remove);
        tile.ContextMenu = menu;
        return tile;
    }

    private static void Launch(LaunchItem item)
    {
        var dir = File.Exists(item.Target) ? Path.GetDirectoryName(item.Target) : null;
        Start(item.Target, null, dir);
    }

    private static void Start(string target, string? args, string? workingDir = null)
    {
        try
        {
            var info = new ProcessStartInfo(target) { UseShellExecute = true };
            if (args != null) info.Arguments = args;
            if (workingDir != null) info.WorkingDirectory = workingDir;
            Process.Start(info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.F($"Không mở được \"{target}\".\n\n{ex.Message}"), "LumenLite", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string DisplayName(string path)
    {
        if (Directory.Exists(path))
            return new DirectoryInfo(path).Name is { Length: > 0 } name ? name : path;
        return Path.GetFileNameWithoutExtension(path);
    }

    private static BitmapSource? LoadIcon(string target)
    {
        if (!File.Exists(target) && !Directory.Exists(target))
            return null;
        var info = new Native.SHFILEINFO();
        Native.SHGetFileInfo(target, 0, ref info, (uint)Marshal.SizeOf(info), Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
        if (info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            Native.DestroyIcon(info.hIcon);
        }
    }
}
