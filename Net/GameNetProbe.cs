using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FrameForge.Net;

/// <summary>Como se mede a latência até um alvo.</summary>
public enum ProbeKind { Icmp, Tcp }

/// <summary>
/// Um alvo de medição. <see cref="Approx"/> = servidor de referência (centro de dados onde muitos jogos alojam servidores),
/// não o servidor exato do jogo: o valor é aproximado.
/// </summary>
public sealed record GameNetTarget(string Id, string Name, string Short, string Host, ProbeKind Kind, int Port, bool Approx, string Note);

/// <summary>A ligação de rede que está a ser usada agora (a que tem gateway IPv4).</summary>
public sealed record ActiveLink(string Name, string Description, string Id, int Index, bool IsWifi, long SpeedBps, IPAddress? Gateway);

/// <summary>Medições de latência (só leitura): ICMP (ping) ou ligação TCP, sempre com tempo limite.</summary>
public static class GameNetProbe
{
    public static readonly GameNetTarget[] Targets =
    {
        new("cf", "Cloudflare (1.1.1.1)", "Cloudflare", "1.1.1.1", ProbeKind.Icmp, 443, false, "Rede pública com servidores em quase todas as cidades europeias."),
        new("google", "Google (8.8.8.8)", "Google", "8.8.8.8", ProbeKind.Icmp, 443, false, "Rede pública da Google."),
        new("quad9", "Quad9 (9.9.9.9)", "Quad9", "9.9.9.9", ProbeKind.Icmp, 443, false, "Rede pública sem fins lucrativos, com sede na Suíça."),
        new("aws-fra", "Jogos · Frankfurt", "Frankfurt", "dynamodb.eu-central-1.amazonaws.com", ProbeKind.Tcp, 443, true, "Centro de dados AWS em Frankfurt, muito usado por servidores de jogos na Europa."),
        new("aws-par", "Jogos · Paris", "Paris", "dynamodb.eu-west-3.amazonaws.com", ProbeKind.Tcp, 443, true, "Centro de dados AWS em Paris."),
        new("aws-mad", "Jogos · Espanha", "Espanha", "dynamodb.eu-south-2.amazonaws.com", ProbeKind.Tcp, 443, true, "Centro de dados AWS em Espanha (Aragão)."),
        new("aws-irl", "Jogos · Irlanda", "Irlanda", "dynamodb.eu-west-1.amazonaws.com", ProbeKind.Tcp, 443, true, "Centro de dados AWS na Irlanda."),
        new("aws-lon", "Jogos · Londres", "Londres", "dynamodb.eu-west-2.amazonaws.com", ProbeKind.Tcp, 443, true, "Centro de dados AWS em Londres."),
        new("aws-sto", "Jogos · Estocolmo", "Estocolmo", "dynamodb.eu-north-1.amazonaws.com", ProbeKind.Tcp, 443, true, "Centro de dados AWS em Estocolmo."),
    };

    public static GameNetTarget Find(string? id) => Targets.FirstOrDefault(t => t.Id == id) ?? Targets[0];

    private static readonly Dictionary<string, (IPAddress Ip, DateTime Until)> DnsCache = new();
    private static readonly object CacheGate = new();

    /// <summary>Resolve o nome uma vez (cache de 10 min) para o tempo de DNS não entrar na medição.</summary>
    public static async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var direct)) return direct;
        lock (CacheGate)
            if (DnsCache.TryGetValue(host, out var c) && c.Until > DateTime.UtcNow) return c.Ip;
        try
        {
            var all = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            var ip = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
            if (ip != null) lock (CacheGate) DnsCache[host] = (ip, DateTime.UtcNow.AddMinutes(10));
            return ip;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Uma medição. Devolve a latência em ms, ou null se não houve resposta dentro do tempo limite (conta como perda).</summary>
    public static async Task<double?> MeasureAsync(GameNetTarget target, CancellationToken ct, int timeoutMs = 1000)
    {
        var ip = await ResolveAsync(target.Host, ct).ConfigureAwait(false);
        if (ip == null) return null;
        if (target.Kind == ProbeKind.Tcp) return await TcpAsync(ip, target.Port, timeoutMs, ct).ConfigureAwait(false);
        try { return await PingAsync(ip, timeoutMs).ConfigureAwait(false); }
        catch (PingException) { return await TcpAsync(ip, target.Port, timeoutMs, ct).ConfigureAwait(false); }
    }

    /// <summary>Ping ICMP clássico (o mesmo do comando ping). null = sem resposta.</summary>
    public static async Task<double?> PingAsync(IPAddress ip, int timeoutMs = 1000)
    {
        using var ping = new Ping();
        var clock = Stopwatch.StartNew();
        var reply = await ping.SendPingAsync(ip, timeoutMs, new byte[32], new PingOptions(64, true)).ConfigureAwait(false);
        clock.Stop();
        if (reply.Status != IPStatus.Success) return null;
        // RoundtripTime vem em ms inteiros (0 em redes locais rápidas): abaixo de 1 ms usa o cronómetro, limitado a 1 ms.
        double rt = reply.RoundtripTime;
        return rt >= 1 ? rt : Math.Min(clock.Elapsed.TotalMilliseconds, 0.9);
    }

    /// <summary>Tempo para abrir uma ligação TCP (1 ida e volta). Usado para servidores que não respondem a ping.</summary>
    public static async Task<double?> TcpAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var clock = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), cts.Token).ConfigureAwait(false);
            clock.Stop();
            return clock.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
        finally
        {
            try { if (socket.Connected) socket.Shutdown(SocketShutdown.Both); } catch { }
        }
    }

    /// <summary>A ligação ativa (com gateway IPv4), preferindo a mais rápida. null se não houver rede.</summary>
    public static ActiveLink? ActiveLink()
    {
        try
        {
            var nic = ActiveInterfaces().OrderByDescending(n => n.Speed).FirstOrDefault();
            if (nic == null) return null;
            var props = nic.GetIPProperties();
            var gw = props.GatewayAddresses.Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            int index = 0;
            try { index = props.GetIPv4Properties()?.Index ?? 0; } catch { }
            return new ActiveLink(nic.Name, nic.Description, nic.Id, index, nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211, nic.Speed, gw);
        }
        catch { return null; }
    }

    /// <summary>Adaptadores ligados com gateway IPv4 (os que levam o tráfego para a Internet).</summary>
    public static IEnumerable<NetworkInterface> ActiveInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(n =>
            n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            && n.GetIPProperties().GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)));
}
