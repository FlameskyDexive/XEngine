// This file is part of the XEngine AI tooling.
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text;

using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>
/// Pure, engine-free validation for .nodeplay Echo text (M20): wire endpoint resolution,
/// prime/start ids, duplicate/empty node ids, dangling connections. Shared by
/// nodeplay_graph_compile and CLI verbs.
/// </summary>
public static class NodePlayGraphValidator
{
    public sealed record NodePlayValidateResult(
        string GraphPath, bool Ok, int Nodes, int Connections, int MissingNodeTypes,
        IReadOnlyList<string> Issues);

    public static NodePlayValidateResult ValidateFile(string absolutePath, string graphPath)
    {
        string text = File.ReadAllText(absolutePath);
        return ValidateText(text, graphPath);
    }

    public static NodePlayValidateResult ValidateText(string echoText, string graphPath)
    {
        EchoObject root = EchoObject.ReadFromString(echoText);
        var issues = new List<string>();
        int missingTypes = 0;

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var byId = new Dictionary<string, EchoObject>(StringComparer.Ordinal);
        EchoObject nodes = root["Nodes"] ?? EchoObject.NewList();

        for (int i = 0; i < nodes.Count; i++)
        {
            EchoObject node = nodes[i];
            string? type = node["$type"]?.StringValue;
            if (type == null)
            {
                issues.Add($"Nodes/{i}: missing $type envelope.");
                missingTypes++;
                continue;
            }

            if (!NodePlayGraphValidator_Ignore.TryResolve(type))
                missingTypes++;

            string id = node["Id"]?.StringValue ?? "";
            if (id.Length == 0)
            {
                issues.Add($"Nodes/{i} ({type}): empty Id.");
                continue;
            }

            if (!nodeIds.Add(id))
                issues.Add($"Nodes/{i} ({type}): duplicate Id '{id}'.");

            byId[id] = node;
        }

        int connectionCount = 0;
        EchoObject connections = root["Connections"] ?? EchoObject.NewList();
        for (int i = 0; i < connections.Count; i++)
        {
            EchoObject wire = connections[i];
            connectionCount++;
            string from = wire["FromNodeId"]?.StringValue ?? "";
            string to = wire["ToNodeId"]?.StringValue ?? "";

            if (from.Length == 0 || !nodeIds.Contains(from))
                issues.Add($"Connections/{i}: FromNodeId '{from}' does not resolve.");
            if (to.Length == 0 || !nodeIds.Contains(to))
                issues.Add($"Connections/{i}: ToNodeId '{to}' does not resolve.");
            if (from.Length > 0 && from == to)
                issues.Add($"Connections/{i}: self-connection on '{from}'.");
        }

        string prime = root["PrimeNodeId"]?.StringValue ?? "";
        if (prime.Length > 0 && !nodeIds.Contains(prime))
            issues.Add($"PrimeNodeId '{prime}' does not resolve.");

        if (nodes.Count == 0)
            issues.Add("Graph has no nodes.");

        return new NodePlayValidateResult(
            graphPath, issues.Count == 0, nodes.Count, connectionCount, missingTypes, issues);
    }
}

/// <summary>Type resolution shim — the offline host cannot load script assemblies, so treat
/// any $type whose namespace starts with a known root as resolvable and flag the rest.</summary>
internal static class NodePlayGraphValidator_Ignore
{
    private static readonly string[] KnownRoots =
    {
        "XEngine.NodePlay",
        "NodePlayCustom",
    };

    public static bool TryResolve(string fullTypeName)
    {
        foreach (string root in KnownRoots)
        {
            if (fullTypeName.StartsWith(root + ".", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
