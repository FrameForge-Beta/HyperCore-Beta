using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using FrameForge.Core;
using FrameForge.Tuning;

namespace FrameForge.Pages;

/// <summary>Painel inicial com ações prioritárias e atalhos para as ferramentas principais.</summary>
public sealed class HomePage : UserControl
{
    private static readonly (string Glyph, string Eyebrow, string Title, string Text, string Tag)[] Actions =
    {
        ("", "01 · ANALISAR", "Diagnosticar o PC", "Encontra problemas de hardware, drivers e configuração.", "FF:diag"),
        ("", "02 · JOGAR", "Gravar uma sessão", "Mede os frames e ajuda a encontrar a causa provável das quebras.", "FF:games"),
        ("", "03 · AJUSTAR", "Melhorar desempenho", "Aplica ajustes reversíveis do Windows, por níveis.", "FF:optimize"),
        ("", "04 · PERSONALIZAR", "Configurar o overlay", "Escolhe sensores, posição, cores e apresentação no jogo.", "FF:overlay"),
    };

    private readonly StackPanel _scan = new();

    public HomePage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = new Thickness(32, 24, 32, 36) };

        var hero = new Border
        {
            Background = Ui.Res("AccentSoft"), BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20), Padding = new Thickness(28, 24, 28, 24), Margin = new Thickness(0, 0, 0, 24)
        };
        var heroGrid = new Grid();
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var intro = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        intro.Children.Add(Ui.T("FRAMEFORGE  /  CENTRO DE CONTROLO", "Label", 11, Ui.Res("Accent2"), FontWeights.Bold));
        intro.Children.Add(Ui.T("O teu PC. Mais claro.", "H1", 30));
        var description = Ui.T("Diagnostica, mede e ajusta o desempenho sem perder tempo à procura das ferramentas.", "MutedText", 14);
        description.Margin = new Thickness(0, 6, 20, 0);
        intro.Children.Add(description);
        Grid.SetColumn(intro, 0);
        heroGrid.Children.Add(intro);
        var heroButton = Ui.Btn("COMEÇAR DIAGNÓSTICO   ›", (_, _) => AppServices.Navigate("FF:diag"), "BtnAccent");
        heroButton.Padding = new Thickness(18, 12, 18, 12);
        heroButton.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(heroButton, 1);
        heroGrid.Children.Add(heroButton);
        hero.Child = heroGrid;
        root.Children.Add(hero);
        root.Children.Add(_scan);
        FillScan();
        ScanService.Instance.Completed += r =>
        {
            if (r.Resultado == ScanOutcome.Completo) Dispatcher.BeginInvoke(FillScan);
        };

        root.Children.Add(Ui.T("O que queres fazer?", "H1", 22));
        var sub = Ui.T("Escolhe uma ação. O resto das ferramentas está organizado no menu lateral.", "MutedText", 13);
        sub.Margin = new Thickness(0, 3, 0, 18);
        root.Children.Add(sub);

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (glyph, eyebrow, title, text, tag) in Actions)
        {
            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var icon = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(11), Background = Ui.Res("Hover"),
                Child = new TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)FindResource("Icons"), FontSize = 20, Foreground = Ui.Res("Accent2"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            DockPanel.SetDock(icon, Dock.Left);
            top.Children.Add(icon);
            var hint = Ui.T(eyebrow, "Label", 10, Ui.Res("Accent2"), FontWeights.Bold);
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.Margin = new Thickness(12, 0, 0, 0);
            top.Children.Add(hint);
            Grid.SetRow(top, 0);
            body.Children.Add(top);
            var heading = Ui.T(title, "H2", 17);
            Grid.SetRow(heading, 1);
            body.Children.Add(heading);
            var detail = Ui.T(text, "MutedText", 13);
            detail.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(detail, 2);
            body.Children.Add(detail);

            var card = Ui.Card(body);
            card.Width = 390;
            card.Margin = new Thickness(0, 0, 12, 12);
            card.MinHeight = 148;
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) => AppServices.Navigate(tag);
            card.MouseEnter += (_, _) => { card.BorderBrush = Ui.Res("Accent"); card.Background = Ui.Res("Hover"); };
            card.MouseLeave += (_, _) => { card.BorderBrush = Ui.Res("Line"); card.Background = Ui.Res("Card"); };
            grid.Children.Add(card);
        }
        root.Children.Add(grid);

        var quick = Ui.Card(new StackPanel
        {
            Children =
            {
                Ui.T("ATALHOS", "Label", 10, Ui.Res("Accent2"), FontWeights.Bold),
                Ui.T("Mais ferramentas", "H2", 16),
                Ui.T("Ventoinhas · Overclock · Benchmark · Rede · Arranque do Windows", "MutedText", 13)
            }
        });
        quick.Margin = new Thickness(0, 2, 0, 18);
        root.Children.Add(quick);

        var steps = new StackPanel();
        steps.Children.Add(Ui.T("Fluxo recomendado", "H2", 16));
        steps.Children.Add(Ui.T("1  Diagnostica o PC     →     2  Grava uma sessão     →     3  Aplica um ajuste     →     4  Compara os resultados", "MutedText", 13));
        root.Children.Add(Ui.Card(steps, Ui.Res("Accent2")));
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
    }

    /// <summary>Cartão do scan: convite para o primeiro, ou o resumo do último (nota + quem segura o PC).</summary>
    private void FillScan()
    {
        _scan.Children.Clear();
        ScanReport? last = null;
        try { last = ScanService.Instance.Last; } catch { }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(16), Background = Ui.Res("Hover"), Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        if (last?.Nota is int nota)
            left.Child = new TextBlock { Text = nota.ToString(), FontSize = 26, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                         Foreground = nota >= 80 ? Ui.Res("Good") : nota >= 60 ? Ui.Res("Warn") : Ui.Res("Bad"), ToolTip = "Nota do último scan (0–100)" };
        else
            left.Child = new TextBlock { Text = "", FontFamily = (System.Windows.Media.FontFamily)FindResource("Icons"), FontSize = 28, Foreground = Ui.Res("Accent2"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(left);
        var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (last == null)
        {
            txt.Children.Add(Ui.T("SCAN E AFINAÇÃO AUTOMÁTICA", "Label", 10, Ui.Res("Accent2"), FontWeights.Bold));
            txt.Children.Add(Ui.T("Faz um scan ao PC", "H2", 17));
            txt.Children.Add(Ui.T("Mede tudo, encontra sozinho o overclock estável da gráfica e diz o que vale a pena mudar.", "MutedText", 13));
        }
        else
        {
            txt.Children.Add(Ui.T($"ÚLTIMO SCAN · {last.Data:dd/MM/yyyy}", "Label", 10, Ui.Res("Accent2"), FontWeights.Bold));
            string main = last.Segurar.Select(f => f.Titulo).FirstOrDefault(t => t.Length > 0) ?? last.Resumo;
            txt.Children.Add(Ui.T(main.Length > 0 ? main : "Scan concluído.", "H2", 16));
            if (last.Resumo.Length > 0 && last.Resumo != main) txt.Children.Add(Ui.T(last.Resumo, "MutedText", 13));
        }
        Grid.SetColumn(txt, 1);
        grid.Children.Add(txt);
        var go = Ui.Btn(last == null ? "INICIAR SCAN   ›" : "VER RELATÓRIO   ›", (_, _) => AppServices.Navigate("FF:scan"), last == null ? "BtnAccent" : "Btn");
        go.VerticalAlignment = VerticalAlignment.Center;
        go.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(go, 2);
        grid.Children.Add(go);
        var card = Ui.Card(grid);
        card.Margin = new Thickness(0, 0, 0, 24);
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) AppServices.Navigate("FF:scan"); };
        _scan.Children.Add(card);
    }
}

/// <summary>Glossário: cada palavra técnica explicada em linguagem simples.</summary>
public sealed class GlossaryPage : UserControl
{
    private static readonly (string Term, string Text)[] Terms =
    {
        ("FPS", "Imagens por segundo. Mais FPS = movimento mais fluido. Só vês tantas como os Hz do monitor (144 Hz → até 144 imagens), mas FPS acima disso ainda reduz o atraso."),
        ("Frametime", "Quanto tempo demora cada imagem, em milissegundos. 144 FPS ≈ 6,9 ms. O importante é ser regular: um frame de 40 ms no meio de 7 ms é um engasgo, mesmo com FPS médio alto."),
        ("1% low / 0,1% low", "O FPS dos piores momentos (o 1% de frames mais lentos). Diz mais sobre a 'suavidade' do que o FPS médio. Se a média é 200 mas o 1% low é 60, vais sentir engasgos."),
        ("Quebra / stutter / engasgo", "Um frame que demora muito mais do que os outros. A app deteta cada um e diz o que estava a acontecer no PC nesse instante."),
        ("Gargalo (bottleneck)", "A peça que está a limitar o FPS. Se a gráfica está a 99%, é ela (normal). Se a gráfica tem folga e um núcleo do processador está cheio, é a CPU."),
        ("CPU / processador", "Faz a lógica do jogo (física, IA, jogadores). Jogos competitivos (Valorant, CS2) dependem muito dela."),
        ("GPU / placa gráfica", "Desenha a imagem. Mais resolução e mais qualidade gráfica = mais trabalho para ela."),
        ("VRAM", "A memória da placa gráfica (ex.: 8 GB). Quando enche (texturas muito altas), o jogo engasga. Baixar 'qualidade de texturas' resolve."),
        ("RAM", "Memória do PC. 16 GB é o mínimo hoje; 32 GB dá folga para jogar com browser e Discord abertos."),
        ("XMP / EXPO", "Perfil da RAM na BIOS que a põe à velocidade para que foi feita. Vem desligado: sem ele a RAM anda bem mais devagar."),
        ("Hotspot (GPU Hot Spot)", "O ponto mais quente do chip da gráfica. É normal estar 10–20 °C acima da temperatura 'normal'. Se a diferença passar de ~25–30 °C, a pasta térmica pode estar má."),
        ("Memory Junction", "Temperatura da memória da gráfica (GDDR6/6X). Pode ir até ~95–100 °C sem problema; acima disso a placa reduz o desempenho."),
        ("Overclock (OC)", "Pôr uma peça a trabalhar mais rápido do que de fábrica. Dá mais FPS, mais calor e consumo. Cada chip aguenta valores diferentes, por isso testa-se aos poucos."),
        ("Undervolt", "Dar menos tensão à peça para a mesma velocidade. Resultado: menos calor, menos barulho e menos consumo, com quase o mesmo desempenho."),
        ("Limite de energia (power limit)", "Quantos watts a gráfica pode gastar. Subir deixa-a manter clocks mais altos; descer poupa energia e calor."),
        ("Curva de ventoinha", "Regra 'a X graus, a ventoinha anda a Y%'. Desenhas a curva e a ventoinha segue-a sozinha."),
        ("Histerese", "Folga para a ventoinha não andar sempre a subir e descer. Com 2 °C, só muda se a temperatura variar pelo menos 2 °C."),
        ("Thermal throttling", "Quando uma peça aquece demais e baixa a velocidade sozinha para se proteger. Aparece como quebras e FPS a cair com o tempo."),
        ("DPC / latência de drivers", "Tempo em que um driver (rede, som, USB) ocupa o processador. Se for alto, causa engasgos e estalidos no som. Atualizar drivers costuma resolver."),
        ("Anti-cheat (Vanguard, EAC, BattlEye)", "Programas dos jogos que procuram batota. Bloqueiam programas que entram dentro do jogo (como o RivaTuner). Esta app nunca entra no jogo."),
        ("Overlay", "Informação desenhada por cima do jogo (FPS, temperaturas). O desta app é uma janela à parte: seguro com anti-cheat, mas precisa do jogo em 'janela sem bordas'."),
        ("Janela sem bordas / ecrã inteiro em janela", "Modo de ecrã em que o jogo ocupa o ecrã todo mas continua a ser uma janela. Permite overlays externos e Alt+Tab rápido."),
        ("V-Sync / G-Sync / FreeSync", "Formas de sincronizar o FPS com o monitor para não haver 'cortes' na imagem. G-Sync/FreeSync fazem-no sem o atraso do V-Sync."),
        ("NVIDIA Reflex", "Opção dentro de alguns jogos que reduz o atraso entre o clique e a ação no ecrã. Liga sempre que existir (Ligado + Boost)."),
        ("DLSS / FSR", "O jogo desenha numa resolução mais baixa e a placa aumenta a imagem de forma inteligente. Dá muito FPS com pouca perda de qualidade."),
        ("HAGS", "Agendamento de GPU acelerado por hardware: a placa gere o próprio trabalho. Necessário para o Frame Generation das RTX 40/50."),
        ("PawnIO", "Driver usado para ler a temperatura do processador e controlar as ventoinhas da placa-mãe. É de kernel: alguns anti-cheats podem não gostar."),
        ("ETW", "Sistema do Windows que regista eventos. A app usa-o para contar os frames do jogo sem lhe tocar (o mesmo que o PresentMon)."),
        ("BIOS / UEFI", "O 'menu' da placa-mãe que aparece antes do Windows (tecla Del ou F2 ao ligar). É lá que se liga o XMP, o Resizable BAR e o PBO."),
        ("PBO / Curve Optimizer", "Funções dos Ryzen na BIOS. PBO deixa subir mais o clock; o Curve Optimizer negativo é um undervolt por núcleo (menos calor, mais boost)."),
    };

    public GlossaryPage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = new Thickness(28, 18, 28, 40) };
        root.Children.Add(Ui.T("Glossário", "H1"));
        var search = new TextBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 14), ToolTip = "Procurar uma palavra" };
        root.Children.Add(Ui.T("Cada palavra que aparece na app, explicada sem complicações. Escreve para procurar.", "MutedText"));
        root.Children.Add(search);
        var list = new WrapPanel();
        void Fill()
        {
            list.Children.Clear();
            string q = search.Text.Trim();
            foreach (var (term, text) in Terms.Where(t => q.Length == 0 || t.Term.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Text.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                var sp = new StackPanel();
                sp.Children.Add(Ui.T(term, "H2"));
                sp.Children.Add(Ui.T(text, "Body"));
                var c = Ui.Card(sp);
                c.Width = 420;
                c.Margin = new Thickness(0, 0, 14, 14);
                list.Children.Add(c);
            }
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
        root.Children.Add(list);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
    }
}
