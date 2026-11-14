using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace LumenLite;

public sealed record RunningApp(int Pid, string Name, string? Path, IntPtr Window);
public sealed record InstalledApp(string Name, string? Publisher, string? Version, long SizeBytes, string? Icon, string Uninstall, string? Location, string Key);
public sealed record UpgradeItem(string Name, string Id, string Version, string Available);
public sealed record ListeningPort(string Protocol, int Port, string Address, int Pid, string Process);

/// <summary>Running apps, installed apps, winget updates and listening ports.</summary>
internal static class AppManager
{
    // ---------- running apps ----------

    /// <summary>Apps with a visible top-level window, one entry per process.</summary>
    public static List<RunningApp> Running()
    {
        var own = Environment.ProcessId;
        var seen = new Dictionary<int, RunningApp>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero || GetWindowTextLength(hwnd) == 0)
                return true;
            var exStyle = GetWindowLongPtr(hwnd, -20).ToInt64();
            if ((exStyle & 0x80) != 0) // tool windows are not apps
                return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0)
                return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == own || seen.ContainsKey((int)pid))
                return true;
            var path = AppInfo.ExePath((int)pid);
            var file = path == null ? "" : System.IO.Path.GetFileName(path).ToLowerInvariant();
            // The shell and system hosts are not things to quit.
            if (file is "explorer.exe" or "applicationframehost.exe" or "textinputhost.exe" or "shellexperiencehost.exe" or "searchhost.exe" or "startmenuexperiencehost.exe" or "lockapp.exe")
                return true;
            var (name, _) = AppInfo.Of((int)pid);
            seen[(int)pid] = new RunningApp((int)pid, name, path, hwnd);
            return true;
        }, IntPtr.Zero);
        return seen.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Asks every window of the app to close, as clicking its X would; the app can still ask to save.</summary>
    public static void Quit(RunningApp app)
    {
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == app.Pid && IsWindowVisible(hwnd))
                PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    public static int ForegroundPid()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return (int)pid;
    }

    // ---------- installed apps ----------

    public static List<InstalledApp> Installed()
    {
        var result = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryKey root, string path)
        {
            using var key = root.OpenSubKey(path);
            if (key == null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                if (app == null) continue;
                var name = app.GetValue("DisplayName") as string;
                var uninstall = app.GetValue("UninstallString") as string;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uninstall)) continue;
                if (app.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (app.GetValue("ParentKeyName") != null) continue; // updates and patches
                var size = app.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;
                var icon = (app.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"');
                result.TryAdd(name.Trim(), new InstalledApp(name.Trim(), app.GetValue("Publisher") as string, app.GetValue("DisplayVersion") as string,
                    size, icon, uninstall, app.GetValue("InstallLocation") as string, sub));
            }
        }
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        Read(Registry.LocalMachine, path);
        Read(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        Read(Registry.CurrentUser, path);
        return result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Starts the app's own uninstaller; Windows asks for admin rights when it needs them.</summary>
    public static Process? StartUninstall(InstalledApp app)
    {
        var command = app.Uninstall.Trim();
        string file, args;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            file = command[1..end];
            args = command[(end + 1)..].Trim();
        }
        else if (command.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase))
        {
            file = "msiexec.exe";
            // /I opens repair-or-remove; /X goes straight to removal.
            args = command[command.IndexOf(' ')..].Trim().Replace("/I{", "/X{", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            file = exe > 0 ? command[..(exe + 4)] : command;
            args = exe > 0 ? command[(exe + 4)..].Trim() : "";
        }
        return Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
    }

    /// <summary>Folders an app usually leaves behind in AppData and ProgramData.</summary>
    public static List<string> Leftovers(InstalledApp app)
    {
        // Only the app's full name: a first word ("Microsoft", "Adobe") names a vendor folder shared by other apps.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { app.Name };
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        var found = new List<string>();
        foreach (var root in roots)
        {
            foreach (var name in names)
            {
                var direct = Path.Combine(root, name);
                if (Directory.Exists(direct)) found.Add(direct);
                if (app.Publisher is { Length: > 0 } publisher)
                {
                    var nested = Path.Combine(root, publisher, name);
                    if (Directory.Exists(nested)) found.Add(nested);
                }
            }
        }
        if (app.Location is { Length: > 3 } location && Directory.Exists(location))
            found.Add(location);
        // Never offer a shared folder itself (an install location of "C:\Program Files", say), whatever the app says.
        var shared = new[]
        {
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop,
        }.Select(f => Environment.GetFolderPath(f).TrimEnd('\\')).Where(f => f.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return found.Select(p => Path.GetFullPath(p).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => p.Count(c => c == '\\') >= 3 && !shared.Contains(p)).ToList();
    }

    // ---------- winget ----------

    public static bool WingetAvailable => FindWinget() != null;

    private static string? FindWinget()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Lists packages winget can update. Parses winget's table output.</summary>
    public static async Task<List<UpgradeItem>> UpgradesAsync()
    {
        var output = await RunWingetAsync("upgrade --disable-interactivity");
        var lines = output.Replace("\r", "").Split('\n')
            .Select(l => l.Contains('\b') ? l[(l.LastIndexOf('\b') + 1)..] : l).ToList();
        var dashes = lines.FindIndex(l => l.StartsWith("----"));
        if (dashes < 1)
            return new List<UpgradeItem>();
        var header = lines[dashes - 1];
        // Columns start where the header words do: Name, Id, Version, Available, Source.
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
                starts.Add(i);
        var result = new List<UpgradeItem>();
        foreach (var line in lines.Skip(dashes + 1))
        {
            if (string.IsNullOrWhiteSpace(line) || starts.Count < 4 || line.Length < starts[3])
                break;
            string Col(int i)
            {
                var start = starts[i];
                var end = i + 1 < starts.Count ? starts[i + 1] : line.Length;
                return start >= line.Length ? "" : line[start..Math.Min(end, line.Length)].Trim();
            }
            var name = Col(0);
            var id = Col(1);
            if (id.Length == 0 || name.EndsWith("upgrades available.", StringComparison.OrdinalIgnoreCase))
                break;
            result.Add(new UpgradeItem(name.TrimEnd('…'), id, Col(2), Col(3)));
        }
        return result;
    }

    /// <summary>Updates one package (or all when id is null) in a visible winget window.</summary>
    public static Process? Upgrade(string? id)
    {
        var winget = FindWinget();
        if (winget == null)
            return null;
        var args = id == null ? "upgrade --all" : $"upgrade --id \"{id}\" --exact";
        // A console window lets the user follow progress and answer installer prompts.
        return Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{winget}\" {args} --accept-package-agreements & echo. & pause\"") { UseShellExecute = true });
    }

    private static async Task<string> RunWingetAsync(string args)
    {
        var winget = FindWinget() ?? throw new InvalidOperationException(L.T("Không tìm thấy winget (App Installer)."));
        var info = new ProcessStartInfo(winget, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    // ---------- ports ----------

    public static List<ListeningPort> Ports()
    {
        var result = new List<ListeningPort>();
        foreach (var family in new[] { 2, 23 })
        {
            var (rows, size) = Table(family, true);
            foreach (var (port, address, pid) in rows)
                result.Add(new ListeningPort(family == 2 ? "TCP" : "TCPv6", port, address, pid, ProcessName(pid)));
            _ = size;
        }
        return result.OrderBy(p => p.Port).ThenBy(p => p.Protocol).ToList();
    }

    private static string ProcessName(int pid)
    {
        if (pid == 0) return "System Idle";
        if (pid == 4) return "System";
        try
        {
            return AppInfo.Of(pid).Name;
        }
        catch
        {
            return $"PID {pid}";
        }
    }

    private static (List<(int Port, string Address, int Pid)>, int) Table(int family, bool tcp)
    {
        var rows = new List<(int, string, int)>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, family, 3, 0) != 0)
                return (rows, size);
            var count = Marshal.ReadInt32(buffer);
            if (family == 2)
            {
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * 24;
                    var addr = new System.Net.IPAddress((uint)Marshal.ReadInt32(row + 4)).ToString();
                    var port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)Marshal.ReadInt16(row + 8));
                    rows.Add((port, addr, Marshal.ReadInt32(row + 20)));
                }
            }
            else
            {
                // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * 56;
                    var bytes = new byte[16];
                    Marshal.Copy(row, bytes, 0, 16);
                    var addr = new System.Net.IPAddress(bytes).ToString();
                    var port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)Marshal.ReadInt16(row + 20));
                    rows.Add((port, addr, Marshal.ReadInt32(row + 52)));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return (rows, size);
    }

    // ---------- native ----------

    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
}
