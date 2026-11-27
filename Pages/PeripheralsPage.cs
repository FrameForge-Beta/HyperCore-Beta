using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FrameForge.Pages;

/// <summary>
/// Testes › Periféricos (tag "FF:peripherals"): rato (polling por Raw Input, botões, roda, duplo clique avariado),
/// teclado (teclado visual, teclas ao mesmo tempo / ghosting), ecrã (refresh real medido, píxeis mortos e rasto em
/// ecrã inteiro), comando (botões, analógicos, drift e vibração), fones (esquerda, direita, ambos, equilíbrio e
/// frequências) e câmara (webcam ao vivo, resolução, FPS e iluminação). Só leitura: nenhum teste muda o PC, por isso
/// não há nada a registar na Central Desfazer.
/// O cabeçalho (título, descrição, abas da secção) vem da janela principal; a página começa logo no conteúdo.
/// </summary>
public sealed class PeripheralsPage : UserControl
{
    private readonly TabStrip _tabs;
    private readonly ContentControl _host = new();
    private readonly Dictionary<string, IPeriphView> _views = new();
    private IPeriphView? _active;
    private bool _loaded;
    private Window? _window; // guardada no Loaded: no Unloaded a página já saiu da árvore e GetWindow devolve null
    private static string _lastTab = "mouse";

    public PeripheralsPage()
    {
        Ui.UseTheme(this);
        var root = new StackPanel { Margin = Ui.PageMargin };

        _tabs = new TabStrip()
            .Add("mouse", "Rato", "", "Polling, botões, roda e duplo clique")
            .Add("keys", "Teclado", "", "Todas as teclas e teclas ao mesmo tempo")
            .Add("screen", "Ecrã", "", "Refresh real, píxeis mortos e rasto")
            .Add("gamepad", "Comando", "", "Botões, analógicos, gatilhos, drift e vibração")
            .Add("audio", "Fones", "", "Esquerda, direita, ambos, equilíbrio e frequências")
            .Add("camera", "Câmara", "", "Webcam ao vivo, resolução, FPS e iluminação");
        _tabs.Margin = new Thickness(0, 0, 0, 14);
        _tabs.SelectionChanged += Show;
        root.Children.Add(_tabs);
        root.Children.Add(_host);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root };

        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            ComponentDispatcher.ThreadPreprocessMessage += OnPreprocess;
            _window = Window.GetWindow(this);
            if (_window != null)
            {
                _window.Deactivated += OnDeactivated;
                _window.Closing += OnClosing;
            }
            _active?.Activate();
        };
        Unloaded += (_, _) =>
        {
            if (!_loaded) return;
            _loaded = false;
            ComponentDispatcher.ThreadPreprocessMessage -= OnPreprocess;
            if (_window != null)
            {
                _window.Deactivated -= OnDeactivated;
                _window.Closing -= OnClosing;
            }
            _window = null;
            _active?.Deactivate();
        };

        _tabs.Select(_lastTab);
        Show(_lastTab);
    }

    /// <summary>Abre uma aba interna por código ("mouse", "keys", "screen", "gamepad", "audio" ou "camera").</summary>
    public void ShowTab(string key) { _tabs.Select(key); Show(key); }

    private void Show(string key)
    {
        if (!_views.TryGetValue(key, out var v))
        {
            v = key switch
            {
                "keys" => new PeriphKeyboardView(),
                "screen" => new PeriphScreenView(),
                "gamepad" => new PeriphGamepadView(),
                "audio" => new PeriphAudioView(),
                "camera" => new PeriphCameraView(),
                _ => new PeriphMouseView(),
            };
            _views[key] = v;
        }
        if (ReferenceEquals(v, _active)) return;
        _active?.Deactivate();
        _active = v;
        _lastTab = key;
        var el = (FrameworkElement)v;
        _host.Content = el;
        // entrada suave (igual à troca de páginas)
        el.Opacity = 0;
        var tt = new TranslateTransform(0, 10);
        el.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        if (_loaded) v.Activate();
    }

    private void OnDeactivated(object? sender, EventArgs e) => _active?.WindowDeactivated();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) => _active?.Deactivate();

    private void OnPreprocess(ref MSG msg, ref bool handled)
    {
        if (handled || _active == null || !IsVisible) return;
        var w = Window.GetWindow(this);
        if (w == null || !w.IsActive) return;
        var main = new WindowInteropHelper(w).Handle;
        if (main == IntPtr.Zero || (msg.hwnd != main && !Peripherals.PeriphNative.IsChild(main, msg.hwnd))) return;
        _active.Preprocess(ref msg, ref handled);
    }
}

/// <summary>Uma aba da página de periféricos.</summary>
internal interface IPeriphView
{
    /// <summary>A aba ficou visível (a página está carregada).</summary>
    void Activate();
    /// <summary>A aba deixou de estar visível: parar medições.</summary>
    void Deactivate();
    /// <summary>A janela principal perdeu o foco (as teclas soltas já não chegam).</summary>
    void WindowDeactivated() { }
    /// <summary>Mensagens do Windows para a janela principal, antes do WPF (teclado e botões laterais do rato).</summary>
    void Preprocess(ref MSG msg, ref bool handled) { }
}

/// <summary>Peças de interface partilhadas pelas abas de periféricos.</summary>
internal static class PeriphUi
{
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static TextBlock Icon(string glyph, double size = 16, string color = "Accent2")
    {
        var t = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, color);
        return t;
    }

    /// <summary>Quadrado com ícone (cabeçalho de cartão).</summary>
    public static Border IconBox(string glyph, double box = 40, string color = "Accent2")
    {
        var b = new Border { Width = box, Height = box, CornerRadius = new CornerRadius(box * 0.28), Child = Icon(glyph, box * 0.48, color), VerticalAlignment = VerticalAlignment.Top };
        b.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        return b;
    }

    /// <summary>Cabeçalho de cartão: ícone + título + linha Muted + (opcional) ações à direita.</summary>
    public static DockPanel CardHead(string glyph, string title, string sub, UIElement? right = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 16), LastChildFill = true };
        if (right != null)
        {
            DockPanel.SetDock(right, Dock.Right);
            if (right is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(16, 0, 0, 0); }
            d.Children.Add(right);
        }
        var ib = IconBox(glyph);
        ib.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(ib, Dock.Left);
        d.Children.Add(ib);
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var h = Ui.T(title, "H2", 16.5);
        h.Margin = new Thickness(0, 0, 0, 2);
        t.Children.Add(h);
        if (sub.Length > 0) t.Children.Add(Ui.T(sub, "MutedText"));
        d.Children.Add(t);
        return d;
    }

    /// <summary>Número com rótulo pequeno por cima (estatística compacta).</summary>
    public static (Border Box, TextBlock Value, TextBlock Hint) Stat(string label, string value = "—", string hint = "")
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.T(label.ToUpperInvariant(), "Label", 10));
        var v = new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold, FontFamily = Mono, Margin = new Thickness(0, 4, 0, 0) };
        v.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        sp.Children.Add(v);
        var h = Ui.T(hint, "MutedText", 11.5);
        sp.Children.Add(h);
        var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 0, 10, 10), Child = sp, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "Hover");
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return (b, v, h);
    }

    /// <summary>Caixa de veredito: ícone colorido + texto. Muda com <see cref="SetVerdict"/>.</summary>
    public static (Border Box, TextBlock Glyph, TextBlock Text) Verdict(string text)
    {
        var g = new TextBlock { Text = "", FontFamily = Icons, FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
        g.SetResourceReference(TextBlock.ForegroundProperty, "Accent2");
        var t = Ui.T(text, "Body");
        var d = new DockPanel();
        DockPanel.SetDock(g, Dock.Left);
        d.Children.Add(g);
        d.Children.Add(t);
        var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12), Child = d, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "Hover");
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return (b, g, t);
    }

    /// <summary>kind: "Good", "Warn", "Bad" ou "Info".</summary>
    public static void SetVerdict((Border Box, TextBlock Glyph, TextBlock Text) v, string kind, string text)
    {
        v.Text.Text = text;
        v.Glyph.Text = kind switch { "Good" => "", "Warn" => "", "Bad" => "", _ => "" };
        string color = kind is "Good" or "Warn" or "Bad" ? kind : "Accent2";
        v.Glyph.SetResourceReference(TextBlock.ForegroundProperty, color);
        if (kind is "Good" or "Warn" or "Bad")
        {
            v.Box.Background = new SolidColorBrush(WithAlpha(kind, 0x16));
            v.Box.BorderBrush = new SolidColorBrush(WithAlpha(kind, 0x50));
        }
        else
        {
            v.Box.SetResourceReference(Border.BackgroundProperty, "Hover");
            v.Box.SetResourceReference(Border.BorderBrushProperty, "Line");
        }
    }

    /// <summary>Botão com ícone + texto.</summary>
    public static Button Btn(string glyph, string text, RoutedEventHandler click, string style = "Btn")
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = sp };
        b.SetResourceReference(FrameworkElement.StyleProperty, style);
        b.Click += click;
        return b;
    }

    public static void SetBtn(Button b, string glyph, string text)
    {
        if (b.Content is StackPanel sp && sp.Children.Count == 2)
        {
            ((TextBlock)sp.Children[0]).Text = glyph;
            ((TextBlock)sp.Children[1]).Text = text;
        }
    }

    public static Border Card(UIElement child)
    {
        var c = Ui.Card(child);
        c.Margin = new Thickness(0, 0, 0, 14);
        return c;
    }

    public static Color WithAlpha(string key, byte a)
    {
        var c = Ui.Res(key) is SolidColorBrush s ? s.Color : Colors.DodgerBlue;
        return Color.FromArgb(a, c.R, c.G, c.B);
    }

    /// <summary>Pílula pequena colorida (estado).</summary>
    public static Border StatePill(string text, string kind)
    {
        var t = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold };
        t.SetResourceReference(TextBlock.ForegroundProperty, kind);
        return new Border
        {
            CornerRadius = new CornerRadius(99), Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(WithAlpha(kind, 0x22)), BorderBrush = new SolidColorBrush(WithAlpha(kind, 0x60)), BorderThickness = new Thickness(1),
            Child = t
        };
    }
}
