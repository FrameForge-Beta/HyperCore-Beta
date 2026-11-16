using FrameForge.Hardware;
using FrameForge.Optimize;

namespace FrameForge.Tuning;

/// <summary>Fase final do scan: limitador, conclusões, passos de BIOS, upgrades, nota geral e resumo.</summary>
public static class ScanAnalysis
{
    public static void Run(ScanReport r, SystemProbe? p)
    {
        r.Limitador = Safe(() => BottleneckFromScan.Compute(r));
        r.Conclusoes.Clear();

        // Upgrades (só quando a peça limita; o cálculo é do índice de desempenho e preços)
        try { r.Upgrades = Upgrades.Suggest(r) ?? new List<UpgradeOption>(); }
        catch { r.Upgrades = new List<UpgradeOption>(); }

        // O que está a segurar o PC (1–3 frases)
        if (r.Limitador != null)
            foreach (var f in r.Limitador.Frases.Take(3))
                r.Conclusoes.Add(new ScanFinding
                {
                    Severidade = 2, Area = r.Limitador.Principal, Categoria = FindingCategory.Segurar,
                    Titulo = f, Detalhe = r.Limitador.Metodo,
                });

        // Configurações a corrigir (medidas + estáticas)
        var advice = Safe(() => HardwareAdvisor.AnalyzeWithScan(p, r)) ?? new List<Advice>();
        foreach (var a in advice)
            r.Conclusoes.Add(new ScanFinding
            {
                Severidade = a.Severity, Area = a.Area, Titulo = a.Title, Detalhe = a.Detail, Acao = a.Action,
                Categoria = FindingCategory.Configuracao, Pagina = a.Page, PaginaTexto = a.PageLabel,
            });

        r.Bios = Safe(() => BiosGuide.Steps(r, p)) ?? new List<BiosStep>();

        foreach (var u in r.Upgrades)
            r.Conclusoes.Add(new ScanFinding
            {
                Severidade = 1, Area = u.Componente.ToString(), Categoria = FindingCategory.Upgrade,
                Titulo = u.Nome + (u.GanhoPct > 0 ? $" (+{u.GanhoPct:0}% FPS estimado)" : ""),
                Detalhe = u.Motivo, Acao = u.PrecoMinEur > 0 ? $"{u.PrecoMinEur:0}–{u.PrecoMaxEur:0} €" : "",
            });

        r.Nota = Score(r);
        r.Resumo = Summary(r);
    }

    /// <summary>Nota 0–100: estabilidade e temperaturas pesam mais; cada configuração a corrigir tira um pouco.</summary>
    public static int? Score(ScanReport r)
    {
        if (r.Cpu == null && r.Gpu == null) return null;
        double s = 100;
        if (r.Cpu is { Estavel: false }) s -= 25;
        if (r.Gpu is { Estavel: false }) s -= 25;
        if (r.Cpu?.Stress.ThrottleTermico == true) s -= 10;
        if (r.Gpu?.Jogo.ThrottleTermico == true) s -= 10;
        if (r.Discos.Any(d => d.SaudeAlerta)) s -= 10;
        double cfg = r.Conclusoes.Where(c => c.Categoria == FindingCategory.Configuracao)
                                 .Sum(c => c.Severidade >= 3 ? 6 : c.Severidade == 2 ? 3 : 0);
        s -= Math.Min(30, cfg);
        return (int)Math.Clamp(Math.Round(s), 20, 100);
    }

    public static string Summary(ScanReport r)
    {
        var parts = new List<string>();
        var lim = r.Limitador;
        if (lim != null)
            parts.Add(lim.Principal switch
            {
                "GPU" => $"A gráfica é o limite em {lim.Resolucao}",
                "CPU" => $"O processador é o limite em {lim.Resolucao}",
                "Monitor" => "O PC já dá mais FPS do que o monitor mostra",
                "Equilibrado" => "CPU e gráfica equilibradas",
                _ => "Limitador por confirmar",
            });
        if (r.Oc is { Estavel: true } oc)
            parts.Add(oc.GanhoPct is double g ? $"overclock estável {g:+0.#;-0.#;0}% FPS" : "overclock estável");
        int fix = r.Conclusoes.Count(c => c.Categoria == FindingCategory.Configuracao && c.Severidade >= 2);
        if (fix > 0) parts.Add(fix == 1 ? "1 coisa a corrigir" : $"{fix} coisas a corrigir");
        if (r.Cpu is { Estavel: false } || r.Gpu is { Estavel: false }) parts.Add("há instabilidade a resolver");
        return string.Join(" · ", parts);
    }

    private static T? Safe<T>(Func<T?> f) where T : class { try { return f(); } catch { return null; } }
}
