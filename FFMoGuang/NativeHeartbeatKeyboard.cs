using System.Runtime.InteropServices;
using FFMoGuang.Core;

namespace FFMoGuang;

public sealed class NativeHeartbeatKeyboard(Func<bool> ready) : IHeartbeatKeyboard
{
    public bool CanContinue() => ready();
    public bool CanPress() => ready() && new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }
        .All(key => (GetAsyncKeyState(key) & 0x8000) == 0);
    public bool Press() => CanPress() && Send(false);
    public bool Release() => Send(true);

    private static bool Send(bool release)
    {
        var inputs = new[] { new KeyboardInput
        {
            Type = 1, VirtualKey = 0xA0, ScanCode = 0x2A,
            Flags = release ? 2u : 0u, ExtraInfo = 0x4D474842,
        } };
        return SendInput(1, inputs, Marshal.SizeOf<KeyboardInput>()) == 1;
    }

    // Win64 INPUT is 40 bytes; KEYBDINPUT starts at offset 8 in its union.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort ScanCode;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nuint ExtraInfo;
    }
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] KeyboardInput[] inputs, int size);
}
