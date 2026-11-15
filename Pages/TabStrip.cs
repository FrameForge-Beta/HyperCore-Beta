using System.Windows;
using System.Windows.Controls;

namespace FrameForge.Pages;

/// <summary>
/// Fila de abas "pill" reutilizável (estilo SectionTab do Theme.xaml): ícone + texto, a selecionada com borda Accent e fundo AccentSoft.
/// Usada pelo cabeçalho de cada secção da janela principal e por qualquer página que precise de abas internas.
/// <code>
/// var tabs = new TabStrip().Add("a", "Resumo", "").Add("b", "Histórico", "");
/// tabs.SelectionChanged += key => Mostrar(key);
/// tabs.Select("a");
/// </code>
/// </summary>
public sealed class TabStrip : WrapPanel
{
    private readonly string _group = "tabs-" + Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, RadioButton> _tabs = new();
    private bool _silent;

    /// <summary>Chamado quando o utilizador (ou Select com notify=true) muda de aba. Recebe a chave da aba.</summary>
    public event Action<string>? SelectionChanged;

    /// <summary>Chave da aba selecionada (null antes da primeira escolha).</summary>
    public string? Selected { get; private set; }

    public IReadOnlyCollection<string> Keys => _tabs.Keys;

    public TabStrip()
    {
        Orientation = Orientation.Horizontal;
    }

    /// <summary>Acrescenta uma aba. <paramref name="icon"/> é um glifo Segoe Fluent Icons (pode ficar vazio).</summary>
    public TabStrip Add(string key, string label, string icon = "", string? tooltip = null)
    {
        var rb = new RadioButton { Content = label, GroupName = _group, Tag = key };
        rb.SetResourceReference(StyleProperty, "SectionTab");
        Glyph.SetIcon(rb, icon);
        if (!string.IsNullOrWhiteSpace(tooltip)) rb.ToolTip = tooltip;
        rb.Checked += (_, _) =>
        {
            Selected = key;
            if (!_silent) SelectionChanged?.Invoke(key);
        };
        _tabs[key] = rb;
        Children.Add(rb);
        return this;
    }

    /// <summary>Pequeno rótulo entre abas (ex.: "AVANÇADO" antes das abas para utilizadores experientes).</summary>
    public TabStrip AddLabel(string text)
    {
        var t = new TextBlock
        {
            Text = text.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 8)
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        Children.Add(t);
        return this;
    }

    /// <summary>Seleciona uma aba por código. Por omissão não dispara SelectionChanged (quem chama já sabe o que vai mostrar).</summary>
    public void Select(string key, bool notify = false)
    {
        if (!_tabs.TryGetValue(key, out var rb)) return;
        _silent = !notify;
        try
        {
            if (rb.IsChecked == true) { Selected = key; if (notify) SelectionChanged?.Invoke(key); }
            else rb.IsChecked = true;
        }
        finally { _silent = false; }
    }
}
