using System.Diagnostics;
using FrameForge.Hardware;

namespace FrameForge.Tuning;

/// <summary>O scan parou por um motivo que não é erro da app (ex.: fechaste a janela do teste). O texto vai para o utilizador.</summary>
public sealed class ScanAbortException : Exception
{
    public ScanAbortException(string message) : base(message) { }
}

/// <summary>Resultado de um trabalho corrido com vigia (<see cref="ScanContext.WithLiveAsync{T}"/>).</summary>
public sealed class LiveRun<T>
{
    public bool Ok { get; init; }
    public T Result { get; init; } = default!;
    /// <summary>Porque parou antes do fim (limite térmico, cancelado, não respondeu…). null = acabou bem ou chegou ao tempo pedido.</summary>
    public string? Stop { get; init; }
    public bool Thermal { get; init; }
    public bool CapReached { get; init; }
    public Exception? Error { get; init; }
}

/// <summary>Tudo o que uma fase precisa: limites do perfil, relatório, estado gravado, vigia e formas de reportar progresso.</summary>
internal sealed class ScanContext
{
    public required TuningLimits Lim { get; init; }
    public required ScanReport Report { get; init; }
    public required TuningState State { get; init; }
    public required CancellationToken Ct { get; init; }
    public SystemProbe? Probe { get; set; }
    /// <summary>Nome (parcial) do adaptador D3D para os testes; vazio = o de mais VRAM (a mesma GPU que a NVML escolhe).</summary>
    public string Adapter { get; set; } = "";
    public bool Nvidia { get; set; }
    public StabilityRunner Runner { get; }

    internal Action<string>? OnLog;
    internal Action<string>? OnEstado;
    internal Action<string?>? OnPasso;
    internal Action<double>? OnFase;
    internal Action<LiveValues>? OnLive;

    private double _subA, _subB = 1;
    private readonly object _logLock = new();

    public ScanContext(TuningLimits lim)
    {
        Runner = new StabilityRunner(lim);
        Runner.OnLive = v => OnLive?.Invoke(v);
        Runner.OnProgress = p => OnFase?.Invoke(_subA + (_subB - _subA) * p);
    }

    /// <summary>Linha para o registo de eventos (fica também no relatório).</summary>
    public void Log(string line)
    {
        lock (_logLock)
        {
            Report.Registo.Add($"{DateTime.Now:HH:mm:ss} {line}");
            if (Report.Registo.Count > 400) Report.Registo.RemoveRange(0, Report.Registo.Count - 400);
        }
        OnLog?.Invoke(line);
    }

    public void Estado(string text) => OnEstado?.Invoke(text);
    public void Passo(string? text) => OnPasso?.Invoke(text);

    /// <summary>Progresso da fase 0–1.</summary>
    public void Fase(double p) => OnFase?.Invoke(Math.Clamp(p, 0, 1));

    /// <summary>As próximas cargas do vigia contam como o troço [a, b] da fase atual.</summary>
    public void Sub(double a, double b) { _subA = Math.Clamp(a, 0, 1); _subB = Math.Clamp(b, _subA, 1); Fase(_subA); }
    public double SubA => _subA;
    public double SubB => _subB;

    public void Live(LiveValues v) => OnLive?.Invoke(v);

    /// <summary>Grava o estado (com o relatório parcial) no disco. Devolve false se não ficou gravado.</summary>
    public bool SaveState()
    {
        State.Hora = DateTime.Now;
        State.Parcial = Report;
        lock (_logLock) return ScanStore.WriteState(State);
    }

    /// <summary>
    /// Corre um trabalho síncrono num thread próprio, com leituras ao vivo 1×/s e o limite térmico da CPU do perfil.
    /// Nunca espera para sempre: ao fim de <paramref name="cap"/> pede para parar (o trabalho devolve o que mediu)
    /// e, se não parar em 30 s, desiste dele (o thread é de fundo e acaba sozinho).
    /// </summary>
    public async Task<LiveRun<T>> WithLiveAsync<T>(Func<CancellationToken, T> work, TimeSpan cap, ThermalTracker? tracker = null,
                                                   Func<double>? progress = null, bool gpuLimits = false)
    {
        // Sem using: se o trabalho for abandonado ainda pode estar a ler o token (o registo sai com o fim do scan)
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var task = Task.Factory.StartNew(() => work(cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var sw = Stopwatch.StartNew();
        string? stop = null;
        bool thermal = false, capReached = false;

        while (!task.IsCompleted)
        {
            var done = await Task.WhenAny(task, Task.Delay(1000, CancellationToken.None));
            if (done == task) break;
            var live = await ScanSensors.ReadAsync(TimeSpan.FromSeconds(3));
            tracker?.Add(live);
            Live(live);
            if (progress != null) { try { Fase(_subA + (_subB - _subA) * Math.Clamp(progress(), 0, 1)); } catch { } }

            if (stop == null && live.CpuTemp > Lim.CpuTempMax)
            { stop = $"CPU a {live.CpuTemp:0} °C (limite {Lim.CpuTempMax:0} °C)"; thermal = true; cts.Cancel(); }
            if (stop == null && gpuLimits && live.GpuTemp > Lim.GpuTempMax)
            { stop = $"GPU a {live.GpuTemp:0} °C (limite {Lim.GpuTempMax:0} °C)"; thermal = true; cts.Cancel(); }
            if (stop == null && Ct.IsCancellationRequested) { stop = "cancelado"; cts.Cancel(); }
            if (!capReached && sw.Elapsed >= cap) { capReached = true; cts.Cancel(); }

            // Pediu-se para parar e não parou em 30 s: desiste (não prende o scan)
            if (cts.IsCancellationRequested && sw.Elapsed >= cap + TimeSpan.FromSeconds(30))
            {
                return new LiveRun<T> { Ok = false, Stop = stop ?? "o teste não terminou a tempo", Thermal = thermal, CapReached = capReached };
            }
        }

        try
        {
            T r = await task;
            return new LiveRun<T> { Ok = stop == null, Result = r, Stop = stop, Thermal = thermal, CapReached = capReached };
        }
        catch (OperationCanceledException)
        {
            return new LiveRun<T> { Ok = false, Stop = stop ?? (capReached ? null : "cancelado"), Thermal = thermal, CapReached = capReached };
        }
        catch (Exception ex)
        {
            return new LiveRun<T> { Ok = false, Stop = stop ?? ex.Message, Thermal = thermal, CapReached = capReached, Error = ex };
        }
    }

    /// <summary>Lança OperationCanceledException se o utilizador parou o scan.</summary>
    public void ThrowIfCancelled() => Ct.ThrowIfCancellationRequested();
}
