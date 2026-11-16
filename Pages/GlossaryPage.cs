using System.Windows;
using System.Windows.Controls;

namespace FrameForge.Pages;

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
        var root = new StackPanel { Margin = Ui.PageMargin };
        var search = new TextBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14), ToolTip = "Procurar uma palavra", Tag = "Procurar uma palavra…" };
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
