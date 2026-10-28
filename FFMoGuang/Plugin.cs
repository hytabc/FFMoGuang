using System.Collections.Concurrent;
using System.Text.Json;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFMoGuang.Core;

namespace FFMoGuang;

public sealed record PluginView(bool Running, string Status, string LastWord, int Submissions, int ManualSubmissions,
    string GameVersion, uint Territory, uint BoundTerritory, string InputBinding, string[] WordBindings,
    AnswerOptions Options, AnswerFrame Preview, InspectorSnapshot Inspector, string Notice,
    TypingLeaderboard Leaderboard, RankingMetric RankingMetric, HeartbeatOptions Heartbeat,
    string HeartbeatStatus, string CaptureStatus, string KeyboardStatus, int? SubmitDelayMs);

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IKeyState keys;
    private readonly IPluginLog log;
    private readonly IChatGui chat;
    private readonly Configuration config;
    private readonly GameUiPort port;
    private readonly AnswerController controller;
    private readonly MainWindow window;
    private readonly IPlayerState playerState;
    private readonly TypingStatistics statistics = new();
    private readonly KeyboardHeartbeat heartbeat;
    private readonly GameEventCapture capture;
    private readonly ConcurrentQueue<Action> requests = new();
    private InspectorSnapshot inspector = new([], "展开检查器后扫描当前游戏 UI");
    private string notice = "进入魔光键影对局后，可点击“自动绑定魔光键影”；首次点击“开始”也会自动识别。";
    private long nextScan;
    private long nextTick;
    private long nextRoster;
    private volatile bool inspectRequested;
    private volatile bool disposed;
    private volatile bool keyboardCaptured;
    private bool? startKeyWasDown;
    private PluginView view;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework,
        IGameGui gameGui, IClientState clientState, IKeyState keyState, IPluginLog pluginLog, IChatGui chatGui,
        IPlayerState playerState, IAddonLifecycle addonLifecycle)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.framework = framework;
        client = clientState;
        keys = keyState;
        log = pluginLog;
        chat = chatGui;
        this.playerState = playerState;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new();
        config.Words ??= [];
        config.Options = (config.Options ?? new()).Clamp();
        config.Heartbeat = (config.Heartbeat ?? new()).Clamp();
        port = new(config, gameGui, client, () => keyboardCaptured);
        controller = new(port);
        controller.ManualSubmissionObserved += OnManualSubmissionObserved;
        controller.KeyboardSubmissionObserved += OnKeyboardSubmissionObserved;
        heartbeat = new(new NativeHeartbeatKeyboard(port.HeartbeatReady));
        capture = new(addonLifecycle, client, log);
        port.AutomaticSubmitted += OnAutomaticSubmitted;
        view = MakeView(new(false, "", "", [], "尚未读取游戏 UI"));
        window = new(this);
        commands.AddHandler("/mg", new CommandInfo(OnCommand)
        {
            HelpMessage = "魔光键影助手：/mg 面板；/mg bind 自动绑定；/mg start 启动；/mg stop 停止；/mg dump 导出节点。F9 开始，F8 停止。",
        });
        pluginInterface.UiBuilder.Draw += window.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += window.Open;
        pluginInterface.UiBuilder.OpenMainUi += window.Open;
        framework.Update += OnUpdate;
    }

    public PluginView View => Volatile.Read(ref view);
    public void SetInspecting(bool value) => inspectRequested = value;
    public void SetKeyboardCapture(bool value) => keyboardCaptured = value;
    public void Start(int? operationDelayMs = null, int? intervalMs = null, bool? randomizeSubmitDelay = null,
        int? submitDelayMinMs = null, int? submitDelayMaxMs = null)
        => Enqueue(() => StartOnFramework(operationDelayMs, intervalMs, randomizeSubmitDelay, submitDelayMinMs, submitDelayMaxMs));
    private void StartOnFramework(int? operationDelayMs = null, int? intervalMs = null, bool? randomizeSubmitDelay = null,
        int? submitDelayMinMs = null, int? submitDelayMaxMs = null)
    {
        if (controller.Running) return;
        if (operationDelayMs.HasValue || intervalMs.HasValue || randomizeSubmitDelay.HasValue
            || submitDelayMinMs.HasValue || submitDelayMaxMs.HasValue)
        {
            config.Options = (config.Options with
            {
                FillDelayMs = operationDelayMs ?? config.Options.FillDelayMs,
                IntervalMs = intervalMs ?? config.Options.IntervalMs,
                RandomizeSubmitDelay = randomizeSubmitDelay ?? config.Options.RandomizeSubmitDelay,
                SubmitDelayMinMs = submitDelayMinMs ?? config.Options.SubmitDelayMinMs,
                SubmitDelayMaxMs = submitDelayMaxMs ?? config.Options.SubmitDelayMaxMs,
            }).Clamp();
            Save();
        }
        if ((config.Input == null || config.Words.Count == 0 || config.Territory == 0 || config.GameVersion.Length == 0)
            && !TryAutoBind())
        {
            controller.Stop("不能启动：" + notice);
            return;
        }
        controller.Start(Environment.TickCount64, config.Options);
    }
    public void Stop() => Enqueue(() => controller.Stop());
    public void AutoBind() => Enqueue(() => { controller.Stop(); TryAutoBind(); });
    public void Refresh() => Enqueue(() => { nextScan = 0; inspector = port.Inspect(); });
    public void SetRankingMetric(RankingMetric metric) => Enqueue(() => { config.RankingMetric = metric; Save(); });
    public void ResetStatistics() => Enqueue(statistics.Reset);
    public void SetHeartbeat(HeartbeatOptions options) => Enqueue(() => { config.Heartbeat = options.Clamp(); Save(); });
    public void StartCapture() => Enqueue(() =>
    {
        if (!client.IsLoggedIn || client.TerritoryType != KeyboundProfile.SupportedTerritory)
        {
            notice = "请进入魔光键影对局后开始采样";
            return;
        }
        capture.Start(port.GameVersion);
        notice = "正在采样 30 秒；请自己和队友分别发送几次词条，再导出采样。";
    });
    public void ExportCapture() => Enqueue(() =>
    {
        var directory = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"typing-capture-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(capture.Export(), new JsonSerializerOptions { WriteIndented = true }));
        notice = "发送采样已导出：" + path;
        chat.Print("[魔光键影] " + notice);
    });
    private void OnAutomaticSubmitted(string text)
    {
        statistics.RecordSelf(text, Environment.TickCount64);
        capture.RecordAutomatic(text);
    }
    private void OnManualSubmissionObserved(string text)
    {
        statistics.RecordSelf(text, Environment.TickCount64);
        capture.RecordManual(text);
    }
    private void OnKeyboardSubmissionObserved(string text)
    {
        statistics.RecordSelf(text, Environment.TickCount64);
        capture.RecordKeyboard(text);
    }

    private bool TryAutoBind()
    {
        inspector = port.Inspect();
        if (!KeyboundProfile.TryCreate(port.GameVersion, client.TerritoryType, inspector.Nodes, out var profile, out var problem))
        {
            notice = problem;
            return false;
        }
        var input = inspector.Nodes.Single(n => n.Binding.Key == profile!.Input!.Key);
        if (!port.CanBind(input))
        {
            notice = "输入控件已变化，请在实际对局中重新点击自动绑定";
            return false;
        }
        // Apply atomically only after validation; never replace a working profile on failure.
        config.Input = profile!.Input;
        config.Words = profile.Words.ToList();
        config.GameVersion = profile.GameVersion;
        config.Territory = profile.Territory;
        Save();
        notice = $"已自动绑定魔光键影：区域 {profile.Territory}，输入框 KTGInput[1]/7，全部 {profile.Words.Count} 个词条槽位";
        controller.Stop("自动绑定完成，可点击开始");
        return true;
    }

    public void Bind(InspectedNode row) => Enqueue(() =>
    {
        controller.Stop("绑定已修改，请重新启动");
        if (!port.CanBind(row)) { notice = "节点已失效或不可见，请刷新后重新选择。输入框必须有有效回调。"; return; }
        if (config.GameVersion != port.GameVersion || config.Territory != client.TerritoryType)
        {
            config.Input = null;
            config.Words.Clear();
        }
        config.GameVersion = port.GameVersion;
        config.Territory = client.TerritoryType;
        if (row.IsInput) config.Input = row.Binding;
        else if (!config.Words.Any(w => w.Key == row.Binding.Key)) config.Words.Add(row.Binding);
        Save();
        notice = "已绑定：" + row.Binding.Key;
    });

    public void RemoveWord(string key) => Enqueue(() =>
    {
        controller.Stop("绑定已修改，请重新启动");
        config.Words.RemoveAll(w => w.Key == key);
        Save();
    });

    public void ResetBindings() => Enqueue(() =>
    {
        controller.Stop();
        config.Input = null;
        config.Words.Clear();
        config.Territory = 0;
        config.GameVersion = "";
        Save();
        notice = "已清除绑定，请在对局中重新绑定。";
    });

    public void SetOptions(AnswerOptions options) => Enqueue(() =>
    {
        controller.Stop("参数已修改，请重新启动");
        config.Options = options.Clamp();
        Save();
    });

    public void Dump() => Enqueue(() =>
    {
        inspector = port.Inspect();
        var directory = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"ui-dump-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            port.GameVersion, Territory = client.TerritoryType, inspector.Message, inspector.Nodes,
        }, new JsonSerializerOptions { WriteIndented = true }));
        notice = "UI 节点已导出：" + path;
        chat.Print("[魔光键影] " + notice);
    });

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "start": Start(); break;
            case "bind": AutoBind(); break;
            case "stop": Stop(); break;
            case "dump": Dump(); break;
            default: window.Open(); break;
        }
    }

    private void OnUpdate(IFramework _)
    {
        if (disposed) return;
        try
        {
            var now = Environment.TickCount64;
            controller.ObserveEnterKey(keys[VirtualKey.RETURN], now);
            var startKeyDown = keys[VirtualKey.F9];
            var startPressed = startKeyWasDown == false && startKeyDown;
            startKeyWasDown = startKeyDown;
            // Hotkeys are processed every frame; F8 wins if both are held. F9 requires a fresh press.
            while (requests.TryDequeue(out var action)) action();
            if (keys[VirtualKey.F8])
            {
                if (controller.Running) controller.Stop("F8：已停止");
            }
            else if (startPressed && !controller.Running && port.IsForeground) StartOnFramework();
            heartbeat.Tick(now, controller.Running && !config.Options.ManualSubmit && !config.Options.KeyboardInput, config.Heartbeat);
            port.TickKeyboard(now, controller.Running);
            if (now < nextTick) return;
            nextTick = now + 50;
            if (now >= nextRoster)
            {
                nextRoster = now + 250;
                var roster = port.ReadTeamRoster(playerState.CharacterName);
                if (roster != null) statistics.SetRoster(roster);
                else statistics.End(now);
            }
            controller.Tick(now);
            if (!controller.Running) port.ReleaseKeyboard();
            if (inspectRequested && now >= nextScan)
            {
                nextScan = now + 750;
                inspector = port.Inspect();
            }
            Volatile.Write(ref view, MakeView(port.Read()));
        }
        catch (Exception ex)
        {
            controller.Stop("读取或输入异常，已停止；详情见 /xllog");
            heartbeat.Release();
            port.ReleaseKeyboard();
            notice = ex.Message;
            log.Error(ex, "FFMoGuang update failed");
            Volatile.Write(ref view, MakeView(new(false, "", "", [], controller.Status)));
        }
    }

    private PluginView MakeView(AnswerFrame preview) => new(controller.Running, controller.Status,
        controller.LastWord, controller.Submissions, controller.ManualSubmissions, port.GameVersion, client.TerritoryType, config.Territory,
        config.Input?.Key ?? "未绑定", config.Words.Select(w => w.Key).ToArray(), config.Options,
        preview, inspector, notice, statistics.Snapshot(Environment.TickCount64, config.RankingMetric),
        config.RankingMetric, config.Heartbeat, heartbeat.Status, capture.Status, port.KeyboardStatus, controller.PendingSubmitDelayMs);

    private void Enqueue(Action action) { if (!disposed) requests.Enqueue(action); }
    private void Save() => pluginInterface.SavePluginConfig(config);

    public void Dispose()
    {
        disposed = true;
        framework.Update -= OnUpdate;
        pluginInterface.UiBuilder.Draw -= window.Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= window.Open;
        pluginInterface.UiBuilder.OpenMainUi -= window.Open;
        commands.RemoveHandler("/mg");
        port.AutomaticSubmitted -= OnAutomaticSubmitted;
        controller.ManualSubmissionObserved -= OnManualSubmissionObserved;
        controller.KeyboardSubmissionObserved -= OnKeyboardSubmissionObserved;
        capture.Dispose();
        heartbeat.Release();
        port.ReleaseKeyboard();
        // No native writes during unload, and no persisted auto-start flag.
        controller.Stop();
    }
}
