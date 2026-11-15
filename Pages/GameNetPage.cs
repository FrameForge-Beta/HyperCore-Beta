using System.Windows.Controls;

namespace FrameForge.Pages;

/// <summary>
/// Rede para jogos › Ping e lag (tag "FF:netgame"). FASE 1: só o cartão "Em construção" — a fase 2 substitui o conteúdo deste ficheiro.
/// O cabeçalho padrão (título, descrição e abas da secção) é desenhado pela janela principal a partir do NavCatalog;
/// esta página começa logo no conteúdo (margens Ui.PageMargin).
/// </summary>
public sealed class GameNetPage : UserControl
{
    public GameNetPage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = Ui.PageMargin };
        root.Children.Add(Ui.UnderConstruction("Ping, jitter e perda de pacotes ao vivo, para saberes de onde vem o lag.",
            "Ping, jitter e perda de pacotes contínuos contra servidores públicos, com gráfico ao vivo.",
            "Teste de bufferbloat (latência em repouso e sob carga) com nota de A a F.",
            "O DNS mais rápido para ti, aplicado só com confirmação e reversível.",
            "Diagnóstico simples: o lag vem do Wi-Fi, do router ou do fornecedor."));
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root };
    }
}
