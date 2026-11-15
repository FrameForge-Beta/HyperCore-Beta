using System.Diagnostics;
using FrameForge.Core;
using ResetExplode;

namespace FrameForge.Tuning;

/// <summary>Tipo de carga que o vigia corre.</summary>
public enum LoadKind
{
    /// <summary>Cena de jogo (GpuGameBench): carga realista para testar clocks altos; mede FPS.</summary>
    GpuGame,
    /// <summary>Stress tipo FurMark (GpuBurn): mede o limite térmico e de energia, não a estabilidade de clocks.</summary>
    GpuBurn,
    /// <summary>CPU com verificação determinística (path tracer com hash) + RAM com padrão verificado (StressTester).</summary>
    CpuRam,
}

public sealed class LoadSpec
{
    public LoadKind Kind { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public string Adapter { get; init; } = "";
    /// <summary>Só CpuRam: fração da RAM livre a testar (0 = sem teste de RAM).</summary>
    public double RamFraction { get; init; } = 0.5;
    /// <summary>Texto curto do que se está a testar (vai para o estado ao vivo).</summary>
    public string Label { get; init; } = "";
}

/// <summary>
/// Corre uma carga com vigia: temperaturas (limites do perfil), eventos do Windows (reset do driver, WHEA),
/// erros de RAM/cálculo e um watchdog que nunca deixa o scan preso (timeout por passo, GPU sem frames = crash).
/// Sem dependências de interface; a telemetria vem do SensorHub + NVML.
/// </summary>
public sealed class StabilityRunner
{
    private readonly TuningLimits _lim;

    public StabilityRunner(TuningLimits limits) { _lim = limits; }

    /// <summary>Chamado ~1×/s com as leituras ao vivo (no thread do vigia).</summary>
    public Action<LiveValues>? OnLive { get; set; }
    /// <summary>Chamado com a fração concluída da carga (0–1).</summary>
    public Action<double>? OnProgress { get; set; }

    /// <summary>Tempo extra para a carga arrancar (compilar shaders, criar o terreno…) antes de o watchdog a dar como presa.</summary>
    public TimeSpan SetupGrace { get; set; } = TimeSpan.FromSeconds(60);

    public Task<StepResult> RunAsync(LoadSpec spec, CancellationToken ct) => spec.Kind switch
    {
        LoadKind.GpuGame => RunGpuGameAsync(spec, ct),
        LoadKind.GpuBurn => RunGpuBurnAsync(spec, ct),
        _ => RunCpuRamAsync(spec, ct),
    };

    // ------------------------------------------------------------------ GPU: cena de jogo

    private async Task<StepResult> RunGpuGameAsync(LoadSpec spec, CancellationToken ct)
    {
        var res = new StepResult();
        var tracker = new ThermalTracker();
        var bench = new GpuGameBench { Width = spec.Width, Height = spec.Height, Duration = spec.Duration, AdapterName = spec.Adapter };
        GpuGameResult? result = null;
        var start = DateTime.Now;
        var sw = Stopwatch.StartNew();
        var thread = new Thread(() => { try { result = bench.Run(); } catch { } }) { IsBackground = true, Name = "Scan · cena de jogo" };
        thread.Start();

        string? fail = await WatchAsync(res, tracker, start, sw, spec.Duration, ct,
            alive: () => thread.IsAlive,
            frames: () => bench.FramesDone,
            fps: () => bench.CurrentFps,
            progress: () => bench.Progress,
            gpu: true, cpu: false,
            stop: bench.Cancel);

        bool ended = await JoinAsync(thread, TimeSpan.FromSeconds(12));
        res.Segundos = sw.Elapsed.TotalSeconds;
        res.Gpu = tracker.Gpu();
        res.PowerCapFracao = tracker.PowerCapFraction();

        if (fail == null && !ended) fail = "a cena de teste deixou de responder (provável crash do driver)";
        if (fail == null && result == null)
        {
            if (bench.ClosedByUser) { res.Cancelado = true; fail = "fechaste a janela do teste"; }
            else if (bench.DeviceRemoved || bench.Hung) { res.CrashDriver = true; fail = "crash do driver de vídeo"; }
            else if (ct.IsCancellationRequested) { res.Cancelado = true; fail = "cancelado"; }
            else fail = "erro no teste: " + (bench.Error ?? "desconhecido");
        }
        if (result != null) { res.AvgFps = result.AvgFps; res.Low1Fps = result.Low1Fps; }

        // O Event Log escreve o 4101 só depois de o driver recuperar: espera um pouco e volta a ver
        if (fail == null || res.CrashDriver) fail = await FinalEventsAsync(res, start, fail);
        Finish(res, fail);
        return res;
    }

    // ------------------------------------------------------------------ GPU: stress de energia (FurMark)

    private async Task<StepResult> RunGpuBurnAsync(LoadSpec spec, CancellationToken ct)
    {
        var res = new StepResult();
        var tracker = new ThermalTracker();
        var burn = new GpuBurn();
        var start = DateTime.Now;
        var sw = Stopwatch.StartNew();
        burn.Start(new GpuBurnSettings { Width = 1280, Height = 720, Msaa = 4, Shells = 128, Iterations = 48, AdapterName = spec.Adapter });

        string? fail = await WatchAsync(res, tracker, start, sw, spec.Duration, ct,
            alive: () => burn.Running,
            frames: () => burn.Frames,
            fps: () => burn.Fps,
            progress: () => Math.Min(1, sw.Elapsed.TotalSeconds / spec.Duration.TotalSeconds),
            gpu: true, cpu: false,
            stop: () => { },
            endsByItself: false);

        await Task.Run(burn.Stop); // Join(3 s) no máximo; o thread é de fundo
        res.Segundos = sw.Elapsed.TotalSeconds;
        res.Gpu = tracker.Gpu();
        res.PowerCapFracao = tracker.PowerCapFraction();
        res.AvgFps = burn.AverageFps;
        if (fail == null && burn.Error != null)
        {
            if (burn.DeviceRemoved || burn.Hung) { res.CrashDriver = true; fail = "crash do driver de vídeo"; }
            else fail = "erro no teste: " + burn.Error;
        }
        if (fail == null && burn.ClosedByUser) { res.Cancelado = true; fail = "fechaste a janela do teste"; }
        if (fail == null || res.CrashDriver) fail = await FinalEventsAsync(res, start, fail);
        Finish(res, fail);
        return res;
    }

    // ------------------------------------------------------------------ CPU + RAM com verificação

    private async Task<StepResult> RunCpuRamAsync(LoadSpec spec, CancellationToken ct)
    {
        var res = new StepResult();
        var tracker = new ThermalTracker();
        var start = DateTime.Now;
        var sw = Stopwatch.StartNew();
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stress = new StressTester();
        bool ram = spec.RamFraction > 0;
        if (ram) stress.Start(cpu: false, ram: true, ramFraction: spec.RamFraction);

        // Carga de CPU = path tracer determinístico em todos os threads (menos 1 para a RAM), comparando o hash de cada volta
        int threads = Math.Max(1, Environment.ProcessorCount - (ram ? 1 : 0));
        int buckets = Math.Max(32, threads * 6);
        long rounds = 0;
        bool mismatch = false;
        var renderer = new BenchRenderer();
        var cpuThread = new Thread(() =>
        {
            try
            {
                ulong? reference = null;
                while (!loadCts.IsCancellationRequested)
                {
                    var h = renderer.RenderHash(threads, buckets, loadCts.Token);
                    if (h == null) break;
                    if (reference == null) reference = h;
                    else if (h != reference) { mismatch = true; break; }
                    Interlocked.Increment(ref rounds);
                }
            }
            catch { }
        }) { IsBackground = true, Name = "Scan · CPU", Priority = ThreadPriority.BelowNormal };
        cpuThread.Start();

        string? fail = await WatchAsync(res, tracker, start, sw, spec.Duration, ct,
            alive: () => cpuThread.IsAlive,
            frames: () => renderer.SamplesDone,
            fps: () => null,
            progress: () => Math.Min(1, sw.Elapsed.TotalSeconds / spec.Duration.TotalSeconds),
            gpu: false, cpu: true,
            stop: () => { },
            endsByItself: false,
            extra: () =>
            {
                if (mismatch) return "erro de cálculo da CPU (o mesmo trabalho deu resultados diferentes)";
                if (ram && stress.MemoryErrors > 0) return $"{stress.MemoryErrors} erros na RAM";
                return null;
            });

        loadCts.Cancel();
        await Task.Run(stress.Stop);
        await JoinAsync(cpuThread, TimeSpan.FromSeconds(10));
        res.ErrosRam = stress.MemoryErrors;
        if (fail == null && mismatch) fail = "erro de cálculo da CPU (o mesmo trabalho deu resultados diferentes)";
        if (fail == null && res.ErrosRam > 0) fail = $"{res.ErrosRam} erros na RAM";
        res.Segundos = sw.Elapsed.TotalSeconds;
        res.Cpu = tracker.Cpu();
        if (fail == null) fail = await FinalEventsAsync(res, start, null);
        Finish(res, fail);
        return res;
    }

    // ------------------------------------------------------------------ vigia comum

    /// <summary>
    /// Vigia até a carga acabar (ou a duração passar, se <paramref name="endsByItself"/> = false). Devolve o motivo da falha (null = ok).
    /// Nunca espera para sempre: depois de Duration + SetupGrace dá a carga como presa.
    /// </summary>
    private async Task<string?> WatchAsync(StepResult res, ThermalTracker tracker, DateTime start, Stopwatch sw, TimeSpan duration,
                                           CancellationToken ct, Func<bool> alive, Func<long> frames, Func<double?> fps, Func<double> progress,
                                           bool gpu, bool cpu, Action stop, bool endsByItself = true, Func<string?>? extra = null)
    {
        var deadline = duration + SetupGrace;
        long lastFrames = -1;
        var lastFrameChange = Stopwatch.StartNew();
        var lastEvents = Stopwatch.StartNew();
        Task<HardwareErrors>? pendingEvents = null;
        bool seenGpuTemp = false;
        var lastGpuTemp = Stopwatch.StartNew();
        string? fail = null;
        var tuner = AppServices.Tuner;

        while (true)
        {
            if (endsByItself ? !alive() : (sw.Elapsed >= duration || !alive())) break;
            if (ct.IsCancellationRequested) { res.Cancelado = true; fail = "cancelado"; break; }

            var live = (await ScanSensors.ReadAsync(TimeSpan.FromSeconds(3))) with { Fps = fps() };
            tracker.Add(live);
            try { OnLive?.Invoke(live); } catch { }
            try { OnProgress?.Invoke(Math.Clamp(progress(), 0, 1)); } catch { }

            // Limites térmicos (todas as fases)
            if (gpu)
            {
                if (live.GpuTemp > _lim.GpuTempMax) { res.Termico = true; fail = $"GPU a {live.GpuTemp:0} °C (limite {_lim.GpuTempMax:0} °C)"; break; }
                if (live.GpuHotspot > _lim.GpuHotspotMax) { res.Termico = true; fail = $"hotspot da GPU a {live.GpuHotspot:0} °C (limite {_lim.GpuHotspotMax:0} °C)"; break; }
                if (live.GpuMemTemp > _lim.GpuMemTempMax) { res.Termico = true; fail = $"memória da GPU a {live.GpuMemTemp:0} °C (limite {_lim.GpuMemTempMax:0} °C)"; break; }
            }
            if (live.CpuTemp > _lim.CpuTempMax) { res.Termico = true; fail = $"CPU a {live.CpuTemp:0} °C (limite {_lim.CpuTempMax:0} °C)"; break; }

            // Reset do driver visto em tempo real pelo vigia do GpuTuner
            if (gpu && tuner.LastDriverReset is DateTime reset && reset >= start) { res.CrashDriver = true; fail = "crash do driver de vídeo"; break; }

            // Sensor de temperatura da GPU perdido a meio (havia leitura e deixou de haver): sem limite térmico não se continua
            if (gpu)
            {
                if (live.GpuTemp is > 0) { seenGpuTemp = true; lastGpuTemp.Restart(); }
                else if (seenGpuTemp && lastGpuTemp.Elapsed.TotalSeconds > 10)
                { res.Termico = true; fail = "deixei de conseguir ler a temperatura da GPU: parei por segurança"; break; }
            }

            // Registo do Windows a cada 3 s (WHEA corrigido de PCIe não reprova). A leitura corre em paralelo: um registo lento
            // (até 10 s) não deixa de lado os limites térmicos, que continuam a ser vistos a cada segundo.
            if (pendingEvents is { IsCompleted: true })
            {
                var ev = pendingEvents.IsCompletedSuccessfully ? pendingEvents.Result : null;
                pendingEvents = null;
                lastEvents.Restart();
                if (ev != null)
                {
                    res.Eventos = ev;
                    if (ev.GpuResets > 0) { res.CrashDriver = true; fail = "crash do driver de vídeo"; break; }
                    if (ev.Failing > 0) { fail = EventText(ev); break; }
                }
            }
            if (pendingEvents == null && lastEvents.Elapsed.TotalSeconds >= 3) pendingEvents = EventsAsync(start);

            if (extra?.Invoke() is string x) { fail = x; break; }

            // Watchdog: a carga tem de avançar
            long f = frames();
            if (f != lastFrames) { lastFrames = f; lastFrameChange.Restart(); }
            else if (f > 0 && lastFrameChange.Elapsed.TotalSeconds > 15) { if (gpu) res.CrashDriver = true; fail = "a carga parou de avançar (a GPU/CPU deixou de responder)"; break; }
            if (sw.Elapsed > deadline) { fail = "o teste não terminou a tempo"; break; }

            await Task.Delay(1000, CancellationToken.None);
        }
        if (fail != null || ct.IsCancellationRequested) { try { stop(); } catch { } }
        return fail;
    }

    private static async Task<string?> FinalEventsAsync(StepResult res, DateTime start, string? fail)
    {
        await Task.Delay(2500, CancellationToken.None);
        var ev = await EventsAsync(start);
        res.Eventos = ev;
        if (ev.GpuResets > 0) { res.CrashDriver = true; return fail ?? "crash do driver de vídeo"; }
        if (fail == null && ev.Failing > 0) return EventText(ev);
        return fail;
    }

    /// <summary>Lê o registo do Windows com prazo (10 s): um registo lento nunca prende o teste.</summary>
    private static async Task<HardwareErrors> EventsAsync(DateTime start)
    {
        var t = Task.Run(() => OcCheck.ErrorsSince(start));
        var done = await Task.WhenAny(t, Task.Delay(10_000, CancellationToken.None));
        return done == t ? await t : new HardwareErrors(0, 0, null, "o registo do Windows não respondeu a tempo");
    }

    private static string EventText(HardwareErrors ev)
    {
        if (ev.WheaFatal > 0) return "o Windows registou um erro de hardware grave (WHEA)";
        if (ev.WheaCorrectedCpu > 0) return "o Windows registou erros do processador corrigidos (WHEA)";
        if (ev.WheaCorrectedMemory > 0) return "o Windows registou erros de memória corrigidos (WHEA)";
        if (ev.GpuResets > 0) return "crash do driver de vídeo";
        return "o Windows registou erros de hardware (WHEA)";
    }

    private static void Finish(StepResult res, string? fail)
    {
        res.Passou = fail == null;
        res.Motivo = fail ?? "";
        if (res.Cancelado) res.Passou = false;
    }

    private static async Task<bool> JoinAsync(Thread t, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (t.IsAlive && sw.Elapsed < timeout) await Task.Delay(100, CancellationToken.None);
        return !t.IsAlive;
    }
}
