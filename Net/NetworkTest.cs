using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using FrameForge.Core;

namespace ResetExplode;

/// <summary>
/// Diagnóstico de rede para lag: latência, jitter e perda de pacotes até ao router e à internet, DNS, velocidade de
/// download/upload (multi-ligação, com aquecimento descartado) e latência sob carga (bufferbloat), além dos dados do
/// adaptador e do Wi-Fi. Não grava nada no disco.
/// </summary>
public static class NetworkTest
{
    private const int DownloadStreams = 8;
    private const int UploadStreams = 8;
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MeasureWindow = TimeSpan.FromSeconds(9);

    private sealed record DownloadTarget(string Name, string Url, long MaxBytes);

    // Duas fontes públicas: a primeira é a que costuma dar melhores resultados; a segunda serve de reserva.
    private static readonly DownloadTarget[] DownloadTargets =
    {
        new("Cloudflare", "https://speed.cloudflare.com/__down?bytes=50000000", 1_500_000_000),
        new("OVH", "https://proof.ovh.net/files/100Mb.dat", 1_500_000_000),
    };

    // Endpoints de envio que descartam os dados (não devolvem o corpo), por isso não inflacionam a medição.
    private static readonly (string Name, string Url)[] UploadTargets =
    {
        ("Cloudflare", "https://speed.cloudflare.com/__up"),
        ("httpbin", "https://httpbin.org/status/200"),
    };

    private sealed record SpeedResult(double Mbps, double AverageMbps, double PeakMbps, double Megabytes, string Server, double Seconds);

    private sealed record PingStat(string Name, string Address, int Sent, int Lost, double Min, double Avg, double Max, double Jitter)
    {
        public double LossPct => Sent == 0 ? 0 : 100.0 * Lost / Sent;
        public bool Answered => Sent - Lost > 0;
        public string Text => !Answered ? "não responde"
            : $"média {Avg:0.#} ms · mín {Min:0.#} · máx {Max:0.#} · jitter {Jitter:0.#} ms · perda {LossPct:0.#}%";
    }

    private static HttpClient NewClient() => new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = Math.Max(DownloadStreams, UploadStreams) + 2,
        ConnectTimeout = TimeSpan.FromSeconds(6),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.None,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    // ---------------------------------------------------------------- latência

    private static PingStat Stat(string name, string address, List<double> rtts, int sent)
    {
        if (rtts.Count == 0) return new PingStat(name, address, sent, sent, 0, 0, 0, 0);
        double jitter = 0;
        for (int i = 1; i < rtts.Count; i++) jitter += Math.Abs(rtts[i] - rtts[i - 1]);
        jitter = rtts.Count > 1 ? jitter / (rtts.Count - 1) : 0;
        return new PingStat(name, address, sent, sent - rtts.Count, rtts.Min(), rtts.Average(), rtts.Max(), jitter);
    }

    private static async Task<(List<double> Rtts, int Sent)> PingLoop(IPAddress ip, int count, int intervalMs, CancellationToken ct)
    {
        var rtts = new List<double>();
        int sent = 0;
        using var ping = new Ping();
        for (int i = 0; (count <= 0 || i < count) && !ct.IsCancellationRequested; i++)
        {
            var sw = Stopwatch.StartNew();
            sent++;
            try
            {
                var reply = await ping.SendPingAsync(ip, 1500);
                if (reply.Status == IPStatus.Success) rtts.Add(Math.Max(1, reply.RoundtripTime));
            }
            catch (PingException) { }
            catch (InvalidOperationException) { }
            int wait = intervalMs - (int)sw.ElapsedMilliseconds;
            if (wait > 0) { try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; } }
        }
        return (rtts, sent);
    }

    // ---------------------------------------------------------------- velocidade

    /// <summary>
    /// Download com várias ligações: descarta os primeiros 2 s e mede a ligação durante a janela seguinte.
    /// Usa a melhor média móvel completa de 3 s; também reporta a média da medição e o pico instantâneo.
    /// </summary>
    private static async Task<SpeedResult?> MeasureDownload(CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var target in DownloadTargets)
        {
            try
            {
                var result = await DownloadFrom(target, ct);
                if (result != null) return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add($"{target.Name}: {ex.Message}"); }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" · ", errors));
        return null;
    }

    private static async Task<SpeedResult?> DownloadFrom(DownloadTarget target, CancellationToken ct)
    {
        using var client = NewClient();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long total = 0;
        int activeStreams = DownloadStreams;
        var streamErrors = new ConcurrentQueue<string>();
        var clock = Stopwatch.StartNew();
        bool interrupted = false;
        var samples = new List<(double T, long B)>();

        var streams = Enumerable.Range(0, DownloadStreams).Select(_ => Task.Run(async () =>
        {
            var buffer = new byte[256 * 1024];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var response = await client.GetAsync(target.Url, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(stop.Token);
                    int read;
                    while ((read = await stream.ReadAsync(buffer, stop.Token)) > 0)
                    {
                        Interlocked.Add(ref total, read);
                        if (target.MaxBytes > 0 && Interlocked.Read(ref total) > target.MaxBytes)
                        {
                            interrupted = true;
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { streamErrors.Enqueue(ex.Message); }
            finally { Interlocked.Decrement(ref activeStreams); }
        })).ToArray();

        // Amostragem: 1.º fotografia no fim do aquecimento, depois pico e fim.
        long bytesAtWarmup = 0; double timeAtWarmup = 0;
        long lastBytes = 0; double lastTime = 0; double peak = 0;
        var totalWindow = Warmup + MeasureWindow;
        while (clock.Elapsed < totalWindow && !ct.IsCancellationRequested && Volatile.Read(ref activeStreams) > 0)
        {
            await Task.Delay(250, ct);
            long now = Interlocked.Read(ref total);
            double seconds = clock.Elapsed.TotalSeconds;
            if (seconds >= Warmup.TotalSeconds)
            {
                samples.Add((seconds, now));
                if (timeAtWarmup == 0) { timeAtWarmup = seconds; bytesAtWarmup = now; lastBytes = now; lastTime = seconds; }
                else if (seconds > lastTime)
                {
                    peak = Math.Max(peak, (now - lastBytes) * 8 / 1e6 / (seconds - lastTime));
                    lastBytes = now; lastTime = seconds;
                }
            }
            if (interrupted) break;
        }
        stop.Cancel();
        try { await Task.WhenAll(streams); } catch { }
        ct.ThrowIfCancellationRequested();

        double endTime = clock.Elapsed.TotalSeconds;
        long endBytes = Interlocked.Read(ref total);
        if (timeAtWarmup == 0 || endTime <= timeAtWarmup)
        {
            string detail = streamErrors.TryPeek(out string? error) ? error : "o servidor não enviou dados";
            throw new InvalidOperationException(detail);
        }
        double measured = endBytes - bytesAtWarmup;
        if (measured <= 0)
        {
            string detail = streamErrors.TryPeek(out string? error) ? error : "não foram recebidos dados durante a medição";
            throw new InvalidOperationException(detail);
        }

        double average = measured * 8 / 1e6 / (endTime - timeAtWarmup);
        double mbps = BestWindow(samples, 3.0) is double sustained && sustained > 0 ? sustained : average;
        return new SpeedResult(mbps, average, peak, measured / 1e6, target.Name, endTime - timeAtWarmup);
    }

    /// <summary>Upload por várias ligações, com um corpo que conta os bytes realmente enviados.</summary>
    private static async Task<(SpeedResult? Result, string? Error)> MeasureUpload(CancellationToken ct)
    {
        string? firstError = null;
        foreach (var (name, url) in UploadTargets)
        {
            try
            {
                var (result, error) = await UploadTo(name, url, ct);
                if (result != null) return (result, null);
                firstError ??= error;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { firstError ??= ex.Message; }
        }
        return (null, firstError ?? "nenhum servidor de upload respondeu");
    }

    private static async Task<(SpeedResult? Result, string? Error)> UploadTo(string name, string url, CancellationToken ct)
    {
        using var client = NewClient();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long total = 0;
        string? error = null;
        var clock = Stopwatch.StartNew();
        var samples = new List<(double T, long B)>();

        var streams = Enumerable.Range(0, UploadStreams).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var content = new CountingContent(size => Interlocked.Add(ref total, size), 8 * 1024 * 1024);
                    using var response = await client.PostAsync(url, content, stop.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        Interlocked.CompareExchange(ref error, $"{(int)response.StatusCode} {response.ReasonPhrase}", null);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Interlocked.CompareExchange(ref error, ex.Message, null); }
        })).ToArray();

        long bytesAtWarmup = 0; double timeAtWarmup = 0;
        long lastBytes = 0; double lastTime = 0; double peak = 0;
        var totalWindow = Warmup + TimeSpan.FromSeconds(7);
        while (clock.Elapsed < totalWindow && !ct.IsCancellationRequested)
        {
            await Task.Delay(250, ct);
            long now = Interlocked.Read(ref total);
            double seconds = clock.Elapsed.TotalSeconds;
            if (seconds >= Warmup.TotalSeconds)
            {
                samples.Add((seconds, now));
                if (timeAtWarmup == 0) { timeAtWarmup = seconds; bytesAtWarmup = now; lastBytes = now; lastTime = seconds; }
                else if (seconds > lastTime)
                {
                    peak = Math.Max(peak, (now - lastBytes) * 8 / 1e6 / (seconds - lastTime));
                    lastBytes = now; lastTime = seconds;
                }
            }
        }
        stop.Cancel();
        try { await Task.WhenAll(streams); } catch { }
        ct.ThrowIfCancellationRequested();

        double endTime = clock.Elapsed.TotalSeconds;
        long endBytes = Interlocked.Read(ref total);
        if (timeAtWarmup == 0 || endTime <= timeAtWarmup)
            return (null, error ?? "o servidor cortou o envio");
        double measured = endBytes - bytesAtWarmup;
        if (measured <= 0) return (null, error ?? "nenhum byte foi aceite");

        double average = measured * 8 / 1e6 / (endTime - timeAtWarmup);
        double mbps = BestWindow(samples, 4.0) is double sustained && sustained > 0 ? sustained : average;
        return (new SpeedResult(mbps, average, peak, measured / 1e6, name, endTime - timeAtWarmup), null);
    }

    /// <summary>
    /// Melhor janela completa de duração fixa (Mbps), interpolando o limite inicial entre amostras.
    /// </summary>
    private static double BestWindow(List<(double T, long B)> samples, double seconds)
    {
        double best = 0;
        int start = 0;
        for (int end = 1; end < samples.Count; end++)
        {
            double startTime = samples[end].T - seconds;
            if (startTime < samples[0].T) continue;
            while (start + 1 < end && samples[start + 1].T < startTime) start++;

            var before = samples[start];
            var after = samples[start + 1];
            double fraction = (startTime - before.T) / (after.T - before.T);
            double startBytes = before.B + (after.B - before.B) * fraction;
            double windowBytes = samples[end].B - startBytes;
            best = Math.Max(best, windowBytes * 8 / 1e6 / seconds);
        }
        return best;
    }

    /// <summary>Corpo de envio que gera dados à medida e conta cada bloco entregue ao socket.</summary>
    private sealed class CountingContent : HttpContent
    {
        private readonly Action<long> _count;
        private readonly long _length;
        public CountingContent(Action<long> count, long length)
        {
            _count = count;
            _length = length;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[128 * 1024];
            new Random(7).NextBytes(buffer);
            long remaining = _length;
            while (remaining > 0)
            {
                int size = (int)Math.Min(buffer.Length, remaining);
                await stream.WriteAsync(buffer.AsMemory(0, size));
                _count(size);
                remaining -= size;
            }
        }

        protected override bool TryComputeLength(out long length) { length = _length; return true; }
    }

    // ---------------------------------------------------------------- adaptador e Wi-Fi

    private static string AdapterType(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet (cabo)",
        _ => type.ToString(),
    };

    /// <summary>Dados do Wi-Fi pelo netsh (funciona em Windows PT e EN, porque só se leem números e nomes curtos).</summary>
    private static string? WifiInfo()
    {
        try
        {
            var info = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var process = Process.Start(info);
            if (process == null) return null;
            string text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);
            if (string.IsNullOrWhiteSpace(text)) return null;

            string? Find(params string[] keys) =>
                text.Split('\n').Select(l => l.Trim())
                    .FirstOrDefault(l => keys.Any(k => l.StartsWith(k, StringComparison.OrdinalIgnoreCase)))
                    ?.Split(':', 2).ElementAtOrDefault(1)?.Trim();

            string? ssid = Find("SSID", "Nome");
            string? signal = Find("Sinal", "Signal");
            string? radio = Find("Tipo de rádio", "Radio type");
            string? channel = Find("Canal", "Channel");
            string? rx = Find("Velocidade de receção", "Receive rate");
            string? tx = Find("Velocidade de transmissão", "Transmit rate");
            if (ssid == null && signal == null) return null;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ssid)) parts.Add(ssid!);
            if (!string.IsNullOrWhiteSpace(signal)) parts.Add($"sinal {signal}");
            if (!string.IsNullOrWhiteSpace(radio)) parts.Add(radio!);
            if (!string.IsNullOrWhiteSpace(channel)) parts.Add($"canal {channel}");
            if (!string.IsNullOrWhiteSpace(rx) || !string.IsNullOrWhiteSpace(tx)) parts.Add($"taxa {rx}↓ / {tx}↑");
            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
        catch { return null; }
    }

    /// <summary>Segmentos TCP retransmitidos desde o arranque (netstat -s): valores altos indicam Wi-Fi/cabo com ruído.</summary>
    private static string? TcpRetransmits()
    {
        try
        {
            var info = new ProcessStartInfo("netstat", "-s -p tcp")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process == null) return null;
            string text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);
            var match = Regex.Match(text, @"(?im)^\s*(?:Segments Retransmitted|Segmentos retransmitidos)\s*[:\.]*\s*([\d\.,]+)");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static string BufferbloatGrade(double deltaMs) => deltaMs switch
    {
        < 30 => "A (excelente)",
        < 60 => "B (bom)",
        < 100 => "C (aceitável)",
        < 200 => "D (mau)",
        < 400 => "E (muito mau)",
        _ => "F (inutilizável em jogo)",
    };

    // ---------------------------------------------------------------- teste completo

    public static async Task<List<DiskHealthCard>> Run(Action<string, double> progress, CancellationToken ct)
    {
        progress("A procurar a ligação ativa…", 0.02);
        NetworkInterface? nic = null;
        try
        {
            nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                            && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                              && !g.Address.Equals(IPAddress.Any)))
                .OrderByDescending(n => n.Speed).FirstOrDefault();
        }
        catch { }
        if (nic == null)
            return new() { new DiskHealthCard
            {
                Title = "Sem ligação", Meta = "Nenhum adaptador ligado com gateway", Verdict = DiskVerdict.Critical, Label = "SEM REDE",
                Reasons = "O Windows não encontrou uma ligação ativa com acesso ao router. Verifica o cabo, o Wi-Fi ou o adaptador.",
            } };

        var props = nic.GetIPProperties();
        var gateway = props.GatewayAddresses.First(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                        && !g.Address.Equals(IPAddress.Any)).Address;
        string ip = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "—";
        var dnsServers = props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).ToList();
        string dns = dnsServers.Count > 0 ? string.Join(", ", dnsServers) : "—";
        string link = nic.Speed > 0 ? $"{nic.Speed / 1_000_000.0:0} Mbps" : "—";
        string mtu = "—";
        try { mtu = props.GetIPv4Properties()?.Mtu.ToString() ?? "—"; } catch { }
        string? wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? WifiInfo() : null;
        string? retransmits = TcpRetransmits();

        // 1) Latência / jitter / perda: router e dois DNS públicos
        progress("A medir latência e perda de pacotes (≈ 7 s)…", 0.08);
        var targets = new (string Name, IPAddress Address)[]
        {
            ("Router", gateway), ("Internet · Cloudflare", IPAddress.Parse("1.1.1.1")), ("Internet · Google", IPAddress.Parse("8.8.8.8")),
        };
        var pings = await Task.WhenAll(targets.Select(async t =>
        {
            var (rtts, sent) = await PingLoop(t.Address, 30, 200, ct);
            return Stat(t.Name, t.Address.ToString(), rtts, sent);
        }));
        ct.ThrowIfCancellationRequested();

        // 2) DNS do sistema (três consultas diferentes, para não apanhar só cache)
        progress("A testar o DNS…", 0.26);
        double? dnsMs = null;
        try
        {
            var times = new List<double>();
            foreach (string host in new[] { "cloudflare.com", "steampowered.com", "nvidia.com" })
            {
                var sw = Stopwatch.StartNew();
                await Dns.GetHostAddressesAsync(host, ct);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            dnsMs = times.Count > 0 ? times.Average() : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // 3) Download com ping em paralelo (latência sob carga)
        progress("A medir o download (≈ 11 s)…", 0.34);
        using var loadStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loadedPing = PingLoop(IPAddress.Parse("1.1.1.1"), 0, 200, loadStop.Token);
        SpeedResult? down = null;
        string? downError = null;
        try { down = await MeasureDownload(ct); }
        catch (OperationCanceledException) { loadStop.Cancel(); throw; }
        catch (Exception ex) { downError = ex.Message; }
        loadStop.Cancel();
        var (loadedRtts, _) = await loadedPing;
        double? loadedMs = loadedRtts.Count >= 5 ? loadedRtts.Average() : null;

        // 4) Upload
        progress("A medir o upload (≈ 9 s)…", 0.72);
        SpeedResult? up = null;
        string? upError = null;
        try { (up, upError) = await MeasureUpload(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { upError = ex.Message; }

        progress("A analisar…", 0.95);

        // ---- Veredito ----
        var router = pings[0];
        var internet = pings.Skip(1).Where(p => p.Answered).ToList();
        double netAvg = internet.Count > 0 ? internet.Average(p => p.Avg) : 0;
        double netLoss = internet.Count > 0 ? internet.Average(p => p.LossPct) : 100;
        double netJitter = internet.Count > 0 ? internet.Max(p => p.Jitter) : 0;
        var warnings = new List<string>();
        bool bad = false;

        bool lanBad = router.Answered && (router.LossPct > 2 || router.Avg > 30 || router.Jitter > 15);
        if (lanBad) warnings.Add($"A rede local está instável (router: {router.Avg:0} ms, perda {router.LossPct:0.#}%, jitter {router.Jitter:0} ms). Se for Wi-Fi, aproxima-te do router ou usa cabo.");
        if (!router.Answered) warnings.Add("O router não responde a ping (alguns modelos bloqueiam). Isso por si só não é problema.");
        if (internet.Count == 0)
        {
            bad = true;
            warnings.Add("Nenhum servidor da internet respondeu: sem acesso à internet ou ICMP bloqueado pela rede.");
        }
        else
        {
            if (netLoss > 5 || netAvg > 150) bad = true;
            if (netLoss > 1) warnings.Add($"Há perda de pacotes até à internet ({netLoss:0.#}%). {(lanBad ? "Provavelmente vem da rede local." : "Como o router está bem, o problema deve estar na linha ou no operador.")}");
            if (netAvg > 80) warnings.Add($"Latência alta até à internet ({netAvg:0} ms).{(lanBad ? "" : " O router responde bem, por isso o atraso vem do operador.")}");
            if (netJitter > 20) warnings.Add($"O jitter está alto ({netJitter:0} ms): a latência oscila, o que provoca engasgos em jogos online.");
        }

        double? bloat = loadedMs != null && internet.Count > 0 ? loadedMs - internet.Min(p => p.Avg) : null;
        if (bloat is double b && b > 100)
            warnings.Add($"A latência subiu {b:0} ms com a ligação cheia (bufferbloat, nota {BufferbloatGrade(b)}). Ativa SQM/QoS no router ou limita a velocidade a ~90%.");
        if (dnsMs > 150) warnings.Add($"O DNS demorou {dnsMs:0} ms. Trocar para 1.1.1.1 ou 8.8.8.8 pode acelerar o carregamento.");
        if (down is null) warnings.Add("Não foi possível medir o download: " + (downError ?? "os servidores de teste estão inacessíveis ou bloqueados nesta rede."));
        else if (down.Mbps < 20) warnings.Add($"Download baixo ({down.Mbps:0.#} Mbps): condiciona atualizações, streaming e jogos com muito conteúdo.");
        if (up is null) warnings.Add("Não foi possível medir o upload" + (upError != null ? $": {upError}." : "."));
        else if (up.Mbps < 5) warnings.Add($"Upload baixo ({up.Mbps:0.#} Mbps): afeta stream, partilha de ecrã e voz em grupo.");
        if (wifi != null && Regex.Match(wifi, @"(\d+)\s*%") is { Success: true } m2 && int.TryParse(m2.Groups[1].Value, out int signalPct) && signalPct < 55)
            warnings.Add($"O sinal de Wi-Fi está em {signalPct}%. Liga por cabo ou aproxima-te do router: é a melhoria mais barata para o lag.");

        var verdict = bad ? DiskVerdict.Critical : warnings.Any(w => !w.StartsWith("O router não responde")) ? DiskVerdict.Warning : DiskVerdict.Good;
        string label = verdict switch { DiskVerdict.Critical => "RUIM", DiskVerdict.Warning => "ATENÇÃO", _ => "ESTÁVEL" };
        string reasons = warnings.Count > 0 ? string.Join(" ", warnings)
            : "Latência baixa, sem perda de pacotes e sem bufferbloat relevante. Se o jogo ainda engasga, o problema provavelmente não é a ligação: vê o Gargalo e as temperaturas.";

        var metrics = new List<KeyValuePair<string, string>>
        {
            new("Download", down is null ? "indisponível" : $"{down.Mbps:0.#} Mbps"),
            new("Upload", up is null ? "indisponível" : $"{up.Mbps:0.#} Mbps"),
        };
        if (down != null)
            metrics.Add(new("Download · servidor / média / pico", $"{down.Server} · média {down.AverageMbps:0.#} Mbps · pico {down.PeakMbps:0.#} Mbps · {down.Megabytes:0} MB"));
        else
            metrics.Add(new("Motivo do download indisponível", downError ?? "os servidores de teste não enviaram dados"));
        if (up != null)
            metrics.Add(new("Upload · servidor / média / pico", $"{up.Server} · média {up.AverageMbps:0.#} Mbps · pico {up.PeakMbps:0.#} Mbps · {up.Megabytes:0} MB"));
        else if (upError != null)
            metrics.Add(new("Motivo do upload indisponível", upError));
        foreach (var p in pings) metrics.Add(new($"{p.Name} ({p.Address})", p.Text));
        if (dnsMs != null) metrics.Add(new("DNS do sistema (média de 3 consultas)", $"{dnsMs:0} ms"));
        if (loadedMs != null) metrics.Add(new("Latência sob carga", $"{loadedMs:0} ms durante o download" + (bloat is double bb ? $" · bufferbloat {BufferbloatGrade(bb)}" : "")));
        metrics.Add(new("Adaptador", $"{nic.Name} · {AdapterType(nic.NetworkInterfaceType)} · ligação {link} · MTU {mtu}"));
        if (wifi != null) metrics.Add(new("Wi-Fi", wifi));
        metrics.Add(new("IP · Router · DNS", $"{ip} · {gateway} · {dns}"));
        if (retransmits != null) metrics.Add(new("Segmentos TCP retransmitidos (desde o arranque)", retransmits));

        progress("Concluído", 1);
        return new() { new DiskHealthCard
        {
            Title = "Diagnóstico de rede",
            Meta = $"{AdapterType(nic.NetworkInterfaceType)} · testado às {DateTime.Now:HH:mm:ss}",
            Verdict = verdict, Label = label, Reasons = reasons, Metrics = metrics,
        } };
    }
}
