using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ResetExplode;

public sealed class ExclusionEntry : INotifyPropertyChanged
{
    public string WingetId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool HasAvailableUpdate { get; init; }

    private bool _isExcluded;
    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (_isExcluded == value) return;
            _isExcluded = value;
            OnChanged();
            OnChanged(nameof(ActionText));
            OnChanged(nameof(StatusLabel));
        }
    }

    public string ActionText => IsExcluded ? "VOLTAR A ATUALIZAR" : "NUNCA ATUALIZAR";
    public string StatusLabel => IsExcluded
        ? (HasAvailableUpdate ? "Nunca atualizar (tem atualização pendente)" : "Nunca atualizar (salvo)")
        : "Permitido nas atualizações";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public partial class ExclusionsWindow : Window
{
    private readonly List<UpdatableApp> _detectedApps;
    private readonly List<ExclusionEntry> _entries = new();
    private TextBox _searchBox = null!;
    private ItemsControl _list = null!;
    private TextBlock _summaryText = null!;
    private TextBlock _emptyText = null!;
    private Button _btnClearAll = null!;

    public bool HasChanges { get; private set; }

    public ExclusionsWindow(List<UpdatableApp> detectedApps)
    {
        _detectedApps = detectedApps ?? new List<UpdatableApp>();
        BuildUi();
        LoadEntries();
    }

    private void BuildUi()
    {
        Title = "Aplicativos marcados como 'Nunca atualizar'";
        Width = 680;
        Height = 520;
        MinWidth = 540;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Cabeçalho
        var headerStack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var title = new TextBlock
        {
            Text = "Nunca atualizar",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");

        var subtitle = new TextBlock
        {
            Text = "Aplicativos marcados como \"Nunca atualizar\" ficam bloqueados permanentemente e não são atualizados nem em lote. Você pode reativá-los quando quiser.",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        headerStack.Children.Add(title);
        headerStack.Children.Add(subtitle);
        Grid.SetRow(headerStack, 0);
        root.Children.Add(headerStack);

        // Barra de busca e resumo
        var filterGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _searchBox = new TextBox
        {
            Tag = "Buscar por nome ou ID winget…",
            Height = 36,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _searchBox.TextChanged += (_, _) => ApplyFilter();
        Grid.SetColumn(_searchBox, 0);
        filterGrid.Children.Add(_searchBox);

        _summaryText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Margin = new Thickness(14, 0, 0, 0)
        };
        _summaryText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Grid.SetColumn(_summaryText, 1);
        filterGrid.Children.Add(_summaryText);

        Grid.SetRow(filterGrid, 1);
        root.Children.Add(filterGrid);

        // Card da lista
        var listCard = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 0, 16)
        };
        listCard.SetResourceReference(Border.BackgroundProperty, "Card");
        listCard.SetResourceReference(Border.BorderBrushProperty, "Line");

        var listContainer = new Grid();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        _list = new ItemsControl
        {
            ItemTemplate = BuildItemTemplate()
        };
        scroll.Content = _list;
        listContainer.Children.Add(scroll);

        _emptyText = new TextBlock
        {
            Text = "Nenhum aplicativo encontrado para gerenciar exclusões.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Visibility = Visibility.Collapsed
        };
        _emptyText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        listContainer.Children.Add(_emptyText);

        listCard.Child = listContainer;
        Grid.SetRow(listCard, 2);
        root.Children.Add(listCard);

        // Rodapé
        var footer = new Grid();
        _btnClearAll = new Button
        {
            Content = "REATIVAR TODOS",
            Padding = new Thickness(14, 8, 14, 8),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (TryFindResource("OutlineButton") is Style outlineStyle)
            _btnClearAll.Style = outlineStyle;
        _btnClearAll.Click += BtnClearAll_Click;
        footer.Children.Add(_btnClearAll);

        var btnClose = new Button
        {
            Content = "CONCLUIR",
            Padding = new Thickness(22, 8, 22, 8),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsCancel = true
        };
        if (TryFindResource("PrimaryButton") is Style primaryStyle)
            btnClose.Style = primaryStyle;
        btnClose.Click += (_, _) =>
        {
            DialogResult = HasChanges;
            Close();
        };
        footer.Children.Add(btnClose);

        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Content = root;
    }

    private DataTemplate BuildItemTemplate()
    {
        var template = new DataTemplate(typeof(ExclusionEntry));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        border.SetValue(Border.PaddingProperty, new Thickness(4, 10, 4, 10));

        var grid = new FrameworkElementFactory(typeof(Grid));
        var colInfo = new FrameworkElementFactory(typeof(ColumnDefinition));
        colInfo.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var colBtn = new FrameworkElementFactory(typeof(ColumnDefinition));
        colBtn.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        grid.AppendChild(colInfo);
        grid.AppendChild(colBtn);

        var infoStack = new FrameworkElementFactory(typeof(StackPanel));
        infoStack.SetValue(Grid.ColumnProperty, 0);
        infoStack.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var nameText = new FrameworkElementFactory(typeof(TextBlock));
        nameText.SetBinding(TextBlock.TextProperty, new Binding(nameof(ExclusionEntry.Name)));
        nameText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        nameText.SetValue(TextBlock.FontSizeProperty, 13.0);
        nameText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        infoStack.AppendChild(nameText);

        var metaPanel = new FrameworkElementFactory(typeof(StackPanel));
        metaPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        metaPanel.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));

        var idText = new FrameworkElementFactory(typeof(TextBlock));
        idText.SetBinding(TextBlock.TextProperty, new Binding(nameof(ExclusionEntry.WingetId)));
        idText.SetValue(TextBlock.FontSizeProperty, 11.0);
        idText.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        if (TryFindResource("Mono") is FontFamily mono)
            idText.SetValue(TextBlock.FontFamilyProperty, mono);
        metaPanel.AppendChild(idText);

        var sep = new FrameworkElementFactory(typeof(TextBlock));
        sep.SetValue(TextBlock.TextProperty, "  •  ");
        sep.SetValue(TextBlock.FontSizeProperty, 11.0);
        sep.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        metaPanel.AppendChild(sep);

        var statusText = new FrameworkElementFactory(typeof(TextBlock));
        statusText.SetBinding(TextBlock.TextProperty, new Binding(nameof(ExclusionEntry.StatusLabel)));
        statusText.SetValue(TextBlock.FontSizeProperty, 11.0);
        statusText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        metaPanel.AppendChild(statusText);

        infoStack.AppendChild(metaPanel);
        grid.AppendChild(infoStack);

        var btnToggle = new FrameworkElementFactory(typeof(Button));
        btnToggle.SetValue(Grid.ColumnProperty, 1);
        btnToggle.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ExclusionEntry.ActionText)));
        btnToggle.SetBinding(FrameworkElement.TagProperty, new Binding("."));
        btnToggle.SetValue(Control.PaddingProperty, new Thickness(12, 5, 12, 5));
        btnToggle.SetValue(Control.FontSizeProperty, 11.0);
        btnToggle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        btnToggle.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
        if (TryFindResource("OutlineButton") is Style outlineStyle)
            btnToggle.SetValue(FrameworkElement.StyleProperty, outlineStyle);
        btnToggle.AddHandler(Button.ClickEvent, new RoutedEventHandler(BtnToggleItem_Click));
        grid.AppendChild(btnToggle);

        border.AppendChild(grid);
        template.VisualTree = border;
        return template;
    }

    private void LoadEntries()
    {
        _entries.Clear();
        var saved = AppInstaller.LoadExclusions();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Primeiro, todos os apps detectados na varredura atual
        foreach (var app in _detectedApps)
        {
            if (!seen.Add(app.WingetId)) continue;
            bool isExcl = saved.ContainsKey(app.WingetId) || app.IsExcluded;
            _entries.Add(new ExclusionEntry
            {
                WingetId = app.WingetId,
                Name = app.Name,
                HasAvailableUpdate = true,
                IsExcluded = isExcl
            });
        }

        // Depois, exclusões salvas no Registro que não estão na lista atual de updates
        foreach (var kv in saved)
        {
            if (!seen.Add(kv.Key)) continue;
            _entries.Add(new ExclusionEntry
            {
                WingetId = kv.Key,
                Name = kv.Value,
                HasAvailableUpdate = false,
                IsExcluded = true
            });
        }

        // Ordena: excluídos primeiro, depois alfabético
        _entries.Sort((a, b) =>
        {
            int cmp = b.IsExcluded.CompareTo(a.IsExcluded);
            return cmp != 0 ? cmp : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        });

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = (_searchBox.Text ?? "").Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _entries
            : _entries.Where(e =>
                e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.WingetId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        _list.ItemsSource = filtered;
        _emptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int excludedCount = _entries.Count(e => e.IsExcluded);
        _summaryText.Text = excludedCount == 1 ? "1 bloqueado" : $"{excludedCount} bloqueados";
        _btnClearAll.IsEnabled = excludedCount > 0;
    }

    private void BtnToggleItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ExclusionEntry entry) return;

        bool newState = !entry.IsExcluded;
        entry.IsExcluded = newState;
        AppInstaller.SaveExclusion(entry.WingetId, newState, entry.Name);

        var app = _detectedApps.FirstOrDefault(a => a.WingetId.Equals(entry.WingetId, StringComparison.OrdinalIgnoreCase));
        if (app != null)
            app.IsExcluded = newState;

        HasChanges = true;
        ApplyFilter();
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        AppInstaller.ClearExclusions();
        foreach (var entry in _entries)
            entry.IsExcluded = false;
        foreach (var app in _detectedApps)
            app.IsExcluded = false;

        HasChanges = true;
        ApplyFilter();
    }
}
