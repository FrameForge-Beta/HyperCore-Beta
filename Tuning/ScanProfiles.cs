namespace FrameForge.Tuning;

/// <summary>
/// Limites e tempos de cada perfil. A segurança vem primeiro: os limites térmicos aplicam-se a TODAS as fases
/// (não só ao overclock) e, ao passar deles, a carga para logo e a GPU volta a Stock.
/// </summary>
public sealed class TuningLimits
{
    public ScanProfile Profile { get; init; }
    public string Nome { get; init; } = "";
    public string Descricao { get; init; } = "";
    public string Risco { get; init; } = "";
    public string? Aviso { get; init; }

    // Limites térmicos (°C)
    public double GpuTempMax { get; init; }
    public double GpuHotspotMax { get; init; }
    public double GpuMemTempMax { get; init; }
    public double CpuTempMax { get; init; }

    // Afinação da GPU
    public bool TocaMemoria { get; init; }
    public bool PowerMaximo { get; init; }
    public int PassoNucleo { get; init; } = 30;
    public int PassoNucleoFino { get; init; } = 15;
    public int PassoMemoria { get; init; } = 200;
    public int MargemNucleo { get; init; }           // MHz a tirar ao máximo estável
    public double MargemMemoriaPct { get; init; }    // % a tirar ao offset de memória estável
    public int MargemMemoriaMin { get; init; }       // ... e pelo menos isto (unidade do driver)
    public int TetoNucleo { get; init; }             // nunca subir acima disto (além do teto do driver)
    public int TetoMemoria { get; init; }
    public TimeSpan PassoDuracao { get; init; }      // carga por passo (cena de jogo)
    public TimeSpan OrcamentoNucleo { get; init; }   // tempo máximo da pesquisa do núcleo
    public TimeSpan OrcamentoMemoria { get; init; }
    public TimeSpan Validacao { get; init; }
    public int TentativasValidacao { get; init; } = 3;

    // CPU / RAM / GPU térmico
    public TimeSpan StressCpu { get; init; }
    public TimeSpan StressGpu { get; init; }
    public TimeSpan BenchMedicao { get; init; } = TimeSpan.FromSeconds(20); // cada passada da medição base (x2, mediana)

    public static TuningLimits For(ScanProfile p) => p switch
    {
        ScanProfile.Avancado => Avancado,
        ScanProfile.Extremo => Extremo,
        _ => Seguro,
    };

    public static IReadOnlyList<TuningLimits> All => new[] { Seguro, Avancado, Extremo };

    public static readonly TuningLimits Seguro = new()
    {
        Profile = ScanProfile.Seguro, Nome = "Seguro",
        Descricao = "Overclock só no núcleo da gráfica, com folga de segurança. Bom para quase todos.",
        Risco = "Baixo",
        GpuTempMax = 83, GpuHotspotMax = 95, GpuMemTempMax = 96, CpuTempMax = 90,
        TocaMemoria = false, PowerMaximo = false,
        MargemNucleo = 30, MargemMemoriaPct = 10, MargemMemoriaMin = 0,
        TetoNucleo = 250, TetoMemoria = 0,
        PassoDuracao = TimeSpan.FromSeconds(25), OrcamentoNucleo = TimeSpan.FromMinutes(4), OrcamentoMemoria = TimeSpan.Zero,
        Validacao = TimeSpan.FromMinutes(5),
        StressCpu = TimeSpan.FromSeconds(90), StressGpu = TimeSpan.FromSeconds(45), BenchMedicao = TimeSpan.FromSeconds(15),
    };

    public static readonly TuningLimits Avancado = new()
    {
        Profile = ScanProfile.Avancado, Nome = "Avançado",
        Descricao = "Núcleo e memória da gráfica, limite de energia no máximo. Mais ganho, mais calor.",
        Risco = "Médio",
        GpuTempMax = 86, GpuHotspotMax = 100, GpuMemTempMax = 100, CpuTempMax = 95,
        TocaMemoria = true, PowerMaximo = true,
        MargemNucleo = 15, MargemMemoriaPct = 5, MargemMemoriaMin = 50,
        TetoNucleo = 400, TetoMemoria = 1200,
        PassoDuracao = TimeSpan.FromSeconds(35), OrcamentoNucleo = TimeSpan.FromMinutes(6), OrcamentoMemoria = TimeSpan.FromMinutes(4),
        Validacao = TimeSpan.FromMinutes(10),
        StressCpu = TimeSpan.FromMinutes(3), StressGpu = TimeSpan.FromSeconds(90),
    };

    public static readonly TuningLimits Extremo = new()
    {
        Profile = ScanProfile.Extremo, Nome = "Extremo",
        Descricao = "O máximo que a placa aguenta, quase sem folga. Para quem quer cada FPS.",
        Risco = "Alto",
        Aviso = "Durante o teste o driver de vídeo pode ir abaixo (o ecrã pisca): é normal e a app recupera sozinha. " +
                "O perfil final pode precisar de mais ventilação na caixa.",
        GpuTempMax = 88, GpuHotspotMax = 105, GpuMemTempMax = 104, CpuTempMax = 95,
        TocaMemoria = true, PowerMaximo = true,
        PassoNucleoFino = 15,
        MargemNucleo = 15, MargemMemoriaPct = 0, MargemMemoriaMin = 100, // "1 passo": 15 MHz no núcleo, meio passo na memória
        TetoNucleo = int.MaxValue, TetoMemoria = int.MaxValue,
        PassoDuracao = TimeSpan.FromSeconds(40), OrcamentoNucleo = TimeSpan.FromMinutes(10), OrcamentoMemoria = TimeSpan.FromMinutes(6),
        Validacao = TimeSpan.FromMinutes(15),
        StressCpu = TimeSpan.FromMinutes(5), StressGpu = TimeSpan.FromSeconds(120),
    };
}
