using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameForge.Core;

/// <summary>Pastas da app em %AppData%\HyperCore (no 1.º arranque copia os dados de %AppData%\FrameForge, ver LegacyMigration).</summary>
public static class AppPaths
{
    public static string Root { get; } = LegacyMigration.PrepareDataRoot();
    public static string Reports => Ensure(Path.Combine(Root, "Relatorios"));
    public static string SettingsFile => Path.Combine(Ensure(Root), "definicoes.json");
    public static string BackupFile => Path.Combine(Ensure(Root), "backup-ajustes.json");

    private static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }
}

/// <summary>Perfil por jogo: o que aplicar ao jogar e o que medir.</summary>
public sealed class GameProfile
{
    public int OptimizeLevel { get; set; } = 0;          // 0 = não mexer; 1..4 = níveis (temporários durante o jogo)
    public string? OcProfile { get; set; }               // nome do perfil de OC a aplicar só durante o jogo
    public bool RecordSession { get; set; } = true;      // gravar frametimes + sensores e gerar relatório no fim
    public bool ShowOverlay { get; set; }                // overlay externo (janela própria, sem injeção)
    public bool HighPerfPlan { get; set; } = true;       // plano de energia de alto desempenho só durante o jogo
    public List<string> CloseBeforeLaunch { get; set; } = new(); // nomes de processos a fechar (com janela, sem forçar)
    public string? ExeOverride { get; set; }
    public string? LastProcess { get; set; }             // nome do processo que o JOGAR detetou a desenhar frames (o jogo a sério)
    public string? ExtraArgs { get; set; }
}

public sealed class ManualGame
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string? Args { get; set; }
}

public sealed class AppSettings
{
    public Dictionary<string, GameProfile> Profiles { get; set; } = new();
    public List<ManualGame> ManualGames { get; set; } = new();
    public List<Overclock.OcProfile> OcProfiles { get; set; } = new();
    public string? OcApplyOnStart { get; set; }
    public bool OcCrashGuard { get; set; } = true;
    public bool AdvancedCpuSensors { get; set; }        // LibreHardwareMonitor + PawnIO (driver de kernel)
    public int OverlayCorner { get; set; } = 0;          // 0 sup-esq, 1 sup-dir, 2 inf-esq, 3 inf-dir
    public double StutterSensitivity { get; set; } = 1.0;
    public HashSet<string> UnstableOcProfiles { get; set; } = new();
    public bool ShowHelp { get; set; } = true;            // barra "o que faz esta página" no topo
    public bool StartWithWindows { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public List<string> PendingRestart { get; set; } = new();   // ajustes à espera de reinício
    public long? PendingRestartBootId { get; set; }             // arranque do Windows (BootId) em que foram pedidos

    // Níveis de desempenho: o que o utilizador escolheu (sobrevive a reiniciar a app e o PC)
    public int AppliedOptimizeLevel { get; set; }               // 0 = nenhum; 1..4 = nível aplicado na página Níveis de desempenho
    public Dictionary<string, bool> TweakOverrides { get; set; } = new(); // ajustes ligados/desligados à mão fora do nível (id → ligado)
    public bool KeepLevelApplied { get; set; } = true;          // reaplicar sozinho no arranque o que o Windows desfizer
    public bool LegacyTweakBackupMigrated { get; set; }         // originais do cartão antigo (settings.json) já copiados para backup-ajustes.json
    public bool LegacyLevelChecked { get; set; }                // já se viu (uma vez) se a versão antiga tinha deixado um nível inteiro aplicado

    // Atualizações: null = ainda não se perguntou (a 1.ª versão nova encontrada pergunta); "auto" = instala sozinho; "manual" = avisa e pergunta
    public string? UpdateMode { get; set; }
    public string? UpdateNotifiedVersion { get; set; }          // última versão já avisada (o aviso não se repete a cada verificação)
    public DateTime? UpdateLastCheckUtc { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly object SaveLock = new();

    public GameProfile ProfileFor(string gameKey)
    {
        if (!Profiles.TryGetValue(gameKey, out var p)) Profiles[gameKey] = p = new GameProfile();
        return p;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try { SaveOrThrow(); } catch { }
    }

    public void SaveOrThrow()
    {
        lock (SaveLock)
        {
            string destination = AppPaths.SettingsFile;
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
