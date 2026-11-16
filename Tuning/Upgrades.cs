namespace FrameForge.Tuning;

// Sugestões de upgrade a partir do relatório do scan.
// Regras: só sugerir a peça que o scan (ou as sessões gravadas) mostra a limitar; ganho = índice do candidato ÷ atual,
// limitado pelo teto medido do outro componente; ordenar por ganho por euro; 2–3 opções por peça.
// Os preços são faixas de referência datadas (PerfIndex.UpdatedText): a página deve mostrar PerfIndex.Disclaimer
// e os botões "Ver preço atual" (PerfIndex.PriceLinks(opcao.PesquisaTermo) + PerfIndex.OpenUrl).
// "Encontrei por ___ €": UserPrices.Set(opcao.PesquisaTermo/Nome do modelo, €) e voltar a chamar Suggest.

/// <summary>Sugestões de upgrade a partir do relatório do scan.</summary>
public static class Upgrades
{
    /// <summary>2–3 opções por peça que limita (GPU/CPU), mais RAM/disco quando o scan mostra um problema claro.</summary>
    public static List<UpgradeOption> Suggest(ScanReport report)
    {
        var res = new List<UpgradeOption>();
        if (report == null || !PerfIndex.Available) return res;
        try
        {
            var inv = report.Inventario ?? new InventorySummary();
            if (inv.Portatil) return res;   // num portátil não se troca a gráfica nem o processador

            int altura = inv.MonitorAltura > 0 ? inv.MonitorAltura : 1080;
            // Os tetos do limitador estão na mesma escala (índices corrigidos pelas medições); os FPS brutos das cenas sintéticas
            // de CPU e de GPU não são comparáveis entre si, por isso só servem de recurso quando o limitador não tem valor.
            double? cpuFps = report.Limitador?.TetoCpuFps is > 0 ? report.Limitador.TetoCpuFps
                           : report.Cpu?.FpsJogo is > 0 ? report.Cpu.FpsJogo : null;
            double? gpuFps = report.Limitador?.TetoGpuMonitorFps is > 0 ? report.Limitador.TetoGpuMonitorFps : GpuFpsAtScreen(report, altura);
            // Tetos em escalas diferentes (ex.: FPS bruto da cena de CPU contra o da GPU) dariam ganhos absurdos: aí usa só os índices
            if (cpuFps is > 0 && gpuFps is > 0 && (cpuFps / gpuFps > 4 || gpuFps / cpuFps > 4)) { cpuFps = null; gpuFps = null; }

            var (gpuLimita, cpuLimita) = WhoLimits(report, cpuFps, gpuFps);

            var ctx = new UpgradeContext(inv.Cpu ?? "", inv.Gpu ?? "", inv.VramGB, inv.PlacaMae ?? "",
                AlturaEcra: altura, CpuFps: cpuFps, GpuFps: gpuFps, Portatil: inv.Portatil);
            string resTxt = ResolutionText(altura);

            var gpuOpts = new List<UpgradeOption>();
            var cpuOpts = new List<UpgradeOption>();

            if (gpuLimita)
            {
                GpuModel? cur = PerfIndex.IsIntegratedGpu(inv.Gpu) ? PerfIndex.IntegratedGpu(inv.Gpu) : null;
                var cands = UpgradeMath.GpuCandidates(ctx, current: cur);
                foreach (var (esc, c) in UpgradeMath.Pick(cands))
                    gpuOpts.Add(ToOption(UpgradeComponent.GPU, esc, c, resTxt, cpuFps, gpuFps, cur != null));
            }
            if (cpuLimita)
            {
                var cands = UpgradeMath.CpuCandidates(ctx);
                foreach (var (esc, c) in UpgradeMath.Pick(cands))
                    cpuOpts.Add(ToOption(UpgradeComponent.CPU, esc, c, resTxt, cpuFps, gpuFps, false));
            }

            // A peça que mais limita primeiro
            bool cpuPrimeiro = cpuLimita && (!gpuLimita || (cpuFps is > 0 && gpuFps is > 0 && cpuFps < gpuFps));
            if (cpuPrimeiro) { res.AddRange(cpuOpts); res.AddRange(gpuOpts); }
            else { res.AddRange(gpuOpts); res.AddRange(cpuOpts); }

            res.AddRange(RamOptions(report));
            res.AddRange(DiskOptions(report));
        }
        catch
        {
            // Sugestões são um extra: um nome estranho ou dados em falta nunca deitam o scan abaixo.
        }
        return res;
    }

    // ---------- Quem limita ----------

    /// <summary>FPS da GPU (de fábrica) à resolução do monitor: 4K → nativo; 1440p → 1440; senão 1080.</summary>
    private static double? GpuFpsAtScreen(ScanReport r, int altura)
    {
        var g = r.Gpu;
        if (g == null) return altura > 1200 ? r.Limitador?.TetoGpu1440Fps ?? r.Limitador?.TetoGpu1080Fps : r.Limitador?.TetoGpu1080Fps;
        if (altura > 1700 && g.FpsNativo is > 0) return g.FpsNativo;
        if (altura > 1200 && g.Fps1440 is > 0) return g.Fps1440;
        return g.Fps1080 is > 0 ? g.Fps1080 : r.Limitador?.TetoGpu1080Fps;
    }

    /// <summary>Decide a(s) peça(s) a sugerir. "Monitor" (FPS acima dos Hz) → nada; "Equilibrado" → as duas, e o filtro de ganho mínimo corta o que não valer a pena.</summary>
    private static (bool Gpu, bool Cpu) WhoLimits(ScanReport r, double? cpuFps, double? gpuFps)
    {
        var lim = r.Limitador;
        string p = lim?.Principal?.Trim() ?? "";
        if (p.Equals("GPU", StringComparison.OrdinalIgnoreCase)) return (true, false);
        if (p.Equals("CPU", StringComparison.OrdinalIgnoreCase)) return (false, true);
        if (p.Equals("Monitor", StringComparison.OrdinalIgnoreCase)) return (false, false);
        if (p.Equals("Equilibrado", StringComparison.OrdinalIgnoreCase)) return (true, true);

        // Sem conclusão do limitador: sessões reais gravadas e depois a comparação dos tetos medidos
        if (lim?.SessoesGpuPct is >= 60) return (true, false);
        if (lim?.SessoesCpuPct is >= 35) return (false, true);
        if (cpuFps is > 0 && gpuFps is > 0)
        {
            int hz = lim?.MonitorHz is > 0 ? lim.MonitorHz : r.Inventario?.MonitorHz ?? 0;
            if (hz > 0 && Math.Min(cpuFps.Value, gpuFps.Value) >= hz * 1.05) return (false, false);
            if (gpuFps < cpuFps * 0.9) return (true, false);
            if (cpuFps < gpuFps * 0.9) return (false, true);
            return (true, true);
        }
        // Sem medições não se sugere nada (seria um palpite)
        return (false, false);
    }

    // ---------- Conversão ----------

    private static UpgradeOption ToOption(UpgradeComponent comp, string escalao, UpgradeCandidate c, string resTxt,
                                          double? cpuFps, double? gpuFps, bool integrada)
    {
        var o = new UpgradeOption
        {
            Componente = comp,
            Nome = c.PrecisaPlataforma ? $"{c.Modelo} + placa-mãe e RAM novas" : c.Modelo,
            GanhoPct = c.GanhoPct,
            PrecoMinEur = c.PrecoMin + c.ExtraMin,
            PrecoMaxEur = c.PrecoMax + c.ExtraMax,
            PesquisaTermo = c.Pesquisa,
        };

        string porque;
        if (cpuFps is > 0 && gpuFps is > 0)
        {
            porque = comp == UpgradeComponent.GPU
                ? $"a tua gráfica dá ~{gpuFps:0} FPS {resTxt} e o processador aguenta ~{cpuFps:0}"
                : $"o processador aguenta ~{cpuFps:0} FPS e a gráfica dá ~{gpuFps:0} {resTxt}";
        }
        else porque = $"comparação entre índices {resTxt}, sem medição completa";
        o.Motivo = $"{escalao}: +{c.GanhoPct:0}% FPS estimado {resTxt} ({porque}). " +
                   $"Índice de desempenho de {PerfIndex.UpdatedText}; valores aproximados.";
        if (c.PrecoUtilizador is > 0)
            o.Motivo += $" Contas feitas com o preço que encontraste ({c.PrecoUtilizador:0} €).";

        if (integrada) o.Avisos.Add("estás a usar os gráficos integrados: o ganho real pode ser ainda maior (variam muito)");
        if (c.LimitadoPeloOutro)
            o.Avisos.Add(c.GanhoBrutoPct > c.GanhoPct + 5
                ? $"sozinha, a peça vale +{c.GanhoBrutoPct:0}%, mas parte do ganho fica presa {(comp == UpgradeComponent.GPU ? "no processador" : "na gráfica")}"
                : $"o ganho já está no limite {(comp == UpgradeComponent.GPU ? "do processador" : "da gráfica")}");
        foreach (var n in c.Notas)
        {
            if (n.StartsWith("parte do ganho", StringComparison.Ordinal)) continue;   // coberto pelo aviso acima
            if (!o.Avisos.Contains(n)) o.Avisos.Add(n);
        }
        if (comp == UpgradeComponent.GPU && c.PsuW is > 0 && !o.Avisos.Any(a => a.StartsWith("fonte", StringComparison.Ordinal)))
            o.Avisos.Add($"fonte recomendada: {c.PsuW} W");
        if (c.PrecisaPlataforma)
            o.Avisos.Add($"o preço inclui placa-mãe e 32 GB de RAM de referência ({c.ExtraMin:0}–{c.ExtraMax:0} €)");
        return o;
    }

    private static string ResolutionText(int altura) => altura <= 1200 ? "em 1080p" : altura <= 1700 ? "em 1440p" : "em 4K";

    // ---------- RAM e disco (só com sinais claros do scan) ----------

    private static IEnumerable<UpgradeOption> RamOptions(ScanReport r)
    {
        var inv = r.Inventario;
        if (inv == null || inv.RamGB <= 0) yield break;
        string t = (inv.RamTipo ?? "").ToUpperInvariant();
        string tipo = t.Contains("DDR5") ? "DDR5" : t.Contains("DDR4") ? "DDR4" : "";
        if (tipo == "") yield break;   // DDR3 ou desconhecido: não há referência no índice

        bool pouca = inv.RamGB < 15;                                   // 8 GB (ou 12) em jogos atuais: quebras certas
        bool canalUnico = r.Ram?.CanalUnicoProvavel == true && inv.Pentes <= 1;
        if (!pouca && !canalUnico) yield break;

        int alvo = pouca && tipo == "DDR4" && inv.RamGB < 12 ? 16 : 32;
        var kit = PerfIndex.Data.Memorias
            .Where(m => m.Tipo == tipo && m.Gb >= alvo && m.PrecoMinEur is > 0 && m.PrecoMaxEur is > 0)
            .OrderBy(m => m.Gb).ThenBy(m => m.PrecoMinEur).FirstOrDefault();
        if (kit == null) yield break;

        var o = new UpgradeOption
        {
            Componente = UpgradeComponent.RAM,
            Nome = kit.Nome,
            // Não há índice medido para RAM: estimativa conservadora (dual channel); 8 → 16/32 GB tira quebras, não sobe a média
            GanhoPct = canalUnico ? 10 : 0,
            PrecoMinEur = kit.PrecoMinEur!.Value,
            PrecoMaxEur = kit.PrecoMaxEur!.Value,
            PesquisaTermo = kit.Pesquisa,
            Motivo = pouca
                ? $"Tens {inv.RamGB:0} GB: em jogos atuais o Windows passa a usar o disco e aparecem quebras. Um kit de {kit.Gb} GB em 2 pentes resolve isso e liga o dual channel."
                : $"O scan mediu banda de memória de canal único ({Math.Max(inv.Pentes, 1)} pente). Dois pentes iguais dão tipicamente +5 a +15% de FPS nos jogos limitados pelo processador e 1% low melhores.",
        };
        o.Avisos.Add($"confirma que a tua placa usa {tipo} e quantas ranhuras tem livres");
        o.Avisos.Add("depois de montar, ativa o XMP/EXPO na BIOS");
        if (canalUnico && !pouca) o.Avisos.Add("alternativa mais barata: um segundo pente igual ao teu (mesma velocidade e latência)");
        yield return o;
    }

    private static IEnumerable<UpgradeOption> DiskOptions(ScanReport r)
    {
        if (r.Discos == null || r.Discos.Count == 0) yield break;
        // Jogos (ou o Windows) num disco rígido: a melhoria mais barata para carregamentos e quebras de streaming
        var hdd = r.Discos.FirstOrDefault(d => d.Jogos && IsHdd(d)) ?? r.Discos.FirstOrDefault(d => d.Sistema && IsHdd(d));
        // Disco dos jogos/sistema quase cheio (<10% e <50 GB livres)
        var cheio = r.Discos.FirstOrDefault(d => (d.Jogos || d.Sistema) && d.TamanhoGB > 0 && d.LivreGB < 50 && d.LivreGB / d.TamanhoGB < 0.10);
        var alvo = hdd ?? cheio;
        if (alvo == null) yield break;

        int gb = alvo.TamanhoGB >= 1500 || hdd == null ? 2000 : 1000;
        var ssd = PerfIndex.Data.Discos.Where(d => d.Tipo == "NVMe" && d.PrecoMinEur is > 0 && d.PrecoMaxEur is > 0)
                                       .OrderBy(d => Math.Abs(d.Gb - gb)).FirstOrDefault();
        if (ssd == null) yield break;
        var o = new UpgradeOption
        {
            Componente = UpgradeComponent.Disco,
            Nome = ssd.Nome,
            GanhoPct = 0,   // não muda o FPS médio: muda carregamentos e quebras ao carregar o mapa
            PrecoMinEur = ssd.PrecoMinEur!.Value,
            PrecoMaxEur = ssd.PrecoMaxEur!.Value,
            PesquisaTermo = ssd.Pesquisa,
            Motivo = hdd != null
                ? $"{(hdd.Jogos ? "Os jogos estão" : "O Windows está")} num disco rígido ({Label(hdd)}). Um SSD NVMe carrega várias vezes mais depressa e acaba com as quebras ao carregar texturas; não muda o FPS médio."
                : $"O disco {Label(cheio!)} tem só {cheio!.LivreGB:0} GB livres: um SSD extra dá espaço para os jogos e evita abrandamentos do Windows.",
        };
        o.Avisos.Add("confirma que a placa-mãe tem uma ranhura M.2 livre (senão, um SSD SATA também resolve)");
        yield return o;
    }

    private static bool IsHdd(DiskResult d) => (d.Tipo ?? "").Contains("HDD", StringComparison.OrdinalIgnoreCase);

    private static string Label(DiskResult d) =>
        string.IsNullOrWhiteSpace(d.Unidade) ? (d.Modelo ?? "") : string.IsNullOrWhiteSpace(d.Modelo) ? d.Unidade : $"{d.Unidade} {d.Modelo}";
}
