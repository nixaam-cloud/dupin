using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsNotch.Services;

public class AiClientService
{
    private static readonly HttpClient _http = new();
    private readonly string _settingsPath;

    public string GeminiApiKey { get; set; } = string.Empty;
    public string ClaudeApiKey { get; set; } = string.Empty;
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string SelectedProvider { get; set; } = "Gemini";
    public double ExpandedHeight { get; set; } = 224;

    public AiClientService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dupinFolder = Path.Combine(appData, "Dupin");
        Directory.CreateDirectory(dupinFolder);
        _settingsPath = Path.Combine(dupinFolder, "settings.json");

        // Migrate from legacy WindowsNotch settings if needed
        string legacySettings = Path.Combine(appData, "WindowsNotch", "settings.json");
        if (!File.Exists(_settingsPath) && File.Exists(legacySettings))
        {
            try { File.Copy(legacySettings, _settingsPath, overwrite: false); } catch { }
        }

        LoadSettings();
    }

    public void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("GeminiApiKey", out var gKey))
                    GeminiApiKey = gKey.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("ClaudeApiKey", out var cKey))
                    ClaudeApiKey = cKey.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("OpenAiApiKey", out var oKey))
                    OpenAiApiKey = oKey.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("SelectedProvider", out var sProv) && !string.IsNullOrWhiteSpace(sProv.GetString()))
                    SelectedProvider = sProv.GetString()!;
                if (doc.RootElement.TryGetProperty("ExpandedHeight", out var expHeight) && expHeight.TryGetDouble(out double eh) && eh >= 200)
                    ExpandedHeight = eh;
            }
        }
        catch { }
    }

    public void SaveSettings()
    {
        try
        {
            var data = new
            {
                GeminiApiKey,
                ClaudeApiKey,
                OpenAiApiKey,
                SelectedProvider,
                ExpandedHeight
            };
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string prompt,
        string provider,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        byte[]? imageBytes = null,
        string imageMimeType = "image/png")
    {
        if (provider.Equals("Claude", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var chunk in StreamClaudeAsync(prompt, imageBytes, imageMimeType, ct))
            {
                yield return chunk;
            }
        }
        else if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var chunk in StreamOpenAiAsync(prompt, imageBytes, imageMimeType, ct))
            {
                yield return chunk;
            }
        }
        else
        {
            await foreach (var chunk in StreamGeminiAsync(prompt, imageBytes, imageMimeType, ct))
            {
                yield return chunk;
            }
        }
    }

    public const string ThoughtPrefix = "\x01THOUGHT:";

    private async IAsyncEnumerable<string> StreamGeminiAsync(
        string prompt,
        byte[]? imageBytes,
        string imageMimeType,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(GeminiApiKey))
        {
            yield return "⚠️ Please provide a Gemini API key in the Notch settings.";
            yield break;
        }

        string[] modelsToTry = new[]
        {
            "gemini-3-flash-preview",
            "gemini-3.1-flash-lite-preview",
            "gemini-3.8-flash"
        };

        object[] contentParts = (imageBytes != null && imageBytes.Length > 0)
            ? new object[]
            {
                new { text = prompt },
                new { inline_data = new { mime_type = imageMimeType, data = Convert.ToBase64String(imageBytes) } }
            }
            : new object[]
            {
                new { text = prompt }
            };

        HttpResponseMessage? response = null;
        string? lastError = null;

        foreach (string model in modelsToTry)
        {
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse&key={GeminiApiKey}";
            object payload = model.StartsWith("gemini-3-") || model.StartsWith("gemini-3.1")
                ? new
                {
                    contents = new[]
                    {
                        new { parts = contentParts }
                    },
                    generationConfig = new
                    {
                        thinkingConfig = new
                        {
                            includeThoughts = true,
                            thinkingLevel = "LOW"
                        }
                    }
                }
                : new
                {
                    contents = new[]
                    {
                        new { parts = contentParts }
                    },
                    generationConfig = new
                    {
                        thinkingConfig = new
                        {
                            includeThoughts = true
                        }
                    }
                };

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            try
            {
                var recv = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (recv.IsSuccessStatusCode)
                {
                    response = recv;
                    break;
                }

                string errBody = await recv.Content.ReadAsStringAsync(ct);
                lastError = $"Gemini Error ({(int)recv.StatusCode}): {errBody}";

                // Retry next model if 503 Unavailable, 429 Rate Limit, or 404 Not Found
                if ((int)recv.StatusCode != 503 && (int)recv.StatusCode != 429 && (int)recv.StatusCode != 404)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"Error connecting to Gemini: {ex.Message}";
            }
        }

        if (response == null)
        {
            yield return lastError ?? "Error connecting to Gemini.";
            yield break;
        }

        using (response)
        using (var stream = await response.Content.ReadAsStreamAsync(ct))
        using (var reader = new StreamReader(stream))
        {
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("data: "))
                {
                    string jsonChunk = line.Substring(6).Trim();
                    foreach (var chunk in ExtractGeminiChunks(jsonChunk))
                    {
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            yield return chunk;
                        }
                    }
                }
            }
        }
    }

    private IEnumerable<string> ExtractGeminiChunks(string json)
    {
        var results = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var textProp))
                        {
                            string? text = textProp.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                bool isThought = part.TryGetProperty("thought", out var thoughtProp) &&
                                                 thoughtProp.ValueKind == JsonValueKind.True;
                                results.Add(isThought ? ThoughtPrefix + text : text);
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return results;
    }

    private async IAsyncEnumerable<string> StreamClaudeAsync(
        string prompt,
        byte[]? imageBytes,
        string imageMimeType,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ClaudeApiKey))
        {
            yield return "⚠️ Please provide an Anthropic Claude API key in the Notch settings.";
            yield break;
        }

        string url = "https://api.anthropic.com/v1/messages";
        object userContent = (imageBytes != null && imageBytes.Length > 0)
            ? new object[]
            {
                new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = imageMimeType,
                        data = Convert.ToBase64String(imageBytes)
                    }
                },
                new { type = "text", text = prompt }
            }
            : prompt;

        var payload = new
        {
            model = "claude-3-5-sonnet-20241022",
            max_tokens = 1024,
            stream = true,
            messages = new[]
            {
                new { role = "user", content = userContent }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", ClaudeApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        HttpResponseMessage? response = null;
        string? connectionError = null;

        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            connectionError = $"Error connecting to Claude: {ex.Message}";
        }

        if (connectionError != null)
        {
            yield return connectionError;
            yield break;
        }

        if (response == null || !response.IsSuccessStatusCode)
        {
            string err = response != null ? await response.Content.ReadAsStringAsync(ct) : "Unknown error";
            yield return $"Claude Error: {err}";
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data: "))
            {
                string jsonChunk = line.Substring(6).Trim();
                if (jsonChunk == "[DONE]") break;

                string? text = ExtractClaudeChunk(jsonChunk);
                if (!string.IsNullOrEmpty(text))
                {
                    yield return text;
                }
            }
        }
    }

    private string? ExtractClaudeChunk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "content_block_delta")
            {
                if (doc.RootElement.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("text", out var textProp))
                {
                    return textProp.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    private async IAsyncEnumerable<string> StreamOpenAiAsync(
        string prompt,
        byte[]? imageBytes,
        string imageMimeType,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(OpenAiApiKey))
        {
            yield return "⚠️ Please provide an OpenAI API key in the Notch settings.";
            yield break;
        }

        string url = "https://api.openai.com/v1/chat/completions";
        object userContent = (imageBytes != null && imageBytes.Length > 0)
            ? new object[]
            {
                new { type = "text", text = prompt },
                new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = $"data:{imageMimeType};base64,{Convert.ToBase64String(imageBytes)}"
                    }
                }
            }
            : prompt;

        var payload = new
        {
            model = "gpt-4o-mini",
            stream = true,
            messages = new[]
            {
                new { role = "user", content = userContent }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", OpenAiApiKey);

        HttpResponseMessage? response = null;
        string? connectionError = null;

        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            connectionError = $"Error connecting to OpenAI: {ex.Message}";
        }

        if (connectionError != null)
        {
            yield return connectionError;
            yield break;
        }

        if (response == null || !response.IsSuccessStatusCode)
        {
            string err = response != null ? await response.Content.ReadAsStringAsync(ct) : "Unknown error";
            yield return $"OpenAI Error: {err}";
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data: "))
            {
                string jsonChunk = line.Substring(6).Trim();
                if (jsonChunk == "[DONE]") break;

                string? text = ExtractOpenAiChunk(jsonChunk);
                if (!string.IsNullOrEmpty(text))
                {
                    yield return text;
                }
            }
        }
    }

    private string? ExtractOpenAiChunk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var contentProp))
                {
                    return contentProp.GetString();
                }
            }
        }
        catch { }
        return null;
    }
}
