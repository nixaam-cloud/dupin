using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WindowsNotch.Services;

public class SystemStatusService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private const int VK_CAPITAL = 0x14;

    private readonly DispatcherTimer _pollTimer;
    private byte _lastAcStatus = 255;
    private byte _lastBatteryPct = 255;
    private bool _lastCapsLock = false;

    public int BatteryPercent { get; private set; } = 100;
    public bool IsCharging { get; private set; } = true;
    public bool HasBattery { get; private set; } = false;
    public string PowerStatusText { get; private set; } = "Connected";

    public event Action<string, string>? PeekNotificationTriggered;
    public event Action? StatusUpdated;

    public SystemStatusService()
    {
        _lastCapsLock = (GetKeyState(VK_CAPITAL) & 0x0001) != 0;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _pollTimer.Tick += (s, e) => CheckStatus();
        _pollTimer.Start();

        CheckStatus(initial: true);
    }

    private void CheckStatus(bool initial = false)
    {
        // 1. Power status check
        if (GetSystemPowerStatus(out var status))
        {
            HasBattery = status.BatteryLifePercent <= 100;
            BatteryPercent = HasBattery ? status.BatteryLifePercent : 100;
            IsCharging = status.ACLineStatus == 1;

            PowerStatusText = IsCharging ? "Charging" : "On Battery";

            if (!initial)
            {
                if (status.ACLineStatus != _lastAcStatus)
                {
                    if (IsCharging)
                    {
                        PeekNotificationTriggered?.Invoke("Charging", $"Charger connected · {BatteryPercent}%");
                    }
                    else
                    {
                        PeekNotificationTriggered?.Invoke("On Battery", $"Unplugged · {BatteryPercent}% left");
                    }
                }
            }

            _lastAcStatus = status.ACLineStatus;
            _lastBatteryPct = status.BatteryLifePercent;
        }

        // 2. Caps lock check
        bool currentCapsLock = (GetKeyState(VK_CAPITAL) & 0x0001) != 0;
        if (!initial && currentCapsLock != _lastCapsLock)
        {
            PeekNotificationTriggered?.Invoke(currentCapsLock ? "Caps Lock ON" : "Caps Lock OFF", currentCapsLock ? "ABC uppercase active" : "abc lowercase active");
        }
        _lastCapsLock = currentCapsLock;

        StatusUpdated?.Invoke();
    }

    public void Dispose()
    {
        _pollTimer.Stop();
    }
}
