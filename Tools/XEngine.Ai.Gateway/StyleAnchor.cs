namespace XEngine.Ai.Gateway;

/// <summary>★ Gen-P3.2 — One project style anchor: LoRA + palette + negatives + reference GUIDs.</summary>
public sealed record StyleAnchor(
    string Id,
    string? LoraModelId = null,
    IReadOnlyList<string>? Palette = null,
    string? NegativePrompt = null,
    IReadOnlyList<string>? ReferenceImageGuids = null);

/// <summary>On-disk document at <c>.xengine/style-anchors.json</c>.</summary>
public sealed record StyleAnchorDocument(IReadOnlyList<StyleAnchor> Anchors);
