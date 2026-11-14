using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LumenLite;

public sealed record RemotePeer(string Id, string Host, string User, string Platform, DateTime LastUsed, bool Showing);

/// <summary>
/// RustDesk's saved machines, read from its config folder (only the ID, host name,
/// user name and platform; passwords are never touched), and switching between
/// them with RustDesk's own "--connect" command, which brings an open session's
/// tab to the front or starts a new connection.
/// </summary>
internal static class RustDesk
{
    private static readonly string Peers = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RustDesk", "config", "peers");

    public static string? Exe()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var path = Path.Combine(root, "RustDesk", "RustDesk.exe");
            if (File.Exists(path))
                return path;
        }
        // A portable copy that is running.
        try
        {
            return Process.GetProcessesByName("rustdesk").Select(p => AppInfo.ExePath(p.Id)).FirstOrDefault(p => p != null);
        }
        catch
        {
            return null;
        }
    }

    public static bool Available => Exe() != null && Directory.Exists(Peers);

    // The machine last switched to from LumenLite. RustDesk opens machines as tabs of one window whose title keeps the
    // first machine's name, so the title cannot tell which tab is on screen; the pick can.
    private static string? _picked;
    private static DateTime _pickedAt;

    /// <summary>
    /// The machine on screen now: the one last picked here while a RustDesk remote window is up (not minimized), or,
    /// with no pick, the one that window's title names ("ID - Remote Desktop - RustDesk", or "ID@host - …"). Null when
    /// every remote window is minimized. A switch made inside RustDesk itself is not seen.
    /// </summary>
    public static string? ShowingId()
    {
        // Just picked: the window may not be up yet.
        if (_picked != null && DateTime.UtcNow - _pickedAt < TimeSpan.FromSeconds(4))
            return _picked;
        return TopRemote() is { } top ? _picked ?? top.Id : null;
    }

    /// <summary>The RustDesk remote window highest in z-order that is not minimized, and the machine its title names.</summary>
    private static (IntPtr Window, string Id)? TopRemote()
    {
        (IntPtr, string)? found = null;
        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window) && !IsIconic(window) && IsRustDesk(window) && RemoteId(Title(window)) is { } id)
            {
                found = (window, id);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string? RemoteId(string title)
    {
        var match = Regex.Match(title, @"^(\d+)(@|\s)");
        return match.Success && title.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase) ? match.Groups[1].Value : null;
    }

    /// <summary>Saved machines, most recently used first.</summary>
    public static List<RemotePeer> List()
    {
        var result = new List<RemotePeer>();
        if (!Directory.Exists(Peers))
            return result;
        var showing = ShowingId();
        foreach (var file in new DirectoryInfo(Peers).EnumerateFiles("*.toml"))
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            string text;
            try
            {
                text = File.ReadAllText(file.FullName);
            }
            catch
            {
                continue;
            }
            string Value(string key)
            {
                var m = Regex.Match(text, @"(?m)^\s*" + key + @"\s*=\s*['""]?([^'""\r\n]*)");
                return m.Success ? m.Groups[1].Value.Trim() : "";
            }
            result.Add(new RemotePeer(id, Value("hostname"), Value("username"), Value("platform"), file.LastWriteTime, id == showing));
        }
        return result.OrderByDescending(p => p.LastUsed).ToList();
    }

    /// <summary>Shows the machine: its tab if it is open, otherwise a new connection.</summary>
    public static void Connect(string id)
    {
        var exe = Exe() ?? throw new InvalidOperationException(L.T("Không tìm thấy RustDesk."));
        _picked = id;
        _pickedAt = DateTime.UtcNow;
        // "--connect" switches the tab but leaves a minimized window (as a second press leaves it) on the taskbar:
        // bring the remote window back first.
        RestoreRemoteWindows();
        // Lets RustDesk bring its remote window to the front instead of only flashing on the taskbar.
        AllowSetForegroundWindow(-1);
        Process.Start(new ProcessStartInfo(exe, $"--connect {id}") { UseShellExecute = false });
    }

    private static void RestoreRemoteWindows()
    {
        EnumWindows((window, _) =>
        {
            // RustDesk's window ignores ShowWindow(SW_RESTORE) from another process; the system command restores it.
            if (IsWindowVisible(window) && IsIconic(window) && IsRustDesk(window) && RemoteId(Title(window)) != null)
                PostMessage(window, WM_SYSCOMMAND, new IntPtr(SC_RESTORE), IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    private static bool IsRustDesk(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("rustdesk", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// One button per machine: the first press shows it; pressing again while
    /// its screen is the window in front on its monitor minimizes RustDesk, back to the desktop.
    /// True when it minimized.
    /// </summary>
    public static bool Toggle(string id)
    {
        // "In front" is per monitor: with RustDesk on one screen and the last-clicked app on another, the app sits
        // above RustDesk in z-order, yet the machine is plainly on screen.
        if (TopRemote() is { } top && ShowingId() == id
            && FrontWindow(MonitorFromWindow(top.Window, MONITOR_DEFAULTTONEAREST)) == top.Window)
        {
            ShowWindow(top.Window, SW_MINIMIZE);
            return true;
        }
        Connect(id);
        return false;
    }

    /// <summary>
    /// The application window the person sees in front on that monitor: the first in z-order that
    /// is shown, not minimized, not cloaked, not always-on-top (the taskbar, the
    /// Island) and not LumenLite's own (the radial menu has the focus as it is used).
    /// </summary>
    private static IntPtr? FrontWindow(IntPtr monitor)
    {
        IntPtr? found = null;
        var self = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window))
                return true;
            if (MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST) != monitor)
                return true;
            GetWindowThreadProcessId(window, out var pid);
            if (pid == self)
                return true;
            var style = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
            if ((style & (WS_EX_TOOLWINDOW | WS_EX_TOPMOST)) != 0)
                return true;
            if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            if (Title(window).Length == 0)
                return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static string Title(IntPtr window)
    {
        var text = new System.Text.StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int GWL_EXSTYLE = -20, SW_MINIMIZE = 6, DWMWA_CLOAKED = 14, WM_SYSCOMMAND = 0x0112, SC_RESTORE = 0xF120;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int length);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    /// <summary>Moves on to the next machine used in the last day, after the one on screen.</summary>
    public static RemotePeer? Next()
    {
        var recent = List().Where(p => DateTime.Now - p.LastUsed < TimeSpan.FromDays(1)).OrderBy(p => p.Id).ToList();
        if (recent.Count == 0)
            recent = List().Take(3).OrderBy(p => p.Id).ToList();
        if (recent.Count == 0)
            return null;
        var index = recent.FindIndex(p => p.Showing);
        var next = recent[(index + 1) % recent.Count];
        Connect(next.Id);
        return next;
    }

    public static void OpenApp()
    {
        AllowSetForegroundWindow(-1);
        if (Exe() is { } exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
