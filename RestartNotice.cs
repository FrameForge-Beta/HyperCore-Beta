using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FrameForge.Core;

namespace FrameForge;

/// <summary>
/// Aviso de "é preciso reiniciar o PC". Aparece sempre que um ajuste só faz efeito depois de reiniciar, com 1 segundo de espera antes
/// de os botões ficarem ativos (para ser lido, não fechado sem querer). O pedido fica pendente (barra no topo da app) até o PC reiniciar.
/// </summary>
public static class RestartNotice
{
    private static RestartDialog? _open;
    public static event Action? Changed;

    public static IReadOnlyList<string> Pending => AppServices.Settings.PendingRestart;

    /// <summary>Há um reinício agendado por nós (shutdown /r) que ainda pode ser cancelado.</summary>
    public static bool Scheduled { get; private set; }

    /// <summary>
    /// Identificador do arranque atual: o contador BootId do Windows (sobe 1 a cada arranque e não depende do relógio — mudanças de hora
    /// ou acertos de NTP não o alteram). Se não existir, usa a hora de arranque em UTC arredondada ao minuto.
    /// </summary>
    private static long BootKey()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
            if (k?.GetValue("BootId") is int id) return id;
        }
        catch { }
        var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return -(boot.Ticks / TimeSpan.TicksPerMinute);
    }

    /// <summary>No arranque: se o PC já reiniciou desde o pedido, limpa a lista.</summary>
    public static void Init()
    {
        var s = AppServices.Settings;
        long now = BootKey();
        bool sameBoot = s.PendingRestartBootId is long saved && (saved == now || (saved < 0 && now < 0 && Math.Abs(saved - now) <= 2));
        if (s.PendingRestart.Count > 0 && !sameBoot)
        {
            s.PendingRestart.Clear();
            s.PendingRestartBootId = null;
            s.Save();
        }
    }

    /// <summary>
    /// Nome estável de cada ajuste; assim, alternar o mesmo ajuste em páginas diferentes mantém o estado coerente.
    /// </summary>
    private static string Canonical(string what) =>
        what.StartsWith("Agendamento de GPU", StringComparison.OrdinalIgnoreCase) ? "Agendamento de GPU acelerado por hardware (HAGS)" : what;

    /// <summary>Regista o que precisa de reinício e mostra o aviso (vários pedidos seguidos juntam-se no mesmo aviso).</summary>
    public static void Request(params string[] reasons)
    {
        var app = Application.Current;
        if (app == null || (reasons.Length == 0 && Pending.Count == 0)) return; // sem motivos = voltar a mostrar os pendentes
        app.Dispatcher.BeginInvoke(() =>
        {
            var s = AppServices.Settings;
            foreach (var r in reasons.Where(r => !string.IsNullOrWhiteSpace(r)))
                if (!s.PendingRestart.Contains(r)) s.PendingRestart.Add(r);
            s.PendingRestartBootId = BootKey();
            s.Save();
            Changed?.Invoke();
            if (_open != null) { _open.Refresh(); _open.Activate(); return; }
            _open = new RestartDialog { Owner = app.MainWindow?.IsVisible == true ? app.MainWindow : null };
            _open.Closed += (_, _) => _open = null;
            _open.ShowDialog();
        });
    }

    /// <summary>
    /// Ajuste que só vale depois de reiniciar foi ligado/desligado. Se o contrário ainda estava à espera de reinício,
    /// os dois anulam-se (não é preciso reiniciar); senão, pede o reinício.
    /// </summary>
    /// <returns>true se ficou um reinício pendente por causa desta mudança; false se anulou uma mudança anterior (não é preciso reiniciar).</returns>
    public static bool Toggled(string what, bool on) => ToggledMany(new[] { (what, on) }) > 0;

    /// <summary>Vários ajustes de uma vez (ex.: aplicar um nível) num só aviso. Devolve quantos ficaram a precisar de reinício.</summary>
    public static int ToggledMany(IEnumerable<(string What, bool On)> changes)
    {
        var add = new List<string>();
        foreach (var (raw, on) in changes)
        {
            string what = Canonical(raw);
            string mine = $"{what} — {(on ? "ligado" : "desligado")}", opposite = $"{what} — {(on ? "desligado" : "ligado")}";
            if (AppServices.Settings.PendingRestart.Contains(opposite)) Withdraw(opposite);
            else add.Add(mine);
        }
        if (add.Count > 0) Request(add.ToArray());
        return add.Count;
    }

    /// <summary>Quando o utilizador desfaz o ajuste antes de reiniciar, já não é preciso.</summary>
    public static void Withdraw(string reason)
    {
        var s = AppServices.Settings;
        if (s.PendingRestart.Remove(reason)) { s.Save(); Changed?.Invoke(); }
    }

    /// <summary>
    /// Agenda o reinício para daqui a 15 s (dá tempo de guardar trabalho). Devolve null se ficou agendado, ou o motivo da falha
    /// (ex.: código 1190 = já havia um encerramento agendado por outro programa/Windows Update — não o cancelamos).
    /// </summary>
    public static string? RestartNow()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 15 /c \"HyperCore: a reiniciar para aplicar os ajustes. Guarda o teu trabalho.\"")
            { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit(5000);
            int code = p.HasExited ? p.ExitCode : -1;
            if (code == 0) { Scheduled = true; return null; }
            return code == 1190 ? "Já há um encerramento/reinício agendado (por exemplo pelo Windows Update). Não mexi nele." : $"O Windows recusou (código {code}).";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Cancela só o reinício que NÓS agendámos.</summary>
    public static void CancelRestart()
    {
        if (!Scheduled) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("shutdown.exe", "/a") { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit(5000);
        }
        catch { }
        Scheduled = false;
    }
}

internal sealed class RestartDialog : Window
{
    private readonly StackPanel _list = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _now, _later;
    private readonly TextBlock _wait;

    public RestartDialog()
    {
        Title = "É preciso reiniciar o PC";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        Brush R(string k, string fallback) => Application.Current.TryFindResource(k) as Brush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        var fg = R("Fg", "#F5F5F5");
        var muted = R("Muted", "#8C8C8C");

        var root = new StackPanel { Width = 470 };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 26, Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = "É preciso reiniciar o PC", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(head);
        root.Children.Add(new TextBlock { Text = "Estes ajustes já estão gravados, mas o Windows só os usa depois de reiniciar:", Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontSize = 13.5 });
        root.Children.Add(_list);
        root.Children.Add(new TextBlock { Text = "Podes reiniciar mais tarde: o aviso fica no topo da app até reiniciares.", Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontSize = 12.5 });

        // 1 segundo de espera antes de deixar responder
        var bar = new Border { Height = 3, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF)), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Margin = new Thickness(0, 16, 0, 0) };
        root.Children.Add(bar);
        _wait = new TextBlock { Text = "Lê o aviso… (1 s)", Foreground = muted, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(_wait);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _later = MakeButton("Reiniciar mais tarde", false);
        _now = MakeButton("Reiniciar agora", true);
        // O estado "reinício agendado" é global (RestartNotice.Scheduled): se fechares e voltares a abrir o aviso, ainda podes cancelar.
        void ShowScheduled()
        {
            _wait.Text = "O PC reinicia daqui a uns segundos. Guarda o que estiveres a fazer.";
            _now.IsEnabled = false;
            _later.Content = "Cancelar o reinício";
        }
        _later.Click += (_, _) => { if (RestartNotice.Scheduled) RestartNotice.CancelRestart(); Close(); };
        _now.Click += (_, _) =>
        {
            if (RestartNotice.RestartNow() is string error) { _wait.Text = "Não consegui agendar o reinício: " + error; return; }
            ShowScheduled();
        };
        // Durante o segundo de espera a janela não fecha (nem com Alt+F4). Depois disso, fechar é seguro: um reinício já agendado
        // continua cancelável na barra do topo, que volta a abrir este aviso com "Cancelar o reinício".
        bool locked = true;
        Closing += (_, e) => { if (locked) e.Cancel = true; };
        Unlock = () => locked = false;
        buttons.Children.Add(_later);
        buttons.Children.Add(_now);
        root.Children.Add(buttons);

        Content = new Border
        {
            Child = root, Padding = new Thickness(26, 22, 26, 22), CornerRadius = new CornerRadius(18),
            Background = R("Card", "#0E0E0E"), BorderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.6 },
            Margin = new Thickness(20),
        };
        Refresh();

        _now.IsEnabled = _later.IsEnabled = false;
        Loaded += (_, _) =>
        {
            bar.BeginAnimation(WidthProperty, new DoubleAnimation(0, 470, TimeSpan.FromSeconds(1)));
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                Unlock?.Invoke();
                _now.IsEnabled = _later.IsEnabled = true;
                _wait.Text = "";
                if (RestartNotice.Scheduled) ShowScheduled();
                // foco no botão SEGURO: um Espaço/Enter vindo do jogo nunca reinicia o PC sem querer
                _later.Focus();
            };
            t.Start();
        };
    }

    private Action? Unlock;

    public void Refresh()
    {
        _list.Children.Clear();
        foreach (var r in RestartNotice.Pending)
            _list.Children.Add(new TextBlock { Text = "•  " + r, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Margin = new Thickness(0, 3, 0, 0),
                                               Foreground = Application.Current.TryFindResource("Fg") as Brush ?? Brushes.White });
    }

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button { Content = text, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(18, 9, 18, 9), Cursor = System.Windows.Input.Cursors.Hand, FontWeight = FontWeights.SemiBold };
        var bg = primary ? new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF)) : new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x23));
        var tpl = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetValue(Border.BackgroundProperty, bg);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(cp);
        tpl.VisualTree = border;
        var dis = new Trigger { Property = IsEnabledProperty, Value = false };
        dis.Setters.Add(new Setter(OpacityProperty, 0.35));
        tpl.Triggers.Add(dis);
        b.Template = tpl;
        b.Foreground = Brushes.White;
        return b;
    }
}
