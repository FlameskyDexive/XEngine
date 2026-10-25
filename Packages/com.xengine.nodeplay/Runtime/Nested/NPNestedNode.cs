using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.NodePlay;

/// <summary>
/// Node form of the nested-subgraph host: serializes <see cref="Nested"/>'s reference and
/// mappings; the execution logic itself lives in <see cref="NPNestedRuntime"/> so module-specific
/// node bases (BT/FSM/Dialogue) can embed it directly.
/// </summary>
public abstract class NPNestedNode : NPNode
{
    public AssetRef<NPGraphAsset> SubGraph = default;
    public List<NPMappingParameter> Mappings = new();

    /// <summary>Editor/test injection taking precedence over <see cref="SubGraph"/>.</summary>
    public NPGraphAsset? SubGraphOverride;

    /// <summary>Mirror of the serialized fields into the shared runtime engine.</summary>
    [SerializeIgnore] protected internal readonly NPNestedRuntime Nested = new();

    /// <summary>Push the serialized surface into the runtime engine (before any start).</summary>
    protected internal NPNestedRuntime BindNested()
    {
        Nested.SubGraph = SubGraph;
        Nested.SubGraphOverride = SubGraphOverride;
        Nested.Mappings = Mappings;
        return Nested;
    }
}
