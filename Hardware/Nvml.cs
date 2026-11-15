using System.Runtime.InteropServices;
using System.Text;

namespace FrameForge.Hardware;

/// <summary>
/// NVML (nvml.dll, vem com o driver NVIDIA). Leitura de sensores e os ajustes que o driver de Windows aceita
/// em GeForce: limite de energia, bloqueio de clocks e (em algumas placas) ventoinha.
/// Os offsets de clock (core/memória) NÃO são aceites pela NVML em Windows: esses vão pela NVAPI (NvApi.cs).
/// </summary>
public static class Nvml
{
    private const string Dll = "nvml.dll";

    public const int Success = 0, NotSupported = 3, NoPermission = 4, InvalidArgument = 2;
    public const int ClockGraphics = 0, ClockSm = 1, ClockMem = 2;

    // Razões de redução de clock (nvmlClocksEventReasons)
    public const ulong ReasonIdle = 0x1, ReasonAppClocks = 0x2, ReasonSwPowerCap = 0x4, ReasonHwSlowdown = 0x8,
                       ReasonSyncBoost = 0x10, ReasonSwThermal = 0x20, ReasonHwThermal = 0x40, ReasonHwPowerBrake = 0x80,
                       ReasonDisplayClock = 0x100;

    [StructLayout(LayoutKind.Sequential)] public struct Memory { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)] public struct Utilization { public uint Gpu, Mem; }

    /// <summary>nvmlPciInfo_t (v3): identifica a placa no barramento PCI (para escolher a MESMA GPU na NVML e na NVAPI).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PciInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string BusIdLegacy;
        public uint Domain, Bus, Device, PciDeviceId, PciSubSystemId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string BusId;
    }

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")] private static extern int nvmlInit();
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCount_v2")] private static extern int nvmlDeviceGetCount(out uint n);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int nvmlDeviceGetHandleByIndex(uint i, out IntPtr h);
    [DllImport(Dll, CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr h, StringBuilder name, uint len);
    [DllImport(Dll, CharSet = CharSet.Ansi)] private static extern int nvmlSystemGetDriverVersion(StringBuilder v, uint len);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMemoryInfo(IntPtr h, out Memory m);
    [DllImport(Dll)] private static extern int nvmlDeviceGetBAR1MemoryInfo(IntPtr h, out Memory m);
    [DllImport(Dll)] private static extern int nvmlDeviceGetUtilizationRates(IntPtr h, out Utilization u);
    [DllImport(Dll)] private static extern int nvmlDeviceGetClockInfo(IntPtr h, int type, out uint mhz);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMaxClockInfo(IntPtr h, int type, out uint mhz);
    [DllImport(Dll)] private static extern int nvmlDeviceGetTemperature(IntPtr h, int sensor, out uint c);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerUsage(IntPtr h, out uint mw);
    [DllImport(Dll)] private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr h, out uint mw);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr h, out uint mw);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr h, out uint min, out uint max);
    [DllImport(Dll)] private static extern int nvmlDeviceSetPowerManagementLimit(IntPtr h, uint mw);
    [DllImport(Dll)] private static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr h, out ulong r);
    [DllImport(Dll)] private static extern int nvmlDeviceGetNumFans(IntPtr h, out uint n);
    [DllImport(Dll)] private static extern int nvmlDeviceGetFanSpeed_v2(IntPtr h, uint fan, out uint pct);
    [DllImport(Dll)] private static extern int nvmlDeviceSetFanSpeed_v2(IntPtr h, uint fan, uint pct);
    [DllImport(Dll)] private static extern int nvmlDeviceSetDefaultFanSpeed_v2(IntPtr h, uint fan);
    [DllImport(Dll)] private static extern int nvmlDeviceSetGpuLockedClocks(IntPtr h, uint min, uint max);
    [DllImport(Dll)] private static extern int nvmlDeviceResetGpuLockedClocks(IntPtr h);
    [DllImport(Dll)] private static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr h, out uint w);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMaxPcieLinkWidth(IntPtr h, out uint w);
    [DllImport(Dll)] private static extern int nvmlDeviceGetCurrPcieLinkGeneration(IntPtr h, out uint g);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMaxPcieLinkGeneration(IntPtr h, out uint g);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPerformanceState(IntPtr h, out int p);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPciInfo_v3")] private static extern int nvmlDeviceGetPciInfo(IntPtr h, out PciInfo pci);

    private static bool? _ok;
    public static bool Available => _ok ??= Try(() => nvmlInit()) == Success;

    private static int Try(Func<int> f)
    {
        try { return f(); }
        catch (DllNotFoundException) { return 999; }
        catch (EntryPointNotFoundException) { return NotSupported; }
    }

    public static string Explain(int rc) => rc switch
    {
        Success => "ok",
        NotSupported => "o driver não permite este ajuste nesta placa",
        NoPermission => "sem permissão (a app tem de correr como administrador)",
        InvalidArgument => "valor fora da faixa aceite",
        999 => "NVML não encontrada (driver NVIDIA ausente)",
        _ => $"erro NVML {rc}"
    };

    /// <summary>
    /// A GPU NVIDIA "principal": a dedicada com mais VRAM (a mesma regra dos benchmarks e da UI). Com uma só placa é o índice 0.
    /// O nome ficou por compatibilidade; todos os leitores/escritores usam esta escolha, por isso a NVML, a NVAPI e os testes
    /// falam com a mesma placa em PCs com várias GPUs.
    /// </summary>
    public static IntPtr FirstDevice()
    {
        if (!Available) return IntPtr.Zero;
        uint n = 0;
        if (Try(() => nvmlDeviceGetCount(out n)) != Success || n == 0) n = 1;
        IntPtr best = IntPtr.Zero;
        ulong bestVram = 0;
        for (uint i = 0; i < Math.Min(n, 16u); i++)
        {
            IntPtr h = IntPtr.Zero;
            uint idx = i;
            if (Try(() => nvmlDeviceGetHandleByIndex(idx, out h)) != Success || h == IntPtr.Zero) continue;
            ulong vram = Vram(h)?.Total ?? 0;
            if (best == IntPtr.Zero || vram > bestVram) { best = h; bestVram = vram; }
        }
        return best;
    }

    /// <summary>Número de GPUs NVIDIA vistas pela NVML (0 sem driver).</summary>
    public static int DeviceCount()
    {
        if (!Available) return 0;
        uint n = 0;
        return Try(() => nvmlDeviceGetCount(out n)) == Success ? (int)n : 0;
    }

    public static PciInfo? Pci(IntPtr h) { PciInfo p = default; return Try(() => nvmlDeviceGetPciInfo(h, out p)) == Success ? p : null; }

    public static string? Name(IntPtr h)
    {
        var sb = new StringBuilder(96);
        return Try(() => nvmlDeviceGetName(h, sb, (uint)sb.Capacity)) == Success ? sb.ToString() : null;
    }

    public static string? DriverVersion()
    {
        var sb = new StringBuilder(96);
        return Try(() => nvmlSystemGetDriverVersion(sb, (uint)sb.Capacity)) == Success ? sb.ToString() : null;
    }

    private delegate int UOut(IntPtr h, out uint v);
    private static uint? U(IntPtr h, UOut f) { uint v = 0; return Try(() => f(h, out v)) == Success ? v : null; }

    public static Memory? Vram(IntPtr h) { Memory m = default; return Try(() => nvmlDeviceGetMemoryInfo(h, out m)) == Success ? m : null; }
    public static Memory? Bar1(IntPtr h) { Memory m = default; return Try(() => nvmlDeviceGetBAR1MemoryInfo(h, out m)) == Success ? m : null; }
    public static Utilization? Util(IntPtr h) { Utilization u = default; return Try(() => nvmlDeviceGetUtilizationRates(h, out u)) == Success ? u : null; }
    public static uint? Clock(IntPtr h, int type) { uint v = 0; return Try(() => nvmlDeviceGetClockInfo(h, type, out v)) == Success ? v : null; }
    public static uint? MaxClock(IntPtr h, int type) { uint v = 0; return Try(() => nvmlDeviceGetMaxClockInfo(h, type, out v)) == Success ? v : null; }
    public static uint? Temperature(IntPtr h) { uint v = 0; return Try(() => nvmlDeviceGetTemperature(h, 0, out v)) == Success ? v : null; }
    public static uint? PowerMw(IntPtr h) => U(h, nvmlDeviceGetPowerUsage);
    public static uint? PowerLimitMw(IntPtr h) => U(h, nvmlDeviceGetEnforcedPowerLimit);
    public static uint? PowerDefaultMw(IntPtr h) => U(h, nvmlDeviceGetPowerManagementDefaultLimit);
    public static uint? FanCount(IntPtr h) => U(h, nvmlDeviceGetNumFans);
    public static uint? PcieWidth(IntPtr h) => U(h, nvmlDeviceGetCurrPcieLinkWidth);
    public static uint? PcieMaxWidth(IntPtr h) => U(h, nvmlDeviceGetMaxPcieLinkWidth);
    public static uint? PcieGen(IntPtr h) => U(h, nvmlDeviceGetCurrPcieLinkGeneration);
    public static uint? PcieMaxGen(IntPtr h) => U(h, nvmlDeviceGetMaxPcieLinkGeneration);
    public static int? PState(IntPtr h) { int v = 0; return Try(() => nvmlDeviceGetPerformanceState(h, out v)) == Success ? v : null; }
    public static ulong? Throttle(IntPtr h) { ulong v = 0; return Try(() => nvmlDeviceGetCurrentClocksThrottleReasons(h, out v)) == Success ? v : null; }
    public static uint? FanPct(IntPtr h, uint fan = 0) { uint v = 0; return Try(() => nvmlDeviceGetFanSpeed_v2(h, fan, out v)) == Success ? v : null; }

    public static (uint Min, uint Max)? PowerRangeMw(IntPtr h)
    {
        uint a = 0, b = 0;
        return Try(() => nvmlDeviceGetPowerManagementLimitConstraints(h, out a, out b)) == Success ? (a, b) : null;
    }

    // ---- escrita (só até reiniciar o PC ou o driver) ----
    public static int SetPowerLimitMw(IntPtr h, uint mw) => Try(() => nvmlDeviceSetPowerManagementLimit(h, mw));
    public static int LockClocks(IntPtr h, uint min, uint max) => Try(() => nvmlDeviceSetGpuLockedClocks(h, min, max));
    public static int ResetClocks(IntPtr h) => Try(() => nvmlDeviceResetGpuLockedClocks(h));
    public static int SetFanPct(IntPtr h, uint fan, uint pct) => Try(() => nvmlDeviceSetFanSpeed_v2(h, fan, pct));
    public static int SetFanAuto(IntPtr h, uint fan) => Try(() => nvmlDeviceSetDefaultFanSpeed_v2(h, fan));

    /// <summary>Texto curto com as razões ativas de redução de clock.</summary>
    public static string ThrottleText(ulong r)
    {
        var parts = new List<string>();
        if ((r & ReasonIdle) != 0) parts.Add("em repouso");
        if ((r & ReasonSwPowerCap) != 0) parts.Add("limite de energia");
        if ((r & (ReasonSwThermal | ReasonHwThermal)) != 0) parts.Add("temperatura");
        if ((r & ReasonHwSlowdown) != 0) parts.Add("travão de hardware");
        if ((r & ReasonHwPowerBrake) != 0) parts.Add("travão da fonte");
        if ((r & ReasonAppClocks) != 0) parts.Add("clock bloqueado");
        return parts.Count == 0 ? "nenhuma (clock livre / limite de tensão)" : string.Join(", ", parts);
    }
}
