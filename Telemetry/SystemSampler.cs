using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FrameForge.Hardware;

namespace FrameForge.Telemetry;

/// <summary>Uma leitura de tudo o que pode causar uma quebra de frames. Valores nulos = sensor indisponível.</summary>
public sealed class Sample
{
    public double T { get; set; }                 // ms desde o início da sessão
    public bool GameForeground { get; set; }

    public float CpuTotal { get; set; }           // %
    public float CpuMaxThread { get; set; }       // % do thread lógico mais ocupado
    public int CpuMaxThreadIdx { get; set; }
    public float CpuClockMHz { get; set; }        // relógio efetivo médio
    public float DpcPct { get; set; }             // tempo da CPU em DPCs (drivers)
    public float IntPct { get; set; }             // tempo da CPU em interrupções
    public float? CpuTemp { get; set; }           // só com sensores avançados (PawnIO)
    public float? CpuPowerW { get; set; }

    public float RamUsedPct { get; set; }
    public float RamAvailMB { get; set; }
    public float CommitPct { get; set; }
    public float HardFaults { get; set; }         // páginas lidas do disco por segundo (RAM a falhar)
    public float DiskBusyPct { get; set; }
    public float DiskQueue { get; set; }

    public float? GpuLoad { get; set; }
    public float? GpuClock { get; set; }
    public float? GpuMemClock { get; set; }
    public float? GpuTemp { get; set; }
    public float? GpuPowerW { get; set; }
    public float? GpuPowerLimitW { get; set; }
    public float? GpuFan { get; set; }
    public ulong GpuThrottle { get; set; }
    public float? VramUsedMB { get; set; }
    public float? VramTotalMB { get; set; }

    public float GameCpu { get; set; }            // % de UM núcleo usado pelo jogo
    public string? TopProc { get; set; }          // processo (que não o jogo) que mais usa CPU
    public float TopProcCpu { get; set; }
    public string? TopGpuProc { get; set; }       // processo (que não o jogo) que mais usa GPU
    public float TopGpuProcPct { get; set; }

    public float? PingMs { get; set; }
    public bool PingLost { get; set; }
}

/// <summary>
/// Lê sensores sem tocar no jogo: contadores do Windows (PDH), NVML (driver NVIDIA) e ping.
/// Em sessão grava 4 leituras por segundo; fora dela fica com a última leitura para o painel/overlay.
/// </summary>
public sealed class SystemSampler : IDisposable
{
    private readonly PdhQuery _fast = new();   // 4 Hz
    private readonly PdhQuery _procs = new();  // 1 Hz, só a gravar (enumerar processos custa um pouco de CPU)
    private readonly IntPtr _gpu;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly object _lock = new();
    private List<Sample>? _rec;
    private DateTime _recStart;
    private Sample _last = new();
    private AdvancedCpuSensors? _adv;

    private float? _ping;
    private bool _pingLost;
    private CancellationTokenSource? _pingCts;

    private readonly Dictionary<string, (string Name, float Cpu)> _procCache = new();
    private (string? Name, float Cpu) _topProc;
    private (string? Name, float Pct) _topGpu;
    private float _gameCpu;

    public int GamePid { get; set; }
    public Sample Last { get { lock (_lock) return _last; } }
    public bool Recording { get { lock (_lock) return _rec != null; } }

    public SystemSampler()
    {
        _gpu = Nvml.FirstDevice();
        _fast.Add("cpu", @"\Processor(_Total)\% Processor Time");
        _fast.Add("threads", @"\Processor(*)\% Processor Time");
        _fast.Add("perf", @"\Processor Information(_Total)\% Processor Performance");
        _fast.Add("freq", @"\Processor Information(_Total)\Processor Frequency");
        _fast.Add("dpc", @"\Processor(_Total)\% DPC Time");
        _fast.Add("int", @"\Processor(_Total)\% Interrupt Time");
        _fast.Add("avail", @"\Memory\Available MBytes");
        _fast.Add("faults", @"\Memory\Pages Input/sec");
        _fast.Add("commit", @"\Memory\% Committed Bytes In Use");
        _fast.Add("diskidle", @"\PhysicalDisk(_Total)\% Idle Time");
        _fast.Add("diskq", @"\PhysicalDisk(_Total)\Avg. Disk Queue Length");
        _procs.Add("pcpu", @"\Process(*)\% Processor Time");
        _procs.Add("pid", @"\Process(*)\ID Process");
        _procs.Add("geng", @"\GPU Engine(*)\Utilization Percentage");
    }

    public void EnableAdvancedCpu(bool on)
    {
        if (on && _adv == null) { _adv = new AdvancedCpuSensors(); _adv.Open(); }
        else if (!on && _adv != null) { _adv.Dispose(); _adv = null; }
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sensores", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void BeginRecording(DateTime start)
    {
        lock (_lock) { _rec = new List<Sample>(20_000); _recStart = start; }
        _pingCts = new CancellationTokenSource();
        _ = PingLoop(_pingCts.Token);
    }

    public List<Sample> EndRecording()
    {
        _pingCts?.Cancel();
        lock (_lock) { var r = _rec ?? new(); _rec = null; return r; }
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long tick = 0;
        double totalMemMB = TotalMemoryMB();
        while (!_stop)
        {
            long due = (tick + 1) * 250;
            bool recording = Recording;
            _fast.Collect();
            if (recording && tick % 4 == 0) { _procs.Collect(); ReadProcesses(); }
            if (_adv != null && tick % 4 == 0) _adv.Update();

            var s = new Sample();
            s.CpuTotal = (float)(_fast.Value("cpu") ?? 0);
            var threads = _fast.Values("threads").Where(x => x.Name != "_Total").ToList();
            if (threads.Count > 0)
            {
                var top = threads.MaxBy(x => x.Value);
                s.CpuMaxThread = (float)Math.Min(100, top.Value);
                int.TryParse(top.Name, out int idx);
                s.CpuMaxThreadIdx = idx;
            }
            double nominal = _fast.Value("freq") ?? 0, perf = _fast.Value("perf") ?? 0;
            s.CpuClockMHz = (float)(nominal * perf / 100);
            s.DpcPct = (float)(_fast.Value("dpc") ?? 0);
            s.IntPct = (float)(_fast.Value("int") ?? 0);
            s.RamAvailMB = (float)(_fast.Value("avail") ?? 0);
            s.RamUsedPct = totalMemMB > 0 ? (float)(100 * (1 - s.RamAvailMB / totalMemMB)) : 0;
            s.HardFaults = (float)(_fast.Value("faults") ?? 0);
            s.CommitPct = (float)(_fast.Value("commit") ?? 0);
            s.DiskBusyPct = (float)Math.Clamp(100 - (_fast.Value("diskidle") ?? 100), 0, 100);
            s.DiskQueue = (float)(_fast.Value("diskq") ?? 0);
            if (_adv != null) { s.CpuTemp = _adv.Temperature; s.CpuPowerW = _adv.PowerW; }

            if (_gpu != IntPtr.Zero)
            {
                var u = Nvml.Util(_gpu);
                s.GpuLoad = u?.Gpu;
                s.GpuClock = Nvml.Clock(_gpu, Nvml.ClockGraphics);
                s.GpuMemClock = Nvml.Clock(_gpu, Nvml.ClockMem);
                s.GpuTemp = Nvml.Temperature(_gpu);
                s.GpuPowerW = Nvml.PowerMw(_gpu) / 1000f;
                s.GpuPowerLimitW = Nvml.PowerLimitMw(_gpu) / 1000f;
                s.GpuFan = Nvml.FanPct(_gpu);
                s.GpuThrottle = Nvml.Throttle(_gpu) ?? 0;
                if (Nvml.Vram(_gpu) is Nvml.Memory m) { s.VramUsedMB = m.Used / 1048576f; s.VramTotalMB = m.Total / 1048576f; }
            }

            s.GameForeground = GamePid != 0 && ForegroundPid() == GamePid;
            s.GameCpu = _gameCpu;
            (s.TopProc, s.TopProcCpu) = _topProc;
            (s.TopGpuProc, s.TopGpuProcPct) = _topGpu;
            s.PingMs = _ping;
            s.PingLost = _pingLost;

            lock (_lock)
            {
                if (_rec != null) { s.T = (DateTime.Now - _recStart).TotalMilliseconds; _rec.Add(s); }
                _last = s;
            }
            tick++;
            long wait = due - sw.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait);
            else tick = sw.ElapsedMilliseconds / 250; // atrasou (PC muito ocupado): realinha sem acumular
        }
    }

    private static readonly Regex GpuEngine = new(@"pid_(\d+)_.*engtype_(\w+)", RegexOptions.Compiled);
    private static readonly HashSet<string> IgnoredProcs = new(StringComparer.OrdinalIgnoreCase) { "_Total", "Idle", "System", "Memory Compression", "Registry" };

    private void ReadProcesses()
    {
        var pids = _procs.Values("pid").ToDictionary(x => x.Name, x => (int)x.Value);
        var pidName = new Dictionary<int, string>();
        foreach (var (inst, pid) in pids) pidName[pid] = inst.Split('#')[0];

        int game = GamePid;
        float gameCpu = 0;
        (string? Name, float Cpu) top = (null, 0);
        foreach (var (inst, cpu) in _procs.Values("pcpu"))
        {
            string name = inst.Split('#')[0];
            if (IgnoredProcs.Contains(name) || name.Equals("FrameForge", StringComparison.OrdinalIgnoreCase)) continue;
            int pid = pids.GetValueOrDefault(inst);
            if (pid == game && game != 0) { gameCpu += (float)cpu; continue; }
            if (cpu > top.Cpu) top = (name, (float)cpu);
        }
        _gameCpu = gameCpu;
        _topProc = top;

        var perPid = new Dictionary<int, double>();
        foreach (var (inst, util) in _procs.Values("geng"))
        {
            var m = GpuEngine.Match(inst);
            if (!m.Success) continue;
            int pid = int.Parse(m.Groups[1].Value);
            perPid[pid] = perPid.GetValueOrDefault(pid) + util;
        }
        var g = perPid.Where(x => x.Key != game && x.Key != Environment.ProcessId).OrderByDescending(x => x.Value).FirstOrDefault();
        _topGpu = g.Key != 0 ? (pidName.GetValueOrDefault(g.Key, $"PID {g.Key}"), (float)Math.Min(100, g.Value)) : (null, 0);
    }

    private async Task PingLoop(CancellationToken ct)
    {
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await ping.SendPingAsync("1.1.1.1", 1000);
                _pingLost = r.Status != IPStatus.Success;
                _ping = r.Status == IPStatus.Success ? r.RoundtripTime : null;
            }
            catch { _pingLost = true; _ping = null; }
            try { await Task.Delay(1000, ct); } catch { break; }
        }
    }

    public static double TotalMemoryMB()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref m) ? m.TotalPhys / 1048576.0 : 0;
    }

    /// <summary>Nome do processo pela lista do sistema (sem abrir o processo — importante com anti-cheats).</summary>
    public static string? ProcessName(int pid)
    {
        string? name = null;
        foreach (var p in Process.GetProcesses())
        {
            if (p.Id == pid) name = p.ProcessName;
            p.Dispose();
        }
        return name;
    }

    public static int ForegroundPid()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(h, out uint pid);
        return (int)pid;
    }

    public void Dispose()
    {
        _stop = true;
        _pingCts?.Cancel();
        _thread?.Join(1000);
        _fast.Dispose();
        _procs.Dispose();
        _adv?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
