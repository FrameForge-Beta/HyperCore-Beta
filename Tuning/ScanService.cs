using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using FrameForge.Core;
using FrameForge.Hardware;
using FrameForge.Overclock;
using ResetExplode;

namespace FrameForge.Tuning;

/// <summary>O que impede ou desaconselha começar um scan agora.</summary>
public sealed record ScanPreflight(List<string> Bloqueios, List<string> Avisos)
{
    public bool PodeComecar => Bloqueios.Count == 0;
}

/// <summary>
/// Orquestrador do "Scan e afinação automática": corre as fases por ordem (inventário → CPU → RAM → disco → gráfica de fábrica →
/// stress da gráfica → overclock do núcleo → memória → validação → relatório) com IProgress e CancellationToken, grava o
/// <see cref="ScanReport"/> e garante valores de fábrica na gráfica no fim, ao cancelar, em exceção e ao fechar a app.
/// <para>Uso pela página:</para>
/// <code>
/// var plan = ScanService.Plan(ScanProfile.Seguro);            // fases + duração estimada
/// var pre = ScanService.Preflight();                          // bloqueios (Afterburner aberto…) e avisos
/// var rep = await ScanService.Instance.RunAsync(perfil, new Progress&lt;ScanProgress&gt;(p => …), cts.Token);
/// </code>
/// O <see cref="Progress{T}"/> criado no thread da interface entrega lá cada <see cref="ScanProgress"/>.
/// <see cref="Completed"/>, <see cref="ProgressChanged"/> e <see cref="RunningChanged"/> são chamados FORA do thread da interface.
/// </summary>
public sealed class ScanService
{
    public static ScanService Instance { get; } = new();

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private TuningState? _state;
    private volatile bool _running;
    private ScanReport? _last;
    private bool _lastLoaded;

    public bool Running => _running;

    /// <summary>Perfil do scan a correr (null se nenhum): a página usa-o para os limites do painel ao vivo.</summary>
    public ScanProfile? RunningProfile { get; private set; }

    /// <summary>O último scan COMPLETO (lido do disco na primeira vez). null se nunca houve.</summary>
    public ScanReport? Last
    {
        get
        {
            lock (_gate)
            {
                if (!_lastLoaded) { _last = ScanStore.LoadLast(); _lastLoaded = true; }
                return _last;
            }
        }
    }

    /// <summary>Scan interrompido encontrado no arranque (crash, bloqueio ou reinício). A página oferece "Retomar".</summary>
    public RecoveryInfo? PendingRecovery { get; private set; }

    /// <summary>O último progresso (para uma página que abre a meio do scan).</summary>
    public ScanProgress? Current { get; private set; }

    public event Action<ScanReport>? Completed;
    public event Action<ScanProgress>? ProgressChanged;
    public event Action<bool>? RunningChanged;

    private ScanService() { }

    // ================================================================== plano e pré-requisitos

    private static readonly (ScanPhase Fase, string Titulo)[] Titles =
    {
        (ScanPhase.Inventario, "Inventário do PC"),
        (ScanPhase.Cpu, "Processador"),
        (ScanPhase.Ram, "Memória RAM"),
        (ScanPhase.Disco, "Discos"),
        (ScanPhase.GpuBase, "Gráfica: desempenho de fábrica"),
        (ScanPhase.GpuTermico, "Gráfica: temperatura e energia"),
        (ScanPhase.GpuNucleo, "Overclock: núcleo"),
        (ScanPhase.GpuMemoria, "Overclock: memória"),
        (ScanPhase.GpuValidacao, "Overclock: validação final"),
        (ScanPhase.Analise, "Relatório"),
    };

    public static string TitleOf(ScanPhase f) => Titles.First(t => t.Fase == f).Titulo;

    private static bool IsOcPhase(ScanPhase f) => f is ScanPhase.GpuNucleo or ScanPhase.GpuMemoria or ScanPhase.GpuValidacao;

    /// <summary>Fases e duração estimada do perfil neste PC (rápido: não corre nada pesado).</summary>
    public static ScanPlan Plan(ScanProfile profile)
    {
        var lim = TuningLimits.For(profile);
        string? ocSkip = null;
        try { ocSkip = GpuAutoTuner.Unsupported(out _); } catch { ocSkip = "A tua placa não permite overclock por software aqui; mostro o guia."; }
        int disks = 1;
        try { disks = CpuRamDiskScan.DiskTargets(allowScan: false).Count; } catch { }
        int h = 1080;
        try
        {
            var probe = AppServices.Probe;
            if (probe.IsCompletedSuccessfully && probe.Result.PrimaryDisplay is Hardware.Display d) h = d.Height;
        }
        catch { }
        var phases = BuildPhases(lim, ocSkip, disks, h);
        return new ScanPlan(profile, phases, TimeSpan.FromSeconds(phases.Sum(p => p.SegundosEstimados)), lim);
    }

    private static List<PhaseStatus> BuildPhases(TuningLimits lim, string? ocSkip, int disks, int monitorH)
    {
        double bench = lim.BenchMedicao.TotalSeconds + 10; // + arranque da cena
        double step = lim.PassoDuracao.TotalSeconds + 12;  // + aplicar, abrir a janela, ler eventos
        double coreSteps = Math.Min(Math.Min(lim.TetoNucleo, 300), 300) / (double)lim.PassoNucleo + 2;
        double memSteps = 1 + Math.Min(lim.TetoMemoria, 1200) / (double)lim.PassoMemoria;

        double Est(ScanPhase f) => f switch
        {
            ScanPhase.Inventario => 8,
            ScanPhase.Cpu => 100 + lim.StressCpu.TotalSeconds,
            ScanPhase.Ram => 25,
            ScanPhase.Disco => 15 + 45 * Math.Max(1, disks),
            ScanPhase.GpuBase => bench * (2 + (monitorH >= 1400 ? 1 : 0) + (monitorH > 1600 ? 1 : 0)),
            ScanPhase.GpuTermico => lim.StressGpu.TotalSeconds + 8,
            ScanPhase.GpuNucleo => Math.Min(lim.OrcamentoNucleo.TotalSeconds, coreSteps * step),
            ScanPhase.GpuMemoria => lim.TocaMemoria ? Math.Min(lim.OrcamentoMemoria.TotalSeconds, memSteps * step) : 0,
            ScanPhase.GpuValidacao => lim.Validacao.TotalSeconds + 4 * bench + 15,
            ScanPhase.Analise => 5,
            _ => 10,
        };

        var list = new List<PhaseStatus>();
        foreach (var (f, t) in Titles)
        {
            var p = new PhaseStatus { Fase = f, Titulo = t, SegundosEstimados = Math.Round(Est(f)) };
            if (IsOcPhase(f) && ocSkip != null) { p.Estado = PhaseState.Saltado; p.Nota = ocSkip; p.SegundosEstimados = 0; }
            else if (f == ScanPhase.GpuMemoria && !lim.TocaMemoria) { p.Estado = PhaseState.Saltado; p.Nota = "O perfil Seguro não mexe na memória da gráfica."; p.SegundosEstimados = 0; }
            list.Add(p);
        }
        return list;
    }

    /// <summary>O que impede começar agora (Bloqueios) e o que convém saber (Avisos).</summary>
    public static ScanPreflight Preflight()
    {
        var block = new List<string>();
        var warn = new List<string>();
        if (Instance.Running) block.Add("Já há um scan a correr.");
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
                block.Add("Abre a app como administrador (é preciso para ler as temperaturas e afinar a gráfica).");
        }
        catch { }
        try
        {
            if (Hardware.Nvml.Available && Hardware.Nvml.FirstDevice() != IntPtr.Zero)
                foreach (var t in GpuAutoTuner.Conflicts())
                    block.Add($"Fecha o {t} antes de começar: escreve na gráfica ao mesmo tempo e estraga o teste.");
        }
        catch { }

        warn.Add("Fecha jogos e programas pesados. O PC vai aquecer e as ventoinhas vão subir — é normal.");
        if (OnBattery()) warn.Add("O portátil está na bateria: liga o carregador (na bateria o desempenho é limitado).");
        try { if (ScanSensors.Read().CpuTemp is not > 0) warn.Add("Não consigo ler a temperatura da CPU: o teste conta com a proteção do próprio processador."); } catch { }
        if (Instance.PendingRecovery is { PodeRetomar: true }) warn.Add("Há um scan interrompido: podes retomá-lo em vez de começar outro.");
        return new ScanPreflight(block, warn);
    }

    // ================================================================== correr

    /// <summary>Corre o scan completo. Lança InvalidOperationException se já houver um a correr. Nunca lança por cancelamento: devolve o relatório com Resultado = Cancelado.</summary>
    public Task<ScanReport> RunAsync(ScanProfile profile, IProgress<ScanProgress>? progress, CancellationToken ct) =>
        Start(profile, null, progress, ct);

    /// <summary>Retoma o scan interrompido (<see cref="PendingRecovery"/>) a partir do último passo estável.</summary>
    public Task<ScanReport> ResumeAsync(IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        if (_running) throw new InvalidOperationException("Já há um scan a correr.");
        var st = ScanStore.ReadRecovered() ?? throw new InvalidOperationException("Não há nenhum scan para retomar.");
        ScanStore.ClearRecovered();
        PendingRecovery = null;
        return Start(st.Perfil, st, progress, ct);
    }

    /// <summary>Pára o scan a correr (a gráfica volta a Stock).</summary>
    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
    }

    private Task<ScanReport> Start(ScanProfile profile, TuningState? resume, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_running) throw new InvalidOperationException("Já há um scan a correr.");
            _running = true;
            RunningProfile = profile;
            ScanStore.Frozen = false; // (um fim de sessão cancelado pelo Windows deixa a app aberta: um scan novo volta a gravar)
        }
        try { RunningChanged?.Invoke(true); } catch { }
        // Fora do thread da interface: as continuações dos awaits não devem ir parar à UI
        return Task.Run(async () =>
        {
            try { return await RunInternal(profile, resume, progress, ct); }
            catch (Exception ex)
            {
                // Só chega aqui um erro antes do try principal: nunca deixar o serviço preso em "a correr" nem a placa reservada
                LogError(ex);
                AppServices.Tuner.AutoTuneActive = false;
                await GpuAutoTuner.RestoreStockAsync();
                AppServices.Tuner.ScanReserved = false;
                lock (_gate) { _running = false; RunningProfile = null; _cts = null; _state = null; }
                try { RunningChanged?.Invoke(false); } catch { }
                var r = new ScanReport { Perfil = profile, Data = DateTime.Now, Resultado = ScanOutcome.Erro, Motivo = ex.Message, Fim = DateTime.Now };
                try { Completed?.Invoke(r); } catch { }
                return r;
            }
        });
    }

    private async Task<ScanReport> RunInternal(ScanProfile profile, TuningState? resume, IProgress<ScanProgress>? progress, CancellationToken outer)
    {
        var lim = TuningLimits.For(profile);
        var report = resume?.Parcial ?? new ScanReport { Perfil = profile, Data = DateTime.Now };
        report.Perfil = profile;
        report.Resultado = ScanOutcome.EmCurso;
        report.Motivo = null;
        report.Fim = null;

        var state = resume ?? new TuningState { Perfil = profile, Inicio = DateTime.Now };
        state.EmCurso = true; state.ErroDaApp = false; state.ErroTexto = null;
        state.ValoresAplicados = false; state.NucleoEmTeste = null; state.MemoriaEmTeste = null;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        lock (_gate) { _cts = cts; _state = state; }

        var ctx = new ScanContext(lim) { Lim = lim, Report = report, State = state, Ct = cts.Token };
        var em = new Emitter(this, progress, report, ctx);

        // Fases: num retomar ficam as que já estavam feitas
        if (resume?.Parcial?.Fases is { Count: > 0 } old)
        {
            foreach (var f in old.Where(f => f.Estado is PhaseState.ACorrer or PhaseState.Falhou)) f.Estado = PhaseState.PorFazer;
            report.Fases = old;
            ctx.Log("• A retomar o scan interrompido");
        }
        else
        {
            string? skip = await GpuAutoTuner.UnsupportedSafeAsync();
            int disks = 1;
            try { disks = CpuRamDiskScan.DiskTargets().Count; } catch { }
            report.Fases = BuildPhases(lim, skip, disks, 1080);
        }
        ctx.SaveState();

        try
        {
            // A partir daqui só o afinador escreve valores acima de fábrica na placa (GameRunner, perfil de arranque e páginas ficam à espera)
            AppServices.Tuner.ScanReserved = true;
            ScanSensors.Start();
            AppServices.Tuner.EnsureWatcher();
            ctx.Log($"Scan {lim.Nome} iniciado");
            if (resume != null) await PrepareResumeAsync(ctx);

            foreach (var ph in report.Fases.ToList())
            {
                if (ph.Estado != PhaseState.PorFazer) continue;
                cts.Token.ThrowIfCancellationRequested();
                em.Begin(ph);
                state.Fase = ph.Fase;
                ctx.SaveState();
                try
                {
                    await RunPhase(ph, ctx);
                    if (ph.Estado == PhaseState.ACorrer) ph.Estado = PhaseState.Feito;
                }
                catch (OperationCanceledException) { ph.Estado = PhaseState.PorFazer; throw; }
                catch (ScanAbortException) { ph.Estado = PhaseState.Falhou; throw; }
                catch (Exception ex)
                {
                    ph.Estado = PhaseState.Falhou;
                    ph.Nota = ex.Message;
                    ctx.Log($"✖ {ph.Titulo}: {ex.Message}");
                    LogError(ex);
                    if (IsOcPhase(ph.Fase))
                    {
                        await GpuAutoTuner.StockAsync(ctx);
                        foreach (var rest in report.Fases.Where(f => IsOcPhase(f.Fase) && f.Estado == PhaseState.PorFazer))
                        { rest.Estado = PhaseState.Saltado; rest.Nota = "Overclock interrompido por um erro."; }
                    }
                }
                em.End(ph);
                ctx.SaveState();
            }
            report.Resultado = ScanOutcome.Completo;
        }
        catch (OperationCanceledException)
        {
            report.Resultado = ScanOutcome.Cancelado;
            report.Motivo ??= state.ErroDaApp ? "A app teve um erro e parei o scan por segurança." : "Paraste o scan.";
        }
        catch (ScanAbortException ex)
        {
            report.Resultado = ScanOutcome.Cancelado;
            report.Motivo = ex.Message;
        }
        catch (Exception ex)
        {
            report.Resultado = ScanOutcome.Erro;
            report.Motivo = ex.Message;
            LogError(ex);
        }
        finally
        {
            AppServices.Tuner.AutoTuneActive = false;
            bool hadOc = state.ValoresAplicados;
            bool stockOk = await GpuAutoTuner.RestoreStockAsync();
            if (hadOc || !stockOk)
                ctx.Log(stockOk ? "• Gráfica de volta aos valores de fábrica" : "⚠ O driver não respondeu ao repor os valores de fábrica: reinicia o PC para garantir");
            if (!stockOk && report.Resultado == ScanOutcome.Completo)
            {
                report.Resultado = ScanOutcome.Erro;
                report.Motivo = "O driver de vídeo não confirmou a reposição dos valores de fábrica. Reinicia o PC para garantir.";
            }
            ctx.Passo(null);

            foreach (var f in report.Fases.Where(f => f.Estado is PhaseState.ACorrer or PhaseState.PorFazer))
                f.Estado = report.Resultado == ScanOutcome.Completo ? PhaseState.Feito : PhaseState.Saltado;

            // Cancelado depois de já haver medições: faz na mesma a análise (não toca no hardware)
            if (report.Resultado != ScanOutcome.Completo && (report.Cpu != null || report.Gpu != null))
            {
                try { ScanAnalysis.Run(report, ctx.Probe); } catch { }
            }
            report.Fim = DateTime.Now;
            if (report.Resultado != ScanOutcome.Completo && report.Motivo != null) ctx.Log("Scan parado: " + report.Motivo);
            else if (report.Resultado == ScanOutcome.Completo) ctx.Log("Scan concluído");

            if (stockOk)
            {
                state.EmCurso = false;
                state.ValoresAplicados = false;
                ScanStore.ClearState();
            }
            else
            {
                // Stock não confirmado: o estado fica "em curso" para o próximo arranque voltar a repor e tratar o valor em teste
                state.EmCurso = true;
                state.ValoresAplicados = true;
                state.ErroTexto = "o driver não confirmou a reposição dos valores de fábrica";
                ctx.SaveState();
            }
            AppServices.Tuner.ScanReserved = false;
            try { ScanStore.Save(report, asLast: report.Resultado == ScanOutcome.Completo); } catch { }
            lock (_gate)
            {
                if (report.Resultado == ScanOutcome.Completo) { _last = report; _lastLoaded = true; }
                _cts = null; _state = null; _running = false; RunningProfile = null;
            }
            em.Final();
            try { cts.Dispose(); } catch { }
            try { RunningChanged?.Invoke(false); } catch { }
            try { Completed?.Invoke(report); } catch { }
        }
        return report;
    }

    private static async Task RunPhase(PhaseStatus ph, ScanContext ctx)
    {
        var r = ctx.Report;
        switch (ph.Fase)
        {
            case ScanPhase.Inventario:
                await InventoryAsync(ctx);
                break;
            case ScanPhase.Cpu:
                await CpuRamDiskScan.RunCpuAsync(ctx);
                break;
            case ScanPhase.Ram:
                await CpuRamDiskScan.RunRamAsync(ctx);
                break;
            case ScanPhase.Disco:
                await CpuRamDiskScan.RunDiskAsync(ctx);
                break;
            case ScanPhase.GpuBase:
                await GpuScan.RunBaseAsync(ctx);
                break;
            case ScanPhase.GpuTermico:
                await GpuScan.RunThermalAsync(ctx);
                break;
            case ScanPhase.GpuNucleo:
                if (OcSkipReason(ctx) is string why)
                {
                    r.Oc = new OcResult { Suportado = false, Explicacao = why, FpsBase = r.Gpu?.Fps1080 };
                    ctx.Log("• Overclock da gráfica: " + why);
                    foreach (var f in r.Fases.Where(f => IsOcPhase(f.Fase) && f.Estado is PhaseState.PorFazer or PhaseState.ACorrer))
                    { f.Estado = PhaseState.Saltado; f.Nota = why; }
                    return;
                }
                r.Oc ??= new OcResult();
                r.Oc.Suportado = true;
                r.Oc.FpsBase ??= r.Gpu?.Fps1080;
                AppServices.Tuner.AutoTuneActive = true;
                await GpuAutoTuner.TuneCoreAsync(ctx);
                break;
            case ScanPhase.GpuMemoria:
                if (r.Oc is not { Suportado: true }) { ph.Estado = PhaseState.Saltado; return; }
                AppServices.Tuner.AutoTuneActive = true;
                await GpuAutoTuner.TuneMemoryAsync(ctx);
                break;
            case ScanPhase.GpuValidacao:
                if (r.Oc is not { Suportado: true }) { ph.Estado = PhaseState.Saltado; return; }
                AppServices.Tuner.AutoTuneActive = true;
                try { await GpuAutoTuner.ValidateAsync(ctx); }
                finally { AppServices.Tuner.AutoTuneActive = false; }
                break;
            case ScanPhase.Analise:
                ctx.Estado("A preparar o relatório…");
                ScanAnalysis.Run(r, ctx.Probe);
                break;
        }
    }

    /// <summary>Porque não se faz overclock (null = pode): outra marca, ferramentas em conflito, gráfica instável ou quente de fábrica.</summary>
    private static string? OcSkipReason(ScanContext ctx)
    {
        if (!ctx.Nvidia) return "A tua placa não permite overclock por software aqui (só placas NVIDIA com o driver a permitir); mostro o guia.";
        var tools = GpuAutoTuner.Conflicts();
        if (tools.Count > 0) return $"Fecha o {string.Join(" e o ", tools)} e repete o scan: escreve na gráfica ao mesmo tempo.";
        var gpu = ctx.Report.Gpu;
        if (gpu is { Estavel: false }) return "A gráfica falhou em valores de fábrica: não faço overclock antes de isso estar resolvido.";
        if (gpu?.Fps1080 == null) return "Não consegui medir a gráfica de fábrica, por isso não há base para comparar o overclock.";
        if (gpu.Jogo.TempMax is double t && t > ctx.Lim.GpuTempMax - 3)
            return $"A gráfica já chega a {t:0} °C de fábrica num jogo: melhora a ventilação antes de fazer overclock.";
        return null;
    }

    /// <summary>Num retomar o inventário já está feito: volta a ler o hardware (sem o refazer no relatório) e confirma a NVIDIA.</summary>
    private static async Task PrepareResumeAsync(ScanContext ctx)
    {
        try
        {
            var task = AppServices.Probe;
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(45), ctx.Ct));
            if (done == task && task.IsCompletedSuccessfully) ctx.Probe = task.Result;
        }
        catch { }
        ctx.Nvidia = await GpuAutoTuner.UnsupportedSafeAsync() == null;
    }

    private static async Task InventoryAsync(ScanContext ctx)
    {
        ctx.Estado("A ler o hardware do PC…");
        ctx.Fase(0.1);
        AppServices.RefreshProbe();
        var task = AppServices.Probe;
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(45), ctx.Ct));
        ctx.ThrowIfCancelled();
        SystemProbe? p = done == task && task.IsCompletedSuccessfully ? task.Result : null;
        ctx.Probe = p;

        var inv = ctx.Report.Inventario;
        if (p != null)
        {
            inv.Cpu = p.CpuName; inv.Nucleos = p.Cores; inv.Threads = p.Threads;
            inv.PlacaMae = p.Board; inv.Bios = p.BiosVersion;
            inv.Gpu = p.GpuName; inv.VramGB = p.VramGB; inv.DriverGpu = p.GpuDriver;
            inv.GpuFabricante = p.GpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || p.GpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                              : p.GpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || p.GpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
                              : p.GpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase) || p.GpuName.Contains("Arc", StringComparison.OrdinalIgnoreCase) ? "Intel" : "?";
            inv.RamGB = p.RamGB; inv.RamTipo = p.RamType; inv.Pentes = p.Ram.Count;
            if (p.Ram.Count > 0)
            {
                inv.RamMHz = p.Ram.Min(r => r.ConfiguredMHz);
                var rated = p.Ram.Where(r => r.RatedMHz != null).Select(r => r.RatedMHz!.Value).ToList();
                inv.RamMHzNominal = rated.Count > 0 ? rated.Min() : null;
                inv.RamCl = p.Ram.Select(r => r.RatedCl).FirstOrDefault(c => c != null);
            }
            if (p.PrimaryDisplay is Hardware.Display d)
            {
                inv.Monitor = $"{d.Width}×{d.Height} {d.Hz} Hz";
                inv.MonitorLargura = d.Width; inv.MonitorAltura = d.Height; inv.MonitorHz = d.Hz; inv.MonitorHzMax = d.MaxHz;
            }
            inv.Windows = p.Os + (p.OsBuild > 0 ? $" ({p.OsBuild})" : "");
            inv.Portatil = p.IsLaptop;
            inv.PlanoEnergia = p.PowerPlan;
            inv.Hags = p.Hags;
            inv.ResizableBar = p.ResizableBar;
            inv.PcieLinhas = p.PcieWidth is (uint cur, uint max) ? $"x{cur} (máx. x{max})" : null;
            inv.Discos = p.Drives.Select(x => $"{x.Letter} {x.Model} · {(x.Bus == "NVMe" ? "NVMe" : x.Media)} · {x.SizeGB:0} GB ({x.FreeGB:0} GB livres)").ToList();
        }
        else ctx.Log("⚠ Não consegui ler todo o hardware (WMI lento): continuo com o que há");

        ctx.Nvidia = await GpuAutoTuner.UnsupportedSafeAsync() == null;

        // Agora que se sabe a resolução do monitor, acerta a estimativa da medição da gráfica
        if (ctx.Report.Fases.FirstOrDefault(f => f.Fase == ScanPhase.GpuBase) is PhaseStatus gb && gb.Estado == PhaseState.PorFazer)
            gb.SegundosEstimados = Math.Round((ctx.Lim.BenchMedicao.TotalSeconds + 10) * (2 + (inv.MonitorAltura >= 1400 ? 1 : 0) + (inv.MonitorAltura > 1600 ? 1 : 0)));
        ctx.Log($"✔ PC: {GameCatalog.ShortCpuName(inv.Cpu)} · {GameCatalog.ShortGpuName(inv.Gpu)} · {inv.RamGB:0} GB {inv.RamTipo}" +
                (inv.MonitorHz > 0 ? $" · monitor {inv.Monitor}" : ""));
        ctx.Fase(1);
    }

    // ================================================================== recuperação

    /// <summary>
    /// Chamar no arranque da app (ANTES de aplicar o perfil de OC do arranque). Se havia um scan "em curso", o passo que estava
    /// a ser testado causou o bloqueio/reinício: fica marcado como teto instável, a gráfica volta a Stock e a página pode retomar.
    /// </summary>
    public RecoveryInfo? CheckRecoveryOnStartup()
    {
        TuningState? st;
        try { st = ScanStore.ReadState(); } catch { st = null; }
        if (st == null || !st.EmCurso)
        {
            if (st != null) ScanStore.ClearState();
            // Recuperação anterior ainda por decidir (retomar/dispensar)
            var old = ScanStore.ReadRecovered();
            PendingRecovery = old != null ? BuildInfo(old, null, isNew: false) : null;
            return PendingRecovery;
        }

        // Valores de fábrica primeiro (se a app rebentou sem o PC reiniciar, o OC de teste ainda está na placa)
        bool stockOk = GpuAutoTuner.RestoreStockBlocking(TimeSpan.FromSeconds(8));

        HardwareErrors? ev = null;
        try { ev = OcCheck.ErrorsSince(st.Hora.AddSeconds(-10)); } catch { }
        bool hwSign = ev != null && (ev.UnexpectedRestarts > 0 || ev.BugChecks > 0 || ev.GpuResets > 0);
        bool blame = st.ValoresAplicados && (hwSign || !st.ErroDaApp);

        if (blame && IsOcPhase(st.Fase))
        {
            if (st.Fase == ScanPhase.GpuMemoria && st.MemoriaEmTeste is int m && m > 0)
                st.MemoriaInstavel = st.MemoriaInstavel is int om ? Math.Min(om, m) : m;
            else if (st.NucleoEmTeste is int c && c > 0)
                st.NucleoInstavel = st.NucleoInstavel is int oc ? Math.Min(oc, c) : c;
            if (st.Fase == ScanPhase.GpuValidacao && st.MemoriaEmTeste is int vm && vm > 0)
                st.MemoriaInstavel = st.MemoriaInstavel is int om2 ? Math.Min(om2, vm) : vm;

            if (st.Parcial?.Oc is OcResult o)
            {
                o.NucleoInstavel = st.NucleoInstavel ?? o.NucleoInstavel;
                o.MemoriaInstavel = st.MemoriaInstavel ?? o.MemoriaInstavel;
                o.Passos.Add(new OcStep
                {
                    Tipo = st.Fase == ScanPhase.GpuMemoria ? "memória" : st.Fase == ScanPhase.GpuValidacao ? "validação" : "núcleo",
                    Nucleo = st.NucleoEmTeste ?? 0, Memoria = st.MemoriaEmTeste ?? 0, PotenciaPct = st.PotenciaPct,
                    Passou = false, Motivo = ev?.BugChecks > 0 ? "ecrã azul" : ev?.UnexpectedRestarts > 0 ? "o PC bloqueou/reiniciou" : "a app fechou a meio do teste",
                });
            }
        }

        var info = BuildInfo(st, ev, isNew: true);
        if (!stockOk)
            info.Mensagem += " Atenção: o driver de vídeo não confirmou os valores de fábrica — reinicia o PC antes de jogar.";
        st.EmCurso = false;
        st.ValoresAplicados = false;
        st.NucleoEmTeste = null; st.MemoriaEmTeste = null;
        // Guarda os valores "em teste" na informação mas não no estado a retomar
        ScanStore.WriteState(st);
        ScanStore.MoveStateToRecovered();
        PendingRecovery = info;
        return info;
    }

    private static RecoveryInfo BuildInfo(TuningState st, HardwareErrors? ev, bool isNew)
    {
        var info = new RecoveryInfo
        {
            Perfil = st.Perfil, Fase = st.Fase, Hora = st.Hora,
            NucleoEmTeste = st.NucleoEmTeste, MemoriaEmTeste = st.MemoriaEmTeste,
            UltimoNucleoEstavel = st.UltimoNucleoEstavel, UltimaMemoriaEstavel = st.UltimaMemoriaEstavel,
            PcReiniciou = ev?.UnexpectedRestarts > 0, EcraAzul = ev?.BugChecks > 0,
            PodeRetomar = st.Parcial != null,
            Novo = isNew,
        };
        string fase = TitleOf(st.Fase).ToLowerInvariant();
        string what = info.EcraAzul ? "Houve um ecrã azul" : info.PcReiniciou ? "O PC bloqueou ou reiniciou" : "O scan foi interrompido (a app fechou a meio)";
        string val = IsOcPhase(st.Fase) && st.NucleoEmTeste is int c
            ? $" com +{c} MHz no núcleo" + (st.MemoriaEmTeste is int m && m > 0 ? $" e +{m} na memória" : "")
            : $" durante \"{fase}\"";
        string marked = IsOcPhase(st.Fase) && (st.NucleoInstavel != null || st.MemoriaInstavel != null) ? " Marquei esse valor como instável." : "";
        string resume = info.PodeRetomar
            ? (IsOcPhase(st.Fase) ? $" Podes retomar a partir do último passo estável (+{st.UltimoNucleoEstavel} MHz)." : " Podes retomar o scan.")
            : "";
        info.Mensagem = isNew
            ? $"{what}{val}. Repus a gráfica nos valores de fábrica.{marked}{resume}"
            : $"Há um scan interrompido ({fase}).{resume}";
        if (isNew && st.ErroTexto is string et && et.Contains("não confirmou", StringComparison.Ordinal))
            info.Mensagem = $"O scan anterior parou porque o driver de vídeo não confirmou os valores de fábrica{val}. Voltei a repô-los agora.{marked}{resume}";
        if (!IsOcPhase(st.Fase) && (info.PcReiniciou || info.EcraAzul) && st.Fase is ScanPhase.Cpu or ScanPhase.Ram)
            info.Mensagem += " Um reinício no teste da CPU/RAM costuma indicar PBO/Curve Optimizer ou XMP instáveis.";
        return info;
    }

    /// <summary>Esquece o scan interrompido (o utilizador não quer retomar).</summary>
    public void DismissRecovery()
    {
        ScanStore.ClearRecovered();
        PendingRecovery = null;
    }

    // ================================================================== fecho da app / erros

    /// <summary>Fecho normal da app: pára o scan e repõe Stock (prazo curto; nunca prende o fecho).</summary>
    public void OnAppExit()
    {
        if (!_running) return;
        Cancel(); // antes do Stock: um Apply que ainda não entrou no driver vê o cancelamento e não escreve
        bool ok = GpuAutoTuner.RestoreStockBlocking(TimeSpan.FromSeconds(4));
        // Não foi um bloqueio: não há nada a recuperar no próximo arranque. Se o Stock não foi confirmado, o estado fica
        // gravado para o próximo arranque voltar a repor (e o thread do scan, que ainda corre, já não grava mais nada).
        if (ok) ScanStore.ClearAndFreeze();
        else if (_state is TuningState st)
        {
            st.ErroDaApp = true;
            st.ValoresAplicados = true;
            st.ErroTexto = "a app fechou sem confirmar os valores de fábrica";
            ScanStore.WriteState(st);
            ScanStore.Frozen = true;
        }
    }

    /// <summary>
    /// Exceção não tratada na app enquanto o scan corre: pára e repõe Stock. Se for fatal, o estado fica gravado com
    /// "erro da app" (no próximo arranque o valor em teste só é culpado se o Windows registar bloqueio/crash do driver).
    /// </summary>
    public void OnUnhandledException(Exception? ex, bool fatal)
    {
        if (!_running) return;
        var st = _state;
        if (st != null)
        {
            st.ErroDaApp = true;
            st.ErroTexto = ex?.Message;
            ScanStore.WriteState(st);
        }
        Cancel();
        bool ok = GpuAutoTuner.RestoreStockBlocking(TimeSpan.FromSeconds(3));
        if (fatal && st != null && ok) { st.ValoresAplicados = false; ScanStore.WriteState(st); }
        if (fatal) ScanStore.Frozen = true; // o processo vai acabar: o thread do scan não sobrescreve este estado
    }

    private static void LogError(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(AppPaths.Root, "erros.log"), $"[{DateTime.Now}] Scan: {ex}\n\n"); } catch { }
    }

    // ================================================================== resultado: perfis e histórico

    public List<ScanReport> History(int max = 10) => ScanStore.History(max);

    /// <summary>O perfil "Auto · …" guardado por um relatório (null se o OC não foi estável ou o perfil foi apagado).</summary>
    public static OcProfile? FoundProfile(ScanReport? r)
    {
        string? name = r?.Oc?.NomePerfil;
        if (name == null) return null;
        lock (AppServices.Settings.OcProfiles) return AppServices.Settings.OcProfiles.FirstOrDefault(p => p.Name == name);
    }

    /// <summary>Aplica já o overclock encontrado. Devolve o registo do driver (vazio se não há perfil).</summary>
    public static Task<List<string>> ApplyFoundAsync(ScanReport r)
    {
        var p = FoundProfile(r);
        if (p == null) return Task.FromResult(new List<string> { "✖ Não há perfil estável deste scan." });
        if (AppServices.Settings.UnstableOcProfiles.Contains(p.Name)) return Task.FromResult(new List<string> { "✖ Este perfil foi marcado como instável." });
        if (Instance.Running) return Task.FromResult(new List<string> { "✖ Há um scan a correr: espera que acabe." });
        return Task.Run(() => AppServices.Tuner.Apply(p));
    }

    /// <summary>"Aplicar sempre ao abrir": usa o OcApplyOnStart que já existe.</summary>
    public static void SetApplyOnStart(string profileName, bool on)
    {
        var s = AppServices.Settings;
        if (on) s.OcApplyOnStart = profileName;
        else if (s.OcApplyOnStart == profileName) s.OcApplyOnStart = null;
        s.Save();
    }

    public static bool IsApplyOnStart(string profileName) => AppServices.Settings.OcApplyOnStart == profileName;

    /// <summary>"Usar nos jogos": põe o perfil em todos os jogos que já têm perfil (ou tira-o). Devolve quantos mudaram.</summary>
    public static int UseInGames(string profileName, bool on)
    {
        var s = AppServices.Settings;
        int n = 0;
        foreach (var g in s.Profiles.Values)
        {
            if (on && g.OcProfile != profileName) { g.OcProfile = profileName; n++; }
            else if (!on && g.OcProfile == profileName) { g.OcProfile = null; n++; }
        }
        if (n > 0) s.Save();
        return n;
    }

    // ================================================================== progresso

    private sealed class Emitter
    {
        private readonly ScanService _svc;
        private readonly IProgress<ScanProgress>? _progress;
        private readonly ScanReport _r;
        private readonly object _lock = new();
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private readonly Stopwatch _phase = new();
        private PhaseStatus? _cur;
        private double _p;
        private string _estado = "";
        private string? _passo;

        public Emitter(ScanService svc, IProgress<ScanProgress>? progress, ScanReport r, ScanContext ctx)
        {
            _svc = svc; _progress = progress; _r = r;
            ctx.OnLog = line => Emit(line, null);
            ctx.OnEstado = e => { lock (_lock) _estado = e; Emit(null, null); };
            ctx.OnPasso = s => { lock (_lock) _passo = s; Emit(null, null); };
            ctx.OnFase = p => { lock (_lock) _p = p; };
            ctx.OnLive = v => Emit(null, v);
        }

        public void Begin(PhaseStatus ph)
        {
            lock (_lock) { _cur = ph; _p = 0; _estado = ph.Titulo + "…"; _passo = null; }
            ph.Estado = PhaseState.ACorrer;
            _phase.Restart();
            Emit(null, null);
        }

        public void End(PhaseStatus ph)
        {
            ph.SegundosReais = Math.Round(_phase.Elapsed.TotalSeconds);
            lock (_lock) { _p = 1; _passo = null; }
            Emit(null, null);
        }

        public void Final()
        {
            lock (_lock) { _cur = null; _p = 1; _passo = null; _estado = _r.Resultado == ScanOutcome.Completo ? "Concluído" : _r.Motivo ?? "Parado"; }
            Emit(null, null);
        }

        private void Emit(string? line, LiveValues? live)
        {
            ScanProgress snap;
            lock (_lock)
            {
                var phases = _r.Fases;
                double total = phases.Sum(f => f.Estado == PhaseState.Saltado ? 0 : f.SegundosEstimados);
                double done = 0, remaining = 0;
                int idx = 0;
                foreach (var f in phases)
                {
                    if (f.Estado == PhaseState.Saltado) continue;
                    if (ReferenceEquals(f, _cur))
                    {
                        done += f.SegundosEstimados * _p;
                        double left = f.SegundosEstimados * (1 - _p);
                        double byClock = f.SegundosEstimados - _phase.Elapsed.TotalSeconds;
                        remaining += Math.Max(_p > 0.05 ? left : byClock, 0);
                    }
                    else if (f.Estado is PhaseState.Feito or PhaseState.Falhou) done += f.SegundosEstimados;
                    else if (f.Estado == PhaseState.PorFazer) remaining += f.SegundosEstimados;
                }
                if (_cur != null) idx = phases.IndexOf(_cur) + 1;
                double pct = _cur == null && _r.Resultado != ScanOutcome.EmCurso ? 100 : total > 0 ? Math.Min(99.5, 100 * done / total) : 0;
                snap = new ScanProgress
                {
                    Fase = _cur?.Fase ?? ScanPhase.Analise,
                    FaseTitulo = _cur?.Titulo ?? "",
                    FaseIndice = idx,
                    FaseTotal = phases.Count,
                    Percent = Math.Round(pct, 1),
                    FasePercent = _p,
                    Restante = TimeSpan.FromSeconds(Math.Round(remaining)),
                    Decorrido = _total.Elapsed,
                    Linha = line,
                    Estado = _estado,
                    PassoOc = _passo,
                    AoVivo = live ?? _svc.Current?.AoVivo,
                    Fases = phases.Select(f => new PhaseStatus
                    {
                        Fase = f.Fase, Titulo = f.Titulo, Estado = f.Estado, Nota = f.Nota,
                        SegundosEstimados = f.SegundosEstimados, SegundosReais = f.SegundosReais,
                    }).ToList(),
                };
                _svc.Current = snap;
            }
            try { _progress?.Report(snap); } catch { }
            try { _svc.ProgressChanged?.Invoke(snap); } catch { }
        }
    }

    // ================================================================== apoio

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus s);

    private static bool OnBattery()
    {
        try { return GetSystemPowerStatus(out var s) && s.ACLineStatus == 0 && s.BatteryFlag != 128; } catch { return false; }
    }
}
