using System;
using System.IO;

using Spine;

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Spine;

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
            // The mesh generator needs the page texture the moment the first batch is built. Resolve
            // through AssetDatabase.Get (synchronous on the main thread): a non-blocking Res stays
            // null until an async stream-in that may never be pumped (idle editor), and
            // EnsureLoaded would deserialize + upload the GPU texture on the background loader
            // thread, which is not a valid render context. Get keeps GPU work on the main thread.
            Texture2D? texture = AssetDatabase.Get(textureRef.AssetID) as Texture2D;
            page.rendererObject = texture ?? (object)textureRef;
            return;
        }

        page.rendererObject = path;
    }

    public void Unload(object texture) { }

    private static bool NamesMatch(string pageFileName, string pathFileName, string pageName)
        => string.Equals(pageFileName, pathFileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(pageFileName, pageName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(pageName, pathFileName, StringComparison.OrdinalIgnoreCase);
}
