using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FrameForge.Pages;

/// <summary>
/// Aba "Teclado": teclado visual (PT, ISO, completo) que acende cada tecla premida, conta as teclas ao mesmo tempo
/// (ghosting / rollover) e verifica combinações típicas de jogos. As teclas são lidas pelo código físico (scan code),
/// antes do WPF, por isso funcionam mesmo as que normalmente fazem outra coisa (Alt, Tab, F10…). Nada sai da app.
/// </summary>
internal sealed class PeriphKeyboardView : StackPanel, IPeriphView
{
    private const double U = 44;     // tamanho de uma tecla
    private const double Gap = 5;

    private sealed class KeyCap
    {
        public int Id;
        public string Label = "";
        public Shape Shape = null!;
        public TextBlock Text = null!;
        public Grid Root = null!;
        public bool Tested, Down;
    }

    private readonly Dictionary<int, KeyCap> _keys = new();
    private readonly HashSet<int> _held = new();
    private readonly Canvas _board = new();
    private readonly (Border Box, TextBlock Value, TextBlock Hint) _sTested, _sNow, _sMax;
    private readonly ProgressBar _progress = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _last = new() { FontSize = 13 };
    private readonly (Border Box, TextBlock Glyph, TextBlock Text) _verdict;
    private readonly Border _capture;
    private readonly TextBlock _captureText = new() { FontSize = 11.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private int _maxHeld;
    private bool _active;
    // AltGr chega como Ctrl esquerdo falso + Alt direito com a mesma hora: guardamos o Ctrl para o desfazer
    private int _lctrlTime = -1;
    private bool _lctrlWasNew;

    private sealed class Combo
    {
        public string Name = "";
        public int[] Ids = Array.Empty<int>();
        public bool Done;
        public Border Chip = null!;
        public TextBlock Glyph = null!;
    }
    private readonly List<Combo> _combos = new();

    public PeriphKeyboardView()
    {
        // ------------------------------------------------ cartão principal
        var clear = PeriphUi.Btn("", "Limpar", (_, _) => ResetAll());
        var card = new StackPanel();
        _capture = new Border { CornerRadius = new CornerRadius(99), Padding = new Thickness(10, 4, 12, 5), Margin = new Thickness(0, 0, 10, 0), BorderThickness = new Thickness(1) };
        var capRow = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, "Good");
        capRow.Children.Add(dot);
        capRow.Children.Add(_captureText);
        _capture.Child = capRow;
        var actions = Ui.Row(_capture, clear);
        card.Children.Add(PeriphUi.CardHead("", "Teclas", "Carrega em cada tecla: acende enquanto a seguras e fica azul depois de testada.", actions));

        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -10, 6) };
        _sTested = PeriphUi.Stat("Testadas", "0", "");
        _sNow = PeriphUi.Stat("Premidas agora", "0", "teclas em baixo");
        _sMax = PeriphUi.Stat("Máximo ao mesmo tempo", "0", "rollover medido");
        _progress.SetResourceReference(Control.ForegroundProperty, "Accent");
        _progress.SetResourceReference(Control.BackgroundProperty, "Bg");
        _progress.BorderThickness = new Thickness(0);
        ((StackPanel)_sTested.Box.Child).Children.Add(_progress);
        stats.Children.Add(_sTested.Box);
        stats.Children.Add(_sNow.Box);
        stats.Children.Add(_sMax.Box);
        card.Children.Add(stats);

        BuildBoard();
        var boardBox = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
        boardBox.SetResourceReference(Border.BackgroundProperty, "Bg");
        boardBox.SetResourceReference(Border.BorderBrushProperty, "Line");
        boardBox.Child = new Viewbox { Child = _board, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Center };
        card.Children.Add(boardBox);

        _last.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _last.Text = "Última tecla: —";
        card.Children.Add(_last);
        Children.Add(PeriphUi.Card(card));

        // ------------------------------------------------ combinações / ghosting
        var gh = new StackPanel();
        gh.Children.Add(PeriphUi.CardHead("", "Teclas ao mesmo tempo (ghosting)",
            "Segura cada combinação de uma vez. Se uma tecla não acender com as outras em baixo, o teclado não a apanha nessa combinação."));
        var wrap = new WrapPanel();
        AddCombo(wrap, "W A S D", 0x11, 0x1E, 0x1F, 0x20);
        AddCombo(wrap, "W + A + Shift + Espaço", 0x11, 0x1E, 0x2A, 0x39);
        AddCombo(wrap, "S + D + Ctrl + Espaço", 0x1F, 0x20, 0x1D, 0x39);
        AddCombo(wrap, "Q W E R", 0x10, 0x11, 0x12, 0x13);
        AddCombo(wrap, "Shift + 1 2 3 4", 0x2A, 0x02, 0x03, 0x04, 0x05);
        gh.Children.Add(wrap);
        _verdict = PeriphUi.Verdict("");
        _verdict.Box.Margin = new Thickness(0, 6, 0, 0);
        gh.Children.Add(_verdict.Box);
        Children.Add(PeriphUi.Card(gh));

        SetCapture(false);
        UpdateStats();
    }

    // =========================================================== IPeriphView

    public void Activate() { _active = true; SetCapture(true); }

    public void Deactivate() { _active = false; ReleaseAll(); SetCapture(false); }

    public void WindowDeactivated() => ReleaseAll();

    public void Preprocess(ref MSG msg, ref bool handled)
    {
        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        if (!_active) return;
        int m = msg.message;
        if (m is not (WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)) return;
        // a escrever num campo (ex.: pesquisa do menu): as teclas são para o campo, não para o teste
        if (System.Windows.Input.Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
        long lp = msg.lParam.ToInt64();
        int scan = (int)((lp >> 16) & 0xFF);
        bool ext = ((lp >> 24) & 1) != 0;
        int vk = (int)(msg.wParam.ToInt64() & 0xFF);
        int id = scan | (ext ? 0x100 : 0);
        id = vk switch { 0x13 => 0x245, 0x90 => 0x145, 0x2C => 0x137, _ => id };
        if (scan == 0 && vk != 0x13 && vk != 0x90 && vk != 0x2C) { handled = true; return; } // tecla sem código físico (macro/multimédia)
        bool down = m is WM_KEYDOWN or WM_SYSKEYDOWN;
        // a página "come" as teclas enquanto o teste está aberto (Alt, Tab, F10… não fazem nada), menos Alt+F4 (fechar a app)
        handled = !(m == WM_SYSKEYDOWN && vk == 0x73);

        if (id is 0x12A or 0x136) return; // Shift falso que o Windows junta às setas/NumLock
        if (down)
        {
            if (id == 0x01D) { _lctrlTime = msg.time; _lctrlWasNew = _keys.TryGetValue(id, out var lc) && !lc.Tested; }
            if (id == 0x138 && msg.time == _lctrlTime && _keys.TryGetValue(0x01D, out var fake))
            {
                // era o AltGr: o Ctrl esquerdo não foi carregado de verdade
                _held.Remove(0x01D);
                fake.Down = false;
                if (_lctrlWasNew) fake.Tested = false;
                Paint(fake);
            }
            KeyPressed(id);
        }
        else KeyReleased(id);
    }

    // =========================================================== lógica

    private void KeyPressed(int id)
    {
        bool isNew = _held.Add(id);
        if (_keys.TryGetValue(id, out var k))
        {
            k.Down = true; k.Tested = true;
            Paint(k);
            _last.Text = $"Última tecla: {Describe(k.Label)}   ·   código {(id >= 0x100 ? "E0 " : "")}{id & 0xFF:X2}";
        }
        else _last.Text = $"Última tecla: tecla extra   ·   código {(id >= 0x100 ? "E0 " : "")}{id & 0xFF:X2}";
        if (isNew)
        {
            if (_held.Count > _maxHeld) _maxHeld = _held.Count;
            foreach (var c in _combos)
                if (!c.Done && c.Ids.All(_held.Contains)) { c.Done = true; PaintCombo(c); }
        }
        UpdateStats();
    }

    private void KeyReleased(int id)
    {
        bool wasHeld = _held.Remove(id);
        if (_keys.TryGetValue(id, out var k))
        {
            k.Down = false;
            // a tecla Imp (Print Screen) só manda o "soltar": conta como testada na mesma
            if (!wasHeld && !k.Tested && id == 0x137)
            {
                k.Tested = true;
                _last.Text = $"Última tecla: {Describe(k.Label)}";
            }
            Paint(k);
        }
        if (id == 0x138 && _keys.TryGetValue(0x01D, out var lc) && lc.Down && !_held.Contains(0x01D)) { lc.Down = false; Paint(lc); }
        UpdateStats();
    }

    private static string Describe(string label) => label switch
    {
        "⌫" => "Retrocesso", "↵" => "Enter", "⊞" => "Windows", "☰" => "Menu", "↑" => "Seta para cima",
        "↓" => "Seta para baixo", "←" => "Seta para a esquerda", "→" => "Seta para a direita", _ => label.Replace("\n", " ")
    };

    private void ReleaseAll()
    {
        foreach (var id in _held.ToList()) if (_keys.TryGetValue(id, out var k)) { k.Down = false; Paint(k); }
        _held.Clear();
        UpdateStats();
    }

    private void ResetAll()
    {
        _held.Clear();
        _maxHeld = 0;
        foreach (var k in _keys.Values) { k.Down = false; k.Tested = false; Paint(k); }
        foreach (var c in _combos) { c.Done = false; PaintCombo(c); }
        _last.Text = "Última tecla: —";
        UpdateStats();
    }

    private void UpdateStats()
    {
        int tested = _keys.Values.Count(k => k.Tested);
        _sTested.Value.Text = $"{tested}";
        _sTested.Hint.Text = $"de {_keys.Count} teclas";
        _progress.Value = _keys.Count > 0 ? (double)tested / _keys.Count : 0;
        _sNow.Value.Text = _held.Count.ToString();
        _sMax.Value.Text = _maxHeld.ToString();
        _sMax.Value.SetResourceReference(TextBlock.ForegroundProperty, _maxHeld >= 6 ? "Good" : "Fg");

        int done = _combos.Count(c => c.Done);
        if (_maxHeld == 0)
            PeriphUi.SetVerdict(_verdict, "Info", "Segura várias teclas ao mesmo tempo. Teclados para jogos aguentam pelo menos 6 (muitos aguentam todas).");
        else if (done == _combos.Count && _maxHeld >= 6)
            PeriphUi.SetVerdict(_verdict, "Good", $"Todas as combinações funcionaram e o teclado aguentou {_maxHeld} teclas ao mesmo tempo. Sem ghosting.");
        else if (done == _combos.Count)
            PeriphUi.SetVerdict(_verdict, "Good", $"Todas as combinações funcionaram. Máximo medido: {_maxHeld} teclas de uma vez.");
        else if (_maxHeld >= 6)
            PeriphUi.SetVerdict(_verdict, "Good", $"O teclado aguentou {_maxHeld} teclas de uma vez. Experimenta as combinações que faltam.");
        else
            PeriphUi.SetVerdict(_verdict, "Info", $"Máximo até agora: {_maxHeld} teclas de uma vez. Faltam {_combos.Count - done} combinações. Se uma não acender toda, é ghosting nessa combinação.");
    }

    private void SetCapture(bool on)
    {
        _captureText.Text = on ? "A LER O TECLADO" : "EM PAUSA";
        _captureText.SetResourceReference(TextBlock.ForegroundProperty, on ? "Good" : "Faint");
        _capture.Background = new SolidColorBrush(PeriphUi.WithAlpha(on ? "Good" : "Faint", 0x1C));
        _capture.BorderBrush = new SolidColorBrush(PeriphUi.WithAlpha(on ? "Good" : "Faint", 0x55));
        _capture.ToolTip = "Enquanto esta aba está aberta, as teclas só servem para o teste (os atalhos da app ficam parados). Muda de aba para voltar ao normal.";
        if (((StackPanel)_capture.Child).Children[0] is Ellipse e) e.SetResourceReference(Shape.FillProperty, on ? "Good" : "Faint");
    }

    // =========================================================== desenho

    private void Paint(KeyCap k)
    {
        k.Root.Effect = null;
        if (k.Down)
        {
            k.Shape.SetResourceReference(Shape.FillProperty, "AccentGradient");
            k.Shape.SetResourceReference(Shape.StrokeProperty, "Accent2");
            k.Text.Foreground = Brushes.White;
            k.Root.Effect = new DropShadowEffect { Color = Color.FromRgb(0x2E, 0x9B, 0xFF), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.75 };
        }
        else if (k.Tested)
        {
            k.Shape.SetResourceReference(Shape.FillProperty, "AccentSoft");
            k.Shape.SetResourceReference(Shape.StrokeProperty, "Accent");
            k.Text.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        }
        else
        {
            k.Shape.SetResourceReference(Shape.FillProperty, "Card");
            k.Shape.SetResourceReference(Shape.StrokeProperty, "LineStrong");
            k.Text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        }
    }

    private void AddCombo(WrapPanel host, string name, params int[] ids)
    {
        var c = new Combo { Name = name, Ids = ids };
        c.Glyph = PeriphUi.Icon("", 13, "Faint");
        c.Glyph.Margin = new Thickness(0, 0, 9, 0);
        var t = Ui.T(name, "Body", 13);
        t.VerticalAlignment = VerticalAlignment.Center;
        c.Chip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 14, 8), Margin = new Thickness(0, 0, 8, 8), BorderThickness = new Thickness(1), Child = Ui.Row(c.Glyph, t) };
        _combos.Add(c);
        host.Children.Add(c.Chip);
        PaintCombo(c);
    }

    private static void PaintCombo(Combo c)
    {
        if (c.Done)
        {
            c.Glyph.Text = "";
            c.Glyph.SetResourceReference(TextBlock.ForegroundProperty, "Good");
            c.Chip.Background = new SolidColorBrush(PeriphUi.WithAlpha("Good", 0x16));
            c.Chip.BorderBrush = new SolidColorBrush(PeriphUi.WithAlpha("Good", 0x60));
        }
        else
        {
            c.Glyph.Text = "";
            c.Glyph.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            c.Chip.SetResourceReference(Border.BackgroundProperty, "Hover");
            c.Chip.SetResourceReference(Border.BorderBrushProperty, "Line");
        }
    }

    /// <summary>Teclado completo PT (ISO) em unidades de tecla. Ids = scan code | 0x100 se estendida.</summary>
    private void BuildBoard()
    {
        _board.Width = 22.5 * U;
        _board.Height = 6.25 * U;

        // fila das funções
        Key("Esc", 0x01, 0, 0);
        string[] f = { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12" };
        int[] fid = { 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58 };
        for (int i = 0; i < 12; i++) Key(f[i], fid[i], 2 + i + (i / 4) * 0.5 - (i >= 4 ? 0 : 0), 0);
        Key("Imp", 0x137, 15.25, 0);
        Key("Bloq\nDesl", 0x46, 16.25, 0, small: true);
        Key("Pausa", 0x245, 17.25, 0, small: true);

        const double r1 = 1.25, r2 = 2.25, r3 = 3.25, r4 = 4.25, r5 = 5.25;
        // números
        Key("\\", 0x29, 0, r1);
        for (int i = 0; i < 10; i++) Key(((i + 1) % 10).ToString(), 0x02 + i, 1 + i, r1);
        Key("'", 0x0C, 11, r1);
        Key("«", 0x0D, 12, r1);
        Key("⌫", 0x0E, 13, r1, 2);
        // QWERTY
        Key("Tab", 0x0F, 0, r2, 1.5);
        string q = "QWERTYUIOP";
        for (int i = 0; i < q.Length; i++) Key(q[i].ToString(), 0x10 + i, 1.5 + i, r2);
        Key("+", 0x1A, 11.5, r2);
        Key("´", 0x1B, 12.5, r2);
        EnterKey(13.5, r2);
        // ASDF
        Key("Caps", 0x3A, 0, r3, 1.75);
        string a = "ASDFGHJKL";
        for (int i = 0; i < a.Length; i++) Key(a[i].ToString(), 0x1E + i, 1.75 + i, r3);
        Key("Ç", 0x27, 10.75, r3);
        Key("º", 0x28, 11.75, r3);
        Key("~", 0x2B, 12.75, r3);
        // ZXCV
        Key("Shift", 0x2A, 0, r4, 1.25);
        Key("<", 0x56, 1.25, r4);
        string z = "ZXCVBNM";
        for (int i = 0; i < z.Length; i++) Key(z[i].ToString(), 0x2C + i, 2.25 + i, r4);
        Key(",", 0x33, 9.25, r4);
        Key(".", 0x34, 10.25, r4);
        Key("-", 0x35, 11.25, r4);
        Key("Shift", 0x36, 12.25, r4, 2.75);
        // fila de baixo
        Key("Ctrl", 0x1D, 0, r5, 1.25);
        Key("⊞", 0x15B, 1.25, r5, 1.25);
        Key("Alt", 0x38, 2.5, r5, 1.25);
        Key("Espaço", 0x39, 3.75, r5, 6.25);
        Key("AltGr", 0x138, 10, r5, 1.25);
        Key("⊞", 0x15C, 11.25, r5, 1.25);
        Key("☰", 0x15D, 12.5, r5, 1.25);
        Key("Ctrl", 0x11D, 13.75, r5, 1.25);
        // navegação
        Key("Ins", 0x152, 15.25, r1); Key("Home", 0x147, 16.25, r1, small: true); Key("PgUp", 0x149, 17.25, r1, small: true);
        Key("Del", 0x153, 15.25, r2); Key("End", 0x14F, 16.25, r2); Key("PgDn", 0x151, 17.25, r2, small: true);
        Key("↑", 0x148, 16.25, r4);
        Key("←", 0x14B, 15.25, r5); Key("↓", 0x150, 16.25, r5); Key("→", 0x14D, 17.25, r5);
        // teclado numérico
        const double n = 18.5;
        Key("Num", 0x145, n, r1, small: true); Key("/", 0x135, n + 1, r1); Key("*", 0x37, n + 2, r1); Key("-", 0x4A, n + 3, r1);
        Key("7", 0x47, n, r2); Key("8", 0x48, n + 1, r2); Key("9", 0x49, n + 2, r2); Key("+", 0x4E, n + 3, r2, 1, 2);
        Key("4", 0x4B, n, r3); Key("5", 0x4C, n + 1, r3); Key("6", 0x4D, n + 2, r3);
        Key("1", 0x4F, n, r4); Key("2", 0x50, n + 1, r4); Key("3", 0x51, n + 2, r4); Key("Enter", 0x11C, n + 3, r4, 1, 2);
        Key("0", 0x52, n, r5, 2); Key(",", 0x53, n + 2, r5);
    }

    private void Key(string label, int id, double x, double y, double w = 1, double h = 1, bool small = false)
    {
        var shape = new Rectangle { RadiusX = 7, RadiusY = 7, StrokeThickness = 1.2 };
        Place(label, id, x, y, w * U - Gap, h * U - Gap, shape, small);
    }

    /// <summary>Enter ISO em forma de L invertido (duas filas).</summary>
    private void EnterKey(double x, double y)
    {
        double w = 1.5 * U - Gap, h = 2 * U - Gap, notch = 0.25 * U, top = U - Gap;
        const double rr = 7;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(rr, 0), true, true);
            ctx.LineTo(new Point(w - rr, 0), true, false);
            ctx.ArcTo(new Point(w, rr), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(w, h - rr), true, false);
            ctx.ArcTo(new Point(w - rr, h), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(notch + rr, h), true, false);
            ctx.ArcTo(new Point(notch, h - rr), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(notch, top), true, false);
            ctx.LineTo(new Point(rr, top), true, false);
            ctx.ArcTo(new Point(0, top - rr), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(0, rr), true, false);
            ctx.ArcTo(new Point(rr, 0), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        var shape = new Path { Data = g, StrokeThickness = 1.2 };
        Place("Enter", 0x1C, x, y, w, h, shape, false);
    }

    private void Place(string label, int id, double x, double y, double w, double h, Shape shape, bool small)
    {
        var text = new TextBlock
        {
            Text = label, FontSize = small ? 10.5 : label.Length > 2 ? 12 : 15, FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 12, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        if (shape is Path) { text.Margin = new Thickness(0.25 * U, 0, 0, U * 0.9); }
        var root = new Grid { Width = w, Height = h };
        root.Children.Add(shape);
        root.Children.Add(text);
        Canvas.SetLeft(root, x * U);
        Canvas.SetTop(root, y * U);
        _board.Children.Add(root);
        var k = new KeyCap { Id = id, Label = label, Shape = shape, Text = text, Root = root };
        _keys[id] = k;
        Paint(k);
    }
}
