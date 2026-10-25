using System.Globalization;

using XEngine.Echo;
using XEngine.Runtime.Rendering;

namespace XEngine.Ai.Host;

/// <summary>
/// Maps an Echo <c>.shadergraph</c> tree onto the Shader Graph POCOs used by
/// <see cref="ShaderGraphSourceCompiler"/>. Host stays engine-agnostic (no ScriptableObject).
/// </summary>
internal static class ShaderGraphEcho
{
    public static string CompileToSource(string echoText)
    {
        Load(echoText, out string shaderName, out ShaderGraphTargetKind target,
            out List<ShaderGraphProperty> properties, out List<ShaderGraphNode> nodes,
            out List<ShaderGraphEdge> edges);
        return ShaderGraphSourceCompiler.CompileToSource(shaderName, target, properties, nodes, edges);
    }

    public static void Load(
        string echoText,
        out string shaderName,
        out ShaderGraphTargetKind target,
        out List<ShaderGraphProperty> properties,
        out List<ShaderGraphNode> nodes,
        out List<ShaderGraphEdge> edges)
    {
        EchoObject root = EchoObject.ReadFromString(echoText);
        shaderName = Scalar(root, "ShaderName");
        if (string.IsNullOrWhiteSpace(shaderName))
            shaderName = "Graph/Unnamed";
        target = ParseEnum(Scalar(root, "Target"), ShaderGraphTargetKind.Unlit);

        properties = new List<ShaderGraphProperty>();
        foreach (EchoObject item in Children(root, "Properties"))
        {
            properties.Add(new ShaderGraphProperty
            {
                Id = Scalar(item, "Id"),
                Name = Scalar(item, "Name"),
                DisplayName = Scalar(item, "DisplayName"),
                Type = ParseEnum(Scalar(item, "Type"), ShaderGraphValueType.Float),
                DefaultValue = Scalar(item, "DefaultValue"),
            });
        }

        nodes = new List<ShaderGraphNode>();
        foreach (EchoObject item in Children(root, "Nodes"))
        {
            var node = new ShaderGraphNode
            {
                Id = Scalar(item, "Id"),
                Type = Scalar(item, "Type"),
                PosX = ParseFloat(Scalar(item, "PosX")),
                PosY = ParseFloat(Scalar(item, "PosY")),
            };
            foreach (EchoObject field in Children(item, "Fields"))
            {
                node.Fields.Add(new ShaderGraphNodeField
                {
                    Key = Scalar(field, "Key"),
                    Value = Scalar(field, "Value"),
                });
            }
            nodes.Add(node);
        }

        edges = new List<ShaderGraphEdge>();
        foreach (EchoObject item in Children(root, "Edges"))
        {
            edges.Add(new ShaderGraphEdge
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
