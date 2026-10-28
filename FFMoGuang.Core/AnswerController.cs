using System.Text;

namespace FFMoGuang.Core;

public sealed record WordTarget(string Key, string Text);
public sealed record AnswerFrame(bool Ready, string Session, string Input, IReadOnlyList<WordTarget> Targets, string Reason = "");
public sealed record OperationResult(bool Success, string Error = "");

public interface IAnswerPort
{
    AnswerFrame Read();
    OperationResult Fill(string session, WordTarget target);
    OperationResult Submit(string session, WordTarget target);
    void ClearOwnedInput(string session, string text);
}

public sealed record AnswerOptions(int StableMs = 150, int FillDelayMs = 100,
    int IntervalMs = 650, int AcknowledgeTimeoutMs = 2000, int MaxAttemptsPerWord = 5)
{
    public AnswerOptions Clamp() => new(Math.Clamp(StableMs, 50, 2000), Math.Clamp(FillDelayMs, 0, 60000),
        Math.Clamp(IntervalMs, 0, 60000), Math.Clamp(AcknowledgeTimeoutMs, 500, 10000),
        Math.Clamp(MaxAttemptsPerWord, 1, 100));
}

/// <summary>Framework-independent controller. All times are monotonic milliseconds.</summary>
public sealed class AnswerController(IAnswerPort port)
{
    private enum Phase { Choosing, Filled, AwaitingClear, Cooldown }
    private Phase phase;
    private string session = "";
    private WordTarget? pending;
    private long since;
    private readonly Dictionary<WordTarget, int> attempts = [];
    private AnswerOptions options = new();

    public bool Running { get; private set; }
    public string Status { get; private set; } = "已停止";
    public string LastWord { get; private set; } = "";
    public int Submissions { get; private set; }

    public bool Start(long now, AnswerOptions settings)
    {
        if (Running) return true;
        var frame = port.Read();
        if (!frame.Ready || string.IsNullOrEmpty(frame.Session))
        {
            Status = "不能启动：" + frame.Reason;
            return false;
        }
        if (frame.Input.Length != 0)
        {
            Status = "不能启动：请先清空游戏输入框";
            return false;
        }
        options = settings.Clamp();
        session = frame.Session;
        attempts.Clear();
        pending = null;
        since = now;
        phase = Phase.Choosing;
        Running = true;
        Submissions = 0;
        LastWord = "";
        Status = "等待可见目标";
        return true;
    }

    public void Stop(string reason = "已停止")
    {
        Running = false;
        pending = null;
        Status = reason;
    }

    public void Tick(long now)
    {
        if (!Running) return;
        var frame = port.Read();
        if (!frame.Ready) { Stop(frame.Reason); return; }
        if (frame.Session != session) { Stop("游戏界面已重建，请重新启动"); return; }
        var live = frame.Targets.Where(IsValid).Distinct().ToHashSet();
        foreach (var target in attempts.Keys.Where(t => !live.Contains(t)).ToArray()) attempts.Remove(target);

        if (phase == Phase.AwaitingClear)
        {
            if (frame.Input.Length == 0)
            {
                phase = Phase.Cooldown;
                since = now;
                Status = "提交回调已处理，等待下一次输入";
            }
            else if (pending == null || frame.Input != pending.Text) Stop("输入被手动更改，已停止");
            else if (now - since >= options.AcknowledgeTimeoutMs) Stop("提交后输入未清空，已停止；请检查提交机制");
            return;
        }

        if (phase == Phase.Filled)
        {
            if (pending == null) { Stop("内部目标状态无效"); return; }
            if (frame.Input != pending.Text) { Stop("填入内容已变化，已停止"); return; }
            if (!live.Contains(pending))
            {
                port.ClearOwnedInput(session, pending.Text);
                pending = null;
                phase = Phase.Choosing;
                Status = "目标已消失，重新选择";
                return;
            }
            if (now - since < options.FillDelayMs) return;
            var result = port.Submit(session, pending);
            if (!result.Success) { Stop(result.Error); return; }
            attempts[pending] = attempts.GetValueOrDefault(pending) + 1;
            Submissions++;
            LastWord = pending.Text;
            phase = Phase.AwaitingClear;
            since = now;
            Status = "已调用提交，等待输入清空";
            return;
        }

        if (frame.Input.Length != 0) { Stop("输入框中存在手动输入，已停止"); return; }
        if (phase == Phase.Cooldown)
        {
            if (now - since < options.IntervalMs) return;
            phase = Phase.Choosing;
            pending = null;
        }

        // Distribute attacks across visible targets; prefer shorter words at equal attempt counts.
        var next = live.Where(t => attempts.GetValueOrDefault(t) < options.MaxAttemptsPerWord)
            .OrderBy(t => attempts.GetValueOrDefault(t)).ThenBy(t => t.Text.EnumerateRunes().Count())
            .ThenBy(t => t.Key, StringComparer.Ordinal).FirstOrDefault();
        if (next == null)
        {
            pending = null;
            if (live.Count > 0) Stop("可见目标均达到连续提交上限，请检查词条或调整上限");
            else Status = "等待可见目标";
            return;
        }
        if (next != pending)
        {
            pending = next;
            since = now;
            Status = "等待词条稳定：" + next.Text;
            return;
        }
        if (now - since < options.StableMs) return;
        var fill = port.Fill(session, next);
        if (!fill.Success) { Stop(fill.Error); return; }
        phase = Phase.Filled;
        since = now;
        Status = "已填入：" + next.Text;
    }

    public static bool IsValid(WordTarget target) => !string.IsNullOrWhiteSpace(target.Text)
        && Encoding.UTF8.GetByteCount(target.Text) <= 512
        && !target.Text.Any(char.IsControl);
}
