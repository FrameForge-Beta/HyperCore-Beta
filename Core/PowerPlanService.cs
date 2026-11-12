using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FrameForge.Core;

public sealed record PowerPlanInfo(string Guid, string Name, bool IsActive);
public sealed record PowerPlanValues(
    int CpuMinAc, int CpuMaxAc, int CpuMinDc, int CpuMaxDc,
    int DisplayMinutesAc, int DisplayMinutesDc,
    int SleepMinutesAc, int SleepMinutesDc,
    int DiskMinutesAc, int DiskMinutesDc);

/// <summary>Operações do powercfg para listar, duplicar, configurar e ativar planos de energia.</summary>
public static class PowerPlanService
{
    private const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string ProcessorSubgroup = "54533251-82be-4824-96c1-47b60b740d00";
    private const string CpuMinSetting = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string CpuMaxSetting = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    private const string DisplaySubgroup = "7516b95f-f776-4464-8c53-06167f40cc99";
    private const string DisplayIdleSetting = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
    private const string SleepSubgroup = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    private const string SleepIdleSetting = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
    private const string DiskSubgroup = "0012ee47-9041-4b5d-9b77-535fba8b1442";
    private const string DiskIdleSetting = "6738e2c4-e8a5-4a42-b16a-e040e769756e";

    private static readonly HashSet<string> BuiltInPlans = new(StringComparer.OrdinalIgnoreCase)
    {
        BalancedGuid,
        "a1841308-3541-4fab-bc81-f71556f20b4a",
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        "e9a42b02-d5df-448d-aa00-03f14749eb61",
    };

    public static List<PowerPlanInfo> List()
    {
        string output = Run("/list");
        var plans = new List<PowerPlanInfo>();
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})\s*\((.*?)\)\s*(\*)?\s*$", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            string guid = match.Groups[1].Value.ToLowerInvariant();
            plans.Add(new PowerPlanInfo(guid, match.Groups[2].Value.Trim(), match.Groups[3].Success));
        }
        return plans;
    }

    public static bool IsBuiltIn(string guid) => BuiltInPlans.Contains(guid);

    public static string ActiveGuid() => List().FirstOrDefault(p => p.IsActive)?.Guid
        ?? throw new InvalidOperationException("Não foi possível determinar o plano de energia ativo.");

    public static PowerPlanInfo Duplicate(string sourceGuid, string name)
    {
        name = ValidateName(name);
        string output = Run("/duplicatescheme", sourceGuid);
        var match = Regex.Match(output, @"(?i)([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})");
        if (!match.Success) throw new InvalidOperationException("O Windows criou o plano, mas não devolveu o respetivo identificador.");
        string guid = match.Groups[1].Value.ToLowerInvariant();
        try { Run("/changename", guid, name); }
        catch
        {
            try { Run("/delete", guid); } catch { }
            throw;
        }
        return new PowerPlanInfo(guid, name, false);
    }

    public static PowerPlanValues ReadValues(string guid) => new(
        ReadIndex(guid, ProcessorSubgroup, CpuMinSetting, true), ReadIndex(guid, ProcessorSubgroup, CpuMaxSetting, true),
        ReadIndex(guid, ProcessorSubgroup, CpuMinSetting, false), ReadIndex(guid, ProcessorSubgroup, CpuMaxSetting, false),
        ReadIndex(guid, DisplaySubgroup, DisplayIdleSetting, true) / 60, ReadIndex(guid, DisplaySubgroup, DisplayIdleSetting, false) / 60,
        ReadIndex(guid, SleepSubgroup, SleepIdleSetting, true) / 60, ReadIndex(guid, SleepSubgroup, SleepIdleSetting, false) / 60,
        ReadIndex(guid, DiskSubgroup, DiskIdleSetting, true) / 60, ReadIndex(guid, DiskSubgroup, DiskIdleSetting, false) / 60);

    public static void WriteValues(string guid, PowerPlanValues values)
    {
        if (values.CpuMinAc is < 0 or > 100 || values.CpuMaxAc is < 0 or > 100 || values.CpuMinAc > values.CpuMaxAc ||
            values.CpuMinDc is < 0 or > 100 || values.CpuMaxDc is < 0 or > 100 || values.CpuMinDc > values.CpuMaxDc)
            throw new ArgumentException("A utilização mínima da CPU deve estar entre 0 e a utilização máxima, que não pode passar de 100%.");
        if (new[] { values.DisplayMinutesAc, values.DisplayMinutesDc, values.SleepMinutesAc, values.SleepMinutesDc, values.DiskMinutesAc, values.DiskMinutesDc }
            .Any(minutes => minutes is < 0 or > 1440))
            throw new ArgumentException("Os tempos devem estar entre 0 e 1440 minutos (0 = nunca).");

        WriteSetting(guid, ProcessorSubgroup, CpuMinSetting, values.CpuMinAc, values.CpuMinDc);
        WriteSetting(guid, ProcessorSubgroup, CpuMaxSetting, values.CpuMaxAc, values.CpuMaxDc);
        WriteSetting(guid, DisplaySubgroup, DisplayIdleSetting, checked(values.DisplayMinutesAc * 60), checked(values.DisplayMinutesDc * 60));
        WriteSetting(guid, SleepSubgroup, SleepIdleSetting, checked(values.SleepMinutesAc * 60), checked(values.SleepMinutesDc * 60));
        WriteSetting(guid, DiskSubgroup, DiskIdleSetting, checked(values.DiskMinutesAc * 60), checked(values.DiskMinutesDc * 60));
    }

    public static void Activate(string guid) => Run("/setactive", guid);

    public static void Delete(string guid)
    {
        if (BuiltInPlans.Contains(guid))
            throw new InvalidOperationException("Os planos predefinidos do Windows não podem ser removidos.");
        if (string.Equals(ActiveGuid(), guid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Não é possível remover o plano atualmente ativo. Ativa outro plano primeiro.");
        Run("/delete", guid);
    }

    private static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl))
            throw new ArgumentException("O nome deve ter entre 1 e 60 caracteres.");
        return name;
    }

    private static int ReadIndex(string scheme, string subgroup, string setting, bool ac)
    {
        string output = Run("/q", scheme, subgroup, setting);
        var indexes = Regex.Matches(output, @"0x([0-9a-fA-F]+)")
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        if (indexes.Length < 2)
            throw new InvalidOperationException($"Não consegui ler uma opção do plano ({subgroup}/{setting}) para {(ac ? "tomada" : "bateria")}.");
        // /q lista primeiro o índice atual da tomada (AC) e depois o da bateria (DC); usar os dois últimos
        // índices hexadecimais evita depender do idioma do Windows.
        return indexes[^ (ac ? 2 : 1)];
    }

    private static void WriteSetting(string scheme, string subgroup, string setting, int acValue, int dcValue)
    {
        Run("/setacvalueindex", scheme, subgroup, setting, acValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Run("/setdcvalueindex", scheme, subgroup, setting, dcValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Run(params string[] args)
    {
        var info = new ProcessStartInfo("powercfg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar powercfg.exe.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
        return stdout;
    }
}
