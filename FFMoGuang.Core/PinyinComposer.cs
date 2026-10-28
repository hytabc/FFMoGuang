using System.Text;

namespace FFMoGuang.Core;

public sealed record ImeSnapshot(bool Available, bool Chinese, string Composition, string[] Candidates);

/// <summary>One Han character at a time. Selects an exact candidate, never submits an unverified conversion.</summary>
public sealed class PinyinComposer(KeyboardTextInput keys, Func<ImeSnapshot> inspect, InputTiming timing)
{
    private enum Phase { Letters, Candidates, Commit }
    private Phase phase;
    private string letters = "";
    private string prefix = "";
    private string character = "";
    private int letterIndex;
    private int pages;
    private string previousPage = "";
    private long started;
    private long nextAt;
    private long commitAt;
    private AnswerOptions options = new();
    public bool Active { get; private set; }
    public string Error { get; private set; } = "";
    public string Status => Error.Length != 0 ? Error : Active
        ? $"全拼：{character} → {letters}，已按 {letterIndex}/{letters.Length} 个字母，候选翻页 {pages}/10"
        : "搜狗全拼待命；需要中文状态及可读取的候选列表";

    public OperationResult Begin(Rune target, string inputPrefix, long now, AnswerOptions settings)
    {
        if (Active || keys.HasHeldKeys) return new(false, "仍有输入法操作或按键尚未结束");
        Error = "";
        if (!PinyinDictionary.TryRead(target, out var reading)) return new(false, $"没有“{target}”的全拼读音，已停止");
        var state = inspect();
        if (!state.Available || !state.Chinese || state.Composition.Length != 0)
            return new(false, "请切换搜狗全拼中文状态，结束已有输入法组合，再重新开始");
        options = settings.Clamp();
        letters = reading; prefix = inputPrefix; character = target.ToString();
        letterIndex = pages = 0; previousPage = "";
        started = nextAt = now; phase = Phase.Letters; Active = true;
        return new(true);
    }

    public void Tick(long now, bool ready, string text)
    {
        if (!Active) return;
        if (!ready) { Fail("拼音输入时焦点、目标或游戏状态变化，已停止；请手动结束输入法组合"); return; }
        if (now - started >= 30000) { Fail("单字拼音输入超过 30 秒，已停止；请检查输入法候选"); return; }
        if (text != prefix && text != prefix + character)
        { Fail("拼音输入期间文本被更改或候选转换不符，已停止"); return; }
        if (keys.HasHeldKeys) return;
        var state = inspect();
        if (!state.Available || !state.Chinese) { Fail("无法读取输入法状态或已切换英文，已停止"); return; }
        if (text == prefix + character)
        {
            if (state.Composition.Length == 0) Active = false;
            return;
        }
        if (now < nextAt) return;
        if (phase == Phase.Letters)
        {
            if (letterIndex < letters.Length)
            {
                if (!Press(LetterScanCode(letters[letterIndex]), now)) return;
                letterIndex++;
                return;
            }
            phase = Phase.Candidates;
            nextAt = now + 200;
            return;
        }
        if (phase == Phase.Candidates)
        {
            var candidate = Array.FindIndex(state.Candidates, value => value == character);
            if (candidate is >= 0 and < 10)
            {
                var scan = candidate == 0 ? (ushort)0x39 : (ushort)(candidate == 9 ? 0x0B : candidate + 2);
                if (!Press(scan, now)) return;
                phase = Phase.Commit;
                commitAt = now;
                return;
            }
            if (state.Candidates.Length == 0) return; // TSF-only/late candidate updates stop at the bounded timeout.
            var fingerprint = string.Join('\0', state.Candidates);
            if (fingerprint == previousPage || pages >= 10)
            { Fail($"当前全拼候选页中未找到“{character}”，已停止；不会猜选或发送"); return; }
            previousPage = fingerprint;
            pages++;
            if (Press(0x51, now, true)) nextAt += 200;
            return;
        }
        if (now - commitAt >= 2000) Fail("选择拼音候选后未读到预期汉字，已停止");
    }

    private bool Press(ushort scan, long now, bool extended = false)
    {
        var hold = timing.Hold(options);
        var result = keys.PressScanCode(scan, now, hold, extended);
        if (!result.Success) { Fail(result.Error); return false; }
        nextAt = now + hold + timing.Gap(options);
        return true;
    }
    public void Cancel() { Active = false; Error = ""; }
    private void Fail(string error) { Error = error; Active = false; }
    public static ushort LetterScanCode(char letter) => letter switch
    {
        'a' => 0x1E, 'b' => 0x30, 'c' => 0x2E, 'd' => 0x20, 'e' => 0x12, 'f' => 0x21,
        'g' => 0x22, 'h' => 0x23, 'i' => 0x17, 'j' => 0x24, 'k' => 0x25, 'l' => 0x26,
        'm' => 0x32, 'n' => 0x31, 'o' => 0x18, 'p' => 0x19, 'q' => 0x10, 'r' => 0x13,
        's' => 0x1F, 't' => 0x14, 'u' => 0x16, 'v' => 0x2F, 'w' => 0x11, 'x' => 0x2D,
        'y' => 0x15, 'z' => 0x2C, _ => throw new ArgumentOutOfRangeException(nameof(letter)),
    };
}
