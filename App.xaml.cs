using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace ResetExplode;

public partial class App : Application
{
    // Uma única instância: abrir o HyperCore de novo só traz a janela que já está aberta para a frente.
    private static Mutex? _single;

    private const uint SmtoAbortIfHung = 0x0002;
    private const int ActivateExistingMessage = 0x8000 + 0x47;
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static bool NotifyExistingInstance()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            bool notified = false;
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0 || pid == current.Id) return true;
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                    if (!string.Equals(process.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { return true; }

                SendMessageTimeout(hWnd, ActivateExistingMessage, IntPtr.Zero, IntPtr.Zero,
                    SmtoAbortIfHung, 1000, out IntPtr result);
                if (result != IntPtr.Zero) notified = true;
                return !notified;
            }, IntPtr.Zero);

            if (notified) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        bool first;
        try { _single = new Mutex(true, @"Local\HyperCore.SingleInstance", out first); }
        catch (UnauthorizedAccessException) { first = false; } // criado por outra instância com outro nível de permissão

        if (!first)
        {
            // Notifica a janela principal pelo HWND, mesmo quando está escondida na bandeja;
            // não depende do título nem de Process.MainWindowHandle (que é zero para janelas ocultas).
            NotifyExistingInstance();
            Shutdown();
            return;
        }
        base.OnStartup(e);
        // Tooltips: aparecem ao fim de 350 ms e ficam o tempo suficiente para ler as explicações longas
        try
        {
            System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(350));
            System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(30000));
        }
        catch (Exception) { }
        // Antes de tudo o resto: cria %AppData%\HyperCore e, no 1.º arranque, copia para lá os dados do FrameForge
        // (definições e backups têm de ser lidos já da pasta nova). O inicializador de AppPaths.Root faz a migração.
        _ = FrameForge.Core.AppPaths.Root;
        DispatcherUnhandledException += (_, ex) =>
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(FrameForge.Core.AppPaths.Root, "erros.log"), $"[{DateTime.Now}] {ex.Exception}\n\n"); } catch { }
            // Scan a correr: pára e devolve a gráfica a Stock antes de mostrar o erro
            try { FrameForge.Tuning.ScanService.Instance.OnUnhandledException(ex.Exception, fatal: false); } catch { }
            MessageBox.Show(ex.Exception.Message, "HyperCore — erro", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        // Erro fatal fora do thread da interface / fim de sessão do Windows: Stock na gráfica se o scan estiver a afinar
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try { FrameForge.Tuning.ScanService.Instance.OnUnhandledException(ex.ExceptionObject as Exception, fatal: ex.IsTerminating); } catch { }
        };
        SessionEnding += (_, _) =>
        {
            try { FrameForge.Tuning.ScanService.Instance.OnAppExit(); } catch { }
            FrameForge.Core.AppServices.ClearOcStartMark(); // desligar/reiniciar o Windows não é o OC de arranque a deitar o PC abaixo
        };

        // Scan interrompido (bloqueio/reinício/crash a meio da afinação)? Tem de ser visto ANTES de o AppServices aplicar o perfil de arranque.
        FrameForge.Tuning.RecoveryInfo? recovery = null;
        try { recovery = FrameForge.Tuning.ScanService.Instance.CheckRecoveryOnStartup(); } catch { }

        // Recuperação nova: a gráfica fica nos valores de fábrica neste arranque (o perfil de arranque não é aplicado por cima)
        bool recovered = recovery is { Novo: true };
        var s = FrameForge.Core.AppServices.Settings;
        string? skippedOc = recovered && s.OcApplyOnStart is string ocp && !s.UnstableOcProfiles.Contains(ocp) ? ocp : null;
        FrameForge.Core.AppServices.Start(skipOcOnStart: recovered);
        var window = new MainWindow();
        window.Show();
        if (recovered && recovery!.Mensagem.Length > 0)
            window.Dispatcher.BeginInvoke(() => FrameForge.Core.AppServices.Toast(recovery.Mensagem +
                                              (skippedOc != null ? $" Desta vez não apliquei o perfil de arranque '{skippedOc}'." : "")),
                                          System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (FrameForge.Core.AppServices.Settings.StartMinimizedToTray &&
            FrameForge.Core.WindowsStartupRegistration.IsStartupLaunch(e.Args))
            window.StartInTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { FrameForge.Tuning.ScanService.Instance.OnAppExit(); } catch { } // scan a meio: Stock na gráfica
        FrameForge.Core.AppServices.Stop();
        base.OnExit(e);
    }
}
