using System.Globalization;
using System.IO;

using XEngine.Echo;
using XEngine.VFX;

namespace XEngine.Ai.Host;

/// <summary>
/// Maps an Echo <c>.vfx</c> tree onto the VFX Graph POCOs used by
/// <see cref="VfxGraphSourceCompiler"/>. Host stays engine-agnostic (no ScriptableObject).
/// </summary>
internal static class VfxGraphEcho
{
    public static string CompileToSource(string echoText, string? projectRoot = null)
    {
        Load(echoText, out string graphName, out VfxTemplateKind template, out int capacity,
            out List<VfxProperty> properties, out List<VfxContext> contexts, out List<VfxBlock> blocks,
            out List<VfxOperator> operators, out List<VfxEdge> edges);
        return VfxGraphSourceCompiler.CompileToSource(
            graphName, template, capacity, properties, contexts, blocks, operators, edges,
            VfxGraphSourceCompiler.ReadTemplateShader(),
            CreateResolver(projectRoot));
    }

    public static VfxGraphLoweredSettings CompileSettings(string echoText, string? projectRoot = null)
    {
        Load(echoText, out string graphName, out VfxTemplateKind template, out int capacity,
            out List<VfxProperty> properties, out List<VfxContext> contexts, out List<VfxBlock> blocks,
            out List<VfxOperator> operators, out List<VfxEdge> edges);
        return VfxGraphSourceCompiler.CompileSettings(
            graphName, template, capacity, properties, contexts, blocks, operators, edges,
            CreateResolver(projectRoot));
    }

    internal static VfxSubgraphResolver CreateResolver(string? projectRoot = null)
    {
        return path =>
        {
            string? file = VfxSubgraph.ResolveFile(path, projectRoot);
            if (file == null)
                return null;
            Load(File.ReadAllText(file), out string graphName, out VfxTemplateKind template, out int capacity,
                out List<VfxProperty> properties, out List<VfxContext> contexts, out List<VfxBlock> blocks,
                out List<VfxOperator> operators, out List<VfxEdge> edges);
            return VfxGraphLists.From(
                graphName, template, capacity, properties, contexts, blocks, operators, edges);
        };
    }

    public static void Load(
        string echoText,
        out string graphName,
        out VfxTemplateKind template,
        out int capacity,
        out List<VfxProperty> properties,
        out List<VfxContext> contexts,
        out List<VfxBlock> blocks,
        out List<VfxOperator> operators,
        out List<VfxEdge> edges)
    {
        EchoObject root = EchoObject.ReadFromString(echoText);
        graphName = Scalar(root, "GraphName");
        if (string.IsNullOrWhiteSpace(graphName))
            graphName = "VFX/Unnamed";
        template = ParseEnum(Scalar(root, "Template"), VfxTemplateKind.Empty);
        capacity = ParseInt(Scalar(root, "Capacity"), 4096);

        properties = new List<VfxProperty>();
        foreach (EchoObject item in Children(root, "Properties"))
        {
            properties.Add(new VfxProperty
            {
                Id = Scalar(item, "Id"),
                Name = Scalar(item, "Name"),
                DisplayName = Scalar(item, "DisplayName"),
                Type = ParseEnum(Scalar(item, "Type"), VfxValueType.Float),
                DefaultValue = Scalar(item, "DefaultValue"),
            });
        }

        contexts = new List<VfxContext>();
        foreach (EchoObject item in Children(root, "Contexts"))
        {
            contexts.Add(new VfxContext
            {
                Id = Scalar(item, "Id"),
                Type = Scalar(item, "Type"),
                PosX = ParseFloat(Scalar(item, "PosX")),
                PosY = ParseFloat(Scalar(item, "PosY")),
                Asset = Scalar(item, "Asset"),
            });
        }

        blocks = new List<VfxBlock>();
        foreach (EchoObject item in Children(root, "Blocks"))
        {
            var block = new VfxBlock
            {
                Id = Scalar(item, "Id"),
                ContextId = Scalar(item, "ContextId"),
                Type = Scalar(item, "Type"),
                Order = ParseInt(Scalar(item, "Order"), 0),
                PosX = ParseFloat(Scalar(item, "PosX")),
                PosY = ParseFloat(Scalar(item, "PosY")),
            };
            foreach (EchoObject field in Children(item, "Fields"))
            {
                block.Fields.Add(new VfxNodeField
                {
                    Key = Scalar(field, "Key"),
                    Value = Scalar(field, "Value"),
                });
            }
            blocks.Add(block);
        }

        operators = new List<VfxOperator>();
        foreach (EchoObject item in Children(root, "Operators"))
        {
            var op = new VfxOperator
            {
                Id = Scalar(item, "Id"),
                Type = Scalar(item, "Type"),
                PosX = ParseFloat(Scalar(item, "PosX")),
                PosY = ParseFloat(Scalar(item, "PosY")),
            };
            foreach (EchoObject field in Children(item, "Fields"))
            {
                op.Fields.Add(new VfxNodeField
                {
                    Key = Scalar(field, "Key"),
                    Value = Scalar(field, "Value"),
                });
            }
            operators.Add(op);
        }

        edges = new List<VfxEdge>();
        foreach (EchoObject item in Children(root, "Edges"))
        {
            edges.Add(new VfxEdge
            {
                Id = Scalar(item, "Id"),
                FromNode = Scalar(item, "FromNode"),
                FromPort = Scalar(item, "FromPort"),
                ToNode = Scalar(item, "ToNode"),
                ToPort = Scalar(item, "ToPort"),
            });
        }
    }

    private static List<EchoObject> Children(EchoObject parent, string key)
    {
        var list = new List<EchoObject>();
        if (!parent.TryGet(key, out EchoObject? node) || node is null)
            return list;
        if (node.TagType == EchoType.List)
        {
            foreach (EchoObject child in node.List)
                list.Add(child);
            return list;
        }
        if (node.TryGet("$values", out EchoObject? values) && values is not null && values.TagType == EchoType.List)
        {
            foreach (EchoObject child in values.List)
                list.Add(child);
        }
        return list;
    }

    private static string Scalar(EchoObject parent, string key)
    {
        if (!parent.TryGet(key, out EchoObject? node) || node is null)
            return "";
        return node.TagType switch
        {
            EchoType.String => node.StringValue ?? "",
            EchoType.Int => node.IntValue.ToString(CultureInfo.InvariantCulture),
            EchoType.Long => node.LongValue.ToString(CultureInfo.InvariantCulture),
            EchoType.Float => node.FloatValue.ToString(CultureInfo.InvariantCulture),
            EchoType.Double => node.DoubleValue.ToString(CultureInfo.InvariantCulture),
            EchoType.Bool => node.BoolValue ? "true" : "false",
            _ => node.StringValue ?? "",
        };
    }

    private static float ParseFloat(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return 0f;
        return float.TryParse(raw.Trim().TrimEnd('F', 'f'), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v
            : 0f;
    }

    private static int ParseInt(string raw, int fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : fallback;
    }

    private static T ParseEnum<T>(string raw, T fallback) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        string trimmed = raw.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            && Enum.IsDefined(typeof(T), n))
        {
            return (T)(object)n;
        }
        return Enum.TryParse(trimmed, ignoreCase: true, out T parsed) ? parsed : fallback;
    }
}
