namespace FFMoGuang.Core;

public sealed record HotkeyOptions
{
    public int StartKey { get; init; } = 0x78; // F9
    public int StopKey { get; init; } = 0x77; // F8

    public static bool IsSupported(int key) => key is 0x08 or 0x09 or 0x0D or 0x1B
        or >= 0x20 and <= 0x28 or 0x2D or 0x2E
        or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A
        or >= 0x60 and <= 0x87 or 0x90 or 0x91
        or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;

    public bool IsValid => IsSupported(StartKey) && IsSupported(StopKey) && StartKey != StopKey;
    public HotkeyOptions Validate() => IsValid ? this : new();
}

public enum HotkeyAction { None, Start, Stop }

public sealed class HotkeyTrigger
{
    private bool? startWasDown;

    public void Reset() => startWasDown = null;

    public HotkeyAction Poll(bool startDown, bool stopDown, bool canStart)
    {
        var pressed = startWasDown == false && startDown;
        startWasDown = startDown;
        if (stopDown) return HotkeyAction.Stop;
        return pressed && canStart ? HotkeyAction.Start : HotkeyAction.None;
    }
}
