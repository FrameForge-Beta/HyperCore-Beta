using System.Diagnostics;
using System.IO;
using FrameForge.Core;
using FrameForge.Hardware;
using ResetExplode;

namespace FrameForge.Tuning;

/// <summary>
/// Medições de CPU, RAM e disco do scan. A CPU e a RAM não têm overclock por software: aqui só se mede e verifica
/// (notas, CPU de jogo, stress com verificação de cálculo e padrão de RAM, temperaturas/throttle) e o relatório
/// recomenda os passos de BIOS (<see cref="BiosGuide"/>).
/// </summary>
public static class CpuRamDiskScan
{
    // ------------------------------------------------------------------ CPU

    internal static async Task RunCpuAsync(ScanContext ctx)
    {
        var cpu = ctx.Report.Cpu ??= new CpuResult();
        var tracker = new ThermalTracker();
        int threads = Environment.ProcessorCount;
        var bench = new BenchRenderer();

        if (ScanSensors.Read().CpuTemp is not > 0)
            ctx.Log("• Sem leitura de temperatura da CPU (precisa do driver PawnIO): o teste conta com a proteção do próprio processador.");

        // 1) Nota multi-thread (uma imagem completa; num CPU lento conta o que fez em 45 s)
        ctx.Estado($"A medir a CPU com {threads} threads…");
        ctx.Sub(0, 0.14);
        var mt = await ctx.WithLiveAsync(t => bench.Run(threads, TimeSpan.Zero, t), TimeSpan.FromSeconds(45), tracker,
                                         () => bench.BucketsTotal > 0 ? (double)bench.BucketsDone / bench.BucketsTotal : 0);
        ctx.ThrowIfCancelled();
        if (mt.Result is BenchResult m && m.Seconds > 1 && m.Samples > 0)
        {
            cpu.NotaMulti = Math.Round(m.Score);
            ctx.Log($"✔ CPU (todos os núcleos): {cpu.NotaMulti:N0} pontos");
        }
        else ctx.Log("✖ Não consegui medir a CPU em multi-thread" + (mt.Stop != null ? $": {mt.Stop}" : ""));
        if (mt.Thermal) cpu.Problemas.Add(mt.Stop!);

        // 2) Nota com 1 thread (20 s; conta amostras por segundo)
        ctx.Estado("A medir a CPU com 1 thread…");
        ctx.Sub(0.14, 0.24);
        var sw1 = Stopwatch.StartNew();
        var st = await ctx.WithLiveAsync(t => bench.Run(1, TimeSpan.Zero, t), TimeSpan.FromSeconds(20), tracker,
                                         () => sw1.Elapsed.TotalSeconds / 20);
        ctx.ThrowIfCancelled();
        if (st.Result is BenchResult s1 && s1.Seconds > 1 && s1.Samples > 0)
        {
            cpu.Nota1Thread = Math.Round(s1.Score);
            ctx.Log($"✔ CPU (1 núcleo): {cpu.Nota1Thread:N0} pontos");
        }

        // 3) CPU de jogo (simulação de 30 mil unidades)
        ctx.Estado("A simular um jogo pesado na CPU…");
        ctx.Sub(0.24, 0.36);
        var sim = new GameSimBench();
        var sr = await ctx.WithLiveAsync(t => sim.Run(TimeSpan.FromSeconds(15), t), TimeSpan.FromSeconds(45), tracker, () => sim.Progress);
        ctx.ThrowIfCancelled();
        if (sr.Result is GameSimResult g && g.Frames > 0)
        {
            cpu.FpsJogo = Math.Round(g.AvgFps);
            cpu.FpsJogo1Low = Math.Round(g.Low1Fps);
            cpu.MsThreadPrincipal = Math.Round(g.MainThreadMs, 2);
            ctx.Log($"✔ CPU de jogo: {cpu.FpsJogo:0} frames/s de simulação (1% low {cpu.FpsJogo1Low:0})");
        }

        // 4) Verificação determinística rápida: a mesma simulação duas vezes tem de dar exatamente o mesmo resultado
        ctx.Estado("A verificar os cálculos da CPU…");
        ctx.Sub(0.36, 0.42);
        var ver = await ctx.WithLiveAsync(t => (A: sim.Verify(400, t), B: sim.Verify(400, t)), TimeSpan.FromSeconds(90), tracker);
        ctx.ThrowIfCancelled();
        if (ver.Ok && ver.Result.A is ulong a && ver.Result.B is ulong b && a != b)
        {
            cpu.ErroCalculo = true;
            cpu.Problemas.Add("erro de cálculo: a mesma simulação deu resultados diferentes");
            ctx.Log("✖ A CPU deu resultados diferentes para o mesmo cálculo (sinal de instabilidade de CPU/RAM)");
        }

        // 5) Stress CPU + RAM com verificação (duração do perfil)
        ctx.Estado($"Stress da CPU e da RAM ({ctx.Lim.StressCpu.TotalMinutes:0} min)…");
        ctx.Sub(0.42, 1);
        var r = await ctx.Runner.RunAsync(new LoadSpec { Kind = LoadKind.CpuRam, Duration = ctx.Lim.StressCpu, RamFraction = 0.5, Label = "CPU + RAM" }, ctx.Ct);
        if (r.Cancelado && ctx.Ct.IsCancellationRequested) throw new OperationCanceledException(ctx.Ct);
        var pre = tracker.Cpu();
        if (pre.TempMax > (r.Cpu.TempMax ?? 0)) r.Cpu.TempMax = pre.TempMax; // o pico pode ter sido nas medições
        cpu.Stress = r.Cpu;
        cpu.MinutosStress = (int)Math.Round(r.Segundos / 60);
        cpu.ErrosRam = r.ErrosRam;
        cpu.ErrosWhea = r.Eventos is HardwareErrors ev ? ev.WheaFatal + ev.WheaCorrectedCpu + ev.WheaCorrectedMemory : 0;
        if (r.Motivo.Contains("cálculo", StringComparison.OrdinalIgnoreCase)) cpu.ErroCalculo = true;
        if (r.Termico) cpu.Stress.ThrottleTermico = true;

        if (r.Passou)
            ctx.Log($"✔ Stress da CPU/RAM sem erros · máx. {Fmt(r.Cpu.TempMax, "°C")}" + (r.Cpu.PotenciaMaxW is double w ? $" · {w:0} W" : ""));
        else
        {
            cpu.Problemas.Add(r.Motivo);
            ctx.Log((r.Termico ? "⚠ Parei o stress da CPU: " : "✖ Stress da CPU/RAM: ") + r.Motivo);
        }
        if (cpu.Stress.ThrottleTermico && !r.Termico) ctx.Log("⚠ O clock da CPU caiu com o calor (throttle térmico)");
        cpu.Estavel = !cpu.ErroCalculo && cpu.ErrosRam == 0 && cpu.ErrosWhea == 0 && (r.Passou || r.Termico);
    }

    // ------------------------------------------------------------------ RAM

    internal static async Task RunRamAsync(ScanContext ctx)
    {
        var ram = ctx.Report.Ram ??= new RamResult();
        var p = ctx.Probe;
        if (p != null && p.Ram.Count > 0)
        {
            ram.MHz = p.Ram.Min(x => x.ConfiguredMHz);
            var rated = p.Ram.Where(x => x.RatedMHz != null).Select(x => x.RatedMHz!.Value).ToList();
            ram.MHzNominal = rated.Count > 0 ? rated.Min() : null;
            ram.XmpDesligado = ram.MHzNominal is int n && ram.MHz > 0 && ram.MHz < n - 150;
            ram.Modulos = p.Ram.Select(x => $"{x.SizeGB:0} GB {x.Manufacturer} {x.PartNumber}".Trim() +
                                           $" · {x.ConfiguredMHz} MHz" + (x.RatedCl is int cl ? $" CL{cl}" : "") +
                                           (x.Bank.Length + x.Slot.Length > 0 ? $" · {x.Slot}".TrimEnd() : "")).ToList();
        }

        ctx.Estado("A medir a latência da RAM…");
        ctx.Sub(0, 0.4);
        var lat = await ctx.WithLiveAsync(t => MemoryBench.MeasureLatencyNs(t), TimeSpan.FromSeconds(60));
        ctx.ThrowIfCancelled();
        if (lat.Ok && lat.Result > 0) ram.LatenciaNs = Math.Round(lat.Result, 1);

        ctx.Estado("A medir a largura de banda da RAM…");
        ctx.Sub(0.4, 1);
        var bw = await ctx.WithLiveAsync(t => MemoryBench.MeasureBandwidth(t), TimeSpan.FromSeconds(90));
        ctx.ThrowIfCancelled();
        if (bw.Ok && bw.Result.Read > 0) { ram.LeituraGBs = Math.Round(bw.Result.Read, 1); ram.CopiaGBs = Math.Round(bw.Result.Copy, 1); }

        // Canal único pela banda medida: abaixo de ~1,1× a banda teórica de UM canal de 64 bits (MT/s × 8 B)
        int mts = ram.MHz < 1300 && ram.MHz > 0 ? ram.MHz * 2 : ram.MHz; // algumas placas reportam o clock real (metade)
        double perChannel = mts * 8 / 1000.0;
        ram.CanalUnicoProvavel = (p?.Ram.Count == 1) || (ram.LeituraGBs is double rd && perChannel > 0 && rd < perChannel * 1.1);

        ctx.Log($"✔ RAM: {Fmt(ram.LatenciaNs, "ns")} de latência · {Fmt(ram.LeituraGBs, "GB/s")} de leitura" +
                (ram.MHz > 0 ? $" · {ram.MHz} MHz" : "") + (ram.XmpDesligado ? " (XMP/EXPO desligado?)" : ""));
    }

    // ------------------------------------------------------------------ Disco

    /// <summary>
    /// Disco do sistema e, se for outro, o disco onde estão os jogos. Com <paramref name="allowScan"/> = false não procura os jogos
    /// (usa só o que já se sabe; para o plano, que tem de ser instantâneo) e começa a procura em segundo plano.
    /// </summary>
    public static List<(string Root, bool Sistema, bool Jogos)> DiskTargets(bool allowScan = true)
    {
        var list = new List<(string, bool, bool)>();
        string sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        string? games;
        if (allowScan) games = GamesDrive();
        else
        {
            lock (GamesLock) games = _gamesDrive;
            if (!_gamesKnown) _ = Task.Run(GamesDrive);
        }
        list.Add((sys.ToUpperInvariant(), true, games == null || string.Equals(games, sys, StringComparison.OrdinalIgnoreCase)));
        if (games != null && !string.Equals(games, sys, StringComparison.OrdinalIgnoreCase)) list.Add((games.ToUpperInvariant(), false, true));
        return list;
    }

    private static readonly object GamesLock = new();
    private static string? _gamesDrive;
    private static volatile bool _gamesKnown;

    /// <summary>A unidade com mais jogos instalados (Steam, Epic, manuais…); null se não houver jogos.</summary>
    public static string? GamesDrive()
    {
        string? d = FindGamesDrive();
        lock (GamesLock) { _gamesDrive = d; _gamesKnown = true; }
        return d;
    }

    private static string? FindGamesDrive()
    {
        try
        {
            var task = Task.Run(() => Games.GameLibrary.Scan(AppServices.Settings));
            if (!task.Wait(TimeSpan.FromSeconds(10))) return null;
            return task.Result.Select(g => g.InstallDir.Length > 0 ? g.InstallDir : g.ExePath ?? "")
                       .Where(d => d.Length > 2 && d[1] == ':')
                       .Select(d => Path.GetPathRoot(d)!)
                       .GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
                       .OrderByDescending(gr => gr.Count())
                       .Select(gr => gr.Key)
                       .FirstOrDefault();
        }
        catch { return null; }
    }

    internal static async Task RunDiskAsync(ScanContext ctx)
    {
        const long fileMb = 512;
        var targets = DiskTargets();
        ctx.Report.Discos.Clear();

        // Saúde S.M.A.R.T. de todos os discos (para cruzar por letra)
        ctx.Estado("A ler a saúde dos discos…");
        ctx.Sub(0, 0.1);
        var health = await ctx.WithLiveAsync(_ => DiskHealth.Read(), TimeSpan.FromSeconds(60));
        ctx.ThrowIfCancelled();
        var cards = health.Ok ? health.Result : new List<DiskHealthCard>();

        for (int i = 0; i < targets.Count; i++)
        {
            var (root, sistema, jogos) = targets[i];
            string letter = root.TrimEnd('\\');
            var info = ctx.Probe?.Drives.FirstOrDefault(d => string.Equals(d.Letter, letter, StringComparison.OrdinalIgnoreCase));
            var d = new DiskResult
            {
                Unidade = letter, Sistema = sistema, Jogos = jogos,
                Modelo = info?.Model ?? "?", Tipo = info == null ? "?" : info.Bus == "NVMe" ? "NVMe" : info.Media,
                TamanhoGB = Math.Round(info?.SizeGB ?? 0), LivreGB = Math.Round(info?.FreeGB ?? 0),
            };
            var card = cards.FirstOrDefault(c => c.Meta.Contains(letter, StringComparison.OrdinalIgnoreCase));
            if (card != null)
            {
                d.Saude = card.VerdictText;
                d.SaudeAlerta = card.Verdict is DiskVerdict.Warning or DiskVerdict.Critical;
            }
            ctx.Report.Discos.Add(d);

            // Disco com a saúde em estado crítico: não se escreve 512 MB num disco que pode estar a morrer
            if (card?.Verdict == DiskVerdict.Critical)
            {
                d.Erro = "não testei a velocidade: a saúde do disco está crítica (faz já cópia de segurança)";
                ctx.Log($"⚠ {letter}: saúde crítica — saltei o teste de escrita");
                continue;
            }

            try
            {
                var di = new DriveInfo(root);
                if (di.AvailableFreeSpace < (fileMb + 600) << 20) { d.Erro = "pouco espaço livre para o teste"; ctx.Log($"• {letter} sem espaço para o teste de velocidade"); continue; }
            }
            catch (Exception ex) { d.Erro = ex.Message; continue; }

            ctx.Estado($"A medir a velocidade do disco {letter}" + (sistema ? " (sistema)" : " (jogos)") + "…");
            double a = 0.1 + 0.9 * i / targets.Count, b = 0.1 + 0.9 * (i + 1) / targets.Count;
            ctx.Sub(a, b);
            double frac = 0;
            var r = await ctx.WithLiveAsync(t => DiskBench.Run(root, fileMb, (_, f) => frac = f, t), TimeSpan.FromMinutes(4), progress: () => frac);
            ctx.ThrowIfCancelled();
            if (r.Result is DiskBenchResult res)
            {
                d.LeituraMBs = Math.Round(res.SeqReadMBs);
                d.EscritaMBs = Math.Round(res.SeqWriteMBs);
                d.Iops4kQ1 = Math.Round(res.ReadIopsQ1);
                d.LatenciaUs = Math.Round(res.ReadLatencyUs);
                ctx.Log($"✔ Disco {letter}: {d.LeituraMBs:N0} MB/s leitura · {d.EscritaMBs:N0} MB/s escrita" + (d.Saude != null ? $" · saúde {d.Saude}" : ""));
            }
            else
            {
                d.Erro = r.Stop ?? "não foi possível medir";
                ctx.Log($"✖ Disco {letter}: {d.Erro}");
            }
        }
    }

    internal static string Fmt(double? v, string unit) => v is double x ? $"{x:0.#} {unit}" : "?";
}
