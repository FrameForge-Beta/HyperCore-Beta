using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using FrameForge;
using FrameForge.Core;
using FrameForge.Pages;
using FrameForge.Telemetry;

namespace ResetExplode;

/// <summary>
/// Integra as páginas do HyperCore, o atalho de gravação e os avisos quando um relatório de sessão fica pronto.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, UserControl> _forgePages = new();
    private const int HotRecord = 0x4652;
    private const int HotUserOverlay = 0x4658;
    // Ctrl+Alt + / − (teclado principal e numérico): tamanho do overlay
    private static readonly (int Id, uint Vk, double Step)[] HotOverlaySize =
        { (0x4654, 0xBB, 0.1), (0x4655, 0x6B, 0.1), (0x4656, 0xBD, -0.1), (0x4657, 0x6D, -0.1) };

    /// <summary>
    /// Cria cada página "FF:chave" uma vez e guarda-a (mantém o estado ao trocar de página).
    /// Página nova: ficheiro Pages/XxxPage.cs (classe UserControl) + um caso aqui + um NavTab("FF:chave", …) no NavCatalog.
    /// </summary>
    private UserControl ForgePage(string key)
    {
        if (_forgePages.TryGetValue(key, out var page)) return page;
        page = key switch
        {
            "home" => new HomePage(),
            "glossary" => new GlossaryPage(),
            "fans" => new FansPage(),
            "priorities" => new PrioritiesPage(),
            "overlay" => new OverlayPage(),
            "games" => new GamesPage(),
            "sessions" => new SessionsPage(),
            "optimize" => new OptimizePage(),
            "maintenance" => new MaintenancePage(),
            "oc" => new OverclockPage(),
            "scan" => new ScanPage(),
            "tips" => new TipsPage(),
            "settings" => new SettingsPage(SetTrayMode),
            "diag" => new DashboardPage(),
            "netgame" => new GameNetPage(),
            "peripherals" => new PeripheralsPage(),
            "undo" => new UndoPage(),
            "gamemode" => new GameModePage(),
            "gamersetup" => new GamerSetupPage(),
            _ => new DashboardPage(),
        };
        return _forgePages[key] = page;
    }

    /// <summary>Chamado pelo construtor (InitForge) depois do InitializeComponent.</summary>
    private void UpdateRestartBar()
    {
        var pending = RestartNotice.Pending;
        RestartBar.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RestartText.Text = string.Join("  ·  ", pending);
    }

    private void RestartNow_Click(object sender, RoutedEventArgs e) => RestartNotice.Request(); // abre o aviso (com 1 s de espera) para confirmar

    private void InitForge()
    {
        InitShell();
        RestartNotice.Init();
        RestartNotice.Changed += () => Dispatcher.Invoke(UpdateRestartBar);
        UpdateRestartBar();
        AppServices.NavigateRequested += tag => Dispatcher.Invoke(() => NavigateTo(tag));
        FrameForge.Overlay.OverlayStore.Changed += () => Dispatcher.BeginInvoke(new Action(ApplyOverlayHotkey));
        AppServices.Notify += msg => Dispatcher.Invoke(() => Toast("HyperCore", msg, ""));
        AppServices.Recorder.Finished += (r, _) => Dispatcher.Invoke(() =>
        {
            Toast($"Relatório de {r.Data.Game}: nota {r.Grade}", r.Verdict + " — abre em Jogos › Sessões e relatórios.", "");
            if (_forgePages.TryGetValue("sessions", out var s)) ((SessionsPage)s).Reload();
        });
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            RegisterHotKey(h, HotRecord, 0x0001 | 0x0002 | 0x4000, 0x52);       // Ctrl+Alt+R
            foreach (var k in HotOverlaySize) RegisterHotKey(h, k.Id, 0x0001 | 0x0002, k.Vk); // com repetição: manter carregado vai crescendo
            ApplyOverlayHotkey();
            HwndSource.FromHwnd(h)?.AddHook(ForgeHotkeys);
        };
        Closed += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(h, HotRecord);
            UnregisterOverlayHotkey();
            foreach (var k in HotOverlaySize) UnregisterHotKey(h, k.Id);
            FrameForge.OverlayWindow.HideOverlay();
        };
    }

    private IntPtr ForgeHotkeys(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0312) return IntPtr.Zero;
        int id = wParam.ToInt32();
        if (id == HotRecord) { handled = true; _ = ToggleRecord(); }
        else if (id == HotUserOverlay)
        {
            handled = true;
            ToggleUserOverlay();
        }
        else if (HotOverlaySize.FirstOrDefault(k => k.Id == id) is { Id: > 0 } size)
        {
            handled = true;
            var p = FrameForge.Overlay.OverlayStore.Active;
            p.Scale = Math.Clamp(Math.Round(p.Scale + size.Step, 2), 0.5, 2.5);
            FrameForge.Overlay.OverlayStore.Save(); // o overlay aberto e o editor reconstroem-se sozinhos
        }
        return IntPtr.Zero;
    }

    private void ApplyOverlayHotkey()
    {
        var settings = FrameForge.Overlay.OverlayStore.Data;
        var hwnd = new WindowInteropHelper(this).Handle;
        UnregisterOverlayHotkey();
        uint modifiers = settings.HotkeyModifiers & HotkeyModifiersMask;
        uint key = settings.HotkeyVirtualKey;
        bool keySupported = key is >= 0x30 and <= 0x5A or >= 0x60 and <= 0x69 or >= 0x70 and <= 0x87;
        if (hwnd == IntPtr.Zero || !settings.HotkeyEnabled || modifiers == 0 || !keySupported) return;
        if (RegisterHotKey(hwnd, HotUserOverlay, modifiers | 0x4000, key))
            _userOverlayHotkeyRegistered = true;
        else
            AppServices.Toast("A combinação de teclas do overlay já está a ser utilizada por outra aplicação.");
    }

    private void UnregisterOverlayHotkey()
    {
        if (!_userOverlayHotkeyRegistered) return;
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HotUserOverlay);
        _userOverlayHotkeyRegistered = false;
    }

    private void ToggleUserOverlay()
    {
        if (FrameForge.OverlayWindow.IsShown)
        {
            FrameForge.OverlayWindow.HideOverlay();
            return;
        }
        int foreground = SystemSampler.ForegroundPid();
        FrameForge.OverlayWindow.ShowFor(AppServices.Frames.ActivePresenters().Any(p => p.Pid == foreground) ? foreground : 0);
    }

    private async Task ToggleRecord()
    {
        var rec = AppServices.Recorder;
        if (rec.Active)
        {
            Toast("HyperCore", "A analisar a sessão…", "");
            await rec.StopAsync();
            return;
        }
        int pid = SystemSampler.ForegroundPid();
        if (!AppServices.Frames.ActivePresenters().Any(p => p.Pid == pid))
        {
            Toast("HyperCore", "Ctrl+Alt+R: a janela em primeiro plano não está a desenhar frames.", "");
            return;
        }
        string name = SystemSampler.ProcessName(pid) ?? $"PID {pid}";
        await rec.StartAsync(pid, name);
        Toast("A gravar " + name, "Ctrl+Alt+R outra vez para parar e gerar o relatório.", "");
    }
    // RegisterHotKey/UnregisterHotKey: declarados em MainWindow.xaml.cs
}
