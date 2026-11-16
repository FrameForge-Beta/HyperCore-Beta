namespace FrameForge.Tuning;

/// <summary>Leituras ao vivo (para o painel "ao vivo" da página). Tudo opcional: null = sem leitura.</summary>
public sealed record LiveValues
{
    public double? CpuTemp { get; init; }
    public double? CpuPowerW { get; init; }
    public double? CpuClockMHz { get; init; }
    public double? CpuLoad { get; init; }
    public double? GpuTemp { get; init; }
    public double? GpuHotspot { get; init; }
    public double? GpuMemTemp { get; init; }
    public double? GpuPowerW { get; init; }
    public double? GpuPowerLimitW { get; init; }
    public double? GpuClockMHz { get; init; }
    public double? GpuMemClockMHz { get; init; }
    public double? GpuLoad { get; init; }
    public double? GpuFanPct { get; init; }
    public ulong GpuThrottle { get; init; }
    public double? Fps { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;

    public bool GpuPowerCapped => (GpuThrottle & Hardware.Nvml.ReasonSwPowerCap) != 0;
    public bool GpuThermalCapped => (GpuThrottle & (Hardware.Nvml.ReasonSwThermal | Hardware.Nvml.ReasonHwThermal | Hardware.Nvml.ReasonHwSlowdown)) != 0;
}

/// <summary>O que o scan reporta à página (por IProgress; chega no thread da interface se o Progress&lt;T&gt; for criado lá).</summary>
public sealed record ScanProgress
{
    public ScanPhase Fase { get; init; }
    public string FaseTitulo { get; init; } = "";
    public int FaseIndice { get; init; }
    public int FaseTotal { get; init; }
    /// <summary>Progresso geral 0–100.</summary>
    public double Percent { get; init; }
    /// <summary>Progresso da fase atual 0–1.</summary>
    public double FasePercent { get; init; }
    public TimeSpan Restante { get; init; }
    public TimeSpan Decorrido { get; init; }
    /// <summary>Linha nova para o registo de eventos (null se este relatório é só de valores ao vivo).</summary>
    public string? Linha { get; init; }
    /// <summary>O que está a acontecer agora ("A medir a CPU…", "+120 MHz núcleo — a testar…").</summary>
    public string Estado { get; init; } = "";
    /// <summary>Passo de OC em teste (null fora da afinação da GPU).</summary>
    public string? PassoOc { get; init; }
    public LiveValues? AoVivo { get; init; }
    /// <summary>Estado de cada fase (cópia).</summary>
    public IReadOnlyList<PhaseStatus> Fases { get; init; } = Array.Empty<PhaseStatus>();
}

/// <summary>Plano de um perfil: fases e duração estimada (para mostrar antes de começar).</summary>
public sealed record ScanPlan(ScanProfile Perfil, IReadOnlyList<PhaseStatus> Fases, TimeSpan Duracao, TuningLimits Limites)
{
    public string DuracaoTexto => Duracao.TotalMinutes < 60 ? $"~{Math.Round(Duracao.TotalMinutes)} min" : $"~{Duracao.TotalHours:0.0} h";
}

/// <summary>Resultado de um passo de estabilidade (uma carga com vigia).</summary>
public sealed class StepResult
{
    public bool Passou { get; set; }
    /// <summary>Motivo legível ("crash do driver", "GPU a 89 °C (limite 86 °C)"…). Vazio se passou.</summary>
    public string Motivo { get; set; } = "";
    /// <summary>O utilizador fechou a janela do teste / cancelou: não é falha de hardware.</summary>
    public bool Cancelado { get; set; }
    /// <summary>A falha foi por limite térmico (não por instabilidade).</summary>
    public bool Termico { get; set; }
    /// <summary>O driver de vídeo foi abaixo (TDR) — instabilidade de OC.</summary>
    public bool CrashDriver { get; set; }
    /// <summary>O driver recusou aplicar o valor (fora da faixa / bloqueado): não é instabilidade.</summary>
    public bool Recusado { get; set; }
    public double? AvgFps { get; set; }
    public double? Low1Fps { get; set; }
    public double Segundos { get; set; }
    public ThermalSummary Cpu { get; set; } = new();
    public ThermalSummary Gpu { get; set; } = new();
    public long ErrosRam { get; set; }
    public ResetExplode.HardwareErrors? Eventos { get; set; }
    public double? GpuPowerLimitW { get; set; }
    /// <summary>Fração do tempo com o clock da GPU limitado pela energia.</summary>
    public double PowerCapFracao { get; set; }
}

/// <summary>O que se encontrou no arranque depois de um scan interrompido (crash, bloqueio, reinício).</summary>
public sealed class RecoveryInfo
{
    public ScanProfile Perfil { get; set; }
    public ScanPhase Fase { get; set; }
    public DateTime Hora { get; set; }
    /// <summary>O valor que estava a ser testado quando tudo parou (offset do núcleo/memória).</summary>
    public int? NucleoEmTeste { get; set; }
    public int? MemoriaEmTeste { get; set; }
    public int UltimoNucleoEstavel { get; set; }
    public int UltimaMemoriaEstavel { get; set; }
    /// <summary>O Windows registou reinício inesperado/ecrã azul desde então.</summary>
    public bool PcReiniciou { get; set; }
    public bool EcraAzul { get; set; }
    /// <summary>Encontrado agora neste arranque (false = já visto antes e ainda por retomar/dispensar).</summary>
    public bool Novo { get; set; }
    /// <summary>Texto curto para o utilizador.</summary>
    public string Mensagem { get; set; } = "";
    /// <summary>Há um relatório parcial e o scan pode continuar a partir do último passo estável.</summary>
    public bool PodeRetomar { get; set; }
}
