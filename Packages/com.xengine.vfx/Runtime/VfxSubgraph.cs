using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.VFX;

/// <summary>Resolves a Subgraph <c>Path</c> / <c>Graph</c> / context <c>Asset</c> to graph lists.</summary>
public delegate VfxGraphLists? VfxSubgraphResolver(string path);

/// <summary>
/// Engine-agnostic Visual Effect Graph lists. The Host and the source compiler share this
/// shape so nested <c>.vfx</c> includes can expand without ScriptableObject / Echo.
/// </summary>
public sealed class VfxGraphLists
{
    public string GraphName = "";
    public VfxTemplateKind Template;
    public int Capacity = 4096;
    public int SubgraphCount;
    public List<VfxProperty> Properties = new();
    public List<VfxContext> Contexts = new();
    public List<VfxBlock> Blocks = new();
    public List<VfxOperator> Operators = new();
    public List<VfxEdge> Edges = new();

    public static VfxGraphLists From(
        string graphName,
        VfxTemplateKind template,
        int capacity,
        IReadOnlyList<VfxProperty>? properties,
        IReadOnlyList<VfxContext>? contexts,
        IReadOnlyList<VfxBlock>? blocks,
        IReadOnlyList<VfxOperator>? operators,
        IReadOnlyList<VfxEdge>? edges)
    {
        var lists = new VfxGraphLists
        {
            GraphName = string.IsNullOrWhiteSpace(graphName) ? "VFX/Unnamed" : graphName,
            Template = template,
            Capacity = capacity < 1 ? 1 : capacity,
        };
        if (properties != null)
        {
            for (int i = 0; i < properties.Count; i++)
                lists.Properties.Add(CloneProperty(properties[i]));
        }
        if (contexts != null)
        {
            for (int i = 0; i < contexts.Count; i++)
                lists.Contexts.Add(CloneContext(contexts[i]));
        }
        if (blocks != null)
        {
            for (int i = 0; i < blocks.Count; i++)
                lists.Blocks.Add(CloneBlock(blocks[i]));
        }
        if (operators != null)
        {
            for (int i = 0; i < operators.Count; i++)
                lists.Operators.Add(CloneOperator(operators[i]));
        }
        if (edges != null)
        {
            for (int i = 0; i < edges.Count; i++)
                lists.Edges.Add(CloneEdge(edges[i]));
        }
        return lists;
    }

    public VfxGraphLists Clone()
    {
        VfxGraphLists copy = From(GraphName, Template, Capacity, Properties, Contexts, Blocks, Operators, Edges);
        copy.SubgraphCount = SubgraphCount;
        return copy;
    }

    internal static VfxProperty CloneProperty(VfxProperty property)
        => new()
        {
            Id = property.Id,
            Name = property.Name,
            DisplayName = property.DisplayName,
            Type = property.Type,
            DefaultValue = property.DefaultValue,
        };

    internal static VfxContext CloneContext(VfxContext context)
        => new()
        {
            Id = context.Id,
            Type = context.Type,
            PosX = context.PosX,
            PosY = context.PosY,
            Asset = context.Asset,
        };

    internal static VfxBlock CloneBlock(VfxBlock block)
    {
        var copy = new VfxBlock
        {
            Id = block.Id,
            ContextId = block.ContextId,
            Type = block.Type,
            Order = block.Order,
            PosX = block.PosX,
            PosY = block.PosY,
        };
        CopyFields(block.Fields, copy.Fields);
        return copy;
    }

    internal static VfxOperator CloneOperator(VfxOperator op)
    {
        var copy = new VfxOperator
        {
            Id = op.Id,
            Type = op.Type,
            PosX = op.PosX,
            PosY = op.PosY,
        };
        CopyFields(op.Fields, copy.Fields);
        return copy;
    }

    internal static VfxEdge CloneEdge(VfxEdge edge)
        => new()
        {
            Id = edge.Id,
            FromNode = edge.FromNode,
            FromPort = edge.FromPort,
            ToNode = edge.ToNode,
            ToPort = edge.ToPort,
        };

    private static void CopyFields(List<VfxNodeField>? source, List<VfxNodeField> destination)
    {
        if (source == null)
            return;
        for (int i = 0; i < source.Count; i++)
        {
            destination.Add(new VfxNodeField
            {
                Key = source[i].Key,
                Value = source[i].Value,
            });
        }
    }
}

/// <summary>
/// Inlines nested <c>.vfx</c> Subgraph operators / blocks / contexts into the parent graph.
/// Child blocks rebind to the parent context of the same type; parent blackboard names win.
/// </summary>
public static class VfxSubgraph
{
    public const int MaxDepth = 4;

    public static bool HasIncludes(VfxGraphLists lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        for (int i = 0; i < lists.Contexts.Count; i++)
        {
            if (IsSubgraphType(lists.Contexts[i].Type))
                return true;
        }
        for (int i = 0; i < lists.Operators.Count; i++)
        {
            if (IsSubgraphType(lists.Operators[i].Type))
                return true;
        }
        for (int i = 0; i < lists.Blocks.Count; i++)
        {
            if (IsSubgraphType(lists.Blocks[i].Type))
                return true;
        }
        return false;
    }

    public static VfxGraphLists Expand(VfxGraphLists root, VfxSubgraphResolver? resolve)
    {
        ArgumentNullException.ThrowIfNull(root);
        VfxGraphLists copy = root.Clone();
        if (!HasIncludes(copy))
            return copy;
        if (resolve == null)
            throw new VfxGraphCompileException("VFX subgraph include needs a resolver.");
        ExpandInPlace(copy, resolve, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        return copy;
    }

    public static string NormalizePath(string path)
    {
        string trimmed = path.Trim().Replace('\\', '/');
        int slash = trimmed.LastIndexOf('/');
        string name = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
        if (!name.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase))
            name += ".vfx";
        return name;
    }

    public static bool PathEquals(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    public static string ReadIncludePath(IReadOnlyList<VfxNodeField>? fields, string? asset = null)
    {
        if (!string.IsNullOrWhiteSpace(asset))
            return asset.Trim();
        string path = Field(fields, VfxExposedNames.SubgraphPath);
        if (string.IsNullOrEmpty(path))
            path = Field(fields, VfxExposedNames.SubgraphGraph);
        return path;
    }

    public static string? ResolveFile(string path, string? extraRoot = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string trimmed = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
            return Path.GetFullPath(trimmed);
        if (File.Exists(trimmed))
            return Path.GetFullPath(trimmed);

        string fileName = NormalizePath(trimmed);
        string stem = Path.GetFileNameWithoutExtension(fileName);

        if (!string.IsNullOrEmpty(extraRoot))
        {
            string? fromExtra = FirstExisting(
                Path.Combine(extraRoot, trimmed),
                Path.Combine(extraRoot, fileName),
                Path.Combine(extraRoot, "Assets", trimmed),
                Path.Combine(extraRoot, "Assets", fileName),
                Path.Combine(extraRoot, "Assets", "VFX", fileName),
                Path.Combine(extraRoot, "Templates", fileName));
            if (fromExtra != null)
                return fromExtra;
        }

        foreach (string root in VfxGraphSourceCompiler.EnumeratePackageRoots())
        {
            string? found = FirstExisting(
                Path.Combine(root, "Templates", fileName),
                Path.Combine(root, "Templates", stem + ".vfx"),
                Path.Combine(root, "Samples~", "SimpleLoop", fileName),
                Path.Combine(root, "Samples~", "SimpleBurst", fileName));
            if (found != null)
                return found;
        }

        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory != null)
        {
            string? found = FirstExisting(
                Path.Combine(directory.FullName, trimmed),
                Path.Combine(directory.FullName, "Assets", fileName),
                Path.Combine(directory.FullName, "Assets", "VFX", fileName),
                Path.Combine(directory.FullName, "Templates", fileName));
            if (found != null)
                return found;
            directory = directory.Parent;
        }

        return null;
    }

    private static void ExpandInPlace(
        VfxGraphLists graph,
        VfxSubgraphResolver resolve,
        HashSet<string> stack,
        int depth)
    {
        var includes = new List<Include>();
        CollectIncludes(graph, includes);
        if (includes.Count == 0)
            return;
        if (depth >= MaxDepth)
            throw new VfxGraphCompileException(
                "VFX subgraph include exceeded max depth (" + MaxDepth + ").");

        for (int i = 0; i < includes.Count; i++)
        {
            Include include = includes[i];
            if (string.IsNullOrWhiteSpace(include.Path))
            {
                throw new VfxGraphCompileException(
                    "VFX subgraph '" + include.Id + "' is missing Path.");
            }

            string key = NormalizePath(include.Path);
            if (stack.Contains(key))
            {
                throw new VfxGraphCompileException(
                    "VFX subgraph include cycle at '" + key + "'.");
            }

            stack.Add(key);
            VfxGraphLists? resolved = resolve(include.Path);
            if (resolved == null)
            {
                stack.Remove(key);
                throw new VfxGraphCompileException(
                    "VFX subgraph '" + include.Path + "' was not found.");
            }

            VfxGraphLists child = resolved.Clone();
            ExpandInPlace(child, resolve, stack, depth + 1);
            Inline(graph, child, include.Id);
            graph.SubgraphCount += 1 + child.SubgraphCount;
            stack.Remove(key);
        }

        RemoveIncludes(graph, includes);
    }

    private static void CollectIncludes(VfxGraphLists graph, List<Include> includes)
    {
        for (int i = 0; i < graph.Contexts.Count; i++)
        {
            VfxContext ctx = graph.Contexts[i];
            if (!IsSubgraphType(ctx.Type))
                continue;
            includes.Add(new Include(ctx.Id, ReadIncludePath(null, ctx.Asset)));
        }
        for (int i = 0; i < graph.Operators.Count; i++)
        {
            VfxOperator op = graph.Operators[i];
            if (!IsSubgraphType(op.Type))
                continue;
            includes.Add(new Include(op.Id, ReadIncludePath(op.Fields)));
        }
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            VfxBlock block = graph.Blocks[i];
            if (!IsSubgraphType(block.Type))
                continue;
            includes.Add(new Include(block.Id, ReadIncludePath(block.Fields)));
        }
    }

    private static void Inline(VfxGraphLists parent, VfxGraphLists child, string includeId)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        string prefix = includeId + "_";

        for (int i = 0; i < child.Blocks.Count; i++)
        {
            VfxBlock block = child.Blocks[i];
            if (IsSubgraphType(block.Type))
                continue;
            string parentContextId = MapContext(parent, child, block.ContextId);
            if (string.IsNullOrEmpty(parentContextId))
                continue;
            VfxBlock copy = VfxGraphLists.CloneBlock(block);
            copy.Id = prefix + block.Id;
            copy.ContextId = parentContextId;
            copy.Order += 100;
            parent.Blocks.Add(copy);
            idMap[block.Id] = copy.Id;
        }

        for (int i = 0; i < child.Operators.Count; i++)
        {
            VfxOperator op = child.Operators[i];
            if (IsSubgraphType(op.Type))
                continue;
            VfxOperator copy = VfxGraphLists.CloneOperator(op);
            copy.Id = prefix + op.Id;
            parent.Operators.Add(copy);
            idMap[op.Id] = copy.Id;
        }

        for (int i = 0; i < child.Edges.Count; i++)
        {
            VfxEdge edge = child.Edges[i];
            if (!idMap.TryGetValue(edge.FromNode, out string? from))
                continue;
            if (!idMap.TryGetValue(edge.ToNode, out string? to))
                continue;
            VfxEdge copy = VfxGraphLists.CloneEdge(edge);
            copy.Id = prefix + edge.Id;
            copy.FromNode = from;
            copy.ToNode = to;
            parent.Edges.Add(copy);
        }

        for (int i = 0; i < child.Properties.Count; i++)
        {
            VfxProperty property = child.Properties[i];
            string name = string.IsNullOrEmpty(property.Name) ? property.DisplayName : property.Name;
            if (string.IsNullOrEmpty(name) || VfxProperties.Find(parent.Properties, name) != null)
                continue;
            VfxProperty copy = VfxGraphLists.CloneProperty(property);
            copy.Id = prefix + property.Id;
            parent.Properties.Add(copy);
        }
    }

    private static string MapContext(VfxGraphLists parent, VfxGraphLists child, string childContextId)
    {
        string childType = "";
        for (int i = 0; i < child.Contexts.Count; i++)
        {
            if (!string.Equals(child.Contexts[i].Id, childContextId, StringComparison.Ordinal))
                continue;
            childType = child.Contexts[i].Type;
            break;
        }
        if (string.IsNullOrEmpty(childType))
            return "";

        for (int i = 0; i < parent.Contexts.Count; i++)
        {
            if (string.Equals(parent.Contexts[i].Type, childType, StringComparison.Ordinal))
                return parent.Contexts[i].Id;
        }
        if (!VfxNodeLibrary.IsOutputContext(childType))
            return "";
        for (int i = 0; i < parent.Contexts.Count; i++)
        {
            if (VfxNodeLibrary.IsOutputContext(parent.Contexts[i].Type))
                return parent.Contexts[i].Id;
        }
        return "";
    }

    private static void RemoveIncludes(VfxGraphLists graph, List<Include> includes)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < includes.Count; i++)
            ids.Add(includes[i].Id);

        for (int i = graph.Contexts.Count - 1; i >= 0; i--)
        {
            if (ids.Contains(graph.Contexts[i].Id) || IsSubgraphType(graph.Contexts[i].Type))
                graph.Contexts.RemoveAt(i);
        }
        for (int i = graph.Operators.Count - 1; i >= 0; i--)
        {
            if (ids.Contains(graph.Operators[i].Id) || IsSubgraphType(graph.Operators[i].Type))
                graph.Operators.RemoveAt(i);
        }
        for (int i = graph.Blocks.Count - 1; i >= 0; i--)
        {
            if (ids.Contains(graph.Blocks[i].Id) || IsSubgraphType(graph.Blocks[i].Type))
                graph.Blocks.RemoveAt(i);
        }
        for (int i = graph.Edges.Count - 1; i >= 0; i--)
        {
            VfxEdge edge = graph.Edges[i];
            if (ids.Contains(edge.FromNode) || ids.Contains(edge.ToNode))
                graph.Edges.RemoveAt(i);
        }
    }

    private static bool IsSubgraphType(string type)
        => string.Equals(type, VfxNodeLibrary.Subgraph, StringComparison.Ordinal);

    private static string Field(IReadOnlyList<VfxNodeField>? fields, string key)
    {
        if (fields == null)
            return "";
        for (int i = 0; i < fields.Count; i++)
        {
            if (string.Equals(fields[i].Key, key, StringComparison.Ordinal))
                return fields[i].Value ?? "";
        }
        return "";
    }

    private static string? FirstExisting(params string[] candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
                return Path.GetFullPath(candidates[i]);
        }
        return null;
    }

    private readonly struct Include
    {
        public readonly string Id;
        public readonly string Path;

        public Include(string id, string path)
        {
            Id = id;
            Path = path;
        }
    }
}
