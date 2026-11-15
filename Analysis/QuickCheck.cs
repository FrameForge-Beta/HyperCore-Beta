using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using FrameForge.Core;
using FrameForge.Hardware;
using FrameForge.Optimize;
using FrameForge.Sensors;
using FrameForge.Tuning;

namespace FrameForge.Analysis;

/// <summary>Uma coisa vista pela análise rápida. Severity 0 = está bem; 1 dica, 2 aviso, 3 importante.</summary>
public sealed class QuickItem
{
    public string Check { get; set; } = "";
    public int Severity { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>Tag de navegação da secção/aba que resolve isto (AppServices.Navigate); null = só informativo.</summary>
    public string? Tag { get; set; }
    /// <summary>Texto curto do botão ("Corrigir", "Ver como").</summary>
    public string? Action { get; set; }
}

/// <summary>Resultado da análise rápida: a Nota HyperCore (0–100) e o que melhorar.</summary>
public sealed class QuickResult
{
    public DateTime When { get; set; } = DateTime.Now;
    public double Seconds { get; set; }
    public int Score { get; set; }
    public List<QuickItem> Items { get; set; } = new();
    /// <summary>Verificações que não acabaram a tempo (ficaram de fora da nota).</summary>
    public List<string> Skipped { get; set; } = new();

    public List<QuickItem> Issues() => Items.Where(i => i.Severity > 0).OrderByDescending(i => i.Severity).ToList();
    public List<QuickItem> Good() => Items.Where(i => i.Severity == 0).ToList();
}

/// <summary>Progresso: a verificação <c>Check</c> acabou (<c>Done</c> de <c>Total</c>); <c>Worst</c> = pior severidade que encontrou.</summary>
public sealed record QuickProgress(string Check, int Done, int Total, int Worst);

/// <summary>
/// Análise rápida do Início ("ANALISAR O PC"): só LÊ (registo, APIs de ecrã, sensores já abertos, registo de eventos,
/// ficheiros da app) e responde em menos de 5 s. As verificações correm em paralelo fora do UI thread; o que não acabar
/// até ao limite fica de fora da nota em vez de a atrasar. Nada aqui muda o PC.
/// </summary>
public static class QuickCheck
{
    public sealed record CheckInfo(string Id, string Label, string Glyph);

    /// <summary>As verificações, pela ordem em que aparecem na lista "a analisar".</summary>
    public static readonly CheckInfo[] Checks =
    {
        new("monitor", "Monitor", "\uE7F4"),
        new("temps", "Temperaturas", "\uE9CA"),
        new("level", "Nível de desempenho", "\uEC4A"),
        new("windows", "Definições de jogo do Windows", "\uE7FC"),
        new("power", "Plano de energia", "\uE945"),
        new("driver", "Driver da gráfica", "\uE964"),
        new("ram", "Memória RAM", "\uE950"),
        new("disk", "Espaço em disco", "\uE88E"),
        new("startup", "Arranque do Windows", "\uE7E8"),
        new("errors", "Erros de hardware", "\uE7BA"),
        new("scan", "Último scan", "\uE721"),
    };

    private const int DeadlineMs = 4500;
    private static string FilePath => Path.Combine(AppPaths.Root, "analise-rapida.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ------------------------------------------------------------------ execução

    public static async Task<QuickResult> RunAsync(IProgress<QuickProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var probeTask = AppServices.Probe;
        async Task<SystemProbe?> Probe()
        {
            try
            {
                var done = await Task.WhenAny(probeTask, Task.Delay(3200, ct)).ConfigureAwait(false);
                return done == probeTask && probeTask.IsCompletedSuccessfully ? probeTask.Result : null;
            }
            catch { return null; }
        }

        var work = new (string Id, Func<SystemProbe?, List<QuickItem>> Fn, bool NeedsProbe)[]
        {
            ("monitor", _ => Monitor(), false),
            ("temps", _ => Temps(), false),
            ("level", _ => Level(), false),
            ("windows", WindowsGaming, true),
            ("power", Power, true),
            ("driver", Driver, true),
            ("ram", Ram, true),
            ("disk", _ => Disk(), false),
            ("startup", _ => Startup(), false),
            ("errors", _ => Errors(), false),
            ("scan", _ => LastScan(), false),
        };

        int done = 0;
        var tasks = work.Select(w => Task.Run(async () =>
        {
            List<QuickItem> items;
            try
            {
                var p = w.NeedsProbe ? await Probe().ConfigureAwait(false) : null;
                ct.ThrowIfCancellationRequested();
                items = w.Fn(p);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Debug.WriteLine(ex); items = new(); }
            foreach (var i in items) i.Check = w.Id;
            int n = Interlocked.Increment(ref done);
            progress?.Report(new QuickProgress(w.Id, n, work.Length, items.Count == 0 ? 0 : items.Max(i => i.Severity)));
            return items;
        }, ct)).ToArray();

        try { await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(DeadlineMs, ct)).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        ct.ThrowIfCancellationRequested();

        var r = new QuickResult();
        for (int k = 0; k < tasks.Length; k++)
        {
            if (tasks[k].IsCompletedSuccessfully) r.Items.AddRange(tasks[k].Result);
            else r.Skipped.Add(work[k].Id);
        }
        r.Score = ScoreOf(r.Items);
        r.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        r.When = DateTime.Now;
        Save(r);
        return r;
    }

    /// <summary>100 menos as penalizações (importante 15, aviso 8, dica 3), nunca abaixo de 5.</summary>
    public static int ScoreOf(IEnumerable<QuickItem> items) =>
        Math.Clamp(100 - items.Sum(i => i.Severity switch { >= 3 => 15, 2 => 8, 1 => 3, _ => 0 }), 5, 100);

    public static string Verdict(int score) => score switch
    {
        >= 90 => "Excelente",
        >= 80 => "Muito bom",
        >= 65 => "Bom",
        >= 45 => "Pode melhorar",
        _ => "Precisa de atenção",
    };

    /// <summary>Chave da cor do tema para a nota (Good/Warn/Bad).</summary>
    public static string ColorKey(int score) => score >= 80 ? "Good" : score >= 60 ? "Warn" : "Bad";

    // ------------------------------------------------------------------ última análise (ficheiro)

    public static QuickResult? LoadLast()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<QuickResult>(File.ReadAllText(FilePath), Json) : null; }
        catch { return null; }
    }

    private static void Save(QuickResult r)
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(r, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }

    // ------------------------------------------------------------------ verificações

    private static QuickItem Ok(string title, string detail = "") => new() { Severity = 0, Title = title, Detail = detail };
    private static QuickItem Bad(int sev, string title, string detail, string? tag, string? action) =>
        new() { Severity = sev, Title = title, Detail = detail, Tag = tag, Action = action };

    private static List<QuickItem> Monitor()
    {
        var list = new List<QuickItem>();
        var displays = ReadDisplays();
        foreach (var d in displays.Where(d => d.MaxHz > d.Hz + 5))
            list.Add(Bad(3, $"Monitor a {d.Hz} Hz (aguenta {d.MaxHz} Hz)",
                $"{(displays.Count > 1 ? (d.Primary ? "No ecrã principal, v" : "Num ecrã secundário, v") : "V")}ês menos imagens por segundo do que o ecrã consegue mostrar.",
                "FF:gamersetup", "Corrigir"));
        if (list.Count == 0 && displays.Count > 0)
        {
            var p = displays.FirstOrDefault(d => d.Primary) ?? displays[0];
            list.Add(Ok($"Monitor no máximo ({p.Hz} Hz)"));
        }
        return list;
    }

    private static List<QuickItem> Temps()
    {
        var list = new List<QuickItem>();
        var live = PcLive.Read();
        bool cpuBusy = live.CpuLoad is float l && l > 40;
        if (live.CpuTemp is float c)
        {
            if (c >= 90) list.Add(Bad(3, $"Processador muito quente ({c:0} °C)", "Acima de 90 °C perde velocidade para se proteger. Vê as ventoinhas e o pó.", "FF:fans", "Ver ventoinhas"));
            else if (c >= 75 && !cpuBusy) list.Add(Bad(2, $"Processador quente em repouso ({c:0} °C)", "Com pouca carga devia estar abaixo de 65 °C. Pode ser pó, pasta térmica ou ventoinhas lentas.", "FF:fans", "Ver ventoinhas"));
        }
        if (live.GpuTemp is float g)
        {
            if (g >= 87) list.Add(Bad(3, $"Gráfica muito quente ({g:0} °C)", "Acima de 85 °C a placa baixa os clocks e perde FPS.", "FF:fans", "Ver ventoinhas"));
            else if (g >= 70 && live.GpuLoad is float gl && gl < 30) list.Add(Bad(2, $"Gráfica quente em repouso ({g:0} °C)", "Sem jogo aberto devia estar abaixo de 60 °C.", "FF:fans", "Ver ventoinhas"));
        }
        if (live.GpuTemp is float g2 && live.GpuHotspot is float hs && hs - g2 > 28)
            list.Add(Bad(2, $"Hotspot da gráfica {hs - g2:0} °C acima do normal", "Uma diferença tão grande costuma ser pasta térmica gasta.", "PageGpu", "Ver gráfica"));
        if (list.Count == 0 && (live.CpuTemp != null || live.GpuTemp != null))
        {
            var parts = new List<string>();
            if (live.CpuTemp is float a) parts.Add($"CPU {a:0} °C");
            if (live.GpuTemp is float b) parts.Add($"GPU {b:0} °C");
            list.Add(Ok("Temperaturas normais", string.Join(" · ", parts)));
        }
        return list;
    }

    private static List<QuickItem> Level()
    {
        var s = AppServices.Settings;
        if (s.AppliedOptimizeLevel == 0 && s.TweakOverrides.Count == 0)
            return new() { Bad(2, "Windows sem afinação para jogos", "Escolhe um nível de desempenho. Tudo volta atrás com um clique.", "FF:optimize", "Escolher nível") };
        string name = s.AppliedOptimizeLevel > 0 ? TweakCatalog.LevelNames[Math.Clamp(s.AppliedOptimizeLevel, 0, 4)] : "personalizado";
        int drift = LevelKeeper.Drifted.Count;
        if (drift > 0)
            return new() { Bad(2, $"O Windows desfez {drift} {(drift == 1 ? "ajuste" : "ajustes")} do nível {name}", "Atualizações e outros programas repõem coisas. Reaplica com um clique.", "FF:optimize", "Reaplicar") };
        var list = new List<QuickItem> { Ok($"Nível {name} aplicado") };
        if (s.PendingRestart.Count > 0)
            list.Add(Bad(1, "Falta reiniciar o PC", $"{s.PendingRestart.Count} {(s.PendingRestart.Count == 1 ? "ajuste está" : "ajustes estão")} à espera de reinício para ficar ativo.", "FF:optimize", "Ver ajustes"));
        return list;
    }

    private static List<QuickItem> WindowsGaming(SystemProbe? p)
    {
        var list = new List<QuickItem>();
        bool gameMode = (SystemProbe.RegDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") ?? 1) == 1;
        bool dvr = (SystemProbe.RegDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") ?? 1) == 1;
        bool? hags = SystemProbe.RegDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") is int h ? h == 2 : null;

        if (!gameMode) list.Add(Bad(2, "Modo de Jogo do Windows desligado", "Com ele ligado, o Windows Update não instala nada a meio do jogo.", "FF:gamersetup", "Ligar"));
        if (dvr) list.Add(Bad(1, "Captura de jogos da Game Bar ligada", "Grava em segundo plano e gasta um pouco de FPS.", "FF:gamersetup", "Desligar"));
        string gpu = p?.GpuName ?? "";
        if (hags == false && Regex.IsMatch(gpu, @"RTX\s*(40|50)\d0", RegexOptions.IgnoreCase))
            list.Add(Bad(1, "HAGS desligado", "A tua gráfica precisa dele para o Frame Generation (DLSS 3/4).", "FF:gamersetup", "Ver"));
        if (list.Count == 0) list.Add(Ok("Windows pronto para jogos", "Modo de Jogo ligado, captura desligada"));
        return list;
    }

    private static List<QuickItem> Power(SystemProbe? p)
    {
        if (p?.IsLaptop == true) return new() { Ok("Portátil: plano de energia à tua escolha") };
        var plan = TweakCatalog.ActivePlan();
        if (plan == null) return new();
        if (TweakCatalog.IsPerformancePlan(plan.Guid, plan.Name)) return new() { Ok($"Plano de energia: {plan.Name}") };
        return new() { Bad(1, $"Plano de energia: {plan.Name}", "Num PC de secretária, o Alto desempenho deixa o processador responder mais depressa.", "FF:optimize", "Mudar") };
    }

    private static List<QuickItem> Driver(SystemProbe? p)
    {
        if (p?.GpuDriverDate is not DateTime d) return new();
        int days = (int)(DateTime.Now - d).TotalDays;
        string name = ResetExplode.GameCatalog.ShortGpuName(p.GpuName);
        if (days > 270) return new() { Bad(2, $"Driver da gráfica com {days / 30} meses", $"{name}: drivers novos trazem otimizações para jogos recentes.", "PageDriver", "Atualizar") };
        if (days > 150) return new() { Bad(1, $"Driver da gráfica com {days / 30} meses", $"{name}: vale a pena ver se há versão nova.", "PageDriver", "Ver drivers") };
        return new() { Ok("Driver da gráfica recente", $"{name} · {d:MM/yyyy}") };
    }

    private static List<QuickItem> Ram(SystemProbe? p)
    {
        var list = new List<QuickItem>();
        if (p != null && p.Ram.Count > 0)
        {
            int cfg = p.Ram.Min(r => r.ConfiguredMHz);
            var rated = p.Ram.Where(r => r.RatedMHz != null).Select(r => r.RatedMHz!.Value).ToList();
            if (rated.Count > 0 && cfg < rated.Min() - 150)
                list.Add(Bad(3, $"RAM a {cfg} MHz em vez de {rated.Min()} MHz", "O perfil XMP/EXPO está desligado na BIOS. Ligá-lo dá FPS e 1% low melhores.", "FF:diag", "Ver como"));
            if (p.Ram.Count == 1)
                list.Add(Bad(3, "RAM num só canal", "Só tens 1 pente: a memória anda a metade da velocidade. Um 2.º pente igual resolve.", "FF:diag", "Ver detalhes"));
            if (p.RamGB is > 0 and < 15)
                list.Add(Bad(2, $"Só {p.RamGB:0} GB de RAM", "Jogos atuais com Discord e browser abertos pedem 16 GB ou mais.", "FF:diag", "Ver detalhes"));
        }
        var (total, avail) = ResetExplode.Optimizer.MemoryStatus();
        if (total > 0)
        {
            double used = (double)(total - avail) / total;
            if (used > 0.85)
                list.Add(Bad(2, $"Memória quase cheia ({used:P0})", "Programas de fundo estão a ocupar a RAM que o jogo precisa.", "FF:gamemode", "Modo Jogo"));
        }
        if (list.Count == 0 && p != null && p.RamGB > 0) list.Add(Ok($"RAM bem configurada ({p.RamGB:0} GB)"));
        return list;
    }

    private static List<QuickItem> Disk()
    {
        var list = new List<QuickItem>();
        string sys = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize < 20_000_000_000) continue;
                double free = (double)d.AvailableFreeSpace / d.TotalSize, freeGb = d.AvailableFreeSpace / 1e9;
                bool system = string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase);
                string letter = d.Name.TrimEnd('\\');
                if (system && (free < 0.05 || freeGb < 10))
                    list.Add(Bad(3, $"Disco {letter} quase cheio ({freeGb:0} GB livres)", "O Windows precisa de espaço para a memória virtual e a cache de shaders.", "FF:maintenance", "Libertar espaço"));
                else if (system && free < 0.12)
                    list.Add(Bad(2, $"Pouco espaço em {letter} ({freeGb:0} GB livres)", "Mantém pelo menos 10–15% livre para o disco não abrandar.", "FF:maintenance", "Libertar espaço"));
                else if (!system && free < 0.05)
                    list.Add(Bad(1, $"Disco {letter} quase cheio ({freeGb:0} GB livres)", "Atualizações de jogos precisam de espaço extra para instalar.", "FF:maintenance", "Ver espaço"));
            }
            catch { }
        }
        // Saúde dos discos medida no último scan
        try
        {
            foreach (var r in (ScanService.Instance.Last?.Discos ?? new()).Where(x => x.SaudeAlerta))
                list.Add(Bad(3, $"Disco {r.Unidade} com sinais de desgaste", "Faz já uma cópia dos teus ficheiros importantes.", "PageDisks", "Ver discos"));
        }
        catch { }
        if (list.Count == 0) list.Add(Ok("Espaço em disco folgado"));
        return list;
    }

    private static List<QuickItem> Startup()
    {
        int n = ResetExplode.StartupManager.List().Count(e => e.Enabled && !e.FileMissing);
        if (n >= 12) return new() { Bad(2, $"Arranque pesado: {n} programas", "Abrem sozinhos com o Windows e ficam a gastar RAM e CPU enquanto jogas.", "PageStartup", "Rever arranque") };
        if (n >= 8) return new() { Bad(1, $"{n} programas abrem com o Windows", "Desliga os que não usas sempre. Não desinstala nada.", "PageStartup", "Rever arranque") };
        return new() { Ok($"Arranque leve ({n} {(n == 1 ? "programa" : "programas")})") };
    }

    private static List<QuickItem> Errors()
    {
        var e = ResetExplode.OcCheck.ErrorsSince(DateTime.Now.AddDays(-7));
        if (e.Error != null) return new();
        var list = new List<QuickItem>();
        if (e.BugChecks > 0)
            list.Add(Bad(3, e.BugChecks == 1 ? "Houve um ecrã azul esta semana" : $"{e.BugChecks} ecrãs azuis esta semana", "Pode ser overclock instável, driver ou RAM. Testa a estabilidade.", "PageOc", "Testar"));
        if (e.GpuResets > 0)
            list.Add(Bad(3, $"O driver de vídeo foi abaixo {e.GpuResets} {(e.GpuResets == 1 ? "vez" : "vezes")}", "Costuma ser overclock da gráfica demasiado alto ou driver com problemas.", "FF:oc", "Ver overclock"));
        int whea = Math.Max(0, e.Failing - e.GpuResets);
        if (whea > 0)
            list.Add(Bad(3, $"{whea} erros de hardware (WHEA) esta semana", "O processador ou a RAM corrigiram erros: sinal de instabilidade.", "PageOc", "Testar"));
        if (list.Count == 0) list.Add(Ok("Sem erros de hardware em 7 dias"));
        return list;
    }

    private static List<QuickItem> LastScan()
    {
        var last = ScanService.Instance.Last;
        if (last == null)
            return new() { Bad(1, "Ainda não fizeste um scan", "Mede o PC a fundo e afina a gráfica sozinho.", "FF:scan", "Fazer scan") };
        int days = (int)(DateTime.Now - last.Data).TotalDays;
        if (days > 90)
            return new() { Bad(1, $"Último scan há {days} dias", "Drivers e Windows mudaram desde então. Vale a pena repetir.", "FF:scan", "Repetir scan") };
        return new() { Ok(last.Nota is int n ? $"Scan feito a {last.Data:dd/MM} (nota {n})" : $"Scan feito a {last.Data:dd/MM}") };
    }

    // ------------------------------------------------------------------ ecrãs (rápido: sem WMI)

    private sealed record Disp(int Hz, int MaxHz, bool Primary);

    private static List<Disp> ReadDisplays()
    {
        var list = new List<Disp>();
        var dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DisplayDevice>())
        {
            if ((dd.StateFlags & 1) == 0) continue;
            var cur = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(dd.DeviceName, -1, ref cur)) continue;
            int max = cur.dmDisplayFrequency;
            var m = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            for (int k = 0; EnumDisplaySettings(dd.DeviceName, k, ref m); k++)
                if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight) max = Math.Max(max, m.dmDisplayFrequency);
            if (cur.dmDisplayFrequency > 1) list.Add(new Disp(cur.dmDisplayFrequency, max, (dd.StateFlags & 4) != 0));
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint num, ref DisplayDevice dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);
}

/// <summary>Leitura instantânea do PC para os cartões ao vivo (barata: pode ser chamada a cada segundo no UI thread).</summary>
public sealed record PcLiveSnapshot(
    float? CpuLoad, float? CpuTemp, float? CpuClockMHz, float? CpuPowerW,
    float? GpuLoad, float? GpuTemp, float? GpuHotspot, float? GpuPowerW, float? VramUsedGB, float? VramTotalGB,
    double RamUsedGB, double RamTotalGB, float? DiskTemp);

/// <summary>Junta o SystemSampler (contadores do Windows, NVML) com o SensorHub (LibreHardwareMonitor) numa só leitura.</summary>
public static class PcLive
{
    public static PcLiveSnapshot Read()
    {
        var s = AppServices.Sampler.Last;
        var hub = SensorHub.Instance;
        List<SensorEntry> all = hub.Ready ? hub.All() : new();

        static bool IsGpu(SensorEntry e) => e.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
        float? Find(Func<SensorEntry, bool> f) => all.FirstOrDefault(e => e.Value != null && f(e))?.Value;

        float? cpuTemp = s.CpuTemp
            ?? Find(e => e.HardwareType == HardwareType.Cpu && e.Kind == SensorKind.Temperature &&
                         (e.Name.Contains("Package") || e.Name.Contains("Tctl") || e.Name.Contains("Tdie")))
            ?? all.Where(e => e.HardwareType == HardwareType.Cpu && e.Kind == SensorKind.Temperature && e.Value is > 0 and < 125 && !e.Name.Contains("Distance"))
                  .Select(e => e.Value).Max();
        float? cpuPower = s.CpuPowerW ?? Find(e => e.HardwareType == HardwareType.Cpu && e.Kind == SensorKind.Power && e.Name.Contains("Package"));
        float? gpuTemp = s.GpuTemp ?? Find(e => IsGpu(e) && e.Kind == SensorKind.Temperature && e.Name == "GPU Core");
        float? hotspot = Find(e => IsGpu(e) && e.Kind == SensorKind.Temperature && e.Name.Contains("Hot Spot"));
        float? gpuLoad = s.GpuLoad ?? Find(e => IsGpu(e) && e.Kind == SensorKind.Load && e.Name == "GPU Core");
        float? gpuPower = s.GpuPowerW ?? Find(e => IsGpu(e) && e.Kind == SensorKind.Power && (e.Name.Contains("Package") || e.Name.Contains("Board") || e.Name == "GPU Power"));
        float? diskTemp = all.Where(e => e.HardwareType == HardwareType.Storage && e.Kind == SensorKind.Temperature && e.Value is > 0 and < 100).Select(e => e.Value).Max();

        var (total, avail) = ResetExplode.Optimizer.MemoryStatus();
        float? vramUsed = s.VramUsedMB is float vu ? vu / 1024f : null;
        float? vramTotal = s.VramTotalMB is float vt && vt > 0 ? vt / 1024f : null;
        return new PcLiveSnapshot(
            s.CpuClockMHz > 0 || s.CpuTotal > 0 ? s.CpuTotal : null, cpuTemp, s.CpuClockMHz > 0 ? s.CpuClockMHz : null, cpuPower,
            gpuLoad, gpuTemp, hotspot, gpuPower, vramUsed, vramTotal,
            total > 0 ? (total - avail) / 1073741824.0 : 0, total > 0 ? total / 1073741824.0 : 0, diskTemp);
    }
}
