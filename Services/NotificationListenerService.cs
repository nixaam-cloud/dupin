using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Xml.Linq;

namespace WindowsNotch.Services;

public class AppNotificationItem
{
    public uint Id { get; set; }
    public string AppName { get; set; } = "Notification";
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
    public string TimeText => ReceivedAt.Date == DateTime.Today
        ? ReceivedAt.ToString("HH:mm")
        : ReceivedAt.ToString("MMM d, HH:mm");
    public string AppInitial => string.IsNullOrWhiteSpace(AppName) ? "N" : AppName.Substring(0, 1).ToUpperInvariant();
}

public class NotificationListenerService
{
    private const int SQLITE_OPEN_READONLY = 0x00000001;
    private const int SQLITE_ROW = 100;

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zVfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr pzTail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_close(IntPtr db);

    private readonly string _wpnDbPath;
    private readonly string _historyPath;
    private readonly DispatcherTimer _pollTimer;
    private long _lastSeenOrder = 0;
    private long _lastClearedOrder = 0;
    private readonly HashSet<uint> _dismissedIds = new();

    public bool IsListening { get; private set; } = false;

    public event Action<AppNotificationItem>? NotificationReceived;
    public ObservableCollection<AppNotificationItem> RecentNotifications { get; } = new();

    public NotificationListenerService()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _wpnDbPath = Path.Combine(localAppData, @"Microsoft\Windows\Notifications\wpndatabase.db");

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dupinDir = Path.Combine(appData, "Dupin");
        Directory.CreateDirectory(dupinDir);
        _historyPath = Path.Combine(dupinDir, "notifications.json");

        LoadHistory();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _pollTimer.Tick += (s, e) => PollWindowsNotifications(isInitialSync: false);
    }

    public Task InitializeAsync()
    {
        try
        {
            PollWindowsNotifications(isInitialSync: true);
            _pollTimer.Start();
            IsListening = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"NotificationListener init error: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_historyPath))
            {
                string json = File.ReadAllText(_historyPath);
                var state = JsonSerializer.Deserialize<NotificationHistoryDto>(json);
                if (state != null)
                {
                    _lastSeenOrder = state.LastSeenOrder;
                    _lastClearedOrder = state.LastClearedOrder;
                    if (state.DismissedIds != null)
                    {
                        foreach (var id in state.DismissedIds)
                            _dismissedIds.Add(id);
                    }
                    if (state.Items != null)
                    {
                        foreach (var item in state.Items)
                        {
                            RecentNotifications.Add(item);
                        }
                    }
                }
            }
        }
        catch { }
    }

    private void SaveHistory()
    {
        try
        {
            var dto = new NotificationHistoryDto
            {
                LastSeenOrder = _lastSeenOrder,
                LastClearedOrder = _lastClearedOrder,
                DismissedIds = _dismissedIds.ToList(),
                Items = RecentNotifications.ToList()
            };
            string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_historyPath, json);
        }
        catch { }
    }

    private void PollWindowsNotifications(bool isInitialSync)
    {
        if (!File.Exists(_wpnDbPath)) return;

        IntPtr db = IntPtr.Zero;
        IntPtr stmt = IntPtr.Zero;
        try
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(_wpnDbPath + "\0");
            if (sqlite3_open_v2(pathBytes, out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0)
                return;

            long minOrder = isInitialSync ? _lastClearedOrder : Math.Max(_lastClearedOrder, _lastSeenOrder);
            string sql = $"SELECT n.[Order], h.PrimaryId, CAST(n.Payload AS TEXT), n.ArrivalTime FROM Notification n LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId WHERE n.Type = 'toast' AND n.[Order] > {minOrder} ORDER BY n.[Order] ASC LIMIT 100;\0";

            if (sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql), -1, out stmt, IntPtr.Zero) != 0)
                return;

            var existingIds = new HashSet<uint>(RecentNotifications.Select(x => x.Id));
            var newlyDiscovered = new List<AppNotificationItem>();

            while (sqlite3_step(stmt) == SQLITE_ROW)
            {
                long order = sqlite3_column_int64(stmt, 0);
                string? rawAppId = PtrToUtf8(sqlite3_column_text(stmt, 1));
                string? payloadXml = PtrToUtf8(sqlite3_column_text(stmt, 2));
                long arrivalFileTime = sqlite3_column_int64(stmt, 3);

                if (order > _lastSeenOrder)
                {
                    _lastSeenOrder = order;
                }

                uint id = (uint)order;
                if (_dismissedIds.Contains(id) || existingIds.Contains(id))
                    continue;

                if (string.IsNullOrWhiteSpace(payloadXml))
                    continue;

                var parsed = ParseToastXml(id, rawAppId, payloadXml, arrivalFileTime);
                if (parsed != null)
                {
                    newlyDiscovered.Add(parsed);
                    existingIds.Add(id);
                }
            }

            if (newlyDiscovered.Count > 0)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    foreach (var item in newlyDiscovered)
                    {
                        RecentNotifications.Insert(0, item);
                    }

                    while (RecentNotifications.Count > 200)
                    {
                        RecentNotifications.RemoveAt(RecentNotifications.Count - 1);
                    }

                    SaveHistory();

                    if (!isInitialSync)
                    {
                        NotificationReceived?.Invoke(newlyDiscovered[^1]);
                    }
                });
            }
        }
        catch { }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    private static string? PtrToUtf8(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        return Marshal.PtrToStringUTF8(ptr);
    }

    private static AppNotificationItem? ParseToastXml(uint id, string? rawAppId, string xml, long arrivalFileTime)
    {
        try
        {
            string appName = FormatFriendlyAppName(rawAppId);
            string title = string.Empty;
            string body = string.Empty;

            var doc = XDocument.Parse(xml);
            var texts = doc.Descendants("text")
                           .Select(t => (t.Value ?? "").Trim())
                           .Where(t => !string.IsNullOrWhiteSpace(t))
                           .ToList();

            if (texts.Count == 0)
                return null;

            if (texts.Count == 1)
            {
                title = appName;
                body = texts[0];
            }
            else
            {
                title = texts[0];
                body = string.Join(" · ", texts.Skip(1));
            }

            DateTime receivedAt = DateTime.Now;
            if (arrivalFileTime > 0)
            {
                try
                {
                    receivedAt = DateTime.FromFileTime(arrivalFileTime);
                }
                catch { }
            }

            return new AppNotificationItem
            {
                Id = id,
                AppName = appName,
                Title = title,
                Message = body,
                ReceivedAt = receivedAt
            };
        }
        catch
        {
            return null;
        }
    }

    private static string FormatFriendlyAppName(string? rawAppId)
    {
        if (string.IsNullOrWhiteSpace(rawAppId))
            return "System";

        string lower = rawAppId.ToLowerInvariant();
        if (lower.Contains("beeper")) return "Beeper";
        if (lower.Contains("outlook")) return "Outlook";
        if (lower.Contains("slack")) return "Slack";
        if (lower.Contains("whatsapp")) return "WhatsApp";
        if (lower.Contains("teams")) return "Teams";
        if (lower.Contains("discord")) return "Discord";
        if (lower.Contains("telegram")) return "Telegram";
        if (lower.Contains("screensketch") || lower.Contains("snipping")) return "Snipping Tool";
        if (lower.Contains("visualstudiocode") || lower.Contains("vscode")) return "VS Code";
        if (lower.Contains("unigetui")) return "UniGetUI";
        if (lower.Contains("chrome")) return "Chrome";
        if (lower.Contains("brave")) return "Brave";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("msedge") || lower.Contains("edge")) return "Edge";
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("claude")) return "Claude";
        if (lower.Contains("gemini")) return "Gemini";

        // Strip MSIX publisher suffix and reverse-DNS prefixes
        string cleaned = rawAppId;
        int bangIdx = cleaned.IndexOf('!');
        if (bangIdx > 0)
            cleaned = cleaned.Substring(0, bangIdx);

        int underscoreIdx = cleaned.IndexOf('_');
        if (underscoreIdx > 0)
            cleaned = cleaned.Substring(0, underscoreIdx);

        string[] parts = cleaned.Split('.', '\\', '/');
        string last = parts.LastOrDefault(p => !string.IsNullOrWhiteSpace(p) && !p.Equals("desktop", StringComparison.OrdinalIgnoreCase) && !p.Equals("app", StringComparison.OrdinalIgnoreCase)) ?? cleaned;

        if (last.Length > 1)
            return char.ToUpperInvariant(last[0]) + last.Substring(1);

        return last;
    }

    public void PushNotification(string appName, string title, string message)
    {
        var item = new AppNotificationItem
        {
            Id = (uint)Random.Shared.Next(1000000, 9999999),
            AppName = appName,
            Title = title,
            Message = message,
            ReceivedAt = DateTime.Now
        };

        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            RecentNotifications.Insert(0, item);
            while (RecentNotifications.Count > 200)
            {
                RecentNotifications.RemoveAt(RecentNotifications.Count - 1);
            }
            SaveHistory();
            NotificationReceived?.Invoke(item);
        });
    }

    public void ClearAll()
    {
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            _lastClearedOrder = _lastSeenOrder;
            _dismissedIds.Clear();
            RecentNotifications.Clear();
            SaveHistory();
        });
    }

    public void Remove(AppNotificationItem item)
    {
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            if (item != null)
            {
                _dismissedIds.Add(item.Id);
                RecentNotifications.Remove(item);
                SaveHistory();
            }
        });
    }

    private sealed class NotificationHistoryDto
    {
        public long LastSeenOrder { get; set; }
        public long LastClearedOrder { get; set; }
        public List<uint>? DismissedIds { get; set; }
        public List<AppNotificationItem>? Items { get; set; }
    }
}
