using System.Windows;

namespace FrameForge.Pages;

/// <summary>
/// Propriedade anexada com o ícone (glifo Segoe Fluent Icons) de um item do menu ou de uma aba.
/// Os estilos NavItem e SectionTab (Theme.xaml) desenham-no à esquerda do texto. Ex.: <c>Glyph.SetIcon(radio, "")</c>.
/// </summary>
public static class Glyph
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Glyph), new FrameworkPropertyMetadata(""));

    public static string GetIcon(DependencyObject d) => (string)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, string value) => d.SetValue(IconProperty, value);
}
