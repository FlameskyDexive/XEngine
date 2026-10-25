using Xunit;

using XEngine.Runtime.Resources;

namespace XEngine.Hudmesh.Tests;

public sealed class HudmeshAssetTests
{
    [Fact]
    public void BundledSpriteArt_IsComplete()
    {
        string sprites = Path.Combine(HudmeshTestPaths.PackageRoot(), "Runtime", "Assets", "Sprites");

        // Digit-atlas styles: four sets (b/c/h/l) of 0-9 and +/-.
        foreach (string prefix in new[] { "b", "c", "h", "l" })
        foreach (string digit in new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "+", "-" })
        {
            Assert.True(File.Exists(Path.Combine(sprites, $"{prefix}{digit}.png")), $"missing {prefix}{digit}.png");
        }

        // Bars, bubbles, signs, slider chrome.
        foreach (string name in new[]
                 {
                     "Blood", "Blood_bg", "_Bg", "_Fill_1", "_Fill_2", "_Fill_3", "_Fill_4",
                     "UI_headtop_bubble_001_17", "UI_headtop_sign_001_6", "UI_headtop_sign_002_7",
                     "slider_icontype_01_fill", "slider_icontype_01_frame",
                 })
        {
            Assert.True(File.Exists(Path.Combine(sprites, $"{name}.png")), $"missing {name}.png");
        }
    }

    [Fact]
    public void ShaderSources_ExistWithExpectedDepthTests()
    {
        string top = HudmeshAssetLibrary.ReadShaderSource(HudmeshAssetLibrary.SpriteTopShaderAssetName);
        Assert.Contains("ZTest Off", top, StringComparison.Ordinal);
        Assert.Contains("Blend Alpha", top, StringComparison.Ordinal);

        string occluded = HudmeshAssetLibrary.ReadShaderSource(HudmeshAssetLibrary.SpriteOccludedShaderAssetName);
        Assert.Contains("ZTest LEqual", occluded, StringComparison.Ordinal);
    }

    [Fact]
    public void Shaders_ParseIntoBuiltInAssets()
    {
        // BuiltInAssets registration happens on module load; parsing is pure (no GPU),
        // so the headless test process can validate the full shader text.
        var top = Assert.IsType<Shader>(HudmeshAssetLibrary.LoadSpriteTopShader());
        var occluded = Assert.IsType<Shader>(HudmeshAssetLibrary.LoadSpriteOccludedShader());
        Assert.NotSame(top, occluded);
    }

    [Fact]
    public void PackagePaths_ResolveAgainstRepoLayout()
    {
        string? shader = HudmeshAssetLibrary.FindAssetFile(Path.Combine("Shaders", HudmeshAssetLibrary.SpriteTopShaderAssetName));
        Assert.NotNull(shader);
        Assert.True(File.Exists(shader));

        string? sprite = HudmeshAssetLibrary.FindAssetFile(Path.Combine("Assets", "Sprites", "Blood.png"));
        Assert.NotNull(sprite);
    }
}
