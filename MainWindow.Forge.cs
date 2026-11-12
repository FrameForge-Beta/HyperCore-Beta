using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using FrameForge;
using FrameForge.Core;
using FrameForge.Pages;
using FrameForge.Telemetry;

namespace ResetExplode;

/// <summary>
/// Integra as páginas do FrameForge, o atalho de gravação e os avisos quando um relatório de sessão fica pronto.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, UserControl> _forgePages = new();
    private readonly Dictionary<string, (System.Windows.Controls.Primitives.ToggleButton Toggle, StackPanel Items)> _navGroups = new();
    private const int HotRecord = 0x4652;
    private const int HotUserOverlay = 0x4658;
    // Ctrl+Alt + / − (teclado principal e numérico): tamanho do overlay
    private static readonly (int Id, uint Vk, double Step)[] HotOverlaySize =
        { (0x4654, 0xBB, 0.1), (0x4655, 0x6B, 0.1), (0x4656, 0xBD, -0.1), (0x4657, 0x6D, -0.1) };

    /// <summary>Cria cada página uma vez e guarda-a (mantém o estado ao trocar de página).</summary>
    private UserControl ForgePage(string key)
    {
        if (_forgePages.TryGetValue(key, out var page)) return page;
        page = key switch
        {
            "home" => new HomePage(),
            "glossary" => new GlossaryPage(),
            "fans" => new FansPage(),
            "priorities" => new PrioritiesPage(),
            "overlay" => new OverlayPage(),
            "games" => new GamesPage(),
            "sessions" => new SessionsPage(),
            "optimize" => new OptimizePage(),
            "maintenance" => new MaintenancePage(),
            "oc" => new OverclockPage(),
            "tips" => new TipsPage(),
            "settings" => new SettingsPage(SetTrayMode),
            _ => new DashboardPage(),
        };
        return _forgePages[key] = page;
    }

    // ---------------- Ajuda: o que faz cada página, em linguagem simples ----------------

    private static readonly Dictionary<string, (string Title, string Text)> PageHelp = new()
    {
        ["FF:home"] = ("Início", "Escolhe o que queres fazer e a app leva-te à página certa. Se é a primeira vez, segue os 4 passos no fundo."),
        ["PageDash"] = ("Visão geral", "Resumo do PC em tempo real: gráfico de temperatura/carga da CPU e da GPU, componentes, memória e uma lista de saúde do PC. Só mostra — não muda nada."),
        ["FF:games"] = ("Jogar e gravar", "Os teus jogos encontrados automaticamente. Escolhe um, define o perfil (otimização/overclock só durante o jogo) e carrega em JOGAR. Quando fechares o jogo, aparece o relatório de quebras. Seguro com anti-cheat."),
        ["FF:sessions"] = ("Relatórios de quebras", "Cada sessão gravada com nota de A a E, FPS, 1% low e a causa de cada engasgo. Também podes gravar um jogo já aberto aqui ou com Ctrl+Alt+R dentro do jogo."),
        ["FF:overlay"] = ("Overlay (editor)", "Monta o que aparece por cima do jogo: arrasta os elementos, escolhe sensores, cores, tamanhos, gráficos e alertas, e define a combinação de teclas que mostra/esconde o overlay. Precisa do jogo em 'janela sem bordas'."),
        ["PageBottleneck"] = ("Gargalo ao vivo", "Diz em tempo real que peça está a limitar o FPS enquanto jogas (põe o jogo em janela para veres os dois)."),
        ["PageGames"] = ("Os meus resultados", "FPS médio e 1% low realmente medidos nos jogos que já gravaste, com nota, quebras e data. Não são estimativas: vêm das tuas sessões."),
        ["PageOverlay"] = ("Overlay clássico", "O overlay original (lista simples de itens). Para escolher qualquer sensor, posição e cor, usa o Overlay (editor)."),
        ["PageLauncher"] = ("Biblioteca clássica", "O launcher original, com menu de gráficos e gravação das opções de arranque da Steam. Para relatórios de quebras usa 'Jogar e gravar'."),
        ["FF:optimize"] = ("Níveis de desempenho", "Ajustes do Windows para jogos, por níveis. Cada um diz o ganho real e o que perdes. 'Restaurar tudo' devolve o PC exatamente como estava. Nada desliga a segurança do Windows."),
        ["FF:priorities"] = ("Prioridade dos programas", "Escolhe que programas passam à frente na fila do processador — como no Gestor de Tarefas → Detalhes → Definir prioridade, mas fica gravado: o Windows aplica sozinho sempre que o programa abre, mesmo depois de reiniciar. Não precisa de reiniciar o PC: basta fechar e abrir o programa."),
        ["FF:maintenance"] = ("Manutenção do PC", "Limpa ficheiros temporários antigos, abre a ferramenta nativa de limpeza do Windows, desfragmenta apenas HDDs e em sequência, e calcula o espaço ocupado por pastas. SSDs e discos não identificados são sempre excluídos da desfragmentação."),
        ["PageOptimize"] = ("Limpeza rápida", "Liberta memória e fecha programas de fundo antes de jogar. Não mexe em jogos abertos."),
        ["FF:oc"] = ("Overclock da gráfica", "Estado da placa em tempo real (clocks, temperatura, consumo e ventoinha) em qualquer marca. A escrita de offsets, limite de energia e ventoinha é só para NVIDIA (NVAPI/NVML); em AMD/Intel usa-se o painel do fabricante. Nada fica gravado na placa."),
        ["FF:fans"] = ("Ventoinhas", "1) Escolhe o que medir (uma temperatura ou a média de várias), 2) desenha a curva, 3) diz que ventoinhas a seguem. Tudo volta ao automático quando desligas ou fechas a app. O assistente faz o exemplo da média núcleo + hotspot da gráfica."),
        ["PageOc"] = ("Teste de estabilidade", "Confirma se um overclock é estável (CPU, GPU ou ambos) e mostra o guia da BIOS para a tua placa-mãe e processador."),
        ["FF:diag"] = ("Diagnóstico", "Lê o PC e aponta o que está a custar desempenho: RAM misturada ou sem XMP, monitor abaixo dos Hz máximos, driver antigo, programas em conflito. Só lê — as correções são tuas."),
        ["PageSystem"] = ("Sistema", "Ficha técnica completa do PC. Útil para pedir ajuda ou vender o PC (podes exportar o relatório)."),
        ["PageSensors"] = ("Sensores", "Todas as leituras do PC em tempo real (temperaturas, clocks, consumos, ventoinhas). Procura ou filtra por tipo."),
        ["PageGpu"] = ("Placa de vídeo", "Tudo sobre a gráfica, como no GPU-Z: chip, memória, PCIe, clocks, limites de energia e sensores."),
        ["PageDisks"] = ("Discos", "Saúde de cada SSD/HDD (S.M.A.R.T.). Se aparecer 'Atenção' ou 'Ruim', faz cópia de segurança dos teus ficheiros."),
        ["PageEnergy"] = ("Energia", "Bateria e consumo da CPU/GPU. Cria cópias de planos do Windows, ajusta opções de CPU/ecrã/suspensão e ativa ou remove planos personalizados."),
        ["PageNet"] = ("Rede", "Teste de ~30 s: ping, jitter, perda de pacotes, velocidade e bufferbloat. Diz se o problema é a tua rede (Wi-Fi/router) ou o operador."),
        ["PageBench"] = ("Benchmark", "Testes que dão uma nota à CPU, GPU, ray tracing, memória e disco. Bom para comparar antes/depois de um ajuste. Põe o PC a trabalhar a fundo durante o teste."),
        ["PageStress"] = ("Stress test", "Põe CPU/RAM/GPU a 100% para ver temperaturas máximas e estabilidade. Pára sozinho se passar do limite de temperatura que escolheres."),
        ["PageStartup"] = ("Arranque do Windows", "Programas que abrem sozinhos com o Windows. Desligar não desinstala nada e pode ser desfeito."),
        ["PageApps"] = ("Instalar apps", "Instala os essenciais (Steam, Discord, Chrome…) de uma vez, pelas fontes oficiais (winget)."),
        ["PageDriver"] = ("Drivers", "Lista os drivers, verifica se o da NVIDIA está atualizado e faz cópia de segurança antes de formatar."),
        ["PageInteg"] = ("Iluminação RGB", "Controla as luzes do PC num só sítio (Corsair iCUE, OpenRGB para MSI/ASUS/Gigabyte, Razer). Em teste."),
        ["PageMakerPorts"] = ("Portas COM", "Para Arduino/ESP32: limpa portas COM fantasma e números COM presos quando a placa não aparece."),
        ["PageMakerDrv"] = ("Drivers seriais", "Diagnóstico e instalação dos drivers CH340, CP210x, FTDI, Prolific e Arduino (incluídos na app, funcionam sem internet)."),
        ["FF:tips"] = ("Dicas e avisos", "O que dá mesmo mais FPS, o que é mito, definições da NVIDIA e da BIOS, e porque é que esta app não dá ban."),
        ["FF:glossary"] = ("Glossário", "Cada palavra técnica explicada de forma simples."),
        ["FF:settings"] = ("Configurações", "Escolhe se o FrameForge inicia com o Windows e se abre ou fica na área de notificação."),
    };

    private void UpdateHelp(string tag)
    {
        bool show = AppServices.Settings.ShowHelp && PageHelp.ContainsKey(tag);
        HelpBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (PageHelp.TryGetValue(tag, out var h)) { HelpTitle.Text = "O que é esta página: " + h.Title; HelpText.Text = h.Text; }
    }

    private void HelpHide_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.ShowHelp = false;
        AppServices.Settings.Save();
        HelpBar.Visibility = Visibility.Collapsed;
        Toast("Explicações escondidas", "Carrega no ? da barra de título para as voltar a ver.", "");
    }

    private void HelpToggle_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.ShowHelp = !AppServices.Settings.ShowHelp;
        AppServices.Settings.Save();
        UpdateHelp(_currentTag);
    }

    private string _currentTag = "FF:home";

    /// <summary>Abre uma página pelo tag (usado pelos cartões do Início): marca o item do menu e abre a categoria.</summary>
    private void BuildNavigation()
    {
        NavList.Children.Clear();
        AddNavigationItem("FF:home", "Início", "");
        AddNavigationItem("PageDash", "Visão geral", "");

        AddNavigationGroup("JOGAR", ("FF:games", "Jogos", ""), ("PageLauncher", "Biblioteca de jogos", ""),
            ("FF:sessions", "Sessões e relatórios", ""), ("FF:overlay", "Overlay de jogo", ""),
            ("PageOverlay", "Overlay simples", ""), ("PageBottleneck", "Gargalo ao vivo", ""),
            ("PageGames", "Os meus resultados", ""));
        AddNavigationGroup("GANHAR DESEMPENHO", ("FF:optimize", "Níveis de desempenho", ""),
            ("FF:priorities", "Prioridade dos programas", ""), ("FF:maintenance", "Manutenção do PC", ""),
            ("PageOptimize", "Limpeza rápida", ""), ("FF:fans", "Ventoinhas", ""),
            ("FF:oc", "Overclock da gráfica", ""), ("PageOc", "Teste de estabilidade", ""));
        AddNavigationGroup("DIAGNÓSTICO DO PC", ("FF:diag", "Diagnóstico", ""), ("PageSystem", "Sistema", ""),
            ("PageSensors", "Sensores", ""), ("PageGpu", "Placa gráfica", ""), ("PageDisks", "Discos", ""),
            ("PageEnergy", "Energia", ""), ("PageNet", "Rede", ""));
        AddNavigationGroup("FERRAMENTAS", ("PageBench", "Benchmark", ""), ("PageStress", "Stress test", ""),
            ("PageStartup", "Arranque do Windows", ""), ("PageApps", "Instalar aplicações", ""),
            ("PageDriver", "Drivers", ""), ("PageInteg", "Iluminação RGB", ""), ("PageMakerPorts", "Portas COM", ""),
            ("PageMakerDrv", "Drivers seriais", ""));
        AddNavigationGroup("AJUDA", ("FF:tips", "Dicas e avisos", ""), ("FF:glossary", "Glossário", ""));
        AddNavigationItem("FF:settings", "Configurações", "");
    }

    private void AddNavigationGroup(string title, params (string Tag, string Label, string Icon)[] entries)
    {
        var toggle = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = title, Style = (Style)FindResource("NavGroup"), IsChecked = false
        };
        var items = new StackPanel { Margin = new Thickness(10, 0, 0, 4), Visibility = Visibility.Collapsed };
        toggle.Checked += (_, _) =>
        {
            foreach (var group in _navGroups.Values)
                if (!ReferenceEquals(group.Toggle, toggle)) group.Toggle.IsChecked = false;
            items.Visibility = Visibility.Visible;
        };
        toggle.Unchecked += (_, _) => items.Visibility = Visibility.Collapsed;
        NavList.Children.Add(toggle);
        NavList.Children.Add(items);
        _navGroups[title] = (toggle, items);
        foreach (var (tag, label, icon) in entries) AddNavigationItem(tag, label, icon, items);
    }

    private void AddNavigationItem(string tag, string label, string icon, Panel? parent = null)
    {
        var item = new RadioButton { Style = (Style)FindResource("NavItem"), Tag = tag, ToolTip = PageHelp.TryGetValue(tag, out var h) ? h.Text : label };
        item.Checked += Nav_Checked;
        item.Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            new TextBlock { Text = icon, FontFamily = (System.Windows.Media.FontFamily)FindResource("Icons"), FontSize = 14, Width = 28, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }
        }};
        (parent ?? NavList).Children.Add(item);
    }

    private void NavigateTo(string tag)
    {
        var rb = FindNav(NavScroll, tag);
        if (rb == null) return;
        foreach (var group in _navGroups.Values)
            if (group.Items.Children.Contains(rb)) group.Toggle.IsChecked = true;
        rb.IsChecked = true;
        rb.BringIntoView();
    }

    private static RadioButton? FindNav(DependencyObject root, string tag)
    {
        if (root is RadioButton { Tag: string t } r && t == tag) return r;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (FindNav(child, tag) is RadioButton found) return found;
        return null;
    }

    /// <summary>Chamado pelo construtor (InitForge) depois do InitializeComponent.</summary>
    private void UpdateRestartBar()
    {
        var pending = RestartNotice.Pending;
        RestartBar.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RestartText.Text = string.Join("  ·  ", pending);
    }

    private void RestartNow_Click(object sender, RoutedEventArgs e) => RestartNotice.Request(); // abre o aviso (com 1 s de espera) para confirmar

    private void InitForge()
    {
        BuildNavigation();
        NavigateTo("FF:home");
        RestartNotice.Init();
        RestartNotice.Changed += () => Dispatcher.Invoke(UpdateRestartBar);
        UpdateRestartBar();
        AppServices.NavigateRequested += tag => Dispatcher.Invoke(() => NavigateTo(tag));
        FrameForge.Overlay.OverlayStore.Changed += () => Dispatcher.BeginInvoke(new Action(ApplyOverlayHotkey));
        AppServices.Notify += msg => Dispatcher.Invoke(() => Toast("FrameForge", msg, ""));
        AppServices.Recorder.Finished += (r, _) => Dispatcher.Invoke(() =>
        {
            Toast($"Relatório de {r.Data.Game}: nota {r.Grade}", r.Verdict + " — abre em FRAMEFORGE › Relatórios de quebras.", "");
            if (_forgePages.TryGetValue("sessions", out var s)) ((SessionsPage)s).Reload();
        });
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            RegisterHotKey(h, HotRecord, 0x0001 | 0x0002 | 0x4000, 0x52);       // Ctrl+Alt+R
            foreach (var k in HotOverlaySize) RegisterHotKey(h, k.Id, 0x0001 | 0x0002, k.Vk); // com repetição: manter carregado vai crescendo
            ApplyOverlayHotkey();
            HwndSource.FromHwnd(h)?.AddHook(ForgeHotkeys);
        };
        Closed += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(h, HotRecord);
            UnregisterOverlayHotkey();
            foreach (var k in HotOverlaySize) UnregisterHotKey(h, k.Id);
            FrameForge.OverlayWindow.HideOverlay();
        };
    }

    private IntPtr ForgeHotkeys(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0312) return IntPtr.Zero;
        int id = wParam.ToInt32();
        if (id == HotRecord) { handled = true; _ = ToggleRecord(); }
        else if (id == HotUserOverlay)
        {
            handled = true;
            ToggleUserOverlay();
        }
        else if (HotOverlaySize.FirstOrDefault(k => k.Id == id) is { Id: > 0 } size)
        {
            handled = true;
            var p = FrameForge.Overlay.OverlayStore.Active;
            p.Scale = Math.Clamp(Math.Round(p.Scale + size.Step, 2), 0.5, 2.5);
            FrameForge.Overlay.OverlayStore.Save(); // o overlay aberto e o editor reconstroem-se sozinhos
        }
        return IntPtr.Zero;
    }

    private void ApplyOverlayHotkey()
    {
        var settings = FrameForge.Overlay.OverlayStore.Data;
        var hwnd = new WindowInteropHelper(this).Handle;
        UnregisterOverlayHotkey();
        uint modifiers = settings.HotkeyModifiers & HotkeyModifiersMask;
        uint key = settings.HotkeyVirtualKey;
        bool keySupported = key is >= 0x30 and <= 0x5A or >= 0x60 and <= 0x69 or >= 0x70 and <= 0x87;
        if (hwnd == IntPtr.Zero || !settings.HotkeyEnabled || modifiers == 0 || !keySupported) return;
        if (RegisterHotKey(hwnd, HotUserOverlay, modifiers | 0x4000, key))
            _userOverlayHotkeyRegistered = true;
        else
            AppServices.Toast("A combinação de teclas do overlay já está a ser utilizada por outra aplicação.");
    }

    private void UnregisterOverlayHotkey()
    {
        if (!_userOverlayHotkeyRegistered) return;
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HotUserOverlay);
        _userOverlayHotkeyRegistered = false;
    }

    private void ToggleUserOverlay()
    {
        if (FrameForge.OverlayWindow.IsShown)
        {
            FrameForge.OverlayWindow.HideOverlay();
            return;
        }
        int foreground = SystemSampler.ForegroundPid();
        FrameForge.OverlayWindow.ShowFor(AppServices.Frames.ActivePresenters().Any(p => p.Pid == foreground) ? foreground : 0);
    }

    private async Task ToggleRecord()
    {
        var rec = AppServices.Recorder;
        if (rec.Active)
        {
            Toast("FrameForge", "A analisar a sessão…", "");
            await rec.StopAsync();
            return;
        }
        int pid = SystemSampler.ForegroundPid();
        if (!AppServices.Frames.ActivePresenters().Any(p => p.Pid == pid))
        {
            Toast("FrameForge", "Ctrl+Alt+R: a janela em primeiro plano não está a desenhar frames.", "");
            return;
        }
        string name = SystemSampler.ProcessName(pid) ?? $"PID {pid}";
        await rec.StartAsync(pid, name);
        Toast("A gravar " + name, "Ctrl+Alt+R outra vez para parar e gerar o relatório.", "");
    }
    // RegisterHotKey/UnregisterHotKey: declarados em MainWindow.xaml.cs
}
