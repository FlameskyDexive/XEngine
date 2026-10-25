namespace XEngine.Ai.Gateway;

/// <summary>★ Gen-P3.2 — Load/save project style anchors from <c>.xengine/style-anchors.json</c>.</summary>
public sealed class StyleAnchorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public StyleAnchorStore(string projectRoot)
    {
        _path = Path.Combine(projectRoot, ".xengine", "style-anchors.json");
    }

    public IReadOnlyList<StyleAnchor> List() => Load().Anchors;

    public StyleAnchor? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        foreach (StyleAnchor a in Load().Anchors)
        {
            if (string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))
                return a;
        }

        return null;
    }

    public StyleAnchor Upsert(StyleAnchor anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor.Id))
            throw new ArgumentException("Style anchor id is required.");

        List<StyleAnchor> list = Load().Anchors.ToList();
        int idx = list.FindIndex(a => string.Equals(a.Id, anchor.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            list[idx] = anchor;
        else
            list.Add(anchor);

        Save(new StyleAnchorDocument(list));
        return anchor;
    }

    /// <summary>Fold the anchor into prompt + extra. Caller-supplied extra keys win.</summary>
    public static (string Prompt, IReadOnlyDictionary<string, string>? Extra) Apply(
        StyleAnchor anchor, string prompt, IReadOnlyDictionary<string, string>? extra)
    {
        string nextPrompt = prompt;
        if (!string.IsNullOrWhiteSpace(anchor.NegativePrompt)
            && nextPrompt.IndexOf(anchor.NegativePrompt, StringComparison.Ordinal) < 0)
        {
            nextPrompt = prompt + "\nNegative: " + anchor.NegativePrompt;
        }

        var merged = extra is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(extra, StringComparer.Ordinal);

        void Put(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || merged.ContainsKey(key))
                return;
            merged[key] = value;
        }

        Put("loraModelId", anchor.LoraModelId);
        if (anchor.Palette is { Count: > 0 })
            Put("palette", string.Join(",", anchor.Palette));
        if (anchor.ReferenceImageGuids is { Count: > 0 })
            Put("referenceImageGuids", string.Join(",", anchor.ReferenceImageGuids));

        return (nextPrompt, merged.Count == 0 ? extra : merged);
    }

    private StyleAnchorDocument Load()
    {
        if (!File.Exists(_path))
            return new StyleAnchorDocument(Array.Empty<StyleAnchor>());
        string text = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<StyleAnchorDocument>(text, JsonOptions)
            ?? new StyleAnchorDocument(Array.Empty<StyleAnchor>());
    }

    private void Save(StyleAnchorDocument doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
