using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrameForge.Core;
using Microsoft.Win32;

namespace FrameForge.Games;

public sealed record AntiCheatInfo(string Name, bool Kernel, string Notes);

public sealed class LibGame
{
    public string Key { get; init; } = "";         // "steam:730", "riot:valorant", "manual:C:\...exe"
    public string Name { get; init; } = "";
    public string Store { get; init; } = "";
    public string InstallDir { get; init; } = "";
    public string? ExePath { get; init; }
    public string? LaunchUri { get; init; }        // steam://, com.epicgames.launcher://, uplay://
    public string? LaunchExe { get; init; }        // ex.: RiotClientServices.exe
    public string? LaunchArgs { get; init; }
    public HashSet<string> ProcessNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public AntiCheatInfo? AntiCheat { get; set; }
    public string? GuessedExe { get; set; }       // primeiro .exe "a sério" na pasta (para o ícone)
    public string IconSource => ExePath ?? GuessedExe ?? LaunchExe ?? "";
    public string StoreBadge => Store.ToUpperInvariant();
    public string AntiCheatBadge => AntiCheat == null ? "" : (AntiCheat.Kernel ? "🛡 " : "") + AntiCheat.Name;
    public override string ToString() => Name;
}

/// <summary>Deteta jogos instalados lendo os ficheiros/registo das lojas (nada é executado nem alterado).</summary>
public static class GameLibrary
{
    public static List<LibGame> Scan(AppSettings settings)
    {
        var list = new List<LibGame>();
        Try(() => Steam(list));
        Try(() => Epic(list));
        Try(() => Riot(list));
        Try(() => Gog(list));
        Try(() => Ubisoft(list));
        Try(() => Uninstall(list));
        foreach (var m in settings.ManualGames.Where(m => File.Exists(m.ExePath)))
            list.Add(Manual(m));
        foreach (var g in list) { AddProcessNames(g); g.AntiCheat ??= AntiCheatDb.Detect(g); }
        return list.GroupBy(g => g.Key).Select(g => g.First()).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void Try(Action a) { try { a(); } catch { } }

    public static LibGame Manual(ManualGame m)
    {
        var g = new LibGame
        {
            Key = "manual:" + m.ExePath.ToLowerInvariant(), Name = m.Name, Store = "manual",
            InstallDir = Path.GetDirectoryName(m.ExePath) ?? "", ExePath = m.ExePath, LaunchArgs = m.Args,
        };
        g.ProcessNames.Add(Path.GetFileNameWithoutExtension(m.ExePath));
        return g;
    }

    // ---------------- Steam ----------------
    private static readonly HashSet<string> SteamTools = new() { "228980", "1070560", "1391110", "1493710", "2180100", "961940", "1826330", "250820", "1628350" };

    private static void Steam(List<LibGame> list)
    {
        string? steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (steam == null || !Directory.Exists(steam)) return;
        var libs = new List<string> { Path.Combine(steam, "steamapps") };
        string lf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(lf))
            foreach (Match m in Regex.Matches(File.ReadAllText(lf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps"));

        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
            foreach (var acf in Directory.EnumerateFiles(lib, "appmanifest_*.acf"))
            {
                string txt = File.ReadAllText(acf);
                string? id = Vdf(txt, "appid"), name = Vdf(txt, "name"), dir = Vdf(txt, "installdir");
                if (id == null || name == null || dir == null || SteamTools.Contains(id)) continue;
                if (Regex.IsMatch(name, "Redistributable|Proton|Steam Linux Runtime|SteamVR|Dedicated Server|Soundtrack|OBS Studio|Wallpaper Engine|Blender|SDK$", RegexOptions.IgnoreCase)) continue;
                list.Add(new LibGame { Key = "steam:" + id, Name = name, Store = "steam", InstallDir = Path.GetFullPath(Path.Combine(lib, "common", dir)), LaunchUri = $"steam://rungameid/{id}" });
            }
    }

    private static string? Vdf(string txt, string key)
    {
        var m = Regex.Match(txt, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ---------------- Epic ----------------
    private static void Epic(List<LibGame> list)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var r = doc.RootElement;
            if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
            if (r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array &&
                !cats.EnumerateArray().Any(c => c.GetString() == "games")) continue;
            string name = r.GetProperty("DisplayName").GetString() ?? "?";
            string loc = r.GetProperty("InstallLocation").GetString() ?? "";
            string exe = r.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() ?? "" : "";
            string ns = r.GetProperty("CatalogNamespace").GetString() ?? "", item = r.GetProperty("CatalogItemId").GetString() ?? "", app = r.GetProperty("AppName").GetString() ?? "";
            list.Add(new LibGame
            {
                Key = "epic:" + app, Name = name, Store = "epic", InstallDir = loc,
                ExePath = exe.Length > 0 ? Path.Combine(loc, exe) : null,
                LaunchUri = $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{app}?action=launch&silent=true",
            });
        }
    }

    // ---------------- Riot (Valorant, League of Legends) ----------------
    private static void Riot(List<LibGame> list)
    {
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string installs = Path.Combine(pd, @"Riot Games\RiotClientInstalls.json");
        if (!File.Exists(installs)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(installs));
        string? rc = doc.RootElement.TryGetProperty("rc_default", out var v) ? v.GetString() : null;
        if (rc == null || !File.Exists(rc)) return;
        rc = Path.GetFullPath(rc); // o ficheiro da Riot usa "/"

        foreach (var (product, name, exe) in new[] { ("valorant", "VALORANT", "VALORANT-Win64-Shipping"), ("league_of_legends", "League of Legends", "League of Legends") })
        {
            string yaml = Path.Combine(pd, $@"Riot Games\Metadata\{product}.live\{product}.live.product_settings.yaml");
            if (!File.Exists(yaml)) continue;
            var m = Regex.Match(File.ReadAllText(yaml), "product_install_full_path:\\s*\"?([^\"\\r\\n]+)\"?");
            var g = new LibGame
            {
                Key = "riot:" + product, Name = name, Store = "riot", InstallDir = m.Success ? m.Groups[1].Value.Replace('/', '\\') : "",
                LaunchExe = rc, LaunchArgs = $"--launch-product={product} --launch-patchline=live",
            };
            g.ProcessNames.Add(exe);
            list.Add(g);
        }
    }

    // ---------------- GOG ----------------
    private static void Gog(List<LibGame> list)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root == null) return;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            if (k?.GetValue("gameName") is not string name || k.GetValue("path") is not string path) continue;
            string? exe = k.GetValue("exe") as string;
            list.Add(new LibGame { Key = "gog:" + id, Name = name, Store = "gog", InstallDir = path, ExePath = exe != null && File.Exists(exe) ? exe : null });
        }
    }

    // ---------------- Ubisoft Connect ----------------
    private static void Ubisoft(List<LibGame> list)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (root == null) return;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            if (k?.GetValue("InstallDir") is not string dir || !Directory.Exists(dir)) continue;
            list.Add(new LibGame { Key = "ubi:" + id, Name = new DirectoryInfo(dir).Name, Store = "ubisoft", InstallDir = dir, LaunchUri = $"uplay://launch/{id}/0" });
        }
    }

    // ---------------- Battle.net / EA (pela lista de programas instalados) ----------------
    private static void Uninstall(List<LibGame> list)
    {
        foreach (var path in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var root = Registry.LocalMachine.OpenSubKey(path);
            if (root == null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                string pub = k?.GetValue("Publisher") as string ?? "";
                string store = pub.Contains("Blizzard") ? "battle.net" : pub.Contains("Electronic Arts") ? "ea" : "";
                if (store == "" || k!.GetValue("DisplayName") is not string name || k.GetValue("InstallLocation") is not string loc || !Directory.Exists(loc)) continue;
                if (Regex.IsMatch(name, "^(Battle\\.net|EA app|Origin)$", RegexOptions.IgnoreCase)) continue;
                string? icon = (k.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"');
                list.Add(new LibGame { Key = $"{store}:{sub}", Name = name, Store = store, InstallDir = loc, ExePath = icon != null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon) ? icon : null });
            }
        }
    }

    // ---------------- executáveis a vigiar ----------------
    private static readonly Regex JunkExe = new("unins|crash|redist|vc_|vcredist|dxsetup|dotnet|easyanticheat|battleye|beservice|cef|setup|install|launcher|helper|report|update|benchmark|server|editor|UnityCrashHandler",
                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void AddProcessNames(LibGame g)
    {
        if (g.ExePath != null) g.ProcessNames.Add(Path.GetFileNameWithoutExtension(g.ExePath));
        if (g.Store == "riot" || !Directory.Exists(g.InstallDir)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(g.InstallDir, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }))
            {
                string n = Path.GetFileNameWithoutExtension(f);
                if (JunkExe.IsMatch(n)) continue;
                g.ProcessNames.Add(n);
                g.GuessedExe ??= f;
                if (g.ProcessNames.Count > 25) break;
            }
        }
        catch { }
    }
}

/// <summary>Que anti-cheat cada jogo usa (para saber o que a app pode ou não fazer com ele).</summary>
public static class AntiCheatDb
{
    private static readonly (string Pattern, AntiCheatInfo Info)[] Known =
    {
        ("valorant", new("Riot Vanguard", true, "Kernel, arranca com o Windows. Bloqueia injeção (RTSS/ReShade) e drivers vulneráveis.")),
        ("league of legends", new("Riot Vanguard", true, "Desde 2024 o LoL também usa o Vanguard.")),
        ("counter-strike|cs2", new("VAC (+ FACEIT opcional)", false, "VAC é user-mode. Com -allow_third_party_software o modo confiável fica desligado (perdes Fator de Confiança). FACEIT AC é kernel.")),
        ("call of duty|warzone", new("Ricochet", true, "Kernel. Não uses ferramentas de injeção.")),
        ("fortnite", new("Easy Anti-Cheat + BattlEye", true, "Kernel.")),
        ("apex legends", new("Easy Anti-Cheat", true, "Kernel.")),
        ("rainbow six", new("BattlEye", true, "Kernel.")),
        ("pubg", new("BattlEye", true, "Kernel.")),
        ("rust", new("Easy Anti-Cheat", true, "Kernel.")),
        ("dead by daylight", new("Easy Anti-Cheat", true, "Kernel.")),
        ("the finals", new("Easy Anti-Cheat", true, "Kernel.")),
        ("marvel rivals", new("NetEase ACE", true, "Kernel.")),
        ("overwatch", new("Defense Matrix (Blizzard)", false, "User-mode.")),
        ("battlefield", new("EA Javelin", true, "Kernel.")),
        ("dota 2", new("VAC", false, "User-mode.")),
        ("rocket league", new("Easy Anti-Cheat", true, "Kernel.")),
        ("escape from tarkov", new("BattlEye", true, "Kernel.")),
        ("arena breakout", new("ACE", true, "Kernel.")),
    };

    public static AntiCheatInfo? Detect(LibGame g)
    {
        foreach (var (p, info) in Known)
            if (Regex.IsMatch(g.Name, p, RegexOptions.IgnoreCase)) return info;
        if (Directory.Exists(g.InstallDir))
        {
            try
            {
                var dirs = Directory.EnumerateDirectories(g.InstallDir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                                    .Select(d => Path.GetFileName(d)).ToList();
                if (dirs.Any(d => d.StartsWith("EasyAntiCheat", StringComparison.OrdinalIgnoreCase))) return new("Easy Anti-Cheat", true, "Detetado pela pasta do jogo.");
                if (dirs.Any(d => d.Equals("BattlEye", StringComparison.OrdinalIgnoreCase))) return new("BattlEye", true, "Detetado pela pasta do jogo.");
            }
            catch { }
        }
        return null;
    }
}
