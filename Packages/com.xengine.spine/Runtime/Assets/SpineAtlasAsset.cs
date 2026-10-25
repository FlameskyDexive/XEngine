using System;
using System.Collections.Generic;
using System.IO;

using Spine;

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Spine;

/// <summary>Imported Spine atlas (<c>.atlas</c> / <c>.atlas.txt</c>).</summary>
public sealed class SpineAtlasAsset : ScriptableObject
{
    public string AtlasText = "";
    public List<string> PageFileNames = [];
    public List<string> RegionNames = [];
    public List<AssetRef<Texture2D>> PageTextures = [];

    public Atlas CreateAtlas(TextureLoader? loader = null)
    {
        using var reader = new StringReader(AtlasText);
        var atlas = new Atlas(reader, "", loader ?? SpineNoopTextureLoader.Instance);
        atlas.FlipV();
        return atlas;
    }

    /// <summary>Replace one page texture. Live skeletons also need <see cref="SpineAtlasSwap.RebindSkeletonPages"/>.</summary>
    public void SetPageTexture(int index, Texture2D? texture)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        while (PageTextures.Count <= index)
            PageTextures.Add(default);
        PageTextures[index] = texture;
    }

    /// <summary>Build an atlas asset from export text and optional page textures (runtime / tests).</summary>
    public static SpineAtlasAsset CreateRuntime(string atlasText, params Texture2D?[] pageTextures)
    {
        var asset = ScriptableObject.CreateInstance<SpineAtlasAsset>();
        asset.AtlasText = atlasText ?? "";
        using var reader = new StringReader(asset.AtlasText);
        var atlas = new Atlas(reader, "", SpineNoopTextureLoader.Instance);
        for (int i = 0; i < atlas.Pages.Count; i++)
            asset.PageFileNames.Add(atlas.Pages[i].name);
        for (int i = 0; i < atlas.Regions.Count; i++)
            asset.RegionNames.Add(atlas.Regions[i].name);

        int textureCount = pageTextures.Length;
        int pageCount = asset.PageFileNames.Count;
        int n = textureCount > pageCount ? textureCount : pageCount;
        for (int i = 0; i < n; i++)
            asset.PageTextures.Add(i < textureCount ? pageTextures[i] : null);
        return asset;
    }
}
