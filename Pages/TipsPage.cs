using System.Windows;
using System.Windows.Controls;

namespace FrameForge.Pages;

/// <summary>Dicas, avisos e mitos — o "manual" da app, em texto direto.</summary>
public sealed class TipsPage : UserControl
{
    private static readonly (string Title, string Tag, string[] Lines)[] Sections =
    {
        ("Porque é que esta app não dá ban", "ANTI-CHEAT", new[]
        {
            "Os anti-cheats (Vanguard, EAC, BattlEye, FACEIT, Ricochet) procuram programas que MEXEM no jogo: injetar DLLs, ler/escrever a memória do jogo, hooks no DirectX, drivers vulneráveis.",
            "O FrameForge não faz nada disso: os frames vêm de eventos ETW que o próprio Windows emite (é o método do PresentMon/CapFrameX, usado em reviews com Valorant), os sensores vêm do driver da NVIDIA e dos contadores do Windows, e o processo do jogo nunca é aberto — nem para mudar a prioridade.",
            "O overclock é aplicado no driver da placa, fora do jogo (igual ao Afterburner sem o RTSS). Os anti-cheats não se importam com isso.",
            "O overlay do FrameForge é uma janela própria transparente por cima do jogo. Só aparece em modo 'janela sem bordas'; em ecrã inteiro exclusivo o Windows não deixa nenhuma janela por cima.",
            "Exceção a ter cuidado: os 'Sensores avançados da CPU' usam o driver PawnIO. É assinado e moderno, mas alguns anti-cheats de kernel recusam drivers deste tipo. Por isso está desligado por omissão.",
            "Nenhum programa externo pode garantir 100% que nunca terás problemas — a regra de ouro é: nada que injete, leia memória ou automatize input.",
        }),
        ("Valorant: estatísticas e overlay", "VALORANT", new[]
        {
            "Para ver FPS no Valorant usa: Definições → Vídeo → Estatísticas (FPS do cliente, tempo de frame CPU/GPU, latência de rede, perda de pacotes). É a fonte mais fiável e é oficial.",
            "O overlay externo do FrameForge requer 'janela sem bordas'; não aparece sobre ecrã inteiro exclusivo.",
            "Para saber DE ONDE vêm as quebras, joga com 'gravar sessão' (botão JOGAR) e abre o relatório: cada quebra aparece no minuto em que aconteceu com a causa (VRAM, RAM, drivers, programa em segundo plano, CPU, GPU, rede ou o próprio jogo).",
            "O Valorant depende muito da CPU e da RAM: XMP/EXPO ligado e poucos programas em segundo plano valem mais do que qualquer tweak do Windows.",
        }),
        ("Níveis de desempenho", "OTIMIZAÇÃO", new[]
        {
            "Seguro: só o que a Microsoft recomenda para jogos (Modo de Jogo, desligar a gravação da Game Bar, otimizações em janela, plano Alto desempenho, rato sem aceleração). Para qualquer PC.",
            "Equilibrado: tira poupanças de energia que causam micro-atrasos (USB, PCIe), ativa o HAGS e trava apps da Loja em segundo plano. Recomendado para desktop.",
            "Competitivo: plano Desempenho Máximo, núcleos sempre acordados, temporizador global, sem upload P2P de atualizações. Desktop. Mais consumo em repouso.",
            "Extremo: desliga serviços de fundo (indexação da pesquisa, SysMain em PCs só com SSD). Perdes conveniência. Só se jogas competitivo a sério.",
            "Ganho realista somando tudo: 0–5% de FPS médio, mas 1% low mais estável e menos picos. Quem promete +30% com tweaks do Windows está a mentir — esses ganhos vêm de hardware, XMP, drivers e definições do jogo.",
        }),
        ("O que dá MESMO mais FPS (por ordem)", "PRIORIDADES", new[]
        {
            "1. Monitor na taxa de atualização máxima (o Painel avisa se não estiver). 2. XMP/EXPO ligado na BIOS. 3. Driver da gráfica atualizado. 4. Definições do jogo: DLSS/FSR, Reflex, sombras/volumétricos. 5. Jogo num SSD. 6. Fechar programas pesados. 7. Undervolt/OC da GPU. 8. Tweaks do Windows.",
            "Limitar o FPS um pouco abaixo da média (5–10%) faz o jogo parecer mais suave do que FPS ilimitado com picos: a GPU deixa de estar a 100% e o frametime fica plano.",
            "G-Sync/FreeSync: liga no painel, V-Sync ligado no painel NVIDIA e desligado no jogo, e limita o FPS 3 abaixo da taxa do monitor (ex.: 141 em 144 Hz). Com Reflex isto é automático.",
        }),
        ("Painel de Controlo NVIDIA", "NVIDIA", new[]
        {
            "Tamanho da cache de sombreamento: 10 GB ou Ilimitado — reduz as quebras de compilação de shaders depois de atualizar o driver ou o jogo.",
            "Modo de baixa latência: Ligado (ou Ultra). Se o jogo tem NVIDIA Reflex, usa o Reflex em vez disto.",
            "Modo de gestão de energia: 'Preferir desempenho máximo' só por jogo (nas definições do programa), não global — globalmente deixa a placa sempre em clock alto, mesmo no ambiente de trabalho.",
            "Depois de instalar um driver novo, as primeiras 2–3 partidas de cada jogo podem ter quebras: é a cache de shaders a reconstruir-se. Não é problema do driver.",
        }),
        ("Checklist da BIOS", "BIOS", new[]
        {
            "XMP/EXPO/DOCP ligado (a RAM vem a 2133/2400/4800 por omissão!).",
            "Resizable BAR + Above 4G Decoding ligados (RTX 30+/RX 6000+), com CSM desligado.",
            "Ryzen: PBO + Curve Optimizer negativo (guia no separador Overclock). Intel 13/14.ª gen: BIOS com microcódigo 0x12B+ e perfil 'Intel Default'.",
            "Atualizar a BIOS traz compatibilidade de RAM e correções. Faz com o PC ligado a uma tomada estável e nunca desligues a meio.",
            "Fan curves: as ventoinhas da caixa devem subir com a temperatura da CPU/GPU, não ficar fixas no mínimo.",
        }),
        ("Mitos e placebos (não estão na app de propósito)", "MITOS", new[]
        {
            "Desligar o algoritmo de Nagle / TcpAckFrequency: só afeta TCP. Valorant, CS2, Fortnite, Apex usam UDP. Zero efeito no ping desses jogos.",
            "NetworkThrottlingIndex = FFFFFFFF e 'SystemResponsiveness': foram feitos para áudio/multimédia no Windows Vista; em testes modernos não mudam FPS nem latência de forma mensurável.",
            "Limpar a RAM em espera em ciclo (ISLC/'memory cleaners'): corrigia um bug do Windows 10 1703. Hoje, RAM 'em espera' é cache útil; limpá-la constantemente pode CAUSAR quebras.",
            "bcdedit useplatformclock / disabledynamictick / desligar HPET: resultados inconsistentes e às vezes piores. Não vale o risco.",
            "Prioridade 'Alta/Tempo real' no jogo: pouco ou nenhum ganho e pode roubar CPU ao áudio e ao rato (input lag, som a falhar). E em jogos com anti-cheat, abrir o processo para isto é exatamente o que deves evitar.",
            "Desligar o Windows Defender, as mitigações Spectre/Meltdown ou o UAC: ganhos mínimos em troca de segurança real. Nunca.",
            "'Debloat scripts' agressivos: removem coisas de que a Loja, o Xbox App (Game Pass), o Windows Update ou anti-cheats precisam. Causam mais problemas do que resolvem.",
            "Desligar o ficheiro de paginação 'porque tenho muita RAM': jogos fecham quando a memória enche em vez de abrandarem. Deixa em automático.",
        }),
        ("Avisos importantes", "AVISOS", new[]
        {
            "Overclock e undervolt nunca são garantidos: cada chip é diferente. Testa sempre e usa a guarda de crash.",
            "Em portáteis: nada de níveis Competitivo/Extremo nem OC. Prefere limitar FPS e undervolt — temperatura é o teu limite.",
            "A app precisa de administrador para ler frames por ETW, aplicar overclock e mudar definições do sistema. Tudo o que muda fica registado e pode ser reposto em 'Restaurar tudo'.",
            "Se o PC ficar instável depois de um ajuste: Overclock → Stock, Otimização → Restaurar tudo, e reinicia.",
        }),
    };

    public TipsPage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 40) };
        root.Children.Add(Ui.T("Dicas e avisos", "H1"));
        var sub = Ui.T("O que funciona, o que é mito e porque é que esta app é segura com anti-cheat.", "MutedText");
        sub.Margin = new Thickness(0, 0, 0, 16);
        root.Children.Add(sub);
        foreach (var (title, tag, lines) in Sections)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Row(Ui.Pill(tag)));
            var h = Ui.T(title, "H2");
            h.Margin = new Thickness(0, 8, 0, 6);
            sp.Children.Add(h);
            foreach (var l in lines)
            {
                var t = Ui.T("• " + l, "Body");
                t.Margin = new Thickness(0, 0, 0, 6);
                sp.Children.Add(t);
            }
            root.Children.Add(Ui.Card(sp, tag == "MITOS" ? Ui.Res("Warn") : tag == "AVISOS" ? Ui.Res("Bad") : null));
        }
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
    }
}
