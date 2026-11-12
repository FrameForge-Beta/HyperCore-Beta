using System.Diagnostics;
using System.IO;
using System.Security;
using FrameForge.Hardware;

namespace FrameForge.Optimize;

public sealed record MaintenanceVolume(string Letter, string Model, string Media, long TotalBytes, long FreeBytes)
{
    public bool CanDefragment => string.Equals(Media, "HDD", StringComparison.OrdinalIgnoreCase);
    public string Description => $"{Letter}  {Model} · {Media} · {FormatBytes(TotalBytes - FreeBytes)} usados / {FormatBytes(TotalBytes)}";

    public static string FormatBytes(long bytes)
    {
        double value = Math.Max(0, bytes);
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

public sealed record TempCleanupResult(long BytesFreed, int FilesDeleted, int Skipped, int Errors);
public sealed class FolderSizeNode(string name, string path)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public FolderSizeNode? Parent { get; set; }
    public long Bytes { get; set; }
    public List<FolderSizeNode> Children { get; } = new();
}
public sealed record FolderSizeResult(FolderSizeNode Root, long TotalBytes, int FilesScanned, int Skipped);
public sealed record FolderScanUpdate(string Path, string ParentPath, string Name, long Bytes, long ParentBytes,
                                      long TotalBytes, int FilesScanned, int Skipped, bool IsNew);
public sealed record DefragResult(string Volume, int ExitCode, string Output);

/// <summary>Operações locais de manutenção; não apaga dados fora das pastas temporárias permitidas.</summary>
public static class MaintenanceService
{
    private static readonly TimeSpan TempFileMinimumAge = TimeSpan.FromHours(24);

    public static List<MaintenanceVolume> ReadVolumes()
    {
        var physical = SystemProbe.Collect().Drives
            .GroupBy(d => d.Letter, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var volumes = new List<MaintenanceVolume>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                string letter = drive.Name.TrimEnd('\\').ToUpperInvariant();
                var disk = physical.GetValueOrDefault(letter);
                volumes.Add(new MaintenanceVolume(
                    letter,
                    disk?.Model ?? "Disco não identificado",
                    disk?.Media ?? "Desconhecido",
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return volumes.OrderBy(v => v.Letter, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<DefragResult> DefragmentHddAsync(string volume, Action<string> progress)
    {
        string normalized = NormalizeVolume(volume);
        var current = ReadVolumes().FirstOrDefault(d => d.Letter.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (current == null)
            throw new InvalidOperationException($"A unidade {normalized} já não está disponível.");
        if (!current.CanDefragment)
            throw new InvalidOperationException($"{normalized} foi bloqueada: só HDDs confirmados podem ser desfragmentados. Tipo detetado: {current.Media}.");

        string defrag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "defrag.exe");
        if (!File.Exists(defrag))
            throw new FileNotFoundException("A ferramenta de otimização de discos do Windows não foi encontrada.", defrag);

        var startInfo = new ProcessStartInfo
        {
            FileName = defrag,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(normalized);
        startInfo.ArgumentList.Add("/D");
        startInfo.ArgumentList.Add("/U");
        startInfo.ArgumentList.Add("/V");

        using var process = new Process { StartInfo = startInfo };
        progress($"{normalized}: a desfragmentar HDD…");
        if (!process.Start())
            throw new InvalidOperationException($"Não foi possível iniciar a desfragmentação de {normalized}.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = (await stdout) + Environment.NewLine + (await stderr);
        return new DefragResult(normalized, process.ExitCode, output.Trim());
    }

    public static TempCleanupResult CleanTemporaryFiles(Action<string> progress)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] roots =
        {
            Path.GetTempPath(),
            Path.Combine(windows, "Temp"),
        };
        var uniqueRoots = roots
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        DateTime cutoff = DateTime.UtcNow - TempFileMinimumAge;
        long bytesFreed = 0;
        int filesDeleted = 0, skipped = 0, errors = 0;
        var directories = new List<string>();
        int visited = 0;

        foreach (string root in uniqueRoots)
        {
            progress($"A analisar temporários em {root}…");
            try
            {
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                {
                    skipped++;
                    continue;
                }
            }
            catch (Exception ex) when (IsFileAccessException(ex)) { errors++; continue; }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                directories.Add(directory);
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
                catch (Exception ex) when (IsFileAccessException(ex)) { errors++; continue; }

                foreach (string entry in entries)
                {
                    if ((++visited % 1000) == 0) progress($"{filesDeleted:N0} ficheiros temporários removidos…");
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            skipped++;
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry);
                            continue;
                        }

                        var file = new FileInfo(entry);
                        if (file.LastWriteTimeUtc > cutoff)
                        {
                            skipped++;
                            continue;
                        }
                        long length = file.Length;
                        file.Delete();
                        bytesFreed += length;
                        filesDeleted++;
                    }
                    catch (Exception ex) when (IsFileAccessException(ex)) { errors++; }
                }
            }
        }

        var protectedRoots = uniqueRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories.Where(d => !protectedRoots.Contains(d)).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch (Exception ex) when (IsFileAccessException(ex)) { errors++; }
        }

        return new TempCleanupResult(bytesFreed, filesDeleted, skipped, errors);
    }

    public static FolderSizeResult ScanFolder(string selectedFolder, Action<FolderScanUpdate> progress, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(selectedFolder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"A pasta selecionada já não existe: {root}");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Não é possível analisar uma pasta que seja uma ligação simbólica.");

        var rootNode = new FolderSizeNode(root, root);
        var pending = new Stack<FolderSizeNode>();
        pending.Push(rootNode);
        long total = 0;
        int files = 0, skipped = 0;
        progress(ToUpdate(rootNode, total, files, skipped, true));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(current.Path).ToArray(); }
            catch (Exception ex) when (IsFileAccessException(ex)) { skipped++; continue; }

            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skipped++;
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var child = new FolderSizeNode(Path.GetFileName(entry), entry);
                        child.Parent = current;
                        current.Children.Add(child);
                        pending.Push(child);
                        progress(ToUpdate(child, total, files, skipped, true));
                        continue;
                    }

                    long length = new FileInfo(entry).Length;
                    for (FolderSizeNode? node = current; node != null; node = node.Parent)
                        node.Bytes += length;
                    total += length;
                    files++;
                    if ((files % 512) == 0)
                        PublishPath(current, total, files, skipped, progress);
                }
                catch (Exception ex) when (IsFileAccessException(ex)) { skipped++; }
            }

            PublishPath(current, total, files, skipped, progress);
        }

        PublishPath(rootNode, total, files, skipped, progress);
        return new FolderSizeResult(rootNode, total, files, skipped);
    }

    private static void PublishPath(FolderSizeNode node, long total, int files, int skipped, Action<FolderScanUpdate> progress)
    {
        for (FolderSizeNode? current = node; current != null; current = current.Parent)
            progress(ToUpdate(current, total, files, skipped, false));
    }

    private static FolderScanUpdate ToUpdate(FolderSizeNode node, long total, int files, int skipped, bool isNew) =>
        new(node.Path, node.Parent?.Path ?? "", node.Name, node.Bytes, node.Parent?.Bytes ?? node.Bytes, total, files, skipped, isNew);

    private static string NormalizeVolume(string volume)
    {
        string normalized = volume.Trim().TrimEnd('\\').ToUpperInvariant();
        if (normalized.Length != 2 || normalized[1] != ':' || !char.IsAsciiLetter(normalized[0]))
            throw new ArgumentException("Seleciona uma letra de unidade válida.", nameof(volume));
        return normalized;
    }

    private static bool IsFileAccessException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException;

}
