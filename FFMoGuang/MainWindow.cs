using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FFMoGuang;

public sealed class MainWindow(Plugin plugin)
{
    private bool open = true;
    private string filter = "";
    private bool inputsOnly;
    private bool includeHidden;
    private bool delaysInitialized;
    private int operationDelayMs;
    private int intervalMs;
    private bool randomizeSubmitDelay;
    private int submitDelayMinMs;
    private int submitDelayMaxMs;

    public void Open() => open = true;

    public void Draw()
    {
        plugin.SetKeyboardCapture(ImGui.GetIO().WantCaptureKeyboard || ImGui.GetIO().WantTextInput);
        if (!open) { plugin.SetInspecting(false); return; }
        ImGui.SetNextWindowSize(new Vector2(860, 800), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("魔光键影助手###FFMoGuang", ref open))
        {
            plugin.SetInspecting(false);
            ImGui.End();
            return;
        }
        var state = plugin.View;
        if (!delaysInitialized)
        {
            operationDelayMs = state.Options.FillDelayMs;
            intervalMs = state.Options.IntervalMs;
            randomizeSubmitDelay = state.Options.RandomizeSubmitDelay;
            submitDelayMinMs = state.Options.SubmitDelayMinMs;
            submitDelayMaxMs = state.Options.SubmitDelayMaxMs;
            delaysInitialized = true;
        }
        ImGui.TextUnformatted(state.Status);
        ImGui.BeginDisabled(state.Running);
        if (ImGui.Button("开始（F9）", new Vector2(120, 0)))
        {
            ClampDelayInputs();
            plugin.Start(operationDelayMs, intervalMs, randomizeSubmitDelay, submitDelayMinMs, submitDelayMaxMs);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("停止（F8）", new Vector2(100, 0))) plugin.Stop();
        ImGui.SameLine();
        ImGui.BeginDisabled(state.Running);
        if (ImGui.Button("自动绑定魔光键影")) plugin.AutoBind();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextUnformatted($"自动提交请求：{state.Submissions}    手动 Enter 清空：{state.ManualSubmissions}");
        ImGui.TextUnformatted("最近词条：" + state.LastWord);
        if (ImGui.RadioButton("自动提交", !state.Options.ManualSubmit))
            plugin.SetOptions(state.Options with { ManualSubmit = false });
        ImGui.SameLine();
        if (ImGui.RadioButton("手动 Enter 提交", state.Options.ManualSubmit))
            plugin.SetOptions(state.Options with { ManualSubmit = true });
        if (ImGui.RadioButton("直接填词", !state.Options.KeyboardInput))
            plugin.SetOptions(state.Options with { KeyboardInput = false, PinyinInput = false });
        ImGui.SameLine();
        if (ImGui.RadioButton("Unicode 逐字输入（实验）", state.Options.KeyboardInput && !state.Options.PinyinInput))
            plugin.SetOptions(state.Options with { KeyboardInput = true, PinyinInput = false });
        if (ImGui.RadioButton("搜狗全拼字母输入（实验）", state.Options.PinyinInput))
            plugin.SetOptions(state.Options with { KeyboardInput = true, PinyinInput = true, RandomizeDelays = true });
        if (state.Options.KeyboardInput)
        {
            var characterDelay = state.Options.CharacterDelayMs;
            if (ImGui.InputInt("逐字间隔（ms，0–1000）", ref characterDelay))
                plugin.SetOptions(state.Options with { CharacterDelayMs = characterDelay });
            var randomize = state.Options.RandomizeDelays;
            if (ImGui.Checkbox("随机输入节奏", ref randomize))
                plugin.SetOptions(state.Options with { RandomizeDelays = randomize });
            if (randomize)
            {
                var min = state.Options.RandomMinMs; var max = state.Options.RandomMaxMs;
                if (ImGui.InputInt("随机间隔下限（ms）", ref min))
                    plugin.SetOptions(state.Options with { RandomMinMs = min });
                if (ImGui.InputInt("随机间隔上限（ms）", ref max))
                    plugin.SetOptions(state.Options with { RandomMaxMs = max });
                ImGui.TextWrapped("间隔范围 20–1000ms；另有约 12% 概率停顿 180–420ms。全拼按键按住 35–80ms，关闭随机后为 50ms。随机节奏不保证游戏将其识别为活动。");
            }
            if (state.Options.PinyinInput)
            {
                ImGui.TextWrapped(state.KeyboardStatus);
                ImGui.TextWrapped("使用搜狗全拼中文状态、关闭大写锁定。按字输入拼音，读取候选页并只选相同汉字，最多翻页 10 次。多音字用字典首读音；候选读不到或文字不符会停止。标点及非汉字仍走 Unicode。此模式尚未实机验证，Shift 心跳暂停，避免切换搜狗中英文。");
            }
            else ImGui.TextWrapped("通过 Windows 逐字发送 Unicode，再模拟 Enter 或等待手动 Enter；不会经拼音输入法组字。");
            ImGui.TextWrapped("开始后点击小游戏输入栏，光标放在末尾且不选中文字。中断会保留残留文字与输入法组合，重新开始前请手动取消组合并清空。最终文字不符不会提交；AFK 计时重置效果待实测。");
        }
        ImGui.TextWrapped("切换模式会保存并停止运行，检查或清空游戏输入栏后重新开始。");
        if (state.Options.ManualSubmit)
            ImGui.TextWrapped("手动模式只自动填词，请保持游戏输入框焦点并按 Enter 提交。等待按键不超时；按键后等待清空最多 2 秒。操作延迟不适用于此模式，键盘心跳暂停。");
        ImGui.BeginDisabled(state.Options.ManualSubmit);
        ImGui.Checkbox("自动提交使用随机操作延迟", ref randomizeSubmitDelay);
        if (randomizeSubmitDelay)
        {
            ImGui.SetNextItemWidth(160);
            ImGui.InputInt("操作延迟下限（ms）", ref submitDelayMinMs);
            ImGui.SetNextItemWidth(160);
            ImGui.InputInt("操作延迟上限（ms）", ref submitDelayMaxMs);
        }
        else
        {
            ImGui.SetNextItemWidth(160);
            ImGui.InputInt("操作延迟（ms，填入 → 提交）", ref operationDelayMs);
        }
        ImGui.EndDisabled();
        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("答题间隔（ms）", ref intervalMs);
        if (ImGui.Button("保存延迟"))
        {
            ClampDelayInputs();
            plugin.SetOptions(state.Options with
            {
                FillDelayMs = operationDelayMs, IntervalMs = intervalMs, RandomizeSubmitDelay = randomizeSubmitDelay,
                SubmitDelayMinMs = submitDelayMinMs, SubmitDelayMaxMs = submitDelayMaxMs,
            });
        }
        ImGui.SameLine();
        var savedOperation = state.Options.RandomizeSubmitDelay
            ? $"随机 {state.Options.SubmitDelayMinMs}–{state.Options.SubmitDelayMaxMs}" : state.Options.FillDelayMs.ToString();
        ImGui.TextUnformatted($"已保存：操作 {savedOperation} ms / 间隔 {state.Options.IntervalMs} ms");
        if (state.SubmitDelayMs.HasValue) ImGui.TextUnformatted($"本次自动提交等待：{state.SubmitDelayMs.Value} ms");
        ImGui.TextWrapped("范围 0–60000 ms；输入完成后每次独立取值，等待中不重新抽取。上限低于下限时按下限保存。点击开始会保存当前延迟；F9 使用已保存参数。运行中保存会停止。");
        ImGui.TextWrapped(state.Notice);
        ImGui.Separator();
        DrawLeaderboard(state);
        if (ImGui.CollapsingHeader("键盘心跳（防无输入超时，待实机验证）"))
        {
            var enabled = state.Heartbeat.Enabled;
            if (ImGui.Checkbox("启用前台键盘心跳（左 Shift）", ref enabled))
                plugin.SetHeartbeat(state.Heartbeat with { Enabled = enabled });
            var interval = state.Heartbeat.IntervalMs;
            if (ImGui.InputInt("心跳间隔（ms，10000–45000）", ref interval, 1000, 5000))
                plugin.SetHeartbeat(state.Heartbeat with { IntervalMs = interval });
            ImGui.TextWrapped(state.HeartbeatStatus);
            ImGui.TextWrapped("仅直接填词加自动提交时运行；手动 Enter 或逐字输入时暂停。在游戏前台、输入框可用且没有按住修饰键时短按左 Shift。60 秒及 15 分钟的超时是否重置尚待实测，系统接受按键不等于游戏确认重置。停止、失焦及卸载时释放插件按下的键。");
        }
        if (ImGui.CollapsingHeader("发送数据采样（接入队友统计）"))
        {
            ImGui.TextWrapped("已有采样被每帧重复刷新挤满，尚未确认队友发送来源。本版过滤布局、按命令和队员去重，仅计分刷新限频为 100ms；提交观察不去重。后续在对局中采样 30 秒，再导出本机 JSON。");
            if (ImGui.Button("开始采样 30 秒")) plugin.StartCapture();
            ImGui.SameLine();
            if (ImGui.Button("停止并导出采样")) plugin.ExportCapture();
            ImGui.TextUnformatted(state.CaptureStatus);
        }
        ImGui.Separator();
        ImGui.TextUnformatted($"客户端：{state.GameVersion}    当前区域：{state.Territory}    绑定区域：{(state.BoundTerritory == 0 ? "未绑定" : state.BoundTerritory.ToString())}");
        ImGui.TextWrapped("输入框：" + state.InputBinding);
        if (!state.Preview.Ready) ImGui.TextWrapped("当前不可输入：" + state.Preview.Reason);
        else
        {
            ImGui.TextUnformatted("输入内容：" + state.Preview.Input);
            ImGui.TextWrapped("可见目标：" + string.Join(" | ", state.Preview.Targets.Select(t => t.Text)));
        }

        if (ImGui.CollapsingHeader($"已绑定词条（{state.WordBindings.Length}）"))
        {
            foreach (var key in state.WordBindings)
            {
                ImGui.PushID(key);
                if (ImGui.SmallButton("移除")) plugin.RemoveWord(key);
                ImGui.SameLine();
                ImGui.TextUnformatted(key);
                ImGui.PopID();
            }
            if (ImGui.Button("清除全部绑定")) plugin.ResetBindings();
        }

        if (ImGui.CollapsingHeader("答题参数"))
        {
            var stable = state.Options.StableMs;
            var maxAttempts = state.Options.MaxAttemptsPerWord;
            if (ImGui.InputInt("词条稳定时间（毫秒）", ref stable))
                plugin.SetOptions(state.Options with { StableMs = stable });
            if (ImGui.InputInt("同一可见词条提交上限（1–100）", ref maxAttempts))
                plugin.SetOptions(state.Options with { MaxAttemptsPerWord = maxAttempts });
            ImGui.TextWrapped("多目标轮流输入；输入未清空、手动改字、界面关闭、切区域或窗口失焦会停止。修改参数后需重新开启。");
        }

        var inspecting = ImGui.CollapsingHeader("节点检查与绑定（首次使用必需）");
        plugin.SetInspecting(inspecting);
        if (inspecting)
        {
            ImGui.TextWrapped("在实际对局中：先勾选“仅输入框”并绑定游戏输入栏，再取消勾选，搜索屏幕上的完整词条，绑定各个目标槽位。检查预览后开启。");
            ImGui.InputText("搜索界面名 / 词条 / 路径", ref filter, 256);
            ImGui.Checkbox("仅输入框", ref inputsOnly);
            ImGui.SameLine();
            ImGui.Checkbox("包括隐藏节点", ref includeHidden);
            if (ImGui.Button("立即刷新")) plugin.Refresh();
            ImGui.SameLine();
            if (ImGui.Button("导出 UI 节点 JSON")) plugin.Dump();
            ImGui.TextWrapped(state.Inspector.Message);
            ImGui.BeginChild("nodes", new Vector2(0, 0));
            var shown = 0;
            foreach (var row in state.Inspector.Nodes)
            {
                if (inputsOnly && !row.IsInput || !includeHidden && !row.Visible) continue;
                var key = row.Binding.Key;
                if (filter.Length != 0 && !key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !row.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                if (++shown > 300) { ImGui.TextUnformatted("结果过多，请缩小搜索范围。"); break; }
                ImGui.PushID(key);
                var selected = row.IsInput ? key == state.InputBinding : state.WordBindings.Contains(key);
                if (selected) ImGui.TextUnformatted("已绑定");
                else
                {
                    ImGui.BeginDisabled(!row.Visible || row.IsInput && !row.HasCallback);
                    if (ImGui.SmallButton(row.IsInput ? "绑定输入框" : "绑定词条")) plugin.Bind(row);
                    ImGui.EndDisabled();
                }
                ImGui.SameLine();
                ImGui.TextUnformatted($"{(row.IsInput ? "[输入]" : "[文字]")} {key}");
                ImGui.TextWrapped(row.Text.Length == 0 ? "（空）" : row.Text);
                ImGui.Separator();
                ImGui.PopID();
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private void ClampDelayInputs()
    {
        operationDelayMs = Math.Clamp(operationDelayMs, 0, 60000);
        intervalMs = Math.Clamp(intervalMs, 0, 60000);
        submitDelayMinMs = Math.Clamp(submitDelayMinMs, 0, 60000);
        submitDelayMaxMs = Math.Clamp(submitDelayMaxMs, submitDelayMinMs, 60000);
    }

    private void DrawLeaderboard(PluginView state)
    {
        if (!ImGui.CollapsingHeader("四人发送排行榜", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (ImGui.RadioButton("按速度", state.RankingMetric == Core.RankingMetric.Speed))
            plugin.SetRankingMetric(Core.RankingMetric.Speed);
        ImGui.SameLine();
        if (ImGui.RadioButton("按累计字数", state.RankingMetric == Core.RankingMetric.Characters))
            plugin.SetRankingMetric(Core.RankingMetric.Characters);
        ImGui.SameLine();
        if (ImGui.SmallButton("清空统计")) plugin.ResetStatistics();
        var rank = 0;
        foreach (var player in state.Leaderboard.Players)
        {
            rank++;
            var known = player.Messages.HasValue;
            ImGui.TextUnformatted($"{(known ? $"#{rank}" : "—")} {player.Name}{(player.IsSelf ? "（自己·辅助发送）" : "")}");
            var overlay = known
                ? $"{player.CharactersPerMinute:0.0} 字/分    {player.Messages} 次    {player.Characters} 字"
                : "暂无发送数据";
            var color = !known ? new Vector4(.35f, .35f, .38f, 1)
                : player.IsSelf ? new Vector4(.2f, .6f, .95f, 1) : new Vector4(.25f, .7f, .45f, 1);
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
            ImGui.ProgressBar(player.Progress, new Vector2(-1, 22), overlay);
            ImGui.PopStyleColor();
        }
        ImGui.TextWrapped(state.Leaderboard.Status);
    }
}
