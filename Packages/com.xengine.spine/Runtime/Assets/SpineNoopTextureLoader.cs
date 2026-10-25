using Spine;

namespace XEngine.Spine;

/// <summary>Parse-only <see cref="TextureLoader"/> used at import and headless reload.</summary>
public sealed class SpineNoopTextureLoader : TextureLoader
{
    public static readonly SpineNoopTextureLoader Instance = new();

    public void Load(AtlasPage page, string path) => page.rendererObject = path;

    public void Unload(object texture) { }
}
