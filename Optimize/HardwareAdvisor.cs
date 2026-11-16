using FrameForge.Analysis;
using FrameForge.Hardware;
using FrameForge.Tuning;

namespace FrameForge.Optimize;

/// <param name="Page">Página da app que resolve isto (tag para AppServices.Navigate, ex.: "FF:fans"); null se não houver.</param>
/// <param name="PageLabel">Texto do botão ("Abrir Ventoinhas").</param>
public sealed record Advice(int Severity /*1 dica, 2 aviso, 3 importante*/, string Area, string Title, string Detail, string Action,
                            string? Page = null, string? PageLabel = null);

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
            a.Add(new Advice(2, "Windows", "Modo de Jogo desligado", "O Modo de Jogo impede o Windows Update de instalar coisas durante o jogo.", "Liga-o em Níveis de desempenho (nível Seguro)."));
        if (p.GameDvr == true)
            a.Add(new Advice(1, "Windows", "Gravação em segundo plano da Xbox Game Bar ativa", "Grava continuamente usando a GPU e o disco.", "Desliga em Níveis de desempenho (nível Seguro)."));
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
                "Se usares o overclock do HyperCore ao mesmo tempo, os dois programas vão escrever valores diferentes na placa e o último a escrever ganha.",
                "Usa só um para o overclock. Se mantiveres o Afterburner só pela curva de ventoinha, não apliques perfis de clock nele."));
        if (p.RunningTools.Contains("Overwolf"))
            a.Add(new Advice(1, "Software", "Overwolf aberto", "O Overwolf injeta overlays e consome RAM/CPU.", "Fecha-o quando não precisas das apps dele."));
        int browsers = p.RunningTools.Count(t => t.Contains("Chrome") || t.Contains("Edge"));
        if (browsers > 0)
            a.Add(new Advice(1, "Software", "Browser aberto", "Browsers com muitos separadores usam 1–4 GB de RAM e alguma VRAM, e vídeos usam a GPU.",
                "Fecha-o nos jogos competitivos ou adiciona-o a 'Fechar antes de jogar' no perfil do jogo."));
    }

    // ------------------------------------------------------------------ conselhos medidos (scan)

    /// <summary>
    /// Os conselhos do <see cref="Analyze"/> mais os que vêm das MEDIÇÕES do scan (latência/banda da RAM, throttle térmico,
    /// limite de energia, disco lento/doente, HAGS…), sem repetir o mesmo tema. Cada um com ação concreta e, quando existe,
    /// a página da app que resolve.
    /// </summary>
    public static List<Advice> AnalyzeWithScan(SystemProbe? p, ScanReport r)
    {
        var measured = Measured(r, p);
        var basic = p != null ? Analyze(p) : new List<Advice>();
        // O medido substitui o estático do mesmo tema
        if (measured.Any(m => m.Title.StartsWith("XMP", StringComparison.Ordinal))) basic.RemoveAll(b => b.Title.StartsWith("XMP/EXPO desligado", StringComparison.Ordinal));
        if (measured.Any(m => m.Title.Contains("canal", StringComparison.OrdinalIgnoreCase))) basic.RemoveAll(b => b.Title.Contains("canal", StringComparison.OrdinalIgnoreCase));
        if (r.Upgrades.Count > 0) basic.RemoveAll(b => b.Title.StartsWith("Upgrade de CPU", StringComparison.Ordinal));
        return measured.Concat(basic.Select(WithPage))
                       .GroupBy(a => a.Title).Select(g => g.First())
                       .OrderByDescending(a => a.Severity).ToList();
    }

    /// <summary>Página da app para os conselhos estáticos que têm uma (plano de energia, Modo de Jogo, disco cheio, driver…).</summary>
    private static Advice WithPage(Advice a)
    {
        if (a.Page != null) return a;
        if (a.Title.StartsWith("Plano de energia") || a.Title.StartsWith("Modo de Jogo") || a.Title.StartsWith("Gravação em segundo plano"))
            return a with { Page = "FF:optimize", PageLabel = "Abrir Níveis de desempenho" };
        if (a.Area == "Disco" && a.Title.Contains("quase cheio")) return a with { Page = "FF:maintenance", PageLabel = "Abrir Manutenção" };
        if (a.Title.StartsWith("Driver da placa gráfica")) return a with { Page = "PageDriver", PageLabel = "Abrir Drivers" };
        if (a.Area == "Software" && a.Title.Contains("Afterburner")) return a with { Page = "FF:oc", PageLabel = "Abrir Overclock da gráfica" };
        return a;
    }

    /// <summary>Só os conselhos que vêm das medições do scan.</summary>
    public static List<Advice> Measured(ScanReport r, SystemProbe? p)
    {
        var a = new List<Advice>();
        var lim = TuningLimits.For(r.Perfil);
        var inv = r.Inventario;

        // RAM
        if (r.Ram is RamResult ram)
        {
            if (ram.XmpDesligado)
                a.Add(new Advice(3, "RAM", "XMP/EXPO desligado (medido)",
                    $"A RAM corre a {ram.MHz} MHz mas foi feita para {ram.MHzNominal} MHz" + (ram.LatenciaNs is double l ? $"; a latência medida é {l:0} ns." : "."),
                    "Na BIOS liga o perfil XMP/EXPO (o passo a passo está no relatório do scan). Em jogos limitados pela CPU dá 5–15% de FPS e 1% low melhores. Faz um scan a seguir para confirmar que não há erros."));
            else if (ram.LatenciaNs is double lat && lat > BottleneckFromScan.LatencyHigh(inv.RamTipo))
                a.Add(new Advice(2, "RAM", $"Latência da RAM alta ({lat:0} ns)",
                    "A CPU espera mais pela memória do que é normal para este tipo de RAM. Nos jogos limitados pela CPU isto custa FPS e 1% low.",
                    "Confirma na BIOS que o XMP/EXPO está no perfil 1; em Ryzen mantém o FCLK 1:1. Se os pentes forem lentos (ex.: 3200 CL22), um kit 3600 CL16 (DDR4) ou 6000 CL30 (DDR5) resolve."));

            if (ram.CanalUnicoProvavel && inv.Pentes >= 2)
                a.Add(new Advice(3, "RAM", "A RAM parece estar em canal único (medido)",
                    $"Tens {inv.Pentes} pentes, mas a banda medida ({ram.LeituraGBs:0} GB/s) é a de um só canal.",
                    "Põe os pentes nas ranhuras indicadas no manual (normalmente A2 e B2, a 2.ª e a 4.ª a contar do processador)."));
            else if (ram.CanalUnicoProvavel && inv.Pentes == 1)
                a.Add(new Advice(3, "RAM", "RAM em canal único (1 pente)",
                    $"A banda medida é {ram.LeituraGBs:0} GB/s: com um só pente a memória fica a metade da velocidade.",
                    "Junta um segundo pente igual (mesmo modelo). Costuma dar +20–40% no 1% low."));
        }

        // CPU
        if (r.Cpu is CpuResult cpu)
        {
            if (!cpu.Estavel && (cpu.ErroCalculo || cpu.ErrosRam > 0 || cpu.ErrosWhea > 0))
            {
                var what = new List<string>();
                if (cpu.ErroCalculo) what.Add("erros de cálculo");
                if (cpu.ErrosRam > 0) what.Add($"{cpu.ErrosRam} erros de RAM");
                if (cpu.ErrosWhea > 0) what.Add($"{cpu.ErrosWhea} erros de hardware (WHEA)");
                a.Add(new Advice(3, "CPU", "CPU/RAM instáveis no stress",
                    "O scan encontrou " + string.Join(", ", what) + ". Isto dá crashes e ecrãs azuis aleatórios.",
                    "Se tens PBO/Curve Optimizer ou XMP ligados, desliga-os (um de cada vez) e repete o scan. Se acontecer de fábrica: atualiza a BIOS e testa a RAM com o MemTest86.",
                    "PageOc", "Abrir Teste de estabilidade"));
            }
            if (cpu.Stress.ThrottleTermico || cpu.Stress.TempMax >= lim.CpuTempMax - 3)
                a.Add(new Advice(cpu.Stress.ThrottleTermico ? 3 : 2, "CPU",
                    $"CPU quente: {cpu.Stress.TempMax:0} °C no stress" + (cpu.Stress.ThrottleTermico ? " e a perder clock" : ""),
                    "Quando passa do limite, a CPU baixa o clock sozinha para se proteger (throttle): perdes FPS nos jogos mais pesados.",
                    "Limpa o pó do cooler, confirma que a ventoinha do cooler está ligada a CPU_FAN e a rodar, e que a caixa tem entrada e saída de ar. Pasta térmica com mais de 4–5 anos: troca. Na BIOS, Curve Optimizer/undervolt baixa muito a temperatura.",
                    "FF:fans", "Abrir Ventoinhas"));
        }

        // GPU
        if (r.Gpu is GpuResult gpu)
        {
            if (!gpu.Estavel)
                a.Add(new Advice(3, "GPU", "A gráfica falhou em valores de fábrica",
                    "Houve um problema com a gráfica sem overclock: " + string.Join("; ", gpu.Problemas.Take(2)) + ".",
                    "Reinstala o driver de vídeo (instalação limpa), confirma os cabos de energia da gráfica (sem adaptadores/derivações) e que a fonte chega para a placa.",
                    "PageDriver", "Abrir Drivers"));

            bool hot = gpu.Jogo.TempMax >= lim.GpuTempMax - 2 || gpu.Jogo.ThrottleTermico;
            if (hot)
                a.Add(new Advice(gpu.Jogo.ThrottleTermico ? 3 : 2, "GPU", $"Gráfica quente: {gpu.Jogo.TempMax:0} °C numa cena de jogo",
                    "Perto deste valor a gráfica baixa o clock sozinha. A causa costuma ser a caixa com pouco fluxo de ar ou pó nos dissipadores.",
                    "Aumenta a curva de ventoinha da gráfica, limpa o pó e confirma a entrada de ar da caixa. Um undervolt (perfil 'Silencioso / Eficiente') baixa 8–15 °C com quase o mesmo FPS.",
                    "FF:fans", "Abrir Ventoinhas"));
            double? hs = gpu.Jogo.HotspotMax ?? gpu.Termico.HotspotMax;
            double? core = gpu.Jogo.HotspotMax != null ? gpu.Jogo.TempMax : gpu.Termico.TempMax;
            if (hs is double h && core is double c && h - c > 25)
                a.Add(new Advice(2, "GPU", $"Diferença grande entre o hotspot e a temperatura da gráfica ({h - c:0} °C)",
                    $"Hotspot a {h:0} °C com o núcleo a {c:0} °C. Mais de 20–25 °C de diferença indica pasta térmica seca ou contacto fraco do dissipador.",
                    "Se a placa tem mais de 3 anos ou já está fora da garantia, a troca da pasta térmica costuma resolver. Se estiver na garantia, contacta a loja/marca."));
            if (gpu.Jogo.ThrottleEnergia && r.Oc is not { PotenciaPct: > 100 })
                a.Add(new Advice(1, "GPU", "O limite de energia segura a gráfica",
                    "Numa cena de jogo a placa bate no limite de energia de fábrica e não sobe mais o clock.",
                    "Subir o limite de energia (perfil Avançado do scan ou o cursor de energia no Overclock) dá mais 2–5% em jogos pesados, com mais consumo e calor.",
                    "FF:oc", "Abrir Overclock da gráfica"));
            if (gpu.Termico.ThrottleTermico && !hot)
                a.Add(new Advice(1, "GPU", "Em stress máximo a gráfica chega ao limite de temperatura",
                    "Nos jogos está bem, mas em carga máxima contínua (renderização, IA) pode perder clock.",
                    "Uma curva de ventoinha um pouco mais forte resolve.", "FF:fans", "Abrir Ventoinhas"));
        }

        // Discos
        foreach (var d in r.Discos)
        {
            if (d.SaudeAlerta)
                a.Add(new Advice(3, "Disco", $"Disco {d.Unidade} com sinais de desgaste ({d.Saude})",
                    $"{d.Modelo}: o S.M.A.R.T. do disco aponta problemas.",
                    "Faz já uma cópia de segurança dos ficheiros importantes e vê os detalhes na página Discos.", "PageDisks", "Abrir Discos"));
            if (d.LeituraMBs is not double rd) continue;
            bool nvme = d.Tipo == "NVMe";
            bool ssd = nvme || d.Tipo == "SSD";
            if ((nvme && rd < 1000) || (ssd && !nvme && rd < 300))
                a.Add(new Advice(2, "Disco", $"Disco {d.Unidade} mais lento do que devia ({rd:N0} MB/s)",
                    nvme ? "Um SSD NVMe devia passar dos 1500 MB/s; este está muito abaixo." : "Um SSD SATA devia andar perto dos 500 MB/s.",
                    nvme ? "Pode estar numa ranhura M.2 mais lenta (do chipset ou PCIe 3.0 x2), quase cheio ou a aquecer. Vê no manual da placa qual é a ranhura M.2 ligada à CPU."
                         : "Confirma o modo AHCI na BIOS, troca o cabo SATA e usa uma porta SATA da placa (não de controladoras extra). SSDs quase cheios também abrandam.",
                    "PageDisks", "Abrir Discos"));
            if (d.Jogos && d.Tipo == "HDD")
                a.Add(new Advice(2, "Disco", $"Os jogos estão num disco rígido ({d.Unidade}, {rd:N0} MB/s)",
                    "Num HDD os mapas demoram a carregar e os jogos de mundo aberto têm quebras ao carregar texturas.",
                    "Passa os jogos que mais jogas para um SSD (um NVMe de 1 TB é barato e 10–20× mais rápido)."));
        }

        // Windows
        if (inv.Hags == false && System.Text.RegularExpressions.Regex.IsMatch(inv.Gpu, @"RTX (30|40|50)\d0|RX (6|7|9)\d{3}"))
            a.Add(new Advice(1, "Windows", "Agendamento de GPU acelerado por hardware desligado",
                "Nas gráficas recentes reduz um pouco a latência e é obrigatório para a geração de frames (DLSS 3/4 Frame Generation).",
                "Definições → Sistema → Ecrã → Gráficos → Alterar as predefinições de gráficos → ligar 'Agendamento de GPU acelerado por hardware' e reiniciar."));

        return a.OrderByDescending(x => x.Severity).ToList();
    }
}
