using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using static FrameForge.Peripherals.PeriphNative;

namespace FrameForge.Peripherals;

/// <summary>Estado partilhado para pausar leituras pesadas de sensores/NVML durante testes visuais de fluidez.</summary>
public static class ScreenTestState
{
    private static int _motionCount;
    public static bool IsMotionTestOpen => Volatile.Read(ref _motionCount) > 0;
    internal static void EnterMotionTest() => Interlocked.Increment(ref _motionCount);
    internal static void ExitMotionTest()
    {
        if (Interlocked.Decrement(ref _motionCount) < 0)
            Interlocked.Exchange(ref _motionCount, 0);
    }
}

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
    private double _dpiScale = 1.0;
    private TimeSpan _last = TimeSpan.Zero;
    private long _lastTick;
    private int _frames;
    private double _fps;
    private long _fpsStart = System.Diagnostics.Stopwatch.GetTimestamp();
    private bool _enteredMotionState;

    public MotionTestWindow(MonitorInfo m) : base(m, "HyperCore · Rasto e movimento")
    {
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
        var shades = new (string Name, Color Bg)[] { ("Fundo preto", Color.FromRgb(0, 0, 0)), ("Cinzento escuro", Color.FromRgb(0x3C, 0x3C, 0x3C)), ("Cinzento claro", Color.FromRgb(0xA0, 0xA0, 0xA0)) };
        for (int i = 0; i < shades.Length; i++)
        {
            _lanes.RowDefinitions.Add(new RowDefinition());
            var bgBrush = new SolidColorBrush(shades[i].Bg); bgBrush.Freeze();
            var lane = new Grid { Background = bgBrush, ClipToBounds = true };
            Grid.SetRow(lane, i);
            var lblBrush = new SolidColorBrush(i == 2 ? Color.FromRgb(0x30, 0x30, 0x30) : Color.FromRgb(0x9A, 0xA4, 0xB8)); lblBrush.Freeze();
            var label = new TextBlock
            {
                Text = shades[i].Name.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(18, 0, 0, 12),
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left,
                Foreground = lblBrush
            };
            lane.Children.Add(label);
            var obj = MakeObject(i == 2);
            obj.CacheMode = new BitmapCache();
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
            _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _lastTick = System.Diagnostics.Stopwatch.GetTimestamp();
            _fpsStart = _lastTick;
            if (!_enteredMotionState) { ScreenTestState.EnterMotionTest(); _enteredMotionState = true; }
            CompositionTarget.Rendering += OnFrame;
            Hint("Segue o objeto com os olhos.\nUm rasto atrás dele é ghosting; um contorno claro à frente é overdrive a mais.\n↑/↓ ou clique muda a velocidade  ·  Espaço pausa  ·  Esc sai");
        };
        DpiChanged += (_, e) => _dpiScale = e.NewDpi.DpiScaleX;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            if (_enteredMotionState) { ScreenTestState.ExitMotionTest(); _enteredMotionState = false; }
        };
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
        _last = t;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = _lastTick == 0 ? 0 : (now - _lastTick) / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastTick = now;
        if (dt > 0.25) dt = 0; // a janela esteve parada

        _frames++;
        double el = (now - _fpsStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (el >= 0.5) { _fps = _frames / el; _frames = 0; _fpsStart = now; UpdateInfo(); }

        double scale = _dpiScale > 0 ? _dpiScale : 1.0;
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

/// <summary>
/// Caixa flutuante arrastável estilo JScreenFix: altera aleatoriamente as cores de cada píxel (ruído RGB puro
/// ou flash RGB) à taxa de atualização do monitor (milhões de transições de píxeis por segundo) para tentar
/// recuperar píxeis presos. Fica por cima das janelas até o utilizador a fechar.
/// </summary>
internal sealed class PixelFixerWindow : Window
{
    private static readonly int[] Sizes = { 120, 200, 320 };
    private static readonly string[] SizeLabels = { "P", "M", "G" };
    // Paleta de alta energia (cores primárias/secundárias puras em BGR32) para estimular os 3 subpíxeis R/G/B
    private static readonly uint[] VividPalette =
    {
        0xFF0000FF, // Vermelho puro
        0xFF00FF00, // Verde puro
        0xFFFF0000, // Azul puro
        0xFF00FFFF, // Amarelo
        0xFFFF00FF, // Magenta
        0xFFFFFF00, // Ciano
        0xFFFFFFFF, // Branco
        0xFF000000  // Preto
    };

    private readonly MonitorInfo _monitor;
    private readonly System.Windows.Controls.Image _img = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _timerText = new() { Text = "00:00", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _sizeBtn;
    private readonly Button _modeBtn;
    private readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Diagnostics.Stopwatch _sw = new();

    private System.Windows.Media.Imaging.WriteableBitmap? _bmp;
    private int _sizeIdx = 1; // 200×200 por omissão
    private bool _flashMode;  // false = ruído por píxel (estilo JScreenFix), true = flash sólido R/G/B/W/K
    private int _flashStep;
    private uint _rng = 0xA3C59AC3u;
    private TimeSpan _lastFrame = TimeSpan.Zero;

    public PixelFixerWindow(MonitorInfo monitor)
    {
        _monitor = monitor;
        Title = "HyperCore · Recuperar píxeis";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x1A));
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var root = new DockPanel { LastChildFill = true };

        // Barra superior compacta (arrastável) com temporizador, tamanho, modo e fechar
        var bar = new DockPanel
        {
            Height = 30,
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            Cursor = Cursors.SizeAll
        };
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };

        var closeBtn = MiniBtn("✕", "Fechar (Esc)", (_, _) => Close());
        closeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5));
        DockPanel.SetDock(closeBtn, Dock.Right);
        bar.Children.Add(closeBtn);

        _modeBtn = MiniBtn("Ruído", "Alternar entre ruído por píxel (JScreenFix) e flash sólido RGB", (_, _) =>
        {
            _flashMode = !_flashMode;
            _modeBtn.Content = _flashMode ? "Flash" : "Ruído";
        });
        DockPanel.SetDock(_modeBtn, Dock.Right);
        bar.Children.Add(_modeBtn);

        _sizeBtn = MiniBtn("M", "Mudar tamanho da caixa (P: 120px · M: 200px · G: 320px)", (_, _) =>
        {
            _sizeIdx = (_sizeIdx + 1) % Sizes.Length;
            ApplyBoxSize();
        });
        DockPanel.SetDock(_sizeBtn, Dock.Right);
        bar.Children.Add(_sizeBtn);

        var leftInfo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var grip = new TextBlock { Text = "⋮⋮", Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)), FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        leftInfo.Children.Add(grip);
        leftInfo.Children.Add(_timerText);
        bar.Children.Add(leftInfo);

        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        // Área de píxeis (também arrastável ao clicar e segurar)
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.NearestNeighbor);
        var stage = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x9B, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeAll,
            ToolTip = "Arrasta para cima do píxel preso · Esc fecha",
            Child = _img
        };
        stage.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        root.Children.Add(stage);

        Content = root;

        _clock.Tick += (_, _) =>
        {
            var e = _sw.Elapsed;
            _timerText.Text = e.TotalHours >= 1 ? e.ToString(@"hh\:mm\:ss") : e.ToString(@"mm\:ss");
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };

        Loaded += (_, _) =>
        {
            ApplyBoxSize(centerOnMonitor: true);
            _sw.Start();
            _clock.Start();
            ScreenTestState.EnterMotionTest();
            CompositionTarget.Rendering += OnRender;
            Activate();
        };

        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRender;
            ScreenTestState.ExitMotionTest();
            _clock.Stop();
            _sw.Stop();
        };
    }

    private static Button MiniBtn(string text, string tip, RoutedEventHandler click)
    {
        var b = new Button
        {
            Content = text,
            ToolTip = tip,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 2, 7, 2),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        b.Click += click;
        return b;
    }

    private void ApplyBoxSize(bool centerOnMonitor = false)
    {
        int px = Sizes[_sizeIdx];
        _sizeBtn.Content = SizeLabels[_sizeIdx];
        Width = px + 2;
        Height = px + 32;
        _bmp = new System.Windows.Media.Imaging.WriteableBitmap(px, px, 96, 96, PixelFormats.Bgr32, null);
        _img.Source = _bmp;

        if (centerOnMonitor)
        {
            double dpiX = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double dpiY = VisualTreeHelper.GetDpi(this).DpiScaleY;
            double monLeft = _monitor.X / dpiX;
            double monTop = _monitor.Y / dpiY;
            double monW = _monitor.Width / dpiX;
            double monH = _monitor.Height / dpiY;
            Left = monLeft + (monW - Width) / 2;
            Top = monTop + (monH - Height) / 2;
        }
    }

    private unsafe void OnRender(object? sender, EventArgs e)
    {
        if (_bmp == null) return;
        var t = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        if (t == _lastFrame) return;
        _lastFrame = t;

        int w = _bmp.PixelWidth;
        int h = _bmp.PixelHeight;
        _bmp.Lock();
        try
        {
            uint* ptr = (uint*)_bmp.BackBuffer;
            int count = w * h;
            if (_flashMode)
            {
                uint color = VividPalette[_flashStep++ % VividPalette.Length];
                for (int i = 0; i < count; i++) ptr[i] = color;
            }
            else
            {
                // Xorshift32 inline: gera milhões de trocas de cor de píxeis por segundo com quase zero CPU
                uint s = _rng;
                fixed (uint* pal = VividPalette)
                {
                    for (int i = 0; i < count; i++)
                    {
                        s ^= s << 13;
                        s ^= s >> 17;
                        s ^= s << 5;
                        ptr[i] = pal[s & 7];
                    }
                }
                _rng = s;
            }
            _bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            _bmp.Unlock();
        }
    }
}
