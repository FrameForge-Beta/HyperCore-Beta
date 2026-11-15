using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Diagnostics.Tracing.Session;

namespace FrameForge.Core;

/// <summary>
/// Passagem do nome antigo (FrameForge) para HyperCore: dados do utilizador, sessões ETW órfãs e tarefa de arranque.
/// Tudo é idempotente e tolerante a falhas — nunca impede a app de abrir e nunca apaga os dados antigos.
/// </summary>
public static class LegacyMigration
{
    public const string LegacyName = "FrameForge";
    public const string AppName = "HyperCore";

    private const string DoneMarker = ".migrado-de-frameforge";
    private const string BusyMarker = ".migracao-a-decorrer";
    private const int MaxAttempts = 3;
    private const long MaxJsonBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Devolve %AppData%\HyperCore. No 1.º arranque, se a pasta nova não existe (ou está vazia) e existe %AppData%\FrameForge,
    /// COPIA tudo para a pasta nova (a antiga fica intacta, para se poder voltar atrás) e corrige os caminhos absolutos
    /// guardados nos JSON copiados. Chamado pelo inicializador de <see cref="AppPaths.Root"/>, por isso corre antes de
    /// qualquer leitura de definições ou de backups, seja qual for a classe que lá chega primeiro.
    /// </summary>
    public static string PrepareDataRoot()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string root = Path.Combine(appData, AppName);
        try { MigrateData(Path.Combine(appData, LegacyName), root); } catch { }
        return root;
    }

    private static void MigrateData(string oldRoot, string newRoot)
    {
        if (!Directory.Exists(oldRoot)) return;
        string done = Path.Combine(newRoot, DoneMarker), busy = Path.Combine(newRoot, BusyMarker);
        if (File.Exists(done)) return;

        // Retoma uma cópia que ficou a meio (ficheiro bloqueado, app fechada à força); senão só copia para uma pasta nova/vazia.
        int attempt = 0;
        if (File.Exists(busy)) { try { int.TryParse(File.ReadAllText(busy).Trim(), out attempt); } catch { } }
        else if (!IsMissingOrEmpty(newRoot)) return;   // já há dados do HyperCore: não mexer

        Directory.CreateDirectory(newRoot);
        attempt++;
        File.WriteAllText(busy, attempt.ToString());

        int failed = CopyTree(oldRoot, newRoot);
        RewritePaths(newRoot, oldRoot, newRoot);

        if (failed == 0 || attempt >= MaxAttempts)
        {
            File.WriteAllText(done, $"Copiado de {oldRoot} em {DateTime.Now:yyyy-MM-dd HH:mm:ss}" +
                                    (failed > 0 ? $" ({failed} ficheiro(s) não copiado(s))" : ""));
            try { File.Delete(busy); } catch { }
        }
    }

    private static bool IsMissingOrEmpty(string dir)
    {
        try { return !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }

    /// <summary>Cópia recursiva sem substituir o que já existe no destino. Devolve quantos ficheiros falharam.</summary>
    private static int CopyTree(string src, string dst)
    {
        int failed = 0;
        try { Directory.CreateDirectory(dst); } catch { return 1; }

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(src).ToList(); } catch { return 1; }
        foreach (var file in files)
        {
            string name = Path.GetFileName(file);
            if (name is DoneMarker or BusyMarker) continue;
            string target = Path.Combine(dst, name);
            if (File.Exists(target)) continue;
            if (!CopyFile(file, target)) failed++;
        }

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(src).ToList(); } catch { return failed + 1; }
        foreach (var dir in dirs)
        {
            try { if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; } catch { continue; }
            failed += CopyTree(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
        return failed;
    }

    private static bool CopyFile(string src, string dst)
    {
        try { File.Copy(src, dst, overwrite: false); return true; }
        catch (IOException) when (!File.Exists(dst))
        {
            // Ficheiro aberto por outro processo: tenta ler partilhado (sem bloquear quem o tem aberto)
            try
            {
                using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                try { File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src)); } catch { }
                return true;
            }
            catch
            {
                try { if (File.Exists(dst) && new FileInfo(dst).Length == 0) File.Delete(dst); } catch { }
                return false;
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// Troca, em todos os JSON da pasta nova, os caminhos absolutos que apontam para a pasta antiga
    /// (ex.: StartupRemoved[].Backup = %AppData%\FrameForge\Startup\x.lnk). Ficheiros sem alterações não são reescritos.
    /// </summary>
    private static void RewritePaths(string dir, string oldRoot, string newRoot)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).ToList(); } catch { return; }
        foreach (var file in files)
        {
            try
            {
                if (new FileInfo(file).Length > MaxJsonBytes) continue;
                string text = File.ReadAllText(file);
                if (text.IndexOf(LegacyName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (node == null) continue;
                if (!RewriteNode(node, oldRoot, newRoot, out var replaced)) continue;
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, (replaced ?? node).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, file, overwrite: true);
            }
            catch { }
        }
    }

    /// <summary>Percorre o JSON e troca as strings que começam pela pasta antiga. Devolve true se mudou alguma coisa.</summary>
    private static bool RewriteNode(JsonNode node, string oldRoot, string newRoot, out JsonNode? replacement)
    {
        replacement = null;
        switch (node)
        {
            case JsonObject obj:
            {
                bool changed = false;
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is not JsonNode child) continue;
                    if (RewriteNode(child, oldRoot, newRoot, out var rep)) { changed = true; if (rep != null) obj[key] = rep; }
                }
                return changed;
            }
            case JsonArray arr:
            {
                bool changed = false;
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is not JsonNode child) continue;
                    if (RewriteNode(child, oldRoot, newRoot, out var rep)) { changed = true; if (rep != null) arr[i] = rep; }
                }
                return changed;
            }
            case JsonValue val when val.TryGetValue(out string? s) && s != null:
            {
                string? fixedPath = RewritePath(s, oldRoot, newRoot);
                if (fixedPath == null) return false;
                replacement = JsonValue.Create(fixedPath);
                return true;
            }
            default:
                return false;
        }
    }

    private static string? RewritePath(string value, string oldRoot, string newRoot)
    {
        string old = oldRoot.TrimEnd('\\', '/');
        if (!value.StartsWith(old, StringComparison.OrdinalIgnoreCase)) return null;
        if (value.Length == old.Length) return newRoot;
        char next = value[old.Length];
        if (next != '\\' && next != '/') return null;   // ex.: ...\FrameForge-Drivers não é a pasta de dados
        return newRoot.TrimEnd('\\', '/') + value[old.Length..];
    }

    /// <summary>
    /// Pára as sessões ETW órfãs com os nomes antigos ("FrameForge-Frames"/"FrameForge-Present") que ficaram de um crash:
    /// a opção Create só limpa sessões com o mesmo nome. Não toca nelas se o FrameForge antigo estiver aberto.
    /// </summary>
    public static void StopLegacyEtwSessions()
    {
        try
        {
            var procs = Process.GetProcessesByName(LegacyName);
            bool legacyRunning = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (legacyRunning) return;

            var active = TraceEventSession.GetActiveSessionNames();
            foreach (string name in new[] { LegacyName + "-Frames", LegacyName + "-Present" })
            {
                if (!active.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    using var session = TraceEventSession.GetActiveSession(name);
                    session?.Stop(noThrow: true);
                }
                catch { }
            }
        }
        catch { }
    }
}
