using System.Windows;
using System.Windows.Controls;
using FrameForge.Core;
using FrameForge.Hardware;
using FrameForge.Optimize;

namespace FrameForge.Pages;

public partial class DashboardPage : UserControl
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => Render(await AppServices.Probe);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        AppServices.RefreshProbe();
        Advice.Children.Clear();
        Advice.Children.Add(Ui.T("A analisar…", "MutedText"));
        Render(await AppServices.Probe);
    }

    private void Render(SystemProbe p)
    {
        Stats.Children.Clear();
        var disp = p.PrimaryDisplay;
        Stats.Children.Add(Ui.Stat("Processador", $"{p.Cores}C/{p.Threads}T", p.CpuName));
        Stats.Children.Add(Ui.Stat("Placa gráfica", $"{p.VramGB:0} GB", p.GpuName));
        Stats.Children.Add(Ui.Stat("RAM", $"{p.RamGB:0} GB", $"{p.RamType} · {(p.Ram.Count > 0 ? p.Ram.Min(r => r.ConfiguredMHz) : 0)} MHz · {p.Ram.Count} pentes"));
        if (disp != null) Stats.Children.Add(Ui.Stat("Monitor", $"{disp.Hz} Hz", $"{disp.Width}×{disp.Height} (máx. {disp.MaxHz} Hz)"));

        Hw.Children.Clear();
        Hw.Children.Add(Ui.T("Hardware", "H2"));
        void Line(string k, string v) => Hw.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), FontSize = 13.5,
            Inlines = { new System.Windows.Documents.Run(k + "  ") { Foreground = Ui.Res("Muted") }, new System.Windows.Documents.Run(v) },
        });
        Line("CPU", $"{p.CpuName} (base {p.CpuBaseMHz} MHz)");
        Line("GPU", $"{p.GpuName} · driver {p.GpuDriver}" + (p.GpuDriverDate is DateTime d ? $" ({d:MM/yyyy})" : "") +
                    (p.PcieWidth is (uint c, uint m) ? $" · PCIe x{c}/x{m}" : "") + (p.ResizableBar is bool rb ? $" · ReBAR {(rb ? "ligado" : "desligado")}" : ""));
        Line("Placa-mãe", $"{p.Board} · BIOS {p.BiosVersion}");
        foreach (var r in p.Ram)
            Line("RAM", $"{r.Bank} {r.Slot}: {r.Manufacturer} {r.PartNumber} · {r.SizeGB:0} GB · a {r.ConfiguredMHz} MHz" +
                        (r.RatedMHz is int rm ? $" (vendido para {rm} MHz{(r.RatedCl is int cl ? $" CL{cl}" : "")})" : ""));
        foreach (var x in p.Displays)
            Line("Ecrã", $"{x.Width}×{x.Height} a {x.Hz} Hz (máx. {x.MaxHz}) · {x.Adapter}{(x.Primary ? " · principal" : "")}");
        foreach (var x in p.Drives)
            Line("Disco", $"{x.Letter} {x.Model} · {x.Media} {x.Bus} · {x.FreeGB:0} de {x.SizeGB:0} GB livres");
        Line("Windows", $"{p.Os} (build {p.OsBuild}) · plano {p.PowerPlan} · HAGS {(p.Hags switch { true => "ligado", false => "desligado", _ => "padrão do Windows" })} · Modo de Jogo {(p.GameMode == true ? "ligado" : "desligado")}");
        if (p.RunningTools.Count > 0) Line("Abertos", string.Join(", ", p.RunningTools));

        Advice.Children.Clear();
        var list = HardwareAdvisor.Analyze(p);
        foreach (var a in list)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Row(Ui.Pill(a.Area.ToUpperInvariant()), Ui.Pill(a.Severity >= 3 ? "IMPORTANTE" : a.Severity == 2 ? "AVISO" : "DICA", Ui.Severity(a.Severity))));
            var title = Ui.T(a.Title, "H2");
            title.Margin = new Thickness(0, 8, 0, 4);
            sp.Children.Add(title);
            sp.Children.Add(Ui.T(a.Detail, "Body", color: Ui.Res("Muted")));
            var fix = Ui.T("➜ " + a.Action, "Body");
            fix.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(fix);
            Advice.Children.Add(Ui.Card(sp, Ui.Severity(a.Severity)));
        }
        if (list.Count == 0) Advice.Children.Add(Ui.T("Nada de relevante. 👌", "MutedText"));
    }
}
