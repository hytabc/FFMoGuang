using System.Text;

namespace FFMoGuang.Core;

public sealed record WordTarget(string Key, string Text);
public sealed record AnswerFrame(bool Ready, string Session, string Input, IReadOnlyList<WordTarget> Targets,
    string Reason = "", bool KeyboardReady = true, bool Composing = false);
public sealed record OperationResult(bool Success, string Error = "");

public interface IAnswerPort
{
    AnswerFrame Read();
    OperationResult Fill(string session, WordTarget target);
    OperationResult Submit(string session, WordTarget target);
    void ClearOwnedInput(string session, string text);
    OperationResult TypeCharacter(string session, WordTarget target, string prefix, string character)
        => new(false, "当前输入接口不支持键盘逐字输入");
    OperationResult PressEnter(string session, WordTarget target)
        => new(false, "当前输入接口不支持模拟 Enter");
}

public sealed record AnswerOptions(int StableMs = 150, int FillDelayMs = 100,
    int IntervalMs = 650, int AcknowledgeTimeoutMs = 2000, int MaxAttemptsPerWord = 5,
    bool ManualSubmit = false, bool KeyboardInput = false, int CharacterDelayMs = 40,
    bool PinyinInput = false, bool RandomizeDelays = false, int RandomMinMs = 60, int RandomMaxMs = 160,
    bool RandomizeSubmitDelay = false, int SubmitDelayMinMs = 100, int SubmitDelayMaxMs = 300)
{
    public AnswerOptions Clamp() => new(Math.Clamp(StableMs, 50, 2000), Math.Clamp(FillDelayMs, 0, 60000),
        Math.Clamp(IntervalMs, 0, 60000), Math.Clamp(AcknowledgeTimeoutMs, 500, 10000),
        Math.Clamp(MaxAttemptsPerWord, 1, 100), ManualSubmit, KeyboardInput || PinyinInput, Math.Clamp(CharacterDelayMs, 0, 1000),
        PinyinInput, RandomizeDelays, Math.Clamp(RandomMinMs, 20, 1000),
        Math.Clamp(Math.Max(RandomMinMs, RandomMaxMs), 20, 1000), RandomizeSubmitDelay,
        Math.Clamp(SubmitDelayMinMs, 0, 60000), Math.Clamp(Math.Max(SubmitDelayMinMs, SubmitDelayMaxMs), 0, 60000));
}

/// <summary>Framework-independent controller. All times are monotonic milliseconds.</summary>
public sealed class AnswerController(IAnswerPort port, InputTiming? inputTiming = null)
{
    private readonly InputTiming timing = inputTiming ?? new();
    private enum Phase { Choosing, Typing, Filled, AwaitingClear, Cooldown }
    private Phase phase;
    private string session = "";
    private WordTarget? pending;
    private long since;
    private readonly Dictionary<WordTarget, int> attempts = [];
    private AnswerOptions options = new();
    private bool enterDown;
    private long? manualEnterAt;
    private string typedPrefix = "";
    private string? awaitingPrefix;
    private long nextCharacterAt;

    public bool Running { get; private set; }
    public string Status { get; private set; } = "已停止";
    public string LastWord { get; private set; } = "";
    public int Submissions { get; private set; }
    public int ManualSubmissions { get; private set; }
    public int? PendingSubmitDelayMs { get; private set; }
    public event Action<string>? ManualSubmissionObserved;
    public event Action<string>? KeyboardSubmissionObserved;

    // Observe every Framework frame, even between answer ticks. Never consume the game's Enter key.
    public void ObserveEnterKey(bool down, long now)
    {
        if (down && !enterDown && Running && options.ManualSubmit && phase == Phase.Filled)
            manualEnterAt ??= now;
        enterDown = down;
    }

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
        manualEnterAt = null;
        typedPrefix = "";
        awaitingPrefix = null;
        since = now;
        phase = Phase.Choosing;
        Running = true;
        Submissions = 0;
        ManualSubmissions = 0;
        LastWord = "";
        PendingSubmitDelayMs = null;
        Status = "等待可见目标";
        return true;
    }

    public void Stop(string reason = "已停止")
    {
        Running = false;
        pending = null;
        manualEnterAt = null;
        awaitingPrefix = null;
        PendingSubmitDelayMs = null;
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
                if (options.KeyboardInput && pending != null)
                {
                    Status = "已观察到模拟 Enter 后输入清空，等待下一次输入";
                    KeyboardSubmissionObserved?.Invoke(pending.Text);
                }
            }
            else if (pending == null || frame.Input != pending.Text) Stop("输入被手动更改，已停止");
            else if (now - since >= options.AcknowledgeTimeoutMs) Stop("提交后输入未清空，已停止；请检查提交机制");
            return;
        }

        if (phase == Phase.Typing)
        {
            if (pending == null || !live.Contains(pending)) { Stop("逐字输入期间目标已变化，已停止；请检查残留文字"); return; }
            if (!frame.KeyboardReady) { Stop("逐字输入期间游戏输入框失去焦点，已停止"); return; }
            if (awaitingPrefix != null)
            {
                if (frame.Composing)
                {
                    if (now - since >= 32000) Stop("拼音组合未在时限内结束，已停止");
                    return;
                }
                if (frame.Input == awaitingPrefix)
                {
                    typedPrefix = awaitingPrefix;
                    awaitingPrefix = null;
                }
                else if ((frame.Composing || frame.Input == typedPrefix)
                    && now - since < (options.PinyinInput ? 32000 : options.AcknowledgeTimeoutMs)) return;
                else { Stop("逐字输入未得到预期文字，已停止；请检查输入法、焦点或人工修改"); return; }
            }
            if (frame.Input != typedPrefix) { Stop("逐字输入内容被更改，已停止"); return; }
            if (typedPrefix == pending.Text)
            {
                phase = Phase.Filled;
                since = now;
                PendingSubmitDelayMs = options.ManualSubmit ? null : timing.SubmitDelay(options);
                Status = options.ManualSubmit ? "逐字输入完成，请按 Enter 提交：" + pending.Text : "逐字输入完成，等待提交";
                return;
            }
            if (now < nextCharacterAt) return;
            if (!Rune.TryGetRuneAt(pending.Text, typedPrefix.Length, out var character))
            { Stop("词条包含无效 Unicode，已停止"); return; }
            var input = port.TypeCharacter(session, pending, typedPrefix, character.ToString());
            if (!input.Success) { Stop(input.Error); return; }
            awaitingPrefix = typedPrefix + character;
            since = now;
            nextCharacterAt = now + timing.Gap(options);
            Status = $"正在逐字输入：{awaitingPrefix}";
            return;
        }

        if (phase == Phase.Filled)
        {
            if (pending == null) { Stop("内部目标状态无效"); return; }
            if (options.ManualSubmit && frame.Input.Length == 0 && manualEnterAt.HasValue)
            {
                // An Enter edge plus cleared input is an observation, not a server acknowledgement.
                var text = pending.Text;
                attempts[pending] = attempts.GetValueOrDefault(pending) + 1;
                ManualSubmissions++;
                LastWord = text;
                manualEnterAt = null;
                pending = null;
                phase = Phase.Cooldown;
                since = now;
                Status = "已观察到手动 Enter 后输入清空，等待下一次填入";
                ManualSubmissionObserved?.Invoke(text);
                return;
            }
            if (frame.Input != pending.Text) { Stop("填入内容已变化，已停止"); return; }
            if (options.KeyboardInput && !frame.KeyboardReady) { Stop("游戏输入框失去焦点，已停止"); return; }
            if (!live.Contains(pending))
            {
                if (options.KeyboardInput) { Stop("逐字输入的目标已消失，已停止；请检查残留文字"); return; }
                port.ClearOwnedInput(session, pending.Text);
                pending = null;
                manualEnterAt = null;
                PendingSubmitDelayMs = null;
                phase = Phase.Choosing;
                Status = "目标已消失，重新选择";
                return;
            }
            if (options.ManualSubmit)
            {
                if (manualEnterAt.HasValue && now - manualEnterAt.Value >= options.AcknowledgeTimeoutMs)
                    Stop("按下 Enter 后输入未清空，已停止；请检查输入框焦点与提交结果");
                else Status = manualEnterAt.HasValue ? "已检测到 Enter，等待输入清空" : "已填入，请在游戏输入框按 Enter 提交：" + pending.Text;
                return;
            }
            if (now - since < PendingSubmitDelayMs.GetValueOrDefault()) return;
            var result = options.KeyboardInput ? port.PressEnter(session, pending) : port.Submit(session, pending);
            if (!result.Success) { Stop(result.Error); return; }
            attempts[pending] = attempts.GetValueOrDefault(pending) + 1;
            Submissions++;
            PendingSubmitDelayMs = null;
            LastWord = pending.Text;
            phase = Phase.AwaitingClear;
            since = now;
            Status = options.KeyboardInput ? "已模拟 Enter，等待输入清空" : "已调用提交，等待输入清空";
            return;
        }

        if (frame.Input.Length != 0) { Stop("输入框中存在手动输入，已停止"); return; }
        if (phase == Phase.Cooldown)
        {
            if (now - since < options.IntervalMs) return;
            phase = Phase.Choosing;
            pending = null;
        }

        if (options.ManualSubmit && enterDown)
        {
            pending = null;
            Status = "请先松开 Enter，再填入下一条";
            return;
        }

        if (options.KeyboardInput && !frame.KeyboardReady)
        {
            pending = null;
            Status = "等待游戏输入框焦点：请点击魔光键影输入栏，并松开修饰键及 Enter";
            return;
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
        if (options.KeyboardInput)
        {
            typedPrefix = "";
            awaitingPrefix = null;
            manualEnterAt = null;
            phase = Phase.Typing;
            nextCharacterAt = now;
            since = now;
            Status = "准备逐字输入：" + next.Text;
            return;
        }
        var fill = port.Fill(session, next);
        if (!fill.Success) { Stop(fill.Error); return; }
        phase = Phase.Filled;
        manualEnterAt = null;
        since = now;
        PendingSubmitDelayMs = options.ManualSubmit ? null : timing.SubmitDelay(options);
        Status = options.ManualSubmit ? "已填入，请在游戏输入框按 Enter 提交：" + next.Text : "已填入：" + next.Text;
    }

    public static bool IsValid(WordTarget target) => !string.IsNullOrWhiteSpace(target.Text)
        && Encoding.UTF8.GetByteCount(target.Text) <= 512
        && !target.Text.Any(char.IsControl);
}
