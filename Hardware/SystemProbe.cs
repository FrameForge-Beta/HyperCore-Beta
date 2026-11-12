using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FrameForge.Hardware;

public sealed record RamModule(string Manufacturer, string PartNumber, double SizeGB, int ConfiguredMHz, int? RatedMHz, int? RatedCl,
                               string Slot, string Bank);
public sealed record Display(string Device, string Adapter, int Width, int Height, int Hz, int MaxHz, bool Primary);
public sealed record DriveInfoEx(string Letter, string Model, string Media, string Bus, double SizeGB, double FreeGB);

/// <summary>Fotografia do hardware e do Windows, lida uma vez (WMI, registo, APIs de ecrã). Só leitura.</summary>
public sealed class SystemProbe
{
    public string CpuName { get; private set; } = "?";
    public int Cores { get; private set; }
    public int Threads { get; private set; }
    public int CpuBaseMHz { get; private set; }
    public string Board { get; private set; } = "?";
    public string BiosVersion { get; private set; } = "?";
    public string GpuName { get; private set; } = "?";
    public double VramGB { get; private set; }
    public string GpuDriver { get; private set; } = "?";
    public DateTime? GpuDriverDate { get; private set; }
    public List<string> OtherAdapters { get; } = new();
    public double RamGB { get; private set; }
    public List<RamModule> Ram { get; } = new();
    public string RamType { get; private set; } = "?";
    public List<Display> Displays { get; } = new();
    public List<DriveInfoEx> Drives { get; } = new();
    public string Os { get; private set; } = "?";
    public int OsBuild { get; private set; }
    public bool IsLaptop { get; private set; }
    public bool? HvciRunning { get; private set; }
    public bool PagefileOff { get; private set; }
    public string PowerPlan { get; private set; } = "?";
    public string PowerPlanGuid { get; private set; } = "";
    public bool? Hags { get; private set; }
    public bool? GameMode { get; private set; }
    public bool? GameDvr { get; private set; }
    public bool? ResizableBar { get; private set; }
    public (uint Cur, uint Max)? PcieWidth { get; private set; }
    public List<string> RunningTools { get; } = new();

    public static SystemProbe Collect()
    {
        var p = new SystemProbe();
        p.Safe(p.ReadCpu); p.Safe(p.ReadBoard); p.Safe(p.ReadGpu); p.Safe(p.ReadRam); p.Safe(p.ReadDisplays);
        p.Safe(p.ReadDrives); p.Safe(p.ReadOs); p.Safe(p.ReadWindowsState); p.Safe(p.ReadTools);
        return p;
    }

    private void Safe(Action a) { try { a(); } catch { } }

    private static IEnumerable<ManagementObject> Wmi(string query, string scope = @"root\cimv2")
    {
        using var s = new ManagementObjectSearcher(scope, query);
        foreach (ManagementObject o in s.Get()) yield return o;
    }

    private void ReadCpu()
    {
        foreach (var o in Wmi("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
        {
            CpuName = (o["Name"]?.ToString() ?? "?").Trim();
            Cores = Convert.ToInt32(o["NumberOfCores"]);
            Threads = Convert.ToInt32(o["NumberOfLogicalProcessors"]);
            CpuBaseMHz = Convert.ToInt32(o["MaxClockSpeed"]);
        }
    }

    private void ReadBoard()
    {
        foreach (var o in Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            Board = $"{o["Manufacturer"]} {o["Product"]}".Trim();
        foreach (var o in Wmi("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
            BiosVersion = o["SMBIOSBIOSVersion"]?.ToString() ?? "?";
        IsLaptop = Wmi("SELECT Name FROM Win32_Battery").Any();
    }

    private void ReadGpu()
    {
        foreach (var o in Wmi("SELECT Name, DriverVersion, DriverDate, AdapterRAM FROM Win32_VideoController"))
        {
            string name = o["Name"]?.ToString() ?? "?";
            bool real = Regex.IsMatch(name, "NVIDIA|AMD|Radeon|Intel|Arc", RegexOptions.IgnoreCase);
            if (real && (GpuName == "?" || name.Contains("NVIDIA") || name.Contains("Radeon")))
            {
                GpuName = name;
                GpuDriver = o["DriverVersion"]?.ToString() ?? "?";
                if (o["DriverDate"] is string d && d.Length >= 8) GpuDriverDate = ManagementDateTimeConverter.ToDateTime(d);
            }
            else OtherAdapters.Add(name);
        }
        OtherAdapters.Remove(GpuName);

        var h = Nvml.FirstDevice();
        if (h != IntPtr.Zero)
        {
            GpuName = Nvml.Name(h) ?? GpuName;
            if (Nvml.Vram(h) is Nvml.Memory m) VramGB = Math.Round(m.Total / 1073741824.0, 1);
            if (Nvml.DriverVersion() is string v) GpuDriver = v;
            if (Nvml.Bar1(h) is Nvml.Memory bar) ResizableBar = bar.Total > 300UL * 1024 * 1024;
            if (Nvml.PcieWidth(h) is uint cw && Nvml.PcieMaxWidth(h) is uint mw) PcieWidth = (cw, mw);
        }
        else
        {
            // Outras marcas: a VRAM vem do registo do adaptador (o AdapterRAM do WMI fica preso em 4 GB)
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
            if (k?.GetValue("HardwareInformation.qwMemorySize") is long q) VramGB = Math.Round(q / 1073741824.0, 1);
        }
    }

    private static readonly Regex RatedA = new(@"(\d{4})C(\d{2})", RegexOptions.Compiled);       // KF3600C17, F4-3600C16, CMK..3200C16
    private static readonly Regex RatedB = new(@"(?:DDR\d|D\d|UD4|U4)[-_]?(\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private void ReadRam()
    {
        foreach (var o in Wmi("SELECT Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, DeviceLocator, BankLabel, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            string part = (o["PartNumber"]?.ToString() ?? "").Trim();
            int? rated = null, cl = null;
            var a = RatedA.Match(part);
            if (a.Success) { rated = int.Parse(a.Groups[1].Value); cl = int.Parse(a.Groups[2].Value); }
            else if (RatedB.Match(part) is { Success: true } b) rated = int.Parse(b.Groups[1].Value);
            int cfg = Convert.ToInt32(o["ConfiguredClockSpeed"] ?? o["Speed"] ?? 0);
            Ram.Add(new RamModule((o["Manufacturer"]?.ToString() ?? "?").Trim(), part, Convert.ToUInt64(o["Capacity"]) / 1073741824.0,
                                  cfg, rated, cl, o["DeviceLocator"]?.ToString() ?? "", o["BankLabel"]?.ToString() ?? ""));
            RamType = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0) switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", _ => RamType };
        }
        RamGB = Math.Round(Ram.Sum(r => r.SizeGB), 1);
    }

    private void ReadDisplays()
    {
        var dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DisplayDevice>())
        {
            if ((dd.StateFlags & 1) == 0) continue; // não ligado ao ambiente de trabalho
            var cur = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(dd.DeviceName, -1, ref cur)) continue;
            int max = cur.dmDisplayFrequency;
            var m = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            for (int k = 0; EnumDisplaySettings(dd.DeviceName, k, ref m); k++)
                if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight) max = Math.Max(max, m.dmDisplayFrequency);
            Displays.Add(new Display(dd.DeviceName, dd.DeviceString, cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency, max, (dd.StateFlags & 4) != 0));
        }
    }

    private void ReadDrives()
    {
        var media = new Dictionary<string, (string Model, string Media, string Bus)>();
        const string st = @"root\Microsoft\Windows\Storage";
        foreach (var o in Wmi("SELECT DeviceId, FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk", st))
        {
            string mt = Convert.ToInt32(o["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "?" };
            string bus = Convert.ToInt32(o["BusType"] ?? 0) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", _ => "" };
            if (bus == "NVMe" && mt == "?") mt = "SSD";
            media[o["DeviceId"]?.ToString() ?? ""] = (o["FriendlyName"]?.ToString() ?? "?", mt, bus);
        }
        foreach (var o in Wmi("SELECT DriveLetter, DiskNumber FROM MSFT_Partition", st))
        {
            if (o["DriveLetter"] is not char c || c == '\0') continue;
            string letter = c + ":";
            var di = new DriveInfo(letter);
            if (!di.IsReady || di.TotalSize < 1_000_000_000) continue; // partições de sistema/recuperação
            var (model, mt, bus) = media.GetValueOrDefault(o["DiskNumber"]?.ToString() ?? "", ("?", "?", ""));
            Drives.Add(new DriveInfoEx(letter, model, mt, bus, di.TotalSize / 1e9, di.AvailableFreeSpace / 1e9));
        }
    }

    private void ReadOs()
    {
        foreach (var o in Wmi("SELECT Caption, BuildNumber FROM Win32_OperatingSystem"))
        {
            Os = o["Caption"]?.ToString()?.Replace("Microsoft ", "") ?? "?";
            OsBuild = int.TryParse(o["BuildNumber"]?.ToString(), out var b) ? b : 0;
        }
    }

    private void ReadWindowsState()
    {
        try
        {
            foreach (var o in Wmi("SELECT SecurityServicesRunning FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard"))
                HvciRunning = (o["SecurityServicesRunning"] as uint[])?.Contains(2u) ?? false;
        }
        catch { }

        bool auto = Wmi("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem").Select(o => o["AutomaticManagedPagefile"] is true).FirstOrDefault();
        PagefileOff = !auto && !Wmi("SELECT Name FROM Win32_PageFileUsage").Any();

        var plan = Run("powercfg.exe", "/getactivescheme");
        var m = Regex.Match(plan, @"([0-9a-fA-F\-]{36})\s+\((.+)\)");
        if (m.Success) { PowerPlanGuid = m.Groups[1].Value.ToLowerInvariant(); PowerPlan = m.Groups[2].Value; }

        Hags = RegDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") is int h ? h == 2 : null;
        GameMode = RegDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") is int g ? g == 1 : true;
        GameDvr = (RegDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") ?? 1) == 1;
    }

    private static readonly (string Proc, string Name)[] KnownTools =
    {
        ("MSIAfterburner", "MSI Afterburner"), ("RTSS", "RivaTuner Statistics Server (RTSS)"), ("EVGAPrecisionX1", "EVGA Precision X1"),
        ("GPUTweakIII", "ASUS GPU Tweak III"), ("ArmouryCrate", "ASUS Armoury Crate"), ("MSI.CentralServer", "MSI Center"),
        ("Overwolf", "Overwolf"), ("Discord", "Discord"), ("obs64", "OBS Studio"), ("chrome", "Google Chrome"), ("msedge", "Microsoft Edge"),
        ("NVIDIA Overlay", "Overlay da NVIDIA"), ("iCUE", "Corsair iCUE"), ("lghub", "Logitech G HUB"), ("Razer Synapse 3", "Razer Synapse"),
        ("FanControl", "FanControl"), ("HWiNFO64", "HWiNFO"), ("vgtray", "Riot Vanguard"), ("parsecd", "Parsec"),
    };

    private void ReadTools()
    {
        var names = Process.GetProcesses().Select(p => { try { return p.ProcessName; } finally { p.Dispose(); } })
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (proc, name) in KnownTools)
            if (names.Contains(proc)) RunningTools.Add(name);
    }

    public static int? RegDword(RegistryKey hive, string path, string name)
    {
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(name) is int v ? v : null;
    }

    public static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return o;
    }

    public Display? PrimaryDisplay => Displays.FirstOrDefault(d => d.Primary) ?? Displays.FirstOrDefault();

    // ---- P/Invoke ecrã ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint num, ref DisplayDevice dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);
}
