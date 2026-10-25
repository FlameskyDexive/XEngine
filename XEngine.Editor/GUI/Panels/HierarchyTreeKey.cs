using System;
using System.Runtime.CompilerServices;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.GUI.Panels;

/// <summary>Cache validity compares source identity and every revision directly.
/// Hash collisions cannot suppress hierarchy rebuilds.</summary>
internal readonly struct HierarchyTreeKey(Scene? scene, long structure, long editorChanges,
    long selection, string search, bool prefabMode, int visibility, Guid ping,
    bool expanded, bool alphabetical, int cut) : IEquatable<HierarchyTreeKey>
{
    private readonly Scene? _scene = scene;
    private readonly long _structure = structure, _editorChanges = editorChanges, _selection = selection;
    private readonly string? _search = search;
    private readonly bool _prefabMode = prefabMode, _expanded = expanded, _alphabetical = alphabetical;
    private readonly int _visibility = visibility, _cut = cut;
    private readonly Guid _ping = ping;

    public bool Equals(HierarchyTreeKey other) => ReferenceEquals(_scene, other._scene)
        && _structure == other._structure && _editorChanges == other._editorChanges
        && _selection == other._selection && string.Equals(_search, other._search, StringComparison.Ordinal)
        && _prefabMode == other._prefabMode && _visibility == other._visibility
        && _ping == other._ping && _expanded == other._expanded
        && _alphabetical == other._alphabetical && _cut == other._cut;
    public override bool Equals(object? value) => value is HierarchyTreeKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_scene == null ? 0 : RuntimeHelpers.GetHashCode(_scene),
        _structure, _editorChanges, _selection, _search, _prefabMode, _visibility,
        HashCode.Combine(_ping, _expanded, _alphabetical, _cut));
}
