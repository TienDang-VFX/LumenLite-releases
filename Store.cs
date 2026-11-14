using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace LumenLite;

public sealed class WidgetState
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Visible { get; set; } = true;
}

public sealed class ScratchPad
{
    /// <summary>A short stable name for the note, shared by every PC that syncs it (its file is notes/&lt;Id&gt;.md).</summary>
    public string Id { get; set; } = "";
    /// <summary>The Git blob of the note as last synced with GitHub; null for a note that was never synced.</summary>
    public string? SyncedSha { get; set; }
    public string Title { get; set; } = L.T("Ghi chú");
    public string Text { get; set; } = "";
    /// <summary>Shown as a sticky note on the desktop.</summary>
    public bool OnDesktop { get; set; }
    /// <summary>A pinned sticky note stays where it is and floats above other windows.</summary>
    public bool Pinned { get; set; }
    public string Color { get; set; } = "yellow";
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
}

/// <summary>A synced note deleted on this PC, still to be removed from GitHub.</summary>
public sealed class NoteTombstone
{
    public string Id { get; set; } = "";
    public string Sha { get; set; } = "";
}

/// <summary>Where the Scratchpad notes sync to. The token is kept apart, encrypted (see NotesSync).</summary>
public sealed class NotesSyncSettings
{
    public bool Enabled { get; set; }
    /// <summary>"owner/name" of a (private) GitHub repository.</summary>
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string Folder { get; set; } = "notes";
    /// <summary>"stored": a token saved (encrypted) on this PC; "gh": the GitHub CLI's sign-in, asked each time.</summary>
    public string TokenSource { get; set; } = "stored";
    public DateTime? LastSync { get; set; }
    public string? LastCommit { get; set; }
    public List<NoteTombstone> Tombstones { get; set; } = new();
}

public sealed class LaunchItem
{
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class AppSettings
{
    public Dictionary<string, WidgetState> Widgets { get; set; } = new();
    public List<LaunchItem>? Launcher { get; set; }
    public bool TaskbarDock { get; set; } = true;
    public bool Island { get; set; } = true;
    public bool IslandAlwaysShow { get; set; } = true;
    /// <summary>What the idle Dynamic Island shows: "off", "system" (CPU and RAM) or "network".</summary>
    public string IslandMetrics { get; set; } = "off";
    /// <summary>The screen the island was dragged to (its device name); empty for the main screen.</summary>
    public string IslandScreen { get; set; } = "";
    /// <summary>The edge the island hangs from: "top", "left" or "right".</summary>
    public string IslandDock { get; set; } = "top";
    public bool AlertCpu { get; set; } = true;
    public bool AlertMemory { get; set; } = true;
    public bool AlertDisk { get; set; } = true;
    public bool AlertBattery { get; set; } = true;
    public bool LowerOnUnplug { get; set; } = true;
    /// <summary>Microphone made the default whenever it is plugged in; null leaves Windows' choice alone.</summary>
    public string? PreferredMic { get; set; }
    public bool CleanerAuto { get; set; }
    public bool AgentAlerts { get; set; } = true;
    public bool AgentOnIsland { get; set; } = true;
    /// <summary>Download and put in place new versions from the releases repository by itself.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Draw on the CPU instead of the GPU: less memory, choppier animations. Applies from the next start.</summary>
    public bool SoftwareRendering { get; set; }
    /// <summary>The panel's look: "system" (follow Windows), "light" or "dark".</summary>
    public string ThemeMode { get; set; } = "system";
    /// <summary>The interface language: "vi" or "en". Applies from the next start.</summary>
    public string Language { get; set; } = "vi";
    /// <summary>
    /// A session-fleet machine name to the RustDesk ID that shows its screen, for machines whose session
    /// records do not name their computer yet (plugin older than 0.3.1), or whose RustDesk name differs.
    /// </summary>
    public Dictionary<string, string> RustDeskMachines { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Holding the middle mouse button opens the radial menu.</summary>
    public bool RadialMiddleHold { get; set; } = true;
    /// <summary>Actions on the radial menu, in clockwise order from the top.</summary>
    public List<string> RadialItems { get; set; } = new() { "commandbar", "scratchpad", "screenshot", "mic", "awake", "timer25", "output", "rustdesk" };
    public bool RadialMigratedRustDesk { get; set; }
    public DateTime CleanerLastRun { get; set; }
    public List<ScratchPad> Pads { get; set; } = new();
    public NotesSyncSettings NotesSync { get; set; } = new();
    public bool CleanLinks { get; set; } = true;
    public bool DownloadAlerts { get; set; } = true;
    public string Notes { get; set; } = "";
}

/// <summary>Settings live in %APPDATA%\LumenLite\settings.json and are written shortly after each change.</summary>
public static class Store
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumenLite");

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static DispatcherTimer? _saveTimer;

    public static AppSettings S { get; private set; } = new();

    public static void Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : ContainedCopy();
            if (path != null)
            {
                S = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
                if (path != FilePath)
                {
                    // Taken in once: saved to the real folder, the contained copy is left as it was.
                    Log("settings taken from " + path);
                    SaveSoon();
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt file is kept aside rather than silently overwritten.
            Log("settings load failed: " + ex.Message);
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
            S = new AppSettings();
        }
    }

    /// <summary>
    /// Settings a copy of LumenLite saved while it ran inside a Store app (the Claude desktop app's terminal, say):
    /// Windows kept them in that app's own folder, %LOCALAPPDATA%\Packages\&lt;app&gt;\LocalCache\Roaming\LumenLite.
    /// The newest one, when this folder has none yet.
    /// </summary>
    private static string? ContainedCopy()
    {
        try
        {
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (!Directory.Exists(packages))
                return null;
            string? newest = null;
            foreach (var package in Directory.EnumerateDirectories(packages))
            {
                var candidate = Path.Combine(package, "LocalCache", "Roaming", "LumenLite", "settings.json");
                if (File.Exists(candidate) && (newest == null || File.GetLastWriteTimeUtc(candidate) > File.GetLastWriteTimeUtc(newest)))
                    newest = candidate;
            }
            return newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static WidgetState Widget(string id)
    {
        if (!S.Widgets.TryGetValue(id, out var state))
            S.Widgets[id] = state = new WidgetState();
        return state;
    }

    public static void SaveSoon()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveTimer.Tick += (_, _) => SaveNow();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void SaveNow()
    {
        _saveTimer?.Stop();
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(S, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log("settings save failed: " + ex.Message);
        }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, "lumen.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
