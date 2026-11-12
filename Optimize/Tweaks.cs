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

/// <summary>Guarda o valor ORIGINAL de cada coisa que a app muda (só na primeira vez), para "Restaurar tudo" voltar ao estado de fábrica do teu PC.</summary>
public static class TweakBackup
{
    private static Dictionary<string, string>? _data;
    private static Dictionary<string, string> Data => _data ??= Load();

    private static Dictionary<string, string> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(AppPaths.BackupFile)) ?? new(); }
        catch { return new(); }
    }

    public static bool Has(string key) => Data.ContainsKey(key);
    public static string? Get(string key) => Data.GetValueOrDefault(key);

    public static void Remember(string key, string value)
    {
        if (Data.ContainsKey(key)) return;
        Data[key] = value;
        Save();
    }

    public static void Forget(string key) { if (Data.Remove(key)) Save(); }
    public static int Count => Data.Count;

    private static void Save()
    {
        try { File.WriteAllText(AppPaths.BackupFile, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
}

public sealed class Tweak : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public int Level { get; init; }                 // 1 Seguro · 2 Equilibrado · 3 Competitivo · 4 Extremo
    public string Title { get; init; } = "";
    public string What { get; init; } = "";         // o que faz
    public string Gain { get; init; } = "";         // ganho real esperado, sem exageros
    public string Warning { get; init; } = "";      // quando não usar / efeitos secundários
    public bool NeedsRestart { get; init; }
    public bool Instant => !NeedsRestart && !IsService;
    public bool IsService { get; init; }
    internal Func<SystemProbe, string?> NotApplicable { get; init; } = _ => null; // motivo para não se aplicar neste PC
    internal Func<bool?> Read { get; init; } = () => null;
    internal Action Apply { get; init; } = () => { };
    internal Action Revert { get; init; } = () => { };
    internal string[] BackupKeys { get; init; } = Array.Empty<string>();

    /// <summary>True se fomos nós a mudar isto (há um valor original guardado). "Restaurar" só mexe nestes.</summary>
    public bool Touched => (BackupKeys.Length == 0 ? new[] { Id } : BackupKeys).Any(TweakBackup.Has);

    public string LevelName => Level switch { 1 => "SEGURO", 2 => "EQUILIBRADO", 3 => "COMPETITIVO", _ => "EXTREMO" };
    public string Badges => (NeedsRestart ? "REINICIAR · " : "") + LevelName;

    private bool _on;
    public bool IsOn { get => _on; set { _on = value; Changed(nameof(IsOn)); } }
    private string _status = "";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    private string? _na;
    public string? Blocked { get => _na; set { _na = value; Changed(nameof(Blocked)); Changed(nameof(Enabled)); } }
    public bool Enabled => _na == null;

    public void Refresh(SystemProbe p)
    {
        Blocked = NotApplicable(p);
        IsOn = Read() ?? false;
    }

    /// <summary>Liga ou desliga (desligar = voltar ao valor original guardado). Devolve a mensagem de erro, se houver.</summary>
    public string? Set(bool on)
    {
        try
        {
            if (on) Apply(); else Revert();
            IsOn = Read() ?? on;
            Status = on ? (NeedsRestart ? "Aplicado — reinicia o PC para fazer efeito." : "Aplicado.") : "Reposto o valor original.";
            return null;
        }
        catch (Exception ex)
        {
            Status = "Falhou: " + ex.Message;
            return ex.Message;
        }
    }

    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Catálogo de ajustes do Windows para jogos, por nível. Só entra aqui o que tem efeito medível e é reversível.
/// Ficam de fora (ver separador Dicas → Mitos) os "tweaks" placebo ou perigosos: Nagle, NetworkThrottlingIndex,
/// prioridades MMCSS, desligar o Defender/mitigações Spectre, bcdedit HPET/dynamic tick, limpar RAM em ciclo.
/// </summary>
public static class TweakCatalog
{
    public const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3", UsbSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string SubPcie = "501a4d13-42af-4429-9fd1-a8218c268e20", Aspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";
    private const string SubCpu = "54533251-82be-4824-96c1-47b60b740d00", CpMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";

    public static readonly string[] LevelNames = { "Nenhum", "Seguro", "Equilibrado", "Competitivo", "Extremo" };

    public static readonly string[] LevelDescriptions =
    {
        "Não mexe em nada.",
        "Para qualquer PC, incluindo portáteis. Só ajustes que a própria Microsoft recomenda para jogos. Risco: nenhum.",
        "Tira poupanças de energia que causam pequenos engasgos (USB, PCIe) e ativa o HAGS. Recomendado para desktop. Risco: baixo (mais consumo em repouso).",
        "Plano Desempenho Máximo e núcleos sempre acordados: melhor 1% low e menos latência. Só desktop. Risco: mais calor/consumo em repouso.",
        "Desliga serviços de fundo que fazem picos de disco/CPU (indexação). Perdes conveniência (pesquisa do Windows mais lenta). Só se jogas competitivo a sério.",
    };

    public static List<Tweak> All() => new()
    {
        // ---------------- 1 · SEGURO ----------------
        Reg("gamemode", 1, "Modo de Jogo do Windows",
            "Dá prioridade de CPU/GPU ao jogo em primeiro plano e impede o Windows Update de instalar drivers ou reiniciar durante a partida.",
            "Pequeno no FPS médio, mas evita quebras causadas por tarefas de fundo. É o padrão no Windows 11.",
            "Nenhum. Em PCs muito fracos com OBS a gravar, pode tirar recursos ao OBS.",
            Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, absentIsOn: true),
        new Tweak
        {
            Id = "gamedvr", Level = 1, BackupKeys = new[] { "gamedvr.a", "gamedvr.b" }, Title = "Desligar gravação em segundo plano (Game DVR / Xbox Game Bar)",
            What = "A Game Bar grava continuamente os últimos minutos de jogo para o botão 'gravar o que aconteceu'. Isso usa o codificador da GPU e o disco o tempo todo.",
            Gain = "1–5% de FPS em GPUs médias e menos picos de disco. Mais notório em portáteis.",
            Warning = "Perdes o 'gravar o que aconteceu' da Game Bar (o ShadowPlay/NVIDIA App continua a funcionar se o usares).",
            Read = () => (SystemProbe.RegDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") ?? 1) == 0 &&
                         (SystemProbe.RegDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled") ?? 1) == 0,
            Apply = () =>
            {
                SetDword("gamedvr.a", Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0);
                SetDword("gamedvr.b", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0);
            },
            Revert = () =>
            {
                RestoreDword("gamedvr.a", Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 1);
                RestoreDword("gamedvr.b", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1);
            },
        },
        new Tweak
        {
            Id = "mouse", Level = 1, BackupKeys = new[] { "mouse.MouseSpeed" }, Title = "Desligar aceleração do rato",
            What = "Desmarca 'Melhorar precisão do ponteiro': o cursor anda sempre a mesma distância para o mesmo movimento da mão.",
            Gain = "Não muda FPS. Mira mais consistente (memória muscular). Jogos com 'raw input' (Valorant, CS2) já ignoram isto, mas os menus e outros jogos não.",
            Warning = "No ambiente de trabalho o rato pode parecer mais lento: sobe a velocidade do ponteiro se precisares.",
            Read = () => ReadString(Registry.CurrentUser, @"Control Panel\Mouse", "MouseSpeed") == "0",
            Apply = () =>
            {
                foreach (var n in MouseKeys) TweakBackup.Remember($"mouse.{n}", ReadString(Registry.CurrentUser, @"Control Panel\Mouse", n) ?? "");
                ApplyMouse(new[] { "0", "0", "0" });
            },
            Revert = () => ApplyMouse(MouseKeys.Select((n, i) => TweakBackup.Get($"mouse.{n}") is { Length: > 0 } v ? v : new[] { "1", "6", "10" }[i]).ToArray()),
        },
        new Tweak
        {
            Id = "windowed", Level = 1, Title = "Otimizações para jogos em janela",
            What = "Faz jogos DirectX 10/11 em janela/sem bordas usarem o modelo de apresentação 'flip' moderno.",
            Gain = "Latência em janela sem bordas igual à de ecrã inteiro e suporte a G-Sync/FreeSync em janela. Importante se jogas em 'ecrã inteiro em janela' (necessário para overlays externos no Valorant).",
            Warning = "Raros jogos antigos podem mostrar o ecrã preto ao alternar: desliga se acontecer.",
            NotApplicable = p => p.OsBuild < 22000 ? "Só existe no Windows 11." : null,
            Read = () => DxSettings().GetValueOrDefault("SwapEffectUpgradeEnable") == "1",
            Apply = () => { TweakBackup.Remember("windowed", DxRaw() ?? "<ausente>"); SetDx("SwapEffectUpgradeEnable", "1"); },
            Revert = () =>
            {
                using var k = Registry.CurrentUser.CreateSubKey(DxPath);
                if (TweakBackup.Get("windowed") is not string prev || prev == "<ausente>") k.DeleteValue("DirectXUserGlobalSettings", false);
                else k.SetValue("DirectXUserGlobalSettings", prev);
            },
        },
        Plan("plan.high", 1, HighPerf, "Plano de energia Alto desempenho",
            "A CPU sobe o clock na hora em vez de esperar, e os núcleos não 'adormecem' tão depressa.",
            "Melhor 1% low (menos quebras quando a carga muda de repente). Em Ryzen 5000 com o plano Equilibrado moderno o ganho é pequeno (0–3%).",
            "Gasta mais energia em repouso. Em portáteis com bateria, evita."),

        // ---------------- 2 · EQUILIBRADO ----------------
        Reg("hags", 2, "Agendamento de GPU acelerado por hardware (HAGS)",
            "A própria placa gere a fila de trabalho, em vez de ser a CPU.",
            "Menos carga de CPU e latência ligeiramente menor. Obrigatório para o Frame Generation do DLSS 3/4 (RTX 40/50). Numa RTX 20/30 o ganho é pequeno (0–3%).",
            "Raramente causa problemas em VR ou captura de ecrã antiga. Requer reiniciar.",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, absentIsOn: false, off: 1, restart: true),
        PowerSetting("usb", 2, SubUsb, UsbSuspend, 0, "Desligar suspensão seletiva USB",
            "O Windows deixa de pôr portas USB a dormir para poupar energia.",
            "Evita pequenos atrasos/desconexões do rato, teclado, headset USB e DACs. Não muda FPS.",
            "Consome uns watts a mais em repouso. Em portáteis gasta mais bateria."),
        PowerSetting("aspm", 2, SubPcie, Aspm, 0, "Desligar poupança de energia do PCIe (ASPM)",
            "A ligação PCIe à placa gráfica e ao SSD NVMe não entra em modo de baixo consumo.",
            "Remove o atraso de 'acordar' a ligação: ajuda em micro-quebras quando a carga é intermitente.",
            "Uns watts a mais em repouso."),
        new Tweak
        {
            Id = "bgapps", Level = 2, BackupKeys = new[] { "bgapps.a", "bgapps.b" }, Title = "Bloquear apps da Loja em segundo plano",
            What = "Impede que apps da Microsoft Store (Mail, Fotos, Xbox, etc.) corram em segundo plano quando não estão abertas.",
            Gain = "Menos processos a acordar durante o jogo. Pequeno, mas reduz picos aleatórios de CPU/disco.",
            Warning = "Apps da Loja deixam de mostrar notificações com a app fechada (ex.: Mail, WhatsApp da Loja). Programas normais (.exe) não são afetados.",
            Read = () => SystemProbe.RegDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled") == 1,
            Apply = () =>
            {
                SetDword("bgapps.a", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1);
                SetDword("bgapps.b", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0);
            },
            Revert = () =>
            {
                RestoreDword("bgapps.a", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 0);
                RestoreDword("bgapps.b", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 1);
            },
        },

        // ---------------- 3 · COMPETITIVO ----------------
        Plan("plan.ultimate", 3, Ultimate, "Plano Desempenho Máximo (Ultimate Performance)",
            "Plano escondido do Windows para estações de trabalho: elimina quase todas as micro-poupanças de energia.",
            "Ganho marginal sobre o Alto desempenho (0–2%), sobretudo no 1% low em jogos competitivos de FPS muito alto.",
            "Mais consumo e calor em repouso. Nunca em portáteis.",
            desktopOnly: true),
        PowerSetting("parking", 3, SubCpu, CpMinCores, 100, "Núcleos da CPU sempre acordados (sem 'core parking')",
            "O Windows deixa de 'estacionar' núcleos da CPU quando a carga baixa.",
            "Menos latência quando o jogo precisa de mais threads de repente (explosões, muitos jogadores). Pequeno mas real no 1% low.",
            "Mais consumo em repouso. Em CPUs Intel híbridas (P/E-cores) pode piorar ligeiramente a gestão: testa.",
            desktopOnly: true),
        new Tweak
        {
            Id = "timer", Level = 3, Title = "Resolução de temporizador global",
            What = "No Windows 11, um jogo que pede um temporizador de alta precisão (1 ms) deixa de o perder quando outra janela está em foco.",
            Gain = "Limitadores de FPS mais precisos (frametime mais regular com o FPS limitado) quando tens um segundo monitor/Discord em foco.",
            Warning = "Consumo ligeiramente maior. Requer reiniciar.",
            NeedsRestart = true,
            NotApplicable = p => p.OsBuild < 22621 ? "Só no Windows 11 22H2 ou mais recente." : null,
            Read = () => SystemProbe.RegDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests") == 1,
            Apply = () => SetDword("timer", Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1),
            Revert = () => RestoreDword("timer", Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 0),
        },
        Reg("dosvc", 3, "Otimização de Entrega: não enviar atualizações para outros PCs",
            "Impede o Windows de usar a tua ligação para enviar atualizações a outros PCs da internet (P2P).",
            "Evita picos de upload que fazem subir o ping durante o jogo. Downloads de atualizações continuam normais.",
            "Nenhum relevante.",
            Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, absentIsOn: false, off: null),

        // ---------------- 4 · EXTREMO ----------------
        Service("svc.search", 4, "WSearch", "Desligar a indexação da Pesquisa do Windows",
            "O serviço que indexa ficheiros faz leituras de disco e uso de CPU em momentos aleatórios.",
            "Elimina uma fonte de picos de disco/CPU a meio do jogo, sobretudo em discos HDD ou SATA.",
            "A pesquisa do menu Iniciar/Explorador fica bem mais lenta e o Outlook pode não pesquisar emails. Reversível."),
        Service("svc.sysmain", 4, "SysMain", "Desligar o SysMain (Superfetch)",
            "O SysMain pré-carrega programas para a RAM com base no teu uso.",
            "Em PCs só com SSD o benefício do SysMain é quase nulo e às vezes provoca atividade de disco durante o jogo.",
            "Programas que usas muito podem abrir um pouco mais devagar. Em PCs com HDD, NÃO desligues (fica mais lento).",
            notApplicable: p => p.Drives.Any(d => d.Media == "HDD") ? "Tens um disco HDD: aqui o SysMain ajuda, não desligues." : null),
    };

    // ------------------------------------------------------------------ construtores

    private static Tweak Reg(string id, int level, string title, string what, string gain, string warning, RegistryKey hive, string path, string name,
                             int on, bool absentIsOn, int? off = null, bool restart = false) => new()
    {
        Id = id, Level = level, Title = title, What = what, Gain = gain, Warning = warning, NeedsRestart = restart,
        Read = () => SystemProbe.RegDword(hive, path, name) is int v ? v == on : absentIsOn,
        Apply = () => SetDword(id, hive, path, name, on),
        Revert = () => RestoreDword(id, hive, path, name, off),
    };

    private static Tweak Plan(string id, int level, string guid, string title, string what, string gain, string warning, bool desktopOnly = false) => new()
    {
        Id = id, Level = level, Title = title, What = what, Gain = gain, Warning = warning, BackupKeys = new[] { "plan" },
        NotApplicable = p => desktopOnly && p.IsLaptop ? "Desativado em portáteis (bateria e temperatura)." : null,
        Read = () => ActiveScheme() == guid,
        Apply = () =>
        {
            TweakBackup.Remember("plan", ActiveScheme() ?? Balanced);
            EnsureScheme(guid);
            PowerCfg($"/setactive {guid}");
        },
        Revert = () =>
        {
            string prev = TweakBackup.Get("plan") ?? Balanced;
            PowerCfg($"/setactive {(prev == guid ? Balanced : prev)}");
        },
    };

    private static Tweak PowerSetting(string id, int level, string sub, string setting, int on, string title, string what, string gain, string warning,
                                      bool desktopOnly = false) => new()
    {
        Id = id, Level = level, Title = title, What = what, Gain = gain, Warning = warning, BackupKeys = new[] { "pwr." + id },
        NotApplicable = p => desktopOnly && p.IsLaptop ? "Desativado em portáteis." : null,
        Read = () => ReadPowerAc(sub, setting) == on,
        Apply = () =>
        {
            string scheme = ActiveScheme() ?? "SCHEME_CURRENT";
            var (ac, dc) = ReadPower(sub, setting);
            TweakBackup.Remember("pwr." + id, $"{scheme}|{ac}|{dc}");
            PowerCfg($"/setacvalueindex SCHEME_CURRENT {sub} {setting} {on}");
            PowerCfg($"/setdcvalueindex SCHEME_CURRENT {sub} {setting} {on}");
            PowerCfg("/setactive SCHEME_CURRENT");
        },
        Revert = () =>
        {
            if (TweakBackup.Get("pwr." + id) is not string b) return;
            var parts = b.Split('|');
            if (parts.Length != 3 || parts[1].Length == 0) return;
            PowerCfg($"/setacvalueindex {parts[0]} {sub} {setting} {parts[1]}");
            PowerCfg($"/setdcvalueindex {parts[0]} {sub} {setting} {parts[2]}");
            PowerCfg("/setactive SCHEME_CURRENT");
        },
    };

    private static Tweak Service(string id, int level, string svc, string title, string what, string gain, string warning,
                                 Func<SystemProbe, string?>? notApplicable = null) => new()
    {
        Id = id, Level = level, Title = title, What = what, Gain = gain, Warning = warning, IsService = true,
        NotApplicable = notApplicable ?? (_ => null),
        Read = () => SystemProbe.RegDword(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{svc}", "Start") == 4,
        Apply = () =>
        {
            SetDword(id, Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{svc}", "Start", 4);
            try { using var sc = new ServiceController(svc); if (sc.Status == ServiceControllerStatus.Running) sc.Stop(); } catch { }
        },
        Revert = () =>
        {
            RestoreDword(id, Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{svc}", "Start", 2);
            try { using var sc = new ServiceController(svc); if (sc.Status == ServiceControllerStatus.Stopped) sc.Start(); } catch { }
        },
    };

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
        else if (prev != null) k.SetValue(name, int.Parse(prev), RegistryValueKind.DWord);
        else if (fallbackOff is int off) k.SetValue(name, off, RegistryValueKind.DWord);
        else k.DeleteValue(name, false);
    }

    private static string? ReadString(RegistryKey hive, string path, string name)
    {
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(name)?.ToString();
    }

    private static readonly string[] MouseKeys = { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" };

    private static void ApplyMouse(string[] v)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse"))
            for (int i = 0; i < 3; i++) k.SetValue(MouseKeys[i], v[i]);
        SystemParametersInfo(0x0004, 0, v.Select(int.Parse).ToArray(), 0x03); // aplica já, sem terminar sessão
    }

    private const string DxPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private static string? DxRaw() => Registry.CurrentUser.OpenSubKey(DxPath)?.GetValue("DirectXUserGlobalSettings") as string;

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

    public static string PowerCfg(string args) => SystemProbe.Run("powercfg.exe", args);

    public static string? ActiveScheme()
    {
        var m = Regex.Match(PowerCfg("/getactivescheme"), @"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>Planos escondidos (Alto desempenho/Desempenho Máximo) são recriados com o GUID original, se faltarem.</summary>
    public static void EnsureScheme(string guid)
    {
        if (!PowerCfg("/list").Contains(guid, StringComparison.OrdinalIgnoreCase))
            PowerCfg($"-duplicatescheme {guid} {guid}");
    }

    /// <summary>Lê o valor atual (AC, DC). O texto do powercfg é traduzido, por isso usa os dois últimos números hexadecimais (sempre AC e DC).</summary>
    private static (string Ac, string Dc) ReadPower(string sub, string setting)
    {
        var hex = Regex.Matches(PowerCfg($"/q SCHEME_CURRENT {sub} {setting}"), @"0x([0-9a-fA-F]+)");
        if (hex.Count < 2) return ("", "");
        return (Convert.ToInt32(hex[^2].Groups[1].Value, 16).ToString(), Convert.ToInt32(hex[^1].Groups[1].Value, 16).ToString());
    }

    private static int? ReadPowerAc(string sub, string setting) => int.TryParse(ReadPower(sub, setting).Ac, out var v) ? v : null;

    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, int[] data, uint winIni);
}
