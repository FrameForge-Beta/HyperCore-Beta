using System.IO;
using System.Text.Json;
using FrameForge.Core;

namespace FrameForge.Undo;

/// <summary>
/// Datas das alterações para a Central Desfazer (ficheiro AppPaths.Root\desfazer-datas.json).
/// Os sistemas antigos (backup-ajustes.json, arranque, IFEO, OC) não guardam quando mudaram cada coisa:
/// - <see cref="MarkExact"/> grava a hora certa (chamado por TweakBackup.Remember quando guarda um original pela 1.ª vez);
/// - <see cref="Seen"/> devolve a data conhecida ou, se não houver, grava "vista pela primeira vez agora" (aproximada: a alteração
///   pode ser mais antiga, por isso a página mostra "desde …").
/// Thread-safe; nunca lança exceções.
/// </summary>
public static class UndoDates
{
    private sealed class Stamp
    {
        public DateTime Utc { get; set; }
        public bool Exact { get; set; }
    }

    private static readonly object Gate = new();
    private static Dictionary<string, Stamp>? _data;
    private static bool _dirty;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(AppPaths.Root, "desfazer-datas.json");

    /// <summary>Hora exata em que algo foi mudado agora (só grava se ainda não houver data exata para a chave).</summary>
    public static void MarkExact(string key)
    {
        try
        {
            lock (Gate)
            {
                var d = Load();
                if (d.TryGetValue(key, out var s) && s.Exact) return;
                d[key] = new Stamp { Utc = DateTime.UtcNow, Exact = true };
                _dirty = true;
                Flush();
            }
        }
        catch { }
    }

    /// <summary>Data exata gravada (hora local), ou null.</summary>
    public static DateTime? Exact(string key)
    {
        lock (Gate) return Load().TryGetValue(key, out var s) && s.Exact ? s.Utc.ToLocalTime() : null;
    }

    /// <summary>Data conhecida desta chave (hora local); se não houver nenhuma, regista "agora" como aproximada.</summary>
    public static (DateTime When, bool Approx) Seen(string key)
    {
        lock (Gate)
        {
            var d = Load();
            if (!d.TryGetValue(key, out var s))
            {
                d[key] = s = new Stamp { Utc = DateTime.UtcNow, Exact = false };
                _dirty = true;
            }
            return (s.Utc.ToLocalTime(), !s.Exact);
        }
    }

    /// <summary>Esquece as chaves que já não correspondem a nenhuma alteração ativa (as de há menos de 2 min ficam: podem estar a nascer).</summary>
    public static void Prune(ICollection<string> live)
    {
        lock (Gate)
        {
            var d = Load();
            var limit = DateTime.UtcNow.AddMinutes(-2);
            foreach (var k in d.Keys.ToList())
                if (!live.Contains(k) && d[k].Utc < limit) { d.Remove(k); _dirty = true; }
            Flush();
        }
    }

    /// <summary>Grava se houver alterações pendentes.</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            if (!_dirty || _data == null) return;
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
                File.Move(tmp, FilePath, overwrite: true);
                _dirty = false;
            }
            catch { }
        }
    }

    private static Dictionary<string, Stamp> Load()
    {
        if (_data != null) return _data;
        try
        {
            _data = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, Stamp>>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch { _data = new(); }
        return _data;
    }
}
