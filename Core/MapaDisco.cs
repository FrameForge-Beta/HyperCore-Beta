using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace FrameForge.Core;

public static class MapaDisco
{
    public enum Tipo
    {
        Videos,
        Imagens,
        Musicas,
        Documentos,
        Programas,
        Compactados,
        Sistema,
        Outros
    }

    public sealed record Arquivo(string Nome, long Tamanho, Tipo Tipo, NoPasta Pasta)
    {
        public string Caminho => System.IO.Path.Combine(Pasta.Caminho, Nome);
    }

    public sealed class NoPasta(string nome, NoPasta? pai)
    {
        public long Proprio;
        public long Tamanho;
        public long Arquivos;
        public long ArquivosProprios;
        public long[] PorTipo = new long[8];
        public long[] PorTipoProprio = new long[8];
        public bool SemAcesso;
        public int Atalhos;
        public bool ForcarPrograma;
        public bool ForcarSistema;
        public string Nome { get; } = nome;
        public NoPasta? Pai { get; } = pai;
        public List<NoPasta> Filhas { get; } = new List<NoPasta>();
        public List<Arquivo>? Grandes { get; set; }

        public string Caminho
        {
            get
            {
                if (Pai == null)
                    return Nome;
                var list = new List<string>();
                for (NoPasta noPasta = this; noPasta != null; noPasta = noPasta.Pai)
                    list.Add(noPasta.Nome);
                list.Reverse();
                return System.IO.Path.Combine(list.ToArray());
            }
        }

        public Tipo TipoPrincipal(bool soProprio = false)
        {
            long[] array = (soProprio ? PorTipoProprio : PorTipo);
            int num = 0;
            for (int i = 1; i < 8; i++)
            {
                if (array[i] > array[num])
                    num = i;
            }
            return (Tipo)num;
        }
    }

    public sealed record Progresso(long Arquivos, long Pastas, long Bytes, long SemAcesso, string Atual);

    public sealed class Resultado
    {
        public required NoPasta Raiz { get; init; }
        public long Pastas { get; init; }
        public long PastasSemAcesso { get; init; }
        public long Atalhos { get; init; }
        public long ArquivosNaNuvem { get; init; }
        public TimeSpan Tempo { get; init; }
        public bool Parado { get; init; }
    }

    private struct InfoArquivo
    {
        public uint Atributos;
        public long Criado;
        public long Acessado;
        public long Gravado;
        public uint SerieVolume;
        public uint TamanhoAlto;
        public uint TamanhoBaixo;
        public uint Links;
        public uint IndiceAlto;
        public uint IndiceBaixo;
    }

    public sealed record Disco(string Raiz, string Rotulo, long Total, long Livre, DriveType Tipo)
    {
        public long Usado => Total - Livre;
    }

    public const int NTipos = 8;
    public const long Grande = 1048576L;

    private static readonly Dictionary<string, Tipo> PorExtensao = Montar();
    private static readonly Dictionary<string, Tipo> PorExtensaoLookup = Montar();

    private static readonly HashSet<string> PastasDePrograma = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Program Files", "Program Files (x86)", "steamapps", "Steam", "Epic Games", "XboxGames", "Riot Games", "WindowsApps", "GOG Games", "EA Games",
        "Ubisoft Game Launcher", "Battle.net", "Origin Games"
    };

    private static readonly string PastaWindows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
    private const FileAttributes NaNuvem = (FileAttributes)4460544;

    public static string NomeTipo(Tipo t)
    {
        return t switch
        {
            Tipo.Videos => "Vídeos",
            Tipo.Imagens => "Imagens",
            Tipo.Musicas => "Músicas",
            Tipo.Documentos => "Documentos",
            Tipo.Programas => "Programas e jogos",
            Tipo.Compactados => "Compactados",
            Tipo.Sistema => "Windows e sistema",
            _ => "Outros",
        };
    }

    private static Dictionary<string, Tipo> Montar()
    {
        Dictionary<string, Tipo> d = new Dictionary<string, Tipo>(StringComparer.OrdinalIgnoreCase);
        Add(Tipo.Videos, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts mts m2ts 3gp vob ogv rmvb braw r3d prproj drp");
        Add(Tipo.Imagens, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng orf rw2 psd psb ai svg ico jfif avif xcf kra");
        Add(Tipo.Musicas, "mp3 wav flac aac m4a ogg wma opus aiff aif mid midi ape alac amr");
        Add(Tipo.Documentos, "pdf doc docx xls xlsx xlsm ppt pptx odt ods odp rtf txt csv md epub mobi pub vsd vsdx one pst ost xps djvu cbr cbz");
        Add(Tipo.Programas, "exe dll msi msix msixbundle appx appxbundle sys ocx cab pak vpk bundle assets resource ress uasset ubulk utoc ucas bsa ba2 esm esp wad bik bk2 xnb pck sav big forge rpf obb nsp xci");
        Add(Tipo.Compactados, "zip rar 7z tar gz tgz bz2 xz zst iso img vhd vhdx vmdk wim esd dmg lz lzh arj");
        return d;
        void Add(Tipo t, string lista)
        {
            string[] array = lista.Split(' ');
            foreach (string key in array)
                d[key] = t;
        }
    }

    private static Tipo TipoDoArquivo(ReadOnlySpan<char> nome, NoPasta pasta)
    {
        if (pasta.ForcarSistema)
            return Tipo.Sistema;
        if (pasta.ForcarPrograma)
            return Tipo.Programas;
        ReadOnlySpan<char> extension = System.IO.Path.GetExtension(nome);
        if (extension.Length > 1)
        {
            ReadOnlySpan<char> readOnlySpan = extension.Slice(1);
            if (pasta.Pai == null && MemoryExtensions.Equals(readOnlySpan, "sys", StringComparison.OrdinalIgnoreCase))
                return Tipo.Sistema;
            string key = readOnlySpan.ToString();
            if (PorExtensaoLookup.TryGetValue(key, out var tipo))
                return tipo;
        }
        return Tipo.Outros;
    }

    public static Task<Resultado> VarrerAsync(string raiz, IProgress<Progresso>? progresso, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            DateTime utcNow = DateTime.UtcNow;
            raiz = System.IO.Path.GetFullPath(raiz);
            if (raiz.Length > 3)
                raiz = raiz.TrimEnd('\\');
            NoPasta noPasta = new NoPasta(raiz, null);
            MarcarEspeciais(noPasta, raiz);
            long arquivos = 0L;
            long pastas = 0L;
            long bytes = 0L;
            long semAcesso = 0L;
            long atalhos = 0L;
            long nuvem = 0L;
            string atual = raiz;
            ConcurrentStack<NoPasta> pilha = new ConcurrentStack<NoPasta>();
            pilha.Push(noPasta);
            int pendentes = 1;
            ConcurrentDictionary<(uint, ulong), byte> links = new ConcurrentDictionary<(uint, ulong), byte>();
            EnumerationOptions opcoes = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.None,
                ReturnSpecialDirectories = false,
                BufferSize = 65536
            };
            int count = Math.Clamp(Environment.ProcessorCount, 2, 8);
            Task[] tasks = (from _ in Enumerable.Range(0, count)
                select Task.Factory.StartNew(Trabalhador, TaskCreationOptions.LongRunning)).ToArray();
            while (!Task.WaitAll(tasks, 200))
            {
                progresso?.Report(new Progresso(Interlocked.Read(ref arquivos), Interlocked.Read(ref pastas), Interlocked.Read(ref bytes), Interlocked.Read(ref semAcesso), atual));
            }
            Somar(noPasta);
            TimeSpan tempo = DateTime.UtcNow - utcNow;
            System.Diagnostics.Debug.WriteLine($"Mapa do disco: {raiz} · {noPasta.Arquivos} arquivos, {pastas} pastas, {Bytes(noPasta.Tamanho)} em {tempo.TotalSeconds:0.0} s · sem acesso {semAcesso} · atalhos {atalhos} · na nuvem {nuvem} · memória {Bytes(Environment.WorkingSet)}{(ct.IsCancellationRequested ? " · PARADO" : "")}");
            return new Resultado
            {
                Raiz = noPasta,
                Pastas = pastas,
                PastasSemAcesso = semAcesso,
                Atalhos = atalhos,
                ArquivosNaNuvem = nuvem,
                Tempo = tempo,
                Parado = ct.IsCancellationRequested
            };

            void Ler(NoPasta pasta)
            {
                string caminho = pasta.Caminho;
                List<NoPasta> novas = new List<NoPasta>();
                long nArq = 0L;
                long soma = 0L;
                long nuvemAqui = 0L;
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(caminho, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        AttributesToSkip = FileAttributes.None,
                        ReturnSpecialDirectories = false
                    }))
                    {
                        if (ct.IsCancellationRequested)
                            break;
                        FileAttributes attrs;
                        try { attrs = File.GetAttributes(entry); }
                        catch { continue; }

                        if ((attrs & FileAttributes.Directory) != 0)
                        {
                            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                            {
                                pasta.Atalhos++;
                                continue;
                            }
                            string nome = System.IO.Path.GetFileName(entry);
                            NoPasta noPasta2 = new NoPasta(nome, pasta);
                            noPasta2.ForcarSistema = pasta.ForcarSistema || (pasta.Pai == null && System.IO.Path.Combine(caminho, noPasta2.Nome).Equals(PastaWindows, StringComparison.OrdinalIgnoreCase));
                            noPasta2.ForcarPrograma = pasta.ForcarPrograma || (!pasta.ForcarSistema && PastasDePrograma.Contains(noPasta2.Nome));
                            novas.Add(noPasta2);
                            continue;
                        }
                        if ((File.GetAttributes(entry) & (FileAttributes)0x441000) != 0)
                        {
                            nuvemAqui++;
                            continue;
                        }
                        long length = new FileInfo(entry).Length;
                        if (pasta.ForcarSistema && length > 0 && LinkJaContado(caminho, System.IO.Path.GetFileName(entry), links))
                        {
                            nArq++;
                            continue;
                        }
                        Tipo tipo = TipoDoArquivo(System.IO.Path.GetFileName(entry), pasta);
                        pasta.PorTipoProprio[(int)tipo] += length;
                        soma += length;
                        nArq++;
                        if (length >= Grande)
                        {
                            NoPasta noPasta3 = pasta;
                            (noPasta3.Grandes ?? (noPasta3.Grandes = new List<Arquivo>())).Add(new Arquivo(System.IO.Path.GetFileName(entry), length, tipo, pasta));
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
                {
                    pasta.SemAcesso = true;
                    Interlocked.Increment(ref semAcesso);
                }
                pasta.Proprio = soma;
                pasta.ArquivosProprios = nArq;
                pasta.Filhas.AddRange(novas);
                Interlocked.Add(ref arquivos, nArq);
                Interlocked.Add(ref bytes, soma);
                Interlocked.Add(ref nuvem, nuvemAqui);
                Interlocked.Add(ref atalhos, pasta.Atalhos);
                Interlocked.Increment(ref pastas);
                atual = caminho;
                if (novas.Count > 0 && !ct.IsCancellationRequested)
                {
                    Interlocked.Add(ref pendentes, novas.Count);
                    foreach (var n in novas) pilha.Push(n);
                }
            }
            void Trabalhador()
            {
                while (true)
                {
                    if (pilha.TryPop(out NoPasta result))
                    {
                        if (!ct.IsCancellationRequested)
                            Ler(result);
                        Interlocked.Decrement(ref pendentes);
                    }
                    else
                    {
                        if (Volatile.Read(ref pendentes) <= 0)
                            break;
                        Thread.Sleep(1);
                    }
                }
            }
        });
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string nome, uint acesso, uint compartilhar, nint seguranca, uint criacao, uint flags, nint modelo);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out InfoArquivo info);

    private static bool LinkJaContado(string pasta, string nome, ConcurrentDictionary<(uint, ulong), byte> vistos)
    {
        try
        {
            using SafeFileHandle safeFileHandle = CreateFileW(System.IO.Path.Join(pasta, nome), 0u, 7u, IntPtr.Zero, 3u, 35651584u, IntPtr.Zero);
            if (safeFileHandle.IsInvalid || !GetFileInformationByHandle(safeFileHandle, out var info) || info.Links <= 1)
                return false;
            return !vistos.TryAdd((info.SerieVolume, ((ulong)info.IndiceAlto << 32) | info.IndiceBaixo), 0);
        }
        catch
        {
            return false;
        }
    }

    private static void MarcarEspeciais(NoPasta no, string caminho)
    {
        if (caminho.Equals(PastaWindows, StringComparison.OrdinalIgnoreCase) || caminho.StartsWith(PastaWindows + "\\", StringComparison.OrdinalIgnoreCase))
            no.ForcarSistema = true;
        else if (caminho.Split('\\').Any(PastasDePrograma.Contains))
            no.ForcarPrograma = true;
    }

    private static void Somar(NoPasta raiz)
    {
        List<NoPasta> list = new List<NoPasta>();
        Stack<NoPasta> stack = new Stack<NoPasta>();
        stack.Push(raiz);
        while (stack.Count > 0)
        {
            NoPasta noPasta = stack.Pop();
            list.Add(noPasta);
            foreach (NoPasta filha in noPasta.Filhas)
                stack.Push(filha);
        }
        for (int num = list.Count - 1; num >= 0; num--)
        {
            NoPasta noPasta2 = list[num];
            noPasta2.Tamanho = noPasta2.Proprio;
            noPasta2.Arquivos = noPasta2.ArquivosProprios;
            Array.Copy(noPasta2.PorTipoProprio, noPasta2.PorTipo, 8);
            foreach (NoPasta filha2 in noPasta2.Filhas)
            {
                noPasta2.Tamanho += filha2.Tamanho;
                noPasta2.Arquivos += filha2.Arquivos;
                for (int i = 0; i < 8; i++)
                    noPasta2.PorTipo[i] += filha2.PorTipo[i];
            }
            noPasta2.Filhas.Sort((NoPasta a, NoPasta b) => b.Tamanho.CompareTo(a.Tamanho));
            noPasta2.Grandes?.Sort((Arquivo a, Arquivo b) => b.Tamanho.CompareTo(a.Tamanho));
        }
    }

    public static List<Arquivo> MaioresArquivos(NoPasta pasta, int n)
    {
        PriorityQueue<Arquivo, long> priorityQueue = new PriorityQueue<Arquivo, long>();
        Stack<NoPasta> stack = new Stack<NoPasta>();
        stack.Push(pasta);
        while (stack.Count > 0)
        {
            NoPasta noPasta = stack.Pop();
            if (noPasta.Grandes != null)
            {
                foreach (Arquivo grande in noPasta.Grandes)
                {
                    if (priorityQueue.Count < n)
                    {
                        priorityQueue.Enqueue(grande, grande.Tamanho);
                        continue;
                    }
                    if (grande.Tamanho > priorityQueue.Peek().Tamanho)
                    {
                        priorityQueue.EnqueueDequeue(grande, grande.Tamanho);
                        continue;
                    }
                    break;
                }
            }
            foreach (NoPasta filha in noPasta.Filhas)
            {
                if (filha.Tamanho >= Grande)
                    stack.Push(filha);
            }
        }
        List<Arquivo> list = new List<Arquivo>(priorityQueue.Count);
        while (priorityQueue.Count > 0)
            list.Add(priorityQueue.Dequeue());
        list.Reverse();
        return list;
    }

    public static List<Disco> Discos()
    {
        List<Disco> list = new List<Disco>();
        DriveInfo[] drives = DriveInfo.GetDrives();
        foreach (DriveInfo driveInfo in drives)
        {
            try
            {
                if (!driveInfo.IsReady)
                    continue;
                DriveType driveType = driveInfo.DriveType;
                if ((uint)(driveType - 2) > 1u)
                    continue;
                list.Add(new Disco(driveInfo.RootDirectory.FullName, driveInfo.VolumeLabel, driveInfo.TotalSize, driveInfo.TotalFreeSpace, driveInfo.DriveType));
            }
            catch { }
        }
        return list;
    }

    public static string Bytes(long bytes)
    {
        if (bytes < 0)
            return "—";
        string[] array = new string[5] { "B", "KB", "MB", "GB", "TB" };
        double num = bytes;
        int num2 = 0;
        while (num >= 1024.0 && num2 < array.Length - 1)
        {
            num /= 1024.0;
            num2++;
        }
        return $"{num:0.#} {array[num2]}";
    }
}