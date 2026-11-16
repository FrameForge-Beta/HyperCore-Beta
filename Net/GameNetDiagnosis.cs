using System.Globalization;

namespace FrameForge.Net;

/// <summary>
/// Veredicto em linguagem simples. Level: -1 = ainda a medir, 0 = bom, 1 = atenção, 2 = mau.
/// Where: "ok", "wifi", "home", "isp", "outside", "bloat", "distance", "down", "wait".
/// </summary>
public sealed record NetVerdict(int Level, string Title, string Detail, string Where);

/// <summary>Traduz os números (ping, jitter, perda, router, bufferbloat) para "de onde vem o lag".</summary>
public static class GameNetDiagnosis
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    /// <summary>Qualidade do ping para jogar: palavra + nível (0 bom, 1 atenção, 2 mau).</summary>
    public static (string Word, int Level) PingQuality(double? ms) => ms switch
    {
        null => ("—", -1),
        <= 30 => ("Excelente", 0),
        <= 60 => ("Bom", 0),
        <= 100 => ("Razoável", 1),
        _ => ("Alto", 2),
    };

    public static int JitterLevel(double j) => j >= 20 ? 2 : j >= 8 ? 1 : 0;
    public static int LossLevel(double p) => p >= 3 ? 2 : p >= 1 ? 1 : 0;

    /// <summary>Troço PC → router (Wi-Fi ou cabo). -1 = o router não responde a ping.</summary>
    public static int LocalLevel(NetStats s)
    {
        if (!s.RouterKnown) return -1;
        double avg = s.RouterAvg ?? 0;
        if (s.RouterLossPct >= 3 || s.RouterJitter >= 15 || avg >= 30) return 2;
        if (s.RouterLossPct >= 1 || s.RouterJitter >= 5 || avg >= 10) return 1;
        return 0;
    }

    /// <summary>Troço router → servidor (fornecedor e Internet). Desconta o que já se perde em casa.</summary>
    public static int RemoteLevel(NetStats s)
    {
        if (s.Count == 0) return -1;
        double loss = s.RouterKnown ? Math.Max(0, s.LossPct - s.RouterLossPct) : s.LossPct;
        double jitter = s.RouterKnown ? Math.Max(0, s.Jitter - s.RouterJitter) : s.Jitter;
        if (loss >= 3 || jitter >= 20) return 2;
        if (loss >= 1 || jitter >= 8) return 1;
        return 0;
    }

    public static NetVerdict Evaluate(NetStats s, ActiveLink? link, GameNetTarget target, BloatResult? bloat)
    {
        if (s.Count < 16)
            return new(-1, "A medir…", "Dá-me uns segundos para ter números fiáveis.", "wait");
        if (s.LossPct >= 95)
            return new(2, "Sem resposta", $"{target.Short} não está a responder. Confirma que tens Internet ou experimenta outro servidor.", "down");

        bool wifi = link?.IsWifi == true;
        int local = LocalLevel(s);
        int remote = RemoteLevel(s);

        if (local == 2 || (local == 1 && remote >= 1))
        {
            string problem = Problem(s.RouterLossPct, s.RouterJitter, s.RouterAvg, home: true);
            return wifi
                ? new(local, "O lag vem do Wi-Fi",
                    $"Entre o PC e o router já há {problem}. Aproxima o PC do router, usa a rede de 5 GHz ou, melhor ainda, liga um cabo de rede.", "wifi")
                : new(local, "O lag vem da tua rede de casa",
                    $"Entre o PC e o router já há {problem}. Experimenta outro cabo ou outra porta do router e reinicia o router.", "home");
        }

        if (remote == 2)
        {
            double loss = s.RouterKnown ? Math.Max(0, s.LossPct - s.RouterLossPct) : s.LossPct;
            double jit = s.RouterKnown ? Math.Max(0, s.Jitter - s.RouterJitter) : s.Jitter;
            string problem = Problem(loss, jit, null, home: false);
            return s.RouterKnown
                ? new(2, "O lag vem do fornecedor de Internet",
                    $"Em casa está tudo limpo até ao router, mas daí para fora há {problem}. Reinicia o router; se continuar, contacta o fornecedor e mostra-lhe este resultado.", "isp")
                : new(2, "O lag vem de fora do PC",
                    $"Há {problem}. O teu router não responde a ping, por isso não dá para separar o router do fornecedor: reinicia o router e, se continuar, contacta o fornecedor.", "outside");
        }

        if (bloat != null && bloat.Grade is "C" or "D" or "F")
            return new(1, "Bom em repouso, mas o ping sobe com downloads",
                $"O teste de bufferbloat deu {bloat.Grade}: quando alguém descarrega ou vê vídeos, o router deixa a fila encher e o ping sobe {F(bloat.ExtraMs)} ms. " +
                "Ativa o QoS/SQM no router ou pausa os downloads enquanto jogas.", "bloat");

        if (remote == 1)
            return new(1, "Pequenas oscilações na Internet",
                $"Jitter de {F(s.Jitter)} ms e {Loss(s.LossPct)}. Normalmente não se nota; se notares, reinicia o router e evita downloads enquanto jogas.", "isp");

        if (s.Avg >= 100)
            return new(1, "Ping alto, mas estável",
                $"Não há perdas nem oscilações: é sobretudo a distância até {target.Short}. No jogo, escolhe uma região ou servidor mais perto.", "distance");

        if (local == 1 && wifi)
            return new(1, "Bom, mas o Wi-Fi oscila um pouco",
                $"Até ao router o ping varia {F(s.RouterJitter)} ms. Dá para jogar; um cabo de rede deixaria tudo mais estável.", "wifi");

        return new(0, "Ligação boa para jogar",
            $"Ping de {F(s.Avg)} ms, jitter de {F(s.Jitter)} ms e {Loss(s.LossPct)}." + (local < 0 ? " (O teu router não responde a ping, por isso só vejo o caminho completo.)" : ""), "ok");
    }

    private static string Problem(double loss, double jitter, double? avg, bool home)
    {
        var parts = new List<string>();
        if (loss >= 1) parts.Add($"{F1(loss)}% de pacotes perdidos");
        if (jitter >= (home ? 5 : 8)) parts.Add($"o ping a oscilar {F(jitter)} ms");
        if (home && avg >= 10) parts.Add($"{F(avg)} ms só até ao router");
        return parts.Count switch
        {
            0 => "pequenas falhas",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " e " + parts[^1],
        };
    }

    public static string Loss(double pct) => pct < 0.05 ? "sem perdas" : $"{F1(pct)}% de perdas";
    public static string F(double? v) => v is double d ? d.ToString(d < 10 ? "0.#" : "0", Pt) : "—";
    public static string F1(double v) => v.ToString("0.#", Pt);
}
