# FrameForge — guia da aplicação

**v0.3.24**
- Corrigida a leitura de RAM no overlay: percentagem e GB usam agora a memória física disponível reportada pelo Windows, como o Gestor de Tarefas.

## 1. Visão geral

O FrameForge é uma aplicação Windows para desempenho em jogos e diagnóstico de quebras. Combina captura de frames, sensores e relatórios para ajudar a identificar causas prováveis e sugerir próximos passos.

## 2. Como funciona

| Módulo | Como funciona | Limitação |
|---|---|---|
| FPS / overlay | ETW: eventos `Present` do DXGI (id 42) e D3D9 (id 1). O overlay do FrameForge é uma janela WPF transparente externa. | Calcula FPS e 1% low ao vivo; não guarda a sessão frame a frame por si só. O overlay externo não aparece sobre jogos em ecrã inteiro exclusivo. |
| Gargalo | Amostras periódicas e regras simples para estimar se o limite está na GPU, CPU, VRAM, temperatura ou RAM. | Não identifica necessariamente a causa de cada quebra e não mede todos os fatores possíveis, como drivers DPC, disco, aplicações em fundo ou rede. |
| Sensores | LibreHardwareMonitorLib, PawnIO opcional e NVML. | O PawnIO é um driver de kernel; pode ter implicações de compatibilidade com software de segurança. |
| Overclock | Ajustes de GPU através de NVAPI/NVML, quando suportados pelo driver. | A compatibilidade varia conforme a GPU e o driver; os ajustes devem ser testados com cuidado. |
| Otimização | Ajustes do Windows com cópia dos valores originais. | O ganho depende do PC e do jogo; nem todos os ajustes melhoram todos os sistemas. |
| Jogos | Deteção de bibliotecas e executáveis, com perfis por jogo. | Nem todos os launchers e jogos podem ser detetados automaticamente. |
| Outras ferramentas | Benchmarks, testes de esforço, discos, rede, RGB e ferramentas Maker. | Os resultados dependem do hardware, dos drivers e das permissões disponíveis. |

## 3. O FrameForge

Código em C#/.NET 8/WPF, focado em desempenho em jogos e diagnóstico de quebras. Desenvolvido por TheKillerMRM & MychTgvs.

### Regras anti-cheat
- Não injeta DLLs, não lê a memória do jogo e não faz hooks no DirectX.
- Não abre o processo do jogo para lhe alterar a prioridade; deteta processos através das APIs do Windows.
- Captura frames por ETW.
- Lê sensores por NVML e contadores do Windows.
- Overclock é aplicado pelo driver, fora do processo do jogo.
- O overlay é uma janela externa e requer modo janela ou janela sem bordas; não aparece em ecrã inteiro exclusivo.
- Sensores avançados de temperatura da CPU (PawnIO) estão desligados por omissão e acompanhados de aviso.

Para mostrar FPS em ecrã inteiro exclusivo, usa as estatísticas ou o overlay incorporados no próprio jogo ou no respetivo launcher.

### Separadores
1. **Painel** — hardware e fatores que podem afetar o desempenho: RAM, XMP, canal de memória, monitor, PCIe, driver, ReBAR, HVCI, ficheiro de paginação, discos e ferramentas em conflito.
2. **Jogos** — deteta Steam, Epic, Riot (Valorant/LoL), GOG, Ubisoft, Battle.net, EA e executáveis manuais. Permite perfis por jogo, gravação de sessões, overlay externo, fecho opcional de programas e argumentos de arranque.
3. **Sessões e relatórios** — grava jogos detetados ou iniciados pelo separador Jogos. Os relatórios incluem FPS médio, 1% e 0,1% low, consistência, quebras por minuto, gráfico de frametime e causas prováveis inferidas a partir dos sensores. As conclusões são estimativas, não provas definitivas.
4. **Otimização** — ajustes em níveis, com explicação, avisos e cópia dos valores originais para permitir restauro.
5. **Overclock** — perfis de GPU, limites suportados pelo driver e proteção que pode repor Stock após determinados erros do driver.
6. **Dicas e avisos** — recomendações sobre definições, BIOS e ajustes que podem não trazer benefícios.

## 4. Observações do PC registadas durante os testes

- RAM misturada: 2× Kingston KF3600C17 + 1× Kingston KHX2400C15 + 1× TeamGroup 3600, todas a 3200. O pente de 2400 CL15 pode limitar o conjunto. Compara os resultados de sessão antes e depois de alterar a configuração da RAM.
- Ecrã virtual do Parsec instalado (inativo).
- Plano de energia "Revision – Ultra Performance". Os níveis Seguro/Equilibrado podem incluir ajustes adicionais.
- Monitores a 240 Hz e 180 Hz. Jogos no SSD C:; D:/E: são HDD.
- Se os relatórios indicarem limite de CPU em jogos como Valorant/CS2, um Ryzen 7 5700X3D/5800X3D pode ser uma opção de atualização para a plataforma B550, após verificar a BIOS e a compatibilidade.

## 5. Como usar

1. Instala `artifacts\\installer\\FrameForge-Setup.exe` ou abre `publicar\\FrameForge.exe`. A publicação autónoma não requer instalar o .NET 8 Desktop Runtime. A aplicação pode pedir administrador para ETW, overclock e ajustes do sistema.
2. No **Painel**, verifica os avisos importantes.
3. Em **Jogos**, escolhe um jogo e ativa a gravação da sessão; ou usa **Sessões e relatórios** para gravar um jogo já aberto com **Ctrl+Alt+R**.
4. Joga durante 10–20 minutos e consulta o relatório em `%AppData%\\FrameForge\\Relatorios`.
5. Aplica as correções adequadas e compara outra sessão. Para overclock, começa com valores conservadores e usa Stock se algo correr mal.

Compilar: `dotnet build -c Release` dentro de `FrameForge\\`. Gerar a publicação autónoma: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publicar`. Para gerar o instalador, compila `installer\\FrameForge.iss` com o Inno Setup a partir da pasta `FrameForge\\installer\\`.

## 6. Estado dos testes

Testado neste PC: compilação, arranque, páginas, leitura NVAPI/NVML, deteção de hardware, conselheiro e jogos, leitura de ajustes, analisador e relatório com uma sessão simulada.

**Ainda por testar em condições reais** (algumas operações requerem permissões de administrador):
- captura de frames por ETW durante uma sessão de jogo;
- aplicação e estabilidade de perfis de overclock;
- aplicação dos níveis de otimização e restauro dos valores originais.

Possíveis melhorias futuras: controlo da curva de ventoinha via NVAPI, suporte AMD (ADLX), testes automáticos de estabilidade, comparação entre relatórios e diagnóstico adicional de rede.
