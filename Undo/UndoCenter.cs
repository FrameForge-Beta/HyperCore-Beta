using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using FrameForge.Core;
using FrameForge.Hardware;
using FrameForge.Optimize;
using FrameForge.Overclock;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using LegacySettings = ResetExplode.AppSettings;

namespace FrameForge.Undo;

// =====================================================================================================================
// Central Desfazer — recolhe tudo o que o HyperCore mudou e ainda está mudado, e desfaz usando as APIs que já existem:
//   • ChangeJournal (funções novas: DNS, Hz do monitor, HAGS, Modo Jogo, cache de shaders…) → ChangeJournal.TryUndo
//   • Níveis de desempenho / ajustes (TweakBackup, backup-ajustes.json) → Tweak.Set(false, exact: true), como "Repor Windows original"
//   • Arranque do Windows (settings.json antigo → StartupRemoved) → StartupManager.Restore
//   • Prioridades gravadas (IFEO\PerfOptions) → ProcessPriority.Set(exe, Auto, Auto)
//   • Overclock aplicado / perfil de arranque / controlo das ventoinhas → GpuTuner.Stock, Settings.OcApplyOnStart, FanEngine.SetEnabled
//   • Números COM libertados (ComDbBackup) → UsbCleaner.RestoreComDb
// Nada aqui muda o PC sozinho: só UndoItem.UndoAsync (chamado pela página depois de o utilizador confirmar).
// =====================================================================================================================

public enum UndoCategory { Level, Tweaks, Power, Services, Network, GamerSetup, GameMode, Startup, Priorities, Overclock, Other }

public sealed record UndoCategoryInfo(UndoCategory Id, string Title, string Glyph, string Hint, string? OpenTag, string OpenLabel);

/// <summary>Resultado de desfazer um item. Restart = ajustes que só ficam completos depois de reiniciar (para um só aviso no fim).</summary>
public sealed record UndoOutcome(bool Ok, string Message, IReadOnlyList<(string What, bool On)>? Restart = null);

/// <summary>Uma alteração ainda ativa, com o que é preciso para a desfazer.</summary>
public sealed class UndoItem
{
    public required string Key { get; init; }
    public required UndoCategory Category { get; init; }
    public required string Title { get; init; }
    /// <summary>O que está mudado agora (curto).</summary>
    public string Detail { get; init; } = "";
    /// <summary>O que acontece ao desfazer (frase para a confirmação).</summary>
    public string Effect { get; init; } = "";
    /// <summary>Explicação longa (tooltip).</summary>
    public string? Tip { get; init; }
    public DateTime? When { get; init; }
    /// <summary>A data é "vista pela primeira vez" (a alteração pode ser mais antiga).</summary>
    public bool WhenApprox { get; init; }
    public bool NeedsRestart { get; init; }
    /// <summary>Aviso (ex.: "pode ter sido outro programa"). Itens com aviso não entram por omissão no "Desfazer tudo".</summary>
    public string? Caution { get; init; }
    /// <summary>Motivo para não se poder desfazer agora (null = pode).</summary>
    public string? Blocked { get; init; }
    /// <summary>Página para tratar disto à mão (tag do menu).</summary>
    public string? OpenTag { get; init; }
    /// <summary>Ordem no "Desfazer tudo" (menor primeiro).</summary>
    public int Stage { get; init; }
    /// <summary>Chave do item que já desfaz este (ex.: o nível desfaz todos os ajustes) — no "Desfazer tudo" este salta-se.</summary>
    public string? CoveredBy { get; init; }
    internal Func<Task<UndoOutcome>> Run { get; init; } = () => Task.FromResult(new UndoOutcome(false, "Nada a fazer."));

    public bool CanUndo => Blocked == null;

    /// <summary>Desfaz (chamar no UI thread: o trabalho pesado vai para segundo plano). Nunca lança exceções.</summary>
    public async Task<UndoOutcome> UndoAsync()
    {
        if (Blocked != null) return new UndoOutcome(false, Blocked);
        try { return await Run(); }
        catch (Exception ex) { return new UndoOutcome(false, "Não foi possível desfazer: " + ex.Message); }
    }
}

public static class UndoCenter
{
    public static readonly IReadOnlyDictionary<UndoCategory, UndoCategoryInfo> Categories = new Dictionary<UndoCategory, UndoCategoryInfo>
    {
        [UndoCategory.Level] = new(UndoCategory.Level, "Nível de desempenho", "", "O nível escolhido em Níveis de desempenho.", "FF:optimize", "Níveis"),
        [UndoCategory.Tweaks] = new(UndoCategory.Tweaks, "Ajustes do Windows", "", "Definições do Windows mudadas para jogar melhor.", "FF:optimize", "Níveis"),
        [UndoCategory.Power] = new(UndoCategory.Power, "Energia", "", "Plano de energia e poupanças de energia desligadas.", "FF:optimize", "Níveis"),
        [UndoCategory.Services] = new(UndoCategory.Services, "Serviços do Windows", "", "Serviços de fundo desligados ou mudados.", "FF:optimize", "Níveis"),
        [UndoCategory.Network] = new(UndoCategory.Network, "Rede", "", "DNS e outras definições de rede.", "FF:netgame", "Rede para jogos"),
        [UndoCategory.GamerSetup] = new(UndoCategory.GamerSetup, "Configuração gamer", "", "Monitor, HAGS, Game DVR, cache de shaders…", "FF:gamersetup", "Configuração gamer"),
        [UndoCategory.GameMode] = new(UndoCategory.GameMode, "Modo Jogo", "", "O que o Modo Jogo fechou ou mudou.", "FF:gamemode", "Modo Jogo"),
        [UndoCategory.Startup] = new(UndoCategory.Startup, "Arranque do Windows", "", "Programas tirados do arranque (continuam instalados).", "PageStartup", "Arranque"),
        [UndoCategory.Priorities] = new(UndoCategory.Priorities, "Prioridade dos programas", "", "Prioridades gravadas que o Windows aplica sempre.", "FF:priorities", "Prioridades"),
        [UndoCategory.Overclock] = new(UndoCategory.Overclock, "Overclock e ventoinhas", "", "Valores da gráfica e controlo das ventoinhas.", "FF:oc", "Overclock"),
        [UndoCategory.Other] = new(UndoCategory.Other, "Outras alterações", "", "Outras coisas com cópia do que estava antes.", null, ""),
    };

    /// <summary>
    /// Definições antigas (settings.json: arranque removido, ComDB) partilhadas com a janela principal. A janela deve ligar isto ao seu
    /// próprio objeto (ver integração) para não haver duas cópias a gravar por cima uma da outra. Sem ligação, usa reflexão
    /// sobre MainWindow._settings e, em último caso, lê do disco.
    /// </summary>
    public static Func<LegacySettings>? LegacySettingsProvider { get; set; }

    private static LegacySettings? _diskLegacy;

    internal static LegacySettings Legacy()
    {
        try { if (LegacySettingsProvider?.Invoke() is LegacySettings p) return p; } catch { }
        try
        {
            var w = Application.Current?.MainWindow;
            if (w != null && w.GetType().GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(w) is LegacySettings s)
                return s;
        }
        catch { }
        return _diskLegacy ??= LegacySettings.Load();
    }

    private static bool Elevated
    {
        get { try { return ResetExplode.SensorMonitor.IsElevated; } catch { return false; } }
    }

    private const string NeedAdmin = "Precisa do HyperCore aberto como administrador.";

    // ------------------------------------------------------------------ recolha

    /// <summary>Lê tudo (só leitura). Chamar no UI thread: as leituras pesadas correm em segundo plano.</summary>
    public static async Task<List<UndoItem>> CollectAsync()
    {
        var legacy = Legacy();
        var startup = legacy.StartupRemoved.ToList();      // cópia no UI thread (a janela mexe na mesma lista)
        byte[]? comDb = legacy.ComDbBackup;
        bool admin = Elevated;

        var items = await Task.Run(() =>
        {
            var list = new List<UndoItem>();
            var live = new HashSet<string>();
            Safe(() => AddJournal(list, live));
            Safe(() => AddTweaks(list, live));
            Safe(() => AddStartup(list, live, startup, admin));
            Safe(() => AddPriorities(list, admin));
            Safe(() => AddOverclock(list, live));
            Safe(() => AddComDb(list, live, comDb, admin));
            // as prioridades usam a data do registo (sem chaves próprias)
            UndoDates.Prune(live);
            UndoDates.Flush();
            return list;
        });
        return items;
    }

    private static void Safe(Action a) { try { a(); } catch { } }

    private static (DateTime? When, bool Approx) SeenDate(HashSet<string> live, string key)
    {
        live.Add(key);
        var (w, approx) = UndoDates.Seen(key);
        return (w, approx);
    }

    // ---- registo novo (ChangeJournal)

    private static (UndoCategory Cat, string? Tag) KindInfo(string kind)
    {
        string k = kind.ToLowerInvariant();
        if (k.StartsWith("dns") || k.StartsWith("net")) return (UndoCategory.Network, "FF:netgame");
        if (k.StartsWith("gamemode") || k.StartsWith("modo-jogo")) return (UndoCategory.GameMode, "FF:gamemode");
        string[] setup = { "display", "hz", "hags", "shader", "gamedvr", "game-dvr", "dvr", "mpo", "vrr", "win-vrr", "fso", "windowed", "gamebar", "wingamemode", "win-gamemode", "gamersetup" };
        if (setup.Any(k.StartsWith)) return (UndoCategory.GamerSetup, "FF:gamersetup");
        return (UndoCategory.Other, null);
    }

    private static void AddJournal(List<UndoItem> list, HashSet<string> live)
    {
        foreach (var e in ChangeJournal.All())
        {
            var (cat, tag) = KindInfo(e.Kind);
            bool can = ChangeJournal.HasHandler(e.Kind);
            string id = e.Id;
            list.Add(new UndoItem
            {
                Key = "journal:" + id,
                Category = cat,
                Title = e.Title.Length > 0 ? e.Title : e.Kind,
                Detail = e.Detail,
                Effect = "Repõe o que estava antes desta alteração.",
                When = e.WhenUtc.ToLocalTime(),
                OpenTag = tag,
                Stage = 0,
                Blocked = can ? null : tag != null ? "Abre a página desta função para a repor (ainda não está carregada)." : "Esta versão ainda não sabe desfazer isto.",
                Run = async () =>
                {
                    var (ok, msg) = await Task.Run(() => ChangeJournal.TryUndo(id));
                    return new UndoOutcome(ok, msg);
                },
            });
        }
    }

    // ---- níveis e ajustes (TweakBackup)

    private static string[] BackupKeys(Tweak t) => t.BackupKeys.Length == 0 ? new[] { t.Id } : t.BackupKeys;

    /// <summary>Chaves do backup-ajustes.json que este ajuste tem guardadas agora.</summary>
    private static List<string> LiveBackupKeys(Tweak t)
    {
        var keys = new List<string>();
        foreach (var k in BackupKeys(t))
        {
            if (TweakBackup.Has(k)) keys.Add(k);
            keys.AddRange(TweakBackup.WithPrefix(k).Select(kv => kv.Key));
        }
        return keys;
    }

    private static UndoCategory CategoryOf(Tweak t) => t.IsService ? UndoCategory.Services : t.IsPlan || t.IsPower ? UndoCategory.Power : UndoCategory.Tweaks;

    private static void AddTweaks(List<UndoItem> list, HashSet<string> live)
    {
        var s = AppServices.Settings;
        var touched = AppServices.Tweaks.Where(t => t.Touched).ToList();
        int level = s.AppliedOptimizeLevel;
        int manual = s.TweakOverrides.Count;
        bool gameRunning = Games.GameRunner.TemporaryTweaksActive;
        string? blocked = gameRunning ? "Há um jogo aberto pelo botão JOGAR: estes ajustes voltam sozinhos quando o fechares." : !Elevated ? NeedAdmin : null;

        DateTime? earliest = null;
        bool anyApprox = false;
        var dates = new Dictionary<Tweak, (DateTime? When, bool Approx)>();
        foreach (var t in touched)
        {
            DateTime? when = null;
            foreach (var k in LiveBackupKeys(t))
            {
                live.Add("tweak:" + k);
                if (UndoDates.Exact("tweak:" + k) is DateTime d && (when == null || d < when)) when = d;
            }
            bool approx = false;
            if (when == null) { var seen = SeenDate(live, "tweak-seen:" + t.Id); when = seen.When; approx = seen.Approx; }
            dates[t] = (when, approx);
            if (when != null && (earliest == null || when < earliest)) earliest = when;
            anyApprox |= approx;
        }

        bool hasLevel = level > 0 || manual > 0;
        if (hasLevel)
        {
            string name = level > 0 ? TweakCatalog.LevelNames[Math.Clamp(level, 0, 4)] : "";
            var lvDate = earliest != null ? (earliest, anyApprox) : SeenDate(live, "level:" + level);
            string detail = touched.Count == 0 ? "Nada mudado no Windows neste momento."
                : $"{touched.Count} {(touched.Count == 1 ? "ajuste mudado" : "ajustes mudados")}" +
                  (manual > 0 ? $" · {manual} {(manual == 1 ? "escolhido" : "escolhidos")} à mão" : "") +
                  (s.KeepLevelApplied && level > 0 ? " · mantido sempre aplicado" : "");
            list.Add(new UndoItem
            {
                Key = "level",
                Category = UndoCategory.Level,
                Title = level > 0 ? $"Nível {name} ativo" : "Ajustes escolhidos à mão",
                Detail = detail,
                Effect = touched.Count > 0
                    ? $"Repõe {(touched.Count == 1 ? "o ajuste" : $"os {touched.Count} ajustes")} exatamente como {(touched.Count == 1 ? "estava" : "estavam")} antes de a app mexer e desliga o nível."
                    : "Desliga o nível (não há nada para repor no Windows).",
                Tip = level > 0 ? TweakCatalog.LevelDescriptions[Math.Clamp(level, 0, 4)] : null,
                When = lvDate.Item1, WhenApprox = lvDate.Item2,
                NeedsRestart = touched.Any(t => t.RestartToFinish),
                Blocked = touched.Count > 0 || gameRunning ? blocked : null,
                OpenTag = "FF:optimize",
                Stage = 5,
                Run = RestoreLevelAsync,
            });
        }

        foreach (var t in touched.OrderBy(t => t.Order).ThenBy(t => t.Level))
        {
            var tw = t;
            bool inLevel = level > 0 && t.Level <= level;
            string lvName = TweakCatalog.LevelNames[Math.Clamp(t.Level, 0, 4)];
            list.Add(new UndoItem
            {
                Key = "tweak:" + t.Id,
                Category = CategoryOf(t),
                Title = t.Title,
                Detail = t.Short.Length > 0 ? t.Short : t.What,
                Effect = "Volta ao valor que o Windows tinha antes." +
                         (t.RestartToFinish ? " Fica completo depois de reiniciar o PC." : "") +
                         (inLevel ? $" O nível {TweakCatalog.LevelNames[level]} continua ativo, sem este ajuste." : ""),
                Tip = string.Join("\n\n", new[] { t.What, t.Gain.Length > 0 ? "Ganho: " + t.Gain : "", $"Faz parte do nível {lvName}." }.Where(x => x.Length > 0)),
                When = dates[t].When, WhenApprox = dates[t].Approx,
                NeedsRestart = t.RestartToFinish,
                Blocked = blocked,
                OpenTag = "FF:optimize",
                Stage = 4,
                CoveredBy = hasLevel ? "level" : null,
                Run = () => RevertTweakAsync(tw),
            });
        }
    }

    /// <summary>Desfaz UM ajuste: volta ao original (como o interruptor da página Níveis) e guarda-o como exceção ao nível.</summary>
    private static async Task<UndoOutcome> RevertTweakAsync(Tweak t)
    {
        if (Games.GameRunner.TemporaryTweaksActive) return new UndoOutcome(false, "Há um jogo aberto pelo botão JOGAR: espera que feche.");
        var probe = await AppServices.Probe;
        var s = AppServices.Settings;
        var tg = TweakCatalog.Target(Math.Max(s.AppliedOptimizeLevel, 1), probe);
        bool was = false, now = false;
        string? err = await Task.Run(() =>
        {
            lock (TweakCatalog.Gate)
            {
                t.Refresh(probe);
                was = t.IsOn;
                var e = t.Set(false, tg, quiet: true, exact: true);
                now = t.IsOn;
                return e;
            }
        });
        if (err != null) return new UndoOutcome(false, $"{t.Title}: {err}");
        bool levelDefault = s.AppliedOptimizeLevel > 0 && t.Level <= s.AppliedOptimizeLevel;
        if (!levelDefault) s.TweakOverrides.Remove(t.Id); else s.TweakOverrides[t.Id] = false;
        s.Save();
        await RecheckAsync(probe); // depois de gravar a exceção: o "Manter sempre aplicado" não o dá como "reposto pelo Windows"
        var restart = t.RestartToFinish && was != now ? new[] { (t.Title, now) } : null;
        return new UndoOutcome(true, $"{t.Title}: reposto.", restart);
    }

    /// <summary>Volta a ler os ajustes (avisa a página Níveis e o "Manter sempre aplicado"). Só lê; nunca reaplica nada.</summary>
    private static Task RecheckAsync(SystemProbe probe) => Task.Run(() =>
    {
        try { lock (TweakCatalog.Gate) LevelKeeper.Recheck(probe, typeof(UndoCenter)); } catch { }
    });

    /// <summary>Igual ao "Repor Windows original" da página Níveis: repõe todos os ajustes mudados e desliga o nível.</summary>
    private static async Task<UndoOutcome> RestoreLevelAsync()
    {
        if (Games.GameRunner.TemporaryTweaksActive) return new UndoOutcome(false, "Há um jogo aberto pelo botão JOGAR: espera que feche.");
        var s = AppServices.Settings;
        var touched = AppServices.Tweaks.Where(t => t.Touched).ToList();
        int ok = 0;
        var fails = new List<string>();
        var restart = new List<(string, bool)>();
        if (touched.Count > 0)
        {
            var probe = await AppServices.Probe;
            var tg = TweakCatalog.Target(Math.Max(s.AppliedOptimizeLevel, 1), probe);
            await Task.Run(() =>
            {
                lock (TweakCatalog.Gate)
                {
                    // Ao contrário da aplicação: serviços/registo, depois energia, por fim o plano
                    foreach (var t in touched.OrderByDescending(t => t.Order))
                    {
                        t.Refresh(probe);
                        bool was = t.IsOn;
                        if (t.Set(false, tg, quiet: true, exact: true) is string err) { fails.Add($"{t.Title} ({err})"); continue; }
                        ok++;
                        if (t.RestartToFinish && was != t.IsOn) restart.Add((t.Title, t.IsOn));
                    }
                }
            });
        }
        s.AppliedOptimizeLevel = 0;
        s.TweakOverrides.Clear();
        s.Save();
        await RecheckAsync(await AppServices.Probe);
        string msg = touched.Count == 0 ? "Nível desligado." :
                     $"Windows original reposto: {ok} {(ok == 1 ? "ajuste" : "ajustes")}." +
                     (fails.Count > 0 ? $" {fails.Count} {(fails.Count == 1 ? "falhou" : "falharam")}: {string.Join("; ", fails)}." : "");
        return new UndoOutcome(fails.Count == 0, msg, restart);
    }

    // ---- arranque do Windows (StartupRemoved, settings.json antigo)

    private static DateTime? DateFromBackupName(string? path)
    {
        try
        {
            string n = Path.GetFileName(path ?? "");
            if (n.Length >= 15 && DateTime.TryParseExact(n[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)) return d;
        }
        catch { }
        return null;
    }

    private static void AddStartup(List<UndoItem> list, HashSet<string> live, List<ResetExplode.StartupBackup> removed, bool admin)
    {
        foreach (var b in removed)
        {
            var bk = b;
            string where = b.Kind == "Folder" ? (b.Machine ? "Pasta Arranque · todos os utilizadores" : "Pasta Arranque") :
                           b.Machine ? "Registo · todos os utilizadores" : "Registo · este utilizador";
            DateTime? when = DateFromBackupName(b.Backup);
            bool approx = false;
            if (when == null) { var s = SeenDate(live, $"startup:{b.Kind}:{b.Machine}:{b.Name}"); when = s.When; approx = s.Approx; }
            bool missing = b.Kind == "Folder" && (b.Backup == null || !File.Exists(b.Backup));
            string? cmdExe = null;
            try { cmdExe = b.Command != null ? ResetExplode.StartupManager.ExeFromCommand(b.Command) : null; } catch { }
            bool gone = cmdExe != null && !File.Exists(cmdExe);
            list.Add(new UndoItem
            {
                Key = $"startup:{b.Kind}:{b.Machine}:{b.Name}",
                Category = UndoCategory.Startup,
                Title = b.Name,
                Detail = "Tirado do arranque · " + where,
                Effect = $"\"{b.Name}\" volta a abrir sozinho quando ligares o PC.",
                Tip = b.Command ?? b.Original,
                When = when, WhenApprox = approx,
                Caution = missing ? "A cópia do atalho já não existe: só sai da lista." : gone ? "O programa parece já não estar instalado." : null,
                Blocked = b.Machine && !admin ? NeedAdmin : null,
                OpenTag = "PageStartup",
                Stage = 3,
                Run = () => RestoreStartupAsync(bk),
            });
        }
    }

    private static async Task<UndoOutcome> RestoreStartupAsync(ResetExplode.StartupBackup b)
    {
        await Task.Run(() => ResetExplode.StartupManager.Restore(b));
        var legacy = Legacy();
        legacy.StartupRemoved.Remove(b);
        // o objeto da janela pode ser outro (cópia de disco): tira pelo conteúdo
        legacy.StartupRemoved.RemoveAll(x => x.Kind == b.Kind && x.Machine == b.Machine && x.Name == b.Name && x.Command == b.Command && x.Backup == b.Backup);
        try { legacy.Save(); } catch (Exception ex) { return new UndoOutcome(true, $"{b.Name} voltou ao arranque, mas não consegui gravar a lista ({ex.Message})."); }
        return new UndoOutcome(true, $"{b.Name} voltou ao arranque do Windows.");
    }

    // ---- prioridades (IFEO\PerfOptions)

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(SafeRegistryHandle hKey, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved, IntPtr lpcSubKeys,
        IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, IntPtr lpcValues, IntPtr lpcbMaxValueNameLen, IntPtr lpcbMaxValueLen,
        IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);

    private static DateTime? KeyWriteTime(string exe)
    {
        try
        {
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
            using var k = hk.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{exe}\PerfOptions");
            if (k == null) return null;
            if (RegQueryInfoKey(k.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long ft) != 0) return null;
            return DateTime.FromFileTimeUtc(ft).ToLocalTime();
        }
        catch { return null; }
    }

    private static void AddPriorities(List<UndoItem> list, bool admin)
    {
        foreach (var r in ProcessPriority.ReadAll())
        {
            var rule = r;
            var parts = new List<string>();
            if (r.UnknownCpu) parts.Add($"Processador: valor {r.RawCpu}");
            else if (r.Cpu != CpuPrio.Auto) parts.Add("Processador: " + ProcessPriority.Name(r.Cpu));
            if (r.UnknownIo) parts.Add($"Disco: valor {r.RawIo}");
            else if (r.Io != IoPrio.Auto) parts.Add("Disco: " + ProcessPriority.Name(r.Io));
            bool foreign = r.UnknownCpu || r.UnknownIo;
            list.Add(new UndoItem
            {
                Key = "prio:" + r.Exe.ToLowerInvariant(),
                Category = UndoCategory.Priorities,
                Title = r.Exe,
                Detail = string.Join(" · ", parts),
                Effect = $"Apaga a regra: {r.Exe} volta a abrir com a prioridade normal do Windows.",
                Tip = r.UnknownCpu ? null : ProcessPriority.Explain(r.Cpu),
                When = KeyWriteTime(r.Exe),
                Caution = foreign ? "Tem um valor que o HyperCore não usa: deve ser de outro programa." : null,
                Blocked = admin ? null : NeedAdmin,
                OpenTag = "FF:priorities",
                Stage = 2,
                Run = async () =>
                {
                    await Task.Run(() => ProcessPriority.Set(rule.Exe, CpuPrio.Auto, IoPrio.Auto));
                    return new UndoOutcome(true, $"{rule.Exe}: regra de prioridade apagada. Vale da próxima vez que abrir.");
                },
            });
        }
    }

    // ---- overclock, perfil de arranque e ventoinhas

    private static void AddOverclock(List<UndoItem> list, HashSet<string> live)
    {
        var tuner = AppServices.Tuner;
        var s = AppServices.Settings;
        string? scanBusy = tuner.ScanReserved ? "Há um scan a afinar a gráfica: espera que acabe." : null;

        var applied = tuner.Applied;
        if (applied != null && !tuner.AppliedIsStock)
        {
            bool test = applied.Name == Tuning.GpuAutoTuner.TestProfileName;
            var d = SeenDate(live, "oc:applied:" + applied.Name);
            list.Add(new UndoItem
            {
                Key = "oc:applied",
                Category = UndoCategory.Overclock,
                Title = $"Perfil \"{applied.Name}\" aplicado na gráfica",
                Detail = OcText(applied.CoreOffset, applied.MemOffset, applied.PowerPct, applied.LockMaxClock),
                Effect = "Põe a gráfica já nos valores de fábrica (Stock).",
                Tip = applied.Description.Length > 0 ? applied.Description : null,
                When = d.When, WhenApprox = d.Approx,
                Blocked = scanBusy,
                Caution = test ? "É um valor em teste do scan." : null,
                OpenTag = "FF:oc",
                Stage = 1,
                Run = StockAsync,
            });
        }
        else if (applied == null && tuner.Supported && !tuner.ScanReserved)
        {
            // Sem perfil nesta sessão: a placa pode ter valores fora de fábrica de uma sessão anterior ou de outro programa (só leitura)
            try
            {
                var st = tuner.Read();
                if ((st.CoreOffset ?? 0) != 0 || (st.MemOffset ?? 0) != 0)
                {
                    var d = SeenDate(live, $"oc:foreign:{st.CoreOffset}:{st.MemOffset}");
                    list.Add(new UndoItem
                    {
                        Key = "oc:foreign",
                        Category = UndoCategory.Overclock,
                        Title = "A gráfica tem valores fora de fábrica",
                        Detail = OcText(st.CoreOffset ?? 0, st.MemOffset ?? 0, null, 0),
                        Effect = "Põe a gráfica já nos valores de fábrica (Stock).",
                        When = d.When, WhenApprox = d.Approx,
                        Caution = "Pode ter sido outro programa (ex.: MSI Afterburner) ou uma sessão anterior.",
                        OpenTag = "FF:oc",
                        Stage = 1,
                        Run = StockAsync,
                    });
                }
            }
            catch { }
        }

        if (s.OcApplyOnStart is string onStart)
        {
            string name = onStart;
            var d = SeenDate(live, "oc:onstart:" + name);
            list.Add(new UndoItem
            {
                Key = "oc:onstart",
                Category = UndoCategory.Overclock,
                Title = $"Aplicar \"{name}\" sempre que o HyperCore abre",
                Detail = s.UnstableOcProfiles.Contains(name) ? "Marcado como instável: já não é aplicado." : "Perfil de arranque ligado.",
                Effect = "Deixa de aplicar este perfil ao abrir. Não mexe na gráfica agora.",
                When = d.When, WhenApprox = d.Approx,
                OpenTag = "FF:oc",
                Stage = 1,
                Run = () =>
                {
                    if (AppServices.Settings.OcApplyOnStart == name) AppServices.Settings.OcApplyOnStart = null;
                    AppServices.Settings.Save();
                    AppServices.ClearOcStartMark();
                    return Task.FromResult(new UndoOutcome(true, "O perfil de arranque foi desligado."));
                },
            });
        }

        var fans = Fans.FanEngine.Instance;
        if (fans.IsAttached || (fans.Config.Enabled && fans.Config.ApplyOnStart))
        {
            var d = SeenDate(live, "fans");
            list.Add(new UndoItem
            {
                Key = "fans",
                Category = UndoCategory.Overclock,
                Title = "Ventoinhas controladas pelo HyperCore",
                Detail = (fans.Config.ApplyOnStart ? "Liga sozinho ao abrir · " : "") + $"{fans.Config.Controls.Count(c => c.Mode != Fans.ControlMode.Auto)} com curva ou valor fixo",
                Effect = "Devolve as ventoinhas à BIOS/placa e deixa de as ligar ao abrir.",
                When = d.When, WhenApprox = d.Approx,
                OpenTag = "FF:fans",
                Stage = 1,
                Run = async () =>
                {
                    await Task.Run(() => Fans.FanEngine.Instance.SetEnabled(false));
                    return new UndoOutcome(true, "As ventoinhas voltaram ao controlo da BIOS/placa.");
                },
            });
        }
    }

    private static string OcText(int core, int mem, int? powerPct, int lockMax)
    {
        var p = new List<string>();
        if (core != 0) p.Add($"Núcleo {core:+0;-0} MHz");
        if (mem != 0) p.Add($"Memória {mem:+0;-0} MHz");
        if (powerPct is int pp && pp != 100) p.Add($"Energia {pp}%");
        if (lockMax > 0) p.Add($"Teto {lockMax} MHz");
        return p.Count > 0 ? string.Join(" · ", p) : "Valores próprios";
    }

    private static async Task<UndoOutcome> StockAsync()
    {
        if (AppServices.Tuner.ScanReserved) return new UndoOutcome(false, "Há um scan a afinar a gráfica: espera que acabe.");
        var r = await Task.Run(() => AppServices.Tuner.StockDetailed());
        bool ok = r.CoreError == null && r.MemError == null;
        return new UndoOutcome(ok, ok ? "A gráfica voltou aos valores de fábrica." : "Não consegui pôr tudo em Stock: " + r.Summary);
    }

    // ---- números COM (ComDbBackup)

    private static void AddComDb(List<UndoItem> list, HashSet<string> live, byte[]? backup, bool admin)
    {
        if (backup == null) return;
        var d = SeenDate(live, "comdb");
        list.Add(new UndoItem
        {
            Key = "comdb",
            Category = UndoCategory.Other,
            Title = "Números COM libertados",
            Detail = "Reservas de portas COM de dispositivos desligados.",
            Effect = "Repõe as reservas de números COM como estavam.",
            When = d.When, WhenApprox = d.Approx,
            Blocked = admin ? null : NeedAdmin,
            OpenTag = "PageMakerPorts",
            Stage = 3,
            Run = async () =>
            {
                await Task.Run(() => ResetExplode.UsbCleaner.RestoreComDb(backup));
                var legacy = Legacy();
                legacy.ComDbBackup = null;
                try { legacy.Save(); } catch { }
                return new UndoOutcome(true, "Reservas de números COM repostas.");
            },
        });
    }

    // ------------------------------------------------------------------ desfazer vários

    /// <summary>Desfaz um item e, se for preciso, mostra o aviso de reinício. Chamar no UI thread.</summary>
    public static async Task<UndoOutcome> UndoOneAsync(UndoItem item)
    {
        var o = await item.UndoAsync();
        if (o.Restart is { Count: > 0 } r) { try { RestartNotice.ToggledMany(r); } catch { } }
        return o;
    }

    /// <summary>
    /// Desfaz uma seleção pela ordem certa (registo novo → OC → prioridades → arranque → ajustes → nível). Os itens cobertos por
    /// outro item também escolhido (ex.: ajustes com o nível) saltam-se. Chamar no UI thread. Junta os pedidos de reinício num só aviso.
    /// </summary>
    public static async Task<List<(UndoItem Item, UndoOutcome Outcome)>> UndoManyAsync(IEnumerable<UndoItem> chosen, IProgress<(int Done, int Total, UndoItem? Current)>? progress = null)
    {
        var all = chosen.Where(i => i.CanUndo).ToList();
        var keys = all.Select(i => i.Key).ToHashSet();
        var run = all.Where(i => i.CoveredBy == null || !keys.Contains(i.CoveredBy))
                     .OrderBy(i => i.Stage).ThenByDescending(i => i.When ?? DateTime.MinValue).ToList();
        var results = new List<(UndoItem, UndoOutcome)>();
        var restart = new List<(string, bool)>();
        for (int n = 0; n < run.Count; n++)
        {
            progress?.Report((n, run.Count, run[n]));
            var o = await run[n].UndoAsync();
            if (o.Restart != null) restart.AddRange(o.Restart);
            results.Add((run[n], o));
        }
        progress?.Report((run.Count, run.Count, null));
        if (restart.Count > 0) { try { RestartNotice.ToggledMany(restart); } catch { } }
        return results;
    }
}
