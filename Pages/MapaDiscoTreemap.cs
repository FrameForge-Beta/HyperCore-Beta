using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FrameForge.Core;

namespace FrameForge.Pages;

public sealed class MapaDiscoTreemap : FrameworkElement
{
    private sealed class Bloco
    {
        public required string Nome;
        public long Tamanho;
        public MapaDisco.Tipo Tipo;
        public MapaDisco.NoPasta? Pasta;
        public MapaDisco.Arquivo? Arquivo;
        public Rect R;
    }

    private MapaDisco.NoPasta? _pasta;
    private readonly List<Bloco> _blocos = new List<Bloco>();
    private Bloco? _sobre;
    private readonly ToolTip _dica = new ToolTip { Placement = PlacementMode.Relative };

    public MapaDisco.NoPasta? Pasta
    {
        get => _pasta;
        set
        {
            _pasta = value;
            _sobre = null;
            Montar();
        }
    }

    public event Action<MapaDisco.NoPasta>? PastaClicada;

    public MapaDiscoTreemap()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        ToolTip = _dica;
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetShowDuration(this, 60000);
        SizeChanged += (_, _) => Montar();
    }

    private void Montar()
    {
        _blocos.Clear();
        if (_pasta == null || ActualWidth < 4.0 || ActualHeight < 4.0 || _pasta.Tamanho <= 0)
        {
            InvalidateVisual();
            return;
        }
        const double TamanhoMinimoBloco = 8.0; // mínimo de pixels para um bloco ser visível e selecionável

        // Lista unificada: pastas filhas + arquivos grandes da pasta atual
        var itens = new List<(string Nome, long Tamanho, MapaDisco.Tipo Tipo, MapaDisco.NoPasta? Pasta, MapaDisco.Arquivo? Arquivo)>();
        foreach (var f in _pasta.Filhas.Where(f => f.Tamanho > 0))
            itens.Add((f.Nome, f.Tamanho, f.TipoPrincipal(), f, null));
        if (_pasta.Grandes != null)
        {
            foreach (var arq in _pasta.Grandes.Where(a => a.Tamanho > 0))
                itens.Add((arq.Nome, arq.Tamanho, arq.Tipo, null, arq));
        }
        itens = itens.OrderByDescending(x => x.Tamanho).ToList();

        if (itens.Count == 0) { InvalidateVisual(); return; }
        double total = itens.Sum(x => (double)x.Tamanho);
        var restante = new Rect(0, 0, ActualWidth, ActualHeight);
        int inicio = 0;
        while (inicio < itens.Count && restante.Width > TamanhoMinimoBloco && restante.Height > TamanhoMinimoBloco)
        {
            double lado = Math.Min(restante.Width, restante.Height);
            double soma = 0;
            int fim = inicio;
            while (fim < itens.Count)
            {
                double proxima = soma + (itens[fim].Tamanho / total) * ActualWidth * ActualHeight;
                if (fim > inicio && proxima / Math.Max(1, lado) > lado) break;
                soma = proxima; fim++;
            }
            if (fim == inicio) { soma = (itens[inicio].Tamanho / total) * ActualWidth * ActualHeight; fim++; }
            double medida = Math.Min(lado, soma / Math.Max(1, lado));
            double cursor = restante.Width >= restante.Height ? restante.Y : restante.X;
            for (int i = inicio; i < fim; i++)
            {
                double a = (itens[i].Tamanho / total) * ActualWidth * ActualHeight;
                double extensao = a / Math.Max(1, medida);
                Rect rect = restante.Width >= restante.Height
                    ? new Rect(restante.X, cursor, medida, Math.Min(extensao, restante.Bottom - cursor))
                    : new Rect(cursor, restante.Y, Math.Min(extensao, restante.Right - cursor), medida);
                // Ignora blocos que ficam muito pequenos para serem selecionados com o mouse
                if (rect.Width < TamanhoMinimoBloco || rect.Height < TamanhoMinimoBloco)
                {
                    cursor += extensao;
                    continue;
                }
                var item = itens[i];
                _blocos.Add(new Bloco
                {
                    Nome = item.Nome,
                    Tamanho = item.Tamanho,
                    Tipo = item.Tipo,
                    Pasta = item.Pasta,
                    Arquivo = item.Arquivo,
                    R = rect
                });
                cursor += extensao;
            }
            restante = restante.Width >= restante.Height
                ? new Rect(restante.X + medida, restante.Y, Math.Max(0, restante.Width - medida), restante.Height)
                : new Rect(restante.X, restante.Y + medida, restante.Width, Math.Max(0, restante.Height - medida));
            inicio = fim;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        Brush brush = (Brush)(TryFindResource("Card") ?? Brushes.Black);
        dc.DrawRectangle(brush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_blocos.Count == 0) return;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Brush corTexto = (Brush)(TryFindResource("Text") ?? Brushes.White);
        Typeface fonte = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        foreach (Bloco bloco in _blocos)
        {
            if (bloco.R.IsEmpty || bloco.R.Width < 1.0 || bloco.R.Height < 1.0) continue;
            dc.DrawRectangle(MapaCores.Pincel(bloco.Tipo), null, bloco.R);
            if (bloco.R.Width >= 40 && bloco.R.Height >= 14)
            {
                Escrever(dc, bloco.Nome, bloco.R.X + 5, bloco.R.Y + 3, bloco.R.Width - 10, 11, fonte, corTexto, pixelsPerDip);
                if (bloco.R.Height >= 26)
                {
                    Escrever(dc, MapaDisco.Bytes(bloco.Tamanho), bloco.R.X + 5, bloco.R.Y + 14, bloco.R.Width - 10, 9, fonte, corTexto, pixelsPerDip);
                }
            }
        }
        if (_sobre != null && !_sobre.R.IsEmpty)
        {
            Pen pen = new Pen((Brush)(TryFindResource("Text") ?? Brushes.White), 2.0);
            ((Freezable)pen).Freeze();
            dc.DrawRectangle(null, pen, new Rect(_sobre.R.X + 1, _sobre.R.Y + 1, Math.Max(0, _sobre.R.Width - 2), Math.Max(0, _sobre.R.Height - 2)));
        }
    }

    private static void Escrever(DrawingContext dc, string texto, double x, double y, double largura, double tam, Typeface fonte, Brush cor, double dpi)
    {
        if (!(largura < 8.0))
        {
            FormattedText formattedText = new FormattedText(texto, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, fonte, tam, cor, dpi)
            {
                MaxTextWidth = largura,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            dc.DrawText(formattedText, new Point(x, y));
        }
    }

    private Bloco? Achar(Point p)
    {
        for (int num = _blocos.Count - 1; num >= 0; num--)
        {
            Bloco bloco = _blocos[num];
            if (bloco.R.Contains(p)) return bloco;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point position = e.GetPosition(this);
        Bloco bloco = Achar(position);
        _dica.HorizontalOffset = position.X + 16;
        _dica.VerticalOffset = position.Y + 18;
        if (bloco != _sobre)
        {
            _sobre = bloco;
            if (bloco == null)
            {
                _dica.IsOpen = false;
                _dica.Content = null;
            }
            else
            {
                _dica.Content = Dica(bloco);
            }
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _sobre = null;
        _dica.IsOpen = false;
        InvalidateVisual();
    }

    private string Dica(Bloco b)
    {
        long num = _pasta?.Tamanho ?? 0;
        double value = ((num > 0) ? ((double)b.Tamanho / (double)num) : 0.0);
        List<string> list = new List<string> { b.Nome, $"{MapaDisco.Bytes(b.Tamanho)}  ·  {value:0.#%}" };
        if (b.Pasta != null)
        {
            list.Add($"Pasta com {b.Pasta.Arquivos:N0} arquivos.");
            list.Add("Clique para entrar nesta pasta.");
        }
        else
        {
            list.Add(MapaDisco.NomeTipo(b.Tipo));
        }
        return string.Join("\n", list);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Bloco bloco = Achar(e.GetPosition(this));
        if (bloco != null && bloco.Pasta != null)
        {
            _dica.IsOpen = false;
            PastaClicada?.Invoke(bloco.Pasta);
        }
    }
}