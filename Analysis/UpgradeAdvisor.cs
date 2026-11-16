using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FrameForge.Tuning;

namespace FrameForge.Analysis;

/// <summary>
/// Sugestões de upgrade concretas a partir do hardware detetado (só quando o relatório mostra que o limite é físico).
/// Usa o índice embebido (Data/perf-index.json via <see cref="PerfIndex"/>): candidatos compatíveis com a placa,
/// ganho estimado limitado pelo outro componente e ordenado por ganho por euro. Sem índice volta às frases antigas.
/// </summary>
public static class UpgradeAdvisor
{
    private static readonly Regex Am4Board = new(@"\b(A320|B350|X370|B450|X470|A520|B550|X570)", RegexOptions.IgnoreCase);
    private static readonly Regex Am5Board = new(@"\b(A620|B650|X670|B850|X870)", RegexOptions.IgnoreCase);
    private static readonly Regex Lga1700 = new(@"\b(H610|B660|H670|Z690|B760|H770|Z790)", RegexOptions.IgnoreCase);

    // ---------- API pública (usada pelo SessionAnalyzer e pelo HardwareAdvisor) ----------

    public static string Cpu(SessionData d) => CpuText(d.Cpu, d.Board, d.Gpu, d.VramGB) ?? LegacyCpu(d.Cpu, d.Board);

    public static string Cpu(string cpu, string board) => CpuText(cpu, board, null, 0) ?? LegacyCpu(cpu, board);

    public static string Gpu(SessionData d) => GpuText(d) ?? LegacyGpu(d);

    public static string Vram(SessionData d)
    {
        string baseTxt = d.VramGB switch
        {
            < 7 => "para jogos atuais o mínimo confortável são 8 GB (texturas médias) e 12–16 GB para texturas altas.",
            < 11 => "8 GB já é curto em jogos de 2024+ com texturas altas, sobretudo em 1440p; uma placa de 12–16 GB elimina este tipo de quebra.",
            _ => "tens VRAM suficiente na maioria dos jogos: baixa só as texturas 'Ultra/Cinematográfico'."
        };
        if (d.VramGB >= 11) return baseTxt;
        try
        {
            if (!PerfIndex.Available || PerfIndex.IsLaptopGpu(d.Gpu)) return baseTxt;
            int h = ScreenHeight();
            var ctx = new UpgradeContext(d.Cpu, d.Gpu, d.VramGB, d.Board, AlturaEcra: h);
            GpuModel? cur = PerfIndex.IsIntegratedGpu(d.Gpu) ? PerfIndex.IntegratedGpu(d.Gpu) : null;
            double minVram = h > 1200 ? 16 : 12;
            var cheapest = UpgradeMath.GpuCandidates(ctx, minGanhoPct: 0, current: cur)
                .Where(c => (PerfIndex.Data.Gpus.FirstOrDefault(g => g.Nome == c.Modelo)?.Vram ?? 0) >= minVram)
                .OrderBy(c => c.Custo).FirstOrDefault();
            if (cheapest == null) return baseTxt;
            return baseTxt.TrimEnd('.') + $". A opção mais barata do índice com {minVram:0} GB ou mais é a {cheapest.Modelo} " +
                   $"({cheapest.FaixaPreco}, {cheapest.GanhoTexto} {ResText(h)}). {ShortDisclaimer}";
        }
        catch { return baseTxt; }
    }

    // ---------- Cálculo com o índice ----------

    private static string ShortDisclaimer =>
        $"Estimativas com base em análises publicadas (atualizado em {PerfIndex.UpdatedText}); os preços mudam todos os dias: confirma na loja (botão \"Ver preço atual\" no Scan).";

    private static string ResText(int h) => h <= 1200 ? "em 1080p" : h <= 1700 ? "em 1440p" : "em 4K";

    private static string? CpuText(string cpu, string board, string? gpu, double vramGb)
    {
        try
        {
            if (!PerfIndex.Available) return null;
            var cur = PerfIndex.FindCpu(cpu);
            if (cur == null) return null;
            int h = ScreenHeight();
            var ctx = new UpgradeContext(cpu, gpu ?? "", vramGb, board ?? "", AlturaEcra: h);
            var picks = UpgradeMath.Pick(UpgradeMath.CpuCandidates(ctx));
            var (socketDet, chipset) = PerfIndex.DetectPlatform(board, cur);
            string socket = socketDet ?? cur.Socket;
            var plat = PerfIndex.Platform(socket);

            if (picks.Count == 0)
            {
                if (cur.X3D || cur.IndiceJogo >= 125)
                    return $"já tens um dos processadores de jogos mais rápidos ({cur.Nome}): o limite está noutro lado ou no próprio jogo.";
                if (plat?.FimDeVida == true)
                    return $"na tua placa ({socket}) não há no índice um processador à venda que dê um ganho claro face ao {cur.Nome}; " +
                           $"o passo seguinte é uma plataforma nova (AM5 ou LGA1851, com placa-mãe e DDR5). {plat.Nota}".TrimEnd();
                return $"não encontrei no índice um processador compatível com a tua placa que valha a pena face ao {cur.Nome}.";
            }

            var parts = new List<string>();
            foreach (var (esc, c) in picks)
            {
                string extra = c.Bios != null ? $"; atualiza a BIOS antes ({c.Bios})" : "";
                parts.Add($"{esc.ToLowerInvariant()}: {c.Modelo} ({c.GanhoTexto} {ResText(h)}, {c.FaixaPreco}{extra})");
            }
            string txt = string.Join("; ", parts) + ".";
            if (picks.Any(p => p.C.LimitadoPeloOutro))
                txt += " Parte do ganho fica presa na gráfica, por isso o número já vem limitado por ela.";
            if (gpu == null)
                txt += " O ganho é só do processador (sem contar com o teto da tua gráfica).";
            return txt + " " + ShortDisclaimer;
        }
        catch { return null; }
    }

    private static string? GpuText(SessionData d)
    {
        try
        {
            if (!PerfIndex.Available || PerfIndex.IsLaptopGpu(d.Gpu)) return null;
            int h = ScreenHeight();
            GpuModel? cur = PerfIndex.IsIntegratedGpu(d.Gpu) ? PerfIndex.IntegratedGpu(d.Gpu) : null;
            if (cur == null && PerfIndex.FindGpu(d.Gpu, d.VramGB) == null) return null;
            var ctx = new UpgradeContext(d.Cpu, d.Gpu, d.VramGB, d.Board, AlturaEcra: h);
            var picks = UpgradeMath.Pick(UpgradeMath.GpuCandidates(ctx, current: cur));
            if (picks.Count == 0)
                return $"não há no índice uma gráfica à venda que dê um salto claro {ResText(h)} face à tua sem ficar presa no processador; " +
                       "começa por DLSS/FSR e pelo overclock automático do Scan.";

            var parts = picks.Select(p => $"{p.Escalao.ToLowerInvariant()}: {p.C.Modelo} ({p.C.GanhoTexto} {ResText(h)}, {p.C.FaixaPreco})");
            string txt = string.Join("; ", parts) + ".";
            int psu = picks.Max(p => p.C.PsuW ?? 0);
            if (psu > 0) txt += $" Confirma que a fonte tem pelo menos {psu} W e as ligações certas (12V-2x6 ou 8 pinos, conforme o modelo).";
            if (picks.Any(p => p.C.LimitadoPeloOutro))
                txt += $" Atenção: com o teu processador ({PerfIndex.FindCpu(d.Cpu)?.Nome ?? d.Cpu}) parte do ganho fica presa na CPU; o número já conta com isso.";
            return txt + " " + ShortDisclaimer;
        }
        catch { return null; }
    }

    // ---------- Altura do ecrã principal (resolução real, sem escala DPI) ----------

    private static int _screenH;

    private static int ScreenHeight()
    {
        if (_screenH > 0) return _screenH;
        int h = 1080;
        try
        {
            var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettings(null, -1 /* ENUM_CURRENT_SETTINGS */, ref dm) && dm.dmPelsHeight >= 600 && dm.dmPelsHeight <= 4400)
                h = dm.dmPelsHeight;
        }
        catch { }
        return _screenH = h;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? device, int mode, ref DevMode dm);

    // ---------- Frases antigas (só quando o índice não está disponível ou o modelo não é reconhecido) ----------

    private static string LegacyCpu(string cpu, string board)
    {
        cpu ??= ""; board ??= "";
        bool x3d = cpu.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        if (Am4Board.IsMatch(board) || Regex.IsMatch(cpu, @"Ryzen \d \d{4}(?!\d)"))
        {
            if (Am4Board.IsMatch(board) || Regex.IsMatch(cpu, @"Ryzen \d [1-5]\d{3}"))
                return x3d
                    ? "já tens o melhor processador de jogos para AM4; o passo seguinte é AM5 (ex.: Ryzen 7 9800X3D) com placa-mãe e DDR5 novas."
                    : $"Ryzen 7 5700X3D ou 5800X3D — encaixam na tua placa ({board}) depois de atualizar a BIOS e, em jogos limitados pela CPU, " +
                      "costumam dar um ganho claro de FPS e 1% low melhores graças à cache 3D V-Cache.";
        }
        if (Am5Board.IsMatch(board))
            return x3d ? "já tens um X3D: o limite está noutro lado." : "Ryzen 7 7800X3D/9800X3D na mesma placa (atualiza a BIOS).";
        if (Lga1700.IsMatch(board))
            return "Core i5-14600K / i7-14700K na mesma placa (atualiza a BIOS e aplica o microcódigo 0x12B ou mais recente).";
        return "uma plataforma atual (ex.: Ryzen 7 7800X3D/9800X3D em AM5).";
    }

    private static string LegacyGpu(SessionData d)
    {
        string g = d.Gpu ?? "";
        string tier = Regex.IsMatch(g, @"(1050|1060|1650|1660|RX 5[5-8]0|RX 6[45]00|2060|3050)", RegexOptions.IgnoreCase)
            ? "uma RTX 5060 Ti 16 GB / RX 9060 XT 16 GB já dá um salto grande"
            : Regex.IsMatch(g, @"(2070|2080|1080|3060|RX 5700|RX 6600|RX 6650|RX 7600)", RegexOptions.IgnoreCase)
                ? "uma RTX 5070 / RX 9070 (12–16 GB) dá cerca do dobro do desempenho e acesso a DLSS 4 / FSR 4"
                : "uma GPU da geração atual um degrau acima da tua";
        string pairing = Regex.IsMatch(d.Cpu ?? "", @"Ryzen \d [1-3]\d{3}|i[357]-[4-9]\d{3}", RegexOptions.IgnoreCase)
            ? " Atenção: com o teu processador, em 1080p competitivo parte do ganho pode ficar preso na CPU."
            : "";
        return tier + "; confirma que a fonte tem os conectores e a potência recomendados." + pairing;
    }
}
