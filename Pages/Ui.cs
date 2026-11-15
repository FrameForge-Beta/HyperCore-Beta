using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FrameForge.Core;

namespace FrameForge.Pages;

/// <summary>Pequenos construtores de interface para as listas geradas em código (cartões, textos, etiquetas).</summary>
internal static class Ui
{
    /// <summary>Tema das páginas do HyperCore, incluindo estilos e cores de destaque.</summary>
    public static readonly ResourceDictionary Theme = new() { Source = new Uri("/HyperCore;component/FfTheme.xaml", UriKind.Relative) };

    private static object Find(string key) => Theme.Contains(key) ? Theme[key] : Application.Current.Resources[key];
    private static Style S(string key) => (Style)Find(key);

    /// <summary>Obtém primeiro a paleta global, que o seletor de tema atualiza em tempo real.</summary>
    public static Brush Res(string key)
    {
        if (Application.Current.Resources.Contains(key) && Application.Current.Resources[key] is Brush global)
            return global;
        return (Brush)Find(key);
    }

    /// <summary>Carrega o tema do HyperCore numa página (estilos implícitos de ComboBox, ScrollBar, etc. só valem lá dentro).</summary>
    public static void UseTheme(FrameworkElement page) =>
        page.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/HyperCore;component/FfTheme.xaml", UriKind.Relative) });

    public static TextBlock T(string text, string style = "Body", double? size = null, Brush? color = null, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, Style = S(style) };
        if (size is double s) t.FontSize = s;
        if (color != null) t.Foreground = color;
        if (weight is FontWeight w) t.FontWeight = w;
        return t;
    }

    public static Border Card(UIElement child, Brush? accent = null)
    {
        var b = new Border { Style = S("FfCard") };
        if (accent == null) b.Child = child;
        else
        {
            // Faixa de cor à esquerda (em vez de pintar o contorno todo)
            var strip = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = accent, Margin = new Thickness(0, 2, 16, 2) };
            var dock = new DockPanel();
            DockPanel.SetDock(strip, Dock.Left);
            dock.Children.Add(strip);
            dock.Children.Add(child);
            b.Child = dock;
        }
        return b;
    }

    public static Border Pill(string text, Brush? bg = null, Brush? fg = null)
    {
        return new Border
        {
            Style = S("Pill"),
            Background = bg ?? Res("Hover"),
            Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = fg ?? Res("Fg") },
        };
    }

    public static Button Btn(string text, RoutedEventHandler click, string style = "Btn")
    {
        var b = new Button { Content = text, Style = S(style) };
        b.Click += click;
        return b;
    }

    public static Brush Severity(int s) => s >= 3 ? Res("Bad") : s == 2 ? Res("Warn") : Res("Accent");

    public static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    public static Border Stat(string label, string value, string? hint = null)
    {
        var sp = new StackPanel();
        sp.Children.Add(T(label.ToUpperInvariant(), "Label"));
        sp.Children.Add(T(value, "Big"));
        if (hint != null) sp.Children.Add(T(hint, "MutedText", 12));
        var b = Card(sp);
        b.Margin = new Thickness(0, 0, 10, 10);
        b.MinWidth = 170;
        return b;
    }
}
