using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace LumenLite;

/// <summary>
/// Blacks out every screen and ignores the keyboard so it can be wiped. The
/// mouse keeps working: a button ends it, and it ends by itself after three
/// minutes. Ctrl+Alt+Del is handled by Windows and always gets through.
/// </summary>
internal static class CleaningMode
{
    private static readonly List<Window> Windows = new();
    private static IntPtr _hook;
    private static LowLevelKeyboardProc? _proc; // kept alive while the hook is installed
    private static DispatcherTimer? _timeout;

    public static bool IsOn => _hook != IntPtr.Zero;

    public static void Start()
    {
        if (IsOn)
            return;
        foreach (var screen in WinForms.Screen.AllScreens)
            Windows.Add(Cover(screen));

        _proc = (code, message, data) => code >= 0 ? new IntPtr(1) : CallNextHookEx(IntPtr.Zero, code, message, data);
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            Store.Log("cleaning mode keyboard hook failed: " + Marshal.GetLastWin32Error());

        _timeout = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _timeout.Tick += (_, _) => Stop();
        _timeout.Start();
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
        _timeout?.Stop();
        foreach (var window in Windows)
            window.Close();
        Windows.Clear();
    }

    private static Window Cover(WinForms.Screen screen)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (screen.Primary)
        {
            var icon = Material.Display.Glyph("", 44);
            icon.Foreground = Material.Display.Secondary;
            panel.Children.Add(icon);
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Đang lau phím — bàn phím đã khóa"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = Material.Display.Text,
                Margin = new Thickness(0, 16, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Bấm nút bên dưới bằng chuột để kết thúc. Tự tắt sau 3 phút."),
                FontSize = 13,
                Foreground = Material.Display.Secondary,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            var done = Material.Display.Pressable(new TextBlock { Text = "Xong", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Material.Display.OnAccent }, Stop, new Thickness(28, 10, 28, 10));
            done.Background = Material.Display.Accent;
            done.CornerRadius = new CornerRadius(Material.Display.ControlRadius);
            done.HorizontalAlignment = HorizontalAlignment.Center;
            done.Margin = new Thickness(0, 24, 0, 0);
            done.MouseLeave += (_, _) => done.Background = Material.Display.Accent;
            panel.Children.Add(done);
        }

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Background = Brushes.Black,
            FontFamily = Theme.Font,
            Content = panel,
            Left = 0,
            Top = 0,
            Width = 100,
            Height = 100,
        };
        // Placed in physical pixels so it covers the screen whatever its scaling.
        window.SourceInitialized += (_, _) =>
        {
            var b = screen.Bounds;
            Native.SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), b.Left, b.Top, b.Width, b.Height, 0);
        };
        window.Show();
        if (screen.Primary)
            window.Activate();
        return window;
    }

    private const int WH_KEYBOARD_LL = 13;

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
