namespace FrameForge.Net;

/// <summary>Uma medição do monitor: latência até ao alvo e até ao router (null = sem resposta).</summary>
public readonly record struct NetSample(DateTime At, double? Ms, double? RouterMs, bool RouterTried);

/// <summary>Resumo de um conjunto de medições.</summary>
public sealed record NetStats(
    int Count, double? Last, double? Avg, double? Median, double? Min, double? Max, double Jitter, double LossPct, int Spikes,
    int RouterCount, double? RouterAvg, double RouterJitter, double RouterLossPct)
{
    public static readonly NetStats Empty = new(0, null, null, null, null, null, 0, 0, 0, 0, null, 0, 0);

    /// <summary>O router respondeu a pings (alguns routers ignoram-nos de propósito; aí não dá para separar casa/fornecedor).</summary>
    public bool RouterKnown => RouterCount >= 10 && RouterLossPct < 90;

    /// <summary>Picos: medições perdidas ou X ms acima da mediana (conta cada episódio uma vez).</summary>
    public static double SpikeThreshold(double median) => median + Math.Max(40, median * 0.8);

    public static NetStats From(IReadOnlyList<NetSample> s)
    {
        if (s.Count == 0) return Empty;
        var ok = s.Where(x => x.Ms != null).Select(x => x.Ms!.Value).ToList();
        double? median = ok.Count > 0 ? MedianOf(ok) : null;

        int spikes = 0;
        bool inSpike = false;
        if (median is double m)
        {
            double limit = SpikeThreshold(m);
            foreach (var x in s)
            {
                bool bad = x.Ms == null || x.Ms > limit;
                if (bad && !inSpike) spikes++;
                inSpike = bad;
            }
        }

        var routerTried = s.Where(x => x.RouterTried).ToList();
        var rOk = routerTried.Where(x => x.RouterMs != null).Select(x => x.RouterMs!.Value).ToList();

        return new NetStats(
            s.Count, s[^1].Ms, ok.Count > 0 ? ok.Average() : null, median, ok.Count > 0 ? ok.Min() : null, ok.Count > 0 ? ok.Max() : null,
            JitterOf(s.Select(x => x.Ms)), 100.0 * (s.Count - ok.Count) / s.Count, spikes,
            routerTried.Count, rOk.Count > 0 ? rOk.Average() : null, JitterOf(routerTried.Select(x => x.RouterMs)),
            routerTried.Count > 0 ? 100.0 * (routerTried.Count - rOk.Count) / routerTried.Count : 0);
    }

    /// <summary>Jitter = variação média entre medições seguidas (como no RFC 3550, sem suavização).</summary>
    public static double JitterOf(IEnumerable<double?> values)
    {
        double sum = 0; int n = 0; double? prev = null;
        foreach (var v in values)
        {
            if (v is not double d) continue;
            if (prev is double p) { sum += Math.Abs(d - p); n++; }
            prev = d;
        }
        return n > 0 ? sum / n : 0;
    }

    public static double MedianOf(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}

/// <summary>
/// Monitor contínuo de ping/jitter/perda (2 medições por segundo ao alvo e ao router). Corre em segundo plano, fora do UI,
/// e continua enquanto o utilizador muda de página (para poder ficar ligado durante um jogo). Só lê: não muda nada no PC.
/// </summary>
public static class GameNetMonitor
{
    public const int IntervalMs = 500;
    private const int Keep = 1200; // 10 minutos

    private static readonly object Gate = new();
    private static readonly List<NetSample> Samples = new();
    private static CancellationTokenSource? _cts;
    private static GameNetTarget _target = GameNetProbe.Targets[0];

    public static bool IsRunning { get; private set; }
    public static DateTime StartedUtc { get; private set; }
    public static ActiveLink? Link { get; private set; }

    /// <summary>Disparado quando liga, desliga ou muda de alvo (pode vir de qualquer thread).</summary>
    public static event Action? StateChanged;

    public static GameNetTarget Target { get { lock (Gate) return _target; } }

    public static void Start(GameNetTarget target)
    {
        lock (Gate)
        {
            if (IsRunning) { SwitchLocked(target); }
            else
            {
                _target = target;
                Samples.Clear();
                StartedUtc = DateTime.UtcNow;
                _cts = new CancellationTokenSource();
                IsRunning = true;
                var ct = _cts.Token;
                _ = Task.Run(() => LoopAsync(ct));
            }
        }
        Raise();
    }

    /// <summary>Muda o alvo. Se estava a medir, o segmento anterior (≥ 30 s) entra no histórico.</summary>
    public static void SetTarget(GameNetTarget target)
    {
        lock (Gate)
        {
            if (_target.Id == target.Id) return;
            if (IsRunning) SwitchLocked(target);
            else _target = target;
        }
        Raise();
    }

    private static void SwitchLocked(GameNetTarget target)
    {
        SaveSegmentLocked();
        _target = target;
        Samples.Clear();
        StartedUtc = DateTime.UtcNow;
    }

    /// <summary>Para o monitor e guarda a sessão no histórico (se durou pelo menos 30 s). Devolve o registo guardado.</summary>
    public static GameNetRecord? Stop()
    {
        GameNetRecord? rec;
        lock (Gate)
        {
            if (!IsRunning) return null;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            rec = SaveSegmentLocked();
        }
        Raise();
        return rec;
    }

    private static GameNetRecord? SaveSegmentLocked()
    {
        if (Samples.Count < 60) return null;
        var stats = NetStats.From(Samples);
        var diag = GameNetDiagnosis.Evaluate(stats, Link, _target, null);
        var rec = new GameNetRecord
        {
            WhenUtc = DateTime.UtcNow, Kind = "ping", Target = _target.Short,
            AvgMs = stats.Avg, JitterMs = stats.Jitter, LossPct = stats.LossPct, Spikes = stats.Spikes,
            DurationS = (int)(Samples[^1].At - Samples[0].At).TotalSeconds, Verdict = diag.Title, Level = diag.Level,
        };
        GameNetHistory.Add(rec);
        return rec;
    }

    /// <summary>As últimas <paramref name="last"/> medições (cópia).</summary>
    public static NetSample[] Snapshot(int last)
    {
        lock (Gate) return Samples.Skip(Math.Max(0, Samples.Count - last)).ToArray();
    }

    public static NetStats Stats(int last) => NetStats.From(Snapshot(last));

    public static TimeSpan Elapsed => IsRunning ? DateTime.UtcNow - StartedUtc : TimeSpan.Zero;

    private static async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(IntervalMs));
        int linkRefresh = 0;
        try
        {
            do
            {
                try
                {
                    if (linkRefresh-- <= 0) { Link = GameNetProbe.ActiveLink(); linkRefresh = 20; }
                    GameNetTarget t;
                    lock (Gate) t = _target;
                    var gw = Link?.Gateway;
                    var a = GameNetProbe.MeasureAsync(t, ct);
                    var b = gw != null ? SafePing(gw) : Task.FromResult<double?>(null);
                    await Task.WhenAll(a, b).ConfigureAwait(false);
                    var sample = new NetSample(DateTime.UtcNow, a.Result, b.Result, gw != null);
                    lock (Gate)
                    {
                        if (!IsRunning || ct.IsCancellationRequested || _target.Id != t.Id) continue;
                        Samples.Add(sample);
                        if (Samples.Count > Keep) Samples.RemoveRange(0, Samples.Count - Keep);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch { /* uma medição falhada não pára o monitor */ }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private static async Task<double?> SafePing(System.Net.IPAddress ip)
    {
        try { return await GameNetProbe.PingAsync(ip, 1000).ConfigureAwait(false); }
        catch { return null; }
    }

    private static void Raise()
    {
        try { StateChanged?.Invoke(); } catch { }
    }
}
