using FrameForge.Sensors;
using LibreHardwareMonitor.Hardware;

namespace FrameForge.Overclock;

/// <summary>
/// Leituras da placa gráfica pelos sensores do driver (LibreHardwareMonitor): clocks, temperatura, consumo e ventoinha.
/// Serve as placas sem NVML (AMD/Intel), em que o painel de estado do overclock ficaria vazio — a escrita de offsets
/// continua a ser só NVIDIA, mas o estado em tempo real passa a funcionar em qualquer marca.
/// </summary>
public static class GpuSensors
{
    public static GpuState Read()
    {
        var state = new GpuState();
        SensorHub.Instance.Start(); // garante que o leitor de sensores está a correr mesmo sem passar pelas Ventoinhas/Overlay
        var all = SensorHub.Instance.All();
        var gpu = all.Where(IsGpu).ToList();
        if (gpu.Count == 0)
        {
            state.Name = "GPU não detetada";
            return state;
        }

        state.Name = gpu[0].Group;
        var sensors = gpu.Where(s => s.Group == state.Name).ToList();

        state.CoreClock = Value(sensors, SensorKind.Clock, "GPU Core", "GPU Clock", "Core") is float core ? (uint)Math.Round(core) : null;
        state.MemClock = Value(sensors, SensorKind.Clock, "GPU Memory", "Memory") is float mem ? (uint)Math.Round(mem) : null;
        state.Temp = Value(sensors, SensorKind.Temperature, "GPU Core", "Core", "Edge") is float temp ? (uint)Math.Round(temp) : null;
        state.PowerW = Value(sensors, SensorKind.Power, "GPU Package", "GPU Power", "Package") is float watts ? (uint)Math.Round(watts) : null;

        float? fanPct = Value(sensors, SensorKind.Control, "GPU Fan", "Fan") ?? Value(sensors, SensorKind.Load, "GPU Fan");
        state.Fan = fanPct is float pct ? (uint)Math.Round(pct) : null;
        state.FanRpm = Value(sensors, SensorKind.Fan, "GPU Fan", "Fan") is float rpm ? (uint)Math.Round(rpm) : null;

        return state;
    }

    private static bool IsGpu(SensorEntry e) =>
        e.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    /// <summary>Primeiro sensor com o tipo pedido cujo nome contenha um dos termos (por ordem de preferência).</summary>
    private static float? Value(List<SensorEntry> sensors, SensorKind kind, params string[] names)
    {
        foreach (string name in names)
        {
            var hit = sensors.FirstOrDefault(s => s.Kind == kind && s.Value is > 0 &&
                                                  s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.Value;
        }
        return null;
    }
}
