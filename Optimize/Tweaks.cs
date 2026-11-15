using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrameForge.Core;
using FrameForge.Hardware;
using Microsoft.Win32;

namespace FrameForge.Optimize;

/// <summary>Guarda o valor ORIGINAL de cada coisa que a app muda (só na primeira vez), para "Repor Windows original" voltar ao estado de fábrica do teu PC.</summary>
public static class TweakBackup
{
    private static readonly object Sync = new();
    private static Dictionary<string, string>? _data;
    private static Dictionary<string, string> Data => _data ??= Load();

    private static Dictionary<string, string> Load()
    {
        Dictionary<string, string> d;
        try { d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(AppPaths.BackupFile)) ?? new(); }
        catch { d = new(); }

        // Formato antigo das definições de energia: "pwr.<id>" = "plano|ac|dc". Passa a uma entrada por plano ("pwr.<id>.<plano>" = "ac|dc").
        // Entradas vazias ("plano||", leitura falhada) não servem para repor nada e são apagadas.
        bool changed = false;
        foreach (var (key, value) in d.Where(kv => kv.Key.StartsWith("pwr.", StringComparison.Ordinal)).ToList())
        {
            var parts = value.Split('|');
            bool legacy = key.Count(c => c == '.') == 1;
            bool valid = parts.Length == (legacy ? 3 : 2) && parts.TakeLast(2).All(p => int.TryParse(p, out _));
            if (legacy || !valid) { d.Remove(key); changed = true; }
            if (legacy && valid && Guid.TryParse(parts[0], out _)) d[$"{key}.{parts[0].ToLowerInvariant()}"] = $"{parts[1]}|{parts[2]}";
        }
        if (changed) Write(d);
        return d;
    }

    public static bool Has(string key) { lock (Sync) return Data.ContainsKey(key); }

    /// <summary>Existe a chave ou alguma "chave.qualquer" (ex.: "pwr.usb" cobre "pwr.usb.&lt;plano&gt;").</summary>
    public static bool HasAny(string key)
    {
        lock (Sync) return Data.Keys.Any(k => k == key || k.StartsWith(key + ".", StringComparison.Ordinal));
    }

    public static string? Get(string key) { lock (Sync) return Data.GetValueOrDefault(key); }

    public static List<KeyValuePair<string, string>> WithPrefix(string key)
    {
        lock (Sync) return Data.Where(kv => kv.Key.StartsWith(key + ".", StringComparison.Ordinal)).ToList();
    }

    public static void Remember(string key, string value)
    {
        lock (Sync)
        {
            if (Data.ContainsKey(key)) return;
            Data[key] = value;
            Write(Data);
        }
    }

    public static void Forget(string key) { lock (Sync) if (Data.Remove(key)) Write(Data); }

    /// <summary>Esquece a chave e todas as "chave.*".</summary>
    public static void ForgetAll(string key)
    {
        lock (Sync)
        {
            var hit = Data.Keys.Where(k => k == key || k.StartsWith(key + ".", StringComparison.Ordinal)).ToList();
            foreach (var k in hit) Data.Remove(k);
            if (hit.Count > 0) Write(Data);
        }
    }

    public static int Count { get { lock (Sync) return Data.Count; } }

    /// <summary>
    /// Uma só vez: traz os originais guardados pelo cartão antigo da Limpeza rápida (settings.json → TweakBackup) para este ficheiro,
    /// para o "Repor Windows original" continuar a repor o que esse cartão mudou. Nunca substitui uma chave que já exista aqui.
    /// </summary>
    public static int MigrateLegacy()
    {
        var s = AppServices.Settings;
        if (s.LegacyTweakBackupMigrated) return 0;
        int added = 0;
        try
        {
            string file = Path.Combine(ResetExplode.AppSettings.Folder, "settings.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("TweakBackup", out var tb) && tb.ValueKind == JsonValueKind.Object)
                    lock (Sync)
                    {
                        foreach (var p in tb.EnumerateObject())
                        {
                            string key = p.Name == "power" ? "plan" : p.Name; // o cartão antigo chamava "power" ao plano
                            string? v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                            if (key.StartsWith("mouse.", StringComparison.Ordinal)) v ??= ""; // "" = predefinido do Windows
                            if (v == null || Data.ContainsKey(key)) continue;
                            Data[key] = v;
                            added++;
                        }
                        if (added > 0) Write(Data);
                    }
            }
            s.LegacyTweakBackupMigrated = true;
            s.Save();
        }
        catch { }
        return added;
    }

    private static void Write(Dictionary<string, string> d)
    {
        try { File.WriteAllText(AppPaths.BackupFile, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
}

/// <summary>Para que nível se aplica: decide o plano de energia alvo (níveis 1-2 → Alto desempenho; 3-4 → Desempenho Máximo, só desktop).</summary>
public sealed record TweakTarget(int Level, bool Laptop)
{
    public string Plan => Level >= 3 && !Laptop ? TweakCatalog.Ultimate : TweakCatalog.HighPerf;
}

public sealed class Tweak : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public int Level { get; init; }                 // 1 Seguro · 2 Equilibrado · 3 Competitivo · 4 Extremo
    public string Title { get; init; } = "";
    public string Short { get; init; } = "";        // ganho real numa linha curta
    public string What { get; init; } = "";         // o que faz
    public string Gain { get; init; } = "";         // ganho real esperado, sem exageros
    public string Warning { get; init; } = "";      // quando não usar / efeitos secundários
    public bool NeedsRestart { get; init; }
    public bool IsService { get; init; }
    public bool IsPlan { get; init; }
    public bool IsPower { get; init; }
    /// <summary>Faz efeito na hora (registo/powercfg): pode ser aplicado só durante o jogo e reaplicado no arranque.</summary>
    public bool Instant => !NeedsRestart && !IsService;
    /// <summary>Só fica completo depois de reiniciar o PC (HAGS, temporizador, serviços).</summary>
    public bool RestartToFinish => NeedsRestart || IsService;
    /// <summary>Ordem de aplicação: o plano primeiro, depois as definições de energia (que se escrevem no plano), depois o resto.</summary>
    public int Order => IsPlan ? 0 : IsPower ? 1 : 2;

    internal Func<SystemProbe, string?> NotApplicable { get; init; } = _ => null; // motivo para não se aplicar neste PC
    internal Func<bool?> Read { get; init; } = () => null;                        // null = estado desconhecido
    internal Func<TweakTarget, bool>? IsSatisfied { get; init; }                  // null = ligado (State == true)
    internal Func<string>? Describe { get; init; }                                // detalhe extra (ex.: plano ativo)
    internal Action<TweakTarget> Apply { get; init; } = _ => { };
    internal Action Revert { get; init; } = () => { };
    /// <summary>"Repor Windows original": volta ao valor guardado tal como estava (null = igual ao Revert).</summary>
    internal Action? RevertOriginal { get; init; }
    /// <summary>Fotografia do valor bruto atual (para o JOGAR repor exatamente o que estava). null = não se consegue fotografar.</summary>
    internal Func<string?>? Capture { get; init; }
    internal Action<string>? Restore { get; init; }
    internal string[] BackupKeys { get; init; } = Array.Empty<string>();

    private string[] Keys => BackupKeys.Length == 0 ? new[] { Id } : BackupKeys;

    /// <summary>True se fomos nós a mudar isto (há um valor original guardado). "Repor" só mexe nestes.</summary>
    public bool Touched => Keys.Any(TweakBackup.HasAny);
    public void ForgetBackup() { foreach (var k in Keys) TweakBackup.ForgetAll(k); }

    public string LevelName => TweakCatalog.LevelNames[Math.Clamp(Level, 0, 4)].ToUpperInvariant();

    private bool? _state;
    /// <summary>Estado real lido do Windows: true ligado, false desligado, null não foi possível ler.</summary>
    public bool? State
    {
        get => _state;
        private set { _state = value; Changed(nameof(State)); Changed(nameof(IsOn)); Changed(nameof(IsUnknown)); }
    }
    public bool IsOn => _state == true;
    public bool IsUnknown => _state == null;

    private string _status = "";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    private string _detail = "";
    public string Detail { get => _detail; private set { _detail = value; Changed(nameof(Detail)); } }
    private string? _na;
    public string? Blocked { get => _na; set { _na = value; Changed(nameof(Blocked)); Changed(nameof(Enabled)); } }
    public bool Enabled => _na == null;

    public void Refresh(SystemProbe p)
    {
        try { Blocked = NotApplicable(p); } catch { Blocked = null; }
        ReadState();
    }

    public void ReadState()
    {
        bool? s;
        try { s = Read(); } catch { s = null; }
        State = s;
        try { Detail = Describe?.Invoke() ?? ""; } catch { Detail = ""; }
    }

    /// <summary>Está como o nível pede? (para o plano: o plano alvo do nível, ou um plano de desempenho personalizado).</summary>
    public bool Satisfied(TweakTarget target)
    {
        try { return IsSatisfied?.Invoke(target) ?? State == true; }
        catch { return false; }
    }

    /// <summary>
    /// Liga ou desliga (desligar = voltar ao valor original guardado). Depois de ligar, volta a ler: se o Windows não ficou com o valor,
    /// devolve erro e NÃO conta como ligado. quiet = não escreve o Status do cartão (usado pelo JOGAR e pelo arranque).
    /// Devolve a mensagem de erro, ou null se correu bem. exact = ao desligar, repõe o original tal como estava ("Repor Windows original").
    /// </summary>
    public string? Set(bool on, TweakTarget target, bool quiet = false, bool exact = false)
    {
        try
        {
            bool hadOriginal = Touched;
            if (on) Apply(target); else if (exact && RevertOriginal != null) RevertOriginal(); else Revert();
            ReadState();
            if (on && !Satisfied(target))
                throw new InvalidOperationException(State == null ? "Não consegui confirmar se o Windows aceitou a mudança." : "O Windows não aceitou a mudança.");
            // Desligar algo que a app nunca mudou: não há original para repor e, se continua ligado, é assim que o Windows vem
            if (!on && !hadOriginal && State == true)
                throw new InvalidOperationException("Já vinha assim no Windows: não há valor original para repor.");
            if (!on) ForgetBackup(); // já está no original: o próximo "ligar" guarda o valor que houver nessa altura
            if (!quiet) Status = on ? (RestartToFinish ? "Ligado — fica completo depois de reiniciar o PC." : "Ligado.") : "Reposto o valor original.";
            return null;
        }
        catch (Exception ex)
        {
            try { ReadState(); } catch { }
            if (!quiet) Status = "Falhou: " + ex.Message;
            return ex.Message;
        }
    }

    /// <summary>Atalho: usa o nível guardado (ou Seguro) para escolher o plano alvo.</summary>
    public string? Set(bool on) => Set(on, TweakCatalog.Target(AppServices.Settings.AppliedOptimizeLevel, null));

    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Plano de energia ativo.</summary>
public sealed record PowerPlan(string Guid, string Name);

/// <summary>
/// Catálogo de ajustes do Windows para jogos, por nível. Só entra aqui o que tem efeito medível e é reversível.
/// Ficam de fora (ver separador Dicas → Mitos) os "tweaks" placebo ou perigosos: Nagle, NetworkThrottlingIndex,
/// prioridades MMCSS, desligar o Defender/mitigações Spectre, bcdedit HPET/dynamic tick, limpar RAM em ciclo.
/// </summary>
public static class TweakCatalog
{
    public const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3", UsbSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string SubPcie = "501a4d13-42af-4429-9fd1-a8218c268e20", Aspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";
    private const string SubCpu = "54533251-82be-4824-96c1-47b60b740d00", CpMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";

    /// <summary>Uma operação de cada vez (página, arranque e JOGAR mexem nos mesmos valores).</summary>
    public static readonly object Gate = new();

    public static readonly string[] LevelNames = { "Nenhum", "Seguro", "Equilibrado", "Competitivo", "Extremo" };

    /// <summary>Ganho real de cada nível, numa linha (sem prometer FPS onde ele é ~0).</summary>
    public static readonly string[] LevelGains =
    {
        "",
        "Mira consistente e menos interrupções do Windows. FPS quase igual.",
        "Menos micro-atrasos de USB e PCIe, e HAGS para o Frame Generation.",
        "1% low um pouco mais estável (0–2%). Mais consumo em repouso.",
        "Menos picos de disco/CPU de fundo. A pesquisa do Windows fica lenta.",
    };

    public static readonly string[] LevelAudience =
    {
        "",
        "Para qualquer PC (num portátil, melhor ligado à corrente).",
        "Para desktops, no dia a dia.",
        "Para desktops em jogos competitivos.",
        "Só para quem joga competitivo a sério.",
    };

    public static readonly string[] LevelDescriptions =
    {
        "Não mexe em nada.",
        "Modo de Jogo, sem gravação da Game Bar, rato sem aceleração, otimizações em janela e plano Alto desempenho. Risco: nenhum.",
        "Tira poupanças de energia que causam pequenos atrasos (USB, PCIe), ativa o HAGS e trava apps da Loja em segundo plano. Risco: baixo (mais consumo em repouso).",
        "Plano Desempenho Máximo, núcleos sempre acordados, temporizador global e sem envio P2P de atualizações. Só desktop. Risco: mais calor/consumo em repouso.",
        "Desliga a indexação da pesquisa e o SysMain (só com SSD). Perdes conveniência: a pesquisa do Windows fica mais lenta.",
    };

    public static TweakTarget Target(int level, SystemProbe? p) => new(Math.Clamp(level, 1, 4), p?.IsLaptop ?? _laptop);
    private static bool _laptop;

    public static List<Tweak> All() => new()
    {
        // ---------------- 1 · SEGURO ----------------
        new Tweak
        {
            Id = "plan", Level = 1, IsPlan = true, BackupKeys = new[] { "plan" }, Title = "Plano de energia de desempenho",
            Short = "1% low um pouco melhor em desktop (0–3%). ~0 em Ryzen recentes.",
            What = "Ativa o plano Alto desempenho (níveis Seguro e Equilibrado) ou Desempenho Máximo (Competitivo e Extremo, só em desktop). A CPU sobe o clock na hora em vez de esperar.",
            Gain = "Menos quebras quando a carga muda de repente: 0–3% no 1% low em desktop. Num Ryzen recente com o plano Equilibrado moderno o ganho é quase nulo.",
            Warning = "Gasta mais energia em repouso. Em portáteis a usar a bateria, prefere o plano normal. Planos de desempenho personalizados (ReviOS, Atlas, cópias tuas) são respeitados.",
            Read = () => ActivePlan() is PowerPlan a ? IsPerformancePlan(a.Guid, a.Name) : null,
            // Nível 1-2: qualquer plano de desempenho serve. Nível 3-4: o Desempenho Máximo, ou um personalizado de desempenho.
            IsSatisfied = tg => ActivePlan() is PowerPlan a &&
                                (a.Guid == tg.Plan || IsCustomPerformancePlan(a.Guid, a.Name) || (tg.Plan == HighPerf && a.Guid == Ultimate)),
            Describe = () => ActivePlan() is PowerPlan a ? "Plano ativo: " + a.Name : "Não consegui ler o plano ativo.",
            Apply = tg =>
            {
                // Sem saber o plano de agora não há como voltar atrás: não muda nada
                string before = ActivePlan()?.Guid ?? throw new InvalidOperationException("Não consegui ler o plano de energia ativo, por isso não mudei nada.");
                TweakBackup.Remember("plan", before);
                EnsureScheme(tg.Plan);
                PowerCfg($"/setactive {tg.Plan}");
            },
            Revert = () =>
            {
                string prev = TweakBackup.Get("plan") ?? Balanced;
                // Se o original já era um dos planos que a app ativa (ou deixou de existir), "desligar" volta ao Equilibrado
                if (prev is HighPerf or Ultimate || !SchemeExists(prev)) prev = Balanced;
                PowerCfg($"/setactive {prev}");
            },
            // "Repor Windows original": o plano que o utilizador tinha, mesmo que fosse o Alto desempenho (só cai no Equilibrado se já não existir)
            RevertOriginal = () =>
            {
                string prev = TweakBackup.Get("plan") ?? Balanced;
                if (!SchemeExists(prev)) prev = Balanced;
                PowerCfg($"/setactive {prev}");
            },
            Capture = () => ActivePlan()?.Guid,
            Restore = guid => PowerCfg($"/setactive {guid}"),
        },
        Reg("gamemode", 1, "Modo de Jogo do Windows",
            "Evita que o Windows Update instale coisas a meio do jogo. Já vem ligado no Windows 11.",
            "Dá prioridade de CPU/GPU ao jogo em primeiro plano e impede o Windows Update de instalar drivers ou reiniciar durante a partida.",
            "~0 FPS. Evita quebras causadas por tarefas de fundo. É o padrão no Windows 11.",
            "Nenhum. Em PCs muito fracos com o OBS a gravar, pode tirar recursos ao OBS.",
            Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, absentIsOn: true),
        new Tweak
        {
            Id = "gamedvr", Level = 1, BackupKeys = new[] { "gamedvr.a", "gamedvr.b" }, Title = "Desligar gravação em segundo plano (Game DVR)",
            Short = "Só ajuda se a gravação da Game Bar estava ligada: menos uso da GPU e do disco.",
            What = "A Game Bar pode gravar continuamente os últimos minutos de jogo para o botão 'gravar o que aconteceu'. Isso usa o codificador da GPU e o disco o tempo todo.",
            Gain = "Pequeno. Só se nota se o 'gravar o que aconteceu' estava ligado (vem desligado por predefinição).",
            Warning = "Perdes o 'gravar o que aconteceu' da Game Bar (o ShadowPlay/NVIDIA App continua a funcionar). A Game Bar e as Definições → Jogos podem voltar a ligá-lo.",
            Read = () => (SystemProbe.RegDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") ?? 1) == 0 &&
                         (SystemProbe.RegDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled") ?? 1) == 0,
            Apply = _ =>
            {
                SetDword("gamedvr.a", Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0);
                SetDword("gamedvr.b", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0);
            },
            Revert = () =>
            {
                RestoreDword("gamedvr.a", Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 1);
                RestoreDword("gamedvr.b", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1);
            },
            Capture = CaptureOf(GameDvrValues), Restore = RestoreOf(GameDvrValues),
        },
        new Tweak
        {
            Id = "mouse", Level = 1, BackupKeys = MouseKeys.Select(n => "mouse." + n).ToArray(), Title = "Desligar aceleração do rato",
            Short = "Não muda FPS: mira mais consistente nos menus e em jogos sem 'raw input'.",
            What = "Desmarca 'Melhorar precisão do ponteiro': o cursor anda sempre a mesma distância para o mesmo movimento da mão.",
            Gain = "0 FPS. Mira mais consistente (memória muscular). Jogos com 'raw input' (Valorant, CS2) já ignoram isto, mas os menus e outros jogos não.",
            Warning = "No ambiente de trabalho o rato pode parecer mais lento: sobe a velocidade do ponteiro se precisares. O software do rato (G HUB, Synapse) pode voltar a ligá-la.",
            Read = () => MouseKeys.Select(n => ReadString(Registry.CurrentUser, @"Control Panel\Mouse", n)).ToList() is var v && v.Any(x => x == null) ? null : v.All(x => x == "0"),
            Apply = _ =>
            {
                foreach (var n in MouseKeys) TweakBackup.Remember($"mouse.{n}", ReadString(Registry.CurrentUser, @"Control Panel\Mouse", n) ?? "");
                ApplyMouse(new[] { "0", "0", "0" });
            },
            Revert = () => ApplyMouse(MouseKeys.Select((n, i) => TweakBackup.Get($"mouse.{n}") is { Length: > 0 } v ? v : new[] { "1", "6", "10" }[i]).ToArray()),
            Capture = CaptureOf(MouseValues),
            Restore = snap =>
            {
                RestoreOf(MouseValues)(snap);
                var now = MouseKeys.Select(n => ReadString(Registry.CurrentUser, @"Control Panel\Mouse", n)).ToArray();
                if (now.All(x => int.TryParse(x, out _))) SystemParametersInfo(0x0004, 0, now.Select(x => int.Parse(x!)).ToArray(), 0x03);
            },
        },
        new Tweak
        {
            Id = "windowed", Level = 1, Title = "Otimizações para jogos em janela",
            Short = "Menos latência e G-Sync/FreeSync em janela sem bordas (DX10/11).",
            What = "Faz jogos DirectX 10/11 em janela/sem bordas usarem o modelo de apresentação 'flip' moderno.",
            Gain = "Latência em janela sem bordas igual à de ecrã inteiro e VRR em janela. 0 em ecrã inteiro exclusivo, DX12 e Vulkan.",
            Warning = "Raros jogos antigos podem mostrar o ecrã preto ao alternar: desliga se acontecer.",
            NotApplicable = p => p.OsBuild is > 0 and < 22000 ? "só existe no Windows 11." : null,
            // Ausente: no Windows 11 24H2 (26100) ou mais recente já vem ligado por predefinição
            Read = () => DxSettings().TryGetValue("SwapEffectUpgradeEnable", out var v) ? v == "1" : Environment.OSVersion.Version.Build >= 26100,
            Apply = _ => { TweakBackup.Remember("windowed", DxRaw() ?? "<ausente>"); SetDx("SwapEffectUpgradeEnable", "1"); },
            Revert = () =>
            {
                using var k = Registry.CurrentUser.CreateSubKey(DxPath);
                if (TweakBackup.Get("windowed") is not string prev || prev == "<ausente>") k.DeleteValue("DirectXUserGlobalSettings", false);
                else k.SetValue("DirectXUserGlobalSettings", prev);
            },
            Capture = CaptureOf(DxValue), Restore = RestoreOf(DxValue),
        },

        // ---------------- 2 · EQUILIBRADO ----------------
        Reg("hags", 2, "Agendamento de GPU acelerado por hardware (HAGS)",
            "0–3% de FPS. Obrigatório para o Frame Generation do DLSS 3/4.",
            "A própria placa gere a fila de trabalho, em vez de ser a CPU.",
            "Menos carga de CPU e latência ligeiramente menor. Obrigatório para o Frame Generation do DLSS 3/4 (RTX 40/50). Numa RTX 20/30 o ganho é pequeno (0–3%).",
            "Raramente causa problemas em VR ou captura de ecrã antiga. Só faz efeito depois de reiniciar e com GPU/driver compatíveis.",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, absentIsOn: null, off: 1, restart: true),
        PowerSetting("usb", 2, SubUsb, UsbSuspend, 0, "Desligar suspensão seletiva USB",
            "Não muda FPS. Evita atrasos/desligar de rato, teclado e headset USB.",
            "O Windows deixa de pôr portas USB a dormir para poupar energia.",
            "0 FPS. Só ajuda quem tem pequenos atrasos ou desconexões em dispositivos USB (rato, headset, DAC).",
            "Consome uns watts a mais em repouso. Em portáteis gasta mais bateria."),
        PowerSetting("aspm", 2, SubPcie, Aspm, 0, "Desligar poupança de energia do PCIe (ASPM)",
            "Marginal: só ajuda micro-quebras com carga intermitente.",
            "A ligação PCIe à placa gráfica e ao SSD NVMe não entra em modo de baixo consumo.",
            "Marginal ou nulo em desktop (os planos de desempenho e muitas BIOS já o desligam). Pode ajudar micro-quebras com carga intermitente.",
            "Uns watts a mais em repouso."),
        new Tweak
        {
            Id = "bgapps", Level = 2, BackupKeys = new[] { "bgapps.a", "bgapps.b" }, Title = "Bloquear apps da Loja em segundo plano",
            Short = "Desprezável no FPS. Menos apps da Loja a acordar durante o jogo.",
            What = "Impede que apps da Microsoft Store (Mail, Fotos, Xbox, etc.) corram em segundo plano quando não estão abertas.",
            Gain = "Desprezável no FPS. Menos processos a acordar durante o jogo. Em algumas versões do Windows 11 o valor é ignorado.",
            Warning = "Apps da Loja deixam de mostrar notificações com a app fechada (ex.: Mail, WhatsApp da Loja). Programas normais (.exe) não são afetados.",
            Read = () => SystemProbe.RegDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled") == 1 &&
                         (SystemProbe.RegDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle") ?? 1) == 0,
            Apply = _ =>
            {
                SetDword("bgapps.a", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1);
                SetDword("bgapps.b", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0);
            },
            Revert = () =>
            {
                RestoreDword("bgapps.a", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 0);
                RestoreDword("bgapps.b", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 1);
            },
            Capture = CaptureOf(BgAppsValues), Restore = RestoreOf(BgAppsValues),
        },

        // ---------------- 3 · COMPETITIVO ----------------
        PowerSetting("parking", 3, SubCpu, CpMinCores, 100, "Núcleos da CPU sempre acordados (sem 'core parking')",
            "Quase nulo com planos de desempenho; pode ajudar um pouco o 1% low.",
            "O Windows deixa de 'estacionar' núcleos da CPU quando a carga baixa.",
            "~0 com o Alto desempenho/Desempenho Máximo (que quase sempre já têm 100% de núcleos mínimos). Pode ajudar um pouco o 1% low noutros planos.",
            "Mais consumo em repouso. Em CPUs Intel híbridas (P/E-cores) pode piorar ligeiramente a gestão: testa.",
            desktopOnly: true),
        new Tweak
        {
            Id = "timer", Level = 3, Title = "Resolução de temporizador global",
            Short = "Limitadores de FPS mais certinhos quando outra janela está em foco.",
            What = "No Windows 11, um jogo que pede um temporizador de alta precisão (1 ms) deixa de o perder quando outra janela está em foco.",
            Gain = "Só ajuda limitadores de FPS quando o jogo não está em foco ou está tapado (segundo monitor, Discord). 0 com o jogo em primeiro plano.",
            Warning = "Consumo ligeiramente maior. Só faz efeito depois de reiniciar.",
            NeedsRestart = true,
            NotApplicable = p => p.OsBuild is > 0 and < 22621 ? "só existe no Windows 11 22H2 ou mais recente." : null,
            Read = () => SystemProbe.RegDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests") == 1,
            Apply = _ => SetDword("timer", Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1),
            Revert = () => RestoreDword("timer", Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 0),
        },
        Reg("dosvc", 3, "Otimização de Entrega: não enviar atualizações para outros PCs",
            "Só ajuda se o Windows estava a enviar atualizações pela Internet (picos de ping).",
            "Impede o Windows de usar a tua ligação para enviar atualizações a outros PCs da Internet (P2P).",
            "~0 na maioria dos PCs (por predefinição só partilha na rede local). Evita picos de upload se o envio pela Internet estava ligado.",
            "As Definições passam a mostrar 'gerido pela organização' nesta opção. Os downloads de atualizações continuam normais.",
            Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, absentIsOn: false, off: null),

        // ---------------- 4 · EXTREMO ----------------
        Service("svc.search", 4, "WSearch", "Desligar a indexação da Pesquisa do Windows",
            "Menos picos de disco; nota-se sobretudo em discos HDD/SATA.",
            "O serviço que indexa ficheiros faz leituras de disco e uso de CPU em momentos aleatórios.",
            "Pequeno em SSD NVMe. Real em HDD/SATA: elimina uma fonte de picos de disco a meio do jogo.",
            "A pesquisa do menu Iniciar/Explorador fica bem mais lenta e o Outlook pode não pesquisar emails. Atualizações grandes do Windows podem voltar a ligá-lo."),
        Service("svc.sysmain", 4, "SysMain", "Desligar o SysMain (Superfetch)",
            "Quase nulo em PCs só com SSD.",
            "O SysMain pré-carrega programas para a RAM com base no teu uso.",
            "~0 em PCs só com SSD; às vezes evita atividade de disco durante o jogo.",
            "Programas que usas muito podem abrir um pouco mais devagar. Em PCs com HDD fica mais lento (por isso está bloqueado nesses PCs).",
            notApplicable: p => p.Drives.Any(d => d.Media == "HDD") ? "tens um disco HDD e aqui o SysMain ajuda." : null),
    };

    // ------------------------------------------------------------------ construtores

    private static Tweak Reg(string id, int level, string title, string shortGain, string what, string gain, string warning, RegistryKey hive, string path, string name,
                             int on, bool? absentIsOn, int? off = null, bool restart = false)
    {
        var val = new[] { new RegVal(hive, path, name) };
        return new()
        {
            Id = id, Level = level, Title = title, Short = shortGain, What = what, Gain = gain, Warning = warning, NeedsRestart = restart,
            Read = () => SystemProbe.RegDword(hive, path, name) is int v ? v == on : absentIsOn,
            Apply = _ => SetDword(id, hive, path, name, on),
            Revert = () => RestoreDword(id, hive, path, name, off),
            Capture = CaptureOf(val), Restore = RestoreOf(val),
        };
    }

    /// <summary>
    /// Definição de energia (powercfg). Escreve AC e DC no plano alvo do nível E no plano ativo (o plano é aplicado antes).
    /// Lê pelo registo (inclui as definições ocultas como o CPMINCORES); se não conseguir ler, o estado fica desconhecido.
    /// </summary>
    private static Tweak PowerSetting(string id, int level, string sub, string setting, int on, string title, string shortGain, string what, string gain, string warning,
                                      bool desktopOnly = false) => new()
    {
        Id = id, Level = level, IsPower = true, Title = title, Short = shortGain, What = what, Gain = gain, Warning = warning, BackupKeys = new[] { "pwr." + id },
        NotApplicable = p => desktopOnly && p.IsLaptop ? "é um portátil." : null,
        Read = () =>
        {
            if (ActiveScheme() is not string s) return null;
            var (ac, dc) = ReadPowerIndex(s, sub, setting);
            return ac is int a ? a == on && (dc ?? on) == on : null;
        },
        Describe = () => ActivePlan() is PowerPlan a ? "Aplica-se ao plano: " + a.Name : "",
        Apply = tg =>
        {
            var schemes = new List<string>();
            if (SchemeExists(tg.Plan)) schemes.Add(tg.Plan);
            string? active = ActiveScheme();
            if (active != null && !schemes.Contains(active)) schemes.Add(active);
            if (schemes.Count == 0) throw new InvalidOperationException("Não encontrei o plano de energia ativo.");
            foreach (var s in schemes)
            {
                var (ac, dc) = ReadPowerIndex(s, sub, setting);
                if (ac is not int a || dc is not int d) throw new InvalidOperationException("Não consegui ler o valor atual, por isso não mudei nada (não teria como voltar atrás).");
                TweakBackup.Remember($"pwr.{id}.{s}", $"{a}|{d}");
            }
            foreach (var s in schemes)
            {
                PowerCfg($"/setacvalueindex {s} {sub} {setting} {on}");
                PowerCfg($"/setdcvalueindex {s} {sub} {setting} {on}");
            }
            if (active != null) PowerCfg($"/setactive {active}"); // o plano ativo só lê os valores novos depois de reativado
        },
        Revert = () =>
        {
            string prefix = $"pwr.{id}.";
            foreach (var (key, value) in TweakBackup.WithPrefix("pwr." + id))
            {
                string scheme = key[prefix.Length..];
                var parts = value.Split('|');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var ac) || !int.TryParse(parts[1], out var dc) || !SchemeExists(scheme)) continue;
                PowerCfg($"/setacvalueindex {scheme} {sub} {setting} {ac}");
                PowerCfg($"/setdcvalueindex {scheme} {sub} {setting} {dc}");
            }
            if (ActiveScheme() is string a) PowerCfg($"/setactive {a}");
        },
        // Fotografia de todos os planos em que o Apply pode escrever (o ativo e os dois de desempenho): "plano|ac|dc;plano|ac|dc"
        Capture = () =>
        {
            if (ActiveScheme() is not string active) return null;
            var parts = new List<string>();
            foreach (var s in new[] { active, HighPerf, Ultimate }.Distinct())
            {
                if (s != active && !SchemeExists(s)) continue;
                var (ac, dc) = ReadPowerIndex(s, sub, setting);
                if (ac is int a && dc is int d) parts.Add($"{s}|{a}|{d}");
                else if (s == active) return null; // sem o valor do plano ativo não há como repor
            }
            return string.Join(';', parts);
        },
        Restore = snap =>
        {
            foreach (var item in snap.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = item.Split('|');
                if (p.Length != 3) continue;
                PowerCfg($"/setacvalueindex {p[0]} {sub} {setting} {p[1]}");
                PowerCfg($"/setdcvalueindex {p[0]} {sub} {setting} {p[2]}");
            }
            if (ActiveScheme() is string a) PowerCfg($"/setactive {a}");
        },
    };

    /// <summary>Serviço do Windows: muda o tipo de arranque pelo SCM (sc config), para valer logo e não só depois de reiniciar.</summary>
    private static Tweak Service(string id, int level, string svc, string title, string shortGain, string what, string gain, string warning,
                                 Func<SystemProbe, string?>? notApplicable = null) => new()
    {
        Id = id, Level = level, Title = title, Short = shortGain, What = what, Gain = gain, Warning = warning, IsService = true,
        NotApplicable = p => !ServiceExists(svc) ? "este serviço não existe neste Windows." : notApplicable?.Invoke(p),
        Read = () => SystemProbe.RegDword(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{svc}", "Start") is int v ? v == 4 : null,
        Apply = _ =>
        {
            TweakBackup.Remember(id, SystemProbe.RegDword(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{svc}", "Start")?.ToString() ?? "<ausente>");
            SystemProbe.Run("sc.exe", $"config {svc} start= disabled");
            try { using var sc = new ServiceController(svc); if (sc.Status == ServiceControllerStatus.Running) sc.Stop(); } catch { } // pode recusar parar: fica parado no próximo arranque
        },
        Revert = () =>
        {
            int start = int.TryParse(TweakBackup.Get(id), out var v) ? v : 2;
            bool delayed = SystemProbe.RegDword(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{svc}", "DelayedAutostart") == 1;
            string mode = start switch { 0 => "boot", 1 => "system", 2 => delayed ? "delayed-auto" : "auto", 3 => "demand", _ => "disabled" };
            SystemProbe.Run("sc.exe", $"config {svc} start= {mode}");
            if (start == 2)
                try { using var sc = new ServiceController(svc); if (sc.Status == ServiceControllerStatus.Stopped) sc.Start(); } catch { }
        },
    };

    private static bool ServiceExists(string svc)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc}");
        return k != null;
    }

    // ------------------------------------------------------------------ registo

    internal static void SetDword(string key, RegistryKey hive, string path, string name, int value)
    {
        TweakBackup.Remember(key, SystemProbe.RegDword(hive, path, name)?.ToString() ?? "<ausente>");
        using var k = hive.CreateSubKey(path);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    internal static void RestoreDword(string key, RegistryKey hive, string path, string name, int? fallbackOff)
    {
        using var k = hive.CreateSubKey(path);
        string? prev = TweakBackup.Get(key);
        if (prev == "<ausente>") k.DeleteValue(name, false);
        else if (prev != null && int.TryParse(prev, out var p)) k.SetValue(name, p, RegistryValueKind.DWord);
        else if (fallbackOff is int off) k.SetValue(name, off, RegistryValueKind.DWord);
        else k.DeleteValue(name, false);
    }

    private static string? ReadString(RegistryKey hive, string path, string name)
    {
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(name)?.ToString();
    }

    // ---- fotografia de valores brutos do registo (para o JOGAR repor exatamente o que estava) ----

    private sealed record RegVal(RegistryKey Hive, string Path, string Name);

    private static readonly RegVal[] GameDvrValues =
    {
        new(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled"),
        new(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
    };
    private static readonly RegVal[] BgAppsValues =
    {
        new(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled"),
        new(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle"),
    };
    private static readonly string[] MouseKeys = { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" };
    private static readonly RegVal[] MouseValues = MouseKeys.Select(n => new RegVal(Registry.CurrentUser, @"Control Panel\Mouse", n)).ToArray();
    private const string DxPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private static readonly RegVal[] DxValue = { new(Registry.CurrentUser, DxPath, "DirectXUserGlobalSettings") };

    private static Func<string?> CaptureOf(RegVal[] vals) => () =>
    {
        var raw = new string?[vals.Length];
        for (int i = 0; i < vals.Length; i++)
        {
            using var k = vals[i].Hive.OpenSubKey(vals[i].Path);
            raw[i] = k?.GetValue(vals[i].Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
            {
                null => null,
                int d => "d:" + d,
                string s => "s:" + s,
                _ => "?", // outro tipo: não se consegue repor com segurança
            };
        }
        return raw.Contains("?") ? null : JsonSerializer.Serialize(raw);
    };

    private static Action<string> RestoreOf(RegVal[] vals) => snap =>
    {
        var raw = JsonSerializer.Deserialize<string?[]>(snap) ?? Array.Empty<string?>();
        for (int i = 0; i < vals.Length && i < raw.Length; i++)
        {
            using var k = vals[i].Hive.CreateSubKey(vals[i].Path);
            switch (raw[i])
            {
                case null: k.DeleteValue(vals[i].Name, false); break;
                case string r when r.StartsWith("d:") && int.TryParse(r[2..], out var d): k.SetValue(vals[i].Name, d, RegistryValueKind.DWord); break;
                case string r when r.StartsWith("s:"): k.SetValue(vals[i].Name, r[2..], RegistryValueKind.String); break;
            }
        }
    };

    private static void ApplyMouse(string[] v)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse"))
            for (int i = 0; i < 3; i++) k.SetValue(MouseKeys[i], v[i]);
        SystemParametersInfo(0x0004, 0, v.Select(int.Parse).ToArray(), 0x03); // aplica já, sem terminar sessão
    }

    private static string? DxRaw() { using var k = Registry.CurrentUser.OpenSubKey(DxPath); return k?.GetValue("DirectXUserGlobalSettings") as string; }

    private static Dictionary<string, string> DxSettings()
    {
        var d = new Dictionary<string, string>();
        foreach (var part in (DxRaw() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (part.Split('=', 2) is { Length: 2 } kv) d[kv[0].Trim()] = kv[1].Trim();
        return d;
    }

    private static void SetDx(string key, string value)
    {
        var d = DxSettings();
        d[key] = value;
        using var k = Registry.CurrentUser.CreateSubKey(DxPath);
        k.SetValue("DirectXUserGlobalSettings", string.Concat(d.Select(kv => $"{kv.Key}={kv.Value};")));
    }

    // ------------------------------------------------------------------ energia

    /// <summary>Corre o powercfg; se o Windows recusar (código de saída ≠ 0), lança exceção com a mensagem dele.</summary>
    public static string PowerCfg(string args) => SystemProbe.Run("powercfg.exe", args);

    private static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    /// <summary>Plano ativo (GUID + nome). Os planos do Windows aparecem com o nome em português, sem depender da página de código do powercfg.</summary>
    public static PowerPlan? ActivePlan()
    {
        try
        {
            string o = PowerCfg("/getactivescheme");
            var m = GuidRx.Match(o);
            if (m.Success)
            {
                string guid = m.Value.ToLowerInvariant();
                var n = Regex.Match(o, @"\((.+)\)\s*$", RegexOptions.Multiline);
                return new PowerPlan(guid, KnownPlanName(guid) ?? (n.Success ? n.Groups[1].Value.Trim() : guid));
            }
        }
        catch { }
        try
        {
            // Alternativa: o registo guarda o plano ativo
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            if (k?.GetValue("ActivePowerScheme") is string g && GuidRx.IsMatch(g))
            {
                g = g.ToLowerInvariant();
                return new PowerPlan(g, KnownPlanName(g) ?? g);
            }
        }
        catch { }
        return null;
    }

    public static string? ActiveScheme() => ActivePlan()?.Guid;

    private static string? KnownPlanName(string guid) => guid switch
    {
        HighPerf => "Alto desempenho",
        Ultimate => "Desempenho Máximo",
        Balanced => "Equilibrado",
        PowerSaver => "Poupança de energia",
        _ => null,
    };

    /// <summary>
    /// Plano de desempenho = Alto desempenho, Desempenho Máximo, ou um personalizado que o pareça (mesma regra do Diagnóstico:
    /// ReviOS/Atlas/"performance"/"desempenho" e cópias destes). Os de poupança/equilibrado nunca contam.
    /// </summary>
    public static bool IsPerformancePlan(string guid, string name) => guid is HighPerf or Ultimate || IsCustomPerformancePlan(guid, name);

    public static bool IsCustomPerformancePlan(string guid, string name) =>
        guid is not (HighPerf or Ultimate or Balanced or PowerSaver) &&
        Regex.IsMatch(name, "ReviOS|Atlas|perform|desempenho|rendimiento|ultimate|bitsum", RegexOptions.IgnoreCase) &&
        !Regex.IsMatch(name, "Equilibrad|Balanced|Poupan|saver|Economi", RegexOptions.IgnoreCase);

    public static bool SchemeExists(string guid)
    {
        try { return PowerCfg("/list").Contains(guid, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Planos escondidos (Alto desempenho/Desempenho Máximo) são recriados com o GUID original, se faltarem.</summary>
    public static void EnsureScheme(string guid)
    {
        if (!SchemeExists(guid)) PowerCfg($"-duplicatescheme {guid} {guid}");
    }

    /// <summary>
    /// Valor (AC, DC) de uma definição de energia num plano. Primeiro pelo registo (também vê as definições ocultas, como o CPMINCORES),
    /// depois pelos valores de fábrica do plano e por fim "powercfg /qh". null = não foi possível ler (nunca "desligado").
    /// </summary>
    internal static (int? Ac, int? Dc) ReadPowerIndex(string scheme, string sub, string setting)
    {
        int? ac = null, dc = null;
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{scheme}\{sub}\{setting}"))
            {
                ac = k?.GetValue("ACSettingIndex") as int?;
                dc = k?.GetValue("DCSettingIndex") as int?;
            }
            if (ac == null || dc == null)
            {
                using var d = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{sub}\{setting}\DefaultPowerSchemeValues\{scheme}");
                ac ??= d?.GetValue("ACSettingIndex") as int?;
                dc ??= d?.GetValue("DCSettingIndex") as int?;
            }
        }
        catch { }
        if (ac == null || dc == null)
        {
            try
            {
                // /qh mostra também as definições ocultas; o texto é traduzido, mas os dois últimos hexadecimais são sempre AC e DC
                var hex = Regex.Matches(PowerCfg($"/qh {scheme} {sub} {setting}"), @"0x([0-9a-fA-F]+)");
                if (hex.Count >= 2)
                {
                    ac ??= Convert.ToInt32(hex[^2].Groups[1].Value, 16);
                    dc ??= Convert.ToInt32(hex[^1].Groups[1].Value, 16);
                }
            }
            catch { }
        }
        return (ac, dc);
    }

    /// <summary>Lembra se o PC é portátil (para o plano alvo quando ainda não há leitura do hardware à mão).</summary>
    internal static void Remember(SystemProbe p) => _laptop = p.IsLaptop;

    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, int[] data, uint winIni);
}

/// <summary>
/// "Manter sempre aplicado": compara o que o utilizador escolheu (nível + ajustes mudados à mão) com o estado real do Windows e
/// volta a aplicar o que o Windows ou outro programa repôs (Game Bar, software do rato, atualizações, utilitários do fabricante).
/// </summary>
public static class LevelKeeper
{
    /// <summary>Ajustes que deviam estar ligados e não estão (última verificação).</summary>
    public static IReadOnlyList<Tweak> Drifted { get; private set; } = Array.Empty<Tweak>();
    /// <summary>Avisa que houve nova verificação; o argumento é quem a pediu (a página ignora as suas).</summary>
    public static event Action<object?>? Changed;

    /// <summary>O utilizador quer este ajuste ligado? (ajuste mudado à mão ganha ao nível).</summary>
    public static bool Desired(Tweak t, AppSettings s) =>
        s.TweakOverrides.TryGetValue(t.Id, out var v) ? v : s.AppliedOptimizeLevel > 0 && t.Level <= s.AppliedOptimizeLevel;

    /// <summary>Volta a ler todos os ajustes e devolve os que deviam estar ligados e não estão. Chamar dentro de TweakCatalog.Gate.</summary>
    public static List<Tweak> Recheck(SystemProbe p, object? source = null)
    {
        TweakCatalog.Remember(p);
        var s = AppServices.Settings;
        var tg = TweakCatalog.Target(s.AppliedOptimizeLevel, p);
        var list = new List<Tweak>();
        foreach (var t in AppServices.Tweaks)
        {
            t.Refresh(p);
            if (t.Enabled && Desired(t, s) && t.State != null && !t.Satisfied(tg)) list.Add(t);
        }
        Drifted = list;
        Changed?.Invoke(source);
        return list;
    }

    /// <summary>Volta a aplicar os ajustes indicados (o plano primeiro). Devolve quantos ficaram bem.</summary>
    public static int Reapply(IEnumerable<Tweak> items, SystemProbe p, List<(string, bool)>? restart = null, bool quiet = false)
    {
        var tg = TweakCatalog.Target(AppServices.Settings.AppliedOptimizeLevel, p);
        int ok = 0;
        foreach (var t in items.OrderBy(t => t.Order))
            if (t.Set(true, tg, quiet) == null)
            {
                ok++;
                if (t.RestartToFinish) restart?.Add((t.Title, true));
            }
        return ok;
    }

    /// <summary>No arranque da app (em segundo plano): reaplica só os instantâneos (registo/powercfg) se "Manter sempre aplicado" estiver ligado.</summary>
    public static async Task StartupCheck()
    {
        try
        {
            TweakBackup.MigrateLegacy();
            var s = AppServices.Settings;
            if (s.AppliedOptimizeLevel == 0 && s.TweakOverrides.Count == 0) return;
            var p = await AppServices.Probe;
            int fixedCount = 0;
            await Task.Run(() =>
            {
                lock (TweakCatalog.Gate)
                {
                    var drift = Recheck(p);
                    if (drift.Count == 0 || !s.KeepLevelApplied) return;
                    fixedCount = Reapply(drift.Where(t => t.Instant), p, quiet: true);
                    if (fixedCount > 0) Recheck(p);
                }
            });
            if (fixedCount > 0)
                AppServices.Toast($"Reapliquei {fixedCount} {(fixedCount == 1 ? "ajuste que o Windows tinha reposto" : "ajustes que o Windows tinha reposto")}.");
        }
        catch { }
    }
}
