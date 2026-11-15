using System.Diagnostics;
using System.IO;

namespace FrameForge.Core;

public static class WindowsStartupRegistration
{
    private const string TaskName = "HyperCore";
    /// <summary>Nome da tarefa criada pelas versões antigas (FrameForge): apagada na migração e ao desligar o arranque.</summary>
    private const string LegacyTaskName = "FrameForge";
    private const string StartupArgument = "--startup-minimized";

    public static void Update(bool enabled, bool startMinimized)
    {
        if (!enabled)
        {
            // Desligar: apaga a tarefa nova e a antiga; só é erro se a nova continuar lá
            TryDelete(LegacyTaskName);
            if (!Exists(TaskName)) return;
            var (code, detail) = Run("/Delete", "/TN", TaskName, "/F");
            if (code != 0) throw Failure(code, detail);
            return;
        }

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Não foi possível determinar o caminho do executável.");
        string taskCommand = $"\"{executable}\"" + (startMinimized ? $" {StartupArgument}" : "");
        var (createCode, createDetail) = Run("/Create", "/TN", TaskName, "/SC", "ONLOGON", "/TR", taskCommand, "/RL", "HIGHEST", "/IT", "/F");
        if (createCode != 0) throw Failure(createCode, createDetail);
    }

    /// <summary>
    /// No arranque da app: se o arranque com o Windows está ligado, apaga a tarefa antiga "FrameForge" (que abria o exe antigo)
    /// e garante que a tarefa "HyperCore" existe e aponta para o exe atual. Best-effort: erros são ignorados.
    /// </summary>
    public static void RefreshOnAppStart(bool enabled, bool startMinimized)
    {
        if (!enabled) return;
        try
        {
            bool legacy = TryDelete(LegacyTaskName);
            if (legacy || !PointsToCurrentExe(TaskName, startMinimized)) Update(enabled: true, startMinimized);
        }
        catch { }
    }

    public static bool IsStartupLaunch(IEnumerable<string> arguments) =>
        arguments.Contains(StartupArgument, StringComparer.OrdinalIgnoreCase);

    private static bool Exists(string name) => Run("/Query", "/TN", name).Code == 0;

    /// <summary>Apaga a tarefa se existir. Devolve true se existia.</summary>
    private static bool TryDelete(string name)
    {
        try
        {
            if (!Exists(name)) return false;
            Run("/Delete", "/TN", name, "/F");
            return true;
        }
        catch { return false; }
    }

    /// <summary>A tarefa existe e o comando dela é o exe atual com o argumento certo?</summary>
    private static bool PointsToCurrentExe(string name, bool startMinimized)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return true;   // sem caminho não há como comparar: não mexer
        var (code, xml) = Run("/Query", "/TN", name, "/XML");
        if (code != 0) return false;
        bool sameExe = xml.Contains(exe, StringComparison.OrdinalIgnoreCase);
        bool hasArg = xml.Contains(StartupArgument, StringComparison.OrdinalIgnoreCase);
        return sameExe && hasArg == startMinimized;
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) startInfo.ArgumentList.Add(a);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Não foi possível iniciar o Agendador de Tarefas do Windows.");
        var errorTask = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        string error = errorTask.Result;
        process.WaitForExit();
        string detail = string.Join(Environment.NewLine, new[] { output, error }.Where(text => !string.IsNullOrWhiteSpace(text)));
        return (process.ExitCode, detail);
    }

    private static InvalidOperationException Failure(int code, string detail) =>
        new(string.IsNullOrWhiteSpace(detail)
            ? $"O Agendador de Tarefas terminou com o código {code}."
            : detail.Trim());
}
