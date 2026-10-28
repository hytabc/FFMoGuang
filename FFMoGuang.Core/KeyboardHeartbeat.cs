namespace FFMoGuang.Core;

public sealed record HeartbeatOptions(bool Enabled = false, int IntervalMs = 30000)
{
    public HeartbeatOptions Clamp() => this with { IntervalMs = Math.Clamp(IntervalMs, 10000, 45000) };
}
public interface IHeartbeatKeyboard
{
    bool CanContinue();
    bool CanPress();
    bool Press();
    bool Release();
}

/// <summary>Nonblocking key hold; must keep ticking after stop so an owned key is released.</summary>
public sealed class KeyboardHeartbeat(IHeartbeatKeyboard keyboard)
{
    private long? nextPress;
    private long releaseAt;
    private bool pressed;
    public int Sent { get; private set; }
    public string Status { get; private set; } = "键盘心跳未开启";
    public void Tick(long now, bool answering, HeartbeatOptions settings)
    {
        var options = settings.Clamp();
        var active = answering && options.Enabled;
        if (pressed)
        {
            if (now >= releaseAt || !active || !keyboard.CanContinue()) Release();
            return;
        }
        if (!active)
        {
            nextPress = null;
            Status = options.Enabled ? "键盘心跳待命（随自动答题运行）" : "键盘心跳未开启";
            return;
        }
        nextPress ??= now + options.IntervalMs;
        if (now < nextPress) return;
        if (!keyboard.CanPress()) { Status = "等待游戏前台与修饰键释放"; return; }
        nextPress = now + options.IntervalMs;
        if (!keyboard.Press()) { Status = "系统未接受键盘心跳，请手动按键"; return; }
        pressed = true;
        releaseAt = now + 120;
        Sent++;
        Status = $"已发送 {Sent} 次左 Shift 心跳；游戏超时是否重置需实测";
    }
    public void Release()
    {
        if (!pressed) return;
        if (keyboard.Release()) pressed = false;
        else Status = "按键释放失败，将重试；请手动松开左 Shift";
    }
}
