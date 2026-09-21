using System.Runtime.InteropServices;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Recovery;
using FreiAtlas.Platform.Windows.Native;

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

    public RecoveryInputResult Send(int processId, RecoveryKey key, DateTimeOffset capturedAt)
    {
        var age = DateTimeOffset.UtcNow - capturedAt;
        if (age < TimeSpan.Zero || age > TimeSpan.FromMilliseconds(250) || !IsTargetForeground(processId))
            return new(RecoveryInputState.Paused, "已暂停：游戏失焦或读数过期");
        if (new ushort[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, key.VirtualKey }.Any(code => (GetAsyncKeyState(code) & 0x8000) != 0))
            return new(RecoveryInputState.Paused, "等待手动按键释放");

        var keys = key.Modifiers.Append(key.VirtualKey).ToArray();
        var inputs = keys.Select(code => CreateInput(code, false))
            .Concat(keys.Reverse().Select(code => CreateInput(code, true))).ToArray();
        if (!IsTargetForeground(processId)) return new(RecoveryInputState.Paused, "已暂停：游戏不在前台");
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyboardInput>());
        if (sent == inputs.Length) return new(RecoveryInputState.Sent, "已发送恢复按键：" + key.Gesture);

        var error = Marshal.GetLastWin32Error();
        // 系统可能只接收部分事件；释放本次按下的键，避免留下按住状态。
        var releases = keys.Reverse().Select(code => CreateInput(code, true)).ToArray();
        var released = SendInput((uint)releases.Length, releases, Marshal.SizeOf<KeyboardInput>());
        return new(RecoveryInputState.Failed, $"系统接收 {sent}/{inputs.Length} 个事件，错误码 {error}；释放事件 {released}/{releases.Length}");
    }

    private static KeyboardInput CreateInput(ushort key, bool up)
        => new() { Type = 1, Key = key, Flags = up ? 2u : 0u };

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
