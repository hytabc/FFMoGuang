using Dalamud.Configuration;
using FFMoGuang.Core;

namespace FFMoGuang;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string GameVersion { get; set; } = "";
    public uint Territory { get; set; }
    public NodeBinding? Input { get; set; }
    public List<NodeBinding> Words { get; set; } = [];
    public AnswerOptions Options { get; set; } = new();
    public HeartbeatOptions Heartbeat { get; set; } = new();
    public HotkeyOptions Hotkeys { get; set; } = new();
    public RankingMetric RankingMetric { get; set; } = RankingMetric.Speed;
}
