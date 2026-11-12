using FrameForge.Analysis;
using FrameForge.Hardware;

namespace FrameForge.Optimize;

public sealed record Advice(int Severity /*1 dica, 2 aviso, 3 importante*/, string Area, string Title, string Detail, string Action);

/// <summary>
/// Lê o hardware e o Windows e aponta o que está a custar desempenho no TEU PC (não conselhos genéricos):
/// RAM sem XMP/misturada/canal único, monitor abaixo dos Hz máximos, PCIe a meia velocidade, drivers antigos, conflitos de ferramentas…
/// </summary>
public static class HardwareAdvisor
{
    public static List<Advice> Analyze(SystemProbe p)
    {
        var a = new List<Advice>();
        Ram(p, a);
        Monitor(p, a);
        Gpu(p, a);
        Cpu(p, a);
        Windows(p, a);
        Storage(p, a);
        Tools(p, a);
        return a.OrderByDescending(x => x.Severity).ToList();
    }

    private static void Ram(SystemProbe p, List<Advice> a)
    {
        if (p.Ram.Count == 0) return;
        int cfg = p.Ram.Min(r => r.ConfiguredMHz);
        var rated = p.Ram.Where(r => r.RatedMHz != null).Select(r => r.RatedMHz!.Value).ToList();
        int? slowestRated = rated.Count > 0 ? rated.Min() : null;
        var kits = p.Ram.GroupBy(r => r.PartNumber).ToList();

        if (kits.Count > 1)
        {
            string list = string.Join(" + ", kits.Select(k => $"{k.Count()}× {k.Key} ({k.First().SizeGB:0} GB" + (k.First().RatedMHz is int m ? $", {m} MHz" + (k.First().RatedCl is int cl ? $" CL{cl}" : "") : "") + ")"));
            a.Add(new Advice(3, "RAM", "Pentes de RAM misturados",
                $"Tens {kits.Count} modelos diferentes: {list}. Todos têm de correr à velocidade e latências do mais fraco, e o XMP/EXPO de um kit pode não ser estável com o outro. " +
                $"Neste momento estão a {cfg} MHz.",
                "Testa duas opções e compara no relatório de sessão (1% low): (A) só os 2 pentes iguais e mais rápidos com XMP ligado; (B) os 4 com velocidade/latências manuais e testados com TestMem5/MemTest86. " +
                "Em Ryzen 5000, 2 pentes a 3600 CL16–18 costumam ser mais rápidos em jogos do que 4 pentes misturados a 3200 com latências folgadas. A longo prazo: um kit 2×16 GB 3600 CL16 igual."));
        }

        if (slowestRated is int sr && cfg < sr - 150)
            a.Add(new Advice(3, "RAM", "XMP/EXPO desligado: a RAM está abaixo da velocidade para que foi feita",
                $"A RAM corre a {cfg} MHz mas o pente mais lento é vendido para {sr} MHz.",
                "Na BIOS ativa o perfil XMP (Intel / Gigabyte chama-lhe 'Extreme Memory Profile (X.M.P.)') ou EXPO/DOCP. Em jogos limitados por CPU dá 5–15% de FPS e 1% low melhores."));

        // Canais: rótulos tipo "P0 CHANNEL A" / "ChannelA-DIMM0"
        var channels = p.Ram.Select(r => ChannelOf(r)).Where(c => c != null).Distinct().ToList();
        if (p.Ram.Count == 1)
            a.Add(new Advice(3, "RAM", "RAM em canal único (single channel)", "Só tens 1 pente: a largura de banda da memória fica a metade.",
                "Adiciona um segundo pente igual. Em jogos isto dá muitas vezes +20–40% no 1% low (sobretudo em gráficos integrados e Ryzen)."));
        else if (channels.Count == 1)
            a.Add(new Advice(3, "RAM", "Os pentes estão todos no mesmo canal", $"Os {p.Ram.Count} pentes estão no canal {channels[0]}.",
                "Muda os pentes para as ranhuras indicadas no manual (normalmente A2 e B2, a 2.ª e 4.ª a contar do processador)."));

        if (p.RamGB < 15)
            a.Add(new Advice(2, "RAM", $"Só {p.RamGB:0} GB de RAM", "Jogos atuais + Discord + browser passam facilmente dos 12 GB; o Windows começa a usar o disco e aparecem quebras.",
                "16 GB é o mínimo hoje; 32 GB dá folga para jogar com browser/stream abertos."));
    }

    private static string? ChannelOf(RamModule r)
    {
        string s = (r.Bank + " " + r.Slot).ToUpperInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(s, @"CHANNEL\s*([A-D])|CHANNEL([A-D])|\b([A-D])\d\b|DIMM_([A-D])");
        if (!m.Success) return null;
        return new[] { m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value }.First(x => x.Length > 0);
    }

    private static void Monitor(SystemProbe p, List<Advice> a)
    {
        foreach (var d in p.Displays)
        {
            if (d.MaxHz > d.Hz + 5)
                a.Add(new Advice(3, "Monitor", $"Monitor a {d.Hz} Hz mas suporta {d.MaxHz} Hz",
                    $"O ecrã {(d.Primary ? "principal" : d.Device)} ({d.Width}×{d.Height}) está configurado abaixo do máximo. É o erro mais comum: o jogo pode dar 200 FPS mas só vês {d.Hz}.",
                    "Definições → Sistema → Ecrã → Ecrã avançado → Taxa de atualização: escolhe o valor mais alto. Usa cabo DisplayPort (ou HDMI 2.0+) se não aparecer."));
            if (d.Adapter.Contains("Parsec", StringComparison.OrdinalIgnoreCase) || d.Adapter.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                a.Add(new Advice(2, "Monitor", "Ecrã virtual ativo", $"'{d.Adapter}' está ligado como monitor. Jogos podem abrir nele, escolher a taxa de atualização errada ou perder o G-Sync.",
                    "Desliga o ecrã virtual do Parsec quando não estás a usar streaming (Parsec → Settings → Host → Virtual Displays)."));
        }
        if (p.OtherAdapters.Any(x => x.Contains("Parsec", StringComparison.OrdinalIgnoreCase)) && !p.Displays.Any(d => d.Adapter.Contains("Parsec")))
            a.Add(new Advice(1, "Monitor", "Driver de ecrã virtual do Parsec instalado", "Não está ativo agora, por isso não afeta os jogos.", "Nada a fazer, apenas atenção se o ativares."));
    }

    private static void Gpu(SystemProbe p, List<Advice> a)
    {
        if (p.PcieWidth is (uint cur, uint max) && cur < max && cur > 0)
            a.Add(new Advice(3, "GPU", $"Placa gráfica a correr em PCIe x{cur} (suporta x{max})",
                "A GPU está a usar menos linhas PCIe do que devia. Pode ser a ranhura errada, pó/contacto ou partilha de linhas com um SSD M.2.",
                "Confirma que a placa está na ranhura PCIe de cima (x16), bem encaixada, e vê no manual se algum M.2 rouba linhas a essa ranhura."));

        if (p.GpuDriverDate is DateTime dd && (DateTime.Now - dd).TotalDays > 180)
            a.Add(new Advice(2, "GPU", "Driver da placa gráfica com mais de 6 meses",
                $"Driver {p.GpuDriver} de {dd:MM/yyyy}. Drivers novos trazem otimizações para jogos recentes e correções de quebras.",
                "Atualiza pela NVIDIA App/AMD Adrenalin. Depois de atualizar, as 2–3 primeiras partidas podem ter quebras enquanto a cache de shaders se refaz: é normal."));

        if (p.GpuName.Contains("NVIDIA") && p.ResizableBar == false && System.Text.RegularExpressions.Regex.IsMatch(p.GpuName, @"RTX (30|40|50)\d0"))
            a.Add(new Advice(2, "GPU", "Resizable BAR desligado", "A tua placa suporta Resizable BAR mas está desligado.",
                "Na BIOS: ativa 'Above 4G Decoding' e 'Re-Size BAR Support' (requer arranque UEFI/CSM desligado). Ganho de 0–10% conforme o jogo."));

        if (p.GpuName.Contains("NVIDIA"))
            a.Add(new Advice(1, "GPU", "Painel de Controlo NVIDIA: 3 definições que valem a pena",
                "Estas definições globais reduzem quebras e latência sem custo de FPS.",
                "Gerir definições 3D → (1) 'Tamanho da cache de sombreamento' = 10 GB ou Ilimitado (menos quebras de compilação de shaders); " +
                "(2) 'Modo de baixa latência' = Ligado (ou usa o Reflex dentro do jogo, que é melhor); (3) por jogo competitivo: 'Modo de gestão de energia' = Preferir desempenho máximo."));
    }

    private static void Cpu(SystemProbe p, List<Advice> a)
    {
        bool ryzen = p.CpuName.Contains("Ryzen");
        if (ryzen && !p.CpuName.Contains("X3D"))
            a.Add(new Advice(1, "CPU", "Ryzen: PBO + Curve Optimizer (na BIOS)",
                "Os Ryzen 5000/7000/9000 sobem o clock sozinhos enquanto tiverem margem de temperatura e tensão. Um 'Curve Optimizer' negativo baixa a tensão em cada ponto: menos calor = mais boost.",
                "BIOS → Precision Boost Overdrive = Enabled (ou Advanced) → Curve Optimizer = All Cores, Negative, começa em 10 e testa (CoreCycler/OCCT 1 h); se estável sobe para 15–20. " +
                "Ganho típico: −5 a −10 °C e +2–5% em jogos. Se tiveres reinícios ou erros WHEA, baixa o valor. Guia completo no separador Overclock."));

        if (p.CpuName.Contains("Ryzen") && System.Text.RegularExpressions.Regex.IsMatch(p.Board, "B550M DS3H|A520|B450M DS3H|A320", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            a.Add(new Advice(2, "CPU", "Placa-mãe de entrada: cuidado com limites de energia altos",
                $"A {p.Board} tem um VRM (alimentação da CPU) simples e muitas vezes sem dissipador grande. Para CPUs de 65 W como o teu é suficiente, mas PBO com limites 'Motherboard' pode aquecer o VRM.",
                "Se ativares PBO, prefere Curve Optimizer negativo com limites de energia normais em vez de subir PPT/TDC/EDC. Garante fluxo de ar por cima do socket."));

        a.Add(new Advice(1, "CPU", "Upgrade de CPU (se o relatório mostrar 'CPU no limite')", UpgradeAdvisor.Cpu(p.CpuName, p.Board),
            "Só vale a pena se os relatórios de sessão mostrarem a CPU como limite nos jogos que mais jogas."));
    }

    private static void Windows(SystemProbe p, List<Advice> a)
    {
        if (p.HvciRunning == true)
            a.Add(new Advice(1, "Windows", "Integridade da memória (HVCI/VBS) está ligada",
                "É uma proteção de segurança que custa 0–5% de FPS em CPUs recentes (até ~10% em CPUs antigas sem MBEC).",
                "Recomendação: DEIXA LIGADA. Só desligues se medires diferença nos teus jogos e aceitares o risco. Alguns anti-cheats (ex.: FACEIT, Vanguard no Windows 11) " +
                "podem exigir funcionalidades de segurança ligadas. Fica em Segurança do Windows → Segurança do dispositivo → Isolamento do núcleo."));
        if (p.PagefileOff)
            a.Add(new Advice(3, "Windows", "Ficheiro de paginação desligado",
                "Sem ficheiro de paginação, quando a RAM enche os jogos fecham ou travam em vez de abrandar. É um 'tweak' antigo que hoje só causa problemas.",
                "Sistema → Acerca de → Definições avançadas do sistema → Desempenho → Avançadas → Memória virtual → 'Gerir automaticamente'."));
        if (p.GameMode == false)
            a.Add(new Advice(2, "Windows", "Modo de Jogo desligado", "O Modo de Jogo impede o Windows Update de instalar coisas durante o jogo.", "Liga-o no separador Otimização (nível Seguro)."));
        if (p.GameDvr == true)
            a.Add(new Advice(1, "Windows", "Gravação em segundo plano da Xbox Game Bar ativa", "Grava continuamente usando a GPU e o disco.", "Desliga no separador Otimização (nível Seguro)."));
        // Só avisa em planos de poupança: planos personalizados de desempenho (ex.: "Ultra Performance" do ReviOS/Atlas) já servem
        if (!p.IsLaptop && System.Text.RegularExpressions.Regex.IsMatch(p.PowerPlan, "Equilibrado|Balanced|Poupança|Power saver|Economia", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            a.Add(new Advice(1, "Windows", $"Plano de energia: {p.PowerPlan}", "Num desktop, o plano Alto desempenho melhora ligeiramente o 1% low.",
                "Aplica no separador Otimização (nível Seguro) ou só durante os jogos no perfil de cada jogo."));
    }

    private static void Storage(SystemProbe p, List<Advice> a)
    {
        foreach (var d in p.Drives)
        {
            if (d.SizeGB > 0 && d.FreeGB / d.SizeGB < 0.1)
                a.Add(new Advice(2, "Disco", $"Disco {d.Letter} quase cheio ({d.FreeGB:0} GB livres)",
                    "SSDs perto de cheios ficam mais lentos a escrever e o Windows precisa de espaço para a paginação e cache de shaders.",
                    "Mantém pelo menos 10–15% livre."));
            if (d.Media == "HDD")
                a.Add(new Advice(1, "Disco", $"{d.Letter} é um disco rígido (HDD)", $"{d.Model}. Jogos num HDD têm carregamentos longos e quebras ao carregar o mapa.",
                    "Mantém os jogos (sobretudo os competitivos e open-world) num SSD; usa o HDD para arquivo."));
        }
    }

    private static void Tools(SystemProbe p, List<Advice> a)
    {
        if (p.RunningTools.Contains("MSI Afterburner"))
            a.Add(new Advice(2, "Software", "MSI Afterburner aberto",
                "Se usares o overclock do FrameForge ao mesmo tempo, os dois programas vão escrever valores diferentes na placa e o último a escrever ganha.",
                "Usa só um para o overclock. Se mantiveres o Afterburner só pela curva de ventoinha, não apliques perfis de clock nele."));
        if (p.RunningTools.Contains("Overwolf"))
            a.Add(new Advice(1, "Software", "Overwolf aberto", "O Overwolf injeta overlays e consome RAM/CPU.", "Fecha-o quando não precisas das apps dele."));
        int browsers = p.RunningTools.Count(t => t.Contains("Chrome") || t.Contains("Edge"));
        if (browsers > 0)
            a.Add(new Advice(1, "Software", "Browser aberto", "Browsers com muitos separadores usam 1–4 GB de RAM e alguma VRAM, e vídeos usam a GPU.",
                "Fecha-o nos jogos competitivos ou adiciona-o a 'Fechar antes de jogar' no perfil do jogo."));
    }
}
