using System.Collections.ObjectModel;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>Encapsula o LibreHardwareMonitorLib e mantém a lista de sensores atualizada.</summary>
public sealed class SensorMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly Dictionary<string, SensorRow> _index = new();
    private volatile bool _open;
    private int _refreshing;

    public ObservableCollection<SensorRow> Rows { get; } = new();
    public bool IsOpen => _open;

    public SensorMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsControllerEnabled = true
        };
    }

    /// <summary>Detecta o hardware (leva de 0,5 a alguns segundos) fora da thread da janela. Até terminar, Refresh não faz nada.</summary>
    public Task OpenAsync() => Task.Run(() => { _computer.Open(); _open = true; });

    public void Refresh()
    {
        if (!_open) return;
        foreach (var hw in _computer.Hardware)
            Walk(hw);
    }

    /// <summary>
    /// Executa a leitura pesada de hardware (hw.Update: MSR, NVML, SMBus, SMART) fora da UI thread
    /// e aplica apenas os valores já lidos nas ObservableCollection rows na UI thread — evitando travamentos de animação.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (!_open) return;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;

        try
        {
            var snapshot = await Task.Run(() =>
            {
                var list = new List<(string Id, string Hardware, string Category, string Name, SensorType Type, HardwareType HwType, float? Value, float? Min, float? Max)>(128);
                if (!_open) return list;
                foreach (var hw in _computer.Hardware)
                    CollectHardware(hw, list);
                return list;
            });

            if (!_open) return;

            foreach (var s in snapshot)
            {
                if (!_index.TryGetValue(s.Id, out var row))
                {
                    row = new SensorRow
                    {
                        Id = s.Id,
                        Hardware = s.Hardware,
                        Category = s.Category,
                        Name = s.Name,
                        Type = s.Type,
                        HardwareType = s.HwType
                    };
                    _index[s.Id] = row;
                    Rows.Add(row);
                }
                row.Update(s.Value, s.Min, s.Max);
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private static void CollectHardware(
        IHardware hw,
        List<(string Id, string Hardware, string Category, string Name, SensorType Type, HardwareType HwType, float? Value, float? Min, float? Max)> list)
    {
        try { hw.Update(); } catch { }
        foreach (var s in hw.Sensors)
        {
            list.Add((
                s.Identifier.ToString(),
                hw.Name,
                SensorRow.CategoryName(s.SensorType),
                s.Name,
                s.SensorType,
                hw.HardwareType,
                s.Value,
                s.Min,
                s.Max));
        }
        foreach (var sub in hw.SubHardware)
            CollectHardware(sub, list);
    }

    private void Walk(IHardware hw)
    {
        hw.Update();
        foreach (var s in hw.Sensors)
        {
            string id = s.Identifier.ToString();
            if (!_index.TryGetValue(id, out var row))
            {
                row = new SensorRow
                {
                    Id = id,
                    Hardware = hw.Name,
                    Category = SensorRow.CategoryName(s.SensorType),
                    Name = s.Name,
                    Type = s.SensorType,
                    HardwareType = hw.HardwareType
                };
                _index[id] = row;
                Rows.Add(row);
            }
            row.Update(s.Value, s.Min, s.Max);
        }
        foreach (var sub in hw.SubHardware)
            Walk(sub);
    }

    /// <summary>Temperatura principal da CPU: Package (Intel) / Tctl-Tdie (AMD), senão a maior entre os núcleos.</summary>
    public float? CpuTemperature
    {
        get
        {
            var temps = Rows.Where(r => r.HardwareType == HardwareType.Cpu
                                     && r.Type == SensorType.Temperature
                                     && r.RawValue is > 0).ToList();
            var main = temps.FirstOrDefault(r => r.Name.Contains("Package") || r.Name.Contains("Tctl") || r.Name.Contains("Tdie"));
            return main?.RawValue ?? temps.Max(r => r.RawValue);
        }
    }

    /// <summary>Carga total da CPU em %.</summary>
    public float? CpuLoad => Rows.FirstOrDefault(r => r.HardwareType == HardwareType.Cpu
                                                   && r.Type == SensorType.Load
                                                   && r.Name == "CPU Total")?.RawValue;

    /// <summary>Consumo do pacote da CPU em W.</summary>
    public float? CpuPower => Rows.FirstOrDefault(r => r.HardwareType == HardwareType.Cpu
                                                    && r.Type == SensorType.Power
                                                    && r.Name.Contains("Package"))?.RawValue;

    /// <summary>
    /// Driver PawnIO (necessário para temperatura/consumo da CPU via MSR/SMN).
    /// A biblioteca procura exatamente esta chave de desinstalação.
    /// </summary>
    public static bool IsPawnIOInstalled
    {
        get
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                                                                     Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key != null;
        }
    }

    public static bool IsElevated =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public enum CpuSensorProblem { None, NotElevated, PawnIOMissing, ReadFailed }

    /// <summary>Por que a temperatura/consumo da CPU não aparecem (o motivo mais provável primeiro).</summary>
    public CpuSensorProblem DiagnoseCpuSensors()
    {
        if (CpuTemperature != null) return CpuSensorProblem.None;
        if (!IsElevated) return CpuSensorProblem.NotElevated;
        if (!IsPawnIOInstalled) return CpuSensorProblem.PawnIOMissing;
        return CpuSensorProblem.ReadFailed;
    }

    public static string Explain(CpuSensorProblem p) => p switch
    {
        CpuSensorProblem.NotElevated =>
            "O HyperCore não está a correr como administrador, e o Windows só disponibiliza os sensores internos da CPU " +
            "nesse modo. Fecha e volta a abrir, aceitando o pedido de administrador.",
        CpuSensorProblem.PawnIOMissing =>
            "Falta o driver PawnIO, que lê a temperatura e o consumo da CPU. É gratuito, assinado e usado também " +
            "pelo LibreHardwareMonitor e pelo FanControl. Depois de instalar, fecha e volta a abrir o HyperCore.\n\n" +
            "Atenção: é um driver de kernel. Alguns anti-cheats de kernel (Vanguard/Valorant, FACEIT) podem recusar drivers deste tipo " +
            "e não deixar o jogo abrir. Se acontecer, desinstala o PawnIO: os relatórios de quebras do HyperCore funcionam sem ele.",
        CpuSensorProblem.ReadFailed =>
            "O PawnIO está instalado, mas a leitura falhou. Se acabaste de instalar, reinicia o PC. Se continuar, " +
            "verifica se o antivírus não bloqueou o driver ou se a CPU é muito antiga/recente para a biblioteca de sensores.",
        _ => ""
    };

    public void Dispose()
    {
        if (_open) _computer.Close();
    }
}
