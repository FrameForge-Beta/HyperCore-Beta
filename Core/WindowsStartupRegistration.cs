using System.Diagnostics;
using System.IO;

namespace FrameForge.Core;

public static class WindowsStartupRegistration
{
    private const string TaskName = "FrameForge";
    private const string StartupArgument = "--startup-minimized";

    public static void Update(bool enabled, bool startMinimized)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(enabled ? "/Create" : "/Delete");
        startInfo.ArgumentList.Add("/TN");
        startInfo.ArgumentList.Add(TaskName);

        if (enabled)
        {
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Não foi possível determinar o caminho do executável.");
            string taskCommand = $"\"{executable}\"" + (startMinimized ? $" {StartupArgument}" : "");
            startInfo.ArgumentList.Add("/SC");
            startInfo.ArgumentList.Add("ONLOGON");
            startInfo.ArgumentList.Add("/TR");
            startInfo.ArgumentList.Add(taskCommand);
            startInfo.ArgumentList.Add("/RL");
            startInfo.ArgumentList.Add("HIGHEST");
            startInfo.ArgumentList.Add("/IT");
            startInfo.ArgumentList.Add("/F");
        }
        else
        {
            startInfo.ArgumentList.Add("/F");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Não foi possível iniciar o Agendador de Tarefas do Windows.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string detail = string.Join(Environment.NewLine, new[] { output, error }.Where(text => !string.IsNullOrWhiteSpace(text)));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"O Agendador de Tarefas terminou com o código {process.ExitCode}."
                : detail.Trim());
        }
    }

    public static bool IsStartupLaunch(IEnumerable<string> arguments) =>
        arguments.Contains(StartupArgument, StringComparer.OrdinalIgnoreCase);
}
