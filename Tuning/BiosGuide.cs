using FrameForge.Hardware;

namespace FrameForge.Tuning;

/// <summary>
/// CPU e RAM não têm overclock por software (e ainda bem): o scan recomenda passos de BIOS concretos, por fabricante da
/// placa e tipo de CPU (o mesmo guia da página Teste de estabilidade), por ordem de ganho e só os que fazem sentido
/// com o que foi medido.
/// </summary>
public static class BiosGuide
{
    public static List<BiosStep> Steps(ScanReport r, SystemProbe? p)
    {
        var list = new List<BiosStep>();
        string board = p?.Board ?? r.Inventario.PlacaMae;
        string cpuName = p?.CpuName ?? r.Inventario.Cpu;
        bool gigabyte = board.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase);
        bool asus = board.Contains("ASUS", StringComparison.OrdinalIgnoreCase);
        bool msi = board.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || board.Contains("MSI", StringComparison.OrdinalIgnoreCase);
        bool asrock = board.Contains("ASRock", StringComparison.OrdinalIgnoreCase);
        bool ryzen = cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
        bool x3d = cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        bool intelK = !ryzen && System.Text.RegularExpressions.Regex.IsMatch(cpuName, @"\d{4,5}K", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool unstable = r.Cpu is { Estavel: false } && r.Cpu.Stress.ThrottleTermico == false;
        bool hot = r.Cpu?.Stress.ThrottleTermico == true || r.Cpu?.Stress.TempMax >= TuningLimits.For(r.Perfil).CpuTempMax - 3;

        string pbo = gigabyte ? "Tweaker → Advanced CPU Settings → Precision Boost Overdrive (ou Settings → AMD Overclocking)"
                   : asus ? "Advanced → AMD Overclocking → Precision Boost Overdrive (ou AI Tweaker → PBO)"
                   : msi ? "OC → Advanced CPU Configuration → AMD Overclocking → Precision Boost Overdrive"
                   : asrock ? "Advanced → AMD Overclocking → Precision Boost Overdrive"
                   : "Advanced → AMD Overclocking → Precision Boost Overdrive";
        string expo = gigabyte ? "Tweaker → Extreme Memory Profile (X.M.P.) → Profile 1 / EXPO1"
                    : asus ? "AI Tweaker → Ai Overclock Tuner → XMP I / EXPO I / D.O.C.P."
                    : msi ? "OC → A-XMP / EXPO → Profile 1"
                    : asrock ? "OC Tweaker → DRAM Profile → XMP/EXPO"
                    : "procura XMP / EXPO / DOCP no menu da memória";

        list.Add(new BiosStep
        {
            Titulo = "Antes de começar: guarda o perfil atual",
            Onde = gigabyte ? "Save & Exit → Save Profiles" : "Save & Exit → Save Profile",
            Texto = "Se o PC não arrancar depois de um ajuste, espera 3 tentativas (muitas placas voltam sozinhas) ou limpa a CMOS (jumper CLR_CMOS ou tirar a pilha 1 minuto).",
            Porque = "Assim voltas atrás em segundos.",
        });

        if (unstable)
        {
            list.Add(new BiosStep
            {
                Titulo = "Primeiro: tira o que já estiver afinado",
                Onde = "Save & Exit → Load Optimized Defaults (ou desliga PBO/Curve Optimizer e o XMP um de cada vez)",
                Texto = "O scan viu erros na CPU/RAM. Repõe os valores de fábrica, confirma com um novo scan que fica estável e só depois volta a afinar, um passo de cada vez.",
                Porque = string.Join("; ", r.Cpu!.Problemas.Take(2)),
            });
        }

        if (r.Ram?.XmpDesligado == true)
            list.Add(new BiosStep
            {
                Titulo = "Liga o perfil da RAM (XMP/EXPO)",
                Onde = expo,
                Texto = "É o maior ganho em jogos para a maioria dos PCs: a RAM passa da velocidade lenta de fábrica para a da etiqueta. Depois faz um scan para confirmar que não há erros de RAM.",
                Porque = $"A RAM corre a {r.Ram.MHz} MHz mas foi feita para {r.Ram.MHzNominal} MHz" + (r.Ram.LatenciaNs is double l ? $" (latência medida: {l:0} ns)." : "."),
            });
        else if (r.Ram?.LatenciaNs is double lat && lat > BottleneckFromScan.LatencyHigh(r.Inventario.RamTipo))
            list.Add(new BiosStep
            {
                Titulo = "Confirma o perfil da RAM (XMP/EXPO)",
                Onde = expo,
                Texto = "O XMP parece ligado, mas a latência medida é alta. Confirma que está no perfil 1 e, em Ryzen, que o FCLK fica 1:1 com a memória (DDR4 até 3600–3800).",
                Porque = $"Latência medida: {lat:0} ns.",
            });

        if (ryzen && !unstable)
        {
            if (hot)
                list.Add(new BiosStep
                {
                    Titulo = "CPU quente: Curve Optimizer antes de mais desempenho",
                    Onde = pbo + " → Curve Optimizer",
                    Texto = "All Cores → Negative → 10. Baixa a tensão: a CPU aquece menos e mantém o boost. Se quiseres ainda mais frio, Eco Mode (65 W) perde pouco em jogos.",
                    Porque = $"No stress a CPU chegou a {r.Cpu?.Stress.TempMax:0} °C" + (r.Cpu?.Stress.ThrottleTermico == true ? " e perdeu clock." : "."),
                });
            else if (!x3d)
            {
                list.Add(new BiosStep
                {
                    Titulo = "Precision Boost Overdrive (PBO)",
                    Onde = pbo,
                    Texto = "Precision Boost Overdrive → Advanced → PBO Limits → Motherboard. O boost dura mais tempo. Faz um scan depois.",
                    Porque = r.Cpu?.Stress.TempMax is double t ? $"Há margem térmica: no stress a CPU ficou nos {t:0} °C." : "Ganho típico de 2–5% em jogos.",
                });
                list.Add(new BiosStep
                {
                    Titulo = "Curve Optimizer (undervolt da CPU)",
                    Onde = pbo + " → Curve Optimizer",
                    Texto = "All Cores → Negative → 10. Se o scan passar, tenta 15, 20… até falhar; volta 5 a partir de onde falhou. Erros WHEA ou reinícios em uso leve = volta um passo.",
                    Porque = "Menos tensão = menos calor = mais boost. Costuma ficar entre −15 e −30.",
                });
            }
            else
                list.Add(new BiosStep
                {
                    Titulo = "X3D: só Curve Optimizer",
                    Onde = pbo + " → Curve Optimizer",
                    Texto = "Os X3D não sobem o clock com PBO, mas aceitam Curve Optimizer negativo (All Cores → Negative → 10 a 20) em BIOS recentes: menos calor e boost mais estável.",
                    Porque = "Ganho pequeno mas grátis em temperatura.",
                });
        }
        else if (!ryzen && !unstable)
        {
            list.Add(new BiosStep
            {
                Titulo = intelK ? "Intel K: undervolt e limites de energia" : "Intel: limites de energia",
                Onde = gigabyte ? "Tweaker → Advanced CPU Settings" : asus ? "AI Tweaker → Internal CPU Power Management" : msi ? "OC → Advanced CPU Configuration" : "menu de overclock da CPU",
                Texto = intelK
                    ? "Prefere um offset de tensão negativo (−0,050 V para começar) e limites PL1/PL2 sensatos em vez de subir multiplicadores. Faz um scan depois de cada passo."
                    : "Nos Intel sem K não há overclock da CPU; confirma só que os limites de energia (PL1/PL2) não estão abaixo do normal da tua CPU.",
                Porque = hot ? $"No stress a CPU chegou a {r.Cpu?.Stress.TempMax:0} °C: menos tensão = menos calor." : "Mais boost sustentado com menos calor.",
            });
        }

        // Só placas que suportam Resizable BAR: NVIDIA RTX 30 ou mais recente (as RTX 20, como a 2070 SUPER, não suportam), AMD RX 6000+ e Intel Arc
        if (r.Inventario.ResizableBar == false &&
            System.Text.RegularExpressions.Regex.IsMatch(r.Inventario.Gpu ?? "", @"RTX\s*(30|40|50)\d0|RX\s*(6|7|9)\d{3}|\bArc\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            list.Add(new BiosStep
            {
                Titulo = "Resizable BAR",
                Onde = gigabyte ? "Settings → IO Ports → Above 4G Decoding + Re-Size BAR Support" : "Advanced → PCI Subsystem → Above 4G Decoding + Re-Size BAR",
                Texto = "Liga 'Above 4G Decoding' e 'Re-Size BAR Support'. Precisa de arranque UEFI com o CSM desligado: confirma antes que o Windows está instalado em modo UEFI (disco GPT — vê em Informações do Sistema → Modo de BIOS), senão o PC deixa de arrancar.",
                Porque = "Ganho de 0–10% conforme o jogo.",
            });

        return list;
    }
}
