using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Core;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Packages;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.XGUI.Editor;

/// <summary>
/// Instantiates the package-owned default control templates. Template data owns the GameObject
/// hierarchy, components, visual defaults, and serialized component links; the menu layer only
/// supplies the scene parent and authoring selection behaviour.
/// </summary>
internal static class XGuiTemplateFactory
{
    private const string TemplateRoot = "Packages/com.xengine.xgui/Editor/Templates/";
    private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;

    internal static GameObject Create(string templateName, GameObject? parent)
    {
        TemplateDefinition definition = Load(templateName);
        if (definition.Root == null)
            throw new InvalidDataException("XGUI template '" + templateName + "' does not define a root node.");

        var nodes = new Dictionary<string, CreatedNode>(StringComparer.Ordinal);
        var created = new List<GameObject>();
        try
        {
            GameObject root = CreateNode(definition.Root, parent, nodes, created);
            var undoRoots = new List<GameObject> { root };
            if (definition.CanvasRoots != null)
            {
                GameObject canvasRoot = ResolveCanvasRoot(root);
                for (int i = 0; i < definition.CanvasRoots.Count; i++)
                    undoRoots.Add(CreateNode(definition.CanvasRoots[i], canvasRoot, nodes, created));
            }
            ApplyBindings(definition.Bindings, nodes, templateName);
            ApplyEnabledStates(nodes);
            Undo.RegisterCreatedObjectGroup(undoRoots, "Create " + templateName);
            Selection.Select(root);
            return root;
        }
        catch
        {
            RollbackCreated(created);
            throw;
        }
    }

    /// <summary>
    /// Destroy everything a failed template creation produced, in reverse creation order
    /// (children before parents), so no half-built hierarchy is left in the scene.
    /// </summary>
    private static void RollbackCreated(List<GameObject> created)
    {
        Scene? scene = Scene.Current;
        for (int i = created.Count - 1; i >= 0; i--)
        {
            GameObject go = created[i];
            if (go.IsDisposed) continue;
            scene?.Remove(go);
            go.Dispose();
        }
    }

    private static void ApplyEnabledStates(Dictionary<string, CreatedNode> nodes)
    {
        foreach (CreatedNode node in nodes.Values)
        {
            if (node.Enabled.HasValue)
                node.GameObject.Enabled = node.Enabled.Value;
        }
    }

    private static TemplateDefinition Load(string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName)
            || templateName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || templateName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid XGUI template name.", nameof(templateName));
        }

        Project? project = Project.Current;
        if (project == null)
            throw new InvalidOperationException("XGUI default controls require an open project.");

        string logicalPath = TemplateRoot + templateName + ".xgui-template.json";
        if (!PackageManager.TryResolveLogicalAssetPath(project, logicalPath, out string physicalPath)
            || !File.Exists(physicalPath))
        {
            throw new FileNotFoundException("XGUI default control template was not found at '" + logicalPath + "'.", logicalPath);
        }

        TemplateDefinition? definition = JsonSerializer.Deserialize<TemplateDefinition>(File.ReadAllText(physicalPath));
        if (definition == null)
            throw new InvalidDataException("XGUI template '" + logicalPath + "' is empty.");
        return definition;
    }

    private static GameObject CreateNode(TemplateNode node, GameObject? parent,
        Dictionary<string, CreatedNode> nodes, List<GameObject> createdObjects)
    {
        if (string.IsNullOrWhiteSpace(node.Id))
            throw new InvalidDataException("An XGUI template node is missing its id.");
        if (!nodes.TryAdd(node.Id, new CreatedNode { Enabled = node.Enabled }))
            throw new InvalidDataException("XGUI template contains duplicate node id '" + node.Id + "'.");

        // registerUndo: false — the factory registers ONE grouped undo step for the whole template
        // after creation succeeds, so a single Ctrl+Z removes the entire control.
        GameObject go = HierarchyPanel.CreateGameObject(
            string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name,
            parent,
            select: false,
            beginRename: false,
            registerUndo: false);
        createdObjects.Add(go);
        CreatedNode created = nodes[node.Id];
        created.GameObject = go;

        if (node.Rect != null)
        {
            RectTransform rect = go.EnsureRectTransform();
            created.Components.Add("RectTransform", rect);
            ApplyRect(rect, node.Rect);
        }

        if (node.Components != null)
        {
            for (int i = 0; i < node.Components.Count; i++)
            {
                TemplateComponent componentDefinition = node.Components[i];
                Type componentType = ResolveComponentType(componentDefinition.Type);
                MonoBehaviour component = go.AddComponent(componentType);
                if (!created.Components.TryAdd(componentDefinition.Type, component))
                    throw new InvalidDataException("Node '" + node.Id + "' repeats component type '" + componentDefinition.Type + "'.");
                ApplyProperties(component, componentDefinition.Properties, node.Id, componentDefinition.Type);
            }
        }

        if (node.Children != null)
        {
            for (int i = 0; i < node.Children.Count; i++)
                CreateNode(node.Children[i], go, nodes, createdObjects);
        }

        return go;
    }

    private static GameObject ResolveCanvasRoot(GameObject root)
    {
        GameCanvas? canvas = root.GetComponentInParent<GameCanvas>(includeSelf: true);
        if (canvas == null)
            throw new InvalidDataException("XGUI template defines a canvas popup but its root is not under a Canvas.");
        return canvas.GameObject;
    }

    private static void ApplyRect(RectTransform rect, TemplateRect definition)
    {
        if (definition.Stretch)
        {
            rect.AnchorMin = Float2.Zero;
            rect.AnchorMax = Float2.One;
            rect.SizeDelta = Float2.Zero;
            rect.AnchoredPosition = Float2.Zero;
        }
        if (definition.AnchorMin != null) rect.AnchorMin = ToFloat2(definition.AnchorMin, "anchorMin");
        if (definition.AnchorMax != null) rect.AnchorMax = ToFloat2(definition.AnchorMax, "anchorMax");
        if (definition.Pivot != null) rect.Pivot = ToFloat2(definition.Pivot, "pivot");
        if (definition.Size != null) rect.SizeDelta = ToFloat2(definition.Size, "size");
        if (definition.Position != null) rect.AnchoredPosition = ToFloat2(definition.Position, "position");
    }

    private static void ApplyProperties(MonoBehaviour component, Dictionary<string, JsonElement>? properties,
        string nodeId, string componentName)
    {
        if (properties == null) return;
        foreach ((string propertyName, JsonElement value) in properties)
        {
            PropertyInfo? property = component.GetType().GetProperty(propertyName, PublicInstance);
            if (property == null)
            {
                throw new InvalidDataException("XGUI template node '" + nodeId + "' component '" + componentName
                    + "' has no property '" + propertyName + "'.");
            }

            if (property.CanWrite)
            {
                property.SetValue(component, ConvertValue(value, property.PropertyType, propertyName));
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array && property.GetValue(component) is IList list)
            {
                list.Clear();
                foreach (JsonElement item in value.EnumerateArray())
                    list.Add(ConvertValue(item, typeof(string), propertyName));
                continue;
            }

            throw new InvalidDataException("XGUI template node '" + nodeId + "' cannot assign property '" + propertyName + "'.");
        }
    }

    private static void ApplyBindings(List<TemplateBinding>? bindings, Dictionary<string, CreatedNode> nodes,
        string templateName)
    {
        if (bindings == null) return;
        for (int i = 0; i < bindings.Count; i++)
        {
            TemplateBinding binding = bindings[i];
            MonoBehaviour source = ResolveComponent(nodes, binding.Node, binding.Component, templateName);
            MonoBehaviour target = ResolveComponent(nodes, binding.TargetNode, binding.TargetComponent, templateName);
            PropertyInfo? property = source.GetType().GetProperty(binding.Property, PublicInstance);
            if (property == null || !property.CanWrite || !property.PropertyType.IsAssignableFrom(target.GetType()))
            {
                throw new InvalidDataException("XGUI template '" + templateName + "' cannot bind "
                    + binding.Node + "." + binding.Component + "." + binding.Property + ".");
            }
            property.SetValue(source, target);
        }
    }

    private static MonoBehaviour ResolveComponent(Dictionary<string, CreatedNode> nodes, string nodeId,
        string componentName, string templateName)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(componentName)
            || !nodes.TryGetValue(nodeId, out CreatedNode? node)
            || !node.Components.TryGetValue(componentName, out MonoBehaviour? component))
        {
            throw new InvalidDataException("XGUI template '" + templateName + "' references missing component '"
                + nodeId + "." + componentName + "'.");
        }
        return component;
    }

    private static Type ResolveComponentType(string componentName)
    {
        return componentName switch
        {
            "GameCanvas" => typeof(GameCanvas),
            "CanvasScaler" => typeof(CanvasScaler),
            "CanvasGroup" => typeof(CanvasGroup),
            "CanvasRenderer" => typeof(CanvasRenderer),
            "EventSystem" => typeof(EventSystem),
            "StandaloneInputModule" => typeof(StandaloneInputModule),
            "Image" => typeof(Image),
            "RawImage" => typeof(RawImage),
            "Text" => typeof(Text),
            "RaycastReceiver" => typeof(RaycastReceiver),
            "Button" => typeof(Button),
            "Toggle" => typeof(Toggle),
            "ToggleGroup" => typeof(ToggleGroup),
            "Slider" => typeof(Slider),
            "Scrollbar" => typeof(Scrollbar),
            "ScrollRect" => typeof(ScrollRect),
            "InputField" => typeof(InputField),
            "Dropdown" => typeof(Dropdown),
            "RectMask2D" => typeof(RectMask2D),
            "Mask" => typeof(Mask),
            "HorizontalLayoutGroup" or "VerticalLayoutGroup" or "GridLayoutGroup" or "LayoutElement"
                or "ContentSizeFitter" or "AspectRatioFitter" or "SafeAreaFitter" => ResolveXguiRuntimeType(componentName),
            _ => throw new InvalidDataException("XGUI template requests unsupported component type '" + componentName + "'."),
        };
    }

    private static Type ResolveXguiRuntimeType(string componentName)
    {
        Type? type = typeof(ToggleGroup).Assembly.GetType("XEngine.Runtime.UI." + componentName);
        if (type == null)
            throw new InvalidDataException("The XGUI runtime does not provide component type '" + componentName + "'.");
        return type;
    }

    private static object ConvertValue(JsonElement value, Type targetType, string propertyName)
    {
        if (targetType == typeof(string)) return value.GetString() ?? string.Empty;
        if (targetType == typeof(bool)) return value.GetBoolean();
        if (targetType == typeof(int)) return value.GetInt32();
        if (targetType == typeof(float)) return value.GetSingle();
        if (targetType == typeof(double)) return value.GetDouble();
        if (targetType == typeof(Float2)) return ToFloat2(ReadNumberArray(value, propertyName), propertyName);
        if (targetType == typeof(Color))
        {
            float[] values = ReadNumberArray(value, propertyName);
            if (values.Length != 4) throw new InvalidDataException("XGUI template color '" + propertyName + "' needs four values.");
            return new Color(values[0], values[1], values[2], values[3]);
        }
        if (targetType.IsEnum)
            return Enum.Parse(targetType, value.GetString() ?? string.Empty, ignoreCase: true);

        throw new InvalidDataException("XGUI template does not support property type '" + targetType.Name
            + "' for '" + propertyName + "'.");
    }

    private static Float2 ToFloat2(float[] values, string name)
    {
        if (values.Length != 2) throw new InvalidDataException("XGUI template vector '" + name + "' needs two values.");
        return new Float2(values[0], values[1]);
    }

    private static float[] ReadNumberArray(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("XGUI template property '" + name + "' needs an array.");
        var values = new List<float>();
        foreach (JsonElement item in value.EnumerateArray()) values.Add(item.GetSingle());
        return values.ToArray();
    }

    private sealed class CreatedNode
    {
        public GameObject GameObject { get; set; } = null!;
        public bool? Enabled { get; init; }
        public Dictionary<string, MonoBehaviour> Components { get; } = new(StringComparer.Ordinal);
    }

    private sealed class TemplateDefinition
    {
        [JsonPropertyName("root")] public TemplateNode? Root { get; set; }
        [JsonPropertyName("canvasRoots")] public List<TemplateNode>? CanvasRoots { get; set; }
        [JsonPropertyName("bindings")] public List<TemplateBinding>? Bindings { get; set; }
    }

    private sealed class TemplateNode
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
        [JsonPropertyName("rect")] public TemplateRect? Rect { get; set; }
        [JsonPropertyName("components")] public List<TemplateComponent>? Components { get; set; }
        [JsonPropertyName("children")] public List<TemplateNode>? Children { get; set; }
    }

    private sealed class TemplateRect
    {
        [JsonPropertyName("stretch")] public bool Stretch { get; set; }
        [JsonPropertyName("anchorMin")] public float[]? AnchorMin { get; set; }
        [JsonPropertyName("anchorMax")] public float[]? AnchorMax { get; set; }
        [JsonPropertyName("pivot")] public float[]? Pivot { get; set; }
        [JsonPropertyName("size")] public float[]? Size { get; set; }
        [JsonPropertyName("position")] public float[]? Position { get; set; }
    }

    private sealed class TemplateComponent
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("properties")] public Dictionary<string, JsonElement>? Properties { get; set; }
    }

    private sealed class TemplateBinding
    {
        [JsonPropertyName("node")] public string Node { get; set; } = string.Empty;
        [JsonPropertyName("component")] public string Component { get; set; } = string.Empty;
        [JsonPropertyName("property")] public string Property { get; set; } = string.Empty;
        [JsonPropertyName("targetNode")] public string TargetNode { get; set; } = string.Empty;
        [JsonPropertyName("targetComponent")] public string TargetComponent { get; set; } = string.Empty;
    }
}
