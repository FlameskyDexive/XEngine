namespace XEngine.UiPrototype;

public readonly record struct PrototypeRow(int Identity, string Text, long Version, bool Selected,
    int Depth = 0, bool HasChildren = false, bool Expanded = false);

/// <summary>Shared read model for all N1 adapters. Index is the current visible rank;
/// Identity stays attached to its source node across tree expansion and selection.</summary>
public interface IRowSource
{
    int Count { get; }
    long Version { get; }
    PrototypeRow GetRow(int visibleIndex);
}
