using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.NodePlay.Editor;

/// <summary>
/// Echo-text clipboard for NodePlay selections (nodes + internal wires + groups + stickies).
/// Payload is a header-prefixed <see cref="NPGraphAsset"/> serialization so the paste path rides
/// the exact same polymorphic Echo reader as file load; ids are regenerated on paste to keep
/// copy/duplicate independent. <see cref="LastText"/> is the headless test seam.
/// </summary>
internal static class NodePlayClipboard
{
    public const string Header = "XEngineNodePlay:";
    public static readonly Float2 PasteOffset = new(48f, 40f);

    internal static string LastText = string.Empty;

    /// <summary>Serialize the selection (optionally also to the OS clipboard).</summary>
    public static void Copy(NPGraphAsset graph, IReadOnlyList<NPNode> nodes,
        IReadOnlyList<NPGroup> groups, IReadOnlyList<NPSticky> stickies, bool toSystemClipboard = true)
    {
        LastText = CopyToText(graph, nodes, groups, stickies);
        if (toSystemClipboard)
            Input.Clipboard = LastText;
    }

    public static string CopyToText(NPGraphAsset graph, IReadOnlyList<NPNode> nodes,
        IReadOnlyList<NPGroup> groups, IReadOnlyList<NPSticky> stickies)
    {
        var fragment = (NPGraphAsset)Activator.CreateInstance(graph.GetType())!;
        fragment.GraphName = graph.GraphName + " (clipboard)";
        fragment.Module = graph.Module;
        fragment.LocalBlackboard = graph.LocalBlackboard;   // variables ride along

        var keep = new HashSet<string>();
        foreach (NPNode node in nodes)
        {
            keep.Add(node.Id);
            fragment.Nodes.Add(node);
        }

        for (int i = 0; i < graph.Connections.Count; i++)
        {
            NPConnection c = graph.Connections[i];
            if (keep.Contains(c.FromNodeId) && keep.Contains(c.ToNodeId))
                fragment.Connections.Add(c);
        }

        foreach (NPGroup group in groups)
            fragment.Groups.Add(group);
        foreach (NPSticky sticky in stickies)
            fragment.Stickies.Add(sticky);

        return Header + Serializer.Serialize(fragment).WriteToString();
    }

    /// <summary>Parse clipboard text; null when the payload is not NodePlay data.</summary>
    public static NPGraphAsset? TextToFragment(string text, Type graphType)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith(Header, StringComparison.Ordinal))
            return null;

        NPGraphAsset? fragment = Echo.EchoObject.ReadFromString(text[Header.Length..]) is { } echo
            ? Serializer.Deserialize(echo, graphType) as NPGraphAsset
            : null;
        return fragment;
    }

    /// <summary>Paste the clipboard payload into the target graph with fresh ids and an offset.</summary>
    public static IReadOnlyList<NPNode> PasteInto(NPGraphAsset target, string text,
        List<NPGroup> outGroups, List<NPSticky> outStickies,
        bool toViewOffset = true)
    {
        NPGraphAsset? fragment = TextToFragment(text, target.GetType());
        if (fragment == null)
            return Array.Empty<NPNode>();

        var idMap = new Dictionary<string, string>();
        Float2 origin = toViewOffset ? PasteOffset : default;
        float minX = float.MaxValue, minY = float.MaxValue;
        foreach (NPNode node in fragment.Nodes)
        {
            minX = MathF.Min(minX, node.Position.X);
            minY = MathF.Min(minY, node.Position.Y);
        }

        if (minX == float.MaxValue)
        {
            minX = 0;
            minY = 0;
        }

        foreach (NPNode node in fragment.Nodes)
        {
            string oldId = node.Id;
            node.Id = NPNode.NewId();
            idMap[oldId] = node.Id;
            node.Position = new Float2(node.Position.X - minX + origin.X, node.Position.Y - minY + origin.Y);
            target.AddNode(node);
        }

        foreach (NPConnection wire in fragment.Connections)
        {
            if (!idMap.TryGetValue(wire.FromNodeId, out string? from) ||
                !idMap.TryGetValue(wire.ToNodeId, out string? to))
                continue;
            if (target.Connect(target.GetNode(from)!, target.GetNode(to)!, out _) == null)
                continue;
            target.Connections[^1].Points.AddRange(wire.Points);
        }

        foreach (NPGroup group in fragment.Groups)
        {
            var copy = new NPGroup
            {
                Id = NPNode.NewId(),
                Title = group.Title,
                X = group.X - minX + origin.X,
                Y = group.Y - minY + origin.Y,
                Width = group.Width,
                Height = group.Height,
                Style = group.Style,
            };
            target.Groups.Add(copy);
            outGroups.Add(copy);
        }

        foreach (NPSticky sticky in fragment.Stickies)
        {
            var copy = new NPSticky
            {
                Id = NPNode.NewId(),
                Text = sticky.Text,
                X = sticky.X - minX + origin.X,
                Y = sticky.Y - minY + origin.Y,
                Width = sticky.Width,
                Height = sticky.Height,
                Style = sticky.Style,
            };
            target.Stickies.Add(copy);
            outStickies.Add(copy);
        }

        target.BuildTopology();
        return fragment.Nodes;
    }
}
