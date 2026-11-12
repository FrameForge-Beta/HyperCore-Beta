using System.Diagnostics;
using System.IO;
using FrameForge.Analysis;
using FrameForge.Core;
using FrameForge.Hardware;

namespace FrameForge.Games;

/// <summary>Grava uma sessão (frames + sensores) de um processo e, no fim, gera o relatório.</summary>
public sealed class SessionRecorder : IDisposable
{
    private SessionData? _data;
    private int _pid;

    public bool Active => _data != null;
    public string? GameName => _data?.Game;
    public int Pid => _pid;
    public DateTime? StartedAt => _data?.Start;
    public event Action<SessionReport, string>? Finished;

    public async Task StartAsync(int pid, string game, LibGame? lib = null, string? ocProfile = null, int optimizeLevel = 0)
    {
        if (Active) await StopAsync();
        var probe = await AppServices.Probe;
        var now = DateTime.Now;
        string? exe = Telemetry.SystemSampler.ProcessName(pid);
        string? media = null;
        if (lib != null && lib.InstallDir.Length >= 2)
            media = probe.Drives.FirstOrDefault(d => lib.InstallDir.StartsWith(d.Letter, StringComparison.OrdinalIgnoreCase))?.Media;

        _data = new SessionData
        {
            Game = game, Exe = exe, AntiCheat = lib?.AntiCheat?.Name, Start = now, OcProfile = ocProfile, OptimizeLevel = optimizeLevel,
            MonitorHz = probe.PrimaryDisplay?.Hz ?? 60, Cpu = probe.CpuName, Gpu = probe.GpuName, RamGB = probe.RamGB, VramGB = probe.VramGB,
            Board = probe.Board, GameDriveMedia = media,
        };
        _pid = pid;
        AppServices.Sampler.GamePid = pid;
        AppServices.Frames.BeginRecording(pid, now);
        AppServices.Sampler.BeginRecording(now);
    }

    public void Retarget(int pid)
    {
        _pid = pid;
        AppServices.Sampler.GamePid = pid;
        AppServices.Frames.RetargetRecording(pid);
    }

    public async Task<(SessionReport Report, string Path)?> StopAsync()
    {
        var d = _data;
        if (d == null) return null;
        _data = null;
        d.Presents = AppServices.Frames.EndRecording();
        d.Samples = AppServices.Sampler.EndRecording();
        d.DurationMs = (DateTime.Now - d.Start).TotalMilliseconds;
        AppServices.Sampler.GamePid = 0;
        _pid = 0;

        var report = await Task.Run(() => SessionAnalyzer.Analyze(d, AppServices.Settings.StutterSensitivity));
        string path = await Task.Run(() => ReportHtml.Save(report));
        Finished?.Invoke(report, path);
        return (report, path);
    }

    public void Dispose()
    {
        if (Active) try { StopAsync().GetAwaiter().GetResult(); } catch { }
    }

    public static IEnumerable<string> ListReports() =>
        Directory.EnumerateFiles(AppPaths.Reports, "*.html").OrderByDescending(File.GetLastWriteTime);
}
