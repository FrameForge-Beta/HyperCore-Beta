using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FrameForge.Core;
using FrameForge.Overlay;
using FrameForge.Telemetry;

namespace FrameForge;

public sealed record OverlayMonitor(string DeviceName, string Label, bool IsPrimary);

/// <summary>
/// Overlay EXTERNO: uma janela transparente do tamanho do monitor do jogo, sempre no topo e que deixa passar os cliques.
/// Desenha o overlay ativo (separador Overlay — editor). Não entra no processo do jogo (ao contrário do RTSS/FPS Monitor),
/// por isso é seguro com anti-cheats — mas só aparece com o jogo em modo janela/sem bordas.
/// </summary>
public sealed class OverlayWindow : Window
{
    private static OverlayWindow? _instance;
    private static DispatcherTimer? _auto;
    private static bool _autoManaged;
    private static Task<HashSet<string>>? _gameProcessNames;
    private static bool _gameScanErrorReported;
    public static bool IsShown => _instance != null;

    private readonly Canvas _canvas = new();
    private readonly List<OverlayVisual> _visuals = new();
    private readonly DispatcherTimer _timer = new();
    private IntPtr _hwnd;
    private int _tick;

    public static void ShowFor(int pid)
    {
        _autoManaged = false;
        ShowCore(pid);
    }

    private static void ShowAutomatically(int pid)
    {
        _autoManaged = true;
        ShowCore(pid);
    }

    private static void ShowCore(int pid)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            OverlayValues.TargetPid = pid;
            _instance ??= new OverlayWindow();
            _instance.Show();
            _instance.FitToGameMonitor();
        });
    }

    public static void HideOverlay()
    {
        _autoManaged = false;
        Application.Current?.Dispatcher.Invoke(() => { _instance?.Close(); _instance = null; OverlayValues.TargetPid = 0; });
    }

    /// <summary>Mostra o overlay com base nos processos dos jogos, sem depender da apresentação de frames.</summary>
    public static void StartAutoMode()
    {
        if (_auto != null) return;
        _gameProcessNames ??= Task.Run(() =>
            FrameForge.Games.GameLibrary.Scan(AppServices.Settings)
                .SelectMany(game => game.ProcessNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
        int missing = 0;
        _auto = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            bool whenForeground = OverlayStore.Data.AutoShow;
            bool whenGameOpen = OverlayStore.Data.AutoShowWhenGameOpen;
            if (!whenForeground && !whenGameOpen)
            {
                missing = 0;
                if (_autoManaged) HideOverlay();
                return;
            }

            int fg = SystemSampler.ForegroundPid();
            var processNames = ConfiguredGameProcessNames();
            if (_gameProcessNames is { IsCompletedSuccessfully: true } scan)
                processNames.UnionWith(scan.Result);
            else if (_gameProcessNames is { IsFaulted: true } failed && !_gameScanErrorReported)
            {
                _gameScanErrorReported = true;
                Trace.TraceError($"Overlay game detection could not scan installed games: {failed.Exception}");
            }

            var games = RunningGameProcesses(processNames);
            bool gameOpen = whenGameOpen && games.Count > 0;
            bool gameForeground = whenForeground && fg != 0 && games.Any(game => game.Pid == fg);
            if (gameOpen || gameForeground)
            {
                missing = 0;
                int currentPid = games.FirstOrDefault(game => game.Pid == OverlayValues.TargetPid).Pid;
                int targetPid = gameForeground ? fg : currentPid != 0 ? currentPid : games[0].Pid;
                if (!IsShown || (_autoManaged && OverlayValues.TargetPid != targetPid))
                    ShowAutomatically(targetPid);
            }
            else if (IsShown && _autoManaged && ++missing >= 2)
            {
                HideOverlay();
            }
        }, Application.Current.Dispatcher);
        _auto.Start();
    }

    private static HashSet<string> ConfiguredGameProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in AppServices.Settings.Profiles.Values)
        {
            AddProcessName(names, profile.LastProcess);
            AddProcessName(names, profile.ExeOverride);
        }
        foreach (var game in AppServices.Settings.ManualGames)
            AddProcessName(names, game.ExePath);
        return names;
    }

    private static void AddProcessName(HashSet<string> names, string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return;
        names.Add(Path.GetFileNameWithoutExtension(executable));
    }

    private static List<(int Pid, string Name)> RunningGameProcesses(HashSet<string> processNames)
    {
        var games = new List<(int Pid, string Name)>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        int pid = process.Id;
                        string name = process.ProcessName;
                        if (pid != Environment.ProcessId && processNames.Contains(name))
                            games.Add((pid, name));
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
            }
        }
        catch (Win32Exception ex)
        {
            Trace.TraceError($"Overlay game detection could not enumerate running processes: {ex}");
        }
        return games.OrderBy(game => game.Pid).ToList();
    }

    private OverlayWindow()
    {
        Title = "FrameForge overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Content = _canvas;
        Rebuild();
        OverlayStore.Changed += OnStoreChanged;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); OverlayStore.Changed -= OnStoreChanged; };
        SizeChanged += (_, _) => PlaceAll();
    }

    private void OnStoreChanged() => Dispatcher.BeginInvoke(() =>
    {
        Rebuild();
        FitToGameMonitor();
    });

    private void Rebuild()
    {
        var p = OverlayStore.Active;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(p.UpdateMs, 100, 5000));
        _canvas.Children.Clear();
        _visuals.Clear();
        foreach (var it in p.Items)
        {
            var v = OverlayRenderer.Build(it, p.Scale);
            _visuals.Add(v);
            _canvas.Children.Add(v.Root);
        }
        PlaceAll();
        OverlayRenderer.UpdateAll(_visuals);
    }

    private void PlaceAll()
    {
        double factor = ActualWidth > 0 ? ActualWidth / OverlayRenderer.DesignWidth : 1;
        foreach (var v in _visuals)
            OverlayRenderer.Place(v, factor, OverlayStore.Active.PositionX, OverlayStore.Active.PositionY);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(_hwnd, -20);
        SetWindowLong(_hwnd, -20, ex | 0x20 /*TRANSPARENT*/ | 0x80000 /*LAYERED*/ | 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/);
        // não aparece em capturas/gravações? Não: deixamos aparecer (útil para streams). Para esconder: SetWindowDisplayAffinity.
    }

    public static IReadOnlyList<OverlayMonitor> GetAvailableMonitors()
    {
        var monitors = new List<OverlayMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), DeviceName = string.Empty };
            if (!GetMonitorInfo(handle, ref info)) return true;
            bool primary = (info.Flags & 1) != 0;
            string resolution = $"{info.Monitor.Right - info.Monitor.Left}×{info.Monitor.Bottom - info.Monitor.Top}";
            monitors.Add(new OverlayMonitor(info.DeviceName, $"{info.DeviceName} — {resolution}" + (primary ? " (principal)" : ""), primary));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    /// <summary>Fixa num monitor escolhido, segue o monitor do jogo ou usa o principal.</summary>
    private void FitToGameMonitor()
    {
        if (_hwnd == IntPtr.Zero) return;
        var selectedDevice = OverlayStore.Active.TargetMonitorDeviceName;
        IntPtr mon;
        if (!string.IsNullOrWhiteSpace(selectedDevice))
        {
            mon = FindMonitor(selectedDevice);
            if (mon == IntPtr.Zero) mon = MonitorFromWindow(IntPtr.Zero, 1 /*PRIMARY se não houver*/);
        }
        else
        {
            IntPtr target = OverlayStore.Active.OnGameMonitor ? GameWindowHandle() : IntPtr.Zero;
            mon = MonitorFromWindow(target, 1 /*PRIMARY se não houver*/);
        }
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), DeviceName = string.Empty };
        if (!GetMonitorInfo(mon, ref info)) return;
        var r = info.Monitor;
        SetWindowPos(_hwnd, new IntPtr(-1), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0010 /*NOACTIVATE*/);
    }

    private IntPtr GameWindowHandle()
    {
        int pid = OverlayValues.TargetPid;
        IntPtr foreground = GetForegroundWindow();
        if (pid <= 0) return foreground;
        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        if (foregroundPid == pid) return foreground;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainWindowHandle;
        }
        catch (ArgumentException) { return IntPtr.Zero; }
        catch (InvalidOperationException) { return IntPtr.Zero; }
    }

    private static IntPtr FindMonitor(string deviceName)
    {
        IntPtr found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), DeviceName = string.Empty };
            if (GetMonitorInfo(handle, ref info) && string.Equals(info.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                found = handle;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private void Tick()
    {
        OverlayRenderer.UpdateAll(_visuals);
        // Jogos sem bordas também se marcam "sempre no topo": reafirmar o lugar por cima; e seguir o jogo se mudar de monitor
        if (++_tick % 4 == 0)
        {
            SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            if (_tick % 8 == 0) FitToGameMonitor();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr mon, ref MonitorInfo mi);
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
}
