using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameForge.Peripherals;

namespace FrameForge.Pages;

/// <summary>
/// Aba "Comando": testa comandos de videojogo (XInput e DirectInput/PlayStation), mostrando botões em tempo real,
/// gatilhos analógicos, precisão/drift dos analógicos e teste de vibração. Só leitura (vibração pára sempre ao sair).
/// </summary>
internal sealed class PeriphGamepadView : StackPanel, IPeriphView
{
    private readonly DispatcherTimer _scanTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _vibTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };

    private List<GamepadDevice> _devices = new();
    private GamepadDevice? _current;
    private bool _active;
    private string _batterySummary = "";
    private long _lastBatteryQueryTicks;
    private long _lastTextUpdateTicks;
    private string _lastVerdictKind = "";
    private string _lastVerdictText = "";
    private int _lastTestedCount = -1;

    // Cabeçalho / seleção
    private readonly ComboBox _selector = new() { MinWidth = 220, Visibility = Visibility.Collapsed };
    private readonly Border _statusPill;
    private readonly TextBlock _statusText = new() { FontSize = 11.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _statusDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _infoLine = new() { FontSize = 12.5 };

    // Estatísticas
    private readonly (Border Box, TextBlock Value, TextBlock Hint) _sBtns, _sLeft, _sRight;
    private readonly ProgressBar _btnProgress = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 2, 0, 0) };

    // Diagrama visual do comando
    private sealed class PadPart
    {
        public string Key = "";
        public Shape Shape = null!;
        public TextBlock? Label;
        public bool Down, Tested;
    }
    private readonly Dictionary<string, PadPart> _parts = new();
    private readonly ScaleTransform _ltScale = new(0, 1);
    private readonly ScaleTransform _rtScale = new(0, 1);
    private readonly TranslateTransform _lStickTransform = new();
    private readonly TranslateTransform _rStickTransform = new();
    private readonly TextBlock _ltCanvasText, _rtCanvasText;
    private bool _ltTested, _rtTested;
    private int _lastLtPct = -1, _lastRtPct = -1;

    // Radares de drift e gatilhos
    private sealed class StickRadar
    {
        public Canvas Canvas = null!;
        public TranslateTransform DotTransform = null!;
        public Line Vec = null!;
        public Polyline Trail = null!;
        public PointCollection TrailPoints = null!;
        public Point LastTrailPoint = new(double.NaN, double.NaN);
        public TextBlock Coords = null!;
        public TextBlock Drift = null!;
        public TextBlock Reach = null!;
        public string LastDriftColor = "";
        public double MaxReach;
        public double RestOffsetSum;
        public int RestSamples;
        public bool Moved;
    }
    private readonly StickRadar _radarL, _radarR;
    private readonly ProgressBar _barLt, _barRt;
    private readonly TextBlock _valLt, _valRt;
    private readonly (Border Box, TextBlock Glyph, TextBlock Text) _verdict;

    // Vibração
    private readonly Slider _vibSlider;
    private readonly TextBlock _vibPct;
    private readonly Button _vibLeft, _vibRight, _vibBoth, _vibStop;
    private readonly TextBlock _vibNote;

    public PeriphGamepadView()
    {
        // ======================= Cartão 1: Comando ao vivo =======================
        _statusPill = new Border { CornerRadius = new CornerRadius(99), Padding = new Thickness(10, 4, 12, 5), Margin = new Thickness(0, 0, 10, 0), BorderThickness = new Thickness(1) };
        _statusPill.Child = Ui.Row(_statusDot, _statusText);

        _selector.Margin = new Thickness(0, 0, 10, 0);
        _selector.SelectionChanged += (_, _) => SelectFromCombo();

        var clearBtn = PeriphUi.Btn("", "Limpar", (_, _) => ResetTests());
        var actions = Ui.Row(_selector, _statusPill, clearBtn);

        var main = new StackPanel();
        main.Children.Add(PeriphUi.CardHead("", "Comando de videojogo",
            "Liga um comando por USB ou Bluetooth e carrega em cada botão, gatilho e analógico para confirmar que tudo responde bem.", actions));

        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -10, 8) };
        _sBtns = PeriphUi.Stat("Botões testados", "0", "de 16 controlos");
        _sLeft = PeriphUi.Stat("Analógico esq.", "0,00 · 0,00", "no centro");
        _sRight = PeriphUi.Stat("Analógico dir.", "0,00 · 0,00", "no centro");
        _btnProgress.SetResourceReference(Control.ForegroundProperty, "Accent");
        _btnProgress.SetResourceReference(Control.BackgroundProperty, "Bg");
        _btnProgress.BorderThickness = new Thickness(0);
        ((StackPanel)_sBtns.Box.Child).Children.Add(_btnProgress);
        stats.Children.Add(_sBtns.Box);
        stats.Children.Add(_sLeft.Box);
        stats.Children.Add(_sRight.Box);
        main.Children.Add(stats);

        var padCanvas = BuildControllerDiagram(out _ltCanvasText, out _rtCanvasText);
        var stage = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 16, 18, 16),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new Viewbox { Child = padCanvas, MaxHeight = 300, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center }
        };
        stage.SetResourceReference(Border.BackgroundProperty, "Bg");
        stage.SetResourceReference(Border.BorderBrushProperty, "Line");
        main.Children.Add(stage);

        _infoLine.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _infoLine.Text = "À procura de comandos ligados… carrega num botão se o comando estiver em suspensão.";
        main.Children.Add(_infoLine);
        Children.Add(PeriphUi.Card(main));

        // ======================= Cartão 2: Analógicos (drift) e gatilhos =======================
        var ana = new StackPanel();
        ana.Children.Add(PeriphUi.CardHead("", "Analógicos (drift e alcance) e gatilhos",
            "Larga os analógicos para verificar se ficam parados no centro (drift) e roda-os em círculo para confirmar o alcance total."));

        var anaGrid = new Grid();
        anaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        anaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        anaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });

        _radarL = BuildStickRadar();
        var leftPanel = WrapRadar(_radarL, "ANALÓGICO ESQUERDO");
        leftPanel.Margin = new Thickness(0, 0, 12, 0);
        anaGrid.Children.Add(leftPanel);

        _radarR = BuildStickRadar();
        var rightPanel = WrapRadar(_radarR, "ANALÓGICO DIREITO");
        rightPanel.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(rightPanel, 1);
        anaGrid.Children.Add(rightPanel);

        // Coluna de gatilhos analógicos
        var trigBox = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14), BorderThickness = new Thickness(1) };
        trigBox.SetResourceReference(Border.BackgroundProperty, "Hover");
        trigBox.SetResourceReference(Border.BorderBrushProperty, "Line");
        var trigSp = new StackPanel();
        trigSp.Children.Add(Ui.T("GATILHOS ANALÓGICOS", "Label", 10));
        var trigSub = Ui.T("Pressiona devagar até ao fundo para testar a progressão de 0% a 100%.", "MutedText", 12);
        trigSub.Margin = new Thickness(0, 4, 0, 14);
        trigSp.Children.Add(trigSub);

        (_barLt, _valLt) = AddTriggerRow(trigSp, "Gatilho esquerdo (LT / L2)");
        (_barRt, _valRt) = AddTriggerRow(trigSp, "Gatilho direito (RT / R2)");

        var legend = Ui.T("Zona tracejada = 10% de zona morta. Um analógico saudável em repouso fica dentro dela.", "MutedText", 11.5);
        legend.Margin = new Thickness(0, 14, 0, 0);
        trigSp.Children.Add(legend);
        trigBox.Child = trigSp;
        Grid.SetColumn(trigBox, 2);
        anaGrid.Children.Add(trigBox);

        ana.Children.Add(anaGrid);

        _verdict = PeriphUi.Verdict("Liga um comando para começar o diagnóstico dos botões e do drift.");
        _verdict.Box.Margin = new Thickness(0, 16, 0, 0);
        ana.Children.Add(_verdict.Box);
        Children.Add(PeriphUi.Card(ana));

        // ======================= Cartão 3: Vibração =======================
        var vib = new StackPanel();
        vib.Children.Add(PeriphUi.CardHead("", "Teste de vibração (rumble)",
            "Ativa cada motor durante 1,5 segundos para confirmar que a vibração do comando funciona."));

        _vibSlider = new Slider { Minimum = 20, Maximum = 100, Value = 75, Width = 200, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
        _vibPct = new TextBlock { Text = "75%", FontFamily = PeriphUi.Mono, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Width = 42 };
        _vibPct.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        _vibSlider.ValueChanged += (_, _) => _vibPct.Text = $"{_vibSlider.Value:0}%";

        var intensityRow = Ui.Row(Ui.T("Intensidade:", "Body"), _vibSlider, _vibPct);
        intensityRow.Margin = new Thickness(0, 0, 0, 14);
        ((TextBlock)intensityRow.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        vib.Children.Add(intensityRow);

        _vibLeft = PeriphUi.Btn("", "Motor esquerdo (grave)", (_, _) => TriggerVibration(1, 0));
        _vibRight = PeriphUi.Btn("", "Motor direito (agudo)", (_, _) => TriggerVibration(0, 1));
        _vibBoth = PeriphUi.Btn("", "Ambos os motores", (_, _) => TriggerVibration(1, 1), "BtnAccent");
        _vibStop = PeriphUi.Btn("", "Parar", (_, _) => StopVibration());
        var vibBtns = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var b in new[] { _vibLeft, _vibRight, _vibBoth, _vibStop })
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            vibBtns.Children.Add(b);
        }
        vib.Children.Add(vibBtns);

        _vibNote = Ui.T("A vibração pára automaticamente ao fim de 1,5 segundos ou quando mudas de aba.", "MutedText", 12);
        vib.Children.Add(_vibNote);
        Children.Add(PeriphUi.Card(vib));

        _scanTimer.Tick += (_, _) => RefreshDevices();
        _vibTimer.Tick += (_, _) => StopVibration();

        SetConnectedUi(false);
        UpdateVibrationEnabled();
    }

    // =========================================================== IPeriphView

    public void Activate()
    {
        if (_active) return;
        _active = true;
        RefreshDevices();
        CompositionTarget.Rendering += OnRenderFrame;
        _scanTimer.Start();
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        CompositionTarget.Rendering -= OnRenderFrame;
        _scanTimer.Stop();
        StopVibration();
    }

    public void WindowDeactivated() => StopVibration();

    // =========================================================== Dispositivos & Polling (60–144 Hz fluido)

    private int _scanning;

    private async void RefreshDevices()
    {
        if (Interlocked.Exchange(ref _scanning, 1) != 0) return;
        List<GamepadDevice> found;
        try
        {
            found = await Task.Run(GamepadProbe.ListDevices);
        }
        finally
        {
            Volatile.Write(ref _scanning, 0);
        }
        if (!_active) return;

        bool changed = found.Count != _devices.Count || !found.Select(d => d.Id).SequenceEqual(_devices.Select(d => d.Id));
        if (!changed && _current != null)
        {
            MaybeRefreshBatteryAsync(_current);
            return;
        }

        string? prevId = _current?.Id;
        _devices = found;

        _selector.Items.Clear();
        foreach (var d in _devices)
            _selector.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d });

        _selector.Visibility = _devices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        int idx = prevId != null ? _devices.FindIndex(d => d.Id == prevId) : -1;
        if (idx < 0 && _devices.Count > 0) idx = 0;

        if (idx >= 0)
        {
            _selector.SelectedIndex = idx;
            _current = _devices[idx];
            _batterySummary = "";
            _lastBatteryQueryTicks = 0;
            ApplyFaceLabels(_current.IsPlayStation);
            SetConnectedUi(true, _current.Name);
            UpdateInfoLine();
            MaybeRefreshBatteryAsync(_current);
        }
        else
        {
            _current = null;
            _batterySummary = "";
            SetConnectedUi(false);
            _infoLine.Text = "Nenhum comando detetado. Liga um comando USB/Bluetooth ou carrega num botão para acordar o comando.";
            SetVerdictCached("Info", "Liga um comando (Xbox, PlayStation, 8BitDo ou USB) para testar botões, analógicos, gatilhos e drift.");
        }
        UpdateVibrationEnabled();
    }

    private void MaybeRefreshBatteryAsync(GamepadDevice dev)
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastBatteryQueryTicks != 0 && (now - _lastBatteryQueryTicks) < Stopwatch.Frequency * 10) return;
        _lastBatteryQueryTicks = now;
        _ = Task.Run(() => GamepadProbe.ReadBatterySummary(dev)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_current?.Id == dev.Id)
                    {
                        _batterySummary = t.Result;
                        UpdateInfoLine();
                    }
                });
            }
        });
    }

    private void UpdateInfoLine()
    {
        if (_current == null) return;
        string bat = string.IsNullOrWhiteSpace(_batterySummary) ? "" : $"  ·  {_batterySummary}";
        _infoLine.Text = $"{_current.Name}{bat}";
    }

    private void SelectFromCombo()
    {
        if (_selector.SelectedItem is ComboBoxItem item && item.Tag is GamepadDevice d && d.Id != _current?.Id)
        {
            StopVibration();
            _current = d;
            _batterySummary = "";
            _lastBatteryQueryTicks = 0;
            ApplyFaceLabels(d.IsPlayStation);
            SetConnectedUi(true, d.Name);
            ResetTests();
            UpdateInfoLine();
            MaybeRefreshBatteryAsync(d);
            UpdateVibrationEnabled();
        }
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        if (!_active || _current == null) return;
        var s = GamepadProbe.Read(_current);
        if (!s.Connected)
        {
            RefreshDevices();
            return;
        }

        // Botões (só altera Brushes quando o estado muda)
        SetPart("dup", s.DUp);
        SetPart("ddown", s.DDown);
        SetPart("dleft", s.DLeft);
        SetPart("dright", s.DRight);
        SetPart("a", s.A);
        SetPart("b", s.B);
        SetPart("x", s.X);
        SetPart("y", s.Y);
        SetPart("lb", s.Lb);
        SetPart("rb", s.Rb);
        SetPart("back", s.Back);
        SetPart("start", s.Start);
        SetPart("l3", s.L3);
        SetPart("r3", s.R3);

        // Gatilhos (GPU ScaleTransform + atualiza texto só quando a % muda)
        if (s.Lt > 0.15) _ltTested = true;
        if (s.Rt > 0.15) _rtTested = true;
        _ltScale.ScaleX = s.Lt;
        _rtScale.ScaleX = s.Rt;
        int ltPct = (int)Math.Round(s.Lt * 100);
        int rtPct = (int)Math.Round(s.Rt * 100);
        if (ltPct != _lastLtPct)
        {
            _lastLtPct = ltPct;
            _ltCanvasText.Text = $"LT {ltPct}%";
            _barLt.Value = ltPct;
            _valLt.Text = $"{ltPct}%";
        }
        if (rtPct != _lastRtPct)
        {
            _lastRtPct = rtPct;
            _rtCanvasText.Text = $"RT {rtPct}%";
            _barRt.Value = rtPct;
            _valRt.Text = $"{rtPct}%";
        }

        // Analógicos no diagrama principal (TranslateTransform puro = zero layout, rótulo L3/R3 acompanha o stick)
        _lStickTransform.X = s.Lx * 15;
        _lStickTransform.Y = s.Ly * 15;
        _rStickTransform.X = s.Rx * 15;
        _rStickTransform.Y = s.Ry * 15;

        // Textos numéricos atualizados a ~20 Hz (50 ms) para não sobrecarregar o motor de texto do WPF
        long now = Stopwatch.GetTimestamp();
        bool updateText = (now - _lastTextUpdateTicks) >= Stopwatch.Frequency / 20;
        if (updateText) _lastTextUpdateTicks = now;

        UpdateRadar(_radarL, s.Lx, s.Ly, updateText);
        UpdateRadar(_radarR, s.Rx, s.Ry, updateText);

        int tested = _parts.Values.Count(p => p.Tested) + (_ltTested ? 1 : 0) + (_rtTested ? 1 : 0);
        int total = _parts.Count + 2;
        if (tested != _lastTestedCount)
        {
            _lastTestedCount = tested;
            _sBtns.Value.Text = tested.ToString();
            _sBtns.Hint.Text = $"de {total} controlos";
            _btnProgress.Value = total > 0 ? (double)tested / total : 0;
        }

        if (updateText)
        {
            double lMag = Math.Sqrt(s.Lx * s.Lx + s.Ly * s.Ly);
            double rMag = Math.Sqrt(s.Rx * s.Rx + s.Ry * s.Ry);
            _sLeft.Value.Text = $"{s.Lx:0.00} · {s.Ly:0.00}";
            _sLeft.Hint.Text = lMag < 0.08 ? "no centro" : $"desvio {Math.Min(100, lMag * 100):0}%";
            _sRight.Value.Text = $"{s.Rx:0.00} · {s.Ry:0.00}";
            _sRight.Hint.Text = rMag < 0.08 ? "no centro" : $"desvio {Math.Min(100, rMag * 100):0}%";
            UpdateVerdict(tested, total);
        }
    }

    private static void UpdateRadar(StickRadar r, double x, double y, bool updateText)
    {
        const double cx = 78;
        const double cy = 78;
        const double radius = 64;

        double mag = Math.Sqrt(x * x + y * y);
        double clampedMag = Math.Min(1.0, mag);
        double dx = Math.Clamp(x, -1, 1) * radius;
        double dy = Math.Clamp(y, -1, 1) * radius;
        double px = cx + dx;
        double py = cy + dy;

        // Movimento via TranslateTransform (zero layout pass)
        r.DotTransform.X = dx;
        r.DotTransform.Y = dy;
        r.Vec.X2 = px;
        r.Vec.Y2 = py;

        if (mag > 0.25) r.Moved = true;
        if (clampedMag > r.MaxReach) r.MaxReach = clampedMag;

        if (mag < 0.35)
        {
            r.RestOffsetSum += mag;
            r.RestSamples++;
        }

        // Atualiza rasto in-place sem realocar PointCollection
        if (double.IsNaN(r.LastTrailPoint.X) ||
            Math.Abs(px - r.LastTrailPoint.X) + Math.Abs(py - r.LastTrailPoint.Y) > 3.5)
        {
            var pt = new Point(px, py);
            r.LastTrailPoint = pt;
            r.TrailPoints.Add(pt);
            if (r.TrailPoints.Count > 36) r.TrailPoints.RemoveAt(0);
        }

        if (!updateText) return;

        r.Coords.Text = $"X {x:+0.00;-0.00;0.00}   Y {y:+0.00;-0.00;0.00}";
        double driftPct = mag < 0.35 ? mag * 100 : (r.RestSamples > 0 ? (r.RestOffsetSum / r.RestSamples) * 100 : 0);
        r.Drift.Text = $"Repouso: {driftPct:0.0}%";
        string color = driftPct > 10 ? "Bad" : driftPct > 6 ? "Warn" : "Good";
        if (r.LastDriftColor != color)
        {
            r.LastDriftColor = color;
            r.Drift.SetResourceReference(TextBlock.ForegroundProperty, color);
        }
        r.Reach.Text = $"Alcance máx.: {r.MaxReach * 100:0}%";
    }

    private void UpdateVerdict(int tested, int total)
    {
        double avgDriftL = _radarL.RestSamples > 20 ? (_radarL.RestOffsetSum / _radarL.RestSamples) * 100 : 0;
        double avgDriftR = _radarR.RestSamples > 20 ? (_radarR.RestOffsetSum / _radarR.RestSamples) * 100 : 0;
        double worstDrift = Math.Max(avgDriftL, avgDriftR);

        if (worstDrift > 10.5)
        {
            string side = avgDriftL >= avgDriftR ? "esquerdo" : "direito";
            SetVerdictCached("Bad", $"Possível drift no analógico {side} (desvio médio de {worstDrift:0.0}% em repouso). Se não estás a tocar no stick, aumenta a zona morta no jogo ou limpa o módulo.");
        }
        else if (worstDrift > 6.5)
        {
            string side = avgDriftL >= avgDriftR ? "esquerdo" : "direito";
            SetVerdictCached("Warn", $"Ligeiro desvio no analógico {side} ({worstDrift:0.0}%). Ainda está perto da zona morta típica (10%), mas vale a pena vigiar.");
        }
        else if (tested == total && _radarL.MaxReach > 0.9 && _radarR.MaxReach > 0.9)
        {
            SetVerdictCached("Good", "Todos os botões, gatilhos e analógicos responderam com alcance completo e sem sinais de drift.");
        }
        else if (tested > 0 || _radarL.Moved || _radarR.Moved)
        {
            SetVerdictCached("Good", $"Comando a responder bem ({tested} de {total} controlos testados). Sem sinais de drift até agora.");
        }
        else
        {
            SetVerdictCached("Info", "Comando ligado. Carrega nos botões, puxa os gatilhos e mexe nos dois analógicos em círculos.");
        }
    }

    private void SetVerdictCached(string kind, string text)
    {
        if (_lastVerdictKind == kind && _lastVerdictText == text) return;
        _lastVerdictKind = kind;
        _lastVerdictText = text;
        PeriphUi.SetVerdict(_verdict, kind, text);
    }

    // =========================================================== Vibração

    private void TriggerVibration(double leftMul, double rightMul)
    {
        if (_current == null || !_current.SupportsVibration) return;
        double power = _vibSlider.Value / 100.0;
        GamepadProbe.SetVibration(_current, leftMul * power, rightMul * power);
        _vibTimer.Stop();
        _vibTimer.Start();
    }

    private void StopVibration()
    {
        _vibTimer.Stop();
        GamepadProbe.StopAllVibration();
    }

    private void UpdateVibrationEnabled()
    {
        bool canVib = _current != null && _current.SupportsVibration;
        _vibLeft.IsEnabled = canVib;
        _vibRight.IsEnabled = canVib;
        _vibBoth.IsEnabled = canVib;
        _vibStop.IsEnabled = canVib;
        _vibSlider.IsEnabled = canVib;
        if (_current == null)
            _vibNote.Text = "Liga um comando compatível (XInput / Xbox) para testar os motores de vibração.";
        else if (!canVib)
            _vibNote.Text = "Este comando está em modo DirectInput (sem vibração XInput). Usa o modo XInput ou DS4Windows para testar os motores.";
        else
            _vibNote.Text = "A vibração pára automaticamente ao fim de 1,5 segundos ou quando mudas de aba.";
    }

    // =========================================================== Helpers de UI

    private void SetConnectedUi(bool on, string? name = null)
    {
        _statusText.Text = on ? "COMANDO LIGADO" : "SEM COMANDO";
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, on ? "Good" : "Faint");
        _statusDot.SetResourceReference(Shape.FillProperty, on ? "Good" : "Faint");
        _statusPill.Background = new SolidColorBrush(PeriphUi.WithAlpha(on ? "Good" : "Faint", 0x1C));
        _statusPill.BorderBrush = new SolidColorBrush(PeriphUi.WithAlpha(on ? "Good" : "Faint", 0x55));
        _statusPill.ToolTip = name ?? "Liga um comando USB ou Bluetooth";
    }

    private void ApplyFaceLabels(bool playstation)
    {
        SetLabel("a", playstation ? "✕" : "A");
        SetLabel("b", playstation ? "◯" : "B");
        SetLabel("x", playstation ? "□" : "X");
        SetLabel("y", playstation ? "△" : "Y");
        SetLabel("lb", playstation ? "L1" : "LB");
        SetLabel("rb", playstation ? "R1" : "RB");
    }

    private void SetLabel(string key, string text)
    {
        if (_parts.TryGetValue(key, out var p) && p.Label != null) p.Label.Text = text;
    }

    private void SetPart(string key, bool down)
    {
        if (!_parts.TryGetValue(key, out var p)) return;
        bool wasTested = p.Tested;
        if (down) p.Tested = true;
        if (p.Down == down && p.Tested == wasTested) return;
        p.Down = down;
        PaintPart(p);
    }

    private static void PaintPart(PadPart p)
    {
        p.Shape.Effect = null;
        if (p.Down)
        {
            p.Shape.SetResourceReference(Shape.FillProperty, "AccentGradient");
            p.Shape.SetResourceReference(Shape.StrokeProperty, "Accent2");
            p.Shape.Effect = new DropShadowEffect { Color = Color.FromRgb(0x2E, 0x9B, 0xFF), BlurRadius = 14, ShadowDepth = 0, Opacity = 0.75 };
            if (p.Label != null) p.Label.Foreground = Brushes.White;
        }
        else if (p.Tested)
        {
            p.Shape.SetResourceReference(Shape.FillProperty, "AccentSoft");
            p.Shape.SetResourceReference(Shape.StrokeProperty, "Accent");
            if (p.Label != null) p.Label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        }
        else
        {
            p.Shape.SetResourceReference(Shape.FillProperty, "Hover");
            p.Shape.SetResourceReference(Shape.StrokeProperty, "LineStrong");
            if (p.Label != null) p.Label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        }
    }

    private void ResetTests()
    {
        foreach (var p in _parts.Values) { p.Down = false; p.Tested = false; PaintPart(p); }
        _ltTested = false; _rtTested = false;
        _lastTestedCount = -1;
        ResetRadar(_radarL);
        ResetRadar(_radarR);
        _sBtns.Value.Text = "0";
        _btnProgress.Value = 0;
        if (_current != null)
            SetVerdictCached("Info", "Contadores limpos. Carrega nos botões e mexe nos analógicos para testar outra vez.");
    }

    private static void ResetRadar(StickRadar r)
    {
        r.TrailPoints.Clear();
        r.LastTrailPoint = new Point(double.NaN, double.NaN);
        r.MaxReach = 0;
        r.RestOffsetSum = 0;
        r.RestSamples = 0;
        r.Moved = false;
        r.LastDriftColor = "";
        r.Drift.Text = "Repouso: —";
        r.Drift.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        r.Reach.Text = "Alcance máx.: 0%";
    }

    // =========================================================== Construção do diagrama visual (ergonómico 540×316)

    private Canvas BuildControllerDiagram(out TextBlock ltTxt, out TextBlock rtTxt)
    {
        var c = new Canvas { Width = 540, Height = 316 };

        // Gatilhos analógicos no topo (LT / RT), alinhados aos ombros
        ltTxt = AddTriggerBar(c, 96, 8, 88, 20, _ltScale, "LT 0%");
        rtTxt = AddTriggerBar(c, 356, 8, 88, 20, _rtScale, "RT 0%");

        // Bumpers curvos (LB / RB) colados ao contorno dos ombros
        AddPathPart(c, "lb", "LB", "M 88,58 C 94,38 118,34 150,34 L 184,34 C 192,34 196,40 196,47 L 196,55 C 196,58 192,60 186,60 L 92,60 C 88,60 86,59 88,58 Z", 92, 37, 100, 22);
        AddPathPart(c, "rb", "RB", "M 452,58 C 446,38 422,34 390,34 L 356,34 C 348,34 344,40 344,47 L 344,55 C 344,58 348,60 354,60 L 448,60 C 452,60 454,59 452,58 Z", 348, 37, 100, 22);

        // Corpo ergonómico simétrico (ponte central espaçosa y=64..232; pegas arredondadas até y=302)
        var bodyPath = new Path
        {
            Data = Geometry.Parse(
                "M 216,66 " +
                "C 182,62 126,60 96,75 " +
                "C 68,89 52,126 46,178 " +
                "L 38,244 " +
                "C 34,282 60,306 92,302 " +
                "C 118,298 136,278 156,246 " +
                "C 164,234 174,230 190,230 " +
                "L 350,230 " +
                "C 366,230 376,234 384,246 " +
                "C 404,278 422,298 448,302 " +
                "C 480,306 506,282 502,244 " +
                "L 494,178 " +
                "C 488,126 472,89 444,75 " +
                "C 414,60 358,62 324,66 Z"),
            StrokeThickness = 1.6
        };
        bodyPath.SetResourceReference(Shape.FillProperty, "Card");
        bodyPath.SetResourceReference(Shape.StrokeProperty, "LineStrong");
        c.Children.Add(bodyPath);

        // Linhas subtis de contorno nas pegas (profundidade visual)
        var gripL = new Path { Data = Geometry.Parse("M 66,184 C 60,224 62,262 78,284"), StrokeThickness = 1.2, Opacity = 0.55 };
        var gripR = new Path { Data = Geometry.Parse("M 474,184 C 480,224 478,262 462,284"), StrokeThickness = 1.2, Opacity = 0.55 };
        gripL.SetResourceReference(Shape.StrokeProperty, "Line");
        gripR.SetResourceReference(Shape.StrokeProperty, "Line");
        c.Children.Add(gripL);
        c.Children.Add(gripR);

        // Emblema central (botão Home decorativo)
        var homeRing = new Ellipse { Width = 32, Height = 32, StrokeThickness = 1.2 };
        homeRing.SetResourceReference(Shape.FillProperty, "Bg");
        homeRing.SetResourceReference(Shape.StrokeProperty, "LineStrong");
        Canvas.SetLeft(homeRing, 254); Canvas.SetTop(homeRing, 82);
        c.Children.Add(homeRing);
        var homeTxt = new TextBlock { Text = "HC", Width = 32, FontSize = 10, FontWeight = FontWeights.Black, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
        homeTxt.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        Canvas.SetLeft(homeTxt, 254); Canvas.SetTop(homeTxt, 91);
        c.Children.Add(homeTxt);

        // Botões centrais (View / Menu)
        AddRectPart(c, "back", "⧉", 216, 126, 26, 18, 9);
        AddRectPart(c, "start", "☰", 298, 126, 26, 18, 9);

        // Analógico esquerdo (poço + cabeça móvel com rótulo L3 integrado)
        AddStickAssembly(c, "l3", "L3", 134, 130, _lStickTransform);

        // D-Pad com base circular encaixada dentro do corpo
        var dWell = new Ellipse { Width = 68, Height = 68, StrokeThickness = 1.1 };
        dWell.SetResourceReference(Shape.FillProperty, "Bg");
        dWell.SetResourceReference(Shape.StrokeProperty, "Line");
        Canvas.SetLeft(dWell, 166); Canvas.SetTop(dWell, 154);
        c.Children.Add(dWell);
        var dCenter = new Rectangle { Width = 20, Height = 20, RadiusX = 2, RadiusY = 2 };
        dCenter.SetResourceReference(Shape.FillProperty, "Hover");
        Canvas.SetLeft(dCenter, 190); Canvas.SetTop(dCenter, 178);
        c.Children.Add(dCenter);
        AddRectPart(c, "dup", "↑", 190, 158, 20, 20, 4);
        AddRectPart(c, "ddown", "↓", 190, 198, 20, 20, 4);
        AddRectPart(c, "dleft", "←", 170, 178, 20, 20, 4);
        AddRectPart(c, "dright", "→", 210, 178, 20, 20, 4);

        // Analógico direito (totalmente dentro da ponte inferior, sem sair do contorno)
        AddStickAssembly(c, "r3", "R3", 340, 186, _rStickTransform);

        // Botões de face (losango centrado em 410, 126)
        AddCirclePart(c, "y", "Y", 395, 81, 30);
        AddCirclePart(c, "x", "X", 365, 111, 30);
        AddCirclePart(c, "b", "B", 425, 111, 30);
        AddCirclePart(c, "a", "A", 395, 141, 30);

        return c;
    }

    private void AddStickAssembly(Canvas c, string key, string label, double cx, double cy, TranslateTransform moveTransform)
    {
        const double wellSize = 64;
        const double knobSize = 36;

        var well = new Ellipse { Width = wellSize, Height = wellSize, StrokeThickness = 1.3 };
        well.SetResourceReference(Shape.FillProperty, "Bg");
        well.SetResourceReference(Shape.StrokeProperty, "LineStrong");
        Canvas.SetLeft(well, cx - wellSize / 2); Canvas.SetTop(well, cy - wellSize / 2);
        c.Children.Add(well);

        var knob = new Ellipse { Width = knobSize, Height = knobSize, StrokeThickness = 1.5 };
        var lbl = new TextBlock
        {
            Text = label, FontSize = 11, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        var group = new Grid { Width = knobSize, Height = knobSize, RenderTransform = moveTransform };
        group.Children.Add(knob);
        group.Children.Add(lbl);
        Canvas.SetLeft(group, cx - knobSize / 2); Canvas.SetTop(group, cy - knobSize / 2);
        c.Children.Add(group);

        var part = new PadPart { Key = key, Shape = knob, Label = lbl };
        _parts[key] = part;
        PaintPart(part);
    }

    private static TextBlock AddTriggerBar(Canvas c, double x, double y, double w, double h, ScaleTransform scale, string text)
    {
        var track = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(h / 2), BorderThickness = new Thickness(1), ClipToBounds = true };
        track.SetResourceReference(Border.BackgroundProperty, "Hover");
        track.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        var g = new Grid();
        var fill = new Border { RenderTransform = scale, RenderTransformOrigin = new Point(0, 0.5), CornerRadius = new CornerRadius(h / 2 - 1) };
        fill.SetResourceReference(Border.BackgroundProperty, "AccentGradient");
        var lbl = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.Bold, FontFamily = PeriphUi.Mono, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        g.Children.Add(fill);
        g.Children.Add(lbl);
        track.Child = g;
        Canvas.SetLeft(track, x); Canvas.SetTop(track, y);
        c.Children.Add(track);
        return lbl;
    }

    private void AddPathPart(Canvas c, string key, string label, string pathData, double lx, double ly, double lw, double lh)
    {
        var shape = new Path { Data = Geometry.Parse(pathData), StrokeThickness = 1.2 };
        var lbl = new TextBlock { Text = label, Width = lw, Height = lh, FontSize = 11, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 3, 0, 0), IsHitTestVisible = false };
        c.Children.Add(shape);
        Canvas.SetLeft(lbl, lx); Canvas.SetTop(lbl, ly);
        c.Children.Add(lbl);
        var part = new PadPart { Key = key, Shape = shape, Label = lbl };
        _parts[key] = part;
        PaintPart(part);
    }

    private void AddRectPart(Canvas c, string key, string label, double x, double y, double w, double h, double radius)
    {
        var shape = new Rectangle { Width = w, Height = h, RadiusX = radius, RadiusY = radius, StrokeThickness = 1.2 };
        var lbl = new TextBlock { Text = label, Width = w, Height = h, FontSize = 11, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, Math.Max(0, (h - 14) / 2), 0, 0), IsHitTestVisible = false };
        Canvas.SetLeft(shape, x); Canvas.SetTop(shape, y);
        Canvas.SetLeft(lbl, x); Canvas.SetTop(lbl, y);
        c.Children.Add(shape);
        c.Children.Add(lbl);
        var part = new PadPart { Key = key, Shape = shape, Label = lbl };
        _parts[key] = part;
        PaintPart(part);
    }

    private void AddCirclePart(Canvas c, string key, string label, double x, double y, double size)
    {
        var shape = new Ellipse { Width = size, Height = size, StrokeThickness = 1.3 };
        var lbl = new TextBlock { Text = label, Width = size, Height = size, FontSize = 12.5, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, (size - 16) / 2, 0, 0), IsHitTestVisible = false };
        Canvas.SetLeft(shape, x); Canvas.SetTop(shape, y);
        Canvas.SetLeft(lbl, x); Canvas.SetTop(lbl, y);
        c.Children.Add(shape);
        c.Children.Add(lbl);
        var part = new PadPart { Key = key, Shape = shape, Label = lbl };
        _parts[key] = part;
        PaintPart(part);
    }

    private static StickRadar BuildStickRadar()
    {
        const double size = 156;
        const double cx = size / 2;
        const double cy = size / 2;
        const double r = 64;

        var canvas = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };

        var outer = new Ellipse { Width = r * 2, Height = r * 2, StrokeThickness = 1.3 };
        outer.SetResourceReference(Shape.FillProperty, "Bg");
        outer.SetResourceReference(Shape.StrokeProperty, "LineStrong");
        Canvas.SetLeft(outer, cx - r); Canvas.SetTop(outer, cy - r);
        canvas.Children.Add(outer);

        var hLine = new Line { X1 = cx - r, X2 = cx + r, Y1 = cy, Y2 = cy, StrokeThickness = 1 };
        var vLine = new Line { X1 = cx, X2 = cx, Y1 = cy - r, Y2 = cy + r, StrokeThickness = 1 };
        hLine.SetResourceReference(Shape.StrokeProperty, "Line");
        vLine.SetResourceReference(Shape.StrokeProperty, "Line");
        canvas.Children.Add(hLine);
        canvas.Children.Add(vLine);

        double dz = r * 0.14;
        var dead = new Ellipse { Width = dz * 2, Height = dz * 2, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } };
        dead.SetResourceReference(Shape.StrokeProperty, "Warn");
        Canvas.SetLeft(dead, cx - dz); Canvas.SetTop(dead, cy - dz);
        canvas.Children.Add(dead);

        var pts = new PointCollection();
        var trail = new Polyline { Points = pts, StrokeThickness = 1.5, Opacity = 0.55 };
        trail.SetResourceReference(Shape.StrokeProperty, "Accent");
        canvas.Children.Add(trail);

        var vec = new Line { X1 = cx, Y1 = cy, X2 = cx, Y2 = cy, StrokeThickness = 2 };
        vec.SetResourceReference(Shape.StrokeProperty, "Accent2");
        canvas.Children.Add(vec);

        var dotTransform = new TranslateTransform();
        var dot = new Ellipse { Width = 12, Height = 12, StrokeThickness = 2, RenderTransform = dotTransform };
        dot.SetResourceReference(Shape.FillProperty, "Accent2");
        dot.Stroke = Brushes.White;
        Canvas.SetLeft(dot, cx - 6); Canvas.SetTop(dot, cy - 6);
        canvas.Children.Add(dot);

        var coords = new TextBlock { Text = "X +0.00   Y +0.00", FontFamily = PeriphUi.Mono, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
        coords.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var drift = new TextBlock { Text = "Repouso: —", FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        drift.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var reach = new TextBlock { Text = "Alcance máx.: 0%", FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center };
        reach.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        return new StickRadar
        {
            Canvas = canvas,
            DotTransform = dotTransform,
            Vec = vec,
            Trail = trail,
            TrailPoints = pts,
            Coords = coords,
            Drift = drift,
            Reach = reach
        };
    }

    private static Border WrapRadar(StickRadar r, string heading)
    {
        var sp = new StackPanel();
        var title = Ui.T(heading, "Label", 10);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(title);
        sp.Children.Add(r.Canvas);
        sp.Children.Add(r.Coords);
        sp.Children.Add(r.Drift);
        sp.Children.Add(r.Reach);
        var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(1), Child = sp };
        b.SetResourceReference(Border.BackgroundProperty, "Hover");
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return b;
    }

    private static (ProgressBar Bar, TextBlock Value) AddTriggerRow(StackPanel host, string title)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var val = new TextBlock { Text = "0%", FontFamily = PeriphUi.Mono, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        val.SetResourceReference(TextBlock.ForegroundProperty, "Accent2");
        DockPanel.SetDock(val, Dock.Right);
        head.Children.Add(val);
        head.Children.Add(Ui.T(title, "Body", 12.5));
        host.Children.Add(head);

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 10, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 12) };
        bar.SetResourceReference(Control.ForegroundProperty, "Accent");
        bar.SetResourceReference(Control.BackgroundProperty, "Bg");
        host.Children.Add(bar);
        return (bar, val);
    }
}
