using System.Windows;
using System.Windows.Media;

namespace ResetExplode;

/// <summary>
/// Troca entre modo escuro e claro substituindo os pincéis da paleta (Theme.xaml). O XAML os referencia com DynamicResource,
/// então a troca vale na hora, sem recarregar janelas. Overlay e aviso de dispositivo ficam sempre escuros (aparecem sobre jogos).
/// </summary>
public static class ThemeManager
{
    public static bool IsLight { get; private set; }

    private static readonly (object Key, string Dark, string Light)[] Palette =
    {
        ("Accent", "#A78BFA", "#7C3AED"),
        ("Accent2", "#C084FC", "#9333EA"),
        ("AccentSoft", "#35264A", "#F0E7FF"),
        ("Bg", "#0D0A14", "#F7F4FB"),
        ("Panel", "#130F1D", "#FFFFFF"),
        ("Card", "#1B1528", "#FFFFFF"),
        ("Hover", "#282039", "#F0EAF8"),
        ("Line", "#30263F", "#E4DAEF"),
        ("LineStrong", "#514166", "#B9A7CE"),
        ("Fg", "#F3EEF9", "#21172E"),
        ("Muted", "#B7AFC5", "#625672"),
        ("Faint", "#817791", "#81758F"),
        ("Ink", "#160D22", "#FFFFFF"),
        ("ThumbBrush", "#514166", "#C6B7D8"),
        ("ThumbHover", "#796394", "#A88CC6"),
        ("ThumbDrag", "#B9A4D6", "#8B6BAB"),
        (SystemColors.ControlBrushKey, "#0D0A14", "#F7F4FB"),
        (SystemColors.HighlightBrushKey, "#35264A", "#EDE3FA"),
        (SystemColors.InactiveSelectionHighlightBrushKey, "#282039", "#F0EAF8"),
    };

    public static void Apply(bool light)
    {
        IsLight = light;
        var res = Application.Current.Resources;
        SetThemeResource(res, "Accent", light ? "#7C3AED" : "#A78BFA");
        SetThemeResource(res, "Accent2", light ? "#9333EA" : "#C084FC");
        SetThemeResource(res, "AccentSoft", light ? "#F0E7FF" : "#35264A");
        foreach (var (key, dark, lightHex) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(light ? lightHex : dark);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            SetThemeResource(res, key, brush); // XAML usa DynamicResource: atualizar a paleta redesenha os controlos
        }
    }

    private static void SetThemeResource(ResourceDictionary resources, object key, object value)
    {
        resources[key] = value;
        foreach (var dictionary in resources.MergedDictionaries)
            SetThemeResource(dictionary, key, value);
    }
}
