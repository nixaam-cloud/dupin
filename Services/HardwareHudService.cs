using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WindowsNotch.Core.Native;

namespace WindowsNotch.Services;

public enum HardwareHudType
{
    Volume,
    VolumeMuted,
    MicMuted,
    MicUnmuted,
    Brightness
}

public class HardwareHudEventArgs : EventArgs
{
    public HardwareHudType HudType { get; init; }
    public string Title { get; init; } = string.Empty;
    public int Percentage { get; init; }
    public string ValueText { get; init; } = string.Empty;
}

/// <summary>
/// Hooks into Windows Shell hooks, low-level media keys, and CoreAudio endpoints
/// to intercept Volume, Microphone Mute, and Brightness changes and display fluid pill HUDs.
/// </summary>
public class HardwareHudService : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private readonly DispatcherTimer _pollTimer;
    private uint _shellHookMsgId;
    private IntPtr _hwnd = IntPtr.Zero;

    private int _lastVolume = -1;
    private bool _lastMuted = false;
    private bool _lastMicMuted = false;
    private int _lastBrightness = -1;
    private int _brightnessTickCounter = 0;
    private bool _initialized = false;

    public event Action<HardwareHudEventArgs>? HudTriggered;

    // ShellHook constants
    private const int HSHELL_APPCOMMAND = 12;
    private const int APPCOMMAND_VOLUME_MUTE = 8;
    private const int APPCOMMAND_VOLUME_DOWN = 9;
    private const int APPCOMMAND_VOLUME_UP = 10;
    private const int APPCOMMAND_MICROPHONE_VOLUME_MUTE = 24;

    public HardwareHudService()
    {
        _proc = HookCallback;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _pollTimer.Tick += (s, e) => CheckAudioAndHardwareState(forceTrigger: false);
    }

    public void Initialize(IntPtr hwnd)
    {
        _hwnd = hwnd;

        try
        {
            _shellHookMsgId = NativeMethods.RegisterWindowMessage("SHELLHOOK");
            NativeMethods.RegisterShellHookWindow(_hwnd);
        }
        catch { }

        try
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            IntPtr moduleHandle = NativeMethods.GetModuleHandle(curModule?.ModuleName);
            _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, moduleHandle, 0);
        }
        catch { }

        // Read baseline state without firing HUD on startup
        CheckAudioAndHardwareState(forceTrigger: false, isInitial: true);
        _initialized = true;
        _pollTimer.Start();
    }

    public void ProcessWindowMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (_shellHookMsgId != 0 && msg == _shellHookMsgId)
        {
            int shellCode = wParam.ToInt32() & 0x7FFF;
            if (shellCode == HSHELL_APPCOMMAND)
            {
                int cmd = ((int)((long)lParam >> 16)) & 0xFFF;
                if (cmd == APPCOMMAND_VOLUME_UP || cmd == APPCOMMAND_VOLUME_DOWN || cmd == APPCOMMAND_VOLUME_MUTE)
                {
                    CheckAudioAndHardwareState(forceTrigger: true);
                }
                else if (cmd == APPCOMMAND_MICROPHONE_VOLUME_MUTE)
                {
                    ToggleOrCheckMicState(forceTrigger: true);
                }
            }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN))
        {
            var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (kb.vkCode == NativeMethods.VK_VOLUME_UP ||
                kb.vkCode == NativeMethods.VK_VOLUME_DOWN ||
                kb.vkCode == NativeMethods.VK_VOLUME_MUTE)
            {
                // Schedule check after Windows applies the key change (~35ms)
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(40);
                    CheckAudioAndHardwareState(forceTrigger: true);
                }));
            }
        }
        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void CheckAudioAndHardwareState(bool forceTrigger, bool isInitial = false)
    {
        try
        {
            if (TryGetEndpointVolume(isCapture: false, out int volPct, out bool isMuted))
            {
                if (isInitial)
                {
                    _lastVolume = volPct;
                    _lastMuted = isMuted;
                }
                else if (forceTrigger || volPct != _lastVolume || isMuted != _lastMuted)
                {
                    _lastVolume = volPct;
                    _lastMuted = isMuted;

                    if (isMuted || volPct == 0)
                    {
                        HudTriggered?.Invoke(new HardwareHudEventArgs
                        {
                            HudType = HardwareHudType.VolumeMuted,
                            Title = "Muted",
                            Percentage = 0,
                            ValueText = "Mute"
                        });
                    }
                    else
                    {
                        HudTriggered?.Invoke(new HardwareHudEventArgs
                        {
                            HudType = HardwareHudType.Volume,
                            Title = "Volume",
                            Percentage = volPct,
                            ValueText = $"{volPct}%"
                        });
                    }
                }
            }

            if (TryGetEndpointVolume(isCapture: true, out _, out bool isMicMuted))
            {
                if (isInitial)
                {
                    _lastMicMuted = isMicMuted;
                }
                else if (isMicMuted != _lastMicMuted)
                {
                    _lastMicMuted = isMicMuted;
                    HudTriggered?.Invoke(new HardwareHudEventArgs
                    {
                        HudType = isMicMuted ? HardwareHudType.MicMuted : HardwareHudType.MicUnmuted,
                        Title = isMicMuted ? "Mic Muted" : "Mic Active",
                        Percentage = isMicMuted ? 0 : 100,
                        ValueText = isMicMuted ? "OFF" : "ON"
                    });
                }
            }

            // Check display brightness periodically (every ~1 second)
            _brightnessTickCounter++;
            if (isInitial || _brightnessTickCounter >= 4)
            {
                _brightnessTickCounter = 0;
                if (TryGetMonitorBrightness(_hwnd, out int brightnessPct))
                {
                    if (isInitial)
                    {
                        _lastBrightness = brightnessPct;
                    }
                    else if (brightnessPct != _lastBrightness && _lastBrightness >= 0)
                    {
                        _lastBrightness = brightnessPct;
                        HudTriggered?.Invoke(new HardwareHudEventArgs
                        {
                            HudType = HardwareHudType.Brightness,
                            Title = "Brightness",
                            Percentage = brightnessPct,
                            ValueText = $"{brightnessPct}%"
                        });
                    }
                }
            }
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    private static bool TryGetMonitorBrightness(IntPtr hwnd, out int brightnessPercent)
    {
        brightnessPercent = -1;
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            IntPtr hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            if (hMon == IntPtr.Zero) return false;

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0)
                return false;

            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, monitors))
                return false;

            try
            {
                if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out uint min, out uint cur, out uint max) && max > min)
                {
                    brightnessPercent = (int)Math.Round(((double)(cur - min) / (max - min)) * 100.0);
                    return true;
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, monitors);
            }
        }
        catch { }
        return false;
    }

    private void ToggleOrCheckMicState(bool forceTrigger)
    {
        if (TryGetEndpointVolume(isCapture: true, out _, out bool isMicMuted))
        {
            _lastMicMuted = isMicMuted;
            if (forceTrigger)
            {
                HudTriggered?.Invoke(new HardwareHudEventArgs
                {
                    HudType = isMicMuted ? HardwareHudType.MicMuted : HardwareHudType.MicUnmuted,
                    Title = isMicMuted ? "Mic Muted" : "Mic Active",
                    Percentage = isMicMuted ? 0 : 100,
                    ValueText = isMicMuted ? "OFF" : "ON"
                });
            }
        }
    }

    private static bool TryGetEndpointVolume(bool isCapture, out int volumePercent, out bool isMuted)
    {
        volumePercent = 0;
        isMuted = false;

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? epVolObj = null;

        try
        {
            Type? enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (enumeratorType == null) return false;

            enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumeratorType);
            if (enumerator == null) return false;

            // eRender = 0, eCapture = 1; eMultimedia = 1
            int dataFlow = isCapture ? 1 : 0;
            int hr = enumerator.GetDefaultAudioEndpoint(dataFlow, 1, out device);
            if (hr != 0 || device == null) return false;

            Guid iidAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
            hr = device.Activate(ref iidAudioEndpointVolume, 23, IntPtr.Zero, out epVolObj);
            if (hr != 0 || epVolObj is not IAudioEndpointVolume epVol) return false;

            epVol.GetMasterVolumeLevelScalar(out float level);
            epVol.GetMute(out isMuted);
            volumePercent = (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100f);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (epVolObj != null && Marshal.IsComObject(epVolObj)) Marshal.ReleaseComObject(epVolObj);
            if (device != null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
            if (enumerator != null && Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator);
        }
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        if (_hookId != IntPtr.Zero)
        {
            try { NativeMethods.UnhookWindowsHookEx(_hookId); } catch { }
            _hookId = IntPtr.Zero;
        }
        if (_hwnd != IntPtr.Zero)
        {
            try { NativeMethods.DeregisterShellHookWindow(_hwnd); } catch { }
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out int pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig]
        int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }
}
