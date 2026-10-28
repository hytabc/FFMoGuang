using System.Text;

namespace FFMoGuang.Core;

public sealed record KeyboardStroke(ushort VirtualKey, ushort ScanCode, uint Flags);
public interface IKeyboardInputSink
{
    int Send(KeyboardStroke[] strokes);
}

/// <summary>Unicode key pairs and a scan-code Enter held across frames; no text-control writes.</summary>
public sealed class KeyboardTextInput(IKeyboardInputSink sink)
{
    private readonly List<KeyboardStroke> releases = [];
    private long releaseAt;
    public bool HasHeldKeys => releases.Count != 0;

    public OperationResult TypeCharacter(Rune character)
    {
        if (HasHeldKeys) return new(false, "仍有模拟按键等待释放，已停止");
        var strokes = character.ToString().SelectMany(c => new[]
        {
            new KeyboardStroke(0, c, 4), new KeyboardStroke(0, c, 6),
        }).ToArray();
        var accepted = Math.Clamp(sink.Send(strokes), 0, strokes.Length);
        // A partial SendInput may end after a key down. Retain its matching release until accepted.
        if (accepted % 2 != 0) releases.Add(strokes[accepted]);
        if (accepted == strokes.Length) return new(true);
        releaseAt = 0;
        Release();
        return new(false, "系统未完整接受逐字输入，已停止；请检查残留文字");
    }

    public OperationResult PressEnter(long now) => PressScanCode(0x1C, now, 120);

    public OperationResult PressScanCode(ushort scan, long now, int holdMs, bool extended = false)
    {
        if (HasHeldKeys) return new(false, "仍有模拟按键等待释放，已停止");
        var flags = extended ? 9u : 8u;
        if (sink.Send([new(0, scan, flags)]) != 1) return new(false, "系统未接受扫描码按键，已停止");
        releases.Add(new(0, scan, flags | 2));
        releaseAt = now + Math.Clamp(holdMs, 20, 500);
        return new(true);
    }

    public void Tick(long now, bool canContinue)
    {
        if (!canContinue || now >= releaseAt) Release();
    }

    public void Release()
    {
        if (!HasHeldKeys) return;
        var accepted = Math.Clamp(sink.Send(releases.ToArray()), 0, releases.Count);
        releases.RemoveRange(0, accepted);
    }
}
