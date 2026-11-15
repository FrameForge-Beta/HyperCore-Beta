using System.Windows.Controls;

namespace FrameForge.Pages;

/// <summary>
/// Modo Jogo (tag "FF:gamemode"). FASE 1: só o cartão "Em construção" — a fase 2 substitui o conteúdo deste ficheiro.
/// O cabeçalho padrão (título, descrição e abas da secção) é desenhado pela janela principal a partir do NavCatalog;
/// esta página começa logo no conteúdo (margens Ui.PageMargin).
/// </summary>
public sealed class GameModePage : UserControl
{
    public GameModePage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = Ui.PageMargin };
        root.Children.Add(Ui.UnderConstruction("Um clique antes de jogar: fecha o que pesa e repõe tudo quando desligas.",
            "Fecha programas pesados de fundo (lista configurável, com confirmação na primeira vez).",
            "Pausa a sincronização da nuvem e o Windows Update enquanto jogas.",
            "Liberta memória e aplica o nível e o plano de energia escolhidos.",
            "Atalho global e entrada no menu da área de notificação."));
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root };
    }
}
