using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace FrameForge.Tuning;

/// <summary>Relatório do scan em HTML (mesmo estilo do relatório de sessão), para "Exportar relatório".</summary>
public static class ScanReportHtml
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    /// <summary>Grava em %AppData%\FrameForge\scan\relatorios e devolve o caminho do ficheiro.</summary>
    public static string Save(ScanReport r, string? folder = null)
    {
        string dir = folder ?? Path.Combine(ScanStore.Folder, "relatorios");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"scan_{r.Data:yyyy-MM-dd_HH-mm}_{r.Perfil}.html");
        File.WriteAllText(path, Build(r), Encoding.UTF8);
        return path;
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string N(double? v, string fmt = "0") => v is double x ? x.ToString(fmt, Pt) : "—";

    public static string Build(ScanReport r)
    {
        var inv = r.Inventario;
        var lim = TuningLimits.For(r.Perfil);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"pt\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>Scan — {r.Data:dd/MM/yyyy}</title><style>{Css}</style></head><body><main>");
        sb.Append("<header><div class=\"brand\">FRAMEFORGE · SCAN E AFINAÇÃO AUTOMÁTICA</div>");
        sb.Append($"<h1>{E(inv.Cpu)} · {E(inv.Gpu)}</h1>");
        sb.Append($"<p class=\"muted\">{r.Data:dd/MM/yyyy HH:mm} · perfil {E(lim.Nome)}" +
                  (r.Fim is DateTime f ? $" · {(f - r.Data).TotalMinutes:0} min" : "") +
                  (r.Resultado != ScanOutcome.Completo ? $" · {E(r.Motivo ?? "incompleto")}" : "") + "</p></header>");

        string grade = r.Nota is int n ? n >= 90 ? "A" : n >= 75 ? "B" : n >= 60 ? "C" : n >= 45 ? "D" : "E" : "?";
        sb.Append($"<section class=\"verdict g{grade}\"><div class=\"grade\">{(r.Nota is int nn ? nn.ToString() : "?")}</div>" +
                  $"<div><h2>{E(r.Resumo)}</h2><p class=\"muted\">Nota geral de 0 a 100: estabilidade, temperaturas e configurações.</p></div></section>");

        // O que está a segurar
        sb.Append("<h3>O que está a segurar o teu PC</h3>");
        if (r.Limitador is LimiterResult lr && lr.Frases.Count > 0)
        {
            foreach (var s in lr.Frases.Take(3)) sb.Append($"<p>{E(s)}</p>");
            sb.Append($"<p class=\"hint\">{E(lr.Metodo)}</p>");
        }
        else sb.Append("<p class=\"muted\">Sem dados suficientes.</p>");

        // Medições
        sb.Append("<h3>Medições</h3><section class=\"cards\">");
        Card(sb, "CPU (todos os núcleos)", N(r.Cpu?.NotaMulti, "N0"), "pontos BENCH");
        Card(sb, "CPU (1 núcleo)", N(r.Cpu?.Nota1Thread, "N0"), "pontos BENCH");
        Card(sb, "CPU de jogo", N(r.Cpu?.FpsJogo), "frames/s da simulação");
        Card(sb, "RAM", N(r.Ram?.LatenciaNs) + " ns", $"{N(r.Ram?.LeituraGBs)} GB/s · {r.Ram?.MHz} MHz");
        Card(sb, "Gráfica 1080p", N(r.Gpu?.Fps1080) + " FPS", $"1% low {N(r.Gpu?.Fps1080Low)}");
        if (r.Gpu?.Fps1440 != null) Card(sb, "Gráfica 1440p", N(r.Gpu.Fps1440) + " FPS", "cena de teste");
        Card(sb, "CPU máx.", N(r.Cpu?.Stress.TempMax) + " °C", r.Cpu?.Stress.ThrottleTermico == true ? "perdeu clock com o calor" : "no stress");
        Card(sb, "Gráfica máx.", N(r.Gpu?.Jogo.TempMax) + " °C", r.Gpu?.Termico.TempMax is double gt ? $"{gt:0} °C em stress máximo" : "numa cena de jogo");
        foreach (var d in r.Discos)
            Card(sb, $"Disco {d.Unidade}", N(d.LeituraMBs, "N0") + " MB/s", (d.Sistema ? "sistema" : "jogos") + (d.Saude != null ? $" · saúde {d.Saude}" : ""));
        sb.Append("</section>");

        // Ganhaste
        sb.Append("<h3>Overclock da gráfica</h3>");
        if (r.Oc is { Estavel: true } oc)
            sb.Append($"<div class=\"finding s1\"><h4>Estável: +{oc.Nucleo} MHz núcleo" + (oc.Memoria > 0 ? $", +{oc.Memoria} memória" : "") +
                      (oc.PotenciaPct > 100 ? $", energia {oc.PotenciaPct}%" : "") + "</h4>" +
                      $"<p>{(oc.GanhoPct is double g ? $"Ganho medido: {g.ToString("+0.#;-0.#;0", Pt)}% FPS. " : "")}Validado {oc.ValidacaoMinutos} min." +
                      (oc.NomePerfil != null ? $" Guardado como \"{E(oc.NomePerfil)}\"." : "") + "</p></div>");
        else sb.Append($"<p class=\"muted\">{E(r.Oc?.Explicacao ?? "Não foi feito.")}</p>");
        if (r.Oc?.Passos.Count > 0)
        {
            sb.Append("<table><tr><th>Passo</th><th>Núcleo</th><th>Memória</th><th>FPS</th><th>Temp.</th><th>Resultado</th></tr>");
            foreach (var p in r.Oc.Passos)
                sb.Append($"<tr><td>{E(p.Tipo)}</td><td>{p.Nucleo:+0;-0;0}</td><td>{p.Memoria:+0;-0;0}</td><td>{N(p.Fps)}</td><td>{N(p.TempMax)} °C</td>" +
                          $"<td>{(p.Passou ? "✔" : "✖ " + E(p.Motivo))}</td></tr>");
            sb.Append("</table>");
        }

        // Configurações
        var cfg = r.Conclusoes.Where(c => c.Categoria == FindingCategory.Configuracao).ToList();
        sb.Append("<h3>Configurações a corrigir</h3>");
        if (cfg.Count == 0) sb.Append("<p class=\"muted\">Nada de importante.</p>");
        foreach (var c in cfg)
            sb.Append($"<div class=\"finding s{Math.Clamp(c.Severidade, 1, 3)}\"><span class=\"tag\">{E(c.Area)}</span><h4>{E(c.Titulo)}</h4><p>{E(c.Detalhe)}</p><p class=\"fix\"><b>O que fazer:</b> {E(c.Acao)}</p></div>");

        if (r.Bios.Count > 0)
        {
            sb.Append("<h3>Passos de BIOS (CPU e RAM)</h3><table><tr><th>Passo</th><th>Onde</th><th>O que fazer</th></tr>");
            foreach (var b in r.Bios)
                sb.Append($"<tr><td><b>{E(b.Titulo)}</b><br><span class=\"hint\">{E(b.Porque)}</span></td><td>{E(b.Onde)}</td><td>{E(b.Texto)}</td></tr>");
            sb.Append("</table>");
        }

        // Upgrades
        sb.Append("<h3>Upgrades que valem a pena</h3>");
        if (r.Upgrades.Count == 0) sb.Append("<p class=\"muted\">Nenhuma peça está a limitar o suficiente para valer a pena trocar.</p>");
        foreach (var u in r.Upgrades)
        {
            string q = WebUtility.UrlEncode(u.PesquisaTermo.Length > 0 ? u.PesquisaTermo : u.Nome);
            sb.Append($"<div class=\"finding s1\"><span class=\"tag\">{E(u.Componente.ToString())}</span><h4>{E(u.Nome)}" +
                      (u.GanhoPct > 0 ? $" · +{u.GanhoPct:0}% FPS estimado" : "") + "</h4>" +
                      $"<p>{E(u.Motivo)}</p>" + (u.PrecoMinEur > 0 ? $"<p><b>{u.PrecoMinEur:0}–{u.PrecoMaxEur:0} €</b> (preço de referência)</p>" : ""));
            foreach (var w in u.Avisos) sb.Append($"<p class=\"hint\">⚠ {E(w)}</p>");
            sb.Append($"<p class=\"hint\">Ver preço atual: <a href=\"https://www.kuantokusta.pt/search?q={q}\">KuantoKusta</a> · " +
                      $"<a href=\"https://www.pcdiga.com/search?q={q}\">PCDIGA</a> · <a href=\"https://www.globaldata.pt/catalogsearch/result/?q={q}\">Globaldata</a> · " +
                      $"<a href=\"https://www.pccomponentes.pt/buscar/?query={q}\">PcComponentes</a></p></div>");
        }
        string disclaimer;
        try { disclaimer = PerfIndex.Disclaimer; } catch { disclaimer = "Estimativas com base em análises publicadas. Os preços mudam todos os dias: confirma na loja."; }
        sb.Append($"<p class=\"hint\">{E(disclaimer)}</p>");

        // PC + registo
        sb.Append("<h3>O teu PC</h3><ul class=\"hw\">");
        foreach (var (k, v) in new[]
        {
            ("Processador", $"{inv.Cpu} ({inv.Nucleos} núcleos / {inv.Threads} threads)"), ("Placa-mãe", $"{inv.PlacaMae} · BIOS {inv.Bios}"),
            ("Gráfica", $"{inv.Gpu} · {inv.VramGB:0} GB · driver {inv.DriverGpu}"), ("RAM", $"{inv.RamGB:0} GB {inv.RamTipo} · {inv.Pentes} pentes · {inv.RamMHz} MHz"),
            ("Monitor", inv.Monitor), ("Windows", inv.Windows), ("Plano de energia", inv.PlanoEnergia),
        })
            sb.Append($"<li><span class=\"muted\">{E(k)}:</span> {E(v)}</li>");
        foreach (var d in inv.Discos) sb.Append($"<li><span class=\"muted\">Disco:</span> {E(d)}</li>");
        sb.Append("</ul>");
        if (r.Registo.Count > 0)
        {
            sb.Append("<h3>Registo</h3><table>");
            foreach (var l in r.Registo) sb.Append($"<tr><td>{E(l)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("<footer>Gerado pelo FrameForge. O overclock da gráfica vale só até reiniciar (nada é gravado na placa).</footer></main></body></html>");
        return sb.ToString();
    }

    private static void Card(StringBuilder sb, string label, string value, string hint) =>
        sb.Append($"<div class=\"card\"><div class=\"label\">{E(label)}</div><div class=\"value\">{E(value)}</div><div class=\"hint\">{E(hint)}</div></div>");

    private const string Css = """
        :root{--bg:#0b0d12;--panel:#141821;--line:#232a36;--text:#e6e9ef;--muted:#8b94a7;--accent:#60a5fa}
        @media (prefers-color-scheme: light){:root{--bg:#f5f6f8;--panel:#fff;--line:#e3e6ec;--text:#141821;--muted:#5d6678}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 "Segoe UI",system-ui,sans-serif}
        main{max-width:1100px;margin:0 auto;padding:24px 16px 48px}a{color:var(--accent)}
        .brand{font-size:12px;letter-spacing:.14em;color:var(--accent);font-weight:700}
        h1{margin:4px 0 0;font-size:26px}h2{margin:0;font-size:20px}h3{margin:28px 0 10px;font-size:16px}h4{margin:0 0 6px}
        .muted{color:var(--muted)}section{margin-top:18px}
        .verdict{display:flex;gap:18px;align-items:center;background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:18px}
        .grade{font-size:30px;font-weight:800;width:72px;height:72px;border-radius:14px;display:grid;place-items:center;background:#1f2937;color:#fff;flex:none}
        .gA .grade{background:#16a34a}.gB .grade{background:#65a30d}.gC .grade{background:#ca8a04}.gD .grade{background:#ea580c}.gE .grade{background:#dc2626}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:10px}
        .card{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:12px 14px}
        .label{font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.06em}.value{font-size:24px;font-weight:700}.hint{font-size:12px;color:var(--muted)}
        .finding{background:var(--panel);border:1px solid var(--line);border-left:4px solid var(--muted);border-radius:12px;padding:14px 16px;margin-bottom:10px}
        .finding.s3{border-left-color:#ef4444}.finding.s2{border-left-color:#f59e0b}.finding.s1{border-left-color:#60a5fa}
        .fix{margin-bottom:0}.tag{display:inline-block;font-size:11px;font-weight:700;padding:2px 8px;border-radius:99px;background:#334155;color:#fff;margin-bottom:6px}
        table{width:100%;border-collapse:collapse;font-size:13px;background:var(--panel);border-radius:12px;overflow:hidden}
        th,td{text-align:left;padding:7px 10px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--muted);font-weight:600}
        .hw{list-style:none;padding:0;display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:6px}
        footer{margin-top:36px;color:var(--muted);font-size:12px}
        @media (max-width:600px){.verdict{flex-direction:column;align-items:flex-start}table{display:block;overflow-x:auto}}
        """;
}
