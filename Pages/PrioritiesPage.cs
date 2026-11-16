using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FrameForge.Optimize;
using Microsoft.Win32;

namespace FrameForge.Pages;

/// <summary>
/// Prioridade de cada programa, como no Gestor de Tarefas → Detalhes → Definir prioridade, mas GRAVADA:
/// mantém-se depois de reiniciar e é aplicada pelo próprio Windows sempre que o programa abre.
/// </summary>
public sealed class PrioritiesPage : UserControl
{
    private readonly StackPanel _rules = new();
    private readonly ComboBox _running = new() { MinWidth = 320 };
    private readonly TextBlock _msg = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<Action> _live = new();

    // Processos do sistema e anti-cheat que não se devem mexer
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.exe", "Registry.exe", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "svchost.exe",
        "dwm.exe", "fontdrvhost.exe", "audiodg.exe", "MsMpEng.exe", "vgc.exe", "vgtray.exe", "EasyAntiCheat.exe", "EasyAntiCheat_EOS.exe",
        "BEService.exe", "BEService_x64.exe", "FACEIT.exe", "faceitservice.exe", "explorer.exe", "HyperCore.exe", "FrameForge.exe", Process.GetCurrentProcess().ProcessName + ".exe",
    };

    public PrioritiesPage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = Ui.PageMargin };

        // ---- explicação
        var ex = new StackPanel();
        ex.Children.Add(Ui.T("O que é a prioridade (em 20 segundos)", "H2"));
        ex.Children.Add(Ui.T("Quando vários programas querem o processador ao mesmo tempo, o Windows atende primeiro os de prioridade mais alta. " +
                             "Não dá mais potência ao PC: só decide quem passa à frente na fila.", "Body"));
        foreach (var p in new[] { CpuPrio.AboveNormal, CpuPrio.High, CpuPrio.BelowNormal, CpuPrio.Idle })
        {
            var t = Ui.T($"• {ProcessPriority.Name(p)}: {ProcessPriority.Explain(p)}", "Body");
            t.Margin = new Thickness(0, 6, 0, 0);
            ex.Children.Add(t);
        }
        var rt = Ui.T("• Tempo real: não está disponível de propósito — pode congelar o rato, o teclado e o som e obrigar a desligar o PC no botão.", "Body", color: Ui.Res("Warn"));
        rt.Margin = new Thickness(0, 6, 0, 0);
        ex.Children.Add(rt);
        var ac = Ui.T("Jogos com anti-cheat: é o próprio Windows que aplica a prioridade quando o jogo arranca — o HyperCore nunca abre o processo do jogo. " +
                      "Alguns jogos/anti-cheats repõem a prioridade deles ao arrancar; nesse caso a regra simplesmente não tem efeito (vês na coluna 'Agora').", "MutedText");
        ac.Margin = new Thickness(0, 10, 0, 0);
        ex.Children.Add(ac);
        root.Children.Add(Ui.Card(ex, Ui.Res("Accent")));

        // ---- adicionar
        var add = new StackPanel();
        add.Children.Add(Ui.T("Adicionar um programa", "H2"));
        add.Children.Add(Ui.T("Escolhe um programa aberto agora, procura o .exe, ou escreve o nome (ex.: VALORANT-Win64-Shipping.exe).", "MutedText"));
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        _running.Margin = new Thickness(0, 0, 12, 8);
        row.Children.Add(_running);
        var addChosen = Ui.Btn("Adicionar o escolhido", (_, _) => { if (_running.SelectedItem is ComboBoxItem { Tag: string exe }) AddRule(exe); }, "BtnAccent");
        addChosen.Margin = new Thickness(0, 0, 8, 8);
        row.Children.Add(addChosen);
        var refresh = Ui.Btn("↻", (_, _) => FillRunning());
        refresh.Margin = new Thickness(0, 0, 0, 8);
        row.Children.Add(refresh);
        add.Children.Add(row);
        var row2 = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
        var typed = new TextBox { Width = 320, Margin = new Thickness(0, 0, 12, 8), ToolTip = "Nome do executável" };
        row2.Children.Add(typed);
        var addByName = Ui.Btn("Adicionar pelo nome", (_, _) => AddRule(typed.Text));
        addByName.Margin = new Thickness(0, 0, 8, 8);
        row2.Children.Add(addByName);
        var searchExe = Ui.Btn("Procurar .exe…", (_, _) =>
        {
            var d = new OpenFileDialog { Filter = "Programa (*.exe)|*.exe" };
            if (d.ShowDialog() == true) AddRule(d.FileName);
        });
        searchExe.Margin = new Thickness(0, 0, 0, 8);
        row2.Children.Add(searchExe);
        add.Children.Add(row2);
        _msg.Style = (Style)Ui.Theme["MutedAuto"];
        _msg.Foreground = Ui.Res("Accent");
        add.Children.Add(_msg);
        root.Children.Add(Ui.Card(add));

        // ---- regras
        var h = Ui.T("Regras gravadas neste PC", "H2");
        h.Margin = new Thickness(0, 10, 0, 6);
        root.Children.Add(h);
        root.Children.Add(Ui.T("Inclui regras criadas por outros programas (ex.: Process Lasso). Mudar para 'Automático' nas duas apaga a regra.", "MutedText"));
        _rules.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_rules);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        Loaded += (_, _) => { FillRunning(); BuildRules(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) => { foreach (var a in _live.ToList()) a(); };
    }

    private void FillRunning()
    {
        _running.Items.Clear();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.SessionId != 0 && p.MainWindowHandle != IntPtr.Zero) names.Add(p.ProcessName + ".exe"); } catch { }
            p.Dispose();
        }
        foreach (var n in names.Where(n => !Blocked.Contains(n)))
            _running.Items.Add(new ComboBoxItem { Content = n, Tag = n });
        if (_running.Items.Count > 0) _running.SelectedIndex = 0;
    }

    private void AddRule(string nameOrPath)
    {
        string exe = ProcessPriority.NormalizeExe(nameOrPath);
        if (exe.Length == 0) { _msg.Text = "Escreve ou escolhe um programa."; return; }
        if (Blocked.Contains(exe)) { _msg.Text = $"{exe} é um processo do Windows ou de anti-cheat: não se mexe nele (pode deixar o PC instável ou o jogo recusar abrir)."; return; }
        try
        {
            if (ProcessPriority.Read(exe) is PriorityRule existing)
                _msg.Text = $"{exe} já tinha uma regra ({ProcessPriority.Name(existing.Cpu)}). Muda-a em baixo.";
            else
            {
                ProcessPriority.SetCpu(exe, CpuPrio.AboveNormal);
                _msg.Text = $"{exe} adicionado com 'Acima do normal'. Aplica-se da próxima vez que abrir — fecha e abre o programa.";
            }
        }
        catch (Exception ex) { _msg.Text = "Não consegui gravar: " + ex.Message + " (a app tem de estar como administrador)."; }
        BuildRules();
    }

    private void BuildRules()
    {
        _rules.Children.Clear();
        _live.Clear();
        var rules = ProcessPriority.ReadAll();
        if (rules.Count == 0) { _rules.Children.Add(Ui.T("Ainda não há regras.", "MutedText")); return; }
        foreach (var r in rules) _rules.Children.Add(RuleCard(r));
    }

    private Border RuleCard(PriorityRule r)
    {
        var sp = new StackPanel();
        var head = new DockPanel();
        var remove = Ui.Btn("Remover", (_, _) => { ProcessPriority.Set(r.Exe, CpuPrio.Auto, IoPrio.Auto); BuildRules(); }, "BtnDanger");
        DockPanel.SetDock(remove, Dock.Right);
        head.Children.Add(remove);
        var now = Ui.T("", "MutedText");
        now.Margin = new Thickness(0, 0, 16, 0);
        now.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(now, Dock.Right);
        head.Children.Add(now);
        void Live() => now.Text = ProcessPriority.RunningNow(r.Exe) is string s ? $"Agora: a correr em {s}" : "Agora: não está aberto";
        Live();
        _live.Add(Live);
        head.Children.Add(Ui.T(r.Exe, "H2"));
        sp.Children.Add(head);

        // Valores que a app não conhece (postos por outro programa) aparecem como "Outro" e NUNCA são reescritos sem o utilizador mudar esse campo
        var cpu = new ComboBox { MinWidth = 260 };
        if (r.UnknownCpu) cpu.Items.Add(new ComboBoxItem { Content = $"Outro (valor {r.RawCpu}, de outro programa)", Tag = "keep", IsSelected = true });
        foreach (var p in new[] { CpuPrio.Auto, CpuPrio.High, CpuPrio.AboveNormal, CpuPrio.Normal, CpuPrio.BelowNormal, CpuPrio.Idle })
            cpu.Items.Add(new ComboBoxItem { Content = ProcessPriority.Name(p), Tag = p, IsSelected = !r.UnknownCpu && p == r.Cpu, ToolTip = ProcessPriority.Explain(p) });
        var io = new ComboBox { MinWidth = 200 };
        if (r.UnknownIo) io.Items.Add(new ComboBoxItem { Content = $"Outro (valor {r.RawIo}, de outro programa)", Tag = "keep", IsSelected = true });
        foreach (var p in new[] { IoPrio.Auto, IoPrio.Normal, IoPrio.Low, IoPrio.VeryLow })
            io.Items.Add(new ComboBoxItem { Content = ProcessPriority.Name(p), Tag = p, IsSelected = !r.UnknownIo && p == r.Io });
        var explain = Ui.T(r.UnknownCpu ? "Esta regra tem um valor que a app não usa (provavelmente Tempo real ou de outra ferramenta)." : ProcessPriority.Explain(r.Cpu), "MutedText", 12);
        void Saved(string what)
        {
            explain.Text = what + "  Fica gravado — aplica-se da próxima vez que o programa abrir.";
            if (ProcessPriority.Read(r.Exe) == null) BuildRules(); // ficou tudo em Automático: a regra desaparece
        }
        // Cada caixa grava SÓ o seu valor
        cpu.SelectionChanged += (_, _) =>
        {
            if (cpu.SelectedItem is not ComboBoxItem { Tag: CpuPrio c }) return;
            try { ProcessPriority.SetCpu(r.Exe, c); Saved(ProcessPriority.Explain(c)); }
            catch (Exception ex) { explain.Text = "Não consegui gravar: " + ex.Message; }
        };
        io.SelectionChanged += (_, _) =>
        {
            if (io.SelectedItem is not ComboBoxItem { Tag: IoPrio i }) return;
            try { ProcessPriority.SetIo(r.Exe, i); Saved($"Disco: {ProcessPriority.Name(i)}."); }
            catch (Exception ex) { explain.Text = "Não consegui gravar: " + ex.Message; }
        };
        var w = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        w.Children.Add(Labeled("Prioridade do processador", cpu));
        w.Children.Add(Labeled("Prioridade do disco (leitura/escrita)", io));
        sp.Children.Add(w);
        sp.Children.Add(explain);
        return Ui.Card(sp);
    }

    private static FrameworkElement Labeled(string label, FrameworkElement input)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 20, 6) };
        sp.Children.Add(Ui.T(label.ToUpperInvariant(), "Label"));
        input.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(input);
        return sp;
    }
}
