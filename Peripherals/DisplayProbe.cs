using System.Diagnostics;
using System.Runtime.InteropServices;
using static FrameForge.Peripherals.PeriphNative;

namespace FrameForge.Peripherals;

/// <summary>Um ecrã ligado ao ambiente de trabalho (coordenadas em píxeis físicos).</summary>
internal sealed record MonitorInfo(
    string Device,        // \\.\DISPLAY1
    string Name,          // nome comercial (EDID) ou genérico
    bool Primary,
    int X, int Y, int Width, int Height,
    double ConfiguredHz,  // refresh configurado (exato, ex.: 164,96)
    int MaxHz)            // máximo que o Windows oferece nesta resolução
{
    public string Short => $"{Width} × {Height}";
}

/// <summary>Resultado de uma medição do refresh real.</summary>
internal sealed record RefreshResult(double Hz, double JitterMs, int Missed, string Method);

/// <summary>Lê os ecrãs e mede o refresh real com o sinal de vblank da gráfica. Só leitura.</summary>
internal static class DisplayProbe
{
    public static List<MonitorInfo> Monitors()
    {
        var friendly = FriendlyNames();
        var list = new List<MonitorInfo>();
        for (uint i = 0; i < 32; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            var dm = NewDevMode();
            if (!EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm)) continue;

            int max = (int)dm.dmDisplayFrequency;
            var m = NewDevMode();
            for (int k = 0; k < 4000 && EnumDisplaySettings(dd.DeviceName, k, ref m); k++)
            {
                if (m.dmPelsWidth == dm.dmPelsWidth && m.dmPelsHeight == dm.dmPelsHeight && m.dmDisplayFrequency > max && m.dmDisplayFrequency < 1000)
                    max = (int)m.dmDisplayFrequency;
                m = NewDevMode();
            }

            friendly.TryGetValue(dd.DeviceName, out var f);
            string name = !string.IsNullOrWhiteSpace(f.Name) ? f.Name : MonitorString(dd.DeviceName);
            double hz = f.Hz > 1 ? f.Hz : dm.dmDisplayFrequency;
            list.Add(new MonitorInfo(dd.DeviceName, name, (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                dm.dmPositionX, dm.dmPositionY, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight, hz, max));
        }
        list.Sort((a, b) => a.Primary == b.Primary ? string.CompareOrdinal(a.Device, b.Device) : a.Primary ? -1 : 1);
        return list;
    }

    private static DEVMODE NewDevMode() => new() { dmDeviceName = "", dmFormName = "", dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    private static string MonitorString(string adapterDevice)
    {
        var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (EnumDisplayDevices(adapterDevice, 0, ref mon, 0) && !string.IsNullOrWhiteSpace(mon.DeviceString)
            && !mon.DeviceString.Contains("PnP", StringComparison.OrdinalIgnoreCase))
            return mon.DeviceString;
        return "Ecrã " + adapterDevice.Replace(@"\\.\DISPLAY", "");
    }

    /// <summary>\\.\DISPLAYn → (nome do EDID, refresh exato) através do QueryDisplayConfig.</summary>
    private static Dictionary<string, (string Name, double Hz)> FriendlyNames()
    {
        var map = new Dictionary<string, (string, double)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return map;
            var paths = new DISPLAYCONFIG_PATH_INFO[np];
            var modes = new DISPLAYCONFIG_MODE_INFO[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return map;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME { viewGdiDeviceName = "" };
                src.header = new DISPLAYCONFIG_DEVICE_INFO_HEADER { type = 1, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), adapterId = p.sourceInfo.adapterId, id = p.sourceInfo.id };
                if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;
                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME { monitorFriendlyDeviceName = "", monitorDevicePath = "" };
                tgt.header = new DISPLAYCONFIG_DEVICE_INFO_HEADER { type = 2, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(), adapterId = p.targetInfo.adapterId, id = p.targetInfo.id };
                string name = DisplayConfigGetDeviceInfo(ref tgt) == 0 ? tgt.monitorFriendlyDeviceName : "";
                var rr = p.targetInfo.refreshRate;
                double hz = rr.Denominator > 0 ? (double)rr.Numerator / rr.Denominator : 0;
                if (!map.ContainsKey(src.viewGdiDeviceName)) map[src.viewGdiDeviceName] = (name, hz);
            }
        }
        catch { }
        return map;
    }

    /// <summary>
    /// Mede o refresh real de um ecrã contando os vblanks da gráfica durante <paramref name="duration"/>.
    /// Corre numa thread de fundo (bloqueia enquanto mede). Se o D3DKMT falhar, usa o DwmFlush (ritmo do compositor).
    /// </summary>
    public static RefreshResult Measure(string device, TimeSpan duration, CancellationToken ct = default)
    {
        IntPtr hdc = IntPtr.Zero;
        var open = new D3DKMT_OPENADAPTERFROMHDC();
        try
        {
            hdc = CreateDC(null, device, null, IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                open.hDc = hdc;
                if (D3DKMTOpenAdapterFromHdc(ref open) == 0)
                {
                    var wait = new D3DKMT_WAITFORVERTICALBLANKEVENT { hAdapter = open.hAdapter, VidPnSourceId = open.VidPnSourceId };
                    var r = Sample(() => D3DKMTWaitForVerticalBlankEvent(ref wait) == 0, duration, ct, "vblank da gráfica");
                    if (r != null) return r;
                }
            }
        }
        catch { }
        finally
        {
            if (open.hAdapter != 0) { var c = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter }; try { D3DKMTCloseAdapter(ref c); } catch { } }
            if (hdc != IntPtr.Zero) DeleteDC(hdc);
        }
        return Sample(() => DwmFlush() == 0, duration, ct, "compositor do Windows") ?? new RefreshResult(0, 0, 0, "indisponível");
    }

    private static RefreshResult? Sample(Func<bool> waitOne, TimeSpan duration, CancellationToken ct, string method)
    {
        if (!waitOne()) return null;
        var stamps = new List<long>(2048) { Stopwatch.GetTimestamp() };
        long end = stamps[0] + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (!ct.IsCancellationRequested)
        {
            if (!waitOne()) return null;
            long t = Stopwatch.GetTimestamp();
            stamps.Add(t);
            if (t >= end || stamps.Count > 20000) break;
        }
        if (stamps.Count < 10) return null;
        var iv = new double[stamps.Count - 1];
        for (int i = 1; i < stamps.Count; i++) iv[i - 1] = (stamps[i] - stamps[i - 1]) * 1000.0 / Stopwatch.Frequency;
        var sorted = iv.OrderBy(x => x).ToArray();
        double median = sorted[sorted.Length / 2];
        if (median < 0.05) return null; // a chamada não está a esperar a sério (ex.: DwmFlush sem composição)
        // média robusta: ignora intervalos absurdos (pausas da thread) para o valor principal
        var good = iv.Where(x => x < median * 1.5 && x > median * 0.5).ToArray();
        double mean = good.Length > 0 ? good.Average() : median;
        double jitter = good.Length > 1 ? Math.Sqrt(good.Sum(x => (x - mean) * (x - mean)) / good.Length) : 0;
        int missed = iv.Count(x => x > median * 1.5);
        return new RefreshResult(1000.0 / mean, jitter, missed, method);
    }
}
