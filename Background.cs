using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LumenLite;

/// <summary>
/// Things that run without a widget: global shortcuts, cleaning tracking junk
/// out of copied links, and noticing finished downloads. Results show in the
/// Dynamic Island. Create on the UI thread.
/// </summary>
internal sealed class Background : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    private const int HotkeyMic = 1, HotkeyOutput = 2, HotkeyPanel = 3, HotkeyCommandBar = 4, HotkeyRadial = 5;
    private const uint VK_SPACE = 0x20;

    private readonly IslandWindow _island;
    private readonly HwndSource _window;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private FileSystemWatcher? _downloads;
    private readonly Dictionary<string, DateTime> _announced = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastCleaned;
    private bool _notifyPending;

    public Background(IslandWindow island)
    {
        _island = island;
        // A message-only window: receives hotkeys and clipboard notices, never shown.
        _window = new HwndSource(new HwndSourceParameters("LumenLite background") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        _window.AddHook(WndProc);

        // Another app may already own a shortcut; then the Shift variant is tried.
        MicHotkey = Register(HotkeyMic, 'M');
        OutputHotkey = Register(HotkeyOutput, 'O');
        PanelHotkey = Register(HotkeyPanel, 'V');
        CommandBarHotkey = Register(HotkeyCommandBar, (char)VK_SPACE, "Space");
        RadialHotkey = Register(HotkeyRadial, 'R');

        // Devices come and go: keep the preferred microphone and watch for headphones being unplugged.
        _devices.Tick += (_, _) => WatchDevices();
        _devices.Start();
        WatchDevices();
        AddClipboardFormatListener(_window.Handle);
        DownloadAlerts = Store.S.DownloadAlerts;
    }

    public bool DownloadAlerts
    {
        get => _downloads != null;
        set
        {
            _downloads?.Dispose();
            _downloads = null;
            if (!value)
                return;
            try
            {
                var folder = DownloadsFolder();
                if (folder == null || !Directory.Exists(folder))
                    return;
                _downloads = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size };
                _downloads.Created += (_, e) => _ = CheckCreatedAsync(e.FullPath);
                _downloads.Renamed += (_, e) =>
                {
                    if (IsPartial(e.OldFullPath) && !IsPartial(e.FullPath))
                        Announce(e.FullPath);
                };
                _downloads.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Store.Log("downloads watcher failed: " + ex.Message);
            }
        }
    }

    /// <summary>The shortcut in use, e.g. "Ctrl+Alt+M", or null when both variants are taken.</summary>
    public string? MicHotkey { get; }
    public string? OutputHotkey { get; }
    public string? PanelHotkey { get; }
    public string? CommandBarHotkey { get; }
    public string? RadialHotkey { get; }
    public Action? OpenRadial { get; set; }

    public Action? OpenPanel { get; set; }
    public Action? OpenCommandBar { get; set; }

    private readonly DispatcherTimer _devices = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _lastOutputId;
    private bool _lastOutputWasHeadphones;
    private string _lastInputs = "";

    private string? Register(int id, char key, string? label = null)
    {
        var name = label ?? key.ToString();
        if (RegisterHotKey(_window.Handle, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, key))
            return $"Ctrl+Alt+{name}";
        if (RegisterHotKey(_window.Handle, id, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, key))
            return $"Ctrl+Alt+Shift+{name}";
        Store.Log($"hotkey Ctrl+Alt+{name} and Ctrl+Alt+Shift+{name} are taken by other apps");
        return null;
    }

    private void WatchDevices()
    {
        try
        {
            // Headphones unplugged: Windows falls back to the speakers at the old volume.
            var outputs = Audio.Outputs();
            var current = Audio.DefaultId();
            if (_lastOutputId != null && current != _lastOutputId && _lastOutputWasHeadphones
                && outputs.All(o => o.Id != _lastOutputId) && Store.S.LowerOnUnplug)
            {
                if (Audio.Volume() is { } volume && volume.Level > 0.25f)
                    Audio.SetVolume(0.25f);
                _island.Notify("", L.T("Đã rút tai nghe, giảm âm lượng còn 25%"));
            }
            var device = outputs.FirstOrDefault(o => o.Id == current);
            _lastOutputId = current;
            _lastOutputWasHeadphones = device?.FormFactor is AudioFormFactor.Headphones or AudioFormFactor.Headset;

            // Preferred microphone: switch to it whenever it shows up.
            if (Store.S.PreferredMic is { } preferred)
            {
                var inputs = Audio.Inputs();
                var signature = string.Join("|", inputs.Select(i => i.Id));
                if (signature != _lastInputs)
                {
                    _lastInputs = signature;
                    if (inputs.Any(i => i.Id == preferred) && Audio.DefaultInputId() != preferred)
                    {
                        Audio.SetDefault(preferred);
                        _island.Notify("", "Micro: " + inputs.First(i => i.Id == preferred).Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Store.Log("device watch failed: " + ex.Message);
            _devices.Interval = TimeSpan.FromSeconds(10);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            handled = true;
            try
            {
                if (wParam.ToInt32() == HotkeyMic)
                    ToggleMic();
                else if (wParam.ToInt32() == HotkeyOutput)
                    NextOutput();
                else if (wParam.ToInt32() == HotkeyPanel)
                    OpenPanel?.Invoke();
                else if (wParam.ToInt32() == HotkeyCommandBar)
                    OpenCommandBar?.Invoke();
                else if (wParam.ToInt32() == HotkeyRadial)
                    OpenRadial?.Invoke();
            }
            catch (Exception ex)
            {
                Store.Log("hotkey failed: " + ex.Message);
            }
        }
        else if (msg == WM_CLIPBOARDUPDATE && Store.S.CleanLinks)
        {
            // Read after the copying app has finished with the clipboard.
            _dispatcher.BeginInvoke(() => CleanClipboard(0), DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    public void ToggleMic()
    {
        if (Audio.MicVolume() is not { } mic)
        {
            _island.Notify("", L.T("Không có micro"));
            return;
        }
        Audio.SetMicMute(!mic.Muted);
        _island.Notify(mic.Muted ? "" : "", mic.Muted ? L.T("Đã bật micro") : L.T("Đã tắt micro"));
    }

    public void NextOutput()
    {
        var name = Audio.NextOutput();
        _island.Notify("", name == null ? L.T("Không có thiết bị phát") : L.T("Đầu ra: ") + name);
    }

    // ---------- clean links ----------

    private static readonly Regex TrackingParam = new(
        @"^(utm_\w+|fbclid|gclid|dclid|gbraid|wbraid|msclkid|yclid|twclid|ttclid|igshid|igsh|mc_cid|mc_eid|_hsenc|_hsmi|mkt_tok|vero_id|oly_anon_id|oly_enc_id|ref_src|ref_url|s_cid|__s|spm|scm|share_source|xmt|wt_mc|_ga|_gl)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "si" (share id) is only tracking on these sites; elsewhere it can be a real parameter.
    private static readonly string[] ShareIdHosts = { "youtube.com", "youtu.be", "spotify.com", "music.youtube.com" };

    private void CleanClipboard(int attempt)
    {
        try
        {
            if (!Clipboard.ContainsText())
                return;
            var text = Clipboard.GetText().Trim();
            if (text == _lastCleaned)
            {
                // SetText can report a locked clipboard after it has in fact written the text.
                if (_notifyPending)
                    Cleaned();
                return;
            }
            if (text.Length > 2048 || text.Any(char.IsWhiteSpace))
                return;
            var cleaned = CleanUrl(text);
            if (cleaned == null)
                return;
            _lastCleaned = cleaned;
            _notifyPending = true;
            Clipboard.SetText(cleaned);
            Cleaned();
        }
        catch (System.Runtime.InteropServices.COMException) when (attempt < 4)
        {
            // The clipboard is often briefly locked by the app that wrote it (or by clipboard history).
            var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            retry.Tick += (_, _) =>
            {
                retry.Stop();
                CleanClipboard(attempt + 1);
            };
            retry.Start();
        }
        catch (Exception ex)
        {
            Store.Log("clean link skipped: " + ex.Message);
        }
    }

    private void Cleaned()
    {
        _notifyPending = false;
        _island.Notify("", L.T("Đã bỏ mã theo dõi khỏi link"));
    }

    /// <summary>The URL without tracking parameters, or null when there was nothing to remove.</summary>
    public static string? CleanUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.Query.Length < 2)
            return null;
        var host = uri.Host.ToLowerInvariant();
        var shareHost = ShareIdHosts.Any(h => host == h || host.EndsWith("." + h));
        var parts = uri.Query[1..].Split('&');
        var kept = parts.Where(part =>
        {
            var name = Uri.UnescapeDataString(part.Split('=')[0]);
            return !(TrackingParam.IsMatch(name) || (shareHost && name.Equals("si", StringComparison.OrdinalIgnoreCase)));
        }).ToArray();
        if (kept.Length == parts.Length)
            return null;
        var builder = new UriBuilder(uri) { Query = string.Join("&", kept) };
        var result = builder.Uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
        // UriBuilder adds the default port and keeps a lone "?"; neither was in the original.
        if (uri.IsDefaultPort)
            result = result.Replace(":" + uri.Port + "/", "/");
        return result.Replace("?#", "#").TrimEnd('?');
    }

    // ---------- downloads ----------

    private static bool IsPartial(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileName(path);
        return ext is ".crdownload" or ".part" or ".tmp" or ".download" or ".partial" or ".opdownload"
            || name.StartsWith("~$") || name.StartsWith(".");
    }

    /// <summary>Browsers that write the final name directly: wait until the file stops growing.</summary>
    private async Task CheckCreatedAsync(string path)
    {
        if (IsPartial(path))
            return;
        long last = -1;
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1500);
            long size;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return;
                size = info.Length;
            }
            catch
            {
                return;
            }
            if (size > 0 && size == last)
            {
                Announce(path);
                return;
            }
            last = size;
        }
    }

    private void Announce(string path) => _dispatcher.BeginInvoke(() =>
    {
        var now = DateTime.Now;
        if (_announced.TryGetValue(path, out var when) && now - when < TimeSpan.FromSeconds(30))
            return;
        _announced[path] = now;
        _island.Notify("", L.T("Đã tải xong: ") + Path.GetFileName(path), 4);
    });

    private static string? DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer) != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    public void Dispose()
    {
        UnregisterHotKey(_window.Handle, HotkeyMic);
        UnregisterHotKey(_window.Handle, HotkeyOutput);
        UnregisterHotKey(_window.Handle, HotkeyPanel);
        UnregisterHotKey(_window.Handle, HotkeyCommandBar);
        UnregisterHotKey(_window.Handle, HotkeyRadial);
        _devices.Stop();
        RemoveClipboardFormatListener(_window.Handle);
        _downloads?.Dispose();
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
