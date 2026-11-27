using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ResetExplode;

public partial class MainWindow
{
    private List<InstalledProgram> _installedPrograms = new();
    private List<LeftoverItem> _currentLeftovers = new();
    private InstalledProgram? _leftoverTargetApp;
    private bool _leftoverForceMode;

    private bool _uninstallLoadedOnce;
    private bool _uninstallScanning;
    private bool _uninstallBusy;
    private int _uninstallRemovedSessionCount;

    // Estado do modal centralizado de aviso de perigo
    private InstalledProgram? _pendingDangerApp;
    private bool _pendingDangerForce;

    private void InitAppsUninstall()
    {
        if (_uninstallScanning || _uninstallBusy) return;
        if (!_uninstallLoadedOnce)
            _ = RefreshInstalledProgramsAsync();
        else
            UpdateUninstallUi();
    }

    private async void BtnUninstallRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstallScanning || _uninstallBusy) return;
        await RefreshInstalledProgramsAsync();
    }

    private async Task RefreshInstalledProgramsAsync()
    {
        if (_uninstallScanning) return;
        _uninstallScanning = true;
        _uninstallLoadedOnce = true;

        BtnUninstallRefresh.IsEnabled = false;
        BtnUninstallForceByName.IsEnabled = false;
        UninstallStatus.Text = "Listando programas instalados e aplicativos do sistema…";
        ShowUninstallEmpty("\uE72C", "Accent", "Carregando programas instalados…", "Lendo programas Win32 (64/32-bit) e aplicativos da Microsoft Store.");

        try
        {
            var programs = await AppUninstaller.GetInstalledProgramsAsync(CancellationToken.None);
            _installedPrograms = programs;
            UninstallMeta.Text = $"{programs.Count} itens detectados  •  Atualizado às {DateTime.Now:HH:mm:ss}";
            UninstallStatus.Text = "Clique em DESINSTALAR para usar o desinstalador oficial e procurar sobras, ou em FORÇAR para remoção direta com limpeza de resíduos.";
        }
        catch (Exception ex)
        {
            UninstallStatus.Text = "Erro ao listar programas: " + ex.Message;
            ShowUninstallEmpty("\uE783", "Bad", "Erro ao carregar programas", ex.Message);
        }
        finally
        {
            _uninstallScanning = false;
            BtnUninstallRefresh.IsEnabled = true;
            BtnUninstallForceByName.IsEnabled = true;
            UpdateUninstallUi();
        }
    }

    private void UpdateUninstallUi()
    {
        int total = _installedPrograms.Count(p => !p.IsRemoved);
        int desktop = _installedPrograms.Count(p => !p.IsRemoved && !p.IsStoreApp);
        int critical = _installedPrograms.Count(p => !p.IsRemoved && p.IsSystemCritical);

        UninstallStatTotal.Text = _uninstallScanning ? "…" : total.ToString();
        UninstallStatDesktop.Text = _uninstallScanning ? "…" : desktop.ToString();
        UninstallStatSystem.Text = _uninstallScanning ? "…" : critical.ToString();
        UninstallStatRemoved.Text = _uninstallRemovedSessionCount.ToString();

        if (_uninstallScanning) return;

        var query = (UninstallSearchBox?.Text ?? "").Trim();
        IEnumerable<InstalledProgram> filtered = _installedPrograms.Where(p => !p.IsRemoved);

        if (ChipUninstDesktop.IsChecked == true)
            filtered = filtered.Where(p => !p.IsStoreApp);
        else if (ChipUninstStore.IsChecked == true)
            filtered = filtered.Where(p => p.IsStoreApp);
        else if (ChipUninstSystem.IsChecked == true)
            filtered = filtered.Where(p => p.IsSystemCritical);

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(p =>
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.InstallLocation.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();
        UninstallListHeader.Text = list.Count == total
            ? $"PROGRAMAS INSTALADOS ({total})"
            : $"PROGRAMAS EXIBIDOS ({list.Count} DE {total})";

        if (list.Count == 0)
        {
            UninstallList.Visibility = Visibility.Collapsed;
            ShowUninstallEmpty(
                "\uE721",
                "Muted",
                "Nenhum programa encontrado",
                string.IsNullOrEmpty(query)
                    ? "Nenhum programa corresponde ao filtro selecionado."
                    : $"Nenhum aplicativo corresponde a \"{query}\".");
        }
        else
        {
            UninstallEmptyPanel.Visibility = Visibility.Collapsed;
            UninstallList.Visibility = Visibility.Visible;
            UninstallList.ItemsSource = list;
        }
    }

    private void ShowUninstallEmpty(string icon, string brushKey, string title, string desc)
    {
        UninstallList.Visibility = Visibility.Collapsed;
        UninstallEmptyPanel.Visibility = Visibility.Visible;
        UninstallEmptyIcon.Text = icon;
        if (TryFindResource(brushKey) is Brush b)
            UninstallEmptyIcon.Foreground = b;
        UninstallEmptyTitle.Text = title;
        UninstallEmptyDesc.Text = desc;
    }

    private void UninstallSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uninstallScanning) UpdateUninstallUi();
    }

    private void UninstFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!_uninstallScanning) UpdateUninstallUi();
    }

    // ---------------- Desinstalação e Aviso Centralizado de Perigo ----------------

    private async void BtnAppUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstallBusy || _uninstallScanning) return;
        if (sender is not Button btn || btn.Tag is not InstalledProgram app || !app.CanUninstall) return;

        if (app.IsSystemCritical)
        {
            ShowDangerModal(app, forceMode: false);
            return;
        }

        await ExecuteUninstallFlowAsync(app, forceMode: false);
    }

    private async void BtnAppForceUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstallBusy || _uninstallScanning) return;
        if (sender is not Button btn || btn.Tag is not InstalledProgram app || !app.CanUninstall) return;

        if (app.IsSystemCritical)
        {
            ShowDangerModal(app, forceMode: true);
            return;
        }

        await ExecuteUninstallFlowAsync(app, forceMode: true);
    }

    private void ShowDangerModal(InstalledProgram app, bool forceMode)
    {
        _pendingDangerApp = app;
        _pendingDangerForce = forceMode;

        DangerModalAppName.Text = app.Name;
        DangerModalReason.Text = string.IsNullOrWhiteSpace(app.DangerReason)
            ? "Este aplicativo foi identificado como um componente crítico do sistema."
            : app.DangerReason;
        BtnDangerModalConfirm.Content = forceMode ? "FORÇAR REMOÇÃO MESMO ASSIM" : "DESINSTALAR MESMO ASSIM";
        UninstallDangerOverlay.Visibility = Visibility.Visible;
    }

    private void BtnDangerModalCancel_Click(object sender, RoutedEventArgs e)
    {
        _pendingDangerApp = null;
        UninstallDangerOverlay.Visibility = Visibility.Collapsed;
    }

    private async void BtnDangerModalConfirm_Click(object sender, RoutedEventArgs e)
    {
        var app = _pendingDangerApp;
        bool force = _pendingDangerForce;
        _pendingDangerApp = null;
        UninstallDangerOverlay.Visibility = Visibility.Collapsed;

        if (app != null)
            await ExecuteUninstallFlowAsync(app, force);
    }

    private async Task ExecuteUninstallFlowAsync(InstalledProgram app, bool forceMode)
    {
        _uninstallBusy = true;
        BtnUninstallRefresh.IsEnabled = false;
        BtnUninstallForceByName.IsEnabled = false;

        try
        {
            if (!forceMode)
            {
                app.Status = "Executando desinstalador oficial…";
                UninstallStatus.Text = $"Desinstalando \"{app.Name}\"… Aguarde a conclusão do desinstalador.";

                var (ok, msg) = await AppUninstaller.RunStandardUninstallAsync(app, CancellationToken.None);
                app.Status = msg;
                UninstallStatus.Text = msg;

                if (!ok)
                {
                    // Se o usuário cancelou ou não havia desinstalador, não prossegue automaticamente para varredura
                    return;
                }
            }
            else
            {
                app.Status = "Modo forçado: procurando arquivos, atalhos e registro…";
                UninstallStatus.Text = $"Modo FORÇAR DESINSTALAÇÃO para \"{app.Name}\": varrendo pasta de instalação, AppData, atalhos e Registro…";

                // Para apps da Store, tenta primeiro remover o pacote Appx
                if (app.IsStoreApp)
                    await AppUninstaller.RunStandardUninstallAsync(app, CancellationToken.None);
            }

            // Passo estilo Revo Uninstaller: varredura de sobras/lixo deixados para trás
            await ScanAndShowLeftoversAsync(app, forceMode);
        }
        catch (Exception ex)
        {
            app.Status = "Erro: " + ex.Message;
            UninstallStatus.Text = "Erro durante a desinstalação: " + ex.Message;
        }
        finally
        {
            _uninstallBusy = false;
            BtnUninstallRefresh.IsEnabled = true;
            BtnUninstallForceByName.IsEnabled = true;
            UpdateUninstallUi();
        }
    }

    // ---------------- Forçar desinstalação por nome (para apps fora da lista) ----------------

    private void BtnUninstallForceByName_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstallBusy) return;
        bool show = UninstallForceNameBar.Visibility != Visibility.Visible;
        UninstallForceNameBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) UninstallForceNameBox.Focus();
    }

    private async void BtnUninstallForceScanRun_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstallBusy) return;
        var name = (UninstallForceNameBox.Text ?? "").Trim();
        if (name.Length < 4)
        {
            UninstallStatus.Text = "Digite pelo menos 4 caracteres com o nome do aplicativo para uma varredura segura.";
            return;
        }

        _uninstallBusy = true;
        try
        {
            var synthetic = new InstalledProgram { Name = name, Publisher = "" };
            UninstallForceNameBar.Visibility = Visibility.Collapsed;
            await ScanAndShowLeftoversAsync(synthetic, forceMode: true);
        }
        finally
        {
            _uninstallBusy = false;
        }
    }

    // ---------------- Painel de Limpeza de Resíduos (estilo Revo) ----------------

    private async Task ScanAndShowLeftoversAsync(InstalledProgram app, bool forceMode)
    {
        _leftoverTargetApp = app;
        _leftoverForceMode = forceMode;

        UninstallLeftoversOverlay.Visibility = Visibility.Visible;
        try { Activate(); } catch { }

        LeftoversTitle.Text = forceMode ? "REMOÇÃO FORÇADA E LIMPEZA DE RESÍDUOS" : "LIMPEZA DE RESÍDUOS (ESTILO REVO)";
        LeftoversAppHeading.Text = app.Name;
        LeftoversSubtitle.Text = "Procurando pastas residuais (AppData, ProgramData, Program Files), atalhos e chaves de Registro…";
        LeftoversList.Visibility = Visibility.Collapsed;
        LeftoversEmptyBox.Visibility = Visibility.Collapsed;
        BtnLeftoversDelete.IsEnabled = false;

        var found = await AppUninstaller.ScanLeftoversAsync(app, forceMode, CancellationToken.None);
        _currentLeftovers = found;

        if (found.Count == 0)
        {
            LeftoversSubtitle.Text = "Varredura concluída: nenhum lixo ou chave de registro residual encontrado.";
            LeftoversEmptyText.Text = $"Nenhum resíduo encontrado para \"{app.Name}\". Tudo limpo!";
            LeftoversEmptyBox.Visibility = Visibility.Visible;
            LeftoversList.Visibility = Visibility.Collapsed;
            BtnLeftoversDelete.IsEnabled = false;

            if (!string.IsNullOrWhiteSpace(app.Id))
            {
                app.IsRemoved = true;
                _uninstallRemovedSessionCount++;
                UpdateUninstallUi();
            }
        }
        else
        {
            LeftoversEmptyBox.Visibility = Visibility.Collapsed;
            LeftoversList.Visibility = Visibility.Visible;
            LeftoversList.ItemsSource = _currentLeftovers;
            UpdateLeftoversSummary();
        }
    }

    private void UpdateLeftoversSummary()
    {
        int total = _currentLeftovers.Count;
        int sel = _currentLeftovers.Count(i => i.Selected);
        long bytes = _currentLeftovers.Where(i => i.Selected).Sum(i => i.SizeBytes);
        string sizeStr = bytes >= 1024L * 1024
            ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
            : bytes >= 1024L ? $"{bytes / 1024.0:0.0} KB" : $"{bytes} B";

        LeftoversSubtitle.Text = $"{total} {(total == 1 ? "item residual encontrado" : "itens residuais encontrados")} • {sel} selecionados ({sizeStr}). Revise antes de limpar.";
        BtnLeftoversDelete.Content = sel > 0 ? $"LIMPAR RESÍDUOS SELECIONADOS ({sel})" : "LIMPAR RESÍDUOS SELECIONADOS";
        BtnLeftoversDelete.IsEnabled = sel > 0 && !_uninstallBusy;
    }

    private void LeftoverCheck_Click(object sender, RoutedEventArgs e) => UpdateLeftoversSummary();

    private void BtnLeftoversAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _currentLeftovers) item.Selected = true;
        UpdateLeftoversSummary();
    }

    private void BtnLeftoversNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _currentLeftovers) item.Selected = false;
        UpdateLeftoversSummary();
    }

    private async void BtnLeftoversDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstallBusy || _currentLeftovers.Count == 0) return;

        _uninstallBusy = true;
        BtnLeftoversDelete.IsEnabled = false;
        LeftoversSubtitle.Text = _leftoverForceMode
            ? "Encerrando processos do aplicativo e apagando arquivos e chaves de registro selecionados…"
            : "Apagando resíduos selecionados…";

        try
        {
            var result = await AppUninstaller.DeleteLeftoversAsync(
                _currentLeftovers,
                _leftoverTargetApp,
                killRunningProcesses: _leftoverForceMode,
                CancellationToken.None);

            string freedStr = result.FreedBytes >= 1024L * 1024
                ? $"{result.FreedBytes / (1024.0 * 1024.0):0.0} MB"
                : $"{result.FreedBytes / 1024.0:0.0} KB";

            UninstallStatus.Text = $"Limpeza concluída: {result.DeletedCount} itens removidos ({freedStr} liberados)" +
                                   (result.FailedCount > 0 ? $" • {result.FailedCount} itens bloqueados/em uso pelo Windows." : ".");

            if (_leftoverTargetApp != null && !string.IsNullOrWhiteSpace(_leftoverTargetApp.Id) && !_leftoverTargetApp.IsRemoved)
            {
                _leftoverTargetApp.IsRemoved = true;
                _uninstallRemovedSessionCount++;
            }

            _currentLeftovers.Clear();
            LeftoversList.ItemsSource = null;
            LeftoversList.Visibility = Visibility.Collapsed;
            LeftoversEmptyText.Text = UninstallStatus.Text;
            LeftoversEmptyBox.Visibility = Visibility.Visible;
            LeftoversSubtitle.Text = "Limpeza finalizada.";
        }
        catch (Exception ex)
        {
            UninstallStatus.Text = "Erro ao limpar resíduos: " + ex.Message;
        }
        finally
        {
            _uninstallBusy = false;
            UpdateUninstallUi();
        }
    }

    private void BtnLeftoversClose_Click(object sender, RoutedEventArgs e)
    {
        UninstallLeftoversOverlay.Visibility = Visibility.Collapsed;
        _currentLeftovers.Clear();
        _leftoverTargetApp = null;
    }
}
