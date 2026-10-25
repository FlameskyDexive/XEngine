using System.Text.Json.Nodes;

using XEngine.RpgVfx.Editor;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.UnityImporter.Editor;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HovlTextureMetaConverterTests
{
    [Theory]
    [InlineData(0, true, TextureMin.NearestMipmapNearest, TextureMag.Nearest)]
    [InlineData(1, true, TextureMin.LinearMipmapNearest, TextureMag.Linear)]
    [InlineData(2, true, TextureMin.LinearMipmapLinear, TextureMag.Linear)]
    [InlineData(-1, true, TextureMin.LinearMipmapNearest, TextureMag.Linear)]
    [InlineData(0, false, TextureMin.Nearest, TextureMag.Nearest)]
    [InlineData(2, false, TextureMin.Linear, TextureMag.Linear)]
    public void UnityFiltering_UsesAuthoredTexelAndMipInterpolation(int filter, bool mipmaps, TextureMin min, TextureMag mag)
    {
        string folder = CreateFolder();
        try
        {
            string source = Path.Combine(folder, "source.meta");
            File.WriteAllText(source, SourceMeta(1, mipmaps ? 1 : 0, filter, 1));
            var warnings = new List<string>();
            var settings = UnityTextureMetaAligner.AlignSettings(source, warnings)!;
            Assert.Equal((int)min, settings["minFilter"].IntValue);
            Assert.Equal((int)mag, settings["magFilter"].IntValue);
            Assert.Equal((int)TextureWrap.ClampToEdge, settings["wrapMode"].IntValue);
            Assert.Empty(warnings);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(-1, TextureWrap.Repeat)]
    [InlineData(0, TextureWrap.Repeat)]
    [InlineData(1, TextureWrap.ClampToEdge)]
    [InlineData(2, TextureWrap.MirroredRepeat)]
    public void UnityWrap_MapsSemanticEnumValues(int sourceWrap, TextureWrap expected)
    {
        string folder = CreateFolder();
        try
        {
            string source = Path.Combine(folder, "source.meta");
            File.WriteAllText(source, SourceMeta(1, 1, 1, sourceWrap));
            var warnings = new List<string>();
            var settings = UnityTextureMetaAligner.AlignSettings(source, warnings)!;
            Assert.Equal((int)expected, settings["wrapMode"].IntValue);
            Assert.Empty(warnings);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Align_OptsIntoSrgbSamplingWhilePreservingColorSpaceGuidAndUnrelatedSettings(int sourceSrgb)
    {
        string folder = CreateFolder();
        try
        {
            string source = Path.Combine(folder, "source.meta");
            string target = Path.Combine(folder, "target.meta");
            const string guid = "ec64df4d-1073-bfe3-bb94-987d5b16cf0f";
            File.WriteAllText(source, SourceMeta(sourceSrgb, 0, -1, 1));
            File.WriteAllText(target, $$$"""
                {"guid":"{{{guid}}}","importer":"TextureImporter","importerVersion":4,"customProvenance":"kept",
                 "settings":{"sprite":{"mode":1},"sRGB":false,"wrapMode":3}}
                """);
            var warnings = new List<string>();
            Assert.True(HovlTextureMetaConverter.Align(source, target, warnings));
            var meta = JsonNode.Parse(File.ReadAllText(target))!;
            Assert.Equal(guid, meta["guid"]!.GetValue<string>());
            Assert.Equal(4, meta["importerVersion"]!.GetValue<int>());
            Assert.Equal("kept", meta["customProvenance"]!.GetValue<string>());
            Assert.Equal(1, meta["settings"]!["sprite"]!["mode"]!.GetValue<int>());
            Assert.Equal(sourceSrgb != 0, meta["settings"]!["sRGB"]!.GetValue<bool>());
            Assert.True(meta["settings"]!["sRGBSampling"]!.GetValue<bool>());
            Assert.False(meta["settings"]!["generateMipmaps"]!.GetValue<bool>());
            Assert.Equal((int)TextureWrap.ClampToEdge, meta["settings"]!["wrapMode"]!.GetValue<int>());
            Assert.Equal((int)TextureAlphaSource.FromInput, meta["settings"]!["alphaSource"]!.GetValue<int>());
            Assert.Empty(warnings);
            string first = File.ReadAllText(target);
            Assert.True(HovlTextureMetaConverter.Align(source, target, warnings));
            Assert.Equal(first, File.ReadAllText(target));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("alphaSource: 0", TextureAlphaSource.None)]
    [InlineData("alphaSource: 1", TextureAlphaSource.FromInput)]
    [InlineData("alphaSource: 2", TextureAlphaSource.FromGrayScale)]
    [InlineData("alphaUsage: 2", TextureAlphaSource.FromGrayScale)]
    [InlineData("grayscaleToAlpha: 1", TextureAlphaSource.FromGrayScale)]
    public void UnityAlphaSettings_AlignToEngineAlphaSource(string alphaField, TextureAlphaSource expected)
    {
        string folder = CreateFolder();
        try
        {
            string source = Path.Combine(folder, "source.meta");
            File.WriteAllText(source, SourceMeta(1, 1, 1, 0, alphaField));
            var warnings = new List<string>();

            var settings = UnityTextureMetaAligner.AlignSettings(source, warnings)!;

            Assert.Equal((int)expected, settings["alphaSource"].IntValue);
            Assert.Empty(warnings);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void UnityAlphaSettings_CurrentFieldWinsOverLegacyFields()
    {
        string folder = CreateFolder();
        try
        {
            string source = Path.Combine(folder, "source.meta");
            File.WriteAllText(source, SourceMeta(1, 1, 1, 0, "alphaSource: 1\n  alphaUsage: 2\n  grayscaleToAlpha: 1"));
            var warnings = new List<string>();

            var settings = UnityTextureMetaAligner.AlignSettings(source, warnings)!;

            Assert.Equal((int)TextureAlphaSource.FromInput, settings["alphaSource"].IntValue);
            Assert.Empty(warnings);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Converter_CopiesAlphaSourceAlongsideSamplingSettings()
    {
        string folder = CreateFolder();
        try
        {
            string source = Path.Combine(folder, "source.meta");
            string target = Path.Combine(folder, "target.meta");
            File.WriteAllText(source, SourceMeta(1, 1, 1, 0, "grayscaleToAlpha: 1"));
            File.WriteAllText(target, "{\"guid\":\"ec64df4d-1073-bfe3-bb94-987d5b16cf0f\",\"settings\":{\"keep\":true}}\n");
            var warnings = new List<string>();

            Assert.True(HovlTextureMetaConverter.Align(source, target, warnings));

            var meta = JsonNode.Parse(File.ReadAllText(target))!;
            Assert.Equal((int)TextureAlphaSource.FromGrayScale, meta["settings"]!["alphaSource"]!.GetValue<int>());
            Assert.True(meta["settings"]!["keep"]!.GetValue<bool>());
            Assert.Empty(warnings);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string SourceMeta(int srgb, int mipmaps, int filter, int wrap, string? alphaField = null) => $$"""
        fileFormatVersion: 2
        guid: 7605196420d204945843c4c794c58361
        TextureImporter:
          {{alphaField}}
          mipmaps:
            enableMipMap: {{mipmaps}}
            sRGBTexture: {{srgb}}
          textureSettings:
            filterMode: {{filter}}
            wrapU: {{wrap}}
            wrapV: {{wrap}}
        """;

    private static string CreateFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "xengine-hovl-texture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
