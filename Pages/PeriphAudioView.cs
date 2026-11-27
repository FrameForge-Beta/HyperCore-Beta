using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameForge.Peripherals;

namespace FrameForge.Pages;

/// <summary>
/// Aba "Fones": testa o canal esquerdo, o canal direito e ambos ao mesmo tempo, com diagrama visual interativo,
/// sequência automática, controlo de equilíbrio (balanço E ↔ D) e frequências (graves, médios e agudos).
/// </summary>
internal sealed class PeriphAudioView : StackPanel, IPeriphView
{
    private readonly AudioTester _audio = new();
    private readonly DispatcherTimer _meterTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };

    private readonly ComboBox _devices = new() { MinWidth = 240 };
    private readonly Slider _volume;
    private readonly TextBlock _volumeLabel;
    private readonly ComboBox _styleCombo;

    // Diagrama visual dos auscultadores
    private readonly Border _leftCup, _rightCup;
    private readonly TextBlock _leftCupText, _rightCupText, _centerStatus;
    private readonly Path _waveL1, _waveL2, _waveR1, _waveR2;
    private readonly Border _vuLeftFill, _vuRightFill;

    // Botões principais e sequência
    private readonly Button _btnLeft, _btnBoth, _btnRight, _btnSeq, _btnStop;
    private readonly Button _btnBalance;
    private readonly Slider _balanceSlider;
    private readonly TextBlock _balanceText;

    // Chips de verificação e veredito
    private readonly (Border Chip, TextBlock Glyph) _chipL, _chipR, _chipB;
    private bool _testedL, _testedR, _testedB;
    private readonly (Border Box, TextBlock Glyph, TextBlock Text) _verdict;

    private CancellationTokenSource? _seqCts;
    private bool _balancePlaying;
    private double _balanceValue; // -1 (E) .. 0 (Centro) .. +1 (D)
    private double _volumeValue = 0.50;
    private string _activeSide = ""; // "L", "R", "B", "BAL", "FREQ"

    public PeriphAudioView()
    {
        // ======================= Cartão 1: Esquerda, Direita e Ambos =======================
        var refreshBtn = PeriphUi.Btn("", "Atualizar", (_, _) => LoadDevices());
        var devRow = Ui.Row(_devices, refreshBtn);
        _devices.Margin = new Thickness(0, 0, 8, 0);

        var main = new StackPanel();
        main.Children.Add(PeriphUi.CardHead("", "Fones e canais estéreo",
            "Testa o lado esquerdo, o lado direito e os dois ao mesmo tempo. Confirma que os fones não estão trocados nem desequilibrados.", devRow));

        // Diagrama visual dos auscultadores (clicável!)
        var stageCanvas = BuildHeadphonesCanvas(
            out _leftCup, out _rightCup, out _leftCupText, out _rightCupText, out _centerStatus,
            out _waveL1, out _waveL2, out _waveR1, out _waveR2, out _vuLeftFill, out _vuRightFill);

        var stageBox = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 16),
            Child = new Viewbox { Child = stageCanvas, MaxHeight = 205, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center }
        };
        stageBox.SetResourceReference(Border.BackgroundProperty, "Bg");
        stageBox.SetResourceReference(Border.BorderBrushProperty, "Line");
        main.Children.Add(stageBox);

        // Botões principais de canal
        _btnLeft = PeriphUi.Btn("", "Esquerda (E)", (_, _) => PlaySingle(AudioChannelMode.Left));
        _btnBoth = PeriphUi.Btn("", "Ambos (E + D)", (_, _) => PlaySingle(AudioChannelMode.Both), "BtnAccent");
        _btnRight = PeriphUi.Btn("", "Direita (D)", (_, _) => PlaySingle(AudioChannelMode.Right));
        _btnSeq = PeriphUi.Btn("", "Sequência (E → D → Ambos)", (_, _) => RunSequence());
        _btnStop = PeriphUi.Btn("", "Parar", (_, _) => StopAll());

        var btnBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var b in new[] { _btnLeft, _btnBoth, _btnRight, _btnSeq, _btnStop })
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            btnBar.Children.Add(b);
        }
        main.Children.Add(btnBar);

        // Volume + tipo de som
        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };

        var volLbl = Ui.T("Volume do teste:", "Body");
        volLbl.VerticalAlignment = VerticalAlignment.Center;
        _volume = new Slider { Minimum = 10, Maximum = 100, Value = 50, Width = 160, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        _volumeLabel = new TextBlock { Text = "50%", FontFamily = PeriphUi.Mono, FontSize = 13, Width = 44, VerticalAlignment = VerticalAlignment.Center };
        _volumeLabel.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        _volume.ValueChanged += (_, _) =>
        {
            _volumeValue = _volume.Value / 100.0;
            _volumeLabel.Text = $"{_volume.Value:0}%";
        };
        var volGroup = Ui.Row(volLbl, _volume, _volumeLabel);
        volGroup.Margin = new Thickness(0, 0, 24, 8);
        controls.Children.Add(volGroup);

        var styleLbl = Ui.T("Tipo de som:", "Body");
        styleLbl.VerticalAlignment = VerticalAlignment.Center;
        styleLbl.Margin = new Thickness(0, 0, 10, 0);
        _styleCombo = new ComboBox { MinWidth = 195 };
        _styleCombo.Items.Add(new ComboBoxItem { Content = "Toque musical (agradável)", Tag = AudioSoundStyle.Chime });
        _styleCombo.Items.Add(new ComboBoxItem { Content = "Tom puro (440 Hz)", Tag = AudioSoundStyle.Tone });
        _styleCombo.Items.Add(new ComboBoxItem { Content = "Ruído rosa suave", Tag = AudioSoundStyle.PinkNoise });
        _styleCombo.SelectedIndex = 0;
        var styleGroup = Ui.Row(styleLbl, _styleCombo);
        styleGroup.Margin = new Thickness(0, 0, 0, 8);
        controls.Children.Add(styleGroup);
        main.Children.Add(controls);

        // Chips de progresso (Esquerda / Direita / Ambos)
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        _chipL = MakeChip("Esquerda (E)");
        _chipR = MakeChip("Direita (D)");
        _chipB = MakeChip("Ambos (E + D)");
        chips.Children.Add(_chipL.Chip);
        chips.Children.Add(_chipR.Chip);
        chips.Children.Add(_chipB.Chip);
        main.Children.Add(chips);

        _verdict = PeriphUi.Verdict("Carrega em Esquerda, Direita ou Ambos (ou clica diretamente nos auscultadores acima) para testar.");
        main.Children.Add(_verdict.Box);
        Children.Add(PeriphUi.Card(main));

        // ======================= Cartão 2: Equilíbrio & Frequências =======================
        var extra = new StackPanel();
        extra.Children.Add(PeriphUi.CardHead("", "Equilíbrio estéreo e frequências",
            "Desliza o som suavemente da esquerda para a direita ou testa graves, médios e agudos para detetar ruídos ou vibrações nos altifalantes."));

        // Balanço contínuo
        var balBox = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 14) };
        balBox.SetResourceReference(Border.BackgroundProperty, "Hover");
        balBox.SetResourceReference(Border.BorderBrushProperty, "Line");
        var balSp = new StackPanel();
        balSp.Children.Add(Ui.T("EQUILÍBRIO (BALANÇO E ↔ D)", "Label", 10));

        var balRow = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
        _btnBalance = PeriphUi.Btn("", "Tocar balanço", (_, _) => ToggleBalance());
        var centerBtn = PeriphUi.Btn("", "Centrar", (_, _) => _balanceSlider.Value = 0);
        var balActions = Ui.Row(_btnBalance, centerBtn);
        DockPanel.SetDock(balActions, Dock.Right);
        balRow.Children.Add(balActions);

        var leftTag = Ui.T("100% E", "Label", 11);
        leftTag.VerticalAlignment = VerticalAlignment.Center;
        leftTag.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(leftTag, Dock.Left);
        balRow.Children.Add(leftTag);

        var rightTag = Ui.T("100% D", "Label", 11);
        rightTag.VerticalAlignment = VerticalAlignment.Center;
        rightTag.Margin = new Thickness(10, 0, 16, 0);
        DockPanel.SetDock(rightTag, Dock.Right);
        balRow.Children.Add(rightTag);

        _balanceSlider = new Slider { Minimum = -100, Maximum = 100, Value = 0, VerticalAlignment = VerticalAlignment.Center };
        _balanceSlider.ValueChanged += (_, _) =>
        {
            _balanceValue = _balanceSlider.Value / 100.0;
            UpdateBalanceText();
        };
        balRow.Children.Add(_balanceSlider);
        balSp.Children.Add(balRow);

        _balanceText = Ui.T("Centro (50% E · 50% D) — o som deve parecer vir exatamente do meio da cabeça.", "MutedText", 12);
        balSp.Children.Add(_balanceText);
        balBox.Child = balSp;
        extra.Children.Add(balBox);

        // Teste de frequências
        var freqLabel = Ui.T("TESTE DE FREQUÊNCIAS (AMBOS OS LADOS)", "Label", 10);
        freqLabel.Margin = new Thickness(0, 2, 0, 8);
        extra.Children.Add(freqLabel);

        var freqWrap = new WrapPanel();
        (string Name, double Hz, string Tip)[] freqs =
        {
            ("Sub-grave · 60 Hz", 60, "Testa os graves profundos e se há vibração solta nas conchas."),
            ("Grave · 120 Hz", 120, "Testa o corpo dos graves (explosões, baixo)."),
            ("Médio · 1 kHz", 1000, "Referência para vozes e nitidez central."),
            ("Agudo · 6 kHz", 6000, "Testa detalhe e brilho sem distorção."),
            ("Muito agudo · 12 kHz", 12000, "Testa a resposta de altas frequências (volume reduzido automaticamente).")
        };
        foreach (var (name, hz, tip) in freqs)
        {
            var fb = PeriphUi.Btn("", name, (_, _) => PlayFreq(hz, name));
            fb.ToolTip = tip;
            fb.Margin = new Thickness(0, 0, 8, 8);
            freqWrap.Children.Add(fb);
        }
        extra.Children.Add(freqWrap);
        Children.Add(PeriphUi.Card(extra));

        _meterTimer.Tick += (_, _) => UpdateMeters();
        LoadDevices();
    }

    // =========================================================== IPeriphView

    public void Activate()
    {
        LoadDevices();
        _meterTimer.Start();
    }

    public void Deactivate()
    {
        StopAll();
        _meterTimer.Stop();
    }

    // =========================================================== Lógica de reprodução

    private int SelectedDeviceId => (_devices.SelectedItem as ComboBoxItem)?.Tag is int id ? id : -1;

    private AudioSoundStyle SelectedStyle =>
        (_styleCombo.SelectedItem as ComboBoxItem)?.Tag is AudioSoundStyle s ? s : AudioSoundStyle.Chime;

    private void LoadDevices()
    {
        int prev = SelectedDeviceId;
        var list = AudioTester.ListOutputs();
        _devices.Items.Clear();
        int selIdx = 0;
        for (int i = 0; i < list.Count; i++)
        {
            _devices.Items.Add(new ComboBoxItem { Content = list[i].Name, Tag = list[i].DeviceId });
            if (list[i].DeviceId == prev) selIdx = i;
        }
        if (_devices.Items.Count > 0) _devices.SelectedIndex = selIdx;
    }

    private void PlaySingle(AudioChannelMode mode)
    {
        CancelSequence();
        StopBalanceMode();
        _activeSide = mode switch { AudioChannelMode.Left => "L", AudioChannelMode.Right => "R", _ => "B" };
        HighlightCups(_activeSide);

        bool ok = _audio.PlayChannel(SelectedDeviceId, mode, SelectedStyle, _volumeValue, 1.7, 0, () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_audio.IsPlaying && !_balancePlaying)
                {
                    _activeSide = "";
                    HighlightCups("");
                }
            });
        });

        if (!ok)
        {
            PeriphUi.SetVerdict(_verdict, "Bad", "Não foi possível abrir o dispositivo de áudio selecionado. Verifica se os fones estão ligados e selecionados.");
            return;
        }

        if (mode == AudioChannelMode.Left) _testedL = true;
        else if (mode == AudioChannelMode.Right) _testedR = true;
        else if (mode == AudioChannelMode.Both) _testedB = true;
        UpdateChipsAndVerdict(mode switch
        {
            AudioChannelMode.Left => "A tocar APENAS no lado ESQUERDO. Se ouviste à direita, os fones estão trocados.",
            AudioChannelMode.Right => "A tocar APENAS no lado DIREITO. Se ouviste à esquerda, os fones estão trocados.",
            _ => "A tocar em AMBOS os lados por igual. O som deve soar perfeitamente centrado."
        });
    }

    private async void RunSequence()
    {
        CancelSequence();
        StopBalanceMode();
        var cts = new CancellationTokenSource();
        _seqCts = cts;
        var ct = cts.Token;

        var steps = new (AudioChannelMode Mode, string Code, string Msg)[]
        {
            (AudioChannelMode.Left, "L", "Passo 1/3 — A tocar no lado ESQUERDO…"),
            (AudioChannelMode.Right, "R", "Passo 2/3 — A tocar no lado DIREITO…"),
            (AudioChannelMode.Both, "B", "Passo 3/3 — A tocar em AMBOS os lados (centro)…")
        };

        try
        {
            foreach (var (mode, code, msg) in steps)
            {
                if (ct.IsCancellationRequested) break;
                _activeSide = code;
                HighlightCups(code);
                PeriphUi.SetVerdict(_verdict, "Info", msg);
                if (!_audio.PlayChannel(SelectedDeviceId, mode, SelectedStyle, _volumeValue, 1.4))
                {
                    PeriphUi.SetVerdict(_verdict, "Bad", "Não foi possível abrir o dispositivo de áudio selecionado.");
                    break;
                }
                if (mode == AudioChannelMode.Left) _testedL = true;
                else if (mode == AudioChannelMode.Right) _testedR = true;
                else _testedB = true;
                PaintChip(_chipL, _testedL);
                PaintChip(_chipR, _testedR);
                PaintChip(_chipB, _testedB);

                await Task.Delay(1650, ct);
            }
            if (!ct.IsCancellationRequested)
            {
                _activeSide = "";
                HighlightCups("");
                UpdateChipsAndVerdict(null);
            }
        }
        catch (TaskCanceledException) { }
    }

    private void ToggleBalance()
    {
        if (_balancePlaying)
        {
            StopAll();
            return;
        }
        CancelSequence();
        _activeSide = "BAL";
        if (!_audio.StartBalanceLoop(SelectedDeviceId, () => _balanceValue, () => _volumeValue))
        {
            PeriphUi.SetVerdict(_verdict, "Bad", "Não foi possível iniciar o som de equilíbrio no dispositivo selecionado.");
            return;
        }
        _balancePlaying = true;
        PeriphUi.SetBtn(_btnBalance, "", "Parar balanço");
        _centerStatus.Text = "EQUILÍBRIO";
        PeriphUi.SetVerdict(_verdict, "Info", "Move o controlo de equilíbrio entre Esquerda e Direita para verificar a transição suave entre os dois ouvidos.");
    }

    private void PlayFreq(double hz, string label)
    {
        CancelSequence();
        StopBalanceMode();
        _activeSide = "B";
        HighlightCups("B");
        _centerStatus.Text = $"{hz:0} Hz";
        if (!_audio.PlayFrequency(SelectedDeviceId, hz, _volumeValue, 1.8, () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_audio.IsPlaying && !_balancePlaying) { _activeSide = ""; HighlightCups(""); }
            });
        }))
        {
            PeriphUi.SetVerdict(_verdict, "Bad", "Não foi possível reproduzir o teste de frequência.");
            return;
        }
        PeriphUi.SetVerdict(_verdict, "Info", $"A tocar {label} em ambos os fones. O som deve ser limpo, sem zumbidos nem vibrações estranhas.");
    }

    private void StopAll()
    {
        CancelSequence();
        StopBalanceMode();
        _audio.Stop();
        _activeSide = "";
        HighlightCups("");
    }

    private void CancelSequence()
    {
        try { _seqCts?.Cancel(); } catch { }
        _seqCts = null;
    }

    private void StopBalanceMode()
    {
        if (!_balancePlaying) return;
        _balancePlaying = false;
        PeriphUi.SetBtn(_btnBalance, "", "Tocar balanço");
    }

    // =========================================================== Visual e medidores

    private void UpdateMeters()
    {
        double l = _audio.LiveLeft;
        double r = _audio.LiveRight;
        _vuLeftFill.Height = Math.Clamp(l, 0, 1) * 108;
        _vuRightFill.Height = Math.Clamp(r, 0, 1) * 108;

        if (_balancePlaying)
        {
            bool lOn = _balanceValue < 0.85;
            bool rOn = _balanceValue > -0.85;
            PaintCup(_leftCup, _leftCupText, lOn);
            PaintCup(_rightCup, _rightCupText, rOn);
            _waveL1.Opacity = lOn ? Math.Clamp(l * 1.4, 0.15, 0.9) : 0;
            _waveL2.Opacity = lOn ? Math.Clamp(l * 1.1, 0.1, 0.6) : 0;
            _waveR1.Opacity = rOn ? Math.Clamp(r * 1.4, 0.15, 0.9) : 0;
            _waveR2.Opacity = rOn ? Math.Clamp(r * 1.1, 0.1, 0.6) : 0;
        }
        else if (_audio.IsPlaying)
        {
            _waveL1.Opacity = l > 0.02 ? Math.Clamp(l * 1.6, 0.25, 1) : 0;
            _waveL2.Opacity = l > 0.02 ? Math.Clamp(l * 1.2, 0.15, 0.7) : 0;
            _waveR1.Opacity = r > 0.02 ? Math.Clamp(r * 1.6, 0.25, 1) : 0;
            _waveR2.Opacity = r > 0.02 ? Math.Clamp(r * 1.2, 0.15, 0.7) : 0;
        }
        else
        {
            _waveL1.Opacity = 0; _waveL2.Opacity = 0;
            _waveR1.Opacity = 0; _waveR2.Opacity = 0;
        }
    }

    private void HighlightCups(string side)
    {
        bool left = side is "L" or "B";
        bool right = side is "R" or "B";
        PaintCup(_leftCup, _leftCupText, left);
        PaintCup(_rightCup, _rightCupText, right);
        _centerStatus.Text = side switch
        {
            "L" => "ESQUERDA",
            "R" => "DIREITA",
            "B" => "AMBOS (E + D)",
            _ => "CLICA NUM LADO"
        };
    }

    private static void PaintCup(Border cup, TextBlock lbl, bool active)
    {
        cup.Effect = null;
        if (active)
        {
            cup.SetResourceReference(Border.BackgroundProperty, "AccentGradient");
            cup.SetResourceReference(Border.BorderBrushProperty, "Accent2");
            cup.Effect = new DropShadowEffect { Color = Color.FromRgb(0x2E, 0x9B, 0xFF), BlurRadius = 22, ShadowDepth = 0, Opacity = 0.75 };
            lbl.Foreground = Brushes.White;
        }
        else
        {
            cup.SetResourceReference(Border.BackgroundProperty, "Hover");
            cup.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        }
    }

    private void UpdateBalanceText()
    {
        int v = (int)Math.Round(_balanceSlider.Value);
        if (Math.Abs(v) <= 3)
            _balanceText.Text = "Centro (50% E · 50% D) — o som deve parecer vir exatamente do meio da cabeça.";
        else if (v < 0)
            _balanceText.Text = $"Desviado para a esquerda ({-v}% E) — o lado esquerdo deve soar mais alto.";
        else
            _balanceText.Text = $"Desviado para a direita ({v}% D) — o lado direito deve soar mais alto.";
    }

    private void UpdateChipsAndVerdict(string? liveMessage)
    {
        PaintChip(_chipL, _testedL);
        PaintChip(_chipR, _testedR);
        PaintChip(_chipB, _testedB);

        if (liveMessage != null)
        {
            PeriphUi.SetVerdict(_verdict, "Info", liveMessage);
        }
        else if (_testedL && _testedR && _testedB)
        {
            PeriphUi.SetVerdict(_verdict, "Good", "Testaste esquerda, direita e ambos os canais. Se cada lado tocou no ouvido certo e ambos soaram centrados, os fones estão perfeitos.");
        }
        else
        {
            var missing = new List<string>();
            if (!_testedL) missing.Add("Esquerda");
            if (!_testedR) missing.Add("Direita");
            if (!_testedB) missing.Add("Ambos");
            PeriphUi.SetVerdict(_verdict, "Info", $"Falta testar: {string.Join(", ", missing)}.");
        }
    }

    private static (Border Chip, TextBlock Glyph) MakeChip(string label)
    {
        var g = PeriphUi.Icon("", 13, "Faint");
        g.Margin = new Thickness(0, 0, 8, 0);
        var t = Ui.T(label, "Body", 12.5);
        t.VerticalAlignment = VerticalAlignment.Center;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 8),
            BorderThickness = new Thickness(1),
            Child = Ui.Row(g, t)
        };
        chip.SetResourceReference(Border.BackgroundProperty, "Hover");
        chip.SetResourceReference(Border.BorderBrushProperty, "Line");
        return (chip, g);
    }

    private static void PaintChip((Border Chip, TextBlock Glyph) c, bool done)
    {
        if (done)
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

    private Canvas BuildHeadphonesCanvas(
        out Border leftCup, out Border rightCup,
        out TextBlock leftText, out TextBlock rightText, out TextBlock centerStatus,
        out Path wL1, out Path wL2, out Path wR1, out Path wR2,
        out Border vuLeft, out Border vuRight)
    {
        var c = new Canvas { Width = 440, Height = 196 };

        // Medidor VU esquerdo
        vuLeft = AddVuMeter(c, 16, 48, "E");
        vuRight = AddVuMeter(c, 404, 48, "D");

        // Arco (banda superior dos auscultadores)
        var band = new Path
        {
            Data = Geometry.Parse("M 118,110 C 118,22 322,22 322,110"),
            StrokeThickness = 12,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        band.SetResourceReference(Shape.StrokeProperty, "LineStrong");
        c.Children.Add(band);

        var bandInner = new Path
        {
            Data = Geometry.Parse("M 134,88 C 142,36 298,36 306,88"),
            StrokeThickness = 4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        bandInner.SetResourceReference(Shape.StrokeProperty, "Card");
        c.Children.Add(bandInner);

        // Concha esquerda (clicável)
        leftText = new TextBlock { Text = "E", FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        leftText.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        leftCup = new Border
        {
            Width = 52, Height = 84, CornerRadius = new CornerRadius(24, 14, 14, 24), BorderThickness = new Thickness(1.5),
            Child = leftText, Cursor = Cursors.Hand, ToolTip = "Clica para testar o lado esquerdo"
        };
        leftCup.SetResourceReference(Border.BackgroundProperty, "Hover");
        leftCup.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        leftCup.MouseLeftButtonDown += (_, _) => PlaySingle(AudioChannelMode.Left);
        Canvas.SetLeft(leftCup, 96); Canvas.SetTop(leftCup, 86);
        c.Children.Add(leftCup);

        // Concha direita (clicável)
        rightText = new TextBlock { Text = "D", FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        rightText.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        rightCup = new Border
        {
            Width = 52, Height = 84, CornerRadius = new CornerRadius(14, 24, 24, 14), BorderThickness = new Thickness(1.5),
            Child = rightText, Cursor = Cursors.Hand, ToolTip = "Clica para testar o lado direito"
        };
        rightCup.SetResourceReference(Border.BackgroundProperty, "Hover");
        rightCup.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        rightCup.MouseLeftButtonDown += (_, _) => PlaySingle(AudioChannelMode.Right);
        Canvas.SetLeft(rightCup, 292); Canvas.SetTop(rightCup, 86);
        c.Children.Add(rightCup);

        // Ondas sonoras (interiores)
        wL1 = MakeWave("M 158,108 A 26,26 0 0 1 158,148");
        wL2 = MakeWave("M 171,98 A 38,38 0 0 1 171,158");
        wR1 = MakeWave("M 282,108 A 26,26 0 0 0 282,148");
        wR2 = MakeWave("M 269,98 A 38,38 0 0 0 269,158");
        c.Children.Add(wL1); c.Children.Add(wL2);
        c.Children.Add(wR1); c.Children.Add(wR2);

        // Etiqueta central (clicável para testar Ambos)
        centerStatus = new TextBlock
        {
            Text = "CLICA NUM LADO", Width = 140, TextAlignment = TextAlignment.Center,
            FontSize = 11, FontWeight = FontWeights.Bold, Cursor = Cursors.Hand,
            ToolTip = "Clica para testar ambos os lados"
        };
        centerStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        centerStatus.MouseLeftButtonDown += (_, _) => PlaySingle(AudioChannelMode.Both);
        Canvas.SetLeft(centerStatus, 150); Canvas.SetTop(centerStatus, 120);
        c.Children.Add(centerStatus);

        return c;
    }

    private static Path MakeWave(string data)
    {
        var p = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0
        };
        p.SetResourceReference(Shape.StrokeProperty, "Accent2");
        return p;
    }

    private static Border AddVuMeter(Canvas c, double x, double y, string label)
    {
        var track = new Border { Width = 18, Height = 108, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), ClipToBounds = true };
        track.SetResourceReference(Border.BackgroundProperty, "Card");
        track.SetResourceReference(Border.BorderBrushProperty, "Line");
        var fill = new Border { Height = 0, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(8) };
        fill.SetResourceReference(Border.BackgroundProperty, "AccentGradient");
        track.Child = fill;
        Canvas.SetLeft(track, x); Canvas.SetTop(track, y);
        c.Children.Add(track);

        var lbl = new TextBlock { Text = label, Width = 18, TextAlignment = TextAlignment.Center, FontSize = 10.5, FontWeight = FontWeights.Bold };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Canvas.SetLeft(lbl, x); Canvas.SetTop(lbl, y + 114);
        c.Children.Add(lbl);
        return fill;
    }
}
