using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FrameForge.Core;
using FrameForge.Optimize;
using Microsoft.Win32;

namespace FrameForge.Games;

// =====================================================================================================================
// Modo Jogo (FF:gamemode)
// ---------------------------------------------------------------------------------------------------------------------
// Um clique antes de jogar: fecha programas pesados de fundo, pausa a nuvem e o Windows Update, liberta a memória em
// cache, aplica o nível de desempenho e o plano de energia. Ao desligar repõe tudo pela "fotografia" tirada ao ligar.
//
// Segurança:
// - nunca fecha (nem abre handles para) um processo que esteja a desenhar frames (um jogo), nem anti-cheats;
// - os programas com janela recebem um pedido para fechar (como clicar no X); só os de fundo marcados como seguros
//   (launchers, nuvem, atualizadores) são terminados se não fecharem sozinhos. Os navegadores nunca são forçados;
// - a fotografia é gravada em modo-jogo.json DEPOIS DE CADA PASSO: se a app (ou o PC) for abaixo com o modo ligado,
//   Init() repõe tudo no arranque seguinte;
// - enquanto está ligado há uma entrada "modo-jogo" no ChangeJournal (Central Desfazer) que também sabe repor tudo;
// - a pausa do Windows Update acaba sozinha (UpdatePauseHours), mesmo que o HyperCore nunca mais abra.
// =====================================================================================================================

/// <summary>Grupo de um programa que o Modo Jogo sabe fechar.</summary>
public enum GameModeGroup { Launcher, Cloud, Heavy, Browser, Custom }

/// <summary>Programa conhecido. <paramref name="Force"/> = pode ser terminado se não fechar a pedido (não perde trabalho).</summary>
public sealed record GameModeApp(string Process, string Name, GameModeGroup Group, bool Force);

/// <summary>Programa aberto agora (agrupado pelo nome do processo). Lido só da lista de processos do Windows, sem abrir handles.</summary>
public sealed record RunningApp(string Process, string Name, GameModeGroup? Group, long MemoryBytes, int Count, bool HasWindow);

public enum GameModeLogKind { Ok, Info, Warn }

public sealed class GameModeLogItem
{
    public DateTime When { get; set; }
    public GameModeLogKind Kind { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>O que o utilizador escolheu (modo-jogo-config.json).</summary>
public sealed class GameModeConfig
{
    /// <summary>0 = não mexer; 1..4 = nível de desempenho (só os ajustes que fazem efeito na hora, temporários).</summary>
    public int Level { get; set; } = 1;
    /// <summary>0 = não mexer; 1 = Alto desempenho; 2 = Desempenho máximo.</summary>
    public int Plan { get; set; } = 1;
    public bool CloseLaunchers { get; set; } = true;
    public bool PauseCloud { get; set; } = true;
    public bool CloseHeavy { get; set; } = true;
    public bool CloseBrowsers { get; set; }
    public bool PauseUpdates { get; set; } = true;
    public bool FreeRam { get; set; } = true;
    public bool Reopen { get; set; } = true;
    /// <summary>O utilizador já confirmou uma vez o que o Modo Jogo faz (o atalho e a bandeja só ligam diretamente depois disso).</summary>
    public bool Confirmed { get; set; }
    /// <summary>Processos que o utilizador acrescentou à lista (nome sem .exe).</summary>
    public List<string> Extra { get; set; } = new();
    /// <summary>Programas conhecidos que o utilizador quer manter abertos (ex.: o launcher do jogo que usa).</summary>
    public List<string> Keep { get; set; } = new() { "steam" };

    // Última sessão (para a página mostrar "o que fez" mesmo depois de reabrir a app)
    public DateTime? LastStartUtc { get; set; }
    public DateTime? LastEndUtc { get; set; }
    public List<GameModeLogItem> LastLog { get; set; } = new();
}

/// <summary>Fotografia do que o Modo Jogo mudou (modo-jogo.json e Data da entrada do ChangeJournal).</summary>
public sealed class GameModeSession
{
    public DateTime StartedUtc { get; set; }
    /// <summary>Hora (UTC) a que o Windows arrancou: se mudar, o PC reiniciou e não se volta a abrir programas.</summary>
    public DateTime BootUtc { get; set; }
    public string JournalId { get; set; } = "";
    public int Level { get; set; }
    public List<ClosedApp> Closed { get; set; } = new();
    public List<TweakSnap> Tweaks { get; set; } = new();
    public string? PlanBefore { get; set; }
    public string? PlanName { get; set; }
    public Dictionary<string, string?>? UpdatesBefore { get; set; }
    public Dictionary<string, string>? UpdatesWritten { get; set; }
    public DateTime? UpdatesUntilUtc { get; set; }
    public long RamFreedBytes { get; set; }
    public bool Reopen { get; set; } = true;
    public List<GameModeLogItem> Log { get; set; } = new();

    public sealed class ClosedApp
    {
        public string Process { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Path { get; set; }
        public GameModeGroup Group { get; set; }
        public long MemoryBytes { get; set; }
    }

    public sealed class TweakSnap
    {
        public string Id { get; set; } = "";
        public string Snap { get; set; } = "";
        public bool Had { get; set; }
    }
}

public static class GameMode
{
    /// <summary>Tipo das entradas no ChangeJournal (diferente do "gamemode" do Modo de Jogo do Windows).</summary>
    public const string JournalKind = "modo-jogo";
    /// <summary>Atalho global sugerido (registado pela janela principal).</summary>
    public const string HotkeyText = "Ctrl+Alt+G";
    /// <summary>A pausa do Windows Update acaba sozinha ao fim deste tempo, mesmo que o HyperCore feche.</summary>
    public const int UpdatePauseHours = 8;

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim Op = new(1, 1);
    private static GameModeSession? _session;
    private static GameModeConfig? _config;
    private static bool _busy, _initDone;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string SessionFile => Path.Combine(AppPaths.Root, "modo-jogo.json");
    private static string ConfigFile => Path.Combine(AppPaths.Root, "modo-jogo-config.json");

    /// <summary>Ligou, desligou ou começou/acabou uma operação. Pode vir de qualquer thread.</summary>
    public static event Action? StateChanged;
    /// <summary>Passo a passo de uma operação ("A fechar Epic Games…"). Pode vir de qualquer thread.</summary>
    public static event Action<string>? Progress;
    /// <summary>O atalho ou a bandeja pediram para ligar, mas o utilizador ainda não confirmou a primeira vez.</summary>
    public static event Action? ConfirmRequested;

    public static bool IsActive { get { lock (Gate) return _session != null; } }
    public static bool IsBusy { get { lock (Gate) return _busy; } }
    /// <summary>Há um pedido de confirmação à espera (a página mostra-o quando abrir).</summary>
    public static bool ConfirmPending { get; set; }

    /// <summary>Cópia da sessão ativa (null = desligado).</summary>
    public static GameModeSession? Session
    {
        get { lock (Gate) return _session == null ? null : Clone(_session); }
    }

    public static GameModeConfig Config
    {
        get { lock (Gate) return _config ??= LoadConfig(); }
    }

    static GameMode()
    {
        ChangeJournal.RegisterHandler(JournalKind, UndoFromJournal);
    }

    // ------------------------------------------------------------------ catálogo

    /// <summary>Programas que o Modo Jogo conhece. Nunca inclui anti-cheats nem clientes que um jogo precisa para arrancar (Riot).</summary>
    public static readonly IReadOnlyList<GameModeApp> Known = new GameModeApp[]
    {
        new("EpicGamesLauncher", "Epic Games", GameModeGroup.Launcher, true),
        new("EADesktop", "EA app", GameModeGroup.Launcher, true),
        new("Origin", "Origin", GameModeGroup.Launcher, true),
        new("upc", "Ubisoft Connect", GameModeGroup.Launcher, true),
        new("UbisoftConnect", "Ubisoft Connect", GameModeGroup.Launcher, true),
        new("Battle.net", "Battle.net", GameModeGroup.Launcher, true),
        new("GalaxyClient", "GOG Galaxy", GameModeGroup.Launcher, true),
        new("steam", "Steam", GameModeGroup.Launcher, true),
        new("XboxPcApp", "Xbox", GameModeGroup.Launcher, true),

        new("OneDrive", "OneDrive", GameModeGroup.Cloud, true),
        new("Dropbox", "Dropbox", GameModeGroup.Cloud, true),
        new("GoogleDriveFS", "Google Drive", GameModeGroup.Cloud, true),
        new("iCloudDrive", "iCloud Drive", GameModeGroup.Cloud, true),

        new("ms-teams", "Microsoft Teams", GameModeGroup.Heavy, false),
        new("Teams", "Microsoft Teams", GameModeGroup.Heavy, false),
        new("Creative Cloud", "Adobe Creative Cloud", GameModeGroup.Heavy, true),
        new("CCXProcess", "Adobe Creative Cloud", GameModeGroup.Heavy, true),
        new("AdobeIPCBroker", "Adobe (fundo)", GameModeGroup.Heavy, true),
        new("PhoneExperienceHost", "Ligação ao Telemóvel", GameModeGroup.Heavy, true),
        new("Skype", "Skype", GameModeGroup.Heavy, false),

        new("chrome", "Chrome", GameModeGroup.Browser, false),
        new("msedge", "Edge", GameModeGroup.Browser, false),
        new("firefox", "Firefox", GameModeGroup.Browser, false),
        new("opera", "Opera", GameModeGroup.Browser, false),
        new("brave", "Brave", GameModeGroup.Browser, false),
        new("vivaldi", "Vivaldi", GameModeGroup.Browser, false),
    };

    /// <summary>Nunca se fecham nem se sugerem (sistema, segurança, drivers, áudio, a própria app).</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost",
        "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "StartMenuExperienceHost",
        "ShellExperienceHost", "SearchHost", "SearchIndexer", "SearchApp", "TextInputHost", "SecurityHealthService", "SecurityHealthSystray",
        "MsMpEng", "NisSrv", "SgrmBroker", "audiodg", "conhost", "dllhost", "spoolsv", "WmiPrvSE", "LockApp", "ApplicationFrameHost",
        "SystemSettings", "smartscreen", "MemCompression", "Secure System", "WUDFHost", "dasHost", "nvcontainer", "NVDisplay.Container",
        "atiesrxx", "atieclxx", "amdfendrsr", "RadeonSoftware", "igfxEM", "PawnIO", "HyperCore", "FrameForge", "cmd", "powershell", "pwsh",
        "WindowsTerminal", "OpenConsole", "Taskmgr", "ShellHost", "CrossDeviceResume", "Widgets", "WidgetService", "GameBar", "GameBarFTServer",
        "XboxGameBarWidgets", "gamingservices", "gamingservicesnet", "Discord", "obs64", "obs32", "Medal", "NVIDIA Overlay", "nvsphelper64",
        "iCUE", "LGHUB", "lghub_agent", "RazerAppEngine", "SteelSeriesGG", "ArmouryCrate", "MSIAfterburner", "RTSS",
    };

    /// <summary>Anti-cheats e clientes de que os jogos dependem: nunca se tocam.</summary>
    private static readonly System.Text.RegularExpressions.Regex AntiCheat =
        new("^(vgc|vgtray|VALORANT|League of Legends|LeagueClient|RiotClient|EasyAntiCheat|EAAntiCheat|BEService|BattlEye|FACEIT|faceit|ACE-|AntiCheat|PnkBstr|GameGuard|xigncode|nProtect|EOSOverlay|EpicWebHelper)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static GameModeApp? Lookup(string process) =>
        Known.FirstOrDefault(k => string.Equals(k.Process, process, StringComparison.OrdinalIgnoreCase));

    public static bool CanClose(string process) =>
        !string.IsNullOrWhiteSpace(process) && !Protected.Contains(process) && !AntiCheat.IsMatch(process);

    private static bool GroupOn(GameModeConfig c, GameModeGroup g) => g switch
    {
        GameModeGroup.Launcher => c.CloseLaunchers,
        GameModeGroup.Cloud => c.PauseCloud,
        GameModeGroup.Heavy => c.CloseHeavy,
        GameModeGroup.Browser => c.CloseBrowsers,
        _ => true,
    };

    /// <summary>Este programa vai ser fechado com a configuração atual?</summary>
    public static bool WillClose(GameModeConfig c, string process)
    {
        if (!CanClose(process)) return false;
        if (c.Keep.Contains(process, StringComparer.OrdinalIgnoreCase)) return false;
        if (c.Extra.Contains(process, StringComparer.OrdinalIgnoreCase)) return true;
        return Lookup(process) is { } k && GroupOn(c, k.Group);
    }

    // ------------------------------------------------------------------ leitura (sem mudar nada)

    /// <summary>
    /// Programas abertos agora, agrupados por nome. Só usa a lista de processos do Windows (nome, sessão, memória, janelas):
    /// não abre nenhum processo e salta os que estão a desenhar frames (jogos).
    /// </summary>
    public static List<RunningApp> ScanRunning()
    {
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        var games = Presenting();
        var windows = WindowPids();
        var groups = new Dictionary<string, (long Mem, int Count, bool Window, bool Game)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != session || p.Id == Environment.ProcessId) continue;
                string name = p.ProcessName;
                if (!CanClose(name)) continue;
                long mem = 0;
                try { mem = p.WorkingSet64; } catch { }
                bool window = windows.Contains(p.Id);
                groups.TryGetValue(name, out var g);
                groups[name] = (g.Mem + mem, g.Count + 1, g.Window || window, g.Game || games.Contains(p.Id));
            }
            catch { }
            finally { p.Dispose(); }
        }
        var list = new List<RunningApp>();
        foreach (var (name, g) in groups)
        {
            if (g.Game) continue; // um jogo (ou algo do jogo) aberto: nunca aparece nem se fecha
            var k = Lookup(name);
            if (k == null && !g.Window) continue; // auxiliares sem janela ficam de fora
            list.Add(new RunningApp(name, k?.Name ?? Pretty(name), k?.Group, g.Mem, g.Count, g.Window));
        }
        return list.OrderByDescending(a => a.MemoryBytes).ToList();
    }

    /// <summary>
    /// Há um jogo aberto agora (um processo a desenhar frames que não é do sistema, um navegador nem um programa da lista)?
    /// Nesse caso os launchers ficam abertos: jogos da EA, Ubisoft ou Battle.net saem se o launcher fechar a meio.
    /// </summary>
    public static bool GameRunningNow()
    {
        try
        {
            foreach (var (pid, _) in AppServices.Frames.ActivePresenters())
            {
                if (pid == Environment.ProcessId) continue;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    string name = p.ProcessName;
                    if (Protected.Contains(name) || Lookup(name) != null) continue;
                    return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>O que vai acontecer ao ligar agora (frases curtas para a confirmação e o resumo).</summary>
    public static List<string> Preview(GameModeConfig c, IReadOnlyList<RunningApp> running)
    {
        var lines = new List<string>();
        bool game = GameRunningNow();
        var close = running.Where(a => WillClose(c, a.Process) && !(game && a.Group == GameModeGroup.Launcher)).Select(a => a.Name).Distinct().ToList();
        if (close.Count > 0) lines.Add("Fechar " + JoinNames(close) + ".");
        else lines.Add("Fechar os programas da lista que estiverem abertos (agora não há nenhum).");
        if (game && c.CloseLaunchers) lines.Add("Há um jogo aberto: os launchers ficam abertos (o jogo pode precisar deles).");
        if (c.PauseUpdates) lines.Add($"Pausar o Windows Update (no máximo {UpdatePauseHours} h).");
        if (c.FreeRam) lines.Add("Libertar a memória em cache (lista de espera da RAM).");
        if (c.Level > 0) lines.Add($"Aplicar o nível {TweakCatalog.LevelNames[Math.Clamp(c.Level, 0, 4)]} enquanto jogas.");
        if (c.Plan > 0) lines.Add("Pôr o plano de energia " + PlanLabel(c.Plan) + ".");
        lines.Add(c.Reopen ? "Ao desligar: repor tudo e voltar a abrir os programas." : "Ao desligar: repor tudo (os programas ficam fechados).");
        return lines;
    }

    public static string PlanLabel(int plan) => plan == 2 ? "Desempenho máximo" : plan == 1 ? "Alto desempenho" : "atual";

    /// <summary>Memória em cache (lista de espera) em bytes, ou null se o Windows não deixar ler.</summary>
    public static long? StandbyBytes()
    {
        try
        {
            EnablePrivilege("SeProfileSingleProcessPrivilege");
            var info = new SYSTEM_MEMORY_LIST_INFORMATION();
            int status = NtQuerySystemInformation(80, ref info, Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>(), out _);
            if (status != 0) return null;
            ulong pages = 0;
            unsafe { for (int i = 0; i < 8; i++) pages += (ulong)info.PageCountByPriority[i]; }
            return (long)(pages * (ulong)Environment.SystemPageSize);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ ligar / desligar

    /// <summary>Liga o Modo Jogo (em segundo plano). Não faz nada se já estiver ligado ou ocupado.</summary>
    public static Task Start() => Task.Run(StartCore);

    /// <summary>Desliga e repõe tudo (em segundo plano).</summary>
    public static Task Stop() => Task.Run(() => { StopCore(); });

    /// <summary>
    /// O HyperCore vai fechar (ou o Windows está a terminar a sessão): repõe já, no thread que chama, sem avisos.
    /// Chamar no UI thread (os avisos e eventos vão para a fila dele, por isso não há bloqueio). Espera no máximo 10 s
    /// por um passo a meio; se não der, o arranque seguinte repõe (como depois de um crash).
    /// </summary>
    /// <param name="reopenApps">false quando o Windows está a terminar a sessão: não faz sentido voltar a abrir programas.</param>
    public static void StopForExit(bool reopenApps)
    {
        lock (Gate)
        {
            if (_session == null) return;
            if (!reopenApps) _session.Reopen = false;
        }
        StopCore(fromStartup: true, waitMs: 10000);
    }

    /// <summary>
    /// Para o atalho global e a bandeja: liga ou desliga. Antes da primeira confirmação não liga às cegas:
    /// abre a página do Modo Jogo e pede confirmação lá.
    /// </summary>
    public static void Toggle()
    {
        if (IsBusy) { AppServices.Toast("O Modo Jogo ainda está a terminar o passo anterior. Um momento…"); return; }
        if (IsActive) { _ = Stop(); return; }
        if (!Config.Confirmed)
        {
            ConfirmPending = true;
            AppServices.Navigate("FF:gamemode");
            ConfirmRequested?.Invoke();
            return;
        }
        _ = Start();
    }

    /// <summary>
    /// Chamar uma vez no arranque da app (depois de AppServices.Start): regista o desfazer na Central e, se a app fechou
    /// com o Modo Jogo ligado, repõe tudo em segundo plano.
    /// </summary>
    public static void Init()
    {
        lock (Gate)
        {
            if (_initDone) return;
            _initDone = true;
        }
        _ = Task.Run(() =>
        {
            GameModeSession? left = null;
            try { if (File.Exists(SessionFile)) left = JsonSerializer.Deserialize<GameModeSession>(File.ReadAllText(SessionFile)); }
            catch { }
            if (left == null) return;
            lock (Gate) _session ??= left;
            if (StopCore(fromStartup: true))
                AppServices.Toast("O Modo Jogo tinha ficado ligado quando o HyperCore fechou. Repus tudo como estava.");
        });
    }

    private static void StartCore()
    {
        if (!Op.Wait(0)) return;
        try
        {
            var c = Config;
            lock (Gate)
            {
                if (_session != null) return;
                _busy = true;
            }
            RaiseState();

            var s = new GameModeSession
            {
                StartedUtc = DateTime.UtcNow, BootUtc = BootTimeUtc(), Level = c.Level, Reopen = c.Reopen,
            };
            lock (Gate) _session = s;
            SaveSession(s);

            // 1) Programas de fundo
            CloseApps(c, s);
            SaveSession(s);

            // 2) Windows Update
            if (c.PauseUpdates) PauseUpdates(s);
            SaveSession(s);

            // 3) Nível de desempenho (só ajustes instantâneos, com fotografia do valor bruto)
            if (c.Level > 0) ApplyLevel(c.Level, s);
            SaveSession(s);

            // 4) Plano de energia
            if (c.Plan > 0) ApplyPlan(c.Plan, s);
            SaveSession(s);

            // 5) Memória em cache (uma vez; não fica nada para repor)
            if (c.FreeRam) FreeRam(s);

            // Central Desfazer: enquanto está ligado, a entrada sabe repor tudo (Data = fotografia)
            string detail = Summary(s);
            try { s.JournalId = ChangeJournal.Record(JournalKind, "Modo Jogo ligado", detail, JsonSerializer.Serialize(s)); } catch { }
            SaveSession(s);
            Log(s, GameModeLogKind.Ok, "Modo Jogo ligado. Bom jogo!");

            lock (Gate) { c.LastStartUtc = s.StartedUtc; c.LastEndUtc = null; c.LastLog = s.Log.ToList(); }
            SaveConfig();
            AppServices.Toast("Modo Jogo ligado: " + detail + " Desliga no mesmo botão ou com " + HotkeyText + ".");
        }
        catch (Exception ex)
        {
            AppServices.Toast("O Modo Jogo não conseguiu ligar tudo: " + ex.Message);
        }
        finally
        {
            lock (Gate) _busy = false;
            Op.Release();
            RaiseState();
        }
    }

    /// <summary>Repõe tudo pela fotografia da sessão ativa. Devolve true se havia sessão e foi reposta.</summary>
    private static bool StopCore(bool fromStartup = false, int? waitMs = null)
    {
        if (!Op.Wait(waitMs ?? (fromStartup ? 30000 : 0))) return false;
        GameModeSession? s;
        try
        {
            lock (Gate)
            {
                s = _session;
                if (s == null) return false;
                _busy = true;
            }
            RaiseState();

            Restore(s, fromStartup);

            if (!string.IsNullOrEmpty(s.JournalId)) ChangeJournal.Remove(s.JournalId);
            else ChangeJournal.RemoveKind(JournalKind);
            try { File.Delete(SessionFile); } catch { }

            var mins = (int)Math.Max(1, (DateTime.UtcNow - s.StartedUtc).TotalMinutes);
            Log(s, GameModeLogKind.Ok, $"Modo Jogo desligado ao fim de {Duration(mins)}. Ficou tudo como estava.");
            var c = Config;
            lock (Gate)
            {
                _session = null;
                c.LastStartUtc = s.StartedUtc; c.LastEndUtc = DateTime.UtcNow; c.LastLog = s.Log.ToList();
            }
            SaveConfig();
            if (!fromStartup) AppServices.Toast("Modo Jogo desligado. Repus tudo como estava.");
            return true;
        }
        catch (Exception ex)
        {
            AppServices.Toast("Não consegui repor tudo do Modo Jogo: " + ex.Message);
            return false;
        }
        finally
        {
            lock (Gate) _busy = false;
            Op.Release();
            RaiseState();
        }
    }

    /// <summary>Central Desfazer: a sessão ativa desliga-se; uma entrada antiga (sessão perdida) repõe pela fotografia guardada.</summary>
    private static (bool ok, string msg) UndoFromJournal(ChangeEntry e)
    {
        GameModeSession? active;
        lock (Gate) active = _session;
        if (IsBusy) return (false, "O Modo Jogo está a meio de um passo. Tenta daqui a uns segundos.");
        if (active != null && (active.JournalId == e.Id || string.IsNullOrEmpty(active.JournalId)))
            return StopCore() ? (true, "Modo Jogo desligado. Ficou tudo como estava.") : (false, "Não consegui desligar o Modo Jogo.");
        GameModeSession? old = null;
        try { old = JsonSerializer.Deserialize<GameModeSession>(e.Data); } catch { }
        if (old == null) return (true, "Não havia nada para repor.");
        if (!Op.Wait(10000)) return (false, "O Modo Jogo está ocupado. Tenta daqui a uns segundos.");
        try { Restore(old, fromStartup: true); }
        finally { Op.Release(); }
        return (true, "Repus o que o Modo Jogo tinha mudado.");
    }

    // ------------------------------------------------------------------ passos

    private static void CloseApps(GameModeConfig c, GameModeSession s)
    {
        var running = ScanRunning().Where(a => WillClose(c, a.Process)).ToList();
        if (GameRunningNow() && running.RemoveAll(a => a.Group == GameModeGroup.Launcher) > 0)
            Log(s, GameModeLogKind.Info, "Há um jogo aberto: deixei os launchers abertos (o jogo pode precisar deles).");
        if (running.Count == 0) { Log(s, GameModeLogKind.Info, "Não havia programas da lista abertos."); return; }
        var games = Presenting();
        var pending = new List<(RunningApp App, bool Force, string? Path)>();
        foreach (var app in running)
        {
            Say($"A fechar {app.Name}…");
            bool force = Lookup(app.Process)?.Force ?? false;
            string? path = null;
            foreach (var p in Process.GetProcessesByName(app.Process))
            {
                try
                {
                    if (p.SessionId != MySession || games.Contains(p.Id)) continue; // só os desta sessão do Windows
                    path ??= ImagePath(p.Id);
                    if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
                }
                catch { }
                finally { p.Dispose(); }
            }
            pending.Add((app, force, path));
        }

        // Dá até 6 s para fecharem sozinhos
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 6 && pending.Any(x => Alive(x.App.Process))) Thread.Sleep(250);

        foreach (var (app, force, path) in pending)
        {
            if (Alive(app.Process) && force)
            {
                foreach (var p in Process.GetProcessesByName(app.Process))
                {
                    try { if (p.SessionId == MySession && !games.Contains(p.Id)) p.Kill(); } catch { }
                    finally { p.Dispose(); }
                }
                var w = Stopwatch.StartNew();
                while (w.Elapsed.TotalSeconds < 3 && Alive(app.Process)) Thread.Sleep(150);
            }
            if (Alive(app.Process))
            {
                Log(s, GameModeLogKind.Warn, app.Group == GameModeGroup.Browser || app.HasWindow
                    ? $"{app.Name} não fechou (pode ter trabalho por guardar). Fecha-o tu se quiseres."
                    : $"{app.Name} não fechou.");
                continue;
            }
            // Um programa com o mesmo nome repetido no catálogo (ex.: duas entradas da Adobe) só se guarda uma vez
            lock (Gate)
                if (!s.Closed.Any(x => x.Process.Equals(app.Process, StringComparison.OrdinalIgnoreCase)))
                    s.Closed.Add(new GameModeSession.ClosedApp
                {
                    Process = app.Process, Name = app.Name, Path = path, Group = app.Group ?? GameModeGroup.Custom, MemoryBytes = app.MemoryBytes
                });
            string verb = app.Group == GameModeGroup.Cloud ? "Pausei" : "Fechei";
            Log(s, GameModeLogKind.Ok, $"{verb} {app.Name} ({Mb(app.MemoryBytes)}).");
        }
    }

    private const string WuKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private static readonly string[] WuValues =
    {
        "PauseUpdatesStartTime", "PauseUpdatesExpiryTime", "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime",
        "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime",
    };

    private static void PauseUpdates(GameModeSession s)
    {
        Say("A pausar o Windows Update…");
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(WuKey, writable: true);
            // Já em pausa (pelo utilizador): não se mexe
            if (key.GetValue("PauseUpdatesExpiryTime") is string exp && DateTime.TryParse(exp, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var until) && until > DateTime.UtcNow)
            {
                Log(s, GameModeLogKind.Info, "O Windows Update já estava em pausa: não mexi.");
                return;
            }
            var before = WuValues.ToDictionary(n => n, n => key.GetValue(n) as string);
            s.UpdatesBefore = before;
            SaveSession(s); // antes de escrever: se for abaixo a meio, o arranque seguinte sabe o que repor
            var now = DateTime.UtcNow;
            var end = now.AddHours(UpdatePauseHours);
            string a = now.ToString("yyyy-MM-ddTHH:mm:ssZ"), b = end.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var written = new Dictionary<string, string>
            {
                ["PauseUpdatesStartTime"] = a, ["PauseUpdatesExpiryTime"] = b,
                ["PauseFeatureUpdatesStartTime"] = a, ["PauseFeatureUpdatesEndTime"] = b,
                ["PauseQualityUpdatesStartTime"] = a, ["PauseQualityUpdatesEndTime"] = b,
            };
            foreach (var (n, v) in written) key.SetValue(n, v, RegistryValueKind.String);
            s.UpdatesWritten = written;
            s.UpdatesUntilUtc = end;
            Log(s, GameModeLogKind.Ok, $"Pausei o Windows Update até às {end.ToLocalTime():HH:mm} (acaba sozinho).");
        }
        catch (Exception ex)
        {
            s.UpdatesBefore = null;
            Log(s, GameModeLogKind.Warn, "Não consegui pausar o Windows Update: " + ex.Message);
        }
    }

    private static void ApplyLevel(int level, GameModeSession s)
    {
        Say($"A aplicar o nível {TweakCatalog.LevelNames[Math.Clamp(level, 0, 4)]}…");
        try
        {
            var probe = AppServices.Probe.GetAwaiter().GetResult();
            var target = TweakCatalog.Target(level, probe);
            int n = 0;
            lock (TweakCatalog.Gate)
                foreach (var t in AppServices.Tweaks.Where(t => t.Level <= level && t.Instant).OrderBy(t => t.Order))
                {
                    t.Refresh(probe);
                    if (!t.Enabled || t.Capture == null || t.Restore == null) continue;
                    if (t.IsPlan ? t.IsOn : t.Satisfied(target)) continue;
                    string? snap;
                    try { snap = t.Capture(); } catch { snap = null; }
                    if (snap == null) continue; // sem fotografia não há como repor: não se mexe
                    bool had = t.Touched;
                    if (t.Set(true, target, quiet: true) == null)
                    {
                        lock (Gate) s.Tweaks.Add(new GameModeSession.TweakSnap { Id = t.Id, Snap = snap, Had = had });
                        n++;
                        SaveSession(s);
                    }
                    else
                    {
                        try { t.Restore(snap); if (!had) t.ForgetBackup(); } catch { }
                    }
                }
            string name = TweakCatalog.LevelNames[Math.Clamp(level, 0, 4)];
            Log(s, n > 0 ? GameModeLogKind.Ok : GameModeLogKind.Info,
                n > 0 ? $"Apliquei o nível {name} ({n} {(n == 1 ? "ajuste" : "ajustes")}, só enquanto jogas)." : $"O nível {name} já estava aplicado.");
        }
        catch (Exception ex) { Log(s, GameModeLogKind.Warn, "Não consegui aplicar o nível: " + ex.Message); }
    }

    private static void ApplyPlan(int plan, GameModeSession s)
    {
        Say("A pôr o plano de energia…");
        try
        {
            lock (TweakCatalog.Gate)
            {
                var active = TweakCatalog.ActivePlan();
                string want = plan == 2 ? TweakCatalog.Ultimate : TweakCatalog.HighPerf;
                bool ok = active != null && (plan == 2 ? active.Guid == TweakCatalog.Ultimate : TweakCatalog.IsPerformancePlan(active.Guid, active.Name));
                if (ok) { s.PlanName = active!.Name; Log(s, GameModeLogKind.Info, $"O plano de energia já era {active.Name}."); return; }
                TweakCatalog.EnsureScheme(want);
                s.PlanBefore = active?.Guid;
                SaveSession(s);
                TweakCatalog.PowerCfg($"/setactive {want}");
                var now = TweakCatalog.ActivePlan();
                s.PlanName = now?.Name ?? PlanLabel(plan);
                Log(s, GameModeLogKind.Ok, $"Pus o plano de energia {s.PlanName}.");
            }
        }
        catch (Exception ex) { Log(s, GameModeLogKind.Warn, "Não consegui mudar o plano de energia: " + ex.Message); }
    }

    private static void FreeRam(GameModeSession s)
    {
        Say("A libertar memória…");
        try
        {
            long? before = StandbyBytes();
            EnablePrivilege("SeProfileSingleProcessPrivilege");
            int command = 4; // MemoryPurgeStandbyList
            int status = NtSetSystemInformation(80 /* SystemMemoryListInformation */, ref command, sizeof(int));
            if (status != 0) { Log(s, GameModeLogKind.Warn, $"O Windows não deixou libertar a memória (0x{status:X8})."); return; }
            Thread.Sleep(200);
            long? after = StandbyBytes();
            s.RamFreedBytes = before is long b && after is long a ? Math.Max(0, b - a) : 0;
            Log(s, GameModeLogKind.Ok, s.RamFreedBytes > 0 ? $"Libertei {Gb(s.RamFreedBytes)} de memória em cache." : "Libertei a memória em cache.");
        }
        catch (Exception ex) { Log(s, GameModeLogKind.Warn, "Não consegui libertar a memória: " + ex.Message); }
    }

    /// <summary>Repõe pela ordem inversa: plano, ajustes, Windows Update, programas.</summary>
    private static void Restore(GameModeSession s, bool fromStartup)
    {
        // Plano de energia
        if (s.PlanBefore is string plan)
        {
            Say("A repor o plano de energia…");
            try
            {
                lock (TweakCatalog.Gate)
                    if (TweakCatalog.SchemeExists(plan)) TweakCatalog.PowerCfg($"/setactive {plan}");
                Log(s, GameModeLogKind.Ok, "Repus o plano de energia.");
            }
            catch (Exception ex) { Log(s, GameModeLogKind.Warn, "Não consegui repor o plano de energia: " + ex.Message); }
        }

        // Ajustes do nível (ao contrário: as definições de energia antes de trocar o plano de volta)
        if (s.Tweaks.Count > 0)
        {
            Say("A repor os ajustes…");
            int n = 0;
            try
            {
                lock (TweakCatalog.Gate)
                    for (int i = s.Tweaks.Count - 1; i >= 0; i--)
                    {
                        var snap = s.Tweaks[i];
                        var t = AppServices.Tweaks.FirstOrDefault(x => x.Id == snap.Id);
                        if (t?.Restore == null) continue;
                        // Escolhido como permanente entretanto (nível aplicado ou interruptor ligado): fica, com o original guardado
                        if (LevelKeeper.Desired(t, AppServices.Settings)) { t.ReadState(); continue; }
                        try { t.Restore(snap.Snap); if (!snap.Had) t.ForgetBackup(); n++; } catch { }
                        t.ReadState();
                    }
                Log(s, GameModeLogKind.Ok, $"Repus {n} {(n == 1 ? "ajuste" : "ajustes")} do nível.");
            }
            catch (Exception ex) { Log(s, GameModeLogKind.Warn, "Não consegui repor os ajustes: " + ex.Message); }
        }

        // Windows Update: só os valores que continuam como os deixámos (se o utilizador mexeu entretanto, fica o dele)
        if (s.UpdatesBefore != null && s.UpdatesWritten != null)
        {
            Say("A retomar o Windows Update…");
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.CreateSubKey(WuKey, writable: true);
                foreach (var (n, written) in s.UpdatesWritten)
                {
                    if (key.GetValue(n) as string != written) continue;
                    if (s.UpdatesBefore.GetValueOrDefault(n) is string old) key.SetValue(n, old, RegistryValueKind.String);
                    else key.DeleteValue(n, throwOnMissingValue: false);
                }
                Log(s, GameModeLogKind.Ok, "O Windows Update voltou ao normal.");
            }
            catch (Exception ex) { Log(s, GameModeLogKind.Warn, "Não consegui retomar o Windows Update (a pausa acaba sozinha): " + ex.Message); }
        }

        // Programas: só na mesma sessão do Windows (depois de reiniciar, os que arrancam com o Windows já voltaram sozinhos)
        bool sameBoot = Math.Abs((BootTimeUtc() - s.BootUtc).TotalMinutes) < 2;
        if (s.Reopen && sameBoot && s.Closed.Count > 0)
        {
            var reopened = new List<string>();
            var failed = new List<string>();
            foreach (var app in s.Closed.GroupBy(a => a.Path ?? a.Process, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                if (Alive(app.Process)) continue; // o utilizador já o voltou a abrir
                if (app.Path == null || !File.Exists(app.Path)) { failed.Add(app.Name); continue; }
                Say($"A voltar a abrir {app.Name}…");
                try
                {
                    // Pelo Explorer: o programa abre como utilizador normal (não herda o administrador do HyperCore)
                    using var p = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { app.Path }, UseShellExecute = false });
                    reopened.Add(app.Name);
                }
                catch { failed.Add(app.Name); }
            }
            if (reopened.Count > 0) Log(s, GameModeLogKind.Ok, "Voltei a abrir " + JoinNames(reopened.Distinct().ToList()) + ".");
            if (failed.Count > 0) Log(s, GameModeLogKind.Warn, "Não consegui voltar a abrir " + JoinNames(failed.Distinct().ToList()) + ". Abre-os tu.");
        }
        else if (s.Closed.Count > 0 && s.Reopen && !sameBoot)
            Log(s, GameModeLogKind.Info, "O PC reiniciou: não voltei a abrir os programas (os do arranque já abriram sozinhos).");
    }

    // ------------------------------------------------------------------ texto

    public static string Summary(GameModeSession s)
    {
        var parts = new List<string>();
        if (s.Closed.Count > 0) parts.Add($"{s.Closed.Count} {(s.Closed.Count == 1 ? "programa fechado" : "programas fechados")}");
        if (s.UpdatesWritten != null) parts.Add("Windows Update em pausa");
        if (s.Tweaks.Count > 0) parts.Add($"nível {TweakCatalog.LevelNames[Math.Clamp(s.Level, 0, 4)]}");
        if (s.PlanBefore != null) parts.Add("plano " + (s.PlanName ?? "de desempenho"));
        if (s.RamFreedBytes > 0) parts.Add($"{Gb(s.RamFreedBytes)} de RAM libertados");
        return parts.Count == 0 ? "nada precisava de mudar." : string.Join(", ", parts) + ".";
    }

    public static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";

    public static string JoinNames(IReadOnlyList<string> names) =>
        names.Count switch
        {
            0 => "",
            1 => names[0],
            <= 4 => string.Join(", ", names.Take(names.Count - 1)) + " e " + names[^1],
            _ => string.Join(", ", names.Take(3)) + $" e mais {names.Count - 3}",
        };

    public static string Mb(long bytes) => bytes >= 1L << 30 ? Gb(bytes) : $"{bytes / 1048576.0:N0} MB";
    public static string Gb(long bytes) => $"{bytes / 1073741824.0:0.0} GB";

    private static string Pretty(string process)
    {
        if (process.Length == 0) return process;
        return char.ToUpperInvariant(process[0]) + process[1..];
    }

    // ------------------------------------------------------------------ configuração

    public static void SaveConfig()
    {
        string json;
        lock (Gate) json = JsonSerializer.Serialize(_config ??= LoadConfig(), Json);
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            string tmp = ConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigFile, overwrite: true);
        }
        catch { }
    }

    /// <summary>Muda a configuração (na thread que chamar) e grava.</summary>
    public static void Update(Action<GameModeConfig> change)
    {
        lock (Gate) change(_config ??= LoadConfig());
        SaveConfig();
        RaiseState();
    }

    private static GameModeConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigFile))
                return JsonSerializer.Deserialize<GameModeConfig>(File.ReadAllText(ConfigFile)) ?? new();
        }
        catch { }
        return new GameModeConfig();
    }

    private static void SaveSession(GameModeSession s)
    {
        try
        {
            string json;
            lock (Gate) json = JsonSerializer.Serialize(s, Json);
            Directory.CreateDirectory(AppPaths.Root);
            string tmp = SessionFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SessionFile, overwrite: true);
        }
        catch { }
    }

    private static GameModeSession Clone(GameModeSession s) =>
        JsonSerializer.Deserialize<GameModeSession>(JsonSerializer.Serialize(s)) ?? new GameModeSession();

    private static void Log(GameModeSession s, GameModeLogKind kind, string text)
    {
        lock (Gate) s.Log.Add(new GameModeLogItem { When = DateTime.Now, Kind = kind, Text = text });
        Say(text);
    }

    private static void Say(string text)
    {
        try { Progress?.Invoke(text); } catch { }
    }

    private static void RaiseState()
    {
        try { StateChanged?.Invoke(); } catch { }
    }

    // ------------------------------------------------------------------ sistema

    private static HashSet<int> Presenting()
    {
        try { return AppServices.Frames.ActivePresenters().Select(x => x.Pid).ToHashSet(); }
        catch { return new HashSet<int>(); }
    }

    /// <summary>PIDs com uma janela principal visível (uma única passagem por todas as janelas, sem abrir processos).</summary>
    private static HashSet<int> WindowPids()
    {
        var set = new HashSet<int>();
        try
        {
            EnumWindows((h, _) =>
            {
                if (IsWindowVisible(h) && GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero && GetWindowThreadProcessId(h, out uint pid) != 0)
                    set.Add((int)pid);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return set;
    }

    /// <summary>Sessão do Windows deste utilizador: o Modo Jogo nunca mexe em programas de outras contas abertas no PC.</summary>
    private static readonly int MySession = GetMySession();
    private static int GetMySession() { using var self = Process.GetCurrentProcess(); return self.SessionId; }

    /// <summary>Há um processo com este nome aberto NESTA sessão do Windows.</summary>
    private static bool Alive(string process)
    {
        bool alive = false;
        foreach (var p in Process.GetProcessesByName(process))
        {
            try { alive |= p.SessionId == MySession; } catch { }
            finally { p.Dispose(); }
        }
        return alive;
    }

    private static DateTime BootTimeUtc()
    {
        var t = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    private static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private static void EnablePrivilege(string name)
    {
        using var self = Process.GetCurrentProcess();
        if (!OpenProcessToken(self.Handle, 0x0020 | 0x0008, out var token)) throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValue(null, name, out long luid)) throw new Win32Exception();
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = 2 };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SYSTEM_MEMORY_LIST_INFORMATION
    {
        public nuint ZeroPageCount, FreePageCount, ModifiedPageCount, ModifiedNoWritePageCount, BadPageCount;
        public fixed ulong PageCountByPriority[8];
        public fixed ulong RepurposedPagesByPriority[8];
        public nuint ModifiedPageCountPageFile;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public long Luid; public uint Attributes; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, ref SYSTEM_MEMORY_LIST_INFORMATION info, int length, out int returned);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint len, IntPtr prev, IntPtr retLen);
}
