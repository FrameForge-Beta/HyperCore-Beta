using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using FrameForge.Core;
using FrameForge.Telemetry;

namespace FrameForge.Sensors;

public enum SensorKind { Temperature, Load, Clock, Power, Fan, Control, Voltage, Data, Throughput, Fps, Time, Text, Other }

/// <summary>Um sensor que o utilizador pode escolher (para ventoinhas, overlay, misturas).</summary>
public sealed class SensorEntry
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public string Group { get; init; } = "";
    public HardwareType HardwareType { get; init; }
    public SensorKind Kind { get; init; }
    public string Unit { get; init; } = "";
    public float? Value { get; set; }
    internal ISensor? Lhm { get; init; }
    public string Display => $"{Group} · {Name}";
    public string LiveText => Value is float v ? $"{SensorHub.Format(v, Kind)} {Unit}".Trim() : "—";
    public override string ToString() => Display;
}

public enum MixFunction { Average, Max, Min, Sum, Subtract }
public enum CustomKind { Mix, TimeAverage, Offset }

/// <summary>Sensor criado pelo utilizador (como os "custom sensors" do FanControl).</summary>
public sealed class CustomSensorDef
{
    public string Name { get; set; } = "Novo sensor";
    public CustomKind Kind { get; set; } = CustomKind.Mix;
    public MixFunction Function { get; set; } = MixFunction.Average;
    public List<string> Sources { get; set; } = new();
    public double Seconds { get; set; } = 10;      // média no tempo
    public double Offset { get; set; }              // offset em °C (ou % se Proportional)
    public bool Proportional { get; set; }
    public string Id => "custom/" + Name;
}

/// <summary>
/// Lê TODOS os sensores do LibreHardwareMonitor (CPU, GPU incl. hotspot e memória, placa-mãe, ventoinhas, discos…) e calcula
/// os sensores personalizados. Serve as ventoinhas e o overlay. Os controlos de ventoinha também vêm daqui.
/// Corre na sua própria thread; <see cref="Ticked"/> é chamado depois de cada leitura (na mesma thread).
/// </summary>
public sealed class SensorHub : IDisposable
{
    public static SensorHub Instance { get; } = new();

    private Computer? _pc;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly object _lock = new();
    private readonly List<SensorEntry> _entries = new();
    private readonly Dictionary<string, SensorEntry> _byId = new();
    private List<CustomSensorDef> _custom = new();
    private readonly Dictionary<string, Queue<(DateTime T, float V)>> _history = new();

    public int IntervalMs { get; set; } = 1000;
    public bool Started => _thread != null;
    public bool Ready { get; private set; }
    public event Action? Ticked;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SensorHub", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void SetCustom(List<CustomSensorDef> defs) { lock (_lock) _custom = defs.ToList(); }

    public List<SensorEntry> All() { lock (_lock) return _entries.ToList(); }
    public List<SensorEntry> Temperatures() => All().Where(e => e.Kind == SensorKind.Temperature).ToList();

    public float? FindValue(Func<SensorEntry, bool> predicate)
    {
        lock (_lock) return _entries.FirstOrDefault(predicate)?.Value;
    }

    public List<float> FindValues(Func<SensorEntry, bool> predicate)
    {
        lock (_lock) return _entries.Where(predicate).Select(e => e.Value).OfType<float>().ToList();
    }

    public float? Value(string? id)
    {
        if (id == null) return null;
        lock (_lock) return _byId.TryGetValue(id, out var e) ? e.Value : null;
    }

    public SensorEntry? Get(string? id)
    {
        if (id == null) return null;
        lock (_lock) return _byId.GetValueOrDefault(id);
    }

    /// <summary>Controlos de ventoinha (sensores do tipo Control com IControl), com a ventoinha (RPM) emparelhada.</summary>
    public List<(SensorEntry Entry, IControl Control, string? FanId)> Controls()
    {
        lock (_lock)
            return _entries.Where(e => e.Kind == SensorKind.Control && e.Lhm?.Control != null)
                           .Select(e =>
                           {
                               string fan = e.Id.Replace("/control/", "/fan/");
                               return (e, e.Lhm!.Control!, _byId.ContainsKey(fan) ? fan : (string?)null);
                           }).ToList();
    }

    private void Loop()
    {
        try
        {
            _pc = new Computer
            {
                IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsControllerEnabled = true,
                IsStorageEnabled = true, IsMemoryEnabled = true, IsPsuEnabled = true, IsBatteryEnabled = true,
            };
            _pc.Open();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }

        var sw = Stopwatch.StartNew();
        while (!_stop)
        {
            long t0 = sw.ElapsedMilliseconds;
            try { Refresh(); } catch (Exception ex) { Debug.WriteLine(ex); }
            Ready = true;
            try { Ticked?.Invoke(); } catch (Exception ex) { Debug.WriteLine(ex); }
            int wait = IntervalMs - (int)(sw.ElapsedMilliseconds - t0);
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    private void Refresh()
    {
        var seen = new List<(ISensor S, IHardware H)>();
        if (_pc != null)
            foreach (var hw in _pc.Hardware) Walk(hw, seen);

        lock (_lock)
        {
            foreach (var (s, h) in seen)
            {
                string id = s.Identifier.ToString();
                if (!_byId.TryGetValue(id, out var e))
                {
                    e = new SensorEntry { Id = id, Name = s.Name, Group = h.Name, HardwareType = h.HardwareType, Kind = KindOf(s.SensorType), Unit = UnitOf(s.SensorType), Lhm = s };
                    _byId[id] = e;
                    _entries.Add(e);
                }
                e.Value = s.Value is float v && !float.IsNaN(v) ? v : null;
            }
            AddSamplerSensors();
            ComputeCustom();
        }
    }

    private static void Walk(IHardware hw, List<(ISensor, IHardware)> seen)
    {
        hw.Update();
        foreach (var s in hw.Sensors) seen.Add((s, hw));
        foreach (var sub in hw.SubHardware) Walk(sub, seen);
    }

    // Sensores do FrameForge que não vêm do LHM (contadores do Windows): ficam disponíveis para misturas e ventoinhas também
    private void AddSamplerSensors()
    {
        var s = AppServices.Sampler.Last;
        void Put(string id, string name, SensorKind k, string unit, float? v)
        {
            if (!_byId.TryGetValue(id, out var e))
            {
                e = new SensorEntry { Id = id, Name = name, Group = "Windows", Kind = k, Unit = unit };
                _byId[id] = e;
                _entries.Add(e);
            }
            e.Value = v;
        }
        Put("v/cpu_total", "CPU total", SensorKind.Load, "%", s.CpuTotal);
        Put("v/cpu_maxthread", "CPU thread mais ocupado", SensorKind.Load, "%", s.CpuMaxThread);
        Put("v/cpu_clock", "CPU clock efetivo", SensorKind.Clock, "MHz", s.CpuClockMHz);
        Put("v/ram_pct", "RAM usada", SensorKind.Load, "%", s.RamUsedPct);
        Put("v/ram_used", "RAM usada", SensorKind.Data, "GB", (float)(SystemSampler.TotalMemoryMB() - s.RamAvailMB) / 1024f);
        Put("v/dpc", "Latência de drivers (DPC+ISR)", SensorKind.Load, "%", s.DpcPct + s.IntPct);
        Put("v/disk", "Disco ocupado", SensorKind.Load, "%", s.DiskBusyPct);
        if (s.VramTotalMB > 0)
        {
            Put("v/vram_pct", "VRAM usada", SensorKind.Load, "%", 100 * s.VramUsedMB / s.VramTotalMB);
            Put("v/vram_used", "VRAM usada", SensorKind.Data, "GB", s.VramUsedMB / 1024f);
        }
        Put("v/ping", "Ping (1.1.1.1, só a gravar)", SensorKind.Other, "ms", s.PingMs);
    }

    private void ComputeCustom()
    {
        var now = DateTime.Now;
        foreach (var c in _custom)
        {
            var vals = c.Sources.Select(id => _byId.TryGetValue(id, out var e) ? e.Value : null).Where(v => v != null).Select(v => v!.Value).ToList();
            float? result = null;
            switch (c.Kind)
            {
                case CustomKind.Mix when vals.Count > 0:
                    result = c.Function switch
                    {
                        MixFunction.Max => vals.Max(),
                        MixFunction.Min => vals.Min(),
                        MixFunction.Sum => vals.Sum(),
                        MixFunction.Subtract => vals[0] - vals.Skip(1).Sum(),
                        _ => vals.Average(),
                    };
                    break;
                case CustomKind.TimeAverage when vals.Count > 0:
                    if (!_history.TryGetValue(c.Name, out var q)) _history[c.Name] = q = new();
                    q.Enqueue((now, vals[0]));
                    while (q.Count > 0 && (now - q.Peek().T).TotalSeconds > Math.Max(1, c.Seconds)) q.Dequeue();
                    result = q.Average(x => x.V);
                    break;
                case CustomKind.Offset when vals.Count > 0:
                    result = c.Proportional ? vals[0] * (float)(1 + c.Offset / 100) : vals[0] + (float)c.Offset;
                    break;
            }
            var kind = c.Sources.Select(id => _byId.GetValueOrDefault(id)?.Kind).FirstOrDefault(k => k != null) ?? SensorKind.Temperature;
            if (!_byId.TryGetValue(c.Id, out var entry))
            {
                entry = new SensorEntry { Id = c.Id, Name = c.Name, Group = "Personalizado", Kind = kind, Unit = UnitOf(kind) };
                _byId[c.Id] = entry;
                _entries.Add(entry);
            }
            entry.Value = result;
        }
        // remove personalizados apagados
        foreach (var gone in _entries.Where(e => e.Group == "Personalizado" && !_custom.Any(c => c.Id == e.Id)).ToList())
        {
            _entries.Remove(gone);
            _byId.Remove(gone.Id);
        }
    }

    public static SensorKind KindOf(SensorType t) => t switch
    {
        SensorType.Temperature => SensorKind.Temperature,
        SensorType.Load or SensorType.Level => SensorKind.Load,
        SensorType.Clock or SensorType.Frequency => SensorKind.Clock,
        SensorType.Power => SensorKind.Power,
        SensorType.Fan => SensorKind.Fan,
        SensorType.Control => SensorKind.Control,
        SensorType.Voltage => SensorKind.Voltage,
        SensorType.Data or SensorType.SmallData => SensorKind.Data,
        SensorType.Throughput => SensorKind.Throughput,
        _ => SensorKind.Other,
    };

    public static string UnitOf(SensorType t) => t switch
    {
        SensorType.SmallData => "MB",
        _ => UnitOf(KindOf(t)),
    };

    public static string UnitOf(SensorKind k) => k switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.Load or SensorKind.Control => "%",
        SensorKind.Clock => "MHz",
        SensorKind.Power => "W",
        SensorKind.Fan => "RPM",
        SensorKind.Voltage => "V",
        SensorKind.Data => "GB",
        SensorKind.Throughput => "B/s",
        SensorKind.Fps => "FPS",
        _ => "",
    };

    public static string Format(float v, SensorKind k) => k switch
    {
        SensorKind.Voltage => v.ToString("0.000"),
        SensorKind.Data => v.ToString("0.0"),
        SensorKind.Temperature => v.ToString("0.0"),
        _ => v.ToString("0"),
    };

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(2000);
        try { _pc?.Close(); } catch { }
    }
}
