using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ResetExplode;

public partial class MainWindow
{
    private List<UpdatableApp> _updatableApps = new();
    private Dictionary<string, string> _appExclusions = new(StringComparer.OrdinalIgnoreCase);
    private bool _appsLoadedOnce;
    private bool _appsScanning;
    private bool _appsInstalling;
    private bool _appsStopRequested;
    private bool _appsViewingExclusions;
    private int _appsUpdatedSessionCount;
    private string? _wingetVersion;
    private DateTime? _appsLastCheckedAt;
    private CancellationTokenSource? _appsCts;

    private void InitApps()
    {
        if (_appsScanning || _appsInstalling) return;
        if (!_appsLoadedOnce)
            _ = RefreshAppsListAsync();
        else
            UpdateAppsUi();
    }

    private async void BtnAppsRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_appsScanning || _appsInstalling) return;
        await RefreshAppsListAsync();
    }

    private async void ChkAppsIncludeUnknown_Click(object sender, RoutedEventArgs e)
    {
        if (_appsScanning || _appsInstalling) return;
        await RefreshAppsListAsync();
    }

    private async Task RefreshAppsListAsync(bool keepStatusMessage = false)
    {
        if (_appsScanning) return;

        _appsScanning = true;
        _appsLoadedOnce = true;

        _appsCts?.Cancel();
        _appsCts?.Dispose();
        _appsCts = new CancellationTokenSource();
        var ct = _appsCts.Token;

        SetAppsControlsState();
        AppsProgress.ScaleX = 0;

        if (!keepStatusMessage)
            AppsStatus.Text = "Verificando atualizações disponíveis via winget…";

        ShowAppsEmptyState(
            "\uE895",
            "Accent",
            "Verificando aplicativos instalados…",
            "Consultando o winget para listar atualizações disponíveis. Isso leva apenas alguns segundos.");

        try
        {
            bool includeUnknown = ChkAppsIncludeUnknown.IsChecked == true;
            var check = await AppInstaller.CheckUpdatesAsync(includeUnknown, ct);

            _appExclusions = AppInstaller.LoadExclusions();
            _wingetVersion = check.WingetVersion;
            _appsLastCheckedAt = DateTime.Now;

            foreach (var app in check.Apps)
                app.IsExcluded = _appExclusions.ContainsKey(app.WingetId);

            _updatableApps = check.Apps;

            BtnAppsStore.Visibility = check.WingetMissing ? Visibility.Visible : Visibility.Collapsed;

            if (check.UnknownVersionCount > 0 && !includeUnknown)
            {
                AppsUnknownHint.Text = check.UnknownVersionCount == 1
                    ? "1 pacote instalado tem versão local desconhecida. Ative \"Incluir versões desconhecidas\" para exibi-lo."
                    : $"{check.UnknownVersionCount} pacotes instalados têm versão local desconhecida. Ative \"Incluir versões desconhecidas\" para exibi-los.";
                AppsUnknownHint.Visibility = Visibility.Visible;
            }
            else
            {
                AppsUnknownHint.Visibility = Visibility.Collapsed;
            }

            if (check.WingetMissing)
            {
                AppsStatus.Text = check.ErrorMessage ?? "O winget não foi encontrado.";
                ShowAppsEmptyState(
                    "\uE7BA",
                    "Warn",
                    "Winget não encontrado",
                    "Instale ou atualize o \"Instalador de Aplicativo\" pela Microsoft Store para detectar e atualizar seus programas automaticamente.");
            }
            else if (!string.IsNullOrWhiteSpace(check.ErrorMessage) && _updatableApps.Count == 0)
            {
                AppsStatus.Text = check.ErrorMessage;
                ShowAppsEmptyState(
                    "\uE783",
                    "Warn",
                    "Não foi possível concluir a verificação",
                    check.ErrorMessage);
            }
            else if (!keepStatusMessage)
            {
                int visiblePending = _updatableApps.Count(a => !a.IsExcluded && a.HasUpdate);
                AppsStatus.Text = visiblePending == 0
                    ? "Todos os seus aplicativos monitorados estão atualizados."
                    : $"{visiblePending} {(visiblePending == 1 ? "aplicativo com atualização disponível" : "aplicativos com atualizações disponíveis")}. Selecione o que deseja atualizar.";
            }
        }
        catch (OperationCanceledException)
        {
            if (!keepStatusMessage)
                AppsStatus.Text = "Verificação cancelada.";
        }
        catch (Exception ex)
        {
            AppsStatus.Text = "Erro ao verificar atualizações: " + ex.Message;
            ShowAppsEmptyState("\uE783", "Bad", "Erro na verificação", ex.Message);
        }
        finally
        {
            _appsScanning = false;
            UpdateAppsUi();
        }
    }

    private List<UpdatableApp> GetFilteredVisibleApps()
    {
        var query = (AppsSearchBox?.Text ?? "").Trim();
        var visible = _updatableApps.Where(a => !a.IsExcluded);

        if (!string.IsNullOrEmpty(query))
        {
            visible = visible.Where(a =>
                a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                a.WingetId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                a.SourceDisplay.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return visible.ToList();
    }

    private void UpdateAppsUi()
    {
        var allVisible = _updatableApps.Where(a => !a.IsExcluded).ToList();
        var filtered = GetFilteredVisibleApps();

        int availableCount = allVisible.Count(a => a.HasUpdate);
        int selectedCount = allVisible.Count(a => a.Selected && a.CanSelect);
        int excludedCount = _appExclusions.Count;

        AppsStatAvailable.Text = _appsScanning ? "…" : availableCount.ToString();
        AppsStatSelected.Text = selectedCount.ToString();
        AppsStatUpdated.Text = _appsUpdatedSessionCount.ToString();
        AppsStatExcluded.Text = excludedCount.ToString();

        BtnAppsExclude.Content = _appsViewingExclusions
            ? $"VOLTAR ÀS ATUALIZAÇÕES ({availableCount})"
            : (excludedCount > 0 ? $"NUNCA ATUALIZAR ({excludedCount})" : "NUNCA ATUALIZAR");

        if (_appsLastCheckedAt.HasValue)
        {
            var verPart = string.IsNullOrWhiteSpace(_wingetVersion) ? "winget" : $"winget {_wingetVersion}";
            AppsMeta.Text = $"{verPart}  •  Última verificação às {_appsLastCheckedAt.Value:HH:mm:ss}";
        }
        else if (!_appsScanning)
        {
            AppsMeta.Text = "Pronto para verificar atualizações via winget.";
        }

        // Alterna botões de cabeçalho conforme visualização (Atualizações vs Nunca Atualizar)
        BtnAppsAll.Visibility = _appsViewingExclusions ? Visibility.Collapsed : Visibility.Visible;
        BtnAppsNone.Visibility = _appsViewingExclusions ? Visibility.Collapsed : Visibility.Visible;
        BtnAppsExclBack.Visibility = _appsViewingExclusions ? Visibility.Visible : Visibility.Collapsed;
        BtnAppsExclClearAll.Visibility = _appsViewingExclusions ? Visibility.Visible : Visibility.Collapsed;
        BtnAppsExclClearAll.IsEnabled = excludedCount > 0;
        ChkAppsIncludeUnknown.Visibility = _appsViewingExclusions ? Visibility.Collapsed : Visibility.Visible;

        if (!_appsScanning)
        {
            if (_appsViewingExclusions)
            {
                AppsList.Visibility = Visibility.Collapsed;
                var entries = BuildExcludedEntries();
                var query = (AppsSearchBox?.Text ?? "").Trim();
                var filteredExcl = string.IsNullOrEmpty(query)
                    ? entries
                    : entries.Where(e =>
                        e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.WingetId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

                AppsListHeader.Text = $"APLICATIVOS MARCADOS COMO \"NUNCA ATUALIZAR\" ({entries.Count})";

                if (entries.Count == 0)
                {
                    AppsExclusionsList.Visibility = Visibility.Collapsed;
                    ShowAppsEmptyState(
                        "\uE73E",
                        "Good",
                        "Nenhum aplicativo bloqueado",
                        "Quando você clicar em \"NUNCA ATUALIZAR\" em algum aplicativo da lista, ele aparecerá aqui e poderá ser reativado a qualquer momento.");
                }
                else if (filteredExcl.Count == 0)
                {
                    AppsExclusionsList.Visibility = Visibility.Collapsed;
                    ShowAppsEmptyState(
                        "\uE721",
                        "Muted",
                        "Nenhum resultado encontrado",
                        $"Nenhum aplicativo bloqueado corresponde a \"{query}\".");
                }
                else
                {
                    AppsEmptyPanel.Visibility = Visibility.Collapsed;
                    AppsExclusionsList.Visibility = Visibility.Visible;
                    AppsExclusionsList.ItemsSource = filteredExcl;
                }
            }
            else
            {
                AppsExclusionsList.Visibility = Visibility.Collapsed;

                if (BtnAppsStore.Visibility == Visibility.Visible)
                {
                    AppsList.Visibility = Visibility.Collapsed;
                    AppsEmptyPanel.Visibility = Visibility.Visible;
                }
                else if (allVisible.Count == 0)
                {
                    AppsList.Visibility = Visibility.Collapsed;
                    ShowAppsEmptyState(
                        "\uE73E",
                        "Good",
                        "Tudo atualizado!",
                        excludedCount > 0
                            ? $"Nenhuma atualização pendente. ({excludedCount} {(excludedCount == 1 ? "aplicativo está marcado" : "aplicativos estão marcados")} como \"Nunca atualizar\".)"
                            : "Nenhum aplicativo instalado precisa de atualização no momento.");
                }
                else if (filtered.Count == 0)
                {
                    AppsList.Visibility = Visibility.Collapsed;
                    ShowAppsEmptyState(
                        "\uE721",
                        "Muted",
                        "Nenhum resultado encontrado",
                        $"Nenhum aplicativo corresponde a \"{(AppsSearchBox?.Text ?? "").Trim()}\".");
                }
                else
                {
                    AppsEmptyPanel.Visibility = Visibility.Collapsed;
                    AppsList.Visibility = Visibility.Visible;
                    AppsList.ItemsSource = filtered;
                }

                AppsListHeader.Text = filtered.Count == allVisible.Count
                    ? $"APLICATIVOS COM ATUALIZAÇÃO DISPONÍVEL ({allVisible.Count})"
                    : $"APLICATIVOS EXIBIDOS ({filtered.Count} DE {allVisible.Count})";
            }
        }

        SetAppsControlsState();
    }

    private void SetAppsControlsState()
    {
        bool busy = _appsScanning || _appsInstalling;
        int selectedCount = _updatableApps.Count(a => !a.IsExcluded && a.Selected && a.CanSelect);
        bool hasVisible = _updatableApps.Any(a => !a.IsExcluded && a.CanSelect);

        BtnAppsRefresh.IsEnabled = !busy;
        BtnAppsExclude.IsEnabled = !busy;
        BtnAppsAll.IsEnabled = !busy && hasVisible;
        BtnAppsNone.IsEnabled = !busy && selectedCount > 0;
        ChkAppsIncludeUnknown.IsEnabled = !busy;

        if (_appsInstalling)
        {
            BtnAppsInstall.Content = _appsStopRequested ? "CANCELAR AGORA" : "PARAR";
            BtnAppsInstall.IsEnabled = true;
        }
        else
        {
            BtnAppsInstall.Content = selectedCount > 0
                ? $"ATUALIZAR SELECIONADOS ({selectedCount})"
                : "ATUALIZAR SELECIONADOS";
            BtnAppsInstall.IsEnabled = !_appsScanning && selectedCount > 0;
        }
    }

    private void ShowAppsEmptyState(string iconGlyph, string brushKey, string title, string desc)
    {
        AppsList.Visibility = Visibility.Collapsed;
        AppsEmptyPanel.Visibility = Visibility.Visible;
        AppsEmptyIcon.Text = iconGlyph;
        if (TryFindResource(brushKey) is Brush b)
            AppsEmptyIcon.Foreground = b;
        AppsEmptyTitle.Text = title;
        AppsEmptyDesc.Text = desc;
    }

    private void AppsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_appsScanning)
            UpdateAppsUi();
    }

    private void AppItemCheck_Click(object sender, RoutedEventArgs e)
    {
        UpdateAppsUi();
    }

    private void BtnAppsAll_Click(object sender, RoutedEventArgs e)
    {
        if (_appsScanning || _appsInstalling) return;
        foreach (var app in GetFilteredVisibleApps().Where(a => a.CanSelect))
            app.Selected = true;
        UpdateAppsUi();
    }

    private void BtnAppsNone_Click(object sender, RoutedEventArgs e)
    {
        if (_appsScanning || _appsInstalling) return;
        foreach (var app in _updatableApps)
            app.Selected = false;
        UpdateAppsUi();
    }

    private List<ExclusionEntry> BuildExcludedEntries()
    {
        var list = new List<ExclusionEntry>();
        foreach (var kv in _appExclusions)
        {
            var detected = _updatableApps.FirstOrDefault(a => a.WingetId.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
            list.Add(new ExclusionEntry
            {
                WingetId = kv.Key,
                Name = detected?.Name ?? kv.Value,
                HasAvailableUpdate = detected != null,
                IsExcluded = true
            });
        }
        list.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return list;
    }

    private void BtnAppsExclude_Click(object sender, RoutedEventArgs e)
    {
        if (_appsScanning || _appsInstalling) return;
        _appsViewingExclusions = !_appsViewingExclusions;
        if (_appsViewingExclusions)
            _appExclusions = AppInstaller.LoadExclusions();
        UpdateAppsUi();
    }

    private void BtnAppRestoreExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (_appsScanning || _appsInstalling) return;
        if (sender is not Button btn || btn.Tag is not ExclusionEntry entry) return;

        AppInstaller.SaveExclusion(entry.WingetId, false);
        _appExclusions.Remove(entry.WingetId);
        var app = _updatableApps.FirstOrDefault(a => a.WingetId.Equals(entry.WingetId, StringComparison.OrdinalIgnoreCase));
        if (app != null) app.IsExcluded = false;

        AppsStatus.Text = $"\"{entry.Name}\" voltou a ser monitorado nas atualizações.";
        UpdateAppsUi();
    }

    private void BtnAppsExclClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_appsScanning || _appsInstalling) return;
        AppInstaller.ClearExclusions();
        _appExclusions.Clear();
        foreach (var app in _updatableApps) app.IsExcluded = false;

        AppsStatus.Text = "Todos os aplicativos bloqueados foram reativados para atualização.";
        UpdateAppsUi();
    }

    private void BtnAppQuickExclude_Click(object sender, RoutedEventArgs e)
    {
        if (_appsScanning || _appsInstalling) return;
        if (sender is not Button btn || btn.Tag is not UpdatableApp app) return;

        app.IsExcluded = true;
        AppInstaller.SaveExclusion(app.WingetId, true, app.Name);
        _appExclusions[app.WingetId] = app.Name;
        AppsStatus.Text = $"\"{app.Name}\" marcado como \"Nunca atualizar\". Você pode reativá-lo a qualquer momento no botão NUNCA ATUALIZAR.";
        UpdateAppsUi();
    }

    private async void BtnAppSingleUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_appsScanning || _appsInstalling) return;
        if (sender is not Button btn || btn.Tag is not UpdatableApp app || !app.CanQuickUpdate) return;

        await RunAppsUpdateBatchAsync(new List<UpdatableApp> { app });
    }

    private async void BtnAppsInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_appsScanning) return;

        if (_appsInstalling)
        {
            if (!_appsStopRequested)
            {
                _appsStopRequested = true;
                AppsStatus.Text = "Parando após o aplicativo atual… Clique novamente em CANCELAR AGORA para interromper imediatamente.";
                SetAppsControlsState();
            }
            else
            {
                BtnAppsInstall.IsEnabled = false;
                AppsStatus.Text = "Cancelando atualização em andamento…";
                _appsCts?.Cancel();
            }
            return;
        }

        var selected = _updatableApps.Where(a => a.Selected && a.CanSelect && !a.IsExcluded).ToList();
        if (selected.Count == 0)
        {
            AppsStatus.Text = "Nenhum aplicativo selecionado para atualizar.";
            return;
        }

        await RunAppsUpdateBatchAsync(selected);
    }

    private async Task RunAppsUpdateBatchAsync(List<UpdatableApp> batch)
    {
        _appsInstalling = true;
        _appsStopRequested = false;

        _appsCts?.Cancel();
        _appsCts?.Dispose();
        _appsCts = new CancellationTokenSource();
        var ct = _appsCts.Token;

        foreach (var app in batch)
        {
            app.State = AppUpdateState.Queued;
            app.Status = "Aguardando na fila…";
            app.ErrorDetail = "";
        }

        SetAppsControlsState();
        AppsProgress.ScaleX = 0;

        int ok = 0;
        int done = 0;
        bool restartAny = false;
        var failed = new List<string>();

        foreach (var app in batch)
        {
            if (_appsStopRequested || ct.IsCancellationRequested)
            {
                app.State = AppUpdateState.Skipped;
                app.Status = "Não atualizado (interrompido pelo usuário).";
                continue;
            }

            app.State = AppUpdateState.Updating;
            app.Status = "Iniciando atualização…";
            AppsStatus.Text = $"Atualizando {app.Name} ({done + 1} de {batch.Count})…";

            var progress = new Progress<string>(stage =>
            {
                app.Status = stage;
            });

            try
            {
                var result = await AppInstaller.InstallAsync(app, ct, progress);
                app.Status = result.Message;
                app.ErrorDetail = result.Detail ?? "";

                if (result.Ok)
                {
                    ok++;
                    _appsUpdatedSessionCount++;
                    app.Selected = false;
                    app.State = result.Message.Contains("Já estava", StringComparison.OrdinalIgnoreCase)
                        ? AppUpdateState.AlreadyUpToDate
                        : AppUpdateState.Updated;
                    if (result.RestartRecommended) restartAny = true;
                }
                else
                {
                    app.State = AppUpdateState.Failed;
                    failed.Add(app.Name);
                }
            }
            catch (OperationCanceledException)
            {
                app.State = AppUpdateState.Skipped;
                app.Status = "Cancelado pelo usuário.";
                _appsStopRequested = true;
            }
            catch (Exception ex)
            {
                app.State = AppUpdateState.Failed;
                app.Status = "Falhou: " + ex.Message;
                app.ErrorDetail = ex.ToString();
                failed.Add(app.Name);
            }

            done++;
            AppsProgress.ScaleX = (double)done / batch.Count;
            UpdateAppsUi();
        }

        _appsInstalling = false;

        var summary = $"{ok} de {batch.Count} {(batch.Count == 1 ? "aplicativo atualizado" : "aplicativos atualizados")}.";
        if (restartAny) summary += " Reinicialização recomendada para concluir alguns ajustes.";
        if (failed.Count > 0) summary += " Falharam: " + string.Join(", ", failed) + " (passe o mouse no erro para detalhes).";
        if (_appsStopRequested) summary += " Processo interrompido.";

        AppsStatus.Text = summary;
        UpdateAppsUi();
    }

    private void BtnAppsStore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?productid=9NBLGGH4NNS1") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppsStatus.Text = "Não foi possível abrir a Microsoft Store: " + ex.Message;
        }
    }

    private void BtnWinstall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://winstall.app/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppsStatus.Text = "Não foi possível abrir o winstall.app: " + ex.Message;
        }
    }

    private void WinstallLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppsStatus.Text = "Não foi possível abrir o link: " + ex.Message;
        }
        e.Handled = true;
    }
}
