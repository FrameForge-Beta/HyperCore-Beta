using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameForge.Analysis;
using FrameForge.Core;
using FrameForge.Pages.Home;
using FrameForge.Sensors;

namespace FrameForge.Pages;

/// <summary>
/// Início: botão "ANALISAR O PC" (análise rápida, só leitura) com a Nota HyperCore e a lista do que melhorar,
/// cartões ao vivo (CPU, GPU, RAM, temperaturas), Modo Jogo, último jogo/FPS e atalhos.
/// </summary>
public sealed partial class HomePage : UserControl
{
    private const double CircleSize = 210;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _ticks;
    private bool _sensorsStarted;

    // ---- análise
    private readonly RingGauge _scoreRing = new() { Width = CircleSize - 26, Height = CircleSize - 26, Thickness = 10 };
    private readonly RingGauge _orbit = new() { Width = CircleSize, Height = CircleSize, Thickness = 3, Value = 0.2 };
    private readonly RotateTransform _orbitRotate = new();
    private readonly Ellipse _glow = new() { Width = 150, Height = 150, IsHitTestVisible = false, Opacity = 0.3, Effect = new BlurEffect { Radius = 48 } };
    private readonly Button _analyze = new() { Width = 150, Height = 150 };
    private readonly ScaleTransform _analyzeScale = new(1, 1);
    private readonly ContentControl _heroRight = new() { Focusable = false };
    private QuickResult? _result;
    private bool _running, _showAllIssues, _showGood;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, TextBlock> _runMarks = new();
    private TextBlock? _runPct;
    private int _runDone;

    public HomePage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = Ui.PageMargin };

        root.Children.Add(BuildHero());

        // Primeiro o que a pessoa quer fazer (ações em linguagem simples); o estado ao vivo vem depois
        var shortTitle = Ui.SectionTitle("O que queres fazer?");
        shortTitle.Margin = new Thickness(0, 10, 0, 10);
        root.Children.Add(shortTitle);
        var shortcuts = (UniformGrid)BuildShortcuts();
        root.Children.Add(shortcuts);

        var liveTitle = Ui.SectionTitle("Agora no teu PC");
        liveTitle.Margin = new Thickness(0, 10, 0, 10);
        root.Children.Add(liveTitle);
        var live = (UniformGrid)BuildLiveCards();
        root.Children.Add(live);

        var duo = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        duo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        duo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        duo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        duo.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        duo.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var gm = BuildGameModeCard();
        duo.Children.Add(gm);
        var game = BuildLastGameCard();
        Grid.SetColumn(game, 2);
        duo.Children.Add(game);
        root.Children.Add(duo);


        // Em janelas estreitas: 2 cartões por linha em vez de 4, e Modo Jogo / último jogo um por baixo do outro.
        root.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            double w = e.NewSize.Width;
            live.Columns = shortcuts.Columns = w >= 940 ? 4 : 2;
            bool stack = w < 680;
            duo.ColumnDefinitions[1].Width = new GridLength(stack ? 0 : 12);
            duo.ColumnDefinitions[2].Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(game, stack ? 0 : 2);
            Grid.SetRow(game, stack ? 1 : 0);
        };

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root };

        _tick.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) => OnVisibility();
        Unloaded += (_, _) => { _tick.Stop(); StopIdleAnimations(); };
        Loaded += (_, _) => OnVisibility();
        AppServices.Recorder.Finished += (_, _) => Dispatcher.BeginInvoke(new Action(LoadLastSession));

        ShowHeroState();
        _ = LoadSavedAsync();
        LoadLastSession();
    }

    // =================================================================== ciclo de vida

    private void OnVisibility()
    {
        if (IsVisible)
        {
            // O leitor completo (LibreHardwareMonitor) carrega um driver de kernel: só o ligamos aqui se o utilizador
            // já escolheu os sensores avançados. Sem isso usamos o que o SystemSampler lê (NVML, contadores do Windows).
            if (!_sensorsStarted && AppServices.Settings.AdvancedCpuSensors) { _sensorsStarted = true; try { SensorHub.Instance.Start(); } catch { } }
            Tick();
            _tick.Start();
            StartIdleAnimations();
        }
        else
        {
            _tick.Stop();
            StopIdleAnimations();
        }
    }

    private void Tick()
    {
        _ticks++;
        try { UpdateLive(PcLive.Read()); } catch { }
        if (_ticks % 2 == 1) UpdateLiveGame();
    }

    private async Task LoadSavedAsync()
    {
        var r = await Task.Run(QuickCheck.LoadLast);
        if (r != null && _result == null && !_running) { _result = r; ShowHeroState(); }
    }

    // =================================================================== cartão principal (análise)

    private Border BuildHero()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var circle = new Grid { Width = CircleSize, Height = CircleSize, VerticalAlignment = VerticalAlignment.Center };
        _glow.Fill = Ui.Res("Accent");
        circle.Children.Add(_glow);
        _orbit.Track = Tint("Accent", 0x1C);
        _orbit.Fill = Ui.Res("AccentGradient");
        _orbit.RenderTransformOrigin = new Point(0.5, 0.5);
        _orbit.RenderTransform = _orbitRotate;
        _orbit.IsHitTestVisible = false;
        circle.Children.Add(_orbit);
        _scoreRing.Track = Ui.Res("Line");
        _scoreRing.IsHitTestVisible = false;
        circle.Children.Add(_scoreRing);

        _analyze.Template = CircleTemplate;
        _analyze.Background = new RadialGradientBrush(Color.FromRgb(0x1D, 0x36, 0x5E), Color.FromRgb(0x0F, 0x18, 0x2B))
        {
            GradientOrigin = new Point(0.35, 0.25), Center = new Point(0.45, 0.4), RadiusX = 0.75, RadiusY = 0.75
        };
        _analyze.BorderBrush = Tint("Accent", 0x66);
        _analyze.Cursor = Cursors.Hand;
        _analyze.FocusVisualStyle = null;
        _analyze.RenderTransformOrigin = new Point(0.5, 0.5);
        _analyze.RenderTransform = _analyzeScale;
        _analyze.ToolTip = "Análise rápida (menos de 5 s). Só lê o PC, não muda nada.";
        _analyze.Click += (_, _) => _ = RunAnalysisAsync();
        _analyze.MouseEnter += (_, _) => { ScaleTo(1.04); _glow.BeginAnimation(OpacityProperty, Anim(0.55, 200)); };
        _analyze.MouseLeave += (_, _) => { ScaleTo(1); if (!_running) StartIdleAnimations(); };
        _analyze.PreviewMouseLeftButtonDown += (_, _) => ScaleTo(0.97, 80);
        _analyze.PreviewMouseLeftButtonUp += (_, _) => ScaleTo(_analyze.IsMouseOver ? 1.04 : 1, 120);
        circle.Children.Add(_analyze);
        grid.Children.Add(circle);

        Grid.SetColumn(_heroRight, 2);
        _heroRight.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_heroRight);

        return new Border
        {
            Background = new LinearGradientBrush(Color.FromRgb(0x12, 0x20, 0x3A), ((SolidColorBrush)Ui.Res("Card")).Color, new Point(0, 0), new Point(1, 1)),
            BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18),
            Padding = new Thickness(26, 22, 28, 22), Margin = new Thickness(0, 0, 0, 8), Child = grid,
        };
    }

    private static readonly ControlTemplate CircleTemplate = (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<Grid><Ellipse Fill='{TemplateBinding Background}' Stroke='{TemplateBinding BorderBrush}' StrokeThickness='1.5'/>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Grid></ControlTemplate>");

    /// <summary>Redesenha o círculo e o lado direito conforme o estado: sem análise, a analisar, ou com resultado.</summary>
    private void ShowHeroState()
    {
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        if (_running)
        {
            _runPct = new TextBlock { Text = "0%", FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = (FontFamily)Ui.Theme["Mono"] };
            center.Children.Add(_runPct);
            center.Children.Add(Centered(Ui.T("a analisar…", "MutedText", 12)));
            _scoreRing.Fill = Ui.Res("AccentGradient");
            _heroRight.Content = BuildRunningPanel();
        }
        else if (_result is { } r)
        {
            var color = Ui.Res(QuickCheck.ColorKey(r.Score));
            center.Children.Add(Centered(new TextBlock { Text = r.Score.ToString(), FontSize = 50, FontWeight = FontWeights.Bold, Foreground = color, Margin = new Thickness(0, -6, 0, -4) }));
            center.Children.Add(Centered(Ui.T("de 100", "MutedText", 11.5)));
            var again = Centered(Ui.T("ANALISAR DE NOVO", "Label", 9.5, Ui.Res("Accent2")));
            again.Margin = new Thickness(0, 8, 0, 0);
            center.Children.Add(again);
            _scoreRing.Fill = color;
            _scoreRing.AnimateTo(r.Score / 100.0, 900);
            _heroRight.Content = BuildResultPanel(r);
        }
        else
        {
            center.Children.Add(Centered(new TextBlock { Text = "", FontFamily = (FontFamily)Ui.Theme["Icons"], FontSize = 26, Foreground = Ui.Res("Accent2"), Margin = new Thickness(0, 0, 0, 6) }));
            center.Children.Add(Centered(new TextBlock { Text = "ANALISAR", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.White }));
            center.Children.Add(Centered(new TextBlock { Text = "O PC", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.White }));
            _scoreRing.Fill = Ui.Res("AccentGradient");
            _scoreRing.AnimateTo(0, 300);
            _heroRight.Content = BuildIntroPanel();
        }
        _analyze.Content = center;
        _analyze.IsEnabled = !_running;
    }

    private UIElement BuildIntroPanel()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.T("NOTA HYPERCORE", "Label", 10.5, Ui.Res("Accent2")));
        var h = Ui.T("Descobre a nota do teu PC", "H1", 24);
        h.Margin = new Thickness(0, 4, 0, 4);
        sp.Children.Add(h);
        sp.Children.Add(Ui.T("Carrega no círculo. Em poucos segundos vejo o que está a travar os teus jogos e digo-te o que melhorar. Só leio — não mudo nada.", "MutedText", 13.5));
        var chips = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var c in QuickCheck.Checks.Take(8))
            chips.Children.Add(Chip(c.Glyph, c.Label, Ui.Res("Muted")));
        sp.Children.Add(chips);
        return sp;
    }

    private UIElement BuildRunningPanel()
    {
        _runMarks.Clear();
        var sp = new StackPanel();
        sp.Children.Add(Ui.T("A ANALISAR O PC", "Label", 10.5, Ui.Res("Accent2")));
        var h = Ui.T("Só um momento…", "H1", 24);
        h.Margin = new Thickness(0, 4, 0, 12);
        sp.Children.Add(h);
        var grid = new UniformGrid { Columns = 2 };
        foreach (var c in QuickCheck.Checks)
        {
            var mark = new TextBlock { Text = "", FontFamily = (FontFamily)Ui.Theme["Icons"], FontSize = 12, Foreground = Ui.Res("Faint"), Width = 18, VerticalAlignment = VerticalAlignment.Center };
            _runMarks[c.Id] = mark;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 12, 3) };
            row.Children.Add(mark);
            row.Children.Add(new TextBlock { Text = c.Label, FontSize = 13, Foreground = Ui.Res("Muted"), VerticalAlignment = VerticalAlignment.Center });
            grid.Children.Add(row);
        }
        sp.Children.Add(grid);
        return sp;
    }

    private UIElement BuildResultPanel(QuickResult r)
    {
        var sp = new StackPanel();
        string when = r.When.Date == DateTime.Today ? $"hoje às {r.When:HH:mm}"
                    : r.When.Date == DateTime.Today.AddDays(-1) ? $"ontem às {r.When:HH:mm}"
                    : $"a {r.When:dd/MM} às {r.When:HH:mm}";
        sp.Children.Add(Ui.T($"NOTA HYPERCORE  ·  {when.ToUpperInvariant()}", "Label", 10.5, Ui.Res("Accent2")));

        var issues = r.Issues();
        var head = new TextBlock { FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2), TextWrapping = TextWrapping.Wrap };
        head.Inlines.Add(new System.Windows.Documents.Run(QuickCheck.Verdict(r.Score)) { Foreground = Ui.Res(QuickCheck.ColorKey(r.Score)) });
        head.Inlines.Add(new System.Windows.Documents.Run(issues.Count == 0 ? "  ·  está tudo em ordem"
            : $"  ·  {issues.Count} {(issues.Count == 1 ? "coisa a melhorar" : "coisas a melhorar")}"));
        sp.Children.Add(head);
        string sub = issues.Count == 0 ? "Não encontrei nada a travar os teus jogos." : "Carrega num ponto para ir direto à solução. Começa pelo de cima.";
        if (r.When < DateTime.Now.AddDays(-2)) sub = "Esta análise já tem uns dias. Carrega no círculo para a atualizar.";
        var subT = Ui.T(sub, "MutedText", 13);
        subT.Margin = new Thickness(0, 0, 0, 10);
        sp.Children.Add(subT);

        int shown = _showAllIssues || issues.Count <= 5 ? issues.Count : 4;
        foreach (var i in issues.Take(shown)) sp.Children.Add(IssueRow(i));

        var foot = new WrapPanel { Margin = new Thickness(4, 6, 0, 0) };
        if (issues.Count > 5)
            foot.Children.Add(LinkButton(_showAllIssues ? "Mostrar menos" : $"Mostrar mais {issues.Count - 4}", _showAllIssues ? "" : "",
                () => { _showAllIssues = !_showAllIssues; ShowHeroState(); }));
        var good = r.Good();
        if (good.Count > 0)
            foot.Children.Add(LinkButton($"{good.Count} {(good.Count == 1 ? "verificação" : "verificações")} sem problemas", "",
                () => { _showGood = !_showGood; ShowHeroState(); }, Ui.Res("Good")));
        if (foot.Children.Count > 0) sp.Children.Add(foot);

        if ((_showGood || issues.Count == 0) && good.Count > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var g in good)
            {
                var chip = Chip("", g.Title, Ui.Res("Good"));
                if (g.Detail.Length > 0) chip.ToolTip = g.Detail;
                wrap.Children.Add(chip);
            }
            sp.Children.Add(wrap);
        }
        return sp;
    }

    private UIElement IssueRow(QuickItem i)
    {
        string key = i.Severity >= 3 ? "Bad" : i.Severity == 2 ? "Warn" : "Accent";
        var color = Ui.Res(key);
        var glyph = QuickCheck.Checks.FirstOrDefault(c => c.Id == i.Check)?.Glyph ?? "";

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = Tint(key, 0x24), Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)Ui.Theme["Icons"], FontSize = 15, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        grid.Children.Add(tile);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = i.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = i.Detail, FontSize = 12, Foreground = Ui.Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var row = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 7, 12, 7), Margin = new Thickness(-10, 0, 0, 2), Background = Brushes.Transparent, Child = grid, ToolTip = i.Detail };
        if (i.Tag is string tag)
        {
            var go = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            go.Children.Add(new TextBlock { Text = i.Action ?? "Abrir", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("Accent"), VerticalAlignment = VerticalAlignment.Center });
            go.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)Ui.Theme["Icons"], FontSize = 10, Foreground = Ui.Res("Accent"), Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(go, 2);
            grid.Children.Add(go);
            row.Cursor = Cursors.Hand;
            row.MouseEnter += (_, _) => row.Background = Ui.Res("Hover");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => AppServices.Navigate(tag);
        }
        return row;
    }

    private async Task RunAnalysisAsync()
    {
        if (_running) return;
        _running = true;
        _runDone = 0;
        _cts = new CancellationTokenSource();
        ShowHeroState();
        _scoreRing.AnimateTo(0, 200);
        StartSpin(fast: true);
        var started = DateTime.Now;
        var progress = new UiProgress<QuickProgress>(Dispatcher, p =>
        {
            if (!_running) return; // verificação que acabou depois do limite: o resultado já está no ecrã
            _runDone = p.Done;
            if (_runPct != null) _runPct.Text = $"{100 * p.Done / Math.Max(1, p.Total)}%";
            _scoreRing.AnimateTo((double)p.Done / Math.Max(1, p.Total), 350);
            if (_runMarks.TryGetValue(p.Check, out var m))
            {
                m.Text = p.Worst == 0 ? "" : "";
                m.Foreground = Ui.Res(p.Worst >= 3 ? "Bad" : p.Worst == 2 ? "Warn" : p.Worst == 1 ? "Accent" : "Good");
                if (m.Parent is StackPanel row && row.Children.Count > 1 && row.Children[1] is TextBlock label) label.Foreground = Ui.Res("Fg");
            }
        });
        QuickResult? result = null;
        try
        {
            result = await QuickCheck.RunAsync(progress, _cts.Token);
            // Deixa ver o anel a fechar (a análise pode acabar em menos de 1 s)
            var left = TimeSpan.FromMilliseconds(1400) - (DateTime.Now - started);
            if (left > TimeSpan.Zero) await Task.Delay(left);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppServices.Toast("A análise rápida falhou: " + ex.Message); }
        finally
        {
            _running = false;
            _cts.Dispose();
            _cts = null;
        }
        if (result != null) { _result = result; _showAllIssues = false; _showGood = false; }
        StartIdleAnimations();
        ShowHeroState();
    }

    // =================================================================== animações do anel

    private void StartIdleAnimations()
    {
        if (!IsVisible) return;
        if (_running) { StartSpin(fast: true); return; }
        StartSpin(fast: false);
        if (_analyze.IsMouseOver) return;
        var pulse = new DoubleAnimation(0.16, 0.38, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        _glow.BeginAnimation(OpacityProperty, pulse);
    }

    private void StopIdleAnimations()
    {
        _orbitRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _glow.BeginAnimation(OpacityProperty, null);
    }

    private void StartSpin(bool fast)
    {
        _orbit.Opacity = fast ? 1 : 0.55;
        _orbit.AnimateTo(fast ? 0.32 : 0.18, 300);
        double from = _orbitRotate.Angle % 360;
        _orbitRotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(from, from + 360, TimeSpan.FromSeconds(fast ? 1.1 : 9)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void ScaleTo(double s, int ms = 160)
    {
        var a = Anim(s, ms);
        _analyzeScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _analyzeScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private static DoubleAnimation Anim(double to, int ms) =>
        new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

    /// <summary>Progresso entregue sempre no UI thread desta página (não depende do SynchronizationContext de quem chama).</summary>
    private sealed class UiProgress<T>(Dispatcher dispatcher, Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => dispatcher.BeginInvoke(handler, value);
    }

    // =================================================================== pequenos construtores

    private static T Centered<T>(T e) where T : FrameworkElement { e.HorizontalAlignment = HorizontalAlignment.Center; if (e is TextBlock t) t.TextAlignment = TextAlignment.Center; return e; }

    /// <summary>A cor do tema com transparência (para fundos de ícones e trilhos).</summary>
    private static SolidColorBrush Tint(string key, byte alpha)
    {
        var c = Ui.Res(key) is SolidColorBrush s ? s.Color : Colors.DodgerBlue;
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private static Border Chip(string glyph, string text, Brush iconColor)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Ui.Theme["Icons"], FontSize = 11.5, Foreground = iconColor, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Ui.Res("Fg"), VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Background = Ui.Res("Hover"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(99),
            Padding = new Thickness(10, 4, 12, 4), Margin = new Thickness(0, 0, 6, 6), Child = sp,
        };
    }

    private static Button LinkButton(string text, string glyph, Action click, Brush? color = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Ui.Theme["Icons"], FontSize = 11, Foreground = color ?? Ui.Res("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = color ?? Ui.Res("Accent"), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = sp, Template = LinkTemplate, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 20, 0), Padding = new Thickness(0, 4, 0, 4) };
        b.Click += (_, _) => click();
        return b;
    }

    private static readonly ControlTemplate LinkTemplate = (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
        "<Border x:Name='b' Background='Transparent' Padding='{TemplateBinding Padding}' Opacity='0.92'>" +
        "<ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'>" +
        "<Setter TargetName='b' Property='Opacity' Value='1'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
}
