using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameForge.Peripherals;

namespace FrameForge.Pages;

/// <summary>Aba "Rato": taxa de polling (Raw Input numa thread própria) e teste de botões, roda e duplo clique avariado.</summary>
internal sealed class PeriphMouseView : StackPanel, IPeriphView
{
    private const double ChatterMs = 80;
    private static readonly int[] Nominal = { 125, 250, 500, 1000, 2000, 4000, 8000 };

    // ---------------- polling
    private readonly RawMouseMonitor _raw = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<(long Ticks, IntPtr Device)> _batch = new(4096);
    private readonly Dictionary<IntPtr, long> _deviceCounts = new();
    private readonly List<double> _history = new();
    private const int HistoryLen = 90; // 9 s
    private long _prevStamp;
    private IntPtr _mainDevice;
    private double _sumTime, _maxHz, _lastHz;
    private long _sumCount, _totalReports;
    private int _idleTicks;
    private bool _measuring;

    private readonly Button _startBtn;
    private readonly Path _ringArc = new() { StrokeThickness = 12, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly TextBlock _ringValue = new() { Text = "0", FontSize = 40, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _ringSub = new() { Text = "Hz agora", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _nominalPill = new() { CornerRadius = new CornerRadius(99), Padding = new Thickness(11, 3, 11, 4), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _nominalText = new() { FontSize = 11.5, FontWeight = FontWeights.Bold };
    private readonly (Border Box, TextBlock Value, TextBlock Hint) _sAvg, _sMax, _sReports;
    private readonly Canvas _chart = new() { ClipToBounds = true, Height = 132 };
    private readonly (Border Box, TextBlock Glyph, TextBlock Text) _pollVerdict;
    private readonly Ellipse _pulse = new() { Width = 170, Height = 170, StrokeThickness = 2, Opacity = 0, IsHitTestVisible = false };

    // ---------------- botões
    private sealed class BtnState
    {
        public string Name = "";
        public FrameworkElement Visual = null!;
        public Border? Shape;
        public int Clicks, Suspect;
        public long LastDown;
        public double MinGapMs = double.NaN;
        public bool Down, Tested;
        public TextBlock CClicks = null!, CSuspect = null!, CGap = null!;
    }
    private readonly Dictionary<MouseButton, BtnState> _btns = new();
    private readonly Border _pad;
    private readonly TextBlock _wheelUp = new(), _wheelDown = new(), _wheelSide = new();
    private readonly TextBlock _arrowUp, _arrowDown;
    private int _wUp, _wDown, _wLeft, _wRight;
    private readonly (Border Box, TextBlock Glyph, TextBlock Text) _btnVerdict;

    public PeriphMouseView()
    {
        // ======================= polling =======================
        _startBtn = PeriphUi.Btn("", "Começar a medir", (_, _) => Toggle(), "BtnAccent");
        _startBtn.Padding = new Thickness(18, 11, 20, 11);
        var poll = new StackPanel();
        poll.Children.Add(PeriphUi.CardHead("", "Taxa de polling",
            "Quantas vezes por segundo o rato fala com o PC. Carrega em Começar e mexe o rato depressa, em círculos.", _startBtn));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // anel
        var ring = new Grid { Width = 170, Height = 170, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var track = new Ellipse { StrokeThickness = 12, Margin = new Thickness(0) };
        track.SetResourceReference(Shape.StrokeProperty, "Hover");
        ring.Children.Add(track);
        _ringArc.SetResourceReference(Shape.StrokeProperty, "AccentGradient");
        _ringArc.Effect = new DropShadowEffect { Color = Color.FromRgb(0x2E, 0x9B, 0xFF), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.45 };
        ring.Children.Add(_ringArc);
        _pulse.SetResourceReference(Shape.StrokeProperty, "Accent2");
        _pulse.RenderTransformOrigin = new Point(0.5, 0.5);
        _pulse.RenderTransform = new ScaleTransform(1, 1);
        ring.Children.Add(_pulse);
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _ringValue.FontFamily = PeriphUi.Mono;
        _ringValue.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        _ringSub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        center.Children.Add(_ringValue);
        center.Children.Add(_ringSub);
        ring.Children.Add(center);
        var ringCol = new StackPanel();
        ringCol.Children.Add(ring);
        _nominalPill.Child = _nominalText;
        _nominalPill.HorizontalAlignment = HorizontalAlignment.Left;
        _nominalPill.Margin = new Thickness(0, 14, 0, 0);
        ringCol.Children.Add(_nominalPill);
        SetNominal(null);
        grid.Children.Add(ringCol);
        DrawArc(0);

        // estatísticas + gráfico
        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -10, 0) };
        _sAvg = PeriphUi.Stat("Média", "—", "enquanto mexes");
        _sMax = PeriphUi.Stat("Máximo", "—", "melhor momento");
        _sReports = PeriphUi.Stat("Leituras", "0", "movimentos recebidos");
        stats.Children.Add(_sAvg.Box);
        stats.Children.Add(_sMax.Box);
        stats.Children.Add(_sReports.Box);
        right.Children.Add(stats);
        var chartBox = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(0), Child = _chart, ClipToBounds = true };
        chartBox.SetResourceReference(Border.BackgroundProperty, "Bg");
        chartBox.SetResourceReference(Border.BorderBrushProperty, "Line");
        right.Children.Add(chartBox);
        _chart.SizeChanged += (_, _) => DrawChart();
        grid.Children.Add(right);
        poll.Children.Add(grid);

        _pollVerdict = PeriphUi.Verdict("Ainda não mediste. Os ratos para jogos costumam estar a 1000 Hz ou mais.");
        _pollVerdict.Box.Margin = new Thickness(0, 16, 0, 0);
        poll.Children.Add(_pollVerdict.Box);
        Children.Add(PeriphUi.Card(poll));

        _timer.Tick += (_, _) => Tick();

        // ======================= botões e roda =======================
        var clear = PeriphUi.Btn("", "Limpar", (_, _) => ResetButtons());
        var bt = new StackPanel();
        bt.Children.Add(PeriphUi.CardHead("", "Botões, roda e duplo clique",
            "Clica dentro da zona com cada botão e mexe a roda. Clica devagar, um clique de cada vez, para apanhar duplos cliques avariados.", clear));

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // zona de teste com o desenho do rato
        var canvas = BuildMouse(out _arrowUp, out _arrowDown);
        var padGrid = new Grid();
        var dash = new Rectangle { RadiusX = 14, RadiusY = 14, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 5, 4 } };
        dash.SetResourceReference(Shape.StrokeProperty, "LineStrong");
        padGrid.Children.Add(dash);
        var padInner = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var padHint = Ui.T("CLICA AQUI DENTRO", "Label", 10, null, FontWeights.Bold);
        padHint.HorizontalAlignment = HorizontalAlignment.Center;
        padHint.Margin = new Thickness(0, 0, 0, 14);
        padHint.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        padInner.Children.Add(padHint);
        padInner.Children.Add(canvas);
        padGrid.Children.Add(padInner);
        _pad = new Border { Height = 330, CornerRadius = new CornerRadius(14), Child = padGrid, Margin = new Thickness(0, 0, 22, 0), Cursor = Cursors.Hand, Focusable = false };
        _pad.SetResourceReference(Border.BackgroundProperty, "Bg");
        _pad.PreviewMouseDown += Pad_Down;
        _pad.PreviewMouseUp += Pad_Up;
        _pad.PreviewMouseWheel += Pad_Wheel;
        _pad.LostMouseCapture += (_, _) => ReleaseAll();
        _pad.MouseEnter += (_, _) => dash.SetResourceReference(Shape.StrokeProperty, "Accent");
        _pad.MouseLeave += (_, _) => dash.SetResourceReference(Shape.StrokeProperty, "LineStrong");
        body.Children.Add(_pad);

        // tabela
        var table = new Grid { VerticalAlignment = VerticalAlignment.Top };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        void Head(string text, int col)
        {
            var t = Ui.T(text.ToUpperInvariant(), "Label", 10);
            t.Margin = new Thickness(0, 0, 0, 8);
            if (col > 0) t.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(t, col);
            table.Children.Add(t);
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Head("Botão", 0); Head("Cliques", 1); Head("Suspeitos", 2); Head("Mais rápido", 3);
        int row = 1;
        foreach (var b in _btns.Values)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0) };
            line.SetResourceReference(Border.BorderBrushProperty, "Line");
            Grid.SetRow(line, row); Grid.SetColumnSpan(line, 4);
            table.Children.Add(line);
            var name = Ui.T(b.Name, "Body");
            name.Margin = new Thickness(0, 9, 0, 9);
            Grid.SetRow(name, row);
            table.Children.Add(name);
            b.CClicks = Cell("0", row, 1);
            b.CSuspect = Cell("0", row, 2);
            b.CGap = Cell("—", row, 3);
            row++;
        }
        TextBlock Cell(string text, int r, int c)
        {
            var t = new TextBlock { Text = text, FontFamily = PeriphUi.Mono, FontSize = 13.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            Grid.SetRow(t, r); Grid.SetColumn(t, c);
            table.Children.Add(t);
            return t;
        }
        // roda
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var wl = new Border { BorderThickness = new Thickness(0, 1, 0, 0) };
        wl.SetResourceReference(Border.BorderBrushProperty, "Line");
        Grid.SetRow(wl, row); Grid.SetColumnSpan(wl, 4);
        table.Children.Add(wl);
        var wn = Ui.T("Roda", "Body");
        wn.Margin = new Thickness(0, 9, 0, 9);
        Grid.SetRow(wn, row);
        table.Children.Add(wn);
        var wheelRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var w in new[] { _wheelUp, _wheelDown, _wheelSide })
        {
            w.FontFamily = PeriphUi.Mono; w.FontSize = 13.5; w.Margin = new Thickness(14, 0, 0, 0);
            w.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            wheelRow.Children.Add(w);
        }
        Grid.SetRow(wheelRow, row); Grid.SetColumn(wheelRow, 1); Grid.SetColumnSpan(wheelRow, 3);
        table.Children.Add(wheelRow);

        var rightCol = new StackPanel();
        rightCol.Children.Add(table);
        _btnVerdict = PeriphUi.Verdict("");
        _btnVerdict.Box.Margin = new Thickness(0, 16, 0, 0);
        rightCol.Children.Add(_btnVerdict.Box);
        Grid.SetColumn(rightCol, 1);
        body.Children.Add(rightCol);
        bt.Children.Add(body);
        Children.Add(PeriphUi.Card(bt));

        UpdateWheel();
        UpdateButtonVerdict();
    }

    // =========================================================== IPeriphView

    public void Activate() { }

    public void Deactivate()
    {
        if (_measuring) Toggle();
        ReleaseAll();
    }

    public void WindowDeactivated() => ReleaseAll();

    /// <summary>Botões laterais e roda horizontal chegam como mensagens próprias: tratamo-los antes do WPF
    /// (assim o botão "Retroceder" do rato não muda de página enquanto o testas).</summary>
    public void Preprocess(ref MSG msg, ref bool handled)
    {
        const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C, WM_XBUTTONDBLCLK = 0x020D, WM_MOUSEHWHEEL = 0x020E;
        if (msg.message is not (WM_XBUTTONDOWN or WM_XBUTTONUP or WM_XBUTTONDBLCLK or WM_MOUSEHWHEEL)) return;
        if (!_pad.IsMouseOver && !_pad.IsMouseCaptured) return;
        int hi = (short)((msg.wParam.ToInt64() >> 16) & 0xFFFF);
        if (msg.message == WM_MOUSEHWHEEL)
        {
            if (hi > 0) _wRight++; else if (hi < 0) _wLeft++;
            UpdateWheel();
            handled = true;
            return;
        }
        var b = hi == 1 ? MouseButton.XButton1 : MouseButton.XButton2;
        if (msg.message == WM_XBUTTONUP) Up(b); else Down(b);
        handled = true;
    }

    // =========================================================== polling

    private void Toggle()
    {
        if (_measuring)
        {
            _measuring = false;
            _timer.Stop();
            _raw.Stop();
            PeriphUi.SetBtn(_startBtn, "", "Medir outra vez");
            StopPulse();
            _ringSub.Text = "Hz (último)";
            if (_sumCount > 0) _ringValue.Text = Math.Round(Avg()).ToString("0");
            return;
        }
        // nova medição
        _history.Clear(); _deviceCounts.Clear();
        _sumTime = 0; _sumCount = 0; _totalReports = 0; _maxHz = 0; _lastHz = 0; _prevStamp = 0; _idleTicks = 0; _mainDevice = IntPtr.Zero;
        _raw.Start();
        if (_raw.Failed)
        {
            PeriphUi.SetVerdict(_pollVerdict, "Bad", "Não foi possível ler o rato diretamente (Raw Input). Fecha outros programas que capturem o rato e tenta outra vez.");
            return;
        }
        _measuring = true;
        PeriphUi.SetBtn(_startBtn, "", "Parar");
        _ringSub.Text = "Hz agora";
        _ringValue.Text = "0";
        _sAvg.Value.Text = "—"; _sMax.Value.Text = "—"; _sReports.Value.Text = "0";
        SetNominal(null);
        PeriphUi.SetVerdict(_pollVerdict, "Info", "A medir… mexe o rato depressa em círculos (no tapete, não precisa de ser dentro da janela).");
        StartPulse();
        _timer.Start();
    }

    private double Avg() => _sumTime > 0 ? _sumCount / _sumTime : 0;

    private void Tick()
    {
        _batch.Clear();
        _raw.Drain(_batch);
        foreach (var (_, dev) in _batch)
        {
            _deviceCounts[dev] = _deviceCounts.GetValueOrDefault(dev) + 1;
            if (_mainDevice == IntPtr.Zero || _deviceCounts[dev] > _deviceCounts.GetValueOrDefault(_mainDevice) + 200) _mainDevice = dev;
        }
        double f = Stopwatch.Frequency;
        double active = 0; long count = 0;
        foreach (var (t, dev) in _batch)
        {
            if (dev != _mainDevice) continue;
            _totalReports++;
            if (_prevStamp != 0)
            {
                double dt = (t - _prevStamp) / f;
                if (dt >= 0 && dt < 0.025) { active += dt; count++; }
            }
            _prevStamp = t;
        }
        double hz = active >= 0.035 && count >= 4 ? count / active : 0;
        if (hz > 0)
        {
            _idleTicks = 0;
            _sumTime += active; _sumCount += count;
            if (active >= 0.07 && hz > _maxHz) _maxHz = hz;
            _lastHz = _lastHz <= 0 ? hz : _lastHz * 0.55 + hz * 0.45; // suaviza o número grande
        }
        else
        {
            _idleTicks++;
            _lastHz = 0;
        }
        _history.Add(hz);
        if (_history.Count > HistoryLen) _history.RemoveAt(0);

        _ringValue.Text = _lastHz > 0 ? Math.Round(_lastHz).ToString("0") : "0";
        _ringSub.Text = _lastHz > 0 ? "Hz agora" : "parado";
        double avg = Avg();
        var nominal = Snap(avg > 0 ? Math.Max(avg, _maxHz * 0.92) : 0);
        DrawArc(_lastHz / Scale(nominal));
        if (avg > 0)
        {
            _sAvg.Value.Text = $"{avg:0}";
            _sAvg.Hint.Text = "Hz enquanto mexes";
            _sMax.Value.Text = _maxHz > 0 ? $"{_maxHz:0}" : "—";
            _sMax.Hint.Text = "Hz no melhor momento";
            SetNominal(nominal);
        }
        _sReports.Value.Text = _totalReports.ToString("N0");
        DrawChart();
        if (_sumTime >= 0.6) ShowPollVerdict(avg, nominal);
        else if (_idleTicks > 25) PeriphUi.SetVerdict(_pollVerdict, "Info", "Não chegou nenhum movimento. Mexe o rato depressa em círculos.");
        if (_idleTicks > 300) Toggle(); // 30 s parado: pára sozinho
    }

    private static int? Snap(double hz)
    {
        if (hz < 60) return null;
        int best = Nominal[0];
        foreach (var n in Nominal) if (Math.Abs(Math.Log(hz / n)) < Math.Abs(Math.Log(hz / best))) best = n;
        return best;
    }

    private static double Scale(int? nominal) => nominal is int n ? n * 1.15 : 1150;

    private void ShowPollVerdict(double avg, int? nominal)
    {
        if (nominal is not int n) return;
        if (n <= 125)
            PeriphUi.SetVerdict(_pollVerdict, "Bad", $"O rato está a ~{n} Hz: é lento para jogar. Se o programa do fabricante deixar, põe 1000 Hz.");
        else if (n <= 500)
            PeriphUi.SetVerdict(_pollVerdict, "Warn", $"O rato está a ~{n} Hz. Funciona, mas a 1000 Hz o movimento fica mais suave e com menos atraso.");
        else if (n <= 1000)
            PeriphUi.SetVerdict(_pollVerdict, "Good", $"O rato está a ~{n} Hz: ótimo para jogar.");
        else
            PeriphUi.SetVerdict(_pollVerdict, "Good", $"O rato está a ~{n} Hz: muito rápido. Se notares quebras de FPS em jogos pesados para a CPU, experimenta 1000–2000 Hz.");
    }

    private void SetNominal(int? n)
    {
        if (n is int v)
        {
            _nominalText.Text = $"≈ {v} Hz";
            _nominalText.SetResourceReference(TextBlock.ForegroundProperty, "Accent2");
            _nominalPill.Background = new SolidColorBrush(PeriphUi.WithAlpha("Accent2", 0x1E));
        }
        else
        {
            _nominalText.Text = "SEM MEDIÇÃO";
            _nominalText.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            _nominalPill.SetResourceReference(Border.BackgroundProperty, "Hover");
        }
    }

    private void DrawArc(double frac)
    {
        frac = Math.Clamp(frac, 0, 0.9999);
        const double size = 170, th = 12;
        double r = (size - th) / 2, cx = size / 2, cy = size / 2;
        if (frac <= 0.001) { _ringArc.Data = null; return; }
        double a = frac * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true));
        _ringArc.Data = new PathGeometry(new[] { fig });
    }

    private void DrawChart()
    {
        _chart.Children.Clear();
        double w = _chart.ActualWidth, h = _chart.ActualHeight;
        if (w < 10 || h < 10) return;
        double top = Math.Max(1150, _history.Count > 0 ? _history.Max() * 1.15 : 0);
        double lastY = double.NegativeInfinity;
        foreach (var level in Enumerable.Reverse(Nominal))
        {
            if (level > top) continue;
            if (level < top * 0.08) break;
            double y = h - level / top * (h - 10);
            if (y - lastY < 18) continue; // linhas demasiado juntas: fica só a mais alta
            lastY = y;
            var ln = new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 4 } };
            ln.SetResourceReference(Shape.StrokeProperty, "Line");
            _chart.Children.Add(ln);
            var lab = new TextBlock { Text = $"{level} Hz", FontSize = 10, Margin = new Thickness(8, 0, 0, 0) };
            lab.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            Canvas.SetLeft(lab, 4); Canvas.SetTop(lab, y - 15);
            _chart.Children.Add(lab);
        }
        if (_history.Count < 2)
        {
            var empty = new TextBlock { Text = _measuring ? "À espera de movimento…" : "O gráfico aparece aqui enquanto medes.", FontSize = 12 };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            empty.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(empty, (w - empty.DesiredSize.Width) / 2); Canvas.SetTop(empty, (h - empty.DesiredSize.Height) / 2);
            _chart.Children.Add(empty);
            return;
        }
        double step = w / (HistoryLen - 1);
        double x0 = w - (_history.Count - 1) * step;
        var line = new Polyline { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        line.SetResourceReference(Shape.StrokeProperty, "Accent2");
        var area = new Polygon();
        area.Fill = new LinearGradientBrush(PeriphUi.WithAlpha("Accent", 0x55), PeriphUi.WithAlpha("Accent", 0x00), 90);
        area.Points.Add(new Point(x0, h));
        for (int i = 0; i < _history.Count; i++)
        {
            var p = new Point(x0 + i * step, h - Math.Min(_history[i], top) / top * (h - 10));
            line.Points.Add(p);
            area.Points.Add(p);
        }
        area.Points.Add(new Point(w, h));
        _chart.Children.Add(area);
        _chart.Children.Add(line);
    }

    private void StartPulse()
    {
        var st = (ScaleTransform)_pulse.RenderTransform;
        var dur = TimeSpan.FromMilliseconds(1400);
        var grow = new DoubleAnimation(1, 1.18, dur) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0, dur) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopPulse()
    {
        var st = (ScaleTransform)_pulse.RenderTransform;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _pulse.BeginAnimation(OpacityProperty, null);
        _pulse.Opacity = 0;
    }

    // =========================================================== botões

    /// <summary>Desenho do rato (Canvas 200×240). Cria também os estados dos botões.</summary>
    private Canvas BuildMouse(out TextBlock arrowUp, out TextBlock arrowDown)
    {
        var c = new Canvas { Width = 210, Height = 236 };
        var body = new Border { Width = 150, Height = 230, CornerRadius = new CornerRadius(75, 75, 66, 66), BorderThickness = new Thickness(1.5) };
        body.SetResourceReference(Border.BackgroundProperty, "Card");
        body.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        Canvas.SetLeft(body, 30);
        c.Children.Add(body);

        Border Part(double x, double y, double w, double h, CornerRadius cr)
        {
            var b = new Border { Width = w, Height = h, CornerRadius = cr, BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BackgroundProperty, "Hover");
            b.SetResourceReference(Border.BorderBrushProperty, "Line");
            Canvas.SetLeft(b, x); Canvas.SetTop(b, y);
            c.Children.Add(b);
            return b;
        }
        void Add(MouseButton mb, string name, Border shape) => _btns[mb] = new BtnState { Name = name, Visual = shape, Shape = shape };

        Add(MouseButton.Left, "Esquerdo", Part(34, 4, 69, 100, new CornerRadius(71, 4, 4, 10)));
        Add(MouseButton.Right, "Direito", Part(107, 4, 69, 100, new CornerRadius(4, 71, 10, 4)));
        var wheel = Part(95, 26, 20, 46, new CornerRadius(10));
        wheel.BorderThickness = new Thickness(1.5);
        Add(MouseButton.Middle, "Meio (roda)", wheel);
        // botões laterais (lado esquerdo do rato)
        Add(MouseButton.XButton2, "Lateral (avançar)", Part(16, 112, 14, 34, new CornerRadius(7, 2, 2, 7)));
        Add(MouseButton.XButton1, "Lateral (retroceder)", Part(16, 152, 14, 34, new CornerRadius(7, 2, 2, 7)));
        // riscas da roda
        for (int i = 0; i < 4; i++)
        {
            var r = new Rectangle { Width = 10, Height = 2, RadiusX = 1, RadiusY = 1, IsHitTestVisible = false };
            r.SetResourceReference(Shape.FillProperty, "LineStrong");
            Canvas.SetLeft(r, 100); Canvas.SetTop(r, 37 + i * 8);
            c.Children.Add(r);
        }
        // setas da roda (aparecem ao rodar)
        arrowUp = PeriphUi.Icon("", 16, "Accent2");
        arrowDown = PeriphUi.Icon("", 16, "Accent2");
        arrowUp.Opacity = 0; arrowDown.Opacity = 0;
        Canvas.SetLeft(arrowUp, 184); Canvas.SetTop(arrowUp, 22);
        Canvas.SetLeft(arrowDown, 184); Canvas.SetTop(arrowDown, 58);
        c.Children.Add(arrowUp);
        c.Children.Add(arrowDown);
        // logótipo discreto
        var logo = new TextBlock { Text = "HC", FontSize = 13, FontWeight = FontWeights.Black, IsHitTestVisible = false };
        logo.SetResourceReference(TextBlock.ForegroundProperty, "LineStrong");
        Canvas.SetLeft(logo, 95); Canvas.SetTop(logo, 172);
        c.Children.Add(logo);
        return c;
    }

    private void Pad_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2) return; // já tratados no Preprocess
        _pad.CaptureMouse();
        Down(e.ChangedButton);
    }

    private void Pad_Up(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2) return;
        Up(e.ChangedButton);
        if (_btns.Values.All(b => !b.Down) && _pad.IsMouseCaptured) _pad.ReleaseMouseCapture();
    }

    private void Pad_Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (e.Delta > 0) { _wUp++; Flash(_arrowUp); } else if (e.Delta < 0) { _wDown++; Flash(_arrowDown); }
        if (_btns.TryGetValue(MouseButton.Middle, out var m) && m.Shape != null && !m.Down)
        {
            var b = new SolidColorBrush(PeriphUi.WithAlpha("Accent2", 0xAA));
            m.Shape.Background = b;
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(PeriphUi.WithAlpha(m.Tested ? "AccentSoft" : "Hover", 0xFF), TimeSpan.FromMilliseconds(350)));
        }
        UpdateWheel();
    }

    private static void Flash(UIElement el) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(450)));

    private void Down(MouseButton mb)
    {
        if (!_btns.TryGetValue(mb, out var b)) return;
        long now = Stopwatch.GetTimestamp();
        bool suspect = false;
        if (b.LastDown != 0)
        {
            double gap = (now - b.LastDown) * 1000.0 / Stopwatch.Frequency;
            if (double.IsNaN(b.MinGapMs) || gap < b.MinGapMs) b.MinGapMs = gap;
            if (gap < ChatterMs) { b.Suspect++; suspect = true; }
        }
        b.LastDown = now;
        b.Clicks++;
        b.Down = true;
        b.Tested = true;
        Paint(b, suspect ? "bad" : "down");
        b.CClicks.Text = b.Clicks.ToString();
        b.CSuspect.Text = b.Suspect.ToString();
        b.CSuspect.SetResourceReference(TextBlock.ForegroundProperty, b.Suspect > 0 ? "Bad" : "Fg");
        b.CGap.Text = double.IsNaN(b.MinGapMs) ? "—" : $"{b.MinGapMs:0} ms";
        b.CGap.SetResourceReference(TextBlock.ForegroundProperty, !double.IsNaN(b.MinGapMs) && b.MinGapMs < ChatterMs ? "Bad" : "Fg");
        UpdateButtonVerdict();
    }

    private void Up(MouseButton mb)
    {
        if (!_btns.TryGetValue(mb, out var b) || !b.Down) return;
        b.Down = false;
        Paint(b, "tested");
    }

    private void ReleaseAll()
    {
        foreach (var b in _btns.Values) if (b.Down) { b.Down = false; Paint(b, "tested"); }
    }

    private void Paint(BtnState b, string state)
    {
        if (b.Shape is not Border s) return;
        s.Effect = null;
        switch (state)
        {
            case "down":
                s.SetResourceReference(Border.BackgroundProperty, "AccentGradient");
                s.SetResourceReference(Border.BorderBrushProperty, "Accent2");
                s.Effect = new DropShadowEffect { Color = Color.FromRgb(0x2E, 0x9B, 0xFF), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.7 };
                break;
            case "bad":
                s.SetResourceReference(Border.BackgroundProperty, "Bad");
                s.SetResourceReference(Border.BorderBrushProperty, "Bad");
                s.Effect = new DropShadowEffect { Color = PeriphUi.WithAlpha("Bad", 0xFF), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.7 };
                break;
            case "tested":
                s.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                s.SetResourceReference(Border.BorderBrushProperty, "Accent");
                break;
            default:
                s.SetResourceReference(Border.BackgroundProperty, "Hover");
                s.SetResourceReference(Border.BorderBrushProperty, "Line");
                break;
        }
    }

    private void UpdateWheel()
    {
        _wheelUp.Text = $"↑ {_wUp}";
        _wheelDown.Text = $"↓ {_wDown}";
        _wheelSide.Text = _wLeft + _wRight > 0 ? $"↔ {_wLeft + _wRight}" : "";
    }

    private void ResetButtons()
    {
        foreach (var b in _btns.Values)
        {
            b.Clicks = 0; b.Suspect = 0; b.LastDown = 0; b.MinGapMs = double.NaN; b.Down = false; b.Tested = false;
            b.CClicks.Text = "0"; b.CSuspect.Text = "0"; b.CGap.Text = "—";
            b.CSuspect.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            b.CGap.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            Paint(b, "idle");
        }
        _wUp = _wDown = _wLeft = _wRight = 0;
        UpdateWheel();
        UpdateButtonVerdict();
    }

    private void UpdateButtonVerdict()
    {
        var bad = _btns.Values.Where(b => b.Suspect > 0).ToList();
        int tested = _btns.Values.Count(b => b.Tested);
        if (bad.Count > 0)
        {
            var b = bad.OrderByDescending(x => x.Suspect).First();
            string vezes = b.Suspect == 1 ? "1 duplo clique" : $"{b.Suspect} duplos cliques";
            PeriphUi.SetVerdict(_btnVerdict, "Bad", $"Botão {b.Name.ToLowerInvariant()}: {vezes} em menos de {ChatterMs:0} ms. Se clicaste só uma vez, o interruptor pode estar gasto (duplo clique avariado).");
        }
        else if (tested == 0)
            PeriphUi.SetVerdict(_btnVerdict, "Info", "Clica 20 a 30 vezes com o botão esquerdo, ao teu ritmo normal. Se aparecerem cliques suspeitos sem fazeres duplo clique, o botão está a falhar.");
        else
        {
            var missing = _btns.Values.Where(b => !b.Tested).Select(b => b.Name.ToLowerInvariant()).ToList();
            PeriphUi.SetVerdict(_btnVerdict, "Good", missing.Count == 0
                ? "Todos os botões responderam e nenhum fez duplo clique sozinho."
                : $"Sem duplos cliques suspeitos. Ainda falta testar: {string.Join(", ", missing)}.");
        }
    }
}
