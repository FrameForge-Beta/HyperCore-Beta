using System.Text.RegularExpressions;

namespace FrameForge.Analysis;

/// <summary>Sugestões de upgrade concretas a partir do hardware detetado (só quando o relatório mostra que o limite é físico).</summary>
public static class UpgradeAdvisor
{
    private static readonly Regex Am4Board = new(@"\b(A320|B350|X370|B450|X470|A520|B550|X570)", RegexOptions.IgnoreCase);
    private static readonly Regex Am5Board = new(@"\b(A620|B650|X670|B850|X870)", RegexOptions.IgnoreCase);
    private static readonly Regex Lga1700 = new(@"\b(H610|B660|H670|Z690|B760|H770|Z790)", RegexOptions.IgnoreCase);

    public static string Cpu(SessionData d) => Cpu(d.Cpu, d.Board);

    public static string Cpu(string cpu, string board)
    {
        bool x3d = cpu.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        if (Am4Board.IsMatch(board) || Regex.IsMatch(cpu, @"Ryzen \d \d{4}(?!\d)"))
        {
            if (Am4Board.IsMatch(board) || Regex.IsMatch(cpu, @"Ryzen \d [1-5]\d{3}"))
                return x3d
                    ? "já tens o melhor processador de jogos para AM4; o passo seguinte é AM5 (ex.: Ryzen 7 9800X3D) com placa-mãe e DDR5 novas."
                    : $"Ryzen 7 5700X3D ou 5800X3D — encaixam na tua placa ({board}) depois de atualizar a BIOS, e em jogos limitados por CPU " +
                      "dão tipicamente +15 a +35% de FPS e 1% low bem melhores, graças à cache 3D V-Cache. É o upgrade com melhor relação custo/ganho para ti.";
        }
        if (Am5Board.IsMatch(board))
            return x3d ? "já tens um X3D: o limite está noutro lado." : "Ryzen 7 7800X3D/9800X3D na mesma placa (atualiza a BIOS).";
        if (Lga1700.IsMatch(board))
            return "Core i5-14600K / i7-14700K na mesma placa (atualiza a BIOS e aplica o microcódigo 0x12B ou mais recente).";
        return "uma plataforma atual (ex.: Ryzen 7 7800X3D/9800X3D em AM5).";
    }

    public static string Gpu(SessionData d)
    {
        string g = d.Gpu;
        string tier = Regex.IsMatch(g, @"(1050|1060|1650|1660|RX 5[5-8]0|RX 6[45]00|2060|3050)", RegexOptions.IgnoreCase)
            ? "uma RTX 5060 Ti 16 GB / RX 9060 XT 16 GB já dá um salto grande"
            : Regex.IsMatch(g, @"(2070|2080|1080|3060|RX 5700|RX 6600|RX 6650|RX 7600)", RegexOptions.IgnoreCase)
                ? "uma RTX 5070 / RX 9070 (12–16 GB) dá cerca do dobro do desempenho e acesso a DLSS 4 / FSR 4"
                : "uma GPU da geração atual um degrau acima da tua";
        string pairing = Regex.IsMatch(d.Cpu, @"Ryzen \d [1-3]\d{3}|i[357]-[4-9]\d{3}", RegexOptions.IgnoreCase)
            ? " Atenção: com o teu processador, em 1080p competitivo parte do ganho pode ficar preso na CPU."
            : "";
        return tier + "; confirma que a fonte tem os conectores e a potência recomendados." + pairing;
    }

    public static string Vram(SessionData d) => d.VramGB switch
    {
        < 7 => "para jogos atuais o mínimo confortável são 8 GB (texturas médias) e 12–16 GB para texturas altas.",
        < 11 => "8 GB já é curto em jogos de 2024+ com texturas altas, sobretudo em 1440p; uma placa de 12–16 GB elimina este tipo de quebra.",
        _ => "tens VRAM suficiente na maioria dos jogos: baixa só as texturas 'Ultra/Cinematográfico'."
    };
}
