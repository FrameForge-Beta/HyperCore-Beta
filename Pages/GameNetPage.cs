using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameForge.Core;
using FrameForge.Net;

namespace FrameForge.Pages;

/// <summary>
/// Rede para jogos › Ping e lag (tag "FF:netgame"): ping/jitter/perda ao vivo com gráfico, diagnóstico simples (Wi-Fi, casa ou
/// fornecedor), teste de bufferbloat com nota A–F, DNS mais rápido (mudar só com confirmação, registado na Central Desfazer),
/// ping por região de servidores de jogos e histórico. O motor está em Net/GameNet*.cs; nada aqui bloqueia o UI.
/// </summary>
public sealed class GameNetPage : UserControl
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");
    private const string Icons = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly FontFamily Num = new("Segoe UI Variable Display, Segoe UI");
    private const string TipText = "Continua a medir mesmo que mudes de página: deixa ligado enquanto jogas e volta aqui para ver os picos.";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(GameNetMonitor.IntervalMs) };
    private ActiveLink? _link;

    // ---- ao vivo
    private readonly GameNetGraph _graph = new() { Height = 228 };
    private readonly Button _startBtn;
    private readonly TextBlock _startIcon = new() { FontFamily = new FontFamily(Icons), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _startText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _liveDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _liveLabel;
    private readonly TextBlock _bigPing = new() { FontSize = 54, FontWeight = FontWeights.SemiBold, Text = "—" };
    private readonly Border _qualityPill;
    private readonly TextBlock _qualityText = new() { FontSize = 11, FontWeight = FontWeights.Bold };
    private TextBlock _jitter = null!, _loss = null!, _spikes = null!, _router = null!;
    private readonly Dictionary<string, RadioButton> _chips = new();
    private readonly ComboBox _regionBox = new() { MinWidth = 150, Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _tip;
    private bool _syncingTarget;

    // ---- caminho PC → router → servidor
    private Border _nodePc = null!, _nodeRouter = null!, _nodeServer = null!;
    private TextBlock _routerGlyph = null!, _serverGlyph = null!, _serverCaption = null!;
    private Rectangle _seg1 = null!, _seg2 = null!;
    private TextBlock _seg1Label = null!, _seg2Label = null!;

    // ---- veredicto
    private readonly Border _verdictBox = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14), BorderThickness = new Thickness(1) };
    private readonly TextBlock _verdictIcon = new() { FontFamily = new FontFamily(Icons), FontSize = 20, Margin = new Thickness(0, 1, 14, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _verdictTitle;
    private readonly TextBlock _verdictDetail;

    // ---- bufferbloat
    private readonly Border _gradeBadge = new() { Width = 84, Height = 84, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _gradeLetter = new() { FontSize = 40, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = "?" };
    private TextBlock _bIdle = null!, _bLoad = null!, _bExtra = null!, _bSpeed = null!;
    private readonly TextBlock _bMeaning;
    private readonly TextBlock _bStatus;
    private readonly ScaleTransform _bProgress = new(0, 1);
    private readonly Button _bBtn;
    private CancellationTokenSource? _bCts;
    private BloatResult? _lastBloat;
    private readonly List<(string Grade, Border Cell)> _gradeCells = new();

    // ---- DNS
    private readonly StackPanel _dnsRows = new() { Margin = new Thickness(-10, 10, -10, 4) };
    private readonly TextBlock _dnsNow;
    private readonly TextBlock _dnsStatus;
    private readonly Button _dnsMeasure, _dnsUse;
    private readonly Border _dnsChangeBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
    private readonly TextBlock _dnsChangeText;
    private List<DnsCandidate> _dnsCandidates = new();
    private readonly Dictionary<string, DnsScore> _dnsScores = new();
    private DnsCandidate? _dnsPick;
    private CancellationTokenSource? _dnsCts;
    private bool _dnsBusy;

    // ---- regiões
    private readonly StackPanel _regionRows = new() { Margin = new Thickness(-10, 4, -10, 0) };
    private readonly Button _regionBtn;
    private readonly TextBlock _regionStatus;
    private readonly Dictionary<string, (double? Ms, double Loss)> _regionResults = new();
    private CancellationTokenSource? _regionCts;

    // ---- histórico
    private readonly StackPanel _histRows = new();
    private readonly Button _histMore, _histClear;
    private bool _histAll;

    public GameNetPage()
    {
        Ui.UseTheme(this);
        SetResourceReference(TextElement.ForegroundProperty, "Fg");
        GameNet.Init();
        var root = new StackPanel { Margin = Ui.PageMargin };

        _liveLabel = Ui.T("PARADO", "Label");
        _qualityPill = new Border { CornerRadius = new CornerRadius(99), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, Child = _qualityText, Margin = new Thickness(2, 0, 0, 0) };
        _verdictTitle = Ui.T("", "H2", 16);
        _verdictTitle.Margin = new Thickness(0, 0, 0, 2);
        _verdictDetail = Ui.T("", "MutedText");
        _tip = Ui.T(TipText, "MutedText", 12);
        _tip.Foreground = Ui.Res("Faint");
        _tip.Margin = new Thickness(0, 10, 0, 0);

        _startBtn = new Button { Style = (Style)Ui.Theme["BtnAccent"], Padding = new Thickness(20, 11, 22, 11), Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        _startBtn.Content = Ui.Row(_startIcon, _startText);
        _startBtn.Click += (_, _) => ToggleMonitor();

        _bMeaning = Ui.T("Mede o ping em repouso e depois com a ligação a descarregar a fundo. Quanto menos subir, melhor.", "MutedText");
        _bStatus = Ui.T("", "MutedAuto");
        _bBtn = Ui.Btn("Testar bufferbloat", (_, _) => RunBloat());
        _bBtn.ToolTip = "Demora cerca de 15 s e descarrega dados de teste da Cloudflare (speed.cloudflare.com) para a memória: nada fica guardado. " +
                        "Em ligações muito rápidas pode gastar até 600 MB. Fecha downloads e vídeos antes, para o resultado ser fiel.";
        ToolTipService.SetShowDuration(_bBtn, 30000);

        _dnsNow = Ui.T("Agora: a ler…", "MutedText", 12.5);
        _dnsStatus = Ui.T("", "MutedAuto");
        _dnsMeasure = Ui.Btn("Medir DNS", (_, _) => MeasureDns());
        _dnsUse = Ui.Btn("Usar…", (_, _) => ApplyDns());
        _dnsUse.Visibility = Visibility.Collapsed;
        _dnsChangeText = Ui.T("", "Body", 12.5);

        _regionBtn = Ui.Btn("Medir regiões", (_, _) => MeasureRegions());
        _regionStatus = Ui.T("", "MutedAuto");
        _histMore = Ui.Btn("Mostrar tudo", (_, _) => { _histAll = !_histAll; RenderHistory(); });
        _histClear = Ui.Btn("Limpar histórico", (_, _) => ClearHistory());

        root.Children.Add(BuildLiveCard());
        root.Children.Add(BuildToolsRow());
        root.Children.Add(BuildRegionsCard());
        root.Children.Add(BuildHistoryCard());

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root };

        _timer.Tick += (_, _) => UpdateLive();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        SyncTargetChips();
        UpdateStartButton();
        UpdateLive();
        RenderDnsRows();
        RenderRegionRows();
        RenderHistory();
        RefreshDnsChange();
    }

    // =====================================================================================================================
    // Ciclo de vida
    // =====================================================================================================================

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        GameNetMonitor.StateChanged += OnMonitorState;
        GameNetHistory.Changed += OnHistoryChanged;
        ChangeJournal.Changed += OnJournalChanged;
        _timer.Start();
        SyncTargetChips();
        UpdateStartButton();
        UpdateLive();
        RenderHistory();
        RefreshDnsChange();
        _ = LoadLinkAsync();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        // O monitor continua a medir de propósito (para ficar ligado durante o jogo); a página só deixa de desenhar.
        GameNetMonitor.StateChanged -= OnMonitorState;
        GameNetHistory.Changed -= OnHistoryChanged;
        ChangeJournal.Changed -= OnJournalChanged;
        _timer.Stop();
    }

    private void OnMonitorState() => Dispatcher.BeginInvoke(new Action(() => { SyncTargetChips(); UpdateStartButton(); UpdateLive(); }));
    private void OnHistoryChanged() => Dispatcher.BeginInvoke(new Action(RenderHistory));
    private void OnJournalChanged() => Dispatcher.BeginInvoke(new Action(RefreshDnsChange));

    /// <summary>Lê a ligação ativa e o DNS atual fora do UI (pode demorar em PCs com muitos adaptadores).</summary>
    public async Task LoadLinkAsync()
    {
        try
        {
            _link = await Task.Run(GameNetProbe.ActiveLink);
            var cands = await Task.Run(GameNetDns.Candidates);
            if (_dnsScores.Count == 0 && !_dnsBusy) { _dnsCandidates = cands; RenderDnsRows(); }
            UpdateDnsNow(cands);
            UpdateLive();
        }
        catch { }
    }

    // =====================================================================================================================
    // 1) Ao vivo
    // =====================================================================================================================

    private Border BuildLiveCard()
    {
        var body = new StackPanel();

        // ---- topo: estado + alvos | botão
        var top = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(_startBtn, Dock.Right);
        top.Children.Add(_startBtn);

        var left = new StackPanel();
        var live = Ui.Row(_liveDot, _liveLabel);
        live.Margin = new Thickness(0, 0, 0, 10);
        left.Children.Add(live);

        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        string group = "netgame-target-" + Guid.NewGuid().ToString("N");
        foreach (var t in GameNetProbe.Targets.Where(t => !t.Approx))
            chips.Children.Add(Chip(t.Short, t.Id, group, $"{t.Name}. {t.Note}"));
        chips.Children.Add(Chip("Servidor de jogos", "games", group,
            "Ping até um centro de dados onde muitos jogos online têm servidores. É aproximado: o servidor exato do teu jogo pode ser outro."));
        foreach (var t in GameNetProbe.Targets.Where(t => t.Approx))
            _regionBox.Items.Add(new ComboBoxItem { Content = t.Short, Tag = t.Id, ToolTip = t.Note });
        _regionBox.SelectedIndex = 0;
        _regionBox.SelectionChanged += (_, _) =>
        {
            if (_syncingTarget) return;
            _chips["games"].IsChecked = true;
            ChooseTarget(SelectedRegion());
        };
        chips.Children.Add(_regionBox);
        left.Children.Add(chips);
        top.Children.Add(left);
        body.Children.Add(top);

        // ---- números | gráfico
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var kpi = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        kpi.Children.Add(Ui.T("PING AGORA", "Label"));
        _bigPing.FontFamily = Num;
        var unit = Ui.T("ms", "MutedText", 16);
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        unit.Margin = new Thickness(6, 0, 0, 13);
        kpi.Children.Add(Ui.Row(_bigPing, unit));
        kpi.Children.Add(_qualityPill);

        var mini = new UniformGrid { Columns = 2, Margin = new Thickness(0, 16, 0, 0) };
        mini.Children.Add(MiniStat("Jitter", "Quanto o ping varia de uma medição para a seguinte. Acima de 8 ms nota-se em jogos rápidos.", out _jitter));
        mini.Children.Add(MiniStat("Perda", "Pacotes que não voltaram. Acima de 1% há teletransportes e tiros que não contam.", out _loss));
        mini.Children.Add(MiniStat("Picos de lag", "Vezes em que o ping disparou ou se perderam pacotes, nos últimos 10 minutos.", out _spikes));
        mini.Children.Add(MiniStat("Router", "Ping só até ao teu router. Se este já é alto ou oscila, o problema está dentro de casa (Wi-Fi ou cabo).", out _router));
        kpi.Children.Add(mini);
        grid.Children.Add(kpi);

        var graphBox = new StackPanel();
        graphBox.Children.Add(_graph);
        var legend = new WrapPanel { Margin = new Thickness(36, 8, 0, 0) };
        legend.Children.Add(LegendItem(new Rectangle { Width = 16, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Ui.Res("AccentGradient") }, "servidor"));
        legend.Children.Add(LegendItem(new Rectangle { Width = 16, Height = 1.5, Stroke = Ui.Res("Muted"), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 2, 2 } }, "router"));
        legend.Children.Add(LegendItem(new Rectangle { Width = 4, Height = 10, RadiusX = 2, RadiusY = 2, Fill = Ui.Res("Bad") }, "pacote perdido"));
        legend.Children.Add(LegendItem(new Rectangle { Width = 16, Height = 1, Stroke = Ui.Res("Warn"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, Opacity = 0.7 }, "limite de pico"));
        graphBox.Children.Add(legend);
        Grid.SetColumn(graphBox, 1);
        grid.Children.Add(graphBox);
        body.Children.Add(grid);

        // ---- caminho + veredicto
        body.Children.Add(new Border { Height = 1, Background = Ui.Res("Line"), Margin = new Thickness(0, 18, 0, 18) });
        body.Children.Add(BuildChain());
        var verdict = new DockPanel();
        DockPanel.SetDock(_verdictIcon, Dock.Left);
        verdict.Children.Add(_verdictIcon);
        var vt = new StackPanel();
        vt.Children.Add(_verdictTitle);
        vt.Children.Add(_verdictDetail);
        verdict.Children.Add(vt);
        _verdictBox.Child = verdict;
        _verdictBox.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(_verdictBox);
        body.Children.Add(_tip);

        return Ui.Card(body);
    }

    private RadioButton Chip(string text, string key, string group, string tooltip)
    {
        var rb = new RadioButton { Content = text, GroupName = group, Style = (Style)Ui.Theme["Chip"], Margin = new Thickness(0, 0, 8, 8), ToolTip = tooltip };
        rb.Checked += (_, _) =>
        {
            if (_syncingTarget) return;
            ChooseTarget(key == "games" ? SelectedRegion() : GameNetProbe.Find(key));
        };
        _chips[key] = rb;
        return rb;
    }

    private GameNetTarget SelectedRegion() =>
        GameNetProbe.Find((_regionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "aws-fra");

    private void ChooseTarget(GameNetTarget t)
    {
        GameNetMonitor.SetTarget(t);
        UpdateLive();
    }

    /// <summary>Mostra nos chips o alvo atual do monitor (que pode ter mudado noutra visita à página).</summary>
    private void SyncTargetChips()
    {
        _syncingTarget = true;
        try
        {
            var t = GameNetMonitor.Target;
            if (t.Approx)
            {
                foreach (ComboBoxItem item in _regionBox.Items)
                    if ((string)item.Tag == t.Id) _regionBox.SelectedItem = item;
                _chips["games"].IsChecked = true;
            }
            else if (_chips.TryGetValue(t.Id, out var rb)) rb.IsChecked = true;
        }
        finally { _syncingTarget = false; }
    }

    private static Border MiniStat(string label, string tooltip, out TextBlock value)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.T(label.ToUpperInvariant(), "Label", 10));
        value = new TextBlock { Text = "—", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 0), FontFamily = Num };
        sp.Children.Add(value);
        var b = new Border
        {
            Child = sp, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 8, 8),
            Background = Ui.Res("Panel"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), ToolTip = tooltip,
        };
        ToolTipService.SetShowDuration(b, 30000);
        return b;
    }

    private static StackPanel LegendItem(FrameworkElement swatch, string text)
    {
        swatch.VerticalAlignment = VerticalAlignment.Center;
        swatch.Margin = new Thickness(0, 0, 6, 0);
        var t = Ui.T(text, "MutedText", 11.5);
        t.Foreground = Ui.Res("Faint");
        var row = Ui.Row(swatch, t);
        row.Margin = new Thickness(0, 0, 16, 0);
        return row;
    }

    private Grid BuildChain()
    {
        var g = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pc = Node("", "Este PC", out _nodePc, out _, out _);
        var router = Node("", "Router", out _nodeRouter, out _routerGlyph, out _);
        var server = Node("", "Servidor", out _nodeServer, out _serverGlyph, out _serverCaption);
        var s1 = Segment(out _seg1, out _seg1Label);
        var s2 = Segment(out _seg2, out _seg2Label);
        Grid.SetColumn(s1, 1); Grid.SetColumn(router, 2); Grid.SetColumn(s2, 3); Grid.SetColumn(server, 4);
        g.Children.Add(pc); g.Children.Add(s1); g.Children.Add(router); g.Children.Add(s2); g.Children.Add(server);
        return g;
    }

    private static StackPanel Node(string glyph, string caption, out Border circle, out TextBlock icon, out TextBlock label)
    {
        icon = new TextBlock { Text = glyph, FontFamily = new FontFamily(Icons), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        circle = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), BorderThickness = new Thickness(1.5), Child = icon, HorizontalAlignment = HorizontalAlignment.Center };
        label = Ui.T(caption, "MutedText", 12);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.TextAlignment = TextAlignment.Center;
        label.Margin = new Thickness(0, 6, 0, 0);
        var sp = new StackPanel { MinWidth = 96 };
        sp.Children.Add(circle);
        sp.Children.Add(label);
        return sp;
    }

    private static StackPanel Segment(out Rectangle line, out TextBlock label)
    {
        label = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Height = 18, Margin = new Thickness(0, 0, 0, 3.5) };
        line = new Rectangle { Height = 3, RadiusX = 1.5, RadiusY = 1.5 };
        var sp = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        sp.Children.Add(label);
        sp.Children.Add(line);
        return sp;
    }

    private void ToggleMonitor()
    {
        if (GameNetMonitor.IsRunning)
        {
            var rec = GameNetMonitor.Stop();
            _tip.Text = rec != null ? "Sessão guardada no histórico (em baixo)." : "Sessão curta: só entra no histórico a partir de 30 s de medição.";
        }
        else
        {
            var t = _chips.TryGetValue("games", out var g) && g.IsChecked == true ? SelectedRegion() : GameNetMonitor.Target;
            GameNetMonitor.Start(t);
            _tip.Text = TipText;
        }
        UpdateStartButton();
        UpdateLive();
    }

    private void UpdateStartButton()
    {
        bool on = GameNetMonitor.IsRunning;
        _startIcon.Text = on ? "" : "";
        _startText.Text = on ? "Parar" : "Começar a medir";
        _liveDot.Fill = on ? Ui.Res("Good") : Ui.Res("Faint");
        if (on)
            _liveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        else
            _liveDot.BeginAnimation(OpacityProperty, null);
    }

    private void UpdateLive()
    {
        bool on = GameNetMonitor.IsRunning;
        var target = GameNetMonitor.Target;
        var link = GameNetMonitor.Link ?? _link;
        var samples = GameNetMonitor.Snapshot(_graph.Capacity);
        var st = NetStats.From(samples);
        var session = GameNetMonitor.Stats(1200);

        _graph.SpikeLine = session.Count >= 16 && session.Median is double med ? NetStats.SpikeThreshold(med) : null;
        _graph.SetSamples(samples);

        var el = GameNetMonitor.Elapsed;
        _liveLabel.Text = on ? $"A MEDIR AO VIVO · {(int)el.TotalMinutes:00}:{el.Seconds:00} · {target.Name.ToUpperInvariant()}"
                             : samples.Length > 0 ? $"PARADO · ÚLTIMA MEDIÇÃO: {target.Name.ToUpperInvariant()}" : "PARADO";

        // Número grande: média das últimas 4 medições (menos nervoso que o valor solto)
        var recent = samples.Skip(Math.Max(0, samples.Length - 4)).Where(s => s.Ms != null).Select(s => s.Ms!.Value).ToList();
        double? now = recent.Count > 0 ? recent.Average() : null;
        var (word, qLevel) = GameNetDiagnosis.PingQuality(st.Avg);
        _bigPing.Text = now is double n ? n.ToString(n < 10 ? "0.#" : "0", Pt) : "—";
        _bigPing.Foreground = now == null ? Ui.Res("Faint") : LevelBrush(GameNetDiagnosis.PingQuality(now).Level);
        if (st.Count >= 6 && st.Avg != null)
        {
            var qb = LevelBrush(qLevel);
            _qualityText.Text = $"{word.ToUpperInvariant()} PARA JOGAR";
            _qualityText.Foreground = qb;
            _qualityPill.Background = Tint(qb, 34);
            _qualityPill.Visibility = Visibility.Visible;
        }
        else _qualityPill.Visibility = Visibility.Hidden;

        bool has = st.Count >= 4;
        _jitter.Text = has ? $"{GameNetDiagnosis.F(st.Jitter)} ms" : "—";
        _jitter.Foreground = has ? LevelBrush(GameNetDiagnosis.JitterLevel(st.Jitter)) : Ui.Res("Faint");
        _loss.Text = has ? $"{GameNetDiagnosis.F1(st.LossPct)}%" : "—";
        _loss.Foreground = has ? LevelBrush(GameNetDiagnosis.LossLevel(st.LossPct)) : Ui.Res("Faint");
        _spikes.Text = session.Count >= 16 ? session.Spikes.ToString(Pt) : "—";
        _spikes.Foreground = session.Count < 16 ? Ui.Res("Faint") : session.Spikes == 0 ? Ui.Res("Good") : session.Spikes <= 3 ? Ui.Res("Warn") : Ui.Res("Bad");
        int localLevel = GameNetDiagnosis.LocalLevel(st);
        _router.Text = st.RouterKnown ? $"{GameNetDiagnosis.F(st.RouterAvg)} ms" : st.RouterCount >= 10 ? "sem ping" : "—";
        _router.Foreground = st.RouterKnown ? LevelBrush(localLevel) : Ui.Res("Faint");

        // Caminho PC → router → servidor
        bool wifi = link?.IsWifi == true;
        _routerGlyph.Text = wifi ? "" : "";
        _serverGlyph.Text = target.Approx ? "" : "";
        _serverCaption.Text = target.Short + (target.Approx ? " (aprox.)" : "");
        bool ready = st.Count >= 16;
        int local = ready ? localLevel : -1;
        int remote = ready ? GameNetDiagnosis.RemoteLevel(st) : -1;
        PaintNode(_nodePc, on || st.Count > 0 ? 0 : -1);
        PaintNode(_nodeRouter, local);
        PaintNode(_nodeServer, remote < 0 ? -1 : Math.Max(remote, qLevel == 2 ? 1 : 0));
        _seg1.Fill = local < 0 ? Ui.Res("LineStrong") : LevelBrush(local);
        _seg2.Fill = remote < 0 ? Ui.Res("LineStrong") : LevelBrush(remote);
        string medium = wifi ? "Wi-Fi" : link != null ? "Cabo" : "Rede local";
        _seg1Label.Text = medium + (st.RouterKnown ? $" · {GameNetDiagnosis.F(st.RouterAvg)} ms" : st.RouterCount >= 10 ? " · router sem ping" : "");
        _seg1Label.Foreground = local >= 0 ? LevelBrush(local) : Ui.Res("Muted");
        _seg2Label.Text = st.Avg is double avg ? $"Internet · {GameNetDiagnosis.F(avg)} ms" : "Internet";
        _seg2Label.Foreground = remote >= 0 ? LevelBrush(remote) : Ui.Res("Muted");

        // Veredicto
        var v = st.Count == 0 && !on
            ? new NetVerdict(-1, "Pronto para medir",
                "Mede o ping até ao servidor e até ao teu router ao mesmo tempo, para saberes se o lag vem do Wi-Fi, do router ou do fornecedor.", "wait")
            : GameNetDiagnosis.Evaluate(st, link, target, _lastBloat);
        ShowVerdict(v);
    }

    private void ShowVerdict(NetVerdict v)
    {
        var b = v.Level < 0 ? Ui.Res("Accent") : LevelBrush(v.Level);
        _verdictBox.Background = Tint(b, 22);
        _verdictBox.BorderBrush = Tint(b, 80);
        _verdictIcon.Foreground = b;
        _verdictIcon.Text = v.Level switch { 0 => "", 1 => "", 2 => "", _ => "" };
        if (_verdictTitle.Text != v.Title) _verdictTitle.Text = v.Title;
        if (_verdictDetail.Text != v.Detail) _verdictDetail.Text = v.Detail;
    }

    private static void PaintNode(Border node, int level)
    {
        var b = level < 0 ? Ui.Res("LineStrong") : LevelBrush(level);
        node.BorderBrush = b;
        node.Background = level < 0 ? Ui.Res("Panel") : Tint(b, 30);
        if (node.Child is TextBlock t) t.Foreground = level < 0 ? Ui.Res("Muted") : b;
    }

    // =====================================================================================================================
    // 2) Bufferbloat + DNS
    // =====================================================================================================================

    private Grid BuildToolsRow()
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bloat = BuildBloatCard();
        var dns = BuildDnsCard();
        bloat.Margin = dns.Margin = new Thickness(0);
        Grid.SetColumn(dns, 2);
        g.Children.Add(bloat);
        g.Children.Add(dns);
        return g;
    }

    private static StackPanel CardHeader(string glyph, string title, string sub)
    {
        var icon = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Background = Ui.Res("AccentSoft"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = glyph, FontFamily = new FontFamily(Icons), FontSize = 16, Foreground = Ui.Res("Accent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var text = new StackPanel();
        var h = Ui.T(title, "H2", 16);
        h.Margin = new Thickness(0, 0, 0, 1);
        text.Children.Add(h);
        text.Children.Add(Ui.T(sub, "MutedText", 12.5));
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(text);
        var sp = new StackPanel();
        sp.Children.Add(dock);
        return sp;
    }

    private Border BuildBloatCard()
    {
        var body = CardHeader("", "Teste de bufferbloat", "O ping aguenta quando alguém em casa descarrega? Demora 15 s.");

        _gradeBadge.Child = _gradeLetter;
        PaintGrade(null);
        var rows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        rows.Children.Add(KeyValue("Em repouso", out _bIdle));
        rows.Children.Add(KeyValue("A descarregar", out _bLoad));
        rows.Children.Add(KeyValue("Subida do ping", out _bExtra));
        rows.Children.Add(KeyValue("Velocidade", out _bSpeed));
        var top = new DockPanel();
        DockPanel.SetDock(_gradeBadge, Dock.Left);
        top.Children.Add(_gradeBadge);
        top.Children.Add(rows);
        body.Children.Add(top);

        _bMeaning.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(_bMeaning);
        body.Children.Add(BuildGradeScale());
        PaintGrade(null);

        var track =new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Ui.Res("Hover"), Margin = new Thickness(0, 14, 0, 0) };
        track.Child = new Border { CornerRadius = new CornerRadius(2), Background = Ui.Res("AccentGradient"), RenderTransform = _bProgress };
        body.Children.Add(track);
        _bStatus.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_bStatus);

        _bBtn.Margin = new Thickness(0, 12, 0, 0);
        _bBtn.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(_bBtn);
        return Ui.Card(body);
    }

    private static DockPanel KeyValue(string key, out TextBlock value)
    {
        var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        value = new TextBlock { Text = "—", FontFamily = Num, FontSize = 13.5, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(value, Dock.Right);
        d.Children.Add(value);
        d.Children.Add(Ui.T(key, "MutedText", 13));
        return d;
    }

    private void PaintGrade(string? grade)
    {
        var b = grade switch { "A" or "B" => Ui.Res("Good"), "C" => Ui.Res("Warn"), "D" or "F" => Ui.Res("Bad"), _ => Ui.Res("LineStrong") };
        _gradeLetter.Text = grade ?? "?";
        _gradeLetter.Foreground = grade == null ? Ui.Res("Faint") : b;
        _gradeBadge.BorderBrush = b;
        _gradeBadge.Background = grade == null ? Ui.Res("Panel") : Tint(b, 30);

        // Escala A–F: realça a nota obtida
        foreach (var (g, cell) in _gradeCells)
        {
            bool on = g == grade;
            var c = GradeBrush(g);
            cell.Background = on ? Tint(c, 40) : Ui.Res("Panel");
            cell.BorderBrush = on ? c : Ui.Res("Line");
            if (cell.Child is StackPanel sp && sp.Children[0] is TextBlock letter)
                letter.Foreground = on || grade == null ? c : Tint(c, 120);
        }
    }

    private static Brush GradeBrush(string g) => g switch { "A" or "B" => Ui.Res("Good"), "C" => Ui.Res("Warn"), _ => Ui.Res("Bad") };

    /// <summary>Legenda da nota: quanto o ping pode subir sob carga para cada letra.</summary>
    private UniformGrid BuildGradeScale()
    {
        var grid = new UniformGrid { Columns = 5, Rows = 1, Margin = new Thickness(0, 14, -6, 0) };
        foreach (var (g, limit) in new[] { ("A", "até +20 ms"), ("B", "até +50"), ("C", "até +100"), ("D", "até +200"), ("F", "mais") })
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = g, FontSize = 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            var sub = Ui.T(limit, "MutedText", 10.5);
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            sub.TextWrapping = TextWrapping.NoWrap;
            sub.Foreground = Ui.Res("Faint");
            sp.Children.Add(sub);
            var cell = new Border
            {
                Child = sp, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(2, 5, 2, 5), Margin = new Thickness(0, 0, 6, 0),
                ToolTip = $"Nota {g}: {GameNetBufferbloat.GradeMeaning(g)}",
            };
            _gradeCells.Add((g, cell));
            grid.Children.Add(cell);
        }
        return grid;
    }

    private async void RunBloat()
    {
        if (_bCts != null) { _bCts.Cancel(); return; }
        _bCts = new CancellationTokenSource();
        var ct = _bCts.Token;
        _bBtn.Content = "Cancelar";
        PaintGrade(null);
        _bIdle.Text = _bLoad.Text = _bExtra.Text = _bSpeed.Text = "—";
        _bExtra.Foreground = Ui.Res("Fg");
        _bProgress.ScaleX = 0;
        _bStatus.Foreground = Ui.Res("Muted");
        _bStatus.Text = "A medir o ping em repouso…";

        var idleSeen = new List<double>();
        var loadSeen = new List<double>();
        var progress = new Progress<BloatProgress>(p =>
        {
            if (ct.IsCancellationRequested) return;
            _bProgress.ScaleX = Math.Clamp(p.Fraction, 0, 1);
            if (p.Phase == "idle")
            {
                if (p.LastMs is double ms) idleSeen.Add(ms);
                if (idleSeen.Count > 0) _bIdle.Text = $"{GameNetDiagnosis.F(NetStats.MedianOf(idleSeen))} ms";
                _bStatus.Text = "A medir o ping em repouso…";
            }
            else
            {
                if (p.LastMs is double ms) loadSeen.Add(ms);
                if (loadSeen.Count > 0) _bLoad.Text = $"{GameNetDiagnosis.F(NetStats.MedianOf(loadSeen))} ms";
                if (p.Mbps is double mb) _bSpeed.Text = $"{mb:0} Mbps";
                _bStatus.Text = "A descarregar a fundo e a medir o ping ao mesmo tempo…";
            }
        });

        try
        {
            var r = await Task.Run(() => GameNetBufferbloat.RunAsync(progress, ct), ct);
            _lastBloat = r;
            PaintGrade(r.Grade);
            _bIdle.Text = $"{GameNetDiagnosis.F(r.IdleMs)} ms";
            _bLoad.Text = $"{GameNetDiagnosis.F(r.LoadedMs)} ms";
            _bExtra.Text = $"+{GameNetDiagnosis.F(r.ExtraMs)} ms";
            _bExtra.Foreground = r.Grade is "A" or "B" ? Ui.Res("Good") : r.Grade == "C" ? Ui.Res("Warn") : Ui.Res("Bad");
            _bSpeed.Text = r.Mbps is double mb ? $"{mb:0} Mbps" : "—";
            _bMeaning.Text = GameNetBufferbloat.GradeMeaning(r.Grade) +
                             (r.Grade is "A" or "B" ? "" : " Ativa o QoS/SQM no router (às vezes chamado \"gestão de largura de banda\") ou pausa os downloads enquanto jogas.");
            _bProgress.ScaleX = 1;
            _bStatus.Text = $"Concluído às {DateTime.Now:HH:mm}.";
            GameNetHistory.Add(new GameNetRecord
            {
                WhenUtc = DateTime.UtcNow, Kind = "bufferbloat", Target = "Cloudflare", Grade = r.Grade, IdleMs = r.IdleMs, LoadedMs = r.LoadedMs,
                AvgMs = r.LoadedMs, Mbps = r.Mbps, LossPct = r.LoadedLossPct, DurationS = 15,
                Verdict = GameNetBufferbloat.GradeMeaning(r.Grade), Level = r.Grade is "A" or "B" ? 0 : r.Grade == "C" ? 1 : 2,
            });
            UpdateLive();
        }
        catch (OperationCanceledException)
        {
            _bStatus.Text = "Teste cancelado.";
            _bProgress.ScaleX = 0;
        }
        catch (Exception ex)
        {
            _bStatus.Foreground = Ui.Res("Bad");
            _bStatus.Text = ex.Message;
            _bProgress.ScaleX = 0;
        }
        finally
        {
            _bCts?.Dispose();
            _bCts = null;
            _bBtn.Content = "Testar outra vez";
        }
    }

    private Border BuildDnsCard()
    {
        var body = CardHeader("", "DNS mais rápido", "Não muda o ping no jogo, mas lojas, launchers e sites começam a abrir mais depressa.");
        body.Children.Add(_dnsNow);
        body.Children.Add(_dnsRows);
        _dnsStatus.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(_dnsStatus);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_dnsMeasure);
        buttons.Children.Add(_dnsUse);
        body.Children.Add(buttons);

        var restore = Ui.Btn("Repor o anterior", (_, _) => RestoreDns());
        restore.Margin = new Thickness(12, 0, 0, 0);
        restore.Padding = new Thickness(12, 6, 12, 6);
        var dock = new DockPanel();
        DockPanel.SetDock(restore, Dock.Right);
        dock.Children.Add(restore);
        var icon = new TextBlock { Text = "", FontFamily = new FontFamily(Icons), FontSize = 14, Foreground = Ui.Res("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        _dnsChangeText.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(_dnsChangeText);
        _dnsChangeBox.Child = dock;
        _dnsChangeBox.Background = Tint(Ui.Res("Accent"), 22);
        _dnsChangeBox.BorderBrush = Tint(Ui.Res("Accent"), 70);
        body.Children.Add(_dnsChangeBox);
        return Ui.Card(body);
    }

    private void UpdateDnsNow(IReadOnlyList<DnsCandidate> cands)
    {
        var cur = cands.FirstOrDefault(c => c.IsCurrent);
        _dnsNow.Text = cur == null ? "Agora: não consegui ler o DNS do Windows."
            : $"Agora: {cur.Name} ({cur.Primary}){(cur.IsProvider ? "" : " · " + cur.Note)}";
    }

    private void RenderDnsRows()
    {
        _dnsRows.Children.Clear();
        var list = _dnsCandidates.Count > 0 ? _dnsCandidates : GameNetDns.Providers.ToList();
        var ordered = _dnsScores.Count == 0 ? list
            : list.OrderBy(c => _dnsScores.TryGetValue(c.Primary, out var s) && s.Usable ? 0 : 1)
                  .ThenBy(c => _dnsScores.TryGetValue(c.Primary, out var s) ? s.MedianMs ?? double.MaxValue : double.MaxValue).ToList();
        double worst = _dnsScores.Values.Where(s => s.Usable).Select(s => s.MedianMs!.Value).DefaultIfEmpty(100).Max();
        string? best = _dnsScores.Values.Where(s => s.Usable && s.Server.IsProvider).OrderBy(s => s.MedianMs).FirstOrDefault()?.Server.Primary;

        foreach (var c in ordered)
        {
            _dnsScores.TryGetValue(c.Primary, out var score);
            bool picked = _dnsPick?.Primary == c.Primary;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star), MinWidth = 120 });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });

            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Ui.T(c.Name, "Body", 13, weight: picked ? FontWeights.SemiBold : FontWeights.Normal));
            if (c.IsCurrent) name.Children.Add(SmallPill("ATUAL", Ui.Res("Accent")));
            if (best == c.Primary) name.Children.Add(SmallPill("MAIS RÁPIDO", Ui.Res("Good")));
            row.Children.Add(name);

            Brush barBrush = score == null ? Ui.Res("Hover") : !score.Usable ? Ui.Res("Bad") : best == c.Primary ? Ui.Res("Good") : c.IsCurrent ? Ui.Res("Accent") : Ui.Res("LineStrong");
            var bar = Bar(score?.MedianMs is double m && score.Usable ? m / Math.Max(worst, 1) : score != null && !score.Usable ? 1 : 0, barBrush);
            bar.Margin = new Thickness(10, 0, 10, 0);
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);

            var val = new TextBlock
            {
                Text = score == null ? (_dnsBusy ? "…" : "—") : score.Usable ? $"{GameNetDiagnosis.F(score.MedianMs)} ms" : "falhou",
                FontFamily = Num, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Foreground = score == null ? Ui.Res("Faint") : score.Usable ? Ui.Res("Fg") : Ui.Res("Bad"),
            };
            Grid.SetColumn(val, 2);
            row.Children.Add(val);

            var item = new Border
            {
                Child = row, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                BorderBrush = picked ? Ui.Res("Accent") : Brushes.Transparent, Background = picked ? Ui.Res("AccentSoft") : Brushes.Transparent,
                ToolTip = $"{c.Primary}{(string.IsNullOrEmpty(c.Secondary) ? "" : " e " + c.Secondary)} · {c.Note}" +
                          (score != null ? $"\n{score.Total - score.Failures} de {score.Total} consultas responderam." : "") +
                          (c.IsProvider && !c.IsCurrent ? "\nClica para escolher." : ""),
            };
            if (c.IsProvider && !c.IsCurrent)
            {
                item.Cursor = System.Windows.Input.Cursors.Hand;
                item.MouseEnter += (_, _) => { if (_dnsPick?.Primary != c.Primary) item.Background = Ui.Res("Hover"); };
                item.MouseLeave += (_, _) => { if (_dnsPick?.Primary != c.Primary) item.Background = Brushes.Transparent; };
                item.MouseLeftButtonUp += (_, _) =>
                {
                    if (_dnsBusy) return;
                    _dnsPick = c;
                    RenderDnsRows();
                    UpdateDnsUse();
                };
            }
            _dnsRows.Children.Add(item);
        }
    }

    private void UpdateDnsUse()
    {
        if (_dnsPick == null || _dnsBusy) { _dnsUse.Visibility = Visibility.Collapsed; return; }
        _dnsUse.Visibility = Visibility.Visible;
        _dnsUse.Content = $"Usar {_dnsPick.Name}…";
    }

    private async void MeasureDns()
    {
        if (_dnsCts != null) { _dnsCts.Cancel(); return; }
        _dnsCts = new CancellationTokenSource();
        var ct = _dnsCts.Token;
        _dnsBusy = true;
        _dnsMeasure.Content = "Cancelar";
        _dnsUse.Visibility = Visibility.Collapsed;
        _dnsStatus.Foreground = Ui.Res("Muted");
        _dnsStatus.Text = "A perguntar a cada servidor por 8 sites de jogos (16 consultas cada)…";
        _dnsScores.Clear();
        _dnsPick = null;
        try
        {
            _dnsCandidates = await Task.Run(GameNetDns.Candidates, ct);
            UpdateDnsNow(_dnsCandidates);
            RenderDnsRows();
            var progress = new Progress<DnsScore>(s => { if (!ct.IsCancellationRequested) { _dnsScores[s.Server.Primary] = s; RenderDnsRows(); } });
            var cands = _dnsCandidates;
            var scores = await Task.Run(() => GameNetDns.BenchmarkAsync(cands, progress, ct), ct);
            foreach (var s in scores) _dnsScores[s.Server.Primary] = s;
            var suggestion = GameNetDns.Suggest(scores);
            _dnsPick = suggestion?.Server;
            var current = scores.FirstOrDefault(s => s.Server.IsCurrent);
            _dnsStatus.Foreground = suggestion != null ? Ui.Res("Accent") : Ui.Res("Good");
            _dnsStatus.Text = suggestion != null
                ? current?.Usable == true
                    ? $"{suggestion.Server.Name} responde em {GameNetDiagnosis.F(suggestion.MedianMs)} ms, contra {GameNetDiagnosis.F(current.MedianMs)} ms do teu atual. Vale a pena mudar."
                    : $"O teu DNS atual não respondeu bem; {suggestion.Server.Name} é o mais rápido para ti."
                : "O teu DNS atual já é rápido: não precisas de mudar. (Se quiseres outro, clica nele na lista.)";
        }
        catch (OperationCanceledException) { _dnsStatus.Text = "Medição cancelada."; }
        catch (Exception ex) { _dnsStatus.Foreground = Ui.Res("Bad"); _dnsStatus.Text = "Não consegui medir: " + ex.Message; }
        finally
        {
            _dnsCts?.Dispose();
            _dnsCts = null;
            _dnsBusy = false;
            _dnsMeasure.Content = "Medir outra vez";
            RenderDnsRows();
            UpdateDnsUse();
        }
    }

    private MessageBoxResult Ask(string text, string title)
    {
        var owner = Window.GetWindow(this);
        return owner != null
            ? MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
    }

    private async void ApplyDns()
    {
        var p = _dnsPick;
        if (p == null || _dnsBusy) return;
        _dnsBusy = true;
        try
        {
            var cur = await Task.Run(() => (Servers: GameNetDns.CurrentServers(out var name, out var isStatic, out _), Name: name, Static: isStatic));
            string now = cur.Servers.Count == 0 ? "desconhecido" : string.Join(", ", cur.Servers) + (cur.Static ? "" : " (automático)");
            string msg = $"Mudar o DNS do adaptador «{cur.Name}» para {p.Name}?\n\n" +
                         $"Novo: {p.Primary} e {p.Secondary}\nAgora: {now}\n\n" +
                         "Fica registado na Central Desfazer: podes repor o anterior quando quiseres, aqui ou lá.";
            if (Ask(msg, "HyperCore · Mudar DNS") != MessageBoxResult.Yes) return;

            _dnsUse.IsEnabled = _dnsMeasure.IsEnabled = false;
            _dnsStatus.Foreground = Ui.Res("Muted");
            _dnsStatus.Text = $"A mudar para {p.Name}…";
            var (ok, text) = await Task.Run(() => GameNetDns.Apply(p));
            _dnsStatus.Foreground = ok ? Ui.Res("Good") : Ui.Res("Bad");
            _dnsStatus.Text = text;
            if (ok)
            {
                _dnsCandidates = await Task.Run(GameNetDns.Candidates);
                _dnsScores.Clear();
                _dnsPick = null;
                UpdateDnsNow(_dnsCandidates);
            }
        }
        catch (Exception ex) { _dnsStatus.Foreground = Ui.Res("Bad"); _dnsStatus.Text = "Não consegui mudar o DNS: " + ex.Message; }
        finally
        {
            _dnsBusy = false;
            _dnsUse.IsEnabled = _dnsMeasure.IsEnabled = true;
            RenderDnsRows();
            UpdateDnsUse();
            RefreshDnsChange();
        }
    }

    private void RefreshDnsChange()
    {
        var e = GameNetDns.ActiveChange();
        if (e == null) { _dnsChangeBox.Visibility = Visibility.Collapsed; return; }
        _dnsChangeBox.Visibility = Visibility.Visible;
        _dnsChangeText.Text = $"{e.Title} em {e.WhenUtc.ToLocalTime():dd/MM} às {e.WhenUtc.ToLocalTime():HH:mm}.";
        _dnsChangeBox.ToolTip = e.Detail;
    }

    private async void RestoreDns()
    {
        var e = GameNetDns.ActiveChange();
        if (e == null || _dnsBusy) return;
        if (Ask($"Repor o DNS como estava antes do HyperCore?\n\n{e.Detail}", "HyperCore · Repor DNS") != MessageBoxResult.Yes) return;

        _dnsBusy = true;
        _dnsMeasure.IsEnabled = false;
        _dnsStatus.Foreground = Ui.Res("Muted");
        _dnsStatus.Text = "A repor o DNS anterior…";
        try
        {
            var (ok, text) = await Task.Run(() => ChangeJournal.TryUndo(e.Id));
            _dnsStatus.Foreground = ok ? Ui.Res("Good") : Ui.Res("Bad");
            _dnsStatus.Text = text;
            _dnsCandidates = await Task.Run(GameNetDns.Candidates);
            _dnsScores.Clear();
            UpdateDnsNow(_dnsCandidates);
        }
        catch (Exception ex) { _dnsStatus.Foreground = Ui.Res("Bad"); _dnsStatus.Text = "Não consegui repor: " + ex.Message; }
        finally
        {
            _dnsBusy = false;
            _dnsMeasure.IsEnabled = true;
            RenderDnsRows();
            UpdateDnsUse();
            RefreshDnsChange();
        }
    }

    // =====================================================================================================================
    // 3) Regiões
    // =====================================================================================================================

    private Border BuildRegionsCard()
    {
        var body = CardHeader("", "Ping por região (aproximado)",
            "Centros de dados onde muitos jogos online têm servidores. No jogo, escolhe a região com menos ping.");
        body.Children.Add(_regionRows);
        _regionStatus.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_regionStatus);
        _regionBtn.Margin = new Thickness(0, 12, 0, 0);
        _regionBtn.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(_regionBtn);
        return Ui.Card(body);
    }

    private void RenderRegionRows()
    {
        _regionRows.Children.Clear();
        var targets = GameNetProbe.Targets.Where(t => t.Approx).ToList();
        if (_regionResults.Count > 0)
            targets = targets.OrderBy(t => _regionResults.TryGetValue(t.Id, out var r) && r.Ms != null ? r.Ms.Value : double.MaxValue).ToList();
        double worst = _regionResults.Values.Where(r => r.Ms != null).Select(r => r.Ms!.Value).DefaultIfEmpty(100).Max();
        string? best = _regionResults.Where(r => r.Value.Ms != null).OrderBy(r => r.Value.Ms).Select(r => r.Key).FirstOrDefault();

        var grid = new UniformGrid { Columns = 2 };
        foreach (var t in targets)
        {
            bool has = _regionResults.TryGetValue(t.Id, out var r);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = Ui.T(t.Short, "Body", 13, weight: best == t.Id ? FontWeights.SemiBold : FontWeights.Normal);
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);

            int lvl = has && r.Ms is double ms ? GameNetDiagnosis.PingQuality(ms).Level : -1;
            if (has && r.Loss >= 1) lvl = Math.Max(lvl, 1);
            var bar = Bar(has && r.Ms is double m ? m / Math.Max(worst, 1) : 0, lvl < 0 ? Ui.Res("Hover") : best == t.Id ? Ui.Res("Good") : LevelBrush(lvl));
            bar.Margin = new Thickness(6, 0, 10, 0);
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);

            var val = new TextBlock
            {
                Text = !has ? (_regionCts != null ? "…" : "—") : r.Ms is double v ? $"{v:0} ms" : "sem resp.",
                FontFamily = Num, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Foreground = !has ? Ui.Res("Faint") : r.Ms == null ? Ui.Res("Bad") : Ui.Res("Fg"),
                ToolTip = has ? $"Mediana de 6 medições · {GameNetDiagnosis.Loss(r.Loss)}" : null,
            };
            Grid.SetColumn(val, 2);
            row.Children.Add(val);

            var watch = new Button
            {
                Content = new TextBlock { Text = "", FontFamily = new FontFamily(Icons), FontSize = 10 },
                Style = (Style)Ui.Theme["Btn"], Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(10, 0, 0, 0),
                ToolTip = $"Ver o ping até {t.Short} ao vivo", VerticalAlignment = VerticalAlignment.Center,
            };
            var target = t;
            watch.Click += (_, _) => WatchTarget(target);
            Grid.SetColumn(watch, 3);
            row.Children.Add(watch);

            grid.Children.Add(new Border { Child = row, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 2), ToolTip = t.Note });
        }
        _regionRows.Children.Add(grid);
    }

    private void WatchTarget(GameNetTarget t)
    {
        GameNetMonitor.SetTarget(t);
        if (!GameNetMonitor.IsRunning) { GameNetMonitor.Start(t); _tip.Text = TipText; }
        SyncTargetChips();
        UpdateStartButton();
        UpdateLive();
        if (Content is ScrollViewer sv) sv.ScrollToTop();
    }

    private async void MeasureRegions()
    {
        if (_regionCts != null) { _regionCts.Cancel(); return; }
        _regionCts = new CancellationTokenSource();
        var ct = _regionCts.Token;
        _regionResults.Clear();
        _regionBtn.Content = "Cancelar";
        _regionStatus.Foreground = Ui.Res("Muted");
        _regionStatus.Text = "A medir cada região (6 vezes)…";
        RenderRegionRows();
        try
        {
            var tasks = GameNetProbe.Targets.Where(t => t.Approx).Select(async (t, i) =>
            {
                await Task.Delay(i * 60, ct);
                var res = await Task.Run(async () =>
                {
                    var ok = new List<double>();
                    int lost = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var ms = await GameNetProbe.MeasureAsync(t, ct, 1500).ConfigureAwait(false);
                        if (ms is double d) ok.Add(d); else lost++;
                        await Task.Delay(150, ct).ConfigureAwait(false);
                    }
                    return (Ms: ok.Count > 0 ? NetStats.MedianOf(ok) : (double?)null, Loss: 100.0 * lost / 6);
                }, ct);
                _regionResults[t.Id] = res; // continua no UI (await sem ConfigureAwait)
                RenderRegionRows();
            });
            await Task.WhenAll(tasks);
            var best = _regionResults.Where(r => r.Value.Ms != null).OrderBy(r => r.Value.Ms).FirstOrDefault();
            _regionStatus.Text = best.Key != null
                ? $"Mais perto: {GameNetProbe.Find(best.Key).Short} ({best.Value.Ms:0} ms). Carrega em ▶ para seguir uma região ao vivo."
                : "Nenhuma região respondeu. Confirma a ligação à Internet.";
        }
        catch (OperationCanceledException) { _regionStatus.Text = "Medição cancelada."; }
        catch (Exception ex) { _regionStatus.Foreground = Ui.Res("Bad"); _regionStatus.Text = "Não consegui medir: " + ex.Message; }
        finally
        {
            _regionCts?.Dispose();
            _regionCts = null;
            _regionBtn.Content = "Medir outra vez";
            RenderRegionRows();
        }
    }

    // =====================================================================================================================
    // 4) Histórico
    // =====================================================================================================================

    private Border BuildHistoryCard()
    {
        var body = CardHeader("", "Histórico", "Sessões de ping (a partir de 30 s) e testes de bufferbloat, para comparares antes e depois de mudar algo.");
        body.Children.Add(_histRows);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_histMore);
        buttons.Children.Add(_histClear);
        body.Children.Add(buttons);
        return Ui.Card(body);
    }

    private void ClearHistory()
    {
        if (Ask("Apagar o histórico de testes de rede?", "HyperCore") == MessageBoxResult.Yes) GameNetHistory.Clear();
    }

    private void RenderHistory()
    {
        _histRows.Children.Clear();
        var all = GameNetHistory.All();
        _histClear.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _histMore.Visibility = all.Count > 6 ? Visibility.Visible : Visibility.Collapsed;
        _histMore.Content = _histAll ? "Mostrar menos" : $"Mostrar tudo ({all.Count})";
        if (all.Count == 0)
        {
            var empty = Ui.T("Ainda não há resultados. Mede pelo menos 30 s ou faz um teste de bufferbloat.", "MutedText");
            empty.Foreground = Ui.Res("Faint");
            _histRows.Children.Add(empty);
            return;
        }

        foreach (var r in _histAll ? all : all.Take(6))
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new TextBlock { Text = r.Kind == "bufferbloat" ? "" : "", FontFamily = new FontFamily(Icons), FontSize = 14, Foreground = LevelBrush(r.Level), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(icon);

            var when = Ui.T(When(r.WhenUtc), "MutedText", 12.5);
            when.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(when, 1);
            row.Children.Add(when);

            string summary = r.Kind == "bufferbloat"
                ? $"Bufferbloat {r.Grade} · +{GameNetDiagnosis.F(Math.Max(0, (r.LoadedMs ?? 0) - (r.IdleMs ?? 0)))} ms" + (r.Mbps is double mb ? $" · {mb:0} Mbps" : "")
                : $"{r.Target} · {GameNetDiagnosis.F(r.AvgMs)} ms · jitter {GameNetDiagnosis.F(r.JitterMs)} · {GameNetDiagnosis.Loss(r.LossPct)}";
            var sum = new TextBlock { Text = summary, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = summary };
            Grid.SetColumn(sum, 2);
            row.Children.Add(sum);

            string extra = r.Kind == "bufferbloat" ? "" : $"{Duration(r.DurationS)} · {r.Spikes} {(r.Spikes == 1 ? "pico" : "picos")} · ";
            var verdict = new TextBlock { Text = extra + r.Verdict, FontSize = 12.5, Foreground = Ui.Res("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(14, 0, 0, 0), ToolTip = extra + r.Verdict };
            Grid.SetColumn(verdict, 3);
            row.Children.Add(verdict);

            _histRows.Children.Add(new Border { Child = row, Padding = new Thickness(4, 8, 4, 8), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Ui.Res("Line") });
        }
    }

    private static string When(DateTime utc)
    {
        var t = utc.ToLocalTime();
        if (t.Date == DateTime.Today) return $"Hoje {t:HH:mm}";
        if (t.Date == DateTime.Today.AddDays(-1)) return $"Ontem {t:HH:mm}";
        return t.ToString("dd/MM HH:mm", Pt);
    }

    private static string Duration(int s) => s < 90 ? $"{s} s" : s < 5400 ? $"{s / 60} min" : $"{s / 3600} h {s % 3600 / 60:00}";

    // =====================================================================================================================
    // Pequenos construtores
    // =====================================================================================================================

    private static Brush LevelBrush(int level) => level switch { 0 => Ui.Res("Good"), 1 => Ui.Res("Warn"), 2 => Ui.Res("Bad"), _ => Ui.Res("Faint") };

    private static Brush Tint(Brush b, byte alpha)
    {
        if (b is not SolidColorBrush s) return b;
        var t = new SolidColorBrush(Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B));
        t.Freeze();
        return t;
    }

    private static Border SmallPill(string text, Brush color) => new()
    {
        CornerRadius = new CornerRadius(99), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = Tint(color, 40),
        Child = new TextBlock { Text = text, FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = color },
    };

    private static Grid Bar(double fraction, Brush fill)
    {
        double f = fraction <= 0 ? 0 : Math.Clamp(fraction, 0.03, 1);
        var g = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(3), Background = Ui.Res("Hover") };
        Grid.SetColumnSpan(track, 2);
        g.Children.Add(track);
        if (f > 0) g.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = fill });
        return g;
    }
}
