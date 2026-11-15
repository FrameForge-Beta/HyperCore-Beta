using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrameForge.Core;

namespace FrameForge.Tuning;

/// <summary>
/// Estado da afinação gravado no disco ANTES de cada passo de overclock. Se a app ou o PC forem abaixo a meio,
/// no próximo arranque este ficheiro diz que valor estava a ser testado (esse valor passa a ser o teto instável).
/// </summary>
public sealed class TuningState
{
    public bool EmCurso { get; set; } = true;
    public ScanProfile Perfil { get; set; }
    public ScanPhase Fase { get; set; }
    public DateTime Inicio { get; set; } = DateTime.Now;
    public DateTime Hora { get; set; } = DateTime.Now;
    /// <summary>Há valores de overclock (diferentes de fábrica) aplicados neste momento.</summary>
    public bool ValoresAplicados { get; set; }
    public int? NucleoEmTeste { get; set; }
    public int? MemoriaEmTeste { get; set; }
    public int PotenciaPct { get; set; } = 100;
    public int UltimoNucleoEstavel { get; set; }
    public int UltimaMemoriaEstavel { get; set; }
    public int? NucleoInstavel { get; set; }
    public int? MemoriaInstavel { get; set; }
    /// <summary>A própria app teve um erro grave (não foi o hardware): na recuperação o valor em teste não é culpado sem provas.</summary>
    public bool ErroDaApp { get; set; }
    public string? ErroTexto { get; set; }
    /// <summary>O relatório até à última fase concluída (para retomar).</summary>
    public ScanReport? Parcial { get; set; }
}

/// <summary>Ficheiros do scan em %AppData%\HyperCore\scan (ultimo.json, historico\*.json, estado-afinacao.json).</summary>
public static class ScanStore
{
    public static string Folder => Ensure(Path.Combine(AppPaths.Root, "scan"));
    public static string HistoryFolder => Ensure(Path.Combine(Folder, "historico"));
    public static string LastFile => Path.Combine(Folder, "ultimo.json");
    public static string StateFile => Path.Combine(Folder, "estado-afinacao.json");
    /// <summary>Estado de um scan interrompido já visto no arranque (fica até o utilizador retomar ou dispensar).</summary>
    public static string RecoveredFile => Path.Combine(Folder, "estado-afinacao.recuperado.json");

    private const int MaxHistory = 30;
    private static readonly object Gate = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }

    // ------------------------------------------------------------------ relatórios

    /// <summary>Grava no histórico e, se <paramref name="asLast"/>, como o último scan (só os completos: um scan parado não substitui o anterior).</summary>
    public static void Save(ScanReport r, bool asLast = true)
    {
        lock (Gate)
        {
            string json = JsonSerializer.Serialize(r, Json);
            if (asLast) WriteAtomic(LastFile, json);
            try
            {
                WriteAtomic(Path.Combine(HistoryFolder, $"{r.Data:yyyy-MM-dd_HH-mm-ss}_{r.Perfil}.json"), json);
                foreach (var old in new DirectoryInfo(HistoryFolder).GetFiles("*.json").OrderByDescending(f => f.Name).Skip(MaxHistory))
                    try { old.Delete(); } catch { }
            }
            catch { }
        }
    }

    public static ScanReport? LoadLast() => Load(LastFile);

    /// <summary>Scans anteriores, do mais recente para o mais antigo.</summary>
    public static List<ScanReport> History(int max = 10)
    {
        var list = new List<ScanReport>();
        try
        {
            foreach (var f in new DirectoryInfo(HistoryFolder).GetFiles("*.json").OrderByDescending(f => f.Name).Take(max))
                if (Load(f.FullName) is ScanReport r) list.Add(r);
        }
        catch { }
        return list;
    }

    private static ScanReport? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ScanReport>(File.ReadAllText(path), Json) : null; }
        catch { return null; }
    }

    // ------------------------------------------------------------------ estado da afinação

    /// <summary>Grava o estado e força a escrita no disco (tem de sobreviver a um bloqueio/reinício logo a seguir).</summary>
    /// <returns>false se não ficou gravado (disco cheio, permissões, relatório a mudar a meio…): quem vai aplicar overclock tem de parar.</returns>
    public static bool WriteState(TuningState s)
    {
        lock (Gate)
        {
            if (Frozen) return true; // a app está a fechar e o estado já foi resolvido: não volta a criar o ficheiro
            try { WriteAtomic(StateFile, JsonSerializer.Serialize(s, Json)); return true; }
            catch
            {
                // Segunda tentativa (uma lista do relatório pode ter mudado durante a serialização)
                try { WriteAtomic(StateFile, JsonSerializer.Serialize(s, Json)); return true; } catch { return false; }
            }
        }
    }

    /// <summary>Depois de o fecho da app ter resolvido o estado (Stock confirmado e ficheiro apagado), não se grava mais nada.</summary>
    public static volatile bool Frozen;

    public static TuningState? ReadState() => ReadStateFile(StateFile);
    public static TuningState? ReadRecovered() => ReadStateFile(RecoveredFile);

    public static void ClearState() { lock (Gate) { if (!Frozen) TryDelete(StateFile); } } // congelado: o fecho já decidiu o que fica

    /// <summary>Apaga o estado e impede gravações seguintes (fecho normal da app com a gráfica já em Stock).</summary>
    public static void ClearAndFreeze() { lock (Gate) { TryDelete(StateFile); Frozen = true; } }
    public static void ClearRecovered() { lock (Gate) TryDelete(RecoveredFile); }

    /// <summary>Passa o estado "em curso" para "recuperado" (para não o tratar duas vezes se a app voltar a fechar no arranque).</summary>
    public static void MoveStateToRecovered()
    {
        lock (Gate)
        {
            try { if (File.Exists(StateFile)) File.Move(StateFile, RecoveredFile, overwrite: true); } catch { }
        }
    }

    private static TuningState? ReadStateFile(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<TuningState>(File.ReadAllText(path), Json) : null; }
        catch { return null; }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static void WriteAtomic(string path, string text)
    {
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
