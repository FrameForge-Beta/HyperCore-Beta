using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using FrameForge.Analysis;
using FrameForge.Core;
using FrameForge.Pages.Home;
using FrameForge.Telemetry;

namespace FrameForge.Pages;

/// <summary>Início — cartões ao vivo, Modo Jogo, último jogo e atalhos.</summary>
public sealed partial class HomePage
{
    /// <summary>Um cartão ao vivo: anel + valor grande + duas linhas + mini-gráfico.</summary>
    private sealed class LiveCard
    {
        public required RingGauge Ring;
        public required TextBlock Center, Big, Line1, Line2;
        public required Sparkline Spark;
    }

    private LiveCard _cpu = null!, _gpu = null!, _ram = null!, _temp = null!;
    private string _cpuName = "", _gpuName = "";

    // ---- último jogo
    private readonly ContentControl _gameBox = new() { Focusable = false };
    private MeasuredSession? _lastSession;
    private int _liveGamePid;
    private TextBlock? _liveFps, _liveLow;

    // Programas que desenham frames mas não são jogos (vídeo no browser, chat, gravação)
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HyperCore", "FrameForge", "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "explorer", "dwm", "Discord", "obs64",
        "steamwebhelper", "EpicGamesLauncher", "Spotify", "vlc", "mpc-hc64", "Teams", "ms-teams", "Zoom", "ApplicationFrameHost",
        "SearchHost", "StartMenuExperienceHost", "TextInputHost", "ShellExperienceHost", "msedgewebview2", "Code", "devenv", "Photos",
    };

    // =================================================================== cartões ao vivo

    private UIElement BuildLiveCards()
    {
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -12, 0) };
        _cpu = LiveCardView(grid, "", "PROCESSADOR", "PageSensors", "Carga, temperatura e clock do processador. Carrega para ver todos os sensores.");
        _gpu = LiveCardView(grid, "", "GRÁFICA", "PageGpu", "Carga, memória (VRAM) e consumo da placa gráfica. Carrega para ver tudo sobre a gráfica.");
        _ram = LiveCardView(grid, "", "MEMÓRIA RAM", "PageSystem", "Memória em uso agora. Acima de 85% os jogos começam a engasgar.");
        _temp = LiveCardView(grid, "", "TEMPERATURAS", "PageSensors", "A peça mais quente agora. Verde até 70 °C, amarelo até 85 °C, vermelho acima.\nA temperatura do processador precisa dos sensores avançados (Jogos › Sessões e relatórios).");
        _ = Task.Run(async () =>
        {
            try
            {
                var p = await AppServices.Probe;
                string cpu = ResetExplode.GameCatalog.ShortCpuName(p.CpuName), gpu = ResetExplode.GameCatalog.ShortGpuName(p.GpuName);
                await Dispatcher.BeginInvoke(new Action(() => { _cpuName = cpu == "?" ? "" : cpu; _gpuName = gpu == "?" ? "" : gpu; }));
            }
            catch { }
        });
        return grid;
    }

    private LiveCard LiveCardView(Panel host, string glyph, string label, string tag, string tip)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Ui.Theme["Icons"], FontSize = 13, Foreground = Ui.Res("Accent2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(Ui.T(label, "Label", 10.5));

        var ring = new RingGauge { Width = 70, Height = 70, Thickness = 7, Track = Ui.Res("Line"), Fill = Ui.Res("AccentGradient") };
        var center = new TextBlock { Text = "—", FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ringBox = new Grid { Width = 70, Height = 70, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        ringBox.Children.Add(ring);
        ringBox.Children.Add(center);

        var big = new TextBlock { Text = "—", FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Ui.Theme["Mono"], TextTrimming = TextTrimming.CharacterEllipsis };
        var l1 = new TextBlock { FontSize = 12, Foreground = Ui.Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        var l2 = new TextBlock { FontSize = 12, Foreground = Ui.Res("Faint"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(big);
        text.Children.Add(l1);
        text.Children.Add(l2);
        var body = new DockPanel();
        DockPanel.SetDock(ringBox, Dock.Left);
        body.Children.Add(ringBox);
        body.Children.Add(text);

        var spark = new Sparkline { Height = 26, Margin = new Thickness(0, 12, 0, 0), Stroke = Ui.Res("Accent"), Max = 100, Capacity = 45, ToolTip = "Últimos 45 segundos" };
        var sp = new StackPanel();
        sp.Children.Add(head);
        sp.Children.Add(body);
        sp.Children.Add(spark);

        var card = Ui.Card(sp);
        card.Padding = new Thickness(16, 14, 16, 12);
        card.Margin = new Thickness(0, 0, 12, 12);
        card.Cursor = Cursors.Hand;
        card.ToolTip = tip;
        card.MouseEnter += (_, _) => { card.BorderBrush = Ui.Res("Accent"); card.Background = Ui.Res("Hover"); };
        card.MouseLeave += (_, _) => { card.BorderBrush = Ui.Res("Line"); card.Background = Ui.Res("Card"); };
        card.MouseLeftButtonUp += (_, _) => AppServices.Navigate(tag);
        host.Children.Add(card);
        return new LiveCard { Ring = ring, Center = center, Big = big, Line1 = l1, Line2 = l2, Spark = spark };
    }

    private static Brush TempBrush(double t) => Ui.Res(t >= 85 ? "Bad" : t >= 70 ? "Warn" : "Good");
    private static string Deg(float? t) => t is float v ? $"{v:0} °C" : "—";

    private void UpdateLive(PcLiveSnapshot s)
    {
        // Processador
        if (s.CpuLoad is float cl)
        {
            _cpu.Ring.AnimateTo(cl / 100.0, 600);
            _cpu.Center.Text = $"{cl:0}%";
        }
        _cpu.Big.Text = s.CpuClockMHz is float mhz ? $"{mhz / 1000:0.0} GHz" : "—";
        _cpu.Line1.Text = string.Join("  ·  ", new[] { s.CpuTemp is float ct ? $"{ct:0} °C" : null, s.CpuPowerW is float cw ? $"{cw:0} W" : null }.Where(x => x != null));
        if (_cpu.Line1.Text.Length == 0) _cpu.Line1.Text = s.CpuClockMHz != null ? "velocidade agora" : "a ler sensores…";
        _cpu.Line2.Text = _cpuName;
        _cpu.Spark.Push(s.CpuLoad);

        // Gráfica
        if (s.GpuLoad is float gl)
        {
            _gpu.Ring.AnimateTo(gl / 100.0, 600);
            _gpu.Center.Text = $"{gl:0}%";
        }
        _gpu.Big.Text = s.VramUsedGB is float vu ? (s.VramTotalGB is float vt ? $"{vu:0.0}/{vt:0} GB" : $"{vu:0.0} GB") : "—";
        _gpu.Line1.Text = "VRAM em uso";
        _gpu.Line2.Text = string.Join("  ·  ", new[] { s.GpuTemp is float gt ? $"{gt:0} °C" : null, s.GpuPowerW is float gw ? $"{gw:0} W" : null, _gpuName }.Where(x => !string.IsNullOrEmpty(x)));
        _gpu.Spark.Push(s.GpuLoad);

        // RAM
        if (s.RamTotalGB > 0)
        {
            double pct = s.RamUsedGB / s.RamTotalGB;
            _ram.Ring.AnimateTo(pct, 600);
            _ram.Ring.Fill = pct > 0.85 ? Ui.Res("Bad") : pct > 0.7 ? Ui.Res("Warn") : Ui.Res("AccentGradient");
            _ram.Center.Text = $"{pct * 100:0}%";
            _ram.Big.Text = $"{s.RamUsedGB:0.0} GB";
            _ram.Line1.Text = $"de {s.RamTotalGB:0} GB em uso";
            _ram.Line2.Text = $"{s.RamTotalGB - s.RamUsedGB:0.0} GB livres";
            _ram.Spark.Push(pct * 100);
        }

        // Temperaturas: a peça mais quente
        var temps = new List<(string Name, float T)>();
        if (s.CpuTemp is float c) temps.Add(("CPU", c));
        if (s.GpuTemp is float g) temps.Add(("GPU", g));
        if (s.GpuHotspot is float h) temps.Add(("Hotspot", h));
        if (s.DiskTemp is float d) temps.Add(("Disco", d));
        if (temps.Count > 0)
        {
            var hot = temps.MaxBy(t => t.T);
            var brush = TempBrush(hot.T);
            _temp.Ring.Fill = brush;
            _temp.Ring.AnimateTo(Math.Clamp(hot.T / 100.0, 0, 1), 600);
            _temp.Center.Text = $"{hot.T:0}°";
            _temp.Big.Text = $"{hot.T:0} °C";
            _temp.Line1.Text = $"{hot.Name} é a mais quente";
            _temp.Line2.Text = s.CpuTemp == null
                ? "CPU: sem leitura"
                : string.Join("  ·  ", temps.Where(t => t.Name != hot.Name).Select(t => $"{t.Name} {t.T:0}°"));
            _temp.Spark.Stroke = brush;
            _temp.Spark.Push(hot.T);
        }
        else
        {
            _temp.Big.Text = "—";
            _temp.Line1.Text = _ticks < 8 ? "a ler sensores…" : "Sem leitura de temperaturas";
            _temp.Line2.Text = _ticks < 8 ? "" : "Vê O meu PC › Sensores";
        }
    }

    // =================================================================== Modo Jogo

    private Border BuildGameModeCard()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(15), Background = Ui.Res("AccentGradient"), Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x2E, 0x9B, 0xFF), BlurRadius = 22, ShadowDepth = 0, Opacity = 0.45 },
            Child = new TextBlock { Text = "", FontFamily = (FontFamily)Ui.Theme["Icons"], FontSize = 24, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        grid.Children.Add(tile);
        var sp = new StackPanel();
        sp.Children.Add(Ui.T("MODO JOGO", "Label", 10.5, Ui.Res("Accent2")));
        var h = Ui.T("Pronto para jogar?", "H2", 17);
        h.Margin = new Thickness(0, 3, 0, 3);
        sp.Children.Add(h);
        var sub = Ui.T("", "MutedText", 13);
        sub.TextWrapping = TextWrapping.Wrap;
        sp.Children.Add(sub);
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var toggle = Ui.Btn("Ligar Modo Jogo", (_, _) => FrameForge.Games.GameMode.Toggle());
        toggle.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(toggle);
        buttons.Children.Add(Ui.Btn("Opções   ›", (_, _) => AppServices.Navigate("FF:gamemode")));
        sp.Children.Add(buttons);
        Grid.SetColumn(sp, 1);
        grid.Children.Add(sp);
        var card = Ui.Card(grid);
        card.Margin = new Thickness(0, 0, 0, 12);

        // O cartão acompanha o estado do Modo Jogo (ligado pelo atalho, pela bandeja ou pela página)
        void Refresh()
        {
            var s = FrameForge.Games.GameMode.Session;
            bool busy = FrameForge.Games.GameMode.IsBusy;
            if (s != null)
            {
                int min = Math.Max(0, (int)(DateTime.UtcNow - s.StartedUtc).TotalMinutes);
                h.Text = "Modo Jogo ligado";
                sub.Text = $"Há {FrameForge.Games.GameMode.Duration(min)}: {FrameForge.Games.GameMode.Summary(s)} Ao desligar, repõe tudo.";
                toggle.Content = "Desligar e repor";
            }
            else
            {
                h.Text = "Pronto para jogar?";
                sub.Text = $"Um clique antes de jogar: fecha o que pesa em fundo, liberta RAM e aplica o teu nível. Ao desligar, repõe tudo. Atalho: {FrameForge.Games.GameMode.HotkeyText}.";
                toggle.Content = "Ligar Modo Jogo";
            }
            toggle.IsEnabled = !busy;
            if (busy) toggle.Content = "A trabalhar…";
        }
        FrameForge.Games.GameMode.StateChanged += () => Dispatcher.BeginInvoke(new Action(Refresh));
        Refresh();
        return card;
    }

    // =================================================================== último jogo / a jogar agora

    private Border BuildLastGameCard()
    {
        var card = Ui.Card(_gameBox);
        card.Margin = new Thickness(0, 0, 0, 12);
        ShowGameBox();
        return card;
    }

    private void LoadLastSession()
    {
        _ = Task.Run(() =>
        {
            MeasuredSession? s = null;
            try { s = MeasuredSessions.Load(3).FirstOrDefault(); } catch { }
            Dispatcher.BeginInvoke(new Action(() => { _lastSession = s; if (_liveGamePid == 0) ShowGameBox(); }));
        });
    }

    private void UpdateLiveGame()
    {
        int pid = 0;
        string? name = null;
        try
        {
            var rec = AppServices.Recorder;
            if (rec.Active && rec.Pid > 0) { pid = rec.Pid; name = rec.GameName; }
            else
            {
                int self = Environment.ProcessId;
                foreach (var (p, fps) in AppServices.Frames.ActivePresenters())
                {
                    if (p == self || fps < 20) continue;
                    string? n = SystemSampler.ProcessName(p);
                    if (n == null || NotGames.Contains(n)) continue;
                    pid = p; name = n;
                    break;
                }
            }
        }
        catch { }

        if (pid != _liveGamePid)
        {
            _liveGamePid = pid;
            ShowGameBox(name);
        }
        if (pid != 0 && _liveFps != null)
        {
            var live = AppServices.Frames.Live(pid);
            _liveFps.Text = live is { Fps: > 0 } l ? $"{l.Fps:0}" : "—";
            if (_liveLow != null) _liveLow.Text = live is { Low1Fps: > 0 } l2 ? $"1% low {l2.Low1Fps:0}  ·  {l2.FrameTimeMs:0.0} ms" : "";
        }
    }

    private void ShowGameBox(string? liveName = null)
    {
        _liveFps = _liveLow = null;
        var sp = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };

        if (_liveGamePid != 0)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Ui.Res("Good"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            var lab = Ui.Row(dot, Ui.T("A JOGAR AGORA", "Label", 10.5, Ui.Res("Good")));
            head.Children.Add(lab);
            sp.Children.Add(head);
            var n = Ui.T(Pretty(liveName ?? "Jogo"), "H2", 17);
            n.Margin = new Thickness(0, 3, 0, 6);
            sp.Children.Add(n);
            _liveFps = new TextBlock { Text = "—", FontSize = 34, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Ui.Theme["Mono"], Foreground = Ui.Res("Fg") };
            var unit = Ui.T("FPS", "Label", 11);
            unit.Margin = new Thickness(8, 0, 0, 7);
            unit.VerticalAlignment = VerticalAlignment.Bottom;
            sp.Children.Add(Ui.Row(_liveFps, unit));
            _liveLow = Ui.T("", "MutedText", 12.5);
            sp.Children.Add(_liveLow);
            var go = Ui.Btn("Overlay no jogo   ›", (_, _) => AppServices.Navigate("FF:overlay"));
            go.HorizontalAlignment = HorizontalAlignment.Left;
            go.Margin = new Thickness(0, 12, 0, 0);
            sp.Children.Add(go);
        }
        else if (_lastSession is { } s)
        {
            head.Children.Add(Ui.T($"ÚLTIMO JOGO  ·  {Ago(s.Start).ToUpperInvariant()}", "Label", 10.5, Ui.Res("Accent2")));
            sp.Children.Add(head);
            var titleRow = new DockPanel { Margin = new Thickness(0, 3, 0, 6) };
            if (s.Grade.Length > 0)
            {
                string key = s.Grade is "A" or "B" ? "Good" : s.Grade == "C" ? "Warn" : "Bad";
                var pill = Ui.Pill($"NOTA {s.Grade}", Tint(key, 0x30), Ui.Res(key));
                pill.Margin = new Thickness(10, 0, 0, 0);
                pill.ToolTip = s.Verdict;
                DockPanel.SetDock(pill, Dock.Right);
                titleRow.Children.Add(pill);
            }
            var name = Ui.T(s.Game, "H2", 17);
            name.Margin = new Thickness(0);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            titleRow.Children.Add(name);
            sp.Children.Add(titleRow);

            var stats = new StackPanel { Orientation = Orientation.Horizontal };
            stats.Children.Add(Stat($"{s.AvgFps:0}", "FPS médio"));
            stats.Children.Add(Stat(s.Low1Fps > 0 ? $"{s.Low1Fps:0}" : "—", "1% low"));
            stats.Children.Add(Stat(s.DurationS >= 60 ? $"{s.DurationS / 60:0}" : $"{s.DurationS:0}", s.DurationS >= 60 ? "minutos" : "segundos"));
            sp.Children.Add(stats);
            var go = Ui.Btn("Sessões e relatórios   ›", (_, _) => AppServices.Navigate("FF:sessions"));
            go.HorizontalAlignment = HorizontalAlignment.Left;
            go.Margin = new Thickness(0, 12, 0, 0);
            sp.Children.Add(go);
        }
        else
        {
            head.Children.Add(Ui.T("ÚLTIMO JOGO", "Label", 10.5, Ui.Res("Accent2")));
            sp.Children.Add(head);
            var h = Ui.T("Ainda sem jogos medidos", "H2", 17);
            h.Margin = new Thickness(0, 3, 0, 3);
            sp.Children.Add(h);
            sp.Children.Add(Ui.T("Abre um jogo pela Biblioteca: o HyperCore mede os FPS e as quebras, sem tocar no jogo.", "MutedText", 13));
            var go = Ui.Btn("Abrir Biblioteca   ›", (_, _) => AppServices.Navigate("FF:games"));
            go.HorizontalAlignment = HorizontalAlignment.Left;
            go.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(go);
        }
        _gameBox.Content = sp;
    }

    private static UIElement Stat(string value, string label)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 26, 0) };
        sp.Children.Add(new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Ui.Theme["Mono"] });
        sp.Children.Add(Ui.T(label, "MutedText", 11.5));
        return sp;
    }

    private static string Pretty(string process) =>
        process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetFileNameWithoutExtension(process) : process;

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 60) return d.TotalMinutes < 2 ? "agora mesmo" : $"há {d.TotalMinutes:0} min";
        if (t.Date == DateTime.Today) return $"hoje às {t:HH:mm}";
        if (t.Date == DateTime.Today.AddDays(-1)) return $"ontem às {t:HH:mm}";
        return d.TotalDays < 30 ? $"há {(int)d.TotalDays} dias" : $"a {t:dd/MM/yyyy}";
    }

    // =================================================================== atalhos

    private static readonly (string Glyph, string Label, string Sub, string Tag)[] Shortcuts =
    {
        ("", "Scan completo", "Mede e afina sozinho", "FF:scan"),
        ("", "Níveis", "Afinar o Windows", "FF:optimize"),
        ("", "Overlay", "FPS por cima do jogo", "FF:overlay"),
        ("", "Ping e lag", "Rede para jogar online", "FF:netgame"),
        ("", "Ventoinhas", "Curvas e ruído", "FF:fans"),
        ("", "Limpeza rápida", "Liberta RAM agora", "PageOptimize"),
        ("", "Visão geral", "Gráficos ao vivo", "PageDash"),
        ("", "Central Desfazer", "Repor o que mudou", "FF:undo"),
    };

    private UIElement BuildShortcuts()
    {
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -10, 0) };
        foreach (var (glyph, label, sub, tag) in Shortcuts)
        {
            var dock = new DockPanel();
            var icon = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Background = Ui.Res("AccentSoft"), Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)Ui.Theme["Icons"], FontSize = 16, Foreground = Ui.Res("Accent2"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Ui.Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
            dock.Children.Add(text);
            var tile = new Border
            {
                Background = Ui.Res("Card"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 10, 10), Cursor = Cursors.Hand, Child = dock,
            };
            tile.MouseEnter += (_, _) => { tile.BorderBrush = Ui.Res("Accent"); tile.Background = Ui.Res("Hover"); };
            tile.MouseLeave += (_, _) => { tile.BorderBrush = Ui.Res("Line"); tile.Background = Ui.Res("Card"); };
            tile.MouseLeftButtonUp += (_, _) => AppServices.Navigate(tag);
            grid.Children.Add(tile);
        }
        return grid;
    }
}
