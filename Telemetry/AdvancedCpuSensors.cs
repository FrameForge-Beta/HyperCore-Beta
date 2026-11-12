using LibreHardwareMonitor.Hardware;

namespace FrameForge.Telemetry;

/// <summary>
/// Temperatura e consumo da CPU via LibreHardwareMonitor, que usa o driver de kernel PawnIO.
/// DESLIGADO POR OMISSÃO: alguns anti-cheats de kernel (ex.: Vanguard) recusam drivers de leitura de hardware que
/// considerem vulneráveis. O PawnIO é assinado e moderno, mas não há garantia oficial; por isso é opt-in.
/// Sem ele, a análise deteta a perda de clock da CPU pelos contadores do Windows (sem driver nenhum).
/// </summary>
internal sealed class AdvancedCpuSensors : IDisposable
{
    private readonly Computer _pc = new() { IsCpuEnabled = true };
    private IHardware? _cpu;
    private volatile bool _open;

    public float? Temperature { get; private set; }
    public float? PowerW { get; private set; }

    public void Open()
    {
        Task.Run(() =>
        {
            try { _pc.Open(); _cpu = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu); _open = true; }
            catch { }
        });
    }

    public void Update()
    {
        if (!_open || _cpu == null) return;
        try
        {
            _cpu.Update();
            var temps = _cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value > 0).ToList();
            Temperature = (temps.FirstOrDefault(s => s.Name.Contains("Tctl") || s.Name.Contains("Tdie") || s.Name.Contains("Package"))
                           ?? temps.OrderByDescending(s => s.Value).FirstOrDefault())?.Value;
            PowerW = _cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name.Contains("Package"))?.Value;
        }
        catch { }
    }

    public void Dispose()
    {
        if (_open) try { _pc.Close(); } catch { }
    }
}
