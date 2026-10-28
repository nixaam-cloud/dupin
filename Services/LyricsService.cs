using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WindowsNotch.Models;

namespace WindowsNotch.Services;

public class LyricsService
{
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly ConcurrentDictionary<string, List<SyncedLyric>> _cache = new();
    private static readonly Regex LrcRegex = new(@"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\](.*)", RegexOptions.Compiled);

    static LyricsService()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "WindowsNotchCompanion/1.0 (https://github.com/notch)");
    }

    public async Task<List<SyncedLyric>> FetchLyricsAsync(string trackName, string artistName, int durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(trackName))
            return new List<SyncedLyric>();

        string cacheKey = $"{trackName.Trim().ToLowerInvariant()}|{artistName.Trim().ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out var cachedLyrics))
        {
            return cachedLyrics;
        }

        try
        {
            // First attempt: Exact match via /api/get
            string url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(trackName)}&artist_name={Uri.EscapeDataString(artistName)}";
            if (durationSeconds > 0)
            {
                url += $"&duration={durationSeconds}";
            }

            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                var parsed = ParseLrcResponse(json);
                if (parsed.Count > 0)
                {
                    _cache[cacheKey] = parsed;
                    return parsed;
                }
            }

            // Fallback attempt: Search via /api/search
            string searchUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString($"{artistName} {trackName}")}";
            var searchResponse = await _httpClient.GetAsync(searchUrl);
            if (searchResponse.IsSuccessStatusCode)
            {
                string searchJson = await searchResponse.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(searchJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("syncedLyrics", out var syncedProp) && syncedProp.ValueKind == JsonValueKind.String)
                        {
                            string? rawLrc = syncedProp.GetString();
                            if (!string.IsNullOrWhiteSpace(rawLrc))
                            {
                                var parsed = ParseLrcString(rawLrc);
                                if (parsed.Count > 0)
                                {
                                    _cache[cacheKey] = parsed;
                                    return parsed;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Graceful fallback on network or API failures
        }

        return new List<SyncedLyric>();
    }

    private List<SyncedLyric> ParseLrcResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("syncedLyrics", out var syncedProp) && syncedProp.ValueKind == JsonValueKind.String)
            {
                string? raw = syncedProp.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return ParseLrcString(raw);
                }
            }

            // Fallback to plainLyrics if synced not available
            if (doc.RootElement.TryGetProperty("plainLyrics", out var plainProp) && plainProp.ValueKind == JsonValueKind.String)
            {
                string? plain = plainProp.GetString();
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    var result = new List<SyncedLyric>();
                    string[] lines = plain.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        result.Add(new SyncedLyric(TimeSpan.FromSeconds(i * 4), lines[i].Trim()));
                    }
                    return result;
                }
            }
        }
        catch
        {
        }

        return new List<SyncedLyric>();
    }

    public static List<SyncedLyric> ParseLrcString(string lrc)
    {
        var lyrics = new List<SyncedLyric>();
        string[] lines = lrc.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
        {
            var match = LrcRegex.Match(line.Trim());
            if (match.Success)
            {
                int min = int.Parse(match.Groups[1].Value);
                int sec = int.Parse(match.Groups[2].Value);
                int ms = 0;

                if (match.Groups[3].Success)
                {
                    string msStr = match.Groups[3].Value;
                    if (msStr.Length == 2)
                        ms = int.Parse(msStr) * 10;
                    else if (msStr.Length == 3)
                        ms = int.Parse(msStr);
                }

                var timestamp = new TimeSpan(0, 0, min, sec, ms);
                string text = match.Groups[4].Value.Trim();

                if (!string.IsNullOrEmpty(text))
                {
                    lyrics.Add(new SyncedLyric(timestamp, text));
                }
            }
        }

        lyrics.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return lyrics;
    }
}
