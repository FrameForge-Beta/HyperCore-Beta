using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FrameForge.Peripherals;

internal sealed record CameraDevice(int Index, string Name, string SymbolicLink);

internal sealed record CameraMode(int Width, int Height, double MaxFps)
{
    public string Label => MaxFps > 0 ? $"{Width} × {Height} ({MaxFps:0} fps)" : $"{Width} × {Height}";
}

/// <summary>
/// Captura de webcam ao vivo via Windows Media Foundation (sem pacotes NuGet externos).
/// Converte automaticamente YUY2/NV12/MJPG para BGR32 através do video processor da Media Foundation,
/// mede os FPS reais e desliga a câmara imediatamente ao parar.
/// </summary>
internal sealed unsafe class WebcamCapture : IDisposable
{
    // ------------------------------------------------------------------ Constantes & GUIDs (verificados no Windows SDK)

    private const uint MF_VERSION = (0x0002u << 16) | 0x0070u;
    private const uint MFSTARTUP_NOSOCKET = 0x1;
    private const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    private const uint MF_SOURCE_READERF_ERROR = 0x00000001;
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;
    private const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x00000020;

    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

    private static readonly Guid IID_IMFMediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");
    private static readonly Guid IID_IMF2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");

    // ------------------------------------------------------------------ P/Invoke Media Foundation & COM

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint dwFlags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    private static extern int MFCreateAttributes(out IntPtr ppMFAttributes, uint cInitialSize);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IntPtr ppMFType);

    [DllImport("mf.dll")]
    private static extern int MFEnumDeviceSources(IntPtr pAttributes, out IntPtr pppSourceActivate, out uint pcSourceActivate);

    [DllImport("mfreadwrite.dll")]
    private static extern int MFCreateSourceReaderFromMediaSource(IntPtr pMediaSource, IntPtr pAttributes, out IntPtr ppSourceReader);

    // ------------------------------------------------------------------ Helpers COM vtable (slots verificados no SDK)

    private static void Release(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        IntPtr cur = p;
        p = IntPtr.Zero;
        try
        {
            var vt = *(void***)cur;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vt[2])(cur);
        }
        catch { }
    }

    private static int QueryInterface(IntPtr p, Guid iid, out IntPtr ppv)
    {
        var vt = *(void***)p;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, out IntPtr, int>)vt[0])(p, &iid, out ppv);
    }

    private static int AttrGetUINT32(IntPtr attr, Guid key, out uint val)
    {
        var vt = *(void***)attr;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, out uint, int>)vt[7])(attr, &key, out val);
    }

    private static int AttrGetUINT64(IntPtr attr, Guid key, out ulong val)
    {
        var vt = *(void***)attr;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, out ulong, int>)vt[8])(attr, &key, out val);
    }

    private static string AttrGetString(IntPtr attr, Guid key)
    {
        var vt = *(void***)attr;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, out IntPtr, out uint, int>)vt[13])(attr, &key, out var strPtr, out _);
        if (hr < 0 || strPtr == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUni(strPtr) ?? ""; }
        finally { Marshal.FreeCoTaskMem(strPtr); }
    }

    private static int AttrSetUINT32(IntPtr attr, Guid key, uint val)
    {
        var vt = *(void***)attr;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)vt[21])(attr, &key, val);
    }

    private static int AttrSetUINT64(IntPtr attr, Guid key, ulong val)
    {
        var vt = *(void***)attr;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)vt[22])(attr, &key, val);
    }

    private static int AttrSetGUID(IntPtr attr, Guid key, Guid val)
    {
        var vt = *(void***)attr;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)vt[24])(attr, &key, &val);
    }

    private static int ActivateObject(IntPtr activate, Guid iid, out IntPtr ppv)
    {
        var vt = *(void***)activate;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, out IntPtr, int>)vt[33])(activate, &iid, out ppv);
    }

    private static int ShutdownObject(IntPtr activate)
    {
        var vt = *(void***)activate;
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)vt[34])(activate);
    }

    private static int ReaderGetNativeMediaType(IntPtr reader, uint stream, uint idx, out IntPtr mt)
    {
        var vt = *(void***)reader;
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, out IntPtr, int>)vt[5])(reader, stream, idx, out mt);
    }

    private static int ReaderGetCurrentMediaType(IntPtr reader, uint stream, out IntPtr mt)
    {
        var vt = *(void***)reader;
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)vt[6])(reader, stream, out mt);
    }

    private static int ReaderSetCurrentMediaType(IntPtr reader, uint stream, IntPtr mt)
    {
        var vt = *(void***)reader;
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)vt[7])(reader, stream, IntPtr.Zero, mt);
    }

    private static int ReaderReadSample(IntPtr reader, uint stream, out uint actualStream, out uint flags, out long timestamp, out IntPtr sample)
    {
        var vt = *(void***)reader;
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, out uint, out uint, out long, out IntPtr, int>)vt[9])(
            reader, stream, 0, out actualStream, out flags, out timestamp, out sample);
    }

    private static int SampleConvertToContiguousBuffer(IntPtr sample, out IntPtr buf)
    {
        var vt = *(void***)sample;
        return ((delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)vt[41])(sample, out buf);
    }

    private static int BufferLock(IntPtr buf, out IntPtr data, out uint maxLen, out uint curLen)
    {
        var vt = *(void***)buf;
        return ((delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, out uint, out uint, int>)vt[3])(buf, out data, out maxLen, out curLen);
    }

    private static int BufferUnlock(IntPtr buf)
    {
        var vt = *(void***)buf;
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)vt[4])(buf);
    }

    private static int Buffer2DLock(IntPtr buf2d, out IntPtr scanline0, out int pitch)
    {
        var vt = *(void***)buf2d;
        return ((delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, out int, int>)vt[3])(buf2d, out scanline0, out pitch);
    }

    private static int Buffer2DUnlock(IntPtr buf2d)
    {
        var vt = *(void***)buf2d;
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)vt[4])(buf2d);
    }

    // ------------------------------------------------------------------ Enumeração de câmaras

    public static List<CameraDevice> ListCameras()
    {
        var result = new List<CameraDevice>();
        // Corre numa thread MTA curta para não interferir com o STA da janela WPF
        var t = new Thread(() =>
        {
            bool comOk = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */) >= 0;
            bool mfOk = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET) >= 0;
            IntPtr attrs = IntPtr.Zero;
            IntPtr devicesPtr = IntPtr.Zero;
            uint count = 0;
            try
            {
                if (!mfOk) return;
                if (MFCreateAttributes(out attrs, 1) < 0) return;
                if (AttrSetGUID(attrs, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID) < 0) return;
                if (MFEnumDeviceSources(attrs, out devicesPtr, out count) < 0 || devicesPtr == IntPtr.Zero) return;

                IntPtr* arr = (IntPtr*)devicesPtr;
                for (uint i = 0; i < count; i++)
                {
                    IntPtr act = arr[i];
                    if (act == IntPtr.Zero) continue;
                    string name = AttrGetString(act, MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                    string link = AttrGetString(act, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
                    if (string.IsNullOrWhiteSpace(name)) name = $"Câmara #{i + 1}";
                    result.Add(new CameraDevice((int)i, name.Trim(), link));
                }
            }
            catch { }
            finally
            {
                if (devicesPtr != IntPtr.Zero)
                {
                    IntPtr* arr = (IntPtr*)devicesPtr;
                    for (uint i = 0; i < count; i++) Release(ref arr[i]);
                    Marshal.FreeCoTaskMem(devicesPtr);
                }
                Release(ref attrs);
                if (mfOk) try { MFShutdown(); } catch { }
                if (comOk) try { CoUninitialize(); } catch { }
            }
        }) { IsBackground = true, Name = "HyperCore camera enum" };
        t.Start();
        t.Join(2500);
        return result;
    }

    // ------------------------------------------------------------------ Captura ao vivo

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private byte[] _frameBuf = Array.Empty<byte>();

    public bool Running => _thread != null;

    /// <summary>Chamado na thread de captura com uma cópia BGR32 do fotograma atual.</summary>
    public event Action<int, int, byte[], double, double>? FrameReady;

    /// <summary>Chamado quando os modos nativos da câmara são descobertos ao abrir.</summary>
    public event Action<List<CameraMode>>? ModesDiscovered;

    /// <summary>Chamado se não for possível abrir ou ler a câmara.</summary>
    public event Action<string>? ErrorOccurred;

    public void Start(string symbolicLink, int desiredWidth = 0, int desiredHeight = 0)
    {
        Stop();
        var cts = new CancellationTokenSource();
        var ct = cts.Token;

        var t = new Thread(() => RunCapture(symbolicLink, desiredWidth, desiredHeight, ct))
        {
            IsBackground = true,
            Name = "HyperCore webcam"
        };

        lock (_sync)
        {
            _cts = cts;
            _thread = t;
        }
        t.Start();
    }

    public void Stop()
    {
        Thread? t;
        lock (_sync)
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            t = _thread;
            _thread = null;
        }
        if (t != null && t != Thread.CurrentThread)
        {
            try { t.Join(1800); } catch { }
        }
    }

    public void Dispose() => Stop();

    private void RunCapture(string symbolicLink, int desiredW, int desiredH, CancellationToken ct)
    {
        bool comOk = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */) >= 0;
        bool mfOk = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET) >= 0;

        IntPtr enumAttrs = IntPtr.Zero;
        IntPtr devicesPtr = IntPtr.Zero;
        uint devCount = 0;
        IntPtr selectedActivate = IntPtr.Zero;
        IntPtr mediaSource = IntPtr.Zero;
        IntPtr readerAttrs = IntPtr.Zero;
        IntPtr reader = IntPtr.Zero;

        try
        {
            if (!mfOk)
            {
                ErrorOccurred?.Invoke("Não foi possível iniciar o subsistema de vídeo do Windows (Media Foundation).");
                return;
            }
            if (MFCreateAttributes(out enumAttrs, 1) < 0 ||
                AttrSetGUID(enumAttrs, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID) < 0 ||
                MFEnumDeviceSources(enumAttrs, out devicesPtr, out devCount) < 0 || devCount == 0)
            {
                ErrorOccurred?.Invoke("Nenhuma câmara ligada ou detetada pelo Windows.");
                return;
            }

            IntPtr* arr = (IntPtr*)devicesPtr;
            for (uint i = 0; i < devCount; i++)
            {
                string link = AttrGetString(arr[i], MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
                if (string.Equals(link, symbolicLink, StringComparison.OrdinalIgnoreCase))
                {
                    selectedActivate = arr[i];
                    arr[i] = IntPtr.Zero; // transfere posse
                    break;
                }
            }
            if (selectedActivate == IntPtr.Zero && devCount > 0)
            {
                selectedActivate = arr[0];
                arr[0] = IntPtr.Zero;
            }
            for (uint i = 0; i < devCount; i++) Release(ref arr[i]);
            Marshal.FreeCoTaskMem(devicesPtr);
            devicesPtr = IntPtr.Zero;

            if (selectedActivate == IntPtr.Zero)
            {
                ErrorOccurred?.Invoke("A câmara selecionada já não está disponível.");
                return;
            }

            int hrAct = ActivateObject(selectedActivate, IID_IMFMediaSource, out mediaSource);
            if (hrAct < 0 || mediaSource == IntPtr.Zero)
            {
                ErrorOccurred?.Invoke(DescribeError(hrAct));
                return;
            }

            if (MFCreateAttributes(out readerAttrs, 1) >= 0)
                AttrSetUINT32(readerAttrs, MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);

            int hrReader = MFCreateSourceReaderFromMediaSource(mediaSource, readerAttrs, out reader);
            if (hrReader < 0 || reader == IntPtr.Zero)
            {
                ErrorOccurred?.Invoke(DescribeError(hrReader));
                return;
            }

            // Descobre os modos (resoluções/FPS) suportados nativamente pela câmara
            var modes = ReadNativeModes(reader);
            if (modes.Count > 0)
            {
                try { ModesDiscovered?.Invoke(modes); } catch { }
            }

            // Escolhe a resolução pretendida (ou a melhor até 1280×720 em modo Auto)
            if (desiredW > 0 && desiredH > 0)
                TrySetOutputRgb32(reader, desiredW, desiredH);
            else
            {
                var best = PickDefaultMode(modes);
                if (best != null) TrySetOutputRgb32(reader, best.Width, best.Height);
                else TrySetOutputRgb32(reader, 0, 0);
            }

            // Lê a resolução e stride efetivamente negociados
            ReadCurrentFormat(reader, out int width, out int height, out int defaultStride);
            if (width <= 0 || height <= 0)
            {
                ErrorOccurred?.Invoke("A câmara não devolveu um formato de vídeo válido.");
                return;
            }

            var fpsWindow = new Queue<long>(64);
            var sw = Stopwatch.StartNew();
            int consecutiveEmpty = 0;

            while (!ct.IsCancellationRequested)
            {
                IntPtr sample = IntPtr.Zero;
                int hr = ReaderReadSample(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, out _, out uint flags, out _, out sample);
                if (hr < 0)
                {
                    if (!ct.IsCancellationRequested) ErrorOccurred?.Invoke(DescribeError(hr));
                    break;
                }
                if ((flags & MF_SOURCE_READERF_ERROR) != 0)
                {
                    if (!ct.IsCancellationRequested) ErrorOccurred?.Invoke("A câmara interrompeu o fluxo de imagem (pode ter sido desligada ou aberta noutra aplicação).");
                    break;
                }
                if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
                if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                    ReadCurrentFormat(reader, out width, out height, out defaultStride);

                if (sample == IntPtr.Zero)
                {
                    consecutiveEmpty++;
                    if (consecutiveEmpty > 40) Thread.Sleep(15);
                    continue;
                }
                consecutiveEmpty = 0;

                try
                {
                    if (ExtractBgr32(sample, width, height, defaultStride, out byte[] frame, out double avgLuma))
                    {
                        long now = sw.ElapsedTicks;
                        fpsWindow.Enqueue(now);
                        long cutoff = now - Stopwatch.Frequency; // janela de 1 s
                        while (fpsWindow.Count > 1 && fpsWindow.Peek() < cutoff) fpsWindow.Dequeue();
                        double fps = fpsWindow.Count > 1
                            ? (fpsWindow.Count - 1) * (double)Stopwatch.Frequency / Math.Max(1, now - fpsWindow.Peek())
                            : 0;

                        if (!ct.IsCancellationRequested)
                            FrameReady?.Invoke(width, height, frame, fps, avgLuma);
                    }
                }
                finally
                {
                    Release(ref sample);
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) ErrorOccurred?.Invoke($"Erro ao ler a câmara: {ex.Message}");
        }
        finally
        {
            Release(ref reader);
            Release(ref readerAttrs);
            Release(ref mediaSource);
            if (selectedActivate != IntPtr.Zero)
            {
                try { ShutdownObject(selectedActivate); } catch { }
                Release(ref selectedActivate);
            }
            if (devicesPtr != IntPtr.Zero)
            {
                IntPtr* arr = (IntPtr*)devicesPtr;
                for (uint i = 0; i < devCount; i++) Release(ref arr[i]);
                Marshal.FreeCoTaskMem(devicesPtr);
            }
            Release(ref enumAttrs);
            if (mfOk) try { MFShutdown(); } catch { }
            if (comOk) try { CoUninitialize(); } catch { }

            lock (_sync)
            {
                if (_thread == Thread.CurrentThread) _thread = null;
            }
        }
    }

    private static List<CameraMode> ReadNativeModes(IntPtr reader)
    {
        var bestByRes = new Dictionary<(int W, int H), double>();
        for (uint idx = 0; idx < 256; idx++)
        {
            int hr = ReaderGetNativeMediaType(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, idx, out var mt);
            if (hr < 0 || mt == IntPtr.Zero) break;
            try
            {
                if (AttrGetUINT64(mt, MF_MT_FRAME_SIZE, out ulong size) >= 0)
                {
                    int w = (int)(size >> 32);
                    int h = (int)(size & 0xFFFFFFFF);
                    if (w >= 160 && h >= 120 && w <= 7680 && h <= 4320)
                    {
                        double fps = 0;
                        if (AttrGetUINT64(mt, MF_MT_FRAME_RATE, out ulong rate) >= 0)
                        {
                            uint num = (uint)(rate >> 32);
                            uint den = (uint)(rate & 0xFFFFFFFF);
                            if (den > 0) fps = (double)num / den;
                        }
                        if (!bestByRes.TryGetValue((w, h), out double old) || fps > old)
                            bestByRes[(w, h)] = fps;
                    }
                }
            }
            finally { Release(ref mt); }
        }
        return bestByRes
            .Select(kv => new CameraMode(kv.Key.W, kv.Key.H, kv.Value))
            .OrderByDescending(m => m.Width * m.Height)
            .ThenByDescending(m => m.MaxFps)
            .ToList();
    }

    private static CameraMode? PickDefaultMode(List<CameraMode> modes)
    {
        if (modes.Count == 0) return null;
        // Prefere 720p ou 1080p a >= 25 fps para pré-visualização rápida e nítida
        var preferred = modes.Where(m => m.Width <= 1280 && m.Height <= 720 && m.MaxFps >= 24).FirstOrDefault()
                     ?? modes.Where(m => m.Width <= 1920 && m.Height <= 1080 && m.MaxFps >= 24).FirstOrDefault()
                     ?? modes.First();
        return preferred;
    }

    private static void TrySetOutputRgb32(IntPtr reader, int w, int h)
    {
        IntPtr mt = IntPtr.Zero;
        try
        {
            if (MFCreateMediaType(out mt) < 0) return;
            AttrSetGUID(mt, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            AttrSetGUID(mt, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
            if (w > 0 && h > 0)
            {
                ulong packed = ((ulong)(uint)w << 32) | (uint)h;
                AttrSetUINT64(mt, MF_MT_FRAME_SIZE, packed);
                if (ReaderSetCurrentMediaType(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, mt) >= 0) return;
            }
            // Fallback: só pedir RGB32 mantendo a resolução padrão da câmara
            Release(ref mt);
            if (MFCreateMediaType(out mt) < 0) return;
            AttrSetGUID(mt, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            AttrSetGUID(mt, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
            ReaderSetCurrentMediaType(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, mt);
        }
        finally { Release(ref mt); }
    }

    private static void ReadCurrentFormat(IntPtr reader, out int width, out int height, out int defaultStride)
    {
        width = 0; height = 0; defaultStride = 0;
        if (ReaderGetCurrentMediaType(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var mt) < 0 || mt == IntPtr.Zero) return;
        try
        {
            if (AttrGetUINT64(mt, MF_MT_FRAME_SIZE, out ulong size) >= 0)
            {
                width = (int)(size >> 32);
                height = (int)(size & 0xFFFFFFFF);
            }
            if (AttrGetUINT32(mt, MF_MT_DEFAULT_STRIDE, out uint strideRaw) >= 0)
                defaultStride = unchecked((int)strideRaw);
            if (defaultStride == 0 && width > 0) defaultStride = width * 4;
        }
        finally { Release(ref mt); }
    }

    private bool ExtractBgr32(IntPtr sample, int width, int height, int defaultStride, out byte[] output, out double avgLuma)
    {
        output = Array.Empty<byte>();
        avgLuma = 0;
        if (width <= 0 || height <= 0) return false;

        int dstStride = width * 4;
        int needed = dstStride * height;
        if (_frameBuf.Length != needed) _frameBuf = new byte[needed];

        if (SampleConvertToContiguousBuffer(sample, out var buf) < 0 || buf == IntPtr.Zero) return false;
        IntPtr buf2d = IntPtr.Zero;
        try
        {
            IntPtr scan0 = IntPtr.Zero;
            int pitch = 0;
            bool locked2d = false;
            bool locked1d = false;

            if (QueryInterface(buf, IID_IMF2DBuffer, out buf2d) >= 0 && buf2d != IntPtr.Zero &&
                Buffer2DLock(buf2d, out scan0, out pitch) >= 0 && scan0 != IntPtr.Zero)
            {
                locked2d = true;
            }
            else if (BufferLock(buf, out var data, out _, out uint curLen) >= 0 && data != IntPtr.Zero && curLen >= (uint)Math.Abs(defaultStride) * (uint)height)
            {
                locked1d = true;
                pitch = defaultStride != 0 ? defaultStride : dstStride;
                scan0 = pitch >= 0 ? data : IntPtr.Add(data, (height - 1) * (-pitch));
            }

            if (!locked2d && !locked1d) return false;

            try
            {
                long lumaSum = 0;
                int lumaCount = 0;
                fixed (byte* dstBase = _frameBuf)
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* srcRow = (byte*)scan0 + (long)y * pitch;
                        byte* dstRow = dstBase + y * dstStride;
                        Buffer.MemoryCopy(srcRow, dstRow, dstStride, dstStride);
                        // Amostra leve de brilho (cada 8 linhas e 8 píxeis) para detetar tampa fechada / pouca luz
                        if ((y & 7) == 0)
                        {
                            for (int x = 0; x < width; x += 8)
                            {
                                int o = x * 4;
                                lumaSum += (dstRow[o] * 29 + dstRow[o + 1] * 150 + dstRow[o + 2] * 77) >> 8;
                                lumaCount++;
                            }
                        }
                    }
                }
                avgLuma = lumaCount > 0 ? (double)lumaSum / lumaCount : 0;
                // Faz cópia imutável rápida para entregar ao UI thread sem corrida
                output = new byte[needed];
                Buffer.BlockCopy(_frameBuf, 0, output, 0, needed);
                return true;
            }
            finally
            {
                if (locked2d) Buffer2DUnlock(buf2d);
                if (locked1d) BufferUnlock(buf);
            }
        }
        finally
        {
            Release(ref buf2d);
            Release(ref buf);
        }
    }

    private static string DescribeError(int hr) => unchecked((uint)hr) switch
    {
        0x80070005 => "Acesso à câmara bloqueado pelas definições de privacidade do Windows.",
        0xC00D3704 or 0x80070020 or 0xC00D3EA2 => "A câmara está a ser usada por outra aplicação (Discord, OBS, Zoom, navegador…).",
        0xC00D36B4 => "A câmara não suporta conversão de vídeo compatível.",
        _ => $"Não foi possível abrir a câmara (código 0x{hr:X8}). Verifica se não está a ser usada noutra aplicação."
    };
}
