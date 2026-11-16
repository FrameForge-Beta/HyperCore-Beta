using System.Runtime.InteropServices;

namespace FrameForge.Hardware;

/// <summary>
/// NVAPI (nvapi64.dll, vem com o driver NVIDIA): é por aqui que o MSI Afterburner aplica os offsets de clock do núcleo e da
/// memória. Usa a interface P-States 2.0 (NvAPI_GPU_GetPstates20 / SetPstates20), obtida por nvapi_QueryInterface.
/// O offset vale até reiniciar o PC/driver; nada é gravado na placa (VBIOS).
/// </summary>
public static class NvApi
{
    // IDs públicos usados por várias ferramentas (nvidiaInspector, NvAPIWrapper)
    private const uint IdInitialize = 0x0150E828, IdEnumPhysicalGpus = 0xE5AC921F,
                       IdGetPstates20 = 0x6FF81213, IdSetPstates20 = 0x0F4DAE6B, IdGetBusId = 0x1BE0B8E5;

    public const int DomainGraphics = 0, DomainMemory = 4;

    // NV_GPU_PERF_PSTATES20_INFO: cabeçalho de 20 bytes + 16 P-states de 456 bytes (+100 bytes na V2/V3)
    private const int PstateSize = 456, ClockEntrySize = 44, HeaderSize = 20;
    private const int SizeV1 = HeaderSize + 16 * PstateSize; // 7316
    private const int SizeV2 = SizeV1 + 100;                 // 7416
    private static uint Ver(int size, int v) => (uint)(size | (v << 16));

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumFn([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PstatesFn(IntPtr gpu, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BusIdFn(IntPtr gpu, out uint busId);

    private static EnumFn? _enum;
    private static PstatesFn? _get, _set;
    private static bool? _ok;
    private static IntPtr _gpu;
    // A NVAPI não é chamada em paralelo: o mesmo buffer/handle é partilhado por leitura e escrita.
    private static readonly object Gate = new();

    public static bool Available { get { lock (Gate) return _ok ??= Init(); } }

    /// <summary>Barramento PCI da GPU escolhida (null se desconhecido).</summary>
    public static uint? SelectedBus { get; private set; }

    /// <summary>
    /// Esquece os handles e volta a inicializar no próximo uso. Depois de um crash do driver (TDR) os handles antigos podem ficar
    /// inválidos; o afinador chama isto depois de cada falha.
    /// </summary>
    public static void Reset()
    {
        lock (Gate) { _ok = null; _gpu = IntPtr.Zero; }
    }

    private static T? Fn<T>(uint id) where T : Delegate
    {
        IntPtr p = QueryInterface(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private static bool Init()
    {
        try
        {
            var init = Fn<InitFn>(IdInitialize);
            if (init == null || init() != 0) return false;
            _enum = Fn<EnumFn>(IdEnumPhysicalGpus);
            _get = Fn<PstatesFn>(IdGetPstates20);
            _set = Fn<PstatesFn>(IdSetPstates20);
            if (_enum == null) return false;
            var handles = new IntPtr[64];
            if (_enum(handles, out int n) != 0 || n < 1) return false;
            _gpu = handles[0];
            SelectedBus = null;
            // Várias GPUs: usa a mesma placa que a NVML escolheu (a dedicada com mais VRAM), comparando o barramento PCI.
            if (n > 1 && Nvml.FirstDevice() is IntPtr nh && nh != IntPtr.Zero && Nvml.Pci(nh) is Nvml.PciInfo pci && Fn<BusIdFn>(IdGetBusId) is BusIdFn bus)
            {
                for (int i = 0; i < Math.Min(n, handles.Length); i++)
                    if (bus(handles[i], out uint b) == 0 && b == pci.Bus) { _gpu = handles[i]; SelectedBus = b; break; }
            }
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public sealed record ClockDelta(int Domain, bool Editable, int TypeId, int DeltaMHz, int MinMHz, int MaxMHz, int BaseMHz);

    /// <summary>Lê os offsets atuais do P0 (desempenho máximo) e a faixa que o driver aceita.</summary>
    public static (ClockDelta? Core, ClockDelta? Mem, string? Error) Read()
    {
        lock (Gate)
        {
            var r = ReadCore();
            // Handle inválido depois de um reset do driver: reinicializa uma vez e tenta de novo
            if (r.Error != null && r.Error.StartsWith("GetPstates20", StringComparison.Ordinal)) { _ok = null; r = ReadCore(); }
            return r;
        }
    }

    private static (ClockDelta? Core, ClockDelta? Mem, string? Error) ReadCore()
    {
        if (!(_ok ??= Init()) || _get == null) return (null, null, "NVAPI indisponível");
        IntPtr buf = Marshal.AllocHGlobal(SizeV2);
        try
        {
            int rc = -1;
            foreach (var (size, v) in new[] { (SizeV2, 3), (SizeV2, 2), (SizeV1, 1) })
            {
                Zero(buf, SizeV2);
                Marshal.WriteInt32(buf, 0, (int)Ver(size, v));
                rc = _get(_gpu, buf);
                if (rc == 0) break;
            }
            if (rc != 0) return (null, null, $"GetPstates20 falhou (código {rc})");

            int numPstates = Marshal.ReadInt32(buf, 8), numClocks = Marshal.ReadInt32(buf, 12);
            ClockDelta? core = null, mem = null;
            for (int p = 0; p < Math.Min(numPstates, 16); p++)
            {
                int ps = HeaderSize + p * PstateSize;
                if (Marshal.ReadInt32(buf, ps) != 0) continue; // só P0
                for (int c = 0; c < Math.Min(numClocks, 8); c++)
                {
                    int ce = ps + 8 + c * ClockEntrySize;
                    int domain = Marshal.ReadInt32(buf, ce);
                    int type = Marshal.ReadInt32(buf, ce + 4);
                    bool editable = (Marshal.ReadInt32(buf, ce + 8) & 1) != 0;
                    int delta = Marshal.ReadInt32(buf, ce + 12), min = Marshal.ReadInt32(buf, ce + 16), max = Marshal.ReadInt32(buf, ce + 20);
                    // single: freq_kHz em +24; range: minFreq em +24, maxFreq em +28
                    int baseKhz = type == 1 ? Marshal.ReadInt32(buf, ce + 28) : Marshal.ReadInt32(buf, ce + 24);
                    var d = new ClockDelta(domain, editable, type, delta / 1000, min / 1000, max / 1000, baseKhz / 1000);
                    if (domain == DomainGraphics) core = d;
                    else if (domain == DomainMemory) mem = d;
                }
            }
            return (core, mem, null);
        }
        catch (Exception ex) { return (null, null, ex.Message); }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Aplica um offset (MHz) ao domínio pedido no P0. Devolve null se correu bem, ou o motivo da falha.</summary>
    public static string? SetOffset(int domain, int offsetMHz, int typeId)
    {
        lock (Gate)
        {
            var (err, rc) = SetCore(domain, offsetMHz, typeId);
            // Códigos que não são "valor recusado" (ex.: handle inválido depois de um TDR): reinicializa e tenta uma vez mais
            if (err != null && rc is not (0 or -5 or -9 or -104)) { _ok = null; (err, _) = SetCore(domain, offsetMHz, typeId); }
            return err;
        }
    }

    private static (string? Error, int Rc) SetCore(int domain, int offsetMHz, int typeId)
    {
        if (!(_ok ??= Init()) || _set == null) return ("NVAPI indisponível", 1);
        IntPtr buf = Marshal.AllocHGlobal(SizeV1);
        try
        {
            Zero(buf, SizeV1);
            Marshal.WriteInt32(buf, 0, (int)Ver(SizeV1, 1));
            Marshal.WriteInt32(buf, 8, 1);  // numPstates
            Marshal.WriteInt32(buf, 12, 1); // numClocks
            Marshal.WriteInt32(buf, 16, 0); // numBaseVoltages
            int ps = HeaderSize;
            Marshal.WriteInt32(buf, ps, 0); // P0
            int ce = ps + 8;
            Marshal.WriteInt32(buf, ce, domain);
            Marshal.WriteInt32(buf, ce + 4, typeId);
            Marshal.WriteInt32(buf, ce + 12, offsetMHz * 1000);
            int rc = _set(_gpu, buf);
            return (rc == 0 ? null : rc switch
            {
                -5 => "o driver recusou o valor (fora da faixa)",
                -9 => "versão de estrutura incompatível com este driver",
                -104 => "o driver não permite overclock nesta placa (portátil bloqueado?)",
                _ => $"SetPstates20 falhou (código {rc})"
            }, rc);
        }
        catch (Exception ex) { return (ex.Message, -1); }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void Zero(IntPtr p, int n)
    {
        for (int i = 0; i < n; i += 4) Marshal.WriteInt32(p, i, 0);
    }
}
