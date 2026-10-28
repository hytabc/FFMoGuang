using System.Collections.Concurrent;
using System.Text.Json;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFMoGuang.Core;

namespace FFMoGuang;

public sealed record PluginView(bool Running, string Status, string LastWord, int Submissions,
    string GameVersion, uint Territory, uint BoundTerritory, string InputBinding, string[] WordBindings,
    AnswerOptions Options, AnswerFrame Preview, InspectorSnapshot Inspector, string Notice);

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
    private readonly ConcurrentQueue<Action> requests = new();
    private InspectorSnapshot inspector = new([], "展开检查器后扫描当前游戏 UI");
    private string notice = "进入魔光键影对局后，可点击“自动绑定魔光键影”；首次点击“开始”也会自动识别。";
    private long nextScan;
    private long nextTick;
    private volatile bool inspectRequested;
    private volatile bool disposed;
    private PluginView view;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework,
        IGameGui gameGui, IClientState clientState, IKeyState keyState, IPluginLog pluginLog, IChatGui chatGui)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.framework = framework;
        client = clientState;
        keys = keyState;
        log = pluginLog;
        chat = chatGui;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new();
        config.Words ??= [];
        config.Options = (config.Options ?? new()).Clamp();
        port = new(config, gameGui, client);
        controller = new(port);
        view = MakeView(new(false, "", "", [], "尚未读取游戏 UI"));
        window = new(this);
        commands.AddHandler("/mg", new CommandInfo(OnCommand)
        {
            HelpMessage = "魔光键影助手：/mg 面板；/mg bind 自动绑定；/mg start 启动；/mg stop 停止；/mg dump 导出节点。F8 停止。",
        });
        pluginInterface.UiBuilder.Draw += window.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += window.Open;
        pluginInterface.UiBuilder.OpenMainUi += window.Open;
        framework.Update += OnUpdate;
    }

    public PluginView View => Volatile.Read(ref view);
    public void SetInspecting(bool value) => inspectRequested = value;
    public void Start(int? operationDelayMs = null, int? intervalMs = null) => Enqueue(() =>
    {
        if (controller.Running) return;
        if (operationDelayMs.HasValue || intervalMs.HasValue)
        {
            config.Options = (config.Options with
            {
                FillDelayMs = operationDelayMs ?? config.Options.FillDelayMs,
                IntervalMs = intervalMs ?? config.Options.IntervalMs,
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
    });
    public void Stop() => Enqueue(() => controller.Stop());
    public void AutoBind() => Enqueue(() => { controller.Stop(); TryAutoBind(); });
    public void Refresh() => Enqueue(() => { nextScan = 0; inspector = port.Inspect(); });

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
            // Stop requests and F8 are processed every frame, independently of the answer polling rate.
            while (requests.TryDequeue(out var action)) action();
            if (controller.Running && keys[VirtualKey.F8]) controller.Stop("F8：已停止");
            var now = Environment.TickCount64;
            if (now < nextTick) return;
            nextTick = now + 50;
            controller.Tick(now);
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
            notice = ex.Message;
            log.Error(ex, "FFMoGuang update failed");
            Volatile.Write(ref view, MakeView(new(false, "", "", [], controller.Status)));
        }
    }

    private PluginView MakeView(AnswerFrame preview) => new(controller.Running, controller.Status,
        controller.LastWord, controller.Submissions, port.GameVersion, client.TerritoryType, config.Territory,
        config.Input?.Key ?? "未绑定", config.Words.Select(w => w.Key).ToArray(), config.Options,
        preview, inspector, notice);

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
        // No native writes during unload, and no persisted auto-start flag.
        controller.Stop();
    }
}
