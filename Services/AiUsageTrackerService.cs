using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsNotch.Services;

/// <summary>
/// Tracks local usage for AI coding tools by incrementally tailing their on-disk session logs:
///  • Claude Code  → %USERPROFILE%\.claude\projects\**\*.jsonl
///  • Codex CLI    → %USERPROFILE%\.codex\sessions\**\*.jsonl
///  • Antigravity  → %USERPROFILE%\.gemini\antigravity\brain\*\.system_generated\logs\transcript.jsonl
/// Everything is read-only and stays on this machine. Also raises workflow events when a tool finishes a turn/task.
/// </summary>
public sealed class AiUsageTrackerService : IDisposable
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Antigravity = "antigravity";

    public sealed class ToolSnapshot
    {
        public string Key = "";
        public bool Installed;
        public long TodayIn, TodayOut, TodayCache;
        public int TodayTurns, TodaySessions;
        public long WeekTotal;
        public DateTime? LastActivity;
        public bool Active;
        public string LastModel = "";
        public string LastProject = "";
        public string RateLimit = "";
    }

    public sealed class WorkflowEventArgs
    {
        public string ToolKey = "";
        public string Title = "";
        public string Message = "";
        public string Project = "";
    }

    private sealed class DayStats
    {
        public long In, Out, Cache;
        public int Turns;
        public HashSet<string> Sessions = new();
    }

    private sealed class ToolState
    {
        public string Key = "";
        public string Root = "";
        public Dictionary<DateTime, DayStats> Days = new();
        public HashSet<string> SeenIds = new();
        public DateTime? LastActivity;
        public string LastModel = "";
        public string LastProject = "";
        public string RateLimit = "";
    }

    private sealed class FileCursor
    {
        public long Offset;
        public string Tool = "";
        public string SessionKey = "";
        public string Project = "";
    }

    private readonly Dictionary<string, ToolState> _tools = new();
    private readonly Dictionary<string, FileCursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastEventPerSession = new();
    private readonly string _home;
    private Timer? _timer;
    private int _scanning;
    private bool _initialScanDone;

    public event Action<IReadOnlyList<ToolSnapshot>>? SnapshotUpdated;
    public event Action<WorkflowEventArgs>? WorkflowEvent;

    public AiUsageTrackerService()
    {
        _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _tools[Claude] = new ToolState { Key = Claude, Root = Path.Combine(_home, ".claude", "projects") };
        _tools[Codex] = new ToolState { Key = Codex, Root = Path.Combine(_home, ".codex", "sessions") };
        _tools[Antigravity] = new ToolState { Key = Antigravity, Root = Path.Combine(_home, ".gemini", "antigravity", "brain") };
    }

    public string GetRoot(string key) => _tools.TryGetValue(key, out var t) ? t.Root : _home;

    public void Start()
    {
        _timer = new Timer(_ => ScanSafe(), null, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(4));
    }

    public void ForceRefresh() => Task.Run(ScanSafe);

    private void ScanSafe()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            Scan();
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private void Scan()
    {
        DateTime cutoff = DateTime.Now.AddDays(-7);
        bool emitEvents = _initialScanDone;

        foreach (var file in EnumerateClaudeFiles(cutoff)) ProcessFile(file, Claude, emitEvents);
        foreach (var file in EnumerateCodexFiles(cutoff)) ProcessFile(file, Codex, emitEvents);
        foreach (var file in EnumerateAntigravityFiles(cutoff)) ProcessFile(file, Antigravity, emitEvents);

        _initialScanDone = true;
        PruneOldDays();
        SnapshotUpdated?.Invoke(BuildSnapshot());
    }

    private IEnumerable<string> EnumerateClaudeFiles(DateTime cutoff)
    {
        string root = _tools[Claude].Root;
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToList(); }
        catch { yield break; }
        foreach (var f in files)
        {
            if (SafeLastWrite(f) >= cutoff) yield return f;
        }
    }

    private IEnumerable<string> EnumerateCodexFiles(DateTime cutoff)
    {
        string root = _tools[Codex].Root;
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToList(); }
        catch { yield break; }
        foreach (var f in files)
        {
            if (SafeLastWrite(f) >= cutoff) yield return f;
        }
    }

    private IEnumerable<string> EnumerateAntigravityFiles(DateTime cutoff)
    {
        string root = _tools[Antigravity].Root;
        if (!Directory.Exists(root)) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); }
        catch { yield break; }
        foreach (var d in dirs)
        {
            string f = Path.Combine(d, ".system_generated", "logs", "transcript.jsonl");
            if (File.Exists(f) && SafeLastWrite(f) >= cutoff) yield return f;
        }
    }

    private static DateTime SafeLastWrite(string f)
    {
        try { return File.GetLastWriteTime(f); } catch { return DateTime.MinValue; }
    }

    private void ProcessFile(string path, string tool, bool emitEvents)
    {
        long length;
        try { length = new FileInfo(path).Length; } catch { return; }

        if (!_cursors.TryGetValue(path, out var cursor))
        {
            cursor = new FileCursor { Tool = tool, SessionKey = path };
            if (tool == Claude)
            {
                // projects\<encoded-cwd>\<session>.jsonl  → readable project name
                string? dir = Path.GetFileName(Path.GetDirectoryName(path));
                if (path.Contains($"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    dir = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))));
                }
                cursor.Project = PrettyClaudeProject(dir);
            }
            else if (tool == Antigravity)
            {
                string convId = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)))) ?? "";
                cursor.Project = convId.Length >= 8 ? $"Conversation {convId[..8]}" : "Conversation";
            }
            _cursors[path] = cursor;
        }

        if (length < cursor.Offset)
        {
            // File was rewritten — Codex is append-only so skip; others are de-duplicated by id.
            cursor.Offset = tool == Codex ? length : 0;
        }
        if (length == cursor.Offset) return;

        string chunk;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(cursor.Offset, SeekOrigin.Begin);
            long toRead = length - cursor.Offset;
            if (toRead > 64L * 1024 * 1024) // very large backlog: only parse the tail
            {
                fs.Seek(length - 64L * 1024 * 1024, SeekOrigin.Begin);
                toRead = 64L * 1024 * 1024;
            }
            var buffer = new byte[toRead];
            int read = 0;
            while (read < toRead)
            {
                int n = fs.Read(buffer, read, (int)Math.Min(toRead - read, 1 << 20));
                if (n <= 0) break;
                read += n;
            }
            // Only consume up to the last complete line
            int lastNl = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(0, read - 1));
            if (lastNl < 0) return;
            chunk = Encoding.UTF8.GetString(buffer, 0, lastNl + 1);
            cursor.Offset = length - toRead + lastNl + 1;
        }
        catch
        {
            return;
        }

        var state = _tools[tool];
        foreach (var rawLine in chunk.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length < 2 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                switch (tool)
                {
                    case Claude: HandleClaude(root, state, cursor, path, emitEvents); break;
                    case Codex: HandleCodex(root, state, cursor, emitEvents); break;
                    case Antigravity: HandleAntigravity(root, state, cursor, path, emitEvents); break;
                }
            }
            catch { }
        }
    }

    // ───────────────────────────── Claude Code ─────────────────────────────
    private void HandleClaude(JsonElement root, ToolState state, FileCursor cursor, string path, bool emitEvents)
    {
        if (!TryStr(root, "type", out var type) || type != "assistant") return;
        if (!root.TryGetProperty("message", out var msg)) return;

        DateTime ts = ParseTs(root, "timestamp");
        string msgId = TryStr(msg, "id", out var id) ? id : "";
        string reqId = TryStr(root, "requestId", out var r) ? r : "";
        string dedupeKey = $"{msgId}|{reqId}";

        if (TryStr(root, "cwd", out var cwd) && !string.IsNullOrWhiteSpace(cwd))
        {
            cursor.Project = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        }

        bool isNew = msgId.Length == 0 || state.SeenIds.Add(dedupeKey);
        var day = GetDay(state, ts);
        day.Sessions.Add(cursor.SessionKey);

        if (isNew && msg.TryGetProperty("usage", out var usage))
        {
            long input = GetLong(usage, "input_tokens") + GetLong(usage, "cache_creation_input_tokens");
            day.In += input;
            day.Out += GetLong(usage, "output_tokens");
            day.Cache += GetLong(usage, "cache_read_input_tokens");
        }

        Touch(state, ts, TryStr(msg, "model", out var model) ? model : null, cursor.Project);

        bool isSubagent = path.Contains($"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        if (isNew && TryStr(msg, "stop_reason", out var stop) && stop == "end_turn" && !isSubagent)
        {
            day.Turns++;
            if (emitEvents)
            {
                string text = ExtractClaudeText(msg);
                RaiseEvent(Claude, cursor.SessionKey, "Claude Code finished", text, cursor.Project);
            }
        }
    }

    private static string ExtractClaudeText(JsonElement msg)
    {
        try
        {
            if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (TryStr(block, "type", out var bt) && bt == "text" && TryStr(block, "text", out var txt))
                        return Snippet(txt);
                }
            }
        }
        catch { }
        return "Turn complete — ready for your next prompt.";
    }

    private static string PrettyClaudeProject(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return "";
        // e.g. "f--sdhelp" → "sdhelp", "C--Users-me-repo" → "repo"
        var parts = encoded.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : encoded;
    }

    // ───────────────────────────── Codex CLI ─────────────────────────────
    private void HandleCodex(JsonElement root, ToolState state, FileCursor cursor, bool emitEvents)
    {
        DateTime ts = ParseTs(root, "timestamp");
        if (!root.TryGetProperty("payload", out var payload)) return;
        TryStr(root, "type", out var type);

        if (type == "session_meta" || type == "turn_context")
        {
            if (TryStr(payload, "cwd", out var cwd) && !string.IsNullOrWhiteSpace(cwd))
                cursor.Project = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            if (TryStr(payload, "model", out var m) && !string.IsNullOrWhiteSpace(m))
                state.LastModel = m;
            return;
        }

        if (type != "event_msg" || !TryStr(payload, "type", out var ptype)) return;
        var day = GetDay(state, ts);
        day.Sessions.Add(cursor.SessionKey);

        if (ptype == "token_count")
        {
            if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
            {
                long input = GetLong(last, "input_tokens");
                long cached = GetLong(last, "cached_input_tokens");
                day.In += Math.Max(0, input - cached);
                day.Cache += cached;
                day.Out += GetLong(last, "output_tokens");
            }
            if (payload.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
            {
                state.RateLimit = FormatCodexRateLimits(rl);
            }
            Touch(state, ts, null, cursor.Project);
        }
        else if (ptype == "task_started")
        {
            if (TryStr(payload, "model", out var m) && !string.IsNullOrWhiteSpace(m)) state.LastModel = m;
            Touch(state, ts, null, cursor.Project);
        }
        else if (ptype == "task_complete")
        {
            day.Turns++;
            Touch(state, ts, null, cursor.Project);
            if (emitEvents)
            {
                string text = TryStr(payload, "last_agent_message", out var lam) ? Snippet(lam) : "Task complete — ready for review.";
                RaiseEvent(Codex, cursor.SessionKey, "Codex task complete", text, cursor.Project);
            }
        }
    }

    private static string FormatCodexRateLimits(JsonElement rl)
    {
        var parts = new List<string>();
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (rl.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object)
            {
                double pct = w.TryGetProperty("used_percent", out var p) && p.TryGetDouble(out var pv) ? pv : -1;
                long mins = GetLong(w, "window_minutes");
                if (pct >= 0)
                {
                    string window = mins >= 1440 ? $"{mins / 1440}d" : mins >= 60 ? $"{mins / 60}h" : $"{mins}m";
                    parts.Add($"{window}: {pct:0}% used");
                }
            }
        }
        return string.Join(" • ", parts);
    }

    // ───────────────────────────── Antigravity ─────────────────────────────
    private void HandleAntigravity(JsonElement root, ToolState state, FileCursor cursor, string path, bool emitEvents)
    {
        if (!TryStr(root, "type", out var type) || type != "PLANNER_RESPONSE") return;
        long step = GetLong(root, "step_index");
        if (!state.SeenIds.Add($"{path}#{step}")) return;

        DateTime ts = ParseTs(root, "created_at");
        var day = GetDay(state, ts);
        day.Sessions.Add(cursor.SessionKey);
        day.In += GetLong(root, "input_tokens");
        day.Out += GetLong(root, "output_tokens");
        day.Cache += GetLong(root, "cache_read_tokens");
        Touch(state, ts, null, cursor.Project);

        bool hasTools = root.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array && tc.GetArrayLength() > 0;
        bool done = !TryStr(root, "status", out var st) || st == "DONE";
        if (!hasTools && done)
        {
            day.Turns++;
            if (emitEvents)
            {
                string text = TryStr(root, "content", out var c) ? Snippet(c) : "Agent finished — ready for your review.";
                RaiseEvent(Antigravity, cursor.SessionKey, "Antigravity agent finished", text, cursor.Project);
            }
        }
    }

    // ───────────────────────────── helpers ─────────────────────────────
    private void RaiseEvent(string tool, string sessionKey, string title, string message, string project)
    {
        DateTime now = DateTime.Now;
        if (_lastEventPerSession.TryGetValue(sessionKey, out var last) && (now - last).TotalSeconds < 15) return;
        _lastEventPerSession[sessionKey] = now;
        WorkflowEvent?.Invoke(new WorkflowEventArgs { ToolKey = tool, Title = title, Message = message, Project = project });
    }

    private static void Touch(ToolState state, DateTime ts, string? model, string project)
    {
        if (state.LastActivity == null || ts > state.LastActivity) state.LastActivity = ts;
        if (!string.IsNullOrWhiteSpace(model) && !model.StartsWith("<")) state.LastModel = model!;
        if (!string.IsNullOrWhiteSpace(project)) state.LastProject = project;
    }

    private static DayStats GetDay(ToolState state, DateTime ts)
    {
        var key = ts.Date;
        if (!state.Days.TryGetValue(key, out var d))
        {
            d = new DayStats();
            state.Days[key] = d;
        }
        return d;
    }

    private void PruneOldDays()
    {
        var cutoff = DateTime.Today.AddDays(-8);
        foreach (var t in _tools.Values)
        {
            foreach (var k in t.Days.Keys.Where(k => k < cutoff).ToList()) t.Days.Remove(k);
        }
    }

    private List<ToolSnapshot> BuildSnapshot()
    {
        var list = new List<ToolSnapshot>();
        var today = DateTime.Today;
        var weekStart = today.AddDays(-6);
        foreach (var t in _tools.Values)
        {
            t.Days.TryGetValue(today, out var d);
            long week = t.Days.Where(kv => kv.Key >= weekStart).Sum(kv => kv.Value.In + kv.Value.Out + kv.Value.Cache);

            // "Active" = a log for this tool was written in the last 90 seconds
            bool active = t.LastActivity is DateTime la && (DateTime.Now - la).TotalSeconds < 90;
            if (!active)
            {
                active = _cursors.Where(c => c.Value.Tool == t.Key)
                                 .Any(c => (DateTime.Now - SafeLastWrite(c.Key)).TotalSeconds < 90);
            }

            list.Add(new ToolSnapshot
            {
                Key = t.Key,
                Installed = Directory.Exists(Path.GetDirectoryName(t.Root) ?? t.Root),
                TodayIn = d?.In ?? 0,
                TodayOut = d?.Out ?? 0,
                TodayCache = d?.Cache ?? 0,
                TodayTurns = d?.Turns ?? 0,
                TodaySessions = d?.Sessions.Count ?? 0,
                WeekTotal = week,
                LastActivity = t.LastActivity,
                Active = active,
                LastModel = t.LastModel,
                LastProject = t.LastProject,
                RateLimit = t.RateLimit
            });
        }
        return list;
    }

    private static DateTime ParseTs(JsonElement root, string prop)
    {
        if (TryStr(root, prop, out var s) && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
        return DateTime.Now;
    }

    private static bool TryStr(JsonElement el, string prop, out string value)
    {
        value = "";
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String)
        {
            value = p.GetString() ?? "";
            return true;
        }
        return false;
    }

    private static long GetLong(JsonElement el, string prop)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v))
            return v;
        return 0;
    }

    private static string Snippet(string text)
    {
        string t = text.Replace("\r", " ").Replace("\n", " ").Replace("**", "").Trim();
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return t.Length > 160 ? t[..157] + "…" : t;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}

