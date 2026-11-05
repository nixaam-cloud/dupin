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

        _syncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _syncTimer.Tick += (s, e) =>
        {
            UpdateKeyboardAndClock();
            SyncHardwareLevels();
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

    [RelayCommand]
    public void ToggleWindowsTaskbar()
    {
        IsWindowsTaskbarHidden = !IsWindowsTaskbarHidden;
        SetWindowsTaskbarVisibility(!IsWindowsTaskbarHidden);
        FeedbackRequested?.Invoke(
            IsWindowsTaskbarHidden ? "Taskbar Hidden" : "Taskbar Restored",
            IsWindowsTaskbarHidden ? "Windows taskbar hidden · Notch active" : "Windows taskbar visible again");
    }

    public static void SetWindowsTaskbarVisibility(bool visible)
    {
        try
        {
            // 1. Set AppBar AutoHide state so maximized windows reclaim the full screen when hidden
            var abd = new APPBARDATA();
            abd.cbSize = Marshal.SizeOf(abd);
            abd.hWnd = FindWindow("Shell_TrayWnd", null);
            if (abd.hWnd != IntPtr.Zero)
            {
                abd.lParam = visible ? ABS_ALWAYSONTOP : ABS_AUTOHIDE;
                SHAppBarMessage(ABM_SETSTATE, ref abd);
                ShowWindow(abd.hWnd, visible ? SW_SHOW : SW_HIDE);
            }

            IntPtr secondaryHwnd = FindWindow("Shell_SecondaryTrayWnd", null);
            if (secondaryHwnd != IntPtr.Zero)
            {
                ShowWindow(secondaryHwnd, visible ? SW_SHOW : SW_HIDE);
            }
        }
        catch { }
    }

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

