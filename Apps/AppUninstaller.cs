using System;
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

public enum LeftoverKind
{
    Folder,
    File,
    Shortcut,
    RegistryKey
}

public sealed class InstalledProgram : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string InstallDate { get; init; } = "";
    public long EstimatedSizeKb { get; init; }
    public string InstallLocation { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public string RegistrySubKey { get; init; } = "";
    public RegistryHive RegistryHive { get; init; } = RegistryHive.LocalMachine;
    public RegistryView RegistryView { get; init; } = RegistryView.Registry64;
    public string PackageFullName { get; init; } = "";
    public bool IsStoreApp { get; init; }
    public bool IsSystemCritical { get; init; }
    public string DangerReason { get; init; } = "";

    private string _status = "";
    private bool _isRemoved;

    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; OnChanged(); OnChanged(nameof(HasStatus)); }
    }

    public bool IsRemoved
    {
        get => _isRemoved;
        set { if (_isRemoved == value) return; _isRemoved = value; OnChanged(); OnChanged(nameof(CanUninstall)); }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(_status);
    public bool CanUninstall => !IsRemoved;
    public bool HasVersion => !string.IsNullOrWhiteSpace(Version);
    public string PublisherDisplay => string.IsNullOrWhiteSpace(Publisher) ? (IsStoreApp ? "Microsoft Store" : "Desconhecido") : Publisher;
    public string SourceDisplay => IsStoreApp ? "Store / UWP" : "Programa";
    public string SizeDisplay => EstimatedSizeKb switch
    {
        >= 1024 * 1024 => $"{EstimatedSizeKb / (1024.0 * 1024.0):0.0} GB",
        >= 1024 => $"{EstimatedSizeKb / 1024.0:0.0} MB",
        > 0 => $"{EstimatedSizeKb} KB",
        _ => "—"
    };

    public string Initials
    {
        get
        {
            var words = (Name ?? "?").Split(new[] { ' ', '-', '.', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "?";
            if (words.Length == 1) return words[0].Substring(0, Math.Min(2, words[0].Length)).ToUpperInvariant();
            return (words[0][0].ToString() + words[1][0]).ToUpperInvariant();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed class LeftoverItem : INotifyPropertyChanged
{
    public LeftoverKind Kind { get; init; }
    public string Path { get; init; } = "";
    public string Detail { get; init; } = "";
    public long SizeBytes { get; init; }
    public RegistryHive RegistryHive { get; init; }
    public RegistryView RegistryView { get; init; }
    public string RegistrySubKey { get; init; } = "";

    private bool _selected = true;
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; OnChanged(); }
    }

    public string KindLabel => Kind switch
    {
        LeftoverKind.Folder => "PASTA",
        LeftoverKind.File => "ARQUIVO",
        LeftoverKind.Shortcut => "ATALHO",
        LeftoverKind.RegistryKey => "REGISTRO",
        _ => "ITEM"
    };

    public string KindIcon => Kind switch
    {
        LeftoverKind.Folder => "\uE8B7",
        LeftoverKind.File => "\uE8A5",
        LeftoverKind.Shortcut => "\uE71B",
        LeftoverKind.RegistryKey => "\uE90F",
        _ => "\uE7BA"
    };

    public string SizeText => Kind == LeftoverKind.RegistryKey
        ? "Chave de registro"
        : SizeBytes switch
        {
            >= 1024L * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024 * 1024):0.00} GB",
            >= 1024L * 1024 => $"{SizeBytes / (1024.0 * 1024):0.0} MB",
            >= 1024L => $"{SizeBytes / 1024.0:0.0} KB",
            > 0 => $"{SizeBytes} B",
            _ => "0 B"
        };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed record LeftoverCleanResult(int DeletedCount, int FailedCount, long FreedBytes, List<string> Errors);

/// <summary>
/// Motor de desinstalação estilo Revo Uninstaller:
/// - Lista programas Win32 (64/32-bit, HKLM/HKCU) e apps Microsoft Store (Appx)
/// - Classifica componentes críticos/perigosos do sistema
/// - Executa desinstalação padrão ou forçada (encerrando processos)
/// - Varre e limpa resíduos (pastas, arquivos, atalhos e chaves de registro) com travas de segurança
/// </summary>
public static class AppUninstaller
{
    private static readonly string[] CriticalNameKeywords =
    {
        "microsoft visual c++", "visual c++", "windows sdk", "windows desktop runtime",
        ".net runtime", ".net desktop runtime", ".net sdk", "asp.net core",
        "microsoft edge webview2", "microsoft edge", "windows security", "windows defender",
        "nvidia graphics driver", "nvidia physx", "nvidia hd audio", "nvidia frameview",
        "amd software", "amd chipset", "intel(r) graphics", "intel(r) chipset",
        "intel(r) management engine", "intel(r) rapid storage", "realtek high definition audio",
        "realtek ethernet", "realtek wireless", "qualcomm", "killer network",
        "directx", "vulkan runtime", "openal", "gaming services", "microsoft store",
        "windows terminal", "app installer", "desktop app installer", "microsoft.ui.xaml",
        "microsoft.vclibs", "microsoft.net.native", "windowsapp", "hevc video",
        "xbox identity", "winget", "hypercore"
    };

    private static readonly string[] CriticalPackagePrefixes =
    {
        "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI",
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.Search", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl",
        "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET.Native",
        "Microsoft.Services.Store", "Microsoft.GamingServices", "Microsoft.XboxIdentityProvider",
        "Microsoft.WebMediaExtensions", "Microsoft.WindowsTerminal", "Microsoft.Edge"
    };

    // Pastas protegidas que NUNCA podem ser sugeridas nem apagadas na varredura de resíduos
    private static readonly HashSet<string> ProtectedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "System32", "SysWOW64", "Program Files", "Program Files (x86)", "ProgramData",
        "Users", "AppData", "Roaming", "Local", "LocalLow", "Temp", "Microsoft", "Common Files",
        "WindowsApps", "Packages", "Start Menu", "Programs", "Desktop", "Documents", "Downloads",
        "Intel", "AMD", "NVIDIA", "NVIDIA Corporation", "Drivers", "dotnet", "Reference Assemblies",
        "Package Cache", "WinSxS", "Boot", "Recovery", "PerfLogs", "Google", "Mozilla", "Adobe",
        "Steam", "Epic Games", "Riot Games", "Electronic Arts", "Ubisoft"
    };

    private static readonly HashSet<string> ProtectedRegistryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Classes", "Policies", "Clients", "RegisteredApplications",
        "WOW6432Node", "ODBC", "Intel", "AMD", "NVIDIA Corporation", "Google", "Mozilla"
    };

    public static async Task<List<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken ct)
    {
        return await Task.Run(async () =>
        {
            var list = new List<InstalledProgram>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Programas Win32 (HKLM 64, HKLM 32, HKCU 64)
            ReadUninstallRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, list, seen);
            ReadUninstallRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, list, seen);
            ReadUninstallRegistry(RegistryHive.CurrentUser, RegistryView.Registry64, list, seen);

            // 2. Aplicativos Microsoft Store / UWP (via PowerShell Get-AppxPackage)
            var storeApps = await ReadStoreAppsAsync(ct);
            foreach (var app in storeApps)
            {
                if (seen.Add("APPX:" + app.PackageFullName))
                    list.Add(app);
            }

            list.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            return list;
        }, ct);
    }

    private static void ReadUninstallRegistry(
        RegistryHive hive,
        RegistryView view,
        List<InstalledProgram> target,
        HashSet<string> seen)
    {
        const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(uninstallPath);
            if (uninstallKey == null) return;

            foreach (var subName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var sub = uninstallKey.OpenSubKey(subName);
                    if (sub == null) continue;

                    var name = (sub.GetValue("DisplayName") as string)?.Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Ignora atualizações de segurança KB / patches internos
                    if (!string.IsNullOrWhiteSpace(sub.GetValue("ParentKeyName") as string))
                        continue;

                    var uninstallStr = (sub.GetValue("UninstallString") as string)?.Trim() ?? "";
                    var quietStr = (sub.GetValue("QuietUninstallString") as string)?.Trim() ?? "";
                    var sysComp = sub.GetValue("SystemComponent") is int sc && sc == 1;
                    var noRemove = sub.GetValue("NoRemove") is int nr && nr == 1;

                    if (string.IsNullOrWhiteSpace(uninstallStr) && string.IsNullOrWhiteSpace(quietStr) && !sysComp)
                        continue;

                    var publisher = (sub.GetValue("Publisher") as string)?.Trim() ?? "";
                    var version = (sub.GetValue("DisplayVersion") as string)?.Trim() ?? "";
                    var installLoc = (sub.GetValue("InstallLocation") as string)?.Trim().Trim('"') ?? "";
                    var installDate = (sub.GetValue("InstallDate") as string)?.Trim() ?? "";
                    long sizeKb = 0;
                    if (sub.GetValue("EstimatedSize") is int sz && sz > 0) sizeKb = sz;

                    var dedupKey = $"{name}|{version}|{publisher}";
                    if (!seen.Add(dedupKey)) continue;

                    var (isCritical, dangerReason) = EvaluateDanger(name, publisher, sysComp || noRemove, isStore: false, packageFullName: "");

                    target.Add(new InstalledProgram
                    {
                        Id = $"{hive}:{view}:{subName}",
                        Name = name,
                        Publisher = publisher,
                        Version = version,
                        InstallDate = FormatInstallDate(installDate),
                        EstimatedSizeKb = sizeKb,
                        InstallLocation = installLoc,
                        UninstallString = uninstallStr,
                        QuietUninstallString = quietStr,
                        RegistrySubKey = $@"{uninstallPath}\{subName}",
                        RegistryHive = hive,
                        RegistryView = view,
                        IsStoreApp = false,
                        IsSystemCritical = isCritical,
                        DangerReason = dangerReason
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    private static async Task<List<InstalledProgram>> ReadStoreAppsAsync(CancellationToken ct)
    {
        var list = new List<InstalledProgram>();
        try
        {
            const string psScript =
                "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage } | " +
                "ForEach-Object { \"$($_.Name)`t$($_.PackageFullName)`t$($_.Version)`t$($_.InstallLocation)`t$($_.NonRemovable)`t$($_.SignatureKind)\" }";

            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{psScript}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var p = Process.Start(psi);
            if (p == null) return list;

            var outputTask = p.StandardOutput.ReadToEndAsync(ct);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            try { await p.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return list; }

            var output = await outputTask;
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 6) continue;

                string pkgName = parts[0].Trim();
                string fullName = parts[1].Trim();
                string ver = parts[2].Trim();
                string loc = parts[3].Trim();
                bool nonRemovable = parts[4].Equals("True", StringComparison.OrdinalIgnoreCase);
                string sigKind = parts[5].Trim();

                if (string.IsNullOrWhiteSpace(pkgName) || string.IsNullOrWhiteSpace(fullName))
                    continue;

                string friendly = FormatAppxFriendlyName(pkgName);
                bool isSystemSig = sigKind.Equals("System", StringComparison.OrdinalIgnoreCase);

                var (isCritical, dangerReason) = EvaluateDanger(friendly, "Microsoft Store", nonRemovable || isSystemSig, isStore: true, packageFullName: fullName);

                list.Add(new InstalledProgram
                {
                    Id = "APPX:" + fullName,
                    Name = friendly,
                    Publisher = pkgName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ? "Microsoft Corporation" : "Microsoft Store",
                    Version = ver,
                    InstallLocation = loc,
                    PackageFullName = fullName,
                    IsStoreApp = true,
                    IsSystemCritical = isCritical,
                    DangerReason = dangerReason
                });
            }
        }
        catch { }

        return list;
    }

    private static (bool IsCritical, string Reason) EvaluateDanger(
        string name,
        string publisher,
        bool flaggedByWindows,
        bool isStore,
        string packageFullName)
    {
        var lowerName = name.ToLowerInvariant();
        var lowerPub = publisher.ToLowerInvariant();

        if (isStore && CriticalPackagePrefixes.Any(p => packageFullName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return (true, "Componente essencial do Windows / Microsoft Store. Remover pode quebrar recursos do sistema.");

        foreach (var kw in CriticalNameKeywords)
        {
            if (lowerName.Contains(kw))
            {
                if (kw.Contains("driver") || kw.Contains("nvidia") || kw.Contains("amd") || kw.Contains("intel") || kw.Contains("realtek"))
                    return (true, "Driver de hardware essencial. Desinstalar pode remover vídeo, som ou rede do PC.");
                if (kw.Contains("visual c++") || kw.Contains(".net") || kw.Contains("directx") || kw.Contains("vulkan") || kw.Contains("webview2"))
                    return (true, "Biblioteca/Runtime necessária para jogos e programas funcionarem corretamente.");
                if (kw == "hypercore")
                    return (true, "Este é o próprio HyperCore que está em execução agora.");
                return (true, "Componente importante do sistema Windows. Desinstalar pode causar instabilidade.");
            }
        }

        if (flaggedByWindows)
            return (true, "Marcado pelo Windows como componente protegido do sistema.");

        if (lowerPub.Contains("microsoft") && (lowerName.Contains("runtime") || lowerName.Contains("driver") || lowerName.Contains("update") || lowerName.Contains("security")))
            return (true, "Componente da Microsoft necessário para estabilidade e compatibilidade de programas.");

        return (false, "");
    }

    // ---------------- Desinstalação padrão ----------------

    public static async Task<(bool Ok, string Message)> RunStandardUninstallAsync(InstalledProgram app, CancellationToken ct)
    {
        if (app.IsStoreApp)
        {
            if (string.IsNullOrWhiteSpace(app.PackageFullName))
                return (false, "Identificador de pacote Store ausente.");

            var safePkg = app.PackageFullName.Replace("'", "''");
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '{safePkg}' -ErrorAction Stop\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o PowerShell.");
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var err = (await errTask).Trim();

            return p.ExitCode == 0
                ? (true, "Aplicativo da Store desinstalado com sucesso.")
                : (false, string.IsNullOrWhiteSpace(err) ? $"Falhou (código {p.ExitCode})." : err.Split('\n')[0].Trim());
        }

        var cmd = !string.IsNullOrWhiteSpace(app.UninstallString) ? app.UninstallString : app.QuietUninstallString;
        if (string.IsNullOrWhiteSpace(cmd))
            return (false, "Este programa não possui comando de desinstalação registrado. Use \"Forçar desinstalação\".");

        // Converte MsiExec.exe /I{GUID} para /X{GUID}
        var msiMatch = Regex.Match(cmd, @"msiexec(?:\.exe)?\s+/[iIxX]\s*(\{[0-9A-Fa-f\-]+\})", RegexOptions.IgnoreCase);
        ProcessStartInfo startInfo;
        if (msiMatch.Success)
        {
            startInfo = new ProcessStartInfo("msiexec.exe", $"/x {msiMatch.Groups[1].Value}")
            {
                UseShellExecute = true
            };
        }
        else
        {
            var (file, args) = SplitCommandLine(cmd);
            if (string.IsNullOrWhiteSpace(file))
                return (false, "Comando de desinstalação inválido. Use \"Forçar desinstalação\".");

            startInfo = new ProcessStartInfo(file, args)
            {
                UseShellExecute = true
            };
        }

        try
        {
            using var proc = Process.Start(startInfo);
            if (proc == null)
                return (false, "Não foi possível iniciar o desinstalador.");

            await proc.WaitForExitAsync(ct);
            return proc.ExitCode switch
            {
                0 or 3010 => (true, "Desinstalador concluído. Agora você pode verificar e limpar os resíduos."),
                1602 or 1223 => (false, "Desinstalação cancelada pelo usuário."),
                _ => (true, $"Desinstalador encerrado (código {proc.ExitCode}). Verifique os resíduos abaixo.")
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, "Erro ao executar desinstalador: " + ex.Message);
        }
    }

    // ---------------- Varredura de resíduos (estilo Revo Uninstaller) ----------------

    public static async Task<List<LeftoverItem>> ScanLeftoversAsync(
        InstalledProgram app,
        bool forceMode,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var items = new List<LeftoverItem>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Pasta de instalação oficial (se ainda existir após desinstalar ou em modo Forçado)
            if (!string.IsNullOrWhiteSpace(app.InstallLocation) &&
                Directory.Exists(app.InstallLocation) &&
                IsSafeFolderToDelete(app.InstallLocation))
            {
                if (seenPaths.Add(app.InstallLocation))
                {
                    items.Add(new LeftoverItem
                    {
                        Kind = LeftoverKind.Folder,
                        Path = app.InstallLocation,
                        Detail = forceMode ? "Pasta principal do aplicativo (remoção forçada)" : "Pasta de instalação residual",
                        SizeBytes = SafeDirectorySize(app.InstallLocation, ct)
                    });
                }
            }

            // Monta termos de busca seguros para varrer AppData / ProgramData / Program Files / Registro
            var searchTokens = BuildSafeSearchTokens(app.Name, app.Publisher);

            // 2. Varre pastas de dados do usuário e do sistema (AppData\Roaming, AppData\Local, ProgramData, Program Files)
            var scanRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            }.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var root in scanRoots)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        ct.ThrowIfCancellationRequested();
                        var dirName = Path.GetFileName(dir);
                        if (string.IsNullOrWhiteSpace(dirName) || ProtectedFolderNames.Contains(dirName))
                            continue;

                        if (MatchesAnyToken(dirName, searchTokens) && IsSafeFolderToDelete(dir) && seenPaths.Add(dir))
                        {
                            items.Add(new LeftoverItem
                            {
                                Kind = LeftoverKind.Folder,
                                Path = dir,
                                Detail = $"Resíduo em {Path.GetFileName(root)}",
                                SizeBytes = SafeDirectorySize(dir, ct)
                            });
                        }
                        else if (!string.IsNullOrWhiteSpace(app.Publisher) &&
                                 dirName.Equals(app.Publisher, StringComparison.OrdinalIgnoreCase))
                        {
                            // Verifica subpastas dentro da pasta do fabricante (ex: AppData\Roaming\Fabricante\App)
                            try
                            {
                                foreach (var subDir in Directory.EnumerateDirectories(dir))
                                {
                                    var subName = Path.GetFileName(subDir);
                                    if (!string.IsNullOrWhiteSpace(subName) &&
                                        !ProtectedFolderNames.Contains(subName) &&
                                        MatchesAnyToken(subName, searchTokens) &&
                                        IsSafeFolderToDelete(subDir) &&
                                        seenPaths.Add(subDir))
                                    {
                                        items.Add(new LeftoverItem
                                        {
                                            Kind = LeftoverKind.Folder,
                                            Path = subDir,
                                            Detail = $"Resíduo em {dirName}",
                                            SizeBytes = SafeDirectorySize(subDir, ct)
                                        });
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            // 3. Varre atalhos (.lnk) na Área de Trabalho e Menu Iniciar
            var shortcutDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            }.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var sDir in shortcutDirs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    foreach (var lnk in Directory.EnumerateFiles(sDir, "*.lnk", SearchOption.AllDirectories))
                    {
                        var lnkName = Path.GetFileNameWithoutExtension(lnk);
                        if (MatchesAnyToken(lnkName, searchTokens) && seenPaths.Add(lnk))
                        {
                            long sz = 0;
                            try { sz = new FileInfo(lnk).Length; } catch { }
                            items.Add(new LeftoverItem
                            {
                                Kind = LeftoverKind.Shortcut,
                                Path = lnk,
                                Detail = "Atalho residual",
                                SizeBytes = sz
                            });
                        }
                    }
                }
                catch { }
            }

            // 4. Chave de Registro de Desinstalação (se ainda existir após desinstalar ou em modo Forçado)
            if (!app.IsStoreApp && !string.IsNullOrWhiteSpace(app.RegistrySubKey))
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(app.RegistryHive, app.RegistryView);
                    using var existing = baseKey.OpenSubKey(app.RegistrySubKey);
                    if (existing != null)
                    {
                        var regDisplay = $"{HiveShort(app.RegistryHive)}\\{app.RegistrySubKey}";
                        if (seenPaths.Add("REG:" + regDisplay))
                        {
                            items.Add(new LeftoverItem
                            {
                                Kind = LeftoverKind.RegistryKey,
                                Path = regDisplay,
                                Detail = forceMode ? "Entrada de desinstalação (remoção forçada)" : "Entrada órfã em Programas e Recursos",
                                RegistryHive = app.RegistryHive,
                                RegistryView = app.RegistryView,
                                RegistrySubKey = app.RegistrySubKey
                            });
                        }
                    }
                }
                catch { }
            }

            // 5. Chaves residuais em HKCU\Software e HKLM\SOFTWARE
            ScanSoftwareRegistry(RegistryHive.CurrentUser, RegistryView.Registry64, searchTokens, app.Publisher, items, seenPaths);
            ScanSoftwareRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, searchTokens, app.Publisher, items, seenPaths);
            ScanSoftwareRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, searchTokens, app.Publisher, items, seenPaths);

            return items;
        }, ct);
    }

    public static async Task<LeftoverCleanResult> DeleteLeftoversAsync(
        List<LeftoverItem> items,
        InstalledProgram? app,
        bool killRunningProcesses,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            int deleted = 0;
            int failed = 0;
            long freed = 0;
            var errors = new List<string>();

            // Se modo forçado, encerra processos que estejam rodando a partir da pasta do aplicativo
            if (killRunningProcesses && app != null && !string.IsNullOrWhiteSpace(app.InstallLocation))
            {
                KillProcessesInDirectory(app.InstallLocation);
            }

            foreach (var item in items.Where(i => i.Selected))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    switch (item.Kind)
                    {
                        case LeftoverKind.Folder:
                            if (!IsSafeFolderToDelete(item.Path))
                            {
                                failed++;
                                errors.Add($"Bloqueado por segurança: {item.Path}");
                                continue;
                            }
                            if (Directory.Exists(item.Path))
                            {
                                if (killRunningProcesses) KillProcessesInDirectory(item.Path);
                                Directory.Delete(item.Path, recursive: true);
                                freed += item.SizeBytes;
                                deleted++;
                            }
                            break;

                        case LeftoverKind.File:
                        case LeftoverKind.Shortcut:
                            if (File.Exists(item.Path))
                            {
                                File.SetAttributes(item.Path, FileAttributes.Normal);
                                File.Delete(item.Path);
                                freed += item.SizeBytes;
                                deleted++;
                            }
                            break;

                        case LeftoverKind.RegistryKey:
                            if (!string.IsNullOrWhiteSpace(item.RegistrySubKey))
                            {
                                using var baseKey = RegistryKey.OpenBaseKey(item.RegistryHive, item.RegistryView);
                                baseKey.DeleteSubKeyTree(item.RegistrySubKey, throwOnMissingSubKey: false);
                                deleted++;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{item.Path}: {ex.Message}");
                }
            }

            return new LeftoverCleanResult(deleted, failed, freed, errors);
        }, ct);
    }

    // ---------------- Helpers de varredura e segurança ----------------

    private static void ScanSoftwareRegistry(
        RegistryHive hive,
        RegistryView view,
        List<string> tokens,
        string publisher,
        List<LeftoverItem> items,
        HashSet<string> seen)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var sw = baseKey.OpenSubKey("Software");
            if (sw == null) return;

            foreach (var name in sw.GetSubKeyNames())
            {
                if (string.IsNullOrWhiteSpace(name) || ProtectedRegistryNames.Contains(name))
                    continue;

                if (MatchesAnyToken(name, tokens))
                {
                    var subPath = $@"Software\{name}";
                    var disp = $"{HiveShort(hive)}\\{subPath}";
                    if (seen.Add("REG:" + disp))
                    {
                        items.Add(new LeftoverItem
                        {
                            Kind = LeftoverKind.RegistryKey,
                            Path = disp,
                            Detail = "Chave residual de configurações",
                            RegistryHive = hive,
                            RegistryView = view,
                            RegistrySubKey = subPath
                        });
                    }
                }
                else if (!string.IsNullOrWhiteSpace(publisher) &&
                         name.Equals(publisher, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var pubKey = sw.OpenSubKey(name);
                        if (pubKey == null) continue;
                        foreach (var sub in pubKey.GetSubKeyNames())
                        {
                            if (!string.IsNullOrWhiteSpace(sub) &&
                                !ProtectedRegistryNames.Contains(sub) &&
                                MatchesAnyToken(sub, tokens))
                            {
                                var subPath = $@"Software\{name}\{sub}";
                                var disp = $"{HiveShort(hive)}\\{subPath}";
                                if (seen.Add("REG:" + disp))
                                {
                                    items.Add(new LeftoverItem
                                    {
                                        Kind = LeftoverKind.RegistryKey,
                                        Path = disp,
                                        Detail = $"Chave residual em {name}",
                                        RegistryHive = hive,
                                        RegistryView = view,
                                        RegistrySubKey = subPath
                                    });
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static List<string> BuildSafeSearchTokens(string appName, string publisher)
    {
        var tokens = new List<string>();
        var cleaned = Regex.Replace(appName ?? "", @"\s*\([^)]*\)|\s+\d+(\.\d+)*.*$", "").Trim();
        if (cleaned.Length >= 4 && !ProtectedFolderNames.Contains(cleaned))
            tokens.Add(cleaned);

        var raw = (appName ?? "").Trim();
        if (raw.Length >= 4 && !tokens.Contains(raw, StringComparer.OrdinalIgnoreCase) && !ProtectedFolderNames.Contains(raw))
            tokens.Add(raw);

        // Versão sem espaços (ex: "Visual Studio Code" -> "VisualStudioCode")
        var compact = cleaned.Replace(" ", "");
        if (compact.Length >= 5 && !tokens.Contains(compact, StringComparer.OrdinalIgnoreCase) && !ProtectedFolderNames.Contains(compact))
            tokens.Add(compact);

        return tokens;
    }

    private static bool MatchesAnyToken(string candidate, List<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(candidate) || tokens.Count == 0) return false;
        foreach (var t in tokens)
        {
            if (candidate.Equals(t, StringComparison.OrdinalIgnoreCase))
                return true;
            if (t.Length >= 6 && candidate.Contains(t, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsSafeFolderToDelete(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "";
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return false;

            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(winDir) && full.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                return false;

            var name = Path.GetFileName(full);
            if (string.IsNullOrWhiteSpace(name) || ProtectedFolderNames.Contains(name))
                return false;

            // Exige pelo menos 2 níveis abaixo da raiz (ex: C:\Program Files\AppName, nunca C:\Program Files)
            var parts = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;

            return true;
        }
        catch { return false; }
    }

    private static long SafeDirectorySize(string dir, CancellationToken ct)
    {
        long total = 0;
        int fileLimit = 3000;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (--fileLimit <= 0 || ct.IsCancellationRequested) break;
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    private static void KillProcessesInDirectory(string dir)
    {
        try
        {
            var normDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var currentPid = Environment.ProcessId;
            foreach (var p in Process.GetProcesses())
            {
                if (p.Id == currentPid) continue;
                try
                {
                    var exePath = p.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exePath) &&
                        exePath.StartsWith(normDir, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(3000);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

    private static (string File, string Args) SplitCommandLine(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int endQuote = cmd.IndexOf('"', 1);
            if (endQuote > 1)
                return (cmd[1..endQuote], cmd[(endQuote + 1)..].Trim());
        }

        // Procura ".exe" no caminho não-aspado
        int exeIdx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
        {
            int splitPos = exeIdx + 4;
            return (cmd[..splitPos].Trim(), cmd[splitPos..].Trim());
        }

        int space = cmd.IndexOf(' ');
        return space > 0 ? (cmd[..space], cmd[(space + 1)..].Trim()) : (cmd, "");
    }

    private static string FormatAppxFriendlyName(string pkgName)
    {
        var s = pkgName;
        if (s.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) s = s["Microsoft.".Length..];
        else if (s.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase)) s = s["Windows.".Length..];
        s = s.Replace(".", " ").Replace("_", " ");
        return Regex.Replace(s, "([a-z])([A-Z])", "$1 $2").Trim();
    }

    private static string FormatInstallDate(string raw)
    {
        if (raw.Length == 8 &&
            int.TryParse(raw[..4], out int y) &&
            int.TryParse(raw[4..6], out int m) &&
            int.TryParse(raw[6..8], out int d))
            return $"{d:00}/{m:00}/{y:0000}";
        return "";
    }

    private static string HiveShort(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        _ => hive.ToString()
    };
}
