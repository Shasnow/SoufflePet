using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Serilog;

namespace ShufuleiPet.Services;

/// <summary>
/// 手柄输入状态快照：字段与模型 cdi3.json 的 ParamGroup3「手柄」一一对应，
/// 值域也与模型参数一致——摇杆/十字键 -1~1，扳机与按键 0~1（布尔键用 0/1 写入）。
/// 实例由 GamepadService 每轮轮询就地更新，读取方只读字段（跨线程读到的可能是上一轮的值，无妨）。
/// </summary>
public sealed class GamepadState
{
    /// <summary>左摇杆 X/Y（-1~1，X 向右为正、Y 向上为正）。</summary>
    public float LStickX, LStickY;

    /// <summary>右摇杆 X/Y（-1~1，同上）。</summary>
    public float RStickX, RStickY;

    /// <summary>左右扳机（0~1 模拟量）。</summary>
    public float LTrigger, RTrigger;

    /// <summary>左右肩键、左右摇杆按下。</summary>
    public bool LShoulder, RShoulder, LStickButton, RStickButton;

    /// <summary>ABXY 四个按键。</summary>
    public bool ButtonA, ButtonB, ButtonX, ButtonY;

    /// <summary>十字键 X/Y（-1/0/1）。</summary>
    public int DPadX, DPadY;

    /// <summary>本轮是否检测到真实手柄（XInput 连接成功）。</summary>
    public bool HasGamepad;

    /// <summary>本轮是否存在有效输入（有手柄，或有键盘/鼠标输入），供调用方决定是否驱动参数。</summary>
    public bool AnyInput;
}

/// <summary>手柄输入服务抽象（DI 注册）。</summary>
public interface IGamepadService : IDisposable
{
    /// <summary>当前输入状态（就地更新的共享实例，只读字段）。</summary>
    GamepadState State { get; }
}

/// <summary>
/// 手柄输入服务：优先读真实手柄（XInput），没有手柄时自动降级到键鼠模拟——
/// WASD 当右摇杆、鼠标移动当左摇杆、方向键当十字键、J/K/L/I 当 A/B/X/Y。
/// 左右通道一律对调（用户设定）：真实手柄的物理左摇杆/左扳机/左肩键驱动模型的右摇杆/右扳机/右肩键，
/// 右侧同理映射到左侧；ABXY 内部也对调——物理/键盘的 A 触发模型 B、B 触发 A、X 触发 Y、Y 触发 X；
/// 十字键保持原位不参与对调。
/// 轮询周期 33ms（约 30Hz）：对桌面宠物这种"跟着动"的输入已经足够，
/// 且每轮只有几次 P/Invoke（XInputGetState / GetAsyncKeyState / GetCursorPos），资源占用可忽略。
/// </summary>
public sealed class GamepadService : IGamepadService
{
    private static readonly ILogger Logger = Log.ForContext<GamepadService>();

    /// <inheritdoc />
    public GamepadState State { get; } = new();

    private readonly DispatcherTimer _timer;

    /// <summary>已连接的手柄序号（-1 = 尚未找到）。优先查它，避免每轮扫描 4 个槽位。</summary>
    private int _padIndex = -1;

    /// <summary>未连接手柄时的扫描计数：手柄插拔不需要 30Hz 检测，每 12 轮（约 0.4 秒）扫一次即可。</summary>
    private int _scanCountdown;

    /// <summary>xinput1_4.dll 不可用（Win8.1 及更早）时改用 xinput1_3.dll。</summary>
    private bool _useLegacyXInput;

    /// <summary>上一轮的光标位置（物理像素），用于把鼠标位移换算成右摇杆偏转。</summary>
    private bool _mouseValid;
    private int _lastMouseX, _lastMouseY;

    /// <summary>左摇杆的平滑值（键鼠回退时由鼠标位移驱动）：鼠标停下时目标为 0，平滑回中（模拟摇杆松手回弹）。</summary>
    private float _smoothMouseX, _smoothMouseY;

    /// <summary>右摇杆的平滑值（键鼠回退时由 WASD 驱动）：按键是 0/1 突变，平滑后更像推摇杆。</summary>
    private float _smoothKbX, _smoothKbY;

    /// <summary>鼠标位移多少物理像素等于摇杆推满（越大越迟钝）。</summary>
    private const float MouseFullDeflectPx = 110f;

    /// <summary>每轮向目标逼近的比例（0~1，越大越跟手）。</summary>
    private const float Smoothing = 0.35f;

    public GamepadService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>轮询一轮：真实手柄 → 键鼠回退，结果写进 State。</summary>
    private void Poll()
    {
        if (TryReadGamepad(out var pad))
        {
            // 真实手柄：轴归一化后带死区处理，Y 轴 XInput 向上为正，与模型参数方向一致。
            // 左右通道整体对调（用户要求）：物理左摇杆/左扳机/左肩键 → 模型的右摇杆/右扳机/右肩键，
            // 物理右侧同理映射到模型左侧（十字键与 ABXY 保持原位，不参与对调）。
            // 死区常量跟随物理轴：左摇杆轴用左摇杆死区，右摇杆轴用右摇杆死区。
            State.HasGamepad = true;
            State.AnyInput = true;
            State.LStickX = NormAxis(pad.SThumbRX, RightThumbDeadZone);
            State.LStickY = NormAxis(pad.SThumbRY, RightThumbDeadZone);
            State.RStickX = NormAxis(pad.SThumbLX, LeftThumbDeadZone);
            State.RStickY = NormAxis(pad.SThumbLY, LeftThumbDeadZone);
            State.LTrigger = NormTrigger(pad.BRightTrigger);
            State.RTrigger = NormTrigger(pad.BLeftTrigger);
            var b = pad.WButtons;
            State.LShoulder = (b & RightShoulder) != 0;
            State.RShoulder = (b & LeftShoulder) != 0;
            State.LStickButton = (b & RightThumb) != 0;
            State.RStickButton = (b & LeftThumb) != 0;
            // ABXY 内部对调（用户要求）：物理 A→模型 B、物理 B→模型 A、物理 X→模型 Y、物理 Y→模型 X
            State.ButtonA = (b & BtnB) != 0;
            State.ButtonB = (b & BtnA) != 0;
            State.ButtonX = (b & BtnY) != 0;
            State.ButtonY = (b & BtnX) != 0;
            State.DPadX = ((b & DpadRight) != 0 ? 1 : 0) - ((b & DpadLeft) != 0 ? 1 : 0);
            State.DPadY = ((b & DpadUp) != 0 ? 1 : 0) - ((b & DpadDown) != 0 ? 1 : 0);
            // 手柄已接管，键鼠平滑值复位（切回键鼠时不会带着旧值跳一下）
            _smoothKbX = _smoothKbY = _smoothMouseX = _smoothMouseY = 0f;
            _mouseValid = false;
            return;
        }

        State.HasGamepad = false;
        ReadKeyboardMouseFallback(State);
    }

    /// <summary>
    /// 键鼠回退（用户指定的映射）：WASD 当视觉右摇杆（逻辑上是左摇杆，平滑推/回中），鼠标位移当视觉左摇杆（逻辑上是右摇杆）
    /// （停下自动回中），方向键当十字键，J/K/L/I 当 A/B/X/Y。任何一项有输入即 AnyInput=true。
    /// 真实手柄接入时走 XInput 分支，左右摇杆与物理键位一致，不受这里的映射影响。
    /// </summary>
    private void ReadKeyboardMouseFallback(GamepadState s)
    {
        // ---- 右摇杆：WASD ----
        var tx = (Key(DKey) ? 1f : 0f) - (Key(AKey) ? 1f : 0f);
        var ty = (Key(WKey) ? 1f : 0f) - (Key(SKey) ? 1f : 0f);
        // 斜向推满时长度会到 1.41，按摇杆习惯归一化到单位圆
        if (tx != 0f && ty != 0f)
        {
            var inv = 1f / MathF.Sqrt(2f);
            tx *= inv;
            ty *= inv;
        }
        _smoothKbX += (tx - _smoothKbX) * Smoothing;
        _smoothKbY += (ty - _smoothKbY) * Smoothing;
        s.RStickX = _smoothKbX;
        s.RStickY = _smoothKbY;

        // ---- 左摇杆：鼠标位移 ----
        var mouseActive = false;
        if (GetCursorPos(out var pt))
        {
            if (_mouseValid)
            {
                // 位移越大偏转越强，停住即回中（模拟松手回弹）
                var tLx = Math.Clamp((pt.X - _lastMouseX) / MouseFullDeflectPx, -1f, 1f);
                var tLy = Math.Clamp(-(pt.Y - _lastMouseY) / MouseFullDeflectPx, -1f, 1f);
                if (tLx != 0f || tLy != 0f) mouseActive = true;
                _smoothMouseX += (tLx - _smoothMouseX) * Smoothing;
                _smoothMouseY += (tLy - _smoothMouseY) * Smoothing;
            }
            _lastMouseX = pt.X;
            _lastMouseY = pt.Y;
            _mouseValid = true;
        }
        s.LStickX = _smoothMouseX;
        s.LStickY = _smoothMouseY;

        // ---- 十字键与功能键 ----
        s.DPadX = (Key(RightKey) ? 1 : 0) - (Key(LeftKey) ? 1 : 0);
        s.DPadY = (Key(UpKey) ? 1 : 0) - (Key(DownKey) ? 1 : 0);
        // ABXY 同样内部对调（与手柄路径保持一致）：J→模型 B、K→模型 A、L→模型 Y、I→模型 X
        s.ButtonA = Key(KKey);
        s.ButtonB = Key(JKey);
        s.ButtonX = Key(LKey);
        s.ButtonY = Key(IKey);
        // 键鼠模式没有模拟量扳机/肩键，保持 0（模型物理里它们只贡献小幅晃动）
        s.LTrigger = s.RTrigger = 0f;
        s.LShoulder = s.RShoulder = s.LStickButton = s.RStickButton = false;

        s.AnyInput = mouseActive
                  || MathF.Abs(s.LStickX) > 0.01f || MathF.Abs(s.LStickY) > 0.01f
                  || MathF.Abs(s.RStickX) > 0.01f || MathF.Abs(s.RStickY) > 0.01f
                  || s.DPadX != 0 || s.DPadY != 0
                  || s.ButtonA || s.ButtonB || s.ButtonX || s.ButtonY;
    }

    /// <summary>
    /// 读取真实手柄：优先查上次连接的槽位，失败或未连接时按扫描周期重扫 0~3。
    /// 返回 false 表示当前没有可用手柄。
    /// </summary>
    private bool TryReadGamepad(out XInputGamepad pad)
    {
        pad = default;
        if (_padIndex >= 0)
        {
            if (XInputGetState(_padIndex, out var st) == 0)
            {
                pad = st.Gamepad;
                return true;
            }
            _padIndex = -1; // 手柄掉线，下一轮起重扫
        }

        // 未连接时低频扫描：手柄插拔是低频事件，没必要每轮把 4 个槽位都问一遍
        if (--_scanCountdown > 0) return false;
        _scanCountdown = 12;

        for (var i = 0; i < 4; i++)
        {
            if (XInputGetState(i, out var st) != 0) continue;
            _padIndex = i;
            pad = st.Gamepad;
            Logger.Information("gamepad connected index={Index}", i);
            return true;
        }
        return false;
    }

    /// <summary>XInputGetState 封装：优先 xinput1_4.dll，缺失时自动退回 xinput1_3.dll。</summary>
    private int XInputGetState(int index, out XInputState state)
    {
        if (_useLegacyXInput) return XInputGetState13(index, out state);
        try
        {
            return XInputGetState14(index, out state);
        }
        catch (DllNotFoundException)
        {
            _useLegacyXInput = true;
            Logger.Warning("xinput1_4.dll not found, fallback to xinput1_3.dll");
            return XInputGetState13(index, out state);
        }
        catch (EntryPointNotFoundException)
        {
            _useLegacyXInput = true;
            Logger.Warning("XInputGetState missing in xinput1_4.dll, fallback to xinput1_3.dll");
            return XInputGetState13(index, out state);
        }
    }

    /// <summary>摇杆轴归一化：先扣死区再按剩余量程缩放，避免中心附近的抖动。</summary>
    private static float NormAxis(short raw, int deadZone)
    {
        var v = (int)raw;
        if (Math.Abs(v) <= deadZone) return 0f;
        var sign = v < 0 ? -1 : 1;
        var scaled = (Math.Abs(v) - deadZone) / (float)(32767 - deadZone);
        return Math.Clamp(sign * scaled, -1f, 1f);
    }

    /// <summary>扳机归一化：XInput 扳机是 0~255，同样扣掉触发阈值。</summary>
    private static float NormTrigger(byte raw)
    {
        if (raw <= TriggerThreshold) return 0f;
        return Math.Clamp((raw - TriggerThreshold) / (float)(255 - TriggerThreshold), 0f, 1f);
    }

    private static bool Key(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose() => _timer.Stop();

    // ===== 虚拟键码 =====
    private const int WKey = 0x57, AKey = 0x41, SKey = 0x53, DKey = 0x44;
    private const int UpKey = 0x26, DownKey = 0x28, LeftKey = 0x25, RightKey = 0x27;
    private const int JKey = 0x4A, KKey = 0x4B, LKey = 0x4C, IKey = 0x49;

    // ===== XInput 常量 =====
    private const int LeftThumbDeadZone = 7849;
    private const int RightThumbDeadZone = 8689;
    private const int TriggerThreshold = 30;
    private const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008;
    private const ushort LeftThumb = 0x0040, RightThumb = 0x0080;
    private const ushort LeftShoulder = 0x0100, RightShoulder = 0x0200;
    private const ushort BtnA = 0x1000, BtnB = 0x2000, BtnX = 0x4000, BtnY = 0x8000;

    // ===== P/Invoke =====
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort WButtons;
        public byte BLeftTrigger;
        public byte BRightTrigger;
        public short SThumbLX;
        public short SThumbLY;
        public short SThumbRX;
        public short SThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint DwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState14(int dwUserIndex, out XInputState state);

    [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState13(int dwUserIndex, out XInputState state);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);
}
