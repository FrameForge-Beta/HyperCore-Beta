using System.Windows.Controls;

namespace FrameForge.Pages;

/// <summary>
/// Testes › Periféricos (tag "FF:peripherals"). FASE 1: só o cartão "Em construção" — a fase 2 substitui o conteúdo deste ficheiro.
/// O cabeçalho padrão (título, descrição e abas da secção) é desenhado pela janela principal a partir do NavCatalog;
/// esta página começa logo no conteúdo (margens Ui.PageMargin).
/// </summary>
public sealed class PeripheralsPage : UserControl
{
    public PeripheralsPage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = Ui.PageMargin };
        root.Children.Add(Ui.UnderConstruction("Testes do rato, do teclado e do ecrã.",
            "Taxa de polling do rato (Hz médio e máximo) e deteção de duplo clique avariado.",
            "Teste de botões, roda e teclas premidas ao mesmo tempo (ghosting), com teclado visual.",
            "Píxeis mortos em ecrã inteiro e teste de rasto de movimento.",
            "Refresh real do ecrã medido."));
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root };
    }
}
