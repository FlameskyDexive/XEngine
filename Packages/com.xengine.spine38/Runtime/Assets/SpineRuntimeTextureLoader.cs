using System;
using System.IO;

using Spine;

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Spine38;

/// <summary>Binds atlas page <see cref="AtlasPage.rendererObject"/> to imported <see cref="Texture2D"/> refs.</summary>
public sealed class SpineRuntimeTextureLoader : TextureLoader
{
    private readonly SpineAtlasAsset _atlas;

    public SpineRuntimeTextureLoader(SpineAtlasAsset atlas) => _atlas = atlas;

    public void Load(AtlasPage page, string path)
    {
        string fileName = Path.GetFileName(path);
        for (int i = 0; i < _atlas.PageFileNames.Count; i++)
        {
            if (!NamesMatch(_atlas.PageFileNames[i], fileName, page.name))
                continue;
            if (i >= _atlas.PageTextures.Count)
                break;
            AssetRef<Texture2D> textureRef = _atlas.PageTextures[i];
            Texture2D? texture = textureRef.AssetID != Guid.Empty
                ? AssetDatabase.Get(textureRef.AssetID) as Texture2D
                : null;
            if (texture != null)
            {
                page.rendererObject = texture;
                return;
            }

            if (textureRef.AssetID != Guid.Empty)
            {
                page.rendererObject = textureRef;
                return;
            }
        }

        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                page.rendererObject = Texture2D.LoadFromFile(path);
                return;
            }
            catch (Exception)
            {
                // Fall through to the path marker so ResolveTexture can still report a miss.
            }
        }

        page.rendererObject = path;
    }

    public void Unload(object texture) { }

    private static bool NamesMatch(string pageFileName, string pathFileName, string pageName)
        => string.Equals(pageFileName, pathFileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(pageFileName, pageName, StringComparison.OrdinalIgnoreCase);
}
