using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FrameForge.Core;
using FrameForge.Optimize;

namespace FrameForge.Pages;

public sealed partial class MapaDiscoPage : UserControl
{
    private readonly StackPanel _content = new();
    private readonly MapaDiscoTreemap _treemap = new();
    private WrapPanel _mapaPainelDiscos = new();
    private Border _mapaVazio = new();
    private Border _mapaConteudo = new();
    private TextBlock _mapaStatus = new();
    private TextBlock _mapaCaminho = new();
    private Button _mapaVoltar = new();
    private StackPanel _mapaListaStack = new();
    private Button _btnPasta = new();

    private MapaDisco.Resultado? _mapaResultado;
    private MapaDisco.NoPasta? _mapaAtual;
    private CancellationTokenSource? _ctsMapa;

    public MapaDiscoPage()
    {
        Ui.UseTheme(this);
        _treemap.PastaClicada += EntrarMapa;

        _content.Margin = new Thickness(32, 4, 32, 24);

        // Título
        var lblTitulo = Ui.T("Mapa do disco", "H2");
        _content.Children.Add(lblTitulo);
        var lblDesc = Ui.T("Veja visualmente o que ocupa espaço no disco com um mapa de blocos (treemap). Cada bloco é uma pasta ou arquivo — quanto maior o bloco, mais espaço ocupa. Só lê: não apaga nada.", "MutedText");
        _content.Children.Add(lblDesc);

        // Botão para escolher pasta
        _btnPasta.Content = "Escolher pasta…";
        _btnPasta.Margin = new Thickness(0, 10, 0, 8);
        _btnPasta.Background = Ui.Res("Card") as Brush ?? Brushes.Transparent;
        _btnPasta.BorderBrush = Ui.Res("Line") as Brush ?? Brushes.Gray;
        _btnPasta.Foreground = Ui.Res("Text") as Brush ?? Brushes.White;
        _btnPasta.Click += async (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Escolha a pasta que você quer medir",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) await VarrerMapaAsync(dlg.FolderName!, 0L);
        };
        _content.Children.Add(_btnPasta);

        // Lista de discos disponíveis
        _mapaPainelDiscos = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        _content.Children.Add(_mapaPainelDiscos);

        // Estado vazio
        _mapaVazio = new Border
        {
            Background = Ui.Res("Card") as Brush ?? new SolidColorBrush(Color.FromRgb(30, 30, 36)),
            BorderBrush = Ui.Res("Line") as Brush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Top
        };
        var vazioPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        vazioPanel.Children.Add(new TextBlock
        {
            Text = "Escolha um disco ou uma pasta acima para ver o que ocupa espaço.",
            FontSize = 14,
            Margin = new Thickness(0, 10, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Res("Text") as Brush ?? Brushes.White
        });
        vazioPanel.Children.Add(new TextBlock
        {
            Text = "A leitura só olha a lista de arquivos e não abre nenhum deles.",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 560,
            Foreground = Ui.Res("Muted") as Brush ?? Brushes.Gray
        });
        _mapaVazio.Child = vazioPanel;
        _content.Children.Add(_mapaVazio);

        // Conteúdo do mapa
        _mapaConteudo = new Border
        {
            Background = Ui.Res("Card") as Brush ?? new SolidColorBrush(Color.FromRgb(30, 30, 36)),
            BorderBrush = Ui.Res("Line") as Brush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Margin = new Thickness(0),
            Visibility = Visibility.Collapsed
        };
        var conteudoGrid = new Grid();
        conteudoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        conteudoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var navegacao = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _mapaVoltar = Ui.Btn("‹ Voltar", (_, _) =>
        {
            if (_mapaAtual?.Pai is { } pai) MostrarMapa(pai);
        });
        _mapaVoltar.Background = Ui.Res("Card") as Brush ?? Brushes.Transparent;
        _mapaVoltar.BorderBrush = Ui.Res("Line") as Brush ?? Brushes.Gray;
        _mapaVoltar.Foreground = Ui.Res("Text") as Brush ?? Brushes.White;
        _mapaVoltar.IsEnabled = false;
        DockPanel.SetDock(_mapaVoltar, Dock.Left);
        navegacao.Children.Add(_mapaVoltar);
        _mapaCaminho = Ui.T("", "Body");
        _mapaCaminho.VerticalAlignment = VerticalAlignment.Center;
        _mapaCaminho.Margin = new Thickness(10, 0, 0, 0);
        _mapaCaminho.Foreground = Ui.Res("Text") as Brush ?? Brushes.White;
        navegacao.Children.Add(_mapaCaminho);
        conteudoGrid.Children.Add(navegacao);
        Grid.SetRow(navegacao, 0);

        var resultadoGrid = new Grid();
        resultadoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        resultadoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        resultadoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });

        var treemapPanel = new DockPanel { MinHeight = 400, MinWidth = 400 };
        treemapPanel.Children.Add(_treemap);
        resultadoGrid.Children.Add(treemapPanel);
        Grid.SetColumn(treemapPanel, 0);
        Grid.SetRow(treemapPanel, 0);

        var listaPanel = new Border
        {
            Background = Ui.Res("Card") as Brush ?? new SolidColorBrush(Color.FromRgb(30, 30, 36)),
            BorderBrush = Ui.Res("Line") as Brush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 10, 6, 6),
            Margin = new Thickness(0)
        };
        var listaContainer = new StackPanel();
        var lblListaTitulo = new TextBlock
        {
            Text = "Maiores pastas",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Res("Text") as Brush ?? Brushes.White,
            Margin = new Thickness(0, 0, 0, 6)
        };
        listaContainer.Children.Add(lblListaTitulo);

        _mapaListaStack = new StackPanel();
        var listaScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        listaScroll.Content = _mapaListaStack;
        listaContainer.Children.Add(listaScroll);
        listaPanel.Child = listaContainer;
        resultadoGrid.Children.Add(listaPanel);
        Grid.SetColumn(listaPanel, 2);
        Grid.SetRow(listaPanel, 0);

        conteudoGrid.Children.Add(resultadoGrid);
        Grid.SetRow(resultadoGrid, 1);

        _mapaConteudo.Child = conteudoGrid;
        _content.Children.Add(_mapaConteudo);

        _mapaStatus = new TextBlock { Text = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("Muted") as Brush ?? Brushes.Gray };
        _content.Children.Add(_mapaStatus);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _content,
            Background = new SolidColorBrush(Color.FromRgb(22, 26, 34))
        };

        Loaded += async (_, _) => await CarregarDiscosAsync();
    }

    private async Task CarregarDiscosAsync()
    {
        _mapaPainelDiscos.Children.Clear();
        try
        {
            var volumes = await Task.Run(MaintenanceService.ReadVolumes);
            if (volumes == null || volumes.Count == 0)
            {
                _mapaPainelDiscos.Children.Add(new TextBlock
                {
                    Text = "Não foram encontradas unidades fixas disponíveis.",
                    FontSize = 13,
                    Foreground = Ui.Res("Muted") as Brush ?? Brushes.Gray,
                    Margin = new Thickness(0, 20, 0, 0)
                });
                return;
            }
            foreach (var volume in volumes)
            {
                double num = (volume.TotalBytes > 0) ? ((double)(volume.TotalBytes - volume.FreeBytes) / (double)volume.TotalBytes) : 0.0;
                string text = string.IsNullOrWhiteSpace(volume.Model)
                    ? ((volume.Media == DriveType.Removable.ToString()) ? "Disco removível" : "Disco local")
                    : volume.Model;
                var stack = new StackPanel { MinWidth = 170.0 };
                var stack2 = new StackPanel { Orientation = Orientation.Horizontal };
                var icon = new TextBlock
                {
                    Text = (volume.Media == DriveType.Removable.ToString()) ? "\ue88e" : "\ueda2",
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Foreground = Ui.Res("Accent") as Brush ?? Brushes.Cyan
                };
                icon.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
                stack2.Children.Add(icon);
                stack2.Children.Add(new TextBlock
                {
                    Text = volume.Letter.TrimEnd('\\') + "  " + text,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Ui.Res("Text") as Brush ?? Brushes.White
                });
                stack.Children.Add(stack2);
                var pb = new ProgressBar { Maximum = 1.0, Value = num, Height = 5.0, Margin = new Thickness(0, 6, 0, 4) };
                pb.Background = Ui.Res("Line") as Brush ?? Brushes.Gray;
                pb.Foreground = Ui.Res("Accent") as Brush ?? Brushes.Cyan;
                if (num > 0.9) pb.Foreground = Ui.Res("Bad") as Brush ?? Brushes.Red;
                stack.Children.Add(pb);
                var txt = new TextBlock
                {
                    Text = $"{MapaDisco.Bytes(volume.FreeBytes)} livres de {MapaDisco.Bytes(volume.TotalBytes)}",
                    FontSize = 11.5,
                    Foreground = Ui.Res("Muted") as Brush ?? Brushes.Gray
                };
                stack.Children.Add(txt);
                var btn = new Button
                {
                    Content = stack,
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 8, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Tag = volume.Letter,
                    Background = Ui.Res("Card") as Brush ?? new SolidColorBrush(Color.FromRgb(35, 35, 45)),
                    BorderBrush = Ui.Res("Line") as Brush ?? Brushes.Gray,
                    Foreground = Ui.Res("Text") as Brush ?? Brushes.White
                };
                btn.Click += async (_, _) =>
                {
                    var letra = volume.Letter.TrimEnd('\\');
                    if (!letra.EndsWith(":")) letra += ":";
                    if (!letra.EndsWith("\\")) letra += "\\";
                    var caminho = letra;

                    var resultado = MessageBox.Show(
                        Window.GetWindow(this),
                        $"Deseja criar o mapa do disco {caminho.TrimEnd('\\')} ({text})?\n\nIsso irá ler todos os arquivos da unidade para calcular quanto espaço cada pasta ocupa.",
                        "Criar mapa do disco",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (resultado == MessageBoxResult.Yes)
                        await VarrerMapaAsync(caminho, volume.TotalBytes - volume.FreeBytes);
                };
                _mapaPainelDiscos.Children.Add(btn);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), $"Não foi possível ler as unidades: {ex.Message}", "Mapa do disco", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EntrarMapa(MapaDisco.NoPasta pasta) => MostrarMapa(pasta);

    private void MostrarMapa(MapaDisco.NoPasta pasta)
    {
        _mapaAtual = pasta;
        _treemap.Pasta = pasta;
        _mapaCaminho.Text = pasta.Caminho;
        _mapaCaminho.ToolTip = pasta.Caminho;
        _mapaVoltar.IsEnabled = pasta.Pai != null;
        AtualizarListaMapa(pasta);
    }

    private async Task VarrerMapaAsync(string caminho, long usado)
    {
        if (_ctsMapa != null) return;
        _ctsMapa = new CancellationTokenSource();
        try
        {
            var progresso = new Progress<MapaDisco.Progresso>(p =>
            {
                Dispatcher.Invoke(() => _mapaStatus.Text = $"{p.Arquivos:N0} arquivos · {MapaDisco.Bytes(p.Bytes)} · lendo {p.Atual}");
            });
            _mapaResultado = await MapaDisco.VarrerAsync(caminho, progresso, _ctsMapa.Token);

            if (_mapaResultado?.Raiz == null || _mapaResultado.Raiz.Tamanho <= 0)
            {
                _mapaStatus.Text = "Nenhum arquivo encontrado ou erro na leitura.";
                return;
            }

            _mapaVazio.Visibility = Visibility.Collapsed;
            _mapaConteudo.Visibility = Visibility.Visible;

            await Dispatcher.InvokeAsync(() =>
            {
                _mapaConteudo.UpdateLayout();
                _treemap.UpdateLayout();
            }, System.Windows.Threading.DispatcherPriority.Render);

            await Task.Delay(50);

            _treemap.InvalidateMeasure();
            _treemap.InvalidateVisual();

            MostrarMapa(_mapaResultado.Raiz);

            _treemap.InvalidateVisual();

            _mapaStatus.Text = _mapaResultado.Parado
                ? $"Leitura parada · {MapaDisco.Bytes(_mapaResultado.Raiz.Tamanho)} lidos · {_mapaResultado.Raiz.Arquivos:N0} arquivos"
                : $"{MapaDisco.Bytes(_mapaResultado.Raiz.Tamanho)} lidos · {_mapaResultado.Raiz.Arquivos:N0} arquivos";
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Não foi possível ler esta pasta. Confira se o disco ainda está conectado e tente de novo.", "Mapa do disco", MessageBoxButton.OK, MessageBoxImage.Exclamation);
        }
        finally { _ctsMapa?.Dispose(); _ctsMapa = null; }
    }

    private void AtualizarListaMapa(MapaDisco.NoPasta no)
    {
        _mapaListaStack.Children.Clear();

        // Lista unificada: pastas filhas + arquivos grandes da pasta atual
        var itens = new List<(string Nome, long Tamanho, MapaDisco.Tipo Tipo, MapaDisco.NoPasta? Pasta, MapaDisco.Arquivo? Arquivo, bool SemAcesso, long Arquivos)>();
        foreach (var f in no.Filhas.Where(f => f.Tamanho > 0))
            itens.Add((f.Nome, f.Tamanho, f.TipoPrincipal(), f, null, f.SemAcesso, f.Arquivos));
        if (no.Grandes != null)
        {
            foreach (var arq in no.Grandes.Where(a => a.Tamanho > 0))
                itens.Add((arq.Nome, arq.Tamanho, arq.Tipo, null, arq, false, 0));
        }
        itens = itens.OrderByDescending(x => x.Tamanho).Take(200).ToList();

        foreach (var item in itens)
        {
            if (item.Tamanho <= 0 && !item.SemAcesso) continue;

            double fracao = (no.Tamanho > 0) ? (double)item.Tamanho / (double)no.Tamanho : 0.0;
            string detalhe;
            if (item.Pasta != null)
                detalhe = item.SemAcesso ? "sem acesso" : $"{NArquivos(item.Arquivos)} · {MapaDisco.NomeTipo(item.Tipo)}";
            else
                detalhe = MapDiscoTipoNome(item.Tipo);
            Brush cor = MapaCores.Pincel(item.Tipo);

            var linha = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 36)),
                BorderBrush = Ui.Res("Line") as Brush ?? Brushes.Gray,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 6, 8, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = item
            };
            linha.MouseEnter += (_, _) => linha.Background = new SolidColorBrush(Color.FromRgb(45, 45, 55));
            linha.MouseLeave += (_, _) => linha.Background = new SolidColorBrush(Color.FromRgb(30, 30, 36));

            // Menu de contexto com botão direito para apagar (apenas na lista lateral)
            var menu = new ContextMenu();
            var itemApagar = new MenuItem { Header = "Apagar" };
            itemApagar.Click += async (_, _) =>
            {
                var resultado = MessageBox.Show(
                    Window.GetWindow(this)!,
                    item.Pasta != null
                        ? $"Deseja realmente excluir a pasta \"{item.Nome}\" e todo o seu conteúdo?\nEsta ação não pode ser desfeita."
                        : $"Deseja realmente excluir o arquivo \"{item.Nome}\"?\nEsta ação não pode ser desfeita.",
                    "Excluir",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (resultado == MessageBoxResult.Yes)
                {
                    try
                    {
                        if (item.Pasta != null)
                            Directory.Delete(item.Pasta.Caminho, true);
                        else if (item.Arquivo != null)
                        {
                            var caminho = System.IO.Path.Combine(no.Caminho, item.Nome);
                            if (File.Exists(caminho)) File.Delete(caminho);
                        }
                        await AtualizarAposExclusao();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(Window.GetWindow(this)!, $"Não foi possível excluir: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            };
            menu.Items.Add(itemApagar);
            linha.ContextMenu = menu;

            linha.MouseLeftButtonUp += (_, _) =>
            {
                if (item.Pasta != null)
                    MostrarMapa(item.Pasta);
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });

            var icon = new TextBlock
            {
                Text = "",
                FontSize = 11,
                Foreground = cor,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            grid.Children.Add(icon);
            Grid.SetColumn(icon, 0);

            var nome = new TextBlock
            {
                Text = item.Nome,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Res("Text") as Brush ?? Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            grid.Children.Add(nome);
            Grid.SetColumn(nome, 1);

            var tamanho = new TextBlock
            {
                Text = MapaDisco.Bytes(item.Tamanho),
                FontSize = 11.5,
                Foreground = Ui.Res("Muted") as Brush ?? Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            grid.Children.Add(tamanho);
            Grid.SetColumn(tamanho, 2);

            var pct = new TextBlock
            {
                Text = fracao.ToString("0.#%"),
                FontSize = 11,
                Foreground = Ui.Res("Muted") as Brush ?? Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            grid.Children.Add(pct);
            Grid.SetColumn(pct, 3);

            linha.Child = grid;
            _mapaListaStack.Children.Add(linha);
        }
    }

    private async Task AtualizarAposExclusao()
    {
        if (_mapaAtual != null)
        {
            await Task.Delay(100);
            MostrarMapa(_mapaAtual);
        }
    }

    private static string MapDiscoTipoNome(MapaDisco.Tipo t) => t switch
    {
        MapaDisco.Tipo.Videos => "Vídeo",
        MapaDisco.Tipo.Imagens => "Imagem",
        MapaDisco.Tipo.Musicas => "Música",
        MapaDisco.Tipo.Documentos => "Documento",
        MapaDisco.Tipo.Programas => "Programa",
        MapaDisco.Tipo.Compactados => "Compactado",
        MapaDisco.Tipo.Sistema => "Sistema",
        _ => "Arquivo",
    };

    private static string NArquivos(long n) => n != 1 ? $"{n:N0} arquivos" : "1 arquivo";
}