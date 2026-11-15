using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FrameForge.Core;
using FrameForge.Tuning;

namespace FrameForge.Pages;

/// <summary>
/// "Scan e afinação automática": escolher o perfil → acompanhar o scan → relatório simples.
/// A página só fala com o <see cref="ScanService"/> (eventos): se o scan foi iniciado noutro sítio ou a página abriu a meio, mostra na mesma.
/// </summary>
public sealed class ScanPage : UserControl
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    private readonly StackPanel _recovery = new();
    private readonly StackPanel _before = new();
    private readonly StackPanel _during = new();
    private readonly StackPanel _after = new();

    // ---- antes
    private ScanProfile _profile = ScanProfile.Seguro;
    private readonly Dictionary<ScanProfile, Border> _profileCards = new();
    private readonly Dictionary<ScanProfile, TextBlock> _durations = new();
    private readonly TextBlock _extremeWarn = new();
    private readonly StackPanel _preflight = new();
    private readonly TextBlock _ocNote = new();
    private readonly StackPanel _lastSummary = new();
    private readonly TextBlock _stopped = new();
    private readonly Button _start;

    // ---- durante
    private readonly TextBlock _state = new();
    private readonly TextBlock _passo = new();
    private readonly TextBlock _pctText = new();
    private readonly ColumnDefinition _barDone = new() { Width = new GridLength(0, GridUnitType.Star) };
    private readonly ColumnDefinition _barLeft = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly StackPanel _phases = new();
    private readonly UniformGrid _live = new() { Columns = 2 };
    private readonly StackPanel _log = new();
    private readonly Button _stop;

    private readonly List<string> _lines = new();
    private ScanProgress? _pending;
    private ScanProgress? _rendered;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>Relatório mostrado agora (null = vista "antes").</summary>
    private ScanReport? _shown;

    public ScanPage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = new Thickness(32, 24, 32, 40) };
        root.Children.Add(Ui.T("Scan e afinação automática", "H1"));
        var sub = Ui.T("Mede o teu PC a fundo, afina a gráfica sozinho e diz-te o que vale a pena mudar.", "MutedText", 14);
        sub.Margin = new Thickness(0, 0, 0, 18);
        root.Children.Add(sub);
        root.Children.Add(_recovery);
        root.Children.Add(_before);
        root.Children.Add(_during);
        root.Children.Add(_after);

        _start = Ui.Btn("Iniciar scan", Start_Click, "BtnAccent");
        _stop = Ui.Btn("Parar", Stop_Click, "BtnDanger");
        BuildBefore();
        BuildDuring();

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root };

        // Os eventos chegam fora do thread da interface: guarda o último e desenha no temporizador (no máximo ~2,5×/s)
        var svc = ScanService.Instance;
        svc.ProgressChanged += p =>
        {
            if (p.Linha is string l)
                lock (_lines)
                {
                    _lines.Add($"{DateTime.Now:HH:mm:ss}  {l}");
                    if (_lines.Count > 200) _lines.RemoveRange(0, _lines.Count - 200);
                }
            _pending = p;
        };
        svc.RunningChanged += running => Dispatcher.BeginInvoke(() =>
        {
            if (running) { lock (_lines) _lines.Clear(); _rendered = null; }
            ShowMode();
        });
        svc.Completed += r => Dispatcher.BeginInvoke(() => OnCompleted(r));

        _tick.Tick += (_, _) =>
        {
            if (ScanService.Instance.Running && _pending is ScanProgress p && !ReferenceEquals(p, _rendered)) RenderProgress(p);
        };
        Loaded += async (_, _) =>
        {
            try
            {
                _tick.Start();
                ShowMode();
                await RefreshBeforeAsync();
            }
            catch { /* async void: um erro a desenhar não pode ir parar ao tratador global (que pararia um scan a correr) */ }
        };
        Unloaded += (_, _) => _tick.Stop();
        ShowMode();
    }

    // ================================================================== estado da página

    private void ShowMode()
    {
        bool running = ScanService.Instance.Running;
        _during.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _after.Visibility = !running && _shown != null ? Visibility.Visible : Visibility.Collapsed;
        _before.Visibility = !running && _shown == null ? Visibility.Visible : Visibility.Collapsed;
        BuildRecovery();
        if (running)
        {
            _stop.IsEnabled = true;
            _stop.Content = "Parar (a gráfica volta aos valores de fábrica)";
            if ((_pending ?? ScanService.Instance.Current) is ScanProgress p) RenderProgress(p);
        }
    }

    private void OnCompleted(ScanReport r)
    {
        // Parou antes de medir alguma coisa: volta ao início com o motivo
        if (r.Resultado != ScanOutcome.Completo && r.Cpu == null && r.Gpu == null)
        {
            _shown = null;
            _stopped.Text = "O scan parou: " + (r.Motivo ?? "sem motivo indicado") + " A gráfica está nos valores de fábrica.";
            _stopped.Visibility = Visibility.Visible;
        }
        else
        {
            _stopped.Visibility = Visibility.Collapsed;
            _shown = r;
            BuildAfter(r);
        }
        ShowMode();
        _ = RefreshBeforeAsync();
    }

    private void BuildRecovery()
    {
        _recovery.Children.Clear();
        var rec = ScanService.Instance.PendingRecovery;
        if (rec == null || ScanService.Instance.Running) return;
        var sp = new StackPanel();
        sp.Children.Add(Ui.T("Scan interrompido", "H2"));
        sp.Children.Add(Ui.T(rec.Mensagem, "Body"));
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (rec.PodeRetomar)
        {
            var resume = Ui.Btn("Retomar scan", Resume_Click, "BtnAccent");
            resume.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(resume);
        }
        row.Children.Add(Ui.Btn("Esquecer", (_, _) => { ScanService.Instance.DismissRecovery(); BuildRecovery(); _ = RefreshBeforeAsync(); }));
        sp.Children.Add(row);
        _recovery.Children.Add(Ui.Card(sp, Ui.Res("Warn")));
    }

    // ================================================================== antes: escolher o perfil

    private void BuildBefore()
    {
        _stopped.Style = (Style)Ui.Theme["Body"];
        _stopped.Foreground = Ui.Res("Warn");
        _stopped.Margin = new Thickness(0, 0, 0, 12);
        _stopped.Visibility = Visibility.Collapsed;
        _before.Children.Add(_stopped);

        _before.Children.Add(Ui.T("1. Escolhe o perfil", "H2", 17));
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var lim in TuningLimits.All)
        {
            var sp = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var risk = RiskPill(lim.Risco);
            DockPanel.SetDock(risk, Dock.Right);
            head.Children.Add(risk);
            head.Children.Add(Ui.T(lim.Nome, "H2", 19));
            sp.Children.Add(head);
            var desc = Ui.T(lim.Descricao, "MutedText", 13);
            desc.MinHeight = 38;
            sp.Children.Add(desc);
            var dur = Ui.T("Duração: a calcular…", "Body", 13, weight: FontWeights.SemiBold);
            dur.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(dur);
            _durations[lim.Profile] = dur;

            var card = Ui.Card(sp);
            card.Margin = new Thickness(0, 0, 12, 12);
            card.Cursor = Cursors.Hand;
            var p = lim.Profile;
            card.MouseLeftButtonUp += (_, _) => SelectProfile(p);
            card.MouseEnter += (_, _) => { if (_profile != p) card.Background = Ui.Res("Hover"); };
            card.MouseLeave += (_, _) => { if (_profile != p) card.Background = Ui.Res("Card"); };
            _profileCards[p] = card;
            grid.Children.Add(card);
        }
        _before.Children.Add(grid);

        _extremeWarn.Style = (Style)Ui.Theme["Body"];
        _extremeWarn.Foreground = Ui.Res("Warn");
        _extremeWarn.Text = "⚠ " + TuningLimits.Extremo.Aviso;
        _extremeWarn.Margin = new Thickness(0, 0, 12, 10);
        _before.Children.Add(_extremeWarn);

        _ocNote.Style = (Style)Ui.Theme["MutedText"];
        _ocNote.Margin = new Thickness(0, 0, 12, 10);
        _ocNote.Visibility = Visibility.Collapsed;
        _before.Children.Add(_ocNote);

        var go = new StackPanel();
        go.Children.Add(Ui.T("2. Prepara o PC", "H2", 17));
        _preflight.Margin = new Thickness(0, 0, 0, 14);
        _preflight.Children.Add(Ui.T("Fecha jogos e programas pesados. O PC vai aquecer e as ventoinhas vão subir — é normal.", "Body"));
        go.Children.Add(_preflight);
        _start.FontSize = 16;
        _start.Padding = new Thickness(30, 14, 30, 14);
        _start.HorizontalAlignment = HorizontalAlignment.Left;
        go.Children.Add(_start);
        var safe = Ui.T("Podes parar quando quiseres. No fim (ou ao parar) a gráfica volta sempre aos valores de fábrica.", "MutedText", 12);
        safe.Margin = new Thickness(0, 10, 0, 0);
        go.Children.Add(safe);
        _before.Children.Add(Ui.Card(go, Ui.Res("Accent")));

        _lastSummary.Margin = new Thickness(0, 6, 0, 0);
        _before.Children.Add(_lastSummary);
        SelectProfile(ScanProfile.Seguro);
    }

    private static Border RiskPill(string risco)
    {
        var (bg, fg) = risco switch
        {
            "Baixo" => (Ui.Res("Good"), Brushes.Black),
            "Médio" => (Ui.Res("Warn"), Brushes.Black),
            _ => (Ui.Res("Bad"), Brushes.White),
        };
        var p = Ui.Pill("RISCO " + risco.ToUpperInvariant(), bg, fg);
        p.Margin = new Thickness(6, 0, 0, 0);
        p.VerticalAlignment = VerticalAlignment.Center;
        return p;
    }

    private void SelectProfile(ScanProfile p)
    {
        _profile = p;
        foreach (var (k, card) in _profileCards)
        {
            bool sel = k == p;
            card.BorderBrush = sel ? Ui.Res("Accent") : Ui.Res("Line");
            card.BorderThickness = new Thickness(sel ? 2 : 1);
            card.Background = sel ? Ui.Res("AccentSoft") : Ui.Res("Card");
        }
        _extremeWarn.Visibility = p == ScanProfile.Extremo ? Visibility.Visible : Visibility.Collapsed;
        _start.Content = $"Iniciar scan {TuningLimits.For(p).Nome}   ›";
    }

    /// <summary>Durações, pré-requisitos e resumo do último scan (lê o hardware fora do thread da interface).</summary>
    private async Task RefreshBeforeAsync()
    {
        BuildLastSummary();
        if (ScanService.Instance.Running) return;
        try
        {
            var (plans, pre) = await Task.Run(() =>
                (TuningLimits.All.Select(l => ScanService.Plan(l.Profile)).ToList(), ScanService.Preflight()));
            foreach (var plan in plans)
                if (_durations.TryGetValue(plan.Perfil, out var t)) t.Text = "Duração: " + plan.DuracaoTexto;
            var skipped = plans[0].Fases.FirstOrDefault(f => f.Fase == ScanPhase.GpuNucleo && f.Estado == PhaseState.Saltado);
            _ocNote.Text = skipped?.Nota is string n ? "Overclock automático indisponível: " + n + " O resto do scan corre na mesma." : "";
            _ocNote.Visibility = _ocNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowPreflight(pre);
        }
        catch { /* o botão volta a verificar ao carregar */ }
    }

    private void ShowPreflight(ScanPreflight pre)
    {
        _preflight.Children.Clear();
        foreach (var b in pre.Bloqueios)
        {
            var t = Ui.T("✖ " + b, "Body", color: Ui.Res("Bad"), weight: FontWeights.SemiBold);
            t.Margin = new Thickness(0, 0, 0, 4);
            _preflight.Children.Add(t);
        }
        foreach (var w in pre.Avisos)
        {
            var t = Ui.T("• " + w, "Body");
            t.Margin = new Thickness(0, 0, 0, 4);
            _preflight.Children.Add(t);
        }
    }

    private void BuildLastSummary()
    {
        _lastSummary.Children.Clear();
        var last = ScanService.Instance.Last;
        if (last == null) return;
        var sp = new StackPanel();
        sp.Children.Add(Ui.T($"ÚLTIMO SCAN · {last.Data.ToString("d 'de' MMMM, HH:mm", Pt)} · {TuningLimits.For(last.Perfil).Nome}", "Label", 10.5, Ui.Res("Accent2")));
        var line = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var open = Ui.Btn("Ver relatório", (_, _) => { _shown = last; BuildAfter(last); ShowMode(); });
        open.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(open, Dock.Right);
        line.Children.Add(open);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        if (last.Nota is int nota)
        {
            var n = Ui.T(nota.ToString(Pt), "Big", 30, ScoreBrush(nota), FontWeights.Bold);
            n.Margin = new Thickness(0, 0, 14, 0);
            n.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(n);
        }
        var txt = Ui.T(last.Resumo.Length > 0 ? last.Resumo : "Scan concluído.", "Body", 14.5);
        txt.VerticalAlignment = VerticalAlignment.Center;
        txt.MaxWidth = 640;
        left.Children.Add(txt);
        line.Children.Add(left);
        sp.Children.Add(line);
        _lastSummary.Children.Add(Ui.Card(sp));
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        _start.IsEnabled = false;
        try
        {
            var pre = await Task.Run(ScanService.Preflight);
            ShowPreflight(pre);
            if (!pre.PodeComecar) return;
            if (_profile == ScanProfile.Extremo &&
                MessageBox.Show(TuningLimits.Extremo.Aviso + "\n\nComeçar o scan Extremo?", "Scan Extremo",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _stopped.Visibility = Visibility.Collapsed;
            _shown = null;
            await ScanService.Instance.RunAsync(_profile, null, CancellationToken.None); // o relatório chega pelo evento Completed
        }
        catch (InvalidOperationException ex) { AppServices.Toast(ex.Message); }
        catch (Exception ex) { AppServices.Toast("Não consegui iniciar o scan: " + ex.Message); } // async void: nada pode escapar
        finally { _start.IsEnabled = true; }
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pre = await Task.Run(ScanService.Preflight);
            if (!pre.PodeComecar) { _shown = null; ShowMode(); ShowPreflight(pre); return; }
            if (ScanService.Instance.PendingRecovery is RecoveryInfo rec) SelectProfile(rec.Perfil); // limites do painel ao vivo
            _shown = null;
            await ScanService.Instance.ResumeAsync(null, CancellationToken.None);
        }
        catch (InvalidOperationException ex) { AppServices.Toast(ex.Message); ScanService.Instance.DismissRecovery(); BuildRecovery(); }
        catch (Exception ex) { AppServices.Toast("Não consegui retomar o scan: " + ex.Message); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _stop.IsEnabled = false;
        _stop.Content = "A parar… a repor a gráfica";
        ScanService.Instance.Cancel();
    }

    // ================================================================== durante

    private void BuildDuring()
    {
        // Estado + barra de progresso
        var top = new StackPanel();
        _state.Style = (Style)Ui.Theme["H2"];
        _state.FontSize = 19;
        top.Children.Add(_state);
        var bar = new Border { Height = 12, CornerRadius = new CornerRadius(6), Background = Ui.Res("Hover"), Margin = new Thickness(0, 6, 0, 8), ClipToBounds = true };
        var barGrid = new Grid();
        barGrid.ColumnDefinitions.Add(_barDone);
        barGrid.ColumnDefinitions.Add(_barLeft);
        barGrid.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = (Brush)Ui.Theme["AccentGradient"] });
        bar.Child = barGrid;
        top.Children.Add(bar);
        _pctText.Style = (Style)Ui.Theme["MutedText"];
        top.Children.Add(_pctText);
        _passo.Style = (Style)Ui.Theme["Body"];
        _passo.FontSize = 15;
        _passo.FontWeight = FontWeights.SemiBold;
        _passo.Foreground = Ui.Res("Accent2");
        _passo.Margin = new Thickness(0, 8, 0, 0);
        top.Children.Add(_passo);
        var stopRow = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(_stop, Dock.Left);
        stopRow.Children.Add(_stop);
        var hint = Ui.T("Não uses o PC durante o teste: mexer noutras coisas estraga as medições.", "MutedText", 12);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(14, 0, 0, 0);
        stopRow.Children.Add(hint);
        top.Children.Add(stopRow);
        _during.Children.Add(Ui.Card(top, Ui.Res("Accent")));

        // Fases | ao vivo
        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ph = new StackPanel();
        ph.Children.Add(Ui.T("Fases", "H2"));
        ph.Children.Add(_phases);
        var phCard = Ui.Card(ph);
        cols.Children.Add(phCard);
        var lv = new StackPanel();
        lv.Children.Add(Ui.T("Ao vivo", "H2"));
        lv.Children.Add(_live);
        var lvCard = Ui.Card(lv);
        Grid.SetColumn(lvCard, 2);
        cols.Children.Add(lvCard);
        _during.Children.Add(cols);

        // Registo curto
        var lg = new StackPanel();
        lg.Children.Add(Ui.T("O que aconteceu", "H2"));
        lg.Children.Add(_log);
        _during.Children.Add(Ui.Card(lg));
    }

    private void RenderProgress(ScanProgress p)
    {
        _rendered = p;
        _state.Text = p.Estado.Length > 0 ? p.Estado : p.FaseTitulo;
        double pct = Math.Clamp(p.Percent, 0, 100);
        _barDone.Width = new GridLength(Math.Max(pct, 0.001), GridUnitType.Star);
        _barLeft.Width = new GridLength(Math.Max(100 - pct, 0.001), GridUnitType.Star);
        string left = p.Restante.TotalSeconds < 60 ? "falta menos de 1 min" : $"faltam ~{Math.Ceiling(p.Restante.TotalMinutes):0} min";
        _pctText.Text = $"{pct:0}%  ·  {left}  ·  já passaram {(int)p.Decorrido.TotalMinutes}:{p.Decorrido.Seconds:00}" +
                        (p.FaseTotal > 0 && p.FaseIndice > 0 ? $"  ·  fase {p.FaseIndice} de {p.FaseTotal}" : "");
        _passo.Text = p.PassoOc ?? "";
        _passo.Visibility = string.IsNullOrEmpty(p.PassoOc) ? Visibility.Collapsed : Visibility.Visible;

        _phases.Children.Clear();
        foreach (var f in p.Fases) _phases.Children.Add(PhaseRow(f));

        RenderLive(p.AoVivo);

        _log.Children.Clear();
        List<string> lines;
        lock (_lines) lines = _lines.TakeLast(12).ToList();
        if (lines.Count == 0) _log.Children.Add(Ui.T("A começar…", "MutedText"));
        foreach (var l in lines)
        {
            var t = Ui.T(l, "Body", 12.5);
            t.FontFamily = (FontFamily)Ui.Theme["Mono"];
            t.Foreground = l.Contains('✖') || l.Contains("falhou") ? Ui.Res("Warn") : Ui.Res("Muted");
            t.Margin = new Thickness(0, 1, 0, 1);
            _log.Children.Add(t);
        }
    }

    private static UIElement PhaseRow(PhaseStatus f)
    {
        var (icon, color) = f.Estado switch
        {
            PhaseState.Feito => ("✓", Ui.Res("Good")),
            PhaseState.ACorrer => ("●", Ui.Res("Accent")),
            PhaseState.Falhou => ("✖", Ui.Res("Bad")),
            PhaseState.Saltado => ("–", Ui.Res("Faint")),
            _ => ("○", Ui.Res("Faint")),
        };
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var i = Ui.T(icon, "Body", 14, color, FontWeights.Bold);
        i.Width = 24;
        DockPanel.SetDock(i, Dock.Left);
        row.Children.Add(i);
        string right = f.Estado switch
        {
            PhaseState.ACorrer => "a correr",
            PhaseState.Saltado => "saltado",
            PhaseState.Falhou => "falhou",
            PhaseState.Feito => f.SegundosReais is double s ? Dur(s) : "feito",
            _ => f.SegundosEstimados > 0 ? "~" + Dur(f.SegundosEstimados) : "",
        };
        var r = Ui.T(right, "MutedText", 12);
        r.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(r, Dock.Right);
        row.Children.Add(r);
        var title = Ui.T(f.Titulo, "Body", 13.5, f.Estado is PhaseState.Saltado or PhaseState.PorFazer ? Ui.Res("Muted") : null,
                         f.Estado == PhaseState.ACorrer ? FontWeights.SemiBold : null);
        row.Children.Add(title);
        if (!string.IsNullOrEmpty(f.Nota)) row.ToolTip = f.Nota;
        return row;
    }

    private static string Dur(double s) => s < 60 ? $"{s:0} s" : $"{Math.Round(s / 60):0} min";

    private void RenderLive(LiveValues? v)
    {
        _live.Children.Clear();
        void Add(string label, string value, Brush? color = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 4, 12, 8) };
            sp.Children.Add(Ui.T(label.ToUpperInvariant(), "Label"));
            var t = Ui.T(value, "Big", 20, color);
            sp.Children.Add(t);
            _live.Children.Add(sp);
        }
        static string N(double? x, string unit) => x is double d && d > 0 ? $"{d:0} {unit}".TrimEnd() : "—";
        Brush? Hot(double? t, double lim) => t is double d && d >= lim ? Ui.Res("Bad") : t is double d2 && d2 >= lim - 8 ? Ui.Res("Warn") : null;
        // Limites do scan que está a correr (a página pode ter aberto a meio ou o perfil escolhido ser outro)
        var lim = TuningLimits.For(ScanService.Instance.RunningProfile ?? _profile);

        Add("Temp. CPU", N(v?.CpuTemp, "°C"), Hot(v?.CpuTemp, lim.CpuTempMax));
        // Núcleo e hotspot têm limites diferentes: a cor é a do que estiver mais perto do seu limite
        static int Level(double? t, double lim) => t is double d && d >= lim ? 2 : t is double d2 && d2 >= lim - 8 ? 1 : 0;
        int gl = Math.Max(Level(v?.GpuTemp, lim.GpuTempMax), Level(v?.GpuHotspot, lim.GpuHotspotMax));
        Add("Temp. gráfica", N(v?.GpuTemp, "°C"), gl == 2 ? Ui.Res("Bad") : gl == 1 ? Ui.Res("Warn") : null);
        if (v?.GpuHotspot is double hs && hs > 0) Add("Hotspot da gráfica", $"{hs:0} °C", Hot(hs, lim.GpuHotspotMax));
        if (v?.GpuMemTemp is double mt && mt > 0) Add("Temp. memória gráfica", $"{mt:0} °C", Hot(mt, lim.GpuMemTempMax));
        Add("Consumo gráfica", v?.GpuPowerW is double w && w > 0 ? $"{w:0} W" + (v.GpuPowerLimitW is double pl && pl > 0 ? $" / {pl:0}" : "") : "—");
        Add("Consumo CPU", N(v?.CpuPowerW, "W"));
        Add("Clock gráfica", N(v?.GpuClockMHz, "MHz"));
        Add("Memória gráfica", N(v?.GpuMemClockMHz, "MHz"));
        Add("Clock CPU", N(v?.CpuClockMHz, "MHz"));
        Add("FPS do teste", N(v?.Fps, ""));
        if (v != null && (v.GpuPowerCapped || v.GpuThermalCapped))
        {
            var t = Ui.T(v.GpuThermalCapped ? "A gráfica está a travar por temperatura" : "A gráfica está no limite de energia",
                         "MutedText", 12, Ui.Res("Warn"));
            t.Margin = new Thickness(0, 2, 0, 0);
            _live.Children.Add(t);
        }
    }

    // ================================================================== depois: relatório simples

    private void BuildAfter(ScanReport r)
    {
        _after.Children.Clear();
        if (r.Resultado != ScanOutcome.Completo)
            _after.Children.Add(Ui.Card(Ui.T($"O scan parou antes do fim: {r.Motivo ?? "sem motivo indicado"} Mostro o que deu para medir.", "Body"), Ui.Res("Warn")));

        // Nota + resumo + ações
        var hero = new Border
        {
            Background = Ui.Res("AccentSoft"), BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(26, 22, 26, 22), Margin = new Thickness(0, 0, 0, 8),
        };
        var hg = new Grid();
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var score = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 26, 0), MinWidth = 90 };
        score.Children.Add(Ui.T(r.Nota is int n ? n.ToString(Pt) : "—", "Big", 52, r.Nota is int n2 ? ScoreBrush(n2) : Ui.Res("Muted"), FontWeights.Bold));
        score.Children.Add(Ui.T("NOTA / 100", "Label"));
        hg.Children.Add(score);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Ui.T($"RELATÓRIO · {r.Data.ToString("d 'de' MMMM yyyy, HH:mm", Pt)} · PERFIL {TuningLimits.For(r.Perfil).Nome.ToUpperInvariant()}", "Label", 10.5, Ui.Res("Accent2")));
        info.Children.Add(Ui.T(r.Resumo.Length > 0 ? r.Resumo : "Scan concluído.", "H2", 19));
        var inv = r.Inventario;
        info.Children.Add(Ui.T($"{Short(inv.Cpu, true)} · {Short(inv.Gpu, false)} · {inv.RamGB:0} GB {inv.RamTipo}", "MutedText"));
        var acts = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var export = Ui.Btn("Exportar relatório", (_, _) => Export(r), "BtnAccent");
        export.Margin = new Thickness(0, 0, 8, 0);
        acts.Children.Add(export);
        acts.Children.Add(Ui.Btn("Fazer outro scan", (_, _) => { _shown = null; ShowMode(); _ = RefreshBeforeAsync(); }));
        info.Children.Add(acts);
        Grid.SetColumn(info, 1);
        hg.Children.Add(info);
        hero.Child = hg;
        _after.Children.Add(hero);

        Section("O que está a segurar o teu PC");
        _after.Children.Add(LimiterBlock(r));

        Section("Ganhaste");
        _after.Children.Add(OcBlock(r));

        var cfg = r.Configuracoes.OrderByDescending(c => c.Severidade).ToList();
        Section(cfg.Count > 0 ? $"Configurações a corrigir ({cfg.Count})" : "Configurações a corrigir");
        ConfigBlock(cfg);
        BiosBlock(r);

        Section("Upgrades que valem a pena");
        _after.Children.Add(UpgradesBlock(r));
    }

    private void Section(string title)
    {
        var t = Ui.T(title, "H2", 18);
        t.Margin = new Thickness(0, 18, 0, 8);
        _after.Children.Add(t);
    }

    private static Brush ScoreBrush(int n) => n >= 80 ? Ui.Res("Good") : n >= 60 ? Ui.Res("Warn") : Ui.Res("Bad");

    private static string Short(string? name, bool cpu)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "?") return cpu ? "CPU" : "Gráfica";
        try { return cpu ? ResetExplode.GameCatalog.ShortCpuName(name) : ResetExplode.GameCatalog.ShortGpuName(name); }
        catch { return name; }
    }

    private static void Export(ScanReport r)
    {
        try
        {
            string path = ScanReportHtml.Save(r);
            Ui.Open(path);
            AppServices.Toast("Relatório guardado em " + path);
        }
        catch (Exception ex) { AppServices.Toast("Não consegui exportar o relatório: " + ex.Message); }
    }

    private static Border LimiterBlock(ScanReport r)
    {
        var sp = new StackPanel();
        var frases = r.Segurar.Select(f => f.Titulo).Where(t => t.Length > 0).Take(3).ToList();
        if (frases.Count == 0)
            sp.Children.Add(Ui.T("Não deu para medir quem limita (o scan parou cedo ou faltaram medições).", "Body", color: Ui.Res("Muted")));
        foreach (var f in frases)
        {
            var t = Ui.T(f, "Body", 15);
            t.Margin = new Thickness(0, 0, 0, 6);
            sp.Children.Add(t);
        }
        var lim = r.Limitador;
        if (lim != null)
        {
            var nums = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            void Num(string label, double? v)
            {
                if (v is not > 0) return;
                var p = Ui.Pill($"{label}: ~{v:0} FPS");
                p.Margin = new Thickness(0, 0, 8, 4);
                nums.Children.Add(p);
            }
            Num("Processador aguenta", lim.TetoCpuFps);
            Num($"Gráfica em {lim.Resolucao}", lim.TetoGpuMonitorFps ?? lim.TetoGpu1080Fps);
            if (lim.MonitorHz > 0) { var hz = Ui.Pill($"Monitor: {lim.MonitorHz} Hz"); hz.Margin = new Thickness(0, 0, 8, 4); nums.Children.Add(hz); }
            if (nums.Children.Count > 0) sp.Children.Add(nums);
            if (lim.Metodo.Length > 0)
            {
                var how = Ui.T("Como cheguei aqui?", "MutedText", 12);
                how.TextDecorations = TextDecorations.Underline;
                how.Cursor = Cursors.Help;
                how.Margin = new Thickness(0, 6, 0, 0);
                how.HorizontalAlignment = HorizontalAlignment.Left;
                how.ToolTip = new ToolTip { Content = new TextBlock { Text = lim.Metodo, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 } };
                ToolTipService.SetShowDuration(how, 60000);
                sp.Children.Add(how);
            }
        }
        return Ui.Card(sp, Ui.Res("Accent2"));
    }

    // ---------------- Ganhaste (overclock da gráfica)

    private Border OcBlock(ScanReport r)
    {
        var sp = new StackPanel();
        var oc = r.Oc;
        var prof = ScanService.FoundProfile(r);
        if (oc == null)
        {
            sp.Children.Add(Ui.T("A afinação da gráfica não chegou a correr.", "Body", color: Ui.Res("Muted")));
            return Ui.Card(sp);
        }
        if (!oc.Suportado)
        {
            sp.Children.Add(Ui.T(oc.Explicacao ?? "A tua placa não permite overclock por software aqui.", "Body"));
            var b = Ui.Btn("Abrir Overclock da gráfica (guia)", (_, _) => AppServices.Navigate("FF:oc"));
            b.Margin = new Thickness(0, 12, 0, 0);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(b);
            return Ui.Card(sp);
        }
        if (!oc.Estavel || prof == null)
        {
            sp.Children.Add(Ui.T(prof == null && oc.Estavel
                ? "O perfil do overclock encontrado já não existe (foi apagado)."
                : "Não encontrei um overclock estável: a tua gráfica fica como vem de fábrica.", "Body"));
            foreach (var n in oc.Notas.Take(3)) sp.Children.Add(Ui.T("• " + n, "MutedText"));
            return Ui.Card(sp);
        }

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.T(oc.GanhoPct is double g ? $"{g:+0.#;-0.#;0}% FPS" : "Overclock estável", "Big", 34, Ui.Res("Good"), FontWeights.Bold));
        if (oc.FpsStockQuente is double a && oc.FpsFinal is double f)
        {
            var ab = Ui.T($"{a:0} → {f:0} FPS no teste, medido com a placa quente", "MutedText", 13);
            ab.VerticalAlignment = VerticalAlignment.Center;
            ab.Margin = new Thickness(14, 0, 0, 0);
            head.Children.Add(ab);
        }
        sp.Children.Add(head);
        sp.Children.Add(Ui.T($"Núcleo +{oc.Nucleo} MHz" + (oc.Memoria > 0 ? $" · Memória +{oc.Memoria}" : "") +
                             (oc.PotenciaPct != 100 ? $" · Energia {oc.PotenciaPct}%" : "") +
                             $" · validado {oc.ValidacaoMinutos} min · guardado como \"{prof.Name}\"", "Body"));
        if (AppServices.Settings.UnstableOcProfiles.Contains(prof.Name))
            sp.Children.Add(Ui.T("⚠ Este perfil causou depois um crash do driver e ficou marcado como instável.", "Body", color: Ui.Res("Bad")));
        else
        {
            var now = Ui.T("Agora a gráfica está nos valores de fábrica. Carrega em Aplicar para usares o overclock.", "MutedText", 12.5);
            now.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(now);
        }

        var status = Ui.T("", "MutedText", 12.5);
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        Button? apply = null;
        apply = Ui.Btn("Aplicar agora", async (_, _) =>
        {
            apply!.IsEnabled = false;
            status.Text = "A aplicar…";
            try
            {
                var log = await ScanService.ApplyFoundAsync(r);
                bool bad = log.Any(l => l.StartsWith("✖", StringComparison.Ordinal));
                status.Text = bad ? string.Join("  ", log.Where(l => l.StartsWith("✖", StringComparison.Ordinal))) : "Aplicado. Volta ao normal ao reiniciar ou com Stock na página Overclock.";
                status.Foreground = bad ? Ui.Res("Bad") : Ui.Res("Good");
            }
            catch (Exception ex) { status.Text = "Não consegui aplicar: " + ex.Message; status.Foreground = Ui.Res("Bad"); }
            finally { apply.IsEnabled = true; }
        }, "BtnAccent");
        apply.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(apply);
        row.Children.Add(Ui.Btn("Ver na página Overclock", (_, _) => AppServices.Navigate("FF:oc")));
        sp.Children.Add(row);

        string name = prof.Name;
        sp.Children.Add(SwitchRow("Aplicar sempre ao abrir a app", ScanService.IsApplyOnStart(name), on =>
        {
            ScanService.SetApplyOnStart(name, on);
            status.Foreground = Ui.Res("Muted");
            status.Text = on ? "Fica aplicado sempre que a app abre." : "Já não aplica ao abrir.";
        }));
        bool inGames = AppServices.Settings.Profiles.Values.Any(p => p.OcProfile == name);
        sp.Children.Add(SwitchRow("Usar nos jogos (só enquanto jogas)", inGames, on =>
        {
            int changed = ScanService.UseInGames(name, on);
            status.Foreground = Ui.Res("Muted");
            status.Text = !on ? "Tirei o overclock dos perfis dos jogos."
                : AppServices.Settings.Profiles.Count == 0 ? "Ainda não tens jogos com perfil: abre Jogos, escolhe um jogo e volta aqui."
                : changed == 1 ? "Pus o overclock em 1 jogo." : $"Pus o overclock em {changed} jogos.";
        }));
        status.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(status);
        if (oc.Notas.Count > 0)
        {
            var notes = Ui.T(string.Join("  ·  ", oc.Notas.Take(3)), "MutedText", 12);
            notes.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(notes);
        }
        return Ui.Card(sp, Ui.Res("Good"));
    }

    private static UIElement SwitchRow(string label, bool isOn, Action<bool> changed)
    {
        var sw = new ToggleButton { Style = (Style)Ui.Theme["Switch"], IsChecked = isOn, VerticalAlignment = VerticalAlignment.Center };
        sw.Click += (_, _) => changed(sw.IsChecked == true);
        var t = Ui.T(label, "Body");
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(10, 0, 0, 0);
        var row = Ui.Row(sw, t);
        row.Margin = new Thickness(0, 10, 0, 0);
        return row;
    }

    // ---------------- Configurações a corrigir

    private void ConfigBlock(List<ScanFinding> cfg)
    {
        if (cfg.Count == 0)
        {
            _after.Children.Add(Ui.Card(Ui.T("Nada a corrigir: as configurações estão bem.", "Body", color: Ui.Res("Good"))));
            return;
        }
        var more = new StackPanel { Visibility = Visibility.Collapsed };
        for (int i = 0; i < cfg.Count; i++)
        {
            var card = FindingCard(cfg[i]);
            if (i < 5) _after.Children.Add(card); else more.Children.Add(card);
        }
        if (more.Children.Count > 0)
        {
            Button? b = null;
            b = Ui.Btn($"Ver mais {more.Children.Count}", (_, _) =>
            {
                bool show = more.Visibility != Visibility.Visible;
                more.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                b!.Content = show ? "Ver menos" : $"Ver mais {more.Children.Count}";
            });
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(0, 0, 0, 12);
            _after.Children.Add(more);
            _after.Children.Add(b);
        }
    }

    private static Border FindingCard(ScanFinding f)
    {
        var dock = new DockPanel();
        if (f.Pagina is string page)
        {
            var b = Ui.Btn(f.PaginaTexto ?? "Abrir", (_, _) => AppServices.Navigate(page));
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(b, Dock.Right);
            dock.Children.Add(b);
        }
        var sp = new StackPanel();
        var pills = Ui.Row(Ui.Pill(f.Area.ToUpperInvariant()),
                           Ui.Pill(f.Severidade >= 3 ? "IMPORTANTE" : f.Severidade == 2 ? "AVISO" : "DICA", Ui.Severity(f.Severidade)));
        pills.Margin = new Thickness(0, 0, 0, 6);
        sp.Children.Add(pills);
        var title = Ui.T(f.Titulo, "H2", 15);
        title.Margin = new Thickness(0, 0, 0, 2);
        sp.Children.Add(title);
        if (f.Acao.Length > 0) sp.Children.Add(Ui.T("➜ " + f.Acao, "Body"));
        if (f.Detalhe.Length > 0)
        {
            sp.ToolTip = new ToolTip { Content = new TextBlock { Text = f.Detalhe, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 } };
            ToolTipService.SetShowDuration(sp, 60000);
        }
        dock.Children.Add(sp);
        var card = Ui.Card(dock, Ui.Severity(f.Severidade));
        card.Padding = new Thickness(18, 14, 18, 14);
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    private void BiosBlock(ScanReport r)
    {
        if (r.Bios.Count == 0) return;
        var body = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var s in r.Bios)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            sp.Children.Add(Ui.T(s.Titulo, "H2", 14.5));
            if (s.Onde.Length > 0) sp.Children.Add(Ui.T("Onde: " + s.Onde, "MutedText", 12.5));
            if (s.Texto.Length > 0) sp.Children.Add(Ui.T(s.Texto, "Body"));
            if (s.Porque.Length > 0) sp.Children.Add(Ui.T(s.Porque, "MutedText", 12.5));
            body.Children.Add(sp);
        }
        var head = new DockPanel();
        Button? b = null;
        b = Ui.Btn("Mostrar", (_, _) =>
        {
            bool show = body.Visibility != Visibility.Visible;
            body.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            b!.Content = show ? "Esconder" : "Mostrar";
        });
        DockPanel.SetDock(b, Dock.Right);
        head.Children.Add(b);
        var txt = new StackPanel();
        txt.Children.Add(Ui.T($"Mais desempenho na BIOS ({r.Bios.Count} {(r.Bios.Count == 1 ? "passo" : "passos")})", "H2", 15));
        txt.Children.Add(Ui.T("O processador e a RAM não se afinam por software: estes passos fazes tu na BIOS.", "MutedText", 12.5));
        head.Children.Add(txt);
        var all = new StackPanel();
        all.Children.Add(head);
        all.Children.Add(body);
        var card = Ui.Card(all);
        card.Margin = new Thickness(0, 4, 0, 8);
        _after.Children.Add(card);
    }

    // ---------------- Upgrades

    private UIElement UpgradesBlock(ScanReport r)
    {
        var sp = new StackPanel();
        List<UpgradeOption> opts;
        // Recalcula: assim os preços "Encontrei por" contam logo (o relatório gravado fica como estava)
        try { opts = PerfIndex.Available ? Upgrades.Suggest(r) : r.Upgrades; }
        catch { opts = r.Upgrades; }
        if (opts.Count == 0)
        {
            sp.Children.Add(Ui.Card(Ui.T(r.Inventario.Portatil
                ? "Num portátil não se troca a gráfica nem o processador. Para mais FPS: carregador ligado, base com ventilação e o perfil de desempenho do fabricante."
                : "Nenhuma peça está claramente a segurar o PC. Não vale a pena gastar dinheiro agora.", "Body")));
        }
        else
        {
            var wrap = new WrapPanel();
            foreach (var o in opts) wrap.Children.Add(UpgradeCard(r, o, sp));
            sp.Children.Add(wrap);
        }
        var disc = Ui.T(PerfIndex.Disclaimer, "MutedText", 11.5);
        disc.Foreground = Ui.Res("Faint");
        disc.Margin = new Thickness(0, 2, 0, 0);
        sp.Children.Add(disc);
        return sp;
    }

    private UIElement UpgradeCard(ScanReport r, UpgradeOption o, StackPanel block)
    {
        var sp = new StackPanel();
        string? tier = null;
        string motivo = o.Motivo;
        int colon = motivo.IndexOf(": ", StringComparison.Ordinal);
        if (o.Componente is UpgradeComponent.GPU or UpgradeComponent.CPU && colon is > 0 and < 40)
        {
            tier = motivo[..colon];
            motivo = motivo[(colon + 2)..];
        }
        string comp = o.Componente switch
        {
            UpgradeComponent.GPU => "GRÁFICA", UpgradeComponent.CPU => "PROCESSADOR",
            UpgradeComponent.RAM => "RAM", _ => "DISCO",
        };
        var pills = Ui.Row(Ui.Pill(comp));
        if (tier != null) pills.Children.Add(Ui.Pill(tier.ToUpperInvariant(), Ui.Res("AccentSoft"), Ui.Res("Accent2")));
        pills.Margin = new Thickness(0, 0, 0, 8);
        sp.Children.Add(pills);
        sp.Children.Add(Ui.T(o.Nome, "H2", 16));
        string gain = o.GanhoPct > 0 ? $"+{o.GanhoPct:0}% FPS estimado"
            : o.Componente == UpgradeComponent.Disco ? "Carregamentos muito mais rápidos"
            : "Menos quebras nos jogos";
        sp.Children.Add(Ui.T(gain, "Body", 18, Ui.Res("Good"), FontWeights.SemiBold));
        sp.Children.Add(Ui.T(PerfIndex.FormatRange(o.PrecoMinEur, o.PrecoMaxEur) + "  (preço de referência)", "Body", 13.5));

        // Modelo do índice (sem o "+ placa-mãe e RAM novas") — é a chave do preço que o utilizador encontrou
        string model = o.Nome.Split(" + ", 2)[0];
        bool keyed = o.Componente is UpgradeComponent.GPU or UpgradeComponent.CPU;
        if (keyed && UserPrices.Get(model) is double mine)
            sp.Children.Add(Ui.T($"Encontraste por {mine:0} €", "MutedText", 12.5, Ui.Res("Accent2")));

        // GPU/CPU: o motivo repete o ganho e a data do índice; fica só a frase entre parênteses (o resto vai para o tooltip)
        if (tier != null && System.Text.RegularExpressions.Regex.Match(motivo, @"\((.+?)\)\.") is { Success: true } m)
            motivo = char.ToUpper(m.Groups[1].Value[0], Pt) + m.Groups[1].Value[1..] + ".";
        var why = Ui.T(tier != null ? motivo : o.Motivo, "MutedText", 12.5);
        why.Margin = new Thickness(0, 6, 0, 0);
        why.ToolTip = new ToolTip { Content = new TextBlock { Text = o.Motivo, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 } };
        sp.Children.Add(why);
        foreach (var a in o.Avisos.Where(x => x.Length > 0).Take(3))
            sp.Children.Add(Ui.T("⚠ " + char.ToUpper(a[0], Pt) + a[1..], "MutedText", 12, Ui.Res("Warn")));

        var priceLbl = Ui.T("VER PREÇO ATUAL", "Label", 10);
        priceLbl.Margin = new Thickness(0, 10, 0, 4);
        sp.Children.Add(priceLbl);
        var shops = new WrapPanel();
        foreach (var (loja, url) in PerfIndex.PriceLinks(o.PesquisaTermo.Length > 0 ? o.PesquisaTermo : model))
        {
            var b = Ui.Btn(loja, (_, _) => PerfIndex.OpenUrl(url));
            b.Margin = new Thickness(0, 0, 6, 6);
            b.Padding = new Thickness(10, 5, 10, 5);
            b.ToolTip = url;
            shops.Children.Add(b);
        }
        sp.Children.Add(shops);

        if (keyed)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var lbl = Ui.T("Encontrei por", "MutedText", 12.5);
            lbl.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(lbl);
            var box = new TextBox { Width = 80, Margin = new Thickness(8, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center,
                                    ToolTip = "Preço que viste numa loja (fica só neste PC, 60 dias). Refaz as contas do custo/benefício." };
            if (UserPrices.Get(model) is double cur) box.Text = cur.ToString("0", Pt);
            row.Children.Add(box);
            var eur = Ui.T("€", "MutedText", 12.5);
            eur.VerticalAlignment = VerticalAlignment.Center;
            eur.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(eur);
            var save = Ui.Btn("Guardar", (_, _) =>
            {
                string s = box.Text.Trim().Replace("€", "").Replace(" ", "");
                double? v = s.Length == 0 ? null
                    : double.TryParse(s, NumberStyles.Number, Pt, out var d) || double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d) ? d : -1;
                if (v is < 0) { AppServices.Toast("Escreve só o valor em euros (ex.: 650)."); return; }
                UserPrices.Set(model, v);
                // Volta a desenhar as sugestões com o preço novo
                int idx = _after.Children.IndexOf(block);
                if (idx >= 0) { _after.Children.RemoveAt(idx); _after.Children.Insert(idx, UpgradesBlock(r)); }
            });
            save.Padding = new Thickness(10, 5, 10, 5);
            row.Children.Add(save);
            sp.Children.Add(row);
        }

        var card = Ui.Card(sp);
        card.Width = 400;
        card.Margin = new Thickness(0, 0, 12, 12);
        return card;
    }
}
