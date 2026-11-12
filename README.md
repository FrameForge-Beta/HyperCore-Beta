<div align="center">

# ⚡ FrameForge

**O ecossistema definitivo para Windows de monitorização em tempo real, telemetria segura para jogos, diagnóstico de hardware e otimização de desempenho.**

[![Versão](https://img.shields.io/badge/Versão-0.3.50_Beta-7928CA?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/FrameForge-Beta/FrameForge-Beta/releases/tag/v0.3.50)
[![Plataforma](https://img.shields.io/badge/.NET-8.0_WPF-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Linguagem](https://img.shields.io/badge/Linguagem-C%23_12_%2F_XAML-239120?style=for-the-badge&logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![Anti-Cheat Safe](https://img.shields.io/badge/Anti--Cheat-100%25_Seguro_(Sem_Injeção)-00C853?style=for-the-badge&logo=shield&logoColor=white)](#-arquitetura-anti-cheat-segura)

<br/>

<img src="Assets/screenshot-overview.png" alt="FrameForge - Painel de Visão Geral" width="900" style="border-radius: 8px; box-shadow: 0 4px 20px rgba(0,0,0,0.5);" />

</div>

---

## 📖 Sobre o Projeto

O **FrameForge** é uma suite completa de diagnóstico, telemetria e afinação para entusiastas de PC gaming e utilizadores exigentes. Desenvolvido para oferecer visibilidade total sobre o comportamento do hardware em jogos, o FrameForge combina telemetria de nível de driver e eventos do kernel sem nunca comprometer a integridade do sistema ou acionar proteções anti-cheat.

Com uma interface moderna, reativa e focada na usabilidade (Dark Theme com acentos roxos e renderização acelerada por GPU), a aplicação elimina a necessidade de usar múltiplas ferramentas pesadas e dispersas.

---

## 🛠️ Tecnologias e Linguagens Utilizadas

| Componente | Tecnologia / Linguagem | Descrição |
|---|---|---|
| **Linguagem Principal** | **C# 12 (.NET 8.0 Windows)** | Código fortemente tipado, assíncrono e de alto desempenho com blocos otimizados. |
| **Interface Gráfica** | **XAML / WPF (Windows Presentation Foundation)** | Interface moderna com aceleração gráfica por hardware (DirectX/D3D). |
| **Captura de Frames** | **ETW (Event Tracing for Windows)** | Rastreamento a nível de kernel via `Microsoft.Diagnostics.Tracing.TraceEvent` para captura de eventos `Present` de DXGI e Direct3D9. |
| **Renderização & Gráficos** | **DirectX 11, DirectX 12, DXGI (Vortice.Windows)** | Interação de baixo nível com a API gráfica para benchmarks sintéticos de GPU e medições de renderização. |
| **Sensores de Hardware** | **LibreHardwareMonitorLib & NVML** | Monitorização de clocks, temperaturas, consumos em Watts e ventoinhas para CPUs e GPUs (NVIDIA, AMD, Intel). |
| **Diagnóstico de Sistema** | **WMI, PDH & Win32 APIs** | Contadores de desempenho do Windows, telemetria de rede, SMART de discos e gestão de processos. |
| **Instalador & Empacotamento**| **Inno Setup & NSIS** | Instalador compilado com compressão sólida LZMA2 e suporte para atualizações limpas. |

---

## ✨ Funcionalidades Principais

### 1. 📊 Visão Geral e Telemetria em Tempo Real
- **Histórico Térmico e de Carga**: Gráficos dinâmicos em tempo real com filtros de 1 min, 5 min e 15 min para CPU e GPU.
- **Tabela Detalhada de Componentes**: Monitorização individual de Processador, Placa de Vídeo, Memória RAM e Discos (temperatura, carga percentual, clock atual e consumo em Watts).
- **Saúde do PC**: Verificação instantânea de integridade do sistema (erros WHEA, estabilidade de drivers de vídeo, Modo de Jogo e sensores).

### 2. 🎮 Gestão de Jogos e Análise de Sessões
- **Biblioteca de Jogos Unificada**: Deteção automática de títulos instalados via Steam, Epic Games, Riot Games (Valorant/LoL), GOG, Ubisoft, Battle.net, EA e executáveis manuais.
- **Gravação de Sessões Reais**: Registo automático ou via atalho global (`Ctrl + Alt + R`) com cálculo preciso de **FPS Médio, 1% Low, 0.1% Low e stutters por minuto**.
- **Relatórios Detalhados em HTML/JSON**: Histórico persistente de frametimes, oscilações e causas prováveis de quebras de desempenho.

### 3. 🖥️ Overlay Externo Sem Injeção
- **100% Seguro**: Funciona como janela transparente externa, garantindo conformidade total com jogos protegidos por **Riot Vanguard, Easy Anti-Cheat, BattlEye e VAC**.
- **Dois Modos**: Overlay completo personalizável e Overlay Simples minimalista.
- **Deteção de Gargalos ao Vivo**: Algoritmo heurístico que analisa se o limitador momentâneo de performance é CPU, GPU, VRAM, temperatura ou memória RAM.

### 4. ⚡ Otimização de Desempenho e Planos de Energia
- **Ajustes de Sistema Graduais**: Níveis de otimização documentados com criação de cópias de segurança para reversão a qualquer momento com 1 clique.
- **Perfis de Energia Dedicados**: Integração com `powercfg` para alternar automaticamente para modos de ultra-desempenho durante as sessões de jogo e restaurar o equilíbrio após sair.
- **Priorização Inteligente de Processos**: Otimização de tempo de CPU para o jogo em primeiro plano sem interferir com processos críticos do Windows.

### 5. 🌐 Diagnóstico de Rede e Benchmark de DNS
- **Medição de Latência Real**: Teste de conectividade e estabilidade de pacotes para gaming.
- **Benchmark DNS Direto**: Medição de tempo de resposta via queries UDP diretas para os principais servidores DNS (Cloudflare, Google, Quad9, etc.), sem alterar as definições da máquina sem o consentimento do utilizador.

### 6. ❄️ Controlo de Ventoinhas e Overclock Seguro
- **Curvas de Ventoinha Personalizadas**: Gestão dinâmica de RPM em função da temperatura do componente.
- **Afinador de GPU (NVIDIA & sensores universais)**: Ajuste de offsets de clock e limites de energia com proteção ativa contra bloqueios do driver de vídeo (**Crash Guard**).

---

## 🛡️ Arquitetura Anti-Cheat Segura

O FrameForge foi desenhado desde o primeiro dia respeitando as diretrizes dos sistemas anti-cheat modernos:
- ❌ **Sem Injeção de DLLs**: Não injeta código no espaço de memória de nenhum jogo.
- ❌ **Sem Hooks de DirectX/Vulkan**: Não intercepta chamadas gráficas diretamente dentro do processo.
- ❌ **Sem Abertura Invasiva de Processos**: Não lê buffers nem manipula ponteiros de processos externos.
- ✔️ **Event Tracing for Windows (ETW)**: Toda a contagem de frames é fornecida pelo próprio kernel do Windows através de canais públicos do sistema operacional.

---

## 🚀 Como Executar ou Instalar

### Opção 1: Instalador Oficial (Recomendado)
1. Acede aos [Releases](https://github.com/FrameForge-Beta/FrameForge-Beta/releases/latest).
2. Transfere o ficheiro `FrameForge-Setup-0.3.50.exe`.
3. Executa o instalador como Administrador e segue os passos simples do assistente.

### Opção 2: Compilar a partir do Código-Fonte
Certifica-te de ter instalado o [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
# 1. Clonar o repositório
git clone https://github.com/FrameForge-Beta/FrameForge-Beta.git
cd FrameForge-Beta

# 2. Restaurar dependências e compilar em modo Release
dotnet build -c Release

# 3. Executar o binário compilado
dotnet run --configuration Release
```

---

## 📄 Licença

Este projeto está licenciado sob os termos da Licença MIT. Consulta o ficheiro [LICENSE](LICENSE) para mais detalhes.
