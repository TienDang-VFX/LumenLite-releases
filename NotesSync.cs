using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace LumenLite;

/// <summary>
/// Keeps the Scratchpad notes in a GitHub repository, one Markdown file per note (notes/&lt;id&gt;.md), so every PC
/// running LumenLite shares them and Git keeps the history of every change.
///
/// Each sync reads the branch's newest commit, compares every note with the version this PC last synced (by Git
/// blob hash), takes in what changed on GitHub, and writes what changed here as one commit. The commit only goes in
/// if nobody pushed in between (the branch must move forward); otherwise the sync starts again from the new commit.
/// A note changed on both sides keeps the GitHub version and gets a conflict copy with this PC's text, so nothing
/// is lost. Place, size and pin on the desktop stay on each PC.
/// </summary>
internal static class NotesSync
{
    private static NotesSyncSettings Cfg => Store.S.NotesSync;

    private static readonly DispatcherTimer Debounce = new() { Interval = TimeSpan.FromSeconds(15) };
    private static readonly DispatcherTimer Poll = new() { Interval = TimeSpan.FromMinutes(3) };
    private static bool _running, _again, _started;

    /// <summary>The source passed to <see cref="StickyNotes.RaiseEdited"/> for text that came from GitHub.</summary>
    public static readonly object Source = new();

    /// <summary>Notes were added, removed or renamed by a sync.</summary>
    public static event Action? ListChanged;

    /// <summary>The state line changed (syncing, done, failed).</summary>
    public static event Action? StatusChanged;

    public static string Status { get; private set; } = "";
    public static bool Busy => _running;
    public static bool Failed { get; private set; }

    /// <summary>A repository and a token are set and syncing is on.</summary>
    public static bool Active => Cfg.Enabled && Cfg.Repo.Contains('/') && Token.Available;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        EnsureIds();
        Debounce.Tick += (_, _) => { Debounce.Stop(); _ = SyncAsync(); };
        Poll.Tick += (_, _) => _ = SyncAsync();
        StickyNotes.Edited += (_, sender) =>
        {
            if (sender != Source) Soon();
        };
        Poll.Start();
        // A few seconds after start, so the sign-in is not slowed down.
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        first.Tick += (_, _) => { first.Stop(); _ = SyncAsync(); };
        first.Start();
        Status = Cfg.LastSync is { } last ? L.F($"Đồng bộ lần cuối lúc {last.ToLocalTime():HH:mm dd/MM}") : "";
    }

    /// <summary>Syncs shortly after the notes stop changing.</summary>
    public static void Soon()
    {
        if (!Active) return;
        Debounce.Stop();
        Debounce.Start();
    }

    /// <summary>A note is being deleted here: once synced, it is also removed from GitHub at the next sync.</summary>
    public static void Forget(ScratchPad pad)
    {
        if (pad.SyncedSha != null && pad.Id.Length > 0)
        {
            Cfg.Tombstones.RemoveAll(t => t.Id == pad.Id);
            Cfg.Tombstones.Add(new NoteTombstone { Id = pad.Id, Sha = pad.SyncedSha });
            Store.SaveSoon();
        }
        Soon();
    }

    /// <summary>The page on GitHub listing every change of one note, or of all of them.</summary>
    public static string? HistoryUrl(ScratchPad? pad = null)
    {
        if (!Cfg.Repo.Contains('/')) return null;
        var path = pad == null || pad.Id.Length == 0 ? Cfg.Folder : $"{Cfg.Folder}/{pad.Id}.md";
        return $"https://github.com/{Cfg.Repo}/commits/{Uri.EscapeDataString(Cfg.Branch)}/{path}";
    }

    public static void EnsureIds()
    {
        var changed = false;
        foreach (var pad in Store.S.Pads)
        {
            if (pad.Id.Length == 0)
            {
                pad.Id = NewId();
                changed = true;
            }
        }
        if (changed) Store.SaveSoon();
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static void SetStatus(string text, bool failed = false)
    {
        Status = text;
        Failed = failed;
        StatusChanged?.Invoke();
    }

    // ---------- the sync ----------

    /// <summary>Runs one sync now (or right after the one running). Returns false when it failed.</summary>
    public static async Task<bool> SyncAsync()
    {
        if (!Active) return false;
        if (_running)
        {
            _again = true;
            return true;
        }
        _running = true;
        var ok = false;
        try
        {
            SetStatus(L.T("Đang đồng bộ…"));
            EnsureIds();
            var token = Token.Load() ?? throw new IOException(Token.FromGh
                ? L.T("GitHub CLI chưa đăng nhập (chạy gh auth login).")
                : L.T("Chưa có token."));
            var api = new GitHubApi(Cfg.Repo, token);
            string? summary = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                summary = await RunAsync(api);
                if (summary != null) break;
            }
            if (summary == null)
                throw new IOException(L.T("GitHub liên tục có thay đổi mới, sẽ thử lại sau."));
            Cfg.LastSync = DateTime.UtcNow;
            Store.SaveSoon();
            SetStatus(summary);
            ok = true;
        }
        catch (Exception ex)
        {
            Store.Log("notes sync failed: " + ex.Message);
            Token.Invalidate();
            SetStatus(L.T("Không đồng bộ được: ") + ex.Message, failed: true);
        }
        finally
        {
            _running = false;
            StatusChanged?.Invoke();
        }
        if (_again)
        {
            _again = false;
            Soon();
        }
        return ok;
    }

    /// <summary>One pass; null when the branch moved while it ran (the caller tries again).</summary>
    private static async Task<string?> RunAsync(GitHubApi api)
    {
        var branch = Cfg.Branch.Trim().Length > 0 ? Cfg.Branch.Trim() : "main";
        var folder = Cfg.Folder.Trim('/', ' ');
        var head = await api.HeadAsync(branch);
        var remote = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // id -> blob sha
        string? readmeSha = null;
        if (head != null)
        {
            foreach (var (path, sha) in await api.FilesAsync(head))
            {
                if (path.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                    readmeSha = sha;
                else if (path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && path.IndexOf('/', folder.Length + 1) < 0)
                    remote[Path.GetFileNameWithoutExtension(path)] = sha;
            }
        }

        var pads = Store.S.Pads;
        var writes = new List<(string Path, string? Content)>();      // null content deletes the file
        var uploaded = new List<(ScratchPad Pad, string Sha)>();
        var listChanged = false;
        var copies = new List<ScratchPad>();
        int pulled = 0, added = 0, updated = 0, deleted = 0, conflicts = 0;

        foreach (var pad in pads.ToList())
        {
            var path = $"{folder}/{pad.Id}.md";
            var content = Serialize(pad);
            var localSha = BlobSha(content);
            var changedHere = localSha != pad.SyncedSha;
            if (remote.TryGetValue(pad.Id, out var remoteSha))
            {
                remote.Remove(pad.Id);
                var changedThere = remoteSha != pad.SyncedSha;
                if (!changedThere && !changedHere)
                    continue;
                if (localSha == remoteSha)
                {
                    pad.SyncedSha = remoteSha;
                    continue;
                }
                if (changedThere)
                {
                    var text = await api.BlobAsync(remoteSha);
                    // Typed into while the note was downloading: compare again.
                    changedHere = BlobSha(Serialize(pad)) != pad.SyncedSha;
                    if (changedHere)
                    {
                        // Both sides changed: GitHub's version stays here, this PC's text goes into a copy.
                        var copy = new ScratchPad { Id = NewId(), Title = L.F($"{pad.Title} (xung đột từ {Environment.MachineName})"), Text = pad.Text, Color = pad.Color };
                        pads.Insert(pads.IndexOf(pad) + 1, copy);
                        copies.Add(copy);
                        conflicts++;
                        listChanged = true;
                    }
                    var titleBefore = pad.Title;
                    Apply(pad, text);
                    pad.SyncedSha = remoteSha;
                    listChanged |= pad.Title != titleBefore;
                    StickyNotes.RaiseEdited(pad, Source);
                    pulled++;
                    continue;
                }
                writes.Add((path, content));
                uploaded.Add((pad, localSha));
                updated++;
            }
            else if (pad.SyncedSha != null && head != null)
            {
                // Was on GitHub and is gone: deleted on another PC. Edits made here since bring it back.
                if (changedHere)
                {
                    writes.Add((path, content));
                    uploaded.Add((pad, localSha));
                    added++;
                }
                else
                {
                    StickyNotes.Remove(pad);
                    pads.Remove(pad);
                    listChanged = true;
                    pulled++;
                }
            }
            else
            {
                writes.Add((path, content));
                uploaded.Add((pad, localSha));
                added++;
            }
        }

        // Conflict copies made in this pass go up with it.
        foreach (var copy in copies)
        {
            var content = Serialize(copy);
            writes.Add(($"{folder}/{copy.Id}.md", content));
            uploaded.Add((copy, BlobSha(content)));
            added++;
        }

        // Notes only on GitHub: new from another PC, or deleted here.
        var doneTombstones = new List<NoteTombstone>();
        foreach (var (id, sha) in remote)
        {
            var tombstone = Cfg.Tombstones.FirstOrDefault(t => t.Id == id);
            if (tombstone != null)
            {
                doneTombstones.Add(tombstone);
                if (tombstone.Sha == sha)
                {
                    writes.Add(($"{folder}/{id}.md", null));
                    deleted++;
                    continue;
                }
                // Changed on another PC after it was deleted here: the newer text comes back.
            }
            var pad = new ScratchPad { Id = id };
            Apply(pad, await api.BlobAsync(sha));
            pad.SyncedSha = sha;
            pads.Add(pad);
            listChanged = true;
            pulled++;
        }
        // Tombstones for notes GitHub no longer has are done too.
        foreach (var t in Cfg.Tombstones)
            if (!remote.ContainsKey(t.Id) && !doneTombstones.Contains(t))
                doneTombstones.Add(t);

        if (writes.Count > 0 || head == null)
        {
            var readme = Readme(pads, folder);
            if (BlobSha(readme) != readmeSha)
                writes.Add(("README.md", readme));
            var parts = new List<string>();
            if (added > 0) parts.Add(L.F($"{added} mới"));
            if (updated > 0) parts.Add(L.F($"{updated} sửa"));
            if (deleted > 0) parts.Add(L.F($"{deleted} xóa"));
            var message = L.F($"Ghi chú từ {Environment.MachineName}: {string.Join(", ", parts)}").TrimEnd(':', ' ');
            var commit = await api.CommitAsync(branch, head, writes, message);
            if (commit == null)
            {
                // Someone pushed in between. What was taken in from GitHub stays; the rest is tried again.
                Notify(listChanged);
                return null;
            }
            Cfg.LastCommit = commit;
            foreach (var (pad, sha) in uploaded)
                pad.SyncedSha = sha;
        }
        foreach (var t in doneTombstones)
            Cfg.Tombstones.Remove(t);
        Store.SaveSoon();
        Notify(listChanged);

        var bits = new List<string>();
        if (pulled > 0) bits.Add(L.F($"nhận {pulled}"));
        if (added + updated + deleted > 0) bits.Add(L.F($"gửi {added + updated + deleted}"));
        if (conflicts > 0) bits.Add(L.F($"{conflicts} xung đột"));
        var when = DateTime.Now.ToString("HH:mm");
        return bits.Count == 0 ? L.F($"Đã đồng bộ lúc {when}, không có gì mới.") : L.F($"Đã đồng bộ lúc {when}: {string.Join(", ", bits)}.");
    }

    private static void Notify(bool listChanged)
    {
        if (listChanged)
        {
            Store.SaveSoon();
            ListChanged?.Invoke();
        }
    }

    // ---------- the note file ----------

    /// <summary>
    /// A note as a Markdown file: a small header (title, colour) then the text, with Unix line ends so the file
    /// reads the same whatever edited it last.
    /// </summary>
    public static string Serialize(ScratchPad pad)
    {
        var title = pad.Title.Replace("\r", " ").Replace("\n", " ").Trim();
        var text = pad.Text.Replace("\r\n", "\n").Replace("\r", "\n");
        return $"---\ntitle: {title}\ncolor: {pad.Color}\n---\n{text}";
    }

    /// <summary>Takes a note file into a note. A file without the header (written by hand) is all text.</summary>
    public static void Apply(ScratchPad pad, string file)
    {
        file = file.Replace("\r\n", "\n");
        var title = pad.Title;
        var color = pad.Color;
        var text = file;
        if (file.StartsWith("---\n"))
        {
            var end = file.IndexOf("\n---\n", 3, StringComparison.Ordinal);
            var close = end >= 0 ? end + 5 : file.EndsWith("\n---") ? file.Length : -1;
            if (close >= 0)
            {
                foreach (var line in file[4..(end >= 0 ? end : file.Length - 4)].Split('\n'))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var key = line[..colon].Trim().ToLowerInvariant();
                    var value = line[(colon + 1)..].Trim();
                    if (key == "title" && value.Length > 0) title = value;
                    if (key == "color" && value.Length > 0) color = value;
                }
                text = file[Math.Min(close, file.Length)..];
            }
        }
        else
        {
            var first = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim().TrimStart('#').Trim();
            if (!string.IsNullOrEmpty(first) && pad.Title.Length == 0) title = first.Length > 40 ? first[..40] : first;
        }
        pad.Title = title.Length > 0 ? title : L.T("Ghi chú");
        pad.Color = color;
        pad.Text = text.Replace("\n", "\r\n");
    }

    private static string Readme(IEnumerable<ScratchPad> pads, string folder)
    {
        var sb = new StringBuilder();
        sb.Append("# LumenLite notes\n\n");
        sb.Append(L.T("Ghi chú Scratchpad của LumenLite, đồng bộ giữa các máy. Mỗi ghi chú là một file; lịch sử commit giữ mọi lần sửa.")).Append("\n\n");
        foreach (var pad in pads)
        {
            var title = pad.Title.Replace("[", "(").Replace("]", ")").Replace("\n", " ");
            sb.Append($"- [{title}]({folder}/{pad.Id}.md)\n");
        }
        return sb.ToString();
    }

    /// <summary>The hash Git gives a file with this content, so a note can be compared without downloading it.</summary>
    public static string BlobSha(string content)
    {
        var body = Encoding.UTF8.GetBytes(content);
        var header = Encoding.ASCII.GetBytes($"blob {body.Length}\0");
        var all = new byte[header.Length + body.Length];
        header.CopyTo(all, 0);
        body.CopyTo(all, header.Length);
        return Convert.ToHexString(SHA1.HashData(all)).ToLowerInvariant();
    }

    /// <summary>Checks a repository and token before they are saved: the repository's default branch, or an error.</summary>
    public static async Task<(bool Ok, string Message, string? DefaultBranch)> CheckAsync(string repo, string token)
    {
        try
        {
            var info = await new GitHubApi(repo, token).RepoAsync();
            var isPrivate = info?["private"]?.GetValue<bool>() ?? false;
            var canPush = info?["permissions"]?["push"]?.GetValue<bool>() ?? false;
            if (!canPush)
                return (false, L.T("Token này chỉ đọc được repo. Cần quyền Contents: Read and write."), null);
            return (true, isPrivate ? L.T("Đã kết nối repo riêng tư.") : L.T("Đã kết nối. Lưu ý: repo này công khai, ai cũng đọc được ghi chú."), info?["default_branch"]?.GetValue<string>());
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    // ---------- set up through the GitHub CLI ----------

    /// <summary>
    /// Sets the sync up with the GitHub CLI's sign-in, so no token is copied by hand: checks gh, finds or (when
    /// asked) creates the private repository, checks access, then syncs. A repository given without an owner is
    /// taken as one of the signed-in user's. Used by the panel's button and by "LumenLite.exe --notes-sync".
    /// </summary>
    public static async Task<(bool Ok, string Message)> SetupWithGhAsync(string repo, string? branch, bool create)
    {
        if (!GhCli.Installed)
            return (false, L.T("Chưa cài GitHub CLI (gh). Cài bằng: winget install GitHub.cli"));
        var token = await Task.Run(GhCli.Token);
        if (token == null)
            return (false, L.T("GitHub CLI chưa đăng nhập. Chạy: gh auth login"));
        repo = repo.Trim().Trim('/');
        if (repo.Length == 0)
            repo = "tin-notes";
        if (!repo.Contains('/'))
        {
            var user = await GhCli.UserAsync();
            if (user == null)
                return (false, L.T("Không biết tài khoản GitHub của gh."));
            repo = $"{user}/{repo}";
        }
        if (!await GhCli.RepoExistsAsync(repo))
        {
            if (!create)
                return (false, L.F($"Không thấy repo {repo}. Thêm --create để tạo repo riêng tư."));
            if (await GhCli.CreatePrivateRepoAsync(repo) is { } error)
                return (false, L.T("Không tạo được repo: ") + error);
        }
        var (ok, message, defaultBranch) = await CheckAsync(repo, token);
        if (!ok)
            return (false, message);
        var cfg = Cfg;
        if (!string.Equals(cfg.Repo, repo, StringComparison.OrdinalIgnoreCase))
        {
            // Another repository: nothing in it was synced from here yet.
            foreach (var pad in Store.S.Pads) pad.SyncedSha = null;
            cfg.Tombstones.Clear();
            cfg.LastCommit = null;
        }
        cfg.Repo = repo;
        cfg.Branch = !string.IsNullOrWhiteSpace(branch) ? branch.Trim() : defaultBranch ?? (cfg.Branch.Length > 0 ? cfg.Branch : "main");
        cfg.TokenSource = "gh";
        cfg.Enabled = true;
        Token.Save(null);
        Token.Invalidate();
        Store.SaveNow();
        var synced = await SyncAsync();
        return (synced, synced ? L.F($"Đồng bộ với {repo} qua GitHub CLI. {Status}") : Status);
    }

    // ---------- the command line ----------

    /// <summary>
    /// "LumenLite.exe --notes-sync [owner/]repo [--branch name] [--create]": sets the sync up through the GitHub CLI,
    /// for an agent setting up a new PC. The request goes through a file, so it reaches LumenLite whichever copy
    /// ends up running it (the one already open, or the copy started outside the agent's terminal). The outcome is
    /// written to notes-sync-result.txt: "OK" or "FAILED" on the first line, then the message.
    /// </summary>
    public static class Request
    {
        public const string Flag = "--notes-sync";
        private static string RequestPath => Path.Combine(Store.Folder, "notes-sync-request.txt");
        public static string ResultPath => Path.Combine(Store.Folder, "notes-sync-result.txt");

        public static bool Pending => File.Exists(RequestPath);

        /// <summary>Writes the request down when the arguments ask for one; true when they did.</summary>
        public static bool Save(string[] args)
        {
            var at = Array.FindIndex(args, a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
                return false;
            var repo = at + 1 < args.Length && !args[at + 1].StartsWith("--") ? args[at + 1] : "";
            var b = Array.FindIndex(args, a => a.Equals("--branch", StringComparison.OrdinalIgnoreCase));
            var branch = b >= 0 && b + 1 < args.Length ? args[b + 1] : "";
            var create = args.Any(a => a.Equals("--create", StringComparison.OrdinalIgnoreCase));
            try
            {
                Directory.CreateDirectory(Store.Folder);
                File.WriteAllText(RequestPath, $"{repo}\n{branch}\n{create}");
                File.WriteAllText(ResultPath, "PENDING\n");
            }
            catch (IOException)
            {
            }
            return true;
        }

        /// <summary>Carries out a waiting request, if any.</summary>
        public static async void RunPending()
        {
            string[] lines;
            try
            {
                if (!File.Exists(RequestPath)) return;
                lines = File.ReadAllText(RequestPath).Split('\n');
                File.Delete(RequestPath);
                File.WriteAllText(ResultPath, "RUNNING\n");
            }
            catch (IOException)
            {
                return;
            }
            var repo = lines.ElementAtOrDefault(0)?.Trim() ?? "";
            var branch = lines.ElementAtOrDefault(1)?.Trim();
            var create = bool.TryParse(lines.ElementAtOrDefault(2)?.Trim(), out var c) && c;
            var (ok, message) = await SetupWithGhAsync(repo, branch, create);
            Store.Log($"notes sync setup via gh ({repo}): {(ok ? "ok" : "failed")}: {message}");
            try
            {
                File.WriteAllText(ResultPath, $"{(ok ? "OK" : "FAILED")}\n{message}\n{Cfg.Repo}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------- the token ----------

    /// <summary>
    /// The GitHub token: the GitHub CLI's sign-in (asked again every half hour, never written down), or a token
    /// pasted in the panel, encrypted for this Windows user (DPAPI) in its own file, never in settings.json.
    /// </summary>
    public static class Token
    {
        private static readonly string FilePath = Path.Combine(Store.Folder, "github-notes.token");
        private static string? _cached;
        private static bool _read;
        private static string? _gh;
        private static DateTime _ghAt;

        public static bool FromGh => Store.S.NotesSync.TokenSource == "gh";

        /// <summary>A token can be had without asking anyone (gh may still turn out to be signed out).</summary>
        public static bool Available => FromGh ? GhCli.Installed : Load() != null;

        /// <summary>Forgets the GitHub CLI's token, so the next sync asks gh again (after a failure, say).</summary>
        public static void Invalidate() => _gh = null;

        public static string? Load()
        {
            if (FromGh)
            {
                if (_gh == null || DateTime.UtcNow - _ghAt > TimeSpan.FromMinutes(30))
                {
                    _gh = GhCli.Token();
                    _ghAt = DateTime.UtcNow;
                }
                return _gh;
            }
            if (_read) return _cached;
            _read = true;
            try
            {
                if (File.Exists(FilePath))
                    _cached = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                Store.Log("notes token unreadable: " + ex.Message);
            }
            return _cached;
        }

        public static void Save(string? token)
        {
            token = token?.Trim();
            _cached = string.IsNullOrEmpty(token) ? null : token;
            _read = true;
            try
            {
                if (_cached == null)
                {
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    return;
                }
                Directory.CreateDirectory(Store.Folder);
                File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(_cached), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                Store.Log("notes token save failed: " + ex.Message);
            }
        }
    }
}

/// <summary>The few GitHub REST calls the notes sync uses: read a branch's files, write a commit on top of it.</summary>
internal sealed class GitHubApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _repo;
    private readonly string _token;

    public GitHubApi(string repo, string token)
    {
        _repo = repo.Trim().Trim('/');
        if (_repo.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            _repo = _repo["https://github.com/".Length..];
        if (_repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            _repo = _repo[..^4];
        _token = token.Trim();
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, $"https://api.github.com/repos/{_repo}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("LumenLite");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body != null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { if (text.Length > 0) json = JsonNode.Parse(text); } catch (JsonException) { }
        return (response.StatusCode, json);
    }

    private static Exception Error(HttpStatusCode status, JsonNode? body)
    {
        var message = body?["message"]?.GetValue<string>() ?? status.ToString();
        return status switch
        {
            HttpStatusCode.Unauthorized => new IOException(L.T("Token sai hoặc đã hết hạn.")),
            HttpStatusCode.NotFound => new IOException(L.T("Không thấy repo (sai tên, hoặc token không được cấp quyền vào repo này).")),
            HttpStatusCode.Forbidden => new IOException(L.T("GitHub từ chối: ") + message),
            _ => new IOException($"GitHub {(int)status}: {message}"),
        };
    }

    public async Task<JsonNode?> RepoAsync()
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "");
        if (status != HttpStatusCode.OK) throw Error(status, body);
        return body;
    }

    /// <summary>The newest commit on the branch; null for an empty repository. A missing branch is made from the default one.</summary>
    public async Task<string?> HeadAsync(string branch)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"/git/ref/heads/{branch}");
        if (status == HttpStatusCode.OK)
            return body?["object"]?["sha"]?.GetValue<string>();
        if (status == HttpStatusCode.Conflict)
            return null; // Git Repository is empty.
        if (status == HttpStatusCode.NotFound)
        {
            var repo = await RepoAsync();
            var main = repo?["default_branch"]?.GetValue<string>();
            if (main == null || main == branch)
                return null;
            var (s2, b2) = await SendAsync(HttpMethod.Get, $"/git/ref/heads/{main}");
            if (s2 == HttpStatusCode.Conflict) return null;
            if (s2 != HttpStatusCode.OK) throw Error(s2, b2);
            var sha = b2!["object"]!["sha"]!.GetValue<string>();
            var (s3, b3) = await SendAsync(HttpMethod.Post, "/git/refs", new { @ref = $"refs/heads/{branch}", sha });
            if (s3 != HttpStatusCode.Created) throw Error(s3, b3);
            return sha;
        }
        throw Error(status, body);
    }

    /// <summary>Every file in the commit, with its blob hash.</summary>
    public async Task<List<(string Path, string Sha)>> FilesAsync(string commit)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"/git/commits/{commit}");
        if (status != HttpStatusCode.OK) throw Error(status, body);
        var tree = body!["tree"]!["sha"]!.GetValue<string>();
        (status, body) = await SendAsync(HttpMethod.Get, $"/git/trees/{tree}?recursive=1");
        if (status != HttpStatusCode.OK) throw Error(status, body);
        var files = new List<(string, string)>();
        foreach (var entry in body!["tree"]!.AsArray())
            if (entry?["type"]?.GetValue<string>() == "blob")
                files.Add((entry["path"]!.GetValue<string>(), entry["sha"]!.GetValue<string>()));
        return files;
    }

    public async Task<string> BlobAsync(string sha)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"/git/blobs/{sha}");
        if (status != HttpStatusCode.OK) throw Error(status, body);
        var base64 = body!["content"]!.GetValue<string>().Replace("\n", "");
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    /// <summary>
    /// Writes the files (null content deletes one) as one commit on top of <paramref name="parent"/> and moves the
    /// branch to it. Returns the new commit, or null when the branch had moved on (nothing is changed then).
    /// </summary>
    public async Task<string?> CommitAsync(string branch, string? parent, List<(string Path, string? Content)> files, string message)
    {
        if (parent == null)
        {
            // An empty repository has no commit to build on; the contents API can make the first one.
            var first = files.FirstOrDefault(f => f.Content != null);
            var (s0, b0) = await SendAsync(HttpMethod.Put, $"/contents/{first.Path}", new
            {
                message,
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(first.Content ?? "")),
                branch,
            });
            if (s0 != HttpStatusCode.Created && s0 != HttpStatusCode.OK) throw Error(s0, b0);
            parent = b0!["commit"]!["sha"]!.GetValue<string>();
            files = files.Where(f => f.Path != first.Path).ToList();
            if (files.Count == 0) return parent;
        }

        var (status, body) = await SendAsync(HttpMethod.Get, $"/git/commits/{parent}");
        if (status != HttpStatusCode.OK) throw Error(status, body);
        var baseTree = body!["tree"]!["sha"]!.GetValue<string>();
        var entries = files.Select(f => f.Content == null
            ? (object)new Dictionary<string, object?> { ["path"] = f.Path, ["mode"] = "100644", ["type"] = "blob", ["sha"] = null }
            : new Dictionary<string, object?> { ["path"] = f.Path, ["mode"] = "100644", ["type"] = "blob", ["content"] = f.Content }).ToList();
        (status, body) = await SendAsync(HttpMethod.Post, "/git/trees", new { base_tree = baseTree, tree = entries });
        if (status != HttpStatusCode.Created) throw Error(status, body);
        var tree = body!["sha"]!.GetValue<string>();

        (status, body) = await SendAsync(HttpMethod.Post, "/git/commits", new { message, tree, parents = new[] { parent } });
        if (status != HttpStatusCode.Created) throw Error(status, body);
        var commit = body!["sha"]!.GetValue<string>();

        (status, body) = await SendAsync(HttpMethod.Patch, $"/git/refs/heads/{branch}", new { sha = commit, force = false });
        if (status == HttpStatusCode.OK)
            return commit;
        if (status == HttpStatusCode.UnprocessableEntity || status == HttpStatusCode.Conflict)
            return null; // Not a fast forward: someone pushed first.
        throw Error(status, body);
    }
}
