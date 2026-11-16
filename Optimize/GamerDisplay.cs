using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FrameForge.Optimize;

// =====================================================================================================================
// Monitores para a Configuração gamer: Hz atual vs máximo, ligação (HDMI/DisplayPort), VRR (G-SYNC / FreeSync / Adaptive-Sync)
// e mudança segura da taxa de atualização. Só lê o driver e o EDID do monitor: nunca toca em jogos.
//   - Lista e nomes: QueryDisplayConfig + DisplayConfigGetDeviceInfo (nome do monitor e caminho do dispositivo).
//   - Modos: EnumDisplaySettings (só os que o monitor aceita) e ChangeDisplaySettingsEx (dinâmico; gravar só depois de confirmado).
//   - VRR: NVAPI (NvAPI_Disp_GetVRRInfo) nas NVIDIA; EDID (bloco AMD FreeSync, HDMI Forum VRR, DisplayID Adaptive-Sync) nas outras.
// =====================================================================================================================

/// <summary>Sincronização variável (VRR) de um monitor, tanto quanto é possível ler.</summary>
public sealed record VrrInfo(
    /// <summary>"G-SYNC", "FreeSync", "Adaptive-Sync", "HDMI VRR" ou "" (não indicado).</summary>
    string Kind,
    /// <summary>O monitor (e a ligação) suportam VRR. null = não se sabe.</summary>
    bool? Capable,
    /// <summary>Está ligado no driver. null = não se consegue ler (AMD/Intel: só no painel do fabricante).</summary>
    bool? Enabled,
    int MinHz, int MaxHz,
    /// <summary>A TV/monitor anuncia ALLM (muda sozinha para o modo de jogo).</summary>
    bool Allm,
    /// <summary>Resposta do driver NVIDIA: true = G-SYNC possível neste monitor e ligação, false = não, null = sem driver NVIDIA ou sem resposta.</summary>
    bool? NvidiaPossible = null);

/// <summary>Um monitor ativo.</summary>
public sealed record GamerMonitor(
    string GdiName, string Name, string DevicePath, bool Primary, bool Internal, bool Virtual,
    int Width, int Height, int Bpp, int CurrentHz, int MaxHz, IReadOnlyList<int> Rates,
    string Connection, VrrInfo Vrr)
{
    /// <summary>Resolução atual, "2560 × 1440".</summary>
    public string Resolution => $"{Width} × {Height}";
    /// <summary>Está abaixo do máximo que o monitor aceita nesta resolução.</summary>
    public bool BelowMax => MaxHz > CurrentHz && MaxHz > 0;
}

public static class GamerDisplay
{
    // ------------------------------------------------------------------ leitura

    /// <summary>Monitores ativos (lento: chama o driver; usar fora do UI thread).</summary>
    public static List<GamerMonitor> Read()
    {
        var list = new List<GamerMonitor>();
        var targets = ReadTargets();
        var primary = PrimaryGdiName();
        Nv? nv = null;
        try { nv = Nv.TryOpen(); } catch { nv = null; }

        foreach (var gdi in ActiveGdiNames())
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettingsEx(gdi, ENUM_CURRENT_SETTINGS, ref dm, 0)) continue;
            int w = (int)dm.dmPelsWidth, h = (int)dm.dmPelsHeight, bpp = (int)dm.dmBitsPerPel, cur = (int)dm.dmDisplayFrequency;
            var rates = RatesFor(gdi, w, h);
            if (cur > 1 && !rates.Contains(cur)) rates.Add(cur);
            rates.Sort();
            targets.TryGetValue(gdi, out var t);

            string name = t?.Name is { Length: > 0 } n ? n : FallbackName(gdi);
            byte[]? edid = t?.DevicePath is { Length: > 0 } p ? ReadEdid(p) : null;
            if (edid != null && EdidName(edid) is { Length: > 0 } en && (name.Length == 0 || name.StartsWith("Generic", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Monitor PnP", StringComparison.OrdinalIgnoreCase)))
                name = en;
            if (name.Length == 0) name = "Monitor";

            var vrr = EdidVrr(edid);
            if (nv != null && nv.Vrr(gdi) is (bool possible, bool enabled))
            {
                // Monitor FreeSync/Adaptive-Sync numa NVIDIA = "G-SYNC Compatible"
                string nvKind = vrr.Kind.Length > 0 && vrr.Kind != "G-SYNC" ? "G-SYNC Compatible" : "G-SYNC";
                vrr = possible
                    ? vrr with { Kind = nvKind, Capable = true, Enabled = enabled, NvidiaPossible = true }
                    : vrr with { Enabled = vrr.Capable == true ? false : vrr.Enabled, NvidiaPossible = false };
            }

            uint tech = t?.Tech ?? uint.MaxValue;
            bool isInternal = tech is 0x80000000 or 6 or 11 or 13;
            bool isVirtual = tech is 15 or 16 or 17 || name.Contains("Parsec", StringComparison.OrdinalIgnoreCase) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
            list.Add(new GamerMonitor(gdi, name, t?.DevicePath ?? "", string.Equals(gdi, primary, StringComparison.OrdinalIgnoreCase),
                isInternal, isVirtual, w, h, bpp, cur, rates.Count > 0 ? rates.Max() : cur, rates, Connection(tech), vrr));
        }
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Virtual).ThenBy(m => m.GdiName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Connection(uint tech) => tech switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 or 11 or 13 or 0x80000000 => "Ecrã interno",
        10 => "DisplayPort",
        18 => "USB-C (DisplayPort)",
        15 => "Miracast",
        16 or 17 => "Ecrã virtual",
        _ => "",
    };

    /// <summary>Taxas (Hz) que o monitor aceita na resolução indicada, sem modos entrelaçados.</summary>
    private static List<int> RatesFor(string gdi, int w, int h)
    {
        var set = new SortedSet<int>();
        var dm = NewDevMode();
        for (int i = 0; i < 4000 && EnumDisplaySettingsEx(gdi, i, ref dm, 0); i++)
        {
            if (dm.dmPelsWidth == w && dm.dmPelsHeight == h && dm.dmDisplayFrequency > 1 && (dm.dmDisplayFlags & DM_INTERLACED) == 0)
                set.Add((int)dm.dmDisplayFrequency);
            dm = NewDevMode();
        }
        return set.ToList();
    }

    private static IEnumerable<string> ActiveGdiNames()
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; i < 32 && EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0 && (dd.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) == 0)
                yield return dd.DeviceName;
            dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        }
    }

    private static string? PrimaryGdiName()
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; i < 32 && EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0) return dd.DeviceName;
            dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        }
        return null;
    }

    private static string FallbackName(string gdi)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevices(gdi, 0, ref dd, 0) ? dd.DeviceString : "";
    }

    private sealed record TargetInfo(string Name, string DevicePath, uint Tech);

    /// <summary>Nome amigável, caminho e tipo de ligação de cada monitor, indexado pelo nome GDI (\\.\DISPLAY1).</summary>
    private static Dictionary<string, TargetInfo> ReadTargets()
    {
        var map = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return map;
            var paths = new DISPLAYCONFIG_PATH_INFO[np];
            var modes = new DISPLAYCONFIG_MODE_INFO[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return map;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                src.header.type = 1; src.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                src.header.adapterId = p.sourceInfo.adapterId; src.header.id = p.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;
                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tgt.header.type = 2; tgt.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                tgt.header.adapterId = p.targetInfo.adapterId; tgt.header.id = p.targetInfo.id;
                if (DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;
                if (!map.ContainsKey(src.viewGdiDeviceName))
                    map[src.viewGdiDeviceName] = new TargetInfo(tgt.monitorFriendlyDeviceName ?? "", tgt.monitorDevicePath ?? "", p.targetInfo.outputTechnology);
            }
        }
        catch { }
        return map;
    }

    // ------------------------------------------------------------------ EDID

    /// <summary>EDID do monitor a partir do caminho \\?\DISPLAY#MODELO#INSTÂNCIA#{guid} (registo Enum\DISPLAY).</summary>
    private static byte[]? ReadEdid(string devicePath)
    {
        try
        {
            var parts = devicePath.Replace(@"\\?\", "").Split('#');
            if (parts.Length < 3) return null;
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}\Device Parameters");
            return k?.GetValue("EDID") as byte[];
        }
        catch { return null; }
    }

    private static string? EdidName(byte[] e)
    {
        if (e.Length < 128) return null;
        for (int d = 54; d <= 108; d += 18)
            if (e[d] == 0 && e[d + 1] == 0 && e[d + 3] == 0xFC)
            {
                var s = Encoding.ASCII.GetString(e, d + 5, 13);
                int nl = s.IndexOf('\n');
                return (nl >= 0 ? s[..nl] : s).Trim();
            }
        return null;
    }

    /// <summary>VRR anunciado pelo próprio monitor: bloco AMD FreeSync, HDMI Forum (VRR + ALLM) ou DisplayID Adaptive-Sync.</summary>
    private static VrrInfo EdidVrr(byte[]? e)
    {
        var none = new VrrInfo("", null, null, 0, 0, false);
        if (e == null || e.Length < 128) return none;
        string kind = ""; int min = 0, max = 0; bool allm = false;
        int ext = Math.Min(e[126], (e.Length / 128) - 1);
        for (int x = 1; x <= ext; x++)
        {
            int b = x * 128;
            if (e[b] == 0x02) // CTA-861
            {
                int end = Math.Min((int)e[b + 2], 127);
                for (int i = b + 4; i < b + end;)
                {
                    int tag = e[i] >> 5, len = e[i] & 31;
                    if (i + len >= b + 128) break;
                    if (tag == 3 && len >= 3)
                    {
                        int oui = e[i + 1] | (e[i + 2] << 8) | (e[i + 3] << 16);
                        if (oui == 0x00001A) // AMD FreeSync
                        {
                            kind = "FreeSync";
                            if (len >= 7 && e[i + 6] >= 20 && e[i + 7] > e[i + 6]) { min = e[i + 6]; max = e[i + 7]; }
                        }
                        else if (oui == 0xC45DD8) // HDMI Forum (dados a seguir ao OUI: versão, TMDS, ..., [5] ALLM, [6]-[7] VRR)
                        {
                            int x0 = i + 4, plen = len - 3;
                            if (plen >= 6) allm |= (e[x0 + 5] & 0x02) != 0;
                            if (plen >= 8)
                            {
                                int vmin = e[x0 + 6] & 0x3F, vmax = ((e[x0 + 6] & 0xC0) << 2) | e[x0 + 7];
                                if (vmax > vmin && vmin > 0)
                                {
                                    if (kind.Length == 0) kind = "HDMI VRR";
                                    if (min == 0) { min = vmin; max = vmax; }
                                }
                            }
                        }
                    }
                    i += len + 1;
                }
            }
            else if (e[b] == 0x70) // DisplayID
            {
                int secLen = e[b + 2];
                for (int i = b + 5; i + 2 < b + Math.Min(5 + secLen, 127);)
                {
                    int tag = e[i], len = e[i + 2];
                    if (tag == 0x2B && kind.Length == 0) kind = "Adaptive-Sync";
                    if (len == 0 && tag == 0) break;
                    i += 3 + len;
                }
            }
        }
        return kind.Length == 0 ? none with { Allm = allm } : new VrrInfo(kind, true, null, min, max, allm);
    }

    // ------------------------------------------------------------------ mudar Hz

    /// <summary>Modo atual de um monitor (para poder voltar atrás).</summary>
    public static (int w, int h, int bpp, int hz)? Current(string gdi)
    {
        var dm = NewDevMode();
        return EnumDisplaySettingsEx(gdi, ENUM_CURRENT_SETTINGS, ref dm, 0)
            ? ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmBitsPerPel, (int)dm.dmDisplayFrequency)
            : null;
    }

    /// <summary>
    /// Muda só a taxa de atualização (mantém a resolução). persist=false: mudança dinâmica (não fica gravada no registo, mas continua
    /// em vigor mesmo que a app feche, até à próxima mudança de modo ou reinício; ver GamerSetup.TryRefresh, que deixa uma marca
    /// em disco para o arranque seguinte voltar atrás). persist=true: grava no registo. Devolve null se correu bem, ou a explicação do erro.
    /// </summary>
    public static string? SetRefresh(string gdi, int hz, bool persist)
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettingsEx(gdi, ENUM_CURRENT_SETTINGS, ref dm, 0)) return "Não consegui ler o modo atual deste monitor.";
        dm.dmDisplayFrequency = (uint)hz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        int test = ChangeDisplaySettingsEx(gdi, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (test != 0) return Explain(test);
        int r = ChangeDisplaySettingsEx(gdi, ref dm, IntPtr.Zero, persist ? CDS_UPDATEREGISTRY : 0, IntPtr.Zero);
        return r == 0 ? null : Explain(r);
    }

    /// <summary>
    /// Grava no registo a taxa que já está a ser usada (depois de uma mudança dinâmica confirmada), sem voltar a mudar o modo
    /// (CDS_NORESET: o ecrã não pisca outra vez). Devolve null se correu bem, ou a explicação do erro.
    /// </summary>
    public static string? SaveRefresh(string gdi, int hz)
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettingsEx(gdi, ENUM_CURRENT_SETTINGS, ref dm, 0)) return "Não consegui ler o modo atual deste monitor.";
        if ((int)dm.dmDisplayFrequency != hz) return SetRefresh(gdi, hz, persist: true);
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        int r = ChangeDisplaySettingsEx(gdi, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        return r == 0 ? null : Explain(r);
    }

    /// <summary>Volta ao modo gravado no registo (o que estava antes de uma mudança dinâmica).</summary>
    public static void RevertToSaved(string gdi)
    {
        try { ChangeDisplaySettingsEx(gdi, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); } catch { }
    }

    /// <summary>Nome GDI atual do monitor com este caminho de dispositivo (o número \\.\DISPLAYn pode mudar entre arranques).</summary>
    public static string? FindGdi(string devicePath, string fallbackGdi)
    {
        if (devicePath.Length > 0)
            foreach (var (gdi, t) in ReadTargets())
                if (string.Equals(t.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase)) return gdi;
        return ActiveGdiNames().Any(g => string.Equals(g, fallbackGdi, StringComparison.OrdinalIgnoreCase)) ? fallbackGdi : null;
    }

    private static string Explain(int code) => code switch
    {
        1 => "O Windows pede um reinício para esta mudança.",
        -2 => "O monitor não aceita esta taxa nesta resolução.",
        -3 => "Não foi possível gravar a definição.",
        -5 => "Parâmetros inválidos.",
        -6 => "O driver da gráfica recusou a mudança.",
        _ => $"O Windows recusou a mudança (código {code}).",
    };

    // ------------------------------------------------------------------ NVAPI (só leitura do estado do G-SYNC)

    private sealed unsafe class Nv
    {
        private readonly delegate* unmanaged[Cdecl]<byte*, uint*, int> _byName;
        private readonly delegate* unmanaged[Cdecl]<uint, byte*, int> _vrr;

        private Nv(IntPtr byName, IntPtr vrr)
        {
            _byName = (delegate* unmanaged[Cdecl]<byte*, uint*, int>)byName;
            _vrr = (delegate* unmanaged[Cdecl]<uint, byte*, int>)vrr;
        }

        private static Nv? _cached;
        private static bool _tried;

        public static Nv? TryOpen()
        {
            if (_tried) return _cached;
            _tried = true;
            if (!Environment.Is64BitProcess || !NativeLibrary.TryLoad("nvapi64.dll", out var lib)) return null;
            if (!NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qiPtr)) return null;
            var qi = (delegate* unmanaged[Cdecl]<uint, IntPtr>)qiPtr;
            IntPtr init = qi(0x0150E828), byName = qi(0xAE457190), vrr = qi(0xDF8FDA57);
            if (init == IntPtr.Zero || byName == IntPtr.Zero || vrr == IntPtr.Zero) return null;
            if (((delegate* unmanaged[Cdecl]<int>)init)() != 0) return null;
            return _cached = new Nv(byName, vrr);
        }

        /// <summary>(possível, ligado) do G-SYNC neste monitor, ou null se o driver não responder.</summary>
        public (bool possible, bool enabled)? Vrr(string gdi)
        {
            var name = Encoding.ASCII.GetBytes(gdi + "\0");
            uint id = 0;
            fixed (byte* n = name)
                if (_byName(n, &id) != 0) return null;
            byte* buf = stackalloc byte[64];
            new Span<byte>(buf, 64).Clear();
            *(uint*)buf = 24u | (1u << 16); // NV_GET_VRR_INFO_VER1
            if (_vrr(id, buf) != 0) return null;
            uint bits = *(uint*)(buf + 4);
            return ((bits & 2) != 0, (bits & 1) != 0);
        }
    }

    // ------------------------------------------------------------------ Win32

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000, DM_INTERLACED = 2;
    private const uint CDS_UPDATEREGISTRY = 1, CDS_TEST = 2, CDS_NORESET = 0x10000000;
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 1, DISPLAY_DEVICE_PRIMARY_DEVICE = 4, DISPLAY_DEVICE_MIRRORING_DRIVER = 8;
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;

    private static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling, refreshNum, refreshDen, scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO { [FieldOffset(0)] public uint infoType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNum, ref DEVMODE devMode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
                                                 ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);
}
