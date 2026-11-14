using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace LumenLite;

/// <summary>
/// Keeps LumenLite up to date from the public releases repository (the source stays private): a minute after start
/// and every six hours it reads the latest release, downloads LumenLite.exe when it is newer, checks it, and puts it
/// in place of the running file (Windows lets a running program's file be renamed). The new version runs from the
/// next start, or at once when the person restarts LumenLite from the notice, Settings or the Command Bar.
/// </summary>
internal static class Updater
{
    /// <summary>
    /// The GitHub "owner/name" this build updates from, stamped in at publish time (release.ps1 passes
    /// -p:UpdateRepository=...). Empty for a local build, so a build you made yourself is never replaced by someone
    /// else's release.
    /// </summary>
    public static string Repository { get; } = Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value ?? "";

    public static bool Enabled => Repository.Length > 0;
    private const string AssetName = "LumenLite.exe";

    public enum State { Idle, Checking, UpToDate, Downloading, Ready, Failed }

    public static State Status { get; private set; } = State.Idle;
    /// <summary>The version waiting for a restart, or the one being downloaded.</summary>
    public static Version? Pending { get; private set; }
    public static string Message { get; private set; } = "";
    public static DateTime? LastCheck { get; private set; }

    /// <summary>Raised on the UI thread when the state changes.</summary>
    public static event Action? Changed;
    /// <summary>Raised on the UI thread when a new version is in place and waits for a restart.</summary>
    public static event Action<Version>? Ready;

    public static Version Current { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    private static readonly HttpClient Http = CreateClient();
    private static DispatcherTimer? _timer;
    private static Dispatcher? _dispatcher;
    private static int _busy;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LumenLite-Updater/" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0"));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static void Start()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        CleanUp();
        // The first check (and any download) waits until well after start-up.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(6);
            if (Store.S.AutoUpdate)
                _ = CheckAsync();
        };
        _timer.Start();
    }

    /// <summary>Looks for a newer release and, when there is one, puts it in place.</summary>
    public static async Task CheckAsync()
    {
        if (!Enabled)
        {
            Set(State.Failed, L.T("Bản tự build: tự cập nhật đã tắt."));
            return;
        }
        if (Status == State.Ready || System.Threading.Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            Set(State.Checking, L.T("Đang kiểm tra…"));
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            if (!response.IsSuccessStatusCode)
            {
                Set(State.Failed, response.StatusCode == System.Net.HttpStatusCode.NotFound ? L.T("Chưa có bản phát hành nào.") : L.F($"GitHub trả lời {(int)response.StatusCode}."));
                return;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            LastCheck = DateTime.Now;
            if (!TryVersion(root.GetProperty("tag_name").GetString(), out var latest) || latest <= Current)
            {
                Set(State.UpToDate, L.F($"Đang dùng bản mới nhất ({CurrentText})."));
                return;
            }
            JsonElement? asset = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                    asset = a;
            if (asset is not { } found)
            {
                Set(State.Failed, L.F($"Bản {Text(latest)} chưa có file {AssetName}."));
                return;
            }
            Pending = latest;
            Set(State.Downloading, L.F($"Đang tải bản {Text(latest)}…"));
            await InstallAsync(found, latest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException or System.Collections.Generic.KeyNotFoundException or InvalidOperationException)
        {
            Store.Log("update failed: " + ex.Message);
            Set(State.Failed, L.T("Không cập nhật được: ") + ex.Message);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static async Task InstallAsync(JsonElement asset, Version latest)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException(L.T("Không biết file LumenLite đang chạy."));
        var download = exe + ".download";
        var size = asset.GetProperty("size").GetInt64();
        await using (var source = await Http.GetStreamAsync(asset.GetProperty("browser_download_url").GetString()))
        await using (var target = File.Create(download))
            await source.CopyToAsync(target);
        if (new FileInfo(download).Length != size)
            throw new IOException(L.T("file tải về không đủ dung lượng"));
        // GitHub states the file's SHA-256: a release without one, or a file that does not match, is not used.
        if (!asset.TryGetProperty("digest", out var digest) || digest.GetString() is not { } d || !d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(download);
            throw new IOException(L.T("bản phát hành không có mã kiểm tra SHA-256"));
        }
        await using (var check = File.OpenRead(download))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(check));
            if (!string.Equals(hash, d[7..], StringComparison.OrdinalIgnoreCase))
            {
                check.Dispose();
                File.Delete(download);
                throw new IOException(L.T("file tải về không khớp mã kiểm tra của GitHub"));
            }
        }
        // The file must be a program that says it is the version the release names.
        var info = FileVersionInfo.GetVersionInfo(download);
        if (!TryVersion(info.ProductVersion?.Split('+')[0], out var stamped) || stamped != latest
            || !string.Equals(info.ProductName, "LumenLite", StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(download);
            throw new IOException(L.F($"file tải về không phải LumenLite {Text(latest)}"));
        }
        // The running file can be renamed but not overwritten: it steps aside, the new one takes its name.
        var old = exe + ".old";
        File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(download, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
        Set(State.Ready, L.F($"Đã tải bản {Text(latest)}. Khởi động lại LumenLite để dùng."));
        _dispatcher?.BeginInvoke(() => Ready?.Invoke(latest));
    }

    /// <summary>Starts the new file once this one has exited, then quits through <paramref name="quit"/>.</summary>
    public static void Restart(Action quit)
    {
        var exe = Environment.ProcessPath;
        if (exe == null)
            return;
        Process.Start(new ProcessStartInfo(exe, $"--after-update {Environment.ProcessId}") { UseShellExecute = false });
        quit();
    }

    /// <summary>A new copy started by <see cref="Restart"/> waits for the old one to exit before it takes over.</summary>
    public static void WaitForPrevious(string[] args)
    {
        var at = Array.IndexOf(args, "--after-update");
        if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out var pid))
            return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(20_000);
        }
        catch (ArgumentException)
        {
        }
    }

    /// <summary>The file the last update set aside, once nothing runs from it.</summary>
    private static void CleanUp()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                if (File.Exists(exe + ".old")) File.Delete(exe + ".old");
                if (File.Exists(exe + ".download")) File.Delete(exe + ".download");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool TryVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        var text = (tag ?? "").TrimStart('v', 'V');
        if (!Version.TryParse(text, out var parsed))
            return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }

    private static string Text(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    private static void Set(State state, string message)
    {
        Status = state;
        Message = message;
        _dispatcher?.BeginInvoke(() => Changed?.Invoke());
    }
}
