using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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

    /// <summary>Margens padrão do conteúdo de uma página dentro da janela (alinhadas com o cabeçalho da secção).</summary>
    public static Thickness PageMargin => new(32, 4, 32, 40);

    /// <summary>
    /// Cabeçalho padrão: título 26 semibold + 1 linha de descrição (Muted) + ações à direita (opcional) + abas por baixo (opcional).
    /// A janela principal usa-o para o cabeçalho de cada secção (título/descrição vêm do <see cref="NavCatalog"/>), por isso as
    /// páginas ligadas ao menu NÃO repetem o título — usa-o só em janelas próprias ou páginas fora do menu.
    /// </summary>
    public static StackPanel PageHeader(string title, string subtitle, UIElement? actions = null, TabStrip? tabs = null)
    {
        var root = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var top = new DockPanel { LastChildFill = true };
        if (actions != null)
        {
            DockPanel.SetDock(actions, Dock.Right);
            if (actions is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(16, 0, 0, 0); }
            top.Children.Add(actions);
        }
        var text = new StackPanel();
        var t = new TextBlock { Text = title };
        t.SetResourceReference(FrameworkElement.StyleProperty, "PageTitle");
        text.Children.Add(t);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var d = new TextBlock { Text = subtitle };
            d.SetResourceReference(FrameworkElement.StyleProperty, "PageDesc");
            text.Children.Add(d);
        }
        top.Children.Add(text);
        root.Children.Add(top);
        if (tabs != null)
        {
            tabs.Margin = new Thickness(0, 16, 0, 0);
            root.Children.Add(tabs);
        }
        return root;
    }

    /// <summary>Título de um bloco dentro de uma página (H2 + linha Muted opcional).</summary>
    public static StackPanel SectionTitle(string title, string? subtitle = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 10) };
        sp.Children.Add(T(title, "H2", 17));
        if (!string.IsNullOrWhiteSpace(subtitle)) sp.Children.Add(T(subtitle, "MutedText"));
        return sp;
    }

    /// <summary>Cartão "Em construção" para as páginas que ainda vão ser feitas.</summary>
    public static Border UnderConstruction(string what, params string[] coming)
    {
        var body = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var icon = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(12), Background = Res("AccentSoft"), Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20, Foreground = Res("Accent"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(T("Em construção", "H2", 17));
        titles.Children.Add(T(what, "MutedText"));
        head.Children.Add(titles);
        body.Children.Add(head);
        if (coming.Length > 0)
        {
            var label = T("O QUE VAI TER", "Label");
            label.Margin = new Thickness(0, 6, 0, 6);
            body.Children.Add(label);
            foreach (var c in coming)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var dot = new Ellipse { Width = 6, Height = 6, Fill = Res("Accent2"), Margin = new Thickness(2, 7, 12, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                row.Children.Add(T(c, "Body"));
                body.Children.Add(row);
            }
        }
        var card = Card(body);
        card.MaxWidth = 760;
        card.HorizontalAlignment = HorizontalAlignment.Left;
        return card;
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
