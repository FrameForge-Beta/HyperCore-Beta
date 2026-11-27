using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ResetExplode;

public enum AppUpdateState
{
    Available,
    Queued,
    Updating,
    Updated,
    AlreadyUpToDate,
    Failed,
    Skipped
}

public sealed class UpdatableApp : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string WingetId { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string AvailableVersion { get; init; } = "";
    public string Source { get; init; } = "winget";
    public bool IsUnknownVersion { get; init; }
    public bool RequiresExplicitTarget { get; init; }

    private bool _selected;
    private bool _isExcluded;
    private string _status = "";
    private string _errorDetail = "";
    private AppUpdateState _state = AppUpdateState.Available;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnChanged();
        }
    }

    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (_isExcluded == value) return;
            _isExcluded = value;
            if (value) _selected = false;
            OnChanged();
            OnChanged(nameof(Selected));
            OnChanged(nameof(CanSelect));
        }
    }

    public AppUpdateState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnChanged();
            OnChanged(nameof(StateKey));
            OnChanged(nameof(BadgeText));
            OnChanged(nameof(CanSelect));
            OnChanged(nameof(CanQuickUpdate));
            OnChanged(nameof(IsDone));
            OnChanged(nameof(HasError));
            OnChanged(nameof(IsUpdating));
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnChanged();
            OnChanged(nameof(HasStatus));
        }
    }

    public string ErrorDetail
    {
        get => _errorDetail;
        set
        {
            if (_errorDetail == value) return;
            _errorDetail = value;
            OnChanged();
            OnChanged(nameof(StatusTooltip));
        }
    }

    public bool HasUpdate => State is AppUpdateState.Available or AppUpdateState.Queued or AppUpdateState.Updating or AppUpdateState.Failed or AppUpdateState.Skipped;
    public bool CanSelect => !IsExcluded && State is AppUpdateState.Available or AppUpdateState.Failed or AppUpdateState.Skipped;
    public bool CanQuickUpdate => !IsExcluded && State is AppUpdateState.Available or AppUpdateState.Failed or AppUpdateState.Skipped;
    public bool IsDone => State is AppUpdateState.Updated or AppUpdateState.AlreadyUpToDate;
    public bool HasError => State == AppUpdateState.Failed;
    public bool IsUpdating => State == AppUpdateState.Updating;
    public bool HasStatus => !string.IsNullOrWhiteSpace(_status);
    public string StatusTooltip => string.IsNullOrWhiteSpace(_errorDetail) ? _status : _errorDetail;

    public string CurrentVersionDisplay => string.IsNullOrWhiteSpace(CurrentVersion) ? "Desconhecida" : CurrentVersion;
    public string VersionText => $"{CurrentVersionDisplay} → {AvailableVersion}";
    public string SourceDisplay => string.IsNullOrWhiteSpace(Source) ? "winget" : Source;

    public string StateKey => State switch
    {
        AppUpdateState.Queued => "Queued",
        AppUpdateState.Updating => "Updating",
        AppUpdateState.Updated or AppUpdateState.AlreadyUpToDate => "Done",
        AppUpdateState.Failed => "Failed",
        AppUpdateState.Skipped => "Skipped",
        _ => "Available"
    };

    public string BadgeText => State switch
    {
        AppUpdateState.Queued => "NA FILA",
        AppUpdateState.Updating => "ATUALIZANDO",
        AppUpdateState.Updated => "ATUALIZADO",
        AppUpdateState.AlreadyUpToDate => "EM DIA",
        AppUpdateState.Failed => "FALHOU",
        AppUpdateState.Skipped => "PARADO",
        _ => IsUnknownVersion ? "VERSÃO DESC." : "DISPONÍVEL"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed class UpdateCheckResult
{
    public List<UpdatableApp> Apps { get; init; } = new();
    public int UnknownVersionCount { get; init; }
    public bool WingetMissing { get; init; }
    public string? WingetVersion { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record AppInstallResult(bool Ok, string Message, string? Detail = null, bool RestartRecommended = false);

public static class AppInstaller
{
    private const string RegistryPath = @"Software\HyperCore\UpdateExclusions";
    private const string LegacyRegistryPath = @"SoftwareHyperCoreUpdateExclusions";
    private static readonly Regex UnknownCountRegex = new(@"(\d+)\s+(?:pacotes?|packages?)\s+(?:t[eê]m|have)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? _cachedWingetPath;
    private static string? _cachedWingetVersion;

    public static string WingetPath => ResolveWingetPath() ?? "";

    public static bool WingetAvailable => !string.IsNullOrEmpty(ResolveWingetPath());

    public static string? ResolveWingetPath()
    {
        if (!string.IsNullOrEmpty(_cachedWingetPath) && File.Exists(_cachedWingetPath))
            return _cachedWingetPath;

        // 1. Alias padrão do App Installer do usuário atual
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultPath = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(defaultPath))
            return _cachedWingetPath = defaultPath;

        // 2. Procura no PATH do ambiente
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(candidate))
                    return _cachedWingetPath = candidate;
            }
            catch { }
        }

        // 3. Fallback na pasta WindowsApps (Program Files)
        try
        {
            var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (Directory.Exists(windowsApps))
            {
                var dir = Directory.EnumerateDirectories(windowsApps, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (dir != null)
                {
                    var exe = Path.Combine(dir, "winget.exe");
                    if (File.Exists(exe))
                        return _cachedWingetPath = exe;
                }
            }
        }
        catch { }

        return null;
    }

    public static async Task<string?> GetWingetVersionAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedWingetVersion))
            return _cachedWingetVersion;

        var exe = ResolveWingetPath();
        if (string.IsNullOrEmpty(exe)) return null;
        try
        {
            var (exitCode, output) = await RunWingetCaptureAsync(exe, "--version", TimeSpan.FromSeconds(15), ct);
            if (exitCode == 0)
            {
                var line = NormalizeTerminalLines(output).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrWhiteSpace(line))
                    return _cachedWingetVersion = line.Trim();
            }
        }
        catch { }
        return null;
    }

    public static async Task<UpdateCheckResult> CheckUpdatesAsync(bool includeUnknown, CancellationToken ct)
    {
        var exe = ResolveWingetPath();
        if (string.IsNullOrEmpty(exe))
        {
            return new UpdateCheckResult
            {
                WingetMissing = true,
                ErrorMessage = "O winget (Instalador de Aplicativo) não foi encontrado neste PC."
            };
        }

        var args = "upgrade --accept-source-agreements --disable-interactivity";
        if (includeUnknown)
            args += " --include-unknown";

        try
        {
            var (exitCode, output) = await RunWingetCaptureAsync(exe, args, TimeSpan.FromSeconds(180), ct);
            var version = await GetWingetVersionAsync(ct);

            var parsed = ParseUpgradeOutput(output, out int unknownCount);

            // ExitCode 0 ou -1978335189 (sem atualizações disponíveis) são normais
            if (parsed.Count == 0 && exitCode != 0 &&
                exitCode != unchecked((int)0x8A15002B) &&
                exitCode != unchecked((int)0x8A150061))
            {
                var lastMeaningful = ExtractMeaningfulLine(output);
                // Se o winget retornar erro de fonte ou rede, informamos ao usuário
                if (!string.IsNullOrWhiteSpace(lastMeaningful) &&
                    !lastMeaningful.Contains("nenhum pacote", StringComparison.OrdinalIgnoreCase) &&
                    !lastMeaningful.Contains("no installed package", StringComparison.OrdinalIgnoreCase) &&
                    !lastMeaningful.Contains("no applicable upgrade", StringComparison.OrdinalIgnoreCase))
                {
                    return new UpdateCheckResult
                    {
                        Apps = parsed,
                        UnknownVersionCount = unknownCount,
                        WingetVersion = version,
                        ErrorMessage = $"O winget retornou um aviso (0x{exitCode:X8}): {lastMeaningful}"
                    };
                }
            }

            return new UpdateCheckResult
            {
                Apps = parsed,
                UnknownVersionCount = unknownCount,
                WingetVersion = version
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return new UpdateCheckResult
            {
                ErrorMessage = "A verificação via winget demorou demais e foi interrompida. Tente novamente."
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                ErrorMessage = "Erro ao executar o winget: " + ex.Message
            };
        }
    }

    public static async Task<AppInstallResult> InstallAsync(
        UpdatableApp app,
        CancellationToken ct,
        IProgress<string>? progress = null,
        bool interactive = false)
    {
        if (app.IsExcluded || LoadExclusions().ContainsKey(app.WingetId))
            return new AppInstallResult(false, "Bloqueado (marcado como \"Nunca atualizar\").");

        var exe = ResolveWingetPath();
        if (string.IsNullOrEmpty(exe))
            return new AppInstallResult(false, "winget não encontrado.");

        var sbArgs = new StringBuilder();
        var cleanId = app.WingetId.Replace("\"", "").Trim();
        var cleanName = app.Name.Replace("\"", "").Trim();

        if (cleanId.Contains('…'))
        {
            if (!string.IsNullOrWhiteSpace(cleanName) && !cleanName.Contains('…'))
            {
                sbArgs.Append("upgrade --name \"").Append(cleanName).Append("\" --exact");
            }
            else
            {
                sbArgs.Append("upgrade --id \"").Append(cleanId.TrimEnd('…').Trim()).Append('"');
            }
        }
        else
        {
            sbArgs.Append("upgrade --id \"").Append(cleanId).Append("\" --exact");
        }

        if (!string.IsNullOrWhiteSpace(app.Source) &&
            (app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase) ||
             app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)))
        {
            sbArgs.Append(" --source \"").Append(app.Source).Append('"');
        }
        if (!interactive)
            sbArgs.Append(" --silent --disable-interactivity");
        sbArgs.Append(" --accept-package-agreements --accept-source-agreements");

        var psi = new ProcessStartInfo(exe, sbArgs.ToString())
        {
            UseShellExecute = false,
            CreateNoWindow = !interactive,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o winget.");

        var allLines = new List<string>();
        var outputLock = new object();

        Task ReadStreamAsync(StreamReader reader) => Task.Run(async () =>
        {
            var buf = new char[1024];
            var lineBuilder = new StringBuilder();
            while (true)
            {
                int read = await reader.ReadAsync(buf, 0, buf.Length);
                if (read == 0) break;

                for (int i = 0; i < read; i++)
                {
                    char c = buf[i];
                    if (c == '\r' || c == '\n')
                    {
                        if (lineBuilder.Length > 0)
                        {
                            var text = lineBuilder.ToString().Trim();
                            lineBuilder.Clear();
                            if (!string.IsNullOrWhiteSpace(text) && !IsSpinnerOrProgressBar(text))
                            {
                                lock (outputLock) allLines.Add(text);
                                var stage = DetectInstallStage(text);
                                if (stage != null) progress?.Report(stage);
                            }
                        }
                    }
                    else
                    {
                        lineBuilder.Append(c);
                    }
                }
            }
            if (lineBuilder.Length > 0)
            {
                var text = lineBuilder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(text) && !IsSpinnerOrProgressBar(text))
                    lock (outputLock) allLines.Add(text);
            }
        }, CancellationToken.None);

        var outTask = ReadStreamAsync(p.StandardOutput);
        var errTask = ReadStreamAsync(p.StandardError);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await p.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                return new AppInstallResult(false, "Tempo limite excedido (10 min).", "O instalador não respondeu dentro do tempo limite.");
            throw;
        }

        await Task.WhenAll(outTask, errTask);

        string detail;
        lock (outputLock)
        {
            detail = string.Join(Environment.NewLine, allLines.TakeLast(6));
        }

        return p.ExitCode switch
        {
            0 => new AppInstallResult(true, "Atualizado com sucesso.", detail),
            3010 or unchecked((int)0x8A150109) => new AppInstallResult(true, "Atualizado (reinicialização recomendada).", detail, RestartRecommended: true),
            unchecked((int)0x8A15002B) or unchecked((int)0x8A150061) => new AppInstallResult(true, "Já estava atualizado.", detail),
            unchecked((int)0x8A150014) => new AppInstallResult(false, "Pacote não encontrado no winget.", detail),
            unchecked((int)0x8A150008) => new AppInstallResult(false, "Falha no download. Verifique a conexão.", detail),
            unchecked((int)0x8A150010) => new AppInstallResult(false, "Falha ao baixar ou verificar integridade (hash) do instalador.", detail),
            unchecked((int)0x8A150011) or unchecked((int)0x8A150045) or 1618 => new AppInstallResult(false, "Feche o aplicativo ou aguarde outro instalador terminar.", detail),
            unchecked((int)0x8A150056) => new AppInstallResult(false, "Requer permissão de administrador ou modo interativo.", detail),
            unchecked((int)0x8A15010D) or 1602 => new AppInstallResult(false, "Atualização cancelada.", detail),
            unchecked((int)0x8A150107) => new AppInstallResult(false, "O instalador não suporta atualização silenciosa.", detail),
            _ => new AppInstallResult(false, BuildGenericErrorMessage(p.ExitCode, allLines), detail),
        };
    }

    /// <summary>
    /// Instala um novo aplicativo via winget a partir do catálogo (subaba "Instalar aplicações").
    /// </summary>
    public static async Task<AppInstallResult> InstallNewAppAsync(
        string wingetId,
        string name,
        CancellationToken ct,
        IProgress<string>? progress = null,
        bool interactive = false)
    {
        var exe = ResolveWingetPath();
        if (string.IsNullOrEmpty(exe))
            return new AppInstallResult(false, "winget não encontrado.");

        var cleanId = (wingetId ?? "").Replace("\"", "").Trim();
        var cleanName = (name ?? "").Replace("\"", "").Trim();
        if (string.IsNullOrWhiteSpace(cleanId))
            return new AppInstallResult(false, "ID winget inválido.");

        var sbArgs = new StringBuilder();
        sbArgs.Append("install --id \"").Append(cleanId).Append("\" --exact --source winget");
        if (!interactive)
            sbArgs.Append(" --silent --disable-interactivity");
        sbArgs.Append(" --accept-package-agreements --accept-source-agreements");

        var psi = new ProcessStartInfo(exe, sbArgs.ToString())
        {
            UseShellExecute = false,
            CreateNoWindow = !interactive,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o winget.");

        var allLines = new List<string>();
        var outputLock = new object();

        Task ReadStreamAsync(StreamReader reader) => Task.Run(async () =>
        {
            var buf = new char[1024];
            var lineBuilder = new StringBuilder();
            while (true)
            {
                int read = await reader.ReadAsync(buf, 0, buf.Length);
                if (read == 0) break;

                for (int i = 0; i < read; i++)
                {
                    char c = buf[i];
                    if (c == '\r' || c == '\n')
                    {
                        if (lineBuilder.Length > 0)
                        {
                            var text = lineBuilder.ToString().Trim();
                            lineBuilder.Clear();
                            if (!string.IsNullOrWhiteSpace(text) && !IsSpinnerOrProgressBar(text))
                            {
                                lock (outputLock) allLines.Add(text);
                                var stage = DetectInstallStage(text);
                                if (stage != null) progress?.Report(stage);
                            }
                        }
                    }
                    else
                    {
                        lineBuilder.Append(c);
                    }
                }
            }
            if (lineBuilder.Length > 0)
            {
                var text = lineBuilder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(text) && !IsSpinnerOrProgressBar(text))
                    lock (outputLock) allLines.Add(text);
            }
        }, CancellationToken.None);

        var outTask = ReadStreamAsync(p.StandardOutput);
        var errTask = ReadStreamAsync(p.StandardError);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await p.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                return new AppInstallResult(false, "Tempo limite excedido (10 min).", "O instalador não respondeu dentro do tempo limite.");
            throw;
        }

        await Task.WhenAll(outTask, errTask);

        string detail;
        lock (outputLock)
        {
            detail = string.Join(Environment.NewLine, allLines.TakeLast(6));
        }

        return p.ExitCode switch
        {
            0 => new AppInstallResult(true, "Instalado com sucesso.", detail),
            3010 or unchecked((int)0x8A150109) => new AppInstallResult(true, "Instalado (reinicialização recomendada).", detail, RestartRecommended: true),
            unchecked((int)0x8A15002B) or unchecked((int)0x8A150061) => new AppInstallResult(true, "Já está instalado na versão mais recente.", detail),
            unchecked((int)0x8A150014) => new AppInstallResult(false, $"Pacote \"{cleanName}\" não encontrado no winget.", detail),
            unchecked((int)0x8A150008) => new AppInstallResult(false, "Falha no download. Verifique a conexão.", detail),
            unchecked((int)0x8A150010) => new AppInstallResult(false, "Falha ao verificar integridade (hash) do instalador.", detail),
            unchecked((int)0x8A150011) or unchecked((int)0x8A150045) or 1618 => new AppInstallResult(false, "Aguarde outro instalador terminar e tente novamente.", detail),
            unchecked((int)0x8A150056) => new AppInstallResult(false, "Requer permissão de administrador ou modo interativo.", detail),
            unchecked((int)0x8A15010D) or 1602 => new AppInstallResult(false, "Instalação cancelada.", detail),
            unchecked((int)0x8A150107) => new AppInstallResult(false, "O instalador não suporta instalação silenciosa.", detail),
            _ => new AppInstallResult(false, BuildGenericErrorMessage(p.ExitCode, allLines), detail),
        };
    }

    /// <summary>
    /// Fallback local via `winget search` quando a API remota estiver indisponível.
    /// </summary>
    public static async Task<List<CatalogApp>> SearchWingetLocalAsync(string query, CancellationToken ct)
    {
        var results = new List<CatalogApp>();
        var exe = ResolveWingetPath();
        if (string.IsNullOrEmpty(exe) || string.IsNullOrWhiteSpace(query)) return results;

        var safeQuery = query.Replace("\"", "").Trim();
        var (exitCode, output) = await RunWingetCaptureAsync(
            exe,
            $"search \"{safeQuery}\" --source winget --accept-source-agreements --disable-interactivity",
            TimeSpan.FromSeconds(45),
            ct);

        if (exitCode != 0 && string.IsNullOrWhiteSpace(output)) return results;

        var lines = NormalizeTerminalLines(output);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (!IsSeparatorLine(trimmed) || i == 0) continue;

            var colStarts = FindColumnStarts(lines[i - 1]);
            if (colStarts.Count < 3) continue;

            int colName = colStarts[0];
            int colId = colStarts[1];
            int colVer = colStarts[2];
            int colEndVer = colStarts.Count >= 4 ? colStarts[3] : int.MaxValue;

            for (int j = i + 1; j < lines.Count; j++)
            {
                var dataLine = lines[j];
                var dataTrim = dataLine.Trim();
                if (string.IsNullOrWhiteSpace(dataTrim) || IsSeparatorLine(dataTrim) || IsFooterLine(dataTrim, colId))
                    break;

                string name = SliceByVisualColumns(dataLine, colName, colId).Trim();
                string id = SliceByVisualColumns(dataLine, colId, colVer).Trim();
                string ver = SliceByVisualColumns(dataLine, colVer, colEndVer).Trim();

                if (string.IsNullOrWhiteSpace(id) || id.Contains(' ') || !seen.Add(id))
                    continue;

                results.Add(new CatalogApp
                {
                    WingetId = id,
                    Name = string.IsNullOrWhiteSpace(name) ? id : name,
                    Publisher = "winget",
                    Description = "Encontrado via pesquisa local do winget",
                    LatestVersion = ver
                });

                if (results.Count >= 40) break;
            }
            break;
        }

        return results;
    }

    /// <summary>
    /// Retorna os IDs winget já instalados no PC para sinalizar itens no catálogo de instalação.
    /// </summary>
    public static async Task<HashSet<string>> GetInstalledPackageIdsAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exe = ResolveWingetPath();
        if (string.IsNullOrEmpty(exe)) return ids;

        try
        {
            var (_, output) = await RunWingetCaptureAsync(
                exe,
                "list --accept-source-agreements --disable-interactivity",
                TimeSpan.FromSeconds(60),
                ct);

            var lines = NormalizeTerminalLines(output);
            for (int i = 0; i < lines.Count; i++)
            {
                if (!IsSeparatorLine(lines[i].Trim()) || i == 0) continue;
                var colStarts = FindColumnStarts(lines[i - 1]);
                if (colStarts.Count < 3) continue;

                int colId = colStarts[1];
                int colVer = colStarts[2];

                for (int j = i + 1; j < lines.Count; j++)
                {
                    var dataLine = lines[j];
                    var dataTrim = dataLine.Trim();
                    if (string.IsNullOrWhiteSpace(dataTrim) || IsSeparatorLine(dataTrim) || IsFooterLine(dataTrim, colId))
                        break;

                    string id = SliceByVisualColumns(dataLine, colId, colVer).Trim();
                    if (!string.IsNullOrWhiteSpace(id) && !id.Contains(' '))
                        ids.Add(id);
                }
                break;
            }
        }
        catch { }

        return ids;
    }

    public static Dictionary<string, string> LoadExclusions()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Migra silenciosamente da chave antiga (sem barras) se existir
        try
        {
            using var legacy = Registry.CurrentUser.OpenSubKey(LegacyRegistryPath);
            if (legacy != null)
            {
                using var current = Registry.CurrentUser.CreateSubKey(RegistryPath);
                foreach (var name in legacy.GetValueNames())
                {
                    if (legacy.GetValue(name) is string val && !string.IsNullOrWhiteSpace(val))
                    {
                        var id = string.IsNullOrWhiteSpace(name) ? val : name;
                        current.SetValue(id, val);
                    }
                }
                Registry.CurrentUser.DeleteSubKeyTree(LegacyRegistryPath, false);
            }
        }
        catch { }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key != null)
            {
                foreach (var name in key.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var display = key.GetValue(name) as string;
                    map[name] = string.IsNullOrWhiteSpace(display) ? name : display;
                }
            }
        }
        catch { }

        return map;
    }

    public static void SaveExclusion(string wingetId, bool exclude, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(wingetId)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            if (exclude)
                key.SetValue(wingetId, string.IsNullOrWhiteSpace(displayName) ? wingetId : displayName);
            else
                key.DeleteValue(wingetId, false);
        }
        catch { }
    }

    public static void ClearExclusions()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false);
        }
        catch { }
    }

    // ---------------- Parsing da tabela do winget ----------------

    internal static List<UpdatableApp> ParseUpgradeOutput(string rawOutput, out int unknownVersionCount)
    {
        unknownVersionCount = 0;
        var results = new List<UpdatableApp>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = NormalizeTerminalLines(rawOutput);

        bool nextTableRequiresExplicit = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            var mUnknown = UnknownCountRegex.Match(trimmed);
            if (mUnknown.Success && int.TryParse(mUnknown.Groups[1].Value, out int cnt))
                unknownVersionCount = cnt;

            if (trimmed.Contains("explicit targeting", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("direcionamento explícito", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("destino explícito", StringComparison.OrdinalIgnoreCase))
            {
                nextTableRequiresExplicit = true;
            }

            if (!IsSeparatorLine(trimmed) || i == 0)
                continue;

            // A linha imediatamente anterior ao separador "-----" é o cabeçalho das colunas
            var headerLine = lines[i - 1];
            var colStarts = FindColumnStarts(headerLine);
            if (colStarts.Count < 4)
                continue;

            int colName = colStarts[0];
            int colId = colStarts[1];
            int colVersion = colStarts[2];
            int colAvailable = colStarts[3];
            int colSource = colStarts.Count >= 5 ? colStarts[4] : -1;

            // Lê todas as linhas de dados até o próximo bloco vazio / resumo / novo cabeçalho
            for (int j = i + 1; j < lines.Count; j++)
            {
                var dataLine = lines[j];
                var dataTrimmed = dataLine.Trim();

                if (string.IsNullOrWhiteSpace(dataTrimmed))
                {
                    i = j;
                    break;
                }

                // Se chegarmos numa linha de rodapé (ex: "22 atualizações disponíveis.") ou novo separador
                if (IsSeparatorLine(dataTrimmed) || IsFooterLine(dataTrimmed, colId))
                {
                    i = j - 1;
                    break;
                }

                // Checa se a próxima linha é um separador (ou seja, esta linha é o cabeçalho da próxima tabela)
                if (j + 1 < lines.Count && IsSeparatorLine(lines[j + 1].Trim()))
                {
                    i = j - 1;
                    break;
                }

                string name = SliceByVisualColumns(dataLine, colName, colId).Trim();
                string id = SliceByVisualColumns(dataLine, colId, colVersion).Trim();
                string currentVer = SliceByVisualColumns(dataLine, colVersion, colAvailable).Trim();
                string availableVer = (colSource > 0
                    ? SliceByVisualColumns(dataLine, colAvailable, colSource)
                    : SliceByVisualColumns(dataLine, colAvailable, int.MaxValue)).Trim();
                string source = colSource > 0
                    ? SliceByVisualColumns(dataLine, colSource, int.MaxValue).Trim()
                    : "winget";

                // Validação: ID do winget não contém espaços e versão disponível não pode estar vazia
                if (string.IsNullOrWhiteSpace(id) || id.Contains(' ') || string.IsNullOrWhiteSpace(availableVer))
                    continue;

                if (string.IsNullOrWhiteSpace(name))
                    name = id;

                if (!seenIds.Add(id))
                    continue;

                bool isUnknown = currentVer.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                                 currentVer.Equals("Desconhecida", StringComparison.OrdinalIgnoreCase) ||
                                 currentVer.Equals("Desconhecido", StringComparison.OrdinalIgnoreCase);

                results.Add(new UpdatableApp
                {
                    Name = name,
                    WingetId = id,
                    CurrentVersion = currentVer,
                    AvailableVersion = availableVer,
                    Source = string.IsNullOrWhiteSpace(source) ? "winget" : source,
                    IsUnknownVersion = isUnknown,
                    RequiresExplicitTarget = nextTableRequiresExplicit,
                });

                i = j;
            }
        }

        results.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return results;
    }

    private static List<string> NormalizeTerminalLines(string raw)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(raw)) return list;

        var rawLines = raw.Split('\n');
        foreach (var rawLine in rawLines)
        {
            // Primeiro remove o '\r' final da quebra de linha padrão do Windows (\r\n)
            var cleaned = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                list.Add("");
                continue;
            }

            // Se ainda houver '\r' no meio da linha (animação de spinner/progresso na mesma linha),
            // mantém o último segmento válido que não seja spinner ou barra de progresso.
            string effective = cleaned;
            if (cleaned.Contains('\r'))
            {
                var segments = cleaned.Split('\r', StringSplitOptions.RemoveEmptyEntries);
                effective = "";
                for (int k = segments.Length - 1; k >= 0; k--)
                {
                    var segTrim = segments[k].Trim();
                    if (!string.IsNullOrEmpty(segTrim) && !IsSpinnerOrProgressBar(segTrim))
                    {
                        effective = segments[k];
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(effective) || IsSpinnerOrProgressBar(effective.Trim()))
                continue;

            list.Add(effective);
        }
        return list;
    }

    private static bool IsSeparatorLine(string trimmed)
    {
        if (trimmed.Length < 20) return false;
        foreach (char c in trimmed)
        {
            if (c != '-') return false;
        }
        return true;
    }

    private static bool IsFooterLine(string trimmed, int idColStart)
    {
        if (char.IsDigit(trimmed[0]) &&
            (trimmed.Contains("atualiza", StringComparison.OrdinalIgnoreCase) ||
             trimmed.Contains("upgrade", StringComparison.OrdinalIgnoreCase) ||
             trimmed.Contains("pacote", StringComparison.OrdinalIgnoreCase) ||
             trimmed.Contains("package", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (GetVisualWidth(trimmed) < idColStart)
            return true;

        return false;
    }

    private static bool IsSpinnerOrProgressBar(string trimmed)
    {
        if (string.IsNullOrEmpty(trimmed)) return false;
        if (trimmed.Length <= 2 && (trimmed == "-" || trimmed == "\\" || trimmed == "|" || trimmed == "/"))
            return true;
        if (trimmed.Contains('█') || trimmed.Contains('▒') || trimmed.Contains('▓'))
            return true;
        return false;
    }

    private static List<int> FindColumnStarts(string headerLine)
    {
        // Tenta primeiro detectar colunas separadas por 2+ espaços (suporta títulos compostos como "Versão disponível")
        var starts = FindColumnStartsByMinSpaces(headerLine, minSpaces: 2);
        if (starts.Count >= 4)
            return starts;

        return FindColumnStartsByMinSpaces(headerLine, minSpaces: 1);
    }

    private static List<int> FindColumnStartsByMinSpaces(string headerLine, int minSpaces)
    {
        var starts = new List<int>();
        int visualCol = 0;
        bool inColumn = false;
        int spaceRun = minSpaces;

        foreach (var rune in headerLine.EnumerateRunes())
        {
            bool isSpace = Rune.IsWhiteSpace(rune);
            int width = GetRuneDisplayWidth(rune);

            if (!isSpace)
            {
                if (!inColumn && spaceRun >= minSpaces)
                {
                    starts.Add(visualCol);
                    inColumn = true;
                }
                spaceRun = 0;
            }
            else
            {
                spaceRun += width;
                if (spaceRun >= minSpaces)
                    inColumn = false;
            }

            visualCol += width;
        }

        return starts;
    }

    private static string SliceByVisualColumns(string line, int startCol, int endCol)
    {
        var sb = new StringBuilder();
        int col = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            int w = GetRuneDisplayWidth(rune);
            if (col >= startCol && col < endCol)
                sb.Append(rune.ToString());
            col += w;
            if (col >= endCol) break;
        }
        return sb.ToString();
    }

    private static int GetVisualWidth(string text)
    {
        int w = 0;
        foreach (var rune in text.EnumerateRunes())
            w += GetRuneDisplayWidth(rune);
        return w;
    }

    private static int GetRuneDisplayWidth(Rune rune)
    {
        int v = rune.Value;
        // Caracteres East Asian Wide / Fullwidth ocupam 2 colunas no console Windows
        if ((v >= 0x1100 && v <= 0x115F) ||
            (v >= 0x2E80 && v <= 0xA4CF) ||
            (v >= 0xAC00 && v <= 0xD7A3) ||
            (v >= 0xF900 && v <= 0xFAFF) ||
            (v >= 0xFE10 && v <= 0xFE19) ||
            (v >= 0xFE30 && v <= 0xFE6F) ||
            (v >= 0xFF00 && v <= 0xFF60) ||
            (v >= 0xFFE0 && v <= 0xFFE6) ||
            (v >= 0x20000 && v <= 0x2FFFD))
            return 2;
        return 1;
    }

    // ---------------- Helpers de execução e diagnóstico ----------------

    private static async Task<(int ExitCode, string Output)> RunWingetCaptureAsync(
        string exe, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o winget.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await p.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new TimeoutException("Tempo limite excedido ao executar o winget.");
            throw;
        }

        var stdout = await outTask;
        var stderr = await errTask;
        return (p.ExitCode, string.IsNullOrEmpty(stderr) ? stdout : stdout + "\n" + stderr);
    }

    private static string? DetectInstallStage(string line)
    {
        if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Baixando", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Transferindo", StringComparison.OrdinalIgnoreCase))
            return "Baixando instalador…";

        if (line.Contains("hash", StringComparison.OrdinalIgnoreCase))
            return "Verificando integridade…";

        if (line.Contains("Starting package install", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Iniciando a instalação", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Installing", StringComparison.OrdinalIgnoreCase))
            return "Instalando atualização…";

        if (line.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Instalado com êxito", StringComparison.OrdinalIgnoreCase))
            return "Concluindo…";

        return null;
    }

    private static string ExtractMeaningfulLine(string rawOutput)
    {
        var lines = NormalizeTerminalLines(rawOutput);
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var t = lines[i].Trim();
            if (!string.IsNullOrWhiteSpace(t) && !IsSeparatorLine(t))
                return t;
        }
        return "";
    }

    private static string BuildGenericErrorMessage(int exitCode, List<string> lines)
    {
        var last = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (!string.IsNullOrWhiteSpace(last) && last.Length <= 110)
            return $"{last} (0x{exitCode:X8})";
        return $"Falhou (código 0x{exitCode:X8}).";
    }
}
