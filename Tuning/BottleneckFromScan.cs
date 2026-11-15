using System.IO;
using System.Text.Json;
using FrameForge.Core;
using ResetExplode;

namespace FrameForge.Tuning;

/// <summary>
/// "O que está a segurar o PC", a partir do scan. Os testes sintéticos (cena de jogo da GPU, simulação da CPU) não estão
/// na mesma escala, por isso a balança CPU↔GPU usa os índices de análises publicadas (RTX 4070 = 100; 7700X = 100)
/// CORRIGIDOS pelo que o scan mediu neste PC (RAM sem XMP/canal único, throttle térmico, overclock estável), num
/// "jogo exigente típico" (mediana do catálogo de jogos). Se houver sessões de jogo gravadas, essas mandam: são medições reais.
/// </summary>
public static class BottleneckFromScan
{
    public static LimiterResult Compute(ScanReport r)
    {
        var inv = r.Inventario;
        var lr = new LimiterResult { MonitorHz = inv.MonitorHz };

        // 1) Índices de referência das peças
        var gpuM = Safe(() => PerfIndex.FindGpu(inv.Gpu, inv.VramGB));
        var cpuM = Safe(() => PerfIndex.FindCpu(inv.Cpu));
        double? g1080 = gpuM?.Indice1080 ?? Safe(() => GameCatalog.GpuPerf(inv.Gpu));
        double? g1440 = gpuM?.Indice1440 ?? g1080;
        double? g4k = gpuM?.Indice4k ?? g1440;
        double? cIdx = cpuM?.IndiceJogo ?? Safe(() => GameCatalog.CpuPerf(inv.Cpu));

        // 2) Correções medidas
        double cpuF = 1, gpuF = 1;
        var why = new List<string>();
        if (r.Ram?.XmpDesligado == true) { cpuF *= 0.92; why.Add("RAM sem XMP/EXPO (−8% na CPU)"); }
        if (r.Ram?.CanalUnicoProvavel == true) { cpuF *= 0.85; why.Add("RAM em canal único (−15% na CPU)"); }
        else if (r.Ram?.LatenciaNs is double lat && lat > LatencyHigh(inv.RamTipo)) { cpuF *= 0.95; why.Add($"latência da RAM alta ({lat:0} ns, −5%)"); }
        if (r.Cpu?.Stress.ThrottleTermico == true) { cpuF *= 0.93; why.Add("CPU a perder clock com o calor (−7%)"); }
        if (r.Gpu != null && (r.Gpu.Jogo.ThrottleTermico)) { gpuF *= 0.95; why.Add("gráfica a perder clock com o calor (−5%)"); }
        if (r.Oc is { Estavel: true, GanhoPct: double gain } && gain > 0) { gpuF *= 1 + gain / 100; why.Add($"overclock estável (+{gain:0.#}%)"); }

        // 3) "Jogo exigente típico": mediana do catálogo (FPS numa RTX 4070 em 1080p alto / teto de um 7700X)
        double gpuRef = Median(GameCatalog.Games.Select(x => x.GpuRef));
        double cpuCap = Median(GameCatalog.Games.Select(x => x.CpuCap));

        if (cIdx is double ci) lr.TetoCpuFps = Math.Round(cpuCap * ci / 100 * cpuF);
        if (g1080 is double a) lr.TetoGpu1080Fps = Math.Round(gpuRef * a / 100 * gpuF);
        if (g1440 is double b) lr.TetoGpu1440Fps = Math.Round(gpuRef * b / 100 * GameCatalog.ResolutionFactor("2560x1440") * gpuF);

        int h = inv.MonitorAltura;
        lr.Resolucao = h > 1700 ? "4K" : h >= 1400 ? "1440p" : "1080p";
        lr.TetoGpuMonitorFps = lr.Resolucao switch
        {
            "4K" => g4k is double k ? Math.Round(gpuRef * k / 100 * GameCatalog.ResolutionFactor("3840x2160") * gpuF) : null,
            "1440p" => lr.TetoGpu1440Fps,
            _ => lr.TetoGpu1080Fps,
        };

        // 4) Sessões de jogo gravadas (medição real): percentagem do tempo com GPU ou CPU no limite
        var (n, gpuPct, cpuPct) = Sessions();
        lr.SessoesUsadas = n;
        lr.SessoesGpuPct = gpuPct;
        lr.SessoesCpuPct = cpuPct;

        double? cpu = lr.TetoCpuFps, gpu = lr.TetoGpuMonitorFps;
        string res = lr.Resolucao;
        if (cpu is double c && gpu is double gg)
        {
            double ratio = c / gg;
            if (inv.MonitorHz > 0 && Math.Min(c, gg) >= inv.MonitorHz * 0.95)
            {
                lr.Principal = "Monitor";
                lr.Frases.Add($"Num jogo exigente típico em {res} o teu PC dá ~{Math.Min(c, gg):0} FPS, mais do que os {inv.MonitorHz} Hz do monitor: o ecrã é o limite.");
            }
            else if (ratio >= 1.15)
            {
                lr.Principal = "GPU";
                lr.Frases.Add($"Em {res} a tua gráfica é o limite (a CPU aguenta ~{c:0} FPS, a gráfica dá ~{gg:0}).");
            }
            else if (ratio <= 0.87)
            {
                lr.Principal = "CPU";
                lr.Frases.Add($"Em {res} o processador é o limite (a CPU aguenta ~{c:0} FPS, a gráfica dava ~{gg:0}).");
            }
            else
            {
                lr.Principal = "Equilibrado";
                lr.Frases.Add($"Em {res} CPU e gráfica estão equilibradas (~{Math.Min(c, gg):0} FPS num jogo exigente típico).");
            }
        }
        else lr.Principal = "Desconhecido";

        // As sessões reais confirmam ou corrigem
        if (n >= 2 && gpuPct is double gp && cpuPct is double cp)
        {
            string? real = gp >= 60 ? "GPU" : cp >= 35 ? "CPU" : null;
            if (real != null && real != lr.Principal && lr.Principal is not "Monitor")
            {
                lr.Principal = real;
                lr.Frases.Insert(0, real == "GPU"
                    ? $"Nos teus jogos gravados a gráfica esteve no limite {gp:0}% do tempo: é ela que segura o FPS."
                    : $"Nos teus jogos gravados o processador esteve no limite {cp:0}% do tempo: é ele que segura o FPS.");
            }
            else if (real != null)
                lr.Frases.Add($"Confirmado nas tuas {n} sessões gravadas ({(real == "GPU" ? $"gráfica no limite {gp:0}%" : $"CPU no limite {cp:0}%")} do tempo).");
        }

        if (why.Count > 0 && lr.Principal is "CPU" or "Equilibrado" && cpuF < 0.97)
            lr.Frases.Add("A CPU está a render menos do que podia: " + string.Join(", ", why.Where(w => !w.StartsWith("gráfica") && !w.StartsWith("overclock"))) + ".");

        lr.Metodo = "Índices de análises publicadas (RTX 4070 = 100, Ryzen 7 7700X = 100) num jogo exigente típico, " +
                    "corrigidos pelas medições deste PC" + (why.Count > 0 ? $" ({string.Join("; ", why)})" : "") +
                    (n > 0 ? $", e {n} sessões de jogo gravadas." : ".") +
                    (gpuM == null && g1080 == null ? " A gráfica não está no índice." : "") +
                    (cpuM == null && cIdx == null ? " O processador não está no índice." : "");
        return lr;
    }

    /// <summary>Latência a partir da qual a RAM é "lenta" (ns, medida pelo MemoryBench).</summary>
    public static double LatencyHigh(string? ramType) => ramType == "DDR5" ? 95 : ramType == "DDR4" ? 85 : 90;

    /// <summary>Média de %GPU/%CPU no limite nas últimas 20 sessões gravadas (resumos JSON dos relatórios).</summary>
    private static (int N, double? GpuPct, double? CpuPct) Sessions()
    {
        try
        {
            var dir = new DirectoryInfo(AppPaths.Reports);
            var g = new List<double>();
            var c = new List<double>();
            foreach (var f in dir.GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(20))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f.FullName));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("GpuBoundPct", out var gp) && root.TryGetProperty("CpuBoundPct", out var cp) &&
                        gp.TryGetDouble(out double gv) && cp.TryGetDouble(out double cv))
                    { g.Add(gv); c.Add(cv); }
                }
                catch { }
            }
            return g.Count == 0 ? (0, null, null) : (g.Count, Math.Round(g.Average()), Math.Round(c.Average()));
        }
        catch { return (0, null, null); }
    }

    private static double Median(IEnumerable<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        if (s.Count == 0) return 100;
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }
}
