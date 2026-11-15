using System.Text.Json.Serialization;

namespace FrameForge.Tuning;

// Modelo do scan: tudo serializável em JSON (scan\ultimo.json, scan\historico\*.json) e sem dependências de interface.

/// <summary>Perfil escolhido antes de começar.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScanProfile { Seguro, Avancado, Extremo }

/// <summary>Fases do scan, pela ordem em que correm.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScanPhase { Inventario, Cpu, Ram, Disco, GpuBase, GpuTermico, GpuNucleo, GpuMemoria, GpuValidacao, Analise }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PhaseState { PorFazer, ACorrer, Feito, Saltado, Falhou }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FindingCategory { Segurar, Configuracao, Upgrade }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpgradeComponent { GPU, CPU, RAM, Disco }

/// <summary>Como o scan acabou.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScanOutcome { Completo, Cancelado, Erro, EmCurso }

/// <summary>O relatório completo de um scan.</summary>
public sealed class ScanReport
{
    public int Versao { get; set; } = 1;
    public DateTime Data { get; set; } = DateTime.Now;
    public DateTime? Fim { get; set; }
    public ScanProfile Perfil { get; set; }
    public ScanOutcome Resultado { get; set; } = ScanOutcome.EmCurso;
    /// <summary>Motivo de cancelamento/erro (texto curto para o utilizador).</summary>
    public string? Motivo { get; set; }
    /// <summary>Nota geral 0–100 (null se o scan não chegou ao fim).</summary>
    public int? Nota { get; set; }
    /// <summary>Frase curta com o resumo ("PC equilibrado; a gráfica é o limite em 1440p").</summary>
    public string Resumo { get; set; } = "";

    public InventorySummary Inventario { get; set; } = new();
    public CpuResult? Cpu { get; set; }
    public RamResult? Ram { get; set; }
    public List<DiskResult> Discos { get; set; } = new();
    public GpuResult? Gpu { get; set; }
    public OcResult? Oc { get; set; }
    public LimiterResult? Limitador { get; set; }
    public List<ScanFinding> Conclusoes { get; set; } = new();
    public List<UpgradeOption> Upgrades { get; set; } = new();
    /// <summary>Passos de BIOS recomendados para CPU/RAM (não há OC por software de CPU/RAM).</summary>
    public List<BiosStep> Bios { get; set; } = new();
    /// <summary>Registo curto do que aconteceu (o mesmo que a página mostra durante o scan).</summary>
    public List<string> Registo { get; set; } = new();
    /// <summary>Fases e o estado em que ficaram.</summary>
    public List<PhaseStatus> Fases { get; set; } = new();

    [JsonIgnore] public IEnumerable<ScanFinding> Segurar => Conclusoes.Where(f => f.Categoria == FindingCategory.Segurar);
    [JsonIgnore] public IEnumerable<ScanFinding> Configuracoes => Conclusoes.Where(f => f.Categoria == FindingCategory.Configuracao);
}

/// <summary>Resumo do SystemProbe no momento do scan.</summary>
public sealed class InventorySummary
{
    public string Cpu { get; set; } = "?";
    public int Nucleos { get; set; }
    public int Threads { get; set; }
    public string PlacaMae { get; set; } = "?";
    public string Bios { get; set; } = "?";
    public string Gpu { get; set; } = "?";
    public string GpuFabricante { get; set; } = "?";   // NVIDIA / AMD / Intel
    public double VramGB { get; set; }
    public string DriverGpu { get; set; } = "?";
    public double RamGB { get; set; }
    public string RamTipo { get; set; } = "?";
    public int RamMHz { get; set; }
    public int? RamMHzNominal { get; set; }
    public int? RamCl { get; set; }
    public int Pentes { get; set; }
    public string Monitor { get; set; } = "?";
    public int MonitorLargura { get; set; }
    public int MonitorAltura { get; set; }
    public int MonitorHz { get; set; }
    public int MonitorHzMax { get; set; }
    public string Windows { get; set; } = "?";
    public bool Portatil { get; set; }
    public string PlanoEnergia { get; set; } = "?";
    public bool? Hags { get; set; }
    public bool? ResizableBar { get; set; }
    public string? PcieLinhas { get; set; }
    public List<string> Discos { get; set; } = new();
}

/// <summary>Temperaturas/energia/clock máximos vistos durante uma carga.</summary>
public sealed class ThermalSummary
{
    public double? TempMax { get; set; }
    public double? HotspotMax { get; set; }
    public double? MemTempMax { get; set; }
    public double? PotenciaMaxW { get; set; }
    public double? ClockMedioMHz { get; set; }
    public double? ClockMinMHz { get; set; }
    public bool ThrottleTermico { get; set; }
    public bool ThrottleEnergia { get; set; }
    public string? Nota { get; set; }
}

public sealed class CpuResult
{
    public double? NotaMulti { get; set; }       // pontos BENCH (BenchRenderer multi-thread)
    public double? Nota1Thread { get; set; }     // pontos BENCH com 1 thread
    public double? FpsJogo { get; set; }         // GameSimBench: "frames de jogo" que a CPU aguenta
    public double? FpsJogo1Low { get; set; }
    public double? MsThreadPrincipal { get; set; }
    public ThermalSummary Stress { get; set; } = new();
    public int MinutosStress { get; set; }
    public bool ErroCalculo { get; set; }        // hashes diferentes entre passadas determinísticas
    public long ErrosRam { get; set; }           // erros do padrão de RAM do StressTester
    public int ErrosWhea { get; set; }
    public bool Estavel { get; set; } = true;
    public List<string> Problemas { get; set; } = new();
}

public sealed class RamResult
{
    public double? LatenciaNs { get; set; }
    public double? LeituraGBs { get; set; }
    public double? CopiaGBs { get; set; }
    public int MHz { get; set; }
    public int? MHzNominal { get; set; }
    public bool XmpDesligado { get; set; }
    public bool CanalUnicoProvavel { get; set; }
    public List<string> Modulos { get; set; } = new();
}

public sealed class DiskResult
{
    public string Unidade { get; set; } = "";
    public string Modelo { get; set; } = "";
    public string Tipo { get; set; } = "";       // SSD / HDD / NVMe
    public bool Sistema { get; set; }
    public bool Jogos { get; set; }
    public double? LeituraMBs { get; set; }
    public double? EscritaMBs { get; set; }
    public double? Iops4kQ1 { get; set; }
    public double? LatenciaUs { get; set; }
    public double TamanhoGB { get; set; }
    public double LivreGB { get; set; }
    public string? Saude { get; set; }           // texto do DiskHealth (Bom/Atenção/Crítico)
    public bool SaudeAlerta { get; set; }
    public string? Erro { get; set; }
}

public sealed class GpuResult
{
    public string Nome { get; set; } = "";
    public double? Fps1080 { get; set; }         // GpuGameBench 1920x1080 (mediana de 2 passadas, de fábrica)
    public double? Fps1080Low { get; set; }
    public double? Fps1440 { get; set; }         // só se o monitor for 1440p ou mais
    public double? Fps1440Low { get; set; }
    public double? FpsNativo { get; set; }       // na resolução do monitor quando é maior que 1440p (ex.: 4K)
    public int? NativoLargura { get; set; }
    public int? NativoAltura { get; set; }
    public ThermalSummary Jogo { get; set; } = new();   // durante a cena de jogo
    public ThermalSummary Termico { get; set; } = new(); // durante o stress de energia (GpuBurn)
    public uint? PotenciaFabricaW { get; set; }
    public uint? PotenciaMaxW { get; set; }
    public bool Estavel { get; set; } = true;
    public List<string> Problemas { get; set; } = new();
}

/// <summary>Um passo testado pelo afinador da GPU.</summary>
public sealed class OcStep
{
    public string Tipo { get; set; } = "";       // "base", "núcleo", "memória", "validação"
    public int Nucleo { get; set; }
    public int Memoria { get; set; }
    public int PotenciaPct { get; set; } = 100;
    public bool Passou { get; set; }
    public string Motivo { get; set; } = "";
    public double? Fps { get; set; }
    public double? TempMax { get; set; }
    public double? HotspotMax { get; set; }
    public double? PotenciaMaxW { get; set; }
    public bool LimiteEnergia { get; set; }
    public double Segundos { get; set; }
}

public sealed class OcResult
{
    /// <summary>false = não foi feito (outra marca, ferramentas em conflito, sem permissão…); ver <see cref="Explicacao"/>.</summary>
    public bool Suportado { get; set; }
    public string? Explicacao { get; set; }
    public bool Estavel { get; set; }
    public int Nucleo { get; set; }              // offset final (já com a margem do perfil)
    public int Memoria { get; set; }
    public int PotenciaPct { get; set; } = 100;
    public int NucleoMaxEstavel { get; set; }    // antes da margem
    public int MemoriaMaxEstavel { get; set; }
    public int? NucleoInstavel { get; set; }     // primeiro valor que falhou (teto)
    public int? MemoriaInstavel { get; set; }
    public double? FpsBase { get; set; }
    public double? FpsFinal { get; set; }
    /// <summary>FPS de fábrica medido logo a seguir, com a placa já quente (a comparação justa para o ganho).</summary>
    public double? FpsStockQuente { get; set; }
    public double? GanhoPct { get; set; }
    public bool LimitadoEnergia { get; set; }
    public string? NomePerfil { get; set; }      // "Auto · Seguro" guardado em OcProfiles
    public int ValidacaoMinutos { get; set; }
    /// <summary>Notas curtas (ex.: "a pesquisa parou no limite térmico").</summary>
    public List<string> Notas { get; set; } = new();
    public List<OcStep> Passos { get; set; } = new();
}

/// <summary>Quem segura o PC, medido (não pelos índices teóricos).</summary>
public sealed class LimiterResult
{
    public string Principal { get; set; } = "";  // "GPU", "CPU", "Monitor", "Equilibrado", "Desconhecido"
    public double? TetoCpuFps { get; set; }
    public double? TetoGpu1080Fps { get; set; }
    public double? TetoGpu1440Fps { get; set; }
    /// <summary>Teto da gráfica na resolução do monitor (a que conta para o limitador).</summary>
    public double? TetoGpuMonitorFps { get; set; }
    /// <summary>Resolução usada na conclusão ("1080p", "1440p", "4K").</summary>
    public string Resolucao { get; set; } = "1080p";
    /// <summary>Como se chegou à conclusão (para "ver detalhes").</summary>
    public string Metodo { get; set; } = "";
    public int MonitorHz { get; set; }
    public List<string> Frases { get; set; } = new();
    public int SessoesUsadas { get; set; }
    public double? SessoesGpuPct { get; set; }
    public double? SessoesCpuPct { get; set; }
}

/// <summary>Uma conclusão do scan (o que segura, o que corrigir, o que comprar).</summary>
public sealed class ScanFinding
{
    public int Severidade { get; set; }          // 1 dica, 2 aviso, 3 importante
    public string Area { get; set; } = "";       // CPU, GPU, RAM, Disco, Monitor, Windows, Software
    public string Titulo { get; set; } = "";
    public string Detalhe { get; set; } = "";
    public string Acao { get; set; } = "";
    public FindingCategory Categoria { get; set; }
    /// <summary>Página da app que resolve isto (tag para AppServices.Navigate, ex.: "FF:fans"), se existir.</summary>
    public string? Pagina { get; set; }
    public string? PaginaTexto { get; set; }     // texto do botão ("Abrir Ventoinhas")
}

/// <summary>Uma sugestão de upgrade (preenchida por <see cref="Upgrades.Suggest"/>).</summary>
public sealed class UpgradeOption
{
    public UpgradeComponent Componente { get; set; }
    public string Nome { get; set; } = "";
    public double GanhoPct { get; set; }
    public double PrecoMinEur { get; set; }
    public double PrecoMaxEur { get; set; }
    public string Motivo { get; set; } = "";
    public List<string> Avisos { get; set; } = new();
    /// <summary>Texto para as pesquisas de preço nas lojas (ex.: "RTX 5070 12GB").</summary>
    public string PesquisaTermo { get; set; } = "";
}

/// <summary>Um passo de BIOS recomendado (CPU/RAM).</summary>
public sealed class BiosStep
{
    public string Titulo { get; set; } = "";
    public string Onde { get; set; } = "";
    public string Texto { get; set; } = "";
    public string Porque { get; set; } = "";
}

public sealed class PhaseStatus
{
    public ScanPhase Fase { get; set; }
    public string Titulo { get; set; } = "";
    public PhaseState Estado { get; set; } = PhaseState.PorFazer;
    public string? Nota { get; set; }
    public double SegundosEstimados { get; set; }
    public double? SegundosReais { get; set; }
}
