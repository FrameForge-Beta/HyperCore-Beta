using System.Windows.Controls;

namespace FrameForge.Pages;

/// <summary>
/// Central Desfazer (tag "FF:undo"). FASE 1: só o cartão "Em construção" — a fase 2 substitui o conteúdo deste ficheiro.
/// O cabeçalho padrão (título, descrição e abas da secção) é desenhado pela janela principal a partir do NavCatalog;
/// esta página começa logo no conteúdo (margens Ui.PageMargin).
/// </summary>
public sealed class UndoPage : UserControl
{
    public UndoPage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = Ui.PageMargin };
        root.Children.Add(Ui.UnderConstruction("Tudo o que o HyperCore mudou no teu PC, para desfazer com um clique.",
            "Lista de cada alteração ainda ativa, com data e o que mudou.",
            "Desfazer por item e Desfazer tudo (com confirmação).",
            "Inclui ajustes, nível aplicado, arranque, prioridades, overclock, DNS e serviços."));
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root };
    }
}
