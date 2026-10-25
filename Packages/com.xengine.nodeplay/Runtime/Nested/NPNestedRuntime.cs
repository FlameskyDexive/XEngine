using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.NodePlay.Dialogue;

namespace XEngine.NodePlay;

/// <summary>
/// Reusable nested-subgraph engine (instances + Write-In/Read-Out mappings). Plain logic object —
/// host nodes (BT SubTree, FSM nested states, dialogue SubDialogueTree) each hold one and expose
/// the serialized <see cref="SubGraph"/>/<see cref="Mappings"/> fields.
/// </summary>
public class NPNestedRuntime
{
    public AssetRef<NPGraphAsset> SubGraph = default;
    public List<NPMappingParameter> Mappings = new();

    /// <summary>Editor/test injection taking precedence over <see cref="SubGraph"/>.</summary>
    public NPGraphAsset? SubGraphOverride;

    private sealed class Entry
    {
        public required NPGraphAsset Graph;
        public bool IsFinished;
    }

    private readonly Dictionary<object, Entry> _instances = new();
    private readonly List<(NPGraphAsset graph, NPVariable variable, Action<NPVariable> handler)> _liveOuts = new();

    /// <summary>Start (or return the running) instance for the owner key.</summary>
    public NPGraphAsset? GetOrStartInstance(object ownerKey, NPGraphAsset parentGraph)
    {
        if (_instances.TryGetValue(ownerKey, out Entry? live))
            return live.IsFinished ? null : live.Graph;

        NPGraphAsset? source = SubGraphOverride ?? SubGraph.Res;
        if (source == null)
            return null;

        NPGraphAsset clone = source.CloneAsInstance();
        clone.BuildTopology();
        clone.OwnerRunner = parentGraph.OwnerRunner;
        clone.LocalBlackboard ??= new NPBlackboardSource();
        clone.GraphBlackboard = clone.LocalBlackboard;
        clone.LocalBlackboard.Identifier = $"{parentGraph.GraphName}:nested";
        clone.LocalBlackboard.Parent = parentGraph.GraphBlackboard;

        WriteMappingsIn(parentGraph.GraphBlackboard!, clone.LocalBlackboard);
        SubscribeReadOuts(clone, parentGraph);
        clone.OnStarted(parentGraph.OwnerRunner!);
        if (clone is NPDialogueAsset dialogue && dialogue.PrimeNode is Dialogue.DTNode prime)
            dialogue.ExecuteNode(prime);

        _instances[ownerKey] = new Entry { Graph = clone };
        return clone;
    }

    public void TickInstances(object ownerKey, float delta)
    {
        if (_instances.TryGetValue(ownerKey, out Entry? entry))
        {
            entry!.Graph.ElapsedTime += delta;
            entry.Graph.TickRunner(delta);
        }
    }

    public void FinishInstance(object ownerKey)
    {
        if (_instances.TryGetValue(ownerKey, out Entry? entry))
            entry!.IsFinished = true;
    }

    public bool IsRunning(object ownerKey)
        => _instances.TryGetValue(ownerKey, out Entry? e) && e is { IsFinished: false };

    public NPGraphAsset? InstanceOf(object ownerKey)
        => _instances.TryGetValue(ownerKey, out Entry? e) ? e.Graph : null;

    public void ReleaseFor(object ownerKey)
    {
        if (!_instances.Remove(ownerKey, out Entry? entry))
            return;

        entry!.Graph.OnStopped(entry.Graph.OwnerRunner!, success: true);
        UnsubscribeReadOuts(entry.Graph);
        entry.Graph.OwnerRunner = null!;
        entry.Graph.Dispose();
    }

    public void ReleaseAll()
    {
        List<object> keys = new(_instances.Keys);
        foreach (object key in keys)
            ReleaseFor(key);
    }

    // ---- mapping mechanics --------------------------------------------------------------

    private void WriteMappingsIn(INPBlackboard parentBb, INPBlackboard subBb)
    {
        if (parentBb is not NPBlackboardSource parent || subBb is not NPBlackboardSource sub)
            return;

        for (int i = 0; i < Mappings.Count; i++)
        {
            NPMappingParameter m = Mappings[i];
            if (m.Mode != NPMappingMode.WriteIn || string.IsNullOrEmpty(m.SubVariableName))
                continue;
            sub.Ensure(m.SubVariableName, "");
            sub.SetValue(m.SubVariableName, m.ParentParameter.GetValue(parent));
        }
    }

    private void SubscribeReadOuts(NPGraphAsset clone, NPGraphAsset parentGraph)
    {
        var sub = clone.LocalBlackboard;
        for (int i = 0; i < Mappings.Count; i++)
        {
            NPMappingParameter m = Mappings[i];
            if (m.Mode != NPMappingMode.ReadOut || string.IsNullOrEmpty(m.SubVariableName))
                continue;
            NPVariable? variable = sub.Get(m.SubVariableName);
            if (variable == null || !variable.IsPublic)
                continue;

            NPParameter sink = m.ParentParameter;
            var handler = (Action<NPVariable>)(v => sink.SetValue(parentGraph.GraphBlackboard, v.Value));
            variable.OnValueChanged += handler;
            _liveOuts.Add((clone, variable, handler));
        }
    }

    private void UnsubscribeReadOuts(NPGraphAsset clone)
    {
        for (int i = _liveOuts.Count - 1; i >= 0; i--)
        {
            (NPGraphAsset g, NPVariable v, Action<NPVariable> h) = _liveOuts[i];
            if (ReferenceEquals(g, clone))
            {
                v.OnValueChanged -= h;
                _liveOuts.RemoveAt(i);
            }
        }
    }

}
