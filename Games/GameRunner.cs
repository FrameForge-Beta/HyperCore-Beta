using System.Diagnostics;
using System.IO;
using FrameForge.Core;
using FrameForge.Optimize;
using FrameForge.Overclock;

namespace FrameForge.Games;

/// <summary>
/// Botão JOGAR: aplica o perfil, abre o jogo, grava a sessão e desfaz o que for temporário quando o jogo fecha.
///
/// Regras anti-cheat (valem para TODOS os jogos, não só os com anti-cheat):
/// - nunca abre o processo do jogo (nem para mudar prioridade): segue-o só pela lista de processos do Windows;
/// - o FrameForge nunca injeta, nunca lê memória do jogo, não faz hooks;
/// - frames via ETW (Windows) e sensores via driver/contadores do sistema;
/// - o overlay FrameForge é uma janela própria, fora do processo do jogo.
/// </summary>
public sealed class GameRunner
{
    public static async Task RunAsync(LibGame g, GameProfile p, Action<string> status, CancellationToken ct)
    {
        // Fotografia do valor bruto de cada ajuste que o JOGAR mexe: no fim repõe-se exatamente isto (não o "original de fábrica")
        var snapshots = new List<(Tweak Tweak, string Snap, bool HadBackup)>();
        string? planBefore = null;
        OcProfile? ocBefore = AppServices.Tuner.Applied;
        // O perfil temporário de um passo do scan nunca é "o perfil de antes" (reaplicá-lo no fim do jogo deixaria um valor em teste na placa)
        if (ocBefore?.Name == FrameForge.Tuning.GpuAutoTuner.TestProfileName || (ocBefore != null && GpuTuner.IsStockValues(ocBefore))) ocBefore = null;
        bool ocChanged = false;
        try
        {
            // 1) Fechar programas pedidos (pede para fechar; não mata à força)
            foreach (var name in p.CloseBeforeLaunch)
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try { status($"A fechar {name}…"); proc.CloseMainWindow(); } catch { }
                    proc.Dispose();
                }

            // 2) Otimizações temporárias (só as que fazem efeito na hora, sem reiniciar)
            if (p.OptimizeLevel > 0)
            {
                var probe = await AppServices.Probe;
                var target = TweakCatalog.Target(p.OptimizeLevel, probe);
                await Task.Run(() =>
                {
                    lock (TweakCatalog.Gate)
                        foreach (var t in AppServices.Tweaks.Where(t => t.Level <= p.OptimizeLevel && t.Instant).OrderBy(t => t.Order))
                        {
                            t.Refresh(probe);
                            if (!t.Enabled || t.Capture == null || t.Restore == null) continue;
                            // Plano: se já há um plano de desempenho ativo (Alto desempenho, Máximo ou personalizado), não se mexe
                            if (t.IsPlan ? t.IsOn : t.Satisfied(target)) continue;
                            string? snap;
                            try { snap = t.Capture(); } catch { snap = null; }
                            if (snap == null) continue; // sem fotografia não havia como repor: não mexer
                            bool had = t.Touched;
                            if (t.Set(true, target, quiet: true) == null) snapshots.Add((t, snap, had));
                            else
                            {
                                try { t.Restore(snap); if (!had) t.ForgetBackup(); } catch { }
                            }
                        }
                });
                status($"Otimização nível {p.OptimizeLevel} aplicada ({snapshots.Count} ajustes temporários).");
            }
            else if (p.HighPerfPlan)
            {
                // Em segundo plano (powercfg demora) e sem abortar o jogo: o powercfg lança exceção se o Windows recusar
                // (ex.: PCs com Modern Standby sem o plano Alto desempenho)
                string? planErr = await Task.Run(() =>
                {
                    lock (TweakCatalog.Gate)
                        try
                        {
                            var plan = TweakCatalog.ActivePlan();
                            if (plan != null && !TweakCatalog.IsPerformancePlan(plan.Guid, plan.Name))
                            {
                                TweakCatalog.EnsureScheme(TweakCatalog.HighPerf);
                                TweakCatalog.PowerCfg($"/setactive {TweakCatalog.HighPerf}");
                                planBefore = plan.Guid;
                            }
                            return null;
                        }
                        catch (Exception ex) { return ex.Message; }
                });
                if (planErr != null) status("Não consegui ativar o plano Alto desempenho: " + planErr);
            }

            // 3) Perfil de overclock só durante o jogo
            if (p.OcProfile is string ocName && AppServices.Settings.OcProfiles.FirstOrDefault(x => x.Name == ocName) is OcProfile oc)
            {
                if (AppServices.Settings.UnstableOcProfiles.Contains(oc.Name))
                    status($"O perfil '{oc.Name}' está marcado como instável: não o apliquei.");
                else if (FrameForge.Tuning.ScanService.Instance.Running)
                    status($"Há um scan a afinar a gráfica: não apliquei o overclock '{oc.Name}' neste jogo.");
                else
                {
                    var log = await Task.Run(() => AppServices.Tuner.Apply(oc));
                    ocChanged = true;
                    status($"Overclock '{oc.Name}': " + string.Join(" · ", log.Take(3)));
                }
            }

            // 4) Abrir o jogo (sem herdar privilégios de administrador quando possível)
            status($"A abrir {g.Name}…");
            Launch(g, p);

            // 6) Esperar pelo processo do jogo (até 4 min: launchers e atualizações demoram)
            int pid = 0;
            for (int i = 0; i < 160 && pid == 0; i++)
            {
                await Task.Delay(1500, ct);
                pid = FindPid(g, p);
                if (i == 20 && pid == 0) status($"À espera do {g.Name}… (o launcher pode estar a atualizar)");
            }
            if (pid == 0) { status("Não encontrei o processo do jogo. Podes gravar manualmente no separador Sessões."); return; }

            if (p.RecordSession)
                await AppServices.Recorder.StartAsync(pid, g.Name, g, ocChanged ? p.OcProfile : ocBefore?.Name, p.OptimizeLevel);
            if (p.ShowOverlay) OverlayWindow.ShowFor(pid);

            // O primeiro processo pode ser um lançador/bootstrap: espera (até ~30 s) pelo que desenha frames — esse é o jogo a sério
            for (int i = 0; i < 20 && !Presenting(pid); i++)
            {
                await Task.Delay(1500, ct);
                int real = FindPid(g, p);
                if (real != 0 && real != pid && Presenting(real))
                {
                    pid = real;
                    AppServices.Recorder.Retarget(pid);
                    if (p.ShowOverlay) OverlayWindow.ShowFor(pid);
                }
            }
            ReportRunning(g, p, pid, status);

            // 7) Seguir o processo pela lista do sistema (sem abrir handle para ele)
            while (true)
            {
                await Task.Delay(2000, ct);
                if (Alive(pid)) continue;
                int again = 0;
                for (int i = 0; i < 8 && again == 0; i++) { await Task.Delay(1500, ct); again = FindPid(g, p); }
                if (again == 0) break;
                pid = again;
                AppServices.Recorder.Retarget(pid);
                if (p.ShowOverlay) OverlayWindow.ShowFor(pid);
                await Task.Delay(3000, ct);
                ReportRunning(g, p, pid, status); // processo novo: volta a confirmar a prioridade
            }
            status($"{g.Name} fechou. A analisar a sessão…");
        }
        catch (OperationCanceledException) { status("Acompanhamento cancelado."); }
        catch (Exception ex) { status("Falhou: " + ex.Message); }
        finally
        {
            OverlayWindow.HideOverlay();
            if (AppServices.Recorder.Active)
            {
                var r = await AppServices.Recorder.StopAsync();
                if (r != null) status($"Relatório pronto: nota {r.Value.Report.Grade} — {r.Value.Report.Verdict}");
            }
            if (snapshots.Count > 0)
                await Task.Run(() =>
                {
                    lock (TweakCatalog.Gate)
                        for (int i = snapshots.Count - 1; i >= 0; i--) // ao contrário: as definições de energia antes de trocar o plano de volta
                        {
                            var (t, snap, had) = snapshots[i];
                            try { t.Restore!(snap); if (!had) t.ForgetBackup(); } catch { }
                            t.ReadState();
                        }
                });
            if (planBefore != null) { try { TweakCatalog.PowerCfg($"/setactive {planBefore}"); } catch { } }
            // Com um scan a correr não se mexe na placa: um Stock a meio de um passo validava um valor que não foi testado.
            // O fim do scan repõe sempre os valores de fábrica.
            if (ocChanged && !FrameForge.Tuning.ScanService.Instance.Running)
                await Task.Run(() => { if (ocBefore != null) AppServices.Tuner.Apply(ocBefore); else AppServices.Tuner.Stock(); });
        }
    }

    private static bool Presenting(int pid) => AppServices.Frames.ActivePresenters().Any(x => x.Pid == pid);

    /// <summary>
    /// Estado "a correr": confirma se a prioridade gravada (PerfOptions) ficou mesmo aplicada — lido da lista de processos, sem abrir o jogo —
    /// e memoriza o nome do processo que desenha frames, para a prioridade do perfil apontar ao .exe certo da próxima vez.
    /// </summary>
    private static void ReportRunning(LibGame g, GameProfile p, int pid, Action<string> status)
    {
        string? name = Telemetry.SystemSampler.ProcessName(pid);
        if (name != null && Presenting(pid) && p.LastProcess != name + ".exe")
        {
            p.LastProcess = name + ".exe";
            AppServices.Settings.Save();
        }
        string? prio = name != null ? ProcessPriority.Verify(pid, name) : null;
        status($"{g.Name} a correr" + (p.RecordSession ? " · a gravar sessão (frames + sensores)" : "") + (prio != null ? " · " + prio : "") + ".");
    }

    private static void Launch(LibGame g, GameProfile p)
    {
        string args = string.Join(" ", new[] { g.LaunchArgs, p.ExtraArgs }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (g.LaunchExe != null)
            Process.Start(new ProcessStartInfo(g.LaunchExe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(g.LaunchExe) ?? "" });
        else if (g.LaunchUri != null && p.ExeOverride == null)
            Process.Start(new ProcessStartInfo(g.LaunchUri) { UseShellExecute = true });
        else if ((p.ExeOverride ?? g.ExePath) is string exe)
        {
            // Sem argumentos, abrir pelo Explorer faz o jogo arrancar como utilizador normal (não herda o administrador do FrameForge)
            if (args.Length == 0) Process.Start("explorer.exe", $"\"{exe}\"");
            else Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
        }
        else throw new InvalidOperationException("Não sei como abrir este jogo: escolhe o .exe no perfil.");
    }

    private static int FindPid(LibGame g, GameProfile p)
    {
        var names = new HashSet<string>(g.ProcessNames, StringComparer.OrdinalIgnoreCase);
        if (p.ExeOverride != null) names.Add(Path.GetFileNameWithoutExtension(p.ExeOverride));
        // Preferir o processo que está a apresentar frames (o jogo em si, não o launcher dele)
        var presenting = AppServices.Frames.ActivePresenters().Select(x => x.Pid).ToHashSet();
        int best = 0;
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (names.Contains(proc.ProcessName) && (best == 0 || presenting.Contains(proc.Id))) best = proc.Id;
            }
            finally { proc.Dispose(); }
        }
        return best;
    }

    private static bool Alive(int pid)
    {
        foreach (var proc in Process.GetProcesses())
        {
            bool hit = proc.Id == pid;
            proc.Dispose();
            if (hit) return true;
        }
        return false;
    }
}
