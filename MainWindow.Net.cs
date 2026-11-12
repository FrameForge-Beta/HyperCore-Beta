using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FrameForge.Core;

namespace ResetExplode;

/// <summary>Páginas Energia (bateria e fonte) e Rede (diagnóstico de lag).</summary>
public partial class MainWindow
{
    // ================= Energia =================

    private bool _energyBusy;
    private bool _loadingPowerPlans;

    private async void RefreshEnergy()
    {
        if (_energyBusy) return;
        _energyBusy = true;
        BtnEnergyRefresh.IsEnabled = false;
        EnergyStatus.Text = "Lendo bateria e energia…";
        try
        {
            var sensors = _monitor.Rows.ToList();
            var cards = await Task.Run(() => BatteryInfo.Read(sensors));
            EnergyCards.ItemsSource = cards;
            bool hasBattery = cards.Count > 1;
            EnergyStatus.Text = (hasBattery ? "Bateria detectada" : "Nenhuma bateria (PC de mesa)") + $" · lido às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { EnergyStatus.Text = "Falhou: " + ex.Message; }
        finally
        {
            _energyBusy = false;
            BtnEnergyRefresh.IsEnabled = true;
        }
    }

    private void BtnEnergyRefresh_Click(object sender, RoutedEventArgs e) => RefreshEnergy();

    private void BtnPowerPlanRefresh_Click(object sender, RoutedEventArgs e) => RefreshPowerPlans();

    private async void RefreshPowerPlans(string? selectGuid = null)
    {
        _loadingPowerPlans = true;
        PowerPlanList.IsEnabled = false;
        BtnPowerPlanRefresh.IsEnabled = false;
        try
        {
            var plans = await Task.Run(PowerPlanService.List);
            PowerPlanList.ItemsSource = plans.Select(p => p.Name + (p.IsActive ? "  · ativo" : "")).ToList();
            PowerPlanList.Tag = plans;
            int index = selectGuid != null ? plans.FindIndex(p => p.Guid.Equals(selectGuid, StringComparison.OrdinalIgnoreCase))
                : plans.FindIndex(p => p.IsActive);
            if (index < 0 && plans.Count > 0) index = 0;
            PowerPlanList.SelectedIndex = index;
            PowerPlanEditorStatus.Text = $"{plans.Count} planos encontrados. A criação duplica um plano e mantém os originais do Windows intactos.";
            _loadingPowerPlans = false;
            PowerPlanList.IsEnabled = true;
            if (SelectedPowerPlan is PowerPlanInfo selected) _ = LoadPowerPlanValuesAsync(selected);
        }
        catch (Exception ex)
        {
            PowerPlanEditorStatus.Text = "Não foi possível ler os planos: " + ex.Message;
        }
        finally { _loadingPowerPlans = false; PowerPlanList.IsEnabled = true; BtnPowerPlanRefresh.IsEnabled = true; }
    }

    private PowerPlanInfo? SelectedPowerPlan => PowerPlanList.Tag is List<PowerPlanInfo> plans && PowerPlanList.SelectedIndex >= 0 && PowerPlanList.SelectedIndex < plans.Count
        ? plans[PowerPlanList.SelectedIndex] : null;

    private void PowerPlan_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPowerPlans || SelectedPowerPlan is not PowerPlanInfo plan) return;
        _ = LoadPowerPlanValuesAsync(plan);
    }

    private async Task LoadPowerPlanValuesAsync(PowerPlanInfo plan)
    {
        BtnPowerPlanActivate.IsEnabled = !plan.IsActive;
        BtnPowerPlanDelete.IsEnabled = !PowerPlanService.IsBuiltIn(plan.Guid) && !plan.IsActive;
        bool editable = !PowerPlanService.IsBuiltIn(plan.Guid);
        BtnPowerPlanSave.IsEnabled = editable;
        PlanCpuMinAc.IsEnabled = PlanCpuMinDc.IsEnabled = PlanCpuMaxAc.IsEnabled = PlanCpuMaxDc.IsEnabled = editable;
        PlanDisplayAc.IsEnabled = PlanDisplayDc.IsEnabled = PlanSleepAc.IsEnabled = PlanSleepDc.IsEnabled = PlanDiskAc.IsEnabled = PlanDiskDc.IsEnabled = editable;
        try
        {
            var v = await Task.Run(() => PowerPlanService.ReadValues(plan.Guid));
            if (!string.Equals(SelectedPowerPlan?.Guid, plan.Guid, StringComparison.OrdinalIgnoreCase)) return;
            PlanCpuMinAc.Text = v.CpuMinAc.ToString(); PlanCpuMinDc.Text = v.CpuMinDc.ToString();
            PlanCpuMaxAc.Text = v.CpuMaxAc.ToString(); PlanCpuMaxDc.Text = v.CpuMaxDc.ToString();
            PlanDisplayAc.Text = v.DisplayMinutesAc.ToString(); PlanDisplayDc.Text = v.DisplayMinutesDc.ToString();
            PlanSleepAc.Text = v.SleepMinutesAc.ToString(); PlanSleepDc.Text = v.SleepMinutesDc.ToString();
            PlanDiskAc.Text = v.DiskMinutesAc.ToString(); PlanDiskDc.Text = v.DiskMinutesDc.ToString();
            PowerPlanEditorStatus.Text = editable
                ? "Plano personalizado: podes editar as opções acima."
                : "Plano predefinido do Windows, só de leitura. Cria uma cópia para personalizar as opções.";
        }
        catch (Exception ex)
        {
            if (string.Equals(SelectedPowerPlan?.Guid, plan.Guid, StringComparison.OrdinalIgnoreCase))
                PowerPlanEditorStatus.Text = "Não consegui ler as opções deste plano: " + ex.Message;
        }
    }

    private async void BtnPowerPlanCreate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var source = SelectedPowerPlan ?? throw new InvalidOperationException("Seleciona primeiro o plano que queres usar como base.");
            string name = PowerPlanName.Text.Trim();
            if (name.Length == 0) throw new ArgumentException("Escreve um nome para o novo perfil.");
            if ((PowerPlanList.Tag as List<PowerPlanInfo>)?.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true)
                throw new ArgumentException("Já existe um plano com esse nome.");
            var created = await Task.Run(() => PowerPlanService.Duplicate(source.Guid, name));
            PowerPlanName.Clear();
            RefreshPowerPlans(created.Guid);
            PowerPlanEditorStatus.Text = $"Perfil '{created.Name}' criado como cópia de '{source.Name}'. Ajusta as opções e guarda.";
        }
        catch (Exception ex) { PowerPlanEditorStatus.Text = "Não foi possível criar o perfil: " + ex.Message; }
    }

    private async void BtnPowerPlanSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var plan = SelectedPowerPlan ?? throw new InvalidOperationException("Seleciona um plano.");
            if (PowerPlanService.IsBuiltIn(plan.Guid)) throw new InvalidOperationException("Cria uma cópia antes de editar um plano do Windows.");
            int Read(TextBox box, string label) => int.TryParse(box.Text, out int value)
                ? value : throw new ArgumentException($"O campo '{label}' tem de ser um número inteiro.");
            var values = new PowerPlanValues(
                Read(PlanCpuMinAc, "CPU mínima tomada"), Read(PlanCpuMaxAc, "CPU máxima tomada"),
                Read(PlanCpuMinDc, "CPU mínima bateria"), Read(PlanCpuMaxDc, "CPU máxima bateria"),
                Read(PlanDisplayAc, "Ecrã tomada"), Read(PlanDisplayDc, "Ecrã bateria"),
                Read(PlanSleepAc, "Suspender tomada"), Read(PlanSleepDc, "Suspender bateria"),
                Read(PlanDiskAc, "Discos tomada"), Read(PlanDiskDc, "Discos bateria"));
            await Task.Run(() => PowerPlanService.WriteValues(plan.Guid, values));
            PowerPlanEditorStatus.Text = $"Opções guardadas em '{plan.Name}'. Os valores também foram definidos para tomada e bateria.";
        }
        catch (Exception ex) { PowerPlanEditorStatus.Text = "Não foi possível guardar as opções: " + ex.Message; }
    }

    private async void BtnPowerPlanActivate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var plan = SelectedPowerPlan ?? throw new InvalidOperationException("Seleciona um plano.");
            await Task.Run(() => PowerPlanService.Activate(plan.Guid));
            RefreshPowerPlans(plan.Guid);
            PowerPlanEditorStatus.Text = $"Plano '{plan.Name}' ativado.";
            AppServices.RefreshProbe();
            RefreshEnergy();
        }
        catch (Exception ex) { PowerPlanEditorStatus.Text = "Não foi possível ativar o plano: " + ex.Message; }
    }

    private async void BtnPowerPlanDelete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var plan = SelectedPowerPlan ?? throw new InvalidOperationException("Seleciona um plano.");
            if (MessageBox.Show(this, $"Apagar o plano personalizado '{plan.Name}'? Esta ação não pode ser desfeita.",
                    "FrameForge · Energia", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await Task.Run(() => PowerPlanService.Delete(plan.Guid));
            RefreshPowerPlans();
            PowerPlanEditorStatus.Text = $"Plano personalizado '{plan.Name}' apagado.";
        }
        catch (Exception ex) { PowerPlanEditorStatus.Text = "Não foi possível apagar o plano: " + ex.Message; }
    }

    // ================= Rede =================

    private CancellationTokenSource? _netCts;
    private bool _netRunning;
    private List<DiskHealthCard>? _lastNet;

    private async void BtnNetTest_Click(object sender, RoutedEventArgs e)
    {
        if (_netRunning) { _netCts?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { NetStatus.Text = "Termine o outro teste antes."; return; }

        _netRunning = true;
        _netCts = new CancellationTokenSource();
        var ct = _netCts.Token;
        BtnNetTest.Content = "CANCELAR";
        NetCards.ItemsSource = null;
        NetProgress.ScaleX = 0;
        try
        {
            var cards = await NetworkTest.Run((text, frac) => Dispatcher.BeginInvoke(new Action(() =>
            {
                NetStatus.Text = text;
                NetProgress.ScaleX = Math.Clamp(frac, 0, 1);
            })), ct);
            _lastNet = cards;
            NetCards.ItemsSource = cards;
            NetStatus.Text = $"Concluído às {DateTime.Now:HH:mm:ss}. O resultado entra no relatório exportado.";
            NetProgress.ScaleX = 1;
        }
        catch (OperationCanceledException) { NetStatus.Text = "Teste cancelado."; NetProgress.ScaleX = 0; }
        catch (Exception ex) { NetStatus.Text = "Falhou: " + ex.Message; NetProgress.ScaleX = 0; }
        finally
        {
            _netRunning = false;
            _netCts?.Dispose();
            _netCts = null;
            BtnNetTest.Content = "TESTAR";
        }
    }

    // ================= DNS =================

    private CancellationTokenSource? _dnsCts;

    /// <summary>Preenche o seletor de fornecedores de DNS e mostra o que o Windows usa neste momento.</summary>
    private void InitDnsProviders()
    {
        if (DnsProviderBox.Items.Count == 0)
        {
            foreach (var provider in DnsBenchmark.Providers)
                DnsProviderBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{provider.Name} ({provider.Primary})", Tag = provider, ToolTip = provider.Note,
                });
            DnsProviderBox.SelectedIndex = 0;
        }
        string servers = DnsInUse();
        bool loopback = servers.StartsWith("127.", StringComparison.Ordinal) || servers.Contains("127.0.0.2") || servers.Contains("127.0.0.3");
        DnsStatus.Text = $"DNS configurado no Windows: {servers}." +
                         (loopback ? " Esse endereço é um resolvedor local (por exemplo AdGuard/NextDNS/pfSense no próprio PC ou no router): o teste abaixo fala diretamente com cada fornecedor, para os poder comparar." : "");
        UpdateDnsHint();
    }

    /// <summary>Servidores DNS e adaptador ativos, lidos da configuração do Windows.</summary>
    private static (string Servers, string Adapter, int? InterfaceIndex) ActiveNetwork()
    {
        try
        {
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.NetworkInterfaceType is not (System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                                or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                            && n.GetIPProperties().GatewayAddresses.Any(g =>
                                g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                && !g.Address.Equals(System.Net.IPAddress.Any)))
                .OrderByDescending(n => n.Speed).FirstOrDefault();
            if (nic == null) return ("—", "—", null);
            var dns = nic.GetIPProperties().DnsAddresses
                .Where(d => d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(d => d.ToString()).ToList();
            return (dns.Count > 0 ? string.Join(", ", dns) : "automático (router)", nic.Name,
                nic.GetIPProperties().GetIPv4Properties()?.Index);
        }
        catch { return ("—", "—", null); }
    }

    private static string DnsInUse() => ActiveNetwork().Servers;

    private void UpdateDnsHint()
    {
        var (_, adapter, _) = ActiveNetwork();
        var provider = SelectedDnsProvider();
        DnsApplyHint.Text = provider == null
            ? "Escolhe um fornecedor e aplica-o ao adaptador ativo. Será pedida autorização de administrador."
            : $"Aplicará {provider.Primary} e {provider.Secondary} ao adaptador «{adapter}». Será pedida autorização de administrador.";
    }

    private DnsProvider? SelectedDnsProvider() =>
        (DnsProviderBox.SelectedItem as ComboBoxItem)?.Tag as DnsProvider;

    private void DnsProvider_Changed(object sender, SelectionChangedEventArgs e) => UpdateDnsHint();

    private async void BtnDnsOne_Click(object sender, RoutedEventArgs e) => await RunDnsAsync(all: false);

    private async void BtnDnsAll_Click(object sender, RoutedEventArgs e) => await RunDnsAsync(all: true);

    private async void BtnDnsApply_Click(object sender, RoutedEventArgs e)
    {
        var provider = SelectedDnsProvider();
        var (_, adapter, interfaceIndex) = ActiveNetwork();
        if (provider == null)
        {
            DnsStatus.Text = "Seleciona primeiro um fornecedor de DNS.";
            return;
        }
        if (interfaceIndex is not > 0)
        {
            DnsStatus.Text = "Não foi possível identificar o adaptador de rede ativo.";
            return;
        }
        if (_dnsCts != null) return;

        var answer = MessageBox.Show(this,
            $"Aplicar {provider.Name} ({provider.Primary} e {provider.Secondary}) ao adaptador «{adapter}»?\n\n" +
            "Isto altera a configuração DNS IPv4 do Windows para este adaptador. A autorização de administrador será pedida a seguir.",
            "FrameForge · Aplicar DNS", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        BtnDnsOne.IsEnabled = BtnDnsAll.IsEnabled = BtnDnsApply.IsEnabled = DnsProviderBox.IsEnabled = false;
        DnsStatus.Text = $"A aplicar {provider.Name}; confirma o pedido de administrador do Windows…";
        try
        {
            string script = "$ErrorActionPreference = 'Stop'; try { " +
                            $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex.Value} " +
                            $"-ServerAddresses '{provider.Primary}','{provider.Secondary}'; exit 0 " +
                            "} catch { exit 1 }";
            string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("Não foi possível iniciar a operação de alteração de DNS.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("O Windows não conseguiu aplicar os servidores DNS. Verifica as permissões e tenta novamente.");

            InitDnsProviders();
            DnsStatus.Text = $"DNS {provider.Name} aplicado ao adaptador «{adapter}» ({provider.Primary}, {provider.Secondary}).";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            DnsStatus.Text = "Pedido de administrador cancelado; o DNS não foi alterado.";
        }
        catch (Exception ex)
        {
            DnsStatus.Text = "Não foi possível alterar o DNS: " + ex.Message;
        }
        finally
        {
            BtnDnsOne.IsEnabled = BtnDnsAll.IsEnabled = BtnDnsApply.IsEnabled = DnsProviderBox.IsEnabled = true;
        }
    }

    private async Task RunDnsAsync(bool all)
    {
        InitDnsProviders();
        if (_dnsCts != null) { _dnsCts.Cancel(); return; }
        var chosen = SelectedDnsProvider();
        if (chosen == null && !all) return;

        _dnsCts = new CancellationTokenSource();
        var ct = _dnsCts.Token;
        BtnDnsOne.IsEnabled = BtnDnsAll.IsEnabled = BtnDnsApply.IsEnabled = DnsProviderBox.IsEnabled = false;
        DnsResults.Children.Clear();
        DnsStatus.Text = all ? "A testar todos os fornecedores (3 consultas cada)…" : $"A testar {chosen!.Name} (3 consultas)…";

        try
        {
            var providers = all ? DnsBenchmark.Providers.ToList() : new List<DnsProvider> { chosen! };
            var results = await Task.WhenAll(providers.Select(p => DnsBenchmark.TestAsync(p, ct)));
            ct.ThrowIfCancellationRequested();

            var ordered = results.OrderBy(r => r.Ok ? 0 : 1).ThenBy(r => r.AverageMs ?? double.MaxValue).ToList();
            double? best = ordered.FirstOrDefault(r => r.Ok)?.AverageMs;
            foreach (var result in ordered) DnsResults.Children.Add(DnsRow(result, best));

            int ok = ordered.Count(r => r.Ok);
            DnsStatus.Text = $"Concluído às {DateTime.Now:HH:mm:ss} · {ok} de {ordered.Count} responderam" +
                             (best != null ? $" · o mais rápido foi {ordered.First(r => r.Ok).Provider} com {best:0} ms de média." : ".");
        }
        catch (OperationCanceledException) { DnsStatus.Text = "Teste de DNS cancelado."; }
        catch (Exception ex) { DnsStatus.Text = "Falhou: " + ex.Message; }
        finally
        {
            _dnsCts.Dispose();
            _dnsCts = null;
            BtnDnsOne.IsEnabled = BtnDnsAll.IsEnabled = BtnDnsApply.IsEnabled = DnsProviderBox.IsEnabled = true;
        }
    }

    private FrameworkElement DnsRow(DnsResult result, double? best)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, System.Windows.GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = result.Provider, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = result.Servers, FontSize = 11, Foreground = (System.Windows.Media.Brush)FindResource("Faint") });
        grid.Children.Add(text);

        bool isBest = result.Ok && best != null && Math.Abs((result.AverageMs ?? 0) - best.Value) < 0.01;
        var value = new TextBlock
        {
            Text = result.Ok ? $"{result.AverageMs:0} ms " + (isBest ? "· mais rápido" : "") : "falhou" + (result.Error != null ? $" ({result.Error})" : ""),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("Mono"),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = result.Ok
                ? (System.Windows.Media.Brush)FindResource(isBest ? "Accent2" : "Fg")
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)),
        };
        System.Windows.Controls.Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    // ================= Tema (escuro / claro) =================

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(!ThemeManager.IsLight);
        _settings.LightTheme = ThemeManager.IsLight;
        try { _settings.Save(); } catch { }
        UpdateThemeButton();
    }

    /// <summary>Mostra o ícone do modo para onde o clique leva (sol = ir para o claro, lua = ir para o escuro) e troca o logo.</summary>
    private void UpdateThemeButton()
    {
        bool light = ThemeManager.IsLight;
        BtnTheme.Content = light ? "\uE708" : "\uE706";
        BtnTheme.ToolTip = light ? "Modo escuro" : "Modo claro";
        TitleLogo.Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri(light ? "pack://application:,,,/Assets/logo-dark.png" : "pack://application:,,,/Assets/logo.png"));
    }
}
