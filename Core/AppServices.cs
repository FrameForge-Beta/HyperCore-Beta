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

    public static event Action<string>? Notify;
    public static void Toast(string msg) => Notify?.Invoke(msg);

    /// <summary>Pede à janela para abrir uma página (tag do menu, ex.: "FF:fans" ou "PageBench").</summary>
    public static event Action<string>? NavigateRequested;
    public static void Navigate(string tag) => NavigateRequested?.Invoke(tag);

    public static void Start()
    {
        _ = Probe;
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

        if (Settings.OcApplyOnStart is string p && !Settings.UnstableOcProfiles.Contains(p))
        {
            var prof = Settings.OcProfiles.FirstOrDefault(x => x.Name == p);
            if (prof != null) Task.Run(() => Tuner.Apply(prof));
        }
    }

    public static void Stop()
    {
        try { Fans.FanEngine.Instance.DetachAndRelease(); } catch { } // desliga o motor e devolve ventoinhas à BIOS/driver
        Sensors.SensorHub.Instance.Dispose();
        Recorder.Dispose();
        Frames.Dispose();
        Sampler.Dispose();
        Tuner.Dispose();
        Settings.Save();
    }
}
