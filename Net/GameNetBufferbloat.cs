using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace FrameForge.Net;

/// <summary>Resultado do teste de bufferbloat. ExtraMs = quanto o ping sobe enquanto a ligação está cheia.</summary>
public sealed record BloatResult(double IdleMs, double LoadedMs, double ExtraMs, string Grade, double? Mbps, double LoadedLossPct);

/// <summary>Progresso: fase ("idle" | "load"), fração 0–1, último ping e velocidade de download (Mbps) até agora.</summary>
public sealed record BloatProgress(string Phase, double Fraction, double? LastMs, double? Mbps);

/// <summary>
/// Teste de bufferbloat: mede o ping em repouso (5 s) e depois enquanto descarrega ficheiros de teste de speed.cloudflare.com
/// (10 s, 4 ligações). Se o ping sobe muito, o router deixa a fila encher quando a ligação está ocupada.
/// Só lê: descarrega dados de teste para a memória (nada é guardado no disco). Limite de 600 MB por teste.
/// </summary>
public static class GameNetBufferbloat
{
    private const string Url = "https://speed.cloudflare.com/__down?bytes=50000000";
    private const long Cap = 600L * 1024 * 1024;
    private const int Streams = 4;
    private static readonly TimeSpan IdleTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LoadTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(1.5);
    private static readonly IPAddress PingIp = IPAddress.Parse("1.1.1.1");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = Streams * 2, PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("HyperCore/0.5");
        return h;
    }

    /// <summary>Nota de A (excelente) a F (mau) pela subida do ping sob carga.</summary>
    public static string Grade(double extraMs) => extraMs switch
    {
        <= 20 => "A",
        <= 50 => "B",
        <= 100 => "C",
        <= 200 => "D",
        _ => "F",
    };

    public static string GradeMeaning(string grade) => grade switch
    {
        "A" => "Excelente: o ping quase não sobe com downloads.",
        "B" => "Bom: sobe um pouco, mal se nota a jogar.",
        "C" => "Razoável: com downloads em casa vais notar lag.",
        "D" => "Mau: downloads ou vídeos em casa estragam o jogo.",
        _ => "Muito mau: o router deixa a fila encher por completo.",
    };

    private sealed class Counter
    {
        private long _value;
        public long Value => Interlocked.Read(ref _value);
        public void Add(long n) => Interlocked.Add(ref _value, n);
    }

    public static async Task<BloatResult> RunAsync(IProgress<BloatProgress>? progress, CancellationToken ct)
    {
        double total = (IdleTime + LoadTime).TotalSeconds;

        // ---- 1) Em repouso
        var idle = new List<double>();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < IdleTime)
        {
            ct.ThrowIfCancellationRequested();
            var t0 = clock.Elapsed;
            double? ms = await Probe().ConfigureAwait(false);
            if (ms is double d) idle.Add(d);
            progress?.Report(new BloatProgress("idle", clock.Elapsed.TotalSeconds / total, ms, null));
            await Pace(clock, t0, ct).ConfigureAwait(false);
        }
        if (idle.Count < 5) throw new InvalidOperationException("A Cloudflare (1.1.1.1) não respondeu. Confirma que tens Internet e tenta outra vez.");
        double idleMs = NetStats.MedianOf(idle);

        // ---- 2) Com a ligação cheia (download)
        var counter = new Counter();
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var downloads = Enumerable.Range(0, Streams).Select(_ => Task.Run(() => DownloadLoop(counter, loadCts.Token))).ToArray();

        var loaded = new List<double>();
        int lost = 0;
        long bytesAtWarm = -1;
        TimeSpan warmAt = TimeSpan.Zero;
        double? mbps = null;
        var load = Stopwatch.StartNew();
        try
        {
            while (load.Elapsed < LoadTime)
            {
                ct.ThrowIfCancellationRequested();
                var t0 = load.Elapsed;
                double? ms = await Probe().ConfigureAwait(false);
                if (load.Elapsed >= Warmup)
                {
                    if (bytesAtWarm < 0) { bytesAtWarm = counter.Value; warmAt = load.Elapsed; }
                    if (ms is double d) loaded.Add(d); else { lost++; loaded.Add(1000); }
                    double secs = (load.Elapsed - warmAt).TotalSeconds;
                    if (secs > 0.4) mbps = (counter.Value - bytesAtWarm) * 8 / secs / 1e6;
                }
                progress?.Report(new BloatProgress("load", (IdleTime.TotalSeconds + load.Elapsed.TotalSeconds) / total, ms, mbps));
                await Pace(load, t0, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            loadCts.Cancel();
            try { await Task.WhenAll(downloads).ConfigureAwait(false); } catch { }
        }

        if (counter.Value < 1_000_000)
            throw new InvalidOperationException("Não consegui descarregar do servidor de teste (speed.cloudflare.com). Pode estar bloqueado por uma firewall.");
        if (loaded.Count == 0) throw new InvalidOperationException("Sem medições durante o download.");

        double loadedMs = NetStats.MedianOf(loaded);
        double extra = Math.Max(0, loadedMs - idleMs);
        return new BloatResult(idleMs, loadedMs, extra, Grade(extra), mbps, 100.0 * lost / loaded.Count);
    }

    private static async Task<double?> Probe()
    {
        try { return await GameNetProbe.PingAsync(PingIp, 1000).ConfigureAwait(false); }
        catch { return null; }
    }

    /// <summary>Uma medição a cada 200 ms.</summary>
    private static Task Pace(Stopwatch clock, TimeSpan t0, CancellationToken ct)
    {
        int wait = 200 - (int)(clock.Elapsed - t0).TotalMilliseconds;
        return wait > 0 ? Task.Delay(wait, ct) : Task.CompletedTask;
    }

    private static async Task DownloadLoop(Counter counter, CancellationToken ct)
    {
        var buffer = new byte[81920];
        int failures = 0;
        while (!ct.IsCancellationRequested && counter.Value < Cap)
        {
            try
            {
                using var resp = await Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                int n;
                while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    counter.Add(n);
                    if (counter.Value >= Cap) return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                if (++failures > 3) return;
                try { await Task.Delay(300, ct).ConfigureAwait(false); } catch { return; }
            }
        }
    }
}
