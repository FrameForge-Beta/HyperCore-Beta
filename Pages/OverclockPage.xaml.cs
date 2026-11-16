using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FrameForge.Core;
using FrameForge.Hardware;
using FrameForge.Overclock;

namespace FrameForge.Pages;

public partial class OverclockPage : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private GpuState _st = new();
    private bool _loading;

    public OverclockPage()
    {
        InitializeComponent();
        FrameForge.Sensors.SensorHub.Instance.Start();
        _timer.Tick += async (_, _) => { _st = await Task.Run(ReadState); ShowState(); };
        Loaded += async (_, _) => { _timer.Start(); await Init(); };
        Unloaded += (_, _) => _timer.Stop();
        Guard.IsChecked = AppServices.Settings.OcCrashGuard;
        BuildGuide();
    }

    /// <summary>NVIDIA lê pela NVML; nas outras marcas o estado vem dos sensores do driver, para o painel não ficar vazio.</summary>
    private static GpuState ReadState() => AppServices.Tuner.Supported ? AppServices.Tuner.Read() : GpuSensors.Read();

    private async Task Init()
    {
        _st = await Task.Run(ReadState);
        GpuName.Text = _st.Name.Length > 0 && _st.Name != "?" ? _st.Name : "Placa gráfica";
        Warnings.Children.Clear();
        bool canWrite = AppServices.Tuner.Supported;
        WriteControls.Visibility = canWrite ? Visibility.Visible : Visibility.Collapsed;
        ReadOnlyNote.Visibility = canWrite ? Visibility.Collapsed : Visibility.Visible;
        if (!canWrite)
        {
            ReadOnlyGpu.Text = $"Placa detetada: {_st.Name}. Os valores abaixo são lidos do driver em tempo real.";
            Warnings.Children.Add(Ui.Card(Ui.T("Esta página está em modo de leitura: os controlos de overclock só existem para NVIDIA. " +
                "Em AMD e Intel, altera clocks e ventoinha no painel oficial do fabricante.", "Body"), Ui.Res("Warn")));
        }
        if (_st.NvApiError != null)
            Warnings.Children.Add(Ui.Card(Ui.T($"Offsets indisponíveis: {_st.NvApiError}. O limite de energia e o teto de clock continuam a funcionar.", "Body"), Ui.Res("Warn")));
        var tools = GpuTuner.ConflictingTools();
        if (tools.Count > 0)
            Warnings.Children.Add(Ui.Card(Ui.T($"⚠ {string.Join(", ", tools)} está aberto. Dois programas de overclock ao mesmo tempo sobrepõem-se: o último a escrever ganha, " +
                "e o Afterburner reaplica o perfil dele ao arrancar. Fecha-o (ou desliga 'Apply overclocking at system startup' nele) antes de usar este separador.", "Body"), Ui.Res("Bad")));

        if (canWrite) ShowAutoProfiles();

        if (_st.CoreMin is int cmin && _st.CoreMax is int cmax) { Core.Minimum = Math.Max(cmin, -500); Core.Maximum = Math.Min(cmax, 400); }
        if (_st.MemMin is int mmin && _st.MemMax is int mmax) { Mem.Minimum = Math.Max(mmin, -1000); Mem.Maximum = Math.Min(mmax, 2000); }
        if (_st.PowerDefaultW is uint def && def > 0 && _st.PowerMinW is uint pmin && _st.PowerMaxW is uint pmax)
        {
            Power.Minimum = Math.Ceiling(100.0 * pmin / def);
            Power.Maximum = Math.Floor(100.0 * pmax / def);
        }
        if (_st.MaxCoreClock is uint mc) Lock.Maximum = mc;
        LoadProfiles();
        ShowState();
    }

    private void LoadProfiles(string? select = null)
    {
        _loading = true;
        var list = GpuTuner.Presets(_st).Concat(AppServices.Settings.OcProfiles).ToList();
        Profiles.ItemsSource = list.Select(p => AppServices.Settings.UnstableOcProfiles.Contains(p.Name) ? $"{p.Name}  (instável)" : p.Name).ToList();
        Profiles.Tag = list;
        int idx = select != null ? list.FindIndex(p => p.Name == select) : list.FindIndex(p => p.Name == (AppServices.Tuner.Applied?.Name ?? "Stock"));
        _loading = false;
        Profiles.SelectedIndex = Math.Max(0, idx);
    }

    private OcProfile? Selected => Profiles.Tag is List<OcProfile> l && Profiles.SelectedIndex >= 0 ? l[Profiles.SelectedIndex] : null;

    private void Profiles_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Selected is not OcProfile p) return;
        ProfileDesc.Text = (p.Description.Length > 0 ? p.Description : "Perfil guardado por ti.") + (p.Risk.Length > 0 ? $"  Risco: {p.Risk}." : "") +
                           (AppServices.Settings.UnstableOcProfiles.Contains(p.Name) ? "  ⚠ Este perfil causou um crash do driver: reduz os valores antes de o usar." : "");
        Core.Value = p.CoreOffset; Mem.Value = p.MemOffset; Power.Value = p.PowerPct; Lock.Value = p.LockMaxClock; Fan.Value = p.FanPct;
        OnStart.IsChecked = AppServices.Settings.OcApplyOnStart == p.Name;
        if (!p.Builtin) ProfileName.Text = p.Name;
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FanLbl == null) return; // durante o InitializeComponent os sliders disparam antes de os rótulos existirem
        CoreLbl.Text = $"OFFSET DO NÚCLEO: {Core.Value:+0;-0;0} MHz" + (_st.CoreMin != null ? $"   (driver aceita {_st.CoreMin}…{_st.CoreMax})" : "");
        MemLbl.Text = $"OFFSET DA MEMÓRIA: {Mem.Value:+0;-0;0} MHz" + (_st.MemMin != null ? $"   (driver aceita {_st.MemMin}…{_st.MemMax})" : "");
        PowerLbl.Text = $"LIMITE DE ENERGIA: {Power.Value:0}%" + (_st.PowerDefaultW is uint d ? $" = {d * Power.Value / 100:0} W (fábrica {d} W)" : "");
        LockLbl.Text = Lock.Value < 300 ? "TETO DE CLOCK: desligado" : $"TETO DE CLOCK (UNDERVOLT): {Lock.Value:0} MHz";
        FanLbl.Text = Fan.Value < 30 ? "VENTOINHA: automática (recomendado)" : $"VENTOINHA: fixa em {Fan.Value:0}%";
    }

    private OcProfile FromSliders(string name) => new()
    {
        Name = name, CoreOffset = (int)Core.Value, MemOffset = (int)Mem.Value, PowerPct = (int)Power.Value,
        LockMaxClock = Lock.Value < 300 ? 0 : (int)Lock.Value, FanPct = Fan.Value < 30 ? 0 : (int)Fan.Value,
    };

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ScanBusy()) return;
        var name = Selected?.Name ?? "Manual";
        var p = FromSliders(name);
        if (p.CoreOffset > 150 || p.MemOffset > 1000)
            if (MessageBox.Show("Valores altos podem causar crash do jogo/driver ou artefactos. Com a guarda de crash ligada, a placa volta a Stock sozinha se o driver for abaixo. Continuar?",
                    "HyperCore", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Log.Text = "A aplicar…";
        var log = await Task.Run(() => AppServices.Tuner.Apply(p));
        Log.Text = string.Join("\n", log) + "\n\nTesta 15–30 min num jogo pesado e vê o relatório da sessão: o FPS tem de SUBIR. Se cair com mais memória, passaste do ponto (a correção de erros da GDDR6 está a trabalhar).";
    }

    private async void Stock_Click(object sender, RoutedEventArgs e)
    {
        if (ScanBusy()) return;
        Log.Text = "A repor valores de fábrica…";
        var log = await Task.Run(AppServices.Tuner.Stock);
        Log.Text = string.Join("\n", log);
        LoadProfiles("Stock");
    }

    /// <summary>Durante o scan quem escreve na placa é o afinador automático: dois a escrever estragam o teste.</summary>
    private bool ScanBusy()
    {
        if (!FrameForge.Tuning.ScanService.Instance.Running) return false;
        Log.Text = "Há um scan a correr na página Scan e afinação automática: espera que acabe (ou pára-o lá) antes de mexer aqui.";
        return true;
    }

    /// <summary>Perfis "Auto · …" encontrados pelo scan: um cartão com o ganho medido e atalhos para escolher/aplicar.</summary>
    private void ShowAutoProfiles()
    {
        var autos = AppServices.Settings.OcProfiles.Where(p => p.Name.StartsWith("Auto · ", StringComparison.Ordinal)).ToList();
        var last = FrameForge.Tuning.ScanService.Instance.Last;
        var sp = new StackPanel();
        sp.Children.Add(Ui.T("Perfis automáticos (do scan)", "H2"));
        if (autos.Count == 0)
        {
            sp.Children.Add(Ui.T("Ainda não tens nenhum. O scan sobe o núcleo (e a memória) aos poucos, testa cada passo e guarda aqui o máximo estável.", "MutedText"));
            var go = Ui.Btn("Abrir Scan e afinação automática", (_, _) => AppServices.Navigate("FF:scan"));
            go.Margin = new Thickness(0, 10, 0, 0);
            go.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(go);
        }
        foreach (var a in autos)
        {
            bool unstable = AppServices.Settings.UnstableOcProfiles.Contains(a.Name);
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var pick = Ui.Btn("Escolher", (_, _) => LoadProfiles(a.Name));
            pick.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(pick);
            if (!unstable)
            {
                var apply = Ui.Btn("Aplicar", async (_, _) =>
                {
                    if (ScanBusy()) return;
                    try
                    {
                        LoadProfiles(a.Name);
                        Log.Text = "A aplicar…";
                        var log = await Task.Run(() => AppServices.Tuner.Apply(a));
                        Log.Text = string.Join("\n", log);
                    }
                    catch (Exception ex) { Log.Text = "Não consegui aplicar: " + ex.Message; } // async void: nada pode escapar
                }, "BtnAccent");
                apply.Margin = new Thickness(8, 0, 0, 0);
                buttons.Children.Add(apply);
            }
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            var txt = new StackPanel();
            txt.Children.Add(Ui.T(a.Name + (unstable ? "  (instável)" : ""), "Body", weight: FontWeights.SemiBold));
            string gain = last?.Oc is { NomePerfil: string n, GanhoPct: double g } && n == a.Name ? $" · {g:+0.#;-0.#;0}% FPS medido" : "";
            txt.Children.Add(Ui.T($"Núcleo {a.CoreOffset:+0;-0;0} MHz · memória {a.MemOffset:+0;-0;0} · energia {a.PowerPct}%{gain}", "MutedText", 12.5));
            row.Children.Add(txt);
            sp.Children.Add(row);
        }
        Warnings.Children.Add(Ui.Card(sp, Ui.Res("Accent")));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = ProfileName.Text.Trim();
        if (name.Length == 0 || GpuTuner.Presets(_st).Any(p => p.Name == name)) { Log.Text = "Escolhe um nome diferente dos perfis de origem."; return; }
        var p = FromSliders(name);
        p.Description = $"Núcleo {p.CoreOffset:+0;-0;0}, memória {p.MemOffset:+0;-0;0}, energia {p.PowerPct}%" + (p.LockMaxClock > 0 ? $", teto {p.LockMaxClock} MHz" : "");
        AppServices.Settings.OcProfiles.RemoveAll(x => x.Name == name);
        AppServices.Settings.OcProfiles.Add(p);
        AppServices.Settings.UnstableOcProfiles.Remove(name);
        AppServices.Settings.Save();
        LoadProfiles(name);
        Log.Text = $"Perfil '{name}' guardado. Podes escolhê-lo no perfil de cada jogo (aplica só enquanto jogas).";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not OcProfile p || p.Builtin) { Log.Text = "Os perfis de origem não se apagam."; return; }
        AppServices.Settings.OcProfiles.RemoveAll(x => x.Name == p.Name);
        if (AppServices.Settings.OcApplyOnStart == p.Name) AppServices.Settings.OcApplyOnStart = null;
        AppServices.Settings.Save();
        LoadProfiles();
    }

    private void OnStart_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not OcProfile p) return;
        if (OnStart.IsChecked == true && p.Builtin && p.Name != "Stock")
        {
            // perfis de origem também podem arrancar com a app: guarda uma cópia
            if (!AppServices.Settings.OcProfiles.Any(x => x.Name == p.Name)) AppServices.Settings.OcProfiles.Add(p);
        }
        AppServices.Settings.OcApplyOnStart = OnStart.IsChecked == true ? p.Name : null;
        AppServices.Settings.Save();
    }

    private void Guard_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.OcCrashGuard = Guard.IsChecked == true;
        AppServices.Settings.Save();
        if (AppServices.Settings.OcCrashGuard) AppServices.Tuner.StartCrashGuard();
    }

    private void ShowState()
    {
        var s = _st;
        bool nvidia = AppServices.Tuner.Supported;
        if (s.Name.Length > 0 && s.Name != "?") GpuName.Text = s.Name;

        string Fan() => s.Fan is uint pct ? $"{pct} %" + (s.FanRpm is uint rpm ? $" · {rpm} RPM" : "")
                        : s.FanRpm is uint only ? $"{only} RPM"
                        : "—";
        string Power() => s.PowerW is uint w ? $"{w} W" + (s.PowerLimitW is uint l ? $" / {l} W" : "")
                          : s.PowerLimitW is uint onlyLimit ? $"— / {onlyLimit} W"
                          : "—";
        static string Col(string value) => value.PadLeft(9);

        State.Text =
            $"Núcleo     {Col(s.CoreClock is uint ck ? $"{ck} MHz" : "—")}\n" +
            $"Memória    {Col(s.MemClock is uint mk ? $"{mk} MHz" : "—")}\n" +
            $"Temp.      {Col(s.Temp is uint tp ? $"{tp} °C" : "—")}\n" +
            $"Consumo    {Col(Power())}\n" +
            $"Ventoinha  {Col(Fan())}\n" +
            $"Offset núc {Col(s.CoreOffset is int c ? $"{c:+0;-0;0} MHz" : "—")}\n" +
            $"Offset mem {Col(s.MemOffset is int m ? $"{m:+0;-0;0} MHz" : "—")}\n" +
            $"Perfil     {AppServices.Tuner.Applied?.Name ?? "(nenhum aplicado aqui)"}\n\n" +
            $"Limitado por: {(nvidia ? Nvml.ThrottleText(s.Throttle) : "— (motivo do travão só em NVIDIA)")}";
    }

    private void BuildGuide()
    {
        void H(string t) { var x = Ui.T(t, "H2"); x.Margin = new Thickness(0, 10, 0, 4); Guide.Children.Add(x); }
        void P(string t) { var x = Ui.T(t, "Body"); x.Margin = new Thickness(0, 0, 0, 6); Guide.Children.Add(x); }
        H("Como fazer overclock com segurança (método passo a passo)");
        P("1. Antes de mexer: grava uma sessão de 10 min de um jogo pesado em Stock (botão JOGAR com 'gravar sessão'). É a tua referência.");
        P("2. Limite de energia no máximo e temperatura controlada (< 80 °C). Só isto já dá FPS em placas que batem no limite de energia ('Limitado por: limite de energia').");
        P("3. Núcleo: sobe em passos de +15 MHz. A cada passo joga 10 min. Crash, ecrã preto ou o jogo fechar = passo atrás de 30 MHz. Usa o valor estável −15 MHz como margem.");
        P("4. Memória: sobe em passos de +100 MHz. Atenção: a GDDR6 corrige erros sozinha, por isso memória instável NÃO crasha — o FPS é que desce. Compara o FPS médio nos relatórios: quando deixar de subir, volta 100–200 MHz.");
        P("5. Faz um teste final de 1 h. Guarda como perfil e escolhe-o no perfil do jogo (aplica só enquanto jogas).");
        H("Undervolt (o melhor para temperatura e ruído)");
        P("Perfil 'Silencioso / Eficiente': offset +100 com teto de clock = a placa chega ao clock máximo com menos tensão. Resultado típico: −10 °C e −25% de consumo com quase o mesmo FPS. Se instável, baixa o offset para +60.");
        H("Avisos");
        P("• Overclock pode anular a garantia de alguns fabricantes e reduz a margem de estabilidade; nunca é garantido — cada chip é diferente (\"lotaria do silício\").");
        P("• Em portáteis o driver normalmente bloqueia offsets. Em portáteis, prefere undervolt/limite de energia mais baixo.");
        P("• Usa só UM programa de overclock. Se usares o Afterburner para a ventoinha, não lhe apliques perfis de clock.");
        P("• Os anti-cheats não se importam com overclock (é feito pelo driver, fora do jogo). O que eles bloqueiam é o overlay do RTSS (injeção).");
        H("Processador (na BIOS — não se faz por software com segurança)");
        P("Ryzen 5000 (ex.: 5700X): BIOS → Tweaker/Advanced → AMD Overclocking → Precision Boost Overdrive = Advanced → PBO Limits = Auto/Motherboard (com placa de entrada, mantém Auto) → " +
          "Curve Optimizer = All Cores, Sign = Negative, Magnitude = 10. Testa (CoreCycler ou OCCT 1 h + uso normal 2–3 dias). Se estável, tenta 15–20. Reinícios em repouso = valor alto demais.");
        P("RAM: ativa o XMP/EXPO (Gigabyte: 'Extreme Memory Profile (X.M.P.)' = Profile1). Em Ryzen 5000 o ideal é 3600 MHz com FCLK 1800 (1:1).");
        P("Intel K: subir o limite de energia (PL1/PL2) e um undervolt ligeiro. Intel 13.ª/14.ª gen: atualiza a BIOS (microcódigo 0x12B+) e usa o perfil 'Intel Default' antes de qualquer OC.");
    }
}
