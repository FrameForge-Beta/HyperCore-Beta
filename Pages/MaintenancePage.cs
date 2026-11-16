using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FrameForge.Optimize;

namespace FrameForge.Pages;

public sealed class MaintenancePage : UserControl
{
    private readonly StackPanel _content = new();
    private readonly StackPanel _volumeChecks = new();
    private readonly ComboBox _diskCleanupVolume = new();
    private readonly ComboBox _scanVolume = new();
    private readonly TextBlock _tempResult = new();
    private readonly TextBlock _defragResult = new();
    private readonly TextBlock _scanPath = new();
    private readonly TextBlock _scanSummary = new();
    private readonly TreeView _scanTree = new();
    private readonly Dictionary<string, FolderTreeEntry> _scanEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button _cleanTempButton;
    private readonly Button _defragButton;
    private readonly Button _scanButton;
    private readonly Button _scanFolderButton;
    private readonly Button _cancelScanButton;
    private CancellationTokenSource? _scanCancellation;
    private int _scanGeneration;
    private List<MaintenanceVolume> _volumes = new();

    public MaintenancePage()
    {
        Ui.UseTheme(this);
        _cleanTempButton = Ui.Btn("Limpar temporários antigos", CleanTemp_Click, "BtnAccent");
        _defragButton = Ui.Btn("Desfragmentar HDDs selecionados", Defragment_Click, "BtnAccent");
        _scanButton = Ui.Btn("Analisar unidade", ScanVolume_Click, "BtnAccent");
        _scanFolderButton = Ui.Btn("Escolher pasta", ChooseFolder_Click);
        _cancelScanButton = Ui.Btn("Cancelar análise", CancelScan_Click);
        _cancelScanButton.IsEnabled = false;

        _content.Margin = new Thickness(32, 4, 32, 24);
        BuildTempCard();
        BuildDiskCleanupCard();
        BuildDefragCard();
        BuildFolderScanCard();

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _content,
        };
        Loaded += async (_, _) => await RefreshVolumesAsync();
    }

    private void BuildTempCard()
    {
        var body = new StackPanel();
        body.Children.Add(Ui.T("Ficheiros temporários", "H2"));
        body.Children.Add(Ui.T("Remove ficheiros com mais de 24 horas nas pastas temporárias do utilizador e do Windows. Ficheiros em uso e ligações simbólicas são ignorados.", "MutedText"));
        _cleanTempButton.Margin = new Thickness(0, 14, 8, 0);
        body.Children.Add(_cleanTempButton);
        _tempResult.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(_tempResult);
        _content.Children.Add(Ui.Card(body));
    }

    private void BuildDiskCleanupCard()
    {
        var body = new StackPanel();
        body.Children.Add(Ui.T("Limpeza de Disco do Windows", "H2"));
        body.Children.Add(Ui.T("Abre a ferramenta nativa do Windows para escolher categorias e confirmar o que queres apagar.", "MutedText"));
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        _diskCleanupVolume.Width = 360;
        _diskCleanupVolume.MaxWidth = 420;
        _diskCleanupVolume.Margin = new Thickness(0, 0, 8, 8);
        row.Children.Add(_diskCleanupVolume);
        var openCleanup = Ui.Btn("Abrir Limpeza de Disco", OpenDiskCleanup_Click);
        openCleanup.Margin = new Thickness(0, 0, 8, 8);
        row.Children.Add(openCleanup);
        body.Children.Add(row);
        _content.Children.Add(Ui.Card(body));
    }

    private void BuildDefragCard()
    {
        var body = new StackPanel();
        body.Children.Add(Ui.T("Desfragmentação de HDD", "H2"));
        body.Children.Add(Ui.T("Só unidades identificadas com segurança como HDD podem ser selecionadas. SSDs, unidades desconhecidas e unidades não disponíveis ficam bloqueadas. A fila é processada uma unidade de cada vez.", "MutedText"));
        var refresh = Ui.Btn("Atualizar unidades", RefreshVolumes_Click);
        refresh.Margin = new Thickness(0, 14, 8, 8);
        body.Children.Add(refresh);
        _volumeChecks.Margin = new Thickness(0, 4, 0, 8);
        _volumeChecks.MaxHeight = 180;
        body.Children.Add(_volumeChecks);
        _defragButton.Margin = new Thickness(0, 4, 8, 0);
        body.Children.Add(_defragButton);
        _defragResult.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(_defragResult);
        _content.Children.Add(Ui.Card(body));
    }

    private void BuildFolderScanCard()
    {
        var body = new StackPanel();
        body.Children.Add(Ui.T("Analisador de pastas", "H2"));
        body.Children.Add(Ui.T("Analisa uma unidade ou pasta e ordena as subpastas pelo espaço ocupado. Expande as pastas maiores para encontrar o que está a usar espaço. A análise é apenas de leitura, pode demorar e não segue ligações simbólicas.", "MutedText"));
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        _scanVolume.Width = 360;
        _scanVolume.MaxWidth = 420;
        _scanVolume.Margin = new Thickness(0, 0, 8, 8);
        buttons.Children.Add(_scanVolume);
        foreach (var button in new[] { _scanButton, _scanFolderButton, _cancelScanButton })
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            buttons.Children.Add(button);
        }
        body.Children.Add(buttons);
        _scanPath.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(_scanPath);
        _scanSummary.Margin = new Thickness(0, 4, 0, 8);
        body.Children.Add(_scanSummary);
        _scanTree.MaxHeight = 480;
        _scanTree.BorderThickness = new Thickness(0);
        _scanTree.Background = System.Windows.Media.Brushes.Transparent;
        _scanTree.Foreground = Ui.Res("Fg");
        body.Children.Add(_scanTree);
        _content.Children.Add(Ui.Card(body));
    }

    private async Task RefreshVolumesAsync()
    {
        try
        {
            string? cleanupSelection = SelectedVolume(_diskCleanupVolume)?.Letter;
            string? scanSelection = SelectedVolume(_scanVolume)?.Letter;
            _volumes = await Task.Run(MaintenanceService.ReadVolumes);
            PopulateVolumeChoices(_diskCleanupVolume, cleanupSelection);
            PopulateVolumeChoices(_scanVolume, scanSelection);

            _volumeChecks.Children.Clear();
            foreach (var volume in _volumes)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                if (volume.CanDefragment)
                {
                    var check = new CheckBox
                    {
                        Tag = volume.Letter,
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "HDD confirmado. Pode ser incluído na fila sequencial.",
                    };
                    row.Children.Add(check);
                }
                else
                {
                    var blocked = Ui.T("X", "Body", color: Ui.Res("Bad"), weight: FontWeights.Bold);
                    blocked.HorizontalAlignment = HorizontalAlignment.Center;
                    blocked.VerticalAlignment = VerticalAlignment.Center;
                    blocked.ToolTip = "Bloqueado: SSD ou tipo de unidade não identificado como HDD.";
                    row.Children.Add(blocked);
                }
                var label = Ui.T(volume.Description, "Body");
                label.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                _volumeChecks.Children.Add(row);
            }
            if (_volumes.Count == 0)
                _volumeChecks.Children.Add(Ui.T("Não foram encontradas unidades fixas disponíveis.", "MutedText"));
        }
        catch (Exception ex)
        {
            ShowError("Não foi possível consultar as unidades", ex);
        }
    }

    private async void RefreshVolumes_Click(object sender, RoutedEventArgs e) => await RefreshVolumesAsync();

    private void PopulateVolumeChoices(ComboBox combo, string? selectedLetter)
    {
        combo.Items.Clear();
        foreach (var volume in _volumes)
        {
            var label = Ui.T(volume.Description, "Body");
            label.TextTrimming = System.Windows.TextTrimming.CharacterEllipsis;
            combo.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = volume,
                ToolTip = volume.Description,
            });
        }

        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => ((MaintenanceVolume)item.Tag).Letter.Equals(selectedLetter, StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static MaintenanceVolume? SelectedVolume(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as MaintenanceVolume;

    private async void ScanVolume_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVolume(_scanVolume) is not MaintenanceVolume volume)
        {
            MessageBox.Show("Seleciona uma unidade disponível para analisar.", "Analisador de pastas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await StartFolderScanAsync(volume.Letter + "\\");
    }

    private async void CleanTemp_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Serão removidos apenas ficheiros com mais de 24 horas nas pastas temporárias conhecidas. Ficheiros em uso serão ignorados. Continuar?",
                "Limpar temporários", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _cleanTempButton.IsEnabled = false;
        _tempResult.Text = "A analisar e limpar…";
        try
        {
            var result = await Task.Run(() => MaintenanceService.CleanTemporaryFiles(
                text => Dispatcher.InvokeAsync(() => _tempResult.Text = text)));
            _tempResult.Text = $"{result.FilesDeleted:N0} ficheiros removidos · {MaintenanceVolume.FormatBytes(result.BytesFreed)} libertados · {result.Skipped:N0} ignorados · {result.Errors:N0} erros.";
        }
        catch (Exception ex) { ShowError("A limpeza de temporários falhou", ex); }
        finally { _cleanTempButton.IsEnabled = true; }
    }

    private void OpenDiskCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVolume(_diskCleanupVolume) is not MaintenanceVolume volume)
        {
            MessageBox.Show("Seleciona uma unidade disponível.", "Limpeza de Disco", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string cleanmgr = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cleanmgr.exe");
        try
        {
            if (!File.Exists(cleanmgr))
                throw new FileNotFoundException("A ferramenta Limpeza de Disco do Windows não foi encontrada.", cleanmgr);
            Process.Start(new ProcessStartInfo(cleanmgr, $"/d {volume.Letter}") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("Não foi possível abrir a Limpeza de Disco do Windows", ex); }
    }

    private async void Defragment_Click(object sender, RoutedEventArgs e)
    {
        var selected = _volumeChecks.Children.OfType<Grid>()
            .SelectMany(row => row.Children.OfType<CheckBox>())
            .Where(c => c.IsEnabled && c.IsChecked == true && c.Tag is string)
            .Select(c => (string)c.Tag)
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("Seleciona pelo menos um HDD confirmado. SSDs e unidades desconhecidas não podem ser desfragmentados por esta ferramenta.",
                "Desfragmentação", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string list = string.Join(", ", selected);
        if (MessageBox.Show($"O Windows irá desfragmentar sequencialmente: {list}.\n\nA operação pode demorar. Confirma que queres continuar?",
                "Confirmar desfragmentação", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _defragButton.IsEnabled = false;
        int succeeded = 0, failed = 0;
        var details = new List<string>();
        try
        {
            foreach (string volume in selected)
            {
                try
                {
                    var result = await MaintenanceService.DefragmentHddAsync(
                        volume, text => _defragResult.Text = text);
                    if (result.ExitCode == 0)
                    {
                        succeeded++;
                        details.Add($"{result.Volume}: concluído.");
                    }
                    else
                    {
                        failed++;
                        string diagnostic = result.Output.Length == 0 ? "sem detalhes" : result.Output;
                        details.Add($"{result.Volume}: erro ({result.ExitCode}) — {diagnostic}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    details.Add($"{volume}: falhou — {ex.Message}");
                }
                _defragResult.Text = string.Join(Environment.NewLine, details);
            }

            _defragResult.Text = $"Fila concluída: {succeeded} com sucesso, {failed} falharam.{Environment.NewLine}{string.Join(Environment.NewLine, details)}";
            if (failed > 0)
                MessageBox.Show(_defragResult.Text, "Resultado da desfragmentação", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _defragButton.IsEnabled = true; }
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Escolher pasta para analisar", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        await StartFolderScanAsync(dialog.FolderName);
    }

    private async Task StartFolderScanAsync(string folder)
    {
        int generation = ++_scanGeneration;
        _scanPath.Text = folder;
        _scanSummary.Text = "A analisar…";
        _scanTree.Items.Clear();
        _scanEntries.Clear();
        _scanButton.IsEnabled = false;
        _scanFolderButton.IsEnabled = false;
        _cancelScanButton.IsEnabled = true;
        _scanCancellation = new CancellationTokenSource();
        try
        {
            var result = await Task.Run(() => MaintenanceService.ScanFolder(
                folder,
                update => Dispatcher.BeginInvoke(() =>
                {
                    if (generation != _scanGeneration) return;
                    UpdateFolderTree(update);
                    _scanSummary.Text = $"A analisar… {update.FilesScanned:N0} ficheiros · {MaintenanceVolume.FormatBytes(update.TotalBytes)} lidos · {update.Skipped:N0} itens ignorados.";
                }),
                _scanCancellation.Token));
            UpdateFolderTree(result.Root, result.Root.Parent?.Bytes ?? result.Root.Bytes);
            SortTreeItems(_scanTree.Items);
            _scanSummary.Text = $"{MaintenanceVolume.FormatBytes(result.TotalBytes)} no total · {result.FilesScanned:N0} ficheiros · {result.Skipped:N0} itens inacessíveis ou ignorados.";
        }
        catch (OperationCanceledException) { _scanSummary.Text = "Análise cancelada."; }
        catch (Exception ex) { ShowError("A análise da pasta falhou", ex); }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            _scanButton.IsEnabled = true;
            _scanFolderButton.IsEnabled = true;
            _cancelScanButton.IsEnabled = false;
        }
    }

    private void UpdateFolderTree(FolderSizeNode node, long parentBytes)
    {
        UpdateFolderTree(new FolderScanUpdate(node.Path, node.Parent?.Path ?? "", node.Name, node.Bytes,
            parentBytes, node.Bytes, 0, 0, !_scanEntries.ContainsKey(node.Path)));
        foreach (var child in node.Children)
            UpdateFolderTree(child, node.Bytes);
    }

    private void UpdateFolderTree(FolderScanUpdate update)
    {
        if (!_scanEntries.TryGetValue(update.Path, out var entry))
        {
            if (!update.IsNew && update.ParentPath.Length != 0)
                return;

            var name = Ui.T(update.Name, "Body", color: Ui.Res("Fg"));
            name.VerticalAlignment = VerticalAlignment.Center;
            name.TextTrimming = System.Windows.TextTrimming.CharacterEllipsis;

            var fill = new Border { Background = Ui.Res("Accent"), HorizontalAlignment = HorizontalAlignment.Left };
            var bar = new Grid { Width = 96, Height = 8, Background = Ui.Res("Hover"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            bar.Children.Add(fill);

            var size = Ui.T(MaintenanceVolume.FormatBytes(update.Bytes), "Body", color: Ui.Res("Fg"));
            size.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
            size.HorizontalAlignment = HorizontalAlignment.Right;
            size.VerticalAlignment = VerticalAlignment.Center;

            var header = new Grid { MinHeight = 25, Margin = new Thickness(2, 1, 8, 1) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

            var glyph = Ui.T("\uE8B7", "Body", color: Ui.Res("Accent"));
            glyph.FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
            glyph.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(glyph);
            Grid.SetColumn(name, 1);
            header.Children.Add(name);
            Grid.SetColumn(bar, 2);
            header.Children.Add(bar);
            Grid.SetColumn(size, 3);
            header.Children.Add(size);

            var item = new TreeViewItem { Header = header, ToolTip = update.Path, Foreground = Ui.Res("Fg") };
            entry = new FolderTreeEntry(item, name, fill, size);
            item.Tag = entry;
            _scanEntries.Add(update.Path, entry);
            if (update.ParentPath.Length == 0)
                _scanTree.Items.Add(item);
            else if (_scanEntries.TryGetValue(update.ParentPath, out var parent))
                parent.Item.Items.Add(item);
            else
                _scanEntries.Remove(update.Path);
        }

        entry.Bytes = update.Bytes;
        entry.ParentBytes = update.ParentBytes;
        entry.Size.Text = MaintenanceVolume.FormatBytes(update.Bytes);
        entry.Name.Foreground = Ui.Res("Fg");
        entry.Fill.Width = 96 * (update.ParentBytes <= 0 ? 0 : Math.Clamp((double)update.Bytes / update.ParentBytes, 0, 1));
    }

    private static void SortTreeItems(ItemCollection items)
    {
        var children = items.OfType<TreeViewItem>().ToList();
        foreach (var child in children)
            SortTreeItems(child.Items);

        children = children
            .OrderByDescending(child => ((FolderTreeEntry)child.Tag!).Bytes)
            .ToList();
        foreach (var child in children)
            items.Remove(child);
        foreach (var child in children)
            items.Add(child);
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private void ShowError(string title, Exception ex) =>
        MessageBox.Show($"{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);

    private sealed class FolderTreeEntry(TreeViewItem item, TextBlock name, Border fill, TextBlock size)
    {
        public TreeViewItem Item { get; } = item;
        public TextBlock Name { get; } = name;
        public Border Fill { get; } = fill;
        public TextBlock Size { get; } = size;
        public long Bytes { get; set; }
        public long ParentBytes { get; set; }
    }
}
