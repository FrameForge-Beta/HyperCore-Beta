using System.Runtime.InteropServices;

namespace FrameForge.Peripherals;

internal sealed record AudioOutputDevice(int DeviceId, string Name);

internal enum AudioChannelMode
{
    Left,
    Right,
    Both,
    CustomBalance
}

internal enum AudioSoundStyle
{
    Chime,
    Tone,
    PinkNoise
}

/// <summary>
/// Sintetizador e reprodutor estéreo puro via WinMM (waveOut): sem dependências externas.
/// Permite isolar totalmente o canal esquerdo, o canal direito, tocar ambos em simultâneo,
/// ajustar equilíbrio contínuo (L ↔ R) e testar frequências (graves, médios e agudos).
/// </summary>
internal sealed class AudioTester : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BitsPerSample = 16;
    private const uint WAVE_MAPPER = unchecked((uint)-1);
    private const uint WHDR_DONE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveOutGetDevCapsW(UIntPtr uDeviceID, out WAVEOUTCAPS pwoc, uint cbwoc);

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(IntPtr hwo, ref WAVEHDR pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(IntPtr hwo, ref WAVEHDR pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(IntPtr hwo, ref WAVEHDR pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(IntPtr hwo);

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private IntPtr _hwo;

    private volatile float _liveLeft;
    private volatile float _liveRight;
    private volatile bool _isPlaying;

    public bool IsPlaying => _isPlaying;
    public double LiveLeft => _liveLeft;
    public double LiveRight => _liveRight;

    public static List<AudioOutputDevice> ListOutputs()
    {
        var list = new List<AudioOutputDevice> { new(-1, "Dispositivo padrão do Windows") };
        try
        {
            uint count = waveOutGetNumDevs();
            for (uint i = 0; i < count; i++)
            {
                if (waveOutGetDevCapsW((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<WAVEOUTCAPS>()) == 0 &&
                    !string.IsNullOrWhiteSpace(caps.szPname))
                {
                    list.Add(new AudioOutputDevice((int)i, caps.szPname.Trim()));
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>Reproduz um teste de canal (esquerda, direita ou ambos) com duração fixa.</summary>
    public bool PlayChannel(int deviceId, AudioChannelMode mode, AudioSoundStyle style, double volume, double durationSec = 1.6, double pan = 0, Action? onFinished = null)
    {
        Stop();
        volume = Math.Clamp(volume, 0.05, 1.0);
        (double lg, double rg) = ChannelGains(mode, pan);
        short[] pcm = Synthesize(style, durationSec, volume, lg, rg, freqHz: 440);
        return StartPlayback(deviceId, pcm, lg, rg, loop: false, onFinished);
    }

    /// <summary>Reproduz uma frequência específica em ambos os ouvidos (ex.: 60 Hz graves, 1 kHz médios, 10 kHz agudos).</summary>
    public bool PlayFrequency(int deviceId, double freqHz, double volume, double durationSec = 1.8, Action? onFinished = null)
    {
        Stop();
        volume = Math.Clamp(volume, 0.05, 1.0);
        // Compensa ligeiramente agudos muito altos para conforto auditivo
        double safeVol = freqHz >= 6000 ? volume * 0.55 : freqHz >= 3000 ? volume * 0.72 : volume * 0.85;
        short[] pcm = Synthesize(AudioSoundStyle.Tone, durationSec, safeVol, 1.0, 1.0, freqHz);
        return StartPlayback(deviceId, pcm, 1.0, 1.0, loop: false, onFinished);
    }

    /// <summary>Reproduz som contínuo em loop com controlo de equilíbrio dinâmico (slider L ↔ R).</summary>
    public bool StartBalanceLoop(int deviceId, Func<double> getPan, Func<double> getVolume)
    {
        Stop();
        var cts = new CancellationTokenSource();
        var ct = cts.Token;
        IntPtr hwo = OpenDevice(deviceId);
        if (hwo == IntPtr.Zero) return false;

        lock (_sync)
        {
            _cts = cts;
            _hwo = hwo;
            _isPlaying = true;
        }

        _thread = new Thread(() =>
        {
            // Streaming com 2 buffers curtos (~80 ms) para responder logo ao slider de balanço
            const int chunkFrames = SampleRate / 12; // ~83 ms
            short[][] bufs = { new short[chunkFrames * 2], new short[chunkFrames * 2] };
            GCHandle[] pins = { GCHandle.Alloc(bufs[0], GCHandleType.Pinned), GCHandle.Alloc(bufs[1], GCHandleType.Pinned) };
            WAVEHDR[] hdrs = new WAVEHDR[2];
            bool[] prepared = new bool[2];
            double phase = 0;

            try
            {
                for (int i = 0; i < 2; i++)
                {
                    FillToneChunk(bufs[i], ref phase, getPan(), getVolume());
                    hdrs[i] = new WAVEHDR { lpData = pins[i].AddrOfPinnedObject(), dwBufferLength = (uint)(bufs[i].Length * 2) };
                    if (waveOutPrepareHeader(hwo, ref hdrs[i], (uint)Marshal.SizeOf<WAVEHDR>()) != 0) break;
                    prepared[i] = true;
                    waveOutWrite(hwo, ref hdrs[i], (uint)Marshal.SizeOf<WAVEHDR>());
                }

                while (!ct.IsCancellationRequested)
                {
                    bool wroteAny = false;
                    for (int i = 0; i < 2 && !ct.IsCancellationRequested; i++)
                    {
                        if (!prepared[i] || (hdrs[i].dwFlags & WHDR_DONE) == 0) continue;
                        double pan = getPan();
                        double vol = getVolume();
                        FillToneChunk(bufs[i], ref phase, pan, vol);
                        waveOutWrite(hwo, ref hdrs[i], (uint)Marshal.SizeOf<WAVEHDR>());
                        wroteAny = true;
                    }
                    if (!wroteAny) Thread.Sleep(15);
                }
            }
            catch { }
            finally
            {
                try { waveOutReset(hwo); } catch { }
                for (int i = 0; i < 2; i++)
                {
                    if (prepared[i]) try { waveOutUnprepareHeader(hwo, ref hdrs[i], (uint)Marshal.SizeOf<WAVEHDR>()); } catch { }
                    if (pins[i].IsAllocated) pins[i].Free();
                }
                try { waveOutClose(hwo); } catch { }
                lock (_sync)
                {
                    if (_hwo == hwo) _hwo = IntPtr.Zero;
                    if (_cts == cts) _isPlaying = false;
                }
                _liveLeft = 0;
                _liveRight = 0;
            }
        }) { IsBackground = true, Name = "HyperCore audio balance" };
        _thread.Start();
        return true;
    }

    private void FillToneChunk(short[] dst, ref double phase, double pan, double volume)
    {
        volume = Math.Clamp(volume, 0.05, 1.0);
        (double lg, double rg) = ChannelGains(AudioChannelMode.CustomBalance, pan);
        double amp = 0.42 * volume;
        double inc = 2.0 * Math.PI * 330.0 / SampleRate;
        int frames = dst.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            double s = Math.Sin(phase) * 0.72 + Math.Sin(phase * 2.0) * 0.18 + Math.Sin(phase * 1.5) * 0.10;
            phase += inc;
            if (phase > 1e6) phase -= Math.Floor(phase / (2 * Math.PI)) * 2 * Math.PI;
            dst[i * 2] = ToSample(s * amp * lg);
            dst[i * 2 + 1] = ToSample(s * amp * rg);
        }
        _liveLeft = (float)(lg * volume);
        _liveRight = (float)(rg * volume);
    }

    private bool StartPlayback(int deviceId, short[] pcm, double lg, double rg, bool loop, Action? onFinished)
    {
        IntPtr hwo = OpenDevice(deviceId);
        if (hwo == IntPtr.Zero) return false;

        var cts = new CancellationTokenSource();
        var ct = cts.Token;

        lock (_sync)
        {
            _cts = cts;
            _hwo = hwo;
            _isPlaying = true;
        }

        _thread = new Thread(() =>
        {
            GCHandle pin = GCHandle.Alloc(pcm, GCHandleType.Pinned);
            var hdr = new WAVEHDR
            {
                lpData = pin.AddrOfPinnedObject(),
                dwBufferLength = (uint)(pcm.Length * sizeof(short))
            };
            bool prepared = false;
            int totalFrames = pcm.Length / 2;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (waveOutPrepareHeader(hwo, ref hdr, (uint)Marshal.SizeOf<WAVEHDR>()) == 0)
                {
                    prepared = true;
                    if (waveOutWrite(hwo, ref hdr, (uint)Marshal.SizeOf<WAVEHDR>()) == 0)
                    {
                        while (!ct.IsCancellationRequested && (hdr.dwFlags & WHDR_DONE) == 0)
                        {
                            int curFrame = Math.Clamp((int)(sw.Elapsed.TotalSeconds * SampleRate), 0, Math.Max(0, totalFrames - 1));
                            // Calcula RMS curto (~512 frames) para os medidores visuais E/D
                            double sumL = 0, sumR = 0;
                            int end = Math.Min(totalFrames, curFrame + 512);
                            int n = Math.Max(1, end - curFrame);
                            for (int f = curFrame; f < end; f++)
                            {
                                double l = pcm[f * 2] / 32768.0;
                                double r = pcm[f * 2 + 1] / 32768.0;
                                sumL += l * l;
                                sumR += r * r;
                            }
                            _liveLeft = (float)Math.Min(1.0, Math.Sqrt(sumL / n) * 2.4 * (lg > 0 ? 1 : 0));
                            _liveRight = (float)Math.Min(1.0, Math.Sqrt(sumR / n) * 2.4 * (rg > 0 ? 1 : 0));
                            Thread.Sleep(22);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                try { waveOutReset(hwo); } catch { }
                if (prepared) try { waveOutUnprepareHeader(hwo, ref hdr, (uint)Marshal.SizeOf<WAVEHDR>()); } catch { }
                try { waveOutClose(hwo); } catch { }
                if (pin.IsAllocated) pin.Free();

                bool finishedNormally = !ct.IsCancellationRequested;
                lock (_sync)
                {
                    if (_hwo == hwo) _hwo = IntPtr.Zero;
                    if (_cts == cts) _isPlaying = false;
                }
                _liveLeft = 0;
                _liveRight = 0;
                if (finishedNormally)
                {
                    try { onFinished?.Invoke(); } catch { }
                }
            }
        }) { IsBackground = true, Name = "HyperCore audio test" };
        _thread.Start();
        return true;
    }

    public void Stop()
    {
        Thread? t;
        IntPtr hwo;
        lock (_sync)
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            t = _thread;
            _thread = null;
            hwo = _hwo;
            _isPlaying = false;
        }
        if (hwo != IntPtr.Zero)
        {
            try { waveOutReset(hwo); } catch { }
        }
        if (t != null && t != Thread.CurrentThread)
        {
            try { t.Join(350); } catch { }
        }
        _liveLeft = 0;
        _liveRight = 0;
    }

    public void Dispose() => Stop();

    private static IntPtr OpenDevice(int deviceId)
    {
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1, // PCM
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = BitsPerSample,
            nBlockAlign = Channels * (BitsPerSample / 8),
            nAvgBytesPerSec = SampleRate * Channels * (BitsPerSample / 8),
            cbSize = 0
        };
        uint id = deviceId < 0 ? WAVE_MAPPER : (uint)deviceId;
        return waveOutOpen(out var hwo, id, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) == 0 ? hwo : IntPtr.Zero;
    }

    private static (double L, double R) ChannelGains(AudioChannelMode mode, double pan) => mode switch
    {
        AudioChannelMode.Left => (1.0, 0.0),
        AudioChannelMode.Right => (0.0, 1.0),
        AudioChannelMode.Both => (1.0, 1.0),
        AudioChannelMode.CustomBalance => (
            Math.Cos((Math.Clamp(pan, -1, 1) + 1.0) * Math.PI / 4.0),
            Math.Sin((Math.Clamp(pan, -1, 1) + 1.0) * Math.PI / 4.0)),
        _ => (1.0, 1.0)
    };

    private static short[] Synthesize(AudioSoundStyle style, double durationSec, double volume, double leftGain, double rightGain, double freqHz)
    {
        int frames = Math.Max(1, (int)(SampleRate * durationSec));
        var pcm = new short[frames * 2];
        double fadeSamples = Math.Min(frames * 0.15, SampleRate * 0.03); // 30 ms anti-click

        if (style == AudioSoundStyle.PinkNoise)
        {
            // Filtro de Paul Kellet para ruído rosa suave (ótimo para comparar volume E vs D)
            var rng = new Random(1337);
            double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
            for (int i = 0; i < frames; i++)
            {
                double white = rng.NextDouble() * 2.0 - 1.0;
                b0 = 0.99886 * b0 + white * 0.0555179;
                b1 = 0.99332 * b1 + white * 0.0750759;
                b2 = 0.96900 * b2 + white * 0.1538520;
                b3 = 0.86650 * b3 + white * 0.3104856;
                b4 = 0.55000 * b4 + white * 0.5329522;
                b5 = -0.7616 * b5 - white * 0.0168980;
                double pink = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362) * 0.06;
                b6 = white * 0.115926;
                double env = EdgeEnvelope(i, frames, fadeSamples);
                double s = Math.Clamp(pink * env * volume, -0.95, 0.95);
                pcm[i * 2] = ToSample(s * leftGain);
                pcm[i * 2 + 1] = ToSample(s * rightGain);
            }
            return pcm;
        }

        if (style == AudioSoundStyle.Chime)
        {
            // Acorde suave de 3 notas agradáveis (Dó5 → Mi5 → Sol5) com harmónicos quentes
            double[] notes = { 523.25, 659.25, 783.99 };
            double noteStep = durationSec * 0.17;
            for (int i = 0; i < frames; i++)
            {
                double t = i / (double)SampleRate;
                double s = 0;
                for (int n = 0; n < notes.Length; n++)
                {
                    double nt = t - n * noteStep;
                    if (nt < 0) continue;
                    double attack = Math.Min(1.0, nt / 0.018);
                    double decay = Math.Exp(-nt * 2.4);
                    double f = notes[n];
                    s += attack * decay * (Math.Sin(2 * Math.PI * f * nt) * 0.7 + Math.Sin(2 * Math.PI * f * 2 * nt) * 0.2 + Math.Sin(2 * Math.PI * f * 0.5 * nt) * 0.15);
                }
                double env = EdgeEnvelope(i, frames, fadeSamples);
                double val = Math.Clamp(s * 0.32 * env * volume, -0.95, 0.95);
                pcm[i * 2] = ToSample(val * leftGain);
                pcm[i * 2 + 1] = ToSample(val * rightGain);
            }
            return pcm;
        }

        // Tom sinusoidal puro
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)SampleRate;
            double env = EdgeEnvelope(i, frames, fadeSamples);
            double s = Math.Sin(2.0 * Math.PI * freqHz * t) * 0.50 * env * volume;
            pcm[i * 2] = ToSample(s * leftGain);
            pcm[i * 2 + 1] = ToSample(s * rightGain);
        }
        return pcm;
    }

    private static double EdgeEnvelope(int i, int total, double fade)
    {
        if (fade <= 0) return 1.0;
        if (i < fade) return i / fade;
        int rem = total - 1 - i;
        if (rem < fade) return Math.Max(0, rem / fade);
        return 1.0;
    }

    private static short ToSample(double v) => (short)Math.Clamp(Math.Round(v * 32767.0), short.MinValue, short.MaxValue);
}
