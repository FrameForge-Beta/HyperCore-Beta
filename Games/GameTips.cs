using System.Text.RegularExpressions;

namespace FrameForge.Games;

/// <summary>Dicas específicas por jogo (definições que realmente mudam FPS/latência/quebras).</summary>
public static class GameTips
{
    private static readonly (string Pattern, string[] Tips)[] Db =
    {
        ("valorant", new[]
        {
            "Usa as estatísticas oficiais do próprio jogo: Definições → Vídeo → Estatísticas → 'FPS do cliente', 'Tempo de frame da CPU/GPU' e 'Perda de pacotes' em Texto+Gráfico.",
            "Se quiseres o overlay externo do FrameForge, põe o jogo em 'Ecrã inteiro em janela' (em ecrã inteiro exclusivo nenhuma janela externa aparece por cima).",
            "NVIDIA Reflex: 'Ligado + Boost'. Limita o FPS (Definições → Geral → Limitar FPS) a um valor que o PC mantém estável — frametime regular sente-se melhor que picos altos.",
            "Gráficos que pesam pouco e ajudam a ver: Qualidade de material/textura/detalhe = Baixa; MSAA 2x ou 4x; Filtragem anisotrópica 4x; Melhorar nitidez = Ligado; 'Bloom', 'Distorção' e 'Sombras' = Desligado.",
            "Valorant é muito dependente da CPU (thread principal): se o relatório disser 'CPU no limite', RAM com XMP/EXPO e um CPU X3D são os maiores ganhos.",
            "'Buffer de entrada bruta' (Raw Input Buffer) = Ligado se tens rato de 1000 Hz+.",
        }),
        ("counter-strike|cs2", new[]
        {
            "Mostrar FPS sem overlay: consola → cl_showfps 1 (ou o 'Telemetria' nas definições de jogo).",
            "NVIDIA Reflex = Ativado + Boost. 'Boost Player Contrast' = Ativado. Sombras de partículas e 'Ambient Occlusion' = Desligado para mais FPS.",
            "Para o ReShade/OBS game capture funcionarem é preciso -allow_third_party_software nas opções de arranque — isso DESLIGA o modo confiável e reduz o teu Fator de Confiança. Evita em jogos competitivos.",
            "fps_max: limita a ~0,9× a tua média para frametimes mais estáveis, ou 0 se a CPU tem folga.",
            "CS2 cria shaders na primeira vez que entras num mapa: as quebras da primeira partida depois de atualizar o driver são normais.",
        }),
        ("fortnite", new[]
        {
            "Modo de renderização 'Desempenho' (DX11 leve) dá muito mais FPS em PCs médios. Em DX12, as primeiras partidas têm quebras de compilação de shaders.",
            "Reflex Ligado+Boost. Limite de FPS = valor da taxa do monitor ou estável. 'Pré-carregar shaders' deixa ligado.",
        }),
        ("apex", new[]
        {
            "Opções de arranque (Steam/EA): +fps_max 0 ou limita ao teu FPS estável. Reflex Ligado+Boost.",
            "'Qualidade das texturas' segundo a VRAM: 8 GB → Alta; menos → Média.",
        }),
        ("league of legends", new[]
        {
            "Limitador de FPS no jogo a 144/240 conforme o monitor. O LoL é leve: quebras costumam vir de programas em segundo plano ou drivers (vê o relatório).",
        }),
        ("cyberpunk|alan wake|black myth|starfield|hogwarts|the last of us|jedi", new[]
        {
            "Jogo pesado em GPU e VRAM: DLSS/FSR em Qualidade, texturas conforme a VRAM (8 GB → Média/Alta). O primeiro carregamento compila shaders (quebras nos primeiros minutos são normais).",
        }),
        ("gta v|grand theft auto v", new[]
        {
            "'Densidade/Variedade de população' e 'Distância de escala' pesam na CPU. Sombras suaves 'Mais suaves' custam muito; 'Suave' chega.",
        }),
        ("rainbow six", new[]
        {
            "Vulkan costuma dar mais FPS e menos input lag que DX11 em GPUs NVIDIA recentes. Reflex Ligado+Boost.",
        }),
    };

    public static string[] For(string game)
    {
        foreach (var (p, tips) in Db)
            if (Regex.IsMatch(game, p, RegexOptions.IgnoreCase)) return tips;
        return new[]
        {
            "Procura nas definições do jogo: NVIDIA Reflex (Ligado+Boost), um limitador de FPS e DLSS/FSR. Estas três dão quase sempre o maior ganho percebido.",
            "Depois de jogar com 'gravar sessão', o relatório diz se o limite é CPU, GPU, VRAM ou outra coisa — ajusta as definições a partir daí.",
        };
    }
}
