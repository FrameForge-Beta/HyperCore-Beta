using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using static FrameForge.Peripherals.PeriphNative;

namespace FrameForge.Peripherals;

/// <summary>Janela de ecrã inteiro num monitor escolhido, sem bordas e por cima de tudo. Esc fecha.</summary>
internal abstract class FullscreenTestWindow : Window
{
    private readonly MonitorInfo _monitor;
    protected readonly Grid Root = new();
    private readonly Border _hint;
    private readonly TextBlock _hintText = new() { FontSize = 15, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly System.Windows.Threading.DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromSeconds(3.2) };

    protected FullscreenTestWindow(MonitorInfo monitor, string title)
    {
        _monitor = monitor;
        Title = title;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black;
        Cursor = Cursors.None;
        UseLayoutRounding = true;
        Left = 0; Top = 0; Width = 400; Height = 300;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x0E, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x2E, 0x9B, 0xFF)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(22, 12, 22, 12), MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 56),
            Child = _hintText, IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6 }
        };
        Panel.SetZIndex(_hint, 100);
        Root.Children.Add(_hint);
        Content = Root;

        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); _hint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))); };
        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) => { Place(); Activate(); Focus(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            OnKey(e);
        };
        Closed += (_, _) => _hintTimer.Stop();
        // se o utilizador sair com Alt+Tab, a janela fecha-se em vez de ficar por cima de tudo
        bool shown = false;
        ContentRendered += (_, _) => shown = true;
        bool closed = false;
        Closed += (_, _) => closed = true;
        Deactivated += (_, _) =>
        {
            if (shown && !closed) Dispatcher.BeginInvoke(() => { if (!closed) try { Close(); } catch (InvalidOperationException) { } });
        };
    }

    private void Place()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero)
            SetWindowPos(h, HWND_TOPMOST, _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, SWP_SHOWWINDOW | SWP_FRAMECHANGED);
    }

    /// <summary>Mostra uma dica em baixo que desaparece sozinha.</summary>
    protected void Hint(string text)
    {
        _hintText.Text = text;
        _hint.BeginAnimation(OpacityProperty, null);
        _hint.Opacity = 1;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    protected virtual void OnKey(KeyEventArgs e) { }
}

/// <summary>Píxeis mortos e presos: o ecrã inteiro numa cor de cada vez.</summary>
internal sealed class DeadPixelWindow : FullscreenTestWindow
{
    private static readonly (string Name, Brush Brush, string Tip)[] Palette =
    {
        ("Preto", Brushes.Black, "Procura pontos claros (píxel preso) e luz a fugir nos cantos."),
        ("Branco", Brushes.White, "Procura pontos pretos: são píxeis mortos."),
        ("Vermelho", new SolidColorBrush(Color.FromRgb(255, 0, 0)), "Um ponto de outra cor é um subpíxel preso."),
        ("Verde", new SolidColorBrush(Color.FromRgb(0, 255, 0)), "Um ponto de outra cor é um subpíxel preso."),
        ("Azul", new SolidColorBrush(Color.FromRgb(0, 0, 255)), "Um ponto de outra cor é um subpíxel preso."),
        ("Cinzento", new SolidColorBrush(Color.FromRgb(128, 128, 128)), "Manchas mais claras ou escuras = cor pouco uniforme."),
        ("Gradiente", new LinearGradientBrush(System.Windows.Media.Colors.Black, System.Windows.Media.Colors.White, 0), "Deve ser suave. Faixas bem marcadas = poucas cores (banding)."),
    };

    private readonly Border _fill = new();
    private int _i;

    public DeadPixelWindow(MonitorInfo m) : base(m, "HyperCore · Píxeis mortos")
    {
        Root.Children.Insert(0, _fill);
        MouseLeftButtonDown += (_, _) => Go(1);
        MouseRightButtonDown += (_, _) => Go(-1);
        MouseWheel += (_, e) => Go(e.Delta < 0 ? 1 : -1);
        Loaded += (_, _) => Go(0);
    }

    private void Go(int delta)
    {
        _i = (_i + delta + Palette.Length) % Palette.Length;
        var c = Palette[_i];
        _fill.Background = c.Brush;
        Hint($"{_i + 1}/{Palette.Length}  ·  {c.Name}\n{c.Tip}\nClica ou → para a próxima cor  ·  ← volta  ·  Esc sai");
    }

    protected override void OnKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right: case Key.Space: case Key.Down: case Key.Enter: Go(1); e.Handled = true; break;
            case Key.Left: case Key.Up: case Key.Back: Go(-1); e.Handled = true; break;
        }
    }
}

/// <summary>
/// Teste de rasto: objetos a andar a velocidade constante sobre três fundos (preto, cinzento escuro e cinzento claro).
/// Um "fantasma" atrás do objeto é ghosting; um contorno claro à frente é overshoot do overdrive.
/// </summary>
internal sealed class MotionTestWindow : FullscreenTestWindow
{
    private static readonly int[] Speeds = { 240, 480, 720, 960, 1440, 1920, 2880, 3840 };
    private readonly List<FrameworkElement> _objects = new();
    private readonly List<TranslateTransform> _moves = new();
    private readonly Grid _lanes = new();
    private readonly TextBlock _info = new() { FontSize = 13.5, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xE4)), Margin = new Thickness(18, 12, 18, 0) };
    private int _speed = 3;
    private bool _paused;
    private double _pos;            // em píxeis físicos
    private TimeSpan _last = TimeSpan.Zero;
    private int _frames;
    private double _fps;
    private long _fpsStart = System.Diagnostics.Stopwatch.GetTimestamp();

    public MotionTestWindow(MonitorInfo m) : base(m, "HyperCore · Rasto e movimento")
    {
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
        var shades = new (string Name, Color Bg)[] { ("Fundo preto", Color.FromRgb(0, 0, 0)), ("Cinzento escuro", Color.FromRgb(0x3C, 0x3C, 0x3C)), ("Cinzento claro", Color.FromRgb(0xA0, 0xA0, 0xA0)) };
        for (int i = 0; i < shades.Length; i++)
        {
            _lanes.RowDefinitions.Add(new RowDefinition());
            var lane = new Grid { Background = new SolidColorBrush(shades[i].Bg), ClipToBounds = true };
            Grid.SetRow(lane, i);
            var label = new TextBlock
            {
                Text = shades[i].Name.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(18, 0, 0, 12),
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left,
                Foreground = new SolidColorBrush(i == 2 ? Color.FromRgb(0x30, 0x30, 0x30) : Color.FromRgb(0x9A, 0xA4, 0xB8))
            };
            lane.Children.Add(label);
            var obj = MakeObject(i == 2);
            var tt = new TranslateTransform();
            obj.RenderTransform = tt;
            lane.Children.Add(obj);
            _objects.Add(obj);
            _moves.Add(tt);
            _lanes.Children.Add(lane);
        }
        Root.Children.Insert(0, _lanes);
        var bar = new Border { Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(0, 0, 12, 0), Padding = new Thickness(0, 0, 0, 12), Child = _info };
        Panel.SetZIndex(bar, 50);
        Root.Children.Add(bar);

        MouseLeftButtonDown += (_, _) => Change(1);
        MouseRightButtonDown += (_, _) => Change(-1);
        MouseWheel += (_, e) => Change(e.Delta > 0 ? 1 : -1);
        Loaded += (_, _) =>
        {
            CompositionTarget.Rendering += OnFrame;
            Hint("Segue o objeto com os olhos.\nUm rasto atrás dele é ghosting; um contorno claro à frente é overdrive a mais.\n↑/↓ ou clique muda a velocidade  ·  Espaço pausa  ·  Esc sai");
        };
        Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    private static FrameworkElement MakeObject(bool onLight)
    {
        var body = new Grid { Width = 168, Height = 96 };
        body.Children.Add(new Border { CornerRadius = new CornerRadius(20), Background = onLight ? new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x18)) : Brushes.White });
        var stack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var eye = new Grid { Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0) };
        eye.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x2E, 0x9B, 0xFF)) });
        eye.Children.Add(new System.Windows.Shapes.Ellipse { Width = 14, Height = 14, Fill = onLight ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x18)) });
        stack.Children.Add(eye);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var fg = onLight ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x18));
        words.Children.Add(new TextBlock { Text = "HYPER", FontSize = 17, FontWeight = FontWeights.Black, Foreground = fg });
        words.Children.Add(new TextBlock { Text = "CORE", FontSize = 17, FontWeight = FontWeights.Black, Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xB8, 0xE6)), Margin = new Thickness(0, -5, 0, 0) });
        stack.Children.Add(words);
        body.Children.Add(stack);
        body.HorizontalAlignment = HorizontalAlignment.Left;
        body.VerticalAlignment = VerticalAlignment.Center;
        return body;
    }

    private void Change(int d)
    {
        _speed = Math.Clamp(_speed + d, 0, Speeds.Length - 1);
        UpdateInfo();
    }

    protected override void OnKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up: case Key.Right: case Key.Add: case Key.OemPlus: Change(1); e.Handled = true; break;
            case Key.Down: case Key.Left: case Key.Subtract: case Key.OemMinus: Change(-1); e.Handled = true; break;
            case Key.Space: _paused = !_paused; UpdateInfo(); e.Handled = true; break;
        }
    }

    private void UpdateInfo()
    {
        int sp = Speeds[_speed];
        string perFrame = _fps > 1 ? $"{sp / _fps:0.#} px por imagem" : "";
        _info.Text = $"{sp} px/s   ·   {perFrame}   ·   {_fps:0} imagens/s{(_paused ? "   ·   EM PAUSA" : "")}        ↑/↓ velocidade   ·   Espaço pausa   ·   Esc sai";
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        if (t == _last) return; // o mesmo frame pode chegar duas vezes
        double dt = _last == TimeSpan.Zero ? 0 : (t - _last).TotalSeconds;
        _last = t;
        if (dt > 0.25) dt = 0; // a janela esteve parada
        _frames++;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double el = (now - _fpsStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (el >= 0.5) { _fps = _frames / el; _frames = 0; _fpsStart = now; UpdateInfo(); }

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (!_paused) _pos += Speeds[_speed] * dt;
        double laneW = ActualWidth * scale;
        double objW = 168 * scale;
        double span = laneW + objW;
        if (span <= 0) return;
        _pos %= span;
        double x = (_pos - objW) / scale;
        x = Math.Round(x * scale) / scale; // anda em píxeis inteiros (sem desfocar de propósito)
        foreach (var m in _moves) m.X = x;
    }
}
