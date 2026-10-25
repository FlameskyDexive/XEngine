using Spine;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Spine38.Tests;

public sealed class Spine38AtlasPageTests
{
    [Fact]
    public void FourPageAtlas_ResolvesEachNamedPageToItsOwnTextureReference()
    {
        var asset = ScriptableObject.CreateInstance<SpineAtlasAsset>();
        var ids = new Guid[4];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = Guid.NewGuid();
            asset.PageFileNames.Add($"effect{i}.png");
            asset.PageTextures.Add(new AssetRef<Texture2D>(ids[i]));
        }
        var loader = new SpineRuntimeTextureLoader(asset);
        for (int i = 0; i < ids.Length; i++)
        {
            var page = new AtlasPage { name = $"effect{i}.png" };
            loader.Load(page, $"missing-atlas-directory/effect{i}.png");
            var reference = Assert.IsType<AssetRef<Texture2D>>(page.rendererObject);
            Assert.Equal(ids[i], reference.AssetID);
        }
        var missing = new AtlasPage { name = "absent.png" };
        loader.Load(missing, "missing-atlas-directory/absent.png");
        Assert.IsType<string>(missing.rendererObject);
    }
}
