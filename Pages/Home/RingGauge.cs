using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FrameForge.Pages.Home;

/// <summary>Anel de progresso (0–1) desenhado à mão: trilho + arco com pontas redondas. <see cref="AnimateTo"/> desliza o valor.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>Muda o valor com uma animação curta (o valor final fica preso pela animação: usa sempre este método).</summary>
    public void AnimateTo(double v, int ms = 650)
    {
        v = Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1);
        BeginAnimation(ValueProperty, new DoubleAnimation(v, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight), t = Thickness;
        if (size <= t * 2) return;
        double r = (size - t) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        if (Track != null) dc.DrawEllipse(null, new Pen(Track, t), c, r, r);
        double v = Math.Clamp(Value, 0, 1);
        if (v < 0.004 || Fill == null) return;
        var pen = new Pen(Fill, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v > 0.9995) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a = v * 2 * Math.PI;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
            ctx.ArcTo(new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a)), new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>Mini-gráfico de linha (últimos N valores) com área esbatida por baixo.</summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly Queue<double> _values = new();
    public int Capacity { get; set; } = 60;
    /// <summary>Teto fixo do eixo (ex.: 100 para %); 0 = automático.</summary>
    public double Max { get; set; } = 100;
    public Brush Stroke { get; set; } = Brushes.DodgerBlue;

    public void Push(double? v)
    {
        if (v is not double d || !double.IsFinite(d)) return;
        _values.Enqueue(d);
        while (_values.Count > Capacity) _values.Dequeue();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4 || _values.Count < 2) return;
        var vals = _values.ToArray();
        double max = Max > 0 ? Max : Math.Max(1, vals.Max() * 1.15);
        double step = w / (Capacity - 1), x0 = w - step * (vals.Length - 1);
        Point P(int i) => new(x0 + step * i, h - 1 - (h - 2) * Math.Clamp(vals[i] / max, 0, 1));

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            for (int i = 1; i < vals.Length; i++) ctx.LineTo(P(i), true, true);
        }
        line.Freeze();
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < vals.Length; i++) ctx.LineTo(P(i), true, true);
            ctx.LineTo(new Point(w, h), true, true);
        }
        area.Freeze();
        if (Stroke is SolidColorBrush sc)
        {
            var c = sc.Color;
            var fill = new LinearGradientBrush(Color.FromArgb(0x50, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);
        }
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
