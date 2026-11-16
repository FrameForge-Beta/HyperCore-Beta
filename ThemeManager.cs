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

    // Identidade HyperCore: azul elétrico + ciano. Escuro = paleta do DESIGN (Theme.xaml); claro = a mesma família em fundo claro.
    private static readonly (object Key, string Dark, string Light)[] Palette =
    {
        ("Accent", "#2E9BFF", "#1673E0"),
        ("Accent2", "#22D3EE", "#0891B2"),
        ("AccentSoft", "#14243D", "#E3EFFE"),
        ("Bg", "#0A0E18", "#F3F6FB"),
        ("Panel", "#0D1320", "#FFFFFF"),
        ("Card", "#131A29", "#FFFFFF"),
        ("Hover", "#1A2336", "#EDF2F9"),
        ("Line", "#212C42", "#DDE4EF"),
        ("LineStrong", "#2F3D5C", "#B9C5D8"),
        ("Fg", "#EDF1F7", "#0F1726"),
        ("Muted", "#8E99AE", "#55617A"),
        ("Faint", "#5F6B82", "#7D879B"),
        ("Ink", "#0A0E18", "#FFFFFF"),
        ("Good", "#34D399", "#059669"),
        ("Warn", "#FBBF24", "#D97706"),
        ("Bad", "#F87171", "#DC2626"),
        ("ThumbBrush", "#2A3550", "#C5D0E0"),
        ("ThumbHover", "#3A4868", "#A6B4CA"),
        ("ThumbDrag", "#4F6290", "#7F91AE"),
        (SystemColors.ControlBrushKey, "#0A0E18", "#F3F6FB"),
        (SystemColors.HighlightBrushKey, "#1C3558", "#DCEBFD"),
        (SystemColors.InactiveSelectionHighlightBrushKey, "#1A2336", "#EDF2F9"),
    };

    public static void Apply(bool light)
    {
        IsLight = light;
        var res = Application.Current.Resources;
        foreach (var (key, dark, lightHex) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(light ? lightHex : dark);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            SetThemeResource(res, key, brush); // XAML usa DynamicResource: atualizar a paleta redesenha os controlos
        }
        // Brilho do fundo da área de conteúdo: azul-noite no escuro, azul muito claro no claro
        var glowColor = (Color)ColorConverter.ConvertFromString(light ? "#D6E6FB" : "#14264A");
        var glow = new RadialGradientBrush
        {
            Center = new Point(1, 0), GradientOrigin = new Point(1, 0), RadiusX = 0.75, RadiusY = 0.9,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xFF, glowColor.R, glowColor.G, glowColor.B), 0),
                new GradientStop(Color.FromArgb(0x90, glowColor.R, glowColor.G, glowColor.B), 0.35),
                new GradientStop(Color.FromArgb(0x00, glowColor.R, glowColor.G, glowColor.B), 1),
            }
        };
        glow.Freeze();
        SetThemeResource(res, "ContentGlow", glow);
    }

    private static void SetThemeResource(ResourceDictionary resources, object key, object value)
    {
        resources[key] = value;
        foreach (var dictionary in resources.MergedDictionaries)
            SetThemeResource(dictionary, key, value);
    }
}
