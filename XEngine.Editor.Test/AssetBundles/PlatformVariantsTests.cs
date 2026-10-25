// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Threading.Tasks;
using System;
using System.IO;
using System.Text;

using XEngine.Editor.AssetBundles.PlatformVariants;
using XEngine.Editor.Build;
using XEngine.Editor.Build.Android;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Xunit;

// Local alias to avoid repeating the XEngine.Runtime.AssetBundles.AssetBundlePlatform fully-qualified name.
using AssetBundlePlatform = XEngine.Runtime.AssetBundles.AssetBundlePlatform;

namespace XEngine.Editor.Test.AssetBundles;

/// <summary>
/// Tests for the Stage F platform-variant layer: BuildTarget.iOS registration, the iOS player
/// export (generate sources, fail clearly off-Mac), the BuildTarget -> AssetBundlePlatform mapping
/// the builder uses, and the TexturePlatformSettings override resolution.
/// </summary>
public class PlatformVariantsTests : EditorTestHarness
{
    public PlatformVariantsTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
    }

    [Fact]
    public async Task IosBuildPipeline_BuildAsync_GeneratesPlayerAndFailsOffMac()
    {
        string output = Path.Combine(Path.GetTempPath(), "xengine-ios-p1-" + Guid.NewGuid().ToString("N"));
        try
        {
            var compilation = ScriptCompiler.CompileAll(Project);
            Assert.True(compilation.Success, compilation.Output);
            Guid sceneGuid = AuthorEmptyScene();
            var settings = new BuildSettings();
            settings.Scenes.Add(new SceneBuildEntry { Path = "Main.scene", SceneGuid = sceneGuid, Enabled = true });
            var pipeline = new IosBuildPipeline();
            var (result, diagnostics) = await IosPipelineTestLog.CaptureAsync(() => pipeline.BuildAsync(Project.RootPath, settings, output));
            Assert.False(string.IsNullOrWhiteSpace(result.OutputPath), result.Errors + Environment.NewLine + diagnostics);
            Assert.True(Directory.Exists(result.OutputPath));
            string[] projects = Directory.GetFiles(result.OutputPath, "*.csproj", SearchOption.AllDirectories);
            Assert.Single(projects);
            string projectText = await File.ReadAllTextAsync(projects[0]);
            Assert.Contains("net10.0-ios", projectText, StringComparison.Ordinal);
            Assert.Contains("ios-arm64", projectText, StringComparison.Ordinal);
            Assert.Contains("XENGINE_IOS", projectText, StringComparison.Ordinal);
            Assert.DoesNotContain("not yet implemented", result.Errors, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(result.OutputPath, "verification", "ios-signing.json")));
            Assert.True(File.Exists(Path.Combine(result.OutputPath, "verification", "ios-dsym.json")));

            if (OperatingSystem.IsMacOS())
            {
                if (!result.Success)
                {
                    Assert.True(
                        result.Errors.Contains("ProvisioningProfilePath", StringComparison.Ordinal) ||
                        result.Errors.Contains("Team ID", StringComparison.Ordinal) ||
                        result.Errors.Contains("publish", StringComparison.OrdinalIgnoreCase) ||
                        result.Errors.Contains("Xcode", StringComparison.OrdinalIgnoreCase),
                        result.Errors);
                }
            }
            else
            {
                Assert.False(result.Success);
                Assert.Contains("macOS", result.Errors, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Generated", result.Errors, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("no desktop executable", result.Errors, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("adb", result.Errors, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void IosBuildPipeline_HasStableId_AndMobileIcon()
    {
        var pipeline = new IosBuildPipeline();
        Assert.Equal("xengine.ios", pipeline.Id);
        Assert.Equal("iOS", pipeline.DisplayName);
        Assert.NotEmpty(pipeline.SupportedRuntimeIdentifiers);
    }

    [Theory]
    [InlineData(BuildTarget.Windows, AssetBundlePlatform.Windows)]
    [InlineData(BuildTarget.Linux, AssetBundlePlatform.Linux)]
    [InlineData(BuildTarget.MacOS, AssetBundlePlatform.MacOS)]
    [InlineData(BuildTarget.Android, AssetBundlePlatform.Android)]
    [InlineData(BuildTarget.iOS, AssetBundlePlatform.iOS)]
    [InlineData(BuildTarget.HarmonyOS, AssetBundlePlatform.HarmonyOS)]
    [InlineData(BuildTarget.Web, AssetBundlePlatform.Web)]
    public void PlatformMapping_ToAssetBundlePlatform_CoversAllTargets(BuildTarget target, AssetBundlePlatform expected)
    {
        Assert.Equal(expected, PlatformVariantMapping.ToAssetBundlePlatform(target));
    }

    [Fact]
    public void TexturePlatformOverrides_ResolveReturnsActiveOverride()
    {
        var overrides = new TexturePlatformOverrides();
        overrides.Overrides[BuildTarget.Android] = new TexturePlatformSettings
        {
            Platform = BuildTarget.Android, Override = true, MaxTextureSize = 1024, Format = TextureVariantFormat.ASTC,
        };
        overrides.Overrides[BuildTarget.iOS] = new TexturePlatformSettings
        {
            Platform = BuildTarget.iOS, Override = false, // inactive -> falls back to default
        };

        var android = overrides.Resolve(BuildTarget.Android);
        Assert.NotNull(android);
        Assert.Equal(1024, android!.MaxTextureSize);

        // iOS override is inactive -> null (caller uses default).
        Assert.Null(overrides.Resolve(BuildTarget.iOS));
        // Windows has no override -> null.
        Assert.Null(overrides.Resolve(BuildTarget.Windows));
    }

    [Fact]
    public void AndroidTextureVariantResolver_AutomaticUsesEtc2Baseline()
    {
        var result = AndroidTextureVariantResolver.Resolve(
            new TexturePlatformSettings { Platform = BuildTarget.Android, Override = true },
            AndroidTextureCapabilities.Baseline);

        Assert.Equal(TextureVariantFormat.Automatic, result.RequestedFormat);
        Assert.Equal(TextureVariantFormat.ETC2_RGBA, result.EffectiveFormat);
        Assert.False(result.IsFallback);
        Assert.Equal("automatic:etc2-rgba-baseline", result.Reason);
    }

    [Fact]
    public void AndroidTextureVariantResolver_AstcFallsBackDeterministically()
    {
        var result = AndroidTextureVariantResolver.Resolve(
            new TexturePlatformSettings
            {
                Platform = BuildTarget.Android,
                Override = true,
                Format = TextureVariantFormat.ASTC,
                MaxTextureSize = 1024,
                CompressionQuality = 80,
            },
            AndroidTextureCapabilities.Baseline);

        Assert.Equal(TextureVariantFormat.ASTC, result.RequestedFormat);
        Assert.Equal(TextureVariantFormat.ETC2_RGBA, result.EffectiveFormat);
        Assert.True(result.IsFallback);
        Assert.Equal(1024, result.MaxTextureSize);
        Assert.Equal(80, result.CompressionQuality);
        Assert.Equal("requested:unsupported:fallback:etc2-rgba", result.Reason);
    }

    [Fact]
    public void AndroidTextureVariantResolver_RejectsInvalidQuality()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AndroidTextureVariantResolver.Resolve(
            new TexturePlatformSettings
            {
                Platform = BuildTarget.Android,
                Override = true,
                CompressionQuality = 101,
            },
            AndroidTextureCapabilities.Baseline));
    }

    [Fact]
    public void AndroidTextureVariantResolver_WithoutEncoderFallsBackToPayloadFormat()
    {
        var result = AndroidTextureVariantResolver.ResolveForBuild(
            new TexturePlatformSettings
            {
                Platform = BuildTarget.Android,
                Override = true,
                Format = TextureVariantFormat.ETC2_RGBA,
            },
            AndroidTextureCapabilities.Baseline,
            encoderAvailable: false);

        Assert.Equal(TextureVariantFormat.ETC2_RGBA, result.RequestedFormat);
        Assert.Equal(TextureVariantFormat.RGBA32, result.EffectiveFormat);
        Assert.True(result.IsFallback);
        Assert.Equal("requested:supported:encoder-unavailable:fallback:rgba32", result.Reason);
    }

    [Theory]
    [InlineData(TextureVariantFormat.ETC2_RGB, true)]
    [InlineData(TextureVariantFormat.ETC2_RGBA, true)]
    [InlineData(TextureVariantFormat.ASTC, true)]
    [InlineData(TextureVariantFormat.RGBA32, false)]
    public void AndroidTextureVariantResolver_IdentifiesEncodedFormats(TextureVariantFormat format, bool expected)
    {
        Assert.Equal(expected, AndroidTextureVariantResolver.IsCompressedFormat(format));
    }

    [Theory]
    [InlineData(false, TextureImageFormat.Astc4x4Srgb)]
    [InlineData(true, TextureImageFormat.Astc4x4)]
    public void AndroidTexturePayloadEncoder_EmitsAstcPayloadAndMipChain_WhenToolIsInstalled(
        bool normalMap,
        TextureImageFormat expectedFormat)
    {
        AndroidTextureEncoderDescriptor? encoder = AndroidTexturePayloadEncoder.DiscoverEncoder();
        if (encoder == null)
            return; // Build hosts without the optional encoder must keep the documented RGBA fallback.

        var source = EchoObject.NewCompound();
        source["Width"] = new EchoObject(8u);
        source["Height"] = new EchoObject(8u);
        source["IsMipMapped"] = new EchoObject(true);
        source["ImageFormat"] = new EchoObject((int)TextureImageFormat.Color4b);
        source["MinFilter"] = new EchoObject(0);
        source["MagFilter"] = new EchoObject(0);
        source["Wrap"] = new EchoObject(0);
        var pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i / 4);
            pixels[i + 1] = 128;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }
        source["Data"] = new EchoObject(pixels);
        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                source.WriteToBinary(writer);
            bytes = stream.ToArray();
        }

        (byte[] payload, int width, int height, int mipCount) = AndroidTexturePayloadEncoder.EncodeAstcPayloadForTest(
            bytes,
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            generateMipmaps: true,
            sRgb: true,
            normalMap,
            maxTextureSize: 4,
            quality: 50,
            encoder);

        Assert.Equal(4, width);
        Assert.Equal(4, height);
        Assert.Equal(3, mipCount);
        using var readStream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(readStream, Encoding.UTF8, leaveOpen: true);
        EchoObject rewritten = EchoObject.ReadFromBinary(reader);
        Assert.Equal((int)expectedFormat, rewritten["ImageFormat"].IntValue);
        Assert.Equal(mipCount, rewritten["MipCount"].IntValue);
        Assert.Equal(
            Texture.GetDataSize(expectedFormat, 4, 4) +
            Texture.GetDataSize(expectedFormat, 2, 2) +
            Texture.GetDataSize(expectedFormat, 1, 1),
            rewritten["Data"].ByteArrayValue.Length);
    }

    [Theory]
    [InlineData(TextureVariantFormat.ETC2_RGB, false, TextureImageFormat.Etc2Rgb8Srgb)]
    [InlineData(TextureVariantFormat.ETC2_RGB, true, TextureImageFormat.Etc2Rgb8)]
    [InlineData(TextureVariantFormat.ETC2_RGBA, false, TextureImageFormat.Etc2Rgba8Srgb)]
    [InlineData(TextureVariantFormat.ETC2_RGBA, true, TextureImageFormat.Etc2Rgba8)]
    public void AndroidTexturePayloadEncoder_EmitsEtc2PayloadAndMipChain_WhenToolIsInstalled(
        TextureVariantFormat variantFormat,
        bool normalMap,
        TextureImageFormat expectedFormat)
    {
        AndroidTextureEncoderDescriptor? encoder = AndroidTexturePayloadEncoder.DiscoverEtc2Encoder();
        if (encoder == null)
            return; // Portable hosts retain the documented uncompressed fallback.

        var source = EchoObject.NewCompound();
        source["Width"] = new EchoObject(8u);
        source["Height"] = new EchoObject(8u);
        source["IsMipMapped"] = new EchoObject(true);
        source["ImageFormat"] = new EchoObject((int)TextureImageFormat.Color4b);
        var pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i / 4);
            pixels[i + 1] = 127;
            pixels[i + 2] = 255;
            pixels[i + 3] = (byte)(variantFormat == TextureVariantFormat.ETC2_RGB ? 255 : i / 4);
        }
        source["Data"] = new EchoObject(pixels);
        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                source.WriteToBinary(writer);
            bytes = stream.ToArray();
        }

        (byte[] payload, int width, int height, int mipCount) = AndroidTexturePayloadEncoder.EncodeEtc2PayloadForTest(
            bytes,
            Guid.Parse("11111111-2222-3333-4444-666666666666"),
            generateMipmaps: true,
            sRgb: true,
            normalMap,
            targetFormat: variantFormat,
            maxTextureSize: 4,
            quality: 50,
            encoder);

        Assert.Equal(4, width);
        Assert.Equal(4, height);
        Assert.Equal(3, mipCount);
        using var readStream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(readStream, Encoding.UTF8, leaveOpen: true);
        EchoObject rewritten = EchoObject.ReadFromBinary(reader);
        Assert.Equal((int)expectedFormat, rewritten["ImageFormat"].IntValue);
        Assert.Equal(mipCount, rewritten["MipCount"].IntValue);
        Assert.Equal(
            Texture.GetDataSize(expectedFormat, 4, 4) +
            Texture.GetDataSize(expectedFormat, 2, 2) +
            Texture.GetDataSize(expectedFormat, 1, 1),
            rewritten["Data"].ByteArrayValue.Length);
    }

    [Fact]
    public void BuildTarget_AndAssetBundlePlatform_KeepStableAppendOnlyValues()
    {
        Assert.Equal(0, (int)BuildTarget.Windows);
        Assert.Equal(1, (int)BuildTarget.Linux);
        Assert.Equal(2, (int)BuildTarget.MacOS);
        Assert.Equal(3, (int)BuildTarget.Android);
        Assert.Equal(4, (int)BuildTarget.iOS);
        Assert.Equal(5, (int)BuildTarget.HarmonyOS);
        Assert.Equal(6, (int)BuildTarget.Web);

        Assert.Equal(0, (int)AssetBundlePlatform.Any);
        Assert.Equal(1, (int)AssetBundlePlatform.Windows);
        Assert.Equal(2, (int)AssetBundlePlatform.Linux);
        Assert.Equal(3, (int)AssetBundlePlatform.MacOS);
        Assert.Equal(4, (int)AssetBundlePlatform.Android);
        Assert.Equal(5, (int)AssetBundlePlatform.iOS);
        Assert.Equal(6, (int)AssetBundlePlatform.HarmonyOS);
        Assert.Equal(7, (int)AssetBundlePlatform.Web);
    }

    [Fact]
    public void IosTextureVariantResolver_AutomaticSelectsAstcWithoutRgbaFallback()
    {
        var result = IosTextureVariantResolver.Resolve(
            new TexturePlatformSettings { Platform = BuildTarget.iOS, Override = true },
            IosTextureCapabilities.AppleGpu);

        Assert.Equal(TextureVariantFormat.Automatic, result.RequestedFormat);
        Assert.Equal(TextureVariantFormat.ASTC, result.EffectiveFormat);
        Assert.False(result.IsFallback);
        Assert.Equal("automatic:astc-4x4-baseline", result.Reason);
    }

    [Fact]
    public void IosTextureVariantResolver_MissingEncoderFailsClosed()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            IosTextureVariantResolver.ResolveForBuild(
                new TexturePlatformSettings
                {
                    Platform = BuildTarget.iOS,
                    Override = true,
                    Format = TextureVariantFormat.ASTC,
                },
                IosTextureCapabilities.AppleGpu,
                encoderAvailable: false));

        Assert.Equal(IosTextureVariantResolver.AstcEncoderMissingError, ex.Message);
    }

    [Fact]
    public void IosTextureVariantResolver_UnsupportedGpuFailsClosed()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            IosTextureVariantResolver.Resolve(
                new TexturePlatformSettings { Platform = BuildTarget.iOS, Override = true, Format = TextureVariantFormat.ASTC },
                new IosTextureCapabilities(false)));
        Assert.Equal(IosTextureVariantResolver.DeviceAstcUnsupportedError, ex.Message);
    }
}
