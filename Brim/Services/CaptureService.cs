using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Security.Cryptography;
using Windows.Storage;
using WinRT;
using Drawing = System.Drawing;

namespace Brim.Services;

/// <summary>Screenshots (full screen or area) and screen recordings with system sound and microphone.</summary>
public sealed class CaptureService
{
    public static string ShotsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    public static string VideoDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Screen Recordings");

    Recorder? _rec;
    public bool Recording => _rec != null;
    public DateTime RecordStart { get; private set; }
    public event Action? Changed;

    /// <summary>Grabs a rectangle of the screen in physical pixels.</summary>
    public static Drawing.Bitmap Grab(Drawing.Rectangle r)
    {
        var bmp = new Drawing.Bitmap(r.Width, r.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, Drawing.CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>Saves a bitmap to Pictures\Screenshots and puts it on the clipboard.</summary>
    public static string Save(Drawing.Bitmap bmp)
    {
        Directory.CreateDirectory(ShotsDir);
        var path = Path.Combine(ShotsDir, "Screenshot " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".png");
        bmp.Save(path, Drawing.Imaging.ImageFormat.Png);
        try
        {
            var h = bmp.GetHbitmap();
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                App.Clip.CopyImageSilently(src);
            }
            finally { DeleteObject(h); }
        }
        catch { }
        return path;
    }

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    public async Task<string?> StartRecordingAsync(Drawing.Rectangle monitorBounds, Drawing.Rectangle? area, bool systemSound, bool mic)
    {
        if (_rec != null) return null;
        Directory.CreateDirectory(VideoDir);
        var path = Path.Combine(VideoDir, "Recording " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".mp4");
        var rec = new Recorder();
        try
        {
            await rec.StartAsync(monitorBounds, area, path, systemSound, mic);
        }
        catch (Exception e)
        {
            rec.Dispose();
            App.Log("record start failed: " + e);
            return null;
        }
        _rec = rec;
        RecordStart = DateTime.Now;
        Changed?.Invoke();
        return path;
    }

    public async Task<string?> StopRecordingAsync()
    {
        var rec = _rec;
        if (rec == null) return null;
        _rec = null;
        Changed?.Invoke();
        try { return await rec.StopAsync(); }
        catch (Exception e) { App.Log("record stop failed: " + e); return null; }
        finally { rec.Dispose(); }
    }
}

/// <summary>Windows.Graphics.Capture → MediaStreamSource → MediaTranscoder (H.264/AAC MP4).</summary>
internal sealed class Recorder : IDisposable
{
    // ---- interop
    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels,
        uint sdk, out IntPtr device, out int level, out IntPtr context);
    [DllImport("d3d11.dll", ExactSpelling = true)] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr inspectable);
    [DllImport("d3d11.dll", ExactSpelling = true)] static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgi, out IntPtr inspectable);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Drawing.Point pt, uint flags);

    static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    static readonly Guid IID_IDXGISurface = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
    static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    static readonly Guid IID_DxgiAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    static readonly Guid IID_CaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid IID_D3D10Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    [StructLayout(LayoutKind.Sequential)]
    struct TexDesc { public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality; public int Usage; public uint BindFlags, CpuAccess, Misc; }
    [StructLayout(LayoutKind.Sequential)]
    struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

    IntPtr _dev, _ctx;
    IDirect3DDevice? _device;
    GraphicsCaptureItem? _item;
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    Box _box;
    int _w, _h;
    readonly object _lock = new();
    IDirect3DSurface? _latest, _last;
    TimeSpan _latestTs, _lastTs;
    TimeSpan? _firstFrame;
    readonly SemaphoreSlim _frameReady = new(0, 1);
    volatile bool _stopping;
    TaskCompletionSource? _done;
    string _path = "";
    readonly Stopwatch _clock = new();

    // audio
    WasapiCapture? _loop, _mic;
    int _rate = 48000;
    readonly object _alock = new();
    readonly Queue<float> _sysQ = new(), _micQ = new();
    long _audioFrames;
    bool _audio;

    public async Task StartAsync(Drawing.Rectangle monitor, Drawing.Rectangle? area, string path, bool systemSound, bool mic)
    {
        _path = path;
        CreateDevice();
        var hmon = MonitorFromPoint(new Drawing.Point(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2), 2);
        _item = CreateItem(hmon);
        var size = _item.Size;
        var crop = area ?? new Drawing.Rectangle(0, 0, size.Width, size.Height);
        crop.Intersect(new Drawing.Rectangle(0, 0, size.Width, size.Height));
        _w = Math.Max(2, crop.Width & ~1);
        _h = Math.Max(2, crop.Height & ~1);
        _box = new Box { Left = (uint)crop.Left, Top = (uint)crop.Top, Front = 0, Right = (uint)(crop.Left + _w), Bottom = (uint)(crop.Top + _h), Back = 1 };

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
        _pool.FrameArrived += OnFrame;
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = true; } catch { }
        try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless); _session.IsBorderRequired = false; } catch { }

        // audio sources
        if (systemSound) try { _loop = new WasapiLoopbackCapture(); } catch { _loop = null; }
        if (mic) try { _mic = new WasapiCapture(new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)); } catch { _mic = null; }
        _audio = _loop != null || _mic != null;
        if (_loop != null) _rate = _loop.WaveFormat.SampleRate;
        else if (_mic != null) _rate = _mic.WaveFormat.SampleRate;

        var vprops = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_w, (uint)_h);
        var vsd = new VideoStreamDescriptor(vprops);
        MediaStreamSource mss;
        if (_audio)
        {
            var asd = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm((uint)_rate, 2, 16));
            mss = new MediaStreamSource(vsd, asd);
        }
        else mss = new MediaStreamSource(vsd);
        mss.BufferTime = TimeSpan.Zero;
        mss.Starting += (s, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        mss.SampleRequested += OnSample;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)_w;
        profile.Video.Height = (uint)_h;
        profile.Video.Bitrate = (uint)Math.Clamp(_w * _h * 30 * 0.09, 4_000_000, 24_000_000);
        profile.Video.FrameRate.Numerator = 30;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Audio = _audio ? AudioEncodingProperties.CreateAac((uint)_rate, 2, 192000) : null;

        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.GenerateUniqueName);
        _path = file.Path;
        var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(mss, stream, profile);
        if (!prep.CanTranscode) throw new InvalidOperationException("Encoder unavailable: " + prep.FailureReason);

        if (_loop != null) { _loop.DataAvailable += (_, e) => Feed(_loop.WaveFormat, e, _sysQ); _loop.StartRecording(); }
        if (_mic != null) { _mic.DataAvailable += (_, e) => Feed(_mic.WaveFormat, e, _micQ); _mic.StartRecording(); }
        _session.StartCapture();

        _done = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            try { await prep.TranscodeAsync(); }
            catch (Exception e) { App.Log("transcode: " + e.Message); }
            finally { stream.Dispose(); _done.TrySetResult(); }
        });
    }

    public async Task<string> StopAsync()
    {
        _stopping = true;
        try { _session?.Dispose(); } catch { }
        try { _loop?.StopRecording(); _mic?.StopRecording(); } catch { }
        if (_frameReady.CurrentCount == 0) try { _frameReady.Release(); } catch { }
        if (_done != null) await Task.WhenAny(_done.Task, Task.Delay(15000));
        return _path;
    }

    // ---------------- video ----------------
    unsafe void CreateDevice()
    {
        int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out _dev, out _, out _ctx);
        if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, 5 /* WARP */, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out _dev, out _, out _ctx);
        Marshal.ThrowExceptionForHR(hr);
        if (Marshal.QueryInterface(_dev, in IID_D3D10Multithread, out var mt) >= 0)
        {
            var vt = *(IntPtr**)mt;
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)vt[5])(mt, 1);
            Marshal.Release(mt);
        }
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_dev, in IID_IDXGIDevice, out var dxgi));
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var insp));
        _device = MarshalInterface<IDirect3DDevice>.FromAbi(insp);
        Marshal.Release(insp);
        Marshal.Release(dxgi);
    }

    static unsafe GraphicsCaptureItem CreateItem(IntPtr hmon)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in IID_CaptureItemInterop, out var interop));
        try
        {
            var vt = *(IntPtr**)interop;
            var iid = IID_GraphicsCaptureItem;
            IntPtr itemPtr;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vt[4])(interop, hmon, &iid, &itemPtr));
            var item = GraphicsCaptureItem.FromAbi(itemPtr);
            Marshal.Release(itemPtr);
            return item;
        }
        finally { Marshal.Release(interop); }
    }

    void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        if (_stopping) return;
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame == null) return;
            _firstFrame ??= frame.SystemRelativeTime;
            if (!_clock.IsRunning) _clock.Start();
            var ts = frame.SystemRelativeTime - _firstFrame.Value;
            var copy = Copy(frame.Surface);
            lock (_lock)
            {
                DisposeSurface(_latest);
                _latest = copy;
                _latestTs = ts;
            }
            if (_frameReady.CurrentCount == 0) try { _frameReady.Release(); } catch (SemaphoreFullException) { }
        }
        catch (Exception e) { App.Log("frame: " + e.Message); }
    }

    unsafe IDirect3DSurface Copy(IDirect3DSurface src)
    {
        var srcAbi = MarshalInterface<IDirect3DSurface>.FromManaged(src);
        IntPtr access = IntPtr.Zero, srcTex = IntPtr.Zero, dst = IntPtr.Zero, dxgiSurf = IntPtr.Zero, insp = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(srcAbi, in IID_DxgiAccess, out access));
            var avt = *(IntPtr**)access;
            var texIid = IID_ID3D11Texture2D;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)avt[3])(access, &texIid, &srcTex));

            var desc = new TexDesc { Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, Usage = 0, BindFlags = 0x20 | 0x8 };
            var dvt = *(IntPtr**)_dev;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, TexDesc*, IntPtr, IntPtr*, int>)dvt[5])(_dev, &desc, IntPtr.Zero, &dst));
            var cvt = *(IntPtr**)_ctx;
            var box = _box;
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)cvt[46])(_ctx, dst, 0, 0, 0, 0, srcTex, 0, &box);

            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(dst, in IID_IDXGISurface, out dxgiSurf));
            Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurf, out insp));
            return MarshalInterface<IDirect3DSurface>.FromAbi(insp);
        }
        finally
        {
            foreach (var p in new[] { srcAbi, access, srcTex, dst, dxgiSurf, insp }) if (p != IntPtr.Zero) Marshal.Release(p);
        }
    }

    static void DisposeSurface(IDirect3DSurface? s) { try { (s as IDisposable)?.Dispose(); } catch { } }

    void OnSample(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var req = args.Request;
        var def = req.GetDeferral();
        bool video = req.StreamDescriptor is VideoStreamDescriptor;
        _ = Task.Run(async () =>
        {
            try { req.Sample = video ? await NextVideo() : await NextAudio(); }
            catch (Exception e) { App.Log("sample: " + e.Message); req.Sample = null; }
            finally { def.Complete(); }
        });
    }

    async Task<MediaStreamSample?> NextVideo()
    {
        while (!_stopping)
        {
            bool got = await _frameReady.WaitAsync(250);
            if (_stopping) break;
            lock (_lock)
            {
                if (got && _latest != null)
                {
                    _last = _latest;
                    _lastTs = _latestTs;
                    _latest = null;
                    return MediaStreamSample.CreateFromDirect3D11Surface(_last, _lastTs);
                }
                if (!got && _last != null)
                {
                    // screen is idle: repeat the previous frame so audio keeps flowing
                    _lastTs = _clock.Elapsed > _lastTs ? _clock.Elapsed : _lastTs + TimeSpan.FromMilliseconds(33);
                    return MediaStreamSample.CreateFromDirect3D11Surface(_last, _lastTs);
                }
            }
        }
        return null;
    }

    // ---------------- audio ----------------
    void Feed(WaveFormat wf, WaveInEventArgs e, Queue<float> q)
    {
        int ch = Math.Max(1, wf.Channels);
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat || (wf.Encoding == WaveFormatEncoding.Extensible && wf.BitsPerSample == 32);
        int bps = wf.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bps * ch);
        double ratio = (double)wf.SampleRate / _rate;
        lock (_alock)
        {
            for (double f = 0; f < frames; f += ratio)
            {
                int fi = (int)f;
                float l = Read(e.Buffer, (fi * ch) * bps, isFloat, bps);
                float r = ch > 1 ? Read(e.Buffer, (fi * ch + 1) * bps, isFloat, bps) : l;
                q.Enqueue(l);
                q.Enqueue(r);
            }
            while (q.Count > _rate * 2 * 4) q.Dequeue();
        }
    }

    static float Read(byte[] b, int o, bool isFloat, int bps) =>
        isFloat ? BitConverter.ToSingle(b, o) : bps == 2 ? BitConverter.ToInt16(b, o) / 32768f : bps == 3 ? ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f : BitConverter.ToInt32(b, o) / 2147483648f;

    async Task<MediaStreamSample?> NextAudio()
    {
        int chunk = _rate / 50;
        while (!_stopping)
        {
            var audioTime = TimeSpan.FromSeconds((double)_audioFrames / _rate);
            var wall = _clock.IsRunning ? _clock.Elapsed : TimeSpan.Zero;
            int have;
            lock (_alock) have = Math.Max(_sysQ.Count, _micQ.Count) / 2;
            if (!_clock.IsRunning || (have < chunk && wall - audioTime < TimeSpan.FromMilliseconds(120)))
            {
                await Task.Delay(8);
                continue;
            }
            var pcm = new byte[chunk * 4];
            lock (_alock)
            {
                for (int i = 0; i < chunk * 2; i++)
                {
                    float s = 0;
                    if (_sysQ.Count > 0) s += _sysQ.Dequeue();
                    if (_micQ.Count > 0) s += _micQ.Dequeue();
                    short v = (short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue);
                    pcm[i * 2] = (byte)v;
                    pcm[i * 2 + 1] = (byte)(v >> 8);
                }
            }
            var sample = MediaStreamSample.CreateFromBuffer(CryptographicBuffer.CreateFromByteArray(pcm), audioTime);
            sample.Duration = TimeSpan.FromSeconds((double)chunk / _rate);
            _audioFrames += chunk;
            return sample;
        }
        return null;
    }

    public void Dispose()
    {
        _stopping = true;
        try { _pool?.Dispose(); } catch { }
        try { _loop?.Dispose(); _mic?.Dispose(); } catch { }
        lock (_lock) { DisposeSurface(_latest); _latest = null; }
        if (_ctx != IntPtr.Zero) { Marshal.Release(_ctx); _ctx = IntPtr.Zero; }
        if (_dev != IntPtr.Zero) { Marshal.Release(_dev); _dev = IntPtr.Zero; }
    }
}
