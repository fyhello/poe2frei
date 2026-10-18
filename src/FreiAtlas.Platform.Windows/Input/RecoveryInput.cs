using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Recovery;
using FreiAtlas.Platform.Windows.Native;
using FreiAtlas.Platform.Windows.Windows;

namespace FreiAtlas.Platform.Windows.Input;

public sealed class RecoveryInput : IRecoveryInput
{
    public bool IsTargetForeground(int processId)
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == 0 || processId <= 0 || NativeMethods.IsIconic(window)) return false;
        NativeMethods.GetWindowThreadProcessId(window, out var foregroundProcess);
        return foregroundProcess == processId;
    }

    /// <summary>
    /// Gets effective client size for the target game process, with foreground window prioritization
    /// and primary screen resolution fallback.
    /// </summary>
    public Vector2? GetEffectiveClientSize(int processId)
    {
        if (processId <= 0) return null;
        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out var fgPid);
        if (fgPid == processId && NativeMethods.GetClientRect(foreground, out var clientRect) && clientRect.Right > clientRect.Left)
        {
            return new Vector2(clientRect.Right - clientRect.Left, clientRect.Bottom - clientRect.Top);
        }

        var window = new GameWindowTracker().Track(processId);
        if (window is { IsMinimized: false, ClientBounds.Width: > 0, ClientBounds.Height: > 0 })
        {
            return new Vector2(window.ClientBounds.Width, window.ClientBounds.Height);
        }

        var screenW = NativeMethods.GetSystemMetrics(0);
        var screenH = NativeMethods.GetSystemMetrics(1);
        return new Vector2(screenW > 0 ? screenW : 2560, screenH > 0 ? screenH : 1440);
    }

    public RecoveryInputResult Send(int processId, RecoveryKey key, DateTimeOffset capturedAt)
    {
        var age = DateTimeOffset.UtcNow - capturedAt;
        if (age < TimeSpan.Zero || age > TimeSpan.FromMilliseconds(250) || !IsTargetForeground(processId))
            return new(RecoveryInputState.Paused, "已暂停：游戏失焦或读数过期");
        if (new ushort[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, key.VirtualKey }.Any(code => (GetAsyncKeyState(code) & 0x8000) != 0))
            return new(RecoveryInputState.Paused, "等待手动按键释放");

        var keys = key.Modifiers.Append(key.VirtualKey).ToArray();
        var inputs = keys.Select(code => CreateKeyboardInput(code, false))
            .Concat(keys.Reverse().Select(code => CreateKeyboardInput(code, true))).ToArray();
        if (!IsTargetForeground(processId)) return new(RecoveryInputState.Paused, "已暂停：游戏不在前台");
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<InputUnion>());
        if (sent == inputs.Length) return new(RecoveryInputState.Sent, "已发送恢复按键：" + key.Gesture);

        var error = Marshal.GetLastWin32Error();
        var releases = keys.Reverse().Select(code => CreateKeyboardInput(code, true)).ToArray();
        var released = SendInput((uint)releases.Length, releases, Marshal.SizeOf<InputUnion>());
        return new(RecoveryInputState.Failed, $"系统接收 {sent}/{inputs.Length} 个事件，错误码 {error}；释放事件 {released}/{releases.Length}");
    }

    /// <summary>Clicks at the existing cursor position; it never moves the cursor.</summary>
    public bool ClickCurrentCursor(int processId)
    {
        if (!IsTargetForeground(processId) || IsLeftMouseButtonHeld()) return false;
        var inputs = new[] { CreateMouseInput(0x0002), CreateMouseInput(0x0004) };
        if (!IsTargetForeground(processId)) return false;
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<InputUnion>()) == inputs.Length;
    }

    /// <summary>
    /// Checks whether a modifier key (e.g. Shift, Ctrl, Alt, Space) is currently pressed down.
    /// </summary>
    public bool IsModifierKeyPressed(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return true;
        var vk = keyName.Trim().ToLowerInvariant() switch
        {
            "shift" or "lshift" => 0x10,
            "rshift" => 0xA1,
            "ctrl" or "control" or "lctrl" => 0x11,
            "rctrl" => 0xA3,
            "alt" or "lalt" or "menu" => 0x12,
            "ralt" => 0xA5,
            "space" or "空格" => 0x20,
            _ => (int)(char.ToUpperInvariant(keyName.Trim()[0]))
        };
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    /// <summary>
    /// Moves the cursor to the exact client point (e.g. ground item label center), performs a click,
    /// and immediately restores the cursor to its original position within ~60-80ms.
    /// </summary>
    public bool TryClickClientPoint(int processId, Vector2 clientPoint)
    {
        if (processId <= 0 || !float.IsFinite(clientPoint.X) || !float.IsFinite(clientPoint.Y))
            return false;
        if (!IsTargetForeground(processId))
            return false;
        if (!GetCursorPos(out var original))
            return false;

        Rectangle bounds;
        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out var fgPid);
        if (fgPid == processId && NativeMethods.GetClientRect(foreground, out var clientRect) && clientRect.Right > clientRect.Left)
        {
            var originPt = new NativeMethods.NativePoint { X = 0, Y = 0 };
            NativeMethods.ClientToScreen(foreground, ref originPt);
            bounds = new Rectangle(originPt.X, originPt.Y, clientRect.Right - clientRect.Left, clientRect.Bottom - clientRect.Top);
        }
        else
        {
            var window = new GameWindowTracker().Track(processId);
            if (window is not null && !window.IsMinimized && window.ClientBounds.Width > 0 && window.ClientBounds.Height > 0)
            {
                bounds = window.ClientBounds;
            }
            else
            {
                var screenW = NativeMethods.GetSystemMetrics(0);
                var screenH = NativeMethods.GetSystemMetrics(1);
                bounds = new Rectangle(0, 0, screenW > 0 ? screenW : 2560, screenH > 0 ? screenH : 1440);
            }
        }

        if (clientPoint.X < 0 || clientPoint.Y < 0 || clientPoint.X >= bounds.Width || clientPoint.Y >= bounds.Height)
            return false;

        // 自然微抖动：在标签几何中心安全区内微扰，水平方向安全冗余大(±3px)，垂直方向标签较薄收敛至±1px，防止WASD滚屏时擦边点空
        var jitterX = Random.Shared.Next(-3, 4);
        var jitterY = Random.Shared.Next(-1, 2);
        var destination = new NativePoint
        {
            X = Math.Clamp(bounds.Left + (int)MathF.Round(clientPoint.X) + jitterX, bounds.Left, bounds.Right - 1),
            Y = Math.Clamp(bounds.Top + (int)MathF.Round(clientPoint.Y) + jitterY, bounds.Top, bounds.Bottom - 1)
        };

        var moved = false;
        var wasLeftHeld = IsLeftMouseButtonHeld();
        try
        {
            if (!SetCursorPos(destination.X, destination.Y)) return false;
            moved = true;
            // 等待游戏 UI 引擎稳定产生 Hover 焦点（跨越 1.5 个渲染帧）
            Thread.Sleep(25 + Random.Shared.Next(0, 4));

            if (!IsTargetForeground(processId)) return false;

            // 模拟人手稳健点击：按住 25~28ms，确保游戏物理与 UI 判定必定捕获
            var downInput = new[] { CreateMouseInput(0x0002) };
            var sentDown = SendInput((uint)downInput.Length, downInput, Marshal.SizeOf<InputUnion>());
            if (sentDown != downInput.Length) return false;

            Thread.Sleep(25 + Random.Shared.Next(0, 4));

            var upInput = new[] { CreateMouseInput(0x0004) };
            var sentUp = SendInput((uint)upInput.Length, upInput, Marshal.SizeOf<InputUnion>());

            // 核心修复：抬起后稳稳留足 25~28ms，确保游戏主循环彻底完成这一帧的“点击命中判定”之后，再执行光标还原，绝不提前扯走导致游戏把点击当成“拖拽出界而废弃”
            Thread.Sleep(25 + Random.Shared.Next(0, 4));
            return sentUp == upInput.Length;
        }
        finally
        {
            if (moved)
            {
                SetCursorPos(original.X, original.Y);
                // 关键自然度优化：若玩家此前正按住左键移动且未松开，恢复原位后立即续上按压，消除跑路停顿感
                if (wasLeftHeld && IsLeftMouseButtonHeld())
                {
                    var restoreDown = new[] { CreateMouseInput(0x0002) };
                    SendInput((uint)restoreDown.Length, restoreDown, Marshal.SizeOf<InputUnion>());
                }
            }
        }
    }

    private static bool IsLeftMouseButtonHeld()
        => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    private static bool IsMouseButtonHeld()
        => (GetAsyncKeyState(0x01) & 0x8000) != 0
           || (GetAsyncKeyState(0x02) & 0x8000) != 0
           || (GetAsyncKeyState(0x04) & 0x8000) != 0;

    private static InputUnion CreateMouseInput(uint flags)
        => new() { Type = 0, Mouse = new MouseInput { Flags = flags } };

    private static InputUnion CreateKeyboardInput(ushort key, bool up)
        => new() { Type = 1, Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } };

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct InputUnion
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, InputUnion[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
