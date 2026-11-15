namespace FrameForge.Net;

/// <summary>Ponto de entrada do módulo "Rede para jogos".</summary>
public static class GameNet
{
    /// <summary>
    /// Chamar uma vez no arranque da app (antes de a Central Desfazer poder ser aberta): regista o handler de desfazer
    /// das mudanças de DNS (kind "dns"). Idempotente; a página também o chama.
    /// </summary>
    public static void Init() => GameNetDns.Init();
}
