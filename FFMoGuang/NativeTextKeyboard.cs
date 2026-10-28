using System.Runtime.InteropServices;
using FFMoGuang.Core;

namespace FFMoGuang;

public sealed class NativeTextKeyboard : IKeyboardInputSink
{
    public static bool ModifiersReleased() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x0D }
        .All(key => (GetAsyncKeyState(key) & 0x8000) == 0);

    public int Send(KeyboardStroke[] strokes)
    {
        var inputs = strokes.Select(stroke => new NativeHeartbeatKeyboard.KeyboardInput
        {
            Type = 1, VirtualKey = stroke.VirtualKey, ScanCode = stroke.ScanCode,
            Flags = stroke.Flags, ExtraInfo = 0x4D475459,
        }).ToArray();
        return (int)SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeHeartbeatKeyboard.KeyboardInput>());
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] NativeHeartbeatKeyboard.KeyboardInput[] inputs, int size);
}
