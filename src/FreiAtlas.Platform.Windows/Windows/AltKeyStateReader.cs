using System.Runtime.InteropServices;

namespace FreiAtlas.Platform.Windows.Windows;

public static class AltKeyStateReader
{
    private const int VirtualKeyMenu = 0x12;
    private const short KeyDownMask = unchecked((short)0x8000);

    public static bool IsDown()
        => IsDown(GetAsyncKeyState(VirtualKeyMenu));

    internal static bool IsDown(short state)
        => (state & KeyDownMask) != 0;

    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
