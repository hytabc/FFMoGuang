namespace FFMoGuang;

public sealed record NodeBinding(string Addon, int Index, uint[] Path)
{
    public string Key => $"{Addon}[{Index}]/{string.Join('/', Path)}";
}

public sealed record InspectedNode(NodeBinding Binding, bool IsInput, string Text, bool Visible, bool HasCallback);
public sealed record InspectorSnapshot(IReadOnlyList<InspectedNode> Nodes, string Message);
