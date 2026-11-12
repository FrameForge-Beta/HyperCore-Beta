using System.Runtime.InteropServices;

namespace FrameForge.Telemetry;

/// <summary>
/// Contadores de desempenho do Windows (PDH) pelo nome em inglês (PdhAddEnglishCounter), para funcionar em Windows
/// em português ou noutra língua. Não abre nenhum processo: lê o que o próprio Windows já contabiliza.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint FmtDouble = 0x200, FmtNoCap100 = 0x8000;
    private const int MoreData = unchecked((int)0x800007D2);

    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new();

    public PdhQuery()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
    }

    public bool Add(string key, string englishPath)
    {
        if (_query == IntPtr.Zero) return false;
        if (PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out var c) != 0) return false;
        _counters[key] = c;
        return true;
    }

    public void Collect()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    public double? Value(string key)
    {
        if (!_counters.TryGetValue(key, out var c)) return null;
        if (PdhGetFormattedCounterValue(c, FmtDouble | FmtNoCap100, IntPtr.Zero, out var v) != 0) return null;
        return v.CStatus <= 1 ? v.Double : null;
    }

    /// <summary>Todos os valores de um contador com instância "*" (nome da instância → valor).</summary>
    public List<(string Name, double Value)> Values(string key)
    {
        var list = new List<(string, double)>();
        if (!_counters.TryGetValue(key, out var c)) return list;
        uint size = 0;
        int rc = PdhGetFormattedCounterArrayW(c, FmtDouble | FmtNoCap100, ref size, out uint count, IntPtr.Zero);
        if (rc != MoreData || size == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            rc = PdhGetFormattedCounterArrayW(c, FmtDouble | FmtNoCap100, ref size, out count, buf);
            if (rc != 0) return list;
            int itemSize = Marshal.SizeOf<ItemW>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<ItemW>(buf + i * itemSize);
                if (item.Value.CStatus > 1) continue;
                list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.Double));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FmtValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double Double;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ItemW
    {
        public IntPtr Name;
        public FmtValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out FmtValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
}
