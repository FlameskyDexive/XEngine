using System;
using System.Collections.Generic;

using XEngine.NodePlay;

namespace XEngine.NodePlay.Editor;

/// <summary>Explorer search: pure filter so tests pin the match rules.</summary>
public static class NodePlayExplorer
{
    /// <summary>Case-insensitive contains over display name, comment and type name.</summary>
    public static List<NPNode> Filter(IReadOnlyList<NPNode> nodes, string query)
    {
        var result = new List<NPNode>();
        if (string.IsNullOrWhiteSpace(query))
        {
            result.AddRange(nodes);
            return result;
        }

        foreach (NPNode node in nodes)
        {
            if (node.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || node.Comment.Contains(query, StringComparison.OrdinalIgnoreCase)
                || node.GetType().Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                result.Add(node);
        }

        return result;
    }
}

/// <summary>Batch refactor: rename a blackboard variable and every parameter bound to it.</summary>
public static class NodePlayRefactor
{
    public static int RenameVariable(NPGraphAsset graph, string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName)
            return 0;

        int changed = 0;
        NPVariable? variable = graph.LocalBlackboard.Get(oldName);
        if (variable != null)
        {
            variable.Name = newName;
            graph.LocalBlackboard.OnValidate();
            changed++;
        }

        for (int i = 0; i < graph.Nodes.Count; i++)
            changed += RenameOnNode(graph.Nodes[i], oldName, newName);

        return changed;
    }

    private static int RenameOnNode(NPNode node, string oldName, string newName)
    {
        int changed = 0;
        for (Type? t = node.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;

            foreach (var field in t.GetFields(flags))
            {
                if (field.FieldType == typeof(NPParameter))
                {
                    var parameter = (NPParameter?)field.GetValue(node);
                    if (parameter != null && parameter.VariableName == oldName)
                    {
                        parameter.VariableName = newName;
                        changed++;
                    }
                }
                else if (field.FieldType == typeof(List<NPParameter>))
                {
                    var list = (List<NPParameter>?)field.GetValue(node);
                    if (list == null)
                        continue;
                    foreach (NPParameter parameter in list)
                    {
                        if (parameter.VariableName == oldName)
                        {
                            parameter.VariableName = newName;
                            changed++;
                        }
                    }
                }
                else if (field.FieldType == typeof(NPNestedRuntime))
                {
                    var runtime = (NPNestedRuntime?)field.GetValue(node);
                    if (runtime == null)
                        continue;
                    foreach (NPMappingParameter mapping in runtime.Mappings)
                    {
                        if (mapping.ParentParameter.VariableName == oldName)
                        {
                            mapping.ParentParameter.VariableName = newName;
                            changed++;
                        }
                    }
                }
                else if (field.FieldType == typeof(List<NPMappingParameter>))
                {
                    var list = (List<NPMappingParameter>?)field.GetValue(node);
                    if (list == null)
                        continue;
                    foreach (NPMappingParameter mapping in list)
                    {
                        if (mapping.ParentParameter.VariableName == oldName)
                        {
                            mapping.ParentParameter.VariableName = newName;
                            changed++;
                        }
                    }
                }
            }
        }

        return changed;
    }
}
