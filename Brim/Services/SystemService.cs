using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using Brim.Core;
using Microsoft.Win32;

namespace Brim.Services;

/// <summary>CPU, memory, network, storage, battery, uptime, camera/mic use, brightness and night light.</summary>
public sealed class SystemService
{
    long _idle, _kernel, _user, _rx, _tx;
    DateTime _netAt;

    public double Cpu { get; private set; }
    public ulong MemTotal { get; private set; }
    public ulong MemUsed { get; private set; }
    public double Down { get; private set; }
    public double Up { get; private set; }
    public long DiskFree { get; private set; }
    public long DiskTotal { get; private set; }
    public int Battery { get; private set; } = -1;
    public bool HasBattery { get; private set; }
    public bool Plugged { get; private set; }
    public bool Charging { get; private set; }
    public TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);
    public bool MicInUse { get; private set; }
    public bool CamInUse { get; private set; }
    public int? Brightness { get; private set; }
    public readonly List<double> CpuHistory = [];

    /// <summary>Raised on the UI thread every second.</summary>
    public event Action? Tick;
    /// <summary>Raised when the charger is plugged or unplugged (true = plugged).</summary>
    public event Action<bool>? PowerChanged;
    public event Action? LowBattery;
    public event Action? BrightnessChanged;

    int _tick, _busy;
    bool _lowFired;
    readonly Timer _timer;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    public SystemService()
    {
        Native.GetSystemTimes(out _idle, out _kernel, out _user);
        (_rx, _tx) = NetBytes();
        _netAt = DateTime.Now;
        ReadPower();
        ReadDisk();
        Publish(Sample());
        // Measuring runs on a pool thread; only the results are handed to the UI thread.
        _timer = new Timer(_ => Background(), null, 1000, 1000);
        Task.Run(() => { Brightness = ReadBrightness(); });
        WatchBrightness();
    }

    void Background()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var events = Sample();
            _ui.BeginInvoke(() => Publish(events));
        }
        catch { }
        finally { _busy = 0; }
    }

    void Publish((bool? power, bool low) events)
    {
        CpuHistory.Add(Cpu);
        if (CpuHistory.Count > 40) CpuHistory.RemoveAt(0);
        if (events.power is bool plugged) PowerChanged?.Invoke(plugged);
        if (events.low) LowBattery?.Invoke();
        Tick?.Invoke();
    }

    /// <summary>Reads all counters (any thread). Returns power events to raise on the UI thread.</summary>
    (bool? power, bool low) Sample()
    {
        _tick++;
        bool? powerEvent = null;
        bool lowEvent = false;
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            long di = idle - _idle, dt = (kernel - _kernel) + (user - _user);
            if (dt > 0) Cpu = Math.Clamp(1.0 - (double)di / dt, 0, 1);
            _idle = idle; _kernel = kernel; _user = user;
        }

        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref m)) { MemTotal = m.ullTotalPhys; MemUsed = m.ullTotalPhys - m.ullAvailPhys; }

        var (rx, tx) = NetBytes();
        var now = DateTime.Now;
        var secs = Math.Max(0.2, (now - _netAt).TotalSeconds);
        Down = Math.Max(0, (rx - _rx) / secs);
        Up = Math.Max(0, (tx - _tx) / secs);
        _rx = rx; _tx = tx; _netAt = now;

        bool wasPlugged = Plugged;
        ReadPower();
        if (HasBattery && wasPlugged != Plugged && _tick > 2) powerEvent = Plugged;
        if (HasBattery && !Plugged && Battery <= 20 && Battery >= 0 && !_lowFired) { _lowFired = true; lowEvent = true; }
        if (Plugged || Battery > 22) _lowFired = false;

        if (_tick % 2 == 0) ReadPrivacy();
        if (_tick % 30 == 0) ReadDisk();
        return (powerEvent, lowEvent);
    }

    static (long, long) NetBytes()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = n.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch { }
        return (rx, tx);
    }

    void ReadPower()
    {
        if (!Native.GetSystemPowerStatus(out var p)) return;
        HasBattery = (p.BatteryFlag & 128) == 0 && p.BatteryLifePercent != 255;
        Battery = p.BatteryLifePercent == 255 ? -1 : p.BatteryLifePercent;
        Plugged = p.ACLineStatus == 1;
        Charging = Plugged && (((p.BatteryFlag & 8) != 0) || Battery < 100);
    }

    void ReadDisk()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            DiskFree = d.AvailableFreeSpace;
            DiskTotal = d.TotalSize;
        }
        catch { }
    }

    void ReadPrivacy()
    {
        MicInUse = InUse("microphone");
        CamInUse = InUse("webcam");
    }

    static bool InUse(string cap)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{cap}");
            if (root == null) return false;
            var self = Environment.ProcessPath ?? "";
            bool Check(RegistryKey k, string name)
            {
                using var sk = k.OpenSubKey(name);
                if (sk == null) return false;
                var stop = sk.GetValue("LastUsedTimeStop");
                var start = sk.GetValue("LastUsedTimeStart");
                return stop is long s && s == 0 && start is long st && st != 0;
            }
            foreach (var n in root.GetSubKeyNames())
            {
                if (n == "NonPackaged")
                {
                    using var np = root.OpenSubKey(n);
                    if (np == null) continue;
                    foreach (var app in np.GetSubKeyNames())
                        if (!app.Replace('#', '\\').Equals(self, StringComparison.OrdinalIgnoreCase) && Check(np, app)) return true;
                }
                else if (Check(root, n)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Trims the working set of every process we may touch. Returns bytes freed.</summary>
    public static Task<long> OptimizeMemoryAsync() => Task.Run(() =>
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref m);
        var before = m.ullAvailPhys;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, p.Id);
                if (h != IntPtr.Zero) { Native.EmptyWorkingSet(h); Native.CloseHandle(h); }
            }
            catch { }
            finally { p.Dispose(); }
        }
        Thread.Sleep(400);
        Native.GlobalMemoryStatusEx(ref m);
        return (long)m.ullAvailPhys - (long)before;
    });

    // ---------------- brightness (laptop panels, via WMI) ----------------
    static int? ReadBrightness()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    int _pendingBrightness = -1;
    bool _brightnessBusy;

    public void SetBrightness(int value)
    {
        Brightness = Math.Clamp(value, 0, 100);
        _pendingBrightness = Brightness.Value;
        if (_brightnessBusy) return;
        _brightnessBusy = true;
        Task.Run(() =>
        {
            while (_pendingBrightness >= 0)
            {
                int v = Interlocked.Exchange(ref _pendingBrightness, -1);
                try
                {
                    using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                    foreach (ManagementObject o in s.Get()) { o.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)v]); break; }
                }
                catch { }
            }
            _brightnessBusy = false;
        });
    }

    void WatchBrightness()
    {
        try
        {
            var w = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            w.EventArrived += (_, e) =>
            {
                try
                {
                    var v = Convert.ToInt32(e.NewEvent["Brightness"]);
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        bool changed = Brightness != v;
                        Brightness = v;
                        if (changed && !_brightnessBusy) BrightnessChanged?.Invoke();
                    });
                }
                catch { }
            };
            w.Start();
        }
        catch { }
    }

    // ---------------- night light (Windows stores it in a CloudStore blob) ----------------
    const string NightKey = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    public static bool? NightLightOn()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(NightKey);
            if (k?.GetValue("Data") is not byte[] d || !LooksValid(d)) return null;
            return IsOn(d);
        }
        catch { return null; }
    }

    // Layout: ... 2A 2B 0E <len> 43 42 01 00 [10 00 when on] ... ; <len> grows by 2 when on.
    static bool LooksValid(byte[] d) =>
        d.Length > 26 && d[15] == 0x2A && d[16] == 0x2B && d[17] == 0x0E && d[18] >= 0x08 && d[18] < 0x40
        && d[19] == 0x43 && d[20] == 0x42 && d[21] == 0x01 && d[22] == 0x00;

    static bool IsOn(byte[] d) => d[23] == 0x10 && d[24] == 0x00;

    /// <summary>Flips night light. Falls back to opening Settings if the blob looks unfamiliar.</summary>
    public static void ToggleNightLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(NightKey, true);
            if (k?.GetValue("Data") is byte[] d && LooksValid(d))
            {
                byte[] n;
                if (IsOn(d))
                {
                    n = new byte[d.Length - 2];
                    Array.Copy(d, 0, n, 0, 23);
                    Array.Copy(d, 25, n, 23, d.Length - 25);
                    n[18] = (byte)(d[18] - 2);
                }
                else
                {
                    n = new byte[d.Length + 2];
                    Array.Copy(d, 0, n, 0, 23);
                    n[23] = 0x10; n[24] = 0x00;
                    Array.Copy(d, 23, n, 25, d.Length - 23);
                    n[18] = (byte)(d[18] + 2);
                }
                for (int i = 10; i < 15; i++) { if (n[i] != 0xFF) { n[i]++; break; } }
                k.SetValue("Data", n, RegistryValueKind.Binary);
                return;
            }
        }
        catch { }
        Shell.Open("ms-settings:nightlight");
    }
}
