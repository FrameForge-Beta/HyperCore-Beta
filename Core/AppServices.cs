using System.IO;
using System.Text.Json;
using FrameForge.Hardware;
using FrameForge.Optimize;
using FrameForge.Overclock;
using FrameForge.Telemetry;

namespace FrameForge.Core;

/// <summary>Os serviços partilhados por todas as páginas (um só de cada, criados no arranque).</summary>
public static class AppServices
{
    public static AppSettings Settings { get; } = AppSettings.Load();
    public static FrameCapture Frames { get; } = new();
    public static SystemSampler Sampler { get; } = new();
    public static GpuTuner Tuner { get; } = new();
    public static List<Tweak> Tweaks { get; } = TweakCatalog.All();
    public static Games.SessionRecorder Recorder { get; } = new();

    private static Task<SystemProbe>? _probe;
    /// <summary>Leitura do hardware (WMI demora 1–3 s): começa no arranque e as páginas esperam por ela.</summary>
    public static Task<SystemProbe> Probe => _probe ??= Task.Run(SystemProbe.Collect);
    public static void RefreshProbe() => _probe = Task.Run(SystemProbe.Collect);

    private static Action<string>? _notify;
    private static readonly List<string> QueuedToasts = new();

    /// <summary>Avisos curtos (toast). Os que chegam antes de a janela subscrever (ex.: verificação do arranque) ficam guardados e saem logo a seguir.</summary>
    public static event Action<string>? Notify
    {
        add
        {
            string[] queued;
            lock (QueuedToasts) { _notify += value; queued = QueuedToasts.ToArray(); QueuedToasts.Clear(); }
            foreach (var m in queued) value?.Invoke(m);
        }
        remove { lock (QueuedToasts) _notify -= value; }
    }

    public static void Toast(string msg)
    {
        Action<string>? n;
        lock (QueuedToasts) { n = _notify; if (n == null) { QueuedToasts.Add(msg); return; } }
        n(msg);
    }

    /// <summary>Pede à janela para abrir uma página (tag do menu, ex.: "FF:fans" ou "PageBench").</summary>
    public static event Action<string>? NavigateRequested;
    public static void Navigate(string tag) => NavigateRequested?.Invoke(tag);

    /// <param name="skipOcOnStart">true = não aplicar o perfil de OC de arranque (houve agora a recuperação de um scan interrompido).</param>
    public static void Start(bool skipOcOnStart = false)
    {
        _ = Probe;
        LegacyMigration.StopLegacyEtwSessions();   // sessões ETW órfãs do FrameForge (crash de uma versão antiga)
        Frames.Start();
        Sampler.EnableAdvancedCpu(Settings.AdvancedCpuSensors);
        Sampler.Start();
        if (Settings.OcCrashGuard) Tuner.StartCrashGuard();
        Tuner.CrashDetected += name =>
        {
            Settings.UnstableOcProfiles.Add(name);
            Settings.Save();
            Toast($"O driver de vídeo foi abaixo com o perfil '{name}'. Voltei a Stock e marquei o perfil como instável.");
        };
        // Ventoinhas: só retomam o controlo sozinhas se o utilizador pediu "ligar ao abrir"
        var fans = Fans.FanEngine.Instance;
        fans.Load();
        if (fans.Config.ApplyOnStart && fans.Config.Enabled) fans.Attach();
        else
        {
            if (fans.Config.Enabled || fans.Config.ApplyOnStart)
            {
                fans.Config.Enabled = false;
                fans.Config.ApplyOnStart = false;
                fans.Save();
            }
            fans.DetachAndRelease();
        }
        if (Overlay.OverlayStore.Data.AutoShow || Overlay.OverlayStore.Data.AutoShowWhenGameOpen)
            System.Windows.Application.Current.Dispatcher.BeginInvoke(FrameForge.OverlayWindow.StartAutoMode);

        ApplyOcOnStart(skipOcOnStart);

        // Níveis de desempenho: confirma que o nível escolhido continua aplicado (o Windows/outros programas podem ter reposto algo).
        // Em segundo plano: espera pela leitura do hardware e corre powercfg/registo sem bloquear a janela.
        _ = Task.Run(LevelKeeper.StartupCheck);

        // Arranque com o Windows: troca a tarefa antiga "FrameForge" pela "HyperCore" (apontada para o exe atual)
        bool startWithWindows = Settings.StartWithWindows, startMinimized = Settings.StartMinimizedToTray;
        _ = Task.Run(() => WindowsStartupRegistration.RefreshOnAppStart(startWithWindows, startMinimized));
    }

    // ------------------------------------------------------------------ perfil de OC ao abrir

    /// <summary>Marcador "apliquei o perfil X ao abrir, a esta hora": se o PC for abaixo antes de a app fechar normalmente, o próximo arranque sabe.</summary>
    private static string OcStartMarkFile => Path.Combine(AppPaths.Root, "oc-arranque.json");
    private sealed record OcStartMark(string Perfil, DateTime Hora);

    /// <summary>
    /// Aplica o perfil de "Aplicar sempre ao abrir", exceto se no arranque anterior foi aplicado o mesmo perfil e desde então houve
    /// ecrã azul, bloqueio/reinício inesperado ou reset do driver: aí não o aplica, marca-o como instável e avisa.
    /// </summary>
    private static void ApplyOcOnStart(bool skip)
    {
        OcStartMark? prev = null;
        try { if (File.Exists(OcStartMarkFile)) prev = JsonSerializer.Deserialize<OcStartMark>(File.ReadAllText(OcStartMarkFile)); } catch { }
        // Recuperação de um scan: o culpado foi o scan (o marcador já não diz nada) e a gráfica fica nos valores de fábrica neste arranque
        if (skip || Settings.OcApplyOnStart is not string p || Settings.UnstableOcProfiles.Contains(p)) { ClearOcStartMark(); return; }
        var prof = Settings.OcProfiles.FirstOrDefault(x => x.Name == p);
        if (prof == null) { ClearOcStartMark(); return; }
        Task.Run(() =>
        {
            if (prev != null && prev.Perfil == p)
            {
                ResetExplode.HardwareErrors? ev = null;
                try { ev = ResetExplode.OcCheck.ErrorsSince(prev.Hora); } catch { }
                if (ev != null && (ev.UnexpectedRestarts > 0 || ev.BugChecks > 0 || ev.GpuResets > 0))
                {
                    Settings.UnstableOcProfiles.Add(p);
                    Settings.Save();
                    ClearOcStartMark();
                    string what = ev.BugChecks > 0 ? "Houve um ecrã azul" : ev.UnexpectedRestarts > 0 ? "O PC bloqueou ou reiniciou" : "O driver de vídeo foi abaixo";
                    Toast($"{what} com o perfil '{p}' aplicado ao abrir a app. Desta vez não o apliquei e marquei-o como instável.");
                    return;
                }
            }
            try { File.WriteAllText(OcStartMarkFile, JsonSerializer.Serialize(new OcStartMark(p, DateTime.UtcNow))); } catch { }
            Tuner.Apply(prof);
        });
    }

    /// <summary>Fecho normal (ou fim de sessão do Windows): o perfil de arranque não fez o PC ir abaixo.</summary>
    public static void ClearOcStartMark()
    {
        try { File.Delete(OcStartMarkFile); } catch { }
    }

    public static void Stop()
    {
        ClearOcStartMark();
        try { Fans.FanEngine.Instance.DetachAndRelease(); } catch { } // desliga o motor e devolve ventoinhas à BIOS/driver
        Sensors.SensorHub.Instance.Dispose();
        Recorder.Dispose();
        Frames.Dispose();
        Sampler.Dispose();
        Tuner.Dispose();
        Settings.Save();
    }
}
