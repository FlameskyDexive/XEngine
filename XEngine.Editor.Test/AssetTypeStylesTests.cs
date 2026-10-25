// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.GUI.Registries;
using XEngine.Editor.Thumbnails;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class AssetTypeStylesTests
{
    public static IEnumerable<object[]> KnownExtensions() =>
    [
        [".mat"], [".prefab"], [".scene"], [".shader"], [".mesh"], [".fbx"],
        [".png"], [".wav"], [".ttf"], [".anim"], [".controller"], [".asset"],
        [".navmesh"], [".inputactions"], [".timeline"], [".pipeline"],
        [".physicmaterial"], [".buildprofile"], [".vfx"], [".shadergraph"],
        [".terraindata"], [".terrainlayer"], [".tile"], [".aseprite"],
    ];

    [Theory]
    [MemberData(nameof(KnownExtensions))]
    public void KnownExtensions_HaveADedicatedIcon(string extension)
    {
        AssetTypeStyle style = AssetTypeStyles.For(extension);
        AssetTypeStyle unknown = AssetTypeStyles.For(".unknown_ext_zzz");
        Assert.NotNull(style.Icon);
        if (style.Badge == null)
            Assert.NotSame(unknown.Icon, style.Icon);
    }

    [Fact]
    public void Material_UsesUnityTypePng()
    {
        AssetTypeStyle style = AssetTypeStyles.For(".mat", "Material");
        Assert.NotNull(style.Icon);
        Assert.True(style.PreserveIconColor);
    }

    [Fact]
    public void TypeLabel_ResolvesUnityPngWithoutExtension()
    {
        AssetTypeStyle style = AssetTypeStyles.For("", "Material");
        Assert.True(style.PreserveIconColor);
        Assert.NotNull(style.Icon);
    }

    [Fact]
    public void PrefabAsset_TypeLabel_UsesPrefabNormal()
    {
        AssetTypeStyle style = AssetTypeStyles.For("", "PrefabAsset");
        Assert.True(style.PreserveIconColor);
        Assert.NotSame(AssetTypeStyles.For(".unknown_ext_zzz").Icon, style.Icon);
    }

    [Fact]
    public void Folder_UsesBareTypeIcon()
    {
        Assert.True(AssetTypeStyles.Folder.Bare);
        Assert.NotNull(AssetTypeStyles.Folder.Icon);
    }
}

public sealed class ThumbnailPreviewTests
{
    [Fact]
    public void TransparentBuffer_IsUnusable()
    {
        Assert.True(ThumbnailGenerator.IsUnusablePreview(new byte[64 * 64 * 4]));
    }

    [Fact]
    public void FlatOpaqueBuffer_IsUnusable()
    {
        var pixels = new byte[32 * 32 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 20;
            pixels[i + 1] = 20;
            pixels[i + 2] = 22;
            pixels[i + 3] = 255;
        }
        Assert.True(ThumbnailGenerator.IsUnusablePreview(pixels));
    }

    [Fact]
    public void VariedOpaqueBuffer_IsUsable()
    {
        var pixels = new byte[32 * 32 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte v = (byte)((i / 4) % 256);
            pixels[i] = v;
            pixels[i + 1] = v;
            pixels[i + 2] = v;
            pixels[i + 3] = 255;
        }
        Assert.False(ThumbnailGenerator.IsUnusablePreview(pixels));
    }
}
