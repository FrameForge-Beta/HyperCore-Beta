using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FrameForge.Fans;

namespace FrameForge.Pages;

/// <summary>
/// Editor gráfico de curva (temperatura → % da ventoinha), como no FanControl:
/// arrasta os pontos; clique esquerdo num sítio vazio adiciona um ponto; clique direito num ponto remove-o.
/// A linha vertical mostra a temperatura atual e o círculo a velocidade que a curva está a pedir.
/// </summary>
public sealed class CurveGraph : Canvas
{
    private const double TMin = 20, TMax = 105, Pad = 36;
    private readonly FanCurve _curve;
    private int _drag = -1;
    public event Action? Changed;
    public double? LiveTemp { get; set; }
    public double? LiveOutput { get; set; }

    public CurveGraph(FanCurve curve)
    {
        _curve = curve;
        Height = 260;
        MinWidth = 380;
        Background = Brushes.Transparent;
        ClipToBounds = true;
        SizeChanged += (_, _) => Redraw();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += (_, _) => { if (_drag >= 0) { _drag = -1; ReleaseMouseCapture(); Changed?.Invoke(); } };
        MouseRightButtonDown += OnRight;
        Cursor = Cursors.Cross;
        ToolTip = "Arrasta os pontos · clique esquerdo adiciona · clique direito remove";
    }

    private double W => Math.Max(10, ActualWidth - Pad - 10);
    private double H => Math.Max(10, ActualHeight - Pad - 8);
    private double X(double t) => Pad + (t - TMin) / (TMax - TMin) * W;
    private double Y(double s) => 8 + (1 - s / 100) * H;
    private double T(double x) => Math.Clamp(Math.Round(TMin + (x - Pad) / W * (TMax - TMin)), TMin, TMax);
    private double S(double y) => Math.Clamp(Math.Round((1 - (y - 8) / H) * 100), 0, 100);

    private List<CurvePoint> Sorted() => _curve.Points.OrderBy(p => p.Temp).ToList();

    public void Redraw()
    {
        Children.Clear();
        var line = Ui.Res("Line");
        var muted = Ui.Res("Muted");
        var accent = Ui.Res("Accent");
        for (double t = 30; t <= 100; t += 10)
        {
            Children.Add(new Line { X1 = X(t), X2 = X(t), Y1 = 8, Y2 = 8 + H, Stroke = line, StrokeThickness = 1 });
            var lbl = new TextBlock { Text = $"{t:0}°", FontSize = 10, Foreground = muted };
            SetLeft(lbl, X(t) - 8); SetTop(lbl, 10 + H);
            Children.Add(lbl);
        }
        for (double s = 0; s <= 100; s += 20)
        {
            Children.Add(new Line { X1 = Pad, X2 = Pad + W, Y1 = Y(s), Y2 = Y(s), Stroke = line, StrokeThickness = 1 });
            var lbl = new TextBlock { Text = $"{s:0}%", FontSize = 10, Foreground = muted };
            SetLeft(lbl, 2); SetTop(lbl, Y(s) - 7);
            Children.Add(lbl);
        }

        if (_curve.Kind == CurveKind.Graph)
        {
            var pts = Sorted();
            var poly = new Polyline { Stroke = accent, StrokeThickness = 2.5 };
            if (pts.Count > 0) poly.Points.Add(new Point(X(TMin), Y(pts[0].Speed)));
            foreach (var p in pts) poly.Points.Add(new Point(X(p.Temp), Y(p.Speed)));
            if (pts.Count > 0) poly.Points.Add(new Point(X(TMax), Y(pts[^1].Speed)));
            Children.Add(poly);
            foreach (var p in pts)
            {
                var e = new Ellipse { Width = 13, Height = 13, Fill = Brushes.White, Stroke = accent, StrokeThickness = 3, ToolTip = $"{p.Temp:0} °C → {p.Speed:0} %" };
                SetLeft(e, X(p.Temp) - 6.5); SetTop(e, Y(p.Speed) - 6.5);
                Children.Add(e);
            }
        }
        else
        {
            // Outros tipos: mostra a resposta da curva (só leitura; edita-se pelos números)
            var poly = new Polyline { Stroke = accent, StrokeThickness = 2.5 };
            for (double t = TMin; t <= TMax; t += 1) poly.Points.Add(new Point(X(t), Y(FanEngine.Evaluate(_curve, t))));
            Children.Add(poly);
        }

        if (LiveTemp is double lt && lt > TMin && lt < TMax)
        {
            Children.Add(new Line { X1 = X(lt), X2 = X(lt), Y1 = 8, Y2 = 8 + H, Stroke = Ui.Res("Warn"), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 } });
            if (LiveOutput is double lo)
            {
                var dot = new Ellipse { Width = 11, Height = 11, Fill = Ui.Res("Warn") };
                SetLeft(dot, X(lt) - 5.5); SetTop(dot, Y(lo) - 5.5);
                Children.Add(dot);
            }
            var lbl = new TextBlock { Text = $"agora {lt:0.0} °C" + (LiveOutput is double o ? $" → {o:0}%" : ""), FontSize = 11, Foreground = Ui.Res("Warn"), FontWeight = FontWeights.SemiBold };
            SetLeft(lbl, Math.Min(X(lt) + 6, Pad + W - 120)); SetTop(lbl, 10);
            Children.Add(lbl);
        }
    }

    private int HitPoint(Point m)
    {
        var pts = _curve.Points;
        for (int i = 0; i < pts.Count; i++)
            if (Math.Abs(X(pts[i].Temp) - m.X) < 10 && Math.Abs(Y(pts[i].Speed) - m.Y) < 10) return i;
        return -1;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_curve.Kind != CurveKind.Graph) return;
        var m = e.GetPosition(this);
        _drag = HitPoint(m);
        if (_drag < 0)
        {
            double t = T(m.X);
            if (_curve.Points.Any(p => Math.Abs(p.Temp - t) < 1)) return;
            _curve.Points.Add(new CurvePoint { Temp = t, Speed = S(m.Y) });
            _drag = _curve.Points.Count - 1;
        }
        CaptureMouse();
        Redraw();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var m = e.GetPosition(this);
        var p = _curve.Points[_drag];
        // não deixa passar por cima dos vizinhos (a curva tem de ser crescente na temperatura)
        var others = _curve.Points.Where((_, i) => i != _drag).ToList();
        double lo = others.Where(o => o.Temp < p.Temp).Select(o => o.Temp + 1).DefaultIfEmpty(TMin).Max();
        double hi = others.Where(o => o.Temp > p.Temp).Select(o => o.Temp - 1).DefaultIfEmpty(TMax).Min();
        p.Temp = Math.Clamp(T(m.X), lo, hi);
        p.Speed = S(m.Y);
        Redraw();
    }

    private void OnRight(object sender, MouseButtonEventArgs e)
    {
        if (_curve.Kind != CurveKind.Graph) return;
        int i = HitPoint(e.GetPosition(this));
        if (i >= 0 && _curve.Points.Count > 2)
        {
            _curve.Points.RemoveAt(i);
            Redraw();
            Changed?.Invoke();
        }
    }
}
