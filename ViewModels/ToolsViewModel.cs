using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using WindowsNotch.Services;

namespace WindowsNotch.ViewModels;

public partial class ToolsViewModel : ObservableObject
{
    private readonly HardwareHudService _hudService;
    private readonly DispatcherTimer _syncTimer;
    private bool _isUpdatingSliders;

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

    public event Action<string, string>? FeedbackRequested;

    public ToolsViewModel(HardwareHudService hudService)
    {
        _hudService = hudService;

        // Read initial volume & brightness
        SyncHardwareLevels();
        _ = RefreshRadiosAsync();
        UpdateKeyboardAndClock();
        EnsureFullTaskbarRestored();

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
        };
        _syncTimer.Start();
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
            // Set Windows power overlay or launch battery saver settings
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

    private static System.Windows.Window? _trayCoverWindow;
    private static int _cachedTrayNotifyLeft = -1;

    [RelayCommand]
    public void ToggleWindowsTaskbar()
    {
        IsWindowsTaskbarHidden = !IsWindowsTaskbarHidden;
        SetWindowsTaskbarVisibility(!IsWindowsTaskbarHidden);
        FeedbackRequested?.Invoke(
            IsWindowsTaskbarHidden ? "System Tray Hidden" : "System Tray Restored",
            IsWindowsTaskbarHidden ? "Right taskbar tray hidden · Start & apps kept" : "Right taskbar system tray visible again");
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

            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                if (visible)
                {
                    if (_trayCoverWindow != null)
                    {
                        _trayCoverWindow.Hide();
                    }
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
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
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
            var border = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x1F, 0x1F)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x2D)),
                BorderThickness = new System.Windows.Thickness(0, 1, 0, 0),
                ToolTip = "System Tray hidden by Dupin (Double-click to restore)"
            };

            _trayCoverWindow = new System.Windows.Window
            {
                WindowStyle = System.Windows.WindowStyle.None,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                AllowsTransparency = false,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x1F, 0x1F)),
                Content = border
            };

            _trayCoverWindow.MouseDoubleClick += (s, e) =>
            {
                if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel mvm)
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
                var hwnd = new System.Windows.Interop.WindowInteropHelper(_trayCoverWindow).Handle;
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

    private static void PositionTrayCoverWindow(System.Windows.Window cover)
    {
        IntPtr shellTray = FindWindow("Shell_TrayWnd", null);
        if (shellTray == IntPtr.Zero || !GetWindowRect(shellTray, out RECT sr))
            return;

        int shellWidth = sr.right - sr.left;
        int shellHeight = sr.bottom - sr.top;
        if (shellWidth <= 0 || shellHeight <= 0)
            return;

        // Find the right edge of the app icons (MSTaskListWClass / MSTaskSwWClass / ReBarWindow32)
        int rebarRight = sr.left + (int)(shellWidth * 0.55);
        IntPtr rebar = FindWindowEx(shellTray, IntPtr.Zero, "ReBarWindow32", null);
        if (rebar != IntPtr.Zero && GetWindowRect(rebar, out RECT rr) && rr.right > sr.left)
        {
            rebarRight = rr.right;
        }

        // Determine the left edge of the System Tray area (chevron + tray icons + ENG INTL + network/sound/battery + clock)
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

        // Sample live taskbar background pixel just to the left of the system tray so color matches 100%
        if (!_trayCoverWindow!.IsVisible && cover.Content is System.Windows.Controls.Border b)
        {
            if (TrySampleScreenPixel(Math.Max(sr.left + 10, trayLeft - 8), sr.top + (shellHeight / 2), out var bgColor))
            {
                b.Background = new System.Windows.Media.SolidColorBrush(bgColor);
                cover.Background = new System.Windows.Media.SolidColorBrush(bgColor);
            }
            if (TrySampleScreenPixel(Math.Max(sr.left + 10, trayLeft - 8), sr.top, out var topBorderColor))
            {
                b.BorderBrush = new System.Windows.Media.SolidColorBrush(topBorderColor);
            }
        }

        var hwnd = new System.Windows.Interop.WindowInteropHelper(cover).Handle;
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
            var src = System.Windows.PresentationSource.FromVisual(System.Windows.Application.Current.MainWindow);
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

    private static bool TrySampleScreenPixel(int x, int y, out System.Windows.Media.Color color)
    {
        color = System.Windows.Media.Color.FromRgb(0x1F, 0x1F, 0x1F);
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            uint pixel = GetPixel(hdc, x, y);
            if (pixel == 0xFFFFFFFF) return false;
            byte r = (byte)(pixel & 0x000000FF);
            byte g = (byte)((pixel & 0x0000FF00) >> 8);
            byte b = (byte)((pixel & 0x00FF0000) >> 16);
            color = System.Windows.Media.Color.FromRgb(r, g, b);
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
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

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

