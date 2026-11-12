using FrameForge.Hardware;
using FrameForge.Telemetry;

namespace FrameForge.Analysis;

/// <summary>Tudo o que foi gravado numa sessão de jogo.</summary>
public sealed class SessionData
{
    public string Game { get; set; } = "";
    public string? Exe { get; set; }
    public string? AntiCheat { get; set; }
    public DateTime Start { get; set; }
    public double DurationMs { get; set; }
    public List<double> Presents { get; set; } = new(); // instantes (ms) de cada frame
    public List<Sample> Samples { get; set; } = new();
    public int MonitorHz { get; set; } = 60;
    public string? OcProfile { get; set; }
    public int OptimizeLevel { get; set; }
    public string Cpu { get; set; } = "";
    public string Gpu { get; set; } = "";
    public double RamGB { get; set; }
    public double VramGB { get; set; }
    public string Board { get; set; } = "";
    public string? GameDriveMedia { get; set; }
}

public enum Cause
{
    VramFull, RamPaging, GpuThermal, GpuPowerBrake, DriverLatency, BackgroundCpu, BackgroundGpu, Disk,
    CpuThrottle, CpuBound, GpuBound, Network, Engine, AltTab
}

/// <summary>Como se resolve: ajuste no jogo, ajuste no PC, rede, limite do hardware ou do próprio jogo.</summary>
public enum FixKind { GameSettings, PcFix, Network, Hardware, GameSide, None }

public sealed record StutterEvent(double T, double WorstMs, double MedianMs, int Frames, string Severity, Cause Cause,
                                  string Detail, Cause? Secondary);

public sealed record Finding(string Title, string Detail, string Fix, FixKind Kind, int Severity /*1 info, 2 aviso, 3 grave*/, int Count = 0);

public sealed class SessionReport
{
    public SessionData Data { get; init; } = new();
    public int Frames { get; set; }
    public double DurationS { get; set; }
    public double AvgFps { get; set; }
    public double Low1Fps { get; set; }
    public double Low01Fps { get; set; }
    public double MedianFt { get; set; }
    public double P99Ft { get; set; }
    public double PacingPct { get; set; }        // % de frames a ±20% da mediana (consistência)
    public double StuttersPerMin { get; set; }
    public int LoadingPauses { get; set; }
    public List<StutterEvent> Events { get; } = new();
    public Dictionary<Cause, int> CauseCounts { get; } = new();
    public double GpuBoundPct { get; set; }
    public double CpuBoundPct { get; set; }
    public double CappedPct { get; set; }
    public List<Finding> Findings { get; } = new();
    public string Verdict { get; set; } = "";
    public string VerdictDetail { get; set; } = "";
    public string Grade { get; set; } = "";       // A..E
    public List<(double T, double Ft)> FrameSeries { get; } = new();   // reduzido para os gráficos
    public List<(double T, double Fps)> FpsSeries { get; } = new();
}

/// <summary>
/// Deteta quebras frame a frame e cruza cada uma com o que os sensores viam nesse instante, para dizer de onde veio:
/// VRAM cheia, RAM/paginação, temperatura, drivers (DPC), um programa em segundo plano, disco, CPU, GPU, rede ou o próprio jogo.
/// </summary>
public static class SessionAnalyzer
{
    public static SessionReport Analyze(SessionData d, double sensitivity = 1.0)
    {
        var r = new SessionReport { Data = d };
        var t = d.Presents;
        if (t.Count < 30)
        {
            r.Verdict = "Sem frames suficientes";
            r.VerdictDetail = "O jogo não entregou frames por DXGI/D3D9 (pode ser Vulkan/OpenGL) ou a sessão foi curta demais.";
            r.Grade = "?";
            return r;
        }

        var ft = new double[t.Count - 1];
        for (int i = 1; i < t.Count; i++) ft[i - 1] = t[i] - t[i - 1];
        r.Frames = ft.Length;
        r.DurationS = (t[^1] - t[0]) / 1000;

        var sorted = (double[])ft.Clone();
        Array.Sort(sorted);
        r.MedianFt = sorted[sorted.Length / 2];
        // Excluir pausas/carregamentos (> 1,5 s) das médias
        var play = ft.Where(x => x < 1500).ToArray();
        r.LoadingPauses = ft.Length - play.Length;
        double playMs = play.Sum();
        r.AvgFps = playMs > 0 ? play.Length * 1000 / playMs : 0;
        var ps = play.OrderBy(x => x).ToArray();
        r.P99Ft = ps[Math.Min(ps.Length - 1, (int)(ps.Length * 0.99))];
        r.Low1Fps = 1000 / AvgOfWorst(ps, 0.01);
        r.Low01Fps = 1000 / AvgOfWorst(ps, 0.001);
        r.PacingPct = 100.0 * play.Count(x => Math.Abs(x - r.MedianFt) <= r.MedianFt * 0.2) / play.Length;

        BuildSeries(r, t, ft);
        var baseline = Baseline(d.Samples);
        DetectStutters(r, t, ft, d, baseline, sensitivity);
        Bound(r, d, t);
        Conclude(r, d, baseline);
        return r;
    }

    private static double AvgOfWorst(double[] ascending, double frac)
    {
        int n = Math.Max(1, (int)(ascending.Length * frac));
        double s = 0;
        for (int i = ascending.Length - n; i < ascending.Length; i++) s += ascending[i];
        return s / n;
    }

    private static void BuildSeries(SessionReport r, List<double> t, double[] ft)
    {
        // Frametime: até ~2400 pontos, guardando o PIOR frame de cada bloco (para as quebras não desaparecerem do gráfico)
        int bucket = Math.Max(1, ft.Length / 2400);
        for (int i = 0; i < ft.Length; i += bucket)
        {
            int end = Math.Min(ft.Length, i + bucket), worst = i;
            for (int k = i; k < end; k++) if (ft[k] > ft[worst]) worst = k;
            r.FrameSeries.Add((t[worst + 1] / 1000, ft[worst]));
        }
        // FPS por segundo
        int j = 0;
        for (double s = Math.Floor(t[0] / 1000); s * 1000 < t[^1]; s++)
        {
            int c = 0;
            while (j < t.Count && t[j] < (s + 1) * 1000) { if (t[j] >= s * 1000) c++; j++; }
            r.FpsSeries.Add((s, c));
        }
    }

    // ---------------- referência normal da sessão ----------------

    private sealed record Base(double Dpc, double TopProc, double Clock, double Ping, double CpuThread, double Gpu);

    private static double Median(IEnumerable<double> v)
    {
        var a = v.OrderBy(x => x).ToArray();
        return a.Length == 0 ? 0 : a[a.Length / 2];
    }

    private static Base Baseline(List<Sample> s)
    {
        var fg = s.Where(x => x.GameForeground).ToList();
        if (fg.Count < 4) fg = s;
        return new Base(Median(fg.Select(x => (double)(x.DpcPct + x.IntPct))), Median(fg.Select(x => (double)x.TopProcCpu)),
                        Median(fg.Select(x => (double)x.CpuClockMHz)), Median(fg.Where(x => x.PingMs != null).Select(x => (double)x.PingMs!.Value)),
                        Median(fg.Select(x => (double)x.CpuMaxThread)), Median(fg.Select(x => (double)(x.GpuLoad ?? 0))));
    }

    // ---------------- quebras ----------------

    private static void DetectStutters(SessionReport r, List<double> t, double[] ft, SessionData d, Base b, double sens)
    {
        const int Window = 90;
        var win = new List<double>(Window + 1);
        var q = new Queue<double>();
        double minExcess = 8 / Math.Max(0.25, sens);

        StutterEvent? open = null;
        int openFrames = 0;
        double openStart = 0, openWorst = 0, openMedian = 0;

        void Close()
        {
            if (open == null) return;
            var (cause, detail, second) = Attribute(openStart, d, b);
            string sev = openWorst - openMedian > 100 ? "congelamento" : openWorst - openMedian > 20 ? "quebra" : "micro-quebra";
            r.Events.Add(new StutterEvent(openStart, openWorst, openMedian, openFrames, sev, cause, detail, second));
            open = null;
        }

        for (int i = 0; i < ft.Length; i++)
        {
            double x = ft[i], at = t[i + 1];
            double med = win.Count >= 20 ? win[win.Count / 2] : r.MedianFt;
            bool pause = x >= 1500;
            bool spike = !pause && x >= med * 2 && x - med >= minExcess;
            if (spike)
            {
                if (open != null && at - openStart <= 300) { openFrames++; openWorst = Math.Max(openWorst, x); }
                else
                {
                    Close();
                    open = new StutterEvent(at, x, med, 1, "", Cause.Engine, "", null);
                    openStart = at; openWorst = x; openMedian = med; openFrames = 1;
                }
            }
            if (!pause && !spike) // só frames normais entram na mediana móvel
            {
                int pos = win.BinarySearch(x);
                win.Insert(pos < 0 ? ~pos : pos, x);
                q.Enqueue(x);
                if (q.Count > Window)
                {
                    double old = q.Dequeue();
                    int k = win.BinarySearch(old);
                    if (k >= 0) win.RemoveAt(k);
                }
            }
        }
        Close();

        foreach (var e in r.Events) r.CauseCounts[e.Cause] = r.CauseCounts.GetValueOrDefault(e.Cause) + 1;
        double playMin = Math.Max(0.1, r.DurationS / 60);
        r.StuttersPerMin = r.Events.Count(e => e.Cause != Cause.AltTab) / playMin;
    }

    /// <summary>Olha para as leituras de ~0,6 s antes até 0,3 s depois da quebra e escolhe a causa mais provável.</summary>
    private static (Cause, string, Cause?) Attribute(double at, SessionData d, Base b)
    {
        var w = d.Samples.Where(s => s.T >= at - 650 && s.T <= at + 300).ToList();
        if (w.Count == 0) return (Cause.Engine, "sem leituras de sensores neste instante", null);

        var found = new List<(Cause C, string D)>();
        if (w.Any(s => !s.GameForeground) && d.Samples.Any(s => s.GameForeground))
            return (Cause.AltTab, "o jogo não estava em primeiro plano (Alt+Tab, notificação ou outra janela)", null);

        var vram = w.Where(s => s.VramTotalMB > 0).Select(s => s.VramUsedMB!.Value / s.VramTotalMB!.Value).DefaultIfEmpty(0).Max();
        if (vram >= 0.96) found.Add((Cause.VramFull, $"VRAM a {vram * 100:0}% ({w.Max(s => s.VramUsedMB) / 1024:0.0} GB)"));

        float avail = w.Min(s => s.RamAvailMB), faults = w.Max(s => s.HardFaults);
        if (avail is > 0 and < 800 || (faults >= 300 && w.Max(s => s.DiskBusyPct) >= 40))
            found.Add((Cause.RamPaging, $"RAM livre {avail:0} MB, {faults:0} páginas/s lidas do disco"));

        ulong thr = w.Aggregate(0UL, (a, s) => a | s.GpuThrottle);
        if ((thr & (Nvml.ReasonHwThermal | Nvml.ReasonSwThermal | Nvml.ReasonHwSlowdown)) != 0)
            found.Add((Cause.GpuThermal, $"GPU a reduzir clock por temperatura ({w.Max(s => s.GpuTemp):0} °C)"));
        if ((thr & Nvml.ReasonHwPowerBrake) != 0)
            found.Add((Cause.GpuPowerBrake, "travão de energia da GPU (sinal da fonte/cabo PCIe)"));

        float dpc = w.Max(s => s.DpcPct + s.IntPct);
        if (dpc >= Math.Max(3.0, b.Dpc * 2.5))
            found.Add((Cause.DriverLatency, $"DPC+interrupções a {dpc:0.0}% da CPU (normal ~{b.Dpc:0.0}%)"));

        var bg = w.OrderByDescending(s => s.TopProcCpu).First();
        if (bg.TopProc != null && bg.TopProcCpu >= Math.Max(40, b.TopProc * 2))
            found.Add((Cause.BackgroundCpu, $"{bg.TopProc} usou {bg.TopProcCpu:0}% de um núcleo"));

        var bgg = w.OrderByDescending(s => s.TopGpuProcPct).First();
        if (bgg.TopGpuProc != null && bgg.TopGpuProcPct >= 15)
            found.Add((Cause.BackgroundGpu, $"{bgg.TopGpuProc} usou {bgg.TopGpuProcPct:0}% da GPU"));

        if (w.Max(s => s.DiskBusyPct) >= 90 || w.Max(s => s.DiskQueue) >= 4)
            found.Add((Cause.Disk, $"disco ocupado a {w.Max(s => s.DiskBusyPct):0}% (fila {w.Max(s => s.DiskQueue):0.0})"));

        float clock = w.Min(s => s.CpuClockMHz);
        float? temp = w.Max(s => s.CpuTemp);
        if ((b.Clock > 0 && clock < b.Clock * 0.8 && w.Max(s => s.CpuTotal) >= 40) || temp >= 90)
            found.Add((Cause.CpuThrottle, temp != null ? $"CPU a {temp:0} °C, clock caiu para {clock:0} MHz" : $"clock da CPU caiu para {clock:0} MHz (normal {b.Clock:0})"));

        var cpuPeak = w.Max(s => s.CpuMaxThread);
        var gpuAt = w.Where(s => s.GpuLoad != null).Select(s => s.GpuLoad!.Value).DefaultIfEmpty(100).Min();
        if (cpuPeak >= 95 && gpuAt < 85)
            found.Add((Cause.CpuBound, $"thread {w.First(s => s.CpuMaxThread == cpuPeak).CpuMaxThreadIdx} da CPU a {cpuPeak:0}% com a GPU a {gpuAt:0}%"));

        if (w.Any(s => s.PingLost) || (b.Ping > 0 && w.Max(s => s.PingMs ?? 0) > Math.Max(b.Ping * 3, b.Ping + 40)))
            found.Add((Cause.Network, w.Any(s => s.PingLost) ? "pacote perdido no teste de ping" : $"ping subiu para {w.Max(s => s.PingMs):0} ms"));

        if (found.Count == 0)
        {
            var gpuPeak = w.Max(s => s.GpuLoad ?? 0);
            if (gpuPeak >= 98) return (Cause.GpuBound, $"GPU a {gpuPeak:0}%: cena pesada (explosão, fumo, muitos jogadores)", null);
            return (Cause.Engine, "nenhum sensor anormal: compilação de shaders, carregamento de recursos ou código do jogo", null);
        }
        return (found[0].C, found[0].D, found.Count > 1 ? found[1].C : null);
    }

    // ---------------- onde está o limite ----------------

    private static void Bound(SessionReport r, SessionData d, List<double> t)
    {
        var fg = d.Samples.Where(s => s.GameForeground).ToList();
        if (fg.Count == 0) fg = d.Samples;
        if (fg.Count == 0) return;
        int gpu = 0, cpu = 0, cap = 0;
        foreach (var s in fg)
        {
            if (s.GpuLoad >= 95) gpu++;
            else if (s.CpuMaxThread >= 90 || (s.GpuLoad != null && s.GpuLoad < 80 && s.CpuMaxThread >= 80)) cpu++;
            else cap++;
        }
        r.GpuBoundPct = 100.0 * gpu / fg.Count;
        r.CpuBoundPct = 100.0 * cpu / fg.Count;
        r.CappedPct = 100.0 * cap / fg.Count;
    }

    // ---------------- conclusões e recomendações ----------------

    private static void Conclude(SessionReport r, SessionData d, Base b)
    {
        int hz = Math.Max(30, d.MonitorHz);
        var real = r.Events.Where(e => e.Cause != Cause.AltTab).ToList();
        int serious = real.Count(e => e.Severity != "micro-quebra");

        // Nota: FPS em relação ao monitor + estabilidade (1% low) + quebras por minuto
        double score = 100;
        score -= Math.Max(0, 1 - r.AvgFps / hz) * 40;
        score -= Math.Max(0, 1 - r.Low1Fps / (r.AvgFps * 0.6)) * 25;
        score -= Math.Min(30, r.StuttersPerMin * 4);
        r.Grade = score >= 90 ? "A" : score >= 75 ? "B" : score >= 60 ? "C" : score >= 45 ? "D" : "E";

        foreach (var g in real.GroupBy(e => e.Cause).OrderByDescending(g => g.Count()))
        {
            var f = ForCause(g.Key, g.ToList(), d);
            if (f != null) r.Findings.Add(f with { Count = g.Count() });
        }

        // Limite geral (não são quebras, é o teto de FPS)
        if (r.AvgFps < hz * 0.95)
        {
            if (r.GpuBoundPct >= 60)
                r.Findings.Add(new Finding("A placa de vídeo é o teto de FPS",
                    $"GPU a ≥95% em {r.GpuBoundPct:0}% do tempo, com média de {r.AvgFps:0} FPS num monitor de {hz} Hz.",
                    "Sem gastar: ativa DLSS/FSR em Qualidade, baixa sombras/reflexos/volumétricos, ou aplica um overclock moderado (perfil 'OC ligeiro'). " +
                    $"Com upgrade: {UpgradeAdvisor.Gpu(d)}", FixKind.Hardware, 2));
            else if (r.CpuBoundPct >= 35)
                r.Findings.Add(new Finding("A CPU é o teto de FPS",
                    $"Um thread da CPU a ≥90% em {r.CpuBoundPct:0}% do tempo enquanto a GPU tinha folga.",
                    "Sem gastar: baixa opções que pesam na CPU (distância de visão, multidões, física, sombras dinâmicas), ativa o NVIDIA Reflex, " +
                    $"confirma que o XMP/EXPO está ligado e fecha programas pesados. Com upgrade: {UpgradeAdvisor.Cpu(d)}", FixKind.Hardware, 2));
        }

        if (r.LoadingPauses > 0)
            r.Findings.Add(new Finding("Pausas longas (carregamentos)", $"{r.LoadingPauses} pausas de mais de 1,5 s (ecrãs de carregamento ou Alt+Tab); não contam como quebras.",
                "Normal em carregamentos. Se acontecerem a meio do jogo, instala o jogo num SSD NVMe.", FixKind.None, 1));

        // Veredito
        int fixable = real.Count(e => KindOf(e.Cause, d) is FixKind.PcFix or FixKind.GameSettings or FixKind.Network);
        int hw = real.Count(e => KindOf(e.Cause, d) == FixKind.Hardware);
        int game = real.Count(e => KindOf(e.Cause, d) == FixKind.GameSide);
        if (real.Count == 0 || r.StuttersPerMin < 0.5)
        {
            r.Verdict = r.AvgFps >= hz * 0.95 ? "Sessão limpa e no limite do monitor" : "Sessão limpa (sem quebras relevantes)";
            r.VerdictDetail = $"{r.AvgFps:0} FPS de média, 1% low {r.Low1Fps:0} FPS, {real.Count} quebras em {r.DurationS / 60:0.0} min.";
        }
        else if (fixable >= hw && fixable >= game)
        {
            r.Verdict = "As quebras têm correção sem trocar peças";
            r.VerdictDetail = $"{fixable} de {real.Count} quebras vêm de coisas que se resolvem no PC ou nas definições (vê as correções abaixo).";
        }
        else if (hw >= game)
        {
            r.Verdict = "As quebras vêm sobretudo de limites do hardware";
            r.VerdictDetail = $"{hw} de {real.Count} quebras coincidem com um componente no limite. Há ajustes que aliviam, mas a solução completa é upgrade.";
        }
        else
        {
            r.Verdict = "As quebras vêm sobretudo do próprio jogo";
            r.VerdictDetail = $"{game} de {real.Count} quebras aconteceram sem nenhum sensor anormal: típico de compilação de shaders, streaming de mapa ou do código do jogo.";
        }
    }

    public static FixKind KindOf(Cause c, SessionData d) => c switch
    {
        Cause.VramFull => FixKind.GameSettings,
        Cause.RamPaging => d.RamGB < 24 ? FixKind.Hardware : FixKind.PcFix,
        Cause.GpuThermal or Cause.DriverLatency or Cause.BackgroundCpu or Cause.BackgroundGpu or Cause.CpuThrottle => FixKind.PcFix,
        Cause.GpuPowerBrake => FixKind.Hardware,
        Cause.Disk => d.GameDriveMedia == "HDD" ? FixKind.Hardware : FixKind.PcFix,
        Cause.CpuBound or Cause.GpuBound => FixKind.Hardware,
        Cause.Network => FixKind.Network,
        Cause.Engine => FixKind.GameSide,
        _ => FixKind.None
    };

    private static Finding? ForCause(Cause c, List<StutterEvent> ev, SessionData d)
    {
        string ex = ev[0].Detail;
        int n = ev.Count;
        var k = KindOf(c, d);
        return c switch
        {
            Cause.VramFull => new("Memória de vídeo (VRAM) cheia", $"Ex.: {ex}. Quando a VRAM enche, a placa vai buscar texturas à RAM pelo PCIe e o jogo engasga.",
                $"Baixa 'Qualidade de texturas' um nível e desliga pacotes de texturas HD. Fecha o browser (usa VRAM). Com {d.VramGB:0} GB, {UpgradeAdvisor.Vram(d)}", k, 3),
            Cause.RamPaging => new("RAM a acabar / Windows a usar o disco", $"Ex.: {ex}.",
                $"Fecha browser, Discord com vídeo e launchers que não estás a usar. Confirma que o ficheiro de paginação está ligado (automático). " +
                (d.RamGB < 24 ? $"Com {d.RamGB:0} GB, passar para 32 GB resolve de vez." : "Tens RAM suficiente: o problema é o que está aberto em paralelo."), k, 3),
            Cause.GpuThermal => new("GPU a reduzir clock por temperatura", $"Ex.: {ex}.",
                "Limpa o pó dos dissipadores, melhora a entrada de ar da caixa, usa uma curva de ventoinha mais agressiva ou o perfil 'Eficiente' (undervolt) no separador Overclock. " +
                "Se a placa tem mais de 4 anos, a pasta térmica/thermal pads podem precisar de troca.", k, 3),
            Cause.GpuPowerBrake => new("Travão de energia da GPU", $"Ex.: {ex}. O sinal vem da fonte ou dos cabos PCIe.",
                "Usa cabos PCIe separados (não em 'Y' do mesmo cabo), confirma que a fonte tem potência e qualidade suficientes. Reduz o limite de energia no separador Overclock como teste.", k, 3),
            Cause.DriverLatency => new("Latência de drivers (DPC/ISR)", $"Ex.: {ex}. Um driver segurou a CPU tempo demais.",
                "Atualiza os drivers de rede/Wi-Fi, áudio e chipset (AMD/Intel). Desliga dispositivos USB/Bluetooth que não usas. Corre o LatencyMon para ver o driver culpado. " +
                "Wi-Fi e placas de som USB são os suspeitos mais comuns.", k, 3),
            Cause.BackgroundCpu => new("Programa em segundo plano a roubar CPU", $"Ex.: {ex}.",
                "Fecha esse programa antes de jogar (adiciona-o à lista 'Fechar antes de jogar' no perfil do jogo) ou desativa-o no arranque do Windows. " +
                "Se for o antivírus, adiciona a pasta do jogo às exclusões.", k, 2),
            Cause.BackgroundGpu => new("Programa em segundo plano a usar a GPU", $"Ex.: {ex}.",
                "Browsers com vídeo, Wallpaper Engine, gravação do OBS/ShadowPlay e overlays usam a GPU. Fecha-os ou desliga a aceleração por hardware no browser/Discord.", k, 2),
            Cause.Disk => new("Disco ocupado no momento da quebra", $"Ex.: {ex}.",
                d.GameDriveMedia == "HDD" ? "O jogo está num disco rígido (HDD): passa-o para um SSD — é a melhoria mais barata para quebras de carregamento."
                                          : "Algo estava a usar o disco (Windows Update, antivírus, Steam a descarregar, indexação). Pausa downloads e agenda atualizações fora do jogo.", k, 2),
            Cause.CpuThrottle => new("CPU a perder clock", $"Ex.: {ex}.",
                "Vê a temperatura da CPU (ativa 'Sensores avançados' para a medir), limpa o cooler e confirma o plano de energia de alto desempenho. " +
                "Em Ryzen, um Curve Optimizer negativo baixa temperatura sem perder desempenho (guia no separador Overclock).", k, 2),
            Cause.CpuBound => new("CPU (thread principal) no limite nas quebras", $"Ex.: {ex}.",
                $"Baixa definições que pesam na CPU, ativa o NVIDIA Reflex (On + Boost) e limita o FPS um pouco abaixo da média (picos ficam mais suaves). Upgrade: {UpgradeAdvisor.Cpu(d)}", k, 2),
            Cause.GpuBound => new("GPU a 100% em cenas pesadas", $"Ex.: {ex}.",
                "Limita o FPS (no jogo ou no Painel NVIDIA) uns 5–10% abaixo da média: a GPU deixa de ficar a 100% e os picos de frametime desaparecem. DLSS/FSR também ajuda.", k, 1),
            Cause.Network => new("Rede instável durante quebras", $"Ex.: {ex}.",
                "Usa cabo em vez de Wi-Fi, fecha downloads/streams noutros aparelhos e reinicia o router. Nota: rede má costuma dar 'teleportes', não quebras de imagem.", k, 2),
            Cause.Engine => new("Quebras sem causa no PC (lado do jogo)", $"{n} quebras sem nenhum sensor anormal.",
                "Normalmente compilação de shaders ou streaming do mapa. Ajuda: no Painel NVIDIA → 'Tamanho da cache de shaders' = 10 GB ou Ilimitado; jogar 2–3 partidas após " +
                "atualizar o driver (a cache reconstrói-se); não apagar a cache de shaders; manter o jogo atualizado. Não se resolve com upgrade.", k, 1),
            _ => null
        };
    }
}
