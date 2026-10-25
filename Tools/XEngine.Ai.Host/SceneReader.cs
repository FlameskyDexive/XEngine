using XEngine.Ai.Protocol;
using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 (B1) — Offline, dependency-free reader of <c>.scene</c> files. The Host stays
/// engine-agnostic (no Runtime/Editor reference): instead of deserializing to a live <c>Scene</c>,
/// this walks the raw <c>EchoObject</c> tag tree produced by the editor's save path
/// (<c>EchoObject.ReadFromString</c>). That is exactly the shape an AI agent needs to reason about a
/// scene's hierarchy, and it keeps the CLI Host a lightweight standalone binary.
/// </summary>
internal static class SceneReader
{
    /// <summary>List .scene files under the project's Assets directory (empty when no Assets dir),
    /// paged with offset+limit. ★ Unified pagination shape (offset/limit/total) matching journal_query/code_symbols.</summary>
    public static SceneFileList ListScenes(string? assetsRoot, string? query = null, int offset = 0, int limit = 100)
    {
        if (string.IsNullOrEmpty(assetsRoot) || !Directory.Exists(assetsRoot))
        {
            return new SceneFileList(assetsRoot ?? "", Array.Empty<SceneFile>(), 0, offset, limit, false);
        }

        var files = new List<SceneFile>();
        foreach (string path in Directory.EnumerateFiles(assetsRoot, "*.scene", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(assetsRoot, path).Replace('\\', '/');
            if (query is not null && relative.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string name = Path.GetFileNameWithoutExtension(path);
            var info = new FileInfo(path);
            files.Add(new SceneFile(name, relative, info.Length, info.LastWriteTimeUtc));
        }

        files.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));
        int total = files.Count;
        int clampedLimit = Math.Clamp(limit, 1, 500);
        var page = files.Skip(offset).Take(clampedLimit).ToArray();
        bool hasMore = offset + page.Length < total;
        return new SceneFileList(assetsRoot!, page, total, offset, clampedLimit, hasMore);
    }

    /// <summary>
    /// Read a scene's hierarchy from disk without loading it into an engine. The XEngine .scene
    /// format stores GameObjects as a flat <c>serializeObj</c> list; each GO has <c>Name</c>,
    /// <c>Transform</c> (Position/Rotation/Scale), <c>Tag</c>, <c>Layer</c>, <c>Children</c> (guids),
    /// and <c>Components</c>. We surface all of these generically.
    /// </summary>
    public static SceneHierarchy ReadHierarchy(string absolutePath, int maxDepth = 32, int maxObjects = 500, TagLayerResolver? tagLayer = null)
    {
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Scene file not found: {absolutePath}");
        }

        string text = File.ReadAllText(absolutePath);
        return EchoFacade.ReadSceneHierarchy(text, absolutePath, maxDepth, maxObjects, tagLayer);
    }

    /// <summary>★ prefab — Read a .prefab's hierarchy. A .prefab is a single GameObject tree on disk
    /// (no serializeObj.array wrapper). We wrap it in a pseudo-scene shape so EchoFacade.ReadSceneHierarchy
    /// can reuse the same read path, then it surfaces as a single root.</summary>
    public static SceneHierarchy ReadPrefabHierarchy(string absolutePath, int maxDepth = 32, int maxObjects = 500, TagLayerResolver? tagLayer = null)
    {
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Prefab file not found: {absolutePath}");
        }

        EchoObject prefabRoot = EchoObject.ReadFromString(File.ReadAllText(absolutePath));
        // Build a pseudo-scene: { serializeObj: { array: [ prefabRoot ] } } so ReadSceneHierarchy works.
        EchoObject array = EchoObject.NewList();
        array.ListAdd(prefabRoot);
        EchoObject serObj = EchoObject.NewCompound();
        serObj["array"] = array;
        EchoObject pseudoScene = EchoObject.NewCompound();
        pseudoScene["serializeObj"] = serObj;

        return EchoFacade.ReadSceneHierarchy(pseudoScene.WriteToString(), absolutePath, maxDepth, maxObjects, tagLayer);
    }

    /// <summary>★ P0-6 — Read a .mat file's shader + properties as a structured MaterialDetail.</summary>
    public static MaterialDetail ReadMaterial(string absolutePath, string targetAsset)
    {
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Material file not found: {absolutePath}");
        }
        return EchoFacade.ReadMaterial(File.ReadAllText(absolutePath), absolutePath, targetAsset);
    }

    /// <summary>★ P2-2 — Read a scene's top-level Fog/Ambient/Skybox settings.</summary>
    public static SceneSettings ReadSceneSettings(string absolutePath, string targetAsset)
    {
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Scene file not found: {absolutePath}");
        }
        return EchoFacade.ReadSceneSettings(File.ReadAllText(absolutePath), targetAsset);
    }

    /// <summary>★ C1 — Read a generic .asset file (ScriptableObject) as a flat path/value list.</summary>
    public static AssetDataDetail ReadAssetData(string absolutePath, string targetAsset)
    {
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Asset data file not found: {absolutePath}");
        }
        return EchoFacade.ReadAssetData(File.ReadAllText(absolutePath), targetAsset);
    }
}
