using System.Globalization;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using Dalamud.Game.Text.SeStringHandling;
using FFMoGuang.Core;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FFMoGuang;

public sealed record CaptureExport(string GameVersion, uint Territory, DateTimeOffset StartedAt,
    int DroppedEvents, CapturedEvent[] Events, int ReceivedEvents, int DuplicateEvents,
    int LayoutEvents, int ThrottledScoreEvents);

/// <summary>Opt-in, bounded diagnostics. Copies refresh values while their native lifetime is valid.</summary>
public sealed unsafe class GameEventCapture : IDisposable
{
    private static readonly string[] Addons = ["KTGInput", "KTGPlayerName", "KTGEWord", "KTGHudLog",
        "KTGHudScore", "KTGResult", "KTGScreenTextStart", "KTGScreenTextClear", "KTGScreenTextGameOver"];
    private readonly IAddonLifecycle lifecycle;
    private readonly IClientState client;
    private readonly IPluginLog log;
    private readonly object gate = new();
    private readonly CaptureBuffer events = new();
    private long started;
    private long ends;
    private DateTimeOffset startedAt;
    private string gameVersion = "";
    public GameEventCapture(IAddonLifecycle lifecycle, IClientState client, IPluginLog log)
    {
        this.lifecycle = lifecycle;
        this.client = client;
        this.log = log;
        lifecycle.RegisterListener(AddonEvent.PreRefresh, Addons, OnEvent);
        lifecycle.RegisterListener(AddonEvent.PostSetup, Addons, OnEvent);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, Addons, OnEvent);
    }
    public void Start(string version)
    {
        lock (gate)
        {
            events.Clear();
            started = Environment.TickCount64;
            ends = started + 30000;
            startedAt = DateTimeOffset.UtcNow;
            gameVersion = version;
        }
    }
    public string Status
    {
        get
        {
            lock (gate)
                return ends > Environment.TickCount64
                    ? $"采样中：剩余 {(ends - Environment.TickCount64 + 999) / 1000} 秒，{events.Count} 条事件"
                    : $"采样记录：{events.Count} 条，去重 {events.Duplicates}，布局 {events.Layout}，计分限频 {events.Throttled}，溢出 {events.Dropped}";
        }
    }
    public void RecordAutomatic(string text) => Append("KTGInput", "AutomaticSubmit", [new("String", text)]);
    public void RecordManual(string text) => Append("KTGInput", "ManualEnterAndClear", [new("String", text)]);
    public void RecordKeyboard(string text) => Append("KTGInput", "KeyboardEnterAndClear", [new("String", text)]);
    public CaptureExport Export()
    {
        lock (gate)
        {
            ends = 0;
            return new(gameVersion, KeyboundProfile.SupportedTerritory, startedAt, events.Dropped, events.Snapshot(),
                events.Received, events.Duplicates, events.Layout, events.Throttled);
        }
    }
    private void OnEvent(AddonEvent eventType, AddonArgs args)
    {
        lock (gate)
        {
            if (ends <= Environment.TickCount64 || client.TerritoryType != KeyboundProfile.SupportedTerritory) return;
        }
        try
        {
            var values = new List<CapturedValue>();
            if (args is AddonRefreshArgs refresh)
            {
                var data = (AtkValue*)refresh.AtkValues;
                if (data != null)
                    for (var i = 0; i < Math.Min(refresh.AtkValueCount, 64); i++) values.Add(Copy(data[i]));
            }
            Append(args.AddonName, eventType.ToString(), values.ToArray());
        }
        catch (Exception ex) { log.Error(ex, "FFMoGuang could not copy diagnostic refresh values"); }
    }
    private void Append(string addon, string kind, CapturedValue[] values)
    {
        lock (gate)
        {
            var now = Environment.TickCount64;
            if (ends <= now) return;
            events.Add(new(now - started, addon, kind, values));
        }
    }
    private static CapturedValue Copy(AtkValue value)
    {
        var type = (uint)value.Type & 0xF;
        var text = type switch
        {
            2 => value.Bool.ToString(),
            3 => value.Int.ToString(CultureInfo.InvariantCulture),
            4 => value.Int64.ToString(CultureInfo.InvariantCulture),
            5 => value.UInt.ToString(CultureInfo.InvariantCulture),
            6 => value.UInt64.ToString(CultureInfo.InvariantCulture),
            7 => value.Float.ToString(CultureInfo.InvariantCulture),
            8 or 10 => "",
            _ => "（未展开）",
        };
        return type is 8 or 10 ? ReadString(value.Type.ToString(), value.String.Value) : new(value.Type.ToString(), text);
    }
    private static CapturedValue ReadString(string type, byte* pointer)
    {
        if (pointer == null) return new(type, "");
        var length = 0;
        while (length < 512 && pointer[length] != 0) length++;
        var bytes = new ReadOnlySpan<byte>(pointer, length).ToArray();
        // Preserve the original SeString bytes: decoding payloads as UTF-8 destroyed some name/icon data before.
        return new(type, SeString.Parse(bytes).TextValue, Convert.ToBase64String(bytes));
    }
    public void Dispose()
    {
        lock (gate) ends = 0;
        lifecycle.UnregisterListener(OnEvent);
    }
}
