using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using FrameForge.Core;
using FrameForge.Peripherals;

namespace FrameForge.Pages;

/// <summary>Aba "Ecrã": refresh real medido por monitor, píxeis mortos e rasto/movimento em ecrã inteiro.</summary>
internal sealed class PeriphScreenView : StackPanel, IPeriphView
{
    private readonly StackPanel _monitorRows = new();
    private readonly Button _measureBtn;
    private readonly ComboBox _target = new() { MinWidth = 260 };
    private readonly StackPanel _targetRow;
    private List<MonitorInfo> _monitors = new();
    private CancellationTokenSource? _cts;
    private bool _measuredOnce;
    private Window? _testWindow;
    private Window? _fixerWindow;

    public PeriphScreenView()
    {
        // ------------------------------------------------ refresh real
        _measureBtn = PeriphUi.Btn("", "Medir outra vez", (_, _) => Measure());
        var rate = new StackPanel();
        rate.Children.Add(PeriphUi.CardHead("", "Frequência real",
            "Quantas imagens por segundo o ecrã mostra mesmo (medido na gráfica), comparado com o que está configurado.", _measureBtn));
        rate.Children.Add(_monitorRows);
        Children.Add(PeriphUi.Card(rate));

        // ------------------------------------------------ escolha do ecrã para os testes de ecrã inteiro
        var label = Ui.T("Testar no ecrã", "Body");
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 12, 0);
        _targetRow = Ui.Row(label, _target);
        _targetRow.Margin = new Thickness(2, 4, 0, 14);
        Children.Add(_targetRow);

        var tests = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -14, 0) };
        tests.Children.Add(TestCard("", "Píxeis mortos",
            "O ecrã fica todo de uma cor de cada vez. Procura pontos que não mudam.",
            new[] { "Ponto preto em fundo claro: píxel morto.", "Ponto de cor em fundo preto: píxel preso.", "Luz nos cantos em preto: fuga de luz (normal em IPS, até certo ponto)." },
            "Abrir em ecrã inteiro", () => OpenTest(m => new DeadPixelWindow(m)), PixelPreview()));
        tests.Children.Add(TestCard("", "Rasto e movimento",
            "Um objeto anda sempre à mesma velocidade. Segue-o com os olhos.",
            new[] { "Rasto escuro atrás: ghosting (resposta lenta).", "Contorno claro: overdrive demasiado alto.", "Mais Hz = imagem mais nítida em movimento." },
            "Abrir em ecrã inteiro", () => OpenTest(m => new MotionTestWindow(m)), MotionPreview()));
        Children.Add(tests);

        // ------------------------------------------------ recuperador de píxeis presos (estilo JScreenFix)
        Children.Add(BuildPixelFixerCard());

        var tip = Ui.T("Dentro dos testes em ecrã inteiro: clica ou usa as setas para mudar · Esc sai.", "MutedText", 12);
        tip.Margin = new Thickness(2, 0, 0, 0);
        Children.Add(tip);

        _ = LoadMonitorsAsync();
    }

    // =========================================================== IPeriphView

    public void Activate()
    {
        if (!_measuredOnce) Measure();
    }

    public void Deactivate()
    {
        _cts?.Cancel();
    }

    // =========================================================== monitores

    /// <summary>Lê os ecrãs fora do UI thread (percorrer os modos de cada ecrã pode demorar centenas de ms).</summary>
    private async Task LoadMonitorsAsync()
    {
        var list = await Task.Run(() =>
        {
            try { return DisplayProbe.Monitors(); } catch { return new List<MonitorInfo>(); }
        });
        _monitors = list;
        _target.Items.Clear();
        int i = 1;
        foreach (var m in _monitors)
            _target.Items.Add(new ComboBoxItem { Content = $"{i++}. {m.Name} · {m.Short}{(m.Primary ? " · principal" : "")}", Tag = m });
        if (_target.Items.Count > 0) _target.SelectedIndex = 0;
        _targetRow.Visibility = _monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RenderRows(null);
    }

    private async void Measure()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _measuredOnce = true;
        _measureBtn.IsEnabled = false;
        await LoadMonitorsAsync();
        if (ct.IsCancellationRequested) { _measuredOnce = false; _measureBtn.IsEnabled = true; return; }
        var monitors = _monitors;
        if (monitors.Count == 0) { _measureBtn.IsEnabled = true; return; }
        var results = new RefreshResult?[monitors.Count];
        RenderRows(results, measuring: true);
        _ = Task.Run(() =>
        {
            // um ecrã de cada vez (medir dois ao mesmo tempo não muda nada, mas assim cada um tem a thread só para si)
            for (int i = 0; i < monitors.Count && !ct.IsCancellationRequested; i++)
            {
                // com o ecrã desligado/em poupança o vblank pode nunca chegar: não esperamos mais de 4 s por ecrã
                results[i] = MeasureWithTimeout(monitors[i].Device, ct);
                Dispatcher.BeginInvoke(() => { if (!ct.IsCancellationRequested) RenderRows(results, measuring: true); });
            }
        }, ct).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            _measureBtn.IsEnabled = true;
            if (ct.IsCancellationRequested) { _measuredOnce = results.Any(r => r != null); RenderRows(results); return; }
            RenderRows(results);
        }));
    }

    // Medições que ficaram presas à espera do vblank (ecrã ligado mas sem sinal): no máximo uma por ecrã, numa thread
    // própria (não do pool). Enquanto essa estiver presa, o mesmo ecrã não é medido outra vez.
    private static readonly Dictionary<string, Thread> Hung = new(StringComparer.OrdinalIgnoreCase);

    private static RefreshResult MeasureWithTimeout(string device, CancellationToken ct)
    {
        var noAnswer = new RefreshResult(0, 0, 0, "sem resposta");
        lock (Hung) if (Hung.TryGetValue(device, out var old) && old.IsAlive) return noAnswer;
        RefreshResult? result = null;
        var t = new Thread(() =>
        {
            try { result = DisplayProbe.Measure(device, TimeSpan.FromSeconds(1.6), ct); }
            catch { result = new RefreshResult(0, 0, 0, "indisponível"); }
        }) { IsBackground = true, Name = "HyperCore vblank" };
        t.Start();
        if (t.Join(TimeSpan.FromSeconds(4))) return result ?? new RefreshResult(0, 0, 0, "indisponível");
        lock (Hung) Hung[device] = t;
        return noAnswer;
    }

    private void RenderRows(RefreshResult?[]? results, bool measuring = false)
    {
        _monitorRows.Children.Clear();
        if (_monitors.Count == 0)
        {
            _monitorRows.Children.Add(Ui.T("Não foi possível ler os ecrãs.", "MutedText"));
            return;
        }
        for (int i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            var r = results != null && i < results.Length ? results[i] : null;
            _monitorRows.Children.Add(MonitorRow(i + 1, m, r, measuring && r == null, i > 0));
        }
    }

    private UIElement MonitorRow(int n, MonitorInfo m, RefreshResult? r, bool busy, bool separator)
    {
        var outer = new StackPanel();
        if (separator)
        {
            var sep = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 14) };
            sep.SetResourceReference(Border.BackgroundProperty, "Line");
            outer.Children.Add(sep);
        }
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

        var badge = new Grid { Width = 46, Height = 46, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        var bg = new Border { CornerRadius = new CornerRadius(12) };
        bg.SetResourceReference(Border.BackgroundProperty, "Hover");
        badge.Children.Add(bg);
        badge.Children.Add(PeriphUi.Icon("", 22, "Accent2"));
        var num = new TextBlock { Text = n.ToString(), FontSize = 9.5, FontWeight = FontWeights.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) };
        num.SetResourceReference(TextBlock.ForegroundProperty, "Accent2");
        badge.Children.Add(num);
        g.Children.Add(badge);

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Ui.T(m.Name, "H2", 15));
        ((TextBlock)name.Children[0]).Margin = new Thickness(0);
        name.Children.Add(Ui.T($"{m.Short}{(m.Primary ? "  ·  principal" : "")}", "MutedText", 12.5));
        Grid.SetColumn(name, 1);
        g.Children.Add(name);

        var conf = Column("CONFIGURADO", $"{m.ConfiguredHz:0.#} Hz", m.MaxHz > Math.Round(m.ConfiguredHz) + 1 ? $"máximo {m.MaxHz} Hz" : "o máximo deste ecrã");
        Grid.SetColumn(conf, 2);
        g.Children.Add(conf);

        FrameworkElement measured;
        string kind = "Info", state = "";
        if (busy)
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(Ui.T("MEDIDO", "Label", 10));
            var bar = new ProgressBar { IsIndeterminate = true, Height = 4, Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 8), BorderThickness = new Thickness(0) };
            bar.SetResourceReference(Control.ForegroundProperty, "Accent2");
            bar.SetResourceReference(Control.BackgroundProperty, "Hover");
            sp.Children.Add(bar);
            sp.Children.Add(Ui.T("a medir…", "MutedText", 11.5));
            measured = sp;
        }
        else if (r == null || r.Hz <= 0)
        {
            measured = Column("MEDIDO", "—", r == null ? "por medir" : "não deu para medir");
        }
        else
        {
            double diff = r.Hz / Math.Max(1, m.ConfiguredHz);
            bool irregular = r.Missed > 8 || r.JitterMs > 0.6;
            if (diff < 0.9) { kind = "Warn"; state = "ABAIXO"; }
            else if (irregular) { kind = "Warn"; state = "IRREGULAR"; }
            else { kind = "Good"; state = "CERTO"; }
            measured = Column("MEDIDO", $"{r.Hz:0.0} Hz", $"variação {r.JitterMs:0.00} ms", kind);
            measured.ToolTip = $"Medido pelo {r.Method} durante 1,6 s. Imagens atrasadas: {r.Missed}.";
        }
        Grid.SetColumn(measured, 3);
        g.Children.Add(measured);

        if (state.Length > 0)
        {
            var pill = PeriphUi.StatePill(state, kind);
            pill.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(pill, 4);
            g.Children.Add(pill);
        }
        outer.Children.Add(g);

        // conselho curto
        string? advice = null;
        string advKind = "Info";
        if (m.MaxHz > Math.Round(m.ConfiguredHz) + 1)
        {
            advice = $"Este ecrã aguenta {m.MaxHz} Hz mas está a {m.ConfiguredHz:0} Hz. Nos jogos estás a perder fluidez.";
            advKind = "Warn";
        }
        else if (r != null && r.Hz > 0 && r.Hz / Math.Max(1, m.ConfiguredHz) < 0.9)
        {
            advice = "O ecrã mostra menos imagens do que o configurado. Verifica o cabo (DisplayPort ou HDMI 2.0+) e se a poupança de energia está a limitar o refresh.";
            advKind = "Warn";
        }
        if (advice != null)
        {
            var v = PeriphUi.Verdict(advice);
            PeriphUi.SetVerdict(v, advKind, advice);
            v.Box.Margin = new Thickness(60, 12, 0, 0);
            if (m.MaxHz > Math.Round(m.ConfiguredHz) + 1)
            {
                var go = new Button { Content = "Corrigir em Configuração gamer  ›", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6) };
                go.SetResourceReference(FrameworkElement.StyleProperty, "Btn");
                go.Click += (_, _) => AppServices.Navigate("FF:gamersetup");
                if (v.Box.Child is DockPanel dp && dp.Children[1] is TextBlock tb)
                {
                    dp.Children.Remove(tb);
                    var stack = new StackPanel();
                    stack.Children.Add(tb);
                    stack.Children.Add(go);
                    dp.Children.Add(stack);
                }
            }
            outer.Children.Add(v.Box);
        }
        return outer;
    }

    private static StackPanel Column(string label, string value, string hint, string? color = null)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(Ui.T(label, "Label", 10));
        var v = new TextBlock { Text = value, FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = PeriphUi.Mono, Margin = new Thickness(0, 3, 0, 1) };
        v.SetResourceReference(TextBlock.ForegroundProperty, color is "Good" or "Warn" or "Bad" ? color : "Fg");
        sp.Children.Add(v);
        sp.Children.Add(Ui.T(hint, "MutedText", 11.5));
        return sp;
    }

    // =========================================================== testes de ecrã inteiro

    private Border TestCard(string glyph, string title, string text, string[] bullets, string action, Action open, FrameworkElement preview)
    {
        var dock = new DockPanel { LastChildFill = true };
        var sp = new StackPanel();
        preview.Margin = new Thickness(0, 0, 0, 16);
        sp.Children.Add(preview);
        sp.Children.Add(PeriphUi.CardHead(glyph, title, text));
        foreach (var b in bullets)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 3) };
            var dot = new Ellipse { Width = 5, Height = 5, Margin = new Thickness(2, 8, 11, 0), VerticalAlignment = VerticalAlignment.Top };
            dot.SetResourceReference(Shape.FillProperty, "Accent2");
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(Ui.T(b, "Body", 13));
            sp.Children.Add(row);
        }
        var btn = PeriphUi.Btn("", action, (_, _) => open());
        btn.Margin = new Thickness(0, 14, 0, 0);
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        DockPanel.SetDock(btn, Dock.Bottom); // botões alinhados em baixo nos dois cartões
        dock.Children.Add(btn);
        dock.Children.Add(sp);
        var card = PeriphUi.Card(dock);
        card.Margin = new Thickness(0, 0, 14, 14);
        return card;
    }

    /// <summary>Miniatura com as cores do teste de píxeis.</summary>
    private static FrameworkElement PixelPreview()
    {
        var g = new UniformGrid { Columns = 7, Height = 64 };
        var colors = new[] { "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#808080" };
        foreach (var c in colors) g.Children.Add(new Rectangle { Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)) });
        g.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.Black, Colors.White, 0) });
        return Rounded(g);
    }

    /// <summary>Miniatura animada do teste de rasto (três faixas com um objeto a andar).</summary>
    private static FrameworkElement MotionPreview()
    {
        var g = new Grid { Height = 64, ClipToBounds = true };
        var shades = new[] { Color.FromRgb(0, 0, 0), Color.FromRgb(0x3C, 0x3C, 0x3C), Color.FromRgb(0xA0, 0xA0, 0xA0) };
        for (int i = 0; i < 3; i++)
        {
            g.RowDefinitions.Add(new RowDefinition());
            var laneBg = new SolidColorBrush(shades[i]); laneBg.Freeze();
            var lane = new Grid { Background = laneBg };
            Grid.SetRow(lane, i);
            var objBg = i == 2 ? new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x18)) : Brushes.White;
            if (objBg is SolidColorBrush sb && !sb.IsFrozen) sb.Freeze();
            var dotBg = new SolidColorBrush(Color.FromRgb(0x2E, 0x9B, 0xFF)); dotBg.Freeze();
            var obj = new Border
            {
                Width = 34, Height = 13, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left,
                Background = objBg,
                CacheMode = new BitmapCache(),
                Child = new Ellipse { Width = 7, Height = 7, Fill = dotBg, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(5, 0, 0, 0) }
            };
            var tt = new TranslateTransform(-40, 0);
            obj.RenderTransform = tt;
            lane.Children.Add(obj);
            g.Children.Add(lane);
            obj.Loaded += (_, _) =>
            {
                double w = Math.Max(200, g.ActualWidth);
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-40, w + 10, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });
            };
            obj.Unloaded += (_, _) => tt.BeginAnimation(TranslateTransform.XProperty, null);
        }
        // posição de repouso para a imagem estática (antes da animação)
        foreach (Grid lane in g.Children) ((TranslateTransform)((Border)lane.Children[0]).RenderTransform).X = 90;
        return Rounded(g);
    }

    private Border BuildPixelFixerCard()
    {
        var sp = new StackPanel();

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var preview = PixelNoisePreview();
        preview.Margin = new Thickness(0, 0, 18, 0);
        preview.VerticalAlignment = VerticalAlignment.Top;
        body.Children.Add(preview);

        var info = new StackPanel();
        info.Children.Add(PeriphUi.CardHead("", "Recuperar píxeis presos (estilo JScreenFix)",
            "Abre uma caixinha flutuante que podes arrastar para cima do píxel afetado. Os píxeis dentro da caixa mudam de cor milhões de vezes por segundo para tentar desbloquear o subpíxel."));

        foreach (var b in new[]
        {
            "Arrasta a caixa para cima do ponto preso e deixa a correr cerca de 10 minutos.",
            "Podes mudar o tamanho da caixa (P / M / G) e alternar entre ruído RGB por píxel e flash sólido.",
            "Atenção: contém luzes a piscar rapidamente — evita olhar diretamente para a caixa se fores sensível."
        })
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 3) };
            var dot = new Ellipse { Width = 5, Height = 5, Margin = new Thickness(2, 8, 11, 0), VerticalAlignment = VerticalAlignment.Top };
            dot.SetResourceReference(Shape.FillProperty, "Accent2");
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(Ui.T(b, "Body", 13));
            info.Children.Add(row);
        }

        var openBtn = PeriphUi.Btn("", "Ativar caixa de recuperação", (_, _) => OpenPixelFixer(), "BtnAccent");
        openBtn.Margin = new Thickness(0, 14, 0, 12);
        openBtn.HorizontalAlignment = HorizontalAlignment.Left;
        info.Children.Add(openBtn);

        var warn = PeriphUi.Verdict("");
        PeriphUi.SetVerdict(warn, "Warn",
            "Aviso: nem sempre os píxeis mortos irão voltar à vida com este processo. Esta técnica funciona sobretudo em píxeis presos (com cor fixa); píxeis totalmente mortos (pretos) por avaria física normalmente não recuperam.");
        info.Children.Add(warn.Box);

        Grid.SetColumn(info, 1);
        body.Children.Add(info);
        sp.Children.Add(body);
        return PeriphUi.Card(sp);
    }

    /// <summary>Miniatura estática de ruído RGB colorido (representação da caixa de recuperação).</summary>
    private static FrameworkElement PixelNoisePreview()
    {
        const int cols = 8, rows = 8;
        var g = new UniformGrid { Columns = cols, Rows = rows, Width = 96, Height = 96 };
        Color[] pal =
        {
            Color.FromRgb(255, 0, 0), Color.FromRgb(0, 255, 0), Color.FromRgb(0, 0, 255),
            Color.FromRgb(255, 255, 0), Color.FromRgb(255, 0, 255), Color.FromRgb(0, 255, 255),
            Colors.White, Colors.Black
        };
        var rng = new Random(42);
        for (int i = 0; i < cols * rows; i++)
            g.Children.Add(new Rectangle { Fill = new SolidColorBrush(pal[rng.Next(pal.Length)]) });
        return Rounded(g);
    }

    private async void OpenPixelFixer()
    {
        if (_monitors.Count == 0) await LoadMonitorsAsync();
        var m = (_target.SelectedItem as ComboBoxItem)?.Tag as MonitorInfo ?? _monitors.FirstOrDefault();
        if (m == null) return;
        try { _fixerWindow?.Close(); } catch { }
        var w = new PixelFixerWindow(m);
        w.Closed += (_, _) => { if (ReferenceEquals(_fixerWindow, w)) _fixerWindow = null; };
        _fixerWindow = w;
        w.Show();
    }

    private static Border Rounded(FrameworkElement inner)
    {
        var b = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = inner, ClipToBounds = true };
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        // cantos arredondados de verdade (o Border não recorta os filhos)
        inner.SizeChanged += (_, e) => inner.Clip = new RectangleGeometry(new Rect(e.NewSize), 9, 9);
        return b;
    }

    private async void OpenTest(Func<MonitorInfo, Window> make)
    {
        if (_monitors.Count == 0) await LoadMonitorsAsync();
        var m = (_target.SelectedItem as ComboBoxItem)?.Tag as MonitorInfo ?? _monitors.FirstOrDefault();
        if (m == null) return;
        try { _testWindow?.Close(); } catch { }
        var w = make(m);
        var owner = Window.GetWindow(this);
        w.Closed += (_, _) => { if (ReferenceEquals(_testWindow, w)) _testWindow = null; owner?.Activate(); };
        _testWindow = w;
        w.Show();
    }
}
