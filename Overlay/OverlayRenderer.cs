using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FrameForge.Overlay;

/// <summary>Um elemento já desenhado, com o que é preciso para o atualizar sem o reconstruir.</summary>
public sealed class OverlayVisual
{
    public OverlayItem Item { get; }
    public FrameworkElement Root { get; }
    internal TextBlock? Label, Value, Unit;
    internal Border? Fill, Track;
    internal Polyline? Line;
    internal Polygon? Area;
    internal Canvas? Plot;
    internal readonly Queue<double> History = new();
    private bool _blink;

    internal OverlayVisual(OverlayItem item, FrameworkElement root) { Item = item; Root = root; }

    public void Update(double? v, string? text)
    {
        var it = Item;
        string shown = text ?? (v is double d ? d.ToString("F" + Math.Clamp(it.Decimals, 0, 3)) : "—");
        var th = v is double val ? it.Thresholds.LastOrDefault(t => t.Below ? val <= t.At : val >= t.At) : null;
        Brush? thBrush = th != null ? OverlayRenderer.B(th.Color) : null;

        if (Value != null) { Value.Text = shown; Value.Foreground = thBrush ?? OverlayRenderer.B(it.ValueColor); }
        if (Fill != null && Track != null)
        {
            double f = v is double x ? Math.Clamp((x - it.Min) / Math.Max(0.0001, it.Max - it.Min), 0, 1) : 0;
            Fill.Width = Track.Width * f;
            Fill.Background = thBrush ?? OverlayRenderer.B(it.BarColor);
        }
        if (Line != null && Plot != null && v is double gv)
        {
            History.Enqueue(gv);
            while (History.Count > Math.Max(10, it.History)) History.Dequeue();
            double min = it.Min, max = it.Max;
            if (it.AutoScale && History.Count > 1) { max = Math.Max(History.Max() * 1.15, min + 1); }
            double w = Plot.Width, h = Plot.Height;
            var pts = new PointCollection();
            int n = Math.Max(10, it.History), i = n - History.Count;
            foreach (var p in History)
            {
                double y = h - Math.Clamp((p - min) / Math.Max(0.0001, max - min), 0, 1) * h;
                pts.Add(new Point(i * w / (n - 1), y));
                i++;
            }
            Line.Points = pts;
            Line.Stroke = thBrush ?? OverlayRenderer.B(it.BarColor);
            if (Area != null)
            {
                var a = new PointCollection(pts);
                if (pts.Count > 0) { a.Add(new Point(pts[^1].X, h)); a.Add(new Point(pts[0].X, h)); }
                Area.Points = a;
                var c = ((SolidColorBrush)(thBrush ?? OverlayRenderer.B(it.BarColor))).Color;
                Area.Fill = new SolidColorBrush(Color.FromArgb(60, c.R, c.G, c.B));
            }
        }

        if (th?.Blink == true) { _blink = !_blink; Root.Opacity = _blink ? it.Opacity : it.Opacity * 0.25; }
        else Root.Opacity = it.Opacity;
    }
}

public static class OverlayRenderer
{
    public const double DesignWidth = 1920;
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    public static SolidColorBrush B(string? hex)
    {
        hex ??= "#FFFFFFFF";
        if (Brushes.TryGetValue(hex, out var b)) return b;
        try { b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { b = new SolidColorBrush(Colors.White); }
        b.Freeze();
        return Brushes[hex] = b;
    }

    private static TextBlock Txt(OverlayItem it, double size, string color, bool bold) => new()
    {
        FontFamily = new FontFamily(it.FontFamily),
        FontSize = Math.Max(6, size),
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        FontStyle = it.Italic ? FontStyles.Italic : FontStyles.Normal,
        Foreground = B(color),
        VerticalAlignment = VerticalAlignment.Bottom,
    };

    public static OverlayVisual Build(OverlayItem it, double scale)
    {
        double ls = it.LabelSize > 0 ? it.LabelSize : it.FontSize * 0.7;
        FrameworkElement content;
        OverlayVisual vis;
        TextBlock? label = null, value = null, unit = null;

        switch (it.Kind)
        {
            case ItemKind.Box:
                content = new Border { Width = it.Width, Height = it.Height };
                break;
            case ItemKind.Text:
                content = Txt(it, it.FontSize, it.ValueColor, it.Bold);
                ((TextBlock)content).Text = it.Label.Length > 0 ? it.Label : "Texto";
                break;
            case ItemKind.Bar:
            {
                var sp = new StackPanel { Width = it.Width };
                var top = new DockPanel { LastChildFill = false };
                if (it.ShowLabel && it.Label.Length > 0) { label = Txt(it, ls, it.LabelColor, false); label.Text = it.Label; top.Children.Add(label); }
                value = Txt(it, it.FontSize, it.ValueColor, it.Bold);
                var right = new StackPanel { Orientation = Orientation.Horizontal };
                right.Children.Add(value);
                if (it.ShowUnit && it.Unit.Length > 0) { unit = Txt(it, ls, it.UnitColor, false); unit.Text = it.Unit; unit.Margin = new Thickness(2, 0, 0, 1); right.Children.Add(unit); }
                DockPanel.SetDock(right, Dock.Right);
                top.Children.Add(right);
                sp.Children.Add(top);
                var track = new Border { Width = it.Width, Height = Math.Max(2, it.Height), CornerRadius = new CornerRadius(it.Height / 2), Background = B(it.BarBack), Margin = new Thickness(0, 3, 0, 0) };
                var fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(it.Height / 2), Background = B(it.BarColor), Width = 0 };
                track.Child = fill;
                sp.Children.Add(track);
                content = sp;
                vis = new OverlayVisual(it, Wrap(it, content, scale)) { Label = label, Value = value, Unit = unit, Fill = fill, Track = track };
                return vis;
            }
            case ItemKind.Graph:
            {
                var grid = new Grid { Width = it.Width, Height = it.Height };
                var plot = new Canvas { Width = it.Width, Height = it.Height, ClipToBounds = true };
                var area = it.FillGraph ? new Polygon() : null;
                var line = new Polyline { StrokeThickness = it.LineWidth, Stroke = B(it.BarColor), StrokeLineJoin = PenLineJoin.Round };
                if (area != null) plot.Children.Add(area);
                plot.Children.Add(line);
                grid.Children.Add(plot);
                var txt = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
                if (it.ShowLabel && it.Label.Length > 0) { label = Txt(it, ls, it.LabelColor, false); label.Text = it.Label + " "; txt.Children.Add(label); }
                value = Txt(it, ls, it.ValueColor, it.Bold);
                txt.Children.Add(value);
                if (it.ShowUnit && it.Unit.Length > 0) { unit = Txt(it, ls, it.UnitColor, false); unit.Text = " " + it.Unit; txt.Children.Add(unit); }
                grid.Children.Add(txt);
                vis = new OverlayVisual(it, Wrap(it, grid, scale)) { Label = label, Value = value, Unit = unit, Line = line, Area = area, Plot = plot };
                return vis;
            }
            default: // Value, Clock, Game
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                if (it.Kind == ItemKind.Value && it.ShowLabel && it.Label.Length > 0)
                { label = Txt(it, ls, it.LabelColor, false); label.Text = it.Label; label.Margin = new Thickness(0, 0, 6, 2); sp.Children.Add(label); }
                value = Txt(it, it.FontSize, it.ValueColor, it.Bold);
                sp.Children.Add(value);
                if (it.Kind == ItemKind.Value && it.ShowUnit && it.Unit.Length > 0)
                { unit = Txt(it, ls, it.UnitColor, false); unit.Text = it.Unit; unit.Margin = new Thickness(3, 0, 0, 2); sp.Children.Add(unit); }
                content = sp;
                break;
            }
        }
        vis = new OverlayVisual(it, Wrap(it, content, scale)) { Label = label, Value = value, Unit = unit };
        return vis;
    }

    /// <summary>Fundo, borda, cantos, sombra e escala comuns a todos os elementos.</summary>
    private static FrameworkElement Wrap(OverlayItem it, FrameworkElement content, double scale)
    {
        var background = B(it.Background);
        var b = new Border
        {
            Child = content,
            Background = background,
            BorderBrush = B(it.BorderColor),
            BorderThickness = new Thickness(B(it.BorderColor).Color.A > 0 ? 1 : 0),
            CornerRadius = new CornerRadius(it.CornerRadius),
            Padding = it.Kind == ItemKind.Box ? new Thickness(0) : new Thickness(it.Padding),
            Opacity = it.Opacity,
            LayoutTransform = Math.Abs(scale - 1) > 0.001 ? new ScaleTransform(scale, scale) : null,
        };
        if (it.Shadow && it.Kind != ItemKind.Box)
            b.Effect = new DropShadowEffect { ShadowDepth = 1, BlurRadius = 4, Opacity = 0.9, Color = Colors.Black };
        return b;
    }

    /// <summary>
    /// Põe o elemento no sítio (âncora + X/Y), escalando de um ecrã de 1920 de largura para a largura real.
    /// O tamanho do overlay também afasta as posições, para o conjunto crescer/encolher como um bloco a partir do canto.
    /// </summary>
    public static void Place(OverlayVisual v, double factor, double offsetX = 0, double offsetY = 0)
    {
        var it = v.Item;
        double size = v.Root.LayoutTransform is ScaleTransform st ? st.ScaleX : 1;
        bool right = it.Anchor is Anchor.TopRight or Anchor.BottomRight, bottom = it.Anchor is Anchor.BottomLeft or Anchor.BottomRight;
        double x = (it.X + (right ? -offsetX : offsetX)) * factor * size;
        double y = (it.Y + (bottom ? -offsetY : offsetY)) * factor * size;
        Canvas.SetLeft(v.Root, right ? double.NaN : x);
        Canvas.SetRight(v.Root, right ? x : double.NaN);
        Canvas.SetTop(v.Root, bottom ? double.NaN : y);
        Canvas.SetBottom(v.Root, bottom ? y : double.NaN);
        v.Root.Visibility = it.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Aplica a escala do overlay sem reconstruir os elementos da pré-visualização.</summary>
    public static void SetScale(OverlayVisual visual, double scale)
    {
        visual.Root.LayoutTransform = Math.Abs(scale - 1) > 0.001 ? new ScaleTransform(scale, scale) : null;
    }

    /// <summary>Atualiza todos os elementos com os valores atuais.</summary>
    public static void UpdateAll(IEnumerable<OverlayVisual> visuals)
    {
        int pid = OverlayValues.ResolvePid();
        var live = pid != 0 ? Core.AppServices.Frames.Live(pid) : null;
        string? game = null;
        foreach (var v in visuals)
        {
            var it = v.Item;
            switch (it.Kind)
            {
                case ItemKind.Clock: v.Update(null, DateTime.Now.ToString(string.IsNullOrWhiteSpace(it.TimeFormat) ? "HH:mm" : it.TimeFormat)); break;
                case ItemKind.Game: v.Update(null, (game ??= OverlayValues.GameName(pid)) is { Length: > 0 } g ? g : "sem jogo"); break;
                case ItemKind.Box or ItemKind.Text: v.Update(null, null); break;
                default: v.Update(OverlayValues.Get(it.SensorId, live), null); break;
            }
        }
    }
}
