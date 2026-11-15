using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FrameForge.Pages;

namespace ResetExplode;

/// <summary>
/// O "shell" da janela: menu lateral de 14 secções (NavCatalog), cabeçalho padrão com abas, transição de página,
/// botão Voltar com histórico (Alt+←, botão "recuar" do rato) e compatibilidade com os tags antigos.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, RadioButton> _navButtons = new();
    private readonly Dictionary<string, string> _lastTab = new();          // secção → última aba aberta
    private readonly List<(string Section, string Tag)> _history = new();  // para o botão Voltar
    private const int HistoryLimit = 60;
    private NavSection? _section;
    private string _currentTag = "";
    private TabStrip? _tabs;
    private bool _syncingNav;

    private void InitShell()
    {
        BuildNavigation();
        PreviewKeyDown += Shell_PreviewKeyDown;
        PreviewMouseDown += Shell_PreviewMouseDown;
        NavigateTo("FF:home", remember: false);
    }

    // ------------------------------------------------------------------ menu lateral

    private void BuildNavigation()
    {
        NavList.Children.Clear();
        NavFooter.Children.Clear();
        _navButtons.Clear();
        string? group = null;
        foreach (var s in NavCatalog.Sections)
        {
            bool footer = s.Group == NavCatalog.Footer;
            if (!footer && s.Group != group)
            {
                group = s.Group;
                if (group.Length > 0) NavList.Children.Add(new TextBlock { Text = group, Style = (Style)FindResource("NavHeader") });
            }
            var item = new RadioButton { Content = s.Label, Tag = s.Id, ToolTip = s.Desc };
            item.SetResourceReference(StyleProperty, "NavItem");
            Glyph.SetIcon(item, s.Icon);
            ToolTipService.SetPlacement(item, System.Windows.Controls.Primitives.PlacementMode.Right);
            item.Checked += (_, _) => { if (!_syncingNav) NavigateTo(s.Id); };
            _navButtons[s.Id] = item;
            (footer ? NavFooter : NavList).Children.Add(item);
        }
    }

    // ------------------------------------------------------------------ navegação

    /// <summary>
    /// Abre uma secção ou aba. Aceita: "S:id" (secção, abre a última aba usada), "S:id/TAG" (aba de uma secção)
    /// ou qualquer tag antigo ("FF:fans", "PageBench"…), que é mapeado para a secção + aba onde está.
    /// </summary>
    private void NavigateTo(string tag) => NavigateTo(tag, remember: true);

    private void NavigateTo(string tag, bool remember)
    {
        if (!TryResolve(tag, out var section, out var tab)) return;
        ShowTab(section, tab, remember);
    }

    private bool TryResolve(string tag, out NavSection section, out NavTab tab)
    {
        section = null!; tab = null!;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        string? wantedTab = null;
        NavSection? s;
        if (tag.StartsWith("S:", StringComparison.Ordinal))
        {
            int slash = tag.IndexOf('/');
            string id = slash > 0 ? tag[..slash] : tag;
            if (slash > 0) wantedTab = tag[(slash + 1)..];
            s = NavCatalog.Section(id);
        }
        else
        {
            wantedTab = tag;
            // Um tag pode estar em mais do que uma secção: fica na atual se a tiver,
            // senão vai para a secção "dona" (a que tem mais abas)
            s = _section != null && _section.Tabs.Any(t => t.Tag == tag)
                ? _section
                : NavCatalog.SectionsWith(tag).OrderByDescending(x => x.Tabs.Length).FirstOrDefault();
        }
        if (s == null || s.Tabs.Length == 0) return false;
        var t = (wantedTab != null ? s.Tabs.FirstOrDefault(x => x.Tag == wantedTab) : null)
                ?? (_lastTab.TryGetValue(s.Id, out var last) ? s.Tabs.FirstOrDefault(x => x.Tag == last) : null)
                ?? s.Tabs[0];
        section = s; tab = t;
        return true;
    }

    private void ShowTab(NavSection section, NavTab tab, bool remember)
    {
        bool sameSection = ReferenceEquals(section, _section);
        if (sameSection && tab.Tag == _currentTag) { SyncNav(section.Id); return; }

        if (remember && _section != null)
        {
            _history.Add((_section.Id, _currentTag));
            if (_history.Count > HistoryLimit) _history.RemoveAt(0);
        }
        _section = section;
        _currentTag = tab.Tag;
        _lastTab[section.Id] = tab.Tag;

        SyncNav(section.Id);
        UpdateHeader(section, tab, rebuildTabs: !sameSection);

        string page = tab.Tag;
        if (page.StartsWith("FF:", StringComparison.Ordinal)) { PageFF.Content = ForgePage(page[3..]); page = "PageFF"; }
        ShowPage(page);
        OnPageOpened(page);

        TitlePage.Text = section.Tabs.Length > 1 ? $"{section.Label}  ›  {tab.Label}" : section.Label;
        BtnBack.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AnimatePageIn();
    }

    private void SyncNav(string sectionId)
    {
        if (!_navButtons.TryGetValue(sectionId, out var rb)) return;
        _syncingNav = true;
        try { rb.IsChecked = true; }
        finally { _syncingNav = false; }
        rb.BringIntoView();
    }

    private void UpdateHeader(NavSection section, NavTab tab, bool rebuildTabs)
    {
        SectionTitle.Text = section.Label;
        SectionDesc.Text = tab.Desc.Length > 0 ? tab.Desc : section.Desc;
        string help = tab.Help.Length > 0 ? tab.Help : section.Desc;
        BtnHelp.ToolTip = help;
        HelpText.Text = help;
        HelpPanel.Visibility = Visibility.Collapsed;

        if (rebuildTabs)
        {
            if (section.Tabs.Length > 1)
            {
                var strip = new TabStrip();
                bool advanced = false;
                foreach (var t in section.Tabs.OrderBy(t => t.Advanced))
                {
                    if (t.Advanced && !advanced) { strip.AddLabel("Avançado"); advanced = true; }
                    strip.Add(t.Tag, t.Label, t.Icon, t.Desc);
                }
                strip.SelectionChanged += key => NavigateTo($"{section.Id}/{key}");
                _tabs = strip;
                SectionTabsHost.Content = strip;
                SectionTabsHost.Visibility = Visibility.Visible;
            }
            else
            {
                _tabs = null;
                SectionTabsHost.Content = null;
                SectionTabsHost.Visibility = Visibility.Collapsed;
            }
        }
        _tabs?.Select(tab.Tag);
    }

    /// <summary>Entrada da página: opacidade 0→1 e desliza 14 px→0 em ~220 ms.</summary>
    private void AnimatePageIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(220);
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        if (PageHost.RenderTransform is TranslateTransform tt)
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, dur) { EasingFunction = ease });
    }

    // ------------------------------------------------------------------ Voltar

    private void GoBack()
    {
        while (_history.Count > 0)
        {
            var (sectionId, tag) = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            var section = NavCatalog.Section(sectionId);
            var tab = section?.Tabs.FirstOrDefault(t => t.Tag == tag);
            if (section == null || tab == null) continue;
            ShowTab(section, tab, remember: false);
            return;
        }
        BtnBack.Visibility = Visibility.Collapsed;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void Help_Click(object sender, RoutedEventArgs e) =>
        HelpPanel.Visibility = HelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void Shell_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.K && mods == ModifierKeys.Control)
        {
            OpenQuickSearch();
            e.Handled = true;
        }
        else if ((e.Key == Key.System && e.SystemKey == Key.Left && mods == ModifierKeys.Alt) || e.Key == Key.BrowserBack)
        {
            if (_searchOverlay?.Visibility == Visibility.Visible) return;
            GoBack();
            e.Handled = true;
        }
    }

    private void Shell_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.XButton1) return;
        if (_searchOverlay?.Visibility == Visibility.Visible) CloseQuickSearch();
        else GoBack();
        e.Handled = true;
    }
}
