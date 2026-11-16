using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace FrameForge.Core;

/// <summary>Uma versão publicada nas Releases do GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, long Size, string? Sha256, string PageUrl);

/// <summary>
/// Atualização automática a partir das Releases do GitHub.
/// <para>Procura: GET /repos/{Owner}/{Repo}/releases/latest; a release tem de ter o asset <see cref="AssetName"/> e a etiqueta "vX.Y.Z".
/// O SHA-256 vem do campo "digest" do asset (GitHub) ou de um asset "HyperCore.exe.sha256"; sem hash a atualização é recusada.</para>
/// <para>Instalar: transfere para %AppData%\HyperCore\atualizacao, confirma hash, nome do produto e versão do ficheiro, troca o exe
/// (o exe em uso é renomeado para .old, o que o Windows permite), abre a versão nova com --esperar &lt;pid&gt; e espera que ela dê sinal
/// de vida. Se não der, volta a pôr o exe antigo no lugar — a app nunca fica sem exe.</para>
/// </summary>
public static class Updater
{
    // Repositório onde são publicadas as versões (tem de ser público: a app procura sem conta nem token)
    public const string Owner = "FrameForge-Beta";
    public const string Repo = "FrameForge-Beta";
    public const string AssetName = "HyperCore.exe";

    public const string ArgWait = "--esperar";
    public const string ArgUpdated = "--atualizado-de";
    private const string AliveEventName = @"Local\HyperCore.Update.Alive";

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Versão nova encontrada na última verificação (null = está em dia ou ainda não se verificou).</summary>
    public static UpdateInfo? Available { get; private set; }
    public static event Action? AvailableChanged;

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Busy = new(1, 1);

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HyperCore-Updater", Current.ToString(3)));
        return h;
    }

    public static string Folder => Path.Combine(AppPaths.Root, "atualizacao");

    /// <summary>Procura uma versão nova. Devolve null se já está em dia. Lança exceção com mensagem simples se não conseguir procurar.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new InvalidOperationException("Sem ligação ao GitHub. Confirma a internet e tenta outra vez."); }

        using (resp)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException("Ainda não há versões publicadas (ou o repositório das versões não é público).");
            if ((int)resp.StatusCode is 403 or 429)
                throw new InvalidOperationException("O GitHub está a limitar pedidos. Tenta daqui a uma hora.");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var info = Parse(json);
            AppServices.Settings.UpdateLastCheckUtc = DateTime.UtcNow;
            try { AppServices.Settings.Save(); } catch { }
            var newer = info != null && info.Version > Current ? info : null;
            Available = newer;
            AvailableChanged?.Invoke();
            return newer;
        }
    }

    internal static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        string tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version)) return null;
        string notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        string page = r.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";

        string? url = null, sha = null, shaUrl = null;
        long size = 0;
        if (r.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                string dl = a.GetProperty("browser_download_url").GetString() ?? "";
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    url = dl;
                    size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    if (a.TryGetProperty("digest", out var dg) && dg.GetString() is string dig && dig.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = dig[7..].Trim().ToLowerInvariant();
                }
                else if (string.Equals(name, AssetName + ".sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = dl;
            }
        if (url == null) return null;
        // O hash fica no Notes só quando não veio no "digest": é lido na transferência
        return new UpdateInfo(version, tag, notes, url, size, sha ?? (shaUrl != null ? "url:" + shaUrl : null), page);
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        string t = tag.Trim().TrimStart('v', 'V');
        int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0) t = t[..cut];
        if (Version.TryParse(t, out var v)) { version = Normalize(v); return true; }
        version = new Version(0, 0, 0);
        return false;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>Transfere e confirma o exe novo. Devolve o caminho do ficheiro pronto a instalar.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        await Busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Folder);
            string final = Path.Combine(Folder, $"HyperCore-{info.Version.ToString(3)}.exe");
            string part = final + ".part";

            string expected = await ExpectedHashAsync(info, ct).ConfigureAwait(false);
            if (File.Exists(final) && Verify(final, expected, info.Version)) return final; // já transferido antes

            using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? info.Size;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            if (!Verify(part, expected, info.Version))
            {
                TryDelete(part);
                throw new InvalidOperationException("O ficheiro transferido não é o original (o código de verificação não bate certo). Nada foi instalado.");
            }
            TryDelete(final);
            File.Move(part, final);
            return final;
        }
        finally { Busy.Release(); }
    }

    private static async Task<string> ExpectedHashAsync(UpdateInfo info, CancellationToken ct)
    {
        string? h = info.Sha256;
        if (h != null && h.StartsWith("url:", StringComparison.Ordinal))
        {
            string txt = await Http.GetStringAsync(h[4..], ct).ConfigureAwait(false);
            h = txt.Trim().Split(' ', '\t', '\r', '\n')[0];
        }
        h = h?.Trim().ToLowerInvariant();
        if (h is not { Length: 64 } || !h.All(Uri.IsHexDigit))
            throw new InvalidOperationException("A versão publicada não tem código de verificação (SHA-256). Por segurança não é instalada.");
        return h;
    }

    private static bool Verify(string file, string sha256, Version version)
    {
        try
        {
            using (var fs = File.OpenRead(file))
                if (!Convert.ToHexString(SHA256.HashData(fs)).Equals(sha256, StringComparison.OrdinalIgnoreCase)) return false;
            var vi = FileVersionInfo.GetVersionInfo(file);
            if (!string.Equals(vi.ProductName, "HyperCore", StringComparison.OrdinalIgnoreCase)) return false;
            return TryParseVersion(vi.ProductVersion ?? vi.FileVersion ?? "", out var fv) && fv == version;
        }
        catch { return false; }
    }

    /// <summary>Há algo a decorrer que uma reabertura da app estragaria (jogo, scan, Modo Jogo, gravação)?</summary>
    public static string? BusyReason()
    {
        try
        {
            if (Tuning.ScanService.Instance.Running) return "o scan está a correr";
            if (Games.GameMode.IsActive) return "o Modo Jogo está ligado";
            if (Games.GameRunner.TemporaryTweaksActive) return "há um jogo aberto pela app";
            if (AppServices.Recorder.Active) return "uma sessão está a ser gravada";
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Troca o exe pelo novo e abre-o. Devolve true se a versão nova arrancou (o chamador deve então fechar a app);
    /// false com a mensagem se algo falhou — nesse caso o exe antigo já voltou ao lugar.
    /// </summary>
    public static (bool ok, string message) Install(string downloaded, bool startHidden = false)
    {
        string? target = Environment.ProcessPath;
        if (target == null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(target).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return (false, "Esta cópia corre a partir do código-fonte; a atualização só funciona no HyperCore.exe publicado.");

        string old = target + ".old";
        try
        {
            TryDelete(old);
            File.Move(target, old);                // o Windows deixa renomear um exe em uso
        }
        catch (Exception ex) { return (false, "Não consegui substituir o programa: " + ex.Message); }

        try { File.Copy(downloaded, target, overwrite: true); }
        catch (Exception ex)
        {
            Rollback(target, old);
            return (false, "Não consegui copiar a versão nova: " + ex.Message);
        }

        using var alive = new EventWaitHandle(false, EventResetMode.ManualReset, AliveEventName);
        alive.Reset();
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo(target)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(target)!,
                ArgumentList = { ArgWait, Environment.ProcessId.ToString(), ArgUpdated, Current.ToString(3) },
            };
            if (startHidden) psi.ArgumentList.Add("--startup-minimized"); // estava na bandeja: a versão nova também abre lá
            p = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Rollback(target, old);
            return (false, "A versão nova não abriu: " + ex.Message);
        }

        // Espera que a versão nova dê sinal de vida (até 30 s). Se fechar ou não responder, volta tudo atrás.
        bool started = alive.WaitOne(TimeSpan.FromSeconds(30));
        if (!started)
        {
            try { if (p is { HasExited: false }) p.Kill(); } catch { }
            try { p?.WaitForExit(5000); } catch { }
            Rollback(target, old);
            return (false, "A versão nova não arrancou. Voltei a pôr a versão anterior; nada mudou.");
        }
        return (true, "");
    }

    private static void Rollback(string target, string old)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (File.Exists(old))
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(old, target);
                }
                return;
            }
            catch { Thread.Sleep(300); }
        }
    }

    /// <summary>
    /// Chamado no arranque, ANTES do mutex de instância única. Se a app foi aberta pelo atualizador: dá sinal de vida e espera
    /// que a versão anterior feche (para o mutex ficar livre).
    /// </summary>
    public static void OnStartupArgs(string[] args)
    {
        int i = Array.IndexOf(args, ArgWait);
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
        try
        {
            using var alive = EventWaitHandle.OpenExisting(AliveEventName);
            alive.Set();
        }
        catch { }
        try
        {
            using var prev = Process.GetProcessById(pid);
            prev.WaitForExit(30000);
        }
        catch { } // já fechou
    }

    /// <summary>Depois de arrancar: apaga o exe antigo e as transferências. Devolve a versão de onde veio, se veio de uma atualização.</summary>
    public static string? CleanupAfterStart(string[] args)
    {
        string? from = null;
        int i = Array.IndexOf(args, ArgUpdated);
        if (i >= 0 && i + 1 < args.Length) from = args[i + 1];
        if (Environment.ProcessPath is string exe)
            for (int k = 0; k < 20 && File.Exists(exe + ".old"); k++)
            {
                try { File.Delete(exe + ".old"); } catch { Thread.Sleep(500); }
            }
        try
        {
            if (Directory.Exists(Folder))
                foreach (var f in Directory.GetFiles(Folder))
                    if (!f.Contains($"HyperCore-{Current.ToString(3)}", StringComparison.OrdinalIgnoreCase) || from != null) TryDelete(f);
        }
        catch { }
        return from;
    }

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
}
