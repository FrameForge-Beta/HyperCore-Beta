using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FrameForge.Tuning;

// Índice de desempenho e preços de referência embebidos (Data/perf-index.json).
// Índices próprios aproximados: RTX 4070 = 100 em cada resolução; Ryzen 7 7700X = 100 em jogos e em multi-thread.
// Preços = faixa típica de preço novo em Portugal (null = já não se vende novo). Nada disto é "tempo real".

public sealed class GpuModel
{
    public string Nome { get; set; } = "";
    public string Marca { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public double Indice1080 { get; set; }
    public double Indice1440 { get; set; }
    public double Indice4k { get; set; }
    public double Vram { get; set; }
    public int Tdp { get; set; }
    public int PsuW { get; set; }
    public string Conector { get; set; } = "";
    public string Pcie { get; set; } = "";
    public double? PrecoMinEur { get; set; }
    public double? PrecoMaxEur { get; set; }
    public string Lancamento { get; set; } = "";
    public string Tecnologia { get; set; } = "";

    public bool AVenda => PrecoMinEur is > 0 && PrecoMaxEur is > 0;
    public double? PrecoMedio => AVenda ? (PrecoMinEur!.Value + PrecoMaxEur!.Value) / 2 : null;
    public string FaixaPreco => PerfIndex.FormatRange(PrecoMinEur, PrecoMaxEur);

    /// <summary>Índice para a altura do ecrã (≤1080 → 1080p, ≤1600 → 1440p, acima → 4K).</summary>
    public double Indice(int alturaEcra) => alturaEcra <= 0 || alturaEcra <= 1200 ? Indice1080 : alturaEcra <= 1700 ? Indice1440 : Indice4k;

    /// <summary>Só 8 linhas PCIe: numa placa PCIe 3.0 perde-se um pouco (sobretudo com 8 GB de VRAM).</summary>
    public bool PcieX8OuMenos => Regex.IsMatch(Pcie, @"x(4|8)\b");
}

public sealed class CpuModel
{
    public string Nome { get; set; } = "";
    public string Marca { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public double IndiceJogo { get; set; }
    public double IndiceMulti { get; set; }
    public string Socket { get; set; } = "";
    public int Tdp { get; set; }
    public string Nucleos { get; set; } = "";
    public string Serie { get; set; } = "";
    public string Lancamento { get; set; } = "";
    public double? PrecoMinEur { get; set; }
    public double? PrecoMaxEur { get; set; }
    /// <summary>true quando o modelo exato não está no índice e foi aproximado por um irmão (ex.: i7-12700 ≈ 95% do i7-12700K).</summary>
    public bool Aproximado { get; set; }

    public bool AVenda => PrecoMinEur is > 0 && PrecoMaxEur is > 0;
    public double? PrecoMedio => AVenda ? (PrecoMinEur!.Value + PrecoMaxEur!.Value) / 2 : null;
    public string FaixaPreco => PerfIndex.FormatRange(PrecoMinEur, PrecoMaxEur);
    public bool X3D => Nome.Contains("X3D", StringComparison.OrdinalIgnoreCase);
}

public sealed class PlatformInfo
{
    public string Memoria { get; set; } = "";
    public string RegexPlaca { get; set; } = "";
    public List<string> Chipsets { get; set; } = new();
    public bool FimDeVida { get; set; }
    public string Nota { get; set; } = "";
    public double? CustoPlacaMinEur { get; set; }
    public double? CustoPlacaMaxEur { get; set; }
    public double? CustoRamMinEur { get; set; }
    public double? CustoRamMaxEur { get; set; }
}

public sealed class CompatRule
{
    public string Socket { get; set; } = "";
    public List<string> Series { get; set; } = new();
    public List<string> Chipsets { get; set; } = new();
    public string Suporte { get; set; } = "";   // nativo | bios | nao
    public string? Bios { get; set; }
}

public sealed class StoreLink
{
    public string Nome { get; set; } = "";
    public string Url { get; set; } = "";      // com {q} no lugar da pesquisa
}

/// <summary>Kit de RAM ou SSD de referência (preço típico novo em Portugal).</summary>
public sealed class PartRef
{
    public string Nome { get; set; } = "";
    public string Pesquisa { get; set; } = "";
    public string Tipo { get; set; } = "";     // DDR4 / DDR5 / NVMe / SATA
    public int Gb { get; set; }
    public double? PrecoMinEur { get; set; }
    public double? PrecoMaxEur { get; set; }
}

public sealed class PerfIndexData
{
    public List<PartRef> Memorias { get; set; } = new();
    public List<PartRef> Discos { get; set; } = new();
    public int Versao { get; set; }
    public string Atualizado { get; set; } = "";
    public string Fonte { get; set; } = "";
    public string BaseGpu { get; set; } = "";
    public string BaseCpu { get; set; } = "";
    public string Aviso { get; set; } = "";
    public List<StoreLink> Lojas { get; set; } = new();
    public List<GpuModel> Gpus { get; set; } = new();
    public List<CpuModel> Cpus { get; set; } = new();
    public Dictionary<string, PlatformInfo> Plataformas { get; set; } = new();
    public List<CompatRule> Compatibilidade { get; set; } = new();
}

public enum CpuSupport { Nativo, PrecisaBios, NaoSuportado, Desconhecido }

/// <summary>Resultado de "este processador encaixa nesta placa?".</summary>
public sealed record CompatResult(CpuSupport Suporte, string? Bios)
{
    public bool Encaixa => Suporte is CpuSupport.Nativo or CpuSupport.PrecisaBios or CpuSupport.Desconhecido;
}

public static class PerfIndex
{
    private const string ResourceName = "Data/perf-index.json";

    private static readonly Lazy<PerfIndexData> _data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Dados embebidos. Nunca é null: se o recurso faltar ou estiver corrompido devolve um índice vazio.</summary>
    public static PerfIndexData Data => _data.Value;

    public static bool Available => Data.Gpus.Count > 0 && Data.Cpus.Count > 0;

    private static PerfIndexData Load()
    {
        try
        {
            using var s = typeof(PerfIndex).Assembly.GetManifestResourceStream(ResourceName);
            if (s == null) return new PerfIndexData();
            var d = JsonSerializer.Deserialize<PerfIndexData>(s, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? new PerfIndexData();
            // Chaves normalizadas pré-calculadas (mais longas primeiro: "RTX 2070 SUPER" antes de "RTX 2070")
            foreach (var g in d.Gpus) GpuKeys[g] = g.Aliases.Append(g.Nome).Select(NormGpu).Where(k => k.Length > 0).Distinct().OrderByDescending(k => k.Length).ToArray();
            foreach (var c in d.Cpus) CpuKeys[c] = c.Aliases.Append(c.Nome).Select(NormCpu).Where(k => k.Length > 0).Distinct().OrderByDescending(k => k.Length).ToArray();
            return d;
        }
        catch { return new PerfIndexData(); }
    }

    private static readonly Dictionary<GpuModel, string[]> GpuKeys = new();
    private static readonly Dictionary<CpuModel, string[]> CpuKeys = new();

    // ---------- Textos ----------

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    /// <summary>"novembro de 2026" (a partir de "2026-11").</summary>
    public static string UpdatedText
    {
        get
        {
            if (DateTime.TryParseExact(Data.Atualizado, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return dt.ToString("MMMM 'de' yyyy", Pt);
            return string.IsNullOrWhiteSpace(Data.Atualizado) ? "data desconhecida" : Data.Atualizado;
        }
    }

    /// <summary>Aviso obrigatório (discreto) em qualquer sítio que mostre estimativas ou preços.</summary>
    public static string Disclaimer => $"Estimativas com base em análises publicadas (atualizado em {UpdatedText}). Os preços mudam todos os dias: confirma na loja.";

    public static string FormatRange(double? min, double? max)
    {
        if (min is not > 0 || max is not > 0) return "já não se vende novo";
        return min == max ? $"~{min:0} €" : $"{min:0}–{max:0} €";
    }

    // ---------- Normalização de nomes ----------

    private static readonly string[] NoiseWords =
        { "nvidia", "geforce", "amd", "radeon", "intel", "processor", "cpu", "graphics", "series", "desktop", "core", "edition", "oc" };

    private static string NormCommon(string s)
    {
        s = (s ?? "").ToLowerInvariant();
        s = Regex.Replace(s, @"\((tm|r|c)\)|™|®|©", " ");
        s = Regex.Replace(s, @"\s(with|w/)\s.*$", "");                 // "Ryzen 5 5600G with Radeon Graphics"
        s = Regex.Replace(s, @"@.*$", "");                              // "i5-12400F CPU @ 2.50GHz"
        s = Regex.Replace(s, @"\b\d+(st|nd|rd|th)\s+gen(eration)?\b", " "); // "12th Gen Intel(R) Core(TM) ..."
        s = Regex.Replace(s, @"\b(\d+|dual|quad|six|eight|ten|twelve|sixteen)[- ]core\b", " "); // "8-Core Processor"
        s = Regex.Replace(s, @"(\d+)\s*gb\b", "$1 gb");                 // "12GB" → "12 gb"
        s = Regex.Replace(s, @"[-_/,]", " ");
        s = Regex.Replace(s, @"[^a-z0-9 ]", " ");
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !NoiseWords.Contains(w));
        return string.Join(' ', words);
    }

    /// <summary>"NVIDIA GeForce RTX 2070 SUPER" → "rtx 2070 super"; "RTX 2080Ti" → "rtx 2080 ti".</summary>
    public static string NormGpu(string name)
    {
        string s = (name ?? "").ToLowerInvariant();
        s = Regex.Replace(s, @"(\d)(ti|super|xtx|xt|gre)\b", "$1 $2");
        return NormCommon(s);
    }

    /// <summary>"AMD Ryzen 7 5700X 8-Core Processor" → "ryzen 7 5700x"; "Intel(R) Core(TM) i5-12400F" → "i5 12400f".</summary>
    public static string NormCpu(string name) => NormCommon(name);

    /// <summary>Gráficos integrados no processador (sem placa dedicada).</summary>
    public static bool IsIntegratedGpu(string? name) =>
        Regex.IsMatch(name ?? "", @"^(AMD )?Radeon\(TM\) Graphics|Radeon Graphics$|Radeon Vega \d+ Graphics|Radeon \d{3}M\b|(Intel.*)?(UHD|HD|Iris(\(R\))?( Xe)?) Graphics|Intel\(R\) Graphics$|^Intel Graphics$|Microsoft Basic Display",
                      RegexOptions.IgnoreCase);

    /// <summary>Modelo aproximado para gráficos integrados (Radeon 780M/Vega/Intel UHD ficam muito abaixo de qualquer placa dedicada atual).</summary>
    public static GpuModel IntegratedGpu(string? name) => new()
    {
        Nome = string.IsNullOrWhiteSpace(name) ? "Gráficos integrados" : name.Trim(),
        Marca = "?", Indice1080 = 15, Indice1440 = 11, Indice4k = 7, Vram = 0, Tdp = 0, PsuW = 0,
        Conector = "nenhum", Pcie = "", Tecnologia = "",
    };

    /// <summary>GPU de portátil: o índice de secretária não se aplica e a placa não se troca.</summary>
    public static bool IsLaptopGpu(string name) =>
        Regex.IsMatch(name ?? "", @"laptop|mobile|max[- ]?q|notebook|\b\d{4}m\b", RegexOptions.IgnoreCase);

    // Chave contida no nome em fronteira de palavra ("rx 7900 xt" não apanha "rx 7900 xtx")
    private static bool ContainsKey(string name, string key) => (" " + name + " ").Contains(" " + key + " ", StringComparison.Ordinal);

    // Segunda tentativa sem espaços ("rtx2070super"), exigindo que não continue com letra/dígito
    private static bool ContainsCompact(string name, string key)
    {
        string n = name.Replace(" ", ""), k = key.Replace(" ", "");
        if (k.Length < 4) return false;
        int i = n.IndexOf(k, StringComparison.Ordinal);
        while (i >= 0)
        {
            bool okL = i == 0 || !char.IsLetterOrDigit(n[i - 1]) || (char.IsLetter(n[i - 1]) != char.IsLetter(k[0]));
            bool okR = i + k.Length == n.Length || !char.IsLetterOrDigit(n[i + k.Length]);
            if (okL && okR) return true;
            i = n.IndexOf(k, i + 1, StringComparison.Ordinal);
        }
        return false;
    }

    // ---------- Procura ----------

    /// <summary>Procura a GPU pelo nome do Windows/driver. Com várias versões do mesmo nome (3060 12/8 GB) escolhe pela VRAM.</summary>
    public static GpuModel? FindGpu(string? name, double vramGb = 0)
    {
        if (string.IsNullOrWhiteSpace(name) || IsLaptopGpu(name)) return null;
        string n = NormGpu(name);
        if (n.Length == 0) return null;
        var hits = Best(Data.Gpus, g => GpuKeys.TryGetValue(g, out var k) ? k : Array.Empty<string>(), n);
        if (hits.Count == 0) return null;
        if (hits.Count == 1 || vramGb <= 0) return hits[0];
        return hits.OrderBy(g => Math.Abs(g.Vram - vramGb)).First();
    }

    /// <summary>Procura a CPU pelo nome do Windows. Intel sem o modelo exato (ex.: i7-12700) é aproximado pelo irmão K/F.</summary>
    public static CpuModel? FindCpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string n = NormCpu(name);
        if (n.Length == 0) return null;
        var hits = Best(Data.Cpus, c => CpuKeys.TryGetValue(c, out var k) ? k : Array.Empty<string>(), n);
        if (hits.Count > 0) return hits[0];

        // Aproximação Intel: "i7 12700" / "i7 12700f" / "i5 13600kf" sem entrada própria → irmão mais próximo
        var m = Regex.Match(n, @"\b(i[3579]|ultra [579]) (\d{3,5})([a-z]*)\b");
        if (m.Success)
        {
            string fam = m.Groups[1].Value, num = m.Groups[2].Value, suf = m.Groups[3].Value;
            bool wantK = suf.Contains('k');
            foreach (var trySuf in wantK ? new[] { "k", "kf", "" } : new[] { "", "f", "k" })
            {
                var sib = Best(Data.Cpus, c => CpuKeys.TryGetValue(c, out var k) ? k : Array.Empty<string>(), $"{fam} {num}{trySuf}");
                if (sib.Count == 0) continue;
                var b = sib[0];
                // Não-K ≈ 95% do K em jogos e ~88% em multi (limites de energia mais baixos)
                double fj = !wantK && b.Nome.EndsWith("K") ? 0.95 : 1.0, fm = !wantK && b.Nome.EndsWith("K") ? 0.88 : 1.0;
                return new CpuModel
                {
                    Nome = Regex.Replace(Regex.Replace(name, @"\((TM|R)\)|CPU|@.*$|\d+th Gen|Intel", "", RegexOptions.IgnoreCase), @"\s+", " ").Trim(),
                    Marca = b.Marca, Aliases = new(), IndiceJogo = Math.Round(b.IndiceJogo * fj), IndiceMulti = Math.Round(b.IndiceMulti * fm),
                    Socket = b.Socket, Tdp = b.Tdp, Nucleos = b.Nucleos, Serie = b.Serie, Lancamento = b.Lancamento,
                    PrecoMinEur = null, PrecoMaxEur = null, Aproximado = true,
                };
            }
        }
        return null;
    }

    /// <summary>Entradas cuja chave mais longa encaixa no nome; empates (mesma chave) ficam todos, pela ordem do ficheiro.</summary>
    private static List<T> Best<T>(List<T> list, Func<T, string[]> keys, string name)
    {
        int best = 0;
        var res = new List<T>();
        foreach (var compact in new[] { false, true })
        {
            foreach (var item in list)
            {
                foreach (var k in keys(item))
                {
                    if (k.Length < best) break;   // chaves ordenadas por tamanho
                    if (!(compact ? ContainsCompact(name, k) : ContainsKey(name, k))) continue;
                    if (k.Length > best) { best = k.Length; res.Clear(); }
                    if (!res.Contains(item)) res.Add(item);
                    break;
                }
            }
            if (res.Count > 0) break;
        }
        return res;
    }

    // ---------- Plataforma e compatibilidade ----------

    /// <summary>Socket e chipset a partir do nome da placa-mãe (regex do JSON); se a placa não disser, usa o socket da CPU atual.</summary>
    public static (string? Socket, string? Chipset) DetectPlatform(string? board, CpuModel? cpu = null)
    {
        if (!string.IsNullOrWhiteSpace(board))
            foreach (var (sock, p) in Data.Plataformas)
            {
                if (string.IsNullOrEmpty(p.RegexPlaca)) continue;
                var m = Regex.Match(board, p.RegexPlaca, RegexOptions.IgnoreCase);
                if (m.Success) return (sock, m.Groups[1].Value.ToUpperInvariant());
            }
        return (cpu?.Socket is { Length: > 0 } s ? s : null, null);
    }

    public static PlatformInfo? Platform(string? socket) =>
        socket != null && Data.Plataformas.TryGetValue(socket, out var p) ? p : null;

    /// <summary>O processador candidato encaixa na placa? Diz também se é preciso atualizar a BIOS e para que versão.</summary>
    public static CompatResult Compat(CpuModel candidate, string? socket, string? chipset)
    {
        if (socket == null) return new(CpuSupport.Desconhecido, null);
        if (!string.Equals(candidate.Socket, socket, StringComparison.OrdinalIgnoreCase)) return new(CpuSupport.NaoSuportado, null);
        if (chipset == null) return new(CpuSupport.Desconhecido, null);
        foreach (var r in Data.Compatibilidade)
        {
            if (!string.Equals(r.Socket, socket, StringComparison.OrdinalIgnoreCase)) continue;
            if (!r.Series.Contains(candidate.Serie)) continue;
            // B650E → regra de B650E; se só existir "B650" na regra aceita também
            if (!r.Chipsets.Any(c => string.Equals(c, chipset, StringComparison.OrdinalIgnoreCase) ||
                                     chipset.StartsWith(c, StringComparison.OrdinalIgnoreCase) && chipset.Length == c.Length + 1)) continue;
            return r.Suporte switch
            {
                "nativo" => new(CpuSupport.Nativo, null),
                "bios" => new(CpuSupport.PrecisaBios, r.Bios),
                "nao" => new(CpuSupport.NaoSuportado, null),
                _ => new(CpuSupport.Desconhecido, null),
            };
        }
        return new(CpuSupport.Desconhecido, null);
    }

    /// <summary>Custo de mudar de plataforma (placa + 32 GB de RAM) em euros, ou null se não houver referência.</summary>
    public static (double Min, double Max)? PlatformSwitchCost(string socket)
    {
        var p = Platform(socket);
        if (p?.CustoPlacaMinEur is not > 0 || p.CustoRamMinEur is not > 0) return null;
        return (p.CustoPlacaMinEur!.Value + p.CustoRamMinEur!.Value, (p.CustoPlacaMaxEur ?? p.CustoPlacaMinEur!.Value) + (p.CustoRamMaxEur ?? p.CustoRamMinEur!.Value));
    }

    // ---------- Lojas ("Ver preço atual") ----------

    /// <summary>Pesquisas já preenchidas nas lojas/comparador PT (o utilizador vê o preço do dia no browser; não há scraping).</summary>
    public static IReadOnlyList<(string Loja, string Url)> PriceLinks(string query)
    {
        string q = Uri.EscapeDataString((query ?? "").Trim()).Replace("%20", "+");
        var lojas = Data.Lojas.Count > 0 ? Data.Lojas : new List<StoreLink>
        {
            new() { Nome = "KuantoKusta", Url = "https://www.kuantokusta.pt/search?q={q}" },
            new() { Nome = "PCDiga", Url = "https://www.pcdiga.com/search?q={q}" },
            new() { Nome = "Globaldata", Url = "https://www.globaldata.pt/catalogsearch/result/?q={q}" },
            new() { Nome = "PcComponentes", Url = "https://www.pccomponentes.pt/buscar/?query={q}" },
        };
        return lojas.Where(l => l.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    .Select(l => (l.Nome, l.Url.Replace("{q}", q))).ToList();
    }

    /// <summary>Abre o URL no browser predefinido. Só https.</summary>
    public static bool OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
        catch { return false; }
    }

    /// <summary>Texto de pesquisa curto para lojas: "RTX 5070", "Ryzen 7 5700X3D", "Core i5-14600K".</summary>
    public static string SearchQuery(string modelName) =>
        Regex.Replace(modelName, @"\s+\d+ GB$", m => m.Value.Replace(" GB", "GB")).Trim();
}

/// <summary>Uma peça candidata com ganho estimado e custo (usado pelo scan e pelas sessões).</summary>
public sealed record UpgradeCandidate(
    string Area,                       // "GPU" | "CPU"
    string Modelo,
    string Pesquisa,                   // texto para "Ver preço atual"
    double PrecoMin, double PrecoMax,
    double? PrecoUtilizador,           // "Encontrei por ___ €" (guardado localmente)
    double ExtraMin, double ExtraMax,  // plataforma nova (placa + RAM) quando a CPU não encaixa
    double GanhoPct,                   // +X% FPS estimado, já limitado pelo teto do outro componente
    double GanhoBrutoPct,              // só a peça, sem esse limite
    bool LimitadoPeloOutro,
    string? Bios, int? PsuW, string? Conector,
    List<string> Notas)
{
    /// <summary>Custo usado nas contas: preço que o utilizador encontrou ou o meio da faixa, mais a plataforma.</summary>
    public double Custo => (PrecoUtilizador is > 0 ? PrecoUtilizador.Value : (PrecoMin + PrecoMax) / 2) + (ExtraMin + ExtraMax) / 2;
    public double GanhoPorEuro => Custo > 0 ? GanhoPct / Custo : 0;
    public bool PrecisaPlataforma => ExtraMax > 0;
    public string FaixaPreco => ExtraMax > 0
        ? $"{PrecoMin + ExtraMin:0}–{PrecoMax + ExtraMax:0} € (com placa-mãe e RAM)"
        : PerfIndex.FormatRange(PrecoMin, PrecoMax);
    public string GanhoTexto => $"+{GanhoPct:0}% FPS estimado";
}

/// <summary>Medições que ancoram as contas. Sem FPS medidos usa a proporção entre índices (menos fiável).</summary>
public sealed record UpgradeContext(
    string CpuName, string GpuName, double VramGB, string Board,
    int AlturaEcra = 1440,             // 1080 / 1440 / 2160
    double? CpuFps = null,             // teto de FPS da CPU medido (GameSimBench)
    double? GpuFps = null,             // FPS da GPU medido à resolução do ecrã (GpuGameBench)
    bool Portatil = false);

public static class UpgradeMath
{
    // Quanto índice de GPU (RTX 4070 = 100) uma CPU com índice de jogo 100 (7700X) consegue alimentar, por resolução.
    // Só se usa quando não há FPS medidos: em 1080p a CPU aparece mais cedo.
    private static double CpuFeedFactor(int altura) => altura <= 1200 ? 1.5 : altura <= 1700 ? 2.0 : 2.6;

    /// <summary>Placas/CPUs que só dão PCIe 3.0 à gráfica (placas de 8 linhas perdem um pouco aí).</summary>
    private static bool Pcie3(string? socket, string? chipset, CpuModel? cpu)
    {
        if (cpu?.Serie is "cml" or "cfl8" or "cfl9" or "zen1" or "zen1apu" or "zenplus" or "zenplusapu" or "zen3apu") return true;
        return socket switch
        {
            "LGA1151v2" => true,
            "LGA1200" => chipset is "H410" or "B460" or "H510",
            "AM4" => chipset is "A320" or "B350" or "X370" or "B450" or "X470" or "A520",
            _ => false,
        };
    }

    /// <summary>Candidatos de GPU à venda, com ganho limitado pelo teto da CPU.</summary>
    /// <param name="current">GPU atual já resolvida (ex.: <see cref="PerfIndex.IntegratedGpu"/>); null = procurar pelo nome.</param>
    public static List<UpgradeCandidate> GpuCandidates(UpgradeContext ctx, double minGanhoPct = 25, GpuModel? current = null)
    {
        var res = new List<UpgradeCandidate>();
        if (!PerfIndex.Available || ctx.Portatil || PerfIndex.IsLaptopGpu(ctx.GpuName)) return res;
        var cur = current ?? PerfIndex.FindGpu(ctx.GpuName, ctx.VramGB);
        if (cur == null) return res;
        var cpu = PerfIndex.FindCpu(ctx.CpuName);
        var (socket, chipset) = PerfIndex.DetectPlatform(ctx.Board, cpu);
        bool pcie3 = Pcie3(socket, chipset, cpu);
        double curIdx = cur.Indice(ctx.AlturaEcra);
        if (curIdx <= 0) return res;

        foreach (var g in PerfIndex.Data.Gpus)
        {
            if (!g.AVenda || ReferenceEquals(g, cur)) continue;
            if (g.Vram < Math.Min(cur.Vram, 16)) continue;                 // nunca descer de VRAM
            if (ctx.AlturaEcra > 1200 && g.Vram < 12) continue;            // 1440p/4K: 8 GB já é curto
            double idx = g.Indice(ctx.AlturaEcra);
            if (pcie3 && g.PcieX8OuMenos) idx *= g.Vram <= 8 ? 0.95 : 0.98;
            double bruto = idx / curIdx - 1, ganho;
            bool limit;
            if (ctx.CpuFps is > 0 && ctx.GpuFps is > 0)
            {
                double antes = Math.Min(ctx.GpuFps.Value, ctx.CpuFps.Value);
                double novoGpu = ctx.GpuFps.Value * idx / curIdx;
                ganho = Math.Min(novoGpu, ctx.CpuFps.Value) / antes - 1;
                limit = novoGpu > ctx.CpuFps.Value;
            }
            else
            {
                double cap = (cpu?.IndiceJogo ?? 100) * CpuFeedFactor(ctx.AlturaEcra);
                ganho = Math.Min(idx, cap) / Math.Min(curIdx, cap) - 1;
                limit = idx > cap;
            }
            if (ganho * 100 < minGanhoPct) continue;

            var notas = new List<string>();
            if (limit) notas.Add("parte do ganho fica presa no processador");
            if (pcie3 && g.PcieX8OuMenos) notas.Add("usa só 8 linhas PCIe: numa placa PCIe 3.0 perde um pouco");
            if (g.Tdp > cur.Tdp + 40 || g.Conector.StartsWith("12V", StringComparison.Ordinal))
                notas.Add($"fonte de {g.PsuW} W ou mais, com ligação {g.Conector}");
            if (!string.IsNullOrEmpty(g.Tecnologia) && g.Tecnologia != cur.Tecnologia) notas.Add(g.Tecnologia);

            res.Add(new UpgradeCandidate("GPU", g.Nome, PerfIndex.SearchQuery(g.Nome), g.PrecoMinEur!.Value, g.PrecoMaxEur!.Value,
                UserPrices.Get(g.Nome), 0, 0, Math.Round(ganho * 100), Math.Round(bruto * 100), limit,
                null, g.PsuW, g.Conector, notas));
        }
        return res;
    }

    /// <summary>Candidatos de CPU: primeiro os que encaixam na placa atual; se a plataforma acabou e o ganho é curto, plataformas novas.</summary>
    public static List<UpgradeCandidate> CpuCandidates(UpgradeContext ctx, double minGanhoPct = 10)
    {
        var res = new List<UpgradeCandidate>();
        if (!PerfIndex.Available || ctx.Portatil) return res;
        var cur = PerfIndex.FindCpu(ctx.CpuName);
        if (cur == null || cur.IndiceJogo <= 0) return res;
        var gpu = PerfIndex.FindGpu(ctx.GpuName, ctx.VramGB);
        var (socketDet, chipset) = PerfIndex.DetectPlatform(ctx.Board, cur);
        string socket = socketDet ?? cur.Socket;
        // Placa de outra plataforma que a CPU (nome estranho): confia na CPU
        if (!string.Equals(socket, cur.Socket, StringComparison.OrdinalIgnoreCase)) { socket = cur.Socket; chipset = null; }

        double Gain(CpuModel c, out bool limit, out double bruto)
        {
            double r = c.IndiceJogo / cur.IndiceJogo;
            bruto = r - 1;
            if (ctx.CpuFps is > 0 && ctx.GpuFps is > 0)
            {
                double antes = Math.Min(ctx.CpuFps.Value, ctx.GpuFps.Value);
                double novoCpu = ctx.CpuFps.Value * r;
                limit = novoCpu > ctx.GpuFps.Value;
                return Math.Min(novoCpu, ctx.GpuFps.Value) / antes - 1;
            }
            if (gpu == null) { limit = false; return bruto; }
            double cap = gpu.Indice(ctx.AlturaEcra) / CpuFeedFactor(ctx.AlturaEcra); // índice de CPU que a GPU consegue aproveitar
            limit = c.IndiceJogo > cap;
            return Math.Min(c.IndiceJogo, cap) / Math.Min(cur.IndiceJogo, cap) - 1;
        }

        // 1) Mesma placa
        foreach (var c in PerfIndex.Data.Cpus)
        {
            if (!c.AVenda || c.Socket != socket || c.Nome == cur.Nome) continue;
            var compat = PerfIndex.Compat(c, socket, chipset);
            if (!compat.Encaixa) continue;
            double g = Gain(c, out bool limit, out double bruto);
            if (g * 100 < minGanhoPct) continue;
            var notas = new List<string>
            {
                compat.Suporte == CpuSupport.Desconhecido
                    ? "mesmo socket da tua placa: confirma a lista de CPUs suportadas no site do fabricante"
                    : $"encaixa na tua placa ({chipset})"
            };
            if (compat.Suporte == CpuSupport.PrecisaBios) notas.Add($"atualiza a BIOS antes de trocar ({compat.Bios})");
            if (limit) notas.Add("parte do ganho fica presa na gráfica");
            if (c.Tdp > cur.Tdp + 30) notas.Add("aquece mais: confirma que o cooler aguenta");
            res.Add(new UpgradeCandidate("CPU", c.Nome, PerfIndex.SearchQuery(c.Nome), c.PrecoMinEur!.Value, c.PrecoMaxEur!.Value,
                UserPrices.Get(c.Nome), 0, 0, Math.Round(g * 100), Math.Round(bruto * 100), limit, compat.Bios, null, null, notas));
        }

        // 2) Plataforma nova, quando a atual acabou e não há ganho bom na mesma placa
        var plat = PerfIndex.Platform(socket);
        double bestSame = res.Count > 0 ? res.Max(r => r.GanhoPct) : 0;
        if ((plat == null || plat.FimDeVida) && bestSame < 20)
        {
            foreach (var c in PerfIndex.Data.Cpus)
            {
                if (!c.AVenda || c.Socket == socket) continue;
                var cost = PerfIndex.PlatformSwitchCost(c.Socket);
                if (cost == null) continue;
                double g = Gain(c, out bool limit, out double bruto);
                if (g * 100 < Math.Max(minGanhoPct, 20)) continue;
                var p = PerfIndex.Platform(c.Socket);
                var notas = new List<string> { $"plataforma nova ({c.Socket}): placa-mãe e 32 GB de {p?.Memoria ?? "RAM"} novas" };
                if (limit) notas.Add("parte do ganho fica presa na gráfica");
                res.Add(new UpgradeCandidate("CPU", c.Nome, PerfIndex.SearchQuery(c.Nome), c.PrecoMinEur!.Value, c.PrecoMaxEur!.Value,
                    UserPrices.Get(c.Nome), cost.Value.Min, cost.Value.Max, Math.Round(g * 100), Math.Round(bruto * 100), limit, null, null, null, notas));
            }
        }
        return res;
    }

    /// <summary>2–3 opções: melhor custo/benefício, equilibrado e máximo útil (o mais barato a 3 pontos do maior ganho).</summary>
    public static List<(string Escalao, UpgradeCandidate C)> Pick(List<UpgradeCandidate> all)
    {
        var res = new List<(string, UpgradeCandidate)>();
        if (all.Count == 0) return res;
        var valor = all.OrderByDescending(c => c.GanhoPorEuro).ThenByDescending(c => c.GanhoPct).First();
        double top = all.Max(c => c.GanhoPct);
        var maximo = all.Where(c => c.GanhoPct >= top - 3).OrderBy(c => c.Custo).First();
        res.Add(("Melhor custo/benefício", valor));
        double mid = (valor.GanhoPct + maximo.GanhoPct) / 2;
        var equil = all.Where(c => c != valor && c != maximo && c.GanhoPct >= mid && c.GanhoPct < maximo.GanhoPct - 3 && c.Custo < maximo.Custo)
                       .OrderByDescending(c => c.GanhoPorEuro).FirstOrDefault();
        if (equil != null) res.Add(("Equilibrado", equil));
        if (maximo != valor && maximo.GanhoPct > valor.GanhoPct + 5) res.Add(("Máximo", maximo));
        return res;
    }
}

/// <summary>"Encontrei por ___ €": preços que o utilizador viu na loja, guardados localmente (scan\precos.json).</summary>
public static class UserPrices
{
    private static readonly object Gate = new();
    private static Dictionary<string, UserPrice>? _cache;
    private static string FilePath => Path.Combine(FrameForge.Core.AppPaths.Root, "scan", "precos.json");

    public sealed class UserPrice
    {
        public double Eur { get; set; }
        public DateTime Data { get; set; }
    }

    private static Dictionary<string, UserPrice> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, UserPrice>>(File.ReadAllText(FilePath));
                if (d != null) _cache = new Dictionary<string, UserPrice>(d, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
        return _cache ??= new Dictionary<string, UserPrice>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Preço que o utilizador indicou (só conta durante 60 dias: os preços mudam).</summary>
    public static double? Get(string model)
    {
        lock (Gate)
        {
            var d = Load();
            return d.TryGetValue(model, out var p) && p.Eur > 0 && (DateTime.Now - p.Data).TotalDays <= 60 ? p.Eur : null;
        }
    }

    /// <summary>Guarda (ou apaga, com null/0) o preço encontrado.</summary>
    public static void Set(string model, double? eur)
    {
        lock (Gate)
        {
            var d = Load();
            if (eur is > 0 and < 20000) d[model] = new UserPrice { Eur = Math.Round(eur.Value, 2), Data = DateTime.Now };
            else d.Remove(model);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
