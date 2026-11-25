using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Brim.Services;

public sealed class AudioSessionInfo
{
    public required AudioSessionControl Control { get; init; }
    public required string Name { get; init; }
    public string? ExePath { get; init; }
    public int Pid { get; init; }
    public float Volume { get => Safe(() => Control.SimpleAudioVolume.Volume, 0f); set => Safe(() => Control.SimpleAudioVolume.Volume = value); }
    public bool Muted { get => Safe(() => Control.SimpleAudioVolume.Mute, false); set => Safe(() => Control.SimpleAudioVolume.Mute = value); }
    static T Safe<T>(Func<T> f, T d) { try { return f(); } catch { return d; } }
    static void Safe(Action a) { try { a(); } catch { } }
}

/// <summary>Master volume, output devices, per-app sessions and a loopback spectrum for visualisers.</summary>
public sealed class AudioService : IMMNotificationClient
{
    readonly MMDeviceEnumerator _en = new();
    MMDevice? _dev;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    /// <summary>Raised on the UI thread when the master volume or mute state changes from anywhere.</summary>
    public event Action? VolumeChanged;
    public event Action? DevicesChanged;

    public AudioService()
    {
        try { _en.RegisterEndpointNotificationCallback(this); } catch { }
        AttachDefault();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => { if (_capture != null && Environment.TickCount64 - _lastBandRequest > 4000) StopCapture(); };
        t.Start();
    }

    void AttachDefault()
    {
        try
        {
            if (_dev != null) _dev.AudioEndpointVolume.OnVolumeNotification -= OnVol;
            _dev = _en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _dev.AudioEndpointVolume.OnVolumeNotification += OnVol;
        }
        catch { _dev = null; }
    }

    void OnVol(AudioVolumeNotificationData d) => _ui.BeginInvoke(() => VolumeChanged?.Invoke());

    public float Volume
    {
        get { try { return _dev?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0; } catch { return 0; } }
        set { try { if (_dev != null) _dev.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0, 1); } catch { } }
    }

    public bool Muted
    {
        get { try { return _dev?.AudioEndpointVolume.Mute ?? false; } catch { return false; } }
        set { try { if (_dev != null) _dev.AudioEndpointVolume.Mute = value; } catch { } }
    }

    public string DefaultId { get { try { return _dev?.ID ?? ""; } catch { return ""; } } }
    public string DefaultName { get { try { return _dev?.FriendlyName ?? "No output"; } catch { return "No output"; } } }

    public List<(string Id, string Name)> Outputs()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var d in _en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add((d.ID, d.FriendlyName));
        }
        catch { }
        return list;
    }

    public void SetDefaultOutput(string id)
    {
        try
        {
            var pc = (IPolicyConfig)new CPolicyConfigClient();
            pc.SetDefaultEndpoint(id, 0);
            pc.SetDefaultEndpoint(id, 1);
            pc.SetDefaultEndpoint(id, 2);
        }
        catch { }
    }

    public List<AudioSessionInfo> Sessions()
    {
        var list = new List<AudioSessionInfo>();
        if (_dev == null) return list;
        try
        {
            var mgr = _dev.AudioSessionManager;
            mgr.RefreshSessions();
            var seen = new HashSet<int>();
            for (int i = 0; i < mgr.Sessions.Count; i++)
            {
                var s = mgr.Sessions[i];
                if (s.State == AudioSessionState.AudioSessionStateExpired) continue;
                int pid = (int)s.GetProcessID;
                string name;
                string? exe = null;
                if (s.IsSystemSoundsSession) name = "System sounds";
                else
                {
                    if (pid == 0 || !seen.Add(pid)) continue;
                    var info = ProcessInfo(pid);
                    if (info == null) continue;
                    exe = info.Value.exe;
                    name = !string.IsNullOrWhiteSpace(s.DisplayName) && !s.DisplayName.StartsWith('@') ? s.DisplayName : info.Value.name;
                }
                list.Add(new AudioSessionInfo { Control = s, Name = name, ExePath = exe, Pid = pid });
            }
        }
        catch { }
        return list.OrderBy(s => s.Name == "System sounds" ? 1 : 0).ThenBy(s => s.Name).ToList();
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);
    readonly Dictionary<int, (string? exe, string name)> _procCache = [];

    /// <summary>Exe path and friendly name for a process, cached (much faster than Process.MainModule).</summary>
    (string? exe, string name)? ProcessInfo(int pid)
    {
        if (_procCache.TryGetValue(pid, out var hit)) return hit;
        string? exe = null;
        var h = OpenProcess(0x1000, false, pid);
        if (h != IntPtr.Zero)
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size)) exe = sb.ToString();
            CloseHandle(h);
        }
        string name;
        try
        {
            var desc = exe != null ? FileVersionInfo.GetVersionInfo(exe).FileDescription : null;
            name = !string.IsNullOrWhiteSpace(desc) ? desc! : exe != null ? System.IO.Path.GetFileNameWithoutExtension(exe) : $"Process {pid}";
        }
        catch { name = exe != null ? System.IO.Path.GetFileNameWithoutExtension(exe) : $"Process {pid}"; }
        if (exe == null && name.StartsWith("Process ")) return null;
        _procCache[pid] = (exe, name);
        return (exe, name);
    }

    // ---------------- loopback spectrum ----------------
    WasapiLoopbackCapture? _capture;
    long _lastBandRequest;
    const int N = 1024;
    readonly float[] _ring = new float[N];
    int _ringPos, _sampleRate = 48000;
    readonly NAudio.Dsp.Complex[] _fft = new NAudio.Dsp.Complex[N];
    readonly double[] _raw = new double[48];
    readonly double[] _shown = new double[48];
    long _lastData;
    public double Level { get; private set; }

    /// <summary>Spectrum levels (0..1) resampled to <paramref name="count"/> bands. Starts capture on demand.</summary>
    public double[] Bands(int count)
    {
        _lastBandRequest = Environment.TickCount64;
        if (_capture == null) StartCapture();
        bool stale = Environment.TickCount64 - _lastData > 150;
        for (int i = 0; i < _shown.Length; i++)
        {
            double target = stale ? 0 : _raw[i];
            _shown[i] = target > _shown[i] ? _shown[i] + (target - _shown[i]) * 0.6 : _shown[i] * 0.82 + target * 0.18;
        }
        var res = new double[count];
        for (int i = 0; i < count; i++)
        {
            double pos = (double)i / Math.Max(1, count - 1) * (_shown.Length - 1);
            int a = (int)pos; int b = Math.Min(a + 1, _shown.Length - 1);
            res[i] = _shown[a] + (_shown[b] - _shown[a]) * (pos - a);
        }
        return res;
    }

    void StartCapture()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            _sampleRate = _capture.WaveFormat.SampleRate;
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, _) => { };
            _capture.StartRecording();
        }
        catch { _capture = null; }
    }

    void StopCapture()
    {
        try { _capture?.StopRecording(); _capture?.Dispose(); } catch { }
        _capture = null;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var cap = _capture;
        if (cap == null || e.BytesRecorded == 0) return;
        var wf = cap.WaveFormat;
        int ch = Math.Max(1, wf.Channels);
        if (wf.Encoding == WaveFormatEncoding.IeeeFloat || (wf.Encoding == WaveFormatEncoding.Extensible && wf.BitsPerSample == 32))
        {
            int frames = e.BytesRecorded / (4 * ch);
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++) sum += BitConverter.ToSingle(e.Buffer, (f * ch + c) * 4);
                Push(sum / ch);
            }
        }
        else if (wf.BitsPerSample == 16)
        {
            int frames = e.BytesRecorded / (2 * ch);
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++) sum += BitConverter.ToInt16(e.Buffer, (f * ch + c) * 2) / 32768f;
                Push(sum / ch);
            }
        }
    }

    void Push(float v)
    {
        _ring[_ringPos++] = v;
        if (_ringPos < N) return;
        _ringPos = 0;
        double rms = 0;
        for (int i = 0; i < N; i++)
        {
            double w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (N - 1)));
            _fft[i].X = (float)(_ring[i] * w);
            _fft[i].Y = 0;
            rms += _ring[i] * _ring[i];
        }
        Level = Math.Clamp(Math.Sqrt(rms / N) * 4, 0, 1);
        NAudio.Dsp.FastFourierTransform.FFT(true, 10, _fft);
        int bands = _raw.Length;
        for (int k = 0; k < bands; k++)
        {
            double f0 = 40 * Math.Pow(16000 / 40.0, (double)k / bands);
            double f1 = 40 * Math.Pow(16000 / 40.0, (double)(k + 1) / bands);
            int b0 = Math.Max(1, (int)(f0 * N / _sampleRate)), b1 = Math.Min(N / 2 - 1, Math.Max(b0, (int)(f1 * N / _sampleRate)));
            double mag = 0;
            for (int b = b0; b <= b1; b++)
                mag = Math.Max(mag, Math.Sqrt(_fft[b].X * _fft[b].X + _fft[b].Y * _fft[b].Y));
            double db = 20 * Math.Log10(mag + 1e-9);
            _raw[k] = Math.Clamp((db + 72) / 56, 0, 1);
        }
        _lastData = Environment.TickCount64;
    }

    // ---------------- device notifications ----------------
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _ui.BeginInvoke(() => DevicesChanged?.Invoke());
    public void OnDeviceAdded(string pwstrDeviceId) => _ui.BeginInvoke(() => DevicesChanged?.Invoke());
    public void OnDeviceRemoved(string deviceId) => _ui.BeginInvoke(() => DevicesChanged?.Invoke());
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render || role != Role.Multimedia) return;
        _ui.BeginInvoke(() =>
        {
            AttachDefault();
            if (_capture != null) { StopCapture(); StartCapture(); }
            DevicesChanged?.Invoke();
            VolumeChanged?.Invoke();
        });
    }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    // ---------------- undocumented default-device switch ----------------
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr fmt);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr fmt);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr a, IntPtr b);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fx, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fx, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class CPolicyConfigClient { }
}
