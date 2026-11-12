using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FrameForge.Core;
using FrameForge.Games;
using FrameForge.Optimize;
using FrameForge.Overclock;
using Microsoft.Win32;

namespace FrameForge.Pages;

public partial class GamesPage : UserControl
{
    private List<LibGame> _games = new();
    private CancellationTokenSource? _run;
    private TextBlock? _status;

    public GamesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (_games.Count == 0) await Scan(); };
    }

    private async Task Scan()
    {
        _games = await Task.Run(() => GameLibrary.Scan(AppServices.Settings));
        Fill();
        if (_games.Count == 0)
        {
            Detail.Children.Clear();
            Detail.Children.Add(Ui.T("Não encontrei jogos (Steam, Epic, Riot, GOG, Ubisoft, Battle.net, EA). Usa 'Adicionar .exe'.", "MutedText"));
        }
        else if (List.SelectedIndex < 0) List.SelectedIndex = 0;
    }

    private void Fill()
    {
        string q = Search.Text.Trim();
        List.Items.Clear();
        foreach (var g in _games.Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 5, 4, 5) };
            row.Children.Add(new Image { Source = ShellIcon.For(g.IconSource), Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0) });
            var txt = new StackPanel();
            txt.Children.Add(new TextBlock { Text = g.Name, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("Fg"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 });
            txt.Children.Add(new TextBlock { Text = g.StoreBadge + (g.AntiCheat != null ? "  ·  " + g.AntiCheatBadge : ""), FontSize = 11, Foreground = Ui.Res("Muted") });
            row.Children.Add(txt);
            List.Items.Add(new ListBoxItem { Content = row, Tag = g });
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => Fill();
    private async void Rescan_Click(object sender, RoutedEventArgs e) => await Scan();

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Jogo (*.exe)|*.exe", Title = "Escolhe o executável do jogo" };
        if (dlg.ShowDialog() != true) return;
        AppServices.Settings.ManualGames.Add(new ManualGame { Name = Path.GetFileNameWithoutExtension(dlg.FileName), ExePath = dlg.FileName });
        AppServices.Settings.Save();
        await Scan();
    }

    private void List_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is ListBoxItem { Tag: LibGame g }) ShowGame(g);
    }

    private void ShowGame(LibGame g)
    {
        var p = AppServices.Settings.ProfileFor(g.Key);
        Detail.Children.Clear();

        // Cabeçalho + JOGAR
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var play = Ui.Btn("▶  JOGAR", async (_, _) => await Play(g, p), "BtnAccent");
        play.FontSize = 16; play.Padding = new Thickness(26, 10, 26, 10);
        DockPanel.SetDock(play, Dock.Right);
        head.Children.Add(play);
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal };
        titleStack.Children.Add(new Image { Source = ShellIcon.For(g.IconSource), Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0) });
        var ts = new StackPanel();
        ts.Children.Add(Ui.T(g.Name, "H1"));
        ts.Children.Add(Ui.T($"{g.StoreBadge} · {g.InstallDir}", "MutedText"));
        titleStack.Children.Add(ts);
        head.Children.Add(titleStack);
        Detail.Children.Add(head);

        _status = Ui.T("", "Body", color: Ui.Res("Accent"));
        _status.Margin = new Thickness(0, 0, 0, 10);
        Detail.Children.Add(_status);
        if (_run != null) Detail.Children.Add(Ui.Btn("Parar acompanhamento", (_, _) => _run?.Cancel(), "BtnDanger"));

        // Anti-cheat
        if (g.AntiCheat is AntiCheatInfo ac)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.T($"🛡 Anti-cheat: {ac.Name}" + (ac.Kernel ? " (kernel)" : ""), "H2"));
            sp.Children.Add(Ui.T(ac.Notes, "Body", color: Ui.Res("Muted")));
            sp.Children.Add(Ui.T("O FrameForge com este jogo: mede frames por ETW (o Windows conta-os, como o PresentMon), lê sensores pelo driver e pelos contadores do sistema, " +
                                 "e o overlay é uma janela à parte. Não injeta DLLs, não abre o processo do jogo, não lê memória. O overclock é feito no driver, fora do jogo." +
                                 (ac.Kernel ? " Mantém os 'Sensores avançados da CPU' desligados com este jogo (usam um driver de kernel que alguns anti-cheats rejeitam)." : ""), "Body"));
            Detail.Children.Add(Ui.Card(sp, Ui.Res("Good")));
        }

        // Perfil
        var prof = new StackPanel();
        prof.Children.Add(Ui.T("Perfil deste jogo", "H2"));

        var lvl = new ComboBox { ItemsSource = TweakCatalog.LevelNames.Select((n, i) => i == 0 ? "Não mexer" : $"{n} (só durante o jogo)").ToList(), SelectedIndex = p.OptimizeLevel };
        lvl.SelectionChanged += (_, _) => { p.OptimizeLevel = lvl.SelectedIndex; AppServices.Settings.Save(); };
        prof.Children.Add(Field("Otimização do Windows", lvl, "Aplica os ajustes desse nível que fazem efeito na hora e desfaz quando o jogo fecha. Os que pedem reinício aplicam-se no separador Otimização."));

        var ocNames = new List<string> { "Não mexer" };
        ocNames.AddRange(GpuTuner.Presets(AppServices.Tuner.Read()).Where(x => x.Name != "Stock").Select(x => x.Name));
        ocNames.AddRange(AppServices.Settings.OcProfiles.Select(x => x.Name).Where(n => !ocNames.Contains(n)));
        var oc = new ComboBox { ItemsSource = ocNames, SelectedItem = p.OcProfile ?? "Não mexer" };
        if (oc.SelectedIndex < 0) oc.SelectedIndex = 0;
        oc.SelectionChanged += (_, _) =>
        {
            string? name = oc.SelectedIndex <= 0 ? null : (string)oc.SelectedItem;
            if (name != null && !AppServices.Settings.OcProfiles.Any(x => x.Name == name))
            {
                var preset = GpuTuner.Presets(AppServices.Tuner.Read()).FirstOrDefault(x => x.Name == name);
                if (preset != null) AppServices.Settings.OcProfiles.Add(preset);
            }
            p.OcProfile = name;
            AppServices.Settings.Save();
        };
        prof.Children.Add(Field("Overclock da GPU", oc, "Aplicado ao carregar em JOGAR e reposto quando o jogo fecha. Perfis instáveis (que já causaram crash) são ignorados."));

        prof.Children.Add(PriorityField(g, p));
        prof.Children.Add(Check("Gravar sessão e gerar relatório de quebras no fim", p.RecordSession, v => p.RecordSession = v));
        prof.Children.Add(Check("Plano de energia de alto desempenho durante o jogo (se a otimização estiver em 'Não mexer')", p.HighPerfPlan, v => p.HighPerfPlan = v));
        prof.Children.Add(Check("Mostrar overlay externo (requer janela ou janela sem bordas; não funciona em ecrã inteiro exclusivo)", p.ShowOverlay, v => p.ShowOverlay = v));

        var close = new TextBox { Text = string.Join(", ", p.CloseBeforeLaunch) };
        close.LostFocus += (_, _) =>
        {
            p.CloseBeforeLaunch = close.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)).ToList();
            AppServices.Settings.Save();
        };
        prof.Children.Add(Field("Fechar antes de jogar (nomes de processos, separados por vírgula)", close, "Ex.: chrome, msedge, Overwolf. Pede para fechar como o botão X — nada é morto à força, não perdes trabalho."));

        var args = new TextBox { Text = p.ExtraArgs ?? "" };
        args.LostFocus += (_, _) => { p.ExtraArgs = args.Text.Trim().Length > 0 ? args.Text.Trim() : null; AppServices.Settings.Save(); };
        prof.Children.Add(Field("Argumentos de arranque extra", args, g.Store == "steam" ? "Na Steam, os argumentos vão nas 'Opções de arranque' do jogo (Propriedades). Aqui só valem para jogos abertos pelo .exe." : "Passados ao executável."));

        Detail.Children.Add(Ui.Card(prof));

        // Dicas
        var tips = new StackPanel();
        tips.Children.Add(Ui.T("Dicas para este jogo", "H2"));
        foreach (var t in GameTips.For(g.Name))
        {
            var x = Ui.T("• " + t, "Body");
            x.Margin = new Thickness(0, 0, 0, 6);
            tips.Children.Add(x);
        }
        Detail.Children.Add(Ui.Card(tips, Ui.Res("Accent")));
    }

    /// <summary>
    /// Prioridade do jogo, gravada no Windows (Image File Execution Options): mantém-se depois de reiniciar e é o Windows que a aplica
    /// quando o jogo arranca — a app não abre o processo do jogo.
    /// </summary>
    private static FrameworkElement PriorityField(LibGame g, GameProfile p)
    {
        // Que .exe é o jogo: o escolhido no perfil, o conhecido da loja, ou os encontrados na pasta
        var exes = new List<string>();
        void Add(string? e) { if (!string.IsNullOrWhiteSpace(e)) { var n = ProcessPriority.NormalizeExe(e); if (!exes.Contains(n, StringComparer.OrdinalIgnoreCase)) exes.Add(n); } }
        Add(p.LastProcess); Add(p.ExeOverride); Add(g.ExePath); Add(g.GuessedExe);
        foreach (var n in g.ProcessNames) Add(n);
        // O .exe que corre de facto primeiro: o detetado na última sessão, depois "-Shipping" (Unreal), e lançadores/bootstraps no fim
        string? last = p.LastProcess != null ? ProcessPriority.NormalizeExe(p.LastProcess) : null;
        int Rank(string e) =>
            last != null && e.Equals(last, StringComparison.OrdinalIgnoreCase) ? 0 :
            e.Contains("-Shipping", StringComparison.OrdinalIgnoreCase) ? 1 :
            System.Text.RegularExpressions.Regex.IsMatch(e, "launcher|bootstrap|start|crash|setup|update", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 9 : 5;
        exes = exes.Select((e, i) => (e, i)).OrderBy(x => Rank(x.e)).ThenBy(x => x.i).Select(x => x.e).ToList();

        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 6) };
        sp.Children.Add(Ui.T("PRIORIDADE DO JOGO NO PROCESSADOR (FICA GRAVADA, MESMO DEPOIS DE REINICIAR)", "Label"));
        if (exes.Count == 0) { sp.Children.Add(Ui.T("Não sei qual é o .exe deste jogo: escolhe-o com 'Adicionar .exe' ou usa a página Prioridade dos programas.", "MutedText", 12)); return sp; }

        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var exe = new ComboBox { MinWidth = 260, Margin = new Thickness(0, 0, 10, 4), ToolTip = "Executável do jogo a que a prioridade se aplica" };
        foreach (var e in exes) exe.Items.Add(new ComboBoxItem { Content = e, Tag = e });
        string? withRule = exes.FirstOrDefault(e => ProcessPriority.Read(e) != null);
        exe.SelectedIndex = Math.Max(0, withRule != null ? exes.IndexOf(withRule) : 0);
        var prio = new ComboBox { MinWidth = 240, Margin = new Thickness(0, 0, 10, 4) };
        foreach (var c in new[] { CpuPrio.Auto, CpuPrio.AboveNormal, CpuPrio.High, CpuPrio.Normal })
            prio.Items.Add(new ComboBoxItem { Content = ProcessPriority.Name(c) + (c == CpuPrio.AboveNormal ? " — recomendado" : ""), Tag = c, ToolTip = ProcessPriority.Explain(c) });
        var info = Ui.T("", "MutedText", 12);
        bool loading = false;
        void Load()
        {
            loading = true;
            var cur = exe.SelectedItem is ComboBoxItem { Tag: string e } ? ProcessPriority.Read(e)?.Cpu ?? CpuPrio.Auto : CpuPrio.Auto;
            foreach (ComboBoxItem it in prio.Items) it.IsSelected = (CpuPrio)it.Tag == cur;
            info.Text = ProcessPriority.Explain(cur);
            loading = false;
        }
        exe.SelectionChanged += (_, _) => Load();
        prio.SelectionChanged += (_, _) =>
        {
            if (loading || exe.SelectedItem is not ComboBoxItem { Tag: string e } || prio.SelectedItem is not ComboBoxItem { Tag: CpuPrio c }) return;
            try
            {
                ProcessPriority.SetCpu(e, c); // só a CPU: a prioridade de disco (e valores de outros programas) ficam como estão
                info.Text = ProcessPriority.Explain(c) + (c == CpuPrio.Auto ? "" : $"  Gravado para {e}: vale a partir da próxima vez que o jogo abrir (não é preciso reiniciar o PC).") +
                            (g.AntiCheat != null ? " Com anti-cheat é o Windows que a aplica (a app não toca no jogo); se o jogo repuser a prioridade dele, o JOGAR avisa." : "") +
                            (g.AntiCheat != null && c == CpuPrio.High ? " ⚠ Em jogos competitivos com anti-cheat recomendamos 'Acima do normal': 'Alta' pode tirar CPU ao som e ao rato." : "");
            }
            catch (Exception ex) { info.Text = "Não consegui gravar: " + ex.Message; }
        };
        Load();
        row.Children.Add(exe);
        row.Children.Add(prio);
        sp.Children.Add(row);
        sp.Children.Add(info);
        return sp;
    }

    private static FrameworkElement Field(string label, FrameworkElement input, string? help = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 6) };
        sp.Children.Add(Ui.T(label.ToUpperInvariant(), "Label"));
        input.Margin = new Thickness(0, 4, 0, 2);
        input.HorizontalAlignment = HorizontalAlignment.Left;
        input.MinWidth = 320;
        sp.Children.Add(input);
        if (help != null) sp.Children.Add(Ui.T(help, "MutedText", 12));
        return sp;
    }

    private static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
        c.Click += (_, _) => { set(c.IsChecked == true); AppServices.Settings.Save(); };
        return c;
    }

    private async Task Play(LibGame g, GameProfile p)
    {
        if (_run != null) { _status!.Text = "Já há um jogo a ser acompanhado."; return; }
        _run = new CancellationTokenSource();
        ShowGame(g);
        try
        {
            await GameRunner.RunAsync(g, p, msg => Dispatcher.Invoke(() => { if (_status != null) _status.Text = msg; }), _run.Token);
        }
        finally
        {
            _run = null;
        }
    }
}
