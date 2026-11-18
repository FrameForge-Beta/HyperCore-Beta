using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrameForge.Core;

namespace FrameForge.Overlay;

public enum ItemKind { Value, Text, Bar, Graph, Box, Clock, Game }
public enum Anchor { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Regra de cor: "se o valor for ≥ (ou ≤) X, pinta com esta cor" — e opcionalmente pisca (alerta).</summary>
public sealed class Threshold
{
    public double At { get; set; }
    public bool Below { get; set; }
    public string Color { get; set; } = "#FFEF4444";
    public bool Blink { get; set; }
}

/// <summary>Um elemento do overlay. Tudo é configurável, como no FPS Monitor.</summary>
public sealed class OverlayItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public ItemKind Kind { get; set; } = ItemKind.Value;
    public bool Visible { get; set; } = true;
    public string? SensorId { get; set; }

    // Posição (em pixels de um ecrã 1920 de largura; escala para outras resoluções) e âncora
    public Anchor Anchor { get; set; } = Anchor.TopLeft;
    public double X { get; set; } = 20;
    public double Y { get; set; } = 20;
    public double Width { get; set; } = 220;   // barra, gráfico, caixa
    public double Height { get; set; } = 60;

    // Texto
    public string Label { get; set; } = "";
    public bool ShowLabel { get; set; } = true;
    public string Unit { get; set; } = "";
    public bool ShowUnit { get; set; } = true;
    public int Decimals { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 18;
    public double LabelSize { get; set; }      // 0 = 70% do tamanho do valor
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public string LabelColor { get; set; } = "#FF9CA3AF";
    public string ValueColor { get; set; } = "#FFFFFFFF";
    public string UnitColor { get; set; } = "#FF9CA3AF";

    // Caixa / fundo
    public string Background { get; set; } = "#00000000";
    public string BorderColor { get; set; } = "#00000000";
    public double CornerRadius { get; set; } = 6;
    public double Padding { get; set; } = 4;
    public bool Shadow { get; set; } = true;
    public double Opacity { get; set; } = 1;

    // Barra e gráfico
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public bool AutoScale { get; set; }
    public string BarColor { get; set; } = "#FF4F8CFF";
    public string BarBack { get; set; } = "#40FFFFFF";
    public int History { get; set; } = 120;
    public double LineWidth { get; set; } = 1.5;
    public bool FillGraph { get; set; } = true;

    // Relógio
    public string TimeFormat { get; set; } = "HH:mm";

    public List<Threshold> Thresholds { get; set; } = new();

    public OverlayItem Clone()
    {
        var json = JsonSerializer.Serialize(this, OverlayStore.Json);
        var c = JsonSerializer.Deserialize<OverlayItem>(json, OverlayStore.Json)!;
        c.Id = Guid.NewGuid().ToString("N")[..8];
        return c;
    }
}

public sealed class OverlayProfile
{
    public string Name { get; set; } = "O meu overlay";
    public List<OverlayItem> Items { get; set; } = new();
    public int UpdateMs { get; set; } = 500;
    public double Scale { get; set; } = 1.0;
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public bool OnGameMonitor { get; set; } = true;
    public string? TargetMonitorDeviceName { get; set; }
}

public sealed class OverlayStoreData
{
    public List<OverlayProfile> Profiles { get; set; } = new();
    public string? Active { get; set; }
    public bool AutoShow { get; set; }        // mostrar sozinho quando um jogo está em primeiro plano
    public bool AutoShowWhenGameOpen { get; set; }
    public bool HotkeyEnabled { get; set; } = true;
    public uint HotkeyModifiers { get; set; } = 0x0001 | 0x0002; // Ctrl+Alt
    public uint HotkeyVirtualKey { get; set; } = 0x4F; // O
    public int Version { get; set; }          // migrações já feitas aos overlays guardados (1 = temperatura do CPU)
}

/// <summary>Guarda os overlays em %AppData%\HyperCore\overlays.json e cria os modelos de origem.</summary>
public static class OverlayStore
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static string File_ => Path.Combine(AppPaths.Root, "overlays.json");
    public static OverlayStoreData Data { get; private set; } = Load();
    public static event Action? Changed;

    private static OverlayStoreData Load()
    {
        try
        {
            if (File.Exists(File_))
            {
                var d = JsonSerializer.Deserialize<OverlayStoreData>(File.ReadAllText(File_), Json);
                if (d != null && d.Profiles.Count > 0) { Migrate(d); return d; }
            }
        }
        catch { }
        var data = new OverlayStoreData { Version = CurrentVersion };
        data.Profiles.AddRange(Presets());
        data.Active = data.Profiles[1].Name;
        return data;
    }

    private const int CurrentVersion = 1;

    /// <summary>
    /// Overlays guardados por versões antigas: junta a temperatura do CPU ao lado do uso do CPU (como a da gráfica ao lado do uso da
    /// gráfica), só nos overlays que mostram o uso do CPU como valor e ainda não têm a temperatura. Corre uma vez.
    /// </summary>
    private static void Migrate(OverlayStoreData d)
    {
        if (d.Version >= CurrentVersion) return;
        foreach (var p in d.Profiles)
        {
            if (p.Items.Any(i => i.SensorId == "v/cpu_temp")) continue;
            var cpu = p.Items.FirstOrDefault(i => i.Kind == ItemKind.Value && i.SensorId == "v/cpu_total");
            if (cpu == null) continue;
            // Mesmo afastamento que a temperatura da gráfica tem do uso da gráfica (no Clássico: 78 px)
            var gpu = p.Items.FirstOrDefault(i => i.Kind == ItemKind.Value && i.SensorId == "v/gpu_load");
            var gpuTemp = p.Items.FirstOrDefault(i => i.Kind == ItemKind.Value && i.SensorId == "v/gpu_temp");
            double dx = gpu != null && gpuTemp != null && Math.Abs(gpu.Y - gpuTemp.Y) < 1 ? gpuTemp.X - gpu.X : 78;
            p.Items.Insert(p.Items.IndexOf(cpu) + 1, new OverlayItem
            {
                Kind = ItemKind.Value, SensorId = "v/cpu_temp", Label = "", Unit = "°C", X = cpu.X + dx, Y = cpu.Y, FontSize = cpu.FontSize,
                Anchor = cpu.Anchor, LabelColor = cpu.LabelColor, ValueColor = gpuTemp?.ValueColor ?? cpu.ValueColor,
                Thresholds = { Hot(80), Hot(90, "#FFEF4444") },
            });
        }
        d.Version = CurrentVersion;
        try { File.WriteAllText(File_, JsonSerializer.Serialize(d, Json)); } catch { }
    }

    public static void Save()
    {
        try { File.WriteAllText(File_, JsonSerializer.Serialize(Data, Json)); } catch { }
        Changed?.Invoke();
    }

    public static OverlayProfile Active => Data.Profiles.FirstOrDefault(p => p.Name == Data.Active) ?? Data.Profiles.First();

    // ---------------------------------------------------------------- modelos de origem

    private static OverlayItem V(string sensor, string label, string unit, double x, double y, double size = 18, int dec = 0,
                                 Anchor a = Anchor.TopLeft, params Threshold[] th) =>
        new() { Kind = ItemKind.Value, SensorId = sensor, Label = label, Unit = unit, X = x, Y = y, FontSize = size, Decimals = dec, Anchor = a, Thresholds = th.ToList() };

    private static Threshold Hot(double at, string color = "#FFF59E0B") => new() { At = at, Color = color };
    private static Threshold Low(double at, string color) => new() { At = at, Below = true, Color = color };

    public static List<OverlayProfile> Presets()
    {
        const string Red = "#FFEF4444", Yellow = "#FFF59E0B";
        return new()
        {
            new OverlayProfile
            {
                Name = "Só FPS",
                Items = { V("v/fps", "", "FPS", 16, 12, 30, th: new[] { Low(60, Red), Low(100, Yellow) }) },
            },
            new OverlayProfile
            {
                Name = "Clássico",
                Items =
                {
                    new OverlayItem { Kind = ItemKind.Box, X = 10, Y = 10, Width = 270, Height = 196, Background = "#A00B0D12", CornerRadius = 10, Shadow = false },
                    V("v/fps", "FPS", "", 22, 18, 26, th: new[] { Low(60, Red), Low(100, Yellow) }),
                    V("v/low1", "1% low", "", 150, 26, 16, th: new[] { Low(50, Red) }),
                    V("v/frametime", "frametime", "ms", 22, 58, 14, 1),
                    new OverlayItem { Kind = ItemKind.Graph, SensorId = "v/frametime", X = 22, Y = 84, Width = 246, Height = 40, Min = 0, Max = 30, AutoScale = true, Background = "#30FFFFFF", CornerRadius = 4, Shadow = false },
                    V("v/gpu_load", "GPU", "%", 22, 134, 15),
                    V("v/gpu_temp", "", "°C", 100, 134, 15, th: new[] { Hot(80), Hot(87, Red) }),
                    V("v/cpu_total", "CPU", "%", 22, 162, 15),
                    V("v/cpu_temp", "", "°C", 100, 162, 15, th: new[] { Hot(80), Hot(90, Red) }),
                    V("v/ram_pct", "RAM", "%", 150, 162, 15, th: new[] { Hot(85), Hot(93, Red) }),
                    V("v/vram_pct", "VRAM", "%", 150, 134, 15, th: new[] { Hot(90), Hot(97, Red) }),
                },
            },
            new OverlayProfile
            {
                Name = "Competitivo (discreto)",
                Items =
                {
                    V("v/fps", "", "", 12, 8, 20, a: Anchor.TopRight, th: new[] { Low(144, Yellow), Low(100, Red) }),
                    V("v/frametime", "", "ms", 12, 36, 12, 1, Anchor.TopRight),
                },
            },
            new OverlayProfile
            {
                Name = "Completo com gráficos",
                Items =
                {
                    new OverlayItem { Kind = ItemKind.Box, X = 10, Y = 10, Width = 330, Height = 330, Background = "#B00B0D12", CornerRadius = 12, BorderColor = "#404F8CFF", Shadow = false },
                    new OverlayItem { Kind = ItemKind.Game, X = 24, Y = 20, FontSize = 13, ValueColor = "#FF2E9BFF" },
                    new OverlayItem { Kind = ItemKind.Clock, X = 266, Y = 20, FontSize = 13, Anchor = Anchor.TopLeft, ValueColor = "#FF9CA3AF", TimeFormat = "HH:mm:ss" },
                    V("v/fps", "FPS", "", 24, 44, 28, th: new[] { Low(60, Red), Low(100, Yellow) }),
                    V("v/low1", "1%", "", 170, 52, 18),
                    new OverlayItem { Kind = ItemKind.Graph, SensorId = "v/frametime", Label = "frametime", X = 24, Y = 88, Width = 300, Height = 50, Min = 0, Max = 30, AutoScale = true, Background = "#30FFFFFF" },
                    new OverlayItem { Kind = ItemKind.Bar, SensorId = "v/gpu_load", Label = "GPU", Unit = "%", X = 24, Y = 152, Width = 300, Height = 14, FontSize = 13 },
                    new OverlayItem { Kind = ItemKind.Bar, SensorId = "v/cpu_maxthread", Label = "CPU (thread)", Unit = "%", X = 24, Y = 188, Width = 300, Height = 14, FontSize = 13, BarColor = "#FF2E9BFF" },
                    new OverlayItem { Kind = ItemKind.Bar, SensorId = "v/vram_pct", Label = "VRAM", Unit = "%", X = 24, Y = 224, Width = 300, Height = 14, FontSize = 13, BarColor = "#FFF472B6",
                                      Thresholds = { new Threshold { At = 95, Color = Red, Blink = true } } },
                    V("v/gpu_temp", "GPU", "°C", 24, 262, 15, th: new[] { Hot(80), Hot(87, Red) }),
                    V("v/gpu_power", "", "W", 140, 262, 15),
                    V("v/gpu_clock", "", "MHz", 210, 262, 15),
                    V("v/ram_used", "RAM", "GB", 24, 296, 15, 1),
                    V("v/cpu_temp", "CPU", "°C", 120, 296, 15, th: new[] { Hot(80), Hot(90, Red) }),
                    V("v/dpc", "DPC", "%", 220, 296, 15, 1, th: new[] { Hot(3, Red) }),
                },
            },
        };
    }
}
