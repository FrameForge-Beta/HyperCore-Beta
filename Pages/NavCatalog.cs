namespace FrameForge.Pages;

/// <summary>Uma aba de uma secção. <c>Tag</c> é o tag de sempre: "FF:xxx" (página em Pages/) ou "PageXxx" (painel do MainWindow.xaml).</summary>
/// <param name="Desc">Uma linha curta, mostrada por baixo do título da secção.</param>
/// <param name="Help">Explicação longa (tooltip/painel do botão ?). Pode ficar vazia.</param>
/// <param name="Keys">Sinónimos para a pesquisa rápida (Ctrl+K), separados por espaço.</param>
/// <param name="Advanced">Aba para utilizadores experientes: aparece no fim, depois do rótulo "Avançado".</param>
public sealed record NavTab(string Tag, string Label, string Icon, string Desc, string Help = "", string Keys = "", bool Advanced = false);

/// <summary>Uma entrada do menu lateral. Abre a última aba usada (ou a primeira).</summary>
/// <param name="Group">Cabeçalho do grupo no menu ("" = sem cabeçalho; <see cref="NavCatalog.Footer"/> = rodapé do menu).</param>
public sealed record NavSection(string Id, string Group, string Label, string Icon, string Desc, NavTab[] Tabs, string Keys = "");

/// <summary>
/// O menu do HyperCore: 14 secções, cada uma com as suas abas. É a ÚNICA lista de páginas — o menu lateral, os cabeçalhos,
/// o botão Voltar, os tags antigos (NavigateTo / AppServices.Navigate) e a pesquisa Ctrl+K saem todos daqui.
/// Para ligar uma página nova: acrescenta um NavTab aqui e (se for "FF:chave") um caso em MainWindow.ForgePage.
/// </summary>
public static class NavCatalog
{
    public const string Footer = "rodapé";

    public static readonly NavSection[] Sections =
    {
        new("S:home", "", "Início", "", "O estado do teu PC e atalhos para o que mais usas.", new NavTab[]
        {
            new("FF:home", "Resumo", "", "A nota do teu PC, o que melhorar e o estado ao vivo.",
                "Carrega em ANALISAR O PC: em poucos segundos a app lê o PC (não muda nada) e dá a Nota HyperCore de 0 a 100 com a lista do que melhorar. Cada ponto leva-te à página que o resolve. Por baixo vês o processador, a gráfica, a RAM e as temperaturas ao vivo, o Modo Jogo e o último jogo medido.",
                "inicio painel principal nota analisar analise rapida pontuacao score estado"),
        }, "inicio home"),

        // ---------------- JOGAR ----------------
        new("S:games", "JOGAR", "Jogos", "", "Os teus jogos, sessões gravadas e FPS medidos.", new NavTab[]
        {
            new("FF:games", "Biblioteca", "", "Escolhe um jogo, define o perfil e carrega em JOGAR.",
                "Os teus jogos encontrados automaticamente. Escolhe um, define o perfil (otimização/overclock só durante o jogo) e carrega em JOGAR. Quando fechares o jogo, aparece o relatório de quebras. Seguro com anti-cheat.",
                "jogar steam epic launcher perfil fps"),
            new("PageLauncher", "Clássica", "", "O launcher original, com opções de arranque da Steam.",
                "O launcher original, com menu de gráficos e gravação das opções de arranque da Steam. Para relatórios de quebras usa a Biblioteca.",
                "launcher opcoes arranque steam resolucao"),
            new("FF:sessions", "Sessões e relatórios", "", "Cada sessão gravada com nota, FPS, 1% low e causa das quebras.",
                "Cada sessão gravada com nota de A a E, FPS, 1% low e a causa de cada engasgo. Também podes gravar um jogo já aberto aqui ou com Ctrl+Alt+R dentro do jogo.",
                "gravar relatorio stutter engasgos quebras frametime 1% low"),
            new("PageGames", "Os meus resultados", "", "FPS realmente medidos nos jogos que já gravaste.",
                "FPS médio e 1% low realmente medidos nos jogos que já gravaste, com nota, quebras e data. Não são estimativas: vêm das tuas sessões.",
                "resultados fps medidos historico"),
        }, "jogos games"),

        new("S:overlay", "JOGAR", "Overlay", "", "FPS e sensores por cima do jogo.", new NavTab[]
        {
            new("FF:overlay", "Editor", "", "Monta o que aparece por cima do jogo e escolhe o atalho.",
                "Monta o que aparece por cima do jogo: arrasta os elementos, escolhe sensores, cores, tamanhos, gráficos e alertas, e define a combinação de teclas que mostra/esconde o overlay. Precisa do jogo em 'janela sem bordas'.",
                "osd hud contador fps afterburner rivatuner"),
            new("PageOverlay", "Simples", "", "O overlay original, com uma lista simples de itens.",
                "O overlay original (lista simples de itens). Para escolher qualquer sensor, posição e cor, usa o Editor.", "osd simples"),
            new("PageBottleneck", "Gargalo ao vivo", "", "Que peça está a limitar o FPS enquanto jogas.",
                "Diz em tempo real que peça está a limitar o FPS enquanto jogas (põe o jogo em janela para veres os dois).",
                "gargalo bottleneck limite cpu gpu"),
        }, "overlay osd hud"),

        new("S:net", "JOGAR", "Rede para jogos", "", "Ping, jitter e perda de pacotes para jogar online.", new NavTab[]
        {
            new("FF:netgame", "Ping e lag", "", "Ping, jitter e perda de pacotes ao vivo, e de onde vem o lag.",
                "Mede continuamente o ping, o jitter e a perda de pacotes contra servidores públicos, testa o bufferbloat e diz em linguagem simples se o lag vem do Wi-Fi, do router ou do fornecedor.",
                "ping lag latencia jitter perda de pacotes bufferbloat dns servidor online wifi router fornecedor isp"),
            new("PageNet", "Rede", "", "Teste de 30 s: velocidade, latência e qualidade da ligação.",
                "Teste de ~30 s: ping, jitter, perda de pacotes, velocidade e bufferbloat. Diz se o problema é a tua rede (Wi-Fi/router) ou o operador. Inclui o seletor de DNS.",
                "internet velocidade speedtest dns wifi router"),
        }, "rede internet ping lag"),

        new("S:gamemode", "JOGAR", "Modo Jogo", "", "Um clique antes de jogar: fecha o que pesa e repõe tudo no fim.", new NavTab[]
        {
            new("FF:gamemode", "Modo Jogo", "", "Um clique antes de jogar: fecha o que pesa e repõe tudo no fim.",
                "Antes de jogar fecha os programas pesados de fundo, liberta memória e aplica o nível e o plano de energia escolhidos. Ao desligar repõe tudo como estava.",
                "modo jogo boost turbo fechar programas fundo ram ctrl alt g atalho bandeja onedrive dropbox launchers windows update pausar"),
        }, "modo jogo boost"),

        // ---------------- DESEMPENHO ----------------
        new("S:scan", "DESEMPENHO", "Scan e afinação", "", "Mede o PC a fundo e afina a gráfica sozinho.", new NavTab[]
        {
            new("FF:scan", "Scan", "", "Mede CPU, gráfica, RAM e discos e diz-te o que vale a pena mudar.",
                "Mede CPU, gráfica, RAM e discos, encontra sozinho o overclock estável da gráfica e diz-te o que corrigir ou comprar.",
                "scan analise afinacao automatica upgrade comprar"),
            new("PageOc", "Teste de estabilidade", "", "Confirma se um overclock é estável e mostra o guia da BIOS.",
                "Confirma se um overclock é estável (CPU, GPU ou ambos) e mostra o guia da BIOS para a tua placa-mãe e processador.",
                "estabilidade bios oc estavel crash"),
        }, "scan afinar analisar"),

        new("S:levels", "DESEMPENHO", "Níveis de desempenho", "", "Afina o Windows para jogos, por níveis. Tudo volta atrás.", new NavTab[]
        {
            new("FF:optimize", "Níveis", "", "Escolhe um nível e carrega em Aplicar. Tudo volta atrás com um clique.",
                "Escolhe um nível e carrega em Aplicar; fica guardado e nada mexe na segurança do Windows.",
                "otimizar otimizacao tweaks ajustes windows fps desempenho"),
            new("FF:priorities", "Prioridade dos programas", "", "Que programas passam à frente na fila do processador.",
                "Escolhe que programas passam à frente na fila do processador — como no Gestor de Tarefas → Detalhes → Definir prioridade, mas fica gravado: o Windows aplica sozinho sempre que o programa abre, mesmo depois de reiniciar.",
                "prioridade cpu processos afinidade"),
            new("FF:gamersetup", "Configuração gamer", "", "Hz do monitor, VRR, HAGS, Modo de Jogo, MPO e cache de shaders.",
                "Para cada monitor, Hz atual vs máximo; estado de G-Sync/FreeSync/VRR, HAGS, Modo de Jogo do Windows, Game DVR, MPO e otimizações para jogos em janela; limpeza da cache de shaders. Cada item com estado, recomendação e correção reversível.",
                "monitor hz refresh 144 165 240 vrr gsync g-sync freesync hags agendamento gpu mpo shaders cache game dvr captura modo de jogo do windows ecra"),
        }, "niveis otimizar"),

        new("S:oc", "DESEMPENHO", "Overclock", "", "Overclock da gráfica e curvas das ventoinhas.", new NavTab[]
        {
            new("FF:oc", "Gráfica", "", "Clocks, temperatura, consumo e perfis de overclock da gráfica.",
                "Estado da placa em tempo real (clocks, temperatura, consumo e ventoinha) em qualquer marca. A escrita de offsets, limite de energia e ventoinha é só para NVIDIA; em AMD/Intel usa-se o painel do fabricante. Nada fica gravado na placa.",
                "oc overclock gpu nvidia clocks memoria power limit"),
            new("FF:fans", "Ventoinhas", "", "Curvas das ventoinhas a partir de qualquer temperatura.",
                "1) Escolhe o que medir (uma temperatura ou a média de várias), 2) desenha a curva, 3) diz que ventoinhas a seguem. Tudo volta ao automático quando desligas ou fechas a app.",
                "ventoinha ventoinhas fan fans rpm curva barulho ruido"),
        }, "overclock oc"),

        new("S:clean", "DESEMPENHO", "Limpeza e arranque", "", "Liberta memória, limpa ficheiros e acelera o arranque.", new NavTab[]
        {
            new("PageOptimize", "Limpeza rápida", "", "Liberta memória e fecha programas de fundo antes de jogar.",
                "Liberta memória e fecha programas de fundo antes de jogar. Não mexe em jogos abertos.", "limpar ram memoria fechar programas"),
            new("FF:maintenance", "Manutenção", "", "Ficheiros temporários, limpeza do Windows, desfragmentação de HDDs e mapa do disco.",
                "Limpa ficheiros temporários antigos, abre a ferramenta nativa de limpeza do Windows, desfragmenta apenas HDDs e mostra um mapa visual do que ocupa espaço no disco. SSDs nunca são desfragmentados.",
                "limpar temporarios espaco disco desfragmentar lixo mapa do disco"),
            new("PageStartup", "Arranque do Windows", "", "Programas que abrem sozinhos com o Windows.",
                "Programas que abrem sozinhos com o Windows. Desligar não desinstala nada e pode ser desfeito.", "arranque inicializacao startup boot lento"),
            new("FF:mapadisco", "Mapa do Disco", "", "Mapa visual do que ocupa espaço no disco com blocos (treemap).",
                "Veja visualmente o que ocupa espaço no disco com um mapa de blocos (treemap). Cada bloco é uma pasta ou arquivo. Só lê: não apaga nada.", "mapa do disco treemap espaco ficheiros pastas"),
        }, "limpeza limpar arranque"),

        // ---------------- O MEU PC ----------------
        new("S:pc", "O MEU PC", "O meu PC", "", "Tudo sobre o teu PC: diagnóstico, peças, sensores e energia.", new NavTab[]
        {
            new("FF:diag", "Diagnóstico", "", "Aponta o que está a custar desempenho. Só lê, não muda nada.",
                "Lê o PC e aponta o que está a custar desempenho: RAM misturada ou sem XMP, monitor abaixo dos Hz máximos, driver antigo, programas em conflito. Só lê — as correções são tuas.",
                "diagnostico problemas xmp ram"),
            new("PageDash", "Visão geral", "", "Temperaturas, carga e saúde do PC em tempo real.",
                "Resumo do PC em tempo real: gráfico de temperatura/carga da CPU e da GPU, componentes, memória e uma lista de saúde do PC.",
                "resumo dashboard"),
            new("PageSystem", "Sistema", "", "Ficha técnica completa do PC, com exportação do relatório.",
                "Ficha técnica completa do PC. Útil para pedir ajuda ou vender o PC (podes exportar o relatório).", "especificacoes ficha tecnica hardware relatorio"),
            new("PageSensors", "Sensores", "", "Todas as leituras em tempo real: temperaturas, clocks e consumos.",
                "Todas as leituras do PC em tempo real (temperaturas, clocks, consumos, ventoinhas). Procura ou filtra por tipo.",
                "temperatura temperaturas graus sensores clocks consumo watts"),
            new("PageGpu", "Gráfica", "", "Tudo sobre a placa gráfica: chip, memória, PCIe e clocks.",
                "Tudo sobre a gráfica, como no GPU-Z: chip, memória, PCIe, clocks, limites de energia e sensores.", "placa grafica video gpu-z vram"),
            new("PageDisks", "Discos", "", "Saúde, desgaste e temperatura de cada SSD e HDD.",
                "Saúde de cada SSD/HDD (S.M.A.R.T.). Se aparecer 'Atenção' ou 'Ruim', faz cópia de segurança dos teus ficheiros.", "ssd hdd smart saude disco nvme"),
            new("PageEnergy", "Energia", "", "Bateria, consumo e planos de energia do Windows.",
                "Bateria e consumo da CPU/GPU. Cria cópias de planos do Windows, ajusta opções de CPU/ecrã/suspensão e ativa ou remove planos personalizados.",
                "energia bateria plano consumo portatil"),
        }, "meu pc computador hardware"),

        new("S:tests", "O MEU PC", "Testes", "", "Benchmark, stress test e teste de periféricos.", new NavTab[]
        {
            new("PageBench", "Benchmark", "", "Dá uma nota à CPU, GPU, memória e disco para comparar.",
                "Testes que dão uma nota à CPU, GPU, ray tracing, memória e disco. Bom para comparar antes/depois de um ajuste. Põe o PC a trabalhar a fundo durante o teste.",
                "benchmark pontuacao nota cinebench comparar"),
            new("PageStress", "Stress test", "", "CPU, RAM e GPU a 100% com corte automático por temperatura.",
                "Põe CPU/RAM/GPU a 100% para ver temperaturas máximas e estabilidade. Pára sozinho se passar do limite de temperatura que escolheres.",
                "stress furmark prime carga maxima estabilidade"),
            new("FF:peripherals", "Periféricos", "", "Rato, teclado, ecrã, comando, fones e câmara.",
                "Taxa de polling do rato, duplo clique avariado, botões e roda, teclas ao mesmo tempo (ghosting), píxeis mortos, rasto de movimento, refresh real do ecrã, comando de videojogo (botões, analógicos, drift e vibração), fones estéreo (esquerda, direita, ambos e frequências) e teste de câmara/webcam.",
                "rato teclado ecra comando controle gamepad joystick xbox playstation drift vibracao fones auscultadores headset audio som esquerda direita stereo estereo balanco camara camera webcam video polling hz duplo clique pixeis mortos presos recuperar jscreenfix ghosting mouse botoes roda rasto refresh real"),
        }, "testes testar"),

        new("S:drivers", "O MEU PC", "Drivers e RGB", "", "Drivers atualizados e as luzes do PC num só sítio.", new NavTab[]
        {
            new("PageDriver", "Drivers", "", "Verifica, transfere e instala drivers; cópia antes de formatar.",
                "Lista os drivers, verifica se o da NVIDIA está atualizado e faz cópia de segurança antes de formatar.", "drivers nvidia amd atualizar"),
            new("PageInteg", "Iluminação RGB", "", "Controla as luzes do PC num só sítio (iCUE, OpenRGB, Razer).",
                "Controla as luzes do PC num só sítio (Corsair iCUE, OpenRGB para MSI/ASUS/Gigabyte, Razer). Em teste.", "rgb luzes leds icue openrgb razer chroma"),
            new("PageMakerPorts", "Portas COM", "", "Arduino/ESP32: limpa portas COM fantasma e números presos.",
                "Para Arduino/ESP32: limpa portas COM fantasma e números COM presos quando a placa não aparece.", "arduino esp32 com serial porta", Advanced: true),
            new("PageMakerDrv", "Drivers seriais", "", "CH340, CP210x, FTDI, Prolific e Arduino, sem internet.",
                "Diagnóstico e instalação dos drivers CH340, CP210x, FTDI, Prolific e Arduino (incluídos na app, funcionam sem internet).",
                "ch340 cp210x ftdi prolific serial", Advanced: true),
        }, "drivers rgb luzes"),

        new("S:apps", "O MEU PC", "Aplicações", "", "Atualiza, instala em lote e desinstala programas com limpeza de resíduos.", new NavTab[]
        {
            new("PageApps", "Atualizar aplicações", "", "Atualiza os apps já instalados no PC via winget.",
                "Detecta automaticamente os aplicativos instalados no seu PC com atualização pendente via winget e permite marcar apps como 'Nunca atualizar'.",
                "atualizar apps programas winget upgrade nunca atualizar"),
            new("PageAppsInstall", "Instalar aplicações", "", "Catálogo completo (+16.500 apps) com categorias, pacotes e instalação em lote.",
                "Pesquise, explore categorias e pacotes prontos (baseados no winstall.app) e instale vários aplicativos de uma só vez via winget.",
                "instalar apps programas loja catalogo winstall pacotes lote batch winget install"),
            new("PageAppsUninstall", "Desinstalar aplicações", "", "Desinstala programas, força remoção e limpa sobras (estilo Revo Uninstaller).",
                "Lista programas Win32 e apps da Microsoft Store, alerta em vermelho sobre componentes críticos do sistema, permite forçar desinstalação e limpa pastas e chaves de registro residuais.",
                "desinstalar remover revo uninstaller lixo residuos forcar registro limpar"),
        }, "atualizar instalar desinstalar programas apps winget winstall revo"),

        // ---------------- rodapé ----------------
        new("S:undo", Footer, "Central Desfazer", "", "Tudo o que o HyperCore mudou, para desfazer com um clique.", new NavTab[]
        {
            new("FF:undo", "Central Desfazer", "", "Tudo o que o HyperCore mudou, para desfazer com um clique.",
                "Lista tudo o que o HyperCore mudou e ainda está mudado (ajustes, nível aplicado, arranque, prioridades, overclock, DNS, serviços), com data, 'Desfazer' por item e 'Desfazer tudo'.",
                "desfazer reverter repor voltar atras restaurar backup undo alteracoes mudancas historico"),
        }, "desfazer reverter"),

        new("S:help", Footer, "Ajuda", "", "Dicas que dão mesmo FPS e as palavras técnicas explicadas.", new NavTab[]
        {
            new("FF:tips", "Dicas e avisos", "", "O que dá mesmo mais FPS, o que é mito e porque não dá ban.",
                "O que dá mesmo mais FPS, o que é mito, definições da NVIDIA e da BIOS, e porque é que esta app não dá ban.", "dicas mitos ban anticheat nvidia bios"),
            new("FF:glossary", "Glossário", "", "Cada palavra técnica explicada de forma simples.",
                "Cada palavra técnica explicada de forma simples.", "glossario palavras significado"),
        }, "ajuda help"),

        new("S:settings", Footer, "Configurações", "", "Arranque com o Windows e área de notificação.", new NavTab[]
        {
            new("FF:settings", "Configurações", "", "Arranque com o Windows e área de notificação.",
                "Escolhe se o HyperCore inicia com o Windows e se abre ou fica na área de notificação.", "definicoes opcoes preferencias arranque bandeja tema"),
        }, "configuracoes definicoes settings"),
    };

    public static NavSection? Section(string id) => Sections.FirstOrDefault(s => s.Id == id);

    /// <summary>Secções que contêm este tag (um tag pode estar em mais do que uma).</summary>
    public static IEnumerable<NavSection> SectionsWith(string tag) => Sections.Where(s => s.Tabs.Any(t => t.Tag == tag));
}
