namespace ResetExplode;

/// <summary>Um evento do registo do Windows relevante para a estabilidade (WHEA, reset do driver de vídeo, reinício inesperado).</summary>
/// <param name="Kind">"whea-fatal", "whea-pcie", "whea-cpu", "whea-memoria", "whea-outro", "gpu-reset", "reinicio", "bugcheck".</param>
public sealed record HardwareEvent(DateTime Time, string Provider, int Id, int Level, string Kind, string Text);

/// <summary>Erros de hardware lidos do registo de eventos do Windows (WHEA e reinício do driver de vídeo).</summary>
public sealed record HardwareErrors(int Whea, int GpuResets, string? LastDetail, string? Error)
{
    /// <summary>Total como sempre foi contado (todos os WHEA + resets de vídeo), para as páginas antigas.</summary>
    public int Total => Whea + GpuResets;

    /// <summary>WHEA corrigidos (o hardware corrigiu sozinho) e fatais (o Windows teve de parar).</summary>
    public int WheaCorrected { get; init; }
    public int WheaFatal { get; init; }
    public int WheaCorrectedPcie { get; init; }
    public int WheaCorrectedCpu { get; init; }
    public int WheaCorrectedMemory { get; init; }

    /// <summary>Kernel-Power 41 / EventLog 6008: o PC desligou-se ou reiniciou sem encerrar bem (bloqueio, falha de energia).</summary>
    public int UnexpectedRestarts { get; init; }
    /// <summary>BugCheck 1001: houve um ecrã azul.</summary>
    public int BugChecks { get; init; }

    /// <summary>Os eventos encontrados (os mais recentes primeiro, no máximo 50).</summary>
    public List<HardwareEvent> Events { get; init; } = new();

    /// <summary>
    /// O que reprova um teste de estabilidade: WHEA fatal, WHEA corrigido de CPU/memória/outro e resets do driver de vídeo.
    /// WHEA corrigido de PCIe NÃO conta (é ruído comum de ligações PCIe/ASPM e não indica overclock instável).
    /// </summary>
    public int Failing => WheaFatal + Math.Max(0, WheaCorrected - WheaCorrectedPcie) + GpuResets;
}
