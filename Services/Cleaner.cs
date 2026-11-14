using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualBasic.FileIO;

namespace LumenLite;

public sealed class CleanCategory
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    public required Func<IEnumerable<string>> Folders { get; init; }
    /// <summary>Only files older than this are touched, so files in use are left alone.</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;
    public long Bytes { get; set; }
    public int Files { get; set; }
}

public sealed record MessagingFolder(string App, string Path, long Bytes, int Files, long OldBytes, int OldFiles);

/// <summary>Finds and removes caches, temporary files and old messaging downloads.</summary>
internal static class Cleaner
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static List<CleanCategory> Categories() => new()
    {
        new CleanCategory
        {
            Id = "temp", Title = L.T("File tạm của bạn"), Glyph = "\uE7C3",
            Description = L.T("Thư mục %TEMP%, chỉ file cũ hơn 1 ngày"),
            Folders = () => new[] { Path.GetTempPath() }, MinAge = TimeSpan.FromDays(1),
        },
        new CleanCategory
        {
            Id = "wintemp", Title = L.T("File tạm của Windows"), Glyph = "\uE770",
            Description = L.T(@"C:\Windows\Temp, phần Windows cho phép xóa"),
            Folders = () => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") }, MinAge = TimeSpan.FromDays(1),
        },
        new CleanCategory
        {
            Id = "browsers", Title = L.T("Cache trình duyệt"), Glyph = "\uE774",
            Description = L.T("Chrome, Edge, Brave, Opera, Firefox. Không đụng tới mật khẩu, lịch sử hay cookie"),
            Folders = BrowserCaches,
        },
        new CleanCategory
        {
            Id = "shaders", Title = L.T("Cache đồ họa"), Glyph = "\uE7F4",
            Description = L.T("Shader DirectX và NVIDIA/AMD; game sẽ tự tạo lại khi cần"),
            Folders = () => new[]
            {
                Path.Combine(Local, "D3DSCache"), Path.Combine(Local, "NVIDIA", "DXCache"), Path.Combine(Local, "NVIDIA", "GLCache"),
                Path.Combine(Local, "AMD", "DxCache"), Path.Combine(Local, "AMD", "GLCache"),
            },
            MinAge = TimeSpan.FromDays(1),
        },
        new CleanCategory
        {
            Id = "crash", Title = L.T("Báo lỗi và bản dump"), Glyph = "\uE7BA",
            Description = L.T("Bản ghi lỗi app (CrashDumps, Windows Error Reporting)"),
            Folders = () => new[]
            {
                Path.Combine(Local, "CrashDumps"), Path.Combine(Local, "Microsoft", "Windows", "WER"),
            },
        },
    };

    private static IEnumerable<string> BrowserCaches()
    {
        var chromium = new[]
        {
            Path.Combine(Local, "Google", "Chrome", "User Data"),
            Path.Combine(Local, "Microsoft", "Edge", "User Data"),
            Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data"),
            Path.Combine(Local, "Vivaldi", "User Data"),
            Path.Combine(Roaming, "Opera Software", "Opera Stable"),
            Path.Combine(Local, "CocCoc", "Browser", "User Data"),
        };
        foreach (var root in chromium.Where(Directory.Exists))
        {
            var profiles = new List<string> { root };
            try
            {
                profiles.AddRange(Directory.GetDirectories(root).Where(d =>
                {
                    var name = Path.GetFileName(d);
                    return name == "Default" || name.StartsWith("Profile ");
                }));
            }
            catch
            {
            }
            foreach (var profile in profiles)
                foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache", Path.Combine("Service Worker", "CacheStorage") })
                    yield return Path.Combine(profile, cache);
            yield return Path.Combine(root, "ShaderCache");
            yield return Path.Combine(root, "GrShaderCache");
        }
        var firefox = Path.Combine(Local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefox))
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(firefox); } catch { dirs = Array.Empty<string>(); }
            foreach (var profile in dirs)
                yield return Path.Combine(profile, "cache2");
        }
    }

    /// <summary>Fills in the size of each category. Slow on big caches: call off the UI thread.</summary>
    public static void Measure(IEnumerable<CleanCategory> categories)
    {
        foreach (var category in categories)
        {
            long bytes = 0;
            var files = 0;
            foreach (var file in FilesOf(category))
            {
                bytes += file.Length;
                files++;
            }
            category.Bytes = bytes;
            category.Files = files;
        }
    }

    /// <summary>Deletes the files; ones that are in use or protected are skipped. Returns bytes freed.</summary>
    public static long Clean(IEnumerable<CleanCategory> categories)
    {
        long freed = 0;
        foreach (var category in categories)
        {
            foreach (var file in FilesOf(category).ToList())
            {
                try
                {
                    var size = file.Length;
                    file.Attributes = FileAttributes.Normal;
                    file.Delete();
                    freed += size;
                }
                catch
                {
                    // In use or protected: left for next time.
                }
            }
            foreach (var folder in category.Folders())
                RemoveEmptyFolders(folder, keepRoot: true);
        }
        return freed;
    }

    private static IEnumerable<FileInfo> FilesOf(CleanCategory category)
    {
        var cutoff = DateTime.Now - category.MinAge;
        foreach (var folder in category.Folders().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder))
                continue;
            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
            }
            catch
            {
                continue;
            }
            using var e = files.GetEnumerator();
            while (true)
            {
                FileInfo current;
                try
                {
                    if (!e.MoveNext()) break;
                    current = e.Current;
                }
                catch
                {
                    break;
                }
                if (category.MinAge > TimeSpan.Zero && current.LastWriteTime > cutoff)
                    continue;
                yield return current;
            }
        }
    }

    private static void RemoveEmptyFolders(string folder, bool keepRoot)
    {
        if (!Directory.Exists(folder))
            return;
        try
        {
            foreach (var sub in Directory.GetDirectories(folder))
                RemoveEmptyFolders(sub, keepRoot: false);
            if (!keepRoot && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch
        {
        }
    }

    /// <summary>Weekly automatic clean of the user's temp files, when turned on.</summary>
    public static void RunScheduled()
    {
        if (!Store.S.CleanerAuto || DateTime.UtcNow - Store.S.CleanerLastRun < TimeSpan.FromDays(7))
            return;
        Store.S.CleanerLastRun = DateTime.UtcNow;
        Store.SaveSoon();
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            var freed = Clean(Categories().Where(c => c.Id is "temp" or "crash"));
            Store.Log($"scheduled clean freed {freed / 1048576} MB");
        });
    }

    // ---------- messaging downloads ----------

    public static List<MessagingFolder> MessagingFolders(int olderThanDays)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        var candidates = new (string App, string Path)[]
        {
            ("Zalo", Path.Combine(documents, "Zalo Received Files")),
            ("Zalo", Path.Combine(profile, "Documents", "Zalo Received Files")),
            ("Telegram", Path.Combine(downloads, "Telegram Desktop")),
            ("Telegram", Path.Combine(documents, "Telegram Desktop")),
            ("Messenger", Path.Combine(downloads, "Messenger")),
            ("Discord", Path.Combine(downloads, "Discord")),
            ("Viber", Path.Combine(documents, "ViberDownloads")),
            ("Skype", Path.Combine(Roaming, "Microsoft", "Skype for Desktop", "media_messaging")),
        };
        var cutoff = DateTime.Now.AddDays(-olderThanDays);
        var result = new List<MessagingFolder>();
        foreach (var (app, path) in candidates.DistinctBy(c => c.Path.ToLowerInvariant()))
        {
            if (!Directory.Exists(path))
                continue;
            long bytes = 0, oldBytes = 0;
            int files = 0, oldFiles = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                bytes += file.Length;
                files++;
                if (file.LastWriteTime < cutoff)
                {
                    oldBytes += file.Length;
                    oldFiles++;
                }
            }
            result.Add(new MessagingFolder(app, path, bytes, files, oldBytes, oldFiles));
        }
        return result;
    }

    /// <summary>Moves files older than the cutoff to the Recycle Bin, so they can still be restored.</summary>
    public static int RecycleOld(string folder, int olderThanDays)
    {
        var cutoff = DateTime.Now.AddDays(-olderThanDays);
        var moved = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList())
        {
            if (file.LastWriteTime >= cutoff)
                continue;
            try
            {
                FileSystem.DeleteFile(file.FullName, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                moved++;
            }
            catch
            {
            }
        }
        return moved;
    }

    public static void RecycleFolder(string folder) =>
        FileSystem.DeleteDirectory(folder, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
}
