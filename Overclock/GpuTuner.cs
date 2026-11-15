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

/// <summary>Resultado estruturado de um Apply: o que o driver aceitou, por parte, e os valores efetivamente escritos.</summary>
public sealed class OcApplyResult
{
    public List<string> Log { get; } = new();
    /// <summary>Os offsets e o teto de clock foram aceites (o limite de energia só conta se foi pedido acima de 100%).</summary>
    public bool Ok => CoreError == null && MemError == null && ClockError == null && !PowerRequired;
    public int CoreOffset { get; set; }
    public int MemOffset { get; set; }
    public uint? PowerLimitW { get; set; }
    public string? PowerError { get; set; }
    public string? CoreError { get; set; }
    public string? MemError { get; set; }
    public string? ClockError { get; set; }
    public string? FanError { get; set; }
    /// <summary>Offsets lidos de volta da NVAPI depois de escrever (null se a leitura falhou).</summary>
    public int? CoreReadBack { get; set; }
    public int? MemReadBack { get; set; }
    public bool Verified => CoreReadBack == CoreOffset && MemReadBack == MemOffset;
    public string Summary => Ok ? "aplicado" : string.Join("; ", new[] { CoreError, MemError, ClockError, PowerError }.Where(x => x != null));
    internal bool PowerRequired { get; set; }
    /// <summary>Não se escreveu nada porque o pedido foi cancelado antes de chegar ao driver.</summary>
    public bool Aborted { get; set; }
}

/// <summary>
/// Overclock/undervolt da GPU NVIDIA ao estilo do Afterburner, com proteção:
/// - offsets de core/memória pela NVAPI, limite de energia/teto de clock/ventoinha pela NVML;
/// - tudo vale só até reiniciar (nada é gravado na VBIOS), e "Stock" volta aos valores de fábrica;
/// - GUARDA DE CRASH: se o driver de vídeo for abaixo (evento 4101 / nvlddmkm), volta a Stock e marca o perfil como instável.
/// Apply/Stock são serializados por um lock (a página, o GameRunner, a guarda e o afinador automático podem chamar ao mesmo tempo).
/// A GPU é a mesma em NVML e NVAPI (a dedicada com mais VRAM: ver Nvml.FirstDevice e NvApi).
/// </summary>
public sealed class GpuTuner : IDisposable
{
    private readonly IntPtr _h = Nvml.FirstDevice();
    private readonly object _gate = new();
    private EventLogWatcher? _watch;
    private volatile bool _guardOn;
    private DateTime _appliedAt;
    private volatile bool _appliedIsStock = true;

    public bool Supported => _h != IntPtr.Zero;
    public OcProfile? Applied { get; private set; }
    /// <summary>Os valores aplicados neste momento são os de fábrica (offsets 0, energia 100%, sem teto).</summary>
    public bool AppliedIsStock => _appliedIsStock;
    public event Action<string>? CrashDetected;

    /// <summary>
    /// Ligado pelo afinador automático enquanto testa valores: um reset do driver repõe Stock, mas NÃO marca o perfil como instável
    /// (o afinador trata o passo) e avisa por <see cref="AutoTuneDriverReset"/>.
    /// </summary>
    public bool AutoTuneActive { get; set; }
    /// <summary>
    /// Ligado enquanto o scan corre: só o afinador automático pode aplicar valores acima de fábrica (Stock continua sempre permitido).
    /// Impede o GameRunner, o perfil de arranque ou outra página de escreverem na placa a meio de um teste.
    /// </summary>
    public volatile bool ScanReserved;
    public event Action<DateTime>? AutoTuneDriverReset;
    /// <summary>Hora do último reset do driver visto pelo vigia (qualquer perfil).</summary>
    public DateTime? LastDriverReset { get; private set; }

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
    public List<string> Apply(OcProfile p) => ApplyDetailed(p).Log;

    public List<string> Stock() => ApplyDetailed(StockProfile()).Log;

    /// <summary>Stock com resultado estruturado; <paramref name="writeFan"/> = false não toca na ventoinha (fica com o FanEngine/placa).</summary>
    public OcApplyResult StockDetailed(bool writeFan = true) => ApplyDetailed(StockProfile(), writeFan);

    public static OcProfile StockProfile() => new() { Name = "Stock", PowerPct = 100 };

    public static bool IsStockValues(OcProfile p) => p.CoreOffset == 0 && p.MemOffset == 0 && p.PowerPct == 100 && p.LockMaxClock == 0;

    /// <summary>
    /// Aplica um perfil e devolve o que cada parte deu. Com <paramref name="writeFan"/> = false a ventoinha não é escrita
    /// (o afinador automático usa isto para não lutar com o FanEngine nem com a curva da placa).
    /// </summary>
    public OcApplyResult ApplyDetailed(OcProfile p, bool writeFan = true) => ApplyDetailed(p, writeFan, null, scanOwner: false);

    /// <summary>
    /// Como <see cref="ApplyDetailed(OcProfile, bool)"/>, com duas proteções verificadas JÁ DENTRO do lock (sem corridas):
    /// <paramref name="allowed"/> = false → não escreve nada (ex.: o scan foi cancelado entre gravar o estado e aplicar, e o Stock do
    /// fecho já passou à frente); e, com <see cref="ScanReserved"/>, só o scan (<paramref name="scanOwner"/>) aplica valores acima de fábrica.
    /// </summary>
    public OcApplyResult ApplyDetailed(OcProfile p, bool writeFan, Func<bool>? allowed, bool scanOwner)
    {
        lock (_gate)
        {
            var r = new OcApplyResult();
            var log = r.Log;
            if (allowed != null && !allowed())
            {
                r.Aborted = true;
                r.CoreError = "cancelado antes de aplicar";
                log.Add("• Cancelado antes de aplicar.");
                return r;
            }
            if (ScanReserved && !scanOwner && !IsStockValues(p))
            {
                r.CoreError = "há um scan a afinar a gráfica";
                log.Add("✖ Há um scan a afinar a gráfica: espera que acabe (ou pára-o na página Scan) antes de aplicar outro perfil.");
                return r;
            }
            if (_h == IntPtr.Zero)
            {
                r.CoreError = "sem GPU NVIDIA";
                log.Add("✖ Sem GPU NVIDIA com NVML. (AMD/Intel: ainda não suportado no overclock.)");
                return r;
            }
            var st = Read();

            // 1) Limite de energia
            if (st.PowerDefaultW is uint def && def > 0)
            {
                uint want = (uint)Math.Round(def * p.PowerPct / 100.0);
                if (st.PowerMinW is uint mn && st.PowerMaxW is uint mx) want = Math.Clamp(want, mn, mx);
                int rc = Nvml.SetPowerLimitMw(_h, want * 1000);
                if (rc == 0) r.PowerLimitW = want;
                else { r.PowerError = Nvml.Explain(rc); r.PowerRequired = p.PowerPct > 100; }
                log.Add(rc == 0 ? $"✔ Limite de energia: {want} W ({p.PowerPct}%)" : $"✖ Limite de energia: {Nvml.Explain(rc)}");
            }

            // 2) Offsets (NVAPI)
            int coreOff = st.CoreMin is int cmin && st.CoreMax is int cmax ? Math.Clamp(p.CoreOffset, cmin, cmax) : p.CoreOffset;
            int memOff = st.MemMin is int mmin && st.MemMax is int mmax ? Math.Clamp(p.MemOffset, mmin, mmax) : p.MemOffset;
            r.CoreOffset = coreOff; r.MemOffset = memOff;
            var e1 = NvApi.SetOffset(NvApi.DomainGraphics, coreOff, st.CoreType ?? 1);
            r.CoreError = e1;
            log.Add(e1 == null ? $"✔ Offset do núcleo: {coreOff:+0;-0;0} MHz" : $"✖ Offset do núcleo: {e1}");
            var e2 = NvApi.SetOffset(NvApi.DomainMemory, memOff, st.MemType ?? 0);
            r.MemError = e2;
            log.Add(e2 == null ? $"✔ Offset da memória: {memOff:+0;-0;0} MHz" : $"✖ Offset da memória: {e2}");

            // 3) Teto de clock (undervolt) — NVML
            if (p.LockMaxClock > 0)
            {
                int rc = Nvml.LockClocks(_h, 210, (uint)p.LockMaxClock);
                if (rc != 0) r.ClockError = Nvml.Explain(rc);
                log.Add(rc == 0 ? $"✔ Teto de clock: {p.LockMaxClock} MHz" : $"✖ Teto de clock: {Nvml.Explain(rc)}");
            }
            else
            {
                int rc = Nvml.ResetClocks(_h);
                if (rc == 0) log.Add("✔ Clock sem teto");
            }

            // 4) Ventoinha
            if (writeFan)
            {
                uint fans = Nvml.FanCount(_h) ?? 1;
                for (uint f = 0; f < fans; f++)
                {
                    int rc = p.FanPct > 0 ? Nvml.SetFanPct(_h, f, (uint)Math.Clamp(p.FanPct, 30, 100)) : Nvml.SetFanAuto(_h, f);
                    if (f == 0)
                    {
                        if (rc != 0) r.FanError = Nvml.Explain(rc);
                        log.Add(rc == 0 ? (p.FanPct > 0 ? $"✔ Ventoinha fixa em {p.FanPct}%" : "✔ Ventoinha em automático")
                                        : $"• Ventoinha: {Nvml.Explain(rc)} (usa a curva do Afterburner/FanControl se precisares)");
                    }
                }
            }

            // 5) Confirma o que ficou escrito
            var (core, mem, _) = NvApi.Read();
            r.CoreReadBack = core?.DeltaMHz;
            r.MemReadBack = mem?.DeltaMHz;

            Applied = p;
            _appliedIsStock = IsStockValues(p) && e1 == null && e2 == null;
            _appliedAt = DateTime.Now;
            return r;
        }
    }

    /// <summary>Vigia o registo do sistema: um reset do driver de vídeo com OC ativo significa OC instável.</summary>
    public void StartCrashGuard()
    {
        _guardOn = true;
        EnsureWatcher();
    }

    /// <summary>Liga o vigia do registo sem ligar a marcação de perfis instáveis (usado pelo afinador automático).</summary>
    public void EnsureWatcher()
    {
        lock (_gate)
        {
            if (_watch != null) return;
            try
            {
                // Mesma lista de eventos que o OcCheck usa nos testes (Display 4101, nvlddmkm 13/14/153, amdkmdag só erros)
                var q = new EventLogQuery("System", PathType.LogName, $"*[System[{ResetExplode.OcCheck.GpuResetFilter}]]");
                _watch = new EventLogWatcher(q);
                _watch.EventRecordWritten += (_, e) =>
                {
                    try { e.EventRecord?.Dispose(); } catch { }
                    LastDriverReset = DateTime.Now;
                    if (AutoTuneActive)
                    {
                        try { StockDetailed(writeFan: false); } catch { }
                        try { AutoTuneDriverReset?.Invoke(DateTime.Now); } catch { }
                        return;
                    }
                    var prof = Applied;
                    // Decide pelos VALORES aplicados, não pelo nome: um perfil chamado "Stock" com offsets também fica protegido
                    if (!_guardOn || prof == null || _appliedIsStock || (DateTime.Now - _appliedAt).TotalSeconds < 3) return;
                    try { Stock(); } catch { }
                    CrashDetected?.Invoke(prof.Name);
                };
                _watch.Enabled = true;
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
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
