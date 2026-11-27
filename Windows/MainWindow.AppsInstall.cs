using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ResetExplode;

public partial class MainWindow
{
    private const int CatalogPageSize = 30;

    private bool _catalogInitialized;
    private bool _catalogLoading;
    private bool _catalogInstalling;
    private bool _catalogStopRequested;
    private bool _catalogViewingQueue;
    private int _catalogInstalledSessionCount;

    private CatalogCategory _activeCategory = WinstallApiClient.Categories[0];
    private CatalogPack? _activePack;
    private List<CatalogApp> _catalogDisplayedApps = new();
    private List<CatalogPack> _catalogPacks = new();
    private int _catalogCurrentTotal;
    private int _catalogCurrentOffset;

    // Fila persistente de instalação em lote (mantém selecionados mesmo ao trocar categoria/busca)
    private readonly Dictionary<string, CatalogApp> _catalogQueue = new(StringComparer.OrdinalIgnoreCase);

    // IDs já instalados no PC (carregado em background para exibir badge "JÁ INSTALADO")
    private HashSet<string> _localInstalledIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _localInstalledScanned;

    private DispatcherTimer? _catalogSearchTimer;
    private CancellationTokenSource? _catalogLoadCts;
    private CancellationTokenSource? _catalogInstallCts;

    private void InitAppsInstall()
    {
        if (!_catalogInitialized)
        {
            _catalogInitialized = true;
            BuildCatalogCategoryChips();

            _catalogSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
            _catalogSearchTimer.Tick += async (_, _) =>
            {
                _catalogSearchTimer.Stop();
                await LoadCatalogAsync(reset: true);
            };

            BtnCatalogStore.Visibility = AppInstaller.WingetAvailable ? Visibility.Collapsed : Visibility.Visible;
            if (!AppInstaller.WingetAvailable)
                CatalogStatus.Text = "O winget não foi encontrado neste PC. Instale o Instalador de Aplicativo na Microsoft Store para instalar programas.";

            _ = LoadLocalInstalledIdsAsync();
            _ = LoadCatalogAsync(reset: true);
        }
        else
        {
            UpdateCatalogUi();
        }
    }

    private void BuildCatalogCategoryChips()
    {
        CatalogCategoryChips.Children.Clear();
        bool first = true;
        foreach (var cat in WinstallApiClient.Categories)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (TryFindResource("Icons") is FontFamily iconsFont)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = cat.IconGlyph,
                    FontFamily = iconsFont,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
            }
            sp.Children.Add(new TextBlock
            {
                Text = cat.Label,
                VerticalAlignment = VerticalAlignment.Center
            });

            var chip = new RadioButton
            {
                GroupName = "CatalogCategories",
                Tag = cat,
                Content = sp,
                IsChecked = first
            };
            if (TryFindResource("Chip") is Style chipStyle)
                chip.Style = chipStyle;

            chip.Click += async (_, _) =>
            {
                if (_catalogInstalling) return;
                _catalogViewingQueue = false;
                _activePack = null;
                _activeCategory = cat;

                // Ao clicar numa categoria, limpa a busca para mostrar a categoria escolhida
                if (!string.IsNullOrWhiteSpace(CatalogSearchBox.Text))
                {
                    _catalogSearchTimer?.Stop();
                    CatalogSearchBox.Text = "";
                }
                await LoadCatalogAsync(reset: true);
            };

            CatalogCategoryChips.Children.Add(chip);
            first = false;
        }
    }

    private async Task LoadLocalInstalledIdsAsync()
    {
        if (_localInstalledScanned || !AppInstaller.WingetAvailable) return;
        _localInstalledScanned = true;
        try
        {
            var ids = await AppInstaller.GetInstalledPackageIdsAsync(CancellationToken.None);
            _localInstalledIds = ids;
            CatalogStatLocal.Text = ids.Count.ToString();

            foreach (var app in _catalogDisplayedApps)
                app.IsInstalledLocally = _localInstalledIds.Contains(app.WingetId);
            foreach (var app in _catalogQueue.Values)
                app.IsInstalledLocally = _localInstalledIds.Contains(app.WingetId);
        }
        catch { }
    }

    private async Task LoadCatalogAsync(bool reset)
    {
        if (_catalogInstalling) return;

        _catalogLoadCts?.Cancel();
        _catalogLoadCts?.Dispose();
        _catalogLoadCts = new CancellationTokenSource();
        var ct = _catalogLoadCts.Token;

        string query = (CatalogSearchBox.Text ?? "").Trim();
        bool isSearch = !string.IsNullOrEmpty(query);
        BtnCatalogClearSearch.Visibility = isSearch ? Visibility.Visible : Visibility.Collapsed;

        // Modo "Ver Fila": exibe apenas os itens no cesto de instalação em lote
        if (_catalogViewingQueue)
        {
            CatalogPacksCard.Visibility = Visibility.Collapsed;
            CatalogActivePackBar.Visibility = Visibility.Collapsed;
            CatalogAppsCard.Visibility = Visibility.Visible;
            BtnCatalogLoadMore.Visibility = Visibility.Collapsed;

            _catalogDisplayedApps = _catalogQueue.Values
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _catalogCurrentTotal = _catalogDisplayedApps.Count;
            UpdateCatalogUi();
            return;
        }

        // Modo "Pacotes Prontos" (sem busca e sem pacote aberto)
        if (!isSearch && _activeCategory.Key == "packs" && _activePack == null)
        {
            _catalogLoading = true;
            CatalogAppsCard.Visibility = Visibility.Collapsed;
            CatalogPacksCard.Visibility = Visibility.Visible;
            CatalogPacksCount.Text = "Carregando pacotes…";
            CatalogStatus.Text = "Carregando pacotes curados do winstall.app…";
            UpdateCatalogControls();

            try
            {
                var packs = await WinstallApiClient.GetPacksAsync(0, 20, ct);
                _catalogPacks = packs;
                CatalogPacksList.ItemsSource = _catalogPacks;
                CatalogPacksCount.Text = $"{_catalogPacks.Count} coleções carregadas";
                CatalogStatus.Text = _catalogPacks.Count > 0
                    ? "Escolha um pacote para ver os apps ou clique em \"+ ADICIONAR PACOTE\" para instalar todos em lote."
                    : "Não foi possível carregar os pacotes no momento. Tente outra categoria ou pesquise diretamente.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                CatalogStatus.Text = "Erro ao carregar pacotes: " + ex.Message;
            }
            finally
            {
                _catalogLoading = false;
                UpdateCatalogUi();
            }
            return;
        }

        // Modo Pacote Aberto (exibindo apps do pacote clicado)
        if (!isSearch && _activePack != null)
        {
            CatalogPacksCard.Visibility = Visibility.Collapsed;
            CatalogAppsCard.Visibility = Visibility.Visible;
            CatalogActivePackBar.Visibility = Visibility.Visible;
            CatalogActivePackTitle.Text = $"{_activePack.Name} ({_activePack.AppCountText})";
            CatalogActivePackDesc.Text = _activePack.Description;
            BtnCatalogLoadMore.Visibility = Visibility.Collapsed;

            _catalogDisplayedApps = _activePack.Apps.Select(SyncCatalogItemWithState).ToList();
            _catalogCurrentTotal = _catalogDisplayedApps.Count;
            CatalogStatus.Text = $"Exibindo os {_catalogDisplayedApps.Count} aplicativos do pacote \"{_activePack.Name}\".";
            UpdateCatalogUi();
            return;
        }

        // Modo Catálogo (Em Alta, Mais Populares, Categorias ou Pesquisa)
        CatalogPacksCard.Visibility = Visibility.Collapsed;
        CatalogActivePackBar.Visibility = Visibility.Collapsed;
        CatalogAppsCard.Visibility = Visibility.Visible;

        if (reset)
        {
            _catalogCurrentOffset = 0;
            _catalogDisplayedApps = new List<CatalogApp>();
        }

        _catalogLoading = true;
        UpdateCatalogControls();

        if (reset)
        {
            ShowCatalogEmptyState(
                "\uE721",
                "Accent",
                isSearch ? $"Pesquisando \"{query}\"…" : $"Carregando {_activeCategory.Label}…",
                "Consultando o catálogo do winstall.app.");
        }

        try
        {
            CatalogPageResult result = isSearch
                ? await WinstallApiClient.SearchAppsAsync(query, _catalogCurrentOffset, CatalogPageSize, ct)
                : await WinstallApiClient.GetAppsByCategoryAsync(_activeCategory, _catalogCurrentOffset, CatalogPageSize, ct);

            var synced = result.Items.Select(SyncCatalogItemWithState).ToList();

            if (reset)
            {
                _catalogDisplayedApps = synced;
            }
            else
            {
                var existingIds = new HashSet<string>(_catalogDisplayedApps.Select(a => a.WingetId), StringComparer.OrdinalIgnoreCase);
                foreach (var item in synced)
                {
                    if (existingIds.Add(item.WingetId))
                        _catalogDisplayedApps.Add(item);
                }
            }

            _catalogCurrentTotal = Math.Max(result.Total, _catalogDisplayedApps.Count);
            _catalogCurrentOffset = _catalogDisplayedApps.Count;

            BtnCatalogLoadMore.Visibility = result.HasMore && _activeCategory.Key != "trending"
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage) && _catalogDisplayedApps.Count == 0)
            {
                CatalogStatus.Text = result.ErrorMessage;
                ShowCatalogEmptyState("\uE783", "Warn", "Não foi possível carregar o catálogo", result.ErrorMessage);
            }
            else if (_catalogDisplayedApps.Count == 0)
            {
                CatalogStatus.Text = isSearch
                    ? $"Nenhum aplicativo encontrado para \"{query}\"."
                    : "Nenhum aplicativo encontrado nesta categoria.";
            }
            else
            {
                var sourceNote = result.FromLocalFallback ? " (via pesquisa local do winget)" : " (via winstall.app)";
                CatalogStatus.Text = isSearch
                    ? $"{_catalogCurrentTotal} resultados encontrados para \"{query}\"{sourceNote}. Marque os apps para adicionar à fila em lote."
                    : $"Mostrando {_catalogDisplayedApps.Count} de {_catalogCurrentTotal} aplicativos em {_activeCategory.Label}{sourceNote}.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CatalogStatus.Text = "Erro ao consultar catálogo: " + ex.Message;
            ShowCatalogEmptyState("\uE783", "Bad", "Erro na consulta", ex.Message);
        }
        finally
        {
            _catalogLoading = false;
            UpdateCatalogUi();
        }
    }

    private CatalogApp SyncCatalogItemWithState(CatalogApp incoming)
    {
        if (_catalogQueue.TryGetValue(incoming.WingetId, out var queued))
        {
            queued.IsInstalledLocally = _localInstalledIds.Contains(queued.WingetId);
            return queued;
        }

        incoming.IsInstalledLocally = _localInstalledIds.Contains(incoming.WingetId);
        return incoming;
    }

    private void UpdateCatalogUi()
    {
        int queueCount = _catalogQueue.Count;
        CatalogStatQueue.Text = queueCount.ToString();
        CatalogStatInstalled.Text = _catalogInstalledSessionCount.ToString();
        CatalogStatShown.Text = _catalogViewingQueue
            ? queueCount.ToString()
            : (_activeCategory.Key == "packs" && _activePack == null && string.IsNullOrWhiteSpace(CatalogSearchBox.Text)
                ? $"{_catalogPacks.Count} packs"
                : (_catalogCurrentTotal > 0 ? _catalogCurrentTotal.ToString("N0") : _catalogDisplayedApps.Count.ToString()));

        BtnCatalogViewQueue.Content = _catalogViewingQueue
            ? $"VOLTAR AO CATÁLOGO ({queueCount})"
            : $"VER FILA ({queueCount})";

        string query = (CatalogSearchBox.Text ?? "").Trim();
        if (_catalogViewingQueue)
        {
            CatalogListHeader.Text = $"FILA DE INSTALAÇÃO EM LOTE ({_catalogDisplayedApps.Count})";
        }
        else if (!string.IsNullOrEmpty(query))
        {
            CatalogListHeader.Text = $"RESULTADOS PARA \"{query.ToUpperInvariant()}\" ({_catalogDisplayedApps.Count} DE {_catalogCurrentTotal})";
        }
        else if (_activePack != null)
        {
            CatalogListHeader.Text = $"PACOTE: {_activePack.Name.ToUpperInvariant()} ({_catalogDisplayedApps.Count})";
        }
        else
        {
            CatalogListHeader.Text = $"{_activeCategory.Label.ToUpperInvariant()} ({_catalogDisplayedApps.Count} DE {_catalogCurrentTotal})";
        }

        if (!_catalogLoading && CatalogAppsCard.Visibility == Visibility.Visible)
        {
            if (_catalogDisplayedApps.Count == 0)
            {
                CatalogList.Visibility = Visibility.Collapsed;
                if (_catalogViewingQueue)
                {
                    ShowCatalogEmptyState(
                        "\uE7B8",
                        "Muted",
                        "Sua fila de instalação está vazia",
                        "Navegue pelas categorias, pacotes ou pesquisa e ative o interruptor dos aplicativos que deseja instalar de uma só vez.");
                }
                else
                {
                    ShowCatalogEmptyState(
                        "\uE721",
                        "Muted",
                        "Nenhum aplicativo encontrado",
                        string.IsNullOrEmpty(query)
                            ? "Tente escolher outra categoria ou pesquisar pelo nome do programa."
                            : $"Nenhum resultado encontrado para \"{query}\". Tente outro termo ou o ID winget.");
                }
            }
            else
            {
                CatalogEmptyPanel.Visibility = Visibility.Collapsed;
                CatalogList.Visibility = Visibility.Visible;
                CatalogList.ItemsSource = null;
                CatalogList.ItemsSource = _catalogDisplayedApps;
            }
        }

        UpdateCatalogControls();
    }

    private void UpdateCatalogControls()
    {
        bool busy = _catalogInstalling;
        int queueCount = _catalogQueue.Count;
        bool hasWinget = AppInstaller.WingetAvailable;

        BtnCatalogClearQueue.IsEnabled = !busy && queueCount > 0;
        BtnCatalogViewQueue.IsEnabled = !busy;
        BtnCatalogSearch.IsEnabled = !busy;
        BtnCatalogClearSearch.IsEnabled = !busy;
        BtnCatalogLoadMore.IsEnabled = !busy && !_catalogLoading;
        BtnCatalogSelectVisible.IsEnabled = !busy && _catalogDisplayedApps.Any(a => a.CanSelect);
        BtnCatalogUnselectVisible.IsEnabled = !busy && _catalogDisplayedApps.Any(a => a.Selected);

        if (_catalogInstalling)
        {
            BtnCatalogInstallBatch.Content = _catalogStopRequested ? "CANCELAR AGORA" : "PARAR";
            BtnCatalogInstallBatch.IsEnabled = true;
        }
        else
        {
            BtnCatalogInstallBatch.Content = queueCount > 0
                ? $"INSTALAR SELECIONADOS ({queueCount})"
                : "INSTALAR SELECIONADOS";
            BtnCatalogInstallBatch.IsEnabled = hasWinget && queueCount > 0;
        }
    }

    private void ShowCatalogEmptyState(string iconGlyph, string brushKey, string title, string desc)
    {
        CatalogList.Visibility = Visibility.Collapsed;
        CatalogEmptyPanel.Visibility = Visibility.Visible;
        CatalogEmptyIcon.Text = iconGlyph;
        if (TryFindResource(brushKey) is Brush b)
            CatalogEmptyIcon.Foreground = b;
        CatalogEmptyTitle.Text = title;
        CatalogEmptyDesc.Text = desc;
    }

    // ---------------- Eventos de busca, categorias e pacotes ----------------

    private void CatalogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_catalogInitialized || _catalogInstalling) return;
        _catalogViewingQueue = false;
        _activePack = null;

        BtnCatalogClearSearch.Visibility = string.IsNullOrWhiteSpace(CatalogSearchBox.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        _catalogSearchTimer?.Stop();
        _catalogSearchTimer?.Start();
    }

    private async void CatalogSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _catalogInstalling) return;
        _catalogSearchTimer?.Stop();
        _catalogViewingQueue = false;
        _activePack = null;
        await LoadCatalogAsync(reset: true);
    }

    private async void BtnCatalogSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogInstalling) return;
        _catalogSearchTimer?.Stop();
        _catalogViewingQueue = false;
        _activePack = null;
        await LoadCatalogAsync(reset: true);
    }

    private async void BtnCatalogClearSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogInstalling) return;
        _catalogSearchTimer?.Stop();
        _catalogViewingQueue = false;
        _activePack = null;
        CatalogSearchBox.Text = "";
        await LoadCatalogAsync(reset: true);
    }

    private async void BtnCatalogLoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogLoading || _catalogInstalling) return;
        await LoadCatalogAsync(reset: false);
    }

    private async void BtnPackOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogInstalling) return;
        if (sender is not Button btn || btn.Tag is not CatalogPack pack) return;
        _catalogViewingQueue = false;
        _activePack = pack;
        await LoadCatalogAsync(reset: true);
    }

    private async void BtnPackBack_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogInstalling) return;
        _activePack = null;
        await LoadCatalogAsync(reset: true);
    }

    private void BtnPackAddAll_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogInstalling) return;
        if (sender is not Button btn || btn.Tag is not CatalogPack pack) return;

        int added = 0;
        foreach (var rawApp in pack.Apps)
        {
            var app = SyncCatalogItemWithState(rawApp);
            if (app.CanSelect && !_catalogQueue.ContainsKey(app.WingetId))
            {
                app.Selected = true;
                _catalogQueue[app.WingetId] = app;
                added++;
            }
        }

        CatalogStatus.Text = added > 0
            ? $"Adicionados {added} apps do pacote \"{pack.Name}\" à fila de instalação em lote (total na fila: {_catalogQueue.Count})."
            : $"Todos os apps do pacote \"{pack.Name}\" já estavam na fila.";
        UpdateCatalogUi();
    }

    // ---------------- Fila de instalação em lote ----------------

    private void CatalogItemCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not CatalogApp app) return;

        if (app.Selected)
        {
            _catalogQueue[app.WingetId] = app;
        }
        else
        {
            _catalogQueue.Remove(app.WingetId);
            if (_catalogViewingQueue)
                _catalogDisplayedApps.Remove(app);
        }

        UpdateCatalogUi();
    }

    private void BtnCatalogSelectVisible_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogInstalling) return;
        foreach (var app in _catalogDisplayedApps.Where(a => a.CanSelect))
        {
            app.Selected = true;
            _catalogQueue[app.WingetId] = app;
        }
        UpdateCatalogUi();
    }

    private void BtnCatalogUnselectVisible_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogInstalling) return;
        foreach (var app in _catalogDisplayedApps)
        {
            app.Selected = false;
            _catalogQueue.Remove(app.WingetId);
        }
        if (_catalogViewingQueue)
            _catalogDisplayedApps.Clear();
        UpdateCatalogUi();
    }

    private async void BtnCatalogViewQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogInstalling) return;
        _catalogViewingQueue = !_catalogViewingQueue;
        await LoadCatalogAsync(reset: true);
    }

    private void BtnCatalogClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogInstalling) return;
        foreach (var app in _catalogQueue.Values)
            app.Selected = false;
        _catalogQueue.Clear();

        if (_catalogViewingQueue)
            _catalogDisplayedApps.Clear();

        CatalogStatus.Text = "Fila de instalação em lote limpa.";
        UpdateCatalogUi();
    }

    // ---------------- Execução da instalação (individual e em lote) ----------------

    private async void BtnCatalogSingleInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogInstalling) return;
        if (!AppInstaller.WingetAvailable)
        {
            BtnCatalogStore.Visibility = Visibility.Visible;
            CatalogStatus.Text = "O winget não foi encontrado neste PC. Instale-o pela Microsoft Store.";
            return;
        }
        if (sender is not Button btn || btn.Tag is not CatalogApp app || !app.CanInstallNow) return;

        await RunCatalogInstallBatchAsync(new List<CatalogApp> { app });
    }

    private async void BtnCatalogInstallBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogInstalling)
        {
            if (!_catalogStopRequested)
            {
                _catalogStopRequested = true;
                CatalogStatus.Text = "Parando após a instalação atual… Clique novamente em CANCELAR AGORA para interromper imediatamente.";
                UpdateCatalogControls();
            }
            else
            {
                BtnCatalogInstallBatch.IsEnabled = false;
                CatalogStatus.Text = "Cancelando instalação em andamento…";
                _catalogInstallCts?.Cancel();
            }
            return;
        }

        if (!AppInstaller.WingetAvailable)
        {
            BtnCatalogStore.Visibility = Visibility.Visible;
            CatalogStatus.Text = "O winget não foi encontrado neste PC. Instale-o pela Microsoft Store.";
            return;
        }

        var batch = _catalogQueue.Values.Where(a => a.CanSelect).ToList();
        if (batch.Count == 0)
        {
            CatalogStatus.Text = "Nenhum aplicativo na fila para instalar.";
            return;
        }

        // Mostra automaticamente a fila durante a instalação em lote para o usuário acompanhar cada app
        _catalogViewingQueue = true;
        CatalogPacksCard.Visibility = Visibility.Collapsed;
        CatalogActivePackBar.Visibility = Visibility.Collapsed;
        CatalogAppsCard.Visibility = Visibility.Visible;
        BtnCatalogLoadMore.Visibility = Visibility.Collapsed;
        _catalogDisplayedApps = batch;
        UpdateCatalogUi();

        await RunCatalogInstallBatchAsync(batch);
    }

    private async Task RunCatalogInstallBatchAsync(List<CatalogApp> batch)
    {
        _catalogInstalling = true;
        _catalogStopRequested = false;

        _catalogInstallCts?.Cancel();
        _catalogInstallCts?.Dispose();
        _catalogInstallCts = new CancellationTokenSource();
        var ct = _catalogInstallCts.Token;

        foreach (var app in batch)
        {
            app.State = CatalogInstallState.Queued;
            app.Status = "Aguardando na fila…";
            app.ErrorDetail = "";
        }

        CatalogProgress.ScaleX = 0;
        UpdateCatalogControls();

        int ok = 0;
        int done = 0;
        bool restartAny = false;
        var failed = new List<string>();

        foreach (var app in batch)
        {
            if (_catalogStopRequested || ct.IsCancellationRequested)
            {
                app.State = CatalogInstallState.Skipped;
                app.Status = "Não instalado (interrompido pelo usuário).";
                continue;
            }

            app.State = CatalogInstallState.Installing;
            app.Status = "Iniciando instalação…";
            CatalogStatus.Text = $"Instalando {app.Name} ({done + 1} de {batch.Count})…";

            var progress = new Progress<string>(stage =>
            {
                app.Status = stage;
            });

            try
            {
                var result = await AppInstaller.InstallNewAppAsync(app.WingetId, app.Name, ct, progress);
                app.Status = result.Message;
                app.ErrorDetail = result.Detail ?? "";

                if (result.Ok)
                {
                    ok++;
                    _catalogInstalledSessionCount++;
                    app.Selected = false;
                    app.IsInstalledLocally = true;
                    _localInstalledIds.Add(app.WingetId);
                    _catalogQueue.Remove(app.WingetId);

                    app.State = result.Message.Contains("Já está instalado", StringComparison.OrdinalIgnoreCase)
                        ? CatalogInstallState.AlreadyInstalled
                        : CatalogInstallState.Installed;

                    if (result.RestartRecommended) restartAny = true;
                }
                else
                {
                    app.State = CatalogInstallState.Failed;
                    failed.Add(app.Name);
                }
            }
            catch (OperationCanceledException)
            {
                app.State = CatalogInstallState.Skipped;
                app.Status = "Cancelado pelo usuário.";
                _catalogStopRequested = true;
            }
            catch (Exception ex)
            {
                app.State = CatalogInstallState.Failed;
                app.Status = "Falhou: " + ex.Message;
                app.ErrorDetail = ex.ToString();
                failed.Add(app.Name);
            }

            done++;
            CatalogProgress.ScaleX = (double)done / batch.Count;
            CatalogStatLocal.Text = _localInstalledIds.Count.ToString();
            UpdateCatalogUi();
        }

        _catalogInstalling = false;

        var summary = $"{ok} de {batch.Count} {(batch.Count == 1 ? "aplicativo instalado" : "aplicativos instalados")}.";
        if (restartAny) summary += " Reinicialização recomendada para concluir alguns ajustes.";
        if (failed.Count > 0) summary += " Falharam: " + string.Join(", ", failed) + " (passe o mouse no erro para detalhes).";
        if (_catalogStopRequested) summary += " Processo interrompido.";

        CatalogStatus.Text = summary;
        UpdateCatalogUi();
    }
}
