using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameForge.Overlay;

namespace FrameForge.Pages;

/// <summary>
/// Editor de overlay com liberdade total (ideia do FPS Monitor): qualquer sensor, qualquer posição, fonte, tamanho, cor,
/// fundo, barras, gráficos, regras de cor e alertas. A pré-visualização usa o mesmo desenhador do overlay real.
/// </summary>
public sealed class OverlayPage : UserControl
{
    private readonly ComboBox _profiles = new() { MinWidth = 0 };
    private readonly ListBox _itemsList = new() { Height = 240 };
    private readonly Canvas _canvas = new() { Width = OverlayRenderer.DesignWidth, Height = 1080, ClipToBounds = true };
    private readonly StackPanel _props = new();
    private readonly Rectangle _sel = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF)), StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
    private readonly Rectangle _groupSel = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x38, 0xD3, 0x99)), StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 8, 4 }, Fill = Brushes.Transparent, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly List<OverlayVisual> _visuals = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _saveLater = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _previewRebuildLater = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _selectedRefreshLater = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _previewRebuildPending, _selectedRefreshPending, _scaleRefreshPending;
    private double _pendingScale;
    private Point _previewPanStart;
    private double _previewPanHorizontal, _previewPanVertical;
    private bool _panningPreview;
    private OverlayItem? _selected;
    private Point _dragStart;
    private (double X, double Y) _dragOrig;
    private (double X, double Y) _overlayDragOrig;
    private bool _dragging;
    private bool _movingOverlay;
    private bool _moveOverlayMode;
    private bool _loadingProfiles;
    private List<SourceOption>? _sources;
    private bool _sensorSourcesLoaded;
    private Button? _moveOverlayButton;
    private CheckBox? _overlayHotkeyEnabled;
    private Button? _overlayHotkeyCapture;
    private TextBlock? _overlayHotkeyStatus;
    private bool _capturingHotkey;
    private TextBox? _positionXInput, _positionYInput;

    private OverlayProfile P => OverlayStore.Active;

    public OverlayPage()
    {
        Ui.UseTheme(this);
        _saveLater.Tick += (_, _) => { _saveLater.Stop(); OverlayStore.Save(); };
        _previewRebuildLater.Tick += (_, _) =>
        {
            _previewRebuildLater.Stop();
            if (!_previewRebuildPending) return;
            _previewRebuildPending = false;
            if (_selected != null && !_movingOverlay)
                RefreshSelectedVisual();
            else
                RebuildVisuals();
        };
        _selectedRefreshLater.Tick += (_, _) =>
        {
            _selectedRefreshLater.Stop();
            if (!_selectedRefreshPending) return;
            _selectedRefreshPending = false;
            RefreshSelectedVisual();
        };
        _tick.Tick += (_, _) =>
        {
            if (!_sensorSourcesLoaded && Sensors.SensorHub.Instance.Ready)
            {
                _sensorSourcesLoaded = true;
                if (_sources != null)
                {
                    _sources = OverlayValues.All();
                    if (_selected != null) BuildProps();
                }
            }
            OverlayRenderer.UpdateAll(_visuals);
            UpdateSelection();
        };
        Loaded += (_, _) => { _tick.Start(); };
        Unloaded += (_, _) =>
        {
            _tick.Stop();
            _saveLater.Stop();
            _previewRebuildLater.Stop();
            _selectedRefreshLater.Stop();
        };
        Content = BuildLayout();
        LoadProfiles();
        RebuildPreview();
        Core.AppServices.Sampler.Start();
        Sensors.SensorHub.Instance.Start();
    }

    private void SaveSoon() { _saveLater.Stop(); _saveLater.Start(); }

    // ================================================================== layout

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(14, 4, 14, 8), ClipToBounds = true };
        Window? layoutWindow = null;
        EventHandler? stateChanged = null;
        void UpdateBottomMargin() =>
            root.Margin = new Thickness(14, 4, 14, layoutWindow?.WindowState == WindowState.Maximized ? 32 : 8);
        Loaded += (_, _) =>
        {
            layoutWindow = Window.GetWindow(this);
            UpdateBottomMargin();
            if (layoutWindow != null && stateChanged == null)
            {
                stateChanged = (_, _) => UpdateBottomMargin();
                layoutWindow.StateChanged += stateChanged;
            }
        };
        Unloaded += (_, _) =>
        {
            if (layoutWindow != null && stateChanged != null)
                layoutWindow.StateChanged -= stateChanged;
            layoutWindow = null;
            stateChanged = null;
        };

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 3) };
        DockPanel.SetDock(head, Dock.Top);
        var top = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Ui.Btn("👁 Mostrar no ecrã", (_, _) => FrameForge.OverlayWindow.ShowFor(0), "BtnAccent"));
        actions.Children.Add(Ui.Btn("Esconder", (_, _) => FrameForge.OverlayWindow.HideOverlay()));
        DockPanel.SetDock(actions, Dock.Right);
        top.Children.Add(actions);
        top.Children.Add(Ui.T("Overlay (editor)", "H1"));
        head.Children.Add(top);
        head.Children.Add(Ui.T("Arrasta os elementos na pré-visualização e seleciona-os para editar. Escolhe abaixo a combinação de teclas que mostra e esconde o overlay no jogo.", "MutedText"));
        var auto = new CheckBox { Content = "Mostrar automaticamente quando um jogo estiver em primeiro plano", IsChecked = OverlayStore.Data.AutoShow, Margin = new Thickness(0, 2, 0, 0) };
        auto.Click += (_, _) => { OverlayStore.Data.AutoShow = auto.IsChecked == true; OverlayStore.Save(); FrameForge.OverlayWindow.StartAutoMode(); };
        head.Children.Add(auto);
        var autoOnGame = new CheckBox { Content = "Mostrar automaticamente enquanto o processo de um jogo estiver aberto", IsChecked = OverlayStore.Data.AutoShowWhenGameOpen, Margin = new Thickness(0, 2, 0, 0) };
        autoOnGame.Click += (_, _) => { OverlayStore.Data.AutoShowWhenGameOpen = autoOnGame.IsChecked == true; OverlayStore.Save(); FrameForge.OverlayWindow.StartAutoMode(); };
        head.Children.Add(autoOnGame);
        head.Children.Add(Ui.T("A deteção usa processos de jogos instalados ou configurados, sem esperar por frames.", "MutedText"));
        head.Children.Add(OverlayHotkeyBar());
        head.Children.Add(SizeBar());
        root.Children.Add(head);

        // ---- esquerda
        var left = new StackPanel { Width = 270, Margin = new Thickness(0, 0, 0, 6) };
        left.Children.Add(Ui.T("OVERLAY EM USO", "Label"));
        _profiles.Margin = new Thickness(0, 5, 0, 6);
        _profiles.SelectionChanged += (_, _) =>
        {
            if (_loadingProfiles || _profiles.SelectedItem is not string name) return;
            OverlayStore.Data.Active = name;
            OverlayStore.Save();
            _selected = null;
            RebuildPreview();
        };
        left.Children.Add(_profiles);
        var rename = new TextBox { ToolTip = "Nome deste overlay (Enter para guardar)" };
        rename.KeyDown += (_, k) =>
        {
            if (k.Key != Key.Enter) return;
            string n = rename.Text.Trim();
            if (n.Length == 0 || OverlayStore.Data.Profiles.Any(p => p.Name == n)) return;
            P.Name = n; OverlayStore.Data.Active = n; OverlayStore.Save(); LoadProfiles();
        };
        left.Children.Add(rename);
        var pr = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        pr.Children.Add(Small("Novo", () => AddProfile(new OverlayProfile { Name = UniqueProfile("Novo overlay") })));
        pr.Children.Add(Small("Duplicar", () =>
        {
            var json = System.Text.Json.JsonSerializer.Serialize(P, OverlayStore.Json);
            var copy = System.Text.Json.JsonSerializer.Deserialize<OverlayProfile>(json, OverlayStore.Json)!;
            copy.Name = UniqueProfile(P.Name + " (cópia)");
            AddProfile(copy);
        }));
        pr.Children.Add(Small("Apagar", () =>
        {
            if (OverlayStore.Data.Profiles.Count <= 1) return;
            OverlayStore.Data.Profiles.Remove(P);
            OverlayStore.Data.Active = OverlayStore.Data.Profiles[0].Name;
            OverlayStore.Save(); LoadProfiles(); RebuildPreview();
        }));
        left.Children.Add(pr);

        left.Children.Add(Gap(Ui.T("MODELOS PRONTOS", "Label")));
        var presets = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var preset in OverlayStore.Presets())
        {
            var p = preset;
            presets.Children.Add(Small(p.Name, () => { p.Name = UniqueProfile(p.Name); AddProfile(p); }));
        }
        left.Children.Add(presets);

        left.Children.Add(Gap(Ui.T("POSIÇÃO DO OVERLAY INTEIRO", "Label")));
        left.Children.Add(Ui.T("Seleciona o overlay inteiro e arrasta-o com o rato. As coordenadas não têm limite fixo.", "MutedText", 12));
        _moveOverlayButton = Small("Mover overlay inteiro", ToggleOverlayMove);
        left.Children.Add(_moveOverlayButton);
        left.Children.Add(Ui.T("POSIÇÃO AUTOMÁTICA", "Label"));
        left.Children.Add(AlignmentPresets());
        left.Children.Add(PositionRow("Deslocamento horizontal (px)", true));
        left.Children.Add(PositionRow("Deslocamento vertical (px)", false));
        var resetPosition = Small("Repor posição", () => SetOverlayPosition(0, 0));
        resetPosition.Width = 150;
        resetPosition.HorizontalAlignment = HorizontalAlignment.Left;
        resetPosition.Margin = new Thickness(0, 8, 6, 6);
        left.Children.Add(resetPosition);

        left.Children.Add(Gap(Ui.T("ADICIONAR ELEMENTO", "Label")));
        var add = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        add.Children.Add(Small("＋ Valor", () => AddItem(new OverlayItem { Kind = ItemKind.Value, SensorId = "v/fps", Label = "FPS" })));
        add.Children.Add(Small("＋ Barra", () => AddItem(new OverlayItem { Kind = ItemKind.Bar, SensorId = "v/gpu_load", Label = "GPU", Unit = "%", Height = 12, FontSize = 14 })));
        add.Children.Add(Small("＋ Gráfico", () => AddItem(new OverlayItem { Kind = ItemKind.Graph, SensorId = "v/frametime", Label = "frametime", Unit = "ms", Width = 260, Height = 50, Max = 30, AutoScale = true, Background = "#30FFFFFF", FontSize = 13, Decimals = 1 })));
        add.Children.Add(Small("＋ Texto", () => AddItem(new OverlayItem { Kind = ItemKind.Text, Label = "O meu texto", FontSize = 16 })));
        add.Children.Add(Small("＋ Caixa de fundo", () => AddItem(new OverlayItem { Kind = ItemKind.Box, Width = 260, Height = 140, Background = "#A00B0D12", CornerRadius = 10, Shadow = false })));
        add.Children.Add(Small("＋ Relógio", () => AddItem(new OverlayItem { Kind = ItemKind.Clock, FontSize = 16 })));
        add.Children.Add(Small("＋ Nome do jogo", () => AddItem(new OverlayItem { Kind = ItemKind.Game, FontSize = 14 })));
        left.Children.Add(add);

        left.Children.Add(Gap(Ui.T("ELEMENTOS (de trás para a frente)", "Label")));
        _itemsList.Margin = new Thickness(0, 5, 0, 6);
        _itemsList.SelectionChanged += (_, _) => { if (_itemsList.SelectedItem is ListBoxItem { Tag: OverlayItem it } && it != _selected) Select(it); };
        left.Children.Add(_itemsList);
        var order = new WrapPanel();
        order.Children.Add(Small("▲ Frente", () => Move(1)));
        order.Children.Add(Small("▼ Trás", () => Move(-1)));
        order.Children.Add(Small("Duplicar", () => { if (_selected != null) { var c = _selected.Clone(); c.X += 20; c.Y += 20; AddItem(c); } }));
        order.Children.Add(Small("Apagar", () => { if (_selected != null) { P.Items.Remove(_selected); _selected = null; SaveSoon(); RebuildPreview(); } }));
        left.Children.Add(order);

        left.Children.Add(Gap(Ui.T("DEFINIÇÕES DESTE OVERLAY", "Label")));
        left.Children.Add(NumberRow("Atualizar a cada (ms)", () => P.UpdateMs, v => { P.UpdateMs = (int)v; _tick.Interval = TimeSpan.FromMilliseconds(v); SaveSoon(); }, 100, 2000, 50));
        left.Children.Add(Gap(Ui.T("MONITOR DO OVERLAY", "Label")));
        var monitor = new ComboBox { MinWidth = 0, Margin = new Thickness(0, 4, 0, 6), ToolTip = "Escolhe seguir o jogo ou fixar o overlay num monitor." };
        var followGame = new ComboBoxItem { Content = "Seguir o monitor do jogo", Tag = "" };
        monitor.Items.Add(followGame);
        ComboBoxItem? selectedMonitor = P.OnGameMonitor && string.IsNullOrWhiteSpace(P.TargetMonitorDeviceName) ? followGame : null;
        var displays = FrameForge.OverlayWindow.GetAvailableMonitors();
        foreach (var display in displays)
        {
            var item = new ComboBoxItem { Content = display.Label, Tag = display.DeviceName };
            monitor.Items.Add(item);
            if (string.Equals(P.TargetMonitorDeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrWhiteSpace(P.TargetMonitorDeviceName) && !P.OnGameMonitor && display.IsPrimary))
                selectedMonitor = item;
        }
        if (!string.IsNullOrWhiteSpace(P.TargetMonitorDeviceName) &&
            !displays.Any(d => string.Equals(d.DeviceName, P.TargetMonitorDeviceName, StringComparison.OrdinalIgnoreCase)))
        {
            var unavailable = new ComboBoxItem { Content = $"{P.TargetMonitorDeviceName} (indisponível)", Tag = P.TargetMonitorDeviceName };
            monitor.Items.Add(unavailable);
            selectedMonitor = unavailable;
        }
        monitor.SelectedItem = selectedMonitor ?? followGame;
        monitor.SelectionChanged += (_, _) =>
        {
            if (monitor.SelectedItem is not ComboBoxItem { Tag: string deviceName }) return;
            if (deviceName.Length == 0)
            {
                P.OnGameMonitor = true;
                P.TargetMonitorDeviceName = null;
            }
            else
            {
                P.OnGameMonitor = false;
                P.TargetMonitorDeviceName = deviceName;
            }
            OverlayStore.Save();
        };
        left.Children.Add(monitor);

        var leftScroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 12, 0) };

        // ---- direita (propriedades)
        var rightScroll = new ScrollViewer
        {
            Content = new Border { Child = _props, Padding = new Thickness(0, 0, 0, 16) },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Width = 380,
            Margin = new Thickness(12, 0, 0, 0)
        };

        // ---- centro (pré-visualização)
        var bg = new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x3A), Color.FromRgb(0x3A, 0x2F, 0x22), 30);
        _canvas.Background = bg;
        for (int x = 0; x <= 1920; x += 120) _canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = 1080, Stroke = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)) });
        for (int y = 0; y <= 1080; y += 120) _canvas.Children.Add(new Line { X1 = 0, X2 = 1920, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)) });
        var hint = new TextBlock { Text = "pré-visualização do ecrã do jogo (1920 de largura — o overlay real escala para a tua resolução)", Foreground = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), FontSize = 26 };
        Canvas.SetLeft(hint, 40); Canvas.SetTop(hint, 1020);
        _canvas.Children.Add(hint);
        _canvas.MouseLeftButtonDown += Canvas_Down;
        _canvas.MouseMove += Canvas_Move;
        _canvas.MouseLeftButtonUp += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            _movingOverlay = false;
            _canvas.ReleaseMouseCapture();
            _canvas.Cursor = _moveOverlayMode ? Cursors.SizeAll : Cursors.Arrow;
            SaveSoon();
            BuildProps();
        };
        // A superfície tem dimensões explícitas para que a rolagem corresponda exatamente à pré-visualização ampliada.
        var previewSurface = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true
        };
        _canvas.HorizontalAlignment = HorizontalAlignment.Left;
        _canvas.VerticalAlignment = VerticalAlignment.Top;
        _canvas.RenderTransformOrigin = new Point(0, 0);
        previewSurface.Children.Add(_canvas);
        var scroll = new ScrollViewer
        {
            Content = previewSurface,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Cursor = Cursors.ScrollAll
        };
        scroll.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            _panningPreview = true;
            _previewPanStart = e.GetPosition(scroll);
            _previewPanHorizontal = scroll.HorizontalOffset;
            _previewPanVertical = scroll.VerticalOffset;
            scroll.CaptureMouse();
            e.Handled = true;
        };
        scroll.PreviewMouseMove += (_, e) =>
        {
            if (!_panningPreview) return;
            var current = e.GetPosition(scroll);
            scroll.ScrollToHorizontalOffset(_previewPanHorizontal + _previewPanStart.X - current.X);
            scroll.ScrollToVerticalOffset(_previewPanVertical + _previewPanStart.Y - current.Y);
            e.Handled = true;
        };
        scroll.PreviewMouseUp += (_, e) =>
        {
            if (!_panningPreview || e.ChangedButton != MouseButton.Middle) return;
            _panningPreview = false;
            scroll.ReleaseMouseCapture();
            e.Handled = true;
        };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        var horizontalPan = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            Minimum = 0,
            Maximum = 0,
            ViewportSize = 0,
            SmallChange = 40,
            LargeChange = 200,
            Height = 10,
            Margin = new Thickness(12, 4, 12, 4),
            ToolTip = "Arrasta para mover a pré-visualização da esquerda para a direita"
        };
        double previewOverflow = 0;
        horizontalPan.ValueChanged += (_, _) => scroll.ScrollToHorizontalOffset(horizontalPan.Value);
        var zoom = new Slider { Minimum = 1, Maximum = 4, Value = 2, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var zoomText = Ui.T("", "MutedText", 12);
        void ApplyZoom()
        {
            double w = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
            double h = scroll.ViewportHeight > 0 ? scroll.ViewportHeight : scroll.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double horizontalPosition = previewOverflow > 0 ? scroll.HorizontalOffset / previewOverflow : 0;
            double fitScale = Math.Min(w / _canvas.Width, h / _canvas.Height) * 0.98;
            double scale = fitScale * zoom.Value;
            _canvas.RenderTransform = new ScaleTransform(scale, scale);
            previewSurface.Width = _canvas.Width * scale;
            previewSurface.Height = _canvas.Height * scale;
            Dispatcher.BeginInvoke(() =>
            {
                previewOverflow = Math.Max(0, scroll.ExtentWidth - scroll.ViewportWidth);
                horizontalPan.Maximum = scroll.ExtentWidth;
                horizontalPan.ViewportSize = scroll.ViewportWidth;
                horizontalPan.Value = previewOverflow * horizontalPosition;
                scroll.ScrollToHorizontalOffset(horizontalPan.Value);
            }, DispatcherPriority.Loaded);
            zoomText.Text = zoom.Value <= 1.05
                ? "ecrã inteiro (sem cortes)"
                : $"{zoom.Value:0.0}× — usa o slider embaixo para mover para os lados e a barra vertical para cima/baixo";
        }
        zoom.ValueChanged += (_, _) => ApplyZoom();
        scroll.SizeChanged += (_, _) => ApplyZoom();
        scroll.ScrollChanged += (_, e) =>
        {
            previewOverflow = Math.Max(0, e.ExtentWidth - e.ViewportWidth);
            horizontalPan.Maximum = e.ExtentWidth;
            horizontalPan.ViewportSize = e.ViewportWidth;
            horizontalPan.Value = e.HorizontalOffset;
        };
        Loaded += (_, _) => Dispatcher.BeginInvoke(ApplyZoom, DispatcherPriority.Loaded);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(Ui.T("ZOOM DA PRÉ-VISUALIZAÇÃO  ", "Label"));
        bar.Children.Add(zoom);
        zoomText.Margin = new Thickness(10, 0, 0, 0);
        zoomText.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(zoomText);
        var center = new Grid { ClipToBounds = true };
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(bar, 0);
        center.Children.Add(bar);
        var previewFrame = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderBrush = Ui.Res("Line"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true
        };
        var previewLayout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(horizontalPan, Dock.Bottom);
        var panTrack = new Border
        {
            Background = Ui.Res("Card"),
            BorderBrush = Ui.Res("Line"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            MinHeight = 48,
            Child = horizontalPan
        };
        DockPanel.SetDock(panTrack, Dock.Bottom);
        previewLayout.Children.Add(panTrack);
        previewLayout.Children.Add(scroll);
        previewFrame.Child = previewLayout;
        Grid.SetRow(previewFrame, 1);
        center.Children.Add(previewFrame);

        var body = new Grid { ClipToBounds = true };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(leftScroll, 0);
        Grid.SetColumn(center, 1);
        Grid.SetColumn(rightScroll, 2);
        body.Children.Add(leftScroll);
        body.Children.Add(center);
        body.Children.Add(rightScroll);
        root.Children.Add(body);
        return root;
    }

    private FrameworkElement OverlayHotkeyBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        _overlayHotkeyEnabled = new CheckBox { Content = "Atalho para mostrar/esconder overlay", IsChecked = OverlayStore.Data.HotkeyEnabled, VerticalAlignment = VerticalAlignment.Center };
        _overlayHotkeyEnabled.Click += (_, _) =>
        {
            OverlayStore.Data.HotkeyEnabled = _overlayHotkeyEnabled.IsChecked == true;
            OverlayStore.Save();
            RefreshOverlayHotkeyStatus();
        };
        row.Children.Add(_overlayHotkeyEnabled);
        _overlayHotkeyCapture = Ui.Btn("", (_, _) => CaptureOverlayHotkey(), "Btn");
        _overlayHotkeyCapture.MinWidth = 150;
        _overlayHotkeyCapture.Margin = new Thickness(10, 0, 8, 0);
        row.Children.Add(_overlayHotkeyCapture);
        _overlayHotkeyStatus = Ui.T("", "MutedText", 11);
        _overlayHotkeyStatus.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_overlayHotkeyStatus);
        RefreshOverlayHotkeyStatus();
        return row;
    }

    private void RefreshOverlayHotkeyStatus()
    {
        if (_overlayHotkeyCapture == null || _overlayHotkeyStatus == null) return;
        var settings = OverlayStore.Data;
        _overlayHotkeyCapture.Content = FormatOverlayHotkey(settings.HotkeyModifiers, settings.HotkeyVirtualKey);
        _overlayHotkeyCapture.IsEnabled = settings.HotkeyEnabled;
        _overlayHotkeyStatus.Text = settings.HotkeyEnabled
            ? "Clica no botão e pressiona a combinação desejada."
            : "Atalho desligado.";
    }

    private async void CaptureOverlayHotkey()
    {
        if (_overlayHotkeyCapture == null || _capturingHotkey) return;
        _capturingHotkey = true;
        _overlayHotkeyCapture.Content = "Pressiona as teclas…";
        _overlayHotkeyStatus!.Text = "Esc cancela. Tem de incluir Ctrl, Alt, Shift ou Win.";
        _overlayHotkeyCapture.Focusable = true;
        _overlayHotkeyCapture.Focus();
        var captured = new TaskCompletionSource<(uint Modifiers, uint Key)?>();
        KeyEventHandler handler = (_, e) =>
        {
            Key pressed = e.Key == Key.System ? e.SystemKey : e.Key;
            if (pressed == Key.Escape)
            {
                captured.TrySetResult(null);
                e.Handled = true;
                return;
            }
            if (pressed is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            uint modifiers = 0;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= 0x0002;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= 0x0001;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= 0x0004;
            if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers |= 0x0008;
            if (modifiers != 0) captured.TrySetResult((modifiers, (uint)KeyInterop.VirtualKeyFromKey(pressed)));
            e.Handled = true;
        };
        _overlayHotkeyCapture.PreviewKeyDown += handler;
        var result = await captured.Task;
        _overlayHotkeyCapture.PreviewKeyDown -= handler;
        _capturingHotkey = false;
        if (result is { } hotkey)
        {
            OverlayStore.Data.HotkeyModifiers = hotkey.Modifiers;
            OverlayStore.Data.HotkeyVirtualKey = hotkey.Key;
            OverlayStore.Data.HotkeyEnabled = true;
            OverlayStore.Save();
        }
        RefreshOverlayHotkeyStatus();
    }

    private static string FormatOverlayHotkey(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((modifiers & 0x0008) != 0) parts.Add("Win");
        string keyName = KeyInterop.KeyFromVirtualKey((int)key).ToString();
        return string.Join(" + ", parts.Append(keyName));
    }

    private Slider? _size;

    /// <summary>Tamanho do overlay inteiro (todos os elementos e distâncias juntos). No jogo: Ctrl+Alt+ + / −.</summary>
    private FrameworkElement SizeBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var title = Ui.T("TAMANHO DO OVERLAY  ", "Label");
        title.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(title);
        _size = new Slider { Minimum = 0.5, Maximum = 2.5, Value = P.Scale, Width = 260, SmallChange = 0.05, LargeChange = 0.1, IsSnapToTickEnabled = true, TickFrequency = 0.05, VerticalAlignment = VerticalAlignment.Center };
        var pct = Ui.T($"{P.Scale:P0}", "MutedText", 13);
        pct.VerticalAlignment = VerticalAlignment.Center;
        pct.Margin = new Thickness(10, 0, 14, 0);
        pct.MinWidth = 44;
        _size.ValueChanged += (_, e) =>
        {
            pct.Text = $"{e.NewValue:P0}";
            if (Math.Abs(P.Scale - e.NewValue) < 0.001) return;
            P.Scale = Math.Round(e.NewValue, 2);
            _pendingScale = P.Scale;
            foreach (var visual in _visuals) OverlayRenderer.SetScale(visual, P.Scale);
            SaveSoon();
            ScheduleScaleRefresh();
        };
        row.Children.Add(_size);
        row.Children.Add(pct);
        row.Children.Add(Small("100%", () => _size.Value = 1));
        row.Children.Add(Ui.T("No jogo: Ctrl+Alt + (maior) e Ctrl+Alt − (menor)", "MutedText", 12));
        ((FrameworkElement)row.Children[^1]).VerticalAlignment = VerticalAlignment.Center;
        OverlayStore.Changed += () => Dispatcher.BeginInvoke(() => { if (_size != null && Math.Abs(_size.Value - P.Scale) > 0.001) _size.Value = P.Scale; });
        return row;
    }

    private static FrameworkElement Gap(FrameworkElement e) { e.Margin = new Thickness(0, 16, 0, 0); return e; }

    private static Button Small(string text, Action a)
    {
        var b = Ui.Btn(text, (_, _) => a());
        b.Padding = new Thickness(10, 6, 10, 6);
        b.Margin = new Thickness(0, 0, 6, 6);
        b.FontSize = 12;
        return b;
    }

    private static string UniqueProfile(string name)
    {
        string n = name; int i = 2;
        while (OverlayStore.Data.Profiles.Any(p => p.Name == n)) n = $"{name} {i++}";
        return n;
    }

    private void AddProfile(OverlayProfile p)
    {
        OverlayStore.Data.Profiles.Add(p);
        OverlayStore.Data.Active = p.Name;
        OverlayStore.Save();
        _selected = null;
        LoadProfiles();
        RebuildPreview();
    }

    private void LoadProfiles()
    {
        _loadingProfiles = true;
        _profiles.ItemsSource = OverlayStore.Data.Profiles.Select(p => p.Name).ToList();
        _profiles.SelectedItem = P.Name;
        _loadingProfiles = false;
    }

    private void AddItem(OverlayItem it)
    {
        if (it.Kind != ItemKind.Box) { it.X = 60; it.Y = 60 + P.Items.Count * 8; }
        P.Items.Add(it);
        SaveSoon();
        RebuildPreview();
        Select(it);
    }

    private void Move(int dir)
    {
        if (_selected == null) return;
        int i = P.Items.IndexOf(_selected), j = Math.Clamp(i + dir, 0, P.Items.Count - 1);
        if (i == j) return;
        P.Items.RemoveAt(i);
        P.Items.Insert(j, _selected);
        SaveSoon();
        RebuildPreview();
    }

    // ================================================================== pré-visualização

    private void RebuildPreview()
    {
        _previewRebuildPending = false;
        _previewRebuildLater.Stop();
        RebuildVisuals();
        RefreshList();
        BuildProps();
        Dispatcher.BeginInvoke(UpdateSelection, DispatcherPriority.Loaded);
    }

    private void RebuildVisuals()
    {
        foreach (var v in _visuals) _canvas.Children.Remove(v.Root);
        _canvas.Children.Remove(_sel);
        _canvas.Children.Remove(_groupSel);
        _visuals.Clear();
        foreach (var it in P.Items)
        {
            var v = OverlayRenderer.Build(it, P.Scale);
            _visuals.Add(v);
            _canvas.Children.Add(v.Root);
            PlacePreview(v);
        }
        _canvas.Children.Add(_groupSel);
        _canvas.Children.Add(_sel);
        OverlayRenderer.UpdateAll(_visuals);
        UpdateSelection();
    }

    private void SchedulePreviewRebuild()
    {
        if (_previewRebuildPending) return;
        _previewRebuildPending = true;
        _previewRebuildLater.Start();
    }

    private void ScheduleScaleRefresh()
    {
        if (_scaleRefreshPending) return;
        _scaleRefreshPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _scaleRefreshPending = false;
            if (_size == null || _visuals.Count == 0) return;
            double scale = _pendingScale;
            foreach (var visual in _visuals)
            {
                OverlayRenderer.SetScale(visual, scale);
                PlacePreview(visual);
            }
            UpdateSelection();
        }, DispatcherPriority.Render);
    }

    private void ScheduleSelectedRefresh()
    {
        if (_selectedRefreshPending) return;
        _selectedRefreshPending = true;
        _selectedRefreshLater.Start();
    }

    private void RefreshSelectedVisual()
    {
        var item = _selected;
        if (item == null) return;
        int index = _visuals.FindIndex(v => ReferenceEquals(v.Item, item));
        if (index < 0) return;
        var visual = OverlayRenderer.Build(item, P.Scale);
        _canvas.Children.Remove(_visuals[index].Root);
        _visuals[index] = visual;
        _canvas.Children.Insert(Math.Min(index, _canvas.Children.Count - 2), visual.Root);
        PlacePreview(visual);
        OverlayRenderer.UpdateAll(new[] { visual });
        UpdateSelection();
    }

    private void RefreshList()
    {
        _itemsList.Items.Clear();
        foreach (var it in P.Items.AsEnumerable().Reverse())
        {
            var vis = new CheckBox { IsChecked = it.Visible, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Visível" };
            vis.Click += (_, _) => { it.Visible = vis.IsChecked == true; SaveSoon(); RebuildPreview(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(vis);
            row.Children.Add(new TextBlock { Text = Describe(it), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210 });
            _itemsList.Items.Add(new ListBoxItem { Content = row, Tag = it, IsSelected = it == _selected });
        }
    }

    private static string Describe(OverlayItem it)
    {
        string kind = it.Kind switch { ItemKind.Value => "Valor", ItemKind.Text => "Texto", ItemKind.Bar => "Barra", ItemKind.Graph => "Gráfico", ItemKind.Box => "Caixa", ItemKind.Clock => "Relógio", _ => "Jogo" };
        string what = it.Kind is ItemKind.Value or ItemKind.Bar or ItemKind.Graph ? OverlayValues.Find(it.SensorId)?.FriendlyName ?? it.SensorId ?? "?" : it.Label;
        return $"{kind} · {(string.IsNullOrEmpty(it.Label) || it.Kind == ItemKind.Text ? what : it.Label)}";
    }

    private void Select(OverlayItem? it)
    {
        _selected = it;
        foreach (ListBoxItem li in _itemsList.Items) li.IsSelected = li.Tag == it;
        BuildProps();
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        if (_moveOverlayMode)
        {
            _sel.Visibility = Visibility.Collapsed;
            UpdateGroupSelection();
            return;
        }
        _groupSel.Visibility = Visibility.Collapsed;
        var v = _visuals.FirstOrDefault(x => x.Item == _selected);
        if (v == null || !v.Item.Visible || v.Root.ActualWidth <= 0) { _sel.Visibility = Visibility.Collapsed; return; }
        var p = v.Root.TranslatePoint(new Point(0, 0), _canvas);
        Canvas.SetLeft(_sel, p.X - 4); Canvas.SetTop(_sel, p.Y - 4);
        _sel.Width = v.Root.ActualWidth * (v.Root.LayoutTransform is ScaleTransform s ? 1 : 1) + 8;
        _sel.Height = v.Root.ActualHeight + 8;
        if (v.Root.LayoutTransform is ScaleTransform st) { _sel.Width = v.Root.ActualWidth * st.ScaleX + 8; _sel.Height = v.Root.ActualHeight * st.ScaleY + 8; }
        _sel.Visibility = Visibility.Visible;
    }

    private void UpdateGroupSelection()
    {
        var bounds = _visuals.Where(v => v.Item.Visible && v.Root.ActualWidth > 0 && v.Root.ActualHeight > 0)
            .Select(v =>
            {
                var p = v.Root.TranslatePoint(new Point(0, 0), _canvas);
                double scale = v.Root.LayoutTransform is ScaleTransform st ? st.ScaleX : 1;
                return (Left: p.X, Top: p.Y, Right: p.X + v.Root.ActualWidth * scale, Bottom: p.Y + v.Root.ActualHeight * scale);
            }).ToList();
        if (bounds.Count == 0) { _groupSel.Visibility = Visibility.Collapsed; return; }
        double left = bounds.Min(b => b.Left), top = bounds.Min(b => b.Top);
        Canvas.SetLeft(_groupSel, left - 6);
        Canvas.SetTop(_groupSel, top - 6);
        _groupSel.Width = bounds.Max(b => b.Right) - left + 12;
        _groupSel.Height = bounds.Max(b => b.Bottom) - top + 12;
        _groupSel.Visibility = Visibility.Visible;
    }

    private void Canvas_Down(object sender, MouseButtonEventArgs e)
    {
        var m = e.GetPosition(_canvas);
        if (_moveOverlayMode)
        {
            Select(null);
            _movingOverlay = true;
        }
        else
        {
            OverlayItem? hit = null;
            // de cima para baixo (o último desenhado está à frente); caixas de fundo só se não houver mais nada
            foreach (var v in _visuals.AsEnumerable().Reverse().OrderBy(v => v.Item.Kind == ItemKind.Box ? 1 : 0))
            {
                if (!v.Item.Visible) continue;
                var p = v.Root.TranslatePoint(new Point(0, 0), _canvas);
                double sx = v.Root.LayoutTransform is ScaleTransform st ? st.ScaleX : 1;
                if (m.X >= p.X && m.X <= p.X + v.Root.ActualWidth * sx && m.Y >= p.Y && m.Y <= p.Y + v.Root.ActualHeight * sx) { hit = v.Item; break; }
            }
            Select(hit);
            _movingOverlay = hit == null;
            if (!_movingOverlay) _dragOrig = (hit!.X, hit.Y);
        }
        _dragging = true;
        _dragStart = m;
        if (_movingOverlay) _overlayDragOrig = (P.PositionX, P.PositionY);
        _canvas.CaptureMouse();
        if (_movingOverlay) _canvas.Cursor = Cursors.SizeAll;
    }

    private void Canvas_Move(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var m = e.GetPosition(_canvas);
        double size = P.Scale > 0 ? P.Scale : 1;
        double dx = (m.X - _dragStart.X) / size, dy = (m.Y - _dragStart.Y) / size;
        if (_movingOverlay)
        {
            SetOverlayPosition(Math.Round(_overlayDragOrig.X + dx), Math.Round(_overlayDragOrig.Y + dy));
            return;
        }
        if (_selected == null) return;
        bool right = _selected.Anchor is Anchor.TopRight or Anchor.BottomRight, bottom = _selected.Anchor is Anchor.BottomLeft or Anchor.BottomRight;
        _selected.X = Math.Round(Math.Max(0, _dragOrig.X + (right ? -dx : dx)) / 2) * 2;
        _selected.Y = Math.Round(Math.Max(0, _dragOrig.Y + (bottom ? -dy : dy)) / 2) * 2;
        var v = _visuals.First(x => x.Item == _selected);
        PlacePreview(v);
        UpdateSelection();
    }

    private void SetOverlayPosition(double? x = null, double? y = null)
    {
        if (x is double px) P.PositionX = px;
        if (y is double py) P.PositionY = py;
        foreach (var v in _visuals) PlacePreview(v);
        SyncPositionInput(_positionXInput, P.PositionX);
        SyncPositionInput(_positionYInput, P.PositionY);
        UpdateSelection();
        SaveSoon();
    }

    private FrameworkElement AlignmentPresets()
    {
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(-3, 3, -3, 3) };
        Add("↖", "Canto superior esquerdo", -1, -1);
        Add("↑", "Centro superior", 0, -1);
        Add("↗", "Canto superior direito", 1, -1);
        Add("←", "Centro esquerdo", -1, 0);
        Add("•", "Centro do ecrã", 0, 0);
        Add("→", "Centro direito", 1, 0);
        Add("↙", "Canto inferior esquerdo", -1, 1);
        Add("↓", "Centro inferior", 0, 1);
        Add("↘", "Canto inferior direito", 1, 1);
        return grid;

        void Add(string glyph, string description, int horizontal, int vertical)
        {
            var button = Ui.Btn(glyph, (_, _) => AlignOverlay(horizontal, vertical));
            button.ToolTip = description;
            button.Margin = new Thickness(3);
            button.Padding = new Thickness(4);
            button.FontSize = 16;
            button.Height = 34;
            grid.Children.Add(button);
        }
    }

    private void AlignOverlay(int horizontal, int vertical)
    {
        var bounds = _visuals
            .Where(v => v.Item.Visible && v.Root.ActualWidth > 0 && v.Root.ActualHeight > 0)
            .Select(v =>
            {
                var p = v.Root.TranslatePoint(new Point(0, 0), _canvas);
                double size = v.Root.LayoutTransform is ScaleTransform st ? st.ScaleX : 1;
                return (Left: p.X, Top: p.Y, Right: p.X + v.Root.ActualWidth * size, Bottom: p.Y + v.Root.ActualHeight * size);
            })
            .ToList();
        if (bounds.Count == 0) return;

        const double margin = 24;
        double width = bounds.Max(b => b.Right) - bounds.Min(b => b.Left);
        double height = bounds.Max(b => b.Bottom) - bounds.Min(b => b.Top);
        double targetLeft = horizontal switch
        {
            < 0 => margin,
            > 0 => _canvas.Width - width - margin,
            _ => (_canvas.Width - width) / 2,
        };
        double targetTop = vertical switch
        {
            < 0 => margin,
            > 0 => _canvas.Height - height - margin,
            _ => (_canvas.Height - height) / 2,
        };
        double scale = P.Scale > 0 ? P.Scale : 1;
        SetOverlayPosition(
            P.PositionX + (targetLeft - bounds.Min(b => b.Left)) / scale,
            P.PositionY + (targetTop - bounds.Min(b => b.Top)) / scale);
    }

    private void ToggleOverlayMove()
    {
        _moveOverlayMode = !_moveOverlayMode;
        if (_moveOverlayButton != null)
            _moveOverlayButton.Content = _moveOverlayMode ? "Concluir movimento" : "Mover overlay inteiro";
        _canvas.Cursor = _moveOverlayMode ? Cursors.SizeAll : Cursors.Arrow;
        UpdateSelection();
    }

    private FrameworkElement PositionRow(string label, bool horizontal)
    {
        var input = new TextBox { Width = 90, Text = Fmt(horizontal ? P.PositionX : P.PositionY), VerticalContentAlignment = VerticalAlignment.Center };
        if (horizontal) _positionXInput = input; else _positionYInput = input;

        void Set(double value)
        {
            if (horizontal) SetOverlayPosition(x: value);
            else SetOverlayPosition(y: value);
        }

        void Commit()
        {
            if (!double.TryParse(input.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double value) ||
                double.IsNaN(value) || double.IsInfinity(value))
            {
                input.BorderBrush = Brushes.OrangeRed;
                input.ToolTip = "Introduz um número válido para a posição.";
                return;
            }
            input.BorderBrush = Ui.Res("LineStrong");
            input.ToolTip = null;
            Set(value);
        }

        input.LostFocus += (_, _) => Commit();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        var buttons = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(Small("−10", () => Set((horizontal ? P.PositionX : P.PositionY) - 10)));
        buttons.Children.Add(Small("+10", () => Set((horizontal ? P.PositionX : P.PositionY) + 10)));
        buttons.Margin = new Thickness(6, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(input);
        row.Children.Add(buttons);
        return Labeled(label, row);
    }

    private static void SyncPositionInput(TextBox? input, double value)
    {
        if (input == null || input.IsKeyboardFocusWithin) return;
        input.Text = Fmt(value);
        input.BorderBrush = Ui.Res("LineStrong");
        input.ToolTip = null;
    }

    private void PlacePreview(OverlayVisual visual) =>
        OverlayRenderer.Place(visual, 1, P.PositionX, P.PositionY);

    // ================================================================== propriedades

    private void Changed(bool rebuild = true)
    {
        SaveSoon();
        if (rebuild) ScheduleSelectedRefresh();
    }

    private void BuildProps()
    {
        _props.Children.Clear();
        var it = _selected;
        if (it == null)
        {
            _props.Children.Add(Ui.T("Nenhum elemento escolhido", "H2"));
            _props.Children.Add(Ui.T("Clica num elemento na pré-visualização (ou na lista à esquerda) para o editar, ou adiciona um novo.", "MutedText"));
            return;
        }
        _props.Children.Add(Ui.T(Describe(it), "H2"));

        if (it.Kind is ItemKind.Value or ItemKind.Bar or ItemKind.Graph)
        {
            Section("O QUE MOSTRAR");
            _sources ??= OverlayValues.All();
            var filter = new TextBox { ToolTip = "Procurar sensor (ex.: temperatura, ventoinha, utilização da CPU)" };
            var cb = new ComboBox { MaxDropDownHeight = 420 };
            void Fill()
            {
                string q = filter.Text.Trim();
                cb.Items.Clear();
                foreach (var s in _sources!.Where(s => q.Length == 0 || s.FriendlyDisplay.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Display.Contains(q, StringComparison.OrdinalIgnoreCase)))
                    cb.Items.Add(new ComboBoxItem { Content = s.FriendlyDisplay, Tag = s, ToolTip = $"{s.Display} · ID interno: {s.Id}", IsSelected = s.Id == it.SensorId });
            }
            Fill();
            filter.TextChanged += (_, _) => Fill();
            cb.SelectionChanged += (_, _) =>
            {
                if (cb.SelectedItem is not ComboBoxItem { Tag: SourceOption s } || s.Id == it.SensorId) return;
                it.SensorId = s.Id;
                it.Unit = s.Unit;
                string friendly = s.FriendlyName;
                if (it.Kind != ItemKind.Graph || string.IsNullOrEmpty(it.Label) || it.Label.Length > 24)
                    it.Label = friendly.Length <= 24 ? friendly : it.Label;
                if (s.Kind is SensorsKindHelper.Temperature) { it.Max = 100; }
                Changed(); BuildProps(); RefreshList();
            };
            _props.Children.Add(Labeled("Procurar", filter));
            _props.Children.Add(Labeled("Sensor", cb));
            var refresh = Small("Atualizar lista de sensores", () => { _sources = OverlayValues.All(); BuildProps(); });
            _props.Children.Add(refresh);
        }

        Section("TEXTO");
        _props.Children.Add(TextRow(it.Kind == ItemKind.Text ? "Texto" : "Rótulo (ex.: GPU)", () => it.Label, v => { it.Label = v; Changed(); RefreshList(); }));
        if (it.Kind is ItemKind.Value or ItemKind.Bar or ItemKind.Graph)
        {
            _props.Children.Add(Check("Mostrar rótulo", () => it.ShowLabel, v => { it.ShowLabel = v; Changed(); }));
            _props.Children.Add(TextRow("Unidade (ex.: °C, %, FPS)", () => it.Unit, v => { it.Unit = v; Changed(); }));
            _props.Children.Add(Check("Mostrar unidade", () => it.ShowUnit, v => { it.ShowUnit = v; Changed(); }));
            _props.Children.Add(NumberRow("Casas decimais", () => it.Decimals, v => { it.Decimals = (int)v; Changed(); }, 0, 3, 1));
        }
        if (it.Kind == ItemKind.Clock)
            _props.Children.Add(TextRow("Formato (HH:mm, HH:mm:ss, dd/MM HH:mm)", () => it.TimeFormat, v => { it.TimeFormat = v; Changed(); }));

        if (it.Kind != ItemKind.Box)
        {
            Section("LETRA");
            var fonts = new ComboBox { MaxDropDownHeight = 400 };
            foreach (var f in Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s))
                fonts.Items.Add(new ComboBoxItem { Content = f, FontFamily = new FontFamily(f), IsSelected = f == it.FontFamily });
            fonts.SelectionChanged += (_, _) => { if (fonts.SelectedItem is ComboBoxItem { Content: string f }) { it.FontFamily = f; Changed(); } };
            _props.Children.Add(Labeled("Fonte", fonts));
            _props.Children.Add(NumberRow("Tamanho do valor", () => it.FontSize, v => { it.FontSize = v; Changed(); }, 6, 120, 1));
            _props.Children.Add(NumberRow("Tamanho do rótulo (0 = automático)", () => it.LabelSize, v => { it.LabelSize = v; Changed(); }, 0, 80, 1));
            _props.Children.Add(Check("Negrito", () => it.Bold, v => { it.Bold = v; Changed(); }));
            _props.Children.Add(Check("Itálico", () => it.Italic, v => { it.Italic = v; Changed(); }));
            _props.Children.Add(Check("Sombra no texto (lê-se melhor por cima do jogo)", () => it.Shadow, v => { it.Shadow = v; Changed(); }));
        }

        Section("CORES");
        if (it.Kind != ItemKind.Box)
        {
            _props.Children.Add(ColorRow(it.Kind == ItemKind.Text ? "Cor do texto" : "Cor do valor", () => it.ValueColor, v => { it.ValueColor = v; Changed(); }));
            if (it.Kind is ItemKind.Value or ItemKind.Bar or ItemKind.Graph)
            {
                _props.Children.Add(ColorRow("Cor do rótulo", () => it.LabelColor, v => { it.LabelColor = v; Changed(); }));
                _props.Children.Add(ColorRow("Cor da unidade", () => it.UnitColor, v => { it.UnitColor = v; Changed(); }));
            }
        }
        if (it.Kind is ItemKind.Bar or ItemKind.Graph)
        {
            _props.Children.Add(ColorRow(it.Kind == ItemKind.Bar ? "Cor da barra" : "Cor da linha", () => it.BarColor, v => { it.BarColor = v; Changed(); }));
            if (it.Kind == ItemKind.Bar) _props.Children.Add(ColorRow("Fundo da barra", () => it.BarBack, v => { it.BarBack = v; Changed(); }));
        }
        _props.Children.Add(ColorRow("Fundo", () => it.Background, v => { it.Background = v; Changed(); }, showOpaqueButton: true));
        _props.Children.Add(ColorRow("Borda", () => it.BorderColor, v => { it.BorderColor = v; Changed(); }));
        _props.Children.Add(NumberRow("Cantos arredondados", () => it.CornerRadius, v => { it.CornerRadius = v; Changed(); }, 0, 40, 1));
        if (it.Kind != ItemKind.Box) _props.Children.Add(NumberRow("Margem interna", () => it.Padding, v => { it.Padding = v; Changed(); }, 0, 30, 1));
        Section("POSIÇÃO E TAMANHO");
        var anchor = new ComboBox();
        foreach (var (t, a) in new[] { ("Canto superior esquerdo", Anchor.TopLeft), ("Canto superior direito", Anchor.TopRight), ("Canto inferior esquerdo", Anchor.BottomLeft), ("Canto inferior direito", Anchor.BottomRight) })
            anchor.Items.Add(new ComboBoxItem { Content = t, Tag = a, IsSelected = it.Anchor == a });
        anchor.SelectionChanged += (_, _) => { if (anchor.SelectedItem is ComboBoxItem { Tag: Anchor a }) { it.Anchor = a; Changed(); BuildProps(); } };
        _props.Children.Add(Labeled("Contar a posição a partir do", anchor));
        _props.Children.Add(NumberRow("Distância horizontal (X)", () => it.X, v => { it.X = v; Changed(); }, 0, 1920, 1));
        _props.Children.Add(NumberRow("Distância vertical (Y)", () => it.Y, v => { it.Y = v; Changed(); }, 0, 1080, 1));
        if (it.Kind is ItemKind.Bar or ItemKind.Graph or ItemKind.Box)
        {
            _props.Children.Add(NumberRow("Largura", () => it.Width, v => { it.Width = v; Changed(); }, 20, 1200, 1));
            _props.Children.Add(NumberRow(it.Kind == ItemKind.Bar ? "Grossura da barra" : "Altura", () => it.Height, v => { it.Height = v; Changed(); }, 2, 600, 1));
        }

        if (it.Kind is ItemKind.Bar or ItemKind.Graph)
        {
            Section("ESCALA");
            _props.Children.Add(NumberRow("Mínimo", () => it.Min, v => { it.Min = v; Changed(); }, -100, 10000, 1));
            _props.Children.Add(NumberRow("Máximo", () => it.Max, v => { it.Max = v; Changed(); }, 1, 10000, 1));
            if (it.Kind == ItemKind.Graph)
            {
                _props.Children.Add(Check("Escala automática (ajusta o máximo aos valores)", () => it.AutoScale, v => { it.AutoScale = v; Changed(); }));
                _props.Children.Add(NumberRow("Pontos de histórico", () => it.History, v => { it.History = (int)v; Changed(); }, 20, 600, 10));
                _props.Children.Add(NumberRow("Grossura da linha", () => it.LineWidth, v => { it.LineWidth = v; Changed(); }, 0.5, 6, 0.5));
                _props.Children.Add(Check("Preencher por baixo da linha", () => it.FillGraph, v => { it.FillGraph = v; Changed(); }));
            }
        }

        if (it.Kind is ItemKind.Value or ItemKind.Bar or ItemKind.Graph)
        {
            Section("REGRAS DE COR E ALERTAS");
            _props.Children.Add(Ui.T("Ex.: temperatura ≥ 80 → laranja; ≥ 87 → vermelho a piscar. FPS ≤ 60 → vermelho. A última regra que se aplica ganha.", "MutedText", 12));
            foreach (var th in it.Thresholds.ToList()) _props.Children.Add(ThresholdRow(it, th));
            _props.Children.Add(Small("＋ Adicionar regra", () => { it.Thresholds.Add(new Threshold { At = it.Max * 0.8 }); Changed(); BuildProps(); }));
        }
    }

    private void Section(string title)
    {
        var t = Ui.T(title, "Label");
        t.Margin = new Thickness(0, 18, 0, 2);
        t.Foreground = Ui.Res("Accent");
        _props.Children.Add(t);
    }

    private static FrameworkElement Labeled(string label, FrameworkElement input)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        sp.Children.Add(Ui.T(label, "MutedText", 12));
        input.Margin = new Thickness(0, 3, 0, 0);
        sp.Children.Add(input);
        return sp;
    }

    private static FrameworkElement TextRow(string label, Func<string> get, Action<string> set)
    {
        var tb = new TextBox { Text = get() };
        tb.TextChanged += (_, _) => set(tb.Text);
        return Labeled(label, tb);
    }

    private static CheckBox Check(string text, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = get(), Margin = new Thickness(0, 6, 0, 0) };
        c.Click += (_, _) => set(c.IsChecked == true);
        return c;
    }

    private FrameworkElement NumberRow(string label, Func<double> get, Action<double> set, double min, double max, double step)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        sp.Children.Add(Ui.T(label, "MutedText", 12));
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        var box = new TextBox { Width = 70, Text = Fmt(get()), Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(box, Dock.Right);
        row.Children.Add(box);
        var sl = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(get(), min, max), SmallChange = step, LargeChange = step * 10, VerticalAlignment = VerticalAlignment.Center };
        bool sync = false;
        sl.ValueChanged += (_, _) =>
        {
            if (sync) return;
            double v = Math.Round(sl.Value / step) * step;
            sync = true; box.Text = Fmt(v); sync = false;
            set(v);
        };
        void Commit()
        {
            if (sync) return;
            if (!double.TryParse(box.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return;
            v = Math.Clamp(v, min, max);
            sync = true; sl.Value = v; sync = false;
            set(v);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, k) => { if (k.Key == Key.Enter) Commit(); };
        row.Children.Add(sl);
        sp.Children.Add(row);
        return sp;
    }

    private static string Fmt(double v) => Math.Abs(v - Math.Round(v)) < 0.001 ? v.ToString("0") : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly string[] Palette =
    {
        "#FFFFFFFF", "#FFD1D5DB", "#FF9CA3AF", "#FF4B5563", "#FF000000", "#FF4F8CFF", "#FF38BDF8", "#FF22D3EE",
        "#FF8B5CF6", "#FFF472B6", "#FFEF4444", "#FFF97316", "#FFF59E0B", "#FFFACC15", "#FF22C55E", "#FF10B981",
    };

    /// <summary>Campo de cor: amostra (clica para a paleta), código #AARRGGBB e transparência.</summary>
    private FrameworkElement ColorRow(string label, Func<string> get, Action<string> set, bool showOpaqueButton = false)
    {
        var row = new StackPanel();
        var colorRow = new DockPanel();
        var swatch = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(6), BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0) };
        var hex = new TextBox { Text = get() };
        var alpha = new Slider
        {
            Tag = new Action<string>(set),
            Minimum = 0,
            Maximum = 255,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Opacidade: 0% = invisível; 100% = totalmente opaco"
        };
        var alphaValue = new TextBox
        {
            Width = 76,
            Padding = new Thickness(4, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Opacidade: 0% = invisível; 100% = totalmente opaco"
        };
        bool sync = false;
        void Show(string h)
        {
            sync = true;
            swatch.Background = OverlayRenderer.B(h);
            if (hex.Text != h) hex.Text = h;
            try
            {
                byte value = ((Color)ColorConverter.ConvertFromString(h)).A;
                alpha.Value = value;
                alphaValue.Text = $"{Math.Round(value / 255.0 * 100):0}%";
            }
            catch { alphaValue.Text = "—"; }
            sync = false;
        }
        void Apply(string h)
        {
            try { ColorConverter.ConvertFromString(h); } catch { return; }
            set(h); Show(h);
        }
        Show(get());
        hex.LostFocus += (_, _) => Apply(hex.Text.Trim());
        hex.KeyDown += (_, k) => { if (k.Key == Key.Enter) Apply(hex.Text.Trim()); };
        alpha.ValueChanged += (_, _) =>
        {
            if (sync) return;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(get());
                c.A = (byte)Math.Round(alpha.Value);
                string color = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
                if (alpha.Tag is Action<string> setColor) setColor(color);
                swatch.Background = OverlayRenderer.B(color);
                alphaValue.Text = $"{Math.Round(c.A / 255.0 * 100):0}%";
                SaveSoon();
                ScheduleSelectedRefresh();
            }
            catch { }
        };
        void CommitAlpha()
        {
            var text = alphaValue.Text.Trim().TrimEnd('%').Trim();
            if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var percent))
            {
                Show(get());
                return;
            }

            percent = Math.Clamp(percent, 0, 100);
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(get());
                c.A = (byte)Math.Round(percent * 255.0 / 100);
                Apply($"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");
            }
            catch { Show(get()); }
        }
        alphaValue.LostFocus += (_, _) => CommitAlpha();
        alphaValue.KeyDown += (_, k) => { if (k.Key == Key.Enter) CommitAlpha(); };
        Button? opaqueButton = null;
        if (showOpaqueButton)
        {
            opaqueButton = Ui.Btn("Opaco", (_, _) =>
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(get());
                    Apply($"#FF{c.R:X2}{c.G:X2}{c.B:X2}");
                }
                catch { }
            });
            opaqueButton.Padding = new Thickness(7, 4, 7, 4);
            opaqueButton.Margin = new Thickness(8, 0, 0, 0);
            opaqueButton.ToolTip = "Definir a opacidade deste fundo como 100%.";
        }
        var opacityRow = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(alpha, 0);
        Grid.SetColumn(alphaValue, 1);
        opacityRow.Children.Add(alpha);
        opacityRow.Children.Add(alphaValue);
        if (opaqueButton != null)
        {
            Grid.SetColumn(opaqueButton, 2);
            opacityRow.Children.Add(opaqueButton);
        }
        var popup = new Popup { PlacementTarget = swatch, StaysOpen = false, AllowsTransparency = true };
        var pal = new WrapPanel { Width = 8 * 30 };
        foreach (var p in Palette)
        {
            var sw = new Border { Width = 26, Height = 26, Margin = new Thickness(2), CornerRadius = new CornerRadius(5), Background = OverlayRenderer.B(p), Cursor = Cursors.Hand, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            sw.MouseLeftButtonUp += (_, _) =>
            {
                var c = (Color)ColorConverter.ConvertFromString(p);
                byte a = (byte)alpha.Value;
                Apply($"#{(a == 0 ? 255 : a):X2}{c.R:X2}{c.G:X2}{c.B:X2}");
                popup.IsOpen = false;
            };
            pal.Children.Add(sw);
        }
        popup.Child = new Border { Child = pal, Background = Ui.Res("Card"), BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(6) };
        swatch.MouseLeftButtonUp += (_, _) => popup.IsOpen = true;
        DockPanel.SetDock(swatch, Dock.Left);
        colorRow.Children.Add(swatch);
        colorRow.Children.Add(hex);
        colorRow.Children.Add(popup);
        row.Children.Add(colorRow);
        row.Children.Add(opacityRow);
        return Labeled(label, row);
    }

    private FrameworkElement ThresholdRow(OverlayItem it, Threshold th)
    {
        var box = new Border { BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 0) };
        var sp = new StackPanel();
        var top = new DockPanel();
        var del = Small("✕", () => { it.Thresholds.Remove(th); Changed(); BuildProps(); });
        DockPanel.SetDock(del, Dock.Right);
        top.Children.Add(del);
        var dir = new ComboBox { MinWidth = 150 };
        dir.Items.Add(new ComboBoxItem { Content = "Se for ≥ (maior ou igual)", Tag = false, IsSelected = !th.Below });
        dir.Items.Add(new ComboBoxItem { Content = "Se for ≤ (menor ou igual)", Tag = true, IsSelected = th.Below });
        dir.SelectionChanged += (_, _) => { if (dir.SelectedItem is ComboBoxItem { Tag: bool b }) { th.Below = b; Changed(); } };
        var at = new TextBox { Text = Fmt(th.At), Width = 70, Margin = new Thickness(8, 0, 0, 0) };
        at.LostFocus += (_, _) => { if (double.TryParse(at.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) { th.At = v; Changed(); } };
        var r = new StackPanel { Orientation = Orientation.Horizontal };
        r.Children.Add(dir);
        r.Children.Add(at);
        top.Children.Add(r);
        sp.Children.Add(top);
        sp.Children.Add(ColorRow("Cor", () => th.Color, v => { th.Color = v; Changed(); }));
        sp.Children.Add(Check("Piscar (alerta)", () => th.Blink, v => { th.Blink = v; Changed(); }));
        box.Child = sp;
        return box;
    }
}

/// <summary>Atalho para comparar o tipo de sensor no editor sem importar o namespace inteiro.</summary>
internal static class SensorsKindHelper
{
    public const Sensors.SensorKind Temperature = Sensors.SensorKind.Temperature;
}
