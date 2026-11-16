using System.Diagnostics;
using FrameForge.Core;
using FrameForge.Hardware;
using FrameForge.Overclock;

namespace FrameForge.Tuning;

/// <summary>Medições da gráfica que servem a qualquer marca: FPS de fábrica (cena de jogo) e limite térmico/energia (stress).</summary>
public static class GpuScan
{
    /// <summary>Fábrica → cena de jogo em 1080p (2 passadas, mediana) e, se o monitor for maior, também em 1440p/nativo.</summary>
    internal static async Task RunBaseAsync(ScanContext ctx)
    {
        var gpu = ctx.Report.Gpu ??= new GpuResult();
        gpu.Nome = ctx.Report.Inventario.Gpu;

        if (ctx.Nvidia)
        {
            var st = await GpuAutoTuner.ReadAsync();
            gpu.PotenciaFabricaW = st.PowerDefaultW;
            gpu.PotenciaMaxW = st.PowerMaxW;
            if (!AppServices.Tuner.AppliedIsStock || st.CoreOffset is not (null or 0) || st.MemOffset is not (null or 0))
            {
                ctx.Log("• Repus a gráfica nos valores de fábrica para medir");
                await GpuAutoTuner.StockAsync(ctx);
            }
        }

        int h = ctx.Report.Inventario.MonitorAltura, w = ctx.Report.Inventario.MonitorLargura;
        bool do1440 = h >= 1400;
        bool doNative = h > 1600 && w > 0;
        double end1080 = do1440 || doNative ? 0.55 : 1;

        ctx.Estado("A medir a gráfica em 1080p (valores de fábrica)…");
        ctx.Sub(0, end1080);
        var (avg, low, last) = await MeasureAsync(ctx, 1920, 1080, 2, ctx.Lim.BenchMedicao, 0, end1080);
        gpu.Fps1080 = avg; gpu.Fps1080Low = low;
        gpu.Jogo = last?.Gpu ?? new ThermalSummary();
        if (avg != null) ctx.Log($"✔ Gráfica em 1080p: {avg:0} FPS (1% low {low:0}) · máx. {CpuRamDiskScan.Fmt(gpu.Jogo.TempMax, "°C")}");
        if (last is { Passou: false })
        {
            gpu.Problemas.Add(last.Motivo);
            if (!last.Termico) gpu.Estavel = false;
            ctx.Log((last.Termico ? "⚠ " : "✖ ") + "Gráfica de fábrica: " + last.Motivo);
            return; // não vale a pena medir mais resoluções com a placa nestas condições
        }

        if (do1440)
        {
            ctx.Estado("A medir a gráfica em 1440p…");
            double e = doNative ? 0.8 : 1;
            var (a2, l2, r2) = await MeasureAsync(ctx, 2560, 1440, 1, ctx.Lim.BenchMedicao, end1080, e);
            gpu.Fps1440 = a2; gpu.Fps1440Low = l2;
            if (a2 != null) ctx.Log($"✔ Gráfica em 1440p: {a2:0} FPS");
            end1080 = e;
            if (r2 is { Passou: false }) { gpu.Problemas.Add(r2.Motivo); return; }
        }
        if (doNative)
        {
            ctx.Estado($"A medir a gráfica em {w}×{h}…");
            var (a3, _, _) = await MeasureAsync(ctx, w, h, 1, ctx.Lim.BenchMedicao, end1080, 1);
            gpu.FpsNativo = a3; gpu.NativoLargura = w; gpu.NativoAltura = h;
            if (a3 != null) ctx.Log($"✔ Gráfica em {w}×{h}: {a3:0} FPS");
        }
    }

    /// <summary>Stress de energia/temperatura (tipo FurMark) em valores de fábrica: até onde a placa aquece e se o limite de energia trava.</summary>
    internal static async Task RunThermalAsync(ScanContext ctx)
    {
        var gpu = ctx.Report.Gpu ??= new GpuResult { Nome = ctx.Report.Inventario.Gpu };
        ctx.Estado($"Stress da gráfica ({ctx.Lim.StressGpu.TotalSeconds:0} s): temperatura e energia no máximo…");
        ctx.Sub(0, 1);
        var r = await ctx.Runner.RunAsync(new LoadSpec { Kind = LoadKind.GpuBurn, Duration = ctx.Lim.StressGpu, Adapter = ctx.Adapter, Label = "stress da gráfica" }, ctx.Ct);
        if (r.Cancelado)
        {
            if (ctx.Ct.IsCancellationRequested) throw new OperationCanceledException(ctx.Ct);
            throw new ScanAbortException("Fechaste a janela do teste da gráfica: parei o scan.");
        }
        gpu.Termico = r.Gpu;
        if (r.Passou)
            ctx.Log($"✔ Stress da gráfica: máx. {CpuRamDiskScan.Fmt(r.Gpu.TempMax, "°C")}" +
                    (r.Gpu.HotspotMax is double hs ? $" (hotspot {hs:0} °C)" : "") +
                    (r.Gpu.PotenciaMaxW is double pw ? $" · {pw:0} W" : "") + (r.Gpu.ThrottleEnergia ? " · limitada pela energia" : ""));
        else if (r.Termico)
        {
            gpu.Termico.ThrottleTermico = true;
            gpu.Termico.Nota = r.Motivo;
            ctx.Log($"⚠ Em stress máximo a gráfica passou do limite do perfil ({r.Motivo}). Parei por segurança.");
        }
        else
        {
            gpu.Estavel = false;
            gpu.Problemas.Add(r.Motivo);
            ctx.Log("✖ Stress da gráfica em valores de fábrica: " + r.Motivo);
        }
    }

    /// <summary>
    /// Cena de jogo <paramref name="passes"/> vezes; devolve a mediana do FPS médio e do 1% low (e o último resultado, para o resumo térmico).
    /// Com 2 passadas muito diferentes (&gt;3%) corre uma 3.ª.
    /// </summary>
    internal static async Task<(double? Avg, double? Low, StepResult? Last)> MeasureAsync(ScanContext ctx, int w, int h, int passes, TimeSpan dur,
                                                                                          double a = 0, double b = 1)
    {
        var avgs = new List<double>();
        var lows = new List<double>();
        StepResult? last = null;
        int total = passes;
        for (int i = 0; i < total; i++)
        {
            ctx.ThrowIfCancelled();
            ctx.Sub(a + (b - a) * i / total, a + (b - a) * (i + 1) / total);
            last = await ctx.Runner.RunAsync(new LoadSpec { Kind = LoadKind.GpuGame, Duration = dur, Width = w, Height = h, Adapter = ctx.Adapter, Label = $"{w}×{h}" }, ctx.Ct);
            if (last.Cancelado)
            {
                if (ctx.Ct.IsCancellationRequested) throw new OperationCanceledException(ctx.Ct);
                throw new ScanAbortException("Fechaste a janela do teste da gráfica: parei o scan.");
            }
            if (!last.Passou) break;
            if (last.AvgFps is double av) avgs.Add(av);
            if (last.Low1Fps is double lw) lows.Add(lw);
            if (i == 1 && total == 2 && avgs.Count == 2 && Math.Abs(avgs[0] - avgs[1]) / Math.Max(avgs[0], avgs[1]) > 0.03) total = 3;
        }
        return (Median(avgs), Median(lows), last);
    }

    internal static double? Median(List<double> v)
    {
        if (v.Count == 0) return null;
        var s = v.OrderBy(x => x).ToList();
        double m = s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        return Math.Round(m, 1);
    }
}

/// <summary>
/// Overclock automático da gráfica NVIDIA (NVAPI para os offsets, NVML para o limite de energia e a telemetria):
/// sobe o núcleo em passos, testa cada passo numa cena de jogo com vigia, recua e afina ao falhar; depois a memória
/// pelo critério "o FPS tem de subir"; aplica a margem do perfil e valida durante vários minutos.
/// Antes de CADA aplicação grava o estado no disco (recuperação de bloqueios/reinícios). A ventoinha nunca é escrita.
/// </summary>
public static class GpuAutoTuner
{
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Nome do perfil temporário aplicado durante cada passo (nunca é guardado; o GameRunner não o repõe).</summary>
    public const string TestProfileName = "Auto · em teste";

    /// <summary>Motivo para não afinar esta placa (null = pode). <paramref name="showGuide"/> = mostrar o guia do fabricante.</summary>
    public static string? Unsupported(out bool showGuide)
    {
        showGuide = false;
        if (!Nvml.Available || Nvml.FirstDevice() == IntPtr.Zero)
        {
            showGuide = true;
            return "A tua placa não permite overclock por software aqui; mostro o guia.";
        }
        if (!NvApi.Available) { showGuide = true; return "O driver NVIDIA não deixou ler os clocks (NVAPI indisponível). Atualiza o driver e tenta de novo."; }
        var st = AppServices.Tuner.Read();
        if (st.CoreMax is not > 0)
        {
            showGuide = true;
            return st.NvApiError != null ? $"O driver não deixa mudar os clocks desta placa ({st.NvApiError})."
                                         : "O driver não permite overclock nesta placa (é comum em portáteis).";
        }
        return null;
    }

    /// <summary><see cref="Unsupported"/> com prazo (lê o driver): um driver pendurado dá "sem overclock" em vez de prender o scan.</summary>
    internal static async Task<string?> UnsupportedSafeAsync()
    {
        const string hung = "O driver de vídeo não respondeu ao ler os clocks: não faço overclock nesta sessão.";
        var t = Task.Run(() => Unsupported(out _));
        var done = await Task.WhenAny(t, Task.Delay(ApplyTimeout));
        if (done != t) return hung;
        try { return await t; } catch { return "A tua placa não permite overclock por software aqui; mostro o guia."; }
    }

    /// <summary>Programas que escrevem na placa ao mesmo tempo (Afterburner e afins). Têm de estar fechados.</summary>
    public static List<string> Conflicts() => GpuTuner.ConflictingTools();

    // ------------------------------------------------------------------ núcleo

    internal static async Task TuneCoreAsync(ScanContext ctx)
    {
        var lim = ctx.Lim;
        var oc = ctx.Report.Oc!;
        var st = await ReadAsync();
        int pct = PowerPct(st, lim);
        oc.PotenciaPct = pct;
        if (pct > 100) ctx.Log($"• Limite de energia no máximo permitido: {pct}% ({st.PowerMaxW} W)");

        int ceiling = Math.Min(st.CoreMax ?? 0, lim.TetoNucleo);
        if (ctx.State.NucleoInstavel is int bad) ceiling = Math.Min(ceiling, bad - lim.PassoNucleoFino);
        int stable = Math.Max(0, ctx.State.UltimoNucleoEstavel);
        double? lastFps = oc.Passos.LastOrDefault(p => p.Passou && p.Tipo == "núcleo")?.Fps ?? ctx.Report.Gpu?.Fps1080;
        bool fine = ctx.State.NucleoInstavel != null; // a retomar depois de um bloqueio: já só em passos finos
        bool triedCeiling = fine;
        int next = stable + (fine ? lim.PassoNucleoFino : lim.PassoNucleo);
        var sw = Stopwatch.StartNew();

        while (true)
        {
            if (next > ceiling)
            {
                if (!triedCeiling && stable < ceiling) { next = ceiling; triedCeiling = true; }
                else { if (stable >= ceiling && ceiling > 0) ctx.Log($"• Cheguei ao teto permitido (+{ceiling} MHz)"); break; }
            }
            if (sw.Elapsed > lim.OrcamentoNucleo) { ctx.Log("• Tempo da pesquisa do núcleo esgotado: fico com o melhor valor estável"); break; }
            ctx.Fase(Math.Max(sw.Elapsed / lim.OrcamentoNucleo, ceiling > 0 ? (double)stable / ceiling : 0) * 0.98);

            var r = await TestAsync(ctx, ScanPhase.GpuNucleo, "núcleo", next, 0, oc.PotenciaPct, lim.PassoDuracao);
            if (r.Passou && lastFps is double lf && r.AvgFps is double fps && fps < lf * 0.96)
            {
                r.Passou = false;
                r.Motivo = $"o FPS caiu {100 * (1 - fps / lf):0}% (sinal de instabilidade)";
                oc.Passos[^1].Passou = false; oc.Passos[^1].Motivo = r.Motivo;
                await RecoverAsync(ctx, r);
            }

            if (r.Passou)
            {
                stable = next;
                ctx.State.UltimoNucleoEstavel = stable;
                lastFps = r.AvgFps ?? lastFps;
                if (r.PowerCapFracao > 0.3 && !oc.LimitadoEnergia)
                {
                    oc.LimitadoEnergia = true;
                    ctx.Log("• A gráfica está a bater no limite de energia: subir mais o clock quase não dá FPS");
                }
                ctx.Log($"✔ +{next} MHz núcleo estável" + (r.AvgFps is double f ? $" ({f:0} FPS)" : ""));
                next = stable + (fine ? lim.PassoNucleoFino : lim.PassoNucleo);
                continue;
            }
            if (r.Termico)
            {
                oc.Notas.Add($"A pesquisa do núcleo parou no limite térmico ({r.Motivo}).");
                ctx.Log($"⚠ +{next} MHz: {r.Motivo} — paro aqui por segurança e fico em +{stable}");
                break;
            }
            if (r.Recusado)
            {
                ctx.Log($"• +{next} MHz: {r.Motivo} — fico em +{stable}");
                break;
            }

            // Instável: o primeiro valor que falhou é o teto
            oc.NucleoInstavel = oc.NucleoInstavel is int u ? Math.Min(u, next) : next;
            ctx.State.NucleoInstavel = oc.NucleoInstavel;
            if (!fine && lim.PassoNucleoFino < lim.PassoNucleo && stable + lim.PassoNucleoFino < next)
            {
                fine = true; triedCeiling = true;
                ceiling = Math.Min(ceiling, next - 1);
                ctx.Log($"✖ +{next} MHz falhou: {r.Motivo} — recuei para +{stable} e afino de {lim.PassoNucleoFino} em {lim.PassoNucleoFino}");
                next = stable + lim.PassoNucleoFino;
                continue;
            }
            ctx.Log($"✖ +{next} MHz falhou: {r.Motivo} — fico em +{stable}");
            break;
        }

        oc.NucleoMaxEstavel = stable;
        ctx.State.UltimoNucleoEstavel = stable;
        ctx.Passo(null);
        await StockAsync(ctx);
        ctx.Fase(1);
    }

    // ------------------------------------------------------------------ memória

    internal static async Task TuneMemoryAsync(ScanContext ctx)
    {
        var lim = ctx.Lim;
        var oc = ctx.Report.Oc!;
        if (!lim.TocaMemoria || lim.PassoMemoria <= 0) return;
        var st = await ReadAsync();
        int core = Math.Max(0, oc.NucleoMaxEstavel - lim.MargemNucleo); // a memória testa-se com o núcleo final, para não somar instabilidades
        // A retomar depois de um bloqueio nesta fase com a memória de fábrica: o culpado foi este núcleo, por isso desce (como na validação)
        if (ctx.State.NucleoInstavel is int cu && core >= cu) core = Math.Max(0, cu - lim.PassoNucleoFino - lim.MargemNucleo);
        int ceiling = Math.Min(st.MemMax ?? 0, lim.TetoMemoria);
        if (ctx.State.MemoriaInstavel is int bad) ceiling = Math.Min(ceiling, bad - lim.PassoMemoria / 2);
        if (ceiling < lim.PassoMemoria / 2) { ctx.Log("• O driver não deixa subir a memória desta placa"); return; }

        // Referência: memória de fábrica com o núcleo final
        int best = Math.Max(0, ctx.State.UltimaMemoriaEstavel);
        ctx.Estado("Memória da gráfica: medição de referência…");
        var refRun = await TestAsync(ctx, ScanPhase.GpuMemoria, "memória (referência)", core, best, oc.PotenciaPct, lim.PassoDuracao);
        if (!refRun.Passou || refRun.AvgFps is not double bestFps)
        {
            ctx.Log($"✖ A referência da memória falhou ({refRun.Motivo}): não mexo na memória");
            await StockAsync(ctx);
            return;
        }

        int next = best + lim.PassoMemoria;
        int flat = 0;
        var sw = Stopwatch.StartNew();
        while (next <= ceiling)
        {
            if (sw.Elapsed > lim.OrcamentoMemoria) { ctx.Log("• Tempo da pesquisa da memória esgotado: fico com o melhor valor"); break; }
            ctx.Fase(Math.Max(sw.Elapsed / lim.OrcamentoMemoria, ceiling > 0 ? (double)best / ceiling : 0) * 0.98);
            var r = await TestAsync(ctx, ScanPhase.GpuMemoria, "memória", core, next, oc.PotenciaPct, lim.PassoDuracao);
            if (!r.Passou)
            {
                if (r.Termico) { oc.Notas.Add($"A pesquisa da memória parou no limite térmico ({r.Motivo})."); ctx.Log($"⚠ +{next} memória: {r.Motivo} — paro aqui"); break; }
                if (r.Recusado) { ctx.Log($"• +{next} memória: {r.Motivo}"); break; }
                oc.MemoriaInstavel = oc.MemoriaInstavel is int u ? Math.Min(u, next) : next;
                ctx.State.MemoriaInstavel = oc.MemoriaInstavel;
                ctx.Log($"✖ +{next} memória falhou: {r.Motivo} — fico em +{best}");
                break;
            }
            double fps = r.AvgFps ?? 0;
            if (fps > bestFps * 1.002)
            {
                best = next; bestFps = fps; flat = 0;
                ctx.State.UltimaMemoriaEstavel = best;
                ctx.Log($"✔ +{next} memória: {fps:0} FPS (subiu)");
            }
            else if (fps < bestFps * 0.99)
            {
                // A GDDR corrige erros sozinha: em vez de crashar, o FPS desce. Passou do ponto.
                oc.MemoriaInstavel = oc.MemoriaInstavel is int u ? Math.Min(u, next) : next;
                ctx.Log($"✖ +{next} memória: o FPS baixou para {fps:0} (a memória está a corrigir erros) — fico em +{best}");
                break;
            }
            else
            {
                flat++;
                ctx.Log($"• +{next} memória: {fps:0} FPS (não subiu)");
                if (flat >= 2) { ctx.Log($"• O FPS deixou de subir: fico em +{best}"); break; }
            }
            next += lim.PassoMemoria;
        }

        oc.MemoriaMaxEstavel = best;
        ctx.State.UltimaMemoriaEstavel = best;
        ctx.Passo(null);
        await StockAsync(ctx);
        ctx.Fase(1);
    }

    // ------------------------------------------------------------------ margem + validação + ganho medido

    internal static async Task ValidateAsync(ScanContext ctx)
    {
        var lim = ctx.Lim;
        var oc = ctx.Report.Oc!;
        int pct = oc.PotenciaPct;
        int core = Math.Max(0, oc.NucleoMaxEstavel - lim.MargemNucleo);
        int mem = oc.MemoriaMaxEstavel;
        if (mem > 0) mem = Math.Max(0, mem - Math.Max(lim.MargemMemoriaMin, (int)Math.Round(mem * lim.MargemMemoriaPct / 100.0)));
        if (ctx.State.NucleoInstavel is int cu && core >= cu) core = Math.Max(0, cu - lim.PassoNucleoFino - lim.MargemNucleo);
        if (ctx.State.MemoriaInstavel is int mu && mem >= mu) mem = Math.Max(0, mu - lim.PassoMemoria);

        if (core == 0 && mem == 0 && pct <= 100)
        {
            oc.Estavel = false;
            oc.Explicacao = "Não encontrei nenhum valor acima do de fábrica que fosse estável.";
            ctx.Log("• Sem overclock estável acima do de fábrica");
            return;
        }

        ctx.Log($"• Margem de segurança do perfil: +{core} MHz núcleo" + (mem > 0 ? $", +{mem} memória" : "") + (pct > 100 ? $", energia {pct}%" : ""));
        bool ok = false;
        for (int attempt = 1; attempt <= lim.TentativasValidacao; attempt++)
        {
            ctx.Sub(0, 0.75);
            ctx.Estado($"Validação final ({lim.Validacao.TotalMinutes:0} min)" + (attempt > 1 ? $" · tentativa {attempt}" : "") + "…");
            var r = await TestAsync(ctx, ScanPhase.GpuValidacao, "validação", core, mem, pct, lim.Validacao);
            pct = Math.Min(pct, oc.PotenciaPct); // o driver pode ter recusado a energia acima de 100%
            if (r.Passou) { ok = true; ctx.Log($"✔ Validação de {lim.Validacao.TotalMinutes:0} min passou"); break; }
            if (r.Termico)
            {
                if (pct > 100) { ctx.Log($"⚠ Validação: {r.Motivo} — volto o limite de energia a 100% e repito"); pct = 100; continue; }
                oc.Notas.Add($"A validação parou no limite térmico ({r.Motivo}).");
                ctx.Log($"⚠ Validação: {r.Motivo} — a gráfica aquece demasiado mesmo assim");
                break;
            }
            if (r.Recusado) { ctx.Log($"✖ Validação: {r.Motivo}"); break; }
            int nc = Math.Max(0, core - lim.PassoNucleoFino);
            int nm = mem > 0 ? Math.Max(0, mem - lim.PassoMemoria / 2) : 0;
            ctx.Log($"✖ Validação falhou: {r.Motivo} — recuo para +{nc} MHz" + (mem > 0 ? $" / +{nm} memória" : ""));
            core = nc; mem = nm;
            if (core == 0 && mem == 0 && pct <= 100) break;
        }

        oc.Nucleo = ok ? core : 0;
        oc.Memoria = ok ? mem : 0;
        oc.PotenciaPct = ok ? pct : 100;
        oc.Estavel = ok;
        oc.ValidacaoMinutos = (int)Math.Round(lim.Validacao.TotalMinutes);
        if (!ok)
        {
            oc.Explicacao ??= "O overclock não passou a validação final. A gráfica fica nos valores de fábrica.";
            await StockAsync(ctx);
            return;
        }

        // Ganho justo: com a placa já quente, alterna OC e fábrica (2 vezes cada) na mesma cena
        ctx.Estado("A medir o ganho real (overclock vs fábrica)…");
        var ocFps = new List<double>();
        var stockFps = new List<double>();
        for (int i = 0; i < 2; i++)
        {
            ctx.Sub(0.75 + 0.25 * (2 * i) / 4, 0.75 + 0.25 * (2 * i + 1) / 4);
            var a = await TestAsync(ctx, ScanPhase.GpuValidacao, "medição", core, mem, pct, lim.BenchMedicao);
            if (!a.Passou && !a.Termico && !a.Recusado)
            {
                // Falhou com os valores finais já validados: não se guarda um perfil que acabou de dar erro
                oc.Nucleo = 0; oc.Memoria = 0; oc.PotenciaPct = 100; oc.Estavel = false;
                oc.NucleoInstavel = oc.NucleoInstavel is int u ? Math.Min(u, core) : core;
                oc.Explicacao = $"O overclock passou a validação mas falhou logo a seguir ({a.Motivo}). Por segurança a gráfica fica nos valores de fábrica.";
                ctx.Log($"✖ Medição do ganho: {a.Motivo} — não guardo o perfil");
                await StockAsync(ctx);
                return;
            }
            if (!a.Passou) break;
            if (a.AvgFps is double fa) ocFps.Add(fa);
            ctx.Sub(0.75 + 0.25 * (2 * i + 1) / 4, 0.75 + 0.25 * (2 * i + 2) / 4);
            await StockAsync(ctx);
            var (sa, _, sr) = await GpuScan.MeasureAsync(ctx, 1920, 1080, 1, lim.BenchMedicao, ctx.SubA, ctx.SubB);
            if (sa is double s) stockFps.Add(s);
            if (sr is { Passou: false }) break;
        }
        await StockAsync(ctx);
        oc.FpsFinal = GpuScan.Median(ocFps);
        oc.FpsStockQuente = GpuScan.Median(stockFps);
        double? baseFps = oc.FpsStockQuente ?? oc.FpsBase;
        if (oc.FpsFinal is double ff && baseFps is double bf && bf > 0) oc.GanhoPct = Math.Round(100 * (ff / bf - 1), 1);
        if (oc.GanhoPct is double gp && gp < 1)
            oc.Notas.Add(gp <= 0
                ? "O overclock é estável mas não deu FPS a mais neste teste (a placa está limitada pela energia ou pela temperatura): não vale a pena usá-lo."
                : "O ganho medido é muito pequeno (menos de 1%): só vale a pena se quiseres cada FPS.");
        ctx.Log($"✔ Overclock estável: +{core} MHz núcleo" + (mem > 0 ? $", +{mem} memória" : "") +
                (oc.GanhoPct is double g ? $" · {g:+0.#;-0.#;0}% FPS medido" : ""));

        SaveProfile(ctx);
        ctx.Fase(1);
    }

    /// <summary>Guarda o resultado como perfil "Auto · &lt;perfil&gt;" (substitui o anterior com o mesmo nome).</summary>
    private static void SaveProfile(ScanContext ctx)
    {
        var oc = ctx.Report.Oc!;
        string name = $"Auto · {ctx.Lim.Nome}";
        var prof = new OcProfile
        {
            Name = name,
            CoreOffset = oc.Nucleo, MemOffset = oc.Memoria, PowerPct = oc.PotenciaPct,
            Risk = ctx.Lim.Risco.ToLowerInvariant(),
            Description = $"Encontrado pelo scan em {DateTime.Now:dd/MM/yyyy}: +{oc.Nucleo} MHz núcleo" + (oc.Memoria > 0 ? $", +{oc.Memoria} memória" : "") +
                          (oc.PotenciaPct > 100 ? $", energia {oc.PotenciaPct}%" : "") +
                          (oc.GanhoPct is double g ? $". Ganho medido: {g:+0.#;-0.#;0}% FPS" : "") + $". Validado {oc.ValidacaoMinutos} min.",
        };
        try
        {
            var s = AppServices.Settings;
            lock (s.OcProfiles)
            {
                s.OcProfiles.RemoveAll(p => p.Name == name);
                s.OcProfiles.Add(prof);
            }
            s.UnstableOcProfiles.Remove(name);
            s.Save();
            oc.NomePerfil = name;
            ctx.Log($"✔ Guardei o perfil \"{name}\" (vê em Overclock da gráfica)");
        }
        catch (Exception ex) { ctx.Log("✖ Não consegui guardar o perfil: " + ex.Message); }
    }

    // ------------------------------------------------------------------ um passo

    private static async Task<StepResult> TestAsync(ScanContext ctx, ScanPhase phase, string tipo, int core, int mem, int pct, TimeSpan dur)
    {
        ctx.ThrowIfCancelled();
        var oc = ctx.Report.Oc!;
        var st = ctx.State;
        st.Fase = phase;
        st.NucleoEmTeste = core;
        st.MemoriaEmTeste = mem;
        st.PotenciaPct = pct;
        st.ValoresAplicados = core != 0 || mem != 0 || pct != 100;
        // ANTES de aplicar: se o PC bloquear, o próximo arranque sabe o que estava a ser testado. Sem estado gravado não há overclock.
        if (!ctx.SaveState())
            throw new ScanAbortException("Não consegui gravar o estado de segurança no disco (disco cheio ou sem permissões). Parei antes de aplicar o overclock.");
        if (ScanStore.Frozen) throw new OperationCanceledException(ctx.Ct);

        string label = Label(core, mem, pct);
        ctx.Passo($"{label} — a testar…");
        ctx.Estado($"Overclock da gráfica: {label} — a testar…");

        var step = new OcStep { Tipo = tipo, Nucleo = core, Memoria = mem, PotenciaPct = pct };
        var prof = new OcProfile { Name = TestProfileName, CoreOffset = core, MemOffset = mem, PowerPct = pct };
        var token = ctx.Ct;
        // O cancelamento é verificado DENTRO do lock do GpuTuner: se o Stock do fecho/erro já passou, este Apply não escreve nada
        var ar = await RunWithTimeout(() => AppServices.Tuner.ApplyDetailed(prof, writeFan: false,
                                                                            allowed: () => !token.IsCancellationRequested && !ScanStore.Frozen,
                                                                            scanOwner: true), ApplyTimeout);
        if (ar == null)
        {
            st.ValoresAplicados = true; // não se sabe o que ficou na placa: o fim do scan (ou o próximo arranque) repõe Stock
            throw new ScanAbortException("O driver de vídeo não respondeu ao aplicar o overclock. Parei e repus os valores de fábrica.");
        }
        if (ar.Aborted) throw new OperationCanceledException(token);
        // O driver encolheu o valor para a faixa dele (ou escreveu outra coisa): o passo não testa o que se pediu
        bool clamped = ar.CoreOffset != core || ar.MemOffset != mem;
        bool mismatch = ar.CoreError == null && ar.MemError == null &&
                        ((ar.CoreReadBack is int rc && Math.Abs(rc - core) > 10) || (ar.MemReadBack is int rm && Math.Abs(rm - mem) > 20));
        if (clamped || mismatch)
        {
            step.Passou = false;
            step.Motivo = clamped ? $"fora da faixa do driver (aceita até +{ar.CoreOffset} MHz núcleo / +{ar.MemOffset} memória)"
                                  : $"o driver ficou com outro valor (+{ar.CoreReadBack} núcleo / +{ar.MemReadBack} memória)";
            oc.Passos.Add(step);
            await StockAsync(ctx);
            return new StepResult { Passou = false, Recusado = true, Motivo = step.Motivo };
        }
        // O limite de energia acima de 100% foi recusado mas os offsets entraram: continua com energia de fábrica
        if (!ar.Ok && ar.CoreError == null && ar.MemError == null && ar.ClockError == null && pct > 100)
        {
            ctx.Log($"• O driver não deixou subir o limite de energia ({ar.PowerError}): continuo com 100%");
            oc.PotenciaPct = 100;
            st.PotenciaPct = 100;
            step.PotenciaPct = 100;
            ctx.SaveState();
        }
        else if (!ar.Ok)
        {
            step.Passou = false;
            step.Motivo = "o driver recusou o valor (" + ar.Summary + ")";
            oc.Passos.Add(step);
            await StockAsync(ctx);
            return new StepResult { Passou = false, Recusado = true, Motivo = step.Motivo };
        }

        var r = await ctx.Runner.RunAsync(new LoadSpec { Kind = LoadKind.GpuGame, Duration = dur, Adapter = ctx.Adapter, Label = label }, ctx.Ct);
        step.Passou = r.Passou;
        step.Motivo = r.Motivo;
        step.Fps = r.AvgFps is double f ? Math.Round(f, 1) : null;
        step.TempMax = r.Gpu.TempMax;
        step.HotspotMax = r.Gpu.HotspotMax;
        step.PotenciaMaxW = r.Gpu.PotenciaMaxW;
        step.LimiteEnergia = r.PowerCapFracao > 0.3;
        step.Segundos = Math.Round(r.Segundos);
        oc.Passos.Add(step);

        if (r.Cancelado)
        {
            await StockAsync(ctx);
            if (ctx.Ct.IsCancellationRequested) throw new OperationCanceledException(ctx.Ct);
            throw new ScanAbortException("Fechaste a janela do teste: parei o scan e repus os valores de fábrica.");
        }
        if (!r.Passou) { await RecoverAsync(ctx, r); return r; }

        // Passou: confirma que a placa esteve mesmo com os valores do passo (outro programa pode ter escrito por cima,
        // e um "estável" com outros clocks deixaria guardar um perfil que nunca foi testado)
        var now = await ReadAsync();
        if ((now.CoreOffset is int nc && nc != ar.CoreOffset) || (now.MemOffset is int nm && nm != ar.MemOffset))
        {
            step.Passou = false;
            step.Motivo = "outro programa mudou os clocks durante o teste";
            await StockAsync(ctx);
            throw new ScanAbortException($"Outro programa mudou os clocks da gráfica a meio do teste (núcleo {now.CoreOffset:+0;-0;0}, memória {now.MemOffset:+0;-0;0}). " +
                                         "Fecha o Afterburner/EVGA Precision/etc. e repete o scan.");
        }
        return r;
    }

    /// <summary>Depois de uma falha: valores de fábrica, handles da NVAPI renovados e tempo para o driver recuperar.</summary>
    private static async Task RecoverAsync(ScanContext ctx, StepResult r)
    {
        ctx.Passo(null);
        await StockAsync(ctx);
        NvApi.Reset();
        if (r.CrashDriver)
        {
            ctx.Estado("A esperar que o driver de vídeo recupere…");
            try { await Task.Delay(8000, ctx.Ct); } catch (OperationCanceledException) { }
            await StockAsync(ctx); // o driver pode ter voltado com os valores antigos
        }
        else
        {
            try { await Task.Delay(2000, ctx.Ct); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Repõe os valores de fábrica (sem tocar na ventoinha) e regista no estado que já não há OC aplicado.</summary>
    /// <remarks>
    /// Se o driver não confirmar o Stock, o estado NÃO é limpo (fica "valores aplicados" para o fim do scan / próximo arranque
    /// voltarem a tentar) e o scan pára: continuar a testar com um driver que não responde seria pior.
    /// </remarks>
    internal static async Task StockAsync(ScanContext ctx)
    {
        if (!await RestoreStockAsync())
        {
            ctx.State.ValoresAplicados = true;
            ctx.SaveState();
            throw new ScanAbortException("O driver de vídeo não confirmou a reposição dos valores de fábrica. Parei o scan; se o ecrã ficar estranho, reinicia o PC.");
        }
        ctx.State.ValoresAplicados = false;
        ctx.State.NucleoEmTeste = null;
        ctx.State.MemoriaEmTeste = null;
        ctx.SaveState();
    }

    /// <summary>Estado da gráfica (NVML + NVAPI) com prazo: um driver pendurado pára o scan em vez de o prender.</summary>
    internal static async Task<GpuState> ReadAsync()
    {
        var st = await RunWithTimeout(() => AppServices.Tuner.Read(), ApplyTimeout);
        return st ?? throw new ScanAbortException("O driver de vídeo deixou de responder. Parei o scan; se o ecrã ficou estranho, reinicia o PC.");
    }

    /// <summary>Stock com prazo (nunca prende quem chama). Devolve false se o driver não respondeu a tempo ou recusou os offsets 0.</summary>
    public static async Task<bool> RestoreStockAsync()
    {
        if (!AppServices.Tuner.Supported) return true;
        var r = await RunWithTimeout(() => AppServices.Tuner.StockDetailed(writeFan: false), ApplyTimeout);
        if (r == null) return false;
        if (StockConfirmed(r)) return true;
        // Uma segunda tentativa (handles da NVAPI renovados): um TDR pode deixar a primeira escrita sem efeito
        NvApi.Reset();
        r = await RunWithTimeout(() => AppServices.Tuner.StockDetailed(writeFan: false), ApplyTimeout);
        return r != null && StockConfirmed(r);
    }

    /// <summary>Versão bloqueante para o fecho da app / exceções (prazo curto).</summary>
    public static bool RestoreStockBlocking(TimeSpan timeout)
    {
        if (!AppServices.Tuner.Supported) return true;
        try
        {
            var t = Task.Run(() => AppServices.Tuner.StockDetailed(writeFan: false));
            return t.Wait(timeout) && StockConfirmed(t.Result);
        }
        catch { return false; }
    }

    /// <summary>
    /// Os offsets ficaram a 0: ou o driver aceitou a escrita, ou a leitura de volta confirma 0. Sem NVAPI (portáteis bloqueados, AMD)
    /// não há offsets que a app possa ter escrito, por isso conta como reposto.
    /// </summary>
    private static bool StockConfirmed(OcApplyResult r)
    {
        if (r.CoreError == null && r.MemError == null) return (r.CoreReadBack is null or 0) && (r.MemReadBack is null or 0);
        return (r.CoreReadBack ?? 0) == 0 && (r.MemReadBack ?? 0) == 0;
    }

    private static async Task<T?> RunWithTimeout<T>(Func<T> f, TimeSpan timeout) where T : class
    {
        var t = Task.Run(f);
        var done = await Task.WhenAny(t, Task.Delay(timeout));
        if (done != t) return null;
        try { return await t; } catch { return null; }
    }

    private static int PowerPct(GpuState st, TuningLimits lim)
    {
        if (!lim.PowerMaximo || st.PowerDefaultW is not uint def || def == 0 || st.PowerMaxW is not uint max) return 100;
        return Math.Max(100, (int)Math.Floor(100.0 * max / def));
    }

    private static string Label(int core, int mem, int pct) =>
        $"{core:+0;-0;0} MHz núcleo" + (mem != 0 ? $" · {mem:+0;-0;0} memória" : "") + (pct > 100 ? $" · energia {pct}%" : "");
}
