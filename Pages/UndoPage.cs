using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameForge.Core;
using FrameForge.Undo;

namespace FrameForge.Pages;

/// <summary>
/// Central Desfazer (tag "FF:undo"): tudo o que o HyperCore mudou no PC e ainda está mudado, agrupado por categoria, com data,
/// "Desfazer" por item (com confirmação na própria linha) e "Desfazer tudo" (janela de escolha por categoria).
/// A recolha e a reversão estão em <see cref="UndoCenter"/> (usa as APIs de reversão que já existem). Nada bloqueia o UI thread.
/// O cabeçalho (título, descrição, botão ?) vem da janela principal (NavCatalog); a página começa logo no conteúdo.
/// </summary>
public sealed class UndoPage : UserControl
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");
    private static readonly FontFamily IconFont = (FontFamily)Ui.Theme["Icons"];

    private readonly Grid _layer = new();
    private readonly StackPanel _root = new() { Margin = Ui.PageMargin };

    // cartão de resumo
    private readonly TextBlock _heroCount = new() { FontSize = 26, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _heroGlyph = new() { FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Ellipse _ring = new() { StrokeThickness = 4 };
    private readonly RotateTransform _ringSpin = new();
    private readonly TextBlock _heroTitle;
    private readonly TextBlock _heroSub;
    private readonly WrapPanel _heroFacts = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _undoAll;
    private readonly Button _refresh;

    private readonly StackPanel _banners = new();
    private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 4, 0, 14) };
    private readonly StackPanel _list = new();
    private readonly StackPanel _logList = new();
    private readonly Border _logCard;

    // janela "Desfazer tudo"
    private readonly Grid _overlay = new() { Visibility = Visibility.Collapsed };
    private readonly Border _dialog;
    private readonly StackPanel _dialogBody = new() { Margin = new Thickness(0, 0, 14, 0) };
    private readonly Border _dialogFooter = new();
    private bool _dialogRunning;

    private List<UndoItem> _items = new();
    private UndoCategory? _filter;
    private int _loadVersion;
    private bool _preview;
    private readonly SemaphoreSlim _undoGate = new(1, 1);
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly List<(DateTime When, bool Ok, string Text)> _log = new();

    public UndoPage()
    {
        Ui.UseTheme(this);

        _root.Children.Add(BuildHero(out _heroTitle, out _heroSub, out _undoAll, out _refresh));
        _root.Children.Add(_banners);
        _root.Children.Add(_chips);
        _root.Children.Add(_list);
        _logCard = BuildLogCard();
        _root.Children.Add(_logCard);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _root };
        _layer.Children.Add(scroll);
        _dialog = BuildOverlay();
        _layer.Children.Add(_overlay);
        Content = _layer;

        _debounce.Tick += (_, _) => { _debounce.Stop(); if (!_preview) _ = ReloadAsync(); };
        Loaded += (_, _) =>
        {
            ChangeJournal.Changed += OnJournalChanged;
            if (!_preview) _ = ReloadAsync();
        };
        Unloaded += (_, _) => ChangeJournal.Changed -= OnJournalChanged;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _overlay.Visibility == Visibility.Visible && !_dialogRunning) { CloseDialog(); e.Handled = true; }
        };
        ShowLoading();
    }

    /// <summary>Para o harness de pré-visualização: preenche a página com itens dados (sem ler o PC).</summary>
    internal void ShowItems(List<UndoItem> items)
    {
        _preview = true;
        _loadVersion++;
        _items = items;
        Render();
    }

    private void OnJournalChanged() => Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });

    // ================================================================== recolha

    private async Task ReloadAsync()
    {
        int v = ++_loadVersion;
        if (_items.Count == 0) ShowLoading();
        _refresh.IsEnabled = false;
        List<UndoItem> items;
        try { items = await UndoCenter.CollectAsync(); }
        catch { items = new(); }
        if (v != _loadVersion) return;
        _items = items;
        _refresh.IsEnabled = true;
        Render();
    }

    private void ShowLoading()
    {
        _heroTitle.Text = "A procurar alterações…";
        _heroSub.Text = "A ver o que o HyperCore mudou no teu PC. Só leitura.";
        _heroCount.Text = "";
        _heroGlyph.Visibility = Visibility.Collapsed;
        _ring.Stroke = Ui.Res("AccentGradient");
        _ring.StrokeDashArray = new DoubleCollection { 9, 14 };
        _ringSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
        _undoAll.Visibility = Visibility.Collapsed;
        _heroFacts.Children.Clear();
    }

    // ================================================================== desenho

    private void Render()
    {
        _ringSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        _ringSpin.Angle = 0;
        _ring.StrokeDashArray = null;

        int total = _items.Count;
        int canUndo = _items.Count(i => i.CanUndo);
        if (total == 0)
        {
            _heroCount.Text = "";
            _heroGlyph.Text = "";
            _heroGlyph.Foreground = Ui.Res("Good");
            _heroGlyph.Visibility = Visibility.Visible;
            _ring.Stroke = Ui.Res("Good");
            _heroTitle.Text = "Está tudo como de origem";
            _heroSub.Text = "O HyperCore não tem nada mudado no teu PC. Quando aplicares um ajuste, aparece aqui para o poderes desfazer.";
            _undoAll.Visibility = Visibility.Collapsed;
        }
        else
        {
            _heroGlyph.Visibility = Visibility.Collapsed;
            _heroCount.Text = total.ToString(Pt);
            _heroCount.Foreground = Ui.Res("Fg");
            _ring.Stroke = Ui.Res("AccentGradient");
            _heroTitle.Text = total == 1 ? "1 alteração ativa" : $"{total} alterações ativas";
            _heroSub.Text = "Tudo o que o HyperCore mudou e ainda está mudado. Desfazer volta exatamente ao que estava antes.";
            _undoAll.Visibility = Visibility.Visible;
            _undoAll.IsEnabled = canUndo > 0;
        }

        _heroFacts.Children.Clear();
        if (total > 0)
        {
            int restart = _items.Count(i => i.NeedsRestart);
            var latest = _items.Where(i => i.When != null && !i.WhenApprox).Select(i => i.When!.Value).DefaultIfEmpty().Max();
            int cats = _items.Select(i => i.Category).Distinct().Count();
            _heroFacts.Children.Add(Fact("", cats == 1 ? "1 categoria" : $"{cats} categorias", null));
            if (latest != default) _heroFacts.Children.Add(Fact("", "Última: " + WhenText(latest, false), null));
            if (restart > 0) _heroFacts.Children.Add(Fact("", restart == 1 ? "1 precisa de reiniciar" : $"{restart} precisam de reiniciar", Ui.Res("Warn")));
            int blocked = total - canUndo;
            if (blocked > 0) _heroFacts.Children.Add(Fact("", blocked == 1 ? "1 não dá agora" : $"{blocked} não dão agora", Ui.Res("Muted")));
        }

        RenderBanners();
        RenderChips();
        RenderList();
    }

    private void RenderBanners()
    {
        _banners.Children.Clear();
        if (_items.Any(i => i.Blocked?.Contains("administrador") == true))
            _banners.Children.Add(Banner("", "Abre o HyperCore como administrador para desfazer tudo.",
                "Algumas alterações mexem em definições de todo o PC e o Windows só deixa mudá-las com permissões de administrador.", Ui.Res("Warn")));
        if (Games.GameRunner.TemporaryTweaksActive)
            _banners.Children.Add(Banner("", "Há um jogo aberto pelo botão JOGAR.",
                "Os ajustes temporários do jogo voltam sozinhos quando o fechares. Até lá, os ajustes do Windows ficam bloqueados aqui.", Ui.Res("Accent")));
    }

    private void RenderChips()
    {
        _chips.Children.Clear();
        var groups = _items.GroupBy(i => i.Category).OrderBy(g => (int)g.Key).ToList();
        if (groups.Count < 2) { _chips.Visibility = Visibility.Collapsed; _filter = null; return; }
        _chips.Visibility = Visibility.Visible;
        if (_filter != null && groups.All(g => g.Key != _filter)) _filter = null;
        string group = "undo-filter-" + GetHashCode();
        _chips.Children.Add(Chip(group, "Tudo", _items.Count, null));
        foreach (var g in groups) _chips.Children.Add(Chip(group, UndoCenter.Categories[g.Key].Title, g.Count(), g.Key));
    }

    private RadioButton Chip(string group, string label, int count, UndoCategory? cat)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = "  " + count.ToString(Pt), Opacity = 0.7, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var rb = new RadioButton { Content = content, GroupName = group, IsChecked = _filter == cat, Margin = new Thickness(0, 0, 8, 8) };
        rb.Style = (Style)Ui.Theme["Chip"];
        rb.Checked += (_, _) =>
        {
            if (_filter == cat) return;
            _filter = cat;
            RenderList();
        };
        return rb;
    }

    private void RenderList()
    {
        _list.Children.Clear();
        if (_items.Count == 0) { _list.Children.Add(EmptyHelp()); return; }
        var groups = _items.Where(i => _filter == null || i.Category == _filter).GroupBy(i => i.Category).OrderBy(g => (int)g.Key);
        int n = 0;
        foreach (var g in groups)
        {
            var card = CategoryCard(UndoCenter.Categories[g.Key], g.ToList());
            _list.Children.Add(card);
            Appear(card, n++ * 40);
        }
    }

    // ---------------------------------------------------------------- cartões

    private Border BuildHero(out TextBlock title, out TextBlock sub, out Button undoAll, out Button refresh)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var ring = new Grid { Width = 78, Height = 78, Margin = new Thickness(0, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center };
        ring.Children.Add(new Ellipse { Fill = Ui.Res("Bg"), Stroke = Ui.Res("Line"), StrokeThickness = 4 });
        _ring.RenderTransformOrigin = new Point(0.5, 0.5);
        _ring.RenderTransform = _ringSpin;
        _ring.StrokeDashCap = PenLineCap.Round;
        ring.Children.Add(_ring);
        _heroGlyph.FontFamily = IconFont;
        ring.Children.Add(_heroCount);
        ring.Children.Add(_heroGlyph);
        grid.Children.Add(ring);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.T("CENTRAL DESFAZER", "Label", 10.5, Ui.Res("Accent2")));
        title = Ui.T("", "H1", 22);
        title.Margin = new Thickness(0, 2, 0, 2);
        text.Children.Add(title);
        sub = Ui.T("", "MutedText", 13);
        sub.Margin = new Thickness(0, 0, 16, 0);
        text.Children.Add(sub);
        text.Children.Add(_heroFacts);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        refresh = IconTextButton("", "Atualizar", "Btn");
        refresh.ToolTip = "Voltar a ler o que está mudado no PC.";
        refresh.Click += (_, _) => _ = ReloadAsync();
        actions.Children.Add(refresh);
        undoAll = IconTextButton("", "Desfazer tudo…", "BtnAccent");
        undoAll.Padding = new Thickness(18, 11, 18, 11);
        undoAll.Margin = new Thickness(0);
        undoAll.ToolTip = "Escolhe o que repor e volta tudo ao que estava antes.";
        undoAll.Click += (_, _) => OpenDialog();
        actions.Children.Add(undoAll);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        return new Border
        {
            Background = new LinearGradientBrush(((SolidColorBrush)Ui.Res("AccentSoft")).Color, ((SolidColorBrush)Ui.Res("Card")).Color, new Point(0, 0), new Point(1, 1)),
            BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18),
            Padding = new Thickness(24, 20, 24, 20), Margin = new Thickness(0, 0, 0, 14), Child = grid,
        };
    }

    private Border CategoryCard(UndoCategoryInfo info, List<UndoItem> items)
    {
        var body = new StackPanel();

        var head = new DockPanel { Margin = new Thickness(12, 0, 12, 8), LastChildFill = true };
        var tile = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(11), Background = Ui.Res("AccentSoft"), Margin = new Thickness(0, 0, 14, 0),
            Child = Icon(info.Glyph, 18, Ui.Res("Accent2")),
        };
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        if (info.OpenTag != null)
        {
            var open = LinkButton(info.OpenLabel, info.OpenTag);
            DockPanel.SetDock(open, Dock.Right);
            head.Children.Add(open);
        }
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var tRow = new StackPanel { Orientation = Orientation.Horizontal };
        var t = Ui.T(info.Title, "H2", 16);
        t.Margin = new Thickness(0, 0, 8, 0);
        tRow.Children.Add(t);
        var pill = Ui.Pill(items.Count.ToString(Pt), Ui.Res("Hover"), Ui.Res("Muted"));
        pill.Margin = new Thickness(0, -4, 0, 0);
        tRow.Children.Add(pill);
        titles.Children.Add(tRow);
        titles.Children.Add(Ui.T(info.Hint, "MutedText", 12.5));
        head.Children.Add(titles);
        body.Children.Add(head);

        bool first = true;
        foreach (var it in items.OrderByDescending(i => i.When ?? DateTime.MinValue))
        {
            if (!first) body.Children.Add(new Border { Height = 1, Background = Ui.Res("Line"), Margin = new Thickness(12, 0, 12, 0), Opacity = 0.7 });
            first = false;
            body.Children.Add(ItemRow(it));
        }

        return new Border
        {
            Background = Ui.Res("Card"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(8, 16, 8, 8), Margin = new Thickness(0, 0, 0, 12), Child = body,
        };
    }

    private static Button RowButton(string glyph, string text)
    {
        var b = IconTextButton(glyph, text, "Btn");
        b.Padding = new Thickness(12, 7, 14, 7);
        b.Margin = new Thickness(0);
        b.FontSize = 12.5;
        b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }

    private FrameworkElement ItemRow(UndoItem it)
    {
        var outer = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 11, 12, 11), Background = Brushes.Transparent };
        var stack = new StackPanel();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 8, Height = 8, Margin = new Thickness(2, 6, 14, 0), VerticalAlignment = VerticalAlignment.Top,
            Fill = it.Caution != null ? Ui.Res("Warn") : it.CanUndo ? Ui.Res("Accent") : Ui.Res("Faint"),
        };
        grid.Children.Add(dot);

        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var title = new TextBlock { Text = it.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Fg") };
        text.Children.Add(title);
        if (it.Detail.Length > 0)
        {
            var d = Ui.T(it.Detail, "MutedText", 12.5);
            d.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(d);
        }
        var tags = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (it.NeedsRestart) tags.Children.Add(Badge("", "Reiniciar para ficar completo", Ui.Res("Warn")));
        if (it.Caution != null) tags.Children.Add(Badge("", it.Caution, Ui.Res("Warn")));
        if (it.Blocked != null) tags.Children.Add(Badge("", it.Blocked, Ui.Res("Muted")));
        if (tags.Children.Count > 0) text.Children.Add(tags);
        if (!string.IsNullOrWhiteSpace(it.Tip)) text.ToolTip = it.Tip;
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (it.When is DateTime w)
        {
            var when = new TextBlock
            {
                Text = WhenText(w, it.WhenApprox), FontSize = 12, Foreground = Ui.Res("Faint"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0),
                ToolTip = it.WhenApprox ? $"A Central Desfazer viu esta alteração pela primeira vez a {w.ToString("d 'de' MMMM, HH:mm", Pt)}. Pode ser mais antiga."
                                        : w.ToString("dddd, d 'de' MMMM 'de' yyyy, HH:mm", Pt),
            };
            Grid.SetColumn(when, 2);
            grid.Children.Add(when);
        }

        Button btn;
        if (!it.CanUndo && it.OpenTag != null && it.Blocked?.Contains("página") == true)
        {
            btn = RowButton("", "Abrir");
            string tag = it.OpenTag;
            btn.Click += (_, _) => AppServices.Navigate(tag);
            btn.ToolTip = it.Blocked;
        }
        else
        {
            btn = RowButton("", "Desfazer");
            btn.IsEnabled = it.CanUndo;
            if (!it.CanUndo) { ToolTipService.SetShowOnDisabled(btn, true); btn.ToolTip = it.Blocked; }
        }
        Grid.SetColumn(btn, 3);
        grid.Children.Add(btn);
        stack.Children.Add(grid);

        // confirmação na própria linha
        var confirm = new Border
        {
            Margin = new Thickness(22, 10, 0, 2), Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(10),
            Background = Ui.Res("Bg"), BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
        };
        var cDock = new DockPanel();
        var cButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        var cancel = Ui.Btn("Cancelar", (_, _) => { confirm.Visibility = Visibility.Collapsed; btn.Visibility = Visibility.Visible; });
        cancel.Padding = new Thickness(12, 7, 12, 7);
        var yes = new Button { Content = IconText("", "Sim, desfazer"), Style = (Style)Ui.Theme["BtnDanger"], Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0) };
        cButtons.Children.Add(cancel);
        cButtons.Children.Add(yes);
        DockPanel.SetDock(cButtons, Dock.Right);
        cDock.Children.Add(cButtons);
        var cText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        cText.Children.Add(Ui.T(it.Effect.Length > 0 ? it.Effect : "Volta ao que estava antes.", "Body", 13));
        if (it.NeedsRestart) cText.Children.Add(Ui.T("Algumas partes só voltam depois de reiniciar o PC.", "MutedText", 12, Ui.Res("Warn")));
        cDock.Children.Add(cText);
        confirm.Child = cDock;
        stack.Children.Add(confirm);

        var result = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 8, 0, 0), Visibility = Visibility.Collapsed };
        stack.Children.Add(result);
        outer.Child = stack;

        outer.MouseEnter += (_, _) => outer.Background = Ui.Res("Hover");
        outer.MouseLeave += (_, _) => outer.Background = Brushes.Transparent;

        if (it.CanUndo)
        {
            btn.Click += (_, _) =>
            {
                result.Visibility = Visibility.Collapsed;
                btn.Visibility = Visibility.Collapsed;
                confirm.Visibility = Visibility.Visible;
                Appear(confirm, 0, 6);
                yes.Focus();
            };
            yes.Click += async (_, _) =>
            {
                yes.IsEnabled = cancel.IsEnabled = false;
                yes.Content = IconText("", "A desfazer…");
                UndoOutcome o;
                await _undoGate.WaitAsync();
                try { o = await UndoCenter.UndoOneAsync(it); }
                finally { _undoGate.Release(); }
                AddLog(o.Ok, o.Message);
                confirm.Visibility = Visibility.Collapsed;
                result.Visibility = Visibility.Visible;
                result.Text = (o.Ok ? "✔  " : "✖  ") + o.Message;
                result.Foreground = o.Ok ? Ui.Res("Good") : Ui.Res("Bad");
                if (o.Ok)
                {
                    dot.Fill = Ui.Res("Good");
                    title.Foreground = Ui.Res("Muted");
                    title.TextDecorations = TextDecorations.Strikethrough;
                    FadeAndReload(outer);
                }
                else
                {
                    yes.IsEnabled = cancel.IsEnabled = true;
                    yes.Content = IconText("", "Sim, desfazer");
                    btn.Visibility = Visibility.Visible;
                    btn.Content = IconText("", "Tentar de novo");
                }
            };
        }
        return outer;
    }

    private void FadeAndReload(FrameworkElement row)
    {
        var fade = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(500)) { BeginTime = TimeSpan.FromMilliseconds(900) };
        fade.Completed += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        row.BeginAnimation(OpacityProperty, fade);
    }

    private static Border EmptyHelp()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.T("O QUE APARECE AQUI", "Label"));
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var c in new[] { UndoCategory.Level, UndoCategory.Startup, UndoCategory.Priorities, UndoCategory.Overclock, UndoCategory.Network, UndoCategory.GamerSetup })
        {
            var info = UndoCenter.Categories[c];
            var row = new DockPanel { Margin = new Thickness(0, 0, 16, 12) };
            var ic = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Ui.Res("Hover"), Margin = new Thickness(0, 0, 12, 0), Child = Icon(info.Glyph, 14, Ui.Res("Accent2")), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(ic, Dock.Left);
            row.Children.Add(ic);
            var txt = new StackPanel();
            txt.Children.Add(Ui.T(info.Title, "Body", 13.5, weight: FontWeights.SemiBold));
            txt.Children.Add(Ui.T(info.Hint, "MutedText", 12));
            row.Children.Add(txt);
            grid.Children.Add(row);
        }
        sp.Children.Add(grid);
        var card = Ui.Card(sp);
        Appear(card, 60);
        return card;
    }

    private Border BuildLogCard()
    {
        var sp = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var clear = Ui.Btn("Limpar", (_, _) => { _log.Clear(); RenderLog(); });
        clear.Padding = new Thickness(10, 5, 10, 5);
        clear.Margin = new Thickness(0);
        clear.FontSize = 12;
        DockPanel.SetDock(clear, Dock.Right);
        head.Children.Add(clear);
        head.Children.Add(Ui.T("Desfeito nesta sessão", "H2", 15));
        sp.Children.Add(head);
        sp.Children.Add(_logList);
        var card = Ui.Card(sp);
        card.Margin = new Thickness(0, 6, 0, 0);
        card.Visibility = Visibility.Collapsed;
        return card;
    }

    private void AddLog(bool ok, string text)
    {
        _log.Insert(0, (DateTime.Now, ok, text));
        if (_log.Count > 40) _log.RemoveAt(_log.Count - 1);
        RenderLog();
    }

    private void RenderLog()
    {
        _logList.Children.Clear();
        _logCard.Visibility = _log.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (when, ok, text) in _log) _logList.Children.Add(ResultRow(ok, text, when));
    }

    private static DockPanel ResultRow(bool ok, string text, DateTime? when = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        if (when is DateTime w)
        {
            var time = new TextBlock { Text = w.ToString("HH:mm", Pt), FontSize = 12, Foreground = Ui.Res("Faint"), Width = 46, VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(time, Dock.Left);
            row.Children.Add(time);
        }
        var ic = Icon(ok ? "" : "", 11, ok ? Ui.Res("Good") : Ui.Res("Bad"));
        ic.Margin = new Thickness(0, 3, 10, 0);
        ic.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(ic, Dock.Left);
        row.Children.Add(ic);
        row.Children.Add(Ui.T(text, ok ? "MutedText" : "Body", 12.5));
        return row;
    }

    // ================================================================== Desfazer tudo

    private Border BuildOverlay()
    {
        var bg = ((SolidColorBrush)Ui.Res("Bg")).Color;
        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0xD0, bg.R, bg.G, bg.B)) };
        backdrop.MouseLeftButtonDown += (_, _) => { if (!_dialogRunning) CloseDialog(); };
        _overlay.Children.Add(backdrop);
        var dialog = new Border
        {
            Background = Ui.Res("Card"), BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18),
            Padding = new Thickness(26, 22, 26, 22), MaxWidth = 620, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 40, ShadowDepth = 8, Opacity = 0.55 },
            Child = DialogLayout(),
        };
        _overlay.Children.Add(dialog);
        return dialog;
    }

    /// <summary>Corpo com scroll + rodapé fixo (os botões ficam sempre à vista, mesmo com muitas categorias).</summary>
    private Grid DialogLayout()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _dialogBody, Margin = new Thickness(0, 0, -14, 0),
        };
        g.Children.Add(scroll);
        Grid.SetRow(_dialogFooter, 1);
        g.Children.Add(_dialogFooter);
        return g;
    }

    private void ClearDialog()
    {
        _dialogBody.Children.Clear();
        _dialogFooter.Child = null;
    }

    private void OpenDialog()
    {
        if (_items.Count == 0) return;
        BuildChooser();
        _overlay.Visibility = Visibility.Visible;
        _overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        var st = new ScaleTransform(0.96, 0.96);
        _dialog.RenderTransformOrigin = new Point(0.5, 0.5);
        _dialog.RenderTransform = st;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        Focus();
    }

    private void CloseDialog()
    {
        _overlay.Visibility = Visibility.Collapsed;
        ClearDialog();
    }

    /// <summary>Passo 1: escolher as categorias (os itens com aviso ficam de fora, a não ser que o utilizador os inclua).</summary>
    private void BuildChooser()
    {
        ClearDialog();
        _dialogBody.Children.Add(DialogHead("", "Desfazer tudo?", "Escolhe o que queres repor. Cada coisa volta ao que estava antes de o HyperCore mexer."));

        var undoable = _items.Where(i => i.CanUndo).ToList();
        var chosen = new HashSet<UndoCategory>(undoable.Select(i => i.Category));
        bool includeCaution = false;
        var go = new Button { Style = (Style)Ui.Theme["BtnDanger"], Margin = new Thickness(0), Padding = new Thickness(18, 10, 18, 10) };
        var restartNote = Ui.T("", "MutedAuto", 12.5);
        restartNote.Foreground = Ui.Res("Warn");
        restartNote.Margin = new Thickness(2, 10, 0, 0);

        List<UndoItem> Selection() => undoable.Where(i => chosen.Contains(i.Category) && (includeCaution || i.Caution == null)).ToList();
        void Update()
        {
            var sel = Selection();
            go.Content = IconText("", sel.Count == 0 ? "Nada escolhido" : sel.Count == 1 ? "Desfazer 1 alteração" : $"Desfazer {sel.Count} alterações");
            go.IsEnabled = sel.Count > 0;
            restartNote.Text = sel.Any(i => i.NeedsRestart) ? "Alguns ajustes só voltam por completo depois de reiniciar o PC." : "";
        }

        var list = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var g in undoable.GroupBy(i => i.Category).OrderBy(g => (int)g.Key))
        {
            var info = UndoCenter.Categories[g.Key];
            var cat = g.Key;
            var content = new DockPanel { Width = 470 };
            var count = Ui.Pill(g.Count().ToString(Pt), Ui.Res("Hover"), Ui.Res("Muted"));
            count.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(count, Dock.Right);
            content.Children.Add(count);
            var ic = Icon(info.Glyph, 15, Ui.Res("Accent2"));
            ic.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(ic, Dock.Left);
            content.Children.Add(ic);
            var names = string.Join(", ", g.Take(3).Select(i => i.Title)) + (g.Count() > 3 ? $" e mais {g.Count() - 3}" : "");
            var txt = new StackPanel();
            txt.Children.Add(new TextBlock { Text = info.Title, FontWeight = FontWeights.SemiBold, FontSize = 13.5 });
            txt.Children.Add(new TextBlock { Text = names, FontSize = 12, Foreground = Ui.Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(txt);
            var cb = new CheckBox { Content = content, IsChecked = true, Margin = new Thickness(0) };
            cb.Checked += (_, _) => { chosen.Add(cat); Update(); };
            cb.Unchecked += (_, _) => { chosen.Remove(cat); Update(); };
            list.Children.Add(new Border { Padding = new Thickness(14, 11, 14, 11), CornerRadius = new CornerRadius(10), Background = Ui.Res("Bg"), Margin = new Thickness(0, 0, 0, 6), Child = cb });
        }
        _dialogBody.Children.Add(list);

        int cautionCount = undoable.Count(i => i.Caution != null);
        if (cautionCount > 0)
        {
            var cc = new CheckBox
            {
                Content = Ui.T(cautionCount == 1 ? "Incluir também 1 item que pode não ter sido o HyperCore a mudar" : $"Incluir também {cautionCount} itens que podem não ter sido o HyperCore a mudar", "Body", 13),
                IsChecked = false, Margin = new Thickness(2, 10, 0, 0),
                ToolTip = string.Join("\n", undoable.Where(i => i.Caution != null).Select(i => $"• {i.Title}: {i.Caution}")),
            };
            cc.Checked += (_, _) => { includeCaution = true; Update(); };
            cc.Unchecked += (_, _) => { includeCaution = false; Update(); };
            _dialogBody.Children.Add(cc);
        }
        int blocked = _items.Count - undoable.Count;
        if (blocked > 0)
        {
            var b = Ui.T(blocked == 1 ? "1 alteração não pode ser desfeita agora e fica como está." : $"{blocked} alterações não podem ser desfeitas agora e ficam como estão.", "MutedText", 12.5);
            b.Margin = new Thickness(2, 10, 0, 0);
            _dialogBody.Children.Add(b);
        }
        _dialogBody.Children.Add(restartNote);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = Ui.Btn("Cancelar", (_, _) => CloseDialog());
        cancel.Padding = new Thickness(16, 10, 16, 10);
        buttons.Children.Add(cancel);
        go.Click += async (_, _) => await RunAllAsync(Selection());
        buttons.Children.Add(go);
        _dialogFooter.Child = buttons;
        Update();
    }

    /// <summary>Passo 2 e 3: desfaz com barra de progresso e mostra o resultado de cada item.</summary>
    private async Task RunAllAsync(List<UndoItem> selection)
    {
        if (selection.Count == 0 || _dialogRunning) return;
        _dialogRunning = true;
        ClearDialog();
        _dialogBody.Children.Add(DialogHead("", "A desfazer…", "Não feches a app. Cada alteração volta ao que estava antes."));

        var track = new Grid { Height = 8, Margin = new Thickness(0, 18, 0, 8), ClipToBounds = true };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Ui.Res("Hover") });
        var fillScale = new ScaleTransform(0, 1);
        track.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Ui.Res("AccentGradient"), RenderTransform = fillScale });
        _dialogBody.Children.Add(track);
        var current = Ui.T("", "MutedText", 12.5);
        _dialogBody.Children.Add(current);

        var progress = new Progress<(int Done, int Total, UndoItem? Current)>(p =>
        {
            double to = p.Total == 0 ? 1 : (double)p.Done / p.Total;
            fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            current.Text = p.Current != null ? $"{p.Done + 1} de {p.Total} · {UndoCenter.Categories[p.Current.Category].Title}: {p.Current.Title}" : "";
        });

        List<(UndoItem Item, UndoOutcome Outcome)> done;
        await _undoGate.WaitAsync();
        try { done = await UndoCenter.UndoManyAsync(selection, progress); }
        finally { _undoGate.Release(); }
        foreach (var (_, o) in done) AddLog(o.Ok, o.Message);
        _dialogRunning = false;
        ShowResults(done);
        await ReloadAsync();
    }

    private void ShowResults(List<(UndoItem Item, UndoOutcome Outcome)> done)
    {
        int ok = done.Count(d => d.Outcome.Ok), fail = done.Count - ok;
        ClearDialog();
        _dialogBody.Children.Add(fail == 0
            ? DialogHead("", "Feito", ok == 1 ? "1 alteração desfeita. Voltou ao que estava antes." : $"{ok} alterações desfeitas. Voltou tudo ao que estava antes.", Ui.Res("Good"))
            : DialogHead("", "Feito, com falhas", $"{ok} desfeitas, {fail} {(fail == 1 ? "falhou" : "falharam")}. As que falharam continuam na lista.", Ui.Res("Warn")));
        var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (_, o) in done.OrderBy(d => d.Outcome.Ok)) list.Children.Add(ResultRow(o.Ok, o.Message));
        _dialogBody.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var close = Ui.Btn("Fechar", (_, _) => CloseDialog());
        close.Margin = new Thickness(0);
        close.Padding = new Thickness(18, 10, 18, 10);
        buttons.Children.Add(close);
        _dialogFooter.Child = buttons;
        close.Focus();
    }

    private static FrameworkElement DialogHead(string glyph, string title, string sub, Brush? color = null)
    {
        var dock = new DockPanel();
        var tile = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top,
            Background = color == null ? Ui.Res("AccentSoft") : Tint(color, 0x26),
            Child = Icon(glyph, 20, color ?? Ui.Res("Accent2")),
        };
        DockPanel.SetDock(tile, Dock.Left);
        dock.Children.Add(tile);
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(Ui.T(title, "H1", 20));
        t.Children.Add(Ui.T(sub, "MutedText", 13));
        dock.Children.Add(t);
        return dock;
    }

    // ================================================================== peças pequenas

    private static TextBlock Icon(string glyph, double size, Brush color) => new()
    {
        Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = color,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private static StackPanel IconText(string glyph, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private static Button IconTextButton(string glyph, string text, string style) =>
        new() { Content = IconText(glyph, text), Style = (Style)Ui.Theme[style] };

    private static Button LinkButton(string label, string tag)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 10, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = sp, Style = (Style)Ui.Theme["Btn"], Padding = new Thickness(12, 6, 10, 6), Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("Muted"), ToolTip = $"Abrir {label}",
        };
        b.Click += (_, _) => AppServices.Navigate(tag);
        return b;
    }

    private static Border Fact(string glyph, string text, Brush? color)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var ic = Icon(glyph, 12, color ?? Ui.Res("Accent2"));
        ic.Margin = new Thickness(0, 1, 7, 0);
        sp.Children.Add(ic);
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = color ?? Ui.Res("Muted"), VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Child = sp, CornerRadius = new CornerRadius(99), Padding = new Thickness(10, 4, 12, 4), Margin = new Thickness(0, 0, 8, 4),
            Background = Tint(Ui.Res("Bg"), 0x99), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1),
        };
    }

    private static Border Badge(string glyph, string text, Brush color)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var ic = Icon(glyph, 10.5, color);
        ic.Margin = new Thickness(0, 1, 6, 0);
        sp.Children.Add(ic);
        sp.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = color, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return new Border { Child = sp, CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 8, 3), Margin = new Thickness(0, 2, 6, 0), Background = Tint(color, 0x1A) };
    }

    private static Border Banner(string glyph, string title, string text, Brush color)
    {
        var dock = new DockPanel();
        var ic = Icon(glyph, 16, color);
        ic.Margin = new Thickness(0, 1, 14, 0);
        ic.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(ic, Dock.Left);
        dock.Children.Add(ic);
        var sp = new StackPanel();
        sp.Children.Add(Ui.T(title, "Body", 13.5, color, FontWeights.SemiBold));
        sp.Children.Add(Ui.T(text, "MutedText", 12.5));
        dock.Children.Add(sp);
        return new Border
        {
            Child = dock, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 12),
            Background = Tint(color, 0x14), BorderBrush = Tint(color, 0x55), BorderThickness = new Thickness(1),
        };
    }

    private static SolidColorBrush Tint(Brush b, byte alpha)
    {
        var c = b is SolidColorBrush s ? s.Color : Colors.Gray;
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    /// <summary>Entrada suave (opacidade + deslize), como a transição das páginas.</summary>
    private static void Appear(FrameworkElement el, int delayMs, double dy = 10)
    {
        var tt = new TranslateTransform(0, dy);
        el.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(delayMs);
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = begin });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(220)) { BeginTime = begin, EasingFunction = ease });
    }

    private static string WhenText(DateTime d, bool approx)
    {
        var today = DateTime.Today;
        int days = (int)(today - d.Date).TotalDays;
        string Day() => d.ToString(d.Year == today.Year ? "d MMM" : "d MMM yyyy", Pt).Replace(".", "");
        if (approx) return "desde " + (days <= 0 ? "hoje" : days == 1 ? "ontem" : Day());
        if (days <= 0) return "hoje, " + d.ToString("HH:mm", Pt);
        if (days == 1) return "ontem, " + d.ToString("HH:mm", Pt);
        if (days < 7) return $"há {days} dias";
        return Day();
    }
}
