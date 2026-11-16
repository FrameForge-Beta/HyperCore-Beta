using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using FrameForge.Core;
using Microsoft.Win32;
using ResetExplode;

namespace FrameForge.Net;

/// <summary>Um servidor DNS candidato. IsCurrent = é o que o Windows está a usar agora.</summary>
public sealed record DnsCandidate(string Name, string Primary, string Secondary, string Note, bool IsCurrent, bool IsProvider);

/// <summary>Resultado da medição de um servidor DNS (mediana das respostas; falhas = sem resposta em 1,5 s).</summary>
public sealed record DnsScore(DnsCandidate Server, double? MedianMs, int Failures, int Total)
{
    public bool Usable => MedianMs != null && Failures * 4 <= Total;
}

/// <summary>Como estava o DNS IPv4 de um adaptador antes de o HyperCore o mudar.</summary>
public sealed class DnsAdapterState
{
    /// <summary>GUID do adaptador ({...}); o índice pode mudar entre arranques.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>true = tinha servidores escritos à mão; false = automático (DHCP, normalmente o router).</summary>
    public bool WasStatic { get; set; }
    public List<string> Servers { get; set; } = new();

    public string Describe() => WasStatic && Servers.Count > 0 ? string.Join(", ", Servers) : "automático";
}

/// <summary>O que fica guardado no ChangeJournal (kind "dns") para poder repor.</summary>
public sealed class DnsChangeData
{
    public string Provider { get; set; } = "";
    public List<DnsAdapterState> Adapters { get; set; } = new();
}

/// <summary>
/// DNS mais rápido: mede servidores públicos e o atual; muda o DNS IPv4 dos adaptadores ativos só quando o utilizador confirma,
/// guardando o estado anterior no <see cref="ChangeJournal"/> (kind "dns") para a Central Desfazer poder repor.
/// </summary>
public static class GameNetDns
{
    public const string Kind = "dns";

    public static readonly DnsCandidate[] Providers =
    {
        new("Cloudflare", "1.1.1.1", "1.0.0.1", "Rápido e com foco na privacidade.", false, true),
        new("Google", "8.8.8.8", "8.8.4.4", "Muito estável, em todo o lado.", false, true),
        new("Quad9", "9.9.9.9", "149.112.112.112", "Bloqueia sites maliciosos.", false, true),
        new("OpenDNS", "208.67.222.222", "208.67.220.220", "Rede pública da Cisco.", false, true),
        new("Control D", "76.76.2.0", "76.76.10.0", "Público, sem filtros.", false, true),
        new("AdGuard", "94.140.14.14", "94.140.15.15", "Bloqueia publicidade (pode partir alguns sites e lojas).", false, true),
    };

    // Domínios de lojas e jogos: é o que um gamer resolve mais vezes
    private static readonly string[] Domains =
    {
        "steampowered.com", "epicgames.com", "riotgames.com", "ea.com", "battle.net", "xbox.com", "playstation.com", "ubisoft.com",
    };

    private static bool _init;
    private static readonly object InitGate = new();

    /// <summary>Regista quem sabe desfazer as mudanças de DNS. Chamar no arranque da app (idempotente).</summary>
    public static void Init()
    {
        lock (InitGate)
        {
            if (_init) return;
            _init = true;
        }
        ChangeJournal.RegisterHandler(Kind, e =>
        {
            DnsChangeData? data = null;
            try { data = JsonSerializer.Deserialize<DnsChangeData>(e.Data); } catch { }
            if (data == null || data.Adapters.Count == 0) return (true, "Não havia nada guardado para repor; a entrada foi limpa.");
            return Restore(data);
        });
    }

    // ------------------------------------------------------------------ leitura

    /// <summary>Servidores DNS IPv4 que o adaptador ativo está a usar (efetivos, sejam automáticos ou manuais).</summary>
    public static List<string> CurrentServers(out string adapterName, out bool isStatic, out IPAddress? gateway)
    {
        adapterName = "—"; isStatic = false; gateway = null;
        try
        {
            var nic = GameNetProbe.ActiveInterfaces().OrderByDescending(n => n.Speed).FirstOrDefault();
            if (nic == null) return new();
            adapterName = nic.Name;
            var props = nic.GetIPProperties();
            gateway = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            isStatic = ReadState(nic).WasStatic;
            return props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).Distinct().ToList();
        }
        catch { return new(); }
    }

    /// <summary>Lista de candidatos: os públicos + o atual (se não for um deles).</summary>
    public static List<DnsCandidate> Candidates()
    {
        var current = CurrentServers(out _, out _, out var gw);
        var list = Providers.Select(p => p with { IsCurrent = current.Count > 0 && current[0] == p.Primary }).ToList();
        if (current.Count > 0 && !list.Any(c => c.IsCurrent))
        {
            string first = current[0];
            bool router = gw != null && first == gw.ToString();
            bool local = first.StartsWith("127.", StringComparison.Ordinal);
            string name = router ? "O teu router" : local ? "Resolvedor local" : "O teu DNS atual";
            string note = router ? "Normalmente reencaminha para o DNS do fornecedor." : local ? "Um programa no PC trata do DNS." : "O que o Windows usa agora.";
            list.Insert(0, new DnsCandidate(name, first, current.Count > 1 ? current[1] : "", note, true, false));
        }
        return list;
    }

    public static DnsAdapterState ReadState(NetworkInterface nic)
    {
        var state = new DnsAdapterState { Id = nic.Id, Name = nic.Name };
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nic.Id}");
            string raw = key?.GetValue("NameServer") as string ?? "";
            var servers = raw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork).ToList();
            state.WasStatic = servers.Count > 0;
            state.Servers = servers;
        }
        catch { }
        return state;
    }

    // ------------------------------------------------------------------ medição (só leitura)

    /// <summary>Mede cada candidato com 16 consultas (8 domínios de jogos × 2). Corre em paralelo, fora do UI.</summary>
    public static async Task<List<DnsScore>> BenchmarkAsync(IReadOnlyList<DnsCandidate> candidates, IProgress<DnsScore>? progress, CancellationToken ct)
    {
        var tasks = candidates.Select(async (c, i) =>
        {
            await Task.Delay(i * 40, ct).ConfigureAwait(false); // não disparar tudo no mesmo milissegundo
            var times = new List<double>();
            int failures = 0, total = 0;
            for (int round = 0; round < 2; round++)
                foreach (var d in Domains)
                {
                    ct.ThrowIfCancellationRequested();
                    total++;
                    try { times.Add(await DnsBenchmark.QueryAsync(c.Primary, d, ct).ConfigureAwait(false)); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { failures++; }
                }
            var score = new DnsScore(c, times.Count > 0 ? NetStats.MedianOf(times) : null, failures, total);
            progress?.Report(score);
            return score;
        });
        var all = await Task.WhenAll(tasks).ConfigureAwait(false);
        return all.OrderBy(s => s.Usable ? 0 : 1).ThenBy(s => s.MedianMs ?? double.MaxValue).ToList();
    }

    /// <summary>Sugestão: o mais rápido, se valer a pena mudar (≥ 5 ms e ≥ 15% mais rápido que o atual). null = fica como está.</summary>
    public static DnsScore? Suggest(IReadOnlyList<DnsScore> scores)
    {
        var best = scores.Where(s => s.Usable && s.Server.IsProvider).OrderBy(s => s.MedianMs).FirstOrDefault();
        if (best == null) return null;
        var current = scores.FirstOrDefault(s => s.Server.IsCurrent);
        if (current == null || !current.Usable) return best;
        if (current.Server.Primary == best.Server.Primary) return null;
        double gain = current.MedianMs!.Value - best.MedianMs!.Value;
        return gain >= 5 && gain >= current.MedianMs.Value * 0.15 ? best : null;
    }

    // ------------------------------------------------------------------ alterações (com registo para desfazer)

    /// <summary>A alteração de DNS do HyperCore ainda ativa (a mais recente), ou null.</summary>
    public static ChangeEntry? ActiveChange() =>
        ChangeJournal.All().FirstOrDefault(e => string.Equals(e.Kind, Kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Muda o DNS IPv4 dos adaptadores ativos para o fornecedor. Guarda antes o estado original (se já havia uma mudança do
    /// HyperCore, mantém o original dessa, para "Repor" voltar sempre ao DNS do utilizador). Bloqueia: chamar fora do UI.
    /// </summary>
    public static (bool ok, string msg) Apply(DnsCandidate provider)
    {
        Init();
        var nics = GameNetProbe.ActiveInterfaces().ToList();
        if (nics.Count == 0) return (false, "Não encontrei nenhum adaptador de rede ligado à Internet.");

        // Estado original: o da primeira mudança do HyperCore ainda ativa, senão o atual
        var existing = ChangeJournal.All().Where(e => string.Equals(e.Kind, Kind, StringComparison.OrdinalIgnoreCase)).Reverse().ToList();
        var original = new Dictionary<string, DnsAdapterState>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in existing)
        {
            DnsChangeData? d = null;
            try { d = JsonSerializer.Deserialize<DnsChangeData>(e.Data); } catch { }
            foreach (var a in d?.Adapters ?? new()) original.TryAdd(a.Id, a);
        }
        foreach (var nic in nics) original.TryAdd(nic.Id, ReadState(nic));

        var done = new List<NetworkInterface>();
        foreach (var nic in nics)
        {
            int index = IndexOf(nic);
            bool ok = index > 0
                      && Netsh($"interface ipv4 set dnsservers name={index} source=static address={provider.Primary} register=primary validate=no")
                      && (string.IsNullOrEmpty(provider.Secondary)
                          || Netsh($"interface ipv4 add dnsservers name={index} address={provider.Secondary} index=2 validate=no"));
            if (ok) ok = ReadState(nic).Servers.FirstOrDefault() == provider.Primary;
            if (!ok)
            {
                // Volta atrás no que já tinha mudado, para não deixar o PC a meio
                foreach (var n in done.Append(nic)) RestoreOne(original[n.Id], out _);
                Flush();
                return (false, $"O Windows não aceitou a mudança no adaptador «{nic.Name}». Nada ficou alterado.");
            }
            done.Add(nic);
        }
        Flush();

        var data = new DnsChangeData { Provider = provider.Name, Adapters = original.Values.ToList() };
        foreach (var e in existing) ChangeJournal.Remove(e.Id);
        string detail = string.Join(" · ", nics.Select(n => $"{n.Name}: {original[n.Id].Describe()} → {provider.Primary}" +
                                                          (string.IsNullOrEmpty(provider.Secondary) ? "" : $", {provider.Secondary}")));
        ChangeJournal.Record(Kind, $"DNS mudado para {provider.Name}", detail, JsonSerializer.Serialize(data));
        return (true, $"DNS mudado para {provider.Name} em {string.Join(", ", nics.Select(n => "«" + n.Name + "»"))}.");
    }

    /// <summary>Repõe o DNS de cada adaptador como estava (também os desligados). Adaptadores removidos do PC são ignorados.</summary>
    public static (bool ok, string msg) Restore(DnsChangeData data)
    {
        var failed = new List<string>();
        var restored = new List<string>();
        foreach (var a in data.Adapters)
        {
            if (RestoreOne(a, out bool missing)) { if (!missing) restored.Add(a.Name); }
            else failed.Add(a.Name);
        }
        Flush();
        if (failed.Count > 0) return (false, $"Não consegui repor o DNS em {string.Join(", ", failed)}.");
        return (true, restored.Count > 0
            ? $"DNS reposto como estava ({string.Join(", ", data.Adapters.Where(a => restored.Contains(a.Name)).Select(a => a.Name + ": " + a.Describe()))})."
            : "Os adaptadores alterados já não existem; nada para repor.");
    }

    private static bool RestoreOne(DnsAdapterState a, out bool missing)
    {
        missing = false;
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Id, a.Id, StringComparison.OrdinalIgnoreCase));
        if (nic == null)
        {
            // Desligado ou fora da dock: não aparece na lista, mas a chave do TCP/IP continua lá. Escreve-se o valor
            // diretamente (vazio = automático) e o Windows usa-o quando o adaptador voltar. Sem chave = foi mesmo removido.
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{a.Id}", writable: true);
                if (key == null) { missing = true; return true; }
                key.SetValue("NameServer", a.WasStatic ? string.Join(",", a.Servers) : "", RegistryValueKind.String);
                return true;
            }
            catch { return false; }
        }
        int index = IndexOf(nic);
        if (index <= 0) return false;
        if (!a.WasStatic || a.Servers.Count == 0)
            return Netsh($"interface ipv4 set dnsservers name={index} source=dhcp") && !ReadState(nic).WasStatic;

        bool ok = Netsh($"interface ipv4 set dnsservers name={index} source=static address={a.Servers[0]} register=primary validate=no");
        for (int i = 1; ok && i < a.Servers.Count; i++)
            ok = Netsh($"interface ipv4 add dnsservers name={index} address={a.Servers[i]} index={i + 1} validate=no");
        return ok && ReadState(nic).Servers.FirstOrDefault() == a.Servers[0];
    }

    private static int IndexOf(NetworkInterface nic)
    {
        try { return nic.GetIPProperties().GetIPv4Properties()?.Index ?? 0; }
        catch { return 0; }
    }

    private static bool Netsh(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern bool DnsFlushResolverCache();

    private static void Flush()
    {
        try { DnsFlushResolverCache(); } catch { }
    }
}
