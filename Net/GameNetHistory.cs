using System.IO;
using System.Text.Json;
using FrameForge.Core;

namespace FrameForge.Net;

/// <summary>Um resultado guardado (sessão de ping ao vivo ou teste de bufferbloat).</summary>
public sealed class GameNetRecord
{
    public DateTime WhenUtc { get; set; }
    /// <summary>"ping" ou "bufferbloat".</summary>
    public string Kind { get; set; } = "ping";
    public string Target { get; set; } = "";
    public double? AvgMs { get; set; }
    public double JitterMs { get; set; }
    public double LossPct { get; set; }
    public int Spikes { get; set; }
    public int DurationS { get; set; }
    public string Grade { get; set; } = "";
    public double? IdleMs { get; set; }
    public double? LoadedMs { get; set; }
    public double? Mbps { get; set; }
    public string Verdict { get; set; } = "";
    /// <summary>0 = bom, 1 = atenção, 2 = mau.</summary>
    public int Level { get; set; }
}

/// <summary>Histórico simples dos testes de rede (últimos 40), em AppPaths.Root\rede-jogos.json.</summary>
public static class GameNetHistory
{
    private const int Max = 40;
    private static readonly object Gate = new();
    private static List<GameNetRecord>? _items;

    public static string FilePath => Path.Combine(AppPaths.Root, "rede-jogos.json");

    /// <summary>Disparado depois de cada mudança (pode vir de qualquer thread).</summary>
    public static event Action? Changed;

    public static IReadOnlyList<GameNetRecord> All()
    {
        lock (Gate) return Load().OrderByDescending(r => r.WhenUtc).ToList();
    }

    public static void Add(GameNetRecord rec)
    {
        lock (Gate)
        {
            var list = Load();
            list.Add(rec);
            if (list.Count > Max) list.RemoveRange(0, list.Count - Max);
            Save();
        }
        try { Changed?.Invoke(); } catch { }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Load().Clear();
            Save();
        }
        try { Changed?.Invoke(); } catch { }
    }

    private static List<GameNetRecord> Load()
    {
        if (_items != null) return _items;
        try
        {
            _items = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<GameNetRecord>>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch { _items = new(); }
        return _items;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items ?? new()));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* sem disco: fica só em memória */ }
    }
}
