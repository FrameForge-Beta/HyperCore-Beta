using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ResetExplode;

public enum CatalogInstallState
{
    Idle,
    Queued,
    Installing,
    Installed,
    AlreadyInstalled,
    Failed,
    Skipped
}

public sealed class CatalogApp : INotifyPropertyChanged
{
    public string WingetId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Description { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public int DownloadCount { get; init; }
    public List<string> Tags { get; init; } = new();

    private bool _selected;
    private bool _isInstalledLocally;
    private string _status = "";
    private string _errorDetail = "";
    private CatalogInstallState _state = CatalogInstallState.Idle;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnChanged();
            OnChanged(nameof(SelectButtonText));
        }
    }

    public bool IsInstalledLocally
    {
        get => _isInstalledLocally;
        set
        {
            if (_isInstalledLocally == value) return;
            _isInstalledLocally = value;
            OnChanged();
            OnChanged(nameof(StateKey));
            OnChanged(nameof(BadgeText));
            OnChanged(nameof(HasBadge));
        }
    }

    public CatalogInstallState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnChanged();
            OnChanged(nameof(StateKey));
            OnChanged(nameof(BadgeText));
            OnChanged(nameof(HasBadge));
            OnChanged(nameof(CanInstallNow));
            OnChanged(nameof(CanSelect));
            OnChanged(nameof(IsBusy));
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnChanged();
            OnChanged(nameof(HasStatus));
            OnChanged(nameof(StatusTooltip));
        }
    }

    public string ErrorDetail
    {
        get => _errorDetail;
        set
        {
            if (_errorDetail == value) return;
            _errorDetail = value;
            OnChanged();
            OnChanged(nameof(StatusTooltip));
        }
    }

    public bool CanSelect => State is CatalogInstallState.Idle or CatalogInstallState.Failed or CatalogInstallState.Skipped;
    public bool CanInstallNow => State is CatalogInstallState.Idle or CatalogInstallState.Failed or CatalogInstallState.Skipped;
    public bool IsBusy => State is CatalogInstallState.Queued or CatalogInstallState.Installing;
    public bool HasStatus => !string.IsNullOrWhiteSpace(_status);
    public string StatusTooltip => string.IsNullOrWhiteSpace(_errorDetail) ? _status : _errorDetail;
    public string SelectButtonText => Selected ? "NA FILA" : "+ FILA";

    public string PublisherDisplay => string.IsNullOrWhiteSpace(Publisher) ? "winget" : Publisher;
    public string VersionDisplay => string.IsNullOrWhiteSpace(LatestVersion) ? "Mais recente" : $"v{LatestVersion}";
    public string DescriptionDisplay => string.IsNullOrWhiteSpace(Description) ? WingetId : Description;

    public string DownloadsDisplay => DownloadCount switch
    {
        >= 1_000_000 => $"{DownloadCount / 1_000_000.0:0.0}M",
        >= 1_000 => $"{DownloadCount / 1_000.0:0.0}k",
        > 0 => DownloadCount.ToString(),
        _ => ""
    };

    public bool HasDownloads => DownloadCount > 0;

    public string Initials
    {
        get
        {
            var words = (Name ?? WingetId).Split(new[] { ' ', '-', '.', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "?";
            if (words.Length == 1) return words[0].Substring(0, Math.Min(2, words[0].Length)).ToUpperInvariant();
            return (words[0][0].ToString() + words[1][0]).ToUpperInvariant();
        }
    }

    public bool HasBadge => State != CatalogInstallState.Idle || IsInstalledLocally;

    public string StateKey => State switch
    {
        CatalogInstallState.Queued => "Queued",
        CatalogInstallState.Installing => "Updating",
        CatalogInstallState.Installed or CatalogInstallState.AlreadyInstalled => "Done",
        CatalogInstallState.Failed => "Failed",
        CatalogInstallState.Skipped => "Skipped",
        _ => IsInstalledLocally ? "Done" : "Idle"
    };

    public string BadgeText => State switch
    {
        CatalogInstallState.Queued => "NA FILA",
        CatalogInstallState.Installing => "INSTALANDO",
        CatalogInstallState.Installed => "INSTALADO",
        CatalogInstallState.AlreadyInstalled => "JÁ INSTALADO",
        CatalogInstallState.Failed => "FALHOU",
        CatalogInstallState.Skipped => "PARADO",
        _ => IsInstalledLocally ? "JÁ INSTALADO" : ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed class CatalogPack
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<CatalogApp> Apps { get; init; } = new();
    public string AppCountText => Apps.Count == 1 ? "1 app" : $"{Apps.Count} apps";
    public string PreviewNames => string.Join(" · ", Apps.Take(5).Select(a => a.Name)) + (Apps.Count > 5 ? $" +{Apps.Count - 5}" : "");
}

public sealed record CatalogCategory(string Key, string Label, string IconGlyph, string Tag, string FallbackSearch);

public sealed class CatalogPageResult
{
    public List<CatalogApp> Items { get; init; } = new();
    public int Total { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
    public bool HasMore => Offset + Items.Count < Total;
    public bool FromLocalFallback { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Cliente personalizado (via engenharia reversa dos endpoints reais do winstall.app)
/// para consultar o catálogo completo de +16.500 pacotes winget, categorias e pacotes curados,
/// com cache em memória e fallback automático para winget search local.
/// </summary>
public static class WinstallApiClient
{
    private const string BaseUrl = "https://api.winstall.app";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, (DateTime ExpiresAt, object Data)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<CatalogCategory> Categories = new List<CatalogCategory>
    {
        new("trending",     "Em Alta",         "\uE734", "",             ""),
        new("packs",        "Pacotes Prontos", "\uE7B8", "",             ""),
        new("popular",      "Mais Populares",  "\uE71D", "",             ""),
        new("browser",      "Navegadores",     "\uE774", "browser",      "browser"),
        new("gaming",       "Jogos",           "\uE7FC", "gaming",       "gaming"),
        new("chat",         "Comunicação",     "\uE8F2", "chat",         "discord chat"),
        new("media",        "Áudio & Vídeo",   "\uE768", "media",        "media player video"),
        new("developer",    "Desenvolvimento", "\uE943", "developer",    "code ide"),
        new("utility",      "Utilitários",     "\uE90F", "utility",      "utility"),
        new("security",     "Segurança & VPN", "\uE72E", "security",     "vpn security"),
        new("productivity", "Produtividade",   "\uE8A5", "productivity", "office pdf"),
        new("ai",           "Inteligência IA", "\uE99A", "ai",           "ai"),
    };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) HyperCore/0.5");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://winstall.app");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://winstall.app/");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return client;
    }

    public static async Task<CatalogPageResult> GetTrendingAppsAsync(CancellationToken ct)
    {
        const string cacheKey = "apps:trending";
        if (TryGetCache<CatalogPageResult>(cacheKey, out var cached))
            return cached;

        try
        {
            using var doc = await GetJsonAsync($"{BaseUrl}/apps/trending", ct);
            var list = ParseAppsArray(doc.RootElement, "data");
            var res = new CatalogPageResult
            {
                Items = list,
                Total = list.Count,
                Offset = 0,
                Limit = list.Count
            };
            if (list.Count > 0) SetCache(cacheKey, res);
            return res;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new CatalogPageResult { ErrorMessage = "Não foi possível carregar os apps em alta: " + ex.Message };
        }
    }

    public static async Task<CatalogPageResult> GetPopularAppsAsync(int offset, int limit, CancellationToken ct)
    {
        var cacheKey = $"apps:popular:{offset}:{limit}";
        if (TryGetCache<CatalogPageResult>(cacheKey, out var cached))
            return cached;

        try
        {
            using var doc = await GetJsonAsync($"{BaseUrl}/apps?offset={offset}&limit={limit}", ct);
            var list = ParseAppsArray(doc.RootElement, "data");
            int total = ReadInt(doc.RootElement, "total", list.Count);
            var res = new CatalogPageResult
            {
                Items = list,
                Total = total,
                Offset = offset,
                Limit = limit
            };
            if (list.Count > 0) SetCache(cacheKey, res);
            return res;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new CatalogPageResult { ErrorMessage = "Não foi possível carregar o catálogo: " + ex.Message };
        }
    }

    public static async Task<List<CatalogPack>> GetPacksAsync(int offset, int limit, CancellationToken ct)
    {
        var cacheKey = $"packs:{offset}:{limit}";
        if (TryGetCache<List<CatalogPack>>(cacheKey, out var cached))
            return cached;

        try
        {
            string url = offset == 0 && limit <= 8
                ? $"{BaseUrl}/packs/trending"
                : $"{BaseUrl}/packs?offset={offset}&limit={limit}";

            using var doc = await GetJsonAsync(url, ct);
            var packs = ParsePacksArray(doc.RootElement, "data");
            if (packs.Count > 0) SetCache(cacheKey, packs);
            return packs;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Tenta /packs/trending como fallback se /packs paginado falhar
            try
            {
                using var doc = await GetJsonAsync($"{BaseUrl}/packs/trending", ct);
                var packs = ParsePacksArray(doc.RootElement, "data");
                if (packs.Count > 0) SetCache(cacheKey, packs);
                return packs;
            }
            catch (OperationCanceledException) { throw; }
            catch { return new List<CatalogPack>(); }
        }
    }

    public static async Task<CatalogPageResult> GetAppsByCategoryAsync(CatalogCategory category, int offset, int limit, CancellationToken ct)
    {
        if (category.Key == "trending")
            return await GetTrendingAppsAsync(ct);

        if (category.Key == "popular")
            return await GetPopularAppsAsync(offset, limit, ct);

        var cacheKey = $"cat:{category.Key}:{offset}:{limit}";
        if (TryGetCache<CatalogPageResult>(cacheKey, out var cached))
            return cached;

        // 1. Tenta o endpoint de tag do winstall
        if (!string.IsNullOrWhiteSpace(category.Tag))
        {
            try
            {
                var url = $"{BaseUrl}/apps/tags/{Uri.EscapeDataString(category.Tag)}?offset={offset}&limit={limit}";
                using var doc = await GetJsonAsync(url, ct);
                var list = ParseAppsArray(doc.RootElement, "data");
                int total = ReadInt(doc.RootElement, "total", list.Count);
                if (list.Count > 0)
                {
                    var res = new CatalogPageResult { Items = list, Total = total, Offset = offset, Limit = limit };
                    SetCache(cacheKey, res);
                    return res;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        // 2. Fallback para busca por palavra-chave no winstall (também contorna rate-limit eventual em /tags)
        if (!string.IsNullOrWhiteSpace(category.FallbackSearch))
        {
            var fallback = await SearchAppsAsync(category.FallbackSearch, offset, limit, ct);
            if (fallback.Items.Count > 0)
            {
                SetCache(cacheKey, fallback);
                return fallback;
            }
        }

        return new CatalogPageResult
        {
            ErrorMessage = "Não foi possível carregar aplicativos desta categoria no momento. Tente novamente em instantes."
        };
    }

    public static async Task<CatalogPageResult> SearchAppsAsync(string query, int offset, int limit, CancellationToken ct)
    {
        var trimmed = (query ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            return await GetPopularAppsAsync(offset, limit, ct);

        var cacheKey = $"search:{trimmed}:{offset}:{limit}";
        if (TryGetCache<CatalogPageResult>(cacheKey, out var cached))
            return cached;

        try
        {
            string url;
            if (trimmed.StartsWith("publisher:", StringComparison.OrdinalIgnoreCase))
            {
                var pub = trimmed["publisher:".Length..].Trim();
                url = $"{BaseUrl}/publishers/{Uri.EscapeDataString(pub)}?offset={offset}&limit={limit}";
            }
            else if (trimmed.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
            {
                var tag = trimmed["tags:".Length..].Trim();
                url = $"{BaseUrl}/apps/tags/{Uri.EscapeDataString(tag)}?offset={offset}&limit={limit}";
            }
            else
            {
                url = $"{BaseUrl}/apps/search?q={Uri.EscapeDataString(trimmed)}&offset={offset}&limit={limit}";
            }

            using var doc = await GetJsonAsync(url, ct);
            var list = ParseAppsArray(doc.RootElement, "data");
            int total = ReadInt(doc.RootElement, "total", list.Count);

            if (list.Count > 0 || offset > 0)
            {
                var res = new CatalogPageResult
                {
                    Items = list,
                    Total = total,
                    Offset = offset,
                    Limit = limit
                };
                if (list.Count > 0) SetCache(cacheKey, res);
                return res;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // Fallback local via `winget search` caso a API remota não responda ou retorne vazio
        if (offset == 0 && AppInstaller.WingetAvailable)
        {
            try
            {
                var localApps = await AppInstaller.SearchWingetLocalAsync(trimmed, ct);
                if (localApps.Count > 0)
                {
                    var res = new CatalogPageResult
                    {
                        Items = localApps,
                        Total = localApps.Count,
                        Offset = 0,
                        Limit = localApps.Count,
                        FromLocalFallback = true
                    };
                    SetCache(cacheKey, res);
                    return res;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return new CatalogPageResult
        {
            Items = new List<CatalogApp>(),
            Total = 0,
            Offset = offset,
            Limit = limit
        };
    }

    // ---------------- Helpers JSON e Cache ----------------

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static List<CatalogApp> ParseAppsArray(JsonElement root, string propertyName)
    {
        var list = new List<CatalogApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            var app = ParseSingleApp(item);
            if (app != null && seen.Add(app.WingetId))
                list.Add(app);
        }
        return list;
    }

    private static CatalogApp? ParseSingleApp(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        string id = ReadString(item, "_id");
        if (string.IsNullOrWhiteSpace(id)) id = ReadString(item, "appId");
        if (string.IsNullOrWhiteSpace(id)) return null;

        string name = ReadString(item, "name");
        if (string.IsNullOrWhiteSpace(name)) name = ReadString(item, "appName");
        if (string.IsNullOrWhiteSpace(name)) name = id;

        string publisher = ReadString(item, "publisher");
        string desc = ReadString(item, "desc");
        if (string.IsNullOrWhiteSpace(desc)) desc = ReadString(item, "fullDesc");
        string latestVer = ReadString(item, "latestVersion");
        int downloads = ReadInt(item, "downloadCount", 0);

        var tags = new List<string>();
        if (item.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tagsEl.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    var s = t.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) tags.Add(s);
                }
            }
        }

        return new CatalogApp
        {
            WingetId = id.Trim(),
            Name = name.Trim(),
            Publisher = publisher.Trim(),
            Description = desc.Trim(),
            LatestVersion = latestVer.Trim(),
            DownloadCount = downloads,
            Tags = tags
        };
    }

    private static List<CatalogPack> ParsePacksArray(JsonElement root, string propertyName)
    {
        var packs = new List<CatalogPack>();
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return packs;

        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            string id = ReadString(el, "_id");
            string name = ReadString(el, "name");
            if (string.IsNullOrWhiteSpace(name)) name = ReadString(el, "title");
            string desc = ReadString(el, "description");
            if (string.IsNullOrWhiteSpace(desc)) desc = ReadString(el, "desc");

            var apps = ParseAppsArray(el, "apps");
            if (string.IsNullOrWhiteSpace(name) || apps.Count == 0) continue;

            packs.Add(new CatalogPack
            {
                Id = id,
                Name = name.Trim(),
                Description = desc.Trim(),
                Apps = apps
            });
        }

        return packs;
    }

    private static string ReadString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString() ?? "";
        return "";
    }

    private static int ReadInt(JsonElement el, string prop, int fallback)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out int n))
                return n;
        }
        return fallback;
    }

    private static bool TryGetCache<T>(string key, out T value) where T : class
    {
        if (Cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow && entry.Data is T typed)
        {
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }

    private static void SetCache(string key, object data)
    {
        Cache[key] = (DateTime.UtcNow.Add(CacheTtl), data);
    }
}
