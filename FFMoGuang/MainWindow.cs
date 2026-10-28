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

    public void Open() => open = true;

    public void Draw()
    {
        if (!open) { plugin.SetInspecting(false); return; }
        ImGui.SetNextWindowSize(new Vector2(800, 650), ImGuiCond.FirstUseEver);
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
            delaysInitialized = true;
        }
        ImGui.TextUnformatted(state.Status);
        ImGui.BeginDisabled(state.Running);
        if (ImGui.Button("开始", new Vector2(100, 0)))
        {
            ClampDelayInputs();
            plugin.Start(operationDelayMs, intervalMs);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("停止（F8）", new Vector2(100, 0))) plugin.Stop();
        ImGui.SameLine();
        ImGui.BeginDisabled(state.Running);
        if (ImGui.Button("自动绑定魔光键影")) plugin.AutoBind();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextUnformatted($"提交调用：{state.Submissions}    最近词条：{state.LastWord}");
        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("操作延迟（ms，填入 → 提交）", ref operationDelayMs);
        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("答题间隔（ms）", ref intervalMs);
        if (ImGui.Button("保存延迟"))
        {
            ClampDelayInputs();
            plugin.SetOptions(state.Options with { FillDelayMs = operationDelayMs, IntervalMs = intervalMs });
        }
        ImGui.SameLine();
        ImGui.TextUnformatted($"已保存：操作 {state.Options.FillDelayMs} ms / 间隔 {state.Options.IntervalMs} ms");
        ImGui.TextWrapped("范围 0–60000 ms；0 表示不额外等待。点击开始会保存当前延迟；运行中保存会停止，需重新开始。");
        ImGui.TextWrapped(state.Notice);
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
    }
}
