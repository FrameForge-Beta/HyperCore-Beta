using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using FrameForge.Hardware;

namespace FrameForge.Overclock;

/// <summary>Um perfil de afinação da GPU (o mesmo que os perfis 1–5 do MSI Afterburner).</summary>
public sealed class OcProfile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int CoreOffset { get; set; }        // MHz
    public int MemOffset { get; set; }         // MHz (unidade do driver)
    public int PowerPct { get; set; } = 100;   // % do limite de energia de fábrica
    public int LockMaxClock { get; set; }      // 0 = livre; >0 = teto de clock (usado para undervolt)
    public int FanPct { get; set; }            // 0 = automático
    public bool Builtin { get; set; }
    public string Risk { get; set; } = "";
    public override string ToString() => Name;
}

public sealed class GpuState
{
    public string Name = "?";
    public int? CoreOffset, MemOffset, CoreMin, CoreMax, MemMin, MemMax, CoreType, MemType, CoreBase, MemBase;
    public uint? PowerW, PowerLimitW, PowerDefaultW, PowerMinW, PowerMaxW, CoreClock, MemClock, Temp, Fan, FanRpm, MaxCoreClock;
    public ulong Throttle;
    public string? NvApiError;
}

/// <summary>
/// Overclock/undervolt da GPU NVIDIA ao estilo do Afterburner, com proteção:
/// - offsets de core/memória pela NVAPI, limite de energia/teto de clock/ventoinha pela NVML;
/// - tudo vale só até reiniciar (nada é gravado na VBIOS), e "Stock" volta aos valores de fábrica;
/// - GUARDA DE CRASH: se o driver de vídeo for abaixo (evento 4101 / nvlddmkm), volta a Stock e marca o perfil como instável.
/// </summary>
public sealed class GpuTuner : IDisposable
{
    private readonly IntPtr _h = Nvml.FirstDevice();
    private EventLogWatcher? _watch;
    private DateTime _appliedAt;

    public bool Supported => _h != IntPtr.Zero;
    public OcProfile? Applied { get; private set; }
    public event Action<string>? CrashDetected;

    public static List<OcProfile> Presets(GpuState s)
    {
        int maxClock = (int)(s.MaxCoreClock ?? 2000);
        int maxPct = s.PowerDefaultW > 0 && s.PowerMaxW > 0 ? (int)(100.0 * s.PowerMaxW!.Value / s.PowerDefaultW!.Value) : 100;
        return new()
        {
            new() { Name = "Stock", Builtin = true, PowerPct = 100, Risk = "nenhum",
                    Description = "Valores de fábrica. Usa para comparar e sempre que algo correr mal." },
            new() { Name = "Silencioso / Eficiente (undervolt)", Builtin = true, CoreOffset = 100, LockMaxClock = Math.Max(1200, maxClock - 250), PowerPct = 90, Risk = "baixo",
                    Description = "Sobe a curva +100 MHz e põe um teto de clock: a placa atinge quase o mesmo clock com menos tensão. " +
                                  "Resultado típico: −8 a −15 °C, −20 a −30% de consumo, ventoinhas mais baixas e 0–3% de FPS a menos. Ótimo contra quebras por temperatura." },
            new() { Name = "OC ligeiro", Builtin = true, CoreOffset = 60, MemOffset = 400, PowerPct = Math.Max(100, maxPct), Risk = "baixo",
                    Description = "Valores que quase todas as placas aguentam. +3 a +6% de FPS em jogos limitados pela GPU. Mais consumo e calor." },
            new() { Name = "OC forte (testar!)", Builtin = true, CoreOffset = 120, MemOffset = 800, PowerPct = Math.Max(100, maxPct), Risk = "médio",
                    Description = "Muitas placas aguentam, algumas não. Testa 30 min num jogo pesado. Se houver crash, artefactos (pontos/cores estranhas) ou o FPS CAIR, baixa a memória primeiro." },
        };
    }

    public GpuState Read()
    {
        var s = new GpuState();
        if (_h == IntPtr.Zero) return s;
        s.Name = Nvml.Name(_h) ?? "GPU NVIDIA";
        s.PowerW = Nvml.PowerMw(_h) / 1000;
        s.PowerLimitW = Nvml.PowerLimitMw(_h) / 1000;
        s.PowerDefaultW = Nvml.PowerDefaultMw(_h) / 1000;
        if (Nvml.PowerRangeMw(_h) is (uint mn, uint mx)) { s.PowerMinW = mn / 1000; s.PowerMaxW = mx / 1000; }
        s.CoreClock = Nvml.Clock(_h, Nvml.ClockGraphics);
        s.MemClock = Nvml.Clock(_h, Nvml.ClockMem);
        s.MaxCoreClock = Nvml.MaxClock(_h, Nvml.ClockGraphics);
        s.Temp = Nvml.Temperature(_h);
        s.Fan = Nvml.FanPct(_h);
        s.Throttle = Nvml.Throttle(_h) ?? 0;
        var (core, mem, err) = NvApi.Read();
        s.NvApiError = err;
        if (core != null) { s.CoreOffset = core.DeltaMHz; s.CoreMin = core.MinMHz; s.CoreMax = core.MaxMHz; s.CoreType = core.TypeId; s.CoreBase = core.BaseMHz; }
        if (mem != null) { s.MemOffset = mem.DeltaMHz; s.MemMin = mem.MinMHz; s.MemMax = mem.MaxMHz; s.MemType = mem.TypeId; s.MemBase = mem.BaseMHz; }
        return s;
    }

    /// <summary>Aplica um perfil passo a passo. Devolve o registo do que correu bem/mal (o driver pode recusar partes).</summary>
    public List<string> Apply(OcProfile p)
    {
        var log = new List<string>();
        if (_h == IntPtr.Zero) { log.Add("✖ Sem GPU NVIDIA com NVML. (AMD/Intel: ainda não suportado no overclock.)"); return log; }
        var st = Read();

        // 1) Limite de energia
        if (st.PowerDefaultW is uint def && def > 0)
        {
            uint want = (uint)Math.Round(def * p.PowerPct / 100.0);
            if (st.PowerMinW is uint mn && st.PowerMaxW is uint mx) want = Math.Clamp(want, mn, mx);
            int rc = Nvml.SetPowerLimitMw(_h, want * 1000);
            log.Add(rc == 0 ? $"✔ Limite de energia: {want} W ({p.PowerPct}%)" : $"✖ Limite de energia: {Nvml.Explain(rc)}");
        }

        // 2) Offsets (NVAPI)
        int coreOff = st.CoreMin is int cmin && st.CoreMax is int cmax ? Math.Clamp(p.CoreOffset, cmin, cmax) : p.CoreOffset;
        int memOff = st.MemMin is int mmin && st.MemMax is int mmax ? Math.Clamp(p.MemOffset, mmin, mmax) : p.MemOffset;
        var e1 = NvApi.SetOffset(NvApi.DomainGraphics, coreOff, st.CoreType ?? 1);
        log.Add(e1 == null ? $"✔ Offset do núcleo: {coreOff:+0;-0;0} MHz" : $"✖ Offset do núcleo: {e1}");
        var e2 = NvApi.SetOffset(NvApi.DomainMemory, memOff, st.MemType ?? 0);
        log.Add(e2 == null ? $"✔ Offset da memória: {memOff:+0;-0;0} MHz" : $"✖ Offset da memória: {e2}");

        // 3) Teto de clock (undervolt) — NVML
        if (p.LockMaxClock > 0)
        {
            int rc = Nvml.LockClocks(_h, 210, (uint)p.LockMaxClock);
            log.Add(rc == 0 ? $"✔ Teto de clock: {p.LockMaxClock} MHz" : $"✖ Teto de clock: {Nvml.Explain(rc)}");
        }
        else
        {
            int rc = Nvml.ResetClocks(_h);
            if (rc == 0) log.Add("✔ Clock sem teto");
        }

        // 4) Ventoinha
        uint fans = Nvml.FanCount(_h) ?? 1;
        for (uint f = 0; f < fans; f++)
        {
            int rc = p.FanPct > 0 ? Nvml.SetFanPct(_h, f, (uint)Math.Clamp(p.FanPct, 30, 100)) : Nvml.SetFanAuto(_h, f);
            if (f == 0)
                log.Add(rc == 0 ? (p.FanPct > 0 ? $"✔ Ventoinha fixa em {p.FanPct}%" : "✔ Ventoinha em automático")
                                : $"• Ventoinha: {Nvml.Explain(rc)} (usa a curva do Afterburner/FanControl se precisares)");
        }

        Applied = p;
        _appliedAt = DateTime.Now;
        return log;
    }

    public List<string> Stock() => Apply(new OcProfile { Name = "Stock", PowerPct = 100 });

    /// <summary>Vigia o registo do sistema: um reset do driver de vídeo com OC ativo significa OC instável.</summary>
    public void StartCrashGuard()
    {
        if (_watch != null) return;
        try
        {
            var q = new EventLogQuery("System", PathType.LogName,
                "*[System[(Provider[@Name='Display'] and EventID=4101) or (Provider[@Name='nvlddmkm'] and (EventID=13 or EventID=14 or EventID=153))]]");
            _watch = new EventLogWatcher(q);
            _watch.EventRecordWritten += (_, e) =>
            {
                var prof = Applied;
                if (prof == null || prof.Name == "Stock" || (DateTime.Now - _appliedAt).TotalSeconds < 3) return;
                try { Stock(); } catch { }
                CrashDetected?.Invoke(prof.Name);
            };
            _watch.Enabled = true;
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    public static List<string> ConflictingTools()
    {
        var names = new[] { ("MSIAfterburner", "MSI Afterburner"), ("EVGAPrecisionX1", "EVGA Precision X1"), ("GPUTweakIII", "ASUS GPU Tweak III"),
                            ("ThunderMaster", "Palit ThunderMaster"), ("AorusEngine", "AORUS Engine"), ("GigabyteControlCenter", "Gigabyte Control Center") };
        var list = new List<string>();
        foreach (var (proc, name) in names)
        {
            var ps = Process.GetProcessesByName(proc);
            if (ps.Length > 0) list.Add(name);
            foreach (var p in ps) p.Dispose();
        }
        return list;
    }

    public void Dispose()
    {
        _watch?.Dispose();
    }
}
