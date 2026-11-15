using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FrameForge.Pages;

namespace ResetExplode;

/// <summary>
/// Pesquisa rápida (Ctrl+K ou o campo no topo do menu): caixa por cima da janela para saltar para qualquer secção/aba.
/// Procura no nome, na descrição e nos sinónimos do NavCatalog ("fps", "ping", "temperatura", "ventoinha", "limpar"…).
/// Setas + Enter abrem; Esc ou clicar fora fecha.
/// </summary>
public partial class MainWindow
{
    /// <summary>Um resultado possível. Name/Keys/DescF/Extra já vêm normalizados (Fold) e pesam por esta ordem.</summary>
    private sealed record SearchHit(string Target, string Icon, string Title, string Where, string Desc,
                                    string Name, string Keys, string DescF, string Extra);

    private Grid? _searchOverlay;
    private TextBox? _searchBox;
    private ListBox? _searchList;
    private List<SearchHit>? _searchIndex;

    private void NavSearch_Click(object sender, RoutedEventArgs e) => OpenQuickSearch();

    private void OpenQuickSearch()
    {
        EnsureSearchOverlay();
        _searchBox!.Text = "";
        RefreshSearch();
        _searchOverlay!.Visibility = Visibility.Visible;
        _searchOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        Dispatcher.BeginInvoke(() => { _searchBox.Focus(); Keyboard.Focus(_searchBox); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseQuickSearch()
    {
        if (_searchOverlay == null) return;
        _searchOverlay.Visibility = Visibility.Collapsed;
    }

    private void EnsureSearchOverlay()
    {
        if (_searchOverlay != null) return;
        _searchIndex = BuildSearchIndex();

        var scrim = new Grid { Background = new SolidColorBrush(Color.FromArgb(0xA0, 0x03, 0x06, 0x0E)), Visibility = Visibility.Collapsed };
        Grid.SetRowSpan(scrim, 2);
        Grid.SetColumnSpan(scrim, 2);
        Panel.SetZIndex(scrim, 100);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(scrim, true);
        scrim.MouseDown += (_, e) => { if (e.OriginalSource == scrim) CloseQuickSearch(); };

        var box = new Border
        {
            Width = 600, Margin = new Thickness(0, 90, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(0, 0, 0, 8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black }
        };
        box.SetResourceReference(Border.BackgroundProperty, "Panel");
        box.SetResourceReference(Border.BorderBrushProperty, "LineStrong");

        var stack = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(18, 6, 14, 6) };
        var icon = new TextBlock { Text = "", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        var esc = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        esc.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        var escText = new TextBlock { Text = "Esc", FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        escText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        esc.Child = escText;
        DockPanel.SetDock(esc, Dock.Right);
        top.Children.Add(esc);
        _searchBox = new TextBox
        {
            Tag = "Para onde queres ir? (ex.: fps, ping, temperatura, ventoinha)", FontSize = 15, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Padding = new Thickness(0, 12, 0, 12)
        };
        _searchBox.TextChanged += (_, _) => RefreshSearch();
        _searchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
        top.Children.Add(_searchBox);
        DockPanel.SetDock(top, Dock.Top);
        stack.Children.Add(top);

        var line = new Border { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        DockPanel.SetDock(line, Dock.Top);
        stack.Children.Add(line);

        _searchList = new ListBox { MaxHeight = 430, Margin = new Thickness(8, 8, 8, 0), Focusable = false };
        ScrollViewer.SetHorizontalScrollBarVisibility(_searchList, ScrollBarVisibility.Disabled);
        _searchList.ItemContainerStyle = (Style)FindResource("ListNavItem");
        _searchList.MouseLeftButtonUp += (_, _) => OpenSearchHit();
        stack.Children.Add(_searchList);

        box.Child = stack;
        scrim.Children.Add(box);
        RootGrid.Children.Add(scrim);
        _searchOverlay = scrim;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_searchList == null) return;
        int n = _searchList.Items.Count;
        switch (e.Key)
        {
            case Key.Escape: CloseQuickSearch(); e.Handled = true; break;
            case Key.Down when n > 0:
                _searchList.SelectedIndex = (_searchList.SelectedIndex + 1) % n;
                _searchList.ScrollIntoView(_searchList.SelectedItem);
                e.Handled = true; break;
            case Key.Up when n > 0:
                _searchList.SelectedIndex = (_searchList.SelectedIndex - 1 + n) % n;
                _searchList.ScrollIntoView(_searchList.SelectedItem);
                e.Handled = true; break;
            case Key.Enter: OpenSearchHit(); e.Handled = true; break;
        }
    }

    private void OpenSearchHit()
    {
        if (_searchList?.SelectedItem is not ListBoxItem { Tag: SearchHit hit }) return;
        CloseQuickSearch();
        NavigateTo(hit.Target);
    }

    private void RefreshSearch()
    {
        if (_searchList == null || _searchIndex == null || _searchBox == null) return;
        string q = Fold(_searchBox.Text);
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<SearchHit> hits;
        if (words.Length == 0)
            hits = _searchIndex.Where(h => !h.Target.Contains('/')); // sem texto: as 14 secções
        else
            hits = _searchIndex
                .Select(h => (h, score: Score(h, words)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Select(x => x.h)
                .Take(12);

        _searchList.Items.Clear();
        foreach (var h in hits) _searchList.Items.Add(new ListBoxItem { Tag = h, Content = HitRow(h) });
        if (_searchList.Items.Count > 0) _searchList.SelectedIndex = 0;
        else
        {
            var none = new TextBlock { Text = "Nada encontrado. Experimenta outra palavra (ex.: rede, jogos, discos).", Margin = new Thickness(14, 10, 14, 10) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _searchList.Items.Add(new ListBoxItem { Content = none, IsEnabled = false });
        }
    }

    /// <summary>Todas as palavras têm de aparecer. Pesa mais o nome, depois os sinónimos próprios, a descrição e por fim o contexto.</summary>
    private static int Score(SearchHit h, string[] words)
    {
        static bool WordStarts(string hay, string w) => hay.Split(' ').Any(x => x.StartsWith(w, StringComparison.Ordinal));
        int total = 0;
        foreach (var w in words)
        {
            int s = 0;
            if (h.Name.StartsWith(w, StringComparison.Ordinal)) s = 100;
            else if (WordStarts(h.Name, w)) s = 70;
            else if (h.Name.Contains(w, StringComparison.Ordinal)) s = 50;
            else if (WordStarts(h.Keys, w)) s = 45 + Math.Min(5, h.Keys.Split(' ').Count(x => x.StartsWith(w, StringComparison.Ordinal))); // mais sinónimos = mais relevante
            else if (h.Keys.Contains(w, StringComparison.Ordinal)) s = 30;
            else if (WordStarts(h.DescF, w)) s = 20;
            else if (WordStarts(h.Extra, w)) s = 12;
            else if (h.DescF.Contains(w, StringComparison.Ordinal) || h.Extra.Contains(w, StringComparison.Ordinal)) s = 8;
            if (s == 0) return 0;
            total += s;
        }
        if (!h.Target.Contains('/')) total += 1; // em empate, a secção aparece antes das suas abas
        return total;
    }

    private UIElement HitRow(SearchHit h)
    {
        var row = new DockPanel();
        var ico = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        ico.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        var glyph = new TextBlock { Text = h.Icon, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        ico.Child = glyph;
        DockPanel.SetDock(ico, Dock.Left);
        row.Children.Add(ico);
        var where = new TextBlock { Text = h.Where, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
        where.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        DockPanel.SetDock(where, Dock.Right);
        row.Children.Add(where);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = h.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        text.Children.Add(title);
        var desc = new TextBlock { Text = h.Desc, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        desc.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(desc);
        row.Children.Add(text);
        return row;
    }

    private static readonly Dictionary<string, string> GroupNames = new()
    {
        ["JOGAR"] = "Jogar", ["DESEMPENHO"] = "Desempenho", ["O MEU PC"] = "O meu PC",
    };

    private static List<SearchHit> BuildSearchIndex()
    {
        var list = new List<SearchHit>();
        foreach (var s in NavCatalog.Sections)
        {
            string where = GroupNames.TryGetValue(s.Group, out var g) ? g : "";
            bool single = s.Tabs.Length < 2;
            // Secção com uma só aba: os sinónimos da aba são da própria secção
            string ownKeys = single ? $"{s.Keys} {s.Tabs[0].Keys}" : s.Keys;
            string extra = single ? "" : string.Join(' ', s.Tabs.Select(t => $"{t.Label} {t.Keys}"));
            list.Add(new SearchHit(s.Id, s.Icon, s.Label, where, s.Desc, Fold(s.Label), Fold(ownKeys), Fold(s.Desc), Fold(extra)));
            if (single) continue;
            foreach (var t in s.Tabs)
            {
                // Aba repetida noutra secção (ex.: Visão geral no Início e em O meu PC): fica só na secção com mais abas
                var home = NavCatalog.SectionsWith(t.Tag).OrderByDescending(x => x.Tabs.Length).First();
                if (!ReferenceEquals(home, s)) continue;
                list.Add(new SearchHit($"{s.Id}/{t.Tag}", t.Icon, t.Label, s.Label, t.Desc,
                                       Fold(t.Label), Fold(t.Keys), Fold(t.Desc), Fold($"{s.Label} {s.Keys}")));
            }
        }
        return list;
    }

    /// <summary>Minúsculas e sem acentos ("Níveis" → "niveis"), para "temperatura" encontrar "Temperaturas" e "ecra" encontrar "ecrã".</summary>
    private static string Fold(string text)
    {
        var norm = (text ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var c in norm)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) || c == '%' ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
