using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using WindowsNotch.Services;

namespace WindowsNotch.ViewModels;

public partial class TrayAppItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string IconGuid { get; set; } = string.Empty;
    public uint? Uid { get; set; }
    public ImageSource? IconSource { get; set; }
    public bool IsTrayApp { get; set; }
    public bool IsOpenWindow { get; set; }
    public IntPtr MainWindowHandle { get; set; }

    public string BadgeText => IsOpenWindow && IsTrayApp ? "Tray · Open" : (IsTrayApp ? "System Tray" : "Open App");
    public string FullTooltip => string.IsNullOrWhiteSpace(StatusText) || string.Equals(StatusText, Name, StringComparison.OrdinalIgnoreCase)
        ? $"{Name} ({BadgeText})\nLeft-click: Open / Activate · Right-click: Actions"
        : $"{Name} — {StatusText}\nLeft-click: Open / Activate · Right-click: Actions";
}

public partial class ToolsViewModel : ObservableObject
{
    private readonly HardwareHudService _hudService;
    private readonly DispatcherTimer _syncTimer;
    private bool _isUpdatingSliders;
    private int _tickCounter = 0;
    private List<TrayAppItem> _allDiscoveredApps = new();

    [ObservableProperty]
    private bool _isWifiEnabled = true;

    [ObservableProperty]
    private string _wifiSubtitle = "Wi-Fi";

    [ObservableProperty]
    private bool _isBluetoothEnabled = true;

    [ObservableProperty]
    private string _bluetoothSubtitle = "Not connected";

    [ObservableProperty]
    private bool _isAirplaneMode = false;

    [ObservableProperty]
    private string _airplaneSubtitle = "Airplane mode";

    [ObservableProperty]
    private bool _isEnergySaver = false;

    [ObservableProperty]
    private string _energySaverSubtitle = "Energy saver";

    [ObservableProperty]
    private bool _isNightLight = false;

    [ObservableProperty]
    private string _nightLightSubtitle = "Night light";

    [ObservableProperty]
    private int _brightness = 75;

    [ObservableProperty]
    private int _volume = 50;

    [ObservableProperty]
    private bool _isMuted = false;

    [ObservableProperty]
    private bool _isWindowsTaskbarHidden = false;

    [ObservableProperty]
    private string _keyboardLayoutCode = "ENG";

    [ObservableProperty]
    private string _keyboardLayoutSub = "INTL";

    [ObservableProperty]
    private string _fullClockText = DateTime.Now.ToString("| ddd, MMM dd yyyy | hh:mm:ss tt |", CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _isTrayAppsPopupOpen = false;

    [ObservableProperty]
    private string _trayAppsFilter = "All";

    [ObservableProperty]
    private int _trayAppsCount = 0;

    public ObservableCollection<TrayAppItem> TrayApps { get; } = new();
    public ObservableCollection<TrayAppItem> PreviewTrayApps { get; } = new();

    public event Action<string, string>? FeedbackRequested;

    public ToolsViewModel(HardwareHudService hudService)
    {
        _hudService = hudService;

        // Read initial volume & brightness
        SyncHardwareLevels();
        _ = RefreshRadiosAsync();
        UpdateKeyboardAndClock();
        EnsureFullTaskbarRestored();
        RefreshTrayApps();

        _syncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _syncTimer.Tick += (s, e) =>
        {
            UpdateKeyboardAndClock();
            SyncHardwareLevels();
            if (IsWindowsTaskbarHidden)
            {
                UpdateTrayCoverPosition();
            }

            _tickCounter++;
            if (_tickCounter % 6 == 0)
            {
                RefreshTrayApps();
            }
        };
        _syncTimer.Start();
    }

    [RelayCommand]
    public void ToggleTrayAppsPopup()
    {
        IsTrayAppsPopupOpen = !IsTrayAppsPopupOpen;
        if (IsTrayAppsPopupOpen)
        {
            RefreshTrayApps();
        }
    }

    [RelayCommand]
    public void SetTrayFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return;
        TrayAppsFilter = filter;
        ApplyTrayAppsFilter();
    }

    [RelayCommand]
    public void RefreshTrayApps()
    {
        try
        {
            var discovered = DiscoverTrayAndOpenApps();
            _allDiscoveredApps = discovered;
            TrayAppsCount = discovered.Count;
            ApplyTrayAppsFilter();
        }
        catch { }
    }

    private void ApplyTrayAppsFilter()
    {
        IEnumerable<TrayAppItem> filtered = _allDiscoveredApps;
        if (string.Equals(TrayAppsFilter, "Tray", StringComparison.OrdinalIgnoreCase))
        {
            filtered = _allDiscoveredApps.Where(a => a.IsTrayApp);
        }
        else if (string.Equals(TrayAppsFilter, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            filtered = _allDiscoveredApps.Where(a => a.IsOpenWindow);
        }

        var list = filtered.ToList();

        // Update TrayApps only if changed or count differs to avoid UI flicker
        bool changed = list.Count != TrayApps.Count;
        if (!changed)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (TrayApps[i].Id != list[i].Id || TrayApps[i].StatusText != list[i].StatusText || TrayApps[i].IsOpenWindow != list[i].IsOpenWindow)
                {
                    changed = true;
                    break;
                }
            }
        }

        if (changed)
        {
            TrayApps.Clear();
            foreach (var item in list)
            {
                TrayApps.Add(item);
            }
        }

        PreviewTrayApps.Clear();
        foreach (var item in _allDiscoveredApps.Where(a => a.IsTrayApp).Take(3))
        {
            PreviewTrayApps.Add(item);
        }
    }

    private static List<TrayAppItem> DiscoverTrayAndOpenApps()
    {
        var results = new List<TrayAppItem>();
        var processes = Process.GetProcesses();
        var procByName = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in processes)
        {
            try
            {
                if (p.Id <= 4) continue;
                string pName = p.ProcessName;
                if (!procByName.TryGetValue(pName, out var list))
                {
                    list = new List<Process>();
                    procByName[pName] = list;
                }
                list.Add(p);
            }
            catch { }
        }

        // Map visible top-level windows by PID
        var openWindowsByPid = new Dictionary<int, (IntPtr Hwnd, string Title)>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            int exStyle = Core.Native.NativeMethods.GetWindowLong(hWnd, Core.Native.NativeMethods.GWL_EXSTYLE);
            if ((exStyle & Core.Native.NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            string title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title) || title == "Program Manager" || title == "Windows Input Experience" || title == "Settings")
                return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid > 4 && !openWindowsByPid.ContainsKey((int)pid))
            {
                openWindowsByPid[(int)pid] = (hWnd, title);
            }
            return true;
        }, IntPtr.Zero);

        // 1. Scan HKCU\Control Panel\NotifyIconSettings for active tray icons
        var trayCandidatesByKey = new Dictionary<string, TrayAppItem>(StringComparer.OrdinalIgnoreCase);

        using (var rootKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
        {
            if (rootKey != null)
            {
                foreach (string subName in rootKey.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = rootKey.OpenSubKey(subName);
                        if (subKey == null) continue;

                        string rawExe = subKey.GetValue("ExecutablePath") as string ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(rawExe)) continue;

                        string resolvedExe = ResolveKnownFolderExecutablePath(rawExe);
                        string leaf = Path.GetFileNameWithoutExtension(resolvedExe);
                        if (string.IsNullOrWhiteSpace(leaf)) continue;

                        // Check if this process is currently running
                        if (!procByName.TryGetValue(leaf, out var runningProcs) || runningProcs.Count == 0)
                            continue;

                        string iconGuid = subKey.GetValue("IconGuid") as string ?? string.Empty;
                        string initialTip = CleanTooltip(subKey.GetValue("InitialTooltip") as string ?? string.Empty);
                        byte[]? iconSnapshot = subKey.GetValue("IconSnapshot") as byte[];
                        object? uidVal = subKey.GetValue("UID");
                        uint? uid = uidVal != null ? Convert.ToUInt32(uidVal) : null;

                        // Filter out noisy explorer.exe entries unless they are known tray utilities
                        if (string.Equals(leaf, "explorer", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.Equals(iconGuid, "{7820AE78-23E3-4229-82C1-E41CB67D5B9C}", StringComparison.OrdinalIgnoreCase))
                            {
                                initialTip = "Safely Remove Hardware and Eject Media";
                            }
                            else if (string.Equals(iconGuid, "{16D5471A-2359-44B0-9585-E95E189DBB3F}", StringComparison.OrdinalIgnoreCase))
                            {
                                initialTip = "Bluetooth Devices";
                            }
                            else
                            {
                                continue;
                            }
                        }

                        // Skip our own old debug/release duplicates except one Dupin entry
                        if (string.Equals(leaf, "Dupin", StringComparison.OrdinalIgnoreCase))
                        {
                            resolvedExe = Environment.ProcessPath ?? resolvedExe;
                            initialTip = "Dupin — Dynamic Island";
                        }

                        // Find best matching process and check if it has an open window
                        Process primaryProc = runningProcs[0];
                        IntPtr mainHwnd = IntPtr.Zero;
                        string windowTitle = string.Empty;

                        foreach (var rp in runningProcs)
                        {
                            if (openWindowsByPid.TryGetValue(rp.Id, out var winInfo))
                            {
                                primaryProc = rp;
                                mainHwnd = winInfo.Hwnd;
                                windowTitle = winInfo.Title;
                                break;
                            }
                            try
                            {
                                if (rp.MainWindowHandle != IntPtr.Zero && mainHwnd == IntPtr.Zero)
                                {
                                    mainHwnd = rp.MainWindowHandle;
                                    windowTitle = rp.MainWindowTitle;
                                }
                            }
                            catch { }
                        }

                        string friendlyName = GetFriendlyAppName(leaf, resolvedExe, initialTip, iconGuid);
                        string status = !string.IsNullOrWhiteSpace(initialTip) && !string.Equals(initialTip, friendlyName, StringComparison.OrdinalIgnoreCase)
                            ? initialTip
                            : (!string.IsNullOrWhiteSpace(windowTitle) ? windowTitle : "Running in System Tray");

                        ImageSource? iconSrc = DecodePngIconSnapshot(iconSnapshot) ?? ExtractExeIcon(resolvedExe);
                        if (iconSrc == null) continue;

                        string dedupKey = string.Equals(leaf, "explorer", StringComparison.OrdinalIgnoreCase)
                            ? $"explorer:{iconGuid}"
                            : leaf.ToLowerInvariant();

                        var item = new TrayAppItem
                        {
                            Id = dedupKey,
                            Name = friendlyName,
                            StatusText = status,
                            ExecutablePath = resolvedExe,
                            ProcessName = leaf,
                            ProcessId = primaryProc.Id,
                            IconGuid = iconGuid,
                            Uid = uid,
                            IconSource = iconSrc,
                            IsTrayApp = true,
                            IsOpenWindow = mainHwnd != IntPtr.Zero,
                            MainWindowHandle = mainHwnd
                        };

                        // Prefer entries that have a non-empty tooltip or newer executable path
                        if (!trayCandidatesByKey.TryGetValue(dedupKey, out var existing) ||
                            (string.IsNullOrWhiteSpace(existing.StatusText) && !string.IsNullOrWhiteSpace(item.StatusText)) ||
                            (File.Exists(item.ExecutablePath) && !File.Exists(existing.ExecutablePath)))
                        {
                            trayCandidatesByKey[dedupKey] = item;
                        }
                    }
                    catch { }
                }
            }
        }

        results.AddRange(trayCandidatesByKey.Values.OrderBy(a => a.Name));

        // 2. Also include open desktop applications that have visible windows (if not already in tray list)
        var seenProcs = new HashSet<string>(trayCandidatesByKey.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in openWindowsByPid)
        {
            try
            {
                int pid = kvp.Key;
                var win = kvp.Value;
                var proc = processes.FirstOrDefault(p => p.Id == pid);
                if (proc == null) continue;

                string pName = proc.ProcessName;
                if (seenProcs.Contains(pName) ||
                    string.Equals(pName, "TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "SearchHost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "SystemSettings", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "Dupin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string exePath = string.Empty;
                try
                {
                    exePath = proc.MainModule?.FileName ?? string.Empty;
                }
                catch { }

                ImageSource? iconSrc = !string.IsNullOrWhiteSpace(exePath) ? ExtractExeIcon(exePath) : null;
                if (iconSrc == null)
                {
                    iconSrc = ExtractWindowIcon(win.Hwnd);
                }
                if (iconSrc == null) continue;

                seenProcs.Add(pName);
                string friendly = GetFriendlyAppName(pName, exePath, win.Title, string.Empty);

                results.Add(new TrayAppItem
                {
                    Id = $"win:{pName.ToLowerInvariant()}",
                    Name = friendly,
                    StatusText = win.Title,
                    ExecutablePath = exePath,
                    ProcessName = pName,
                    ProcessId = pid,
                    IconSource = iconSrc,
                    IsTrayApp = false,
                    IsOpenWindow = true,
                    MainWindowHandle = win.Hwnd
                });
            }
            catch { }
        }

        return results;
    }

    private static string ResolveKnownFolderExecutablePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return string.Empty;

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.GetFolderPath(Environment.SpecialFolder.System),
            ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            ["{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        foreach (var kv in replacements)
        {
            if (rawPath.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value + rawPath.Substring(kv.Key.Length);
            }
        }

        return rawPath;
    }

    private static string CleanTooltip(string tip)
    {
        if (string.IsNullOrWhiteSpace(tip)) return string.Empty;
        string firstLine = tip.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        // Filter out corrupted/unprintable high-surrogate strings
        foreach (char c in firstLine)
        {
            if (char.IsControl(c) || c > 0x2FFF)
                return string.Empty;
        }
        return firstLine;
    }

    private static string GetFriendlyAppName(string leaf, string exePath, string tooltip, string iconGuid)
    {
        if (string.Equals(leaf, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(iconGuid, "{7820AE78-23E3-4229-82C1-E41CB67D5B9C}", StringComparison.OrdinalIgnoreCase))
                return "Safely Remove USB";
            if (string.Equals(iconGuid, "{16D5471A-2359-44B0-9585-E95E189DBB3F}", StringComparison.OrdinalIgnoreCase))
                return "Bluetooth Tray";
            return "File Explorer";
        }

        return leaf.ToLowerInvariant() switch
        {
            "claude" => "Claude",
            "geminiapplauncher" => "Gemini",
            "unigetui" => "UniGetUI",
            "beeper" => "Beeper",
            "powertoys.awake" => "PowerToys Awake",
            "powertoys" => "PowerToys",
            "nearby_share" => "Quick Share",
            "windhawk" => "Windhawk",
            "vpnui" => "Cisco AnyConnect",
            "ms-teams" => "Microsoft Teams",
            "onedrive" => "OneDrive",
            "outlook" => "Outlook",
            "olk" => "Outlook (New)",
            "onenotem" => "OneNote Quick",
            "nvdisplay.container" => "NVIDIA Settings",
            "securityhealthsystray" => "Windows Security",
            "dupin" => "Dupin",
            "code" => "VS Code",
            "antigravity" => "Antigravity",
            "cursor" => "Cursor",
            "firefox" => "Firefox",
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "docker desktop" => "Docker Desktop",
            "podman desktop" => "Podman Desktop",
            _ => FormatFallbackName(leaf, exePath)
        };
    }

    private static string FormatFallbackName(string leaf, string exePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(info.ProductName) && info.ProductName.Length <= 28)
                    return info.ProductName.Trim();
                if (!string.IsNullOrWhiteSpace(info.FileDescription) && info.FileDescription.Length <= 28)
                    return info.FileDescription.Trim();
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(leaf)) return "App";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(leaf.Replace('_', ' ').Replace('.', ' '));
    }

    private static ImageSource? DecodePngIconSnapshot(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 16) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? ExtractExeIcon(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;
        try
        {
            IntPtr[] largeIcons = new IntPtr[1];
            IntPtr[] smallIcons = new IntPtr[1];
            uint count = ExtractIconEx(exePath, 0, largeIcons, smallIcons, 1);
            IntPtr hIcon = largeIcons[0] != IntPtr.Zero ? largeIcons[0] : smallIcons[0];
            if (hIcon == IntPtr.Zero) return null;

            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                src.Freeze();
                return src;
            }
            finally
            {
                if (largeIcons[0] != IntPtr.Zero) Core.Native.NativeMethods.DestroyIcon(largeIcons[0]);
                if (smallIcons[0] != IntPtr.Zero && smallIcons[0] != largeIcons[0]) Core.Native.NativeMethods.DestroyIcon(smallIcons[0]);
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? ExtractWindowIcon(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            IntPtr hIcon = SendMessage(hwnd, 0x007F /* WM_GETICON */, new IntPtr(1 /* ICON_BIG */), IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
            {
                hIcon = SendMessage(hwnd, 0x007F /* WM_GETICON */, IntPtr.Zero, IntPtr.Zero);
            }
            if (hIcon == IntPtr.Zero) return null;

            var src = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    public void OpenTrayApp(TrayAppItem? app)
    {
        if (app == null) return;

        try
        {
            // 1. Special Windows explorer tray utilities
            if (string.Equals(app.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(app.IconGuid, "{7820AE78-23E3-4229-82C1-E41CB67D5B9C}", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,Control_RunDLL hotplug.dll") { UseShellExecute = true });
                    FeedbackRequested?.Invoke(app.Name, "Opened Safely Remove Hardware dialog");
                    return;
                }
                if (string.Equals(app.IconGuid, "{16D5471A-2359-44B0-9585-E95E189DBB3F}", StringComparison.OrdinalIgnoreCase))
                {
                    OpenSettingsUri("ms-settings:bluetooth");
                    FeedbackRequested?.Invoke(app.Name, "Opened Bluetooth Settings");
                    return;
                }
            }

            if (string.Equals(app.ProcessName, "Dupin", StringComparison.OrdinalIgnoreCase))
            {
                FeedbackRequested?.Invoke("Dupin", "Dupin is active right here!");
                return;
            }

            if (string.Equals(app.ProcessName, "onedrive", StringComparison.OrdinalIgnoreCase))
            {
                string oneDriveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive");
                if (Directory.Exists(oneDriveDir))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{oneDriveDir}\"") { UseShellExecute = true });
                    FeedbackRequested?.Invoke("OneDrive", "Opened OneDrive folder");
                    return;
                }
            }

            // 2. Try restoring an existing top-level window belonging to this process name
            if (TryRestoreOrActivateProcessWindow(app.ProcessName, app.MainWindowHandle))
            {
                FeedbackRequested?.Invoke(app.Name, "Brought window to front");
                return;
            }

            // 3. Try sending tray left-click callback to the process's tray owner window
            if (TrySendTrayMouseClick(app, rightClick: false))
            {
                FeedbackRequested?.Invoke(app.Name, "Triggered tray app action");
                return;
            }

            // 4. Launch single-instance executable (signals existing tray instance to open its main window)
            if (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath))
            {
                string args = string.Equals(app.ProcessName, "powertoys", StringComparison.OrdinalIgnoreCase) ? "--show-settings" : string.Empty;
                Process.Start(new ProcessStartInfo(app.ExecutablePath, args) { UseShellExecute = true });
                FeedbackRequested?.Invoke(app.Name, "Opened application window");
            }
        }
        catch { }
    }

    [RelayCommand]
    public void ShowTrayAppMenu(TrayAppItem? app)
    {
        if (app == null) return;
        var menu = CreateAppActionsContextMenu(app);
        menu.IsOpen = true;
    }

    public ContextMenu CreateAppActionsContextMenu(TrayAppItem app)
    {
        var menu = new ContextMenu();

        var headerItem = new MenuItem
        {
            Header = $"{app.Name} ({app.BadgeText})",
            FontWeight = FontWeights.Bold,
            IsEnabled = false
        };
        menu.Items.Add(headerItem);

        if (!string.IsNullOrWhiteSpace(app.StatusText) && !string.Equals(app.StatusText, app.Name, StringComparison.OrdinalIgnoreCase))
        {
            menu.Items.Add(new MenuItem
            {
                Header = $"Status: {app.StatusText}",
                IsEnabled = false
            });
        }

        menu.Items.Add(new Separator());

        // Primary Action: Open / Focus Window
        var openItem = new MenuItem
        {
            Header = "Open / Bring to Front",
            FontWeight = FontWeights.SemiBold
        };
        openItem.Click += (s, e) => OpenTrayApp(app);
        menu.Items.Add(openItem);

        // App-Specific Actions
        string pName = app.ProcessName.ToLowerInvariant();
        if (pName == "powertoys.awake")
        {
            menu.Items.Add(new Separator());
            var awakeIndef = new MenuItem { Header = "⚡ Keep Screen On Indefinitely" };
            awakeIndef.Click += (s, e) => RunProcessSilent(app.ExecutablePath, "--keep-awake-on --display-on true", "PowerToys Awake: Indefinite");
            menu.Items.Add(awakeIndef);

            var awake1h = new MenuItem { Header = "⏱ Keep Awake for 1 Hour" };
            awake1h.Click += (s, e) => RunProcessSilent(app.ExecutablePath, "--time-limit 3600 --display-on true", "PowerToys Awake: 1 Hour");
            menu.Items.Add(awake1h);

            var awake2h = new MenuItem { Header = "⏱ Keep Awake for 2 Hours" };
            awake2h.Click += (s, e) => RunProcessSilent(app.ExecutablePath, "--time-limit 7200 --display-on true", "PowerToys Awake: 2 Hours");
            menu.Items.Add(awake2h);
        }
        else if (pName == "explorer" && string.Equals(app.IconGuid, "{7820AE78-23E3-4229-82C1-E41CB67D5B9C}", StringComparison.OrdinalIgnoreCase))
        {
            var ejectItem = new MenuItem { Header = "⏏ Safely Remove Hardware Dialog" };
            ejectItem.Click += (s, e) => Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,Control_RunDLL hotplug.dll") { UseShellExecute = true });
            menu.Items.Add(ejectItem);

            var diskItem = new MenuItem { Header = "💿 Open Disk Management" };
            diskItem.Click += (s, e) => Process.Start(new ProcessStartInfo("diskmgmt.msc") { UseShellExecute = true });
            menu.Items.Add(diskItem);
        }
        else if (pName == "explorer" && string.Equals(app.IconGuid, "{16D5471A-2359-44B0-9585-E95E189DBB3F}", StringComparison.OrdinalIgnoreCase))
        {
            var btSettings = new MenuItem { Header = "⚙ Open Bluetooth Settings" };
            btSettings.Click += (s, e) => OpenSettingsUri("ms-settings:bluetooth");
            menu.Items.Add(btSettings);

            var btTransfer = new MenuItem { Header = "📲 Send or Receive Files via Bluetooth" };
            btTransfer.Click += (s, e) => Process.Start(new ProcessStartInfo("fsquirt.exe") { UseShellExecute = true });
            menu.Items.Add(btTransfer);
        }
        else if (pName == "onedrive")
        {
            var odFolder = new MenuItem { Header = "📁 Open OneDrive Folder" };
            odFolder.Click += (s, e) => OpenTrayApp(app);
            menu.Items.Add(odFolder);

            var odApp = new MenuItem { Header = "☁ Launch OneDrive Sync Client" };
            odApp.Click += (s, e) =>
            {
                if (File.Exists(app.ExecutablePath))
                    Process.Start(new ProcessStartInfo(app.ExecutablePath) { UseShellExecute = true });
            };
            menu.Items.Add(odApp);
        }

        // Trigger Native Tray Menu / Click
        if (app.IsTrayApp)
        {
            var trayRightClick = new MenuItem { Header = "Trigger Tray Icon Right-Click Menu" };
            trayRightClick.Click += (s, e) =>
            {
                if (!TrySendTrayMouseClick(app, rightClick: true))
                {
                    OpenNativeWindowsTray();
                }
            };
            menu.Items.Add(trayRightClick);
        }

        // Minimize / Hide Window if Open
        if (app.IsOpenWindow && app.MainWindowHandle != IntPtr.Zero)
        {
            var minItem = new MenuItem { Header = "Minimize Window to Tray / Taskbar" };
            minItem.Click += (s, e) =>
            {
                ShowWindow(app.MainWindowHandle, SW_MINIMIZE);
                RefreshTrayApps();
            };
            menu.Items.Add(minItem);
        }

        menu.Items.Add(new Separator());

        // Open File Location
        if (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath))
        {
            var locItem = new MenuItem { Header = "Open File Location" };
            locItem.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{app.ExecutablePath}\"") { UseShellExecute = true });
                }
                catch { }
            };
            menu.Items.Add(locItem);
        }

        // Restart & Quit (for non-explorer apps)
        if (!string.Equals(app.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(app.ProcessName, "Dupin", StringComparison.OrdinalIgnoreCase))
        {
            var restartItem = new MenuItem { Header = $"Restart {app.Name}" };
            restartItem.Click += async (s, e) =>
            {
                await RestartTrayAppAsync(app);
            };
            menu.Items.Add(restartItem);

            var quitItem = new MenuItem { Header = $"Quit {app.Name}" };
            quitItem.Click += (s, e) =>
            {
                QuitTrayApp(app);
            };
            menu.Items.Add(quitItem);
        }

        return menu;
    }

    private void RunProcessSilent(string exePath, string args, string feedback)
    {
        try
        {
            if (File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo(exePath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                FeedbackRequested?.Invoke("Action Executed", feedback);
            }
        }
        catch { }
    }

    private async Task RestartTrayAppAsync(TrayAppItem app)
    {
        try
        {
            QuitTrayApp(app, silent: true);
            await Task.Delay(600);
            if (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath))
            {
                Process.Start(new ProcessStartInfo(app.ExecutablePath) { UseShellExecute = true });
                FeedbackRequested?.Invoke(app.Name, $"Restarted {app.Name}");
            }
            await Task.Delay(800);
            RefreshTrayApps();
        }
        catch { }
    }

    private void QuitTrayApp(TrayAppItem app, bool silent = false)
    {
        try
        {
            var procs = Process.GetProcessesByName(app.ProcessName);
            foreach (var p in procs)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        p.CloseMainWindow();
                        if (!p.WaitForExit(400))
                        {
                            p.Kill();
                        }
                    }
                    else
                    {
                        p.Kill();
                    }
                }
                catch { }
            }

            if (!silent)
            {
                FeedbackRequested?.Invoke(app.Name, $"Closed {app.Name}");
                RefreshTrayApps();
            }
        }
        catch { }
    }

    [RelayCommand]
    public void OpenNativeWindowsTray()
    {
        try
        {
            // Check if TopLevelWindowForOverflowXamlIsland exists and show/toggle it
            IntPtr overflowHwnd = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
            if (overflowHwnd == IntPtr.Zero)
            {
                overflowHwnd = FindWindow("NotifyIconOverflowWindow", null);
            }

            if (overflowHwnd != IntPtr.Zero)
            {
                bool isVis = IsWindowVisible(overflowHwnd);
                ShowWindow(overflowHwnd, isVis ? SW_HIDE : SW_SHOW);
                if (!isVis)
                {
                    SetForegroundWindow(overflowHwnd);
                    FeedbackRequested?.Invoke("System Tray", "Opened native Windows hidden icons flyout");
                    return;
                }
            }

            // Fallback: open our floating bottom Tray Apps Flyout right above the taskbar!
            ToggleBottomTrayFlyoutWindow();
        }
        catch { }
    }

    private static bool TryRestoreOrActivateProcessWindow(string processName, IntPtr knownMainHwnd)
    {
        if (knownMainHwnd != IntPtr.Zero && IsWindow(knownMainHwnd))
        {
            if (IsIconic(knownMainHwnd) || !IsWindowVisible(knownMainHwnd))
            {
                ShowWindow(knownMainHwnd, SW_RESTORE);
                ShowWindow(knownMainHwnd, SW_SHOW);
            }
            SetForegroundWindow(knownMainHwnd);
            return true;
        }

        var procs = Process.GetProcessesByName(processName);
        if (procs.Length == 0) return false;
        var pids = new HashSet<uint>(procs.Select(p => (uint)p.Id));

        IntPtr foundHwnd = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (!pids.Contains(pid)) return true;

            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            string title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title) ||
                title.Contains("Default IME", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("MSCTFIME", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("GDI+ Window", StringComparison.OrdinalIgnoreCase) ||
                title.Contains(".NET-BroadcastEventWindow", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IsWindowVisible(hWnd) || IsIconic(hWnd))
            {
                foundHwnd = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (foundHwnd != IntPtr.Zero)
        {
            if (IsIconic(foundHwnd) || !IsWindowVisible(foundHwnd))
            {
                ShowWindow(foundHwnd, SW_RESTORE);
                ShowWindow(foundHwnd, SW_SHOW);
            }
            SetForegroundWindow(foundHwnd);
            return true;
        }

        return false;
    }

    private static bool TrySendTrayMouseClick(TrayAppItem app, bool rightClick)
    {
        try
        {
            var procs = Process.GetProcessesByName(app.ProcessName);
            if (procs.Length == 0) return false;
            var pids = new HashSet<uint>(procs.Select(p => (uint)p.Id));

            var candidateWindows = new List<IntPtr>();
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pids.Contains(pid))
                {
                    candidateWindows.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);

            // Also check HWND_MESSAGE windows for tray listeners
            IntPtr msgChild = IntPtr.Zero;
            while ((msgChild = FindWindowEx(new IntPtr(-3) /* HWND_MESSAGE */, msgChild, null!, null)) != IntPtr.Zero)
            {
                GetWindowThreadProcessId(msgChild, out uint pid);
                if (pids.Contains(pid))
                {
                    candidateWindows.Add(msgChild);
                }
            }

            if (candidateWindows.Count == 0) return false;

            uint uId = app.Uid ?? 0;
            IntPtr lParamMsg = new IntPtr(rightClick ? 0x0205 /* WM_RBUTTONUP */ : 0x0202 /* WM_LBUTTONUP */);

            // Common tray callback messages used by WinForms, WPF, Electron, Qt, Tauri, and Win32 tray apps
            uint[] commonCallbacks = new uint[]
            {
                0x0400 + 1,   // WM_USER + 1 (Electron / Chromium / Tauri / Win32 standard)
                0x0400 + 100, // WM_USER + 100
                0x0400 + 101, // WM_USER + 101
                0x0400 + 20,  // Qt QSystemTrayIcon
                0x0800 + 1,   // WM_APP + 1 (WinForms NotifyIcon / PowerToys)
                0x0800 + 2,   // WM_APP + 2
                0xC000        // Registered tray message fallback
            };

            foreach (var hWnd in candidateWindows)
            {
                foreach (uint cb in commonCallbacks)
                {
                    PostMessage(hWnd, cb, new IntPtr(uId), lParamMsg);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    partial void OnBrightnessChanged(int value)
    {
        if (_isUpdatingSliders) return;
        _hudService.SetBrightnessLevel(value);
    }

    partial void OnVolumeChanged(int value)
    {
        if (_isUpdatingSliders) return;
        _hudService.SetMasterVolume(value);
        if (value > 0 && IsMuted)
        {
            IsMuted = false;
        }
    }

    public void SyncHardwareLevels()
    {
        _isUpdatingSliders = true;
        try
        {
            if (HardwareHudService.TryGetMasterVolume(out int vol, out bool muted))
            {
                Volume = vol;
                IsMuted = muted;
            }

            int b = _hudService.GetCurrentBrightness();
            if (b >= 0 && b <= 100)
            {
                Brightness = b;
            }
        }
        catch { }
        finally
        {
            _isUpdatingSliders = false;
        }
    }

    private void UpdateKeyboardAndClock()
    {
        FullClockText = DateTime.Now.ToString("| ddd, MMM dd yyyy | hh:mm:ss tt |", CultureInfo.InvariantCulture);

        try
        {
            var lang = InputLanguageManager.Current?.CurrentInputLanguage ?? CultureInfo.CurrentUICulture;
            KeyboardLayoutCode = lang.ThreeLetterISOLanguageName.ToUpperInvariant();
            string region = lang.Name.Contains('-') ? lang.Name.Split('-')[1].ToUpperInvariant() : "INTL";
            KeyboardLayoutSub = region == "US" ? "INTL" : region;
        }
        catch
        {
            KeyboardLayoutCode = "ENG";
            KeyboardLayoutSub = "INTL";
        }
    }

    public async Task RefreshRadiosAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var wifiRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            if (wifiRadio != null)
            {
                IsWifiEnabled = wifiRadio.State == RadioState.On;
            }
            else
            {
                IsWifiEnabled = NetworkInterface.GetIsNetworkAvailable();
            }

            if (IsWifiEnabled)
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile != null && profile.IsWlanConnectionProfile && profile.WlanConnectionProfileDetails != null)
                {
                    string ssid = profile.WlanConnectionProfileDetails.GetConnectedSsid();
                    WifiSubtitle = string.IsNullOrWhiteSpace(ssid) ? "Wi-Fi" : ssid;
                }
                else
                {
                    WifiSubtitle = "Wi-Fi";
                }
            }
            else
            {
                WifiSubtitle = "Off";
            }

            var btRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (btRadio != null)
            {
                IsBluetoothEnabled = btRadio.State == RadioState.On;
                BluetoothSubtitle = IsBluetoothEnabled ? "Not connected" : "Off";
            }
        }
        catch
        {
            IsWifiEnabled = NetworkInterface.GetIsNetworkAvailable();
            WifiSubtitle = IsWifiEnabled ? "Wi-Fi" : "Off";
        }
    }

    [RelayCommand]
    public async Task ToggleWifiAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access == RadioAccessStatus.Allowed)
            {
                var radios = await Radio.GetRadiosAsync();
                var wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
                if (wifi != null)
                {
                    var target = IsWifiEnabled ? RadioState.Off : RadioState.On;
                    await wifi.SetStateAsync(target);
                    IsWifiEnabled = target == RadioState.On;
                    WifiSubtitle = IsWifiEnabled ? "Wi-Fi" : "Off";
                    FeedbackRequested?.Invoke("Wi-Fi", IsWifiEnabled ? "Wi-Fi enabled" : "Wi-Fi turned off");
                    return;
                }
            }
        }
        catch { }

        IsWifiEnabled = !IsWifiEnabled;
        WifiSubtitle = IsWifiEnabled ? "Wi-Fi" : "Off";
        OpenSettingsUri("ms-settings:network-wifi");
    }

    [RelayCommand]
    public void OpenWifiSettings() => OpenSettingsUri("ms-settings:network-wifi");

    [RelayCommand]
    public async Task ToggleBluetoothAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access == RadioAccessStatus.Allowed)
            {
                var radios = await Radio.GetRadiosAsync();
                var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (bt != null)
                {
                    var target = IsBluetoothEnabled ? RadioState.Off : RadioState.On;
                    await bt.SetStateAsync(target);
                    IsBluetoothEnabled = target == RadioState.On;
                    BluetoothSubtitle = IsBluetoothEnabled ? "Not connected" : "Off";
                    FeedbackRequested?.Invoke("Bluetooth", IsBluetoothEnabled ? "Bluetooth enabled" : "Bluetooth turned off");
                    return;
                }
            }
        }
        catch { }

        IsBluetoothEnabled = !IsBluetoothEnabled;
        BluetoothSubtitle = IsBluetoothEnabled ? "Not connected" : "Off";
        OpenSettingsUri("ms-settings:bluetooth");
    }

    [RelayCommand]
    public void OpenBluetoothSettings() => OpenSettingsUri("ms-settings:bluetooth");

    [RelayCommand]
    public async Task ToggleAirplaneModeAsync()
    {
        IsAirplaneMode = !IsAirplaneMode;
        AirplaneSubtitle = IsAirplaneMode ? "Airplane on" : "Airplane mode";
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access == RadioAccessStatus.Allowed)
            {
                var radios = await Radio.GetRadiosAsync();
                foreach (var r in radios)
                {
                    if (r.Kind == RadioKind.WiFi || r.Kind == RadioKind.Bluetooth || r.Kind == RadioKind.MobileBroadband)
                    {
                        await r.SetStateAsync(IsAirplaneMode ? RadioState.Off : RadioState.On);
                    }
                }
                await RefreshRadiosAsync();
                FeedbackRequested?.Invoke("Airplane Mode", IsAirplaneMode ? "All wireless radios disabled" : "Wireless radios restored");
                return;
            }
        }
        catch { }

        OpenSettingsUri("ms-settings:network-airplanemode");
    }

    [RelayCommand]
    public void ToggleEnergySaver()
    {
        IsEnergySaver = !IsEnergySaver;
        EnergySaverSubtitle = IsEnergySaver ? "Saver active" : "Energy saver";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = IsEnergySaver ? "/setdcvalueindex SCHEME_CURRENT SUB_ENERGYSAVER ESBATTTHRESHOLD 100" : "/setdcvalueindex SCHEME_CURRENT SUB_ENERGYSAVER ESBATTTHRESHOLD 20",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            Process.Start(new ProcessStartInfo("powercfg.exe", "/setactive SCHEME_CURRENT") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }
        FeedbackRequested?.Invoke("Energy Saver", IsEnergySaver ? "Battery energy saver enabled" : "Standard power profile active");
    }

    [RelayCommand]
    public void ToggleNightLight()
    {
        IsNightLight = !IsNightLight;
        NightLightSubtitle = IsNightLight ? "Warm display" : "Night light";
        OpenSettingsUri("ms-settings:nightlight");
    }

    [RelayCommand]
    public void OpenAccessibility() => OpenSettingsUri("ms-settings:easeofaccess");

    [RelayCommand]
    public void ToggleMute()
    {
        IsMuted = _hudService.ToggleMasterMute();
    }

    [RelayCommand]
    public void OpenSoundOutput() => OpenSettingsUri("ms-settings:sound");

    [RelayCommand]
    public void OpenWindowsSettings() => OpenSettingsUri("ms-settings:");

    [RelayCommand]
    public void OpenLanguageSettings() => OpenSettingsUri("ms-settings:regionlanguage");

    private static Window? _trayCoverWindow;
    private static Window? _bottomTrayFlyoutWindow;
    private static int _cachedTrayNotifyLeft = -1;

    [RelayCommand]
    public void ToggleWindowsTaskbar()
    {
        IsWindowsTaskbarHidden = !IsWindowsTaskbarHidden;
        SetWindowsTaskbarVisibility(!IsWindowsTaskbarHidden);
        FeedbackRequested?.Invoke(
            IsWindowsTaskbarHidden ? "System Tray Hidden" : "System Tray Restored",
            IsWindowsTaskbarHidden ? "Right taskbar tray hidden · Click ^ to view tray apps" : "Right taskbar system tray visible again");
    }

    public static void EnsureFullTaskbarRestored()
    {
        try
        {
            IntPtr shellTray = FindWindow("Shell_TrayWnd", null);
            if (shellTray != IntPtr.Zero)
            {
                var abd = new APPBARDATA();
                abd.cbSize = Marshal.SizeOf(abd);
                abd.hWnd = shellTray;
                abd.lParam = ABS_ALWAYSONTOP;
                SHAppBarMessage(ABM_SETSTATE, ref abd);
                ShowWindow(shellTray, SW_SHOW);

                IntPtr trayNotify = FindWindowEx(shellTray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (trayNotify != IntPtr.Zero && GetWindowRect(trayNotify, out RECT nr) && nr.right > nr.left)
                {
                    _cachedTrayNotifyLeft = nr.left;
                }
            }

            IntPtr secondaryHwnd = FindWindow("Shell_SecondaryTrayWnd", null);
            if (secondaryHwnd != IntPtr.Zero)
            {
                ShowWindow(secondaryHwnd, SW_SHOW);
            }

            if (_bottomTrayFlyoutWindow != null && _bottomTrayFlyoutWindow.IsVisible)
            {
                _bottomTrayFlyoutWindow.Hide();
            }
        }
        catch { }
    }

    public static void SetWindowsTaskbarVisibility(bool visible)
    {
        try
        {
            // Always keep main Shell_TrayWnd (Start Menu + App Icons) visible!
            EnsureFullTaskbarRestored();

            IntPtr shellTray = FindWindow("Shell_TrayWnd", null);
            if (shellTray != IntPtr.Zero)
            {
                IntPtr trayNotify = FindWindowEx(shellTray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (trayNotify != IntPtr.Zero)
                {
                    if (GetWindowRect(trayNotify, out RECT nr) && nr.right > nr.left && nr.left > 0)
                    {
                        _cachedTrayNotifyLeft = nr.left;
                    }

                    // Hide/Show only the right-side System Tray / Notification / Clock area
                    ShowWindow(trayNotify, visible ? SW_SHOW : SW_HIDE);
                    EnumChildWindows(trayNotify, (child, _) =>
                    {
                        ShowWindow(child, visible ? SW_SHOW : SW_HIDE);
                        return true;
                    }, IntPtr.Zero);
                }
            }

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                if (visible)
                {
                    _trayCoverWindow?.Hide();
                    _bottomTrayFlyoutWindow?.Hide();
                }
                else
                {
                    ShowOrUpdateTrayCoverWindow();
                }
            });
        }
        catch { }
    }

    public static void UpdateTrayCoverPosition()
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            if (_trayCoverWindow != null && _trayCoverWindow.IsVisible)
            {
                PositionTrayCoverWindow(_trayCoverWindow);
            }
        });
    }

    private static void ShowOrUpdateTrayCoverWindow()
    {
        if (_trayCoverWindow == null)
        {
            var rootGrid = new Grid();
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left interactive '^' Hidden Icons button on the cover bar so user can pop open tray apps right from the taskbar too!
            var chevronBtn = new Button
            {
                Content = "⌃",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF7)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 32,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Show Hidden Tray & Open Apps (Click to open · Double-click bar to restore taskbar tray)"
            };
            chevronBtn.Click += (s, e) =>
            {
                ToggleBottomTrayFlyoutWindow();
            };
            Grid.SetColumn(chevronBtn, 0);
            rootGrid.Children.Add(chevronBtn);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                ToolTip = "System Tray hidden by Dupin (Click ^ for tray apps · Double-click to restore)",
                Child = rootGrid
            };

            _trayCoverWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = false,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)),
                Content = border
            };

            _trayCoverWindow.MouseDoubleClick += (s, e) =>
            {
                if (Application.Current?.MainWindow?.DataContext is MainViewModel mvm)
                {
                    mvm.Tools.ToggleWindowsTaskbar();
                }
                else
                {
                    SetWindowsTaskbarVisibility(true);
                }
            };

            _trayCoverWindow.SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(_trayCoverWindow).Handle;
                int exStyle = Core.Native.NativeMethods.GetWindowLong(hwnd, Core.Native.NativeMethods.GWL_EXSTYLE);
                exStyle |= Core.Native.NativeMethods.WS_EX_TOOLWINDOW | Core.Native.NativeMethods.WS_EX_NOACTIVATE;
                Core.Native.NativeMethods.SetWindowLong(hwnd, Core.Native.NativeMethods.GWL_EXSTYLE, exStyle);
            };
        }

        PositionTrayCoverWindow(_trayCoverWindow);
        if (!_trayCoverWindow.IsVisible)
        {
            _trayCoverWindow.Show();
        }
    }

    public static void ToggleBottomTrayFlyoutWindow()
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            if (Application.Current?.MainWindow?.DataContext is not MainViewModel mvm)
                return;

            mvm.Tools.RefreshTrayApps();

            if (_bottomTrayFlyoutWindow != null && _bottomTrayFlyoutWindow.IsVisible)
            {
                _bottomTrayFlyoutWindow.Hide();
                return;
            }

            if (_bottomTrayFlyoutWindow == null)
            {
                _bottomTrayFlyoutWindow = CreateBottomTrayFlyoutWindow(mvm.Tools);
            }

            RebuildBottomTrayFlyoutContent(_bottomTrayFlyoutWindow, mvm.Tools);
            PositionBottomTrayFlyoutWindow(_bottomTrayFlyoutWindow);
            _bottomTrayFlyoutWindow.Show();
            _bottomTrayFlyoutWindow.Activate();
        });
    }

    private static Window CreateBottomTrayFlyoutWindow(ToolsViewModel toolsVm)
    {
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Width = 236,
            Height = 200
        };

        win.Deactivated += (s, e) =>
        {
            win.Hide();
        };

        return win;
    }

    private static void RebuildBottomTrayFlyoutContent(Window win, ToolsViewModel toolsVm)
    {
        var items = toolsVm._allDiscoveredApps.Where(a => a.IsTrayApp).ToList();
        if (items.Count == 0)
        {
            items = toolsVm._allDiscoveredApps.ToList();
        }

        int cols = 5;
        int rows = Math.Max(1, (int)Math.Ceiling(items.Count / (double)cols));
        win.Width = 236;
        win.Height = Math.Min(300, (rows * 42) + 46);

        var outerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x46)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(6),
            Padding = new Thickness(10, 8, 10, 8),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 14,
                ShadowDepth = 3,
                Opacity = 0.65
            }
        };

        var stack = new StackPanel();

        // Header row
        var headerGrid = new Grid { Margin = new Thickness(2, 0, 2, 6) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleBlock = new TextBlock
        {
            Text = $"System Tray & Open Apps ({items.Count})",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xB5)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleBlock, 0);
        headerGrid.Children.Add(titleBlock);

        var wrapGrid = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = cols
        };

        foreach (var app in items)
        {
            var btn = new Button
            {
                Width = 36,
                Height = 36,
                Margin = new Thickness(2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = app.FullTooltip,
                Content = new Image
                {
                    Source = app.IconSource,
                    Width = 20,
                    Height = 20,
                    Stretch = Stretch.Uniform
                }
            };

            btn.Click += (s, e) =>
            {
                win.Hide();
                toolsVm.OpenTrayApp(app);
            };

            btn.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                var cm = toolsVm.CreateAppActionsContextMenu(app);
                cm.PlacementTarget = btn;
                cm.IsOpen = true;
            };

            wrapGrid.Children.Add(btn);
        }

        stack.Children.Add(headerGrid);
        stack.Children.Add(wrapGrid);
        outerBorder.Child = stack;
        win.Content = outerBorder;
    }

    private static void PositionBottomTrayFlyoutWindow(Window win)
    {
        double dpiX = 1.0, dpiY = 1.0;
        var src = PresentationSource.FromVisual(Application.Current.MainWindow);
        if (src?.CompositionTarget != null)
        {
            dpiX = src.CompositionTarget.TransformToDevice.M11;
            dpiY = src.CompositionTarget.TransformToDevice.M22;
        }

        IntPtr shellTray = FindWindow("Shell_TrayWnd", null);
        if (shellTray != IntPtr.Zero && GetWindowRect(shellTray, out RECT sr))
        {
            int trayLeft = _cachedTrayNotifyLeft > sr.left ? _cachedTrayNotifyLeft : (sr.right - 320);
            win.Left = Math.Max(12, (trayLeft / dpiX) - 20);
            win.Top = (sr.top / dpiY) - win.Height - 4;
        }
        else
        {
            win.Left = SystemParameters.WorkArea.Right - win.Width - 24;
            win.Top = SystemParameters.WorkArea.Bottom - win.Height - 8;
        }
    }

    private static void PositionTrayCoverWindow(Window cover)
    {
        IntPtr shellTray = FindWindow("Shell_TrayWnd", null);
        if (shellTray == IntPtr.Zero || !GetWindowRect(shellTray, out RECT sr))
            return;

        int shellWidth = sr.right - sr.left;
        int shellHeight = sr.bottom - sr.top;
        if (shellWidth <= 0 || shellHeight <= 0)
            return;

        int rebarRight = sr.left + (int)(shellWidth * 0.55);
        IntPtr rebar = FindWindowEx(shellTray, IntPtr.Zero, "ReBarWindow32", null);
        if (rebar != IntPtr.Zero && GetWindowRect(rebar, out RECT rr) && rr.right > sr.left)
        {
            rebarRight = rr.right;
        }

        int trayLeft = _cachedTrayNotifyLeft;
        IntPtr trayNotify = FindWindowEx(shellTray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (trayNotify != IntPtr.Zero && GetWindowRect(trayNotify, out RECT nr) && nr.right > nr.left && nr.left > sr.left)
        {
            trayLeft = nr.left;
            _cachedTrayNotifyLeft = nr.left;
        }

        int fallbackTrayLeft = sr.right - (int)(shellWidth * 0.341);
        if (trayLeft <= sr.left || trayLeft > fallbackTrayLeft)
        {
            trayLeft = Math.Max(Math.Min(rebarRight, fallbackTrayLeft), fallbackTrayLeft);
        }

        if (!_trayCoverWindow!.IsVisible && cover.Content is Border b)
        {
            if (TrySampleScreenPixel(Math.Max(sr.left + 10, trayLeft - 8), sr.top + (shellHeight / 2), out var bgColor))
            {
                b.Background = new SolidColorBrush(bgColor);
                cover.Background = new SolidColorBrush(bgColor);
            }
            if (TrySampleScreenPixel(Math.Max(sr.left + 10, trayLeft - 8), sr.top, out var topBorderColor))
            {
                b.BorderBrush = new SolidColorBrush(topBorderColor);
            }
        }

        var hwnd = new WindowInteropHelper(cover).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Core.Native.NativeMethods.SetWindowPos(
                hwnd,
                Core.Native.NativeMethods.HWND_TOPMOST,
                trayLeft,
                sr.top,
                Math.Max(60, sr.right - trayLeft),
                shellHeight,
                Core.Native.NativeMethods.SWP_NOACTIVATE | Core.Native.NativeMethods.SWP_SHOWWINDOW);
        }
        else
        {
            double dpiX = 1.0, dpiY = 1.0;
            var src = PresentationSource.FromVisual(Application.Current.MainWindow);
            if (src?.CompositionTarget != null)
            {
                dpiX = src.CompositionTarget.TransformToDevice.M11;
                dpiY = src.CompositionTarget.TransformToDevice.M22;
            }
            cover.Left = trayLeft / dpiX;
            cover.Top = sr.top / dpiY;
            cover.Width = Math.Max(60, (sr.right - trayLeft) / dpiX);
            cover.Height = shellHeight / dpiY;
        }
    }

    private static bool TrySampleScreenPixel(int x, int y, out Color color)
    {
        color = Color.FromRgb(0x1F, 0x1F, 0x1F);
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            uint pixel = GetPixel(hdc, x, y);
            if (pixel == 0xFFFFFFFF) return false;
            byte r = (byte)(pixel & 0x000000FF);
            byte g = (byte)((pixel & 0x0000FF00) >> 8);
            byte b = (byte)((pixel & 0x00FF0000) >> 16);
            color = Color.FromRgb(r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

    private static void OpenSettingsUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch { }
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint ABM_SETSTATE = 0x0000000A;
    private const int ABS_AUTOHIDE = 0x0000001;
    private const int ABS_ALWAYSONTOP = 0x0000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("shell32.dll")]
    private static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
}
