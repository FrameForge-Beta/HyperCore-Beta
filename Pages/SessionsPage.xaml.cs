using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FrameForge.Core;

namespace FrameForge.Pages;

public partial class SessionsPage : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _saveSensitivityLater = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public SessionsPage()
    {
        InitializeComponent();
        Sens.Value = AppServices.Settings.StutterSensitivity;
        AdvCpu.IsChecked = AppServices.Settings.AdvancedCpuSensors;
        _timer.Tick += (_, _) => RefreshPresenters();
        _saveSensitivityLater.Tick += (_, _) => { _saveSensitivityLater.Stop(); AppServices.Settings.Save(); };
        Loaded += (_, _) => { _timer.Start(); RefreshPresenters(); Reload(); };
        Unloaded += (_, _) => { _timer.Stop(); _saveSensitivityLater.Stop(); };
    }

    private void RefreshPresenters()
    {
        var sel = (Presenters.SelectedItem as ListBoxItem)?.Tag as int?;
        Presenters.Items.Clear();
        var names = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses()) { names[p.Id] = p.ProcessName; p.Dispose(); } // lista do sistema, sem abrir processos
        foreach (var (pid, fps) in AppServices.Frames.ActivePresenters())
        {
            if (!names.TryGetValue(pid, out var name) || name is "dwm" or "FrameForge") continue;
            var item = new ListBoxItem { Content = $"{name}   ·   {fps:0} FPS   ·   PID {pid}", Tag = pid };
            Presenters.Items.Add(item);
            if (sel == pid) item.IsSelected = true;
        }
        if (Presenters.SelectedIndex < 0 && Presenters.Items.Count > 0) Presenters.SelectedIndex = 0;

        var rec = AppServices.Recorder;
        RecStatus.Text = rec.Active ? $"● A gravar {rec.GameName} há {(DateTime.Now - rec.StartedAt!.Value):mm\\:ss}" : "Nenhuma gravação ativa.";
        EtwStatus.Text = AppServices.Frames.Error is string err
            ? (err.Contains("ACCESSDENIED", StringComparison.OrdinalIgnoreCase) || err.Contains("Administrator", StringComparison.OrdinalIgnoreCase)
                ? "⚠ Captura de frames desligada: a app não está a correr como administrador. Fecha-a e abre de novo aceitando o pedido do Windows."
                : "Captura de frames indisponível: " + err) :(AppServices.Frames.Running ? "Captura de frames (ETW) ativa." : "Captura de frames parada.");
    }

    private async void Rec_Click(object sender, RoutedEventArgs e)
    {
        if ((Presenters.SelectedItem as ListBoxItem)?.Tag is not int pid) { RecStatus.Text = "Abre o jogo primeiro (tem de estar a desenhar frames)."; return; }
        string name = Telemetry.SystemSampler.ProcessName(pid) ?? $"PID {pid}";
        await AppServices.Recorder.StartAsync(pid, name);
        RefreshPresenters();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!AppServices.Recorder.Active) return;
        RecStatus.Text = "A analisar…";
        var r = await AppServices.Recorder.StopAsync();
        if (r != null) Ui.Open(r.Value.Path);
        Reload();
    }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        if (OverlayWindow.IsShown) OverlayWindow.HideOverlay();
        else if ((Presenters.SelectedItem as ListBoxItem)?.Tag is int pid) OverlayWindow.ShowFor(pid);
    }

    private void Sens_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SensText == null) return;
        AppServices.Settings.StutterSensitivity = Sens.Value;
        _saveSensitivityLater.Stop();
        _saveSensitivityLater.Start();
        SensText.Text = $"{Sens.Value:0.00}× — conta como quebra um frame com mais do dobro do tempo normal e pelo menos {8 / Sens.Value:0.0} ms a mais. " +
                        "Mais alto = apanha micro-quebras mais pequenas (útil em jogos de 240+ FPS).";
    }

    private void AdvCpu_Click(object sender, RoutedEventArgs e)
    {
        bool on = AdvCpu.IsChecked == true;
        if (on && MessageBox.Show("Isto carrega o driver PawnIO para ler a temperatura da CPU. Com anti-cheats de kernel (Valorant, FACEIT, etc.) o jogo pode recusar abrir enquanto estiver ativo. Ligar mesmo assim?",
                "FrameForge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            AdvCpu.IsChecked = false;
            return;
        }
        AppServices.Settings.AdvancedCpuSensors = on;
        AppServices.Settings.Save();
        AppServices.Sampler.EnableAdvancedCpu(on);
    }

    private void Folder_Click(object sender, RoutedEventArgs e) => Ui.Open(AppPaths.Reports);

    public void Reload()
    {
        Reports.Children.Clear();
        foreach (var html in Games.SessionRecorder.ListReports().Take(40))
        {
            string json = Path.ChangeExtension(html, ".json");
            string title = Path.GetFileNameWithoutExtension(html), sub = "";
            string grade = "?";
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var r = doc.RootElement;
                title = $"{r.GetProperty("Game").GetString()} — {r.GetProperty("Start").GetDateTime():dd/MM HH:mm}";
                grade = r.GetProperty("Grade").GetString() ?? "?";
                sub = $"{r.GetProperty("AvgFps").GetDouble():0} FPS · 1% low {r.GetProperty("Low1Fps").GetDouble():0} · {r.GetProperty("StuttersPerMin").GetDouble():0.0} quebras/min · {r.GetProperty("Verdict").GetString()}";
            }
            catch { }
            var dock = new DockPanel();
            var open = Ui.Btn("Abrir", (_, _) => Ui.Open(html));
            DockPanel.SetDock(open, Dock.Right);
            dock.Children.Add(open);
            var g = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 12, 0),
                Background = grade switch { "A" => Ui.Res("Good"), "B" => Ui.Res("Accent"), "C" => Ui.Res("Warn"), "?" => Ui.Res("Hover"), _ => Ui.Res("Bad") },
                Child = new TextBlock { Text = grade, FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.White },
            };
            DockPanel.SetDock(g, Dock.Left);
            dock.Children.Add(g);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(Ui.T(title, "H2"));
            sp.Children.Add(Ui.T(sub, "MutedText"));
            dock.Children.Add(sp);
            var card = Ui.Card(dock);
            card.Padding = new Thickness(12);
            Reports.Children.Add(card);
        }
        if (Reports.Children.Count == 0) Reports.Children.Add(Ui.T("Ainda não há relatórios. Joga com 'gravar sessão' ligado ou grava aqui um jogo aberto.", "MutedText"));
    }
}
