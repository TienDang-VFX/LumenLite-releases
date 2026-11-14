using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace LumenLite;

/// <summary>Name and icon of a running process, cached by executable path.</summary>
internal static class AppInfo
{
    private static readonly ConcurrentDictionary<string, (string Name, BitmapSource? Icon)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static (string Name, BitmapSource? Icon) Of(int pid)
    {
        var path = ExePath(pid);
        if (path == null)
        {
            try
            {
                return (Process.GetProcessById(pid).ProcessName, null);
            }
            catch
            {
                return (L.F($"Tiến trình {pid}"), null);
            }
        }
        return Cache.GetOrAdd(path, p =>
        {
            string name;
            try
            {
                var description = FileVersionInfo.GetVersionInfo(p).FileDescription;
                name = string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(p) : description.Trim();
            }
            catch
            {
                name = Path.GetFileNameWithoutExtension(p);
            }
            return (name, FileIcon(p));
        });
    }

    /// <summary>Works for most processes, including elevated ones, unlike Process.MainModule.</summary>
    public static string? ExePath(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static BitmapSource? FileIcon(string path)
    {
        var info = new Native.SHFILEINFO();
        Native.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
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

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
