using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFMoGuang.Core;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FFMoGuang;

/// <summary>Only call on Framework.Update. Never retain native pointers between calls.</summary>
public sealed unsafe class GameUiPort(Configuration config, IGameGui gameGui, IClientState clientState,
    Func<bool> keyboardCaptured) : IAnswerPort
{
    private readonly KeyboardTextInput textKeyboard = new(new NativeTextKeyboard());
    private PinyinComposer? pinyin;
    private string pinyinSession = "";
    private WordTarget? pinyinTarget;
    public string GameVersion { get; } = ReadGameVersion();
    public bool IsForeground => IsGameForeground();
    public event Action<string>? AutomaticSubmitted;
    public string KeyboardStatus => pinyin?.Status ?? "搜狗全拼尚未输入；请切换中文状态并关闭大写锁定";

    public TeamRoster? ReadTeamRoster(string localName)
    {
        if (!clientState.IsLoggedIn || clientState.TerritoryType != KeyboundProfile.SupportedTerritory
            || GameVersion != KeyboundProfile.SupportedVersion) return null;
        var inputNode = Resolve(new("KTGInput", 1, [7]), out var inputAddon, out var inputShown);
        if (inputNode == null || !inputShown || AsInput(inputNode) == null) return null;
        var members = new List<TeamMember>();
        for (var slot = 0; slot < 4; slot++)
        {
            var node = Resolve(new("KTGPlayerName", 1, [slot == 0 ? 2u : (uint)(20000 + slot), 3]), out _, out var visible);
            if (node == null || !visible || node->Type != NodeType.Text) continue;
            var name = ReadText((AtkTextNode*)node).Trim();
            if (name.Length != 0) members.Add(new(slot, name, name == localName));
        }
        return members.Count == 0 ? null : new($"{inputAddon->Id}:{(nint)inputAddon:X}", members);
    }

    public bool HeartbeatReady()
    {
        if (CheckContext() != null || clientState.TerritoryType != KeyboundProfile.SupportedTerritory) return false;
        var input = AsInput(Resolve(config.Input!, out _, out var visible));
        return input != null && visible && input->Enabled;
    }

    public AnswerFrame Read()
    {
        var reason = CheckContext();
        if (reason != null) return Unavailable(reason);
        if (config.Options.PinyinInput && pinyin is { Error.Length: > 0 }) return Unavailable(pinyin.Error);
        var node = Resolve(config.Input!, out var addon, out var visible);
        var input = AsInput(node);
        if (input == null || !visible || !input->Enabled) return Unavailable("输入控件不存在、不可见或已禁用");
        if (input->OwnerAddon != addon || input->Callback == null) return Unavailable("输入控件缺少有效的提交回调");

        var words = new List<WordTarget>();
        foreach (var binding in config.Words)
        {
            var word = Resolve(binding, out _, out var shown);
            if (word == null || !shown || word->Type != NodeType.Text) continue;
            var text = ReadText((AtkTextNode*)word);
            // Include the live node address so re-created targets are not mistaken for an old target.
            var target = new WordTarget($"{binding.Key}@{(nint)word:X}", text);
            if (AnswerController.IsValid(target)) words.Add(target);
        }
        var session = $"{clientState.TerritoryType}/{addon->Id}:{(nint)addon:X}/{(nint)input:X}";
        return new(true, session, input->RawString.ToString(), words,
            KeyboardReady: !config.Options.KeyboardInput || KeyboardFocused(input), Composing: pinyin?.Active == true);
    }

    private bool KeyboardFocused(AtkComponentTextInput* input)
    {
        if (keyboardCaptured() || input == null || !input->IsActive) return false;
        var stage = AtkStage.Instance();
        if (stage == null || stage->AtkInputManager == null || !stage->AtkInputManager->IsTextInputActive) return false;
        var text = stage->AtkInputManager->TextInput;
        return text != null && text->TargetTextInputEventInterface == &input->AtkTextInputEventInterface
            && (pinyin?.Active == true || text->SelectionStart == text->SelectionEnd && text->CursorPos == text->TextLength);
    }

    public void TickKeyboard(long now, bool running)
    {
        // Releases are allowed after focus loss, but never start an input outside the guarded methods below.
        var input = CheckContext() == null ? AsInput(Resolve(config.Input!, out _, out _)) : null;
        textKeyboard.Tick(now, running && config.Options.KeyboardInput && KeyboardFocused(input));
        if (!running || !config.Options.PinyinInput) pinyin?.Cancel();
        else if (pinyin?.Active == true)
        {
            var frame = Read();
            pinyin.Tick(now, pinyinTarget != null && Matches(frame, pinyinSession, pinyinTarget)
                && frame.KeyboardReady && NativeTextKeyboard.ModifiersReleased(), frame.Input);
        }
    }
    public void ReleaseKeyboard() { pinyin?.Cancel(); textKeyboard.Release(); }

    public OperationResult TypeCharacter(string session, WordTarget target, string prefix, string character)
    {
        var frame = Read();
        if (!config.Options.KeyboardInput || !Matches(frame, session, target) || !frame.KeyboardReady
            || frame.Input != prefix || !target.Text.StartsWith(prefix + character, StringComparison.Ordinal))
            return new(false, "逐字输入前目标、焦点或输入内容变化，已停止");
        if (!NativeTextKeyboard.ModifiersReleased()) return new(false, "逐字输入时检测到 Enter 或修饰键，已停止");
        if (!Rune.TryGetRuneAt(character, 0, out var rune) || rune.Utf16SequenceLength != character.Length)
            return new(false, "逐字输入字符无效，已停止");
        if (config.Options.PinyinInput && PinyinDictionary.IsHan(rune))
        {
            pinyin ??= new(textKeyboard, NativeIme.Read, new());
            pinyinSession = session;
            pinyinTarget = target;
            return pinyin.Begin(rune, prefix, Environment.TickCount64, config.Options);
        }
        return textKeyboard.TypeCharacter(rune);
    }

    public OperationResult PressEnter(string session, WordTarget target)
    {
        var frame = Read();
        if (!config.Options.KeyboardInput || !Matches(frame, session, target) || !frame.KeyboardReady || frame.Input != target.Text)
            return new(false, "模拟 Enter 前目标、焦点或输入内容变化，已停止");
        var input = AsInput(Resolve(config.Input!, out _, out _));
        if (input == null || input->EvaluatedString.ToString() != target.Text || !NativeTextKeyboard.ModifiersReleased())
            return new(false, "模拟 Enter 前求值文本或修饰键不符合要求，已停止");
        if (config.Options.PinyinInput && (pinyin?.Active == true || NativeIme.Read().Composition.Length != 0))
            return new(false, "仍有拼音组合未确认，不发送 Enter");
        return textKeyboard.PressEnter(Environment.TickCount64);
    }

    public OperationResult Fill(string session, WordTarget target)
    {
        var frame = Read();
        if (!Matches(frame, session, target) || frame.Input.Length != 0)
            return new(false, "填入前目标或输入状态发生变化，已停止");
        var input = AsInput(Resolve(config.Input!, out _, out _));
        if (input == null) return new(false, "输入控件已失效");
        input->SetText(target.Text);
        var after = Read();
        if (!Matches(after, session, target)) return new(false, "填入过程中目标或界面发生变化，未提交");
        input = AsInput(Resolve(config.Input!, out _, out _));
        // SetText applies the real control's length/character restrictions. Never submit truncated text.
        if (input == null || input->RawString.ToString() != target.Text || input->EvaluatedString.ToString() != target.Text)
            return new(false, "输入控件改变或截断了词条，未提交；请检查游戏输入框");
        return new(true);
    }

    public OperationResult Submit(string session, WordTarget target)
    {
        var frame = Read();
        if (!Matches(frame, session, target) || frame.Input != target.Text)
            return new(false, "提交前目标或输入状态发生变化，已停止");
        var input = AsInput(Resolve(config.Input!, out var addon, out _));
        if (input == null || input->OwnerAddon != addon || input->Callback == null)
            return new(false, "提交回调已失效");
        if (input->EvaluatedString.ToString() != target.Text)
            return new(false, "输入框实际求值内容与词条不一致，未提交");

        // Invoke the input's own registered Enter handler with its actual event kind.
        // Owned UTF-8 buffers remain stable even if the callback modifies the input strings.
        var raw = Encoding.UTF8.GetBytes(target.Text + '\0');
        var evaluated = Encoding.UTF8.GetBytes(target.Text + '\0');
        InputCallbackResult result;
        fixed (byte* rawPtr = raw)
        fixed (byte* evaluatedPtr = evaluated)
            result = input->Callback(addon, InputCallbackType.Enter, rawPtr, evaluatedPtr, input->CallbackEventKind);

        // The normal input handler consumes ClearText. As we called its callback directly,
        // honor that return value ourselves, after resolving all pointers again.
        if (result == InputCallbackResult.ClearText) ClearOwnedInput(session, target.Text);
        else if (result != InputCallbackResult.None)
            return new(false, $"输入回调返回了不支持的结果 {result}，已停止");
        AutomaticSubmitted?.Invoke(target.Text);
        return new(true);
    }

    public void ClearOwnedInput(string session, string text)
    {
        var frame = Read();
        if (!frame.Ready || frame.Session != session || frame.Input != text) return;
        var input = AsInput(Resolve(config.Input!, out _, out _));
        if (input != null && input->RawString.ToString() == text) input->SetText("");
    }

    public bool CanBind(InspectedNode row)
    {
        if (!clientState.IsLoggedIn || GameVersion.Length == 0) return false;
        var node = Resolve(row.Binding, out var addon, out var visible);
        if (!visible || node == null) return false;
        if (!row.IsInput) return node->Type == NodeType.Text;
        var input = AsInput(node);
        return input != null && input->Enabled && input->OwnerAddon == addon && input->Callback != null;
    }

    public InspectorSnapshot Inspect()
    {
        if (!clientState.IsLoggedIn) return new([], "请先登录角色并进入小游戏");
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return new([], "游戏 UI 尚未就绪");
        var list = &stage->RaptureAtkUnitManager->AllLoadedUnitsList;
        var rows = new List<InspectedNode>();
        var visited = new HashSet<nint>();
        var budget = 30000;
        var count = Math.Min((int)list->Count, 256);
        for (var i = 0; i < count; i++)
        {
            var addon = list->Entries[i].Value;
            if (addon == null || !addon->IsReady || !addon->IsVisible) continue;
            var name = addon->NameString;
            var index = 0;
            for (var candidate = 1; candidate <= 256; candidate++)
            {
                var found = gameGui.GetAddonByName<AtkUnitBase>(name, candidate);
                if (found == null) break;
                if (found == addon) { index = candidate; break; }
            }
            if (index == 0) continue;
            InspectManager(&addon->UldManager, name, index, [], true, rows, visited, ref budget, 0);
        }
        return new(rows, $"可见界面中找到 {rows.Count} 个文本/输入节点。只绑定小游戏的实际词条，勿绑定提示文字。"
            + (budget <= 0 ? " 已达到扫描上限。" : ""));
    }

    private void InspectManager(AtkUldManager* manager, string addon, int index, uint[] prefix, bool parentVisible,
        List<InspectedNode> rows, HashSet<nint> visited, ref int budget, int depth)
    {
        if (depth > 12 || manager->NodeList == null || manager->NodeListCount > 10000) return;
        var ids = new HashSet<uint>();
        var duplicates = new HashSet<uint>();
        for (var i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node != null && !ids.Add(node->NodeId)) duplicates.Add(node->NodeId);
        }
        for (var i = 0; i < manager->NodeListCount && budget > 0; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || !visited.Add((nint)node) || duplicates.Contains(node->NodeId)) continue;
            budget--;
            var visible = parentVisible && IsShown(node);
            uint[] path = [.. prefix, node->NodeId];
            var binding = new NodeBinding(addon, index, path);
            if (node->Type == NodeType.Text)
                rows.Add(new(binding, false, ReadText((AtkTextNode*)node), visible, false));
            if ((ushort)node->Type < 1000) continue;
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null) continue;
            if (component->GetComponentType() == ComponentType.TextInput)
            {
                var input = (AtkComponentTextInput*)component;
                rows.Add(new(binding, true, input->RawString.ToString(), visible, input->Callback != null));
                // Do not offer the input's internal display text as an attack target.
                continue;
            }
            InspectManager(&component->UldManager, addon, index, path, visible, rows, visited, ref budget, depth + 1);
        }
    }

    private AtkResNode* Resolve(NodeBinding binding, out AtkUnitBase* addon, out bool visible)
    {
        visible = false;
        addon = null;
        if (binding.Path == null || binding.Path.Length is 0 or > 13 || binding.Index is < 1 or > 256) return null;
        addon = gameGui.GetAddonByName<AtkUnitBase>(binding.Addon, binding.Index);
        if (addon == null || !addon->IsReady || !addon->IsVisible) return null;
        var manager = &addon->UldManager;
        AtkResNode* node = null;
        visible = true;
        for (var depth = 0; depth < binding.Path.Length; depth++)
        {
            if (manager->NodeList == null || manager->NodeListCount > 10000) return null;
            node = null;
            for (var i = 0; i < manager->NodeListCount; i++)
            {
                var candidate = manager->NodeList[i];
                if (candidate == null || candidate->NodeId != binding.Path[depth]) continue;
                if (node != null) return null; // Never resolve an ambiguous ID.
                node = candidate;
            }
            if (node == null) return null;
            visible &= IsShown(node);
            if (depth == binding.Path.Length - 1) break;
            if ((ushort)node->Type < 1000) return null;
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null || component->GetComponentType() == ComponentType.TextInput) return null;
            manager = &component->UldManager;
        }
        return node;
    }

    private string? CheckContext()
    {
        if (!clientState.IsLoggedIn) return "角色未登录，已停止";
        if (gameGui.GameUiHidden) return "游戏 UI 已隐藏，已停止";
        if (!IsGameForeground()) return "游戏窗口不在前台，已停止";
        return new BindingProfile(config.GameVersion, config.Territory, config.Input, config.Words)
            .GetProblem(clientState.TerritoryType, GameVersion);
    }

    private static bool Matches(AnswerFrame frame, string session, WordTarget target) =>
        frame.Ready && frame.Session == session && frame.Targets.Contains(target);

    private static AnswerFrame Unavailable(string reason) => new(false, "", "", [], reason);

    private static AtkComponentTextInput* AsInput(AtkResNode* node)
    {
        if (node == null || (ushort)node->Type < 1000) return null;
        var component = ((AtkComponentNode*)node)->Component;
        return component != null && component->GetComponentType() == ComponentType.TextInput
            ? (AtkComponentTextInput*)component : null;
    }

    private static bool IsShown(AtkResNode* node)
    {
        var remaining = 128;
        for (var current = node; current != null; current = current->ParentNode)
        {
            if (--remaining == 0 || !current->IsVisible() || current->IsDrawDisabled || current->Color.A == 0) return false;
        }
        return true;
    }

    private static string ReadText(AtkTextNode* node)
    {
        var bytes = node->NodeText.AsSpan();
        if (bytes.Length > 4096) return "";
        // Extract display text from SeString color/icon payloads, preserving exact punctuation and spaces.
        return SeString.Parse(bytes).TextValue;
    }

    private static string ReadGameVersion()
    {
        try
        {
            var executable = Environment.ProcessPath;
            return File.ReadAllText(Path.Combine(Path.GetDirectoryName(executable)!, "ffxivgame.ver")).Trim();
        }
        catch { return ""; }
    }

    private static bool IsGameForeground()
    {
        var window = GetForegroundWindow();
        if (window == 0) return false;
        GetWindowThreadProcessId(window, out var processId);
        return processId == Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
