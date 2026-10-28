using FFMoGuang.Core;
using FFMoGuang;
using System.Text.Json;

// No game/Dalamud dependency. Failure exits nonzero, suitable for CI and a clean SDK installation.
var cases = new (string Name, Action Test)[]
{
    ("exact Chinese, Unicode and punctuation reach submit", () =>
    {
        var (port, engine) = Create("艾米利欧·测试 ！𠮷 ");
        AdvanceToSubmit(engine);
        Equal("艾米利欧·测试 ！𠮷 ", port.Submitted.Single().Text);
        Equal(1, engine.Submissions);
    }),
    ("nonempty input prevents start", () =>
    {
        var port = new FakePort { Input = "我的输入" };
        var engine = new AnswerController(port);
        Equal(false, engine.Start(0, new()));
        engine.Tick(1000);
        Equal(0, port.Writes);
    }),
    ("unavailable UI prevents start", () =>
    {
        var port = new FakePort { Ready = false };
        Equal(false, new AnswerController(port).Start(0, new()));
    }),
    ("unstable word is not filled", () =>
    {
        var (port, engine) = Create();
        engine.Tick(0);
        port.Targets = [new("slot1", "新词条")];
        engine.Tick(150);
        Equal(0, port.Writes);
        engine.Tick(300);
        Equal("新词条", port.Input);
    }),
    ("target disappearing after fill clears only owned input", () =>
    {
        var (port, engine) = Create();
        engine.Tick(0); engine.Tick(150);
        port.Targets = [];
        engine.Tick(250);
        Equal("", port.Input);
        Equal(0, port.Submitted.Count);
        Equal(true, engine.Running);
    }),
    ("replacement text in same slot is not submitted as previous word", () =>
    {
        var (port, engine) = Create("旧词");
        engine.Tick(0); engine.Tick(150);
        port.Targets = [new("slot1", "新词")];
        engine.Tick(250);
        Equal(0, port.Submitted.Count);
        Equal("", port.Input);
        engine.Tick(300); engine.Tick(450); engine.Tick(550);
        Equal("新词", port.Submitted.Single().Text);
    }),
    ("manual edits stop without clearing user's text", () =>
    {
        var (port, engine) = Create();
        engine.Tick(0); engine.Tick(150);
        port.Input = "手动更改";
        engine.Tick(250);
        Equal(false, engine.Running);
        Equal("手动更改", port.Input);
        Equal(0, port.Submitted.Count);
    }),
    ("session recreation stops without stale submission", () =>
    {
        var (port, engine) = Create();
        engine.Tick(0); engine.Tick(150);
        port.Session = "replacement-addon";
        engine.Tick(250);
        Equal(false, engine.Running);
        Equal(0, port.Submitted.Count);
    }),
    ("closing UI stops immediately", () =>
    {
        var (port, engine) = Create();
        engine.Tick(0); engine.Tick(150);
        port.Ready = false;
        engine.Tick(250);
        Equal(false, engine.Running);
        Equal(0, port.Submitted.Count);
    }),
    ("missing acknowledgement times out without repeated Enter", () =>
    {
        var (port, engine) = Create();
        port.ClearOnSubmit = false;
        AdvanceToSubmit(engine);
        engine.Tick(1000); engine.Tick(2250); engine.Tick(5000);
        Equal(false, engine.Running);
        Equal(1, port.Submitted.Count);
    }),
    ("manual edits while awaiting clear stop", () =>
    {
        var (port, engine) = Create();
        port.ClearOnSubmit = false;
        AdvanceToSubmit(engine);
        port.Input = "人工输入";
        engine.Tick(300);
        Equal(false, engine.Running);
        Equal("人工输入", port.Input);
    }),
    ("same visible word can receive multiple attacks after clear", () =>
    {
        var (port, engine) = Create();
        AdvanceToSubmit(engine);
        engine.Tick(300); engine.Tick(950); engine.Tick(1100); engine.Tick(1200);
        Equal(2, port.Submitted.Count);
        Equal(port.Submitted[0], port.Submitted[1]);
    }),
    ("no duplicate submission before cooldown expires", () =>
    {
        var (port, engine) = Create();
        AdvanceToSubmit(engine);
        engine.Tick(300); engine.Tick(400); engine.Tick(800); engine.Tick(949);
        Equal(1, port.Submitted.Count);
        Equal(1, port.Writes);
    }),
    ("unchanged target reaches configured limit and stops", () =>
    {
        var port = new FakePort();
        var engine = new AnswerController(port);
        engine.Start(0, new(MaxAttemptsPerWord: 1));
        AdvanceToSubmit(engine);
        engine.Tick(300); engine.Tick(950);
        Equal(false, engine.Running);
        Equal(1, port.Submitted.Count);
    }),
    ("same word in distinct slots is handled separately", () =>
    {
        var (port, engine) = Create();
        port.Targets = [new("slot1", "巴雷特"), new("slot2", "巴雷特")];
        AdvanceToSubmit(engine);
        engine.Tick(300); engine.Tick(950); engine.Tick(1100); engine.Tick(1200);
        Equal("slot1", port.Submitted[0].Key);
        Equal("slot2", port.Submitted[1].Key);
    }),
    ("word leaving and returning resets its attack limit", () =>
    {
        var port = new FakePort();
        var engine = new AnswerController(port);
        engine.Start(0, new(MaxAttemptsPerWord: 1));
        AdvanceToSubmit(engine);
        port.Targets = [];
        engine.Tick(300); engine.Tick(950);
        port.Targets = [new("slot1", "巴雷特")];
        engine.Tick(1000); engine.Tick(1150); engine.Tick(1250);
        Equal(2, port.Submitted.Count);
    }),
    ("empty wave waits and resumes when a target appears", () =>
    {
        var (port, engine) = Create();
        port.Targets = [];
        engine.Tick(0); engine.Tick(3000);
        Equal(true, engine.Running);
        Equal(0, port.Writes);
        port.Targets = [new("slot1", "萨菲罗斯")];
        engine.Tick(3100); engine.Tick(3250); engine.Tick(3350);
        Equal("萨菲罗斯", port.Submitted.Single().Text);
    }),
    ("truncated or rejected fill stops without submit", () =>
    {
        var (port, engine) = Create();
        port.RejectFill = true;
        AdvanceToSubmit(engine);
        Equal(false, engine.Running);
        Equal(0, port.Submitted.Count);
    }),
    ("submit failure is not counted as a submission", () =>
    {
        var (port, engine) = Create();
        port.RejectSubmit = true;
        AdvanceToSubmit(engine);
        Equal(false, engine.Running);
        Equal(0, engine.Submissions);
    }),
    ("stop cancels pending submit and restart respects nonempty input", () =>
    {
        var (port, engine) = Create();
        engine.Tick(0); engine.Tick(150);
        engine.Stop();
        engine.Tick(250);
        Equal(0, port.Submitted.Count);
        Equal(false, engine.Start(300, new()));
    }),
    ("control characters and overlong payloads are excluded", () =>
    {
        foreach (var text in new[] { "", "  ", "巴\r雷特", "巴\n雷特", "巴\0雷特", new string('巴', 171) })
            Equal(false, AnswerController.IsValid(new("slot", text)));
        Equal(true, AnswerController.IsValid(new("slot", " 艾米利欧·测试！ ")));
    }),
    ("custom millisecond operation delay gates submit", () =>
    {
        var port = new FakePort();
        var engine = new AnswerController(port);
        engine.Start(0, new(FillDelayMs: 1234));
        engine.Tick(0); engine.Tick(150);
        engine.Tick(1383);
        Equal(0, port.Submitted.Count);
        engine.Tick(1384);
        Equal(1, port.Submitted.Count);
    }),
    ("custom millisecond answer interval gates next fill", () =>
    {
        var port = new FakePort();
        var engine = new AnswerController(port);
        engine.Start(0, new(IntervalMs: 2345));
        AdvanceToSubmit(engine);
        engine.Tick(300); engine.Tick(2644);
        Equal(1, port.Writes);
        engine.Tick(2645); engine.Tick(2795);
        Equal(2, port.Writes);
    }),
    ("zero delay still separates fill and submit into different ticks", () =>
    {
        var port = new FakePort();
        var engine = new AnswerController(port);
        engine.Start(0, new(FillDelayMs: 0, IntervalMs: 0));
        engine.Tick(0); engine.Tick(150);
        Equal(0, port.Submitted.Count);
        engine.Tick(151);
        Equal(1, port.Submitted.Count);
    }),
    ("user delays are bounded to zero through sixty seconds", () =>
    {
        var options = new AnswerOptions(FillDelayMs: -5, IntervalMs: int.MaxValue).Clamp();
        Equal(0, options.FillDelayMs);
        Equal(60000, options.IntervalMs);
    }),
    ("unbound configuration reports missing bindings instead of area mismatch", () =>
    {
        var problem = new BindingProfile("", 0, null, []).GetProblem(1359, KeyboundProfile.SupportedVersion)!;
        Equal(true, problem.Contains("尚未完成绑定"));
        Equal(false, problem.Contains("不一致"));
    }),
    ("real first-wave export binds input and all twenty slots", () =>
    {
        var dump = ReadFixture("keybound-wave1.json");
        Equal(true, KeyboundProfile.TryCreate(dump.GameVersion, dump.Territory, dump.Nodes, out var profile, out _));
        Equal("KTGInput[1]/7", profile!.Input!.Key);
        Equal(20, profile.Words.Count);
        Equal("KTGEWord[1]/2/20", profile.Words[0].Key);
        Equal("KTGEWord[1]/20019/20", profile.Words[^1].Key);
        Equal<string?>(null, profile.GetProblem(1359, dump.GameVersion));
        Equal(true, dump.Nodes.Count(n => n.Binding.Addon == "KTGEWord" && !n.Visible) > 0);
    }),
    ("real later-wave export uses identical bindings despite changed enemies", () =>
    {
        var first = ReadFixture("keybound-wave1.json");
        var second = ReadFixture("keybound-wave2.json");
        KeyboundProfile.TryCreate(first.GameVersion, first.Territory, first.Nodes, out var p1, out _);
        Equal(true, KeyboundProfile.TryCreate(second.GameVersion, second.Territory, second.Nodes, out var p2, out _));
        Equal(string.Join('|', p1!.Words.Select(w => w.Key)), string.Join('|', p2!.Words.Select(w => w.Key)));
        Equal(true, second.Nodes.Any(n => n.Visible && n.Text == "伊弗利特"));
    }),
    ("genuine territory mismatch includes both IDs", () =>
    {
        var dump = ReadFixture("keybound-wave1.json");
        KeyboundProfile.TryCreate(dump.GameVersion, dump.Territory, dump.Nodes, out var profile, out _);
        var problem = profile!.GetProblem(144, dump.GameVersion)!;
        Equal(true, problem.Contains("144"));
        Equal(true, problem.Contains("1359"));
    }),
    ("automatic binding rejects wrong territory and unknown game versions", () =>
    {
        var dump = ReadFixture("keybound-wave1.json");
        Equal(false, KeyboundProfile.TryCreate(dump.GameVersion, 144, dump.Nodes, out var profile, out _));
        Equal<BindingProfile?>(null, profile);
        Equal(false, KeyboundProfile.TryCreate("future-version", 1359, dump.Nodes, out profile, out _));
        Equal<BindingProfile?>(null, profile);
    }),
    ("automatic binding requires visible input and valid callback", () =>
    {
        foreach (var variant in new[] { "hidden", "no-callback", "missing" })
        {
            var dump = ReadFixture("keybound-wave1.json");
            var input = dump.Nodes.Single(n => n.IsInput);
            dump.Nodes.Remove(input);
            if (variant != "missing") dump.Nodes.Add(input with
            {
                Visible = variant != "hidden", HasCallback = variant != "no-callback",
            });
            Equal(false, KeyboundProfile.TryCreate(dump.GameVersion, 1359, dump.Nodes, out _, out _));
        }
    }),
    ("automatic binding refuses incomplete or ambiguous word layout", () =>
    {
        var dump = ReadFixture("keybound-wave1.json");
        var hidden = dump.Nodes.First(n => n.Binding.Addon == "KTGEWord" && !n.Visible);
        dump.Nodes.Remove(hidden);
        Equal(false, KeyboundProfile.TryCreate(dump.GameVersion, 1359, dump.Nodes, out _, out _));
        dump.Nodes.Add(hidden); dump.Nodes.Add(hidden);
        Equal(false, KeyboundProfile.TryCreate(dump.GameVersion, 1359, dump.Nodes, out _, out _));
    }),
    ("automatic binding waits for actual visible enemies", () =>
    {
        var dump = ReadFixture("keybound-wave1.json");
        var nodes = dump.Nodes.Select(n => n.IsInput ? n : n with { Visible = false }).ToArray();
        Equal(false, KeyboundProfile.TryCreate(dump.GameVersion, 1359, nodes, out _, out _));
    }),
};

var failures = 0;
foreach (var test in cases)
{
    try { test.Test(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} passed");
return failures == 0 ? 0 : 1;

static (FakePort Port, AnswerController Engine) Create(string text = "巴雷特")
{
    var port = new FakePort { Targets = [new("slot1", text)] };
    var engine = new AnswerController(port);
    Equal(true, engine.Start(0, new()));
    return (port, engine);
}

static void AdvanceToSubmit(AnswerController engine) { engine.Tick(0); engine.Tick(150); engine.Tick(250); }
static ExportFixture ReadFixture(string name) => JsonSerializer.Deserialize<ExportFixture>(
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)))!;
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'");
}

sealed record ExportFixture(string GameVersion, uint Territory, List<InspectedNode> Nodes);

sealed class FakePort : IAnswerPort
{
    public string Session = "session";
    public bool Ready = true;
    public string Input = "";
    public List<WordTarget> Targets = [new("slot1", "巴雷特")];
    public List<WordTarget> Submitted = [];
    public int Writes;
    public bool ClearOnSubmit = true;
    public bool RejectFill;
    public bool RejectSubmit;
    public AnswerFrame Read() => new(Ready, Session, Input, Targets, "UI unavailable");
    public OperationResult Fill(string session, WordTarget target)
    {
        if (RejectFill) return new(false, "text truncated");
        Input = target.Text;
        Writes++;
        return new(true);
    }
    public OperationResult Submit(string session, WordTarget target)
    {
        if (RejectSubmit) return new(false, "callback unavailable");
        Submitted.Add(target);
        if (ClearOnSubmit) Input = "";
        return new(true);
    }
    public void ClearOwnedInput(string session, string text)
    {
        if (Session == session && Input == text) Input = "";
    }
}
