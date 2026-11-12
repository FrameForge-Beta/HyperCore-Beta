using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrameForge.Core;
using FrameForge.Sensors;
using LibreHardwareMonitor.Hardware;

namespace FrameForge.Fans;

public enum CurveKind { Graph, Linear, Flat, Trigger, Mix, Sync }
public enum ControlMode { Auto, Curve, Manual }

public sealed class CurvePoint
{
    public double Temp { get; set; }
    public double Speed { get; set; }
}

/// <summary>Uma curva de ventoinha (os mesmos tipos do FanControl). A saída é sempre uma percentagem 0–100.</summary>
public sealed class FanCurve
{
    public string Name { get; set; } = "Nova curva";
    public CurveKind Kind { get; set; } = CurveKind.Graph;
    public string? SourceId { get; set; }
    public List<CurvePoint> Points { get; set; } = new()
    {
        new() { Temp = 30, Speed = 30 }, new() { Temp = 50, Speed = 40 }, new() { Temp = 65, Speed = 60 },
        new() { Temp = 75, Speed = 80 }, new() { Temp = 85, Speed = 100 },
    };
    // Linear
    public double MinTemp { get; set; } = 40;
    public double MaxTemp { get; set; } = 80;
    public double MinSpeed { get; set; } = 30;
    public double MaxSpeed { get; set; } = 100;
    // Plana
    public double FlatValue { get; set; } = 50;
    // Gatilho
    public double IdleTemp { get; set; } = 45;
    public double LoadTemp { get; set; } = 60;
    public double IdleSpeed { get; set; } = 30;
    public double LoadSpeed { get; set; } = 70;
    // Mistura
    public MixFunction MixFunction { get; set; } = MixFunction.Max;
    public List<string> MixCurves { get; set; } = new();
    // Sincronizar
    public string? SyncControlId { get; set; }
    public double SyncOffset { get; set; }
    public bool SyncProportional { get; set; }
    // Comuns
    public double Hysteresis { get; set; } = 2;
    public double ResponseTime { get; set; } = 1;
    public bool IgnoreHysteresisAtLimits { get; set; } = true;
}

/// <summary>Configuração de uma ventoinha (controlo).</summary>
public sealed class FanControlCfg
{
    public string ControlId { get; set; } = "";
    public string? Label { get; set; }
    public ControlMode Mode { get; set; } = ControlMode.Auto;
    public string? CurveName { get; set; }
    public double ManualValue { get; set; } = 50;
    public double StepUp { get; set; } = 8;      // % por segundo
    public double StepDown { get; set; } = 4;    // % por segundo
    public double StartPct { get; set; }
    public double StopPct { get; set; }
    public double MinPct { get; set; }
    public double OffsetPct { get; set; }
}

public sealed class FanConfig
{
    public bool Enabled { get; set; }
    public bool ApplyOnStart { get; set; } = true;
    public double EmergencyTemp { get; set; } = 90;
    public List<CustomSensorDef> Sensors { get; set; } = new();
    public List<FanCurve> Curves { get; set; } = new();
    public List<FanControlCfg> Controls { get; set; } = new();
}

/// <summary>
/// Motor de ventoinhas: a cada leitura do <see cref="SensorHub"/> calcula as curvas e escreve nos controlos.
/// Segurança: se a temperatura de uma curva deixar de existir durante 3 s, essa ventoinha vai a 100%;
/// se qualquer temperatura de CPU/GPU passar o limite de emergência, todas as ventoinhas controladas vão a 100%;
/// ao desligar (ou fechar a app) cada ventoinha volta ao controlo automático da BIOS/driver.
/// </summary>
public sealed class FanEngine
{
    public static FanEngine Instance { get; } = new();
    private static string File_ => Path.Combine(AppPaths.Root, "ventoinhas.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public FanConfig Config { get; private set; } = new();

    private sealed class CurveState { public double? LastIn; public double Output; public DateTime LastChange = DateTime.MinValue; public bool High; public DateTime MissingSince = DateTime.MaxValue; }
    private sealed class ControlState { public double Current = -1; public double LastWritten = -1; public DateTime LastWrite = DateTime.MinValue; public bool Touched; public double Target; public bool Failsafe; }

    private readonly Dictionary<string, CurveState> _curves = new();
    private readonly Dictionary<string, ControlState> _controls = new();
    private readonly object _lock = new();
    private DateTime _lastTick = DateTime.Now;
    private bool _hooked;

    public bool Emergency { get; private set; }
    public string? EmergencyReason { get; private set; }

    public void Load()
    {
        try { if (File.Exists(File_)) Config = JsonSerializer.Deserialize<FanConfig>(File.ReadAllText(File_), Json) ?? new(); } catch { Config = new(); }
        SensorHub.Instance.SetCustom(Config.Sensors);
    }

    public void Save()
    {
        lock (_lock)
        {
            try { File.WriteAllText(File_, JsonSerializer.Serialize(Config, Json)); } catch { }
            SensorHub.Instance.SetCustom(Config.Sensors);
        }
    }

    /// <summary>Ativa/desativa o controlo sem afetar a leitura partilhada dos sensores.</summary>
    public bool IsAttached { get { lock (_lock) return _hooked; } }

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            Config.Enabled = enabled;
            Config.ApplyOnStart = enabled && Config.ApplyOnStart;
        }
        if (enabled) Attach();
        else DetachAndRelease();
        Save();
    }

    /// <summary>Liga o motor apenas se o utilizador tiver ativado o controlo.</summary>
    public void Attach()
    {
        if (!Config.Enabled) return;
        SensorHub.Instance.Start();
        lock (_lock)
        {
            if (_hooked || !Config.Enabled) return;
            _hooked = true;
            SensorHub.Instance.Ticked += Tick;
        }
    }

    /// <summary>Desliga completamente o motor e devolve à BIOS/driver todas as ventoinhas que controlávamos.</summary>
    public void DetachAndRelease()
    {
        lock (_lock)
        {
            if (_hooked)
            {
                SensorHub.Instance.Ticked -= Tick;
                _hooked = false;
            }
        }
        ReleaseAll();
        lock (_lock)
        {
            _curves.Clear();
            Emergency = false;
            EmergencyReason = null;
        }
    }

    public double? CurveOutput(string name) { lock (_lock) return _curves.TryGetValue(name, out var s) ? s.Output : null; }
    public (double Target, double Current, bool Failsafe)? ControlOutput(string id)
    {
        lock (_lock) return _controls.TryGetValue(id, out var s) ? (s.Target, s.Current, s.Failsafe) : null;
    }

    /// <summary>Saída de uma curva para uma temperatura qualquer (para o gráfico do editor), sem histerese.</summary>
    public static double Evaluate(FanCurve c, double t) => c.Kind switch
    {
        CurveKind.Graph => Interp(c.Points, t),
        CurveKind.Linear => t <= c.MinTemp ? c.MinSpeed : t >= c.MaxTemp ? c.MaxSpeed
                            : c.MinSpeed + (t - c.MinTemp) / Math.Max(0.1, c.MaxTemp - c.MinTemp) * (c.MaxSpeed - c.MinSpeed),
        CurveKind.Flat => c.FlatValue,
        CurveKind.Trigger => t >= c.LoadTemp ? c.LoadSpeed : c.IdleSpeed,
        _ => 0,
    };

    private static double Interp(List<CurvePoint> pts, double t)
    {
        if (pts.Count == 0) return 100;
        var p = pts.OrderBy(x => x.Temp).ToList();
        if (t <= p[0].Temp) return p[0].Speed;
        if (t >= p[^1].Temp) return p[^1].Speed;
        for (int i = 1; i < p.Count; i++)
            if (t <= p[i].Temp)
                return p[i - 1].Speed + (t - p[i - 1].Temp) / Math.Max(0.01, p[i].Temp - p[i - 1].Temp) * (p[i].Speed - p[i - 1].Speed);
        return p[^1].Speed;
    }

    private static (double Min, double Max) Limits(FanCurve c) => c.Kind switch
    {
        CurveKind.Graph when c.Points.Count > 0 => (c.Points.Min(p => p.Temp), c.Points.Max(p => p.Temp)),
        CurveKind.Linear => (c.MinTemp, c.MaxTemp),
        _ => (double.MinValue, double.MaxValue),
    };

    // ---------------------------------------------------------------- ciclo

    private void Tick()
    {
        var hub = SensorHub.Instance;
        var now = DateTime.Now;
        double dt = Math.Clamp((now - _lastTick).TotalSeconds, 0.1, 5);
        _lastTick = now;

        lock (_lock)
        {
            if (!_hooked || !Config.Enabled) return;
            var cfg = Config;
            // Emergência: temperatura PRINCIPAL da CPU (Tctl/Package) ou do núcleo da GPU acima do limite.
            // O hotspot e a memória da gráfica ficam de fora: andam normalmente 10–20 °C acima do núcleo e disparavam a emergência sempre
            // em jogo (medido nesta RTX 2070 SUPER: núcleo 86 °C, hotspot 99 °C) — para eles há margem própria de +15 °C.
            var hot = hub.Temperatures().Where(e => e.Value is float v && v < 150 && IsMainTemp(e) &&
                                                   v >= cfg.EmergencyTemp + (IsGpuHotspot(e) ? 15 : 0)).ToList();
            Emergency = cfg.Enabled && hot.Count > 0;
            EmergencyReason = Emergency ? $"{hot[0].Display} a {hot[0].Value:0} °C" : null;

            var computed = new Dictionary<string, double?>();
            foreach (var c in cfg.Curves) computed[c.Name] = EvalCurve(c, cfg, computed, now, 0);

            var controls = hub.Controls();
            foreach (var cc in cfg.Controls)
            {
                var hw = controls.FirstOrDefault(x => x.Entry.Id == cc.ControlId);
                if (hw.Control == null) continue;
                if (!_controls.TryGetValue(cc.ControlId, out var st)) _controls[cc.ControlId] = st = new ControlState();

                if (!cfg.Enabled || cc.Mode == ControlMode.Auto)
                {
                    if (st.Touched) { try { hw.Control.SetDefault(); } catch { } st.Touched = false; st.Current = -1; st.LastWritten = -1; }
                    st.Target = hw.Entry.Value ?? 0;
                    st.Failsafe = false;
                    continue;
                }

                double? curveOut = cc.Mode == ControlMode.Manual ? cc.ManualValue : cc.CurveName != null ? computed.GetValueOrDefault(cc.CurveName) : null;
                bool failsafe = curveOut == null && cc.Mode == ControlMode.Curve;
                double target = failsafe ? 100 : curveOut!.Value;
                target += cc.OffsetPct;
                target = Math.Max(target, cc.MinPct);
                if (cc.StopPct > 0 && target < cc.StopPct) target = 0;
                if (Emergency) target = 100;
                target = Math.Clamp(target, 0, 100);

                double cur = st.Current < 0 ? (hw.Entry.Value ?? target) : st.Current;
                if (cur <= 0.5 && target > 0 && cc.StartPct > 0) cur = Math.Max(target, cc.StartPct); // arranque a partir de parada
                else if (Emergency || failsafe) cur = target;
                else if (target > cur) cur = Math.Min(target, cur + Math.Max(1, cc.StepUp) * dt);
                else cur = Math.Max(target, cur - Math.Max(1, cc.StepDown) * dt);
                st.Current = cur;
                st.Target = target;
                st.Failsafe = failsafe;

                // Limites do hardware (ex.: a RTX 2070 SUPER aceita 20–100%)
                float min = hw.Control.MinSoftwareValue, max = hw.Control.MaxSoftwareValue;
                double write = Math.Clamp(cur, cur <= 0.5 && min <= 0 ? 0 : min, max <= 0 ? 100 : max);
                if (Math.Abs(write - st.LastWritten) >= 0.5 || (now - st.LastWrite).TotalSeconds > 5)
                {
                    try { hw.Control.SetSoftware((float)write); st.Touched = true; st.LastWritten = write; st.LastWrite = now; } catch { }
                }
            }
        }
    }

    /// <summary>Hotspot e memória da gráfica (NVIDIA "GPU Memory Junction", AMD "GPU Memory"): contam com +15 °C de margem.</summary>
    private static bool IsGpuHotspot(SensorEntry e) => e.Id.Contains("gpu") && (e.Name.Contains("Hot Spot") || e.Name.Contains("Junction") || e.Name.Contains("Memory"));

    /// <summary>Temperaturas que contam para a emergência: CPU Tctl/Tdie/Package/Core Max e GPU núcleo/hotspot/memória (estas com margem).</summary>
    private static bool IsMainTemp(SensorEntry e)
    {
        if (e.Id.Contains("gpu")) return e.Name.Contains("Core") || IsGpuHotspot(e);
        if (e.Id.Contains("cpu")) return e.Name.Contains("Tctl") || e.Name.Contains("Tdie") || e.Name.Contains("Package") || e.Name.Contains("Core Max");
        return false;
    }

    private double? EvalCurve(FanCurve c, FanConfig cfg, Dictionary<string, double?> done, DateTime now, int depth)
    {
        if (depth > 8) return null;
        if (done.TryGetValue(c.Name, out var cached) && cached != null && depth > 0) return cached;
        if (!_curves.TryGetValue(c.Name, out var st)) _curves[c.Name] = st = new CurveState();

        switch (c.Kind)
        {
            case CurveKind.Flat:
                return st.Output = c.FlatValue;
            case CurveKind.Mix:
            {
                var vals = c.MixCurves.Select(n => cfg.Curves.FirstOrDefault(x => x.Name == n)).Where(x => x != null && x != c)
                                      .Select(x => EvalCurve(x!, cfg, done, now, depth + 1)).Where(v => v != null).Select(v => v!.Value).ToList();
                if (vals.Count == 0) return null;
                return st.Output = Math.Clamp(c.MixFunction switch
                {
                    MixFunction.Min => vals.Min(),
                    MixFunction.Average => vals.Average(),
                    MixFunction.Sum => vals.Sum(),
                    MixFunction.Subtract => vals[0] - vals.Skip(1).Sum(),
                    _ => vals.Max(),
                }, 0, 100);
            }
            case CurveKind.Sync:
            {
                if (c.SyncControlId == null || !_controls.TryGetValue(c.SyncControlId, out var other) || other.Current < 0) return null;
                double v = c.SyncProportional ? other.Current * (1 + c.SyncOffset / 100) : other.Current + c.SyncOffset;
                return st.Output = Math.Clamp(v, 0, 100);
            }
        }

        // Curvas com temperatura (gráfico, linear, gatilho)
        float? t = SensorHub.Instance.Value(c.SourceId);
        if (t == null)
        {
            if (st.MissingSince == DateTime.MaxValue) st.MissingSince = now;
            return (now - st.MissingSince).TotalSeconds > 3 ? null : st.Output; // 3 s sem leitura → falha (a ventoinha vai a 100%)
        }
        st.MissingSince = DateTime.MaxValue;
        double temp = t.Value;
        bool canChange = (now - st.LastChange).TotalSeconds >= c.ResponseTime;

        if (c.Kind == CurveKind.Trigger)
        {
            if (st.LastIn == null) { st.High = temp >= c.LoadTemp; st.LastIn = temp; st.LastChange = now; }
            else if (canChange && !st.High && temp >= c.LoadTemp) { st.High = true; st.LastChange = now; }
            else if (canChange && st.High && temp <= c.IdleTemp) { st.High = false; st.LastChange = now; }
            return st.Output = st.High ? c.LoadSpeed : c.IdleSpeed;
        }

        var (lo, hi) = Limits(c);
        bool atLimit = c.IgnoreHysteresisAtLimits && (temp <= lo || temp >= hi);
        if (st.LastIn == null || atLimit || (canChange && Math.Abs(temp - st.LastIn.Value) >= c.Hysteresis))
        {
            st.LastIn = temp;
            st.LastChange = now;
            st.Output = Math.Clamp(Evaluate(c, temp), 0, 100);
        }
        return st.Output;
    }

    /// <summary>Devolve todas as ventoinhas ao controlo da BIOS/driver (ao desligar ou ao fechar a app).</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            var controls = SensorHub.Instance.Controls();
            foreach (var (id, st) in _controls.Where(x => x.Value.Touched))
            {
                var hw = controls.FirstOrDefault(x => x.Entry.Id == id);
                try { hw.Control?.SetDefault(); } catch { }
                st.Touched = false;
                st.Current = -1;
                st.LastWritten = -1;
                st.Target = 0;
                st.Failsafe = false;
            }
        }
    }

    /// <summary>
    /// Assistente: cria a configuração do exemplo clássico — média entre a temperatura do núcleo e o hotspot da GPU,
    /// uma curva em gráfico sobre essa média e a(s) ventoinha(s) da GPU a seguir essa curva.
    /// </summary>
    public string QuickSetupGpu()
    {
        if (!Config.Enabled || !IsAttached)
            return "O controlo de ventoinhas está desligado. Ativa-o primeiro para aplicar a configuração do assistente.";
        var hub = SensorHub.Instance;
        var temps = hub.Temperatures();
        var core = temps.FirstOrDefault(e => e.Id.Contains("gpu") && e.Name.Contains("Core"));
        var hot = temps.FirstOrDefault(e => e.Id.Contains("gpu") && e.Name.Contains("Hot Spot"));
        if (core == null) return "Não encontrei a temperatura da GPU. Espera uns segundos e tenta de novo.";
        var srcs = new List<string> { core.Id };
        if (hot != null) srcs.Add(hot.Id);
        const string sensorName = "GPU (média núcleo + hotspot)";
        Config.Sensors.RemoveAll(s => s.Name == sensorName);
        Config.Sensors.Add(new CustomSensorDef { Name = sensorName, Kind = CustomKind.Mix, Function = MixFunction.Average, Sources = srcs });
        const string curveName = "Curva da GPU";
        Config.Curves.RemoveAll(c => c.Name == curveName);
        Config.Curves.Add(new FanCurve
        {
            Name = curveName, Kind = CurveKind.Graph, SourceId = "custom/" + sensorName, Hysteresis = 2, ResponseTime = 2,
            Points = new() { new() { Temp = 35, Speed = 30 }, new() { Temp = 55, Speed = 40 }, new() { Temp = 65, Speed = 55 }, new() { Temp = 72, Speed = 70 }, new() { Temp = 80, Speed = 90 }, new() { Temp = 86, Speed = 100 } },
        });
        int n = 0;
        foreach (var (e, _, _) in hub.Controls().Where(x => x.Entry.Id.Contains("gpu")))
        {
            var cc = Config.Controls.FirstOrDefault(x => x.ControlId == e.Id);
            if (cc == null) Config.Controls.Add(cc = new FanControlCfg { ControlId = e.Id });
            cc.Mode = ControlMode.Curve;
            cc.CurveName = curveName;
            n++;
        }
        Save();
        return hot != null
            ? $"Criado: sensor \"{sensorName}\", \"{curveName}\" e {n} ventoinha(s) da GPU a segui-la. Liga o controlo para começar."
            : $"Criado com a temperatura do núcleo (esta GPU não mostra hotspot) e {n} ventoinha(s). Liga o controlo para começar.";
    }
}
