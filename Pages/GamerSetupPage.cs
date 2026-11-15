using System.Windows.Controls;

namespace FrameForge.Pages;

/// <summary>
/// Níveis de desempenho › Configuração gamer (tag "FF:gamersetup"). FASE 1: só o cartão "Em construção" — a fase 2 substitui o conteúdo deste ficheiro.
/// O cabeçalho padrão (título, descrição e abas da secção) é desenhado pela janela principal a partir do NavCatalog;
/// esta página começa logo no conteúdo (margens Ui.PageMargin).
/// </summary>
public sealed class GamerSetupPage : UserControl
{
    public GamerSetupPage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = Ui.PageMargin };
        root.Children.Add(Ui.UnderConstruction("O Windows e o monitor prontos para jogar, item a item.",
            "Hz atual de cada monitor e o máximo suportado, com correção segura (volta sozinho se não confirmares).",
            "Estado de G-Sync/FreeSync/VRR, HAGS, Modo de Jogo, Game DVR e MPO.",
            "Limpeza da cache de shaders (NVIDIA, AMD e DirectX) com tamanho e confirmação.",
            "Cada correção é reversível e fica na Central Desfazer."));
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root };
    }
}
