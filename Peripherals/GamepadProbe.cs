using System.Runtime.InteropServices;

namespace FrameForge.Peripherals;

/// <summary>Um comando detetado (XInput ou genérico/DirectInput via WinMM).</summary>
internal sealed record GamepadDevice(
    string Id,
    string Name,
    bool IsXInput,
    int SlotIndex,
    bool SupportsVibration,
    bool IsPlayStation);

/// <summary>Estado instantâneo de um comando (eixos normalizados em [-1, 1], gatilhos em [0, 1]).</summary>
internal sealed class GamepadSnapshot
{
    public bool Connected { get; init; }
    public double Lx { get; init; }
    public double Ly { get; init; }
    public double Rx { get; init; }
    public double Ry { get; init; }
    public double Lt { get; init; }
    public double Rt { get; init; }

    public bool DUp { get; init; }
    public bool DDown { get; init; }
    public bool DLeft { get; init; }
    public bool DRight { get; init; }

    public bool A { get; init; }
    public bool B { get; init; }
    public bool X { get; init; }
    public bool Y { get; init; }

    public bool Lb { get; init; }
    public bool Rb { get; init; }
    public bool Back { get; init; }
    public bool Start { get; init; }
    public bool L3 { get; init; }
    public bool R3 { get; init; }

    public string BatterySummary { get; init; } = "";
}

/// <summary>
/// Leitura de comandos de videojogo sem dependências externas:
/// tenta XInput (xinput1_4 / xinput1_3 / xinput9_1_0) para Xbox, comandos com emulação XInput e vibração,
/// e complementa com WinMM (joyGetPosEx) para comandos PlayStation / Nintendo / USB genéricos.
/// </summary>
internal static class GamepadProbe
{
    private const int ErrorSuccess = 0;

    // ------------------------------------------------------------------ XInput (carregado dinamicamente)

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    private delegate uint XInputGetStateFn(uint userIndex, out XINPUT_STATE state);
    private delegate uint XInputSetStateFn(uint userIndex, ref XINPUT_VIBRATION vibration);
    private delegate uint XInputGetBatteryFn(uint userIndex, byte devType, out XINPUT_BATTERY_INFORMATION info);

    private static readonly object XLock = new();
    private static bool _xLoaded;
    private static XInputGetStateFn? _xGetState;
    private static XInputSetStateFn? _xSetState;
    private static XInputGetBatteryFn? _xGetBattery;

    private static void EnsureXInput()
    {
        if (_xLoaded) return;
        lock (XLock)
        {
            if (_xLoaded) return;
            _xLoaded = true;
            foreach (var dll in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" })
            {
                if (!NativeLibrary.TryLoad(dll, out var h)) continue;
                if (NativeLibrary.TryGetExport(h, "XInputGetState", out var pGet))
                    _xGetState = Marshal.GetDelegateForFunctionPointer<XInputGetStateFn>(pGet);
                if (NativeLibrary.TryGetExport(h, "XInputSetState", out var pSet))
                    _xSetState = Marshal.GetDelegateForFunctionPointer<XInputSetStateFn>(pSet);
                if (NativeLibrary.TryGetExport(h, "XInputGetBatteryInformation", out var pBat))
                    _xGetBattery = Marshal.GetDelegateForFunctionPointer<XInputGetBatteryFn>(pBat);
                if (_xGetState != null) break;
            }
        }
    }

    // ------------------------------------------------------------------ WinMM (DirectInput / HID genérico)

    private const uint JOY_RETURNALL = 0x000000FF;
    private const uint JOY_POVCENTERED = 65535;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public int dwSize;
        public uint dwFlags;
        public uint dwXpos;
        public uint dwYpos;
        public uint dwZpos;
        public uint dwRpos;
        public uint dwUpos;
        public uint dwVpos;
        public uint dwButtons;
        public uint dwButtonNumber;
        public uint dwPOV;
        public uint dwReserved1;
        public uint dwReserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOYCAPS
    {
        public ushort wMid;
        public ushort wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax;
        public uint wNumButtons;
        public uint wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax;
        public uint wCaps;
        public uint wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
    }

    [DllImport("winmm.dll")]
    private static extern uint joyGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern uint joyGetPosEx(uint uJoyID, ref JOYINFOEX pji);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint joyGetDevCapsW(uint uJoyID, ref JOYCAPS pjc, uint cbjc);

    // ------------------------------------------------------------------ API pública

    /// <summary>Lista os comandos atualmente ligados (XInput primeiro; depois comandos genéricos/PlayStation não-XInput).</summary>
    public static List<GamepadDevice> ListDevices()
    {
        EnsureXInput();
        var list = new List<GamepadDevice>();
        int xCount = 0;

        if (_xGetState != null)
        {
            for (uint i = 0; i < 4; i++)
            {
                try
                {
                    if (_xGetState(i, out _) == ErrorSuccess)
                    {
                        xCount++;
                        list.Add(new GamepadDevice(
                            $"xinput:{i}",
                            $"Comando {i + 1} (XInput)",
                            IsXInput: true,
                            SlotIndex: (int)i,
                            SupportsVibration: _xSetState != null,
                            IsPlayStation: false));
                    }
                }
                catch { }
            }
        }

        // Complemento WinMM para comandos que não falam XInput (PlayStation DualShock/DualSense, Switch Pro, USB genérico)
        try
        {
            uint maxDevs = Math.Min(joyGetNumDevs(), 16);
            int joyFound = 0;
            for (uint i = 0; i < maxDevs; i++)
            {
                var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL };
                if (joyGetPosEx(i, ref info) != ErrorSuccess) continue;

                var caps = new JOYCAPS();
                bool hasCaps = joyGetDevCapsW(i, ref caps, (uint)Marshal.SizeOf<JOYCAPS>()) == ErrorSuccess;
                ushort vid = hasCaps ? caps.wMid : (ushort)0;
                ushort pid = hasCaps ? caps.wPid : (ushort)0;
                bool isSony = vid == 0x054C;
                bool isMsXbox = vid == 0x045E;

                // Evita duplicar comandos Xbox já listados via XInput
                if (xCount > 0 && isMsXbox) continue;
                joyFound++;
                if (xCount > 0 && !isSony && joyFound <= xCount && vid == 0) continue;

                string label = isSony
                    ? (pid is 0x0CE6 or 0x0DF2 ? $"PlayStation DualSense (#{i + 1})" : $"PlayStation DualShock (#{i + 1})")
                    : vid == 0x057E ? $"Nintendo Controller (#{i + 1})"
                    : vid == 0x046D ? $"Logitech Gamepad (#{i + 1})"
                    : vid == 0x2DC8 ? $"8BitDo Controller (#{i + 1})"
                    : $"Comando USB #{i + 1} (DirectInput)";

                list.Add(new GamepadDevice(
                    $"winmm:{i}",
                    label,
                    IsXInput: false,
                    SlotIndex: (int)i,
                    SupportsVibration: false,
                    IsPlayStation: isSony));
            }
        }
        catch { }

        return list;
    }

    /// <summary>Lê o estado atual do comando (rápido, sem chamadas de bateria bloqueantes).</summary>
    public static GamepadSnapshot Read(GamepadDevice device)
    {
        return device.IsXInput ? ReadXInput((uint)device.SlotIndex) : ReadWinMm((uint)device.SlotIndex, device.IsPlayStation);
    }

    /// <summary>
    /// Lê o estado da ligação/bateria do comando. Pode envolver I/O USB/Bluetooth no driver,
    /// pelo que deve ser chamado apenas ocasionalmente e fora do ciclo de 60+ Hz do UI.
    /// </summary>
    public static string ReadBatterySummary(GamepadDevice device)
    {
        if (!device.IsXInput)
            return device.IsPlayStation ? "Modo DirectInput (HID)" : "Modo DirectInput (USB)";
        EnsureXInput();
        if (_xGetBattery == null) return "";
        try
        {
            if (_xGetBattery((uint)device.SlotIndex, 0, out var bi) == ErrorSuccess)
            {
                return bi.BatteryType switch
                {
                    1 => "Ligação por cabo",
                    2 or 3 => "Sem fios · bateria " + bi.BatteryLevel switch
                    {
                        0 => "vazia",
                        1 => "fraca",
                        2 => "média",
                        3 => "cheia",
                        _ => "desconhecida"
                    },
                    _ => ""
                };
            }
        }
        catch { }
        return "";
    }

    /// <summary>Define a intensidade dos motores de vibração (0..1) num comando XInput.</summary>
    public static bool SetVibration(GamepadDevice device, double left, double right)
    {
        if (!device.IsXInput || !device.SupportsVibration) return false;
        EnsureXInput();
        if (_xSetState == null) return false;
        try
        {
            var vib = new XINPUT_VIBRATION
            {
                wLeftMotorSpeed = (ushort)Math.Clamp(Math.Round(left * 65535), 0, 65535),
                wRightMotorSpeed = (ushort)Math.Clamp(Math.Round(right * 65535), 0, 65535),
            };
            return _xSetState((uint)device.SlotIndex, ref vib) == ErrorSuccess;
        }
        catch { return false; }
    }

    public static void StopAllVibration()
    {
        EnsureXInput();
        if (_xSetState == null) return;
        var zero = new XINPUT_VIBRATION { wLeftMotorSpeed = 0, wRightMotorSpeed = 0 };
        for (uint i = 0; i < 4; i++)
        {
            try { _xSetState(i, ref zero); } catch { }
        }
    }

    private static GamepadSnapshot ReadXInput(uint slot)
    {
        EnsureXInput();
        if (_xGetState == null) return new GamepadSnapshot { Connected = false };
        try
        {
            if (_xGetState(slot, out var st) != ErrorSuccess)
                return new GamepadSnapshot { Connected = false };

            var g = st.Gamepad;
            ushort b = g.wButtons;

            return new GamepadSnapshot
            {
                Connected = true,
                Lx = NormShort(g.sThumbLX),
                Ly = -NormShort(g.sThumbLY), // ecrã: cima negativo, baixo positivo
                Rx = NormShort(g.sThumbRX),
                Ry = -NormShort(g.sThumbRY),
                Lt = g.bLeftTrigger / 255.0,
                Rt = g.bRightTrigger / 255.0,
                DUp = (b & 0x0001) != 0,
                DDown = (b & 0x0002) != 0,
                DLeft = (b & 0x0004) != 0,
                DRight = (b & 0x0008) != 0,
                Start = (b & 0x0010) != 0,
                Back = (b & 0x0020) != 0,
                L3 = (b & 0x0040) != 0,
                R3 = (b & 0x0080) != 0,
                Lb = (b & 0x0100) != 0,
                Rb = (b & 0x0200) != 0,
                A = (b & 0x1000) != 0,
                B = (b & 0x2000) != 0,
                X = (b & 0x4000) != 0,
                Y = (b & 0x8000) != 0,
                BatterySummary = ""
            };
        }
        catch
        {
            return new GamepadSnapshot { Connected = false };
        }
    }

    private static GamepadSnapshot ReadWinMm(uint slot, bool isSony)
    {
        try
        {
            var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL };
            if (joyGetPosEx(slot, ref info) != ErrorSuccess)
                return new GamepadSnapshot { Connected = false };

            double lx = NormU16(info.dwXpos);
            double ly = NormU16(info.dwYpos);
            double rx = NormU16(info.dwZpos);
            double ry = NormU16(info.dwRpos);

            uint btn = info.dwButtons;
            bool Btn(int idx) => (btn & (1u << idx)) != 0;

            double lt, rt;
            if (isSony)
            {
                // DualShock 4 / DualSense expõem L2/R2 em U/V ou botões 6/7
                lt = info.dwUpos > 0 ? Math.Clamp(info.dwUpos / 65535.0, 0, 1) : (Btn(6) ? 1.0 : 0.0);
                rt = info.dwVpos > 0 ? Math.Clamp(info.dwVpos / 65535.0, 0, 1) : (Btn(7) ? 1.0 : 0.0);
            }
            else
            {
                lt = Btn(6) ? 1.0 : 0.0;
                rt = Btn(7) ? 1.0 : 0.0;
            }

            bool dUp = false, dDown = false, dLeft = false, dRight = false;
            if (info.dwPOV != JOY_POVCENTERED && info.dwPOV < 36000)
            {
                int deg = (int)(info.dwPOV / 100);
                dUp = deg >= 315 || deg <= 45;
                dRight = deg >= 45 && deg <= 135;
                dDown = deg >= 135 && deg <= 225;
                dLeft = deg >= 225 && deg <= 315;
            }

            if (isSony)
            {
                return new GamepadSnapshot
                {
                    Connected = true,
                    Lx = lx, Ly = ly, Rx = rx, Ry = ry,
                    Lt = lt, Rt = rt,
                    DUp = dUp, DDown = dDown, DLeft = dLeft, DRight = dRight,
                    X = Btn(0),      // Quadrado
                    A = Btn(1),      // Cruz
                    B = Btn(2),      // Círculo
                    Y = Btn(3),      // Triângulo
                    Lb = Btn(4),     // L1
                    Rb = Btn(5),     // R1
                    Back = Btn(8),   // Share / Create
                    Start = Btn(9),  // Options
                    L3 = Btn(10),
                    R3 = Btn(11),
                    BatterySummary = "Modo DirectInput (HID)"
                };
            }

            return new GamepadSnapshot
            {
                Connected = true,
                Lx = lx, Ly = ly, Rx = rx, Ry = ry,
                Lt = lt, Rt = rt,
                DUp = dUp, DDown = dDown, DLeft = dLeft, DRight = dRight,
                A = Btn(0),
                B = Btn(1),
                X = Btn(2),
                Y = Btn(3),
                Lb = Btn(4),
                Rb = Btn(5),
                Back = Btn(8),
                Start = Btn(9),
                L3 = Btn(10),
                R3 = Btn(11),
                BatterySummary = "Modo DirectInput (USB)"
            };
        }
        catch
        {
            return new GamepadSnapshot { Connected = false };
        }
    }

    private static double NormShort(short v) => v < 0 ? v / 32768.0 : v / 32767.0;
    private static double NormU16(uint v) => Math.Clamp((v - 32767.5) / 32767.5, -1.0, 1.0);
}
