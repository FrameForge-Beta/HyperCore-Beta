using System.IO;
using System.Text.Json;
using FrameForge.Core;

namespace FrameForge.Analysis;

/// <summary>Resultado real de uma sessão gravada (FPS medidos por ETW, não estimativas).</summary>
public sealed record MeasuredSession(
    string Game, DateTime Start, double DurationS, double AvgFps, double Low1Fps, double Low01Fps,
    double StuttersPerMin, string Grade, string Verdict, string HtmlPath);

public static class MeasuredSessions
{
    /// <summary>Lê os resumos JSON que ficam ao lado de cada relatório em %AppData%\FrameForge\Relatorios.</summary>
    public static List<MeasuredSession> Load(int max = 500)
    {
        var list = new List<MeasuredSession>();
        string folder = AppPaths.Reports;
        if (!Directory.Exists(folder)) return list;

        foreach (string json in Directory.EnumerateFiles(folder, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(max))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var r = doc.RootElement;
                string game = Str(r, "Game");
                if (game.Length == 0) continue;
                list.Add(new MeasuredSession(
                    game,
                    DateTime.TryParse(Str(r, "Start"), out var start) ? start : File.GetLastWriteTime(json),
                    Num(r, "DurationS"), Num(r, "AvgFps"), Num(r, "Low1Fps"), Num(r, "Low01Fps"),
                    Num(r, "StuttersPerMin"), Str(r, "Grade"), Str(r, "Verdict"),
                    Path.ChangeExtension(json, ".html")));
            }
            catch { /* relatório ilegível: ignora */ }
        }

        return list.OrderByDescending(s => s.Start).ToList();
    }

    private static string Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : 0;
}
