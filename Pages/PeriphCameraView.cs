using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FrameForge.Peripherals;

namespace FrameForge.Pages;

/// <summary>
/// Aba "Câmara": pré-visualização ao vivo da webcam (Windows Media Foundation), escolha de câmara e resolução,
/// FPS reais, verificação de iluminação/tampa fechada, espelho horizontal, grelha e captura de foto de teste.
/// A câmara desliga-se imediatamente ao sair da aba.
/// </summary>
internal sealed class PeriphCameraView : StackPanel, IPeriphView
{
    private readonly WebcamCapture _capture = new();
    private List<CameraDevice> _cameras = new();
    private List<CameraMode> _modes = new();
    private bool _streaming;
    private bool _updatingModes;
    private bool _mirror = true;
    private bool _showGrid;
    private WriteableBitmap? _bitmap;
    private int _frameBusy;
    private int _curW, _curH;
    private double _curFps, _curLuma;

    // Controlos superiores
    private readonly ComboBox _camCombo = new() { MinWidth = 230 };
    private readonly ComboBox _resCombo = new() { MinWidth = 180 };
    private readonly Button _startBtn;
    private readonly Button _mirrorBtn;
    private readonly Button _gridBtn;
    private readonly Button _snapBtn;

    // Palco de vídeo
    private readonly Image _videoImage = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScaleTransform _mirrorScale = new(-1, 1);
    private readonly Grid _gridOverlay = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _liveBadge;
    private readonly StackPanel _placeholder;
    private readonly TextBlock _placeholderTitle;
    private readonly TextBlock _placeholderSub;

    // Estatísticas & veredito
    private readonly (Border Box, TextBlock Value, TextBlock Hint) _sRes, _sFps, _sLight;
    private readonly (Border Box, TextBlock Glyph, TextBlock Text) _verdict;

    public PeriphCameraView()
    {
        _startBtn = PeriphUi.Btn("", "Ligar câmara", (_, _) => ToggleCamera(), "BtnAccent");
        _startBtn.Padding = new Thickness(18, 10, 20, 10);
        var refreshBtn = PeriphUi.Btn("", "Procurar", (_, _) => LoadCameras());

        var main = new StackPanel();
        main.Children.Add(PeriphUi.CardHead("", "Câmara / Webcam",
            "Testa a imagem ao vivo da webcam, a resolução, os FPS reais e a iluminação. A luz da câmara apaga-se assim que sais desta aba.",
            Ui.Row(refreshBtn, _startBtn)));

        // Seletores (câmara + resolução)
        var selRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        var camLbl = Ui.T("Câmara:", "Body");
        camLbl.VerticalAlignment = VerticalAlignment.Center;
        camLbl.Margin = new Thickness(0, 0, 10, 0);
        _camCombo.SelectionChanged += (_, _) => OnCameraChanged();
        var camGroup = Ui.Row(camLbl, _camCombo);
        camGroup.Margin = new Thickness(0, 0, 18, 8);
        selRow.Children.Add(camGroup);

        var resLbl = Ui.T("Resolução:", "Body");
        resLbl.VerticalAlignment = VerticalAlignment.Center;
        resLbl.Margin = new Thickness(0, 0, 10, 0);
        _resCombo.Items.Add(new ComboBoxItem { Content = "Automática (melhor)", Tag = (0, 0) });
        _resCombo.SelectedIndex = 0;
        _resCombo.SelectionChanged += (_, _) => OnResolutionChanged();
        var resGroup = Ui.Row(resLbl, _resCombo);
        resGroup.Margin = new Thickness(0, 0, 0, 8);
        selRow.Children.Add(resGroup);
        main.Children.Add(selRow);

        // Palco de pré-visualização
        _videoImage.RenderTransformOrigin = new Point(0.5, 0.5);
        _videoImage.RenderTransform = _mirrorScale;
        RenderOptions.SetBitmapScalingMode(_videoImage, BitmapScalingMode.Linear);

        BuildRuleOfThirdsGrid();

        // Selo "AO VIVO"
        var liveDot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        liveDot.SetResourceReference(Shape.FillProperty, "Bad");
        var liveTxt = new TextBlock { Text = "AO VIVO", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        _liveBadge = new Border
        {
            CornerRadius = new CornerRadius(99),
            Padding = new Thickness(10, 4, 12, 4),
            Margin = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x14, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = Ui.Row(liveDot, liveTxt),
            Visibility = Visibility.Collapsed
        };

        // Placeholder quando desligada
        var phIcon = PeriphUi.Icon("", 36, "Faint");
        phIcon.Margin = new Thickness(0, 0, 0, 10);
        _placeholderTitle = Ui.T("Câmara desligada", "H2", 16);
        _placeholderTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _placeholderSub = Ui.T("Carrega em Ligar câmara para ver a imagem ao vivo.", "MutedText", 13);
        _placeholderSub.HorizontalAlignment = HorizontalAlignment.Center;
        _placeholder = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _placeholder.Children.Add(phIcon);
        _placeholder.Children.Add(_placeholderTitle);
        _placeholder.Children.Add(_placeholderSub);

        var stage = new Grid { MinHeight = 340, MaxHeight = 460, ClipToBounds = true };
        stage.Children.Add(_videoImage);
        stage.Children.Add(_gridOverlay);
        stage.Children.Add(_placeholder);
        stage.Children.Add(_liveBadge);

        var stageFrame = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 0, 14),
            Child = stage
        };
        stageFrame.SetResourceReference(Border.BackgroundProperty, "Bg");
        stageFrame.SetResourceReference(Border.BorderBrushProperty, "Line");
        stage.SizeChanged += (_, e) => stage.Clip = new RectangleGeometry(new Rect(e.NewSize), 13, 13);
        main.Children.Add(stageFrame);

        // Barra de opções de visualização
        _mirrorBtn = PeriphUi.Btn("", "Espelhar: Ligado", (_, _) => ToggleMirror());
        _gridBtn = PeriphUi.Btn("", "Grelha: Desligada", (_, _) => ToggleGrid());
        _snapBtn = PeriphUi.Btn("", "Guardar foto de teste", (_, _) => SaveSnapshot());
        _snapBtn.IsEnabled = false;
        var privacyBtn = PeriphUi.Btn("", "Privacidade do Windows", (_, _) => Ui.Open("ms-settings:privacy-webcam"));

        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var b in new[] { _mirrorBtn, _gridBtn, _snapBtn, privacyBtn })
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            tools.Children.Add(b);
        }
        main.Children.Add(tools);

        // Estatísticas
        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -10, 6) };
        _sRes = PeriphUi.Stat("Resolução", "—", "aguardando vídeo");
        _sFps = PeriphUi.Stat("FPS reais", "—", "fotogramas/seg");
        _sLight = PeriphUi.Stat("Iluminação", "—", "brilho da imagem");
        stats.Children.Add(_sRes.Box);
        stats.Children.Add(_sFps.Box);
        stats.Children.Add(_sLight.Box);
        main.Children.Add(stats);

        _verdict = PeriphUi.Verdict("Escolhe a câmara e carrega em Ligar câmara. Nada é gravado nem enviado para fora do PC.");
        main.Children.Add(_verdict.Box);
        Children.Add(PeriphUi.Card(main));

        _capture.FrameReady += OnFrameReady;
        _capture.ModesDiscovered += OnModesDiscovered;
        _capture.ErrorOccurred += OnCaptureError;
    }

    // =========================================================== IPeriphView

    public void Activate()
    {
        if (_cameras.Count == 0) LoadCameras();
    }

    public void Deactivate()
    {
        StopCamera();
    }

    // =========================================================== Câmaras & Fluxo

    private async void LoadCameras()
    {
        string? prevLink = (_camCombo.SelectedItem as ComboBoxItem)?.Tag is CameraDevice cd ? cd.SymbolicLink : null;
        var list = await Task.Run(WebcamCapture.ListCameras);
        _cameras = list;

        _camCombo.Items.Clear();
        int sel = 0;
        for (int i = 0; i < _cameras.Count; i++)
        {
            _camCombo.Items.Add(new ComboBoxItem { Content = _cameras[i].Name, Tag = _cameras[i] });
            if (_cameras[i].SymbolicLink == prevLink) sel = i;
        }

        if (_cameras.Count > 0)
        {
            _camCombo.SelectedIndex = sel;
            _startBtn.IsEnabled = true;
            if (!_streaming)
            {
                _placeholderTitle.Text = $"{_cameras.Count} {(_cameras.Count == 1 ? "câmara pronta" : "câmaras prontas")}";
                _placeholderSub.Text = "Carrega em Ligar câmara para ver a pré-visualização ao vivo.";
                PeriphUi.SetVerdict(_verdict, "Info", "Câmara detetada. Carrega em Ligar câmara para testar imagem, resolução e FPS.");
            }
        }
        else
        {
            _startBtn.IsEnabled = false;
            _placeholderTitle.Text = "Nenhuma câmara encontrada";
            _placeholderSub.Text = "Liga uma webcam USB ou confirma se a câmara integrada está ativa no Windows.";
            PeriphUi.SetVerdict(_verdict, "Warn", "Nenhuma webcam detetada. Verifica a ligação USB ou as definições de privacidade do Windows.");
        }
    }

    private void ToggleCamera()
    {
        if (_streaming) StopCamera();
        else StartCamera();
    }

    private void StartCamera()
    {
        if (_camCombo.SelectedItem is not ComboBoxItem item || item.Tag is not CameraDevice dev)
        {
            LoadCameras();
            return;
        }
        (int w, int h) = (_resCombo.SelectedItem as ComboBoxItem)?.Tag is ValueTuple<int, int> r ? r : (0, 0);

        _streaming = true;
        _curW = 0; _curH = 0; _curFps = 0; _curLuma = 0;
        PeriphUi.SetBtn(_startBtn, "", "Parar câmara");
        _placeholderTitle.Text = "A ligar a câmara…";
        _placeholderSub.Text = dev.Name;
        _placeholder.Visibility = Visibility.Visible;
        _liveBadge.Visibility = Visibility.Collapsed;
        _snapBtn.IsEnabled = false;
        PeriphUi.SetVerdict(_verdict, "Info", $"A iniciar {dev.Name}…");
        _capture.Start(dev.SymbolicLink, w, h);
    }

    private void StopCamera()
    {
        _capture.Stop();
        _streaming = false;
        _videoImage.Source = null;
        _bitmap = null;
        _liveBadge.Visibility = Visibility.Collapsed;
        _placeholder.Visibility = Visibility.Visible;
        _placeholderTitle.Text = "Câmara desligada";
        _placeholderSub.Text = "Carrega em Ligar câmara para testar outra vez.";
        _snapBtn.IsEnabled = false;
        PeriphUi.SetBtn(_startBtn, "", "Ligar câmara");
        _sRes.Value.Text = "—"; _sRes.Hint.Text = "desligada";
        _sFps.Value.Text = "—"; _sFps.Hint.Text = "fotogramas/seg";
        _sLight.Value.Text = "—"; _sLight.Hint.Text = "brilho da imagem";
    }

    private void OnCameraChanged()
    {
        if (!_streaming)
        {
            ResetResolutionList();
            return;
        }
        ResetResolutionList();
        StartCamera();
    }

    private void OnResolutionChanged()
    {
        if (_updatingModes || !_streaming) return;
        StartCamera();
    }

    private void ResetResolutionList()
    {
        _updatingModes = true;
        try
        {
            _modes.Clear();
            _resCombo.Items.Clear();
            _resCombo.Items.Add(new ComboBoxItem { Content = "Automática (melhor)", Tag = (0, 0) });
            _resCombo.SelectedIndex = 0;
        }
        finally { _updatingModes = false; }
    }

    private void OnModesDiscovered(List<CameraMode> modes)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_streaming || modes.Count == 0) return;
            if (_modes.Count == modes.Count && _modes.Select(m => (m.Width, m.Height)).SequenceEqual(modes.Select(m => (m.Width, m.Height)))) return;

            _updatingModes = true;
            try
            {
                var currentTag = (_resCombo.SelectedItem as ComboBoxItem)?.Tag is ValueTuple<int, int> t ? t : (0, 0);
                _modes = modes;
                _resCombo.Items.Clear();
                _resCombo.Items.Add(new ComboBoxItem { Content = "Automática (melhor)", Tag = (0, 0) });
                int selIdx = 0;
                for (int i = 0; i < modes.Count; i++)
                {
                    var m = modes[i];
                    _resCombo.Items.Add(new ComboBoxItem { Content = m.Label, Tag = (m.Width, m.Height) });
                    if (currentTag == (m.Width, m.Height)) selIdx = i + 1;
                }
                _resCombo.SelectedIndex = selIdx;
            }
            finally { _updatingModes = false; }
        });
    }

    private void OnFrameReady(int width, int height, byte[] bgra, double fps, double avgLuma)
    {
        // Evita empilhar fotogramas no UI thread se o ecrã estiver ocupado
        if (Interlocked.Exchange(ref _frameBusy, 1) != 0) return;

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (!_streaming) return;

                if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
                {
                    _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                    _videoImage.Source = _bitmap;
                }

                _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);

                if (_placeholder.Visibility == Visibility.Visible)
                {
                    _placeholder.Visibility = Visibility.Collapsed;
                    _liveBadge.Visibility = Visibility.Visible;
                    _snapBtn.IsEnabled = true;
                }

                _curW = width; _curH = height; _curFps = fps; _curLuma = avgLuma;
                UpdateMetrics();
            }
            catch { }
            finally
            {
                Volatile.Write(ref _frameBusy, 0);
            }
        });
    }

    private void OnCaptureError(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StopCamera();
            _placeholderTitle.Text = "Não foi possível abrir a câmara";
            _placeholderSub.Text = message;
            PeriphUi.SetVerdict(_verdict, "Bad", message);
        });
    }

    private void UpdateMetrics()
    {
        _sRes.Value.Text = $"{_curW} × {_curH}";
        _sRes.Hint.Text = _curH >= 1080 ? "Full HD ou superior" : _curH >= 720 ? "HD (720p)" : "definição padrão";

        _sFps.Value.Text = _curFps >= 1 ? $"{_curFps:0.0}" : "…";
        _sFps.Hint.Text = "fotogramas/seg";

        string lightText, lightHint;
        if (_curLuma < 8) { lightText = "Preta"; lightHint = "tampa fechada ou sem luz"; }
        else if (_curLuma < 45) { lightText = "Escura"; lightHint = "pouca luz ambiente"; }
        else if (_curLuma > 225) { lightText = "Muito alta"; lightHint = "imagem sobre-exposta"; }
        else { lightText = "Boa"; lightHint = $"brilho médio {_curLuma * 100 / 255:0}%"; }
        _sLight.Value.Text = lightText;
        _sLight.Hint.Text = lightHint;

        if (_curLuma < 8)
            PeriphUi.SetVerdict(_verdict, "Warn", "A câmara está ligada mas a imagem está toda preta. Verifica se a tampa física de privacidade está fechada ou se há um botão de câmara no teclado.");
        else if (_curFps >= 1 && _curFps < 14)
            PeriphUi.SetVerdict(_verdict, "Warn", $"Imagem recebida a {_curFps:0.0} FPS (lenta). Pouca luz ambiente ou resolução demasiado alta pode baixar os FPS.");
        else if (_curFps >= 14)
            PeriphUi.SetVerdict(_verdict, "Good", $"Câmara a funcionar bem ({_curW} × {_curH} a {_curFps:0.0} FPS).");
    }

    // =========================================================== Ferramentas (espelho, grelha, foto)

    private void ToggleMirror()
    {
        _mirror = !_mirror;
        _mirrorScale.ScaleX = _mirror ? -1 : 1;
        PeriphUi.SetBtn(_mirrorBtn, "", _mirror ? "Espelhar: Ligado" : "Espelhar: Desligado");
    }

    private void ToggleGrid()
    {
        _showGrid = !_showGrid;
        _gridOverlay.Visibility = _showGrid ? Visibility.Visible : Visibility.Collapsed;
        PeriphUi.SetBtn(_gridBtn, "", _showGrid ? "Grelha: Ligada" : "Grelha: Desligada");
    }

    private void SaveSnapshot()
    {
        if (_bitmap == null) return;
        try
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string file = System.IO.Path.Combine(dir, $"HyperCore_Camara_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            BitmapSource src = _bitmap;
            if (_mirror) src = new TransformedBitmap(src, new ScaleTransform(-1, 1));

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            using (var fs = File.Create(file)) encoder.Save(fs);

            PeriphUi.SetVerdict(_verdict, "Good", $"Foto guardada em: {file}");
        }
        catch (Exception ex)
        {
            PeriphUi.SetVerdict(_verdict, "Bad", $"Não foi possível guardar a foto: {ex.Message}");
        }
    }

    private void BuildRuleOfThirdsGrid()
    {
        for (int i = 0; i < 3; i++)
        {
            _gridOverlay.ColumnDefinitions.Add(new ColumnDefinition());
            _gridOverlay.RowDefinitions.Add(new RowDefinition());
        }
        var stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        for (int c = 1; c <= 2; c++)
        {
            var b = new Border { BorderBrush = stroke, BorderThickness = new Thickness(1, 0, 0, 0) };
            Grid.SetColumn(b, c); Grid.SetRowSpan(b, 3);
            _gridOverlay.Children.Add(b);
        }
        for (int r = 1; r <= 2; r++)
        {
            var b = new Border { BorderBrush = stroke, BorderThickness = new Thickness(0, 1, 0, 0) };
            Grid.SetRow(b, r); Grid.SetColumnSpan(b, 3);
            _gridOverlay.Children.Add(b);
        }
    }
}
