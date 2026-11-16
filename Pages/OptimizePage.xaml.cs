using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FrameForge.Core;
using FrameForge.Hardware;
using FrameForge.Optimize;

namespace FrameForge.Pages;

/// <summary>
/// Níveis de desempenho: 4 cartões (Seguro → Extremo), uma linha de estado e a lista de ajustes recolhida.
/// O nível aplicado e os ajustes mudados à mão ficam em AppSettings (sobrevivem a reiniciar a app e o PC).
/// Tudo o que lê o Windows (powercfg, registo, WMI) corre em segundo plano.
/// </summary>
public partial class OptimizePage : UserControl
{
    private static AppSettings S => AppServices.Settings;
    private static List<Tweak> All => AppServices.Tweaks;

    private SystemProbe? _probe;
    private int _selected;               // nível escolhido nos cartões (0 = ainda nada)
    private bool _busy, _refreshing;
    private bool _planOk;                // o plano ativo está como o nível guardado pede (lido em segundo plano)
    private readonly HashSet<string> _expanded = new();

    private readonly RadioButton[] _cards = new RadioButton[5];
    private readonly Border[] _activeTags = new Border[5];
    private readonly TextBlock[] _counts = new TextBlock[5];
    private readonly List<(ToggleButton Switch, Tweak Tweak)> _switches = new();
    private readonly List<Button> _noticeButtons = new();

    public OptimizePage()
    {
        InitializeComponent();
        KeepBox.IsChecked = S.KeepLevelApplied;
        BuildCards();

        // A página é criada uma vez e guardada: estas subscrições duram o tempo da app
        LevelKeeper.Changed += src =>
        {
            if (src == this) return; // foi a própria página a pedir a verificação
            Dispatcher.InvokeAsync(async () => { if (IsLoaded) await RefreshAsync(); });
        };
        RestartNotice.Changed += () => Dispatcher.InvokeAsync(() => { if (IsLoaded && !_busy) UpdateAll(); });
        Loaded += async (_, _) => await RefreshAsync();
    }

    // ------------------------------------------------------------------ leitura do estado

    /// <summary>Volta a ler todos os ajustes (em segundo plano) e redesenha.</summary>
    private async Task RefreshAsync()
    {
        if (_busy || _refreshing) return;
        _refreshing = true;
        try
        {
            if (_probe == null)
            {
                StatusTitle.Text = "A ler o estado do Windows…";
                StatusDetail.Text = "";
                _probe = await AppServices.Probe;
            }
            var probe = _probe;
            await Task.Run(() =>
            {
                lock (TweakCatalog.Gate)
                {
                    LevelKeeper.Recheck(probe, this);
                    AdoptEarlierLevel(probe);
                    var plan = All.FirstOrDefault(t => t.IsPlan);
                    _planOk = plan != null && plan.Satisfied(TweakCatalog.Target(S.AppliedOptimizeLevel, probe));
                }
            });
            if (_selected == 0) _selected = S.AppliedOptimizeLevel > 0 ? S.AppliedOptimizeLevel : probe.IsLaptop ? 1 : 2;
        }
        catch (Exception ex)
        {
            Result.Text = "Não consegui ler o estado: " + ex.Message;
            if (_probe == null) { StatusTitle.Text = "Não consegui ler o hardware deste PC"; StatusDetail.Text = "Fecha e volta a abrir a app."; }
        }
        finally { _refreshing = false; }
        UpdateAll();
    }

    /// <summary>
    /// Versões antigas não guardavam o nível. Se não há nível guardado mas a app já tinha aplicado um nível inteiro
    /// (há valores originais guardados), assume esse nível para o utilizador não o "perder".
    /// Só uma vez (na primeira abertura desta versão): depois de um "Repor" que falhe em parte, não volta a ligar um nível sozinha.
    /// </summary>
    private static void AdoptEarlierLevel(SystemProbe p)
    {
        if (S.LegacyLevelChecked) return;
        // Com um JOGAR a correr, os ajustes temporários do jogo parecem um nível aplicado: adia-se para a próxima abertura
        if (FrameForge.Games.GameRunner.TemporaryTweaksActive || FrameForge.Games.GameMode.IsActive) return;
        TweakBackup.MigrateLegacy(); // os originais do cartão antigo contam como "mudado pela app" (o arranque pode ainda não o ter feito)
        S.LegacyLevelChecked = true;
        if (S.AppliedOptimizeLevel == 0 && S.TweakOverrides.Count == 0 && All.Any(t => t.Touched))
        {
            int found = 0;
            for (int n = 1; n <= 4; n++)
            {
                var tg = TweakCatalog.Target(n, p);
                var lv = All.Where(t => t.Enabled && t.Level <= n).ToList();
                if (lv.Count == 0 || !lv.All(t => t.Satisfied(tg))) break;
                if (lv.Any(t => t.Touched && t.Level == n)) found = n;
            }
            if (found > 0) S.AppliedOptimizeLevel = found;
        }
        S.Save();
    }

    /// <summary>Ligado como o nível guardado pede (o plano usa a leitura feita em segundo plano).</summary>
    private bool Ok(Tweak t) => t.IsPlan ? _planOk : t.State == true;

    private static bool Desired(Tweak t) => LevelKeeper.Desired(t, S);

    private static bool HasOverride(Tweak t) => S.TweakOverrides.ContainsKey(t.Id);

    private static string Plural(int n, string one, string many) => n == 1 ? one : many;

    // ------------------------------------------------------------------ cartões de nível

    private void BuildCards()
    {
        for (int i = 1; i <= 4; i++)
        {
            int lv = i;
            var name = Ui.T(TweakCatalog.LevelNames[i], "H2");
            name.Margin = new Thickness(0);
            var tag = Ui.Pill("ATIVO", (Brush)FindResource("AccentGradient"), Brushes.White);
            tag.Margin = new Thickness(6, 0, 0, 0);
            tag.Visibility = Visibility.Collapsed;
            var top = new DockPanel();
            DockPanel.SetDock(tag, Dock.Right);
            top.Children.Add(tag);
            top.Children.Add(name);

            var gain = Ui.T(TweakCatalog.LevelGains[i], "Body", 12.5);
            gain.Margin = new Thickness(0, 8, 0, 0);
            var who = Ui.T(TweakCatalog.LevelAudience[i], "MutedText", 12);
            who.Margin = new Thickness(0, 6, 0, 0);
            var count = Ui.T("", "Label");
            count.Margin = new Thickness(0, 10, 0, 0);

            var body = new StackPanel();
            body.Children.Add(top);
            body.Children.Add(gain);
            body.Children.Add(who);
            body.Children.Add(count);

            var card = new RadioButton
            {
                Style = (Style)FindResource("LevelCard"), Content = body, IsEnabled = false,
                VerticalAlignment = VerticalAlignment.Stretch, ToolTip = TweakCatalog.LevelDescriptions[i],
            };
            card.Checked += (_, _) => { if (_selected != lv) { _selected = lv; UpdateAll(); } };
            _cards[i] = card;
            _activeTags[i] = tag;
            _counts[i] = count;
            LevelCards.Children.Add(card);
        }
    }

    private void UpdateCards()
    {
        int applied = S.AppliedOptimizeLevel;
        for (int i = 1; i <= 4; i++)
        {
            int n = All.Count(t => t.Level <= i && t.Enabled);
            int skip = All.Count(t => t.Level <= i && !t.Enabled);
            _counts[i].Text = $"{n} {Plural(n, "AJUSTE", "AJUSTES")}" + (skip > 0 ? $" · {skip} NÃO SE {Plural(skip, "APLICA", "APLICAM")}" : "");
            _activeTags[i].Visibility = i == applied ? Visibility.Visible : Visibility.Collapsed;
            _cards[i].IsEnabled = _probe != null && !_busy;
            if (i == _selected && _cards[i].IsChecked != true) _cards[i].IsChecked = true;
        }
    }

    // ------------------------------------------------------------------ estado + avisos

    private void UpdateAll()
    {
        if (_probe == null) return;
        UpdateCards();

        int applied = S.AppliedOptimizeLevel;
        var desired = All.Where(t => t.Enabled && Desired(t)).ToList();
        var drifted = desired.Where(t => t.State != null && !Ok(t)).ToList();
        var unknown = desired.Where(t => t.State == null).ToList();
        int working = desired.Count(Ok);
        int manual = All.Count(HasOverride);
        bool anyTouched = All.Any(t => t.Touched);
        string planName = All.FirstOrDefault(t => t.IsPlan)?.Detail ?? "";

        var detail = new List<string>();
        if (applied == 0 && manual == 0)
        {
            StatusTitle.Text = anyTouched ? "Personalizado" : "Nenhum nível aplicado";
            detail.Add(anyTouched ? "Há ajustes que a app mudou fora de um nível. Escolhe um nível ou repõe o original."
                                  : "O Windows está como veio. Escolhe um nível e carrega em Aplicar.");
        }
        else
        {
            string name = manual > 0 ? "Personalizado" : TweakCatalog.LevelNames[applied];
            StatusTitle.Text = $"Ativo: {name} · {working} de {desired.Count} {Plural(desired.Count, "ajuste", "ajustes")} a funcionar";
            if (manual > 0)
                detail.Add((applied > 0 ? $"Base {TweakCatalog.LevelNames[applied]}, com " : "Com ") +
                           $"{manual} {Plural(manual, "ajuste mudado", "ajustes mudados")} à mão.");
            int skip = All.Count(t => !t.Enabled && t.Level <= applied);
            if (skip > 0) detail.Add($"{skip} {Plural(skip, "ajuste não se aplica", "ajustes não se aplicam")} a este PC.");
        }
        if (planName.Length > 0) detail.Add(planName + ".");
        StatusDetail.Text = string.Join(" ", detail);

        // Avisos (só os que pedem atenção)
        Notices.Children.Clear();
        _noticeButtons.Clear();
        if (drifted.Count > 0)
            Notices.Children.Add(Notice(
                $"{drifted.Count} {Plural(drifted.Count, "ajuste foi reposto", "ajustes foram repostos")} pelo Windows ou por outro programa.",
                "Bad", "Reaplicar", () => ReapplyAsync(drifted, instantOnly: false),
                string.Join("\n", drifted.Select(t => "• " + t.Title))));
        if (unknown.Count > 0)
            Notices.Children.Add(Notice(
                $"Não consegui ler {unknown.Count} {Plural(unknown.Count, "ajuste", "ajustes")} (aparece com ? na lista).", "Muted", null, null,
                string.Join("\n", unknown.Select(t => "• " + t.Title))));
        var restart = All.Where(t => t.RestartToFinish && RestartNotice.Pending.Contains(t.Title)).ToList();
        if (restart.Count > 0)
            Notices.Children.Add(Notice(
                $"Reinicia o PC para terminar de aplicar {restart.Count} {Plural(restart.Count, "ajuste", "ajustes")}.",
                "Warn", "Reiniciar…", () => { RestartNotice.Request(); return Task.CompletedTask; },
                string.Join("\n", restart.Select(t => "• " + t.Title))));

        // Botões
        string sel = TweakCatalog.LevelNames[Math.Clamp(_selected, 1, 4)];
        bool nothingToDo = _selected == applied && manual == 0 && drifted.Count == 0 && working == desired.Count;
        ApplyBtn.Content = nothingToDo ? $"{sel} está ativo" : $"Aplicar {sel}";
        ApplyBtn.IsEnabled = !_busy && !nothingToDo;
        RestoreBtn.IsEnabled = !_busy && (anyTouched || applied > 0 || manual > 0);
        KeepBox.IsEnabled = !_busy;

        BuildList(drifted);
    }

    private UIElement Notice(string text, string colorKey, string? action, Func<Task>? onClick, string? tip)
    {
        var dot = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, Foreground = Ui.Res(colorKey),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        };
        var msg = Ui.T(text, "Body", 13);
        msg.VerticalAlignment = VerticalAlignment.Center;
        var dock = new DockPanel { Margin = new Thickness(0, 8, 0, 0), ToolTip = tip };
        DockPanel.SetDock(dot, Dock.Left);
        dock.Children.Add(dot);
        if (action != null && onClick != null)
        {
            var b = Ui.Btn(action, async (_, _) => { if (!_busy) await onClick(); });
            b.Padding = new Thickness(12, 5, 12, 5);
            b.Margin = new Thickness(12, 0, 0, 0);
            b.FontSize = 12;
            b.IsEnabled = !_busy;
            DockPanel.SetDock(b, Dock.Right);
            dock.Children.Add(b);
            _noticeButtons.Add(b);
        }
        dock.Children.Add(msg);
        return dock;
    }

    // ------------------------------------------------------------------ lista de ajustes

    private void BuildList(List<Tweak> drifted)
    {
        TweaksHeader.Text = $"Ver os ajustes ({All.Count})";
        List.Children.Clear();
        _switches.Clear();
        var ordered = All.OrderBy(t => t.Level).ToList();
        for (int i = 0; i < ordered.Count; i++)
            List.Children.Add(Row(ordered[i], drifted.Contains(ordered[i]), last: i == ordered.Count - 1));
    }

    private Border Row(Tweak t, bool drifted, bool last)
    {
        // Interruptor (cinzento quando não foi possível ler o estado)
        var sw = new ToggleButton
        {
            Style = (Style)FindResource("Switch"), IsChecked = t.IsOn, IsEnabled = t.Enabled && !_busy,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 14, 0),
            Opacity = t.IsUnknown && t.Enabled ? 0.5 : 1,
            ToolTip = !t.Enabled ? "Não se aplica a este PC." : t.IsUnknown ? "Não consegui ler o estado no Windows." : null,
        };
        sw.Click += async (_, _) => await ToggleAsync(t, sw.IsChecked == true, sw);
        _switches.Add((sw, t));

        // Nome + ganho numa linha + uma linha de estado quando interessa
        var text = new StackPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = t.What };
        var titleRow = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left };
        var chevron = new TextBlock
        {
            Text = _expanded.Contains(t.Id) ? "" : "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 9,
            Foreground = Ui.Res("Faint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 2, 0, 0),
        };
        DockPanel.SetDock(chevron, Dock.Right);
        titleRow.Children.Add(chevron);
        titleRow.Children.Add(Ui.T(t.Title, "Body", 13.5, weight: FontWeights.SemiBold));
        text.Children.Add(titleRow);
        var gain = Ui.T(t.Short, "MutedText", 12);
        gain.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(gain);

        string? line = null; Brush? lineColor = null;
        if (!t.Enabled) { line = "Não se aplica a este PC: " + t.Blocked; lineColor = Ui.Res("Faint"); }
        else if (drifted) { line = "Reposto pelo Windows ou por outro programa."; lineColor = Ui.Res("Warn"); }
        else if (t.Status.Length > 0) { line = t.Status; lineColor = t.Status.StartsWith("Falhou") ? Ui.Res("Bad") : Ui.Res("Muted"); }
        if (t.IsPlan && t.Detail.Length > 0) line = line == null ? t.Detail + "." : line + " " + t.Detail + "."; // sempre o nome do plano ativo
        if (line != null)
        {
            var l = Ui.T(line, "MutedText", 11.5, lineColor ?? Ui.Res("Muted"));
            l.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(l);
        }

        // Detalhes (abrem ao clicar na linha)
        var more = new StackPanel { Margin = new Thickness(0, 8, 0, 2), Visibility = _expanded.Contains(t.Id) ? Visibility.Visible : Visibility.Collapsed };
        more.Children.Add(Ui.T(t.What, "Body", 12.5));
        var g = Ui.T("Ganho: " + t.Gain, "Body", 12.5, Ui.Res("Good"));
        g.Margin = new Thickness(0, 5, 0, 0);
        more.Children.Add(g);
        var w = Ui.T("Atenção: " + t.Warning, "Body", 12.5, Ui.Res("Warn"));
        w.Margin = new Thickness(0, 4, 0, 0);
        more.Children.Add(w);
        if (t.RestartToFinish) more.Children.Add(Small("Fica completo depois de reiniciar o PC."));
        if (!t.IsPlan && t.Detail.Length > 0) more.Children.Add(Small(t.Detail + "."));
        text.Children.Add(more);
        text.MouseLeftButtonUp += (_, _) =>
        {
            bool open = more.Visibility != Visibility.Visible;
            more.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? "" : "";
            if (open) _expanded.Add(t.Id); else _expanded.Remove(t.Id);
        };

        // Etiquetas pequenas à direita: desconhecido, reiniciar, à mão, nível
        var pills = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 1, 0, 0) };
        if (t.IsUnknown && t.Enabled)
        {
            var q = Ui.Pill("?", Ui.Res("Hover"), Ui.Res("Muted"));
            q.ToolTip = "Estado desconhecido: não consegui ler este valor no Windows.";
            pills.Children.Add(q);
        }
        if (t.RestartToFinish)
        {
            var r = Ui.Pill("REINICIAR", Ui.Res("AccentSoft"), Ui.Res("Accent2"));
            r.ToolTip = "Ao ligar ou desligar, só fica completo depois de reiniciar o PC.";
            pills.Children.Add(r);
        }
        if (HasOverride(t))
        {
            var m = Ui.Pill("À MÃO", Ui.Res("Hover"), Ui.Res("Muted"));
            m.ToolTip = "Mudaste este ajuste fora do nível.";
            pills.Children.Add(m);
        }
        var lvl = Ui.Pill(TweakCatalog.LevelNames[t.Level].ToUpperInvariant(), Ui.Res("Hover"), Ui.Res("Muted"));
        lvl.Margin = new Thickness(0);
        pills.Children.Add(lvl);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(pills, 2);
        grid.Children.Add(sw);
        grid.Children.Add(text);
        grid.Children.Add(pills);
        if (!t.Enabled) text.Opacity = 0.6;

        return new Border
        {
            Child = grid, Padding = new Thickness(0, 12, 0, 12),
            BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1),
        };
    }

    private static TextBlock Small(string s)
    {
        var t = Ui.T(s, "MutedText", 12);
        t.Margin = new Thickness(0, 4, 0, 0);
        return t;
    }

    // ------------------------------------------------------------------ ações

    private void SetBusy(bool busy, string? msg = null)
    {
        _busy = busy;
        if (msg != null) Result.Text = msg;
        ApplyBtn.IsEnabled = RestoreBtn.IsEnabled = KeepBox.IsEnabled = !busy;
        for (int i = 1; i <= 4; i++) _cards[i].IsEnabled = !busy && _probe != null;
        foreach (var b in _noticeButtons) b.IsEnabled = !busy;
        foreach (var (sw, t) in _switches) sw.IsEnabled = !busy && t.Enabled;
    }

    /// <summary>O que "Aplicar" vai fazer, calculado em segundo plano (lê o plano ativo).</summary>
    private sealed record ApplyPlan(List<Tweak> Undo, List<Tweak> Manual, bool PlanDown, int ToTurnOn, int Skipped);

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_probe == null || _busy || _selected < 1) return;
        var probe = _probe;
        int sel = _selected, applied = S.AppliedOptimizeLevel;
        var tg = TweakCatalog.Target(sel, probe);
        string name = TweakCatalog.LevelNames[sel];

        SetBusy(true, "A preparar…");
        ApplyPlan plan;
        try
        {
            plan = await Task.Run(() =>
            {
                lock (TweakCatalog.Gate)
                {
                    foreach (var t in All) t.Refresh(probe);
                    // Ajustes acima do nível escolhido que a app ligou: desfazê-los só com confirmação
                    var above = All.Where(t => t.Enabled && t.Level > sel && t.IsOn && t.Touched).ToList();
                    var manual = above.Where(t => S.TweakOverrides.TryGetValue(t.Id, out var v) && v).ToList();
                    // Descer de Competitivo/Extremo para Seguro/Equilibrado troca o Desempenho Máximo pelo Alto desempenho
                    var p = All.FirstOrDefault(t => t.IsPlan);
                    bool planDown = p != null && p.Enabled && !KeptOff(p) && tg.Plan == TweakCatalog.HighPerf && p.Touched &&
                                    TweakCatalog.ActiveScheme() == TweakCatalog.Ultimate;
                    int toOn = All.Count(t => t.Enabled && t.Level <= sel && !KeptOff(t) && !t.Satisfied(tg));
                    int skipped = All.Count(t => t.Level <= sel && !t.Enabled);
                    return new ApplyPlan(above.Except(manual).ToList(), manual, planDown, toOn, skipped);
                }
            });
        }
        catch (Exception ex) { SetBusy(false, "Falhou: " + ex.Message); UpdateAll(); return; }

        // Confirmações: nunca desfazer nada em silêncio
        int undoCount = plan.Undo.Count + (plan.PlanDown ? 1 : 0);
        if (undoCount > 0)
        {
            int from = Math.Max(applied, plan.Undo.Select(t => t.Level).DefaultIfEmpty(0).Max());
            var names = plan.Undo.Select(t => "• " + t.Title).ToList();
            if (plan.PlanDown) names.Insert(0, "• Plano Desempenho Máximo → Alto desempenho");
            if (Ask($"Vais desfazer {undoCount} {Plural(undoCount, "ajuste", "ajustes")} do nível {TweakCatalog.LevelNames[Math.Clamp(from, 1, 4)]}:\n\n" +
                    string.Join("\n", names) + $"\n\nContinuar com o nível {name}?") != MessageBoxResult.Yes)
            { SetBusy(false, ""); UpdateAll(); return; }
        }
        bool undoManual = false;
        if (plan.Manual.Count > 0)
            undoManual = Ask($"Ligaste à mão {plan.Manual.Count} {Plural(plan.Manual.Count, "ajuste que não faz", "ajustes que não fazem")} parte do nível {name}:\n\n" +
                             string.Join("\n", plan.Manual.Select(t => "• " + t.Title)) +
                             "\n\nQueres desligá-los também?\n(Não = ficam ligados como estão.)") == MessageBoxResult.Yes;
        if (sel >= 3 && sel > applied && plan.ToTurnOn > 0 &&
            Ask($"O nível {name} gasta um pouco mais de energia e aquece mais em repouso" +
                (sel == 4 ? ", e desliga a indexação da pesquisa e o SysMain (a pesquisa do Windows fica mais lenta)" : "") +
                ".\n\nVolta tudo atrás em 'Repor Windows original'. Continuar?") != MessageBoxResult.Yes)
        { SetBusy(false, ""); UpdateAll(); return; }

        Result.Text = $"A aplicar {name}…";
        int on = 0, undone = 0;
        var fails = new List<string>();
        var restart = new List<(string, bool)>();
        var manualDone = new List<string>();
        await Task.Run(() =>
        {
            lock (TweakCatalog.Gate)
            {
                // 1) Desfazer o que fica acima do nível (ao contrário da aplicação: serviços/registo, energia, plano)
                var undo = plan.Undo.Concat(undoManual ? plan.Manual : Enumerable.Empty<Tweak>()).OrderByDescending(t => t.Order);
                foreach (var t in undo)
                {
                    bool was = t.IsOn;
                    if (t.Set(false, tg) is string err) { fails.Add($"{t.Title} ({err})"); continue; }
                    undone++;
                    if (plan.Manual.Contains(t)) manualDone.Add(t.Id);
                    if (t.RestartToFinish && was != t.IsOn) restart.Add((t.Title, t.IsOn));
                }
                // 2) O plano primeiro, depois as definições de energia (escrevem-se no plano), depois o resto
                var p = All.FirstOrDefault(t => t.IsPlan);
                if (plan.PlanDown && p != null)
                {
                    if (p.Set(true, tg) is string err) fails.Add($"{p.Title} ({err})"); else undone++;
                }
                foreach (var t in All.Where(t => t.Enabled && t.Level <= sel && !KeptOff(t)).OrderBy(t => t.Order))
                {
                    t.ReadState(); // o plano pode ter mudado entretanto: confirma no plano novo
                    if (t.Satisfied(tg)) continue;
                    if (t.Set(true, tg) is string err) { fails.Add($"{t.Title} ({err})"); continue; }
                    on++;
                    if (t.RestartToFinish) restart.Add((t.Title, true));
                }
            }
        });

        // Guardar a escolha (sobrevive a reiniciar): nível + só as exceções que ainda fazem sentido
        S.AppliedOptimizeLevel = sel;
        foreach (var id in manualDone) S.TweakOverrides.Remove(id);
        CleanOverrides(sel);
        S.Save();

        int pending = restart.Count > 0 ? RestartNotice.ToggledMany(restart) : 0;
        var parts = new List<string>
        {
            on == 0 && undone == 0 && fails.Count == 0 ? $"{name} ativo: já estava tudo aplicado." :
            $"{name} aplicado: {on} {Plural(on, "ajuste ligado", "ajustes ligados")}" + (undone > 0 ? $", {undone} {Plural(undone, "desfeito", "desfeitos")}" : "") + ".",
        };
        if (fails.Count > 0) parts.Add($"{fails.Count} {Plural(fails.Count, "falhou", "falharam")}: {string.Join("; ", fails)}.");
        if (plan.Skipped > 0) parts.Add($"{plan.Skipped} {Plural(plan.Skipped, "ajuste não se aplica", "ajustes não se aplicam")} a este PC.");
        if (pending > 0) parts.Add("Alguns ficam completos depois de reiniciar o PC.");
        SetBusy(false, string.Join(" ", parts));
        await RefreshAsync();
    }

    /// <summary>Ajuste do nível que o utilizador desligou à mão: Aplicar não o volta a ligar.</summary>
    private static bool KeptOff(Tweak t) => S.TweakOverrides.TryGetValue(t.Id, out var v) && !v;

    /// <summary>Tira as exceções que já coincidem com o nível (ex.: ligado à mão e depois o nível passou a incluí-lo).</summary>
    private static void CleanOverrides(int level)
    {
        foreach (var (id, on) in S.TweakOverrides.ToList())
        {
            var t = All.FirstOrDefault(x => x.Id == id);
            if (t == null || on == (level > 0 && t.Level <= level)) S.TweakOverrides.Remove(id);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_probe == null || _busy) return;
        var probe = _probe;
        var touched = All.Where(t => t.Touched).ToList();
        string q = touched.Count > 0
            ? $"Vais repor {touched.Count} {Plural(touched.Count, "ajuste", "ajustes")} exatamente como {Plural(touched.Count, "estava", "estavam")} antes de a app mexer. " +
              "O nível deixa de estar ativo.\n\nContinuar?"
            : "A app ainda não mudou nada no Windows. Queres só tirar o nível ativo?";
        if (Ask(q) != MessageBoxResult.Yes) return;

        SetBusy(true, "A repor o Windows original…");
        int ok = 0;
        var fails = new List<string>();
        var restart = new List<(string, bool)>();
        var tg = TweakCatalog.Target(Math.Max(S.AppliedOptimizeLevel, 1), probe);
        await Task.Run(() =>
        {
            lock (TweakCatalog.Gate)
                // Ao contrário da aplicação: serviços/registo, depois energia, por fim o plano
                foreach (var t in touched.OrderByDescending(t => t.Order))
                {
                    t.Refresh(probe);
                    bool was = t.IsOn;
                    // Set(false) volta ao valor original guardado (tal como estava, exact) e, se correu bem, esquece-o (TweakBackup.Forget)
                    if (t.Set(false, tg, exact: true) is string err) { fails.Add($"{t.Title} ({err})"); continue; }
                    ok++;
                    if (t.RestartToFinish && was != t.IsOn) restart.Add((t.Title, t.IsOn));
                }
        });
        S.AppliedOptimizeLevel = 0;
        S.TweakOverrides.Clear();
        S.Save();
        _selected = probe.IsLaptop ? 1 : 2;

        int pending = restart.Count > 0 ? RestartNotice.ToggledMany(restart) : 0;
        string msg = touched.Count == 0 ? "Nível retirado. Não havia nada para repor." :
                     $"Windows original reposto: {ok} {Plural(ok, "ajuste", "ajustes")}." +
                     (fails.Count > 0 ? $" {fails.Count} {Plural(fails.Count, "falhou", "falharam")}: {string.Join("; ", fails)}." : "") +
                     (pending > 0 ? " Alguns só voltam depois de reiniciar o PC." : "");
        SetBusy(false, msg);
        await RefreshAsync();
    }

    /// <summary>Interruptor de um ajuste: muda só esse e guarda-o como exceção ao nível.</summary>
    private async Task ToggleAsync(Tweak t, bool want, ToggleButton sw)
    {
        if (_probe == null || _busy) { sw.IsChecked = t.IsOn; return; }
        var probe = _probe;
        SetBusy(true, (want ? "A ligar: " : "A repor: ") + t.Title + "…");
        var tg = TweakCatalog.Target(Math.Max(S.AppliedOptimizeLevel, 1), probe);
        bool was = t.IsOn;
        string? err = await Task.Run(() => { lock (TweakCatalog.Gate) return t.Set(want, tg); });
        string msg;
        if (err == null)
        {
            bool levelDefault = S.AppliedOptimizeLevel > 0 && t.Level <= S.AppliedOptimizeLevel;
            if (want == levelDefault) S.TweakOverrides.Remove(t.Id); else S.TweakOverrides[t.Id] = want;
            S.Save();
            msg = (want ? "Ligado: " : "Reposto: ") + t.Title + ".";
            if (t.RestartToFinish && was != t.IsOn)
                msg += RestartNotice.Toggled(t.Title, t.IsOn) ? " Fica completo depois de reiniciar o PC." : " Já não é preciso reiniciar.";
        }
        else msg = $"{t.Title}: {err}";
        SetBusy(false, msg);
        await RefreshAsync();
    }

    /// <summary>Volta a aplicar o que o Windows ou outro programa repôs.</summary>
    private async Task ReapplyAsync(List<Tweak> items, bool instantOnly)
    {
        if (_probe == null || _busy) return;
        var probe = _probe;
        var list = items.Where(t => !instantOnly || t.Instant).ToList();
        if (list.Count == 0) return;
        SetBusy(true, "A reaplicar…");
        var restart = new List<(string, bool)>();
        int ok = await Task.Run(() => { lock (TweakCatalog.Gate) return LevelKeeper.Reapply(list, probe, restart); });
        int pending = restart.Count > 0 ? RestartNotice.ToggledMany(restart) : 0;
        int fail = list.Count - ok;
        SetBusy(false, $"Reapliquei {ok} {Plural(ok, "ajuste", "ajustes")}." +
                       (fail > 0 ? $" {fail} {Plural(fail, "falhou", "falharam")} (vê a lista de ajustes)." : "") +
                       (pending > 0 ? " Alguns ficam completos depois de reiniciar o PC." : ""));
        await RefreshAsync();
    }

    private async void Keep_Click(object sender, RoutedEventArgs e)
    {
        S.KeepLevelApplied = KeepBox.IsChecked == true;
        S.Save();
        if (!S.KeepLevelApplied || _probe == null) return;
        // Ligado agora: reaplica já os instantâneos que estejam repostos (como faria no próximo arranque)
        var drift = All.Where(t => t.Enabled && Desired(t) && t.State != null && !Ok(t)).ToList();
        if (drift.Any(t => t.Instant)) await ReapplyAsync(drift, instantOnly: true);
    }

    private MessageBoxResult Ask(string text)
    {
        var owner = Window.GetWindow(this);
        return owner != null
            ? MessageBox.Show(owner, text, "Níveis de desempenho", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(text, "Níveis de desempenho", MessageBoxButton.YesNo, MessageBoxImage.Question);
    }
}
