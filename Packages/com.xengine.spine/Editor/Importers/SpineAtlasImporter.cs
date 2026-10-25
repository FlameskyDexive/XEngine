using System;
using System.Collections.Generic;
using System.IO;

using Spine;

using XEngine.Editor;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Spine;

namespace XEngine.Spine.Editor;

/// <summary>
/// Claims <c>.atlas</c> always and <c>.txt</c> only when the file is <c>*.atlas.txt</c>.
/// </summary>
[ImporterFor(".atlas", ".txt", Priority = 100)]
public sealed class SpineAtlasImporter : AssetImporter
{
    public override int Version => 1;

    public override bool CanImport(string absolutePath)
    {
        string ext = Path.GetExtension(absolutePath);
        if (ext.Equals(".atlas", StringComparison.OrdinalIgnoreCase))
            return true;
        return ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            && SpineAssetPaths.IsAtlasTxtFileName(absolutePath);
    }

    public override bool Import(ImportContext ctx)
    {
        if (!CanImport(ctx.AbsolutePath))
            return false;

        string text = File.ReadAllText(ctx.AbsolutePath);
        Atlas atlas;
        try
        {
            using var reader = new StringReader(text);
            atlas = new Atlas(reader, Path.GetDirectoryName(ctx.AbsolutePath) ?? "", SpineNoopTextureLoader.Instance);
        }
        catch (Exception)
        {
            return false;
        }

        var pageFileNames = new List<string>(atlas.Pages.Count);
        var pageTextures = new List<AssetRef<Texture2D>>(atlas.Pages.Count);
        for (int i = 0; i < atlas.Pages.Count; i++)
        {
            string pageName = atlas.Pages[i].name;
            pageFileNames.Add(pageName);

            string? dir = Path.GetDirectoryName(ctx.AbsolutePath);
            if (string.IsNullOrEmpty(dir))
                continue;

            string pagePath = Path.Combine(dir, pageName);
            if (!TryAddMetaDependency(pagePath, ctx, nameof(Texture2D), out Guid guid))
                continue;
            pageTextures.Add(new AssetRef<Texture2D>(guid));
        }

        var regionNames = new List<string>(atlas.Regions.Count);
        for (int i = 0; i < atlas.Regions.Count; i++)
            regionNames.Add(atlas.Regions[i].name);

        var asset = new SpineAtlasAsset
        {
            Name = SpineAssetPaths.GetDisplayName(ctx.AbsolutePath),
            AtlasText = text,
            PageFileNames = pageFileNames,
            RegionNames = regionNames,
            PageTextures = pageTextures,
        };
        ctx.SetMainAsset(asset);
        return true;
    }

    internal static bool TryAddMetaDependency(string assetPath, ImportContext ctx, string role, out Guid guid)
    {
        guid = Guid.Empty;
        string metaPath = MetaFile.GetMetaPath(assetPath);
        if (!File.Exists(metaPath))
            return false;

        try
        {
            MetaFileData meta = MetaFile.Read(metaPath);
            if (meta.Guid == Guid.Empty)
                return false;
            guid = meta.Guid;
            ctx.AddDependency(guid, role);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
