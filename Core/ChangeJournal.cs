using System.IO;
using System.Text.Json;

namespace FrameForge.Core;

// =====================================================================================================================
// Registo de alterações reversíveis (Central Desfazer)
// ---------------------------------------------------------------------------------------------------------------------
// Ficheiro: %AppData%\HyperCore\alteracoes.json (AppPaths.Root). Sobrevive a reinícios da app e do PC.
//
// Regra para as funções NOVAS (Rede para jogos, Configuração gamer, Modo Jogo, ...): sempre que mudares algo no PC que o
// utilizador possa querer repor, guarda ANTES de mudar o necessário para voltar atrás e regista:
//
//   1) No arranque do teu módulo (uma vez; ex.: construtor estático), regista quem sabe desfazer esse tipo:
//        ChangeJournal.RegisterHandler("dns", e =>
//        {
//            var antes = JsonSerializer.Deserialize<DnsAntes>(e.Data)!;      // o que guardaste em Record(...)
//            bool ok = Dns.Repor(antes.Adaptador, antes.Servidores);
//            return (ok, ok ? "DNS reposto." : "Não consegui repor o DNS.");
//        });
//
//   2) Depois de aplicar a alteração com sucesso:
//        string id = ChangeJournal.Record("dns", "DNS mudado para Cloudflare", "Wi-Fi: 192.168.1.1 → 1.1.1.1",
//                                         JsonSerializer.Serialize(new DnsAntes(adaptador, servidoresAntigos)));
//      (guarda o id se a tua página tiver o seu próprio botão "Repor"; aí chama ChangeJournal.TryUndo(id).)
//
//   3) Se o utilizador repuser pela tua própria página (sem passar pela Central), chama ChangeJournal.Remove(id)
//      ou ChangeJournal.RemoveKind("dns") para a entrada não ficar órfã.
//
// A Central Desfazer (Pages/UndoPage.cs) mostra All() e chama TryUndo(id). TryUndo só apaga a entrada quando o handler
// devolve ok=true. Um "kind" sem handler registado nesta sessão devolve (false, "...") e a entrada fica.
// Changed é disparado (em qualquer thread) a cada Record/Remove/TryUndo bem-sucedido: na UI, usa Dispatcher.BeginInvoke.
// Os sistemas antigos (TweakBackup/backup-ajustes.json, arranque, IFEO, OC...) NÃO passam por aqui — a UndoPage lê-os
// diretamente; só as funções novas escrevem neste registo.
// Tudo é thread-safe (um único lock) e os handlers correm FORA do lock (podem demorar ou chamar Record/Remove).
// =====================================================================================================================

/// <summary>Uma alteração ainda ativa no PC, com o necessário (Data) para a desfazer.</summary>
public sealed class ChangeEntry
{
    public string Id { get; set; } = "";
    /// <summary>Tipo da alteração (ex.: "dns", "display-hz", "hags", "gamemode"). Liga a entrada ao handler de desfazer.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Frase curta para a lista ("DNS mudado para Cloudflare").</summary>
    public string Title { get; set; } = "";
    /// <summary>Detalhe opcional ("Wi-Fi: 192.168.1.1 → 1.1.1.1").</summary>
    public string Detail { get; set; } = "";
    public DateTime WhenUtc { get; set; }
    /// <summary>Estado anterior, serializado pelo módulo que fez a alteração (normalmente JSON). Só o handler o interpreta.</summary>
    public string Data { get; set; } = "";
}

public static class ChangeJournal
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Func<ChangeEntry, (bool ok, string msg)>> Handlers = new(StringComparer.OrdinalIgnoreCase);
    private static List<ChangeEntry>? _entries;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Caminho do ficheiro do registo.</summary>
    public static string FilePath => Path.Combine(AppPaths.Root, "alteracoes.json");

    /// <summary>Disparado depois de cada mudança no registo (Record, Remove, TryUndo com sucesso). Pode vir de qualquer thread.</summary>
    public static event Action? Changed;

    /// <summary>Regista uma alteração feita agora. Devolve o id da entrada.</summary>
    public static string Record(string kind, string title, string detail = "", string data = "")
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("kind vazio", nameof(kind));
        var entry = new ChangeEntry
        {
            Id = Guid.NewGuid().ToString("N"), Kind = kind, Title = title ?? "", Detail = detail ?? "",
            WhenUtc = DateTime.UtcNow, Data = data ?? ""
        };
        lock (Gate)
        {
            Load().Add(entry);
            Save();
        }
        RaiseChanged();
        return entry.Id;
    }

    /// <summary>Apaga uma entrada (sem desfazer nada no PC). Devolve false se não existia.</summary>
    public static bool Remove(string id)
    {
        bool removed;
        lock (Gate)
        {
            removed = Load().RemoveAll(e => e.Id == id) > 0;
            if (removed) Save();
        }
        if (removed) RaiseChanged();
        return removed;
    }

    /// <summary>Apaga todas as entradas de um tipo (ex.: o utilizador repôs tudo pela página da própria função).</summary>
    public static int RemoveKind(string kind)
    {
        int n;
        lock (Gate)
        {
            n = Load().RemoveAll(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Save();
        }
        if (n > 0) RaiseChanged();
        return n;
    }

    /// <summary>Cópia das entradas, da mais recente para a mais antiga.</summary>
    public static IReadOnlyList<ChangeEntry> All()
    {
        lock (Gate) return Load().OrderByDescending(e => e.WhenUtc).Select(Clone).ToList();
    }

    public static ChangeEntry? Get(string id)
    {
        lock (Gate) return Load().FirstOrDefault(e => e.Id == id) is { } e ? Clone(e) : null;
    }

    /// <summary>Regista (ou substitui) quem sabe desfazer as alterações de um tipo. Chamar uma vez por tipo, no arranque do módulo.</summary>
    public static void RegisterHandler(string kind, Func<ChangeEntry, (bool ok, string msg)> undo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        lock (Gate) Handlers[kind] = undo;
    }

    public static bool HasHandler(string kind)
    {
        lock (Gate) return Handlers.ContainsKey(kind);
    }

    /// <summary>
    /// Desfaz uma alteração: chama o handler do tipo e, se devolver ok, apaga a entrada.
    /// Exceções do handler são apanhadas e devolvidas como (false, mensagem).
    /// </summary>
    public static (bool ok, string msg) TryUndo(string id)
    {
        ChangeEntry? entry;
        Func<ChangeEntry, (bool ok, string msg)>? handler;
        lock (Gate)
        {
            entry = Load().FirstOrDefault(e => e.Id == id) is { } e ? Clone(e) : null;
            handler = entry != null && Handlers.TryGetValue(entry.Kind, out var h) ? h : null;
        }
        if (entry == null) return (false, "Esta alteração já não está no registo.");
        if (handler == null) return (false, $"Ainda não sei desfazer alterações do tipo \"{entry.Kind}\" nesta versão.");

        (bool ok, string msg) result;
        try { result = handler(entry); }
        catch (Exception ex) { return (false, "Não foi possível desfazer: " + ex.Message); }

        if (result.ok)
        {
            bool removed;
            lock (Gate)
            {
                removed = Load().RemoveAll(e => e.Id == id) > 0;
                if (removed) Save();
            }
            if (removed) RaiseChanged();
        }
        return (result.ok, string.IsNullOrWhiteSpace(result.msg) ? (result.ok ? "Desfeito." : "Não foi possível desfazer.") : result.msg);
    }

    // ------------------------------------------------------------------ ficheiro

    /// <summary>true = a leitura do ficheiro falhou por um bloqueio (antivírus, backup): o que está no disco ainda não foi juntado.</summary>
    private static bool _diskUnread;

    /// <summary>Lê o ficheiro do disco. null = não deu para o ler agora (bloqueado ou sem acesso); estragado = cópia e lista vazia.</summary>
    private static List<ChangeEntry>? ReadDisk(int attempts)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                string text = File.ReadAllText(FilePath);
                try { return JsonSerializer.Deserialize<List<ChangeEntry>>(text) ?? new(); }
                catch (JsonException)
                {
                    // Estragado de facto: guarda uma cópia (nome único, não pisa as anteriores) e começa vazio
                    try { File.WriteAllText($"{FilePath}.estragado-{DateTime.Now:yyyyMMdd-HHmmss}", text); } catch { }
                    return new();
                }
            }
            catch when (attempt < attempts) { Thread.Sleep(100); }
            catch { return null; }
        }
    }

    /// <summary>Junta o que está no disco ao que já está em memória (depois de uma leitura bloqueada). Chamar dentro do lock.</summary>
    private static bool MergeDisk(int attempts)
    {
        var disk = ReadDisk(attempts);
        if (disk == null) return false;
        var have = _entries!.Select(e => e.Id).ToHashSet();
        _entries.InsertRange(0, disk.Where(e => !string.IsNullOrEmpty(e.Id) && !have.Contains(e.Id)));
        _diskUnread = false;
        return true;
    }

    /// <summary>Lê o ficheiro na primeira utilização. Chamar sempre dentro do lock.</summary>
    private static List<ChangeEntry> Load()
    {
        if (_entries != null)
        {
            if (_diskUnread) MergeDisk(attempts: 1); // a leitura anterior foi bloqueada: tenta outra vez (sem esperas)
            return _entries;
        }
        var disk = ReadDisk(attempts: 5);
        _diskUnread = disk == null;
        _entries = disk ?? new();
        _entries.RemoveAll(e => string.IsNullOrEmpty(e.Id));
        return _entries;
    }

    /// <summary>Escreve de forma atómica (ficheiro temporário + troca). Chamar sempre dentro do lock.</summary>
    private static void Save()
    {
        // Nunca gravar por cima de um ficheiro que ainda não se conseguiu ler (apagava o registo verdadeiro): fica em memória
        // e a próxima gravação tenta outra vez
        if (_diskUnread && !MergeDisk(attempts: 5)) return;
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries ?? new(), Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* sem disco/permissões: o registo continua em memória nesta sessão */ }
    }

    private static ChangeEntry Clone(ChangeEntry e) => new()
    {
        Id = e.Id, Kind = e.Kind, Title = e.Title, Detail = e.Detail, WhenUtc = e.WhenUtc, Data = e.Data
    };

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }
}
