using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameForge.Games;
using FrameForge.Optimize;

namespace FrameForge.Pages;

/// <summary>
/// Modo Jogo (tag "FF:gamemode"): botão grande Ligar/Desligar, o que vai fazer, as opções, os programas que mais pesam
/// e o registo do que fez. O motor está em <see cref="GameMode"/>; esta página só lê e pede.
/// </summary>
public sealed class GameModePage : UserControl
{
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // Botão redondo
    private readonly Grid _orb = new() { Width = 168, Height = 168 };
    private readonly Ellipse _wave = new() { Width = 150, Height = 150, StrokeThickness = 2, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly Ellipse _ringOn = new() { Width = 150, Height = 150, StrokeThickness = 3, Opacity = 0 };
    private readonly Path _arc = new() { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), Width = 150, Height = 150 };
    private readonly Ellipse _inner = new() { Width = 122, Height = 122, StrokeThickness = 1 };
    private readonly TextBlock _orbGlyph = new() { Text = "\uE7E8", FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _orbLabel = new() { FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
    private readonly DropShadowEffect _glow = new() { BlurRadius = 30, ShadowDepth = 0, Opacity = 0, Color = Color.FromRgb(0x2E, 0x9B, 0xFF) };
    private readonly ScaleTransform _orbScale = new(1, 1);

    // Cartão principal
    private readonly Border _hero = new();
    private readonly Border _statePill = new() { CornerRadius = new CornerRadius(99), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _stateText = new() { FontSize = 10.5, FontWeight = FontWeights.Bold };
    private readonly Ellipse _stateDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sub = Ui.T("", "MutedText", 13.5);
    private readonly StackPanel _preview = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly WrapPanel _stats = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly WrapPanel _hints = new() { Margin = new Thickness(0, 16, 0, 0) };

    // Opções
    private readonly WrapPanel _launcherChips = new();
    private readonly WrapPanel _cloudChips = new();
    private readonly WrapPanel _heavyChips = new();
    private readonly WrapPanel _browserChips = new();
    private readonly TextBlock _optionsNote = Ui.T("", "MutedText");
    private readonly TextBlock _levelGain = Ui.T("", "MutedText");
    private readonly TextBlock _ramNow = Ui.T("", "MutedText");

    // Programas
    private readonly StackPanel _heavyList = new();
    private readonly WrapPanel _extraChips = new();
    private readonly WrapPanel _keepChips = new();

    // Registo
    private readonly TextBlock _logHead = Ui.T("", "MutedText");
    private readonly StackPanel _log = new();

    // Confirmação (1.ª vez)
    private readonly Grid _overlay = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _confirmList = new() { Margin = new Thickness(0, 12, 0, 6) };

    private readonly Grid _columns = new();
    private readonly Border _perfCard, _progCard;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private List<RunningApp> _running = new();
    private string _runningSig = "";
    private bool _scanned;
    private string _progress = "";
    private bool _scanning;

    public GameModePage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = Ui.PageMargin };

        root.Children.Add(BuildHero());

        var opts = Ui.SectionTitle("O que faz quando ligas", "Escolhe o que fechar e pausar. Tudo volta ao que estava quando desligas.");
        opts.Margin = new Thickness(0, 22, 0, 10);
        root.Children.Add(opts);
        root.Children.Add(BuildOptions());

        _perfCard = BuildPerformance();
        _progCard = BuildPrograms();
        _columns.ColumnDefinitions.Add(new ColumnDefinition());
        _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        _columns.ColumnDefinitions.Add(new ColumnDefinition());
        _columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _columns.Children.Add(_perfCard);
        _columns.Children.Add(_progCard);
        _columns.Margin = new Thickness(0, 6, 0, 0);
        LayoutColumns(true);
        root.Children.Add(_columns);

        var logTitle = Ui.SectionTitle("O que fez");
        logTitle.Margin = new Thickness(0, 12, 0, 10);
        root.Children.Add(logTitle);
        root.Children.Add(BuildLog());

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root };
        var host = new Grid();
        host.Children.Add(scroll);
        host.Children.Add(BuildOverlay());
        Content = host;

        SizeChanged += (_, e) => LayoutColumns(e.NewSize.Width >= 900);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _overlay.Visibility == Visibility.Visible) { HideConfirm(); e.Handled = true; }
        };
        _timer.Tick += (_, _) => { Rescan(); UpdateElapsed(); };
        Loaded += (_, _) =>
        {
            GameMode.Init(); // a janela já o chama no arranque; aqui é só uma rede de segurança (não faz nada da 2.ª vez)
            GameMode.StateChanged += OnState;
            GameMode.Progress += OnProgress;
            GameMode.ConfirmRequested += OnConfirmRequested;
            Rescan();
            _timer.Start();
            Refresh();
            if (GameMode.ConfirmPending) ShowConfirm();
        };
        Unloaded += (_, _) =>
        {
            GameMode.StateChanged -= OnState;
            GameMode.Progress -= OnProgress;
            GameMode.ConfirmRequested -= OnConfirmRequested;
            _timer.Stop();
        };

        Refresh();
    }

    // =================================================================================================== construção

    private UIElement BuildHero()
    {
        // ---- botão redondo
        _wave.Stroke = Ui.Res("Accent2");
        _wave.RenderTransform = new ScaleTransform(1, 1);
        _ringOn.Stroke = Ui.Res("AccentGradient");
        var track = new Ellipse { Width = 150, Height = 150, StrokeThickness = 3, Stroke = Ui.Res("Line") };
        _arc.Data = Geometry.Parse("M 75,1.5 A 73.5,73.5 0 0 1 148.5,75");
        _arc.Stroke = Ui.Res("AccentGradient");
        _arc.RenderTransform = new RotateTransform(0);
        _inner.Effect = _glow;
        _orbGlyph.FontFamily = Icons;
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        center.Children.Add(_orbGlyph);
        center.Children.Add(_orbLabel);
        foreach (var e in new UIElement[] { _wave, track, _ringOn, _arc, _inner, center }) _orb.Children.Add(e);
        foreach (var e in new FrameworkElement[] { _wave, track, _ringOn, _arc, _inner })
        {
            e.HorizontalAlignment = HorizontalAlignment.Center;
            e.VerticalAlignment = VerticalAlignment.Center;
        }
        _orb.RenderTransformOrigin = new Point(0.5, 0.5);
        _orb.RenderTransform = _orbScale;

        var orbButton = new Button
        {
            Content = _orb, Cursor = Cursors.Hand, Focusable = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Template = PlainTemplate(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 28, 0),
        };
        orbButton.Click += (_, _) => OrbClicked();
        orbButton.MouseEnter += (_, _) => AnimateScale(1.04, 160);
        orbButton.MouseLeave += (_, _) => AnimateScale(1.0, 200);
        orbButton.PreviewMouseLeftButtonDown += (_, _) => AnimateScale(0.97, 80);
        orbButton.PreviewMouseLeftButtonUp += (_, _) => AnimateScale(1.04, 80);
        ToolTipService.SetInitialShowDelay(orbButton, 350);
        orbButton.ToolTip = "Liga ou desliga o Modo Jogo (" + GameMode.HotkeyText + ")";

        // ---- texto
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
        pillRow.Children.Add(_stateDot);
        pillRow.Children.Add(_stateText);
        _statePill.Child = pillRow;

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_statePill);
        text.Children.Add(_title);
        text.Children.Add(_sub);
        text.Children.Add(_preview);
        text.Children.Add(_stats);
        text.Children.Add(_hints);

        _hints.Children.Add(Hint("\uE765", GameMode.HotkeyText, "liga e desliga em qualquer lado, até dentro do jogo"));
        _hints.Children.Add(Hint("\uE7A7", "Reversível", "fica na Central Desfazer enquanto está ligado"));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(orbButton);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        _hero.CornerRadius = new CornerRadius(16);
        _hero.BorderThickness = new Thickness(1);
        _hero.Padding = new Thickness(26, 24, 26, 20);
        _hero.Child = grid;
        return _hero;
    }

    private UIElement BuildOptions()
    {
        var c = GameMode.Config;
        var list = new StackPanel();
        list.Children.Add(OptionRow("\uE7FC", "Fechar launchers que não estás a usar",
            "Epic, EA app, Ubisoft Connect, Battle.net, GOG. Toca num para o manter aberto (ex.: o do jogo que vais abrir).",
            c.CloseLaunchers, v => GameMode.Update(x => x.CloseLaunchers = v), _launcherChips));
        list.Children.Add(Divider());
        list.Children.Add(OptionRow("\uE753", "Pausar a nuvem",
            "OneDrive, Dropbox e Google Drive deixam de usar disco e rede. Voltam a sincronizar quando desligas.",
            c.PauseCloud, v => GameMode.Update(x => x.PauseCloud = v), _cloudChips));
        list.Children.Add(Divider());
        list.Children.Add(OptionRow("\uE71D", "Fechar programas pesados de fundo",
            "Teams, Adobe Creative Cloud, Ligação ao Telemóvel e os que juntares à lista.",
            c.CloseHeavy, v => GameMode.Update(x => x.CloseHeavy = v), _heavyChips));
        list.Children.Add(Divider());
        list.Children.Add(OptionRow("\uE774", "Fechar navegadores",
            "Opcional. Pede para fechar como no X, nunca à força: se tiveres algo por guardar, o navegador fica aberto.",
            c.CloseBrowsers, v => GameMode.Update(x => x.CloseBrowsers = v), _browserChips));
        list.Children.Add(Divider());
        list.Children.Add(OptionRow("\uE895", "Pausar o Windows Update",
            $"Não descarrega nem instala nada enquanto jogas. A pausa acaba sozinha em {GameMode.UpdatePauseHours} h, mesmo que feches o HyperCore.",
            c.PauseUpdates, v => GameMode.Update(x => x.PauseUpdates = v), null));
        list.Children.Add(Divider());
        list.Children.Add(OptionRow("\uE950", "Libertar memória em cache",
            "Esvazia uma vez a lista de espera da RAM. Ajuda nos PCs com 8–16 GB quando o jogo engasga ao carregar zonas novas.",
            c.FreeRam, v => GameMode.Update(x => x.FreeRam = v), null, _ramNow));
        list.Children.Add(Divider());
        list.Children.Add(OptionRow("\uE72C", "Voltar a abrir os programas no fim",
            "Ao desligar, abre outra vez o que o Modo Jogo fechou (launchers, nuvem e o resto).",
            c.Reopen, v => GameMode.Update(x => x.Reopen = v), null));
        _optionsNote.Margin = new Thickness(54, 0, 0, 10);
        _optionsNote.Foreground = Ui.Res("Warn");
        list.Children.Add(_optionsNote);
        var card = Ui.Card(list);
        card.Padding = new Thickness(20, 6, 20, 6);
        return card;
    }

    private Border BuildPerformance()
    {
        var c = GameMode.Config;
        var sp = new StackPanel();
        sp.Children.Add(CardHeader("\uEC4A", "Desempenho", "Só enquanto o Modo Jogo está ligado."));

        sp.Children.Add(Small("NÍVEL DE DESEMPENHO", new Thickness(0, 16, 0, 8)));
        var levels = new WrapPanel();
        for (int i = 0; i <= 4; i++)
        {
            int lvl = i;
            var rb = Chip(TweakCatalog.LevelNames[i], "gm-level", c.Level == i, () =>
            {
                GameMode.Update(x => x.Level = lvl);
                UpdateLevelGain();
            });
            rb.ToolTip = TweakCatalog.LevelDescriptions[i];
            levels.Children.Add(rb);
        }
        sp.Children.Add(levels);
        _levelGain.Margin = new Thickness(0, 2, 0, 0);
        sp.Children.Add(_levelGain);
        UpdateLevelGain();

        sp.Children.Add(Small("PLANO DE ENERGIA", new Thickness(0, 18, 0, 8)));
        var plans = new WrapPanel();
        string[] names = { "Não mexer", "Alto desempenho", "Desempenho máximo" };
        string[] tips =
        {
            "Fica o plano que tens agora.",
            "O processador responde logo, sem poupar energia. Recomendado.",
            "Ainda mais agressivo. Num portátil gasta mais bateria e aquece mais.",
        };
        for (int i = 0; i < 3; i++)
        {
            int plan = i;
            var rb = Chip(names[i], "gm-plan", c.Plan == i, () => GameMode.Update(x => x.Plan = plan));
            rb.ToolTip = tips[i];
            plans.Children.Add(rb);
        }
        sp.Children.Add(plans);
        var note = Ui.T("Usa só os ajustes que fazem efeito na hora (sem reiniciar). Ao desligar, cada um volta ao valor exato que tinha.", "MutedText", 12.5);
        note.Margin = new Thickness(0, 10, 0, 0);
        note.Foreground = Ui.Res("Faint");
        sp.Children.Add(note);
        var card = Ui.Card(sp);
        card.Margin = new Thickness(0, 0, 0, 12);
        return card;
    }

    private Border BuildPrograms()
    {
        var sp = new StackPanel();
        sp.Children.Add(CardHeader("\uE9D9", "O que pesa agora", "Os programas abertos que mais memória usam."));
        _heavyList.Margin = new Thickness(-10, 10, -8, 0);
        sp.Children.Add(_heavyList);
        sp.Children.Add(Small("SEMPRE NA LISTA PARA FECHAR", new Thickness(0, 14, 0, 6)));
        sp.Children.Add(_extraChips);
        sp.Children.Add(Small("MANTER SEMPRE ABERTOS", new Thickness(0, 10, 0, 6)));
        sp.Children.Add(_keepChips);
        var card = Ui.Card(sp);
        card.Margin = new Thickness(0, 0, 0, 12);
        return card;
    }

    private UIElement BuildLog()
    {
        var sp = new StackPanel();
        sp.Children.Add(_logHead);
        _log.Margin = new Thickness(0, 10, 0, 0);
        sp.Children.Add(_log);
        return Ui.Card(sp);
    }

    private UIElement BuildOverlay()
    {
        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x06, 0x09, 0x10)) };
        backdrop.MouseLeftButtonDown += (_, _) => HideConfirm();
        _overlay.Children.Add(backdrop);

        var body = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var icon = IconTile("\uE7FC", 44);
        icon.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var h = Ui.T("Ligar o Modo Jogo", "H2", 19);
        h.Margin = new Thickness(0, 0, 0, 2);
        titles.Children.Add(h);
        titles.Children.Add(Ui.T("É a primeira vez, por isso mostro-te o que vai acontecer agora:", "MutedText"));
        head.Children.Add(titles);
        body.Children.Add(head);
        body.Children.Add(_confirmList);

        var warn = new DockPanel { Margin = new Thickness(0, 6, 0, 20) };
        var wi = GlyphText("\uE7BA", 14, Ui.Res("Warn"));
        wi.Margin = new Thickness(0, 2, 10, 0);
        wi.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(wi, Dock.Left);
        warn.Children.Add(wi);
        warn.Children.Add(Ui.T("Guarda o que tiveres aberto nos programas da lista. A partir de agora, o botão e o atalho " +
                               Keys + " ligam logo, sem perguntar.", "MutedText", 12.5));
        body.Children.Add(warn);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Ui.Btn("Cancelar", (_, _) => HideConfirm()));
        var go = Ui.Btn("Ligar Modo Jogo", (_, _) =>
        {
            GameMode.Update(x => x.Confirmed = true);
            HideConfirm();
            _ = GameMode.Start();
        }, "BtnAccent");
        go.Margin = new Thickness(0);
        buttons.Children.Add(go);
        body.Children.Add(buttons);

        var card = Ui.Card(body);
        card.MaxWidth = 560;
        card.Margin = new Thickness(24);
        card.Padding = new Thickness(24, 22, 24, 22);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        card.BorderBrush = Ui.Res("LineStrong");
        card.Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black };
        _overlay.Children.Add(card);
        return _overlay;
    }

    // =================================================================================================== estado

    private void OnState() => Dispatcher.BeginInvoke(() => { Refresh(); Rescan(); });

    private void OnProgress(string text) => Dispatcher.BeginInvoke(() =>
    {
        _progress = text;
        if (GameMode.IsBusy) _sub.Text = text;
        RefreshLog();
    });

    private void OnConfirmRequested() => Dispatcher.BeginInvoke(ShowConfirm);

    private void OrbClicked()
    {
        if (GameMode.IsBusy) return;
        if (GameMode.IsActive) { _ = GameMode.Stop(); return; }
        if (!GameMode.Config.Confirmed) { ShowConfirm(); return; }
        _ = GameMode.Start();
    }

    private void ShowConfirm()
    {
        GameMode.ConfirmPending = false;
        if (GameMode.IsActive || GameMode.IsBusy) return;
        _confirmList.Children.Clear();
        foreach (var line in GameMode.Preview(GameMode.Config, _running))
            _confirmList.Children.Add(CheckLine(line, 13.5));
        _overlay.Visibility = Visibility.Visible;
        _overlay.Opacity = 0;
        _overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    private void HideConfirm() => _overlay.Visibility = Visibility.Collapsed;

    /// <summary>Lê a lista de processos em segundo plano (só nomes, memória e janelas) e atualiza o que depende dela.</summary>
    private void Rescan()
    {
        if (_scanning || (IsLoaded && !IsVisible)) return;
        _scanning = true;
        Task.Run(() =>
        {
            List<RunningApp> list;
            try { list = GameMode.ScanRunning(); } catch { list = new(); }
            long? standby = GameMode.StandbyBytes();
            Dispatcher.BeginInvoke(() =>
            {
                _scanning = false;
                _ramNow.Text = standby is long b && b > 0 ? $"Agora há {GameMode.Gb(b)} em cache." : "";
                _ramNow.Visibility = _ramNow.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                // Só reconstrói quando muda o que está aberto (ou a memória mexe a sério): o rato não perde o hover a cada 4 s
                string sig = string.Join("|", list.Select(a => $"{a.Process}:{a.MemoryBytes >> 26}:{a.HasWindow}"));
                if (_scanned && sig == _runningSig) return;
                _scanned = true;
                _runningSig = sig;
                _running = list;
                RefreshDynamic();
            });
        });
    }

    /// <summary>Tudo o que depende do estado do motor (ligado/desligado/ocupado).</summary>
    private void Refresh()
    {
        var s = GameMode.Session;
        bool busy = GameMode.IsBusy, on = s != null;
        var accent = Ui.Res("Accent");

        // Botão
        _orbLabel.Text = busy ? (on ? "A TRABALHAR" : "A LIGAR…") : on ? "DESLIGAR" : "LIGAR";
        _inner.Fill = on ? Ui.Res("AccentGradient") : new LinearGradientBrush(Color.FromRgb(0x1C, 0x27, 0x3D), Color.FromRgb(0x12, 0x19, 0x28), 90);
        _inner.Stroke = on ? Brushes.Transparent : Ui.Res("LineStrong");
        _orbGlyph.Foreground = on ? Brushes.White : accent;
        _orbLabel.Foreground = on ? Brushes.White : Ui.Res("Muted");
        _glow.Opacity = on ? 0.65 : 0.18;
        _ringOn.Opacity = on && !busy ? 1 : 0;
        SetSpinner(busy);
        SetPulse(on && !busy);

        // Cartão
        _hero.Background = on
            ? new LinearGradientBrush(Color.FromRgb(0x12, 0x2A, 0x4A), Color.FromRgb(0x13, 0x1A, 0x29), new Point(0, 0), new Point(1, 1))
            : new LinearGradientBrush(Color.FromRgb(0x16, 0x21, 0x37), Color.FromRgb(0x13, 0x1A, 0x29), new Point(0, 0), new Point(1, 1));
        _hero.BorderBrush = on ? new SolidColorBrush(Color.FromArgb(0x99, 0x2E, 0x9B, 0xFF)) : Ui.Res("Line");
        _statePill.Background = busy ? new SolidColorBrush(Color.FromArgb(0x26, 0xFB, 0xBF, 0x24))
            : on ? new SolidColorBrush(Color.FromArgb(0x26, 0x34, 0xD3, 0x99)) : Ui.Res("Hover");
        _stateDot.Fill = busy ? Ui.Res("Warn") : on ? Ui.Res("Good") : Ui.Res("Faint");
        _stateText.Foreground = busy ? Ui.Res("Warn") : on ? Ui.Res("Good") : Ui.Res("Muted");
        _stateText.Text = busy ? "A TRABALHAR" : on ? "LIGADO" : "DESLIGADO";
        _title.Text = busy ? (on && GameMode.Session?.JournalId is { Length: > 0 } ? "A repor tudo…" : "A preparar o PC…") : on ? "Modo Jogo ligado" : "Pronto para jogar?";
        if (busy) _sub.Text = _progress.Length > 0 ? _progress : "Um momento…";
        else if (on) _sub.Text = "Quando acabares de jogar, desliga no mesmo botão: repõe tudo como estava.";
        else _sub.Text = "Liga antes de abrir o jogo. Isto é o que vai fazer agora:";

        _optionsNote.Text = on ? "Estas escolhas valem para a próxima vez que ligares." : "";
        _optionsNote.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        UpdateStats(s);
        RefreshDynamic();
        RefreshLog();
    }

    /// <summary>O que depende da lista de processos e da configuração: chips, resumo e "o que pesa agora".</summary>
    private void RefreshDynamic()
    {
        var c = GameMode.Config;
        bool on = GameMode.IsActive, busy = GameMode.IsBusy;

        // Resumo do que vai fazer (só desligado)
        _preview.Children.Clear();
        _preview.Visibility = on || busy ? Visibility.Collapsed : Visibility.Visible;
        if (!on && !busy)
            foreach (var line in GameMode.Preview(c, _running).Take(5))
                _preview.Children.Add(CheckLine(line, 13));

        FillGroupChips(_launcherChips, GameModeGroup.Launcher, c, c.CloseLaunchers, "Nenhum launcher aberto agora.");
        FillGroupChips(_cloudChips, GameModeGroup.Cloud, c, c.PauseCloud, "Nenhuma nuvem a sincronizar agora.");
        FillGroupChips(_heavyChips, GameModeGroup.Heavy, c, c.CloseHeavy, "Nenhum aberto agora.");
        FillGroupChips(_browserChips, GameModeGroup.Browser, c, c.CloseBrowsers, "Nenhum navegador aberto.");

        // O que pesa agora (6 maiores)
        _heavyList.Children.Clear();
        var top = _running.Where(a => a.HasWindow || a.Group != null).Take(6).ToList();
        if (top.Count == 0)
        {
            var t = Ui.T(_scanned ? "Não há programas pesados abertos." : "A ler os programas abertos…", "MutedText");
            t.Margin = new Thickness(10, 0, 0, 0);
            _heavyList.Children.Add(t);
        }
        foreach (var app in top) _heavyList.Children.Add(HeavyRow(app, c));

        _extraChips.Children.Clear();
        foreach (var name in c.Extra)
        {
            string n = name;
            _extraChips.Children.Add(RemovableChip(DisplayName(n), () => GameMode.Update(x => x.Extra.RemoveAll(e => e.Equals(n, StringComparison.OrdinalIgnoreCase))),
                "Tirar da lista"));
        }
        if (c.Extra.Count == 0) _extraChips.Children.Add(Faint("Ainda nenhum. Usa \"Fechar no Modo Jogo\" acima."));

        _keepChips.Children.Clear();
        foreach (var name in c.Keep)
        {
            string n = name;
            _keepChips.Children.Add(RemovableChip(DisplayName(n), () => GameMode.Update(x => x.Keep.RemoveAll(e => e.Equals(n, StringComparison.OrdinalIgnoreCase))),
                "Deixar o Modo Jogo fechar este também"));
        }
        if (c.Keep.Count == 0) _keepChips.Children.Add(Faint("Nenhum."));
    }

    private void UpdateStats(GameModeSession? s)
    {
        _stats.Children.Clear();
        _stats.Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
        if (s == null) return;
        int mins = (int)Math.Max(0, (DateTime.UtcNow - s.StartedUtc).TotalMinutes);
        _stats.Children.Add(Stat("\uE916", "LIGADO HÁ", mins < 1 ? "agora" : GameMode.Duration(mins)));
        _stats.Children.Add(Stat("\uE711", "FECHADOS", s.Closed.Count == 0 ? "nenhum" : s.Closed.Count.ToString(),
            s.Closed.Count > 0 ? string.Join(", ", s.Closed.Select(a => a.Name).Distinct()) : null));
        if (s.RamFreedBytes > 0) _stats.Children.Add(Stat("\uE950", "RAM LIBERTADA", GameMode.Gb(s.RamFreedBytes)));
        if (s.UpdatesUntilUtc is DateTime u) _stats.Children.Add(Stat("\uE895", "WINDOWS UPDATE", "em pausa", $"Até às {u.ToLocalTime():HH:mm}"));
        if (s.Tweaks.Count > 0) _stats.Children.Add(Stat("\uEC4A", "NÍVEL", TweakCatalog.LevelNames[Math.Clamp(s.Level, 0, 4)], $"{s.Tweaks.Count} ajustes temporários"));
        if (s.PlanName != null) _stats.Children.Add(Stat("\uE945", "ENERGIA", s.PlanName));
    }

    private void UpdateElapsed()
    {
        if (GameMode.IsActive && !GameMode.IsBusy) UpdateStats(GameMode.Session);
    }

    private void RefreshLog()
    {
        var c = GameMode.Config;
        var s = GameMode.Session;
        var items = s?.Log ?? c.LastLog;
        _log.Children.Clear();
        if (s != null) _logHead.Text = $"Sessão atual · ligado às {s.StartedUtc.ToLocalTime():HH:mm}";
        else if (c.LastStartUtc is DateTime st)
        {
            string when = When(st.ToLocalTime());
            string dur = c.LastEndUtc is DateTime en ? " · durou " + GameMode.Duration((int)Math.Max(1, (en - st).TotalMinutes)) : "";
            _logHead.Text = "Última vez: " + when + dur;
        }
        else _logHead.Text = "Ainda não usaste o Modo Jogo. Quando o ligares, aparece aqui tudo o que fez e o que repôs.";
        foreach (var item in items.TakeLast(14)) _log.Children.Add(LogRow(item));
        _log.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLevelGain()
    {
        int l = Math.Clamp(GameMode.Config.Level, 0, 4);
        _levelGain.Text = l == 0 ? "Não mexe nos ajustes do Windows." : TweakCatalog.LevelGains[l];
    }

    private void LayoutColumns(bool wide)
    {
        Grid.SetRow(_perfCard, 0); Grid.SetColumn(_perfCard, 0); Grid.SetColumnSpan(_perfCard, wide ? 1 : 3);
        Grid.SetRow(_progCard, wide ? 0 : 1); Grid.SetColumn(_progCard, wide ? 2 : 0); Grid.SetColumnSpan(_progCard, wide ? 1 : 3);
    }

    // =================================================================================================== animações

    private void AnimateScale(double to, int ms)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _orbScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _orbScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private bool _spinning, _pulsing;

    private void SetSpinner(bool on)
    {
        if (on == _spinning) return;
        _spinning = on;
        var rot = (RotateTransform)_arc.RenderTransform;
        if (on)
        {
            _arc.Opacity = 1;
            rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            rot.BeginAnimation(RotateTransform.AngleProperty, null);
            _arc.Opacity = 0;
        }
    }

    private void SetPulse(bool on)
    {
        if (on == _pulsing) return;
        _pulsing = on;
        var sc = (ScaleTransform)_wave.RenderTransform;
        if (on)
        {
            var dur = TimeSpan.FromSeconds(2.2);
            var grow = new DoubleAnimation(1, 1.12, dur) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _wave.BeginAnimation(OpacityProperty, new DoubleAnimation(0.6, 0, dur) { RepeatBehavior = RepeatBehavior.Forever });
            _glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
                new DoubleAnimation(26, 40, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _wave.BeginAnimation(OpacityProperty, null);
            _wave.Opacity = 0;
            _glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
        }
    }

    // =================================================================================================== peças

    private static ControlTemplate PlainTemplate() =>
        new(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };

    private static TextBlock GlyphText(string glyph, double size, Brush color) =>
        new() { Text = glyph, FontFamily = Icons, FontSize = size, Foreground = color };

    private static TextBlock Faint(string text)
    {
        var t = Ui.T(text, "MutedText", 12);
        t.Foreground = Ui.Res("Faint");
        return t;
    }

    private static Border IconTile(string glyph, double size = 38) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), Background = Ui.Res("AccentSoft"),
        Child = new TextBlock
        {
            Text = glyph, FontFamily = Icons, FontSize = size * 0.45, Foreground = Ui.Res("Accent"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static UIElement Divider() => new Border { Height = 1, Background = Ui.Res("Line"), Margin = new Thickness(54, 0, 0, 0) };

    private static TextBlock Small(string text, Thickness margin)
    {
        var t = Ui.T(text, "Label");
        t.Margin = margin;
        t.Foreground = Ui.Res("Faint");
        return t;
    }

    private static UIElement CardHeader(string glyph, string title, string sub)
    {
        var dock = new DockPanel();
        var icon = IconTile(glyph, 34);
        icon.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var t = Ui.T(title, "H2", 15.5);
        t.Margin = new Thickness(0);
        sp.Children.Add(t);
        sp.Children.Add(Ui.T(sub, "MutedText", 12.5));
        dock.Children.Add(sp);
        return dock;
    }

    private static UIElement OptionRow(string glyph, string title, string desc, bool on, Action<bool> changed, WrapPanel? chips, TextBlock? extra = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 14, 0, 14) };
        var icon = IconTile(glyph);
        icon.Margin = new Thickness(0, 0, 16, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        var sw = new ToggleButton { IsChecked = on, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 7, 0, 0) };
        sw.SetResourceReference(StyleProperty, "Switch");
        sw.Checked += (_, _) => changed(true);
        sw.Unchecked += (_, _) => changed(false);
        DockPanel.SetDock(sw, Dock.Right);
        row.Children.Add(sw);

        var mid = new StackPanel();
        mid.Children.Add(Ui.T(title, "Body", 14, weight: FontWeights.SemiBold));
        var d = Ui.T(desc, "MutedText", 12.5);
        d.Margin = new Thickness(0, 2, 0, 0);
        mid.Children.Add(d);
        if (extra != null)
        {
            extra.Margin = new Thickness(0, 4, 0, 0);
            extra.FontSize = 12.5;
            extra.Foreground = Ui.Res("Accent2");
            mid.Children.Add(extra);
        }
        if (chips != null)
        {
            chips.Margin = new Thickness(0, 9, 0, -6);
            mid.Children.Add(chips);
        }
        row.Children.Add(mid);
        return row;
    }

    private RadioButton Chip(string text, string group, bool on, Action picked)
    {
        var rb = new RadioButton { Content = text, GroupName = group + GetHashCode(), IsChecked = on, Margin = new Thickness(0, 0, 8, 8) };
        rb.SetResourceReference(StyleProperty, "Chip");
        rb.Checked += (_, _) => picked();
        ToolTipService.SetInitialShowDelay(rb, 350);
        return rb;
    }

    /// <summary>Chips dos programas de um grupo que estão abertos agora: tocar alterna entre "fecha" e "fica aberto".</summary>
    private void FillGroupChips(WrapPanel panel, GameModeGroup group, GameModeConfig c, bool groupOn, string empty)
    {
        panel.Children.Clear();
        var apps = _running.Where(a => a.Group == group).ToList();
        if (apps.Count == 0)
        {
            var t = Faint(_scanned ? empty : "A ver o que está aberto…");
            t.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(t);
            return;
        }
        foreach (var app in apps)
        {
            bool kept = c.Keep.Contains(app.Process, StringComparer.OrdinalIgnoreCase);
            panel.Children.Add(AppChip(app, GameMode.WillClose(c, app.Process), kept, groupOn));
        }
    }

    private static readonly Brush ChipLine = new SolidColorBrush(Color.FromArgb(0x70, 0x2E, 0x9B, 0xFF));

    private static UIElement AppChip(RunningApp app, bool closes, bool kept, bool groupOn)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var mark = GlyphText(closes ? "\uE711" : kept ? "\uE72E" : "\uE73E", 9, closes ? Ui.Res("Accent2") : kept ? Ui.Res("Good") : Ui.Res("Faint"));
        mark.Margin = new Thickness(0, 1, 7, 0);
        mark.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(mark);
        row.Children.Add(new TextBlock { Text = app.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = closes ? Ui.Res("Fg") : Ui.Res("Muted"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock
        {
            Text = kept ? "  fica aberto" : "  " + GameMode.Mb(app.MemoryBytes), FontSize = 11.5,
            Foreground = kept ? Ui.Res("Good") : Ui.Res("Faint"), VerticalAlignment = VerticalAlignment.Center
        });
        var b = new Border
        {
            CornerRadius = new CornerRadius(99), Padding = new Thickness(10, 4, 11, 4), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
            Background = closes ? Ui.Res("AccentSoft") : Ui.Res("Hover"), BorderThickness = new Thickness(1),
            BorderBrush = closes ? ChipLine : Ui.Res("Line"), Child = row,
            ToolTip = kept ? "Fica aberto. Toca para o Modo Jogo também o fechar."
                : closes ? "Vai ser fechado. Toca para o manter aberto."
                : groupOn ? "Toca para o fechar no Modo Jogo." : "Liga o interruptor para fechar este grupo.",
        };
        ToolTipService.SetInitialShowDelay(b, 350);
        b.MouseEnter += (_, _) => b.BorderBrush = Ui.Res("Accent");
        b.MouseLeave += (_, _) => b.BorderBrush = closes ? ChipLine : Ui.Res("Line");
        b.MouseLeftButtonUp += (_, _) =>
        {
            string p = app.Process;
            GameMode.Update(x =>
            {
                if (kept) x.Keep.RemoveAll(k => k.Equals(p, StringComparison.OrdinalIgnoreCase));
                else if (closes)
                {
                    x.Extra.RemoveAll(k => k.Equals(p, StringComparison.OrdinalIgnoreCase));
                    x.Keep.Add(p);
                }
                else if (groupOn) x.Extra.Add(p);
            });
        };
        return b;
    }

    private static UIElement HeavyRow(RunningApp app, GameModeConfig c)
    {
        bool closes = GameMode.WillClose(c, app.Process);
        var row = new DockPanel();
        var wrap = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 8, 6), Background = Brushes.Transparent, Child = row };
        wrap.MouseEnter += (_, _) => wrap.Background = Ui.Res("Hover");
        wrap.MouseLeave += (_, _) => wrap.Background = Brushes.Transparent;

        var glyph = app.Group switch
        {
            GameModeGroup.Launcher => "\uE7FC", GameModeGroup.Cloud => "\uE753", GameModeGroup.Browser => "\uE774", _ => "\uE71D",
        };
        var icon = GlyphText(glyph, 15, closes ? Ui.Res("Accent2") : Ui.Res("Muted"));
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        var b = new Button
        {
            Content = closes ? "Não fechar" : "Fechar no Modo Jogo", Padding = new Thickness(11, 5, 11, 5), FontSize = 12, Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, MinWidth = closes ? 0 : 140,
        };
        b.SetResourceReference(StyleProperty, "Btn");
        b.Click += (_, _) => GameMode.Update(x =>
        {
            if (closes)
            {
                if (x.Extra.RemoveAll(e => e.Equals(app.Process, StringComparison.OrdinalIgnoreCase)) == 0 || GameMode.WillClose(x, app.Process))
                    x.Keep.Add(app.Process);
            }
            else
            {
                x.Keep.RemoveAll(k => k.Equals(app.Process, StringComparison.OrdinalIgnoreCase));
                if (!GameMode.WillClose(x, app.Process)) x.Extra.Add(app.Process);
            }
        });
        DockPanel.SetDock(b, Dock.Right);
        row.Children.Add(b);

        if (closes)
        {
            var tag = new Border
            {
                CornerRadius = new CornerRadius(99), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Ui.Res("AccentSoft"), Child = new TextBlock { Text = "VAI FECHAR", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Ui.Res("Accent2") }
            };
            DockPanel.SetDock(tag, Dock.Right);
            row.Children.Add(tag);
        }

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = app.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        mid.Children.Add(new TextBlock
        {
            Text = GameMode.Mb(app.MemoryBytes) + (app.Count > 1 ? $" · {app.Count} processos" : ""), FontSize = 11.5, Foreground = Ui.Res("Faint")
        });
        row.Children.Add(mid);
        return wrap;
    }

    private static UIElement RemovableChip(string text, Action remove, string tip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var x = new TextBlock { Text = "\uE711", FontFamily = Icons, FontSize = 9, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("Muted") };
        row.Children.Add(x);
        var b = new Border
        {
            CornerRadius = new CornerRadius(99), Padding = new Thickness(10, 4, 9, 4), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
            Background = Ui.Res("Hover"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), Child = row, ToolTip = tip,
        };
        b.MouseEnter += (_, _) => { b.BorderBrush = Ui.Res("Bad"); x.Foreground = Ui.Res("Bad"); };
        b.MouseLeave += (_, _) => { b.BorderBrush = Ui.Res("Line"); x.Foreground = Ui.Res("Muted"); };
        b.MouseLeftButtonUp += (_, _) => remove();
        return b;
    }

    private static UIElement CheckLine(string text, double size)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 2, 0, 3) };
        var g = GlyphText("\uE73E", 12, Ui.Res("Accent2"));
        g.Margin = new Thickness(0, 4, 10, 0);
        g.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(g, Dock.Left);
        dock.Children.Add(g);
        dock.Children.Add(Ui.T(text, "Body", size));
        return dock;
    }

    private static UIElement Hint(string glyph, string strong, string rest)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 6) };
        var g = GlyphText(glyph, 13, Ui.Res("Accent"));
        g.Margin = new Thickness(0, 1, 7, 0);
        g.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(g);
        sp.Children.Add(new TextBlock { Text = strong, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = "  " + rest, FontSize = 12, Foreground = Ui.Res("Muted"), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private static UIElement Stat(string glyph, string label, string value, string? tip = null)
    {
        var sp = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        var g = GlyphText(glyph, 11, Ui.Res("Accent2"));
        g.Margin = new Thickness(0, 1, 6, 0);
        top.Children.Add(g);
        top.Children.Add(new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Ui.Res("Muted") });
        sp.Children.Add(top);
        sp.Children.Add(new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 0) });
        var b = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 9, 14, 9), Margin = new Thickness(0, 0, 6, 6), MinWidth = 96,
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0x0A, 0x0E, 0x18)), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), Child = sp,
        };
        if (tip != null) { b.ToolTip = tip; ToolTipService.SetInitialShowDelay(b, 350); }
        return b;
    }

    private static UIElement LogRow(GameModeLogItem item)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var (glyph, brush) = item.Kind switch
        {
            GameModeLogKind.Ok => ("\uE73E", Ui.Res("Good")),
            GameModeLogKind.Warn => ("\uE7BA", Ui.Res("Warn")),
            _ => ("\uE946", Ui.Res("Muted")),
        };
        var g = GlyphText(glyph, 12, brush);
        g.Margin = new Thickness(0, 4, 12, 0);
        g.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(g, Dock.Left);
        dock.Children.Add(g);
        var time = new TextBlock { Text = item.When.ToString("HH:mm"), FontSize = 11.5, Foreground = Ui.Res("Faint"), Margin = new Thickness(12, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(time, Dock.Right);
        dock.Children.Add(time);
        dock.Children.Add(Ui.T(item.Text, "Body", 13));
        return dock;
    }

    /// <summary>"Ctrl+Alt+G" sem quebrar a linha a meio.</summary>
    private static string Keys => GameMode.HotkeyText.Replace("+", "\u2060+\u2060");

    private static string DisplayName(string process) => GameMode.Lookup(process)?.Name ?? process;

    private static string When(DateTime local)
    {
        var today = DateTime.Today;
        if (local.Date == today) return $"hoje às {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"ontem às {local:HH:mm}";
        return $"{local:dd/MM} às {local:HH:mm}";
    }
}
