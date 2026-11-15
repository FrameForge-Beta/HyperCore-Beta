using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FrameForge.Net;

namespace FrameForge.Pages;

/// <summary>
/// Gráfico ao vivo do ping (linha + área com gradiente), do router (tracejado) e das perdas (marcas vermelhas no topo).
/// Desenhado em OnRender (barato a 2 atualizações por segundo). Passar o rato mostra o valor de cada medição.
/// </summary>
internal sealed class GameNetGraph : FrameworkElement
{
    private NetSample[] _samples = Array.Empty<NetSample>();
    private Point? _mouse;
    private static readonly double[] Scales = { 20, 40, 60, 80, 100, 150, 200, 300, 500, 1000 };
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>Quantas medições cabem na largura (120 = 60 s a 2 por segundo).</summary>
    public int Capacity { get; set; } = 120;

    /// <summary>Texto mostrado quando ainda não há medições.</summary>
    public string EmptyText { get; set; } = "Carrega em «Começar a medir» para veres o ping ao vivo.";

    /// <summary>Limite acima do qual uma medição conta como pico (linha de referência). null = não desenha.</summary>
    public double? SpikeLine { get; set; }

    public GameNetGraph()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    public void SetSamples(NetSample[] samples)
    {
        _samples = samples;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var line = Ui.Res("Line");
        var faint = Ui.Res("Faint");
        var accent = ((SolidColorBrush)Ui.Res("Accent")).Color;
        var accent2 = ((SolidColorBrush)Ui.Res("Accent2")).Color;
        var bad = Ui.Res("Bad");

        // Fundo transparente para receber o rato em toda a área
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        const double left = 36, right = 6, top = 10, bottom = 8;
        var plot = new Rect(left, top, Math.Max(10, w - left - right), Math.Max(10, h - top - bottom));

        double max = 0;
        foreach (var s in _samples)
        {
            if (s.Ms is double a) max = Math.Max(max, a);
            if (s.RouterMs is double r) max = Math.Max(max, r);
        }
        double scale = Scales.FirstOrDefault(v => v >= max * 1.15, 1000);

        // Grelha horizontal com valores
        var gridPen = new Pen(line, 1) { DashStyle = new DashStyle(new double[] { 2, 4 }, 0) };
        gridPen.Freeze();
        for (int i = 0; i <= 4; i++)
        {
            double v = scale * i / 4;
            double y = Math.Round(plot.Bottom - plot.Height * i / 4) + 0.5;
            dc.DrawLine(i == 0 ? new Pen(line, 1) : gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Text(v.ToString("0", CultureInfo.InvariantCulture), 10, faint, dpi);
            dc.DrawText(ft, new Point(plot.Left - 8 - ft.Width, y - ft.Height / 2));
        }

        if (_samples.Length == 0)
        {
            var ft = Text(EmptyText, 12.5, Ui.Res("Muted"), dpi);
            ft.MaxTextWidth = Math.Max(50, plot.Width - 40);
            ft.TextAlignment = TextAlignment.Center;
            dc.DrawText(ft, new Point(plot.Left + 20, plot.Top + plot.Height / 2 - ft.Height / 2 - 6));
            return;
        }

        double step = plot.Width / Math.Max(1, Capacity - 1);
        double X(int i) => plot.Right - (_samples.Length - 1 - i) * step;
        double Y(double ms) => plot.Bottom - plot.Height * Math.Min(ms, scale) / scale;

        // Linha de pico
        if (SpikeLine is double spike && spike < scale)
        {
            var sp = new Pen(new SolidColorBrush(Color.FromArgb(90, 0xFB, 0xBF, 0x24)), 1) { DashStyle = new DashStyle(new double[] { 6, 4 }, 0) };
            double y = Y(spike);
            dc.DrawLine(sp, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        // Router (tracejado discreto)
        var routerPen = new Pen(Ui.Res("Muted"), 1.3) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0), LineJoin = PenLineJoin.Round };
        DrawSeries(dc, routerPen, null, i => _samples[i].RouterMs, X, Y, plot);

        // Alvo: área com gradiente + linha
        var fill = new LinearGradientBrush(Color.FromArgb(110, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B), 90);
        var stroke = new LinearGradientBrush(accent, accent2, 0);
        var pen = new Pen(stroke, 2.2) { LineJoin = PenLineJoin.Round };
        DrawSeries(dc, pen, fill, i => _samples[i].Ms, X, Y, plot);

        // Perdas: marcas vermelhas no topo
        for (int i = 0; i < _samples.Length; i++)
            if (_samples[i].Ms == null)
                dc.DrawRoundedRectangle(bad, null, new Rect(X(i) - 2, plot.Top - 6, 4, 10), 2, 2);

        // Último ponto
        var last = _samples[^1];
        if (last.Ms is double lm)
        {
            var c = new Point(X(_samples.Length - 1), Y(lm));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, accent2.R, accent2.G, accent2.B)), null, c, 7, 7);
            dc.DrawEllipse(new SolidColorBrush(accent2), null, c, 3.2, 3.2);
        }

        // Cursor do rato
        if (_mouse is Point m && plot.Contains(new Point(Math.Clamp(m.X, plot.Left, plot.Right), m.Y)))
        {
            int idx = (int)Math.Round(_samples.Length - 1 - (plot.Right - m.X) / step);
            if (idx >= 0 && idx < _samples.Length)
            {
                var s = _samples[idx];
                double x = X(idx);
                dc.DrawLine(new Pen(Ui.Res("LineStrong"), 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
                if (s.Ms is double v) dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(accent), 2), new Point(x, Y(v)), 4, 4);

                string ago = Ago(DateTime.UtcNow - s.At);
                string main = s.Ms is double mv ? $"{mv:0} ms" : "perdido";
                string sub = (s.RouterMs is double rv ? $"router {rv:0.#} ms · " : s.RouterTried ? "router sem resposta · " : "") + ago;
                var t1 = Text(main, 13, s.Ms == null ? bad : Ui.Res("Fg"), dpi, bold: true);
                var t2 = Text(sub, 10.5, Ui.Res("Muted"), dpi);
                double bw = Math.Max(t1.Width, t2.Width) + 20, bh = t1.Height + t2.Height + 12;
                double bx = x + 12 + bw > plot.Right ? x - 12 - bw : x + 12;
                double by = plot.Top + 4;
                dc.DrawRoundedRectangle(Ui.Res("Panel"), new Pen(Ui.Res("LineStrong"), 1), new Rect(bx, by, bw, bh), 8, 8);
                dc.DrawText(t1, new Point(bx + 10, by + 6));
                dc.DrawText(t2, new Point(bx + 10, by + 6 + t1.Height));
            }
        }
    }

    private void DrawSeries(DrawingContext dc, Pen pen, Brush? fill, Func<int, double?> value, Func<int, double> X, Func<double, double> Y, Rect plot)
    {
        int i = 0;
        while (i < _samples.Length)
        {
            while (i < _samples.Length && value(i) == null) i++;
            int start = i;
            while (i < _samples.Length && value(i) != null) i++;
            int end = i - 1;
            if (end < start) continue;
            if (end == start)
            {
                dc.DrawEllipse(pen.Brush, null, new Point(X(start), Y(value(start)!.Value)), pen.Thickness, pen.Thickness);
                continue;
            }

            if (fill != null)
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(X(start), plot.Bottom), true, true);
                    for (int k = start; k <= end; k++) g.LineTo(new Point(X(k), Y(value(k)!.Value)), true, true);
                    g.LineTo(new Point(X(end), plot.Bottom), true, false);
                }
                area.Freeze();
                dc.DrawGeometry(fill, null, area);
            }

            var path = new StreamGeometry();
            using (var g = path.Open())
            {
                g.BeginFigure(new Point(X(start), Y(value(start)!.Value)), false, false);
                for (int k = start + 1; k <= end; k++) g.LineTo(new Point(X(k), Y(value(k)!.Value)), true, true);
            }
            path.Freeze();
            dc.DrawGeometry(null, pen, path);
        }
    }

    private static string Ago(TimeSpan t) => t.TotalSeconds < 1.5 ? "agora" : t.TotalSeconds < 90 ? $"há {t.TotalSeconds:0} s" : $"há {t.TotalMinutes:0} min";

    private static FormattedText Text(string s, double size, Brush brush, double dpi, bool bold = false) =>
        new(s, CultureInfo.GetCultureInfo("pt-PT"), FlowDirection.LeftToRight, bold ? FaceBold : Face, size, brush, dpi);
}
