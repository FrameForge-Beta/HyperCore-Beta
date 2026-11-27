using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FrameForge.Core;
using FrameForge.Optimize;

namespace FrameForge.Pages;

public sealed partial class MaintenancePage : UserControl
{
    private readonly StackPanel _content = new();
    private readonly ComboBox _diskCleanupVolume = new();
    private readonly TextBlock _tempResult = new();
    private readonly TextBlock _defragResult = new();
    private readonly StackPanel _volumeChecks = new();

    private readonly Button _cleanTempButton;
    private readonly Button _defragButton;

    private List<MaintenanceVolume> _volumes = new();

    public MaintenancePage()
    {
        InitializeComponent();
        Ui.UseTheme(this);
        _cleanTempButton = Ui.Btn("Limpar temporários antigos", CleanTemp_Click, "BtnAccent");
        _defragButton = Ui.Btn("Desfragmentar HDDs selecionados", Defragment_Click, "BtnAccent");
        _content.Margin = new Thickness(32, 4, 32, 24);
        BuildTempCard();
        BuildDiskCleanupCard();
        BuildDefragCard();

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _content };
        Loaded += async (_, _) => await RefreshVolumesAsync();
    }

    private void BuildTempCard()
    {
        var body = new StackPanel();
        body.Children.Add(Ui.T("Ficheiros temporários", "H2"));
        body.Children.Add(Ui.T("Remove ficheiros com mais de 24 horas nas pastas temporárias do utilizador e do Windows.", "MutedText"));
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
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        _diskCleanupVolume.Width = 360; _diskCleanupVolume.MaxWidth = 420; _diskCleanupVolume.Margin = new Thickness(0, 0, 8, 8);
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
        body.Children.Add(Ui.T("Só unidades identificadas como HDD podem ser selecionadas.", "MutedText"));
        var refresh = Ui.Btn("Atualizar unidades", RefreshVolumes_Click);
        refresh.Margin = new Thickness(0, 14, 8, 8);
        body.Children.Add(refresh);
        _volumeChecks.Margin = new Thickness(0, 4, 0, 8); _volumeChecks.MaxHeight = 180;
        body.Children.Add(_volumeChecks);
        _defragButton.Margin = new Thickness(0, 4, 8, 0);
        body.Children.Add(_defragButton);
        _defragResult.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(_defragResult);
        _content.Children.Add(Ui.Card(body));
    }

    private async Task RefreshVolumesAsync()
    {
        try
        {
            string? cleanupSelection = SelectedVolume(_diskCleanupVolume)?.Letter;
            _volumes = await Task.Run(MaintenanceService.ReadVolumes);
            PopulateVolumeChoices(_diskCleanupVolume, cleanupSelection);

            _volumeChecks.Children.Clear();
            foreach (var volume in _volumes)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                if (volume.CanDefragment)
                {
                    var check = new CheckBox { Tag = volume.Letter, VerticalAlignment = VerticalAlignment.Center, ToolTip = "HDD confirmado." };
                    row.Children.Add(check);
                }
                else
                {
                    var blocked = Ui.T("X", "Body", color: Ui.Res("Bad"), weight: FontWeights.Bold);
                    blocked.HorizontalAlignment = HorizontalAlignment.Center;
                    blocked.VerticalAlignment = VerticalAlignment.Center;
                    blocked.ToolTip = "Bloqueado: SSD ou tipo não identificado.";
                    row.Children.Add(blocked);
                }
                var label = Ui.T(volume.Description, "Body");
                label.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                _volumeChecks.Children.Add(row);
            }
            if (_volumes.Count == 0) _volumeChecks.Children.Add(Ui.T("Não foram encontradas unidades fixas disponíveis.", "MutedText"));
        }
        catch (Exception ex) { ShowError("Não foi possível consultar as unidades", ex); }
    }

    private async void RefreshVolumes_Click(object sender, RoutedEventArgs e) => await RefreshVolumesAsync();

    private void PopulateVolumeChoices(ComboBox combo, string? selectedLetter)
    {
        combo.Items.Clear();
        foreach (var volume in _volumes)
        {
            var label = Ui.T(volume.Description, "Body");
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = volume.Letter, ToolTip = volume.Description });
        }
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => ((string)item.Tag).Equals(selectedLetter, StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private MaintenanceVolume? SelectedVolume(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string != null
            ? _volumes.FirstOrDefault(v => v.Letter == (string)(combo.SelectedItem as ComboBoxItem)!.Tag)
            : null;

    private async void CleanTemp_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Serão removidos apenas ficheiros com mais de 24 horas nas pastas temporárias conhecidas. Continuar?",
                "Limpar temporários", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _cleanTempButton.IsEnabled = false;
        _tempResult.Text = "A limpar…";
        try
        {
            var result = await Task.Run(() => MaintenanceService.CleanTemporaryFiles(
                text => Dispatcher.InvokeAsync(() => _tempResult.Text = text)));
            _tempResult.Text = $"{result.FilesDeleted:N0} ficheiros removidos · {MaintenanceVolume.FormatBytes(result.BytesFreed)} libertados · {result.Skipped:N0} ignorados · {result.Errors:N0} erros.";
        }
        catch (Exception ex) { ShowError("A limpeza falhou", ex); }
        finally { _cleanTempButton.IsEnabled = true; }
    }

    private void OpenDiskCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVolume(_diskCleanupVolume) is not MaintenanceVolume volume) return;
        string cleanmgr = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cleanmgr.exe");
        try { Process.Start(new ProcessStartInfo(cleanmgr, $"/d {volume.Letter}") { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Não foi possível abrir a Limpeza de Disco", ex); }
    }

    private async void Defragment_Click(object sender, RoutedEventArgs e)
    {
        var selected = _volumeChecks.Children.OfType<Grid>()
            .SelectMany(row => row.Children.OfType<CheckBox>())
            .Where(c => c.IsEnabled && c.IsChecked == true && c.Tag is string)
            .Select(c => (string)c.Tag)
            .ToArray();
        if (selected.Length == 0) return;

        string list = string.Join(", ", selected);
        if (MessageBox.Show($"O Windows irá desfragmentar sequencialmente: {list}.\n\nConfirma?",
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
                        details.Add($"{result.Volume}: erro ({result.ExitCode}) — {result.Output}");
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
            if (failed > 0) MessageBox.Show(_defragResult.Text, "Resultado da desfragmentação", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _defragButton.IsEnabled = true; }
    }

    private void ShowError(string title, Exception ex) => MessageBox.Show($"{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
}