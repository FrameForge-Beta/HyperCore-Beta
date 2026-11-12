using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using FrameForge.Core;
using FrameForge.Telemetry;

namespace FrameForge.Analysis;

/// <summary>Relatório pós-jogo num único ficheiro HTML (gráficos em SVG embutido, sem internet) + JSON com os dados brutos.</summary>
public static class ReportHtml
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string CauseName(Cause c) => c switch
    {
        Cause.VramFull => "VRAM cheia",
        Cause.RamPaging => "RAM / paginação",
        Cause.GpuThermal => "Temperatura da GPU",
        Cause.GpuPowerBrake => "Energia da GPU",
        Cause.DriverLatency => "Drivers (DPC)",
        Cause.BackgroundCpu => "Programa em 2.º plano (CPU)",
        Cause.BackgroundGpu => "Programa em 2.º plano (GPU)",
        Cause.Disk => "Disco",
        Cause.CpuThrottle => "CPU a perder clock",
        Cause.CpuBound => "CPU no limite",
        Cause.GpuBound => "GPU no limite",
        Cause.Network => "Rede",
        Cause.Engine => "Lado do jogo",
        Cause.AltTab => "Alt+Tab / fora do jogo",
        _ => c.ToString()
    };

    public static string KindName(FixKind k) => k switch
    {
        FixKind.GameSettings => "Corrigível nas definições do jogo",
        FixKind.PcFix => "Corrigível no PC",
        FixKind.Network => "Rede",
        FixKind.Hardware => "Limite do hardware",
        FixKind.GameSide => "Lado do jogo",
        _ => "Informação"
    };

    private static string Color(Cause c) => c switch
    {
        Cause.VramFull or Cause.RamPaging => "#f472b6",
        Cause.GpuThermal or Cause.CpuThrottle or Cause.GpuPowerBrake => "#fb923c",
        Cause.DriverLatency => "#facc15",
        Cause.BackgroundCpu or Cause.BackgroundGpu => "#a78bfa",
        Cause.Disk => "#38bdf8",
        Cause.CpuBound or Cause.GpuBound => "#f87171",
        Cause.Network => "#34d399",
        _ => "#94a3b8"
    };

    public static string Save(SessionReport r)
    {
        string safe = string.Concat(r.Data.Game.Where(ch => char.IsLetterOrDigit(ch) || ch == ' ')).Trim().Replace(' ', '-');
        string baseName = $"{r.Data.Start:yyyy-MM-dd_HH-mm}_{(safe.Length > 0 ? safe : "jogo")}";
        string html = Path.Combine(AppPaths.Reports, baseName + ".html");
        File.WriteAllText(html, Build(r), Encoding.UTF8);
        try
        {
            var summary = new
            {
                r.Data.Game, r.Data.Start, r.DurationS, r.AvgFps, r.Low1Fps, r.Low01Fps, r.StuttersPerMin, r.Grade, r.Verdict,
                Causes = r.CauseCounts.ToDictionary(x => x.Key.ToString(), x => x.Value), r.GpuBoundPct, r.CpuBoundPct,
            };
            File.WriteAllText(Path.Combine(AppPaths.Reports, baseName + ".json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return html;
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string F(double v, string fmt = "0") => v.ToString(fmt, Inv);

    public static string Build(SessionReport r)
    {
        var d = r.Data;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"pt\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>Relatório — {E(d.Game)}</title><style>{Css}</style></head><body><main>");
        sb.Append($"<header><div class=\"brand\">FRAMEFORGE · RELATÓRIO DE SESSÃO</div><h1>{E(d.Game)}</h1>");
        sb.Append($"<p class=\"muted\">{d.Start:dd/MM/yyyy HH:mm} · {F(r.DurationS / 60, "0.0")} min · monitor {d.MonitorHz} Hz");
        if (d.AntiCheat != null) sb.Append($" · anti-cheat: {E(d.AntiCheat)} (medição via ETW, sem injeção)");
        if (d.OcProfile != null) sb.Append($" · OC: {E(d.OcProfile)}");
        if (d.OptimizeLevel > 0) sb.Append($" · otimização nível {d.OptimizeLevel}");
        sb.Append("</p></header>");

        sb.Append($"<section class=\"verdict g{r.Grade}\"><div class=\"grade\">{r.Grade}</div><div><h2>{E(r.Verdict)}</h2><p>{E(r.VerdictDetail)}</p></div></section>");

        sb.Append("<section class=\"cards\">");
        Card(sb, "FPS médio", F(r.AvgFps), "");
        Card(sb, "1% low", F(r.Low1Fps), "média do pior 1% dos frames");
        Card(sb, "0.1% low", F(r.Low01Fps), "");
        Card(sb, "Quebras/min", F(r.StuttersPerMin, "0.0"), $"{r.Events.Count(e => e.Cause != Cause.AltTab)} no total");
        Card(sb, "Consistência", F(r.PacingPct) + "%", "frames a ±20% da mediana");
        Card(sb, "Frametime", F(r.MedianFt, "0.0") + " ms", $"p99 {F(r.P99Ft, "0.0")} ms");
        sb.Append("</section>");

        sb.Append("<section><h3>Frametime (cada pico é uma quebra)</h3>");
        sb.Append(FrameChart(r));
        sb.Append("<div class=\"legend\">");
        foreach (var c in r.CauseCounts.OrderByDescending(x => x.Value))
            sb.Append($"<span><i style=\"background:{Color(c.Key)}\"></i>{E(CauseName(c.Key))} ({c.Value})</span>");
        sb.Append("</div></section>");

        sb.Append("<section><h3>FPS por segundo</h3>");
        sb.Append(LineChart(r.FpsSeries.Select(p => (p.T, p.Fps)).ToList(), "#60a5fa", null, d.MonitorHz));
        sb.Append("</section>");

        if (d.Samples.Count > 4)
        {
            double t0 = r.FrameSeries.Count > 0 ? 0 : 0;
            sb.Append("<section><h3>Carga: GPU (azul) · thread mais ocupado da CPU (vermelho)</h3>");
            sb.Append(MultiChart(d.Samples, new (Func<Sample, double?>, string)[] { (s => s.GpuLoad, "#60a5fa"), (s => s.CpuMaxThread, "#f87171") }, 100));
            sb.Append($"<p class=\"muted\">GPU no limite {F(r.GpuBoundPct)}% do tempo · CPU no limite {F(r.CpuBoundPct)}% · nenhum (trava de FPS/V-Sync/menu) {F(r.CappedPct)}%</p></section>");

            sb.Append("<section><h3>Memória: VRAM % (rosa) · RAM % (amarelo)</h3>");
            sb.Append(MultiChart(d.Samples, new (Func<Sample, double?>, string)[]
            {
                (s => s.VramTotalMB > 0 ? 100.0 * s.VramUsedMB / s.VramTotalMB : null, "#f472b6"), (s => s.RamUsedPct, "#facc15")
            }, 100));
            sb.Append("</section>");

            sb.Append("<section><h3>Temperatura GPU °C (laranja) · consumo GPU W (verde)</h3>");
            sb.Append(MultiChart(d.Samples, new (Func<Sample, double?>, string)[] { (s => s.GpuTemp, "#fb923c"), (s => s.GpuPowerW, "#34d399") },
                Math.Max(100, d.Samples.Max(s => s.GpuPowerW ?? 0) * 1.1)));
            sb.Append("</section>");
        }

        sb.Append("<section><h3>Diagnóstico e correções</h3>");
        if (r.Findings.Count == 0) sb.Append("<p>Nada a apontar nesta sessão. 👌</p>");
        foreach (var f in r.Findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Count))
        {
            sb.Append($"<article class=\"finding s{f.Severity}\"><div class=\"tag k{(int)f.Kind}\">{E(KindName(f.Kind))}</div>");
            sb.Append($"<h4>{E(f.Title)}{(f.Count > 0 ? $" <small>× {f.Count}</small>" : "")}</h4><p>{E(f.Detail)}</p><p class=\"fix\"><b>O que fazer:</b> {E(f.Fix)}</p></article>");
        }
        sb.Append("</section>");

        var real = r.Events.Where(e => e.Cause != Cause.AltTab).ToList();
        if (real.Count > 0)
        {
            sb.Append("<section><h3>Lista de quebras</h3><table><tr><th>Minuto</th><th>Pior frame</th><th>Normal</th><th>Tipo</th><th>Causa</th><th>O que os sensores viram</th></tr>");
            foreach (var e in real.OrderByDescending(e => e.WorstMs).Take(150).OrderBy(e => e.T))
            {
                var ts = TimeSpan.FromMilliseconds(e.T);
                sb.Append($"<tr><td>{(int)ts.TotalMinutes}:{ts.Seconds:00}</td><td>{F(e.WorstMs, "0.0")} ms</td><td>{F(e.MedianMs, "0.0")} ms</td><td>{E(e.Severity)}</td>" +
                          $"<td><i class=\"dot\" style=\"background:{Color(e.Cause)}\"></i>{E(CauseName(e.Cause))}" +
                          (e.Secondary is Cause s2 ? $" <small>+ {E(CauseName(s2))}</small>" : "") + $"</td><td>{E(e.Detail)}</td></tr>");
            }
            sb.Append("</table>");
            if (real.Count > 150) sb.Append($"<p class=\"muted\">Mostradas as 150 piores de {real.Count}.</p>");
            sb.Append("</section>");
        }

        sb.Append("<section><h3>Hardware nesta sessão</h3><ul class=\"hw\">");
        sb.Append($"<li><b>CPU</b> {E(d.Cpu)}</li><li><b>GPU</b> {E(d.Gpu)} ({F(d.VramGB, "0")} GB)</li><li><b>RAM</b> {F(d.RamGB, "0")} GB</li><li><b>Placa-mãe</b> {E(d.Board)}</li>");
        if (d.GameDriveMedia != null) sb.Append($"<li><b>Disco do jogo</b> {E(d.GameDriveMedia)}</li>");
        sb.Append("</ul></section>");

        sb.Append("<footer>Gerado pelo FrameForge. Frames medidos por ETW (eventos Present do Windows, como o PresentMon) — nada é injetado no jogo. " +
                  "Uma quebra é um frame com mais do dobro do tempo normal e pelo menos 8 ms a mais; a causa é o sensor anormal no mesmo instante.</footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void Card(StringBuilder sb, string label, string value, string hint) =>
        sb.Append($"<div class=\"card\"><div class=\"label\">{E(label)}</div><div class=\"value\">{E(value)}</div><div class=\"hint\">{E(hint)}</div></div>");

    private const int W = 1000, H = 200;

    private static string FrameChart(SessionReport r)
    {
        if (r.FrameSeries.Count < 2) return "<p class=\"muted\">Sem dados.</p>";
        double tMax = r.FrameSeries[^1].T, tMin = r.FrameSeries[0].T;
        double yMax = Math.Max(r.MedianFt * 4, Math.Min(r.FrameSeries.Max(p => p.Ft), r.MedianFt * 12));
        string X(double t) => F((t - tMin) / Math.Max(0.001, tMax - tMin) * W, "0.0");
        string Y(double v) => F(H - Math.Min(v, yMax) / yMax * H, "0.0");
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" preserveAspectRatio=\"none\" class=\"chart\">");
        sb.Append($"<line x1=\"0\" x2=\"{W}\" y1=\"{Y(r.MedianFt)}\" y2=\"{Y(r.MedianFt)}\" class=\"ref\"/>");
        sb.Append("<polyline fill=\"none\" stroke=\"#60a5fa\" stroke-width=\"1\" vector-effect=\"non-scaling-stroke\" points=\"");
        foreach (var p in r.FrameSeries) sb.Append(X(p.T)).Append(',').Append(Y(p.Ft)).Append(' ');
        sb.Append("\"/>");
        foreach (var e in r.Events)
            sb.Append($"<circle cx=\"{X(e.T / 1000)}\" cy=\"{Y(e.WorstMs)}\" r=\"3.5\" fill=\"{Color(e.Cause)}\"><title>{E(CauseName(e.Cause))}: {F(e.WorstMs, "0.0")} ms — {E(e.Detail)}</title></circle>");
        sb.Append($"<text x=\"4\" y=\"14\" class=\"ax\">{F(yMax, "0")} ms</text><text x=\"4\" y=\"{F(double.Parse(Y(r.MedianFt), Inv) - 4, "0")}\" class=\"ax\">mediana {F(r.MedianFt, "0.0")} ms</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string LineChart(List<(double T, double V)> pts, string color, double? yMaxFixed, int refLine)
    {
        if (pts.Count < 2) return "<p class=\"muted\">Sem dados.</p>";
        double tMin = pts[0].T, tMax = pts[^1].T, yMax = yMaxFixed ?? Math.Max(refLine * 1.1, pts.Max(p => p.V) * 1.1);
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" preserveAspectRatio=\"none\" class=\"chart\">");
        double yr = H - refLine / yMax * H;
        sb.Append($"<line x1=\"0\" x2=\"{W}\" y1=\"{F(yr, "0.0")}\" y2=\"{F(yr, "0.0")}\" class=\"ref\"/><text x=\"4\" y=\"{F(yr - 4, "0")}\" class=\"ax\">{refLine} Hz</text>");
        sb.Append($"<polyline fill=\"none\" stroke=\"{color}\" stroke-width=\"1.5\" vector-effect=\"non-scaling-stroke\" points=\"");
        foreach (var p in pts) sb.Append(F((p.T - tMin) / Math.Max(0.001, tMax - tMin) * W, "0.0")).Append(',').Append(F(H - Math.Min(p.V, yMax) / yMax * H, "0.0")).Append(' ');
        sb.Append($"\"/><text x=\"4\" y=\"14\" class=\"ax\">{F(yMax, "0")}</text></svg>");
        return sb.ToString();
    }

    private static string MultiChart(List<Sample> s, (Func<Sample, double?> Sel, string Color)[] series, double yMax)
    {
        double tMin = s[0].T, tMax = Math.Max(tMin + 1, s[^1].T);
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" preserveAspectRatio=\"none\" class=\"chart\">");
        int step = Math.Max(1, s.Count / 1500);
        foreach (var (sel, color) in series)
        {
            sb.Append($"<polyline fill=\"none\" stroke=\"{color}\" stroke-width=\"1.5\" vector-effect=\"non-scaling-stroke\" points=\"");
            for (int i = 0; i < s.Count; i += step)
                if (sel(s[i]) is double v)
                    sb.Append(F((s[i].T - tMin) / (tMax - tMin) * W, "0.0")).Append(',').Append(F(H - Math.Min(v, yMax) / yMax * H, "0.0")).Append(' ');
            sb.Append("\"/>");
        }
        sb.Append($"<text x=\"4\" y=\"14\" class=\"ax\">{F(yMax, "0")}</text></svg>");
        return sb.ToString();
    }

    private const string Css = """
        :root{--bg:#0b0d12;--panel:#141821;--line:#232a36;--text:#e6e9ef;--muted:#8b94a7;--accent:#60a5fa}
        @media (prefers-color-scheme: light){:root{--bg:#f5f6f8;--panel:#fff;--line:#e3e6ec;--text:#141821;--muted:#5d6678}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 "Segoe UI",system-ui,sans-serif}
        main{max-width:1100px;margin:0 auto;padding:24px 16px 48px}
        .brand{font-size:12px;letter-spacing:.14em;color:var(--accent);font-weight:700}
        h1{margin:4px 0 0;font-size:30px}h2{margin:0;font-size:20px}h3{margin:28px 0 10px;font-size:16px}h4{margin:0 0 6px}
        .muted{color:var(--muted)}section{margin-top:18px}
        .verdict{display:flex;gap:18px;align-items:center;background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:18px}
        .grade{font-size:44px;font-weight:800;width:72px;height:72px;border-radius:14px;display:grid;place-items:center;background:#1f2937;color:#fff;flex:none}
        .gA .grade{background:#16a34a}.gB .grade{background:#65a30d}.gC .grade{background:#ca8a04}.gD .grade{background:#ea580c}.gE .grade{background:#dc2626}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px}
        .card{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:12px 14px}
        .label{font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.06em}.value{font-size:26px;font-weight:700}.hint{font-size:12px;color:var(--muted)}
        .chart{width:100%;height:200px;background:var(--panel);border:1px solid var(--line);border-radius:12px}
        .ref{stroke:var(--muted);stroke-dasharray:4 4;stroke-width:1;vector-effect:non-scaling-stroke}.ax{fill:var(--muted);font-size:12px}
        .legend{display:flex;flex-wrap:wrap;gap:14px;margin-top:8px;font-size:13px}.legend i,.dot{display:inline-block;width:10px;height:10px;border-radius:50%;margin-right:6px}
        .finding{background:var(--panel);border:1px solid var(--line);border-left:4px solid var(--muted);border-radius:12px;padding:14px 16px;margin-bottom:10px}
        .finding.s3{border-left-color:#ef4444}.finding.s2{border-left-color:#f59e0b}.finding.s1{border-left-color:#60a5fa}
        .fix{margin-bottom:0}.tag{display:inline-block;font-size:11px;font-weight:700;padding:2px 8px;border-radius:99px;background:#334155;color:#fff;margin-bottom:6px}
        .k0{background:#2563eb}.k1{background:#16a34a}.k2{background:#0d9488}.k3{background:#dc2626}.k4{background:#7c3aed}
        table{width:100%;border-collapse:collapse;font-size:13px;background:var(--panel);border-radius:12px;overflow:hidden}
        th,td{text-align:left;padding:7px 10px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--muted);font-weight:600}
        .hw{list-style:none;padding:0;display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:6px}
        footer{margin-top:36px;color:var(--muted);font-size:12px}
        @media (max-width:600px){.verdict{flex-direction:column;align-items:flex-start}table{display:block;overflow-x:auto}}
        """;
}
