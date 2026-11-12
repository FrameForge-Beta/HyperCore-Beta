using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FrameForge.Telemetry;

/// <summary>
/// Mede os frames de qualquer jogo como o PresentMon: escuta, via ETW, os eventos "Present" que o próprio Windows emite
/// quando um programa entrega um frame (DXGI = DirectX 10/11/12, e Direct3D 9).
///
/// PORQUÊ É SEGURO COM ANTI-CHEAT: não injeta DLL, não abre o processo do jogo, não lê a memória dele e não faz hooks.
/// É o mesmo mecanismo do PresentMon/CapFrameX/FrameView. O RivaTuner (RTSS), pelo contrário, injeta uma DLL no jogo
/// para desenhar o overlay — é exatamente isso que o Vanguard (Valorant) bloqueia.
///
/// Limitação: jogos Vulkan/OpenGL só aparecem se apresentarem por DXGI (comum em drivers recentes, não garantido).
/// </summary>
public sealed class FrameCapture : IDisposable
{
    private const string SessionName = "FrameForge-Present";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42, DxgiPresentStop = 43, D3D9PresentStart = 1, D3D9PresentStop = 2;
    private const double LiveWindowMs = 10_000;

    private TraceEventSession? _session;
    private Thread? _thread;
    private readonly object _lock = new();
    // (pid, swapchain) → instantes (ms) dos últimos 10 s. Um jogo pode ter mais de uma swapchain (FiveM, launchers com UI própria):
    // só a principal — a que entrega mais frames — conta para o FPS, senão os FPS aparecem a dobrar.
    private readonly Dictionary<(int Pid, ulong Chain), Queue<double>> _live = new();
    private readonly Dictionary<(int Pid, ulong Chain), long> _lastSeen = new();
    private readonly Dictionary<int, ulong> _main = new();   // pid → swapchain principal
    private long _mainCheck;
    // Present dentro de Present (ReShade, mods gráficos, wrappers do DXGI) gera dois eventos por frame: só conta o de fora
    private readonly Dictionary<int, (int Depth, double Since)> _nest = new();

    // gravação de sessão (um processo de cada vez)
    private int _recPid;
    private DateTime _recStart;
    private List<double>? _recTimes;

    public string? Error { get; private set; }
    public bool Running => _thread?.IsAlive == true;

    public void Start()
    {
        if (Running) return;
        try
        {
            _session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create) { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue);
            _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational, ulong.MaxValue);
            _session.Source.AllEvents += OnEvent;
            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Error = ex.Message; }
            }) { IsBackground = true, Name = "ETW Present" };
            _thread.Start();
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _session?.Dispose();
            _session = null;
        }
    }

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        bool dxgi = e.ProviderGuid == DxgiProvider, d3d9 = e.ProviderGuid == D3D9Provider;
        bool start = (dxgi && id == DxgiPresentStart) || (d3d9 && id == D3D9PresentStart);
        bool stop = (dxgi && id == DxgiPresentStop) || (d3d9 && id == D3D9PresentStop);
        if (!start && !stop) return;

        DateTime ts = e.TimeStamp;
        double t = ts.Ticks / (double)TimeSpan.TicksPerMillisecond;
        lock (_lock)
        {
            _nest.TryGetValue(e.ThreadID, out var n);
            if (stop) { if (n.Depth > 0) _nest[e.ThreadID] = (n.Depth - 1, n.Since); return; }
            // um Stop perdido não pode bloquear a contagem para sempre: um Present nunca dura 250 ms
            if (n.Depth > 0 && t - n.Since > 250) n.Depth = 0;
            _nest[e.ThreadID] = (n.Depth + 1, n.Depth == 0 ? t : n.Since);
            if (n.Depth > 0) return;

            var key = (e.ProcessID, SwapChain(e, dxgi));
            if (!_live.TryGetValue(key, out var q)) _live[key] = q = new Queue<double>();
            q.Enqueue(t);
            while (q.Count > 0 && q.Peek() < t - LiveWindowMs) q.Dequeue();
            _lastSeen[key] = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_mainCheck).TotalMilliseconds > 500) PickMainChains();

            if (_recTimes != null && e.ProcessID == _recPid && ts >= _recStart && IsMain(key))
                _recTimes.Add((ts - _recStart).TotalMilliseconds);
        }
    }

    private static ulong SwapChain(TraceEvent e, bool dxgi)
    {
        try { return Convert.ToUInt64(e.PayloadByName(dxgi ? "pIDXGISwapChain" : "pSwapchain") ?? 0UL); }
        catch { return 0; }
    }

    private bool IsMain((int Pid, ulong Chain) key) => !_main.TryGetValue(key.Pid, out var c) || c == key.Chain;

    /// <summary>Por processo, a swapchain principal é a que entregou mais frames nos últimos 10 s. Limpa as que morreram.</summary>
    private void PickMainChains()
    {
        _mainCheck = Stopwatch.GetTimestamp();
        foreach (var k in _lastSeen.Where(x => Stopwatch.GetElapsedTime(x.Value).TotalSeconds > 30).Select(x => x.Key).ToList())
        { _live.Remove(k); _lastSeen.Remove(k); }
        _main.Clear();
        foreach (var g in _live.Where(x => Stopwatch.GetElapsedTime(_lastSeen[x.Key]).TotalSeconds <= 2).GroupBy(x => x.Key.Pid))
            _main[g.Key] = g.MaxBy(x => x.Value.Count).Key.Chain;
        if (_nest.Count > 512) _nest.Clear();
    }

    /// <summary>Fila de instantes da swapchain principal do processo (chamar com o lock).</summary>
    private Queue<double>? MainQueue(int pid, out long lastSeen)
    {
        lastSeen = 0;
        if (!_main.TryGetValue(pid, out var chain))
        {
            var any = _live.Keys.Where(k => k.Pid == pid).ToList();
            if (any.Count == 0) return null;
            chain = any.MaxBy(k => _live[k].Count).Chain;
        }
        if (!_live.TryGetValue((pid, chain), out var q)) return null;
        lastSeen = _lastSeen[(pid, chain)];
        return q;
    }

    /// <summary>Começa a guardar todos os frames do processo (instantes em ms desde <paramref name="start"/>).</summary>
    public void BeginRecording(int pid, DateTime start)
    {
        lock (_lock) { _recPid = pid; _recStart = start; _recTimes = new List<double>(200_000); }
    }

    /// <summary>Troca o processo gravado (jogos que reabrem com outro PID) sem perder o que já foi gravado.</summary>
    public void RetargetRecording(int pid)
    {
        lock (_lock) _recPid = pid;
    }

    public List<double> EndRecording()
    {
        lock (_lock)
        {
            var r = _recTimes ?? new List<double>();
            _recTimes = null;
            r.Sort();
            return r;
        }
    }

    /// <summary>PIDs que entregaram frames nos últimos 2 s, com o FPS atual.</summary>
    public List<(int Pid, double Fps)> ActivePresenters()
    {
        var list = new List<(int, double)>();
        lock (_lock)
        {
            foreach (int pid in _live.Keys.Select(k => k.Pid).Distinct().ToList())
            {
                var q = MainQueue(pid, out long seen);
                if (q == null || Stopwatch.GetElapsedTime(seen).TotalSeconds > 2 || q.Count < 10) continue;
                var arr = q.ToArray();
                double span = arr[^1] - arr[0];
                list.Add((pid, span > 0 ? (arr.Length - 1) * 1000 / span : 0));
            }
        }
        return list.OrderByDescending(x => x.Item2).ToList();
    }

    public sealed record LiveStats(double Fps, double FrameTimeMs, double Low1Fps, double[] Recent);

    /// <summary>Estatísticas ao vivo (overlay): FPS do último segundo, 1% low dos últimos 10 s e os últimos 120 frametimes.</summary>
    public LiveStats? Live(int pid)
    {
        double[] ts;
        lock (_lock)
        {
            var q = MainQueue(pid, out long seen);
            if (q == null || q.Count < 3) return null;
            if (Stopwatch.GetElapsedTime(seen).TotalSeconds > 2) return null;
            ts = q.ToArray();
        }
        var ft = new double[ts.Length - 1];
        for (int i = 1; i < ts.Length; i++) ft[i - 1] = ts[i] - ts[i - 1];
        double last = ts[^1];
        int first = Array.FindIndex(ts, x => x >= last - 1000);
        double span = last - ts[first];
        double fps = span > 0 ? (ts.Length - 1 - first) * 1000 / span : 0;
        var sorted = (double[])ft.Clone();
        Array.Sort(sorted);
        double p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
        return new LiveStats(fps, fps > 0 ? 1000 / fps : 0, p99 > 0 ? 1000 / p99 : 0, ft.Skip(Math.Max(0, ft.Length - 120)).ToArray());
    }

    public void Dispose()
    {
        try { _session?.Source.StopProcessing(); } catch { }
        _session?.Dispose();
        _session = null;
    }
}
