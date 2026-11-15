using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FrameForge.Pages;

/// <summary>
/// Janelas pequenas com o tema do HyperCore para a Configuração gamer: confirmação antes de mudar o PC e a contagem
/// "Manter estas definições?" (volta atrás sozinha, como o Windows faz ao mudar o ecrã).
/// </summary>
internal static class GamerDialogs
{
    /// <summary>Pergunta antes de mudar algo. Enter = confirmar, Esc = cancelar. Devolve true se o utilizador confirmou.</summary>
    public static bool Confirm(DependencyObject? from, string title, string text, string okLabel, IEnumerable<string>? bullets = null,
                               string glyph = "", bool danger = false, string cancelLabel = "Cancelar")
    {
        var w = Shell(from, out var body, out var buttons);
        body.Children.Add(Head(glyph, title, danger ? Ui.Res("Warn") : Ui.Res("Accent")));
        var t = Ui.T(text, "Body");
        t.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(t);
        if (bullets != null)
        {
            var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var b in bullets)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var dot = new Ellipse { Width = 6, Height = 6, Fill = Ui.Res("Accent2"), Margin = new Thickness(2, 7, 12, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                row.Children.Add(Ui.T(b, "Body"));
                list.Children.Add(row);
            }
            body.Children.Add(list);
        }
        bool result = false;
        var cancel = Ui.Btn(cancelLabel, (_, _) => w.Close());
        cancel.IsCancel = true;
        var ok = Ui.Btn(okLabel, (_, _) => { result = true; w.Close(); }, danger ? "BtnDanger" : "BtnAccent");
        ok.IsDefault = true;
        ok.Margin = new Thickness(0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        w.Loaded += (_, _) => ok.Focus();
        w.ShowDialog();
        return result;
    }

    /// <summary>Só informa (um botão).</summary>
    public static void Info(DependencyObject? from, string title, string text, bool warn = false)
    {
        var w = Shell(from, out var body, out var buttons);
        body.Children.Add(Head(warn ? "" : "", title, warn ? Ui.Res("Warn") : Ui.Res("Accent")));
        var t = Ui.T(text, "Body");
        t.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(t);
        var ok = Ui.Btn("OK", (_, _) => w.Close());
        ok.IsDefault = true; ok.IsCancel = true; ok.Margin = new Thickness(0);
        buttons.Children.Add(ok);
        w.ShowDialog();
    }

    /// <summary>
    /// "Manter estas definições?" com contagem decrescente. Se o tempo acabar, se carregar em Esc/Enter ou fechar a janela, devolve false
    /// (voltar atrás) — só um clique em "Manter" devolve true. Assim, se o ecrã ficar preto, basta não fazer nada.
    /// </summary>
    public static bool Countdown(DependencyObject? from, string title, string text, int seconds, string keepLabel, string revertLabel)
    {
        var w = Shell(from, out var body, out var buttons);
        w.Topmost = true;
        w.Owner = null; // fica por cima de tudo, centrado no ecrã principal
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Anel com os segundos
        const double size = 76, thick = 6;
        var ring = new Grid { Width = size, Height = size, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Top };
        ring.Children.Add(new Ellipse { Stroke = Ui.Res("Line"), StrokeThickness = thick });
        double units = Math.PI * (size - thick) / thick;
        var arc = new Ellipse
        {
            Stroke = Ui.Res("AccentGradient"), StrokeThickness = thick, StrokeDashCap = PenLineCap.Round,
            StrokeDashArray = new DoubleCollection { units, units * 2 }, RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-90),
        };
        ring.Children.Add(arc);
        var num = new TextBlock
        {
            Text = seconds.ToString(), FontSize = 26, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)Ui.Theme["Mono"], Foreground = Ui.Res("Fg"),
        };
        ring.Children.Add(num);
        arc.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, units, TimeSpan.FromSeconds(seconds)));

        var top = new DockPanel();
        DockPanel.SetDock(ring, Dock.Left);
        top.Children.Add(ring);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.T(title, "H2", 18));
        texts.Children.Add(Ui.T(text, "MutedText"));
        top.Children.Add(texts);
        body.Children.Add(top);

        bool keep = false;
        int left = seconds;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            left--;
            num.Text = Math.Max(0, left).ToString();
            if (left <= 0) { timer.Stop(); w.Close(); }
        };
        var revert = Ui.Btn(revertLabel, (_, _) => w.Close());
        revert.IsCancel = true; revert.IsDefault = true;
        var ok = Ui.Btn(keepLabel, (_, _) => { keep = true; w.Close(); }, "BtnAccent");
        ok.Margin = new Thickness(0);
        buttons.Children.Add(revert);
        buttons.Children.Add(ok);
        w.Loaded += (_, _) => { timer.Start(); w.Activate(); revert.Focus(); };
        w.Closed += (_, _) => timer.Stop();
        try { w.ShowDialog(); }
        catch { keep = false; }
        return keep;
    }

    // ------------------------------------------------------------------ base

    private static Window Shell(DependencyObject? from, out StackPanel body, out StackPanel buttons)
    {
        var owner = from != null ? Window.GetWindow(from) : Application.Current?.MainWindow;
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Title = "HyperCore",
            WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (owner != null && owner.IsVisible) w.Owner = owner;
        Ui.UseTheme(w);
        if (w.TryFindResource("UiFont") is FontFamily ff) w.FontFamily = ff;
        w.Foreground = Ui.Res("Fg");
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);

        body = new StackPanel();
        buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var inner = new StackPanel();
        inner.Children.Add(body);
        inner.Children.Add(buttons);
        var card = new Border
        {
            Background = Ui.Res("Panel"), BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24, 22, 24, 20), Margin = new Thickness(24), Width = 460, Child = inner,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black },
        };
        // Arrastar pela janela
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { w.DragMove(); } catch { } };
        w.Content = card;
        // Entrada suave
        card.Opacity = 0;
        card.RenderTransform = new TranslateTransform(0, 10);
        w.Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        };
        return w;
    }

    private static DockPanel Head(string glyph, string title, Brush color)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var tile = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(11), Background = Ui.Res("AccentSoft"), Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock
            {
                Text = glyph, FontFamily = (FontFamily)Ui.Theme["Icons"], FontSize = 17, Foreground = color,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(tile, Dock.Left);
        d.Children.Add(tile);
        var t = Ui.T(title, "H2", 17);
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(0);
        d.Children.Add(t);
        return d;
    }
}
