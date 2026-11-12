using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using FrameForge.Core;
using FrameForge.Hardware;
using FrameForge.Optimize;

namespace FrameForge.Pages;

public partial class OptimizePage : UserControl
{
    private int _level = 1;
    private SystemProbe? _probe;

    public OptimizePage()
    {
        InitializeComponent();
        for (int i = 1; i <= 4; i++)
        {
            int lv = i;
            var rb = new RadioButton { Content = TweakCatalog.LevelNames[i], Style = (Style)FindResource("Chip"), IsChecked = i == 1, GroupName = "lvl" };
            rb.Checked += (_, _) => { _level = lv; LevelText.Text = TweakCatalog.LevelDescriptions[lv]; };
            Levels.Children.Add(rb);
        }
        LevelText.Text = TweakCatalog.LevelDescriptions[1];
        Loaded += async (_, _) =>
        {
            _probe = await AppServices.Probe;
            await Task.Run(() => { foreach (var t in AppServices.Tweaks) t.Refresh(_probe); });
            Build();
        };
    }

    private void Build()
    {
        List.Children.Clear();
        foreach (var grp in AppServices.Tweaks.GroupBy(t => t.Level))
        {
            var h = Ui.T(TweakCatalog.LevelNames[grp.Key].ToUpperInvariant(), "Label");
            h.Margin = new Thickness(2, 10, 0, 6);
            List.Children.Add(h);
            foreach (var t in grp) List.Children.Add(Row(t));
        }
    }

    private Border Row(Tweak t)
    {
        var sw = new ToggleButton { Style = (Style)FindResource("Switch"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 2, 0, 0) };
        sw.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Tweak.IsOn)) { Source = t, Mode = BindingMode.OneWay });
        sw.SetBinding(IsEnabledProperty, new Binding(nameof(Tweak.Enabled)) { Source = t, Mode = BindingMode.OneWay });
        sw.Click += async (_, _) =>
        {
            bool want = sw.IsChecked == true;
            sw.IsEnabled = false;
            bool ok = await Task.Run(() => t.Set(want)) == null;
            sw.IsEnabled = t.Enabled;
            if (ok && t.NeedsRestart)
                t.Status = RestartNotice.Toggled(t.Title, want)
                    ? (want ? "Aplicado — reinicia o PC para fazer efeito." : "Reposto — reinicia o PC para voltar ao original.")
                    : "Anulou a mudança anterior: já não é preciso reiniciar.";
        };

        var body = new StackPanel();
        var title = Ui.T(t.Title, "H2");
        title.Margin = new Thickness(0, 0, 0, 2);
        body.Children.Add(Ui.Row(Ui.Pill(t.LevelName), t.NeedsRestart ? Ui.Pill("REINICIAR", Ui.Res("AccentSoft")) : new FrameworkElement()));
        title.Margin = new Thickness(0, 6, 0, 2);
        body.Children.Add(title);
        body.Children.Add(Ui.T(t.What, "Body"));
        var gain = Ui.T("Ganho: " + t.Gain, "Body", color: Ui.Res("Good"));
        gain.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(gain);
        var warn = Ui.T("Atenção: " + t.Warning, "Body", color: Ui.Res("Warn"));
        warn.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(warn);
        var blocked = Ui.T("", "MutedAuto");
        blocked.SetBinding(TextBlock.TextProperty, new Binding(nameof(Tweak.Blocked)) { Source = t });
        body.Children.Add(blocked);
        var status = Ui.T("", "MutedAuto");
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(Tweak.Status)) { Source = t });
        body.Children.Add(status);

        var dock = new DockPanel();
        DockPanel.SetDock(sw, Dock.Right);
        dock.Children.Add(sw);
        dock.Children.Add(body);
        return Ui.Card(dock);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_probe == null) return;
        if (_level >= 3 && MessageBox.Show($"O nível {TweakCatalog.LevelNames[_level]} aumenta o consumo/temperatura em repouso" +
                (_level == 4 ? " e desliga a indexação da pesquisa do Windows" : "") + ". Tudo é reversível em 'Restaurar tudo'. Continuar?",
                "FrameForge", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ApplyBtn.IsEnabled = false;
        Result.Text = "A aplicar…";
        int on = 0, off = 0, fail = 0;
        var restart = new List<(string, bool)>();
        await Task.Run(() =>
        {
            foreach (var t in AppServices.Tweaks)
            {
                t.Refresh(_probe);
                if (!t.Enabled) continue;
                bool want = t.Level <= _level;
                if (want == t.IsOn) continue;
                if (!want && !t.Touched) continue; // não mexer no que não fomos nós a mudar
                if (t.Set(want) != null) { fail++; continue; }
                if (want) on++; else off++;
                if (t.NeedsRestart) restart.Add((t.Title, want));
            }
        });
        int pending = restart.Count > 0 ? RestartNotice.ToggledMany(restart) : 0;
        Result.Text = $"{on} ligados, {off} repostos" + (fail > 0 ? $", {fail} falharam (vê o estado de cada um)" : "") + (pending > 0 ? ". Alguns só valem depois de reiniciar o PC." : ".");
        ApplyBtn.IsEnabled = true;
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_probe == null) return;
        Result.Text = "A restaurar…";
        var restart = new List<(string, bool)>();
        await Task.Run(() =>
        {
            foreach (var t in AppServices.Tweaks)
            {
                t.Refresh(_probe);
                if (!t.Touched) continue;
                bool was = t.IsOn;
                if (t.Set(false) == null && t.NeedsRestart && was != t.IsOn) restart.Add((t.Title, t.IsOn));
            }
        });
        int pending = restart.Count > 0 ? RestartNotice.ToggledMany(restart) : 0;
        Result.Text = "Valores originais repostos" + (pending > 0 ? " (alguns só voltam depois de reiniciar)." : ".");
    }
}
