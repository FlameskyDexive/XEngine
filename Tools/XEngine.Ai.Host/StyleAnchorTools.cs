using XEngine.Ai.Gateway;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

internal static class StyleAnchorTools
{
    public static StyleAnchorListResult List(string projectRoot)
    {
        var store = new StyleAnchorStore(projectRoot);
        var infos = new List<StyleAnchorInfo>();
        foreach (StyleAnchor a in store.List())
            infos.Add(ToInfo(a));
        return new StyleAnchorListResult(infos);
    }

    public static StyleAnchorInfo Upsert(
        string projectRoot, string id, string? lora, string? palette, string? negative, string? refs)
    {
        var store = new StyleAnchorStore(projectRoot);
        StyleAnchor saved = store.Upsert(new StyleAnchor(
            id.Trim(),
            string.IsNullOrWhiteSpace(lora) ? null : lora,
            Split(palette),
            string.IsNullOrWhiteSpace(negative) ? null : negative,
            Split(refs)));
        return ToInfo(saved);
    }

    private static StyleAnchorInfo ToInfo(StyleAnchor a)
        => new(a.Id, a.LoraModelId, a.Palette ?? Array.Empty<string>(), a.NegativePrompt,
            a.ReferenceImageGuids ?? Array.Empty<string>());

    private static IReadOnlyList<string>? Split(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return null;
        var list = new List<string>();
        foreach (string part in csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            list.Add(part);
        return list.Count == 0 ? null : list;
    }
}
