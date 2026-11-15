using System.Diagnostics.Eventing.Reader;
using System.Management;

namespace ResetExplode;

/// <summary>
/// Apoio do overclock: procura erros de hardware no registo do Windows e identifica a placa-mãe.
/// A lista de eventos de "reset do driver de vídeo" é a MESMA que a guarda de crash do GpuTuner usa (<see cref="GpuResetFilter"/>).
/// </summary>
public static class OcCheck
{
    /// <summary>
    /// Resets do driver de vídeo: Display 4101 (TDR recuperado), nvlddmkm 13/14/153 (erro/página/timeout do motor NVIDIA)
    /// e amdkmdag só com nível Erro/Crítico (o driver AMD escreve muitos eventos informativos).
    /// </summary>
    public const string GpuResetFilter =
        "(Provider[@Name='Display'] and EventID=4101) or (Provider[@Name='nvlddmkm'] and (EventID=13 or EventID=14 or EventID=153)) " +
        "or (Provider[@Name='amdkmdag'] and (Level=1 or Level=2))";

    private const string WheaFilter = "Provider[@Name='Microsoft-Windows-WHEA-Logger']";

    /// <summary>Kernel-Power 41 (reinício sem encerrar), EventLog 6008 (encerramento inesperado) e BugCheck 1001 (ecrã azul).</summary>
    private const string RestartFilter =
        "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or (Provider[@Name='EventLog'] and EventID=6008) " +
        "or (Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001)";

    /// <summary>Conta erros WHEA (CPU/RAM/PCIe, corrigidos e fatais), resets do driver de vídeo e reinícios inesperados desde o instante indicado.</summary>
    public static HardwareErrors ErrorsSince(DateTime since)
    {
        string t = since.ToUniversalTime().ToString("o");
        string time = $"TimeCreated[@SystemTime>='{t}']";
        var events = new List<HardwareEvent>();
        try
        {
            int whea = 0, wheaFatal = 0, wheaCorr = 0, pcie = 0, cpu = 0, mem = 0;
            string? lastWhea = null, lastGpu = null;
            foreach (var e in Read($"*[System[{WheaFilter} and {time}]]", ClassifyWhea))
            {
                whea++;
                lastWhea ??= Detail(e);
                switch (e.Kind)
                {
                    case "whea-fatal": wheaFatal++; break;
                    case "whea-pcie": wheaCorr++; pcie++; break;
                    case "whea-cpu": wheaCorr++; cpu++; break;
                    case "whea-memoria": wheaCorr++; mem++; break;
                    default: wheaCorr++; break;
                }
                if (events.Count < 50) events.Add(e);
            }

            int gpu = 0;
            foreach (var e in Read($"*[System[({GpuResetFilter}) and {time}]]", _ => "gpu-reset"))
            {
                gpu++;
                lastGpu ??= Detail(e);
                if (events.Count < 50) events.Add(e);
            }

            int restarts = 0, bugchecks = 0;
            foreach (var e in Read($"*[System[({RestartFilter}) and {time}]]", rec => rec.Id == 1001 ? "bugcheck" : "reinicio"))
            {
                if (e.Kind == "bugcheck") bugchecks++; else restarts++;
                if (events.Count < 50) events.Add(e);
            }

            return new HardwareErrors(whea, gpu, lastWhea ?? lastGpu, null)
            {
                WheaFatal = wheaFatal, WheaCorrected = wheaCorr, WheaCorrectedPcie = pcie, WheaCorrectedCpu = cpu, WheaCorrectedMemory = mem,
                UnexpectedRestarts = restarts, BugChecks = bugchecks,
                Events = events.OrderByDescending(e => e.Time).ToList(),
            };
        }
        catch (Exception ex)
        {
            return new HardwareErrors(0, 0, null, ex.Message);
        }
    }

    /// <summary>
    /// WHEA-Logger: nível Erro/Crítico = fatal; Aviso/Informação = corrigido. A origem vem do ID
    /// (17 PCI Express, 19 processador/cache, 47 memória) ou, se não for conhecido, do texto do evento.
    /// </summary>
    private static string ClassifyWhea(EventRecord rec)
    {
        int level = rec.Level ?? 0;
        if (level is 1 or 2) return "whea-fatal";
        switch (rec.Id)
        {
            case 17: return "whea-pcie";
            case 19: return "whea-cpu";
            case 47: return "whea-memoria";
        }
        string text = SafeText(rec);
        if (text.Contains("PCI Express", StringComparison.OrdinalIgnoreCase)) return "whea-pcie";
        if (text.Contains("Memory", StringComparison.OrdinalIgnoreCase) || text.Contains("Memória", StringComparison.OrdinalIgnoreCase)) return "whea-memoria";
        if (text.Contains("Processor", StringComparison.OrdinalIgnoreCase) || text.Contains("Cache", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Processador", StringComparison.OrdinalIgnoreCase)) return "whea-cpu";
        return "whea-outro";
    }

    private static List<HardwareEvent> Read(string xpath, Func<EventRecord, string> kind)
    {
        var list = new List<HardwareEvent>();
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true });
        int n = 0;
        for (EventRecord? rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
        {
            using (rec)
            {
                // Só os primeiros levam texto (FormatDescription é lento); os outros só contam
                string text = n < 5 ? SafeText(rec) : "";
                list.Add(new HardwareEvent(rec.TimeCreated ?? DateTime.Now, rec.ProviderName ?? "", rec.Id, rec.Level ?? 0, kind(rec), text));
                if (++n >= 500) break; // um registo a transbordar de eventos não deve bloquear o teste
            }
        }
        return list;
    }

    private static string SafeText(EventRecord rec)
    {
        try { return (rec.FormatDescription() ?? "").Split('\n')[0].Trim(); } catch { return ""; }
    }

    private static string Detail(HardwareEvent e) =>
        $"{e.Time:dd/MM HH:mm} · {e.Provider} {e.Id}" + (e.Text.Length > 0 ? " · " + e.Text : "");

    public static (string Maker, string Model) Motherboard()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject mo in s.Get())
                return (mo["Manufacturer"]?.ToString()?.Trim() ?? "", mo["Product"]?.ToString()?.Trim() ?? "");
        }
        catch { }
        return ("", "");
    }
}
