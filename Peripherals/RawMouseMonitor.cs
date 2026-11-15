using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;
using static FrameForge.Peripherals.PeriphNative;

namespace FrameForge.Peripherals;

/// <summary>
/// Lê o rato por Raw Input numa thread própria (janela invisível + ciclo de mensagens), para medir a taxa de polling
/// sem carregar o UI thread. Cada movimento recebido fica com a hora exata (Stopwatch) num anel; a página lê-os com
/// <see cref="Drain"/>. Só leitura: não muda nada no Windows e desliga-se sozinho com <see cref="Stop"/>.
/// </summary>
internal sealed class RawMouseMonitor : IDisposable
{
    private const int Capacity = 1 << 16; // 65 536 eventos ≈ 8 s a 8000 Hz
    private readonly long[] _stamps = new long[Capacity];
    private readonly IntPtr[] _devices = new IntPtr[Capacity];
    private long _written;   // total escrito (só a thread do rato escreve)
    private long _read;      // total lido (só quem chama Drain lê)
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private HwndSource? _source;

    public bool Running => _thread != null;

    /// <summary>Verdadeiro se o registo do Raw Input falhou (o teste não pode medir).</summary>
    public bool Failed { get; private set; }

    public void Start()
    {
        if (_thread != null) return;
        Failed = false;
        Interlocked.Exchange(ref _read, Interlocked.Read(ref _written));
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                var p = new HwndSourceParameters("HyperCore.RawMouse") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) /* WS_POPUP, invisível */ };
                _source = new HwndSource(p);
                _source.AddHook(Hook);
                var dev = new[] { new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = _source.Handle } };
                if (!RegisterRawInputDevices(dev, 1, (uint)System.Runtime.InteropServices.Marshal.SizeOf<RAWINPUTDEVICE>())) Failed = true;
                _dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch { Failed = true; }
            finally { ready.Set(); }
            if (!Failed) Dispatcher.Run();
            try
            {
                var off = new[] { new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero } };
                RegisterRawInputDevices(off, 1, (uint)System.Runtime.InteropServices.Marshal.SizeOf<RAWINPUTDEVICE>());
                _source?.Dispose();
            }
            catch { }
        })
        { IsBackground = true, Name = "HyperCore raw mouse", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(2000);
        if (Failed) Stop();
    }

    public void Stop()
    {
        var t = _thread;
        if (t == null) return;
        _thread = null;
        try { _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send); } catch { }
        _dispatcher = null;
        t.Join(500);
    }

    public void Dispose() => Stop();

    private unsafe IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        long now = Stopwatch.GetTimestamp();
        byte* buf = stackalloc byte[64];
        uint size = 64;
        uint headerSize = (uint)sizeof(RAWINPUTHEADER);
        uint got = GetRawInputData(lParam, RID_INPUT, buf, ref size, headerSize);
        if (got == uint.MaxValue || got < headerSize + 20) return IntPtr.Zero;
        var header = (RAWINPUTHEADER*)buf;
        if (header->dwType != RIM_TYPEMOUSE) return IntPtr.Zero;
        byte* mouse = buf + headerSize;
        int dx = *(int*)(mouse + 12);
        int dy = *(int*)(mouse + 16);
        if (dx == 0 && dy == 0) return IntPtr.Zero; // só botões/roda: não contam para o polling
        long i = _written;
        _stamps[i & (Capacity - 1)] = now;
        _devices[i & (Capacity - 1)] = header->hDevice;
        Volatile.Write(ref _written, i + 1);
        return IntPtr.Zero; // DefWindowProc tem de ver o WM_INPUT
    }

    /// <summary>Copia os movimentos recebidos desde a última chamada (hora em ticks do Stopwatch e dispositivo).</summary>
    public int Drain(List<(long Ticks, IntPtr Device)> into)
    {
        long w = Volatile.Read(ref _written);
        long r = _read;
        if (w - r > Capacity) r = w - Capacity; // ficou para trás: perde os mais antigos
        int n = 0;
        for (; r < w; r++, n++) into.Add((_stamps[r & (Capacity - 1)], _devices[r & (Capacity - 1)]));
        _read = r;
        return n;
    }
}
