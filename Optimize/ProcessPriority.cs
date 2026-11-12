using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace FrameForge.Optimize;

/// <summary>Prioridade de CPU (valores do registo PerfOptions\CpuPriorityClass). Tempo real não existe aqui de propósito.</summary>
public enum CpuPrio { Auto = 0, Idle = 1, Normal = 2, High = 3, BelowNormal = 5, AboveNormal = 6 }

/// <summary>Prioridade de disco (PerfOptions\IoPriority). Alta não é permitida pelo Windows para programas normais.</summary>
public enum IoPrio { Auto = -1, VeryLow = 0, Low = 1, Normal = 2 }

/// <summary>Regra gravada. RawCpu/RawIo guardam o valor tal como está no registo (pode ser um que a app não conhece, ex.: 4 ou 3).</summary>
public sealed record PriorityRule(string Exe, CpuPrio Cpu, IoPrio Io, int? RawCpu = null, int? RawIo = null)
{
    public bool UnknownCpu => RawCpu is int v && !Enum.IsDefined(typeof(CpuPrio), v);
    public bool UnknownIo => RawIo is int v && !Enum.IsDefined(typeof(IoPrio), v);
}

/// <summary>
/// Prioridade PERSISTENTE por executável (o que o Gestor de Tarefas não faz): grava em
/// HKLM\...\Image File Execution Options\&lt;nome.exe&gt;\PerfOptions. É o próprio Windows que a aplica sempre que esse .exe arranca —
/// mantém-se depois de reiniciar e a app nunca precisa de abrir o processo (importante com anti-cheats).
/// No Windows 7+ esta chave é PARTILHADA entre 32 e 64 bits (não é redirecionada), por isso uma escrita vale para os dois.
/// Só toca no subvalor PerfOptions — nunca em Debugger, GlobalFlag ou MitigationOptions (que anti-cheats e antivírus vigiam).
/// </summary>
public static class ProcessPriority
{
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private static readonly RegistryView[] Views = { Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default };

    public static string Name(CpuPrio p) => p switch
    {
        CpuPrio.Auto => "Automático (o Windows decide)",
        CpuPrio.Idle => "Baixa",
        CpuPrio.BelowNormal => "Abaixo do normal",
        CpuPrio.Normal => "Normal",
        CpuPrio.AboveNormal => "Acima do normal",
        CpuPrio.High => "Alta",
        _ => p.ToString(),
    };

    public static string Name(IoPrio p) => p switch
    {
        IoPrio.Auto => "Automático",
        IoPrio.VeryLow => "Muito baixa",
        IoPrio.Low => "Baixa",
        IoPrio.Normal => "Normal",
        _ => p.ToString(),
    };

    /// <summary>Explicação curta de cada nível, para quem não sabe o que é prioridade.</summary>
    public static string Explain(CpuPrio p) => p switch
    {
        CpuPrio.Auto => "Não mexe: o programa arranca como sempre (normalmente em Normal).",
        CpuPrio.Idle => "Só usa a CPU quando sobra. Bom para downloads e tarefas de fundo que não queres que roubem desempenho ao jogo. Não uses no OBS (gravação) nem no Discord (voz): começam a falhar.",
        CpuPrio.BelowNormal => "Cede a CPU aos outros programas. Bom para launchers, browsers e apps de fundo enquanto jogas.",
        CpuPrio.Normal => "O padrão do Windows.",
        CpuPrio.AboveNormal => "Ligeira vantagem sobre os programas de fundo. A escolha segura para jogos: ajuda o 1% low quando há coisas abertas, sem efeitos secundários.",
        CpuPrio.High => "Vantagem forte. Pode ajudar em PCs com muitos programas abertos, mas pode roubar CPU ao som, ao Discord e ao rato (estalidos, atrasos). Testa e compara no relatório.",
        _ => "",
    };

    public static string NormalizeExe(string nameOrPath)
    {
        string n = Path.GetFileName(nameOrPath.Trim().Trim('"'));
        if (n.Length == 0) return "";
        if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n += ".exe";
        return n;
    }

    public static PriorityRule? Read(string exe)
    {
        exe = NormalizeExe(exe);
        if (exe.Length == 0) return null;
        foreach (var view in Views)
        {
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = hk.OpenSubKey($@"{Ifeo}\{exe}\PerfOptions");
            if (k == null) continue;
            int? rc = k.GetValue("CpuPriorityClass") as int?, ri = k.GetValue("IoPriority") as int?;
            if (rc == null && ri == null) continue;
            var cpu = rc is int c && Enum.IsDefined(typeof(CpuPrio), c) ? (CpuPrio)c : CpuPrio.Auto;
            var io = ri is int i && Enum.IsDefined(typeof(IoPrio), i) ? (IoPrio)i : IoPrio.Auto;
            return new PriorityRule(exe, cpu, io, rc, ri);
        }
        return null;
    }

    /// <summary>Todas as regras que existem no PC (incluindo as criadas por outros programas, ex.: Process Lasso).</summary>
    public static List<PriorityRule> ReadAll()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in Views)
        {
            RegistryKey? root = null;
            try
            {
                using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                root = hk.OpenSubKey(Ifeo);
            }
            catch { }
            if (root == null) continue;
            using (root)
                foreach (var sub in root.GetSubKeyNames())
                {
                    // uma chave sem permissão/corrompida não pode esconder as outras
                    try
                    {
                        using var p = root.OpenSubKey($@"{sub}\PerfOptions");
                        if (p != null && (p.GetValue("CpuPriorityClass") != null || p.GetValue("IoPriority") != null)) names.Add(sub);
                    }
                    catch { }
                }
        }
        var list = new List<PriorityRule>();
        foreach (var n in names)
            try { if (Read(n) is PriorityRule r) list.Add(r); } catch { }
        return list.OrderBy(r => r.Exe, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Grava a prioridade de CPU e a de disco (Automático apaga só esse valor). Precisa de administrador.</summary>
    public static void Set(string exe, CpuPrio cpu, IoPrio io)
    {
        SetValue(exe, "CpuPriorityClass", cpu == CpuPrio.Auto ? null : (int)cpu);
        SetValue(exe, "IoPriority", io == IoPrio.Auto ? null : (int)io);
    }

    /// <summary>Muda só a prioridade de CPU: deixa intactos IoPriority/PagePriority, mesmo com valores que a app não conhece.</summary>
    public static void SetCpu(string exe, CpuPrio cpu) => SetValue(exe, "CpuPriorityClass", cpu == CpuPrio.Auto ? null : (int)cpu);

    /// <summary>Muda só a prioridade de disco.</summary>
    public static void SetIo(string exe, IoPrio io) => SetValue(exe, "IoPriority", io == IoPrio.Auto ? null : (int)io);

    /// <summary>
    /// Escreve (ou apaga, se null) UM valor em PerfOptions. Nunca apaga outros valores; a subchave PerfOptions e a chave do .exe
    /// só são removidas se ficarem completamente vazias (não toca em Debugger, MitigationOptions, PagePriority de outros programas).
    /// </summary>
    private static void SetValue(string exe, string name, int? value)
    {
        exe = NormalizeExe(exe);
        if (exe.Length == 0 || exe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Nome de executável inválido.");
        foreach (var view in Views)
        {
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            if (value is int v)
            {
                using var k = hk.CreateSubKey($@"{Ifeo}\{exe}\PerfOptions", writable: true);
                k.SetValue(name, v, RegistryValueKind.DWord);
                continue;
            }
            using (var perf = hk.OpenSubKey($@"{Ifeo}\{exe}\PerfOptions", writable: true))
            {
                if (perf == null) continue;
                perf.DeleteValue(name, false);
                if (perf.ValueCount > 0 || perf.SubKeyCount > 0) continue;
            }
            using (var exeKey = hk.OpenSubKey($@"{Ifeo}\{exe}", writable: true))
            {
                if (exeKey == null) continue;
                exeKey.DeleteSubKey("PerfOptions", false);
                if (exeKey.ValueCount > 0 || exeKey.SubKeyCount > 0) continue;
            }
            using var root = hk.OpenSubKey(Ifeo, writable: true);
            root?.DeleteSubKey(exe, false);
        }
    }

    /// <summary>Prioridade base que o Windows dá a cada classe (para comparar com a do processo a correr).</summary>
    public static int BaseOf(CpuPrio p) => p switch { CpuPrio.Idle => 4, CpuPrio.BelowNormal => 6, CpuPrio.AboveNormal => 10, CpuPrio.High => 13, _ => 8 };

    /// <summary>
    /// Depois de o jogo abrir: a regra ficou mesmo aplicada? (Alguns jogos/anti-cheats repõem a prioridade deles.)
    /// Lê da lista de processos do Windows, sem abrir o processo. Devolve null se não houver regra.
    /// </summary>
    public static string? Verify(int pid, string processName)
    {
        var rule = Read(processName);
        if (rule == null || rule.Cpu == CpuPrio.Auto) return null;
        int? b = null;
        foreach (var p in Process.GetProcesses()) { if (p.Id == pid) try { b = p.BasePriority; } catch { } p.Dispose(); }
        if (b == null) return null;
        return b == BaseOf(rule.Cpu)
            ? $"prioridade {Name(rule.Cpu)} aplicada ✔"
            : $"a regra pedia {Name(rule.Cpu)}, mas o jogo está em prioridade base {b} (o jogo ou o anti-cheat repôs a dele)";
    }

    /// <summary>
    /// Prioridade base atual dos processos com este nome, lida da lista de processos do Windows (sem abrir nenhum processo).
    /// 4 = baixa, 6 = abaixo do normal, 8 = normal, 10 = acima do normal, 13 = alta, 24 = tempo real.
    /// </summary>
    public static string? RunningNow(string exe)
    {
        string name = Path.GetFileNameWithoutExtension(NormalizeExe(exe));
        var ps = Process.GetProcessesByName(name);
        try
        {
            if (ps.Length == 0) return null;
            int b = ps[0].BasePriority;
            string n = b switch { <= 4 => "Baixa", <= 6 => "Abaixo do normal", <= 8 => "Normal", <= 10 => "Acima do normal", <= 13 => "Alta", _ => "Tempo real" };
            return ps.Length > 1 ? $"{n} ({ps.Length} processos)" : n;
        }
        catch { return "a correr"; }
        finally { foreach (var p in ps) p.Dispose(); }
    }
}
