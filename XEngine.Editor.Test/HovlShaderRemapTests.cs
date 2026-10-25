using System.IO;
using System.Linq;

using XEngine.RpgVfx.Editor;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// rpgvfx A4 — Hovl shader remap contract: all 12 source Unity shader GUIDs map to ported
/// engine shaders, the Add_CenterGlow _Blend2 split resolves, fallbacks carry warnings, and
/// every mapped Hovl/* shader has a .shader file in the package.
/// </summary>
public sealed class HovlShaderRemapTests
{
    private static string ShadersDir
    {
        get
        {
            string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            return Path.Combine(repoRoot, "Packages", "com.xengine.rpgvfx", "Runtime", "Shaders");
        }
    }

    [Fact]
    public void RemapTable_CoversAllTwelveSourceShaders()
    {
        Assert.Equal(13, HovlShaderRemap.EngineShaderByGuid.Count);
        foreach (var (shortName, guid) in HovlShaderRemap.ShadersByGuid)
            Assert.True(HovlShaderRemap.EngineShaderByGuid.ContainsKey(guid), $"missing GUID mapping for {shortName}");
    }

    [Fact]
    public void AddCenterGlow_Blend2_SplitsIntoTwoShaders()
    {
        const string guid = "bd6af5d577c618947b73aa60db554e51";

        Assert.True(HovlShaderRemap.TryResolveEngineShader(guid, 1f, out var one, out _));
        Assert.Equal("Hovl/Add_CenterGlow", one);

        Assert.True(HovlShaderRemap.TryResolveEngineShader(guid, 6f, out var fade, out _));
        Assert.Equal("Hovl/Add_CenterGlowFade", fade);
    }

    [Fact]
    public void FallbackEntries_CarryWarnings()
    {
        Assert.True(HovlShaderRemap.TryResolveEngineShader("ca825309dbbb03640874997c5db5cb48", 0f, out _, out var distortWarn));
        Assert.NotNull(distortWarn);

        Assert.True(HovlShaderRemap.TryResolveEngineShader("c6a2ded4c4b574a46babd82f68db8f4d", 0f, out _, out var normalsWarn));
        Assert.NotNull(normalsWarn);

        Assert.True(HovlShaderRemap.TryResolveEngineShader("13a43378542625f489f7c1c85ba6a4c4", 0f, out _, out var litWarn));
        Assert.NotNull(litWarn);

        // Direct ports must NOT warn.
        Assert.True(HovlShaderRemap.TryResolveEngineShader("734ad3a5e0f9f384683a822995bcecd3", 0f, out _, out var smokeWarn));
        Assert.Null(smokeWarn);
    }

    [Fact]
    public void EveryMappedHovlShader_HasPackageFile()
    {
        var names = HovlShaderRemap.EngineShaderByGuid.Values
            .Where(n => n.StartsWith("Hovl/"))
            .Select(n => n.Substring("Hovl/".Length) + ".shader")
            .Distinct()
            .ToList();
        // 9 distinct files (Add_CenterGlowFade ships as a file but only appears via the
        // _Blend2 split, not as a direct map value); Default/Standard fallback excluded.
        Assert.Equal(9, names.Count);
        foreach (var file in names)
            Assert.True(File.Exists(Path.Combine(ShadersDir, file)), $"missing package shader file: {file}");
        Assert.True(File.Exists(Path.Combine(ShadersDir, "Add_CenterGlowFade.shader")),
            "the _Blend2 split shader must ship as a package file");
    }

    [Fact]
    public void UnknownGuid_FailsResolution()
    {
        Assert.False(HovlShaderRemap.TryResolveEngineShader("00000000000000000000000000000000", 0f, out _, out _));
    }
}
