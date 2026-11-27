using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace ResetExplode;

public sealed record DnsProvider(string Name, string Primary, string Secondary, string Note);

public sealed record DnsResult(string Provider, string Servers, double? AverageMs, double? MinMs, bool Ok, string? Error);

/// <summary>
/// Mede a latência de resolução de nomes em servidores DNS conhecidos, com uma consulta A feita diretamente por UDP.
/// Só lê: não altera a configuração de rede do Windows.
/// </summary>
public static class DnsBenchmark
{
    public static readonly DnsProvider[] Providers =
    {
        new("Cloudflare", "1.1.1.1", "1.0.0.1", "rápido, com foco na privacidade"),
        new("Google", "8.8.8.8", "8.8.4.4", "muito estável"),
        new("Quad9", "9.9.9.9", "149.112.112.112", "bloqueia domínios maliciosos"),
        new("AdGuard DNS", "94.140.14.14", "94.140.15.15", "bloqueia publicidade"),
        new("OpenDNS", "208.67.222.222", "208.67.220.220", "rede pública da Cisco"),
        new("CleanBrowsing", "185.228.168.9", "185.228.169.9", "filtro de conteúdo"),
        new("Control D", "76.76.2.0", "76.76.10.0", "resolvedor público com opções de filtragem"),
        new("DNS0", "193.110.81.0", "185.253.5.0", "privacidade e bloqueio de domínios maliciosos"),
        new("DNS.WATCH", "84.200.69.80", "84.200.70.40", "resolvedor público sem filtragem"),
    };

    public static async Task<DnsResult> TestAsync(DnsProvider provider, CancellationToken ct, string host = "steampowered.com", int tries = 3)
    {
        var times = new List<double>();
        string? error = null;
        for (int i = 0; i < tries; i++)
        {
            try { times.Add(await QueryAsync(provider.Primary, host, ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { error ??= ex.Message; }
        }
        return new DnsResult(provider.Name, $"{provider.Primary} / {provider.Secondary}",
            times.Count > 0 ? times.Average() : null, times.Count > 0 ? times.Min() : null, times.Count > 0, error);
    }

    /// <summary>Consulta A por UDP: devolve o tempo até à resposta válida.</summary>
    public static async Task<double> QueryAsync(string server, string host, CancellationToken ct)
    {
        byte[] query = BuildQuery(host, out ushort id);
        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 1500;
        var target = new IPEndPoint(IPAddress.Parse(server), 53);

        var clock = Stopwatch.StartNew();
        await udp.SendAsync(query, query.Length, target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
        var response = await udp.ReceiveAsync(timeout.Token);
        clock.Stop();

        byte[] data = response.Buffer;
        if (data.Length < 12) throw new InvalidOperationException("resposta inválida");
        if ((ushort)((data[0] << 8) | data[1]) != id) throw new InvalidOperationException("resposta de outra consulta");
        int answers = (data[6] << 8) | data[7];
        if (answers <= 0) throw new InvalidOperationException("sem resposta para o domínio");
        return clock.Elapsed.TotalMilliseconds;
    }

    private static byte[] BuildQuery(string host, out ushort id)
    {
        id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id);
        ms.WriteByte(0x01); ms.WriteByte(0x00);          // consulta padrão, recursão pedida
        ms.WriteByte(0x00); ms.WriteByte(0x01);          // 1 pergunta
        for (int i = 0; i < 6; i++) ms.WriteByte(0x00);  // 0 respostas/autoridades/extra

        foreach (string label in host.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("nome de domínio inválido", nameof(host));
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0x00);                              // fim do nome
        ms.WriteByte(0x00); ms.WriteByte(0x01);          // tipo A
        ms.WriteByte(0x00); ms.WriteByte(0x01);          // classe IN
        return ms.ToArray();
    }
}
