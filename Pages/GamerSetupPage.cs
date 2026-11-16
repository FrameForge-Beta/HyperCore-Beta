using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameForge.Core;
using FrameForge.Optimize;

namespace FrameForge.Pages;

/// <summary>
/// Níveis de desempenho › Configuração gamer (tag "FF:gamersetup"): o Windows e o monitor prontos para jogar, item a item.
///   1) Resumo: quantos itens estão bem + "Corrigir o recomendado" (o único botão principal da página).
///   2) Monitores: Hz atual vs máximo (com mudança segura: confirmação + volta sozinho em 15 s), ligação e VRR.
///   3) Windows para jogos: HAGS, Modo de Jogo, gravação em segundo plano, jogos em janela, VRR do Windows e MPO.
///   4) Cache de shaders: tamanho por fabricante, limpar (com cópia por 7 dias) e desfazer.
/// Tudo o que muda o PC pede confirmação e fica no ChangeJournal (Central Desfazer). Leituras sempre fora do UI thread.
/// O cabeçalho (título, descrição e abas) é desenhado pela janela principal a partir do NavCatalog.
/// </summary>
public sealed class GamerSetupPage : UserControl
{
    private const int RevertSeconds = 15;

    private readonly StackPanel _root;
    private readonly Border _heroCard = new();
    private readonly Border _flash;
    private readonly TextBlock _flashText;
    private readonly TextBlock _flashIcon;
    private readonly StackPanel _monitors = new();
    private readonly Border _checksCard = new();
    private readonly Border _shaderCard = new();
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromSeconds(9) };
    private readonly DispatcherTimer _journalDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Dictionary<string, FrameworkElement> _checkRows = new();

    private sealed record Snapshot(List<GamerMonitor> Monitors, List<SetupCheck> Checks, Dictionary<string, (string Id, int OriginalHz)> HzEntries);
    private sealed record ShaderSnap(List<ShaderDir> Dirs, ShaderBackupInfo? Backup);

    private Snapshot? _snap;
    private ShaderSnap? _shaders;
    private bool _loading, _again, _busy, _subscribed;

    public GamerSetupPage()
    {
        Ui.UseTheme(this);
        _root = new StackPanel { Margin = Ui.PageMargin };

        // Resumo
        _heroCard.Style = (Style)Ui.Theme["FfCard"];
        _heroCard.Padding = new Thickness(24, 22, 24, 22);
        _heroCard.Margin = new Thickness(0, 0, 0, 12);
        _root.Children.Add(_heroCard);

        // Linha de resultado das ações (aparece e desaparece)
        _flashIcon = new TextBlock { FontFamily = Icons, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _flashText = Ui.T("", "Body");
        _flashText.VerticalAlignment = VerticalAlignment.Center;
        var flashRow = new DockPanel();
        DockPanel.SetDock(_flashIcon, Dock.Left);
        flashRow.Children.Add(_flashIcon);
        flashRow.Children.Add(_flashText);
        _flash = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 11, 16, 11), Margin = new Thickness(0, 0, 0, 12),
            BorderThickness = new Thickness(1), Child = flashRow, Visibility = Visibility.Collapsed,
        };
        _root.Children.Add(_flash);
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); HideFlash(); };

        _root.Children.Add(Ui.SectionTitle("Monitores", "A taxa de atualização (Hz) é quantas imagens por segundo o monitor mostra. Mais Hz = jogo mais fluido."));
        _root.Children.Add(_monitors);

        var winTitle = Ui.SectionTitle("Windows para jogos", "Interruptores do Windows que mexem nos FPS e no atraso. Passa o rato no ⓘ para saberes o que cada um faz.");
        winTitle.Margin = new Thickness(0, 14, 0, 10);
        _root.Children.Add(winTitle);
        _checksCard.Style = (Style)Ui.Theme["FfCard"];
        _checksCard.Padding = new Thickness(6, 4, 6, 4);
        _root.Children.Add(_checksCard);

        var shTitle = Ui.SectionTitle("Cache de shaders", "Os jogos guardam aqui os efeitos gráficos já preparados, para não os prepararem de novo.");
        shTitle.Margin = new Thickness(0, 14, 0, 10);
        _root.Children.Add(shTitle);
        _shaderCard.Style = (Style)Ui.Theme["FfCard"];
        _root.Children.Add(_shaderCard);

        _root.Children.Add(Footer());

        RenderLoading();
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _root };

        _journalDebounce.Tick += (_, _) => { _journalDebounce.Stop(); if (!_busy) _ = RefreshAsync(); };
        Loaded += (_, _) =>
        {
            if (!_subscribed) { ChangeJournal.Changed += OnJournalChanged; _subscribed = true; }
            _ = RefreshAsync();
            _ = RefreshShadersAsync();
        };
        Unloaded += (_, _) =>
        {
            if (_subscribed) { ChangeJournal.Changed -= OnJournalChanged; _subscribed = false; }
            _journalDebounce.Stop();
        };
    }

    private static FontFamily Icons => (FontFamily)Ui.Theme["Icons"];
    private static FontFamily Mono => (FontFamily)Ui.Theme["Mono"];

    private void OnJournalChanged()
    {
        try { Dispatcher.BeginInvoke(() => { _journalDebounce.Stop(); _journalDebounce.Start(); }); } catch { }
    }

    // ================================================================== leitura (fora do UI thread)

    private async Task RefreshAsync()
    {
        if (_loading) { _again = true; return; }
        _loading = true;
        try
        {
            do
            {
                _again = false;
                Snapshot s;
                try
                {
                    s = await Task.Run(() =>
                    {
                        GamerSetup.Init();
                        List<GamerMonitor> mons;
                        try { mons = GamerDisplay.Read(); } catch { mons = new(); }
                        try { GamerSetup.PruneStale(mons); } catch { }
                        var checks = GamerSetup.ReadChecks(mons);
                        var hz = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
                        foreach (var m in mons) if (GamerSetup.HzEntry(m) is { } e) hz[m.GdiName] = e;
                        return new Snapshot(mons, checks, hz);
                    });
                }
                catch (Exception ex)
                {
                    ShowFlash("Não consegui ler a configuração: " + ex.Message, false);
                    return;
                }
                _snap = s;
                RenderAll();
            } while (_again);
        }
        finally { _loading = false; }
    }

    private async Task RefreshShadersAsync()
    {
        try
        {
            _shaders = await Task.Run(() => new ShaderSnap(ShaderCache.Scan(), ShaderCache.Backup()));
        }
        catch { _shaders = new ShaderSnap(new(), null); }
        RenderShaders();
    }

    // ================================================================== desenho

    private void RenderLoading()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new ProgressRing(56));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
        texts.Children.Add(Ui.T("A verificar o monitor e o Windows…", "H2", 18));
        texts.Children.Add(Ui.T("Só leitura: nada é mudado sem perguntar.", "MutedText"));
        sp.Children.Add(texts);
        _heroCard.Child = sp;
        _monitors.Children.Clear();
        _monitors.Children.Add(Placeholder(150));
        _checksCard.Child = Placeholder(220, inCard: true);
        _shaderCard.Child = Ui.T("A medir a cache…", "MutedText");
    }

    private static Border Placeholder(double height, bool inCard = false)
    {
        var b = new Border
        {
            Height = height, CornerRadius = new CornerRadius(14), Background = Ui.Res("Card"), BorderBrush = Ui.Res("Line"),
            BorderThickness = new Thickness(inCard ? 0 : 1), Margin = new Thickness(0, 0, 0, inCard ? 0 : 12),
        };
        var pulse = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        b.BeginAnimation(OpacityProperty, pulse);
        return b;
    }

    private void RenderAll()
    {
        if (_snap == null) return;
        RenderHero(_snap);
        RenderMonitors(_snap);
        RenderChecks(_snap);
    }

    // ------------------------------------------------------------------ resumo

    private void RenderHero(Snapshot s)
    {
        var realMonitors = s.Monitors.Where(m => !m.Virtual).ToList();
        var checks = s.Checks.Where(c => c.State is SetupState.Good or SetupState.Improve).ToList();
        var vrrMonitors = realMonitors.Where(m => m.Vrr.Capable == true).ToList();
        var vrrOff = vrrMonitors.Where(m => m.Vrr.Enabled == false).ToList();
        int total = checks.Count + realMonitors.Count + vrrMonitors.Count;
        int good = checks.Count(c => c.State == SetupState.Good) + realMonitors.Count(m => !m.BelowMax) + vrrMonitors.Count - vrrOff.Count;
        var fixable = s.Checks.Where(c => c.Recommended && c.FixLabel != null).ToList();
        var slowMonitors = realMonitors.Where(m => m.BelowMax).ToList();
        int toImprove = total - good;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Anel
        Brush ringColor = toImprove == 0 ? Ui.Res("Good") : Ui.Res("AccentGradient");
        var ring = Ring(92, 8, total == 0 ? 0 : good / (double)total, ringColor);
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(new TextBlock
        {
            Text = $"{good}/{total}", FontFamily = Mono, FontSize = 22, FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Res("Fg"), HorizontalAlignment = HorizontalAlignment.Center,
        });
        center.Children.Add(new TextBlock { Text = "PRONTOS", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Ui.Res("Muted"), HorizontalAlignment = HorizontalAlignment.Center });
        ring.Children.Add(center);
        ring.Margin = new Thickness(0, 0, 22, 0);
        Grid.SetColumn(ring, 0);
        grid.Children.Add(ring);

        // Texto
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string title = toImprove == 0 ? "Tudo pronto para jogar" : toImprove == 1 ? "Quase pronto: falta 1 coisa" : $"Há {toImprove} coisas a melhorar";
        texts.Children.Add(Ui.T(title, "H2", 20));
        string sub;
        if (toImprove == 0) sub = "O monitor está no máximo de Hz e o Windows está como deve estar para jogos.";
        else if (fixable.Count > 0 && slowMonitors.Count > 0) sub = "Corrige o Windows com um clique e põe o monitor no máximo de Hz no cartão em baixo.";
        else if (fixable.Count > 0 && vrrOff.Count > 0) sub = "Corrige o Windows com um clique. Para o VRR, vê o cartão do monitor em baixo.";
        else if (fixable.Count > 0) sub = "Um clique corrige tudo. Cada mudança fica na Central Desfazer.";
        else if (slowMonitors.Count > 0) sub = "Põe o monitor no máximo de Hz no cartão em baixo: é o que mais se nota a jogar.";
        else sub = "Vê o cartão do monitor em baixo: lá explico como ligar.";
        var subT = Ui.T(sub, "MutedText");
        subT.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(subT);

        // Atalhos para o que falta (clicáveis)
        if (toImprove > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var m in slowMonitors)
                wrap.Children.Add(Jump($"{m.Name}: {m.CurrentHz} → {m.MaxHz} Hz", "\uE7F4", () => ScrollTo(_monitors)));
            foreach (var m in vrrOff)
                wrap.Children.Add(Jump($"{m.Name}: {(m.Vrr.Kind.Length > 0 ? m.Vrr.Kind : "VRR")} desligado", "\uE895", () => ScrollTo(_monitors)));
            foreach (var c in s.Checks.Where(c => c.State == SetupState.Improve))
            {
                var id = c.Id;
                wrap.Children.Add(Jump(c.Title, c.Glyph, () => { if (_checkRows.TryGetValue(id, out var row)) ScrollTo(row, highlight: true); }));
            }
            texts.Children.Add(wrap);
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        // Botões
        var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        if (fixable.Count > 0)
        {
            var fix = Ui.Btn(fixable.Count == 1 ? "Corrigir o recomendado" : $"Corrigir os {fixable.Count} recomendados", async (_, _) => await FixRecommended(), "BtnAccent");
            fix.Content = IconText("\uE945", fixable.Count == 1 ? "Corrigir o recomendado" : $"Corrigir os {fixable.Count} recomendados", Brushes.White);
            fix.Padding = new Thickness(20, 12, 20, 12);
            fix.Margin = new Thickness(0, 0, 0, 8);
            fix.ToolTip = "Mostra a lista do que vai mudar antes de mudar.";
            actions.Children.Add(fix);
        }
        var again = Ui.Btn("Verificar de novo", async (_, _) => { await RefreshAsync(); await RefreshShadersAsync(); ShowFlash("Verificado agora.", true); });
        again.Content = IconText("\uE72C", "Verificar de novo");
        again.Margin = new Thickness(0);
        actions.Children.Add(again);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        Fade(grid);
        _heroCard.Child = grid;
    }

    private Border Jump(string text, string glyph, Action go)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 11.5, Foreground = Ui.Res("Warn"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Ui.Res("Fg"), VerticalAlignment = VerticalAlignment.Center });
        var b = new Border
        {
            CornerRadius = new CornerRadius(99), Padding = new Thickness(11, 5, 12, 5), Margin = new Thickness(0, 0, 6, 6),
            Background = Soft("Warn", 0x1A), BorderBrush = Soft("Warn", 0x40), BorderThickness = new Thickness(1), Child = row, Cursor = Cursors.Hand,
            ToolTip = "Ir para este item",
        };
        b.MouseEnter += (_, _) => b.BorderBrush = Ui.Res("Warn");
        b.MouseLeave += (_, _) => b.BorderBrush = Soft("Warn", 0x40);
        b.MouseLeftButtonUp += (_, _) => go();
        return b;
    }

    // ------------------------------------------------------------------ monitores

    private void RenderMonitors(Snapshot s)
    {
        _monitors.Children.Clear();
        if (s.Monitors.Count == 0)
        {
            _monitors.Children.Add(Ui.Card(Ui.T("Não consegui ler os monitores. Se usas ligação remota (Ambiente de Trabalho Remoto), abre o HyperCore no próprio PC.", "MutedText")));
            return;
        }
        foreach (var m in s.Monitors)
        {
            s.HzEntries.TryGetValue(m.GdiName, out var entry);
            var card = MonitorCard(m, entry.Id != null ? entry : null);
            Fade(card);
            _monitors.Children.Add(card);
        }
    }

    private Border MonitorCard(GamerMonitor m, (string Id, int OriginalHz)? entry)
    {
        var body = new StackPanel();

        // Cabeçalho: ícone, nome, etiquetas
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var tile = Tile("\uE7F4", m.BelowMax && !m.Virtual ? Ui.Res("Warn") : Ui.Res("Accent"));
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        var pills = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (m.Primary) pills.Children.Add(InfoTag("Principal", Ui.Res("Accent")));
        if (m.Connection.Length > 0) pills.Children.Add(InfoTag(m.Connection));
        pills.Children.Add(InfoTag(m.Resolution));
        DockPanel.SetDock(pills, Dock.Right);
        head.Children.Add(pills);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nm = Ui.T(m.Name, "H2", 16.5);
        nm.Margin = new Thickness(0);
        names.Children.Add(nm);
        names.Children.Add(Ui.T(MonitorSub(m), "MutedText", 12));
        head.Children.Add(names);
        body.Children.Add(head);

        // Hz atual | escolha da taxa
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var big = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var num = new StackPanel { Orientation = Orientation.Horizontal };
        num.Children.Add(new TextBlock
        {
            Text = m.CurrentHz.ToString(CultureInfo.InvariantCulture), FontFamily = Mono, FontSize = 44, FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Res("AccentTextGradient"), VerticalAlignment = VerticalAlignment.Bottom,
        });
        num.Children.Add(new TextBlock { Text = " Hz", FontSize = 18, Foreground = Ui.Res("Muted"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 0, 8) });
        big.Children.Add(num);
        big.Children.Add(Ui.T(m.MaxHz > m.CurrentHz ? $"agora · máximo {m.MaxHz} Hz" : "agora · é o máximo", "MutedText", 12));
        big.Children.Add(HzBar(m));
        Grid.SetColumn(big, 0);
        grid.Children.Add(big);

        var right = new StackPanel { Margin = new Thickness(12, 2, 0, 0) };
        var lbl = Ui.T("TAXAS QUE ESTE MONITOR ACEITA EM " + m.Resolution.Replace(" ", ""), "Label");
        lbl.Margin = new Thickness(0, 0, 0, 8);
        right.Children.Add(lbl);
        var chips = new WrapPanel();
        Button? apply = null;
        int selected = m.MaxHz;
        string group = "hz-" + m.GdiName;
        // 59/119/143 Hz são as variantes "NTSC" (59,94 Hz) da taxa ao lado: escondem-se, a não ser que seja a atual
        var rates = m.Rates.Where(r => r >= 24 && (r == m.CurrentHz || !m.Rates.Contains(r + 1))).ToList();
        if (rates.Count == 0) rates.Add(m.CurrentHz);
        foreach (var r in rates)
        {
            int rate = r;
            var rb = new RadioButton { GroupName = group, IsChecked = rate == selected, Margin = new Thickness(0, 0, 6, 6) };
            rb.SetResourceReference(StyleProperty, "Chip");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = $"{rate} Hz", FontFamily = Mono, FontSize = 12.5 });
            if (rate == m.CurrentHz)
                content.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Ui.Res("Good"), Margin = new Thickness(7, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Taxa atual" });
            rb.Content = content;
            rb.ToolTip = rate == m.CurrentHz ? "Taxa atual" : rate == m.MaxHz ? "O máximo deste monitor" : null;
            rb.Checked += (_, _) =>
            {
                selected = rate;
                if (apply != null) UpdateApply(apply, m, rate);
            };
            chips.Children.Add(rb);
        }
        right.Children.Add(chips);

        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        apply = Ui.Btn("", async (_, _) => await ChangeHz(m, selected, entry));
        UpdateApply(apply, m, selected);
        apply.Margin = new Thickness(0, 0, 8, 6);
        actions.Children.Add(apply);
        if (entry is { } en && en.OriginalHz != m.CurrentHz)
        {
            var undo = Ui.Btn("", async (_, _) => await ChangeHz(m, en.OriginalHz, entry));
            undo.Content = IconText("\uE7A7", $"Repor {en.OriginalHz} Hz");
            undo.ToolTip = "Volta à taxa que tinhas antes de a mudares aqui.";
            undo.Margin = new Thickness(0, 0, 8, 6);
            actions.Children.Add(undo);
        }
        right.Children.Add(actions);
        right.Children.Add(Advice(HzAdvice(m), m.BelowMax && !m.Virtual ? "Warn" : "Good"));
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        body.Children.Add(grid);

        // VRR
        body.Children.Add(Divider(new Thickness(0, 16, 0, 14)));
        body.Children.Add(VrrRow(m));

        var card = Ui.Card(body);
        card.Padding = new Thickness(22, 20, 22, 18);
        return card;
    }

    private static string MonitorSub(GamerMonitor m)
    {
        var parts = new List<string>();
        if (m.Internal) parts.Add("Ecrã do portátil");
        if (m.Virtual) parts.Add("Ecrã virtual (ligação remota ou captura)");
        parts.Add(m.GdiName.Replace(@"\\.\", "").Replace("DISPLAY", "Ecrã "));
        return string.Join(" · ", parts);
    }

    private static void UpdateApply(Button b, GamerMonitor m, int hz)
    {
        bool same = hz == m.CurrentHz;
        b.Content = IconText(same ? "\uE73E" : "\uE7F4", same ? $"Já está a {hz} Hz" : $"Pôr a {hz} Hz");
        b.IsEnabled = !same;
        b.ToolTip = same ? null : $"Muda para {hz} Hz. Se não confirmares em {RevertSeconds} s, volta sozinho a {m.CurrentHz} Hz.";
    }

    private static string HzAdvice(GamerMonitor m)
    {
        if (m.Virtual) return "Ecrã virtual: não conta para os jogos.";
        if (m.Rates.Count <= 1) return "Este monitor só aceita uma taxa nesta resolução.";
        if (m.BelowMax)
            return m.CurrentHz <= 60
                ? $"Estás a {m.CurrentHz} Hz, mas o monitor aceita {m.MaxHz} Hz. Põe no máximo: tudo fica muito mais fluido, sem custo."
                : $"O monitor aceita {m.MaxHz} Hz. Põe no máximo para jogos mais fluidos.";
        return m.MaxHz >= 120 ? "No máximo. Bom para jogar." : "Já está no máximo que o monitor aceita nesta resolução.";
    }

    private static FrameworkElement HzBar(GamerMonitor m)
    {
        double frac = m.MaxHz > 0 ? Math.Clamp(m.CurrentHz / (double)m.MaxHz, 0.04, 1) : 1;
        var track = new Grid { Height = 6, Width = 150, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Ui.Res("Hover") });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0,
            Background = m.BelowMax ? Ui.Res("Warn") : Ui.Res("AccentGradient"),
        };
        track.Children.Add(fill);
        fill.BeginAnimation(WidthProperty, new DoubleAnimation(0, 150 * frac, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        track.ToolTip = $"{m.CurrentHz} de {m.MaxHz} Hz";
        return track;
    }

    private FrameworkElement VrrRow(GamerMonitor m)
    {
        var v = m.Vrr;
        string title, text, state;
        string kind = v.Kind.Length > 0 ? v.Kind : "VRR";
        string range = v.MinHz > 0 && v.MaxHz > v.MinHz ? $" ({v.MinHz}–{v.MaxHz} Hz)" : "";
        if (v.Enabled == true)
        {
            title = $"{kind} ligado{range}"; state = "Good";
            text = "Sem rasgos nem engasgos quando os FPS sobem e descem.";
        }
        else if (v.Capable == true && v.Enabled == false && v.NvidiaPossible == false)
        {
            title = $"{kind} não está a ser usado{range}"; state = "Warn";
            text = "O monitor suporta, mas a placa NVIDIA não o está a usar. Liga no Painel de Controlo NVIDIA › Ecrã › Configurar G-SYNC" +
                   (m.Connection == "HDMI" ? ". Se a opção não aparecer, liga o monitor por DisplayPort: em HDMI muitos monitores não fazem G-SYNC." : ".");
        }
        else if (v.Capable == true && v.Enabled == false)
        {
            title = $"{kind} desligado{range}"; state = "Warn";
            text = "O monitor suporta, mas está desligado. Liga no Painel de Controlo NVIDIA › Ecrã › Configurar G-SYNC.";
        }
        else if (v.Capable == true)
        {
            title = $"{kind} suportado{range}"; state = "Accent";
            text = "O monitor suporta. Confirma que está ligado no menu do próprio monitor e no painel da gráfica (AMD: Ecrã › FreeSync; NVIDIA: Configurar G-SYNC).";
        }
        else
        {
            title = "Não indicado pelo monitor"; state = "Faint";
            text = "Se o teu monitor tiver G-SYNC ou FreeSync, liga-o no menu dele e no painel da gráfica.";
        }

        var row = new DockPanel();
        var icon = new TextBlock { Text = "\uE895", FontFamily = Icons, FontSize = 16, Foreground = Ui.Res(state == "Faint" ? "Muted" : state), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 2, 14, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (v.Allm) right.Children.Add(WithTip(InfoTag("ALLM"), "A TV/monitor muda sozinha para o modo de jogo quando há um jogo aberto."));
        right.Children.Add(StatusPill(v.Enabled == true ? "Ligado" : v.Capable == true && v.Enabled == false ? "Desligado" : v.Capable == true ? "Suportado" : "Sem dados", state));
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var texts = new StackPanel();
        var t = Ui.T("Sincronização variável · " + title, "Body", 13.5, weight: FontWeights.SemiBold);
        texts.Children.Add(t);
        texts.Children.Add(Ui.T(text, "MutedText", 12.5));
        texts.Margin = new Thickness(0, 0, 12, 0);
        texts.ToolTip = "VRR (G-SYNC, FreeSync, Adaptive-Sync): o monitor acompanha os FPS do jogo em vez de ter uma taxa fixa. Acaba com o \"tearing\" (imagem rasgada) sem o atraso do V-Sync.";
        row.Children.Add(texts);
        return row;
    }

    // ------------------------------------------------------------------ Windows para jogos

    private void RenderChecks(Snapshot s)
    {
        _checkRows.Clear();
        var list = new StackPanel();
        for (int i = 0; i < s.Checks.Count; i++)
        {
            if (i > 0) list.Children.Add(Divider(new Thickness(16, 0, 16, 0)));
            var row = CheckRow(s.Checks[i]);
            _checkRows[s.Checks[i].Id] = row;
            list.Children.Add(row);
        }
        Fade(list);
        _checksCard.Child = list;
    }

    private FrameworkElement CheckRow(SetupCheck c)
    {
        string color = StateColor(c.State);
        var grid = new Grid { Margin = new Thickness(16, 14, 14, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tile = Tile(c.Glyph, Ui.Res(color == "Faint" ? "Muted" : color == "Good" ? "Accent" : color), 36);
        tile.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(tile, 0);
        grid.Children.Add(tile);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        var titleRow = new WrapPanel();
        titleRow.Children.Add(Ui.T(c.Title, "Body", 14, weight: FontWeights.SemiBold));
        var info = new TextBlock
        {
            Text = "\uE946", FontFamily = Icons, FontSize = 12.5, Foreground = Ui.Res("Faint"), Margin = new Thickness(8, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Help, ToolTip = c.Help, Background = Brushes.Transparent,
        };
        ToolTipService.SetShowDuration(info, 60000);
        ToolTipService.SetInitialShowDelay(info, 150);
        info.MouseEnter += (_, _) => info.Foreground = Ui.Res("Accent");
        info.MouseLeave += (_, _) => info.Foreground = Ui.Res("Faint");
        titleRow.Children.Add(info);
        if (c.RestartPending) titleRow.Children.Add(WithTip(StatusPill("Reinicia o PC", "Warn", 9.5, new Thickness(10, 0, 0, 0)), "Já está mudado, mas só faz efeito depois de reiniciar o PC."));
        if (c.JournalId != null) titleRow.Children.Add(WithTip(StatusPill("Mudado pelo HyperCore", "Accent", 9.5, new Thickness(8, 0, 0, 0)), "Está na Central Desfazer: podes repor como estava."));
        texts.Children.Add(titleRow);
        var adv = Ui.T(c.Advice, "MutedText", 12.5);
        adv.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(adv);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var pill = StatusPill(c.Status, color);
        pill.Margin = new Thickness(0, 0, 10, 0);
        pill.MinWidth = 96;
        right.Children.Add(pill);
        if (c.JournalId != null)
        {
            var undo = Ui.Btn("", async (_, _) => await UndoCheck(c));
            undo.Content = IconText("\uE7A7", "Desfazer");
            undo.ToolTip = "Repõe o valor que estava antes de o HyperCore mudar.";
            undo.Padding = new Thickness(13, 7, 13, 7);
            right.Children.Add(undo);
        }
        else if (c.FixLabel != null)
        {
            var fix = Ui.Btn(c.FixLabel, async (_, _) => await FixCheck(c));
            fix.Padding = new Thickness(16, 7, 16, 7);
            fix.MinWidth = 92;
            if (!c.Recommended)
            {
                // Opcional (ex.: desligar o MPO): botão discreto, para não parecer uma recomendação
                fix.Background = Brushes.Transparent;
                fix.BorderBrush = Ui.Res("Line");
                fix.Foreground = Ui.Res("Muted");
                fix.FontWeight = FontWeights.Normal;
            }
            right.Children.Add(fix);
        }
        if (c.SettingsUri != null)
        {
            var open = new Button { Content = "\uE8A7", ToolTip = "Abrir nas Definições do Windows", Width = 32, Height = 32 };
            open.SetResourceReference(StyleProperty, "IconButton");
            open.Margin = new Thickness(c.FixLabel != null || c.JournalId != null ? 0 : 0, 0, 0, 0);
            open.Click += (_, _) => Ui.Open(c.SettingsUri);
            right.Children.Add(open);
        }
        else right.Children.Add(new Border { Width = 32 });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var host = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(10), Child = grid, Margin = new Thickness(0, 2, 0, 2) };
        host.MouseEnter += (_, _) => host.Background = Soft("Hover", 0x80);
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;
        return host;
    }

    // ------------------------------------------------------------------ cache de shaders

    private void RenderShaders()
    {
        var s = _shaders;
        if (s == null) return;
        var body = new StackPanel();
        long total = s.Dirs.Sum(d => d.Bytes);
        int files = s.Dirs.Sum(d => d.Files);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var big = new StackPanel();
        big.Children.Add(new TextBlock
        {
            Text = total > 0 ? ShaderCache.Size(total) : "Vazia", FontFamily = Mono, FontSize = 32, FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Res("AccentTextGradient"),
        });
        big.Children.Add(Ui.T(total > 0 ? $"{files:N0} ficheiros".Replace(",", " ") : "Nada para limpar", "MutedText", 12));
        Grid.SetColumn(big, 0);
        top.Children.Add(big);

        var bars = new StackPanel { Margin = new Thickness(12, 4, 0, 0) };
        var byVendor = s.Dirs.GroupBy(d => d.Vendor).Select(g => (Vendor: g.Key, Bytes: g.Sum(x => x.Bytes), Paths: g.Select(x => x.Path).ToList()))
                             .Where(g => g.Bytes > 0).OrderByDescending(g => g.Bytes).ToList();
        long maxB = byVendor.Count > 0 ? byVendor.Max(v => v.Bytes) : 1;
        if (byVendor.Count == 0)
            bars.Children.Add(Ui.T(s.Dirs.Count == 0 ? "Não encontrei pastas de cache de shaders neste PC." : "As pastas da cache estão vazias.", "MutedText"));
        foreach (var v in byVendor)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8), ToolTip = string.Join("\n", v.Paths) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            var name = Ui.T(v.Vendor, "Body", 12.5);
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            var track = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Ui.Res("Hover") });
            var fill = new Border { CornerRadius = new CornerRadius(4), Background = VendorBrush(v.Vendor), HorizontalAlignment = HorizontalAlignment.Left };
            track.Children.Add(fill);
            double frac = Math.Max(0.03, v.Bytes / (double)maxB);
            track.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * frac;
            Grid.SetColumn(track, 1);
            row.Children.Add(track);
            var size = new TextBlock { Text = ShaderCache.Size(v.Bytes), FontFamily = Mono, FontSize = 12, Foreground = Ui.Res("Muted"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(size, 2);
            row.Children.Add(size);
            bars.Children.Add(row);
        }
        Grid.SetColumn(bars, 1);
        top.Children.Add(bars);
        body.Children.Add(top);

        body.Children.Add(Advice("Limpar ajuda quando há engasgos, erros gráficos ou jogos que fecham depois de atualizar o driver. " +
                                 "Não dá mais FPS: os jogos voltam a preparar os shaders e o primeiro arranque demora mais.", "Accent", new Thickness(0, 10, 0, 0)));

        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var clear = Ui.Btn("", async (_, _) => await ClearShaders(total));
        clear.Content = IconText("\uE74D", total > 0 ? $"Limpar a cache ({ShaderCache.Size(total)})" : "Limpar a cache");
        clear.IsEnabled = total > 0;
        clear.Margin = new Thickness(0, 0, 8, 6);
        actions.Children.Add(clear);
        if (s.Backup is { } bk)
        {
            var undo = Ui.Btn("", async (_, _) => await UndoShaders(bk));
            undo.Content = IconText("\uE7A7", "Desfazer a última limpeza");
            undo.ToolTip = "Devolve os ficheiros que foram limpos (os que os jogos já criaram de novo ficam).";
            undo.Margin = new Thickness(0, 0, 8, 6);
            actions.Children.Add(undo);
            var del = Ui.Btn("Apagar a cópia já", async (_, _) => await DeleteShaderCopy(bk), "BtnDanger");
            del.ToolTip = "Liberta já o espaço da cópia. Depois disto já não dá para desfazer a limpeza.";
            del.Margin = new Thickness(0, 0, 8, 6);
            actions.Children.Add(del);
        }
        body.Children.Add(actions);
        if (s.Backup is { } b2)
        {
            var note = Ui.T($"Cópia da última limpeza ({ShaderCache.Size(b2.Bytes)}) guardada até {b2.ExpiresUtc.ToLocalTime():dd/MM 'às' HH:mm}. Depois é apagada sozinha.", "MutedText", 12);
            note.Margin = new Thickness(0, 2, 0, 0);
            body.Children.Add(note);
        }
        Fade(body);
        _shaderCard.Child = body;
    }

    private static Brush VendorBrush(string vendor) => vendor switch
    {
        "NVIDIA" => new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00)),
        "AMD" => new SolidColorBrush(Color.FromRgb(0xED, 0x4B, 0x4B)),
        "Intel" => new SolidColorBrush(Color.FromRgb(0x3B, 0x9B, 0xF5)),
        _ => Ui.Res("AccentGradient"),
    };

    private FrameworkElement Footer()
    {
        var row = new DockPanel { Margin = new Thickness(4, 8, 0, 0) };
        var icon = new TextBlock { Text = "\uE7A7", FontFamily = Icons, FontSize = 13, Foreground = Ui.Res("Accent2"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 10, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var t = Ui.T("", "MutedText", 12.5);
        t.Inlines.Add(new Run("Tudo o que mudares aqui fica na Central Desfazer: podes repor como estava, a qualquer momento.  "));
        var link = new Hyperlink(new Run("Abrir a Central Desfazer")) { Foreground = Ui.Res("Accent"), TextDecorations = null, Cursor = Cursors.Hand };
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        link.Click += (_, _) => AppServices.Navigate("S:undo");
        t.Inlines.Add(link);
        row.Children.Add(t);
        return row;
    }

    // ================================================================== ações (todas com confirmação)

    private async Task FixRecommended()
    {
        if (_busy || _snap == null) return;
        var items = _snap.Checks.Where(c => c.Recommended && c.FixLabel != null).ToList();
        if (items.Count == 0) return;
        var bullets = items.Select(c => GamerSetup.TitleFor(c.Id, c.TargetOn) + (c.NeedsRestart ? " (depois de reiniciar)" : "")).ToList();
        bullets.Add("Tudo fica na Central Desfazer: podes repor com um clique.");
        if (!GamerDialogs.Confirm(this, items.Count == 1 ? "Corrigir 1 item?" : $"Corrigir {items.Count} itens?",
                "Vou mudar estas definições do Windows para jogares melhor:", "Corrigir", bullets, "\uE945")) return;

        await Busy("A aplicar…", async () =>
        {
            var results = await Task.Run(() => items.Select(c => GamerSetup.Set(c.Id, c.TargetOn)).ToList());
            int ok = results.Count(r => r.ok);
            var fails = results.Where(r => !r.ok).Select(r => r.msg).Distinct().ToList();
            bool restart = items.Where((c, i) => results[i].ok && c.NeedsRestart).Any();
            string msg = ok == items.Count ? (ok == 1 ? "Corrigido." : $"{ok} itens corrigidos.") : $"{ok} de {items.Count} corrigidos. {string.Join(" ", fails)}";
            if (restart && ok > 0) msg += " Alguns só fazem efeito depois de reiniciar o PC.";
            ShowFlash(msg, fails.Count == 0);
        });
    }

    private async Task FixCheck(SetupCheck c)
    {
        if (_busy) return;
        string action = GamerSetup.TitleFor(c.Id, c.TargetOn);
        var bullets = new List<string>();
        if (c.NeedsRestart) bullets.Add("Só faz efeito depois de reiniciar o PC.");
        bullets.Add("Fica na Central Desfazer: podes repor com um clique.");
        bool danger = c.Id == "mpo" && !c.TargetOn;
        string text = danger
            ? "Desliga só se tens cintilação, ecrã preto ou engasgos com o navegador aberto. Se não tens nenhum destes problemas, deixa ligado."
            : c.Help;
        if (!GamerDialogs.Confirm(this, $"{c.FixLabel}: {c.Title}?", text, c.FixLabel ?? "Mudar", bullets, c.Glyph, danger)) return;
        await Busy("A aplicar…", async () =>
        {
            var r = await Task.Run(() => GamerSetup.Set(c.Id, c.TargetOn));
            ShowFlash(r.ok ? (r.msg.Length > 0 ? r.msg : action + ".") : r.msg, r.ok);
        });
    }

    private async Task UndoCheck(SetupCheck c)
    {
        if (_busy || c.JournalId == null) return;
        string id = c.JournalId;
        await Busy("A repor…", async () =>
        {
            var r = await Task.Run(() => ChangeJournal.TryUndo(id));
            ShowFlash(r.ok ? $"{c.Title}: {r.msg}" : r.msg, r.ok);
        });
    }

    private async Task ChangeHz(GamerMonitor m, int hz, (string Id, int OriginalHz)? entry)
    {
        if (_busy || hz == m.CurrentHz) return;
        int old = m.CurrentHz;
        bool goingBack = entry is { } e && e.OriginalHz == hz;
        var bullets = new List<string>
        {
            "O ecrã pode ficar preto um ou dois segundos.",
            $"Se a imagem não aparecer ou ficar estranha, não faças nada: volta sozinho a {old} Hz em {RevertSeconds} segundos.",
        };
        bullets.Add(goingBack ? "Volta à taxa que tinhas antes (sai da Central Desfazer)." : "Fica na Central Desfazer: podes repor com um clique.");
        if (!GamerDialogs.Confirm(this, $"Pôr o {m.Name} a {hz} Hz?", $"Agora está a {old} Hz.", $"Pôr a {hz} Hz", bullets, "\uE7F4")) return;

        _busy = true;
        try
        {
            ShowFlash($"A mudar o {m.Name} para {hz} Hz…", null);
            string? err = await Task.Run(() => GamerSetup.TryRefresh(m, hz));
            if (err != null)
            {
                ShowFlash(err, false);
                GamerDialogs.Info(this, "Não foi possível mudar", err, warn: true);
                return;
            }
            await Task.Delay(500); // deixa o monitor acertar a imagem antes de perguntar
            bool keep = GamerDialogs.Countdown(this, "Manter esta taxa?", $"O {m.Name} está agora a {hz} Hz. Se não confirmares, volta a {old} Hz.",
                                               RevertSeconds, "Manter", $"Voltar a {old} Hz");
            if (keep)
            {
                var r = await Task.Run(() => GamerSetup.KeepRefresh(m, old, hz));
                ShowFlash(r.msg, r.ok);
            }
            else
            {
                await Task.Run(() => GamerSetup.RevertRefresh(m, old));
                ShowFlash($"Ficou como estava: {old} Hz.", true);
            }
        }
        catch (Exception ex)
        {
            try { await Task.Run(() => GamerSetup.RevertRefresh(m, old)); } catch { }
            ShowFlash("Algo correu mal e voltei à taxa anterior: " + ex.Message, false);
        }
        finally
        {
            _busy = false;
            await RefreshAsync();
        }
    }

    private async Task ClearShaders(long total)
    {
        if (_busy) return;
        var bullets = new List<string>
        {
            "Fecha os jogos e os launchers antes (Steam, Epic, …). Os ficheiros em uso ficam onde estão.",
            "O primeiro arranque de cada jogo demora mais (volta a preparar os shaders).",
            $"Os ficheiros vão para uma cópia durante {ShaderCache.KeepDays} dias: podes desfazer na Central Desfazer.",
        };
        if (!GamerDialogs.Confirm(this, $"Limpar a cache de shaders ({ShaderCache.Size(total)})?",
                "Ajuda quando há engasgos ou erros gráficos depois de atualizar o driver.", "Limpar", bullets, "\uE74D")) return;
        await Busy("A limpar a cache…", async () =>
        {
            var r = await Task.Run(ShaderCache.Clear);
            ShowFlash(r.msg, r.ok);
            await RefreshShadersAsync();
        });
    }

    private async Task UndoShaders(ShaderBackupInfo bk)
    {
        if (_busy) return;
        await Busy("A repor a cache…", async () =>
        {
            var r = await Task.Run(() => ChangeJournal.TryUndo(bk.EntryId));
            ShowFlash(r.msg, r.ok);
            await RefreshShadersAsync();
        });
    }

    private async Task DeleteShaderCopy(ShaderBackupInfo bk)
    {
        if (_busy) return;
        if (!GamerDialogs.Confirm(this, "Apagar a cópia da cache?", $"Liberta {ShaderCache.Size(bk.Bytes)}. Depois disto já não dá para desfazer a limpeza.",
                "Apagar", null, "\uE74D", danger: true)) return;
        await Busy("A apagar a cópia…", async () =>
        {
            long freed = await Task.Run(ShaderCache.DeleteBackups);
            ShowFlash($"Cópia apagada: {ShaderCache.Size(freed)} libertados.", true);
            await RefreshShadersAsync();
        });
    }

    private async Task Busy(string what, Func<Task> work)
    {
        _busy = true;
        ShowFlash(what, null);
        try { await work(); }
        catch (Exception ex) { ShowFlash("Não consegui: " + ex.Message, false); }
        finally
        {
            _busy = false;
            await RefreshAsync();
        }
    }

    // ================================================================== mensagens

    /// <summary>ok: true = verde, false = vermelho, null = a trabalhar (azul, sem desaparecer).</summary>
    private void ShowFlash(string msg, bool? ok)
    {
        string key = ok == true ? "Good" : ok == false ? "Bad" : "Accent";
        _flash.Background = Soft(key, 0x16);
        _flash.BorderBrush = Soft(key, 0x55);
        _flashIcon.Text = ok == true ? "\uE73E" : ok == false ? "\uE7BA" : "\uE823";
        _flashIcon.Foreground = Ui.Res(key);
        _flashText.Text = msg;
        if (_flash.Visibility != Visibility.Visible)
        {
            _flash.Visibility = Visibility.Visible;
            _flash.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        _flashTimer.Stop();
        if (ok != null) _flashTimer.Start();
    }

    private void HideFlash()
    {
        var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
        a.Completed += (_, _) => { if (!_flashTimer.IsEnabled) { _flash.Visibility = Visibility.Collapsed; _flash.BeginAnimation(OpacityProperty, null); } };
        _flash.BeginAnimation(OpacityProperty, a);
    }

    // ================================================================== peças pequenas

    private static string StateColor(SetupState s) => s switch
    {
        SetupState.Good => "Good",
        SetupState.Improve => "Warn",
        SetupState.Optional => "Accent",
        _ => "Faint",
    };

    private static SolidColorBrush Soft(string key, byte alpha)
    {
        var c = Ui.Res(key) is SolidColorBrush sc ? sc.Color : Colors.Gray;
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private static Border StatusPill(string text, string colorKey, double size = 11, Thickness? margin = null)
    {
        var fg = colorKey == "Faint" ? Ui.Res("Muted") : Ui.Res(colorKey);
        var b = new Border
        {
            CornerRadius = new CornerRadius(99), Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center,
            Background = Soft(colorKey == "Faint" ? "Muted" : colorKey, 0x1C), BorderBrush = Soft(colorKey == "Faint" ? "Muted" : colorKey, 0x45),
            BorderThickness = new Thickness(1), Margin = margin ?? new Thickness(0),
            Child = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = fg, HorizontalAlignment = HorizontalAlignment.Center },
        };
        return b;
    }

    private static Border InfoTag(string text, Brush? fg = null)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Ui.Res("Hover"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = fg ?? Ui.Res("Muted"), FontWeight = fg != null ? FontWeights.SemiBold : FontWeights.Normal },
        };
    }

    private static T WithTip<T>(T el, string tip) where T : FrameworkElement
    {
        el.ToolTip = tip;
        return el;
    }

    private static Border Tile(string glyph, Brush color, double size = 40)
    {
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3), Background = Ui.Res("AccentSoft"), Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = Icons, FontSize = size * 0.44, Foreground = color,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static FrameworkElement Advice(string text, string colorKey, Thickness? margin = null)
    {
        var row = new DockPanel { Margin = margin ?? new Thickness(0, 4, 0, 0) };
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = Ui.Res(colorKey), Margin = new Thickness(0, 2, 10, 2) };
        DockPanel.SetDock(bar, Dock.Left);
        row.Children.Add(bar);
        row.Children.Add(Ui.T(text, "Body", 13));
        return row;
    }

    private static Border Divider(Thickness margin) => new() { Height = 1, Background = Ui.Res("Line"), Margin = margin };

    private static StackPanel IconText(string glyph, string text, Brush? iconColor = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = iconColor ?? Ui.Res("Accent2") });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    /// <summary>Anel de progresso (fração 0–1) com o arco a crescer.</summary>
    private static Grid Ring(double size, double thick, double fraction, Brush color)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse { Stroke = Ui.Res("Line"), StrokeThickness = thick });
        double units = Math.PI * (size - thick) / thick;
        double fill = Math.Clamp(fraction, 0, 1) * units;
        if (fill > 0.01)
        {
            var arc = new Ellipse
            {
                Stroke = color, StrokeThickness = thick, StrokeDashCap = fraction >= 0.999 ? PenLineCap.Flat : PenLineCap.Round,
                StrokeDashArray = new DoubleCollection { fill, units * 2 }, RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(-90),
            };
            arc.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(fill, 0, TimeSpan.FromMilliseconds(700)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            g.Children.Add(arc);
        }
        return g;
    }

    private static void Fade(UIElement el)
    {
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    private void ScrollTo(FrameworkElement el, bool highlight = false)
    {
        el.BringIntoView(new Rect(0, -40, el.ActualWidth, el.ActualHeight + 80));
        if (highlight && el is Border b)
        {
            var brush = Soft("Accent", 0x30).Clone();
            b.Background = brush;
            brush.BeginAnimation(SolidColorBrush.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1600)) { BeginTime = TimeSpan.FromMilliseconds(300) });
        }
    }

    /// <summary>Anel a girar enquanto lê.</summary>
    private sealed class ProgressRing : Grid
    {
        public ProgressRing(double size)
        {
            Width = size; Height = size;
            Children.Add(new Ellipse { Stroke = Ui.Res("Line"), StrokeThickness = 5 });
            double units = Math.PI * (size - 5) / 5;
            var arc = new Ellipse
            {
                Stroke = Ui.Res("AccentGradient"), StrokeThickness = 5, StrokeDashCap = PenLineCap.Round,
                StrokeDashArray = new DoubleCollection { units * 0.28, units * 2 }, RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var rot = new RotateTransform();
            arc.RenderTransform = rot;
            rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
            Children.Add(arc);
        }
    }
}
