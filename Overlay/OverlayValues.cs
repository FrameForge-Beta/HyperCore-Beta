using FrameForge.Core;
using FrameForge.Sensors;
using FrameForge.Telemetry;

namespace FrameForge.Overlay;

public sealed record SourceOption(string Id, string Group, string Name, string Unit, SensorKind Kind)
{
    public string Display => $"{Group} · {Name}";
    private bool IsCustom => Group.Contains("Personalizado", StringComparison.OrdinalIgnoreCase) ||
                             Group.Contains("Custom", StringComparison.OrdinalIgnoreCase);
    public string FriendlyName => IsCustom ? Name : FriendlySensorName(Name);
    public string FriendlyDisplay => $"{FriendlyGroup(Group)} · {FriendlyName}";
    public override string ToString() => FriendlyDisplay;

    private static readonly string[] HardwarePrefixes =
    {
        "NVIDIA GeForce ", "NVIDIA ", "AMD ", "Intel(R) ", "Intel ",
        "ASUSTeK COMPUTER INC. ", "ASUS ", "Micro-Star International Co., Ltd. ", "MSI ",
        "Gigabyte Technology Co., Ltd. ", "Gigabyte ", "ASRock ", "Generic ",
    };

    public static string FriendlyGroup(string group)
    {
        string g = group.Trim();
        switch (g)
        {
            case "v": case "Windows": return "Sistema";
            case "Generic Memory": case "Memory": return "Memória";
            case "GpuNvidia": return "GPU NVIDIA";
            case "GpuAmd": return "GPU AMD";
            case "GpuIntel": return "GPU Intel";
            case "Cpu": return "Processador";
            case "Motherboard": return "Placa-mãe";
            case "SuperIO": return "Controlador da placa-mãe";
            case "Storage": return "Armazenamento";
            case "Network": return "Rede";
            case "Battery": return "Bateria";
            case "Controller": return "Controlador";
        }

        g = System.Text.RegularExpressions.Regex.Replace(g, @"\s*\([^)]*\)", "").Trim();
        foreach (string prefix in HardwarePrefixes)
            if (g.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { g = g[prefix.Length..].Trim(); break; }
        return System.Text.RegularExpressions.Regex.Replace(g, @"\s+", " ");
    }

    // A ordem importa: as expressões mais específicas vêm primeiro.
    private static readonly (string From, string To)[] NameMap =
    {
        ("Average Effective Clock", "Clock efetivo médio"),
        ("Effective Clock", "Clock efetivo"),
        ("CPU Core #", "Núcleo "),
        (" Thread #", " · Thread "),
        ("Core #", "Núcleo "),
        ("Core Residency", "Residência do núcleo"),
        ("Core Distance", "Distância do núcleo"),
        ("Core VID", "Tensão do núcleo"),
        ("Core Voltage", "Tensão do núcleo"),
        ("Core Current", "Corrente do núcleo"),
        ("Core Power", "Consumo do núcleo"),
        ("Core Max", "Núcleo mais quente"),
        ("Core Average", "Média dos núcleos"),
        ("CPU Total", "Utilização total"),
        ("CPU Package", "Pacote do processador"),
        ("CPU Core", "Núcleo"),
        ("Bus Speed", "Velocidade do bus"),
        ("GPU Hot Spot", "Hotspot gráfico"),
        ("GPU Memory Junction", "Temperatura da VRAM"),
        ("GPU Memory Used", "VRAM utilizada"),
        ("GPU Memory Total", "VRAM total"),
        ("GPU Memory Free", "VRAM livre"),
        ("GPU Memory", "Memória gráfica"),
        ("GPU Core", "Núcleo gráfico"),
        ("GPU Fan", "Ventoinha gráfica"),
        ("GPU Power", "Consumo gráfico"),
        ("GPU Package", "Pacote gráfico"),
        ("GPU Voltage", "Tensão gráfica"),
        ("GPU Clock", "Clock gráfico"),
        ("Memory Used", "Memória utilizada"),
        ("Memory Available", "Memória disponível"),
        ("Memory Total", "Memória total"),
        ("Used Space", "Espaço utilizado"),
        ("Free Space", "Espaço livre"),
        ("Read Activity", "Leitura"),
        ("Write Activity", "Escrita"),
        ("Total Activity", "Atividade total"),
        ("Read Rate", "Velocidade de leitura"),
        ("Write Rate", "Velocidade de escrita"),
        ("Remaining Life", "Vida restante"),
        ("Charge Level", "Nível de carga"),
        ("Degradation Level", "Desgaste"),
        ("Remaining Time", "Tempo restante"),
        ("Temperature #", "Temperatura "),
        ("Fan #", "Ventoinha "),
        ("Voltage #", "Tensão "),
        ("Power #", "Potência "),
        ("Current #", "Corrente "),
        ("Package", "Pacote"),
        ("DIMM", "Módulo de memória"),
        ("Fan", "Ventoinha"),
        ("CPU", "Processador"),
        ("GPU", "Gráfica"),
        ("Uso", "Utilização"),
    };

    public static string FriendlySensorName(string name)
    {
        // Os parênteses ficam: nomes como "CCD1 (Tdie)" perdem o sentido sem eles.
        string cleaned = name.Trim();
        foreach (var (from, to) in NameMap)
            cleaned = cleaned.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        cleaned = cleaned.Replace("#", "n.º ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\bVID\b", "Tensão");
        return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
    }
}

/// <summary>
/// De onde vêm os valores do overlay: FPS/frametime do jogo (ETW), leituras rápidas do Windows/NVML e TODOS os sensores do
/// LibreHardwareMonitor (via <see cref="SensorHub"/>), incluindo os sensores personalizados das ventoinhas.
/// </summary>
public static class OverlayValues
{
    /// <summary>Processo do jogo. 0 = automático (o programa em primeiro plano que está a desenhar frames).</summary>
    public static int TargetPid { get; set; }

    private static readonly SourceOption[] Quick =
    {
        new("v/fps", "Jogo", "FPS", "FPS", SensorKind.Fps),
        new("v/low1", "Jogo", "1% low (últimos 10 s)", "FPS", SensorKind.Fps),
        new("v/frametime", "Jogo", "Frametime", "ms", SensorKind.Other),
        new("v/gpu_load", "Gráfica", "Utilização", "%", SensorKind.Load),
        new("v/gpu_temp", "Gráfica", "Temperatura do núcleo", "°C", SensorKind.Temperature),
        new("v/gpu_power", "Gráfica", "Consumo", "W", SensorKind.Power),
        new("v/gpu_clock", "Gráfica", "Clock do núcleo", "MHz", SensorKind.Clock),
        new("v/gpu_memclock", "Gráfica", "Clock da memória", "MHz", SensorKind.Clock),
        new("v/gpu_fan", "Gráfica", "Ventoinha", "%", SensorKind.Control),
        new("v/vram_pct", "Gráfica", "VRAM usada", "%", SensorKind.Load),
        new("v/vram_used", "Gráfica", "VRAM usada", "GB", SensorKind.Data),
        new("v/cpu_temp", "Processador", "Temperatura", "°C", SensorKind.Temperature),
        new("v/cpu_total", "Processador", "Uso total", "%", SensorKind.Load),
        new("v/cpu_maxthread", "Processador", "Thread mais ocupado", "%", SensorKind.Load),
        new("v/cpu_clock", "Processador", "Clock efetivo", "MHz", SensorKind.Clock),
        new("v/ram_pct", "Memória", "RAM usada", "%", SensorKind.Load),
        new("v/ram_used", "Memória", "RAM usada", "GB", SensorKind.Data),
        new("v/dpc", "Sistema", "Latência de drivers (DPC+ISR)", "%", SensorKind.Load),
        new("v/disk", "Sistema", "Disco ocupado", "%", SensorKind.Load),
    };

    /// <summary>Lista para escolher no editor: rápidos primeiro, depois todos os sensores do PC.</summary>
    public static List<SourceOption> All()
    {
        SensorHub.Instance.Start();
        var list = Quick.ToList();
        var known = list.Select(x => x.Id).ToHashSet();
        foreach (var e in SensorHub.Instance.All().Where(e => !known.Contains(e.Id)))
            list.Add(new SourceOption(e.Id, e.Group, e.Name, e.Unit, e.Kind));
        return list;
    }

    public static SourceOption? Find(string? id) => id == null ? null : Quick.FirstOrDefault(q => q.Id == id) ??
        (SensorHub.Instance.Get(id) is SensorEntry e ? new SourceOption(e.Id, e.Group, e.Name, e.Unit, e.Kind) : null);

    public static int ResolvePid()
    {
        if (TargetPid != 0) return TargetPid;
        if (AppServices.Recorder.Active) return AppServices.Recorder.Pid;
        var pres = AppServices.Frames.ActivePresenters();
        int fg = SystemSampler.ForegroundPid();
        if (pres.Any(p => p.Pid == fg) && fg != Environment.ProcessId) return fg;
        return pres.FirstOrDefault(p => p.Pid != Environment.ProcessId).Pid;
    }

    public static double? Get(string? id, FrameCapture.LiveStats? live)
    {
        if (id == null) return null;
        SensorHub.Instance.Start();
        var s = AppServices.Sampler.Last;
        switch (id)
        {
            case "v/fps": return live?.Fps;
            case "v/low1": return live?.Low1Fps;
            case "v/frametime": return live?.FrameTimeMs;
            case "v/gpu_load": return GpuValue(SensorKind.Load, "GPU Core") ?? s.GpuLoad;
            case "v/gpu_temp": return GpuValue(SensorKind.Temperature, "GPU Core") ?? GpuValue(SensorKind.Temperature) ?? s.GpuTemp;
            case "v/gpu_power": return GpuValue(SensorKind.Power, "GPU Package", "GPU Power") ?? GpuValue(SensorKind.Power) ?? s.GpuPowerW;
            case "v/gpu_clock": return GpuValue(SensorKind.Clock, "GPU Core") ?? s.GpuClock;
            case "v/gpu_memclock": return GpuValue(SensorKind.Clock, "GPU Memory") ?? s.GpuMemClock;
            case "v/gpu_fan": return GpuValue(SensorKind.Fan, "GPU Fan") ?? GpuValue(SensorKind.Fan) ??
                                      GpuValue(SensorKind.Control, "GPU Fan") ?? s.GpuFan;
            case "v/vram_pct":
            {
                var used = GpuValue(SensorKind.Data, "GPU Memory Used");
                var total = GpuValue(SensorKind.Data, "GPU Memory Total");
                if (used is > 0 && total is > 0) return 100.0 * used.Value / total.Value;
                return s.VramTotalMB > 0 ? 100.0 * s.VramUsedMB!.Value / s.VramTotalMB.Value : null;
            }
            case "v/vram_used": return GpuValue(SensorKind.Data, "GPU Memory Used") is float vram ? vram / 1024.0 : s.VramUsedMB / 1024.0;
            case "v/cpu_temp": return CpuTemperature() ?? s.CpuTemp;
            case "v/cpu_total": return CpuValue(SensorKind.Load, "CPU Total") ?? s.CpuTotal;
            case "v/cpu_maxthread":
            {
                var cores = SensorHub.Instance.FindValues(e => e.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu &&
                    e.Kind == SensorKind.Load && e.Name.StartsWith("CPU Core #", StringComparison.Ordinal) && !e.Name.Contains("Thread", StringComparison.Ordinal));
                return cores.Count > 0 ? Math.Min(100, cores.Max()) : s.CpuMaxThread;
            }
            case "v/cpu_clock":
            {
                var clocks = SensorHub.Instance.FindValues(e => e.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu &&
                    e.Kind == SensorKind.Clock && e.Name.StartsWith("Core #", StringComparison.Ordinal) && !e.Name.Contains("Effective", StringComparison.Ordinal));
                return clocks.Count > 0 ? clocks.Average() : s.CpuClockMHz;
            }
            case "v/ram_pct":
            {
                var (total, available) = ResetExplode.Optimizer.MemoryStatus();
                return total > 0
                    ? Math.Clamp(100.0 * (total - available) / total, 0, 100)
                    : s.RamUsedPct;
            }
            case "v/ram_used":
            {
                var (total, available) = ResetExplode.Optimizer.MemoryStatus();
                return total > 0
                    ? Math.Max(0, total - available) / 1073741824.0
                    : (SystemSampler.TotalMemoryMB() - s.RamAvailMB) / 1024.0;
            }
            case "v/dpc": return s.DpcPct + s.IntPct;
            case "v/disk": return s.DiskBusyPct;
        }
        return SensorHub.Instance.Value(id);
    }

    private static float? GpuValue(SensorKind kind, params string[] names) =>
        SensorHub.Instance.FindValue(e => e.HardwareType.ToString().StartsWith("Gpu", StringComparison.Ordinal) &&
            e.Kind == kind && (names.Length == 0 || names.Contains(e.Name, StringComparer.OrdinalIgnoreCase)));

    private static float? CpuValue(SensorKind kind, string name) =>
        SensorHub.Instance.FindValue(e => e.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu &&
            e.Kind == kind && e.Name == name);

    // Nomes do sensor principal de temperatura do processador, por ordem de preferência (AMD Tctl/Tdie, Intel Package...)
    private static readonly string[] CpuTempNames =
        { "Core (Tctl/Tdie)", "CPU Package", "Package", "Tctl", "Tdie", "Core (Tctl)", "Core (Tdie)", "CCD1 (Tdie)", "Core Max", "Core Average" };

    /// <summary>
    /// Temperatura do CPU vinda da melhor fonte disponível: o sensor do próprio processador e, se não houver,
    /// o sensor "CPU" do chip da placa-mãe (ITE/Nuvoton). Valores fora de 1–125 °C são ignorados (sensor desligado/avariado).
    /// </summary>
    public static double? CpuTemperature()
    {
        static bool Ok(float v) => v is > 1 and < 125;
        var hub = SensorHub.Instance;
        foreach (var name in CpuTempNames)
            if (hub.FindValue(e => e.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu && e.Kind == SensorKind.Temperature &&
                                   string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) && e.Value is float v && Ok(v)) is float t)
                return t;
        var any = hub.FindValues(e => e.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu && e.Kind == SensorKind.Temperature &&
                                      e.Value is float v && Ok(v));
        if (any.Count > 0) return any.Max();
        // Placa-mãe: o chip de sensores (Super I/O) costuma ter uma entrada "CPU" com a temperatura do socket
        var board = hub.FindValues(e => e.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.SuperIO && e.Kind == SensorKind.Temperature &&
                                        e.Lhm?.Name is "CPU" or "CPU Core" or "CPU Socket" && e.Value is float v && Ok(v));
        return board.Count > 0 ? board.Max() : null;
    }

    public static string GameName(int pid) => pid == 0 ? "" : SystemSampler.ProcessName(pid) ?? "";
}
