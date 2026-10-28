using FFMoGuang.Core;

namespace FFMoGuang;

public sealed record BindingProfile(string GameVersion, uint Territory, NodeBinding? Input, IReadOnlyList<NodeBinding> Words)
{
    public string? GetProblem(uint currentTerritory, string currentGameVersion)
    {
        // Missing bindings are not a territory mismatch: zero is the initial configuration value.
        if (Input == null || Words.Count == 0 || Territory == 0 || GameVersion.Length == 0)
            return "尚未完成绑定：请在魔光键影对局中点击“自动绑定魔光键影”，或点击“开始”自动识别";
        if (currentTerritory != Territory)
            return $"当前区域 {currentTerritory} 与绑定区域 {Territory} 不一致，请进入已绑定的魔光键影对局";
        if (currentGameVersion.Length == 0) return "无法读取当前游戏版本，未启用输入";
        if (currentGameVersion != GameVersion) return "游戏版本已变化，请重新绑定输入框与词条";
        return null;
    }
}

/// <summary>Observed in both user exports from territory 1359 on 2026-10-28.</summary>
public static class KeyboundProfile
{
    public const uint SupportedTerritory = 1359;
    public const string SupportedVersion = "2026.10.06.0000.0000";

    public static bool TryCreate(string gameVersion, uint territory, IReadOnlyList<InspectedNode> nodes,
        out BindingProfile? profile, out string problem)
    {
        profile = null;
        if (territory != SupportedTerritory)
        {
            problem = $"自动绑定需要进入魔光键影对局（区域 {SupportedTerritory}），当前区域为 {territory}";
            return false;
        }
        if (gameVersion != SupportedVersion)
        {
            problem = "当前游戏版本尚无内置节点适配，请使用节点检查器手动绑定";
            return false;
        }
        var input = new NodeBinding("KTGInput", 1, [7]);
        var inputRows = nodes.Where(n => n.Binding.Key == input.Key).ToArray();
        if (inputRows.Length != 1 || !inputRows[0].IsInput || !inputRows[0].Visible || !inputRows[0].HasCallback)
        {
            problem = "未找到可用的魔光键影输入框 KTGInput[1]/7，请进入实际对局后重试";
            return false;
        }

        var words = new List<NodeBinding>();
        var visibleWords = 0;
        for (var slot = 0; slot < 20; slot++)
        {
            var container = slot == 0 ? 2u : (uint)(20000 + slot);
            var binding = new NodeBinding("KTGEWord", 1, [container, 20]);
            var matches = nodes.Where(n => n.Binding.Key == binding.Key).ToArray();
            // All slots, including hidden slots for later waves, must match the actual export shape.
            if (matches.Length != 1 || matches[0].IsInput)
            {
                problem = $"词条槽位结构不匹配：{binding.Key}；请导出节点或手动绑定";
                return false;
            }
            if (matches[0].Visible && AnswerController.IsValid(new(binding.Key, matches[0].Text))) visibleWords++;
            words.Add(binding);
        }
        if (visibleWords == 0)
        {
            problem = "已找到小游戏控件，但当前没有可见目标词条，请等敌人出现后重试";
            return false;
        }
        profile = new(gameVersion, territory, input, words);
        problem = "";
        return true;
    }
}
