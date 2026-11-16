using FrameForge.Hardware;
using FrameForge.Sensors;
using LibreHardwareMonitor.Hardware;

namespace FrameForge.Tuning;

/// <summary>
/// Telemetria do scan: SensorHub (LibreHardwareMonitor: CPU, hotspot e memória da GPU) + NVML (temperatura, consumo, clocks e
/// razões de throttle da GPU NVIDIA). Não usa o SensorMonitor da janela (que é pausado durante os benchmarks).
/// </summary>
public static class ScanSensors
{
    private static IntPtr _nv = IntPtr.Zero;
    private static bool _nvTried;

    private static IntPtr Nv
    {
        get
        {
            if (!_nvTried) { _nvTried = true; _nv = Nvml.FirstDevice(); }
            return _nv;
        }
    }

    public static void Start() => SensorHub.Instance.Start();

    private static Task<LiveValues>? _inFlight;
    private static readonly object ReadGate = new();

    /// <summary>
    /// Leitura com prazo, para os vigias: se o driver estiver pendurado (TDR) a NVML pode não responder, e o vigia não pode ficar
    /// preso com ele. Passado o prazo devolve uma leitura vazia (sem temperaturas) — o vigia trata isso como "perdi o sensor".
    /// Nunca acumula leituras: enquanto uma está pendurada, não se lança outra.
    /// </summary>
    public static async Task<LiveValues> ReadAsync(TimeSpan timeout)
    {
        Task<LiveValues> t;
        lock (ReadGate)
        {
            if (_inFlight == null || _inFlight.IsCompleted) _inFlight = Task.Run(Read);
            t = _inFlight;
        }
        var done = await Task.WhenAny(t, Task.Delay(timeout));
        return done == t ? await t : new LiveValues();
    }

    /// <summary>Uma leitura de tudo (rápida: o SensorHub já lê em segundo plano; a NVML é uma chamada ao driver).</summary>
    public static LiveValues Read()
    {
        // Nunca deixa uma leitura falhada parar um teste (ex.: sensores a fechar com a app)
        try { return ReadCore(); } catch { return new LiveValues(); }
    }

    private static LiveValues ReadCore()
    {
        var hub = SensorHub.Instance;
        double? cpuTemp = Cpu(hub, SensorKind.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Package", "Core Max", "Core Average", "CPU");
        double? cpuPower = Cpu(hub, SensorKind.Power, "Package", "CPU Package");
        var clocks = hub.FindValues(e => e.HardwareType == HardwareType.Cpu && e.Kind == SensorKind.Clock &&
                                         e.Name.StartsWith("Core #", StringComparison.Ordinal) && !e.Name.Contains("Effective", StringComparison.Ordinal));
        double? cpuClock = clocks.Count > 0 ? clocks.Average() : AppServicesSampler()?.CpuClockMHz;
        double? cpuLoad = Cpu(hub, SensorKind.Load, "CPU Total") ?? AppServicesSampler()?.CpuTotal;

        // Hotspot ≠ memória: "Junction" sozinho também apanharia "GPU Memory Junction" (o limite do hotspot ficava a comparar a memória)
        double? gpuHot = GpuWhere(hub, SensorKind.Temperature, e => !e.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase),
                             "GPU Hot Spot", "Hot Spot", "Junction");
        double? gpuMem = Gpu(hub, SensorKind.Temperature, "GPU Memory Junction", "Memory Junction", "GPU Memory", "Memory");

        IntPtr h = Nv;
        if (h != IntPtr.Zero)
        {
            return new LiveValues
            {
                CpuTemp = cpuTemp, CpuPowerW = cpuPower, CpuClockMHz = cpuClock, CpuLoad = cpuLoad,
                GpuTemp = Nvml.Temperature(h),
                GpuHotspot = gpuHot, GpuMemTemp = gpuMem,
                GpuPowerW = Nvml.PowerMw(h) is uint mw ? mw / 1000.0 : null,
                GpuPowerLimitW = Nvml.PowerLimitMw(h) is uint lw ? lw / 1000.0 : null,
                GpuClockMHz = Nvml.Clock(h, Nvml.ClockGraphics),
                GpuMemClockMHz = Nvml.Clock(h, Nvml.ClockMem),
                GpuLoad = Nvml.Util(h)?.Gpu,
                GpuFanPct = Nvml.FanPct(h),
                GpuThrottle = Nvml.Throttle(h) ?? 0,
            };
        }
        return new LiveValues
        {
            CpuTemp = cpuTemp, CpuPowerW = cpuPower, CpuClockMHz = cpuClock, CpuLoad = cpuLoad,
            // Temperatura do núcleo: nunca o hotspot nem a memória (cada um tem o seu limite)
            GpuTemp = GpuWhere(hub, SensorKind.Temperature, e => !IsHotOrMem(e.Name), "GPU Core", "Core", "Edge", "GPU"),
            GpuHotspot = gpuHot, GpuMemTemp = gpuMem,
            GpuPowerW = Gpu(hub, SensorKind.Power, "GPU Package", "GPU Power", "Package", "Total Board"),
            GpuClockMHz = Gpu(hub, SensorKind.Clock, "GPU Core", "Core"),
            GpuMemClockMHz = Gpu(hub, SensorKind.Clock, "GPU Memory", "Memory"),
            GpuLoad = Gpu(hub, SensorKind.Load, "GPU Core", "D3D 3D"),
            GpuFanPct = Gpu(hub, SensorKind.Control, "GPU Fan", "Fan"),
        };
    }

    /// <summary>Há leitura de temperatura da CPU? (precisa de administrador e do driver PawnIO no LibreHardwareMonitor).</summary>
    public static bool HasCpuTemp => Read().CpuTemp is > 0;

    private static Telemetry.Sample? AppServicesSampler()
    {
        try { return Core.AppServices.Sampler.Last; } catch { return null; }
    }

    private static bool IsGpu(SensorEntry e) => e.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private static double? Cpu(SensorHub hub, SensorKind kind, params string[] names)
    {
        var all = hub.All();
        foreach (string n in names)
        {
            var hit = all.FirstOrDefault(e => e.HardwareType == HardwareType.Cpu && e.Kind == kind && e.Value is > 0 &&
                                              e.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.Value;
        }
        return null;
    }

    private static bool IsHotOrMem(string n) =>
        n.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) || n.Contains("Hotspot", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("Junction", StringComparison.OrdinalIgnoreCase) || n.Contains("Memory", StringComparison.OrdinalIgnoreCase);

    private static double? Gpu(SensorHub hub, SensorKind kind, params string[] names) => GpuWhere(hub, kind, null, names);

    /// <summary>Sensor da GPU dedicada (o grupo NVIDIA/AMD com sensores; a iGPU Intel só se for a única).</summary>
    private static double? GpuWhere(SensorHub hub, SensorKind kind, Func<SensorEntry, bool>? filter, params string[] names)
    {
        var gpus = hub.All().Where(IsGpu).ToList();
        if (gpus.Count == 0) return null;
        var dedicated = gpus.Where(e => e.HardwareType != HardwareType.GpuIntel).ToList();
        var pool = dedicated.Count > 0 ? dedicated : gpus;
        if (filter != null) pool = pool.Where(filter).ToList();
        foreach (string n in names)
        {
            var hit = pool.FirstOrDefault(e => e.Kind == kind && e.Value is > 0 && e.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                      ?? pool.FirstOrDefault(e => e.Kind == kind && e.Value is > 0 && e.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.Value;
        }
        return null;
    }
}

/// <summary>Acumula leituras durante uma carga e resume-as (máximos, médias, throttle).</summary>
public sealed class ThermalTracker
{
    private readonly List<LiveValues> _s = new();
    private readonly object _lock = new();

    public void Add(LiveValues v) { lock (_lock) _s.Add(v); }
    public int Count { get { lock (_lock) return _s.Count; } }

    public ThermalSummary Cpu()
    {
        lock (_lock)
        {
            var t = new ThermalSummary
            {
                TempMax = Max(_s.Select(x => x.CpuTemp)),
                PotenciaMaxW = Max(_s.Select(x => x.CpuPowerW)),
                ClockMedioMHz = Avg(_s.Select(x => x.CpuClockMHz)),
                ClockMinMHz = Min(_s.Skip(_s.Count / 4).Select(x => x.CpuClockMHz)),
            };
            // Throttle térmico da CPU: na 2.ª metade, clock a cair >8% face à 1.ª metade com a temperatura perto do máximo
            if (_s.Count >= 20)
            {
                var first = Avg(_s.Take(_s.Count / 2).Select(x => x.CpuClockMHz));
                var second = Avg(_s.Skip(_s.Count / 2).Select(x => x.CpuClockMHz));
                var hot = Max(_s.Skip(_s.Count / 2).Select(x => x.CpuTemp));
                if (first > 0 && second > 0 && second < first * 0.92 && hot >= 88) t.ThrottleTermico = true;
            }
            return t;
        }
    }

    public ThermalSummary Gpu()
    {
        lock (_lock)
        {
            var load = _s.Where(x => x.GpuLoad is null or >= 50).ToList(); // ignora o arranque/fim da carga
            if (load.Count == 0) load = _s;
            return new ThermalSummary
            {
                TempMax = Max(_s.Select(x => x.GpuTemp)),
                HotspotMax = Max(_s.Select(x => x.GpuHotspot)),
                MemTempMax = Max(_s.Select(x => x.GpuMemTemp)),
                PotenciaMaxW = Max(_s.Select(x => x.GpuPowerW)),
                ClockMedioMHz = Avg(load.Select(x => x.GpuClockMHz)),
                ClockMinMHz = Min(load.Select(x => x.GpuClockMHz)),
                ThrottleTermico = load.Count(x => x.GpuThermalCapped) > load.Count * 0.1,
                ThrottleEnergia = load.Count(x => x.GpuPowerCapped) > load.Count * 0.3,
            };
        }
    }

    public double PowerCapFraction()
    {
        lock (_lock)
        {
            var load = _s.Where(x => x.GpuLoad is null or >= 50).ToList();
            return load.Count == 0 ? 0 : load.Count(x => x.GpuPowerCapped) / (double)load.Count;
        }
    }

    private static double? Max(IEnumerable<double?> v) { var l = v.OfType<double>().ToList(); return l.Count > 0 ? l.Max() : null; }
    private static double? Min(IEnumerable<double?> v) { var l = v.OfType<double>().Where(x => x > 0).ToList(); return l.Count > 0 ? l.Min() : null; }
    private static double? Avg(IEnumerable<double?> v) { var l = v.OfType<double>().Where(x => x > 0).ToList(); return l.Count > 0 ? l.Average() : null; }
}
