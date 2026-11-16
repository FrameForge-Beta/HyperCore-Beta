using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using FrameForge.Core;
using Microsoft.Win32;

namespace FrameForge.Optimize;

// =====================================================================================================================
// Configuração gamer (Pages/GamerSetupPage.cs): o Windows e o monitor prontos para jogar, item a item.
// Cada correção guarda o valor anterior EXATO e fica no ChangeJournal (Central Desfazer) com um handler de desfazer:
//   "hags", "win-gamemode", "game-dvr", "windowed-opt", "win-vrr", "mpo"  → valores do registo (GamerSetup)
//   "display-hz"                                                          → taxa de atualização de um monitor (GamerSetup)
//   "shader-cache"                                                        → ficheiros da cache movidos para uma cópia (ShaderCache)
// GamerSetup.Init() regista os handlers (chamar uma vez no arranque da app; a página também o chama).
// =====================================================================================================================

/// <summary>Estado de um item da Configuração gamer.</summary>
public enum SetupState
{
    /// <summary>Está como deve estar.</summary>
    Good,
    /// <summary>Recomendado mudar.</summary>
    Improve,
    /// <summary>Opcional: depende do caso (ex.: MPO desligado de propósito).</summary>
    Optional,
    /// <summary>Não foi possível ler.</summary>
    Unknown,
    /// <summary>Não existe neste PC (ex.: placa sem suporte).</summary>
    NotAvailable,
}

/// <summary>Um item do Windows para jogos (HAGS, Modo de Jogo, ...), já com o texto para mostrar.</summary>
public sealed class SetupCheck
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Glyph { get; init; } = "";
    public SetupState State { get; init; }
    /// <summary>Estado em 1–3 palavras ("Ligado").</summary>
    public string Status { get; init; } = "";
    /// <summary>Recomendação curta (uma frase).</summary>
    public string Advice { get; init; } = "";
    /// <summary>Explicação para o tooltip/"?".</summary>
    public string Help { get; init; } = "";
    /// <summary>O que o botão faz: true = ligar, false = desligar.</summary>
    public bool TargetOn { get; init; }
    /// <summary>Texto do botão ("Ligar"), ou null sem botão.</summary>
    public string? FixLabel { get; init; }
    /// <summary>Entra no "Corrigir o recomendado".</summary>
    public bool Recommended { get; init; }
    /// <summary>A mudança só faz efeito depois de reiniciar.</summary>
    public bool NeedsRestart { get; init; }
    /// <summary>Já mudou no registo mas só vale depois de reiniciar.</summary>
    public bool RestartPending { get; init; }
    /// <summary>Entrada do ChangeJournal se fomos nós que mudámos (para o "Desfazer").</summary>
    public string? JournalId { get; init; }
    /// <summary>Página das Definições do Windows com este interruptor (opcional).</summary>
    public string? SettingsUri { get; init; }
}

public static class GamerSetup
{
    public const string KindHz = "display-hz";
    public static readonly string[] RegKinds = { "hags", "win-gamemode", "game-dvr", "windowed-opt", "win-vrr", "mpo" };
    private const string HagsRestartName = "Agendamento de GPU acelerado por hardware (HAGS)";
    private const string MpoRestartName = "Planos de sobreposição (MPO)";

    private static readonly object InitGate = new();
    private static bool _init;

    /// <summary>Regista os handlers de desfazer (idempotente) e limpa as cópias antigas da cache de shaders em segundo plano.</summary>
    public static void Init()
    {
        lock (InitGate)
        {
            if (_init) return;
            _init = true;
        }
        foreach (var k in RegKinds) ChangeJournal.RegisterHandler(k, UndoReg);
        ChangeJournal.RegisterHandler(KindHz, UndoHz);
        ChangeJournal.RegisterHandler(ShaderCache.Kind, ShaderCache.Undo);
        Task.Run(() => { try { RecoverPendingHz(); } catch { } try { ShaderCache.PurgeExpired(); } catch { } });
    }

    // ================================================================== leitura

    private const string GfxPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string GameBarPath = @"Software\Microsoft\GameBar";
    private const string GameCfgPath = @"System\GameConfigStore";
    private const string DvrPath = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string DwmPath = @"SOFTWARE\Microsoft\Windows\Dwm";
    private const string DxPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string DxName = "DirectXUserGlobalSettings";

    private static int Build => Environment.OSVersion.Version.Build;

    /// <summary>Os itens do Windows para jogos (lento: lê o driver; usar fora do UI thread). monitors serve para saber se há VRR.</summary>
    public static List<SetupCheck> ReadChecks(IReadOnlyList<GamerMonitor> monitors)
    {
        var journal = ChangeJournal.All();
        string? J(string kind) => journal.FirstOrDefault(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase))?.Id;
        var list = new List<SetupCheck>();

        // ---- HAGS
        {
            int? reg = Dword(Registry.LocalMachine, GfxPath, "HwSchMode");
            var caps = GpuSched.Read();
            bool regOn = reg == 2 || (reg == null && caps is { Enabled: true });
            const string help = "Agendamento de GPU acelerado por hardware: a própria placa gráfica organiza o trabalho, em vez de ser a CPU. " +
                                "Tira um pouco de carga à CPU e é obrigatório para o Frame Generation (DLSS 3/4, FSR 3 com AFMF). Só vale depois de reiniciar.";
            if (caps is { Supported: false })
                list.Add(new SetupCheck
                {
                    Id = "hags", Title = "Agendamento de GPU (HAGS)", Glyph = "\uE950", State = SetupState.NotAvailable,
                    Status = "Sem suporte", Advice = "A tua placa gráfica ou o driver não suportam. Atualizar o driver pode ajudar.", Help = help,
                });
            else
            {
                bool active = caps?.Enabled ?? regOn;
                bool pending = regOn != active;
                list.Add(new SetupCheck
                {
                    Id = "hags", Title = "Agendamento de GPU (HAGS)", Glyph = "\uE950", Help = help, NeedsRestart = true, RestartPending = pending,
                    State = regOn ? SetupState.Good : SetupState.Improve,
                    Status = regOn ? (pending ? "Liga ao reiniciar" : "Ligado") : (pending ? "Desliga ao reiniciar" : "Desligado"),
                    Advice = regOn ? "Está como deve estar." : "Liga: menos carga na CPU e é preciso para o Frame Generation.",
                    TargetOn = !regOn, FixLabel = regOn ? null : "Ligar", Recommended = !regOn, JournalId = J("hags"),
                    SettingsUri = "ms-settings:display-advancedgraphics",
                });
            }
        }

        // ---- Modo de Jogo do Windows
        {
            bool on = (Dword(Registry.CurrentUser, GameBarPath, "AutoGameModeEnabled") ?? 1) != 0;
            list.Add(new SetupCheck
            {
                Id = "win-gamemode", Title = "Modo de Jogo do Windows", Glyph = "\uE7FC",
                Help = "Quando um jogo está aberto, o Windows dá-lhe prioridade e não instala atualizações nem drivers a meio da partida. Vem ligado por predefinição.",
                State = on ? SetupState.Good : SetupState.Improve, Status = on ? "Ligado" : "Desligado",
                Advice = on ? "Está como deve estar." : "Liga: o jogo passa à frente e o Windows Update espera.",
                TargetOn = true, FixLabel = on ? null : "Ligar", Recommended = !on, JournalId = J("win-gamemode"),
                SettingsUri = "ms-settings:gaming-gamemode",
            });
        }

        // ---- Gravação em segundo plano (Game DVR)
        {
            bool capture = (Dword(Registry.CurrentUser, GameCfgPath, "GameDVR_Enabled") ?? 1) != 0 &&
                           (Dword(Registry.CurrentUser, DvrPath, "AppCaptureEnabled") ?? 1) != 0;
            bool background = capture && (Dword(Registry.CurrentUser, DvrPath, "HistoricalCaptureEnabled") ?? 0) != 0;
            list.Add(new SetupCheck
            {
                Id = "game-dvr", Title = "Gravação em segundo plano", Glyph = "\uE714",
                Help = "A Game Bar pode gravar sempre os últimos minutos de jogo (\"Gravar o que aconteceu\"). Isso usa a gráfica e o disco o tempo todo. " +
                       "Desligar isto não tira a captura manual (Win+Alt+R) nem o ShadowPlay/NVIDIA App.",
                State = background ? SetupState.Improve : SetupState.Good,
                Status = background ? "A gravar sempre" : capture ? "Só quando pedes" : "Desligada",
                Advice = background ? "Desliga se não usas o \"Gravar o que aconteceu\": poupa gráfica e disco." : "Está como deve estar.",
                TargetOn = false, FixLabel = background ? "Desligar" : null, Recommended = background, JournalId = J("game-dvr"),
                SettingsUri = "ms-settings:gaming-gamedvr",
            });
        }

        // ---- Otimizações para jogos em janela
        {
            const string help = "Faz os jogos DirectX 10/11 em janela ou janela sem bordas usarem o modo de apresentação moderno: menos atraso e G-SYNC/FreeSync também em janela. " +
                                "Não mexe em DX12, Vulkan nem ecrã inteiro exclusivo.";
            if (Build is > 0 and < 22000)
                list.Add(new SetupCheck
                {
                    Id = "windowed-opt", Title = "Otimizações para jogos em janela", Glyph = "\uE737", State = SetupState.NotAvailable,
                    Status = "Só no Windows 11", Advice = "Esta opção só existe no Windows 11.", Help = help,
                });
            else
            {
                bool on = DxGet("SwapEffectUpgradeEnable") is string v ? v == "1" : Build >= 26100;
                list.Add(new SetupCheck
                {
                    Id = "windowed-opt", Title = "Otimizações para jogos em janela", Glyph = "\uE737", Help = help,
                    State = on ? SetupState.Good : SetupState.Improve, Status = on ? "Ligado" : "Desligado",
                    Advice = on ? "Está como deve estar." : "Liga: menos atraso em janela sem bordas.",
                    TargetOn = true, FixLabel = on ? null : "Ligar", Recommended = !on, JournalId = J("windowed-opt"),
                    SettingsUri = "ms-settings:display-advancedgraphics",
                });
            }
        }

        // ---- Taxa de atualização variável do Windows
        {
            bool on = DxGet("VRROptimizeEnable") == "1";
            bool anyVrr = monitors.Any(m => m.Vrr.Capable == true);
            list.Add(new SetupCheck
            {
                Id = "win-vrr", Title = "Taxa de atualização variável do Windows", Glyph = "\uE895",
                Help = "Deixa jogos DirectX 11 que não suportam VRR de origem usar o G-SYNC/FreeSync do teu monitor. Não muda nada nos jogos que já o suportam. " +
                       "Só faz diferença em monitores com VRR.",
                State = on ? SetupState.Good : anyVrr ? SetupState.Improve : SetupState.Optional,
                Status = on ? "Ligada" : "Desligada",
                Advice = on ? "Está como deve estar." : anyVrr ? "Liga: mais jogos usam o VRR do teu monitor." : "Só serve com um monitor G-SYNC/FreeSync.",
                TargetOn = true, FixLabel = on ? null : "Ligar", Recommended = !on && anyVrr, JournalId = J("win-vrr"),
                SettingsUri = "ms-settings:display-advancedgraphics",
            });
        }

        // ---- MPO
        {
            bool off = Dword(Registry.LocalMachine, DwmPath, "OverlayTestMode") == 5;
            list.Add(new SetupCheck
            {
                Id = "mpo", Title = "Planos de sobreposição (MPO)", Glyph = "\uE81E", NeedsRestart = true,
                Help = "O MPO deixa a gráfica juntar o jogo, o vídeo e o rato sem passar tudo pelo Windows: poupa trabalho e atraso. " +
                       "Com alguns drivers causa cintilação, ecrã preto ou engasgos com o navegador aberto; só nesse caso se deve desligar. Em versões recentes do Windows 11 este interruptor pode já não ter efeito.",
                State = off ? SetupState.Optional : SetupState.Good,
                Status = off ? "Desligado" : "Ligado",
                Advice = off ? "Só deve ficar desligado se tinhas cintilação ou ecrã preto." : "Deixa ligado. Desliga só se vires cintilação ou ecrã preto.",
                TargetOn = off, FixLabel = off ? "Voltar a ligar" : "Desligar", Recommended = false, JournalId = J("mpo"),
            });
        }
        return list;
    }

    /// <summary>Apaga do registo de alterações o que já não está mudado (o utilizador repôs à mão, ou outro sistema mudou).</summary>
    public static void PruneStale(IReadOnlyList<GamerMonitor> monitors)
    {
        foreach (var e in ChangeJournal.All())
        {
            try
            {
                if (RegKinds.Contains(e.Kind, StringComparer.OrdinalIgnoreCase))
                {
                    var d = JsonSerializer.Deserialize<RegData>(e.Data);
                    if (d != null && IsOn(e.Kind) == d.BeforeOn) ChangeJournal.Remove(e.Id);
                }
                else if (string.Equals(e.Kind, KindHz, StringComparison.OrdinalIgnoreCase))
                {
                    var d = JsonSerializer.Deserialize<HzData>(e.Data);
                    var m = d == null ? null : monitors.FirstOrDefault(x => Same(x, d));
                    if (m != null && m.CurrentHz == d!.OldHz) ChangeJournal.Remove(e.Id);
                }
            }
            catch { }
        }
    }

    // ================================================================== correções do registo

    private sealed record Snap(string Hive, string Path, string Name, string? Raw, string? DxKey);
    private sealed record RegData(bool BeforeOn, List<Snap> Snaps);

    /// <summary>Estado atual (no registo) de um item: true = ligado. Para "game-dvr": true = gravação em segundo plano ligada.</summary>
    private static bool IsOn(string id) => id switch
    {
        "hags" => Dword(Registry.LocalMachine, GfxPath, "HwSchMode") is int v ? v == 2 : GpuSched.Read() is { Enabled: true },
        "win-gamemode" => (Dword(Registry.CurrentUser, GameBarPath, "AutoGameModeEnabled") ?? 1) != 0,
        "game-dvr" => (Dword(Registry.CurrentUser, GameCfgPath, "GameDVR_Enabled") ?? 1) != 0 &&
                      (Dword(Registry.CurrentUser, DvrPath, "AppCaptureEnabled") ?? 1) != 0 &&
                      (Dword(Registry.CurrentUser, DvrPath, "HistoricalCaptureEnabled") ?? 0) != 0,
        "windowed-opt" => DxGet("SwapEffectUpgradeEnable") is string s ? s == "1" : Build >= 26100,
        "win-vrr" => DxGet("VRROptimizeEnable") == "1",
        "mpo" => Dword(Registry.LocalMachine, DwmPath, "OverlayTestMode") != 5,
        _ => false,
    };

    /// <summary>Título curto para a Central Desfazer.</summary>
    public static string TitleFor(string id, bool on) => id switch
    {
        "hags" => on ? "Agendamento de GPU (HAGS) ligado" : "Agendamento de GPU (HAGS) desligado",
        "win-gamemode" => on ? "Modo de Jogo do Windows ligado" : "Modo de Jogo do Windows desligado",
        "game-dvr" => on ? "Gravação em segundo plano ligada" : "Gravação em segundo plano desligada",
        "windowed-opt" => on ? "Otimizações para jogos em janela ligadas" : "Otimizações para jogos em janela desligadas",
        "win-vrr" => on ? "Taxa de atualização variável ligada" : "Taxa de atualização variável desligada",
        "mpo" => on ? "MPO (planos de sobreposição) ligado" : "MPO (planos de sobreposição) desligado",
        _ => id,
    };

    /// <summary>
    /// Liga/desliga um item. Se já tínhamos mudado este item e o pedido é voltar ao estado de antes, desfaz pela entrada existente
    /// (repõe os valores exatos). Corre em qualquer thread. Devolve (ok, mensagem para o utilizador).
    /// </summary>
    public static (bool ok, string msg) Set(string id, bool on)
    {
        Init();
        if (!RegKinds.Contains(id)) return (false, "Item desconhecido.");
        try
        {
            var existing = ChangeJournal.All().FirstOrDefault(e => string.Equals(e.Kind, id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                RegData? d = null;
                try { d = JsonSerializer.Deserialize<RegData>(existing.Data); } catch { }
                if (d != null && d.BeforeOn == on) return ChangeJournal.TryUndo(existing.Id);
            }
            bool before = IsOn(id);
            if (before == on) return (true, "Já estava assim.");
            var snaps = Capture(id);
            Write(id, on);
            if (existing == null)
                ChangeJournal.Record(id, TitleFor(id, on), DetailFor(id, before, on), JsonSerializer.Serialize(new RegData(before, snaps)));
            string msg = TitleFor(id, on) + ".";
            if (NeedsRestart(id))
            {
                Ui(() => RestartNotice.Toggled(RestartName(id), on));
                msg += " Faz efeito depois de reiniciar o PC.";
            }
            return (true, msg);
        }
        catch (UnauthorizedAccessException) { return (false, "Sem permissão para mudar isto. A app tem de estar aberta como administrador."); }
        catch (Exception ex) { return (false, "Não consegui mudar: " + ex.Message); }
    }

    private static bool NeedsRestart(string id) => id is "hags" or "mpo";
    private static string RestartName(string id) => id == "hags" ? HagsRestartName : MpoRestartName;

    private static string DetailFor(string id, bool before, bool on)
    {
        static string W(bool b) => b ? "ligado" : "desligado";
        string s = $"Antes: {W(before)} · agora: {W(on)}.";
        return NeedsRestart(id) ? s + " Faz efeito depois de reiniciar." : s;
    }

    private static List<Snap> Capture(string id) => id switch
    {
        "hags" => new() { Raw("HKLM", GfxPath, "HwSchMode") },
        "win-gamemode" => new() { Raw("HKCU", GameBarPath, "AutoGameModeEnabled") },
        "game-dvr" => new() { Raw("HKCU", DvrPath, "HistoricalCaptureEnabled") },
        "windowed-opt" => new() { new Snap("HKCU", DxPath, DxName, DxGet("SwapEffectUpgradeEnable"), "SwapEffectUpgradeEnable") },
        "win-vrr" => new() { new Snap("HKCU", DxPath, DxName, DxGet("VRROptimizeEnable"), "VRROptimizeEnable") },
        "mpo" => new() { Raw("HKLM", DwmPath, "OverlayTestMode") },
        _ => new(),
    };

    private static void Write(string id, bool on)
    {
        switch (id)
        {
            case "hags": SetDword(Registry.LocalMachine, GfxPath, "HwSchMode", on ? 2 : 1); break;
            case "win-gamemode": SetDword(Registry.CurrentUser, GameBarPath, "AutoGameModeEnabled", on ? 1 : 0); break;
            case "game-dvr":
                SetDword(Registry.CurrentUser, DvrPath, "HistoricalCaptureEnabled", on ? 1 : 0);
                break;
            case "windowed-opt": DxSet("SwapEffectUpgradeEnable", on ? "1" : "0"); break;
            case "win-vrr": DxSet("VRROptimizeEnable", on ? "1" : "0"); break;
            case "mpo":
                using (var k = Registry.LocalMachine.CreateSubKey(DwmPath))
                {
                    if (on) { if (k.GetValue("OverlayTestMode") is int v && v == 5) k.DeleteValue("OverlayTestMode", false); }
                    else k.SetValue("OverlayTestMode", 5, RegistryValueKind.DWord);
                }
                break;
        }
    }

    private static (bool ok, string msg) UndoReg(ChangeEntry e)
    {
        var d = JsonSerializer.Deserialize<RegData>(e.Data);
        if (d == null) return (false, "Faltam os dados para desfazer.");
        foreach (var s in d.Snaps) Restore(s);
        if (NeedsRestart(e.Kind))
        {
            Ui(() => RestartNotice.Toggled(RestartName(e.Kind), d.BeforeOn));
            return (true, "Reposto. Faz efeito depois de reiniciar o PC.");
        }
        return (true, "Reposto como estava.");
    }

    private static Snap Raw(string hive, string path, string name)
    {
        using var k = HiveOf(hive).OpenSubKey(path);
        string? raw = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            null => null,
            int d => "d:" + d,
            string s => "s:" + s,
            _ => throw new InvalidOperationException($"Valor {name} com um tipo que não sei repor."),
        };
        return new Snap(hive, path, name, raw, null);
    }

    private static void Restore(Snap s)
    {
        if (s.DxKey != null)
        {
            var dict = DxSettings();
            if (s.Raw == null) dict.Remove(s.DxKey); else dict[s.DxKey] = s.Raw;
            DxWrite(dict);
            return;
        }
        using var k = HiveOf(s.Hive).CreateSubKey(s.Path);
        switch (s.Raw)
        {
            case null: k.DeleteValue(s.Name, false); break;
            case string r when r.StartsWith("d:") && int.TryParse(r[2..], out var d): k.SetValue(s.Name, d, RegistryValueKind.DWord); break;
            case string r when r.StartsWith("s:"): k.SetValue(s.Name, r[2..], RegistryValueKind.String); break;
        }
    }

    private static RegistryKey HiveOf(string h) => h == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;

    private static int? Dword(RegistryKey hive, string path, string name)
    {
        try
        {
            using var k = hive.OpenSubKey(path);
            return k?.GetValue(name) is int v ? v : null;
        }
        catch { return null; }
    }

    private static void SetDword(RegistryKey hive, string path, string name, int value)
    {
        using var k = hive.CreateSubKey(path);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    // DirectXUserGlobalSettings = "Chave1=valor;Chave2=valor;"
    private static Dictionary<string, string> DxSettings()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(DxPath);
            foreach (var part in (k?.GetValue(DxName) as string ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (part.Split('=', 2) is { Length: 2 } kv) d[kv[0].Trim()] = kv[1].Trim();
        }
        catch { }
        return d;
    }

    private static string? DxGet(string key) => DxSettings().TryGetValue(key, out var v) ? v : null;

    private static void DxSet(string key, string value)
    {
        var d = DxSettings();
        d[key] = value;
        DxWrite(d);
    }

    private static void DxWrite(Dictionary<string, string> d)
    {
        using var k = Registry.CurrentUser.CreateSubKey(DxPath);
        if (d.Count == 0) k.DeleteValue(DxName, false);
        else k.SetValue(DxName, string.Concat(d.Select(kv => $"{kv.Key}={kv.Value};")), RegistryValueKind.String);
    }

    private static void Ui(Action a)
    {
        try
        {
            var app = Application.Current;
            if (app != null) app.Dispatcher.BeginInvoke(a);
        }
        catch { }
    }

    // ================================================================== Hz do monitor

    private sealed record HzData(string GdiName, string DevicePath, string Monitor, int Width, int Height, int OldHz, int NewHz);

    private static bool Same(GamerMonitor m, HzData d) =>
        d.DevicePath.Length > 0 ? string.Equals(m.DevicePath, d.DevicePath, StringComparison.OrdinalIgnoreCase)
                                : string.Equals(m.GdiName, d.GdiName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Entrada da Central Desfazer de um monitor (se mudámos a taxa dele) e a taxa original, de antes da primeira mudança.</summary>
    public static (string Id, int OriginalHz)? HzEntry(GamerMonitor m)
    {
        foreach (var e in ChangeJournal.All().Where(e => string.Equals(e.Kind, KindHz, StringComparison.OrdinalIgnoreCase)))
        {
            try { if (JsonSerializer.Deserialize<HzData>(e.Data) is { } d && Same(m, d)) return (e.Id, d.OldHz); } catch { }
        }
        return null;
    }

    // Marca em disco durante a contagem dos 15 s: a mudança dinâmica fica em vigor mesmo que a app feche ou vá abaixo,
    // por isso o arranque seguinte volta ao modo gravado se a marca ainda lá estiver (ninguém confirmou).
    private sealed record PendingHz(string GdiName, string DevicePath, int OldHz);
    private static string PendingFile => Path.Combine(AppPaths.Root, "hz-pendente.json");

    private static void ClearPendingHz() { try { File.Delete(PendingFile); } catch { } }

    private static void RecoverPendingHz()
    {
        if (!File.Exists(PendingFile)) return;
        // Só marcas de uma execução anterior: uma escrita depois do arranque é uma contagem a decorrer agora
        using (var self = System.Diagnostics.Process.GetCurrentProcess())
            if (File.GetLastWriteTimeUtc(PendingFile) >= self.StartTime.ToUniversalTime()) return;
        PendingHz? p = null;
        try { p = JsonSerializer.Deserialize<PendingHz>(File.ReadAllText(PendingFile)); } catch { }
        if (p != null && GamerDisplay.FindGdi(p.DevicePath, p.GdiName) is string gdi
            && GamerDisplay.Current(gdi) is { } c && c.hz != p.OldHz)
        {
            GamerDisplay.RevertToSaved(gdi);
            if (GamerDisplay.Current(gdi) is { } now && now.hz != p.OldHz) GamerDisplay.SetRefresh(gdi, p.OldHz, persist: false);
        }
        ClearPendingHz();
    }

    /// <summary>Passo 1: muda a taxa de forma dinâmica (não gravada). Devolve null se correu bem.</summary>
    public static string? TryRefresh(GamerMonitor m, int hz)
    {
        try { Directory.CreateDirectory(AppPaths.Root); File.WriteAllText(PendingFile, JsonSerializer.Serialize(new PendingHz(m.GdiName, m.DevicePath, m.CurrentHz))); } catch { }
        string? err = GamerDisplay.SetRefresh(m.GdiName, hz, persist: false);
        if (err != null) ClearPendingHz();
        return err;
    }

    /// <summary>Passo 2a (o utilizador confirmou): grava a nova taxa e regista na Central Desfazer.</summary>
    public static (bool ok, string msg) KeepRefresh(GamerMonitor m, int oldHz, int hz)
    {
        Init();
        ClearPendingHz();
        string? err = GamerDisplay.SaveRefresh(m.GdiName, hz);
        if (err != null) { RevertRefresh(m, oldHz); return (false, err); }

        // Uma só entrada por monitor, sempre com a taxa ORIGINAL (de antes da primeira mudança)
        int original = oldHz;
        foreach (var e in ChangeJournal.All().Where(e => string.Equals(e.Kind, KindHz, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (JsonSerializer.Deserialize<HzData>(e.Data) is { } d && Same(m, d))
                {
                    original = d.OldHz;
                    ChangeJournal.Remove(e.Id);
                }
            }
            catch { }
        }
        if (original != hz)
            ChangeJournal.Record(KindHz, $"{m.Name}: {original} → {hz} Hz", $"{m.Resolution} · {m.GdiName.Replace(@"\\.\", "")}",
                JsonSerializer.Serialize(new HzData(m.GdiName, m.DevicePath, m.Name, m.Width, m.Height, original, hz)));
        return (true, $"{m.Name} ficou a {hz} Hz.");
    }

    /// <summary>Passo 2b (não confirmou ou o tempo acabou): volta ao modo gravado (o de antes).</summary>
    public static void RevertRefresh(GamerMonitor m, int oldHz)
    {
        ClearPendingHz();
        GamerDisplay.RevertToSaved(m.GdiName);
        // Se o driver não voltou sozinho, força a taxa antiga (dinâmica)
        if (GamerDisplay.Current(m.GdiName) is { } c && c.hz != oldHz) GamerDisplay.SetRefresh(m.GdiName, oldHz, persist: false);
    }

    private static (bool ok, string msg) UndoHz(ChangeEntry e)
    {
        var d = JsonSerializer.Deserialize<HzData>(e.Data);
        if (d == null) return (false, "Faltam os dados para desfazer.");
        string? gdi = GamerDisplay.FindGdi(d.DevicePath, d.GdiName);
        if (gdi == null) return (false, $"O monitor {d.Monitor} não está ligado agora. Liga-o e tenta de novo.");
        if (GamerDisplay.Current(gdi) is { } c && c.hz == d.OldHz) return (true, $"{d.Monitor} já estava a {d.OldHz} Hz.");
        string? err = GamerDisplay.SetRefresh(gdi, d.OldHz, persist: true);
        return err == null ? (true, $"{d.Monitor} voltou a {d.OldHz} Hz.") : (false, err);
    }

    // ================================================================== HAGS (D3DKMT: suporte e estado real deste arranque)

    /// <summary>Suporte e estado ATIVO do HAGS nas placas físicas (lido do kernel gráfico; o registo só vale depois de reiniciar).</summary>
    public static class GpuSched
    {
        public sealed record Caps(bool Supported, bool Enabled);

        public static Caps? Read()
        {
            try
            {
                var e = new D3DKMT_ENUMADAPTERS2();
                if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return null;
                int size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
                IntPtr buf = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
                try
                {
                    e.pAdapters = buf;
                    if (D3DKMTEnumAdapters2(ref e) != 0) return null;
                    bool any = false, supported = false, enabled = false;
                    for (int i = 0; i < e.NumAdapters; i++)
                    {
                        var a = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buf + i * size);
                        try
                        {
                            uint type = Query(a.hAdapter, 15) ?? 0;          // KMTQAITYPE_ADAPTERTYPE
                            if ((type & 0x4) != 0 || (type & 0x1) == 0) continue; // software (Basic Render) ou sem render
                            if (Query(a.hAdapter, 70) is not uint caps) continue; // KMTQAITYPE_WDDM_2_7_CAPS
                            any = true;
                            supported |= (caps & 1) != 0;
                            enabled |= (caps & 2) != 0;
                        }
                        finally
                        {
                            var c = new D3DKMT_CLOSEADAPTER { hAdapter = a.hAdapter };
                            D3DKMTCloseAdapter(ref c);
                        }
                    }
                    return any ? new Caps(supported, enabled) : null;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return null; }
        }

        private static uint? Query(uint adapter, int type)
        {
            IntPtr data = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(data, 0);
                var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = adapter, Type = type, pPrivateDriverData = data, PrivateDriverDataSize = 4 };
                return D3DKMTQueryAdapterInfo(ref q) == 0 ? (uint)Marshal.ReadInt32(data) : null;
            }
            finally { Marshal.FreeHGlobal(data); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_ADAPTERINFO { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

        [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 e);
        [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO q);
        [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER c);
    }
}
