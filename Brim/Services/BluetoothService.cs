using Brim.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Brim.Services;

public sealed class BtDevice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public ulong Address { get; init; }
    public bool Connected { get; set; }
    public bool IsLe { get; init; }
    public string Kind { get; init; } = "other";   // audio | input | phone | computer | other
    public int? Battery { get; set; }
    public string Glyph => Kind switch
    {
        "audio" => Ic.Headphones,
        "phone" => Ic.Phone,
        "computer" => Ic.Monitor,
        "input" => "",
        _ => Ic.Bluetooth,
    };
}

/// <summary>Bluetooth radio state, paired devices, connect/disconnect and battery levels.</summary>
public sealed class BluetoothService
{
    Radio? _radio;

    public async Task<bool?> RadioOnAsync()
    {
        try
        {
            _radio ??= (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            return _radio == null ? null : _radio.State == RadioState.On;
        }
        catch { return null; }
    }

    public async Task SetRadioAsync(bool on)
    {
        try
        {
            await Radio.RequestAccessAsync();
            _radio ??= (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (_radio != null) await _radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        }
        catch { }
    }

    public async Task<List<BtDevice>> PairedAsync()
    {
        var list = new List<BtDevice>();
        var batteries = await BatteriesAsync();
        try
        {
            foreach (var di in await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                         ["System.Devices.Aep.ContainerId"]))
            {
                try
                {
                    using var d = await BluetoothDevice.FromIdAsync(di.Id);
                    if (d == null) continue;
                    var kind = d.ClassOfDevice.MajorClass switch
                    {
                        BluetoothMajorClass.AudioVideo => "audio",
                        BluetoothMajorClass.Peripheral => "input",
                        BluetoothMajorClass.Phone => "phone",
                        BluetoothMajorClass.Computer => "computer",
                        _ => "other",
                    };
                    list.Add(new BtDevice
                    {
                        Id = di.Id, Name = string.IsNullOrWhiteSpace(d.Name) ? di.Name : d.Name, Address = d.BluetoothAddress,
                        Connected = d.ConnectionStatus == BluetoothConnectionStatus.Connected, Kind = kind,
                        Battery = Battery(di, batteries),
                    });
                }
                catch { }
            }
            foreach (var di in await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                         ["System.Devices.Aep.ContainerId"]))
            {
                try
                {
                    using var d = await BluetoothLEDevice.FromIdAsync(di.Id);
                    if (d == null || list.Any(x => x.Name == d.Name)) continue;
                    var cat = d.Appearance?.Category ?? 0;
                    var kind = cat switch { 0x0F => "input", 0x01 => "phone", 0x02 => "computer", 0x29 or 0x2A => "audio", _ => "other" };
                    list.Add(new BtDevice
                    {
                        Id = di.Id, Name = string.IsNullOrWhiteSpace(d.Name) ? di.Name : d.Name, Address = d.BluetoothAddress,
                        Connected = d.ConnectionStatus == BluetoothConnectionStatus.Connected, IsLe = true, Kind = kind,
                        Battery = Battery(di, batteries),
                    });
                }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }

    static int? Battery(DeviceInformation di, Dictionary<Guid, int> map)
    {
        if (di.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var c) && c is Guid g && map.TryGetValue(g, out var b))
            return b;
        return null;
    }

    /// <summary>Battery levels that Windows exposes on device nodes, keyed by container id.</summary>
    static async Task<Dictionary<Guid, int>> BatteriesAsync()
    {
        var map = new Dictionary<Guid, int>();
        const string batteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        try
        {
            var all = await DeviceInformation.FindAllAsync("System.Devices.Present:=System.StructuredQueryType.Boolean#True",
                ["System.Devices.ContainerId", batteryKey], DeviceInformationKind.Device);
            foreach (var d in all)
            {
                if (d.Properties.TryGetValue(batteryKey, out var v) && v is byte b &&
                    d.Properties.TryGetValue("System.Devices.ContainerId", out var c) && c is Guid g)
                    map[g] = b;
            }
        }
        catch { }
        return map;
    }

    static readonly Guid[] AudioServices =
    [
        new("0000110b-0000-1000-8000-00805f9b34fb"), // A2DP sink
        new("0000111e-0000-1000-8000-00805f9b34fb"), // Hands-free
        new("0000110e-0000-1000-8000-00805f9b34fb"), // AVRCP
        new("00001108-0000-1000-8000-00805f9b34fb"), // Headset
    ];
    static readonly Guid[] InputServices = [new("00001124-0000-1000-8000-00805f9b34fb")];

    /// <summary>Connects (re-enables services) or disconnects a classic device. LE devices open Settings.</summary>
    public Task<bool> SetConnectedAsync(BtDevice dev, bool connect) => Task.Run(() =>
    {
        if (dev.IsLe) { Shell.Open("ms-settings:bluetooth"); return false; }
        var info = new Native.BLUETOOTH_DEVICE_INFO
        {
            dwSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.BLUETOOTH_DEVICE_INFO>(),
            Address = dev.Address, szName = "",
        };
        var services = dev.Kind == "input" ? InputServices : AudioServices;
        bool any = false;
        foreach (var s in services)
        {
            var g = s;
            if (connect)
            {
                Native.BluetoothSetServiceState(IntPtr.Zero, ref info, ref g, Native.BLUETOOTH_SERVICE_DISABLE);
                Thread.Sleep(150);
                any |= Native.BluetoothSetServiceState(IntPtr.Zero, ref info, ref g, Native.BLUETOOTH_SERVICE_ENABLE) == 0;
            }
            else any |= Native.BluetoothSetServiceState(IntPtr.Zero, ref info, ref g, Native.BLUETOOTH_SERVICE_DISABLE) == 0;
        }
        return any;
    });
}
