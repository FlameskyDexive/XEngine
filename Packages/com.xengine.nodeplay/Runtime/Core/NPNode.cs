using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.NodePlay;

/// <summary>
/// Base class of every NodePlay graph element that lives on the canvas: behaviour-tree nodes,
/// FSM states, dialogue nodes. Instances are persisted polymorphically inside
/// <see cref="NPGraphAsset.Nodes"/> as Echo text (a <c>$type</c> envelope per entry), so the
/// on-disk graph stays human/AI-diffable — derived classes only add plain public fields.
/// </summary>
/// <remarks>
/// Persisted members are the plain public fields. The connection adjacency lists, run status,
/// and back-reference to the owning graph are rebuilt after load by
/// <see cref="NPGraphAsset.BuildTopology"/> and therefore marked <see cref="SerializeIgnoreAttribute"/>.
/// </remarks>
public abstract class NPNode
{
    public string Id = NewId();
    public string Name = "";
    public Float2 Position;
    public string Comment = "";

    /// <summary>Muted node: skipped during execution (module semantics decide the fallback path).</summary>
    public bool Disabled;

    /// <summary>Editor breakpoint: a running hit pauses the runner and focuses the editor here.</summary>
    public bool Breakpoint;

    [SerializeIgnore] public NPGraphAsset Graph = null!;
    [SerializeIgnore] public NPStatus Status = NPStatus.Resting;
    [SerializeIgnore] public readonly List<NPConnection> InConnections = new();
    [SerializeIgnore] public readonly List<NPConnection> OutConnections = new();

    /// <summary>Display name: explicit override, else the attribute/display name provided by subclasses.</summary>
    public virtual string DisplayName => !string.IsNullOrEmpty(Name) ? Name : GetType().Name;

    /// <summary>Maximum incoming links; int.MaxValue means unlimited.</summary>
    public virtual int MaxInConnections => int.MaxValue;

    /// <summary>Maximum outgoing links; int.MaxValue means unlimited.</summary>
    public virtual int MaxOutConnections => int.MaxValue;

    /// <summary>Module hook: reject a wire before it is created (returns a UI-facing reason).</summary>
    public virtual string? ValidateConnection(NPNode to) => null;

    public static string NewId() => Guid.NewGuid().ToString("N");

    public override string ToString() => $"{GetType().Name}({(string.IsNullOrEmpty(Name) ? Id : Name)})";
}
