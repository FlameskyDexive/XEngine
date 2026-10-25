namespace XEngine.Editor.UI;

/// <summary>Opaque identity. A slot is meaningful only within its owning store and generation.</summary>
public readonly record struct UiNodeHandle
{
    internal UiNodeHandle(object owner, int index, ulong generation)
    { Owner = owner; Index = index; Generation = generation; }
    internal object? Owner { get; }
    internal int Index { get; }
    internal ulong Generation { get; }
    public bool IsNull => Owner is null;
}

[Flags]
public enum UiInvalidation : byte
{
    None = 0, Measure = 1, Arrange = 2, Paint = 4, HitTest = 8, Structure = 16,
    All = Measure | Arrange | Paint | HitTest | Structure
}
