using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ P2-1 — Resolves Tag/Layer indices to names by reading the project's
/// <c>ProjectSettings/Tags &amp; Layers.yaml</c> offline. The runtime's <c>TagLayerManager</c> lives in
/// XEngine.Runtime and keeps its own in-memory copy; the Host is engine-agnostic, so it parses the
/// on-disk YAML (via <see cref="EchoObject.ReadFromYaml"/>) and surfaces a parallel resolver.
///
/// <para>The file is written by <c>EditorRegistries.SaveSettings</c> as the Echo-YAML form of a
/// <c>TagsAndLayersSettings</c> instance: a compound with <c>Tags</c> (a list of strings) and
/// <c>Layers</c> (a 32-element array of strings, empty slots = unnamed). Indices in scene
/// GameObjects (<c>TagIndex</c>/<c>LayerIndex</c>) index into these lists directly.</para>
///
/// <para>When the file is missing or unparseable, the resolver falls back to the engine's built-in
/// defaults (matching <c>TagLayerManager.ResetDefault</c>) so reads still produce useful names.</para>
/// </summary>
internal sealed class TagLayerResolver
{
    private readonly List<string> _tags;
    private readonly List<string> _layers;

    /// <summary>A lazily-built instance backed by the engine defaults (no project file read).
    /// Used when no project root is available or as a non-throwing fallback.</summary>
    public static TagLayerResolver Default { get; } = new TagLayerResolver(DefaultTags(), DefaultLayers());

    private TagLayerResolver(List<string> tags, List<string> layers)
    {
        _tags = tags;
        _layers = layers;
    }

    /// <summary>The tag name at <paramref name="index"/>, or "Untagged" when out of range.</summary>
    public string GetTag(int index)
        => index >= 0 && index < _tags.Count ? _tags[index] : "Untagged";

    /// <summary>The layer name at <paramref name="index"/>, or "Default" when out of range / empty.</summary>
    public string GetLayer(int index)
    {
        if (index < 0 || index >= _layers.Count) return "Default";
        string name = _layers[index];
        return string.IsNullOrEmpty(name) ? "(empty)" : name;
    }

    /// <summary>Load from a project root (reads <c>ProjectSettings/Tags &amp; Layers.yaml</c>).
    /// Falls back to defaults when the file is absent or unparseable.</summary>
    public static TagLayerResolver Load(string projectRoot)
    {
        string path = Path.Combine(projectRoot, "ProjectSettings", "Tags & Layers.yaml");
        List<string> tags = DefaultTags();
        List<string> layers = DefaultLayers();

        if (File.Exists(path))
        {
            try
            {
                EchoObject root = EchoObject.ReadFromYaml(File.ReadAllText(path));
                if (root.TryGet("Tags", out EchoObject? tagsNode) && tagsNode!.TagType == EchoType.List)
                {
                    tags = ToStringList(tagsNode);
                }
                if (root.TryGet("Layers", out EchoObject? layersNode) && layersNode!.TagType == EchoType.List)
                {
                    layers = ToStringList(layersNode);
                }
            }
            catch
            {
                // Fall back to defaults if the YAML is malformed — never break scene reads.
            }
        }

        return new TagLayerResolver(tags, layers);
    }

    private static List<string> ToStringList(EchoObject listNode)
    {
        var result = new List<string>(listNode.List.Count);
        foreach (EchoObject item in listNode.List)
        {
            result.Add(item.TagType == EchoType.String ? item.StringValue : "");
        }
        return result;
    }

    /// <summary>Engine defaults (mirror of TagLayerManager.ResetDefault) for the fallback path.</summary>
    private static List<string> DefaultTags() =>
        new() { "Untagged", "Main Camera", "Player", "Editor Only", "Re-spawn", "Finish", "Game Controller" };

    private static List<string> DefaultLayers()
    {
        var layers = new List<string>(32) { "Default", "TransparentFX", "Ignore Raycast", "Water" };
        for (int i = 4; i < 32; i++) layers.Add("");
        return layers;
    }
}
