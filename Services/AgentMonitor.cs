using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;

namespace LumenLite;

public enum AgentProvider { Claude, Codex }

/// <param name="NeedsInput">The agent stopped to ask the person something: a question, a plan to approve, or the Claude app marked the session as waiting for them.</param>
/// <param name="OpenUrl">Deep link that opens this session in the Claude or Codex desktop app; empty when there is none.</param>
/// <param name="Machine">The other machine the session runs on, from the shared session-fleet registry; empty for this one.</param>
/// <param name="Host">That machine's computer name, as RustDesk shows it, when the registry has it.</param>
public sealed record AgentSession(AgentProvider Provider, string Id, string Title, string Project, string Activity, bool Working,
    bool NeedsInput, DateTime LastActive, DateTime WorkingSince, long Tokens, string OpenUrl, string Machine = "", string Host = "");

public sealed record ModelUsage(string Model, long Input, long Output, long CacheWrite, long CacheRead)
{
    public long Total => Input + Output + CacheWrite + CacheRead;
}

/// <summary>Tokens and their API value for one agent over a period.</summary>
public sealed record AgentSpend(AgentProvider Provider, long Input, long Output, long CacheWrite, long CacheRead, double Cost, bool Priced)
{
    public long Total => Input + Output + CacheWrite + CacheRead;
}

/// <param name="Session">The short window (Claude's five hours); otherwise a longer one, usually the week.</param>
/// <param name="Estimated">The renewal time is worked out from readings, not reported.</param>
public sealed record LimitWindow(bool Session, int Minutes, double Used, DateTime? ResetsAt, bool Estimated);

public sealed record AgentLimits(AgentProvider Provider, IReadOnlyList<LimitWindow> Windows, DateTime Observed);

/// <summary>
/// Follows Claude Code and Codex through the files they write on this machine:
/// the transcripts (%USERPROFILE%\.claude\projects, %USERPROFILE%\.codex\sessions)
/// for tokens, sessions and what they are doing, the plan limits the Claude app
/// and Codex record, and the Claude app's own session list to open a session
/// there. Read-only and local; nothing is sent anywhere.
/// </summary>
internal static class AgentMonitor
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string ClaudeRoot = Path.Combine(Home, ".claude", "projects");
    private static readonly string CodexHome = Path.Combine(Home, ".codex");
    private static readonly string CodexRoot = Path.Combine(CodexHome, "sessions");

    private sealed class FileState
    {
        public AgentProvider Provider;
        public long Offset;
        public string Id = "";
        public string Title = "";
        public string Project = "";
        public string Activity = "";
        public string Model = "";
        public bool EndedTurn = true;
        public DateTime LastEntry;
        public DateTime TurnStarted;
        public long Tokens;
        public bool WasWorking;
        /// <summary>A person's own session, not a subagent's transcript or a scripted Codex run.</summary>
        public bool Listed = true;
        /// <summary>The question tool call still waiting for its answer.</summary>
        public string Question = "";
        /// <summary>A tool call is still running (a build, a long command), so a quiet transcript is still a working session.</summary>
        public bool ToolRunning;
        // Codex keeps running totals; only their growth is new usage.
        public long LastTotal;
        public bool SawRecords;
    }

    private sealed record DesktopSession(string LocalId, string Title, bool Waiting, DateTime LastActivity);

    private static readonly Dictionary<string, FileState> Files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SeenMessages = new();
    // (local day, agent, model) -> input, output, cache write, cache read
    private static readonly Dictionary<(DateTime Day, AgentProvider Provider, string Model), long[]> Usage = new();
    private static readonly Dictionary<AgentProvider, AgentLimits> Limits = new();
    private static readonly Dictionary<AgentProvider, string> Plans = new();
    private static readonly Dictionary<string, DesktopSession> Desktop = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (DateTime Stamp, DesktopSession? Session, string[] Ids)> DesktopFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> CodexTitles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> Stamps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static Timer? _timer;
    private static Dispatcher? _dispatcher;
    private static int _busy;
    private static bool _scanned;

    public static bool Available => Directory.Exists(ClaudeRoot) || Directory.Exists(CodexRoot);

    /// <summary>Raised on the UI thread after each scan.</summary>
    public static event Action? Updated;

    /// <summary>Raised on the UI thread when a session that worked for a while finishes its turn.</summary>
    public static event Action<AgentSession>? Finished;

    /// <summary>Re-announces the current state, e.g. after a setting about it changed.</summary>
    public static void Nudge() => _dispatcher?.BeginInvoke(() => Updated?.Invoke());

    public static void Start()
    {
        if (!Available || _timer != null)
            return;
        _dispatcher = Dispatcher.CurrentDispatcher;
        // The first scan reads a week of transcripts (tens of MB): leave start-up to Windows first.
        _timer = new Timer(_ => Scan(), null, 15000, 10000);
    }

    public static string Name(AgentProvider provider) => provider == AgentProvider.Claude ? "Claude" : "Codex";

    /// <summary>Agents that left something on this machine, Claude first.</summary>
    public static List<AgentProvider> Providers()
    {
        lock (Gate)
        {
            return new[] { AgentProvider.Claude, AgentProvider.Codex }
                .Where(p => Files.Values.Any(f => f.Provider == p) || Limits.ContainsKey(p))
                .ToList();
        }
    }

    /// <summary>A person's sessions active in the last 12 hours, plus any still waiting for them; working and waiting first.</summary>
    public static List<AgentSession> Sessions()
    {
        lock (Gate)
        {
            var now = DateTime.Now;
            return Files.Values
                .Where(f => f.Id.Length > 0 && f.Listed)
                .Select(ToSession)
                .Concat(Fleet)
                .Where(s => now - s.LastActive < TimeSpan.FromHours(12) || s.NeedsInput && now - s.LastActive < TimeSpan.FromDays(2))
                // A session resumed in a new file keeps one row.
                .GroupBy(s => (s.Provider, s.Id)).Select(g => g.OrderByDescending(s => s.LastActive).First())
                .OrderByDescending(s => s.Working || s.NeedsInput).ThenByDescending(s => s.LastActive)
                .ToList();
        }
    }

    public static int WorkingCount
    {
        get
        {
            lock (Gate)
                return Files.Values.Count(f => f.Listed && IsWorking(f)) + Fleet.Count(s => s.Working);
        }
    }

    public static DateTime? LastActivity(AgentProvider provider)
    {
        lock (Gate)
        {
            var times = Files.Values.Where(f => f.Provider == provider && f.LastEntry != default).Select(f => f.LastEntry).ToList();
            return times.Count > 0 ? times.Max() : null;
        }
    }

    public static AgentLimits? LimitsOf(AgentProvider provider)
    {
        lock (Gate)
            return Limits.TryGetValue(provider, out var limits) ? limits : null;
    }

    public static string? Plan(AgentProvider provider)
    {
        lock (Gate)
            return Plans.TryGetValue(provider, out var plan) ? plan : null;
    }

    /// <summary>Usage between two local days (inclusive), per model.</summary>
    public static List<ModelUsage> UsageBetween(DateTime from, DateTime to)
    {
        lock (Gate)
        {
            return Usage.Where(u => u.Key.Day >= from.Date && u.Key.Day <= to.Date)
                .GroupBy(u => u.Key.Model)
                .Select(g => new ModelUsage(g.Key, g.Sum(x => x.Value[0]), g.Sum(x => x.Value[1]), g.Sum(x => x.Value[2]), g.Sum(x => x.Value[3])))
                .OrderByDescending(m => m.Total)
                .ToList();
        }
    }

    /// <summary>Tokens and API value per agent between two local days (inclusive).</summary>
    public static List<AgentSpend> SpendBetween(DateTime from, DateTime to)
    {
        lock (Gate)
        {
            return Usage.Where(u => u.Key.Day >= from.Date && u.Key.Day <= to.Date)
                .GroupBy(u => u.Key.Provider)
                .Select(g =>
                {
                    double cost = 0;
                    var priced = true;
                    foreach (var (key, t) in g)
                    {
                        if (AgentPricing.Cost(key.Model, t[0], t[1], t[2], t[3]) is { } value)
                            cost += value;
                        else if (t.Sum() > 0)
                            priced = false;
                    }
                    return new AgentSpend(g.Key, g.Sum(x => x.Value[0]), g.Sum(x => x.Value[1]), g.Sum(x => x.Value[2]), g.Sum(x => x.Value[3]), cost, priced);
                })
                .OrderBy(s => s.Provider)
                .ToList();
        }
    }

    /// <summary>Total tokens per day for the last <paramref name="days"/> days, oldest first.</summary>
    public static List<(DateTime Day, long Tokens)> Daily(int days)
    {
        lock (Gate)
        {
            var today = DateTime.Today;
            return Enumerable.Range(0, days).Select(i => today.AddDays(i - days + 1))
                .Select(day => (day, Usage.Where(u => u.Key.Day == day).Sum(u => u.Value.Sum())))
                .ToList();
        }
    }

    private static bool IsWorking(FileState f) =>
        f.Id.Length > 0 && !f.EndedTurn && f.Question.Length == 0
        && DateTime.Now - f.LastEntry < TimeSpan.FromMinutes(f.ToolRunning ? 30 : f.Provider == AgentProvider.Codex ? 5 : 3);

    private static AgentSession ToSession(FileState f)
    {
        Desktop.TryGetValue(f.Id, out var desktop);
        // A Codex thread's wait comes from the session-fleet Codex hook: its permission prompts and replies are not in the log.
        var codexWaits = f.Provider == AgentProvider.Codex && CodexLive.TryGetValue(f.Id, out var live)
            && live.State == "waiting" && live.At >= f.TurnStarted && DateTime.Now - live.At < TimeSpan.FromHours(12);
        var waiting = f.Question.Length > 0 || codexWaits || desktop is { Waiting: true } && !IsWorking(f);
        var title = desktop?.Title is { Length: > 0 } named ? named
            : f.Title.Length > 0 ? f.Title
            : f.Provider == AgentProvider.Codex && CodexTitles.TryGetValue(f.Id, out var thread) ? thread
            : L.T("Phiên ") + f.Id[..Math.Min(8, f.Id.Length)];
        var url = f.Provider == AgentProvider.Codex
            ? "codex://threads/" + Uri.EscapeDataString(f.Id)
            : desktop != null
                ? "claude://code/continue?session=" + Uri.EscapeDataString(desktop.LocalId)
                // A session run in a terminal is brought into the Claude app.
                : "claude://resume?session=" + Uri.EscapeDataString(f.Id);
        var activity = codexWaits ? L.T("Đang chờ bạn trong app Codex")
            : waiting && f.Question.Length == 0 ? L.T("Đang chờ bạn trong app Claude") : f.Activity;
        var last = desktop != null && desktop.LastActivity > f.LastEntry ? desktop.LastActivity : f.LastEntry;
        return new(f.Provider, f.Id, title, f.Project, activity, IsWorking(f) && !codexWaits, waiting, last, f.TurnStarted, f.Tokens, url);
    }

    // ---------- scanning ----------

    private static void Scan()
    {
        // Reading and parsing transcripts is background work: it must not compete with what the person does.
        var priority = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        try
        {
            ScanCore();
        }
        finally
        {
            Thread.CurrentThread.Priority = priority;
        }
    }

    private static void ScanCore()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        var finished = new List<AgentSession>();
        try
        {
            var cutoff = DateTime.Now.AddDays(-8);
            foreach (var (root, provider) in new[] { (ClaudeRoot, AgentProvider.Claude), (CodexRoot, AgentProvider.Codex) })
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
                {
                    if (file.LastWriteTime < cutoff)
                        continue;
                    FileState state;
                    lock (Gate)
                    {
                        if (!Files.TryGetValue(file.FullName, out state!))
                        {
                            Files[file.FullName] = state = new FileState { Provider = provider };
                            // Subagents write their transcripts in a folder under the session's.
                            if (provider == AgentProvider.Claude)
                                state.Listed = string.Equals(file.Directory?.Parent?.FullName.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    if (file.Length <= state.Offset)
                        continue;
                    Read(file.FullName, state);
                    lock (Gate)
                    {
                        var working = IsWorking(state);
                        if (state.Listed && state.WasWorking && !working && state.EndedTurn
                            && state.LastEntry - state.TurnStarted > TimeSpan.FromMinutes(1))
                            finished.Add(ToSession(state));
                        state.WasWorking = working;
                    }
                }
            }
            // Sessions that went quiet without a final entry stop counting as working.
            lock (Gate)
                foreach (var state in Files.Values)
                    if (state.WasWorking && !IsWorking(state))
                        state.WasWorking = false;
            ReadCodexTitles();
            ReadDesktopSessions();
            ReadClaudePlan();
            ReadClaudeLimits();
            ReadCodexLive();
            StartFleetRead();
        }
        catch (Exception ex)
        {
            Store.Log("agent scan failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
        var first = !_scanned;
        _scanned = true;
        _dispatcher?.BeginInvoke(() =>
        {
            // The first scan reads days of history; give that memory back once it is done.
            if (first) Memory.TrimSoon(TimeSpan.FromSeconds(5));
            Updated?.Invoke();
            foreach (var session in finished)
                Finished?.Invoke(session);
        });
    }

    /// <summary>True once per change of the file's write time.</summary>
    private static bool Changed(string path, out DateTime stamp)
    {
        stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        if (stamp == default || Stamps.TryGetValue(path, out var seen) && seen == stamp)
            return false;
        Stamps[path] = stamp;
        return true;
    }

    // One read buffer for every file: scans never overlap, and a fresh megabyte per file was gigabytes of garbage on the
    // first read of the history.
    private static readonly byte[] ReadBuffer = new byte[1 << 20];
    private static readonly List<byte> Carry = new();

    /// <summary>Reads the complete lines added since last time, by bytes, so multi-byte text never shifts the position.</summary>
    private static void Read(string path, FileState state)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var buffer = ReadBuffer;
        var carry = Carry;
        carry.Clear();
        stream.Seek(state.Offset, SeekOrigin.Begin);
        var position = state.Offset;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)10)
                    continue;
                // A line within the buffer is read in place; only one that spans reads is copied.
                ReadOnlyMemory<byte> line;
                if (carry.Count > 0)
                {
                    carry.AddRange(new ArraySegment<byte>(buffer, start, i - start));
                    line = carry.ToArray();
                    carry.Clear();
                }
                else
                    line = new ReadOnlyMemory<byte>(buffer, start, i - start);
                start = i + 1;
                state.Offset = position + start;
                Parse(line, state);
            }
            // The unfinished last line stays for the next read.
            if (start < read)
                carry.AddRange(new ArraySegment<byte>(buffer, start, read - start));
            position += read;
        }
        carry.Clear();
        if (carry.Capacity > 1 << 20)
            carry.Capacity = 0;
    }

    // Only these entries matter; the rest (request blobs, attachments, snapshots) can be megabytes.
    private static readonly byte[][] ClaudeWanted = Markers("\"type\":\"assistant\"", "\"type\":\"user\"", "\"customTitle\"");
    private static readonly byte[][] CodexWanted = Markers("\"session_meta\"", "\"turn_context\"", "\"token_count\"", "\"token_usage_record\"",
        "\"task_started\"", "\"task_complete\"", "\"turn_aborted\"", "\"function_call", "\"custom_tool_call");

    private static byte[][] Markers(params string[] text) => text.Select(Encoding.UTF8.GetBytes).ToArray();

    private static void Parse(ReadOnlyMemory<byte> line, FileState state)
    {
        if (line.Length < 2)
            return;
        var span = line.Span;
        var wanted = false;
        foreach (var marker in state.Provider == AgentProvider.Codex ? CodexWanted : ClaudeWanted)
        {
            if (span.IndexOf(marker) >= 0)
            {
                wanted = true;
                break;
            }
        }
        if (!wanted)
            return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (state.Provider == AgentProvider.Codex)
                ApplyCodex(doc.RootElement, state);
            else
                ApplyClaude(doc.RootElement, state);
        }
        catch (JsonException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

    private static DateTime? Time(JsonElement entry) =>
        Str(entry, "timestamp") is { } text && DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed.ToLocalTime() : null;

    private static string ProjectName(string? cwd)
    {
        var path = (cwd ?? "").TrimEnd('\\', '/');
        // A worktree kept inside the repository still belongs to it.
        var worktree = path.IndexOf(@"\.claude\worktrees\", StringComparison.OrdinalIgnoreCase);
        if (worktree > 0) path = path[..worktree];
        return Path.GetFileName(path);
    }

    private static void AddUsage(DateTime time, AgentProvider provider, string model, long input, long output, long write, long read, FileState state)
    {
        var key = (time.Date, provider, model);
        if (!Usage.TryGetValue(key, out var totals))
            Usage[key] = totals = new long[4];
        totals[0] += input;
        totals[1] += output;
        totals[2] += write;
        totals[3] += read;
        state.Tokens += input + output + write + read;
    }

    // ---------- Claude Code ----------

    private static void ApplyClaude(JsonElement entry, FileState state)
    {
        var type = Str(entry, "type");
        if (Str(entry, "sessionId") is { Length: > 0 } sid && state.Id.Length == 0)
            state.Id = sid;
        if (Str(entry, "cwd") is { } cwd)
            state.Project = ProjectName(cwd);
        if (type == "custom-title" && Str(entry, "customTitle") is { } title)
            state.Title = title;

        var when = Time(entry);
        if (type == "user" && when is { } userTime)
        {
            if (entry.TryGetProperty("message", out var um) && um.ValueKind == JsonValueKind.Object && um.TryGetProperty("content", out var uc))
            {
                if (uc.ValueKind == JsonValueKind.String)
                {
                    // A new prompt means any question was answered or set aside.
                    state.Activity = L.T("Đang đọc yêu cầu: ") + Shorten(uc.GetString());
                    state.Question = "";
                }
                else if (uc.ValueKind == JsonValueKind.Array)
                    foreach (var block in uc.EnumerateArray())
                    {
                        if (Str(block, "type") != "tool_result")
                            continue;
                        state.ToolRunning = false;
                        if (state.Question.Length > 0 && Str(block, "tool_use_id") == state.Question)
                            state.Question = "";
                    }
            }
            // A real prompt (not a tool result) starts a new turn.
            if (state.EndedTurn)
                state.TurnStarted = userTime;
            state.EndedTurn = false;
            state.LastEntry = userTime;
            return;
        }
        if (type != "assistant" || !entry.TryGetProperty("message", out var message) || when is not { } time)
            return;

        state.LastEntry = time;
        var stop = Str(message, "stop_reason");
        state.EndedTurn = stop == "end_turn";
        state.ToolRunning = false;
        if (stop == "end_turn")
        {
            state.Activity = L.T("Đã xong lượt");
            state.Question = "";
        }

        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var kind = Str(block, "type");
                if (kind == "tool_use")
                {
                    state.ToolRunning = true;
                    state.Activity = Describe(Str(block, "name") ?? "", block.TryGetProperty("input", out var input) ? input : default);
                    // These tools stop and wait for the person's answer.
                    if (Str(block, "name") is "AskUserQuestion" or "ExitPlanMode")
                    {
                        state.Question = Str(block, "id") ?? "?";
                        state.Activity = Str(block, "name") == "ExitPlanMode" ? L.T("Chờ bạn duyệt kế hoạch") : L.T("Đang hỏi bạn");
                    }
                }
                else if (kind == "text" && stop != "end_turn" && block.TryGetProperty("text", out var text))
                    state.Activity = Shorten(text.GetString());
            }
        }

        // Streaming writes one line per content block with the same usage; count each message once.
        var id = Str(message, "id");
        if (id == null || !message.TryGetProperty("usage", out var usage))
            return;
        lock (Gate)
        {
            if (!SeenMessages.Add(id))
                return;
            var model = Str(message, "model") ?? "?";
            if (model.StartsWith('<'))
                return; // synthetic entries
            state.Model = model;
            AddUsage(time, AgentProvider.Claude, model, Num(usage, "input_tokens"), Num(usage, "output_tokens"),
                Num(usage, "cache_creation_input_tokens"), Num(usage, "cache_read_input_tokens"), state);
        }
    }

    // ---------- Codex ----------

    private static void ApplyCodex(JsonElement entry, FileState state)
    {
        var type = Str(entry, "type");
        if (!entry.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return;
        var when = Time(entry) ?? DateTime.Now;
        switch (type)
        {
            case "session_meta":
                if (Str(payload, "id") is { Length: > 0 } id && state.Id.Length == 0)
                    state.Id = id;
                if (Str(payload, "cwd") is { } cwd)
                    state.Project = ProjectName(cwd);
                // Scripted runs (codex exec) and the subagents Codex starts itself are not threads a person opens.
                var scripted = Str(payload, "originator") == "codex_exec" || Str(payload, "source") == "exec";
                var helper = payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object
                    && (source.TryGetProperty("subagent", out _) || source.TryGetProperty("internal", out _));
                state.Listed = !scripted && !helper;
                if (state.LastEntry == default)
                    state.LastEntry = when;
                return;
            case "turn_context":
                if (Str(payload, "model") is { Length: > 0 } model)
                    state.Model = model;
                if (Str(payload, "cwd") is { } turnCwd)
                    state.Project = ProjectName(turnCwd);
                return;
            case "token_usage_record":
                if (payload.TryGetProperty("usage", out var record))
                {
                    state.SawRecords = true;
                    lock (Gate)
                        AddCodexUsage(when, record, state);
                }
                state.LastEntry = when;
                return;
            case "response_item":
                ApplyCodexItem(payload, state, when);
                return;
            case "event_msg":
                break;
            default:
                return;
        }

        switch (Str(payload, "type"))
        {
            case "task_started":
                state.EndedTurn = false;
                state.TurnStarted = when;
                state.LastEntry = when;
                state.Activity = L.T("Đang suy nghĩ");
                return;
            case "task_complete":
            case "turn_aborted":
                state.EndedTurn = true;
                state.ToolRunning = false;
                state.Question = "";
                state.LastEntry = when;
                state.Activity = L.T("Đã xong lượt");
                return;
            case "token_count":
                state.LastEntry = when;
                if (payload.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
                    ReadCodexLimits(limits, when);
                if (!state.SawRecords && payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                    && info.TryGetProperty("total_token_usage", out var total))
                {
                    var sum = Num(total, "total_tokens");
                    if (sum > state.LastTotal && info.TryGetProperty("last_token_usage", out var last))
                        lock (Gate)
                            AddCodexUsage(when, last, state);
                    state.LastTotal = Math.Max(state.LastTotal, sum);
                }
                return;
        }
    }

    private static void ApplyCodexItem(JsonElement item, FileState state, DateTime when)
    {
        var kind = Str(item, "type");
        if (kind is "function_call" or "custom_tool_call")
        {
            var name = Str(item, "name") ?? "";
            state.LastEntry = when;
            state.ToolRunning = true;
            if (name.Contains("request_user_input", StringComparison.Ordinal))
            {
                state.Question = Str(item, "call_id") ?? "?";
                state.Activity = L.T("Đang hỏi bạn");
            }
            else
                state.Activity = Describe(name, default);
        }
        else if (kind is "function_call_output" or "custom_tool_call_output")
        {
            state.LastEntry = when;
            state.ToolRunning = false;
            if (state.Question.Length > 0 && Str(item, "call_id") == state.Question)
                state.Question = "";
        }
    }

    private static void AddCodexUsage(DateTime when, JsonElement usage, FileState state)
    {
        var input = Num(usage, "input_tokens");
        var cached = Math.Min(input, Num(usage, "cached_input_tokens"));
        var written = Math.Min(input - cached, Num(usage, "cache_write_input_tokens"));
        AddUsage(when, AgentProvider.Codex, state.Model.Length > 0 ? state.Model : "?", input - cached - written,
            Num(usage, "output_tokens"), written, cached, state);
    }

    /// <summary>Windows are told apart by their length, never by their slot: an account can report only its week, in either slot.</summary>
    private static void ReadCodexLimits(JsonElement limits, DateTime observed)
    {
        // Codex also reports a bucket per model; only the main one is the plan's.
        var bucket = (Str(limits, "limit_id") ?? "").ToLowerInvariant();
        if (bucket.Length > 0 && bucket != "codex")
            return;
        var windows = new List<LimitWindow>();
        foreach (var slot in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(slot, out var window) || window.ValueKind != JsonValueKind.Object
                || !window.TryGetProperty("used_percent", out var used) || used.ValueKind != JsonValueKind.Number)
                continue;
            var minutes = (int)Num(window, "window_minutes");
            DateTime? resets = Num(window, "resets_at") is > 0 and var epoch ? DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime : null;
            windows.Add(new LimitWindow(minutes is > 0 and <= 720, minutes, Math.Clamp(used.GetDouble(), 0, 100), resets, false));
        }
        lock (Gate)
        {
            if (Limits.TryGetValue(AgentProvider.Codex, out var known) && known.Observed > observed)
                return;
            if (windows.Count > 0)
                Limits[AgentProvider.Codex] = new AgentLimits(AgentProvider.Codex, windows.OrderBy(w => w.Minutes).ToList(), observed);
            if (Str(limits, "plan_type") is { Length: > 0 } plan)
                Plans[AgentProvider.Codex] = CodexPlan(plan);
        }
    }

    private static string CodexPlan(string plan) => plan.ToLowerInvariant() switch
    {
        "plus" => "Plus",
        "pro" => "Pro",
        "go" => "Go",
        "free" => "Free",
        "business" or "team" => "Business",
        "enterprise" => "Enterprise",
        "edu" => "Edu",
        _ => char.ToUpperInvariant(plan[0]) + plan[1..],
    };

    /// <summary>Codex names its app threads in session_index.jsonl.</summary>
    private static void ReadCodexTitles()
    {
        var path = Path.Combine(CodexHome, "session_index.jsonl");
        if (!Changed(path, out _))
            return;
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (Str(doc.RootElement, "id") is { } id && Str(doc.RootElement, "thread_name") is { Length: > 0 } name)
                        titles[id] = name;
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
            return;
        }
        lock (Gate)
        {
            CodexTitles.Clear();
            foreach (var (id, name) in titles)
                CodexTitles[id] = name;
        }
    }

    // ---------- the Claude app ----------

    /// <summary>Where the Claude desktop app keeps its data: the classic install, the Store package, or a custom data folder.</summary>
    private static IEnumerable<string> ClaudeAppFolders()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folders = new List<string> { Path.Combine(roaming, "Claude") };
        try
        {
            foreach (var package in Directory.EnumerateDirectories(Path.Combine(local, "Packages"), "Claude_*"))
                folders.Add(Path.Combine(package, "LocalCache", "Roaming", "Claude"));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        var data = Path.Combine(local, "Claude-Data");
        if (Directory.Exists(data))
            folders.AddRange(Directory.EnumerateDirectories(data));
        return folders.Where(Directory.Exists);
    }

    /// <summary>
    /// The Claude app's Code sessions, by the Claude Code session they run: the
    /// app's own id opens the session there, and its summary says when the
    /// session is waiting for the person.
    /// </summary>
    private static void ReadDesktopSessions()
    {
        var recent = DateTime.UtcNow.AddDays(-2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in ClaudeAppFolders())
        {
            var root = Path.Combine(folder, "claude-code-sessions");
            if (!Directory.Exists(root))
                continue;
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.json", SearchOption.AllDirectories))
            {
                if (file.LastWriteTimeUtc < recent)
                    continue;
                seen.Add(file.FullName);
                if (DesktopFiles.TryGetValue(file.FullName, out var known) && known.Stamp == file.LastWriteTimeUtc)
                    continue;
                DesktopFiles[file.FullName] = (file.LastWriteTimeUtc, ReadDesktopSession(file.FullName, out var ids), ids);
            }
        }
        foreach (var gone in DesktopFiles.Keys.Where(k => !seen.Contains(k)).ToList())
            DesktopFiles.Remove(gone);
        lock (Gate)
        {
            Desktop.Clear();
            foreach (var (_, session, ids) in DesktopFiles.Values.OrderBy(v => v.Session?.LastActivity))
                if (session != null)
                    foreach (var id in ids)
                        Desktop[id] = session;
        }
    }

    private static DesktopSession? ReadDesktopSession(string path, out string[] ids)
    {
        ids = Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (Str(root, "sessionId") is not { } local || !local.StartsWith("local_", StringComparison.Ordinal)
                || root.TryGetProperty("isArchived", out var archived) && archived.ValueKind == JsonValueKind.True)
                return null;
            ids = new[] { Str(root, "cliSessionId"), Str(root, "lastAssistantCliSessionId") }
                .Where(id => !string.IsNullOrEmpty(id)).Distinct().ToArray()!;
            // The app files a turn under "blocked" or "need input" when the person has to act.
            var waiting = false;
            if (root.TryGetProperty("postTurnSummary", out var summary) && summary.ValueKind == JsonValueKind.Object
                && Str(summary, "status_category") is "blocked" or "need_input" or "needs_input")
                waiting = Str(root, "postTurnSummaryFor") is not { } about || about == Str(root, "lastAssistantUuid");
            var last = Num(root, "lastActivityAt") is > 0 and var ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : default;
            return new DesktopSession(local, Str(root, "title") ?? "", waiting, last);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The plan Claude Code is signed in with, from ~/.claude.json.</summary>
    private static void ReadClaudePlan()
    {
        var path = Path.Combine(Home, ".claude.json");
        if (!Changed(path, out _))
            return;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("oauthAccount", out var account) || account.ValueKind != JsonValueKind.Object)
                return;
            var tier = ((Str(account, "organizationRateLimitTier") ?? "") + " " + (Str(account, "organizationType") ?? "")).ToLowerInvariant();
            string? plan = tier.Contains("max_20x") ? "Max 20×"
                : tier.Contains("max_5x") ? "Max 5×"
                : tier.Contains("max") ? "Max"
                : tier.Contains("enterprise") ? "Enterprise"
                : tier.Contains("team") ? "Team"
                : tier.Contains("pro") ? "Pro"
                : null;
            lock (Gate)
            {
                if (plan != null) Plans[AgentProvider.Claude] = plan;
                else Plans.Remove(AgentProvider.Claude);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Sample(DateTime Time, string? Org, double? Session, double? Week);

    /// <summary>
    /// The Claude app checks the plan's limits itself and keeps them in
    /// plan-usage-history.json, a percentage per window every few minutes. The
    /// file has no renewal times: a session renews five hours after it began,
    /// which the readings bracket, and a week renews where its drops say.
    /// </summary>
    private static void ReadClaudeLimits()
    {
        var path = ClaudeAppFolders().Select(f => Path.Combine(f, "plan-usage-history.json")).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (path == null)
            return;
        var samples = new List<Sample>();
        if (Changed(path, out _))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                if (!doc.RootElement.TryGetProperty("samples", out var list) || list.ValueKind != JsonValueKind.Array)
                    return;
                var version = Num(doc.RootElement, "version");
                foreach (var entry in list.EnumerateArray())
                {
                    var ms = Num(entry, "t");
                    if (ms <= 0) continue;
                    var values = version == 1 ? entry : entry.TryGetProperty("u", out var u) ? u : default;
                    double? Get(string key) => values.ValueKind == JsonValueKind.Object && values.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
                        ? Math.Clamp(v.GetDouble(), 0, 100) : null;
                    samples.Add(new Sample(DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime, Str(entry, "org"), Get("fh"), Get("sd")));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return;
            }
            _claudeSamples = samples.OrderBy(s => s.Time).ToList();
        }
        var history = _claudeSamples;
        if (history.Count == 0)
            return;
        var latest = history[^1];
        history = history.Where(s => s.Org == latest.Org).ToList();
        var now = DateTime.Now;
        var windows = new List<LimitWindow>();
        if (now - latest.Time < TimeSpan.FromDays(7))
        {
            var session = TimeSpan.FromHours(5);
            if (latest.Session is { } used && now - latest.Time < session)
            {
                var (resets, estimated) = SessionEnd(history, session);
                if (used <= 0 || resets is { } r && r > now)
                    windows.Add(new LimitWindow(true, 300, used, used > 0 ? resets : null, estimated));
            }
            if (latest.Week is { } week)
            {
                var resets = WeekRenewal(history, latest.Time);
                if (resets is { } r ? r > now : now - latest.Time < TimeSpan.FromDays(1))
                    windows.Add(new LimitWindow(false, 10080, week, resets, resets != null));
            }
        }
        lock (Gate)
        {
            if (windows.Count > 0)
                Limits[AgentProvider.Claude] = new AgentLimits(AgentProvider.Claude, windows, latest.Time);
            else
                Limits.Remove(AgentProvider.Claude);
        }
    }

    private static List<Sample> _claudeSamples = new();

    /// <summary>
    /// The run of readings above zero ending with the latest places the session's
    /// start. Readings a few minutes apart bracket it closely; after a longer gap
    /// (the PC asleep, the app closed) the middle of the gap is the best guess.
    /// </summary>
    private static (DateTime? Resets, bool Estimated) SessionEnd(List<Sample> history, TimeSpan length)
    {
        var first = history.Count - 1;
        if ((history[first].Session ?? 0) <= 0)
            return (null, false);
        while (first > 0 && history[first - 1].Session is > 0 and var before && before <= (history[first].Session ?? 0) + 1
               && history[first].Time - history[first - 1].Time < length)
            first--;
        var latest = history[first].Time;
        if (first == 0 || latest - history[first - 1].Time <= TimeSpan.FromMinutes(30))
            return (latest + length, false);
        var lower = history[first - 1].Time > latest - length ? history[first - 1].Time : latest - length;
        return (lower + (latest - lower) / 2 + length, true);
    }

    /// <summary>The week renews where its last drop happened, on the hour, every seven days after.</summary>
    private static DateTime? WeekRenewal(List<Sample> history, DateTime reading)
    {
        for (var i = history.Count - 1; i > 0; i--)
        {
            if (history[i - 1].Week is not { } before || history[i].Week is not { } after || after + 1 >= before)
                continue;
            var earliest = history[i - 1].Time;
            var hour = new DateTime(earliest.Year, earliest.Month, earliest.Day, earliest.Hour, 0, 0).AddHours(1);
            var moment = hour <= history[i].Time ? hour : history[i].Time;
            while (moment <= reading)
                moment = moment.AddDays(7);
            return moment;
        }
        return null;
    }

    // ---------- Codex threads' state from the session-fleet Codex hook ----------

    private static readonly Dictionary<string, (string State, DateTime At)> CodexLive = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> CodexLiveStamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The registry folder and this machine's name in it, from ~/.claude/session-fleet.json.</summary>
    private static (string Registry, string Machine)? FleetConfig()
    {
        try
        {
            var config = Path.Combine(Home, ".claude", "session-fleet.json");
            if (!File.Exists(config))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(config));
            var registry = Str(doc.RootElement, "registryDir") ?? Path.Combine(Home, ".claude", "session-fleet-registry");
            var machine = Str(doc.RootElement, "machineName") ?? Environment.MachineName;
            return (registry, machine);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// This machine's Codex threads as the session-fleet Codex hook records them: working, waiting (a permission
    /// prompt, or a reply that needs the person) or idle. Read from the registry folder, changed files only.
    /// </summary>
    private static void ReadCodexLive()
    {
        if (FleetConfig() is not { } fleet)
            return;
        var folder = Path.Combine(fleet.Registry, "sessions", fleet.Machine);
        if (!Directory.Exists(folder))
            return;
        var recent = DateTime.Now.AddDays(-2);
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.json"))
        {
            if (file.LastWriteTime < recent || CodexLiveStamps.TryGetValue(file.FullName, out var seen) && seen == file.LastWriteTime)
                continue;
            CodexLiveStamps[file.FullName] = file.LastWriteTime;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
                var rec = doc.RootElement;
                if (Str(rec, "provider") != "codex" || !rec.TryGetProperty("live", out var live) || Str(rec, "sessionId") is not { } id)
                    continue;
                if (Str(live, "state") is { } state && Str(live, "at") is { } at
                    && DateTime.TryParse(at, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when))
                    lock (Gate)
                        CodexLive[id] = (state, when.ToLocalTime());
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ---------- other machines ----------

    private static List<AgentSession> Fleet = new();
    private static DateTime _fleetRead;
    private static int _fleetBusy;

    /// <summary>Fetching can take many seconds over the network: it runs on its own, and the page redraws when it lands.</summary>
    private static void StartFleetRead()
    {
        if (DateTime.Now - _fleetRead < TimeSpan.FromSeconds(60) || Interlocked.Exchange(ref _fleetBusy, 1) == 1)
            return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                ReadFleet();
            }
            catch (Exception ex)
            {
                Store.Log("fleet read failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _fleetBusy, 0);
                Nudge();
            }
        });
    }

    /// <summary>
    /// Sessions on the account's other machines, from the session-fleet plugin's
    /// shared registry: a git clone each machine pushes its sessions to, with what
    /// each is doing (working, waiting for the person) and its Remote Control
    /// session. Fetched once a minute and read from the fetched branch, so the
    /// plugin's own pulls and pushes are never in the way.
    /// </summary>
    private static void ReadFleet()
    {
        if (DateTime.Now - _fleetRead < TimeSpan.FromSeconds(60))
            return;
        _fleetRead = DateTime.Now;
        string? registry = null, machine = null;
        try
        {
            var config = Path.Combine(Home, ".claude", "session-fleet.json");
            if (!File.Exists(config))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(config));
            registry = Str(doc.RootElement, "registryDir");
            machine = Str(doc.RootElement, "machineName");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return;
        }
        registry ??= Path.Combine(Home, ".claude", "session-fleet-registry");
        if (!Directory.Exists(Path.Combine(registry, ".git")))
            return;
        Git(registry, null, "fetch", "-q");
        // Nothing pushed since the last read: the sessions are as they were, only older.
        var head = Git(registry, null, "rev-parse", "@{upstream}")?.Trim();
        if (head == null)
            return;
        if (head == _fleetHead)
        {
            var now = DateTime.Now;
            lock (Gate)
                Fleet = _fleetRecords.Select(r => FleetSession(r, now)).OfType<AgentSession>().ToList();
            return;
        }
        var listed = Git(registry, null, "ls-tree", "-r", "--name-only", "@{upstream}", "--", "sessions");
        if (listed == null)
            return;
        var paths = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith(".json") && p.Split('/').Length == 3 && !string.Equals(p.Split('/')[1], machine, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var records = new List<JsonElement>();
        if (paths.Count > 0 && Git(registry, string.Join("\n", paths.Select(p => "@{upstream}:" + p)) + "\n", "cat-file", "--batch") is { } batch)
        {
            // Each object is "<sha> blob <size>\n<json>\n"; every record is one JSON object. Only the ones that say what
            // the session is doing are kept: the rest never show.
            foreach (var chunk in batch.Split("\n{", StringSplitOptions.None).Skip(1))
            {
                var end = chunk.LastIndexOf('}');
                if (end < 0 || !chunk.Contains("\"live\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse("{" + chunk[..(end + 1)]);
                    records.Add(doc.RootElement.Clone());
                }
                catch (JsonException)
                {
                }
            }
        }
        _fleetHead = head;
        _fleetRecords = records;
        var at = DateTime.Now;
        lock (Gate)
            Fleet = records.Select(r => FleetSession(r, at)).OfType<AgentSession>().ToList();
    }

    private static string? _fleetHead;
    private static List<JsonElement> _fleetRecords = new();

    private static AgentSession? FleetSession(JsonElement rec, DateTime now)
    {
        if (Str(rec, "status") == "done" || !rec.TryGetProperty("live", out var live) || live.ValueKind != JsonValueKind.Object)
            return null;
        var state = Str(live, "state");
        if (Str(live, "at") is not { } atText || !DateTime.TryParse(atText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
            return null;
        at = at.ToLocalTime();
        // A machine that went to sleep mid-turn never says the turn ended.
        var working = state == "working" && now - at < TimeSpan.FromHours(6);
        // A wait longer than a working day is more likely a push that never came than a person still awaited.
        var waiting = state == "waiting" && now - at < TimeSpan.FromHours(12);
        // A session that finished its turn stays listed for 12 hours, as this machine's own do; only working and
        // waiting ones reach the Island.
        if (!working && !waiting && now - at >= TimeSpan.FromHours(12))
            return null;
        var machine = Str(rec, "machine") ?? "?";
        var title = Str(rec, "userTitle") is { Length: > 0 } user ? user
            : $"[{machine}] {(Str(rec, "feature") is { Length: > 0 } feature ? feature : "new session")}";
        var codex = Str(rec, "provider") == "codex";
        var remote = Str(rec, "remoteSession");
        // A Codex thread on another machine has no link that opens it here: its machine's screen does.
        var url = !codex && remote is { Length: > 0 } ? "claude://claude.ai/code/" + Uri.EscapeDataString(remote) : "";
        return new AgentSession(codex ? AgentProvider.Codex : AgentProvider.Claude, Str(rec, "sessionId") ?? "", title, ProjectName(Str(rec, "cwd")),
            "", working, waiting, at, at, 0, url, machine, Str(rec, "host") ?? "");
    }

    /// <summary>Runs git in the registry; its output, or null when it failed or took too long.</summary>
    private static string? Git(string folder, string? input, params string[] args)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = input != null,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            // Never wait on a credential prompt.
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var git = System.Diagnostics.Process.Start(start);
            if (git == null) return null;
            var output = git.StandardOutput.ReadToEndAsync();
            _ = git.StandardError.ReadToEndAsync();
            if (input != null)
            {
                git.StandardInput.Write(input);
                git.StandardInput.Close();
            }
            if (!git.WaitForExit(30_000))
            {
                try { git.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            return git.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---------- text ----------

    private static string Describe(string name, JsonElement input)
    {
        string? detail = null;
        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in new[] { "description", "file_path", "path", "pattern", "url", "query", "command", "prompt" })
            {
                if (Str(input, field) is { } value)
                {
                    detail = field is "file_path" or "path" ? Path.GetFileName(value) : value;
                    break;
                }
            }
        }
        var verb = name switch
        {
            "Bash" or "PowerShell" or "shell" or "shell_command" or "exec_command" or "local_shell" => L.T("Chạy lệnh"),
            "Read" or "view_image" => L.T("Đọc"),
            "Edit" or "MultiEdit" or "apply_patch" => L.T("Sửa"),
            "Write" => L.T("Viết"),
            "Grep" or "Glob" => L.T("Tìm"),
            "WebFetch" or "WebSearch" or "web_search" => "Tra web",
            "Agent" or "Task" or "spawn_agent" => L.T("Giao việc cho agent"),
            "update_plan" => L.T("Cập nhật kế hoạch"),
            _ => name.StartsWith("mcp__") ? L.T("Dùng công cụ ") + name.Split("__").Last() : name,
        };
        return detail is { Length: > 0 } ? $"{verb}: {Shorten(detail)}" : verb;
    }

    private static string Shorten(string? text)
    {
        text = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length > 90 ? text[..88] + "…" : text;
    }

    public static string Tokens(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1e9:0.0}B",
        >= 1_000_000 => $"{value / 1e6:0.0}M",
        >= 1_000 => $"{value / 1e3:0.0}K",
        _ => value.ToString(),
    };
}

/// <summary>
/// API list prices in USD per million tokens, to show what the use would have
/// cost on the API. Plans are billed differently; this is a value, not a bill.
/// </summary>
internal static class AgentPricing
{
    // input, output, cache read, cache write (5-minute)
    private static readonly (string Id, double In, double Out, double Read, double Write)[] Prices =
    {
        ("claude-fable-5-1", 10, 50, 0.25, 12.5),
        ("claude-fable-5", 10, 50, 1, 12.5),
        ("claude-opus-5-5", 4, 20, 0.2, 5),
        ("claude-opus-5", 5, 25, 0.5, 6.25),
        ("claude-opus-4", 15, 75, 1.5, 18.75),
        ("claude-sonnet-5-5", 2, 10, 0.1, 2.5),
        ("claude-sonnet-5", 2, 10, 0.2, 2.5),
        ("claude-sonnet-4", 3, 15, 0.3, 3.75),
        ("claude-haiku-5-5", 0.1, 0.5, 0.01, 0.125),
        ("claude-haiku-4", 1, 5, 0.1, 1.25),
        ("gpt-6.1-sol", 2, 10, 0.1, 2.5),
        ("gpt-6.1-terra", 1, 5, 0.05, 1.25),
        ("gpt-6-astra", 10, 50, 1, 12.5),
        ("gpt-6-sol", 2, 10, 0.2, 2.5),
        ("gpt-6-luna", 0.1, 0.5, 0.01, 0.125),
    };

    /// <summary>Null for a model without a known price; a longer id wins over its family.</summary>
    public static double? Cost(string model, long input, long output, long cacheWrite, long cacheRead)
    {
        var matches = Prices.Where(p => model.StartsWith(p.Id, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Id.Length).ToList();
        if (matches.Count == 0)
            return null;
        var price = matches[0];
        return (input * price.In + output * price.Out + cacheRead * price.Read + cacheWrite * price.Write) / 1e6;
    }

    public static string Format(double usd) => usd >= 1000 ? $"${usd:#,0}" : $"${usd:0.00}";
}
