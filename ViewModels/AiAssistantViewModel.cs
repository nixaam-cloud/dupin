using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsNotch.Services;

namespace WindowsNotch.ViewModels;

public partial class AiAssistantViewModel : ObservableObject
{
    private readonly AiClientService _aiService;
    private CancellationTokenSource? _cts;
    private byte[]? _attachedImageBytes;
    private string _attachedMimeType = "image/png";

    [ObservableProperty]
    private string _prompt = string.Empty;

    [ObservableProperty]
    private string _responseText = "Ask Gemini, Claude, or OpenAI anything — or click ✂️ Snip to analyze any screen region!";

    [ObservableProperty]
    private bool _isStreaming = false;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _thinkingText = string.Empty;

    [ObservableProperty]
    private bool _hasThinkingText = false;

    [ObservableProperty]
    private ImageSource? _attachedImagePreview;

    [ObservableProperty]
    private bool _hasAttachedImage = false;

    [ObservableProperty]
    private string _attachedImageLabel = string.Empty;

    [ObservableProperty]
    private string _selectedProvider = "Gemini";

    [ObservableProperty]
    private string _geminiKey = string.Empty;

    [ObservableProperty]
    private string _claudeKey = string.Empty;

    [ObservableProperty]
    private string _openAiKey = string.Empty;

    [ObservableProperty]
    private bool _isSettingsVisible = false;

    public double ExpandedHeight
    {
        get => _aiService.ExpandedHeight;
        set => _aiService.ExpandedHeight = value;
    }

    public void SaveSettings() => _aiService.SaveSettings();

    public event Action<string>? ExportToShelfRequested;

    public AiAssistantViewModel(AiClientService aiService)
    {
        _aiService = aiService;
        GeminiKey = _aiService.GeminiApiKey;
        ClaudeKey = _aiService.ClaudeApiKey;
        OpenAiKey = _aiService.OpenAiApiKey;
        SelectedProvider = _aiService.SelectedProvider;
    }

    partial void OnSelectedProviderChanged(string value)
    {
        if (_aiService != null && !string.IsNullOrWhiteSpace(value))
        {
            _aiService.SelectedProvider = value;
            _aiService.SaveSettings();
        }
    }

    [RelayCommand]
    public void ClearAttachment()
    {
        _attachedImageBytes = null;
        AttachedImagePreview = null;
        HasAttachedImage = false;
        AttachedImageLabel = string.Empty;
    }

    public void AttachImage(byte[] imageBytes, string mimeType, string label, ImageSource? preview)
    {
        _attachedImageBytes = imageBytes;
        _attachedMimeType = mimeType;
        AttachedImageLabel = label;
        AttachedImagePreview = preview;
        HasAttachedImage = true;
    }

    public async Task AnalyzeFileFromShelfAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string fileName = Path.GetFileName(filePath);

            if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")
            {
                byte[] bytes = await File.ReadAllBytesAsync(filePath);
                string mime = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    _ => "image/png"
                };

                BitmapImage? bmp = null;
                try
                {
                    bmp = new BitmapImage();
                    using var ms = new MemoryStream(bytes);
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                }
                catch { }

                AttachImage(bytes, mime, fileName, bmp);
                if (string.IsNullOrWhiteSpace(Prompt))
                {
                    Prompt = $"Analyze this image ({fileName}): describe what is shown, extract any key text/code, and provide helpful insights.";
                }
                await SendPromptAsync();
            }
            else if (File.Exists(filePath))
            {
                ClearAttachment();
                var fi = new FileInfo(filePath);
                string snippet;
                using (var sr = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    char[] buf = new char[12000];
                    int read = await sr.ReadAsync(buf, 0, buf.Length);
                    snippet = new string(buf, 0, read);
                }

                Prompt = $"Analyze the file '{fileName}' ({fi.Length} bytes):\n\n```\n{snippet}\n```";
                await SendPromptAsync();
            }
        }
        catch (Exception ex)
        {
            ResponseText = $"Could not analyze file: {ex.Message}";
        }
    }

    [RelayCommand]
    public void CancelStream()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    public async Task SendPromptAsync()
    {
        if ((string.IsNullOrWhiteSpace(Prompt) && _attachedImageBytes == null) || IsStreaming) return;

        string currentPrompt = string.IsNullOrWhiteSpace(Prompt)
            ? "Analyze this screen snip: describe what you see, extract any text or errors, and explain or solve it concisely."
            : Prompt.Trim();

        byte[]? currentImageBytes = _attachedImageBytes;
        string currentMime = _attachedMimeType;

        Prompt = string.Empty;
        ResponseText = string.Empty;
        ThinkingText = string.Empty;
        HasThinkingText = false;
        IsSettingsVisible = false;
        IsStreaming = true;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string phase = currentImageBytes != null
            ? $"Vision Analysis ({SelectedProvider})"
            : $"Thinking with {SelectedProvider}";
        StatusText = $"{phase}... 0.0s";

        // Background ticker to update live elapsed time while thinking/streaming
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested && IsStreaming)
                {
                    await Task.Delay(100, token);
                    double sec = sw.Elapsed.TotalSeconds;
                    Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        if (IsStreaming)
                        {
                            StatusText = $"{phase}... {sec:0.0}s";
                        }
                    });
                }
            }
            catch { }
        }, token);

        try
        {
            var answerSb = new StringBuilder();
            var thoughtSb = new StringBuilder();

            await foreach (var chunk in _aiService.StreamResponseAsync(currentPrompt, SelectedProvider, token, currentImageBytes, currentMime))
            {
                if (chunk.StartsWith(AiClientService.ThoughtPrefix, StringComparison.Ordinal))
                {
                    string thoughtPart = chunk.Substring(AiClientService.ThoughtPrefix.Length);
                    thoughtSb.Append(thoughtPart);
                    ThinkingText = thoughtSb.ToString().Trim();
                    HasThinkingText = !string.IsNullOrWhiteSpace(ThinkingText);
                    phase = $"Reasoning ({SelectedProvider})";
                }
                else
                {
                    answerSb.Append(chunk);
                    ResponseText = answerSb.ToString();
                    phase = $"Streaming ({SelectedProvider})";
                }
            }

            if (string.IsNullOrWhiteSpace(ResponseText) && HasThinkingText)
            {
                ResponseText = ThinkingText;
            }
        }
        catch (OperationCanceledException)
        {
            if (string.IsNullOrWhiteSpace(ResponseText))
            {
                ResponseText = "⏹️ Request cancelled.";
            }
        }
        catch (Exception ex)
        {
            ResponseText = $"Error: {ex.Message}";
        }
        finally
        {
            sw.Stop();
            IsStreaming = false;
            StatusText = string.Empty;
        }
    }

    [RelayCommand]
    public void CopyResponse()
    {
        if (!string.IsNullOrWhiteSpace(ResponseText))
        {
            try
            {
                Clipboard.SetText(ResponseText);
            }
            catch { }
        }
    }

    [RelayCommand]
    public void SaveKeys()
    {
        _aiService.GeminiApiKey = GeminiKey?.Trim() ?? "";
        _aiService.ClaudeApiKey = ClaudeKey?.Trim() ?? "";
        _aiService.OpenAiApiKey = OpenAiKey?.Trim() ?? "";
        _aiService.SaveSettings();
        IsSettingsVisible = false;
    }

    [RelayCommand]
    public void ToggleSettings()
    {
        IsSettingsVisible = !IsSettingsVisible;
    }

    public bool HasActiveKey
    {
        get
        {
            if (SelectedProvider.Equals("Claude", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(_aiService.ClaudeApiKey);
            if (SelectedProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(_aiService.OpenAiApiKey);
            return !string.IsNullOrWhiteSpace(_aiService.GeminiApiKey);
        }
    }

    [RelayCommand]
    public void OpenInstalledApp()
    {
        try
        {
            string currentPrompt = Prompt?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(currentPrompt))
            {
                try
                {
                    Clipboard.SetText(currentPrompt);
                }
                catch { }
            }

            IsSettingsVisible = false;
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string packagesDir = Path.Combine(localAppData, "Packages");

            if (SelectedProvider.Equals("Claude", StringComparison.OrdinalIgnoreCase))
            {
                string claudePkgDir = Path.Combine(packagesDir, "Claude_pzs8sxrjxfjjc");
                string claudeExe1 = Path.Combine(localAppData, @"AnthropicClaude\claude.exe");
                string claudeExe2 = Path.Combine(localAppData, @"Programs\claude\Claude.exe");

                if (Directory.Exists(claudePkgDir))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = @"shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude",
                        UseShellExecute = true
                    });
                }
                else if (File.Exists(claudeExe1))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = claudeExe1,
                        UseShellExecute = true
                    });
                }
                else if (File.Exists(claudeExe2))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = claudeExe2,
                        UseShellExecute = true
                    });
                }
                else
                {
                    string url = !string.IsNullOrWhiteSpace(currentPrompt)
                        ? $"https://claude.ai/new?q={Uri.EscapeDataString(currentPrompt)}"
                        : "https://claude.ai/new";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
            }
            else if (SelectedProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                string? chatGptPkgName = null;
                if (Directory.Exists(packagesDir))
                {
                    var dirs = Directory.GetDirectories(packagesDir, "OpenAI.ChatGPT-Desktop*");
                    if (dirs.Length > 0)
                    {
                        chatGptPkgName = Path.GetFileName(dirs[0]);
                    }
                }

                string chatGptExe1 = Path.Combine(localAppData, @"Programs\ChatGPT\ChatGPT.exe");
                string chatGptExe2 = Path.Combine(localAppData, @"ChatGPT\ChatGPT.exe");

                if (!string.IsNullOrEmpty(chatGptPkgName))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $@"shell:AppsFolder\{chatGptPkgName}!ChatGPT",
                        UseShellExecute = true
                    });
                }
                else if (File.Exists(chatGptExe1))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = chatGptExe1,
                        UseShellExecute = true
                    });
                }
                else if (File.Exists(chatGptExe2))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = chatGptExe2,
                        UseShellExecute = true
                    });
                }
                else
                {
                    string url = !string.IsNullOrWhiteSpace(currentPrompt)
                        ? $"https://chatgpt.com/?q={Uri.EscapeDataString(currentPrompt)}"
                        : "https://chatgpt.com/";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
            }
            else
            {
                string geminiDir = Path.Combine(localAppData, @"Google\Gemini");
                string geminiRootExe = Path.Combine(geminiDir, "Gemini.exe");
                string geminiLnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs\Gemini.lnk");

                string? targetPath = null;
                if (File.Exists(geminiRootExe))
                {
                    targetPath = geminiRootExe;
                }
                else if (Directory.Exists(geminiDir))
                {
                    var appDirs = Directory.GetDirectories(geminiDir, "app-*");
                    if (appDirs.Length > 0)
                    {
                        Array.Sort(appDirs);
                        string candidate = Path.Combine(appDirs[^1], "Gemini.exe");
                        if (File.Exists(candidate))
                            targetPath = candidate;
                    }
                }

                if (targetPath != null)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = targetPath,
                        WorkingDirectory = Path.GetDirectoryName(targetPath) ?? geminiDir,
                        UseShellExecute = true
                    });
                }
                else if (File.Exists(geminiLnk))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = geminiLnk,
                        UseShellExecute = true
                    });
                }
                else
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "https://gemini.google.com/app",
                        UseShellExecute = true
                    });
                }
            }

            ResponseText = !string.IsNullOrWhiteSpace(currentPrompt)
                ? $"🚀 Opened {SelectedProvider} (prompt copied to clipboard — press Ctrl+V to paste)."
                : $"🚀 Opened {SelectedProvider}.";
        }
        catch (Exception ex)
        {
            ResponseText = $"Could not open {SelectedProvider}: {ex.Message}";
        }
    }

    [RelayCommand]
    public void OpenApiKeyGuide()
    {
        try
        {
            string url;
            if (SelectedProvider.Equals("Claude", StringComparison.OrdinalIgnoreCase))
                url = "https://console.anthropic.com/settings/keys";
            else if (SelectedProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
                url = "https://platform.openai.com/api-keys";
            else
                url = "https://aistudio.google.com/app/apikey";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    public void SendToShelf()
    {
        if (!string.IsNullOrWhiteSpace(ResponseText))
        {
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "Dupin");
                Directory.CreateDirectory(tempDir);
                string fileName = $"Dupin_AI_{DateTime.Now:yyyyMMdd_HHmmss}.md";
                string filePath = Path.Combine(tempDir, fileName);
                File.WriteAllText(filePath, ResponseText);

                ExportToShelfRequested?.Invoke(filePath);
            }
            catch { }
        }
    }
}
