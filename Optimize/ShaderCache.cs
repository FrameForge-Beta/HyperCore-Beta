using System.IO;
using System.Text.Json;
using FrameForge.Core;

namespace FrameForge.Optimize;

/// <summary>Uma pasta de cache de shaders encontrada neste PC.</summary>
public sealed record ShaderDir(string Vendor, string Path, long Bytes, int Files);

/// <summary>A última limpeza que ainda se pode desfazer (cópia guardada).</summary>
public sealed record ShaderBackupInfo(string EntryId, DateTime WhenUtc, DateTime ExpiresUtc, long Bytes);

/// <summary>
/// Cache de shaders (NVIDIA, AMD, Intel e DirectX). "Limpar" MOVE os ficheiros para uma cópia em %LocalAppData%\HyperCore
/// (mesmo disco: é só mudar o nome, instantâneo) e regista na Central Desfazer; "Desfazer" devolve-os. A cópia é apagada
/// sozinha passados <see cref="KeepDays"/> dias, ou já com <see cref="DeleteBackups"/>.
/// Ficheiros em uso (jogo aberto) ficam onde estão.
/// </summary>
public static class ShaderCache
{
    public const string Kind = "shader-cache";
    public const int KeepDays = 7;

    private sealed record Item(string Orig, string Copy);
    private sealed record Data(string Root, List<Item> Items, long Bytes);

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string LocalLow => Path.Combine(Path.GetDirectoryName(Local.TrimEnd('\\')) ?? Local, "LocalLow");
    private static string BackupBase => Path.Combine(Local, "HyperCore", "cache-shaders-copia");

    private static IEnumerable<(string Vendor, string Path)> Known()
    {
        yield return ("NVIDIA", Path.Combine(Local, "NVIDIA", "DXCache"));
        yield return ("NVIDIA", Path.Combine(Local, "NVIDIA", "GLCache"));
        yield return ("NVIDIA", Path.Combine(LocalLow, "NVIDIA", "PerDriverVersion", "DXCache"));
        yield return ("NVIDIA", Path.Combine(LocalLow, "NVIDIA", "PerDriverVersion", "GLCache"));
        yield return ("AMD", Path.Combine(Local, "AMD", "DxCache"));
        yield return ("AMD", Path.Combine(Local, "AMD", "DxcCache"));
        yield return ("AMD", Path.Combine(Local, "AMD", "GLCache"));
        yield return ("AMD", Path.Combine(Local, "AMD", "VkCache"));
        yield return ("Intel", Path.Combine(LocalLow, "Intel", "ShaderCache"));
        yield return ("DirectX", Path.Combine(Local, "D3DSCache"));
    }

    private static readonly EnumerationOptions All = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Pastas de cache com tamanho (lento em caches grandes: usar fora do UI thread).</summary>
    public static List<ShaderDir> Scan()
    {
        var list = new List<ShaderDir>();
        foreach (var (vendor, path) in Known())
        {
            if (!Directory.Exists(path)) continue;
            long bytes = 0; int files = 0;
            try
            {
                foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", All))
                {
                    try { bytes += f.Length; files++; } catch { }
                }
            }
            catch { }
            list.Add(new ShaderDir(vendor, path, bytes, files));
        }
        return list;
    }

    /// <summary>Limpeza que ainda se pode desfazer (a mais recente), ou null.</summary>
    public static ShaderBackupInfo? Backup()
    {
        foreach (var e in ChangeJournal.All().Where(e => e.Kind == Kind))
        {
            try
            {
                if (JsonSerializer.Deserialize<Data>(e.Data) is { } d && Directory.Exists(d.Root))
                    return new ShaderBackupInfo(e.Id, e.WhenUtc, e.WhenUtc.AddDays(KeepDays), d.Bytes);
            }
            catch { }
        }
        return null;
    }

    /// <summary>Move a cache para a cópia e regista na Central Desfazer. Devolve (ok, mensagem, bytes movidos).</summary>
    public static (bool ok, string msg, long bytes) Clear()
    {
        GamerSetup.Init();
        var dirs = Scan().Where(d => d.Files > 0).ToList();
        if (dirs.Count == 0) return (true, "A cache de shaders já está vazia.", 0);

        string root = Path.Combine(BackupBase, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        var items = new List<Item>();
        long moved = 0; int skipped = 0;
        for (int i = 0; i < dirs.Count; i++)
        {
            string orig = dirs[i].Path, copy = Path.Combine(root, i.ToString());
            try
            {
                // Mesmo disco e nada em uso: muda só o nome da pasta (instantâneo)
                Directory.Move(orig, copy);
                Directory.CreateDirectory(orig);
                moved += dirs[i].Bytes;
                items.Add(new Item(orig, copy));
                continue;
            }
            catch { }
            // Algum ficheiro em uso (ou outro disco): ficheiro a ficheiro, saltando os que estão abertos
            bool any = false;
            foreach (var f in new DirectoryInfo(orig).EnumerateFiles("*", All).ToList())
            {
                try
                {
                    long len = f.Length;
                    string dest = Path.Combine(copy, Path.GetRelativePath(orig, f.FullName));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Move(f.FullName, dest);
                    moved += len; any = true;
                }
                catch { skipped++; }
            }
            if (any) items.Add(new Item(orig, copy));
        }

        if (items.Count == 0)
        {
            TryDelete(root);
            return (false, "Os ficheiros da cache estão todos em uso. Fecha os jogos (e o launcher) e tenta de novo.", 0);
        }
        ChangeJournal.Record(Kind, $"Cache de shaders limpa ({Size(moved)})",
            $"Cópia guardada durante {KeepDays} dias. Desfazer devolve os ficheiros.",
            JsonSerializer.Serialize(new Data(root, items, moved)));
        string msg = $"Cache de shaders limpa: {Size(moved)}. Os jogos voltam a preparar os shaders no próximo arranque (o primeiro pode demorar mais).";
        if (skipped > 0) msg += $" {skipped} ficheiro(s) em uso ficaram onde estavam.";
        return (true, msg, moved);
    }

    /// <summary>Handler da Central Desfazer: devolve os ficheiros (sem substituir os que o driver já criou de novo).</summary>
    internal static (bool ok, string msg) Undo(ChangeEntry e)
    {
        var d = JsonSerializer.Deserialize<Data>(e.Data);
        if (d == null) return (false, "Faltam os dados para desfazer.");
        if (!Directory.Exists(d.Root)) return (true, "A cópia já tinha sido apagada; não havia nada para devolver.");
        int back = 0, kept = 0, failed = 0;
        foreach (var it in d.Items)
        {
            if (!Directory.Exists(it.Copy)) continue;
            foreach (var f in new DirectoryInfo(it.Copy).EnumerateFiles("*", All).ToList())
            {
                try
                {
                    string dest = Path.Combine(it.Orig, Path.GetRelativePath(it.Copy, f.FullName));
                    if (File.Exists(dest)) { kept++; continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Move(f.FullName, dest);
                    back++;
                }
                catch { failed++; } // em uso ou sem acesso: fica na cópia para tentar outra vez
            }
        }
        if (failed > 0)
            return (false, $"{failed} ficheiro(s) da cache estão em uso e ficaram na cópia ({back} devolvidos). Fecha os jogos (e o launcher) e tenta de novo.");
        TryDelete(d.Root);
        return (true, kept > 0 ? $"Cache de shaders reposta ({back} ficheiros; {kept} já tinham sido criados de novo)." : "Cache de shaders reposta.");
    }

    /// <summary>Apaga já todas as cópias (liberta o espaço). Deixa de ser possível desfazer as limpezas.</summary>
    public static long DeleteBackups()
    {
        long freed = 0;
        foreach (var e in ChangeJournal.All().Where(e => e.Kind == Kind))
        {
            try { if (JsonSerializer.Deserialize<Data>(e.Data) is { } d) { freed += d.Bytes; TryDelete(d.Root); } } catch { }
            ChangeJournal.Remove(e.Id);
        }
        TryDelete(BackupBase);
        return freed;
    }

    /// <summary>No arranque: apaga as cópias com mais de <see cref="KeepDays"/> dias e as que já não têm entrada no registo.</summary>
    internal static void PurgeExpired()
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in ChangeJournal.All().Where(e => e.Kind == Kind))
        {
            Data? d = null;
            try { d = JsonSerializer.Deserialize<Data>(e.Data); } catch { }
            if (d == null || !Directory.Exists(d.Root)) { ChangeJournal.Remove(e.Id); continue; }
            if (DateTime.UtcNow - e.WhenUtc > TimeSpan.FromDays(KeepDays)) { TryDelete(d.Root); ChangeJournal.Remove(e.Id); }
            else keep.Add(Path.GetFullPath(d.Root));
        }
        if (!Directory.Exists(BackupBase)) return;
        foreach (var dir in Directory.EnumerateDirectories(BackupBase))
            if (!keep.Contains(Path.GetFullPath(dir))) TryDelete(dir);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    /// <summary>Tamanho legível em português ("1,4 GB").</summary>
    public static string Size(long b)
    {
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-PT");
        if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0", pt) + " GB";
        if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0", pt) + " MB";
        if (b >= 1L << 10) return (b / 1024.0).ToString("0", pt) + " KB";
        return b + " B";
    }
}
