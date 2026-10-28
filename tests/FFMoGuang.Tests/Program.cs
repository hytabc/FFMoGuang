using FFMoGuang.Core;
using FFMoGuang;
using System.Text.Json;
using System.Text;

// No game/Dalamud dependency. Failure exits nonzero, suitable for CI and a clean SDK installation.
var cases = new (string Name, Action Test)[]
{
    ("hotkeys preserve defaults and round trip custom keys", () =>
    {
        var defaults = JsonSerializer.Deserialize<HotkeyOptions>("{}")!;
        Equal(0x78, defaults.StartKey);
        Equal(0x77, defaults.StopKey);
        var custom = new HotkeyOptions { StartKey = 0x41, StopKey = 0x7B };
        Equal(custom, JsonSerializer.Deserialize<HotkeyOptions>(JsonSerializer.Serialize(custom))!.Validate());
    }),
    ("hotkeys reject conflicts invalid keys modifiers and mouse buttons", () =>
    {
        foreach (var key in new[] { -1, 0, 1, 0x10, 0x11, 0x12, 0x5B, 0xFF })
            Equal(new HotkeyOptions(), new HotkeyOptions { StartKey = key }.Validate());
        Equal(false, new HotkeyOptions { StartKey = 0x77 }.IsValid);
        Equal(true, new HotkeyOptions { StartKey = 0x41, StopKey = 0x42 }.IsValid);
    }),
    ("start hotkey requires release then fresh press including after rebind", () =>
    {
        var trigger = new HotkeyTrigger();
        Equal(HotkeyAction.None, trigger.Poll(true, false, true));
        Equal(HotkeyAction.None, trigger.Poll(false, false, true));
        Equal(HotkeyAction.Start, trigger.Poll(true, false, true));
        Equal(HotkeyAction.None, trigger.Poll(true, false, true));
        trigger.Reset();
        Equal(HotkeyAction.None, trigger.Poll(true, false, true));
        trigger.Poll(false, false, true);
        Equal(HotkeyAction.Start, trigger.Poll(true, false, true));
    }),
    ("stop hotkey wins and blocked start is not deferred", () =>
    {
        var trigger = new HotkeyTrigger();
        trigger.Poll(false, false, true);
        Equal(HotkeyAction.Stop, trigger.Poll(true, true, true));
        Equal(HotkeyAction.None, trigger.Poll(true, false, true));
        trigger.Poll(false, false, false);
        Equal(HotkeyAction.None, trigger.Poll(true, false, false));
        Equal(HotkeyAction.None, trigger.Poll(true, false, true));
        Equal(HotkeyAction.Stop, trigger.Poll(false, true, false));
        Equal(HotkeyAction.Start, trigger.Poll(true, false, true));
    }),
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
    ("random submit delay is sampled once per word and changes for the next word", () =>
    {
        var calls = 0;
        var timing = new InputTiming((min, max) => ++calls == 1 ? max - 1 : min);
        var port = new FakePort(); var engine = new AnswerController(port, timing);
        engine.Start(0, new(RandomizeSubmitDelay: true, SubmitDelayMinMs: 100, SubmitDelayMaxMs: 300));
        engine.Tick(0); engine.Tick(150);
        Equal(300, engine.PendingSubmitDelayMs!.Value); Equal(1, calls);
        engine.Tick(200); engine.Tick(300); engine.Tick(449);
        Equal(0, port.Submitted.Count); Equal(1, calls);
        engine.Tick(450); Equal(1, port.Submitted.Count); Equal(null, engine.PendingSubmitDelayMs);
        engine.Tick(500); engine.Tick(1150); engine.Tick(1300);
        Equal(100, engine.PendingSubmitDelayMs!.Value); Equal(2, calls);
        engine.Tick(1399); Equal(1, port.Submitted.Count);
        engine.Tick(1400); Equal(2, port.Submitted.Count);
    }),
    ("random submit delay supports inclusive endpoints and equal bounds", () =>
    {
        var upper = new InputTiming((min, max) => max - 1);
        var lower = new InputTiming((min, max) => min);
        Equal(60000, upper.SubmitDelay(new(RandomizeSubmitDelay: true, SubmitDelayMinMs: 0, SubmitDelayMaxMs: 60000)));
        Equal(0, lower.SubmitDelay(new(RandomizeSubmitDelay: true, SubmitDelayMinMs: 0, SubmitDelayMaxMs: 60000)));
        Equal(123, upper.SubmitDelay(new(RandomizeSubmitDelay: true, SubmitDelayMinMs: 123, SubmitDelayMaxMs: 123)));
    }),
    ("keyboard automatic Enter waits for the sampled delay after all text is confirmed", () =>
    {
        var port = new FakePort { Targets = [new("slot1", "中")] };
        var engine = new AnswerController(port, new InputTiming((min, max) => max - 1));
        engine.Start(0, new(KeyboardInput: true, RandomizeSubmitDelay: true, SubmitDelayMinMs: 200, SubmitDelayMaxMs: 400));
        engine.Tick(0); engine.Tick(150); engine.Tick(200);
        Equal(null, engine.PendingSubmitDelayMs);
        engine.Tick(250); Equal(400, engine.PendingSubmitDelayMs!.Value);
        engine.Tick(649); Equal(0, port.EnterRequests);
        engine.Tick(650); Equal(1, port.EnterRequests); Equal(0, port.Submitted.Count);
    }),
    ("manual Enter ignores random submit delay without sampling or auto submission", () =>
    {
        var port = new FakePort();
        var engine = new AnswerController(port, new InputTiming((min, max) => throw new Exception("Unexpected sample")));
        engine.Start(0, new(ManualSubmit: true, RandomizeSubmitDelay: true, SubmitDelayMinMs: 60000, SubmitDelayMaxMs: 60000));
        engine.Tick(0); engine.Tick(150); engine.Tick(120000);
        Equal(null, engine.PendingSubmitDelayMs); Equal(true, engine.Running); Equal(0, port.Submitted.Count);
        engine.ObserveEnterKey(true, 120001); port.Input = ""; engine.Tick(120050);
        Equal(1, engine.ManualSubmissions);
    }),
    ("fixed delay keeps its existing behavior when random submit delay is disabled", () =>
    {
        var port = new FakePort();
        var engine = new AnswerController(port, new InputTiming((min, max) => throw new Exception("Unexpected sample")));
        engine.Start(0, new(FillDelayMs: 100, SubmitDelayMinMs: 60000, SubmitDelayMaxMs: 60000));
        engine.Tick(0); engine.Tick(150); engine.Tick(249); Equal(0, port.Submitted.Count);
        engine.Tick(250); Equal(1, port.Submitted.Count);
    }),
    ("long random waits still revalidate targets and cancel on stop or manual edits", () =>
    {
        foreach (var change in new[] { "stop", "target", "edit" })
        {
            var port = new FakePort(); var engine = new AnswerController(port, new InputTiming((min, max) => min));
            engine.Start(0, new(RandomizeSubmitDelay: true, SubmitDelayMinMs: 60000, SubmitDelayMaxMs: 60000));
            engine.Tick(0); engine.Tick(150);
            if (change == "stop") engine.Stop();
            if (change == "target") port.Targets = [];
            if (change == "edit") port.Input = "我的文字";
            engine.Tick(200); engine.Tick(60150);
            Equal(0, port.Submitted.Count); Equal(null, engine.PendingSubmitDelayMs);
            if (change == "edit") Equal("我的文字", port.Input);
        }
    }),
    ("random submit settings clamp safely and survive serialization with old defaults", () =>
    {
        var reversed = new AnswerOptions(SubmitDelayMinMs: 500, SubmitDelayMaxMs: 100).Clamp();
        Equal(500, reversed.SubmitDelayMinMs); Equal(500, reversed.SubmitDelayMaxMs);
        var extremes = new AnswerOptions(SubmitDelayMinMs: int.MinValue, SubmitDelayMaxMs: int.MaxValue).Clamp();
        Equal(0, extremes.SubmitDelayMinMs); Equal(60000, extremes.SubmitDelayMaxMs);
        var options = JsonSerializer.Deserialize<AnswerOptions>(JsonSerializer.Serialize(
            new AnswerOptions(RandomizeSubmitDelay: true, SubmitDelayMinMs: 123, SubmitDelayMaxMs: 456)))!.Clamp();
        Equal(true, options.RandomizeSubmitDelay); Equal(123, options.SubmitDelayMinMs); Equal(456, options.SubmitDelayMaxMs);
        Equal(false, JsonSerializer.Deserialize<AnswerOptions>("{}")!.RandomizeSubmitDelay);
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
    ("leaderboard distinguishes missing teammates from known zero", () =>
    {
        var stats = NewStatistics();
        var rows = stats.Snapshot(0, RankingMetric.Speed).Players;
        Equal(4, rows.Count);
        Equal(0L, rows.Single(p => p.IsSelf).Messages);
        Equal(true, rows.Where(p => !p.IsSelf).All(p => p.Messages == null && p.Characters == null && p.CharactersPerMinute == null));
    }),
    ("statistics count Unicode scalars and preserve repeated sends", () =>
    {
        var stats = NewStatistics();
        stats.RecordSelf("艾𠮷!", 1000); stats.RecordSelf("艾𠮷!", 1001);
        var self = stats.Snapshot(1001, RankingMetric.Speed).Players.Single(p => p.IsSelf);
        Equal(2L, self.Messages);
        Equal(6L, self.Characters);
        Equal(36d, self.CharactersPerMinute);
    }),
    ("live speed decays at the ten-second boundary without losing totals", () =>
    {
        var stats = NewStatistics();
        stats.RecordSelf("测试", 1000);
        Equal(12d, stats.Snapshot(10999, RankingMetric.Speed).Players.Single(p => p.IsSelf).CharactersPerMinute);
        var self = stats.Snapshot(11000, RankingMetric.Speed).Players.Single(p => p.IsSelf);
        Equal(0d, self.CharactersPerMinute); Equal(2L, self.Characters);
    }),
    ("four-player ranking is normalized and changes with the selected metric", () =>
    {
        var stats = NewStatistics();
        stats.Record(1, "累计更多但已经过期的长文字", 0);
        stats.Record(2, "现在快", 11000);
        var speed = stats.Snapshot(11000, RankingMetric.Speed).Players;
        Equal(2, speed[0].Slot); Equal(1f, speed[0].Progress);
        var total = stats.Snapshot(11000, RankingMetric.Characters).Players;
        Equal(1, total[0].Slot); Equal(1f, total[0].Progress);
        Equal(true, total.All(p => float.IsFinite(p.Progress) && p.Progress is >= 0 and <= 1));
    }),
    ("statistics survive temporary UI hiding and reset for a new session", () =>
    {
        var stats = NewStatistics();
        stats.RecordSelf("测试", 0); stats.End(500);
        Equal(12d, stats.Snapshot(50000, RankingMetric.Speed).Players.Single(p => p.IsSelf).CharactersPerMinute);
        stats.SetRoster(TestRoster());
        Equal(2L, stats.Snapshot(50000, RankingMetric.Speed).Players.Single(p => p.IsSelf).Characters);
        stats.SetRoster(TestRoster() with { Session = "new-match" });
        Equal(0L, stats.Snapshot(50001, RankingMetric.Speed).Players.Single(p => p.IsSelf).Characters);
    }),
    ("ambiguous identity does not assign self sends to an arbitrary player", () =>
    {
        var stats = new TypingStatistics();
        stats.SetRoster(new("match", [new(0, "同名", true), new(1, "同名", true)]));
        Equal(false, stats.RecordSelf("测试", 0));
        Equal(true, stats.Snapshot(0, RankingMetric.Speed).Players.Where(p => p.IsSelf).All(p => p.Messages == 0));
    }),
    ("statistics reset clears totals and speed without dropping roster", () =>
    {
        var stats = NewStatistics(); stats.RecordSelf("测试", 0); stats.Reset();
        var self = stats.Snapshot(1, RankingMetric.Speed).Players.Single(p => p.IsSelf);
        Equal("自己", self.Name); Equal(0L, self.Messages); Equal(0d, self.CharactersPerMinute);
    }),
    ("manual mode fills but never calls submit even after a long wait", () =>
    {
        var (port, engine) = CreateManual();
        engine.Tick(0); engine.Tick(150); engine.Tick(250); engine.Tick(120000);
        Equal("巴雷特", port.Input);
        Equal(true, engine.Running);
        Equal(0, port.Submitted.Count);
        Equal(0, engine.Submissions);
        Equal(0, engine.ManualSubmissions);
    }),
    ("manual Enter and clear count once and respect the answer interval", () =>
    {
        var (port, engine) = CreateManual();
        var observed = new List<string>();
        engine.ManualSubmissionObserved += observed.Add;
        engine.Tick(0); engine.Tick(150);
        engine.ObserveEnterKey(true, 180); engine.ObserveEnterKey(false, 190);
        port.Input = "";
        engine.Tick(200); engine.Tick(201); engine.Tick(849);
        Equal(1, engine.ManualSubmissions);
        Equal(0, engine.Submissions);
        Equal("巴雷特", observed.Single());
        Equal(1, port.Writes);
        engine.Tick(850); engine.Tick(1000);
        Equal(2, port.Writes);
        Equal(0, port.Submitted.Count);
    }),
    ("clearing a manual-mode word without Enter stops without counting", () =>
    {
        var (port, engine) = CreateManual();
        engine.Tick(0); engine.Tick(150); port.Input = ""; engine.Tick(200);
        Equal(false, engine.Running);
        Equal(0, engine.ManualSubmissions);
        Equal(0, port.Submitted.Count);
    }),
    ("manual Enter with no clear times out without synthesizing submission", () =>
    {
        var (port, engine) = CreateManual();
        engine.Tick(0); engine.Tick(150); engine.ObserveEnterKey(true, 200);
        engine.Tick(2199); Equal(true, engine.Running);
        engine.Tick(2200); Equal(false, engine.Running);
        Equal("巴雷特", port.Input);
        Equal(0, port.Submitted.Count);
        Equal(0, engine.ManualSubmissions);
    }),
    ("holding Enter blocks the next manual fill until release", () =>
    {
        var (port, engine) = CreateManual();
        engine.Tick(0); engine.Tick(150); engine.ObserveEnterKey(true, 200);
        port.Input = ""; engine.Tick(200); engine.Tick(1000); engine.Tick(2000);
        Equal(1, port.Writes);
        engine.ObserveEnterKey(false, 2050); engine.Tick(2050); engine.Tick(2200);
        Equal(2, port.Writes);
        Equal(1, engine.ManualSubmissions);
    }),
    ("Enter already held at start cannot submit a newly filled manual word", () =>
    {
        var port = new FakePort(); var engine = new AnswerController(port);
        engine.ObserveEnterKey(true, 0); engine.Start(0, new(ManualSubmit: true));
        engine.Tick(0); engine.Tick(150); Equal(0, port.Writes);
        engine.ObserveEnterKey(false, 200); engine.Tick(200); engine.Tick(350);
        Equal(1, port.Writes);
        port.Input = ""; engine.Tick(400);
        Equal(false, engine.Running);
        Equal(0, engine.ManualSubmissions);
    }),
    ("manual target loss clears owned text and chooses a replacement", () =>
    {
        var (port, engine) = CreateManual(); engine.Tick(0); engine.Tick(150);
        port.Targets = [new("slot1", "新的词条")]; engine.Tick(200);
        Equal("", port.Input);
        engine.Tick(250); engine.Tick(400);
        Equal("新的词条", port.Input);
        Equal(0, engine.ManualSubmissions);
        Equal(0, port.Submitted.Count);
    }),
    ("manual Enter cleared in the same tick as target disappearance is observed", () =>
    {
        var (port, engine) = CreateManual(); engine.Tick(0); engine.Tick(150);
        engine.ObserveEnterKey(true, 200); port.Input = ""; port.Targets = [];
        engine.Tick(200); engine.Tick(250);
        Equal(true, engine.Running);
        Equal(1, engine.ManualSubmissions);
        Equal(0, port.Submitted.Count);
    }),
    ("manual edits after Enter stop and preserve the edited text", () =>
    {
        var (port, engine) = CreateManual(); engine.Tick(0); engine.Tick(150);
        engine.ObserveEnterKey(true, 200); port.Input = "人工文字"; engine.Tick(200);
        Equal(false, engine.Running);
        Equal("人工文字", port.Input);
        Equal(0, engine.ManualSubmissions);
    }),
    ("stopping or losing context cancels pending manual observations", () =>
    {
        foreach (var stop in new[] { true, false })
        {
            var (port, engine) = CreateManual(); engine.Tick(0); engine.Tick(150);
            engine.ObserveEnterKey(true, 200); port.Input = "";
            if (stop) engine.Stop(); else port.Ready = false;
            engine.Tick(200);
            Equal(false, engine.Running);
            Equal(0, engine.ManualSubmissions);
        }
    }),
    ("manual submission attempts enforce the same per-word limit", () =>
    {
        var port = new FakePort(); var engine = new AnswerController(port);
        engine.Start(0, new(IntervalMs: 0, MaxAttemptsPerWord: 1, ManualSubmit: true));
        engine.Tick(0); engine.Tick(150); engine.ObserveEnterKey(true, 200);
        port.Input = ""; engine.Tick(200); engine.ObserveEnterKey(false, 201); engine.Tick(201);
        Equal(false, engine.Running);
        Equal(1, engine.ManualSubmissions);
        Equal(0, port.Submitted.Count);
    }),
    ("manual submit preference survives clamping and config round trip", () =>
    {
        var options = JsonSerializer.Deserialize<AnswerOptions>(JsonSerializer.Serialize(
            new AnswerOptions(FillDelayMs: -1, ManualSubmit: true)))!.Clamp();
        Equal(true, options.ManualSubmit);
        Equal(0, options.FillDelayMs);
        Equal(false, JsonSerializer.Deserialize<AnswerOptions>("{\"IntervalMs\":650}")!.ManualSubmit);
    }),
    ("keyboard mode waits for the bound input focus without sending text", () =>
    {
        var (port, engine) = CreateKeyboard(); port.KeyboardReady = false;
        engine.Tick(0); engine.Tick(150); engine.Tick(30000);
        Equal(true, engine.Running); Equal(0, port.Typed.Count); Equal(0, port.Writes);
        port.KeyboardReady = true;
        engine.Tick(30100); engine.Tick(30250); engine.Tick(30300);
        Equal("中", port.Input);
    }),
    ("keyboard mode types Unicode scalars and submits through Enter rather than callbacks", () =>
    {
        var (port, engine) = CreateKeyboard("中𠮷！");
        var observed = new List<string>(); engine.KeyboardSubmissionObserved += observed.Add;
        engine.Tick(0); engine.Tick(150); engine.Tick(200); engine.Tick(250); engine.Tick(300);
        Equal("中𠮷！", port.Input); Equal(3, port.Typed.Count); Equal("𠮷", port.Typed[1]);
        engine.Tick(350); engine.Tick(450); engine.Tick(500); engine.Tick(550);
        Equal(1, port.EnterRequests); Equal(1, engine.Submissions); Equal("中𠮷！", observed.Single());
        Equal(0, port.Writes); Equal(0, port.Submitted.Count);
    }),
    ("keyboard manual mode types text but waits for physical Enter", () =>
    {
        var (port, engine) = CreateKeyboard(manual: true);
        engine.Tick(0); engine.Tick(150); engine.Tick(200); engine.Tick(250); engine.Tick(300); engine.Tick(90000);
        Equal("中文", port.Input); Equal(0, port.EnterRequests);
        engine.ObserveEnterKey(true, 90010); port.Input = ""; engine.Tick(90050);
        Equal(1, engine.ManualSubmissions); Equal(0, engine.Submissions);
        Equal(0, port.EnterRequests); Equal(0, port.Submitted.Count);
    }),
    ("keyboard input confirms each character before sending the next", () =>
    {
        var (port, engine) = CreateKeyboard(); port.AcceptTyped = false;
        engine.Tick(0); engine.Tick(150); engine.Tick(200); engine.Tick(1000);
        Equal(1, port.Typed.Count); Equal(0, port.EnterRequests);
        port.Input = "中"; port.AcceptTyped = true; engine.Tick(1050);
        Equal("中文", port.Input); Equal(2, port.Typed.Count);
    }),
    ("keyboard character acknowledgement timeout stops without retry or fallback", () =>
    {
        var (port, engine) = CreateKeyboard(); port.AcceptTyped = false;
        engine.Tick(0); engine.Tick(150); engine.Tick(200); engine.Tick(2199);
        Equal(true, engine.Running); engine.Tick(2200);
        Equal(false, engine.Running); Equal(1, port.Typed.Count);
        Equal(0, port.Writes); Equal(0, port.EnterRequests);
    }),
    ("keyboard focus loss or user edits stop and preserve partially typed text", () =>
    {
        foreach (var focusLost in new[] { true, false })
        {
            var (port, engine) = CreateKeyboard();
            engine.Tick(0); engine.Tick(150); engine.Tick(200);
            if (focusLost) port.KeyboardReady = false; else port.Input = "人工修改";
            var remaining = port.Input; engine.Tick(250);
            Equal(false, engine.Running); Equal(remaining, port.Input);
            Equal(1, port.Typed.Count); Equal(0, port.EnterRequests);
        }
    }),
    ("keyboard target disappearance cancels typing without deleting partial text", () =>
    {
        var (port, engine) = CreateKeyboard(); engine.Tick(0); engine.Tick(150); engine.Tick(200);
        port.Targets = []; engine.Tick(250);
        Equal(false, engine.Running); Equal("中", port.Input); Equal(0, port.Clears);
    }),
    ("keyboard completed target loss or focus loss prevents Enter", () =>
    {
        foreach (var focusLost in new[] { true, false })
        {
            var (port, engine) = CreateKeyboard();
            engine.Tick(0); engine.Tick(150); engine.Tick(200); engine.Tick(250); engine.Tick(300);
            if (focusLost) port.KeyboardReady = false; else port.Targets = [];
            engine.Tick(400);
            Equal(false, engine.Running); Equal("中文", port.Input); Equal(0, port.EnterRequests);
        }
    }),
    ("keyboard character delay is enforced after text acknowledgement", () =>
    {
        var port = new FakePort { Targets = [new("slot1", "中文")] };
        var engine = new AnswerController(port);
        engine.Start(0, new(KeyboardInput: true, CharacterDelayMs: 120));
        engine.Tick(0); engine.Tick(150); engine.Tick(200); engine.Tick(250); engine.Tick(319);
        Equal(1, port.Typed.Count); engine.Tick(320); Equal(2, port.Typed.Count);
    }),
    ("keyboard press failure is not counted and successful press still needs clear", () =>
    {
        foreach (var rejected in new[] { true, false })
        {
            var (port, engine) = CreateKeyboard(); port.RejectEnter = rejected; port.ClearOnEnter = false;
            var observations = 0; engine.KeyboardSubmissionObserved += _ => observations++;
            engine.Tick(0); engine.Tick(150); engine.Tick(200); engine.Tick(250); engine.Tick(300);
            engine.Tick(400); engine.Tick(2400);
            Equal(false, engine.Running); Equal(rejected ? 0 : 1, engine.Submissions);
            Equal(0, observations); Equal(0, port.Submitted.Count);
        }
    }),
    ("keyboard settings round trip and legacy options keep direct input", () =>
    {
        var settings = JsonSerializer.Deserialize<AnswerOptions>(JsonSerializer.Serialize(
            new AnswerOptions(KeyboardInput: true, CharacterDelayMs: -1)))!.Clamp();
        Equal(true, settings.KeyboardInput); Equal(0, settings.CharacterDelayMs);
        Equal(1000, new AnswerOptions(CharacterDelayMs: int.MaxValue).Clamp().CharacterDelayMs);
        Equal(false, JsonSerializer.Deserialize<AnswerOptions>("{}")!.KeyboardInput);
    }),
    ("Unicode keyboard emits full surrogate pairs as down and up events", () =>
    {
        var sink = new FakeInputSink(); var keyboard = new KeyboardTextInput(sink);
        Equal(true, keyboard.TypeCharacter(new Rune(0x20BB7)).Success);
        var strokes = sink.Batches.Single(); Equal(4, strokes.Length);
        Equal((ushort)0xD842, strokes[0].ScanCode); Equal((ushort)0xDFB7, strokes[2].ScanCode);
        Equal(4u, strokes[0].Flags); Equal(6u, strokes[1].Flags);
        Equal((ushort)0, strokes[0].VirtualKey); Equal(false, keyboard.HasHeldKeys);
    }),
    ("partial Unicode injection releases accepted down keys and retries failed release", () =>
    {
        var sink = new FakeInputSink(); sink.Accepted.Enqueue(3); sink.Accepted.Enqueue(0);
        var keyboard = new KeyboardTextInput(sink);
        Equal(false, keyboard.TypeCharacter(new Rune(0x20BB7)).Success);
        Equal(true, keyboard.HasHeldKeys);
        Equal(false, keyboard.TypeCharacter(new Rune('中')).Success);
        keyboard.Tick(1, true); Equal(false, keyboard.HasHeldKeys);
        Equal((ushort)0xDFB7, sink.Batches.Last().Single().ScanCode);
        Equal(6u, sink.Batches.Last().Single().Flags);
    }),
    ("scan-code Enter holds across frames and releases on stop or focus loss", () =>
    {
        foreach (var canContinue in new[] { true, false })
        {
            var sink = new FakeInputSink(); var keyboard = new KeyboardTextInput(sink);
            Equal(true, keyboard.PressEnter(100).Success); keyboard.Tick(219, true);
            Equal(true, keyboard.HasHeldKeys); Equal(1, sink.Batches.Count);
            keyboard.Tick(canContinue ? 220 : 219, canContinue);
            Equal(false, keyboard.HasHeldKeys);
            Equal((ushort)0x1C, sink.Batches[0].Single().ScanCode);
            Equal(8u, sink.Batches[0].Single().Flags); Equal(10u, sink.Batches[1].Single().Flags);
        }
    }),
    ("scan-code Enter failed press creates no owned key and failed release is retried", () =>
    {
        var sink = new FakeInputSink(); sink.Accepted.Enqueue(0);
        var keyboard = new KeyboardTextInput(sink);
        Equal(false, keyboard.PressEnter(0).Success); Equal(false, keyboard.HasHeldKeys);
        Equal(true, keyboard.PressEnter(100).Success); sink.Accepted.Enqueue(0);
        keyboard.Tick(220, true); Equal(true, keyboard.HasHeldKeys);
        keyboard.Tick(221, true); Equal(false, keyboard.HasHeldKeys);
    }),
    ("keyboard stop cancels further characters and preserves partial text", () =>
    {
        var (port, engine) = CreateKeyboard(); engine.Tick(0); engine.Tick(150); engine.Tick(200);
        engine.Stop(); engine.Tick(250); engine.Tick(10000);
        Equal(1, port.Typed.Count); Equal("中", port.Input); Equal(0, port.EnterRequests);
    }),
    ("pinyin dictionary loads real resource and normalizes umlaut and tone marks", () =>
    {
        Equal(true, PinyinDictionary.TryRead(new Rune('中'), out var zhong)); Equal("zhong", zhong);
        Equal("lv", PinyinDictionary.Normalize("lǜ")); Equal("nv", PinyinDictionary.Normalize("nǚ"));
        Equal(false, PinyinDictionary.TryRead(new Rune('!'), out _));
        Equal(true, PinyinDictionary.IsHan(new Rune('〇')));
        Equal(false, PinyinDictionary.IsHan(new Rune('a')));
    }),
    ("pinyin types real letter scan codes and selects only an exact candidate", () =>
    {
        var sink = new FakeInputSink(); var keys = new KeyboardTextInput(sink);
        var ime = new PinyinComposer(keys, () => new(true, true, "", ["钟", "中", "忠"]), new());
        Equal(true, ime.Begin(new Rune('中'), "", 0, new(CharacterDelayMs: 0)).Success);
        DrivePinyin(ime, keys, 1000, "");
        var down = sink.Batches.SelectMany(b => b).Where(k => k.Flags == 8).ToArray();
        Equal(6, down.Length);
        Equal(true, down.Take(5).Select(k => k.ScanCode).SequenceEqual("zhong".Select(PinyinComposer.LetterScanCode)));
        Equal((ushort)3, down[5].ScanCode); // second candidate -> digit 2
        Equal(false, down.Any(k => k.ScanCode == 0x1C));
        keys.Tick(1010, true); ime.Tick(1010, true, "中");
        Equal(false, ime.Active); Equal("", ime.Error);
    }),
    ("pinyin first candidate commits with space and verifies the resulting character", () =>
    {
        var sink = new FakeInputSink(); var keys = new KeyboardTextInput(sink);
        var ime = new PinyinComposer(keys, () => new(true, true, "", ["中"]), new());
        ime.Begin(new Rune('中'), "前", 0, new(CharacterDelayMs: 0));
        DrivePinyin(ime, keys, 1000, "前");
        Equal((ushort)0x39, sink.Batches.SelectMany(b => b).Last(k => k.Flags == 8).ScanCode);
        ime.Tick(1010, true, "前钟"); Equal(false, ime.Active); Equal(true, ime.Error.Length > 0);
    }),
    ("pinyin rejects English mode and existing composition before any keystrokes", () =>
    {
        foreach (var state in new[] { new ImeSnapshot(false, false, "", []), new(true, false, "", []), new(true, true, "ni", []) })
        {
            var sink = new FakeInputSink(); var keys = new KeyboardTextInput(sink);
            var ime = new PinyinComposer(keys, () => state, new());
            Equal(false, ime.Begin(new Rune('中'), "", 0, new()).Success); Equal(0, sink.Batches.Count);
        }
    }),
    ("pinyin waits for readable candidates and stops at a bounded timeout", () =>
    {
        var sink = new FakeInputSink(); var keys = new KeyboardTextInput(sink);
        var ime = new PinyinComposer(keys, () => new(true, true, "", []), new());
        ime.Begin(new Rune('中'), "", 0, new(CharacterDelayMs: 0));
        DrivePinyin(ime, keys, 29990, ""); Equal(true, ime.Active);
        ime.Tick(30000, true, ""); Equal(false, ime.Active);
        Equal(true, ime.Error.Length > 0);
        Equal(5, sink.Batches.SelectMany(b => b).Count(k => k.Flags == 8));
    }),
    ("pinyin pages candidates and stops when the page no longer changes", () =>
    {
        var sink = new FakeInputSink(); var keys = new KeyboardTextInput(sink);
        var ime = new PinyinComposer(keys, () => new(true, true, "", ["钟", "忠"]), new());
        ime.Begin(new Rune('中'), "", 0, new(CharacterDelayMs: 0));
        DrivePinyin(ime, keys, 2000, "");
        Equal(false, ime.Active); Equal(true, ime.Error.Length > 0);
        Equal(1, sink.Batches.SelectMany(b => b).Count(k => k.ScanCode == 0x51 && k.Flags == 9));
        Equal(false, sink.Batches.SelectMany(b => b).Any(k => k.ScanCode == 0x1C));
    }),
    ("pinyin can select an exact character after a candidate page update", () =>
    {
        var sink = new FakeInputSink(); var keys = new KeyboardTextInput(sink);
        ImeSnapshot Snapshot() => new(true, true, "", sink.Batches.SelectMany(b => b).Any(k => k.ScanCode == 0x51)
            ? ["中"] : ["钟"]);
        var ime = new PinyinComposer(keys, Snapshot, new());
        ime.Begin(new Rune('中'), "", 0, new(CharacterDelayMs: 0));
        DrivePinyin(ime, keys, 1500, ""); ime.Tick(1510, true, "中");
        Equal(false, ime.Active); Equal("", ime.Error);
    }),
    ("pinyin focus loss cancels further key downs and held keys can be released", () =>
    {
        var sink = new FakeInputSink(); var keys = new KeyboardTextInput(sink);
        var ime = new PinyinComposer(keys, () => new(true, true, "", ["中"]), new());
        ime.Begin(new Rune('中'), "", 0, new()); ime.Tick(0, true, "");
        Equal(true, keys.HasHeldKeys); ime.Tick(10, false, ""); keys.Tick(10, false);
        Equal(false, ime.Active); Equal(false, keys.HasHeldKeys);
        Equal(1, sink.Batches.SelectMany(b => b).Count(k => k.Flags == 8));
    }),
    ("pinyin key press failure stops without guessing a candidate", () =>
    {
        var sink = new FakeInputSink(); sink.Accepted.Enqueue(0); var keys = new KeyboardTextInput(sink);
        var ime = new PinyinComposer(keys, () => new(true, true, "", ["中"]), new());
        ime.Begin(new Rune('中'), "", 0, new()); ime.Tick(0, true, "");
        Equal(false, ime.Active); Equal(false, keys.HasHeldKeys); Equal(true, ime.Error.Length > 0);
    }),
    ("random input delays are sampled once and include bounded pauses", () =>
    {
        var calls = 0; var timing = new InputTiming((min, max) => { calls++; return max - 1; });
        Equal(160, timing.Gap(new(RandomizeDelays: true)));
        Equal(80, timing.Hold(new(RandomizeDelays: true)));
        var pause = new InputTiming((min, max) => min);
        Equal(240, pause.Gap(new(RandomizeDelays: true)));
        Equal(40, timing.Gap(new())); Equal(50, timing.Hold(new()));
        Equal(3, calls);
    }),
    ("pinyin and random settings clamp and preserve legacy behavior", () =>
    {
        var settings = new AnswerOptions(PinyinInput: true, RandomizeDelays: true, RandomMinMs: 1000, RandomMaxMs: -1).Clamp();
        Equal(true, settings.KeyboardInput); Equal(1000, settings.RandomMaxMs);
        Equal(20, new AnswerOptions(RandomMinMs: -1, RandomMaxMs: -2).Clamp().RandomMinMs);
        var old = JsonSerializer.Deserialize<AnswerOptions>("{}")!.Clamp();
        Equal(false, old.PinyinInput); Equal(false, old.RandomizeDelays);
    }),
    ("answer controller waits for pinyin conversion instead of resending characters", () =>
    {
        var port = new FakePort { Targets = [new("slot1", "中")], AcceptTyped = false, Composing = true };
        var engine = new AnswerController(port); engine.Start(0, new(PinyinInput: true));
        engine.Tick(0); engine.Tick(150); engine.Tick(200); engine.Tick(5000);
        Equal(true, engine.Running); Equal(1, port.Typed.Count); Equal(0, port.EnterRequests);
        port.Composing = false; port.Input = "中"; engine.Tick(6000); engine.Tick(6100);
        Equal(1, port.EnterRequests); Equal(0, port.Submitted.Count);
    }),
    ("candidate list parser uses the current page and validates native offsets", () =>
    {
        var data = CandidateBytes(["钟", "忠", "中"], 1, 2);
        Equal(true, ImeCandidateList.Parse(data).SequenceEqual(new[] { "忠", "中" }));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28, 4), 0xFFFFFFFE);
        Equal(0, ImeCandidateList.Parse(data).Length);
        Equal(0, ImeCandidateList.Parse([1, 2, 3]).Length);
    }),
    ("complete pinyin text is not submitted while the IME operation is still active", () =>
    {
        var port = new FakePort { Targets = [new("slot1", "中")], Composing = true };
        var engine = new AnswerController(port); engine.Start(0, new(PinyinInput: true, FillDelayMs: 0));
        engine.Tick(0); engine.Tick(150); engine.Tick(200); engine.Tick(250); engine.Tick(1000);
        Equal("中", port.Input); Equal(0, port.EnterRequests);
        port.Composing = false; engine.Tick(1050); engine.Tick(1100);
        Equal(1, port.EnterRequests);
    }),
    ("pinyin does not treat a matching but uncommitted composition as final text", () =>
    {
        var sink = new FakeInputSink(); var keys = new KeyboardTextInput(sink);
        var composition = "";
        var ime = new PinyinComposer(keys, () => new(true, true, composition, ["中"]), new());
        ime.Begin(new Rune('中'), "", 0, new(CharacterDelayMs: 0));
        DrivePinyin(ime, keys, 1000, "");
        composition = "中"; ime.Tick(1010, true, "中"); Equal(true, ime.Active);
        composition = ""; ime.Tick(1020, true, "中"); Equal(false, ime.Active);
    }),
    ("capture buffer preserves thirty seconds of real refresh schemas without overflow", () =>
    {
        var fixture = JsonSerializer.Deserialize<CapturedEvent[]>(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "fixtures", "capture-refresh-samples.json")))!;
        var buffer = new CaptureBuffer();
        for (var at = 0; at < 30000; at += 16)
            foreach (var item in fixture) buffer.Add(item with { Milliseconds = at });
        Equal(true, buffer.Received > 20000); Equal(0, buffer.Dropped);
        Equal(true, buffer.Count < 1000); Equal(true, buffer.Layout > 0); Equal(true, buffer.Duplicates > 0);
        Equal(4, buffer.Snapshot().Count(e => e.Addon == "KTGPlayerName"));
    }),
    ("capture de-duplicates per player slot but preserves every send observation", () =>
    {
        var buffer = new CaptureBuffer();
        for (var slot = 0; slot < 4; slot++)
            for (var repeat = 0; repeat < 10; repeat++)
                buffer.Add(new(repeat, "KTGPlayerName", "PreRefresh", [new("Int", "0"), new("Int", slot.ToString()), new("String", "name")]));
        buffer.Add(new(10, "KTGInput", "AutomaticSubmit", [new("String", "重复词") ]));
        buffer.Add(new(11, "KTGInput", "AutomaticSubmit", [new("String", "重复词") ]));
        Equal(6, buffer.Count); Equal(36, buffer.Duplicates); Equal(0, buffer.Dropped);
    }),
    ("capture score throttling does not suppress unrecognized game events", () =>
    {
        var buffer = new CaptureBuffer();
        buffer.Add(new(0, "KTGHudScore", "PreRefresh", [new("Int", "1"), new("UInt", "100")]));
        buffer.Add(new(50, "KTGHudScore", "PreRefresh", [new("Int", "1"), new("UInt", "90")]));
        buffer.Add(new(100, "KTGHudScore", "PreRefresh", [new("Int", "1"), new("UInt", "80")]));
        buffer.Add(new(101, "KTGHudScore", "PreRefresh", [new("Int", "9"), new("String", "unknown")]));
        Equal(3, buffer.Count); Equal(1, buffer.Throttled);
    }),
    ("repeated word and unknown refresh events remain separate diagnostic observations", () =>
    {
        var buffer = new CaptureBuffer();
        foreach (var command in new[] { "2", "3" })
            foreach (var at in new[] { 0, 500, 1000 })
                buffer.Add(new(at, "KTGEWord", "PreRefresh", [new("Int", command), new("UInt", "1073795066")]));
        buffer.Add(new(1001, "KTGInput", "PreRefresh", [new("Int", "7")]));
        buffer.Add(new(1002, "KTGInput", "PreRefresh", [new("Int", "7")]));
        Equal(8, buffer.Count); Equal(0, buffer.Duplicates);
    }),
    ("new real capture retains target events and every own send across the full capture", () =>
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "capture-195327.json")));
        var events = data.RootElement.GetProperty("Events").Deserialize<CapturedEvent[]>()!;
        var buffer = new CaptureBuffer();
        foreach (var item in events) buffer.Add(item);
        Equal(0, buffer.Dropped); Equal(events.Length, buffer.Count);
        Equal(27, buffer.Snapshot().Count(e => e.Addon == "KTGEWord"));
        Equal(15, buffer.Snapshot().Count(e => e.Event == "AutomaticSubmit"));
        Equal(true, buffer.Snapshot()[^1].Milliseconds - buffer.Snapshot()[0].Milliseconds > 29000);
    }),
    ("capture overflow and reset counters remain explicit and bounded", () =>
    {
        var buffer = new CaptureBuffer(2);
        for (var at = 0; at < 3; at++) buffer.Add(new(at, "KTGInput", "AutomaticSubmit", []));
        Equal(2, buffer.Count); Equal(1, buffer.Dropped); Equal(1L, buffer.Snapshot()[0].Milliseconds);
        buffer.Clear(); Equal(0, buffer.Count); Equal(0, buffer.Dropped); Equal(0, buffer.Received);
    }),
    ("heartbeat is opt-in and inactive while answering is stopped", () =>
    {
        var keyboard = new FakeKeyboard(); var heartbeat = new KeyboardHeartbeat(keyboard);
        heartbeat.Tick(0, true, new()); heartbeat.Tick(60000, true, new());
        heartbeat.Tick(70000, false, new(true)); heartbeat.Tick(120000, false, new(true));
        Equal(0, keyboard.Presses);
    }),
    ("heartbeat waits for interval and holds for multiple game frames", () =>
    {
        var keyboard = new FakeKeyboard(); var heartbeat = new KeyboardHeartbeat(keyboard);
        heartbeat.Tick(0, true, new(true)); heartbeat.Tick(29999, true, new(true));
        Equal(0, keyboard.Presses);
        heartbeat.Tick(30000, true, new(true)); heartbeat.Tick(30050, true, new(true));
        Equal(1, keyboard.Presses); Equal(0, keyboard.Releases);
        heartbeat.Tick(30120, true, new(true)); Equal(1, keyboard.Releases);
    }),
    ("heartbeat respects foreground and held modifiers", () =>
    {
        var keyboard = new FakeKeyboard { Active = false }; var heartbeat = new KeyboardHeartbeat(keyboard);
        heartbeat.Tick(0, true, new(true)); heartbeat.Tick(30000, true, new(true));
        Equal(0, keyboard.Presses);
        keyboard.Active = true; keyboard.ModifierHeld = true;
        heartbeat.Tick(31000, true, new(true)); Equal(0, keyboard.Presses);
        keyboard.ModifierHeld = false;
        heartbeat.Tick(32000, true, new(true)); Equal(1, keyboard.Presses);
    }),
    ("heartbeat releases owned key immediately on stop or focus loss", () =>
    {
        foreach (var loseFocus in new[] { true, false })
        {
            var keyboard = new FakeKeyboard(); var heartbeat = new KeyboardHeartbeat(keyboard);
            heartbeat.Tick(0, true, new(true)); heartbeat.Tick(30000, true, new(true));
            keyboard.Active = !loseFocus;
            heartbeat.Tick(30001, loseFocus, new(true));
            Equal(1, keyboard.Releases);
        }
    }),
    ("failed heartbeat press is not counted or released", () =>
    {
        var keyboard = new FakeKeyboard { PressSucceeds = false }; var heartbeat = new KeyboardHeartbeat(keyboard);
        heartbeat.Tick(0, true, new(true)); heartbeat.Tick(30000, true, new(true)); heartbeat.Tick(30120, true, new(true));
        Equal(0, heartbeat.Sent); Equal(0, keyboard.Releases);
    }),
    ("failed heartbeat release is retried until successful", () =>
    {
        var keyboard = new FakeKeyboard { ReleaseSucceeds = false }; var heartbeat = new KeyboardHeartbeat(keyboard);
        heartbeat.Tick(0, true, new(true)); heartbeat.Tick(30000, true, new(true)); heartbeat.Tick(30120, true, new(true));
        keyboard.ReleaseSucceeds = true;
        heartbeat.Tick(30121, false, new(true)); Equal(2, keyboard.Releases);
        heartbeat.Tick(30122, false, new(true)); Equal(2, keyboard.Releases);
    }),
    ("heartbeat interval is bounded below the reported sixty-second timeout", () =>
    {
        Equal(10000, new HeartbeatOptions(true, -1).Clamp().IntervalMs);
        Equal(45000, new HeartbeatOptions(true, int.MaxValue).Clamp().IntervalMs);
        Equal(40, System.Runtime.InteropServices.Marshal.SizeOf<NativeHeartbeatKeyboard.KeyboardInput>());
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
static void DrivePinyin(PinyinComposer ime, KeyboardTextInput keys, int end, string input)
{
    for (var now = 0; now <= end; now += 10) { keys.Tick(now, true); ime.Tick(now, true, input); }
}
static byte[] CandidateBytes(string[] candidates, int start, int pageSize)
{
    var strings = candidates.Select(c => Encoding.Unicode.GetBytes(c + '\0')).ToArray();
    var offset = 24 + candidates.Length * 4;
    var data = new byte[offset + strings.Sum(s => s.Length)];
    void Write(int at, int value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at, 4), (uint)value);
    Write(0, data.Length); Write(8, candidates.Length); Write(16, start); Write(20, pageSize);
    for (var i = 0; i < strings.Length; i++) { Write(24 + i * 4, offset); strings[i].CopyTo(data, offset); offset += strings[i].Length; }
    return data;
}
static (FakePort Port, AnswerController Engine) CreateManual()
{
    var port = new FakePort();
    var engine = new AnswerController(port);
    Equal(true, engine.Start(0, new(ManualSubmit: true)));
    return (port, engine);
}
static (FakePort Port, AnswerController Engine) CreateKeyboard(string text = "中文", bool manual = false)
{
    var port = new FakePort { Targets = [new("slot1", text)] };
    var engine = new AnswerController(port);
    Equal(true, engine.Start(0, new(ManualSubmit: manual, KeyboardInput: true, CharacterDelayMs: 50)));
    return (port, engine);
}
static ExportFixture ReadFixture(string name) => JsonSerializer.Deserialize<ExportFixture>(
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)))!;
static TeamRoster TestRoster() => new("match", [new(0, "自己", true), new(1, "队友甲", false),
    new(2, "队友乙", false), new(3, "队友丙", false)]);
static TypingStatistics NewStatistics() { var stats = new TypingStatistics(); stats.SetRoster(TestRoster()); return stats; }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'");
}

sealed record ExportFixture(string GameVersion, uint Territory, List<InspectedNode> Nodes);

sealed class FakeInputSink : IKeyboardInputSink
{
    public readonly List<KeyboardStroke[]> Batches = [];
    public readonly Queue<int> Accepted = new();
    public int Send(KeyboardStroke[] strokes)
    {
        Batches.Add(strokes);
        return Accepted.Count > 0 ? Accepted.Dequeue() : strokes.Length;
    }
}

sealed class FakeKeyboard : IHeartbeatKeyboard
{
    public bool Active = true;
    public bool ModifierHeld;
    public bool PressSucceeds = true;
    public bool ReleaseSucceeds = true;
    public int Presses;
    public int Releases;
    public bool CanContinue() => Active;
    public bool CanPress() => Active && !ModifierHeld;
    public bool Press() { Presses++; return PressSucceeds; }
    public bool Release() { Releases++; return ReleaseSucceeds; }
}

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
    public bool KeyboardReady = true;
    public bool Composing;
    public bool AcceptTyped = true;
    public bool ClearOnEnter = true;
    public bool RejectEnter;
    public int EnterRequests;
    public int Clears;
    public List<string> Typed = [];
    public AnswerFrame Read() => new(Ready, Session, Input, Targets, "UI unavailable", KeyboardReady, Composing);
    public OperationResult TypeCharacter(string session, WordTarget target, string prefix, string character)
    {
        Typed.Add(character);
        if (AcceptTyped) Input += character;
        return new(true);
    }
    public OperationResult PressEnter(string session, WordTarget target)
    {
        if (RejectEnter) return new(false, "Enter rejected");
        EnterRequests++;
        if (ClearOnEnter) Input = "";
        return new(true);
    }
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
        Clears++;
        if (Session == session && Input == text) Input = "";
    }
}
