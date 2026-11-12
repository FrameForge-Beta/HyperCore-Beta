using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FrameForge.Fans;
using FrameForge.Sensors;

namespace FrameForge.Pages;

/// <summary>Ventoinhas: sensores personalizados → curvas → ventoinhas, com tudo explicado e valores ao vivo.</summary>
public sealed class FansPage : UserControl
{
    private readonly StackPanel _root = new() { Margin = new Thickness(28, 24, 28, 40) };
    private readonly StackPanel _sensors = new(), _curves = new(), _controls = new();
    private readonly List<Action> _live = new();
    private StackPanel _detectionList = new();
    private bool _building;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextBlock _status = new();
    private TextBlock _detectionStatus = new();
    private Button? _refreshDetection;
    private FanEngine E => FanEngine.Instance;
    private FanConfig C => FanEngine.Instance.Config;

    public FansPage()
    {
        Ui.UseTheme(this);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _root };
        SensorHub.Instance.Start();
        _timer.Tick += (_, _) => { foreach (var a in _live.ToList()) a(); };
        Loaded += async (_, _) =>
        {
            for (int i = 0; i < 40 && !SensorHub.Instance.Ready; i++) await Task.Delay(150);
            BuildAll();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Save() => E.Save();

    // ------------------------------------------------------------------ montagem

    private void BuildAll()
    {
        if (_building) return; // dois "Loaded" seguidos (trocar de aba depressa) não podem reconstruir ao mesmo tempo
        _building = true;
        try
        {
        _root.Children.Clear();
        _live.Clear();
        _root.Children.Add(Ui.T("Ventoinhas", "H1"));
        var sub = Ui.T("Escolhe O QUE MEDIR (uma temperatura ou a média de várias), desenha COMO REAGIR (uma curva) e diz QUEM OBEDECE (cada ventoinha). " +
                       "Exemplo: média entre o núcleo e o hotspot da gráfica → curva → ventoinha da gráfica.", "MutedText");
        sub.Margin = new Thickness(0, 0, 0, 16);
        _root.Children.Add(sub);
        _root.Children.Add(StatusCard());
        _root.Children.Add(DetectionCard());
        _root.Children.Add(Section("1 · O que medir", "Sensores personalizados: juntam várias leituras numa só (média, máximo, mínimo, soma, diferença), fazem a média ao longo do tempo (para a ventoinha não andar aos saltos) ou somam um desvio.",
                                   "＋ Adicionar sensor", () => { C.Sensors.Add(new CustomSensorDef { Name = UniqueSensorName() }); Save(); BuildSensors(); }));
        _root.Children.Add(_sensors);
        _root.Children.Add(Section("2 · Como reagir", "Curvas: para cada temperatura, que % de velocidade queres. Tipos: Gráfico (desenhas com o rato), Linear (de A a B), Fixa, Gatilho (dois níveis), Mistura (combina curvas) e Sincronizar (copia outra ventoinha).",
                                   "＋ Adicionar curva", () => { C.Curves.Add(new FanCurve { Name = UniqueCurveName(), SourceId = SensorHub.Instance.Temperatures().FirstOrDefault()?.Id }); Save(); BuildCurves(); }));
        _root.Children.Add(_curves);
        _root.Children.Add(Section("3 · Quem obedece", "Cada ventoinha que o PC deixa controlar. Automático = a BIOS/driver decide (como sem esta app). Curva = segue uma das tuas curvas. Manual = velocidade fixa.", null, null));
        _root.Children.Add(_controls);
        BuildSensors();
        BuildCurves();
        BuildControls();
        }
        finally { _building = false; }
    }

    private Border DetectionCard()
    {
        var sp = new StackPanel();
        var head = new DockPanel();
        _refreshDetection = Ui.Btn("ATUALIZAR DETEÇÃO", (_, _) => RefreshDetection(), "Btn");
        _refreshDetection.Padding = new Thickness(12, 7, 12, 7);
        DockPanel.SetDock(_refreshDetection, Dock.Right);
        head.Children.Add(_refreshDetection);
        head.Children.Add(Ui.T("Deteção de hardware", "H2"));
        sp.Children.Add(head);
        _detectionStatus = Ui.T("A aguardar leitura dos controlos de hardware…", "MutedText", 12);
        _detectionStatus.Margin = new Thickness(0, 4, 0, 10);
        sp.Children.Add(_detectionStatus);
        // Painel novo em cada reconstrução: um StackPanel não pode ser filho de dois cartões ao mesmo tempo.
        (_detectionList.Parent as Panel)?.Children.Remove(_detectionList);
        _detectionList = new StackPanel();
        sp.Children.Add(_detectionList);
        RefreshDetection();
        return Ui.Card(sp);
    }

    private void RefreshDetection()
    {
        if (_refreshDetection == null) return;
        _refreshDetection.IsEnabled = false;
        _detectionStatus.Text = "A atualizar inventário de ventoinhas e controladores…";
        _ = Task.Run(() =>
        {
            try
            {
                var controls = SensorHub.Instance.Controls();
                var fanSensors = SensorHub.Instance.All().Where(e => e.Kind == SensorKind.Fan).ToList();
                Dispatcher.Invoke(() =>
                {
                    _detectionList.Children.Clear();
                    foreach (var (entry, control, fanId) in controls)
                    {
                        var rpm = fanId != null ? SensorHub.Instance.Value(fanId) : null;
                        AddDetectionRow(entry.Display, entry.HardwareType.ToString(),
                            $"Controlo gravável · intervalo {control.MinSoftwareValue:0}–{control.MaxSoftwareValue:0}%",
                            rpm is float r ? $"{r:0} RPM" : "RPM indisponível", canWrite: true);
                    }

                    var readOnlyFans = fanSensors.Where(f => !controls.Any(c => c.FanId == f.Id)).ToList();
                    foreach (var fan in readOnlyFans)
                        AddDetectionRow(fan.Display, fan.HardwareType.ToString(), "Só leitura · o driver não expõe escrita de velocidade",
                            fan.Value is float rpm ? $"{rpm:0} RPM" : "Sem leitura", canWrite: false);

                    int gpuControls = controls.Count(c => c.Entry.HardwareType is LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia
                        or LibreHardwareMonitor.Hardware.HardwareType.GpuAmd or LibreHardwareMonitor.Hardware.HardwareType.GpuIntel);
                    int otherControls = controls.Count - gpuControls;
                    _detectionStatus.Text = $"{controls.Count} controlo(s) gravável(eis) · {readOnlyFans.Count} ventoinha(s) só de leitura · {gpuControls} GPU / {otherControls} outros controladores. " +
                        "A disponibilidade de escrita depende do hardware, driver e permissões; o FrameForge não instala drivers automaticamente.";
                    _refreshDetection.IsEnabled = true;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    _detectionStatus.Text = "Falha ao detetar ventoinhas: " + ex.Message;
                    _refreshDetection.IsEnabled = true;
                });
            }
        });
    }


    private void AddDetectionRow(string title, string hardware, string capability, string reading, bool canWrite)
    {
        var row = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var details = new StackPanel();
        details.Children.Add(Ui.T(title, "Body", 13, Ui.Res("Fg"), FontWeights.SemiBold));
        details.Children.Add(Ui.T($"{hardware} · {capability}", "MutedText", 11));
        row.Children.Add(details);
        var state = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        state.Children.Add(Ui.T(reading, "MutedText", 12, canWrite ? Ui.Res("Accent2") : Ui.Res("Faint"), FontWeights.SemiBold));
        state.Children.Add(Ui.T(canWrite ? "CONTROLO DISPONÍVEL" : "SÓ LEITURA", "Label", 9, canWrite ? Ui.Res("Accent2") : Ui.Res("Faint")));
        Grid.SetColumn(state, 1);
        row.Children.Add(state);
        _detectionList.Children.Add(row);
        _detectionList.Children.Add(new Border { Height = 1, Background = Ui.Res("Line"), Margin = new Thickness(0, 5, 0, 0) });
    }

    private Border StatusCard()
    {
        var sp = new StackPanel();
        var sw = new ToggleButton { Style = (Style)FindResource("Switch"), IsChecked = C.Enabled && E.IsAttached, VerticalAlignment = VerticalAlignment.Center };
        sw.Click += (_, _) =>
        {
            E.SetEnabled(sw.IsChecked == true);
            BuildAll();
        };
        var head = new DockPanel();
        DockPanel.SetDock(sw, Dock.Right);
        head.Children.Add(sw);
        head.Children.Add(Ui.T("Controlo de ventoinhas pelo FrameForge", "H2"));
        sp.Children.Add(head);
        sp.Children.Add(Ui.T("DESLIGADO = o FrameForge não escreve velocidades nem mantém um controlador ativo; as ventoinhas ficam sob controlo da BIOS/driver. Ao desligar, qualquer controlo aplicado é libertado imediatamente. A deteção e a leitura de temperaturas/RPM continuam disponíveis.", "MutedText"));

        var onStart = new CheckBox { Content = "Ligar automaticamente sempre que o FrameForge abrir", IsChecked = C.ApplyOnStart, Margin = new Thickness(0, 10, 0, 0) };
        onStart.Click += (_, _) =>
        {
            C.ApplyOnStart = onStart.IsChecked == true;
            if (C.ApplyOnStart && !E.IsAttached) E.SetEnabled(true);
            else Save();
            BuildAll();
        };
        sp.Children.Add(onStart);

        sp.Children.Add(Number("Temperatura de emergência (CPU/GPU): acima disto, TODAS as ventoinhas controladas vão a 100%", () => C.EmergencyTemp, v => C.EmergencyTemp = v, 60, 105, "°C"));

        var wiz = Ui.Btn("✨ Assistente: ventoinha da gráfica a seguir a média núcleo + hotspot", (_, _) =>
        {
            if (!C.Enabled || !E.IsAttached)
            {
                _status.Text = "O controlo está desligado. Ativa-o primeiro se queres que o assistente altere a configuração das ventoinhas.";
                return;
            }
            _status.Text = E.QuickSetupGpu();
            BuildAll();
        }, "BtnAccent");
        wiz.IsEnabled = C.Enabled && E.IsAttached;
        wiz.HorizontalAlignment = HorizontalAlignment.Left;
        wiz.Margin = new Thickness(0, 12, 0, 0);
        sp.Children.Add(wiz);

        _status = Ui.T("", "MutedAuto", color: Ui.Res("Accent"));
        _status.Margin = new Thickness(0, 10, 0, 0);
        sp.Children.Add(_status);
        var em = Ui.T("", "MutedAuto", color: Ui.Res("Bad"), weight: FontWeights.SemiBold);
        _live.Add(() => em.Text = E.Emergency ? $"⚠ EMERGÊNCIA: {E.EmergencyReason} — ventoinhas a 100%." : "");
        sp.Children.Add(em);

        return Ui.Card(sp, Ui.Res("Accent"));
    }

    private static Border Section(string title, string desc, string? addText, Action? add)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 14, 0, 8) };
        if (addText != null && add != null)
        {
            var b = Ui.Btn(addText, (_, _) => add());
            b.Margin = new Thickness(12, 0, 0, 0);
            b.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(b, Dock.Right);
            dock.Children.Add(b);
        }
        var sp = new StackPanel();
        sp.Children.Add(Ui.T(title, "H2"));
        sp.Children.Add(Ui.T(desc, "MutedText"));
        dock.Children.Add(sp);
        return new Border { Child = dock };
    }

    // ------------------------------------------------------------------ 1. sensores

    private string UniqueSensorName() { int i = 1; while (C.Sensors.Any(s => s.Name == $"Sensor {i}")) i++; return $"Sensor {i}"; }
    private string UniqueCurveName() { int i = 1; while (C.Curves.Any(s => s.Name == $"Curva {i}")) i++; return $"Curva {i}"; }

    private void BuildSensors()
    {
        _sensors.Children.Clear();
        if (C.Sensors.Count == 0) _sensors.Children.Add(Ui.T("Ainda não tens sensores personalizados. Podes usar diretamente qualquer temperatura nas curvas, ou criar um aqui (ex.: média de duas).", "MutedText"));
        foreach (var s in C.Sensors.ToList()) _sensors.Children.Add(SensorCard(s));
    }

    private Border SensorCard(CustomSensorDef s)
    {
        var sp = new StackPanel();
        var head = new DockPanel();
        var del = Ui.Btn("Apagar", (_, _) => { C.Sensors.Remove(s); Save(); BuildSensors(); BuildCurves(); }, "BtnDanger");
        DockPanel.SetDock(del, Dock.Right);
        head.Children.Add(del);
        var value = Ui.T("—", "Big");
        value.Margin = new Thickness(16, 0, 16, 0);
        DockPanel.SetDock(value, Dock.Right);
        head.Children.Add(value);
        _live.Add(() => value.Text = SensorHub.Instance.Get(s.Id)?.LiveText ?? "—");
        var name = new TextBox { Text = s.Name, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        name.LostFocus += (_, _) =>
        {
            string nn = name.Text.Trim();
            if (nn.Length == 0 || nn == s.Name || C.Sensors.Any(x => x.Name == nn)) { name.Text = s.Name; return; }
            foreach (var c in C.Curves.Where(c => c.SourceId == s.Id)) c.SourceId = "custom/" + nn;
            s.Name = nn; Save(); BuildCurves();
        };
        head.Children.Add(name);
        sp.Children.Add(head);

        var kind = Combo(new[] { ("Mistura de vários sensores", CustomKind.Mix), ("Média ao longo do tempo (suaviza)", CustomKind.TimeAverage), ("Desvio (soma/subtrai graus)", CustomKind.Offset) }, s.Kind,
                         v => { s.Kind = v; Save(); BuildSensors(); });
        sp.Children.Add(Field("Tipo", kind));

        if (s.Kind == CustomKind.Mix)
            sp.Children.Add(Field("Função", Combo(new[] { ("Média", MixFunction.Average), ("Máximo (o mais quente manda)", MixFunction.Max), ("Mínimo", MixFunction.Min),
                                                          ("Soma", MixFunction.Sum), ("Subtração (1.º menos os outros)", MixFunction.Subtract) }, s.Function, v => { s.Function = v; Save(); })));
        if (s.Kind == CustomKind.TimeAverage)
            sp.Children.Add(Number("Janela de tempo", () => s.Seconds, v => s.Seconds = v, 2, 120, "s"));
        if (s.Kind == CustomKind.Offset)
        {
            sp.Children.Add(Number("Desvio", () => s.Offset, v => s.Offset = v, -30, 30, s.Proportional ? "%" : "°C"));
            var prop = new CheckBox { Content = "Proporcional (em % do valor, em vez de graus)", IsChecked = s.Proportional };
            prop.Click += (_, _) => { s.Proportional = prop.IsChecked == true; Save(); };
            sp.Children.Add(prop);
        }

        sp.Children.Add(Ui.T(s.Kind == CustomKind.Mix ? "LEITURAS A JUNTAR (marca duas ou mais)" : "LEITURA DE ORIGEM (marca uma)", "Label"));
        var list = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var e in SensorHub.Instance.All().Where(e => e.Kind == SensorKind.Temperature && e.Id != s.Id && e.Value is > 0))
        {
            var cb = new CheckBox { IsChecked = s.Sources.Contains(e.Id), Margin = new Thickness(0, 2, 18, 2), MinWidth = 280 };
            var txt = new TextBlock();
            cb.Content = txt;
            _live.Add(() => txt.Text = $"{e.Display}  ({e.LiveText})");
            txt.Text = $"{e.Display}  ({e.LiveText})";
            cb.Click += (_, _) =>
            {
                if (cb.IsChecked == true) { if (s.Kind != CustomKind.Mix) s.Sources.Clear(); s.Sources.Add(e.Id); }
                else s.Sources.Remove(e.Id);
                Save();
                if (s.Kind != CustomKind.Mix) BuildSensors();
            };
            list.Children.Add(cb);
        }
        sp.Children.Add(list);
        return Ui.Card(sp);
    }

    // ------------------------------------------------------------------ 2. curvas

    private List<SensorEntry> SourceOptions() =>
        SensorHub.Instance.All().Where(e => (e.Kind == SensorKind.Temperature && e.Value is > 0) || e.Group == "Personalizado").ToList();

    private void BuildCurves()
    {
        _curves.Children.Clear();
        if (C.Curves.Count == 0) _curves.Children.Add(Ui.T("Ainda não há curvas. Carrega em \"Adicionar curva\" ou usa o assistente lá em cima.", "MutedText"));
        foreach (var c in C.Curves.ToList()) _curves.Children.Add(CurveCard(c));
    }

    private Border CurveCard(FanCurve c)
    {
        var sp = new StackPanel();
        var head = new DockPanel();
        var del = Ui.Btn("Apagar", (_, _) =>
        {
            C.Curves.Remove(c);
            foreach (var cc in C.Controls.Where(x => x.CurveName == c.Name)) { cc.CurveName = null; cc.Mode = ControlMode.Auto; }
            Save(); BuildCurves(); BuildControls();
        }, "BtnDanger");
        DockPanel.SetDock(del, Dock.Right);
        head.Children.Add(del);
        var outText = Ui.T("—", "Big");
        outText.Margin = new Thickness(16, 0, 16, 0);
        DockPanel.SetDock(outText, Dock.Right);
        head.Children.Add(outText);
        var name = new TextBox { Text = c.Name, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        name.LostFocus += (_, _) =>
        {
            string nn = name.Text.Trim();
            if (nn.Length == 0 || nn == c.Name || C.Curves.Any(x => x.Name == nn)) { name.Text = c.Name; return; }
            foreach (var cc in C.Controls.Where(x => x.CurveName == c.Name)) cc.CurveName = nn;
            foreach (var m in C.Curves) for (int i = 0; i < m.MixCurves.Count; i++) if (m.MixCurves[i] == c.Name) m.MixCurves[i] = nn;
            c.Name = nn; Save(); BuildControls();
        };
        head.Children.Add(name);
        sp.Children.Add(head);

        sp.Children.Add(Field("Tipo de curva", Combo(new[]
        {
            ("Gráfico — desenhas os pontos", CurveKind.Graph), ("Linear — de uma temperatura a outra", CurveKind.Linear), ("Fixa — sempre a mesma %", CurveKind.Flat),
            ("Gatilho — dois níveis (repouso / carga)", CurveKind.Trigger), ("Mistura — combina outras curvas", CurveKind.Mix), ("Sincronizar — copia outra ventoinha", CurveKind.Sync),
        }, c.Kind, v => { c.Kind = v; Save(); BuildCurves(); })));

        CurveGraph? graph = null;
        if (c.Kind is CurveKind.Graph or CurveKind.Linear or CurveKind.Trigger)
        {
            var opts = SourceOptions();
            var src = new ComboBox { MinWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var o in opts) src.Items.Add(new ComboBoxItem { Content = o.Display, Tag = o.Id, IsSelected = o.Id == c.SourceId });
            src.SelectionChanged += (_, _) => { if (src.SelectedItem is ComboBoxItem { Tag: string id }) { c.SourceId = id; Save(); } };
            sp.Children.Add(Field("Temperatura que manda nesta curva", src));

            graph = new CurveGraph(c) { Margin = new Thickness(0, 10, 0, 6) };
            graph.Changed += Save;
            sp.Children.Add(graph);
        }

        switch (c.Kind)
        {
            case CurveKind.Linear:
                sp.Children.Add(Row(Number("Temp. mínima", () => c.MinTemp, v => { c.MinTemp = v; graph?.Redraw(); }, 0, 110, "°C"),
                                    Number("Velocidade mínima", () => c.MinSpeed, v => { c.MinSpeed = v; graph?.Redraw(); }, 0, 100, "%"),
                                    Number("Temp. máxima", () => c.MaxTemp, v => { c.MaxTemp = v; graph?.Redraw(); }, 0, 110, "°C"),
                                    Number("Velocidade máxima", () => c.MaxSpeed, v => { c.MaxSpeed = v; graph?.Redraw(); }, 0, 100, "%")));
                break;
            case CurveKind.Flat:
                sp.Children.Add(Number("Velocidade fixa", () => c.FlatValue, v => c.FlatValue = v, 0, 100, "%"));
                break;
            case CurveKind.Trigger:
                sp.Children.Add(Ui.T("Fica na velocidade de repouso até chegar à temperatura de carga; depois fica na velocidade de carga até descer à temperatura de repouso. Bom para ventoinhas que não queres a mudar de som o tempo todo.", "MutedText"));
                sp.Children.Add(Row(Number("Temp. de repouso", () => c.IdleTemp, v => { c.IdleTemp = v; graph?.Redraw(); }, 0, 110, "°C"),
                                    Number("Velocidade em repouso", () => c.IdleSpeed, v => { c.IdleSpeed = v; graph?.Redraw(); }, 0, 100, "%"),
                                    Number("Temp. de carga", () => c.LoadTemp, v => { c.LoadTemp = v; graph?.Redraw(); }, 0, 110, "°C"),
                                    Number("Velocidade em carga", () => c.LoadSpeed, v => { c.LoadSpeed = v; graph?.Redraw(); }, 0, 100, "%")));
                break;
            case CurveKind.Mix:
                sp.Children.Add(Field("Função", Combo(new[] { ("Máximo (a curva que pede mais ganha)", MixFunction.Max), ("Média", MixFunction.Average), ("Mínimo", MixFunction.Min),
                                                              ("Soma", MixFunction.Sum), ("Subtração", MixFunction.Subtract) }, c.MixFunction, v => { c.MixFunction = v; Save(); })));
                var wp = new WrapPanel();
                foreach (var other in C.Curves.Where(o => o != c))
                {
                    var cb = new CheckBox { Content = other.Name, IsChecked = c.MixCurves.Contains(other.Name), Margin = new Thickness(0, 2, 18, 2) };
                    cb.Click += (_, _) => { if (cb.IsChecked == true) c.MixCurves.Add(other.Name); else c.MixCurves.Remove(other.Name); Save(); };
                    wp.Children.Add(cb);
                }
                sp.Children.Add(Field("Curvas a combinar", wp));
                break;
            case CurveKind.Sync:
                var ctl = new ComboBox { MinWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var (e, _, _) in SensorHub.Instance.Controls())
                    ctl.Items.Add(new ComboBoxItem { Content = Label(e), Tag = e.Id, IsSelected = e.Id == c.SyncControlId });
                ctl.SelectionChanged += (_, _) => { if (ctl.SelectedItem is ComboBoxItem { Tag: string id }) { c.SyncControlId = id; Save(); } };
                sp.Children.Add(Field("Copiar a velocidade de", ctl));
                sp.Children.Add(Number("Desvio", () => c.SyncOffset, v => c.SyncOffset = v, -50, 50, c.SyncProportional ? "%" : "pontos %"));
                var prop = new CheckBox { Content = "Desvio proporcional", IsChecked = c.SyncProportional };
                prop.Click += (_, _) => { c.SyncProportional = prop.IsChecked == true; Save(); };
                sp.Children.Add(prop);
                break;
        }

        if (c.Kind is CurveKind.Graph or CurveKind.Linear)
        {
            sp.Children.Add(Row(Number("Histerese (só muda se a temperatura variar pelo menos)", () => c.Hysteresis, v => c.Hysteresis = v, 0, 15, "°C"),
                                Number("Tempo de resposta (espera entre mudanças)", () => c.ResponseTime, v => c.ResponseTime = v, 0, 30, "s")));
            var ign = new CheckBox { Content = "Ignorar a histerese nos extremos da curva (reage logo no mínimo e no máximo)", IsChecked = c.IgnoreHysteresisAtLimits };
            ign.Click += (_, _) => { c.IgnoreHysteresisAtLimits = ign.IsChecked == true; Save(); };
            sp.Children.Add(ign);
        }
        else if (c.Kind == CurveKind.Trigger)
            sp.Children.Add(Number("Tempo de resposta", () => c.ResponseTime, v => c.ResponseTime = v, 0, 60, "s"));

        _live.Add(() =>
        {
            double? o = E.CurveOutput(c.Name);
            outText.Text = o is double v ? $"{v:0}%" : "—";
            if (graph != null) { graph.LiveTemp = SensorHub.Instance.Value(c.SourceId); graph.LiveOutput = o; graph.Redraw(); }
        });
        return Ui.Card(sp);
    }

    // ------------------------------------------------------------------ 3. ventoinhas

    private static string Label(SensorEntry e) => $"{e.Group} · {e.Name}";

    private void BuildControls()
    {
        _controls.Children.Clear();
        var list = SensorHub.Instance.Controls();
        if (list.Count == 0)
        {
            _controls.Children.Add(Ui.Card(Ui.T("Nenhum controlo gravável foi detetado. A leitura de temperaturas e RPM pode continuar disponível; a escrita de velocidades depende do controlador, do hardware e das permissões.", "MutedText")) );
            return;
        }
        var heading = Ui.T("Ventoinhas controláveis", "H2");
        heading.Margin = new Thickness(0, 0, 0, 8);
        _controls.Children.Add(heading);
        foreach (var (e, ctl, fanId) in list)
        {
            var cc = C.Controls.FirstOrDefault(x => x.ControlId == e.Id);
            if (cc == null) { cc = new FanControlCfg { ControlId = e.Id }; C.Controls.Add(cc); }
            _controls.Children.Add(ControlCard(e, ctl, fanId, cc));
        }
        Save();
    }

    private Border ControlCard(SensorEntry e, LibreHardwareMonitor.Hardware.IControl ctl, string? fanId, FanControlCfg cc)
    {
        var sp = new StackPanel();
        var head = new DockPanel();
        var live = Ui.T("—", "Big");
        DockPanel.SetDock(live, Dock.Right);
        head.Children.Add(live);
        var title = new StackPanel();
        var name = new TextBox { Text = cc.Label ?? Label(e), Width = 340, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Dá-lhe um nome que percebas (ex.: Frente da caixa)" };
        name.LostFocus += (_, _) => { cc.Label = name.Text.Trim().Length > 0 ? name.Text.Trim() : null; Save(); };
        title.Children.Add(name);
        title.Children.Add(Ui.T($"{Label(e)} · o hardware aceita {ctl.MinSoftwareValue:0}–{ctl.MaxSoftwareValue:0}%", "MutedText", 12));
        head.Children.Add(title);
        sp.Children.Add(head);
        var details = new WrapPanel { Margin = new Thickness(0, 10, 0, 12) };
        details.Children.Add(StatusPill("Hardware", Label(e)));
        details.Children.Add(StatusPill("Controlo", $"{ctl.MinSoftwareValue:0}–{ctl.MaxSoftwareValue:0}%"));
        details.Children.Add(StatusPill("Velocidade", "A ler…"));
        sp.Children.Add(details);
        var status = Ui.T("AUTOMÁTICO · FrameForge não escreve na ventoinha", "Label", 10, Ui.Res("Muted"), FontWeights.SemiBold);
        status.Margin = new Thickness(0, 0, 0, 8);
        sp.Children.Add(status);
        var update = new Action(() =>
        {
            float? rpm = SensorHub.Instance.Value(fanId);
            var o = E.ControlOutput(e.Id);
            live.Text = rpm is float r ? $"{r:0} RPM" : e.Value is float v ? $"{v:0}%" : "—";
            live.Foreground = o is { Failsafe: true } ? Ui.Res("Bad") : Ui.Res("Accent2");
            live.ToolTip = o is { } x ? $"Alvo {x.Target:0}% · a aplicar {x.Current:0}%" + (x.Failsafe ? " · falha de leitura → 100% por segurança" : "") : "Velocidade real (RPM), quando o sensor está disponível.";
            status.Text = !C.Enabled || !E.IsAttached || cc.Mode == ControlMode.Auto
                ? "AUTOMÁTICO · FrameForge não escreve na ventoinha"
                : o is { Failsafe: true } ? "PROTEÇÃO · sem leitura de temperatura; alvo 100%"
                : cc.Mode == ControlMode.Manual ? "CONTROLO MANUAL ATIVO"
                : $"CURVA ATIVA · {cc.CurveName ?? "sem curva selecionada"}";
            status.Foreground = o is { Failsafe: true } ? Ui.Res("Bad") : C.Enabled && E.IsAttached && cc.Mode != ControlMode.Auto ? Ui.Res("Accent2") : Ui.Res("Muted");
        });
        _live.Add(update);
        update();

        var mode = Combo(new[] { ("Automático (BIOS/driver decide)", ControlMode.Auto), ("Seguir uma curva", ControlMode.Curve), ("Manual (velocidade fixa)", ControlMode.Manual) },
                         cc.Mode, v => { cc.Mode = v; Save(); BuildControls(); });
        sp.Children.Add(Field("Modo", mode));

        if (cc.Mode == ControlMode.Curve)
        {
            var curve = new ComboBox { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var c in C.Curves) curve.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Name, IsSelected = c.Name == cc.CurveName });
            curve.SelectionChanged += (_, _) => { if (curve.SelectedItem is ComboBoxItem { Tag: string n }) { cc.CurveName = n; Save(); } };
            sp.Children.Add(Field("Curva", C.Curves.Count == 0 ? Ui.T("Cria primeiro uma curva no passo 2.", "MutedText") : curve));
        }
        if (cc.Mode == ControlMode.Manual)
            sp.Children.Add(Number("Velocidade", () => cc.ManualValue, v => cc.ManualValue = v, 0, 100, "%"));

        if (cc.Mode != ControlMode.Auto)
        {
            var adv = new Expander { Header = "Opções avançadas (como no FanControl)", Margin = new Thickness(0, 8, 0, 0), Foreground = Ui.Res("Fg") };
            var a = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            a.Children.Add(Row(Number("Mínimo — nunca abaixo de", () => cc.MinPct, v => cc.MinPct = v, 0, 100, "%"),
                               Number("Desvio — soma à curva", () => cc.OffsetPct, v => cc.OffsetPct = v, -50, 50, "%")));
            a.Children.Add(Row(Number("Paragem — abaixo disto pára (0 RPM)", () => cc.StopPct, v => cc.StopPct = v, 0, 60, "%"),
                               Number("Arranque — empurrão para voltar a rodar", () => cc.StartPct, v => cc.StartPct = v, 0, 100, "%")));
            a.Children.Add(Row(Number("Subida máx. por segundo", () => cc.StepUp, v => cc.StepUp = v, 1, 100, "%/s"),
                               Number("Descida máx. por segundo", () => cc.StepDown, v => cc.StepDown = v, 1, 100, "%/s")));
            a.Children.Add(Ui.T("Subida/descida lentas = som mais suave (a ventoinha não 'acelera aos saltos'). Paragem só funciona se a ventoinha/placa suportar 0 RPM.", "MutedText", 12));
            adv.Content = a;
            sp.Children.Add(adv);
        }
        return Ui.Card(sp);
    }

    // ------------------------------------------------------------------ campos

    private static Border StatusPill(string label, string value)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.T(label.ToUpperInvariant(), "Label", 9, Ui.Res("Faint"), FontWeights.SemiBold));
        stack.Children.Add(Ui.T(value, "MutedText", 11, Ui.Res("Fg")));
        return new Border
        {
            Background = Ui.Res("Hover"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0), Child = stack
        };
    }

    private static FrameworkElement Field(string label, FrameworkElement input)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        sp.Children.Add(Ui.T(label.ToUpperInvariant(), "Label"));
        input.Margin = new Thickness(0, 5, 0, 0);
        if (input is ComboBox cb) { cb.HorizontalAlignment = HorizontalAlignment.Left; if (cb.MinWidth < 300) cb.MinWidth = 300; }
        sp.Children.Add(input);
        return sp;
    }

    private ComboBox Combo<T>((string Text, T Value)[] items, T current, Action<T> set)
    {
        var cb = new ComboBox();
        foreach (var (t, v) in items) cb.Items.Add(new ComboBoxItem { Content = t, Tag = v, IsSelected = Equals(v, current) });
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem { Tag: T v } && !Equals(v, current)) { current = v; set(v); } };
        return cb;
    }

    /// <summary>Slider + caixa de número, ligados um ao outro.</summary>
    private FrameworkElement Number(string label, Func<double> get, Action<double> set, double min, double max, string unit)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 10, 24, 0), MinWidth = 260 };
        sp.Children.Add(Ui.T(label.ToUpperInvariant(), "Label"));
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var box = new TextBox { Width = 64, Text = get().ToString("0.#"), Margin = new Thickness(10, 0, 0, 0) };
        var u = Ui.T(unit, "MutedText");
        u.Margin = new Thickness(6, 0, 0, 0);
        u.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(u, Dock.Right);
        DockPanel.SetDock(box, Dock.Right);
        row.Children.Add(u);
        row.Children.Add(box);
        var sl = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(get(), min, max), VerticalAlignment = VerticalAlignment.Center, MinWidth = 140 };
        bool sync = false;
        sl.ValueChanged += (_, _) => { if (sync) return; sync = true; double v = Math.Round(sl.Value); box.Text = v.ToString("0"); set(v); sync = false; };
        sl.PreviewMouseUp += (_, _) => Save();
        void Commit()
        {
            if (!double.TryParse(box.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) { box.Text = get().ToString("0.#"); return; }
            v = Math.Clamp(v, min, max);
            sync = true; sl.Value = v; sync = false;
            box.Text = v.ToString("0.#");
            set(v); Save();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, k) => { if (k.Key == System.Windows.Input.Key.Enter) Commit(); };
        row.Children.Add(sl);
        sp.Children.Add(row);
        return sp;
    }

    private static WrapPanel Row(params FrameworkElement[] items)
    {
        var w = new WrapPanel();
        foreach (var i in items) w.Children.Add(i);
        return w;
    }
}
