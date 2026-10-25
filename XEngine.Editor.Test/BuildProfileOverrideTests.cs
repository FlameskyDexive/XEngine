// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using System.Text;

using XEngine.Echo;
using XEngine.Editor.AssetBundles.PlatformVariants;
using XEngine.Editor.Build;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// BP3: per-profile Player Settings overrides and asset import downgrades. The property that matters
/// most here is that a build never leaves the editor's own settings changed.
/// </summary>
public class BuildProfileOverrideTests : EditorTestHarness
{
    public BuildProfileOverrideTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
    }

    private static BuildProfile ProfileWithOverride(string settingsName, string field, string value)
    {
        var profile = new BuildProfile();
        profile.SettingOverrides.Add(new ProfileSettingOverride
        {
            SettingsName = settingsName,
            FieldPath = field,
            Value = value,
        });
        return profile;
    }

    [Fact]
    public void PipelineAssetOverrideIsScopedToTheBuild()
    {
        var graphics = EditorRegistries.GetSettings<GraphicsSettings>();
        var original = graphics.DefaultPipeline;
        Guid mobilePipeline = Guid.NewGuid();
        using (var scope = ProfileSettingsOverrideScope.Apply(
            ProfileWithOverride("Graphics", "DefaultPipeline", mobilePipeline.ToString())))
        {
            Assert.Equal(1, scope.AppliedCount);
            Assert.Empty(scope.Warnings);
            Assert.Equal(mobilePipeline, graphics.DefaultPipeline.AssetID);
        }
        Assert.Equal(original.AssetID, graphics.DefaultPipeline.AssetID);
    }

    [Fact]
    public void FullQualityPipelineOverrideIsScopedToTheBuild()
    {
        var quality = EditorRegistries.GetSettings<QualitySettingsPanel>();
        var original = quality.FullPipeline;
        Guid mobilePipeline = Guid.NewGuid();
        using (var scope = ProfileSettingsOverrideScope.Apply(
            ProfileWithOverride("Quality", "FullPipeline", mobilePipeline.ToString())))
        {
            Assert.Equal(1, scope.AppliedCount);
            Assert.Empty(scope.Warnings);
            Assert.Equal(mobilePipeline, quality.FullPipeline.AssetID);
        }
        Assert.Equal(original.AssetID, quality.FullPipeline.AssetID);
    }

    [Fact]
    public void Overrides_AreVisibleInsideTheScopeAndGoneAfterIt()
    {
        var general = EditorRegistries.GetSettings<GeneralSettings>();
        general.ProductName = "Authored";

        using (var scope = ProfileSettingsOverrideScope.Apply(
            ProfileWithOverride("General", "ProductName", "Store Build")))
        {
            Assert.Equal(1, scope.AppliedCount);
            Assert.Empty(scope.Warnings);
            Assert.Equal("Store Build", general.ProductName);
        }

        Assert.Equal("Authored", general.ProductName);
    }

    [Fact]
    public void Overrides_AreRestoredEvenWhenTheBuildThrows()
    {
        var general = EditorRegistries.GetSettings<GeneralSettings>();
        general.ProductName = "Authored";

        Action build = () =>
        {
            using var scope = ProfileSettingsOverrideScope.Apply(
                ProfileWithOverride("General", "ProductName", "Store Build"));
            throw new InvalidOperationException("build blew up");
        };

        Assert.Throws<InvalidOperationException>(build);

        Assert.Equal("Authored", general.ProductName);
    }

    [Theory]
    [InlineData("ProductName", "Renamed", "Renamed")]
    public void Overrides_DecodeStringValues(string field, string value, string expected)
    {
        var general = EditorRegistries.GetSettings<GeneralSettings>();
        using var scope = ProfileSettingsOverrideScope.Apply(ProfileWithOverride("General", field, value));

        Assert.Equal(1, scope.AppliedCount);
        Assert.Equal(expected, general.ProductName);
    }

    [Fact]
    public void Overrides_ReportRatherThanGuessWhenTheTargetIsWrong()
    {
        using var unknownGroup = ProfileSettingsOverrideScope.Apply(
            ProfileWithOverride("NoSuchGroup", "ProductName", "x"));
        Assert.Equal(0, unknownGroup.AppliedCount);
        Assert.Contains("NoSuchGroup", Assert.Single(unknownGroup.Warnings), StringComparison.Ordinal);

        using var unknownField = ProfileSettingsOverrideScope.Apply(
            ProfileWithOverride("General", "NoSuchField", "x"));
        Assert.Equal(0, unknownField.AppliedCount);
        Assert.Contains("NoSuchField", Assert.Single(unknownField.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Overrides_SkipDisabledEntries()
    {
        var general = EditorRegistries.GetSettings<GeneralSettings>();
        general.ProductName = "Authored";

        BuildProfile profile = ProfileWithOverride("General", "ProductName", "Ignored");
        profile.SettingOverrides[0].Enabled = false;

        using var scope = ProfileSettingsOverrideScope.Apply(profile);

        Assert.Equal(0, scope.AppliedCount);
        Assert.Equal("Authored", general.ProductName);
    }

    [Fact]
    public void Overrides_OnANullProfileAreInert()
    {
        using var scope = ProfileSettingsOverrideScope.Apply(null);

        Assert.Equal(0, scope.AppliedCount);
        Assert.Empty(scope.Warnings);
    }

    [Fact]
    public void AssetImportOverrides_LowerThePlatformTextureBudgetButNeverRaiseIt()
    {
        var platform = new TexturePlatformSettings { MaxTextureSize = 2048, Format = TextureVariantFormat.ASTC };

        var lowering = new ProfileAssetImportOverrides { Enabled = true, MaxTextureSize = 512 };
        Assert.Equal(512, lowering.ApplyTo(platform).MaxTextureSize);

        // The project deliberately budgeted 2048; a profile asking for 4096 must not raise it.
        var raising = new ProfileAssetImportOverrides { Enabled = true, MaxTextureSize = 4096 };
        Assert.Equal(2048, raising.ApplyTo(platform).MaxTextureSize);
    }

    [Fact]
    public void AssetImportOverrides_ForceUncompressedReplacesTheRequestedFormat()
    {
        var platform = new TexturePlatformSettings { MaxTextureSize = 2048, Format = TextureVariantFormat.ASTC };
        var overrides = new ProfileAssetImportOverrides { Enabled = true, ForceUncompressed = true };

        TexturePlatformSettings result = overrides.ApplyTo(platform);

        Assert.Equal(TextureVariantFormat.RGBA32, result.Format);
        Assert.Equal(2048, result.MaxTextureSize);
    }

    [Fact]
    public void AssetImportOverrides_LeaveThePlatformSettingsObjectUntouched()
    {
        var platform = new TexturePlatformSettings { MaxTextureSize = 2048, Format = TextureVariantFormat.ASTC };
        var overrides = new ProfileAssetImportOverrides { Enabled = true, MaxTextureSize = 256, ForceUncompressed = true };

        overrides.ApplyTo(platform);

        Assert.Equal(2048, platform.MaxTextureSize);
        Assert.Equal(TextureVariantFormat.ASTC, platform.Format);
    }

    [Fact]
    public void AssetImportOverrides_AreAPassthroughWhenDisabled()
    {
        var platform = new TexturePlatformSettings { MaxTextureSize = 2048, Format = TextureVariantFormat.ASTC };
        var overrides = new ProfileAssetImportOverrides { MaxTextureSize = 256, ForceUncompressed = true };

        Assert.Same(platform, overrides.ApplyTo(platform));
    }

    [Fact]
    public void TextureDowngrader_ResizesColor4bPayloadsAndLeavesSmallerOnesAlone()
    {
        byte[] source = MakeRgbaEcho(8, 8);
        var clamp = new TexturePlatformSettings { MaxTextureSize = 4 };

        Assert.True(BuildTexturePayloadDowngrader.TryRewrite(source, clamp, out byte[] rewritten));

        EchoObject echo = ReadEcho(rewritten);
        Assert.Equal(4u, echo["Width"].UIntValue);
        Assert.Equal(4u, echo["Height"].UIntValue);
        Assert.Equal(4 * 4 * 4, echo["Data"].ByteArrayValue.Length);

        Assert.False(BuildTexturePayloadDowngrader.TryRewrite(rewritten, clamp, out _));
    }

    [Fact]
    public void TextureDowngrader_DoesNotRewriteWhenMaxSizeIsUnlimited()
    {
        byte[] source = MakeRgbaEcho(8, 8);
        Assert.False(BuildTexturePayloadDowngrader.TryRewrite(
            source, new TexturePlatformSettings { MaxTextureSize = 0 }, out _));
    }

    [Fact]
    public void ResolveSettings_FoldsTheActiveProfilesImportOverrides()
    {
        var settings = EditorRegistries.GetSettings<BuildSettings>();
        var baseline = new TexturePlatformSettings
        {
            Platform = BuildTarget.Windows,
            Override = true,
            MaxTextureSize = 2048,
            Format = TextureVariantFormat.BC7,
        };

        BuildProfileHandle handle = BuildProfileRegistry.Create("Windows QA", "xengine.desktop")!.Value;
        handle.Profile.AssetImportOverrides.Enabled = true;
        handle.Profile.AssetImportOverrides.MaxTextureSize = 512;
        handle.Profile.AssetImportOverrides.ForceUncompressed = true;
        BuildProfileRegistry.Save(handle);
        BuildProfileRegistry.SetActive(settings, handle.Guid, requestRecompile: false);

        TexturePlatformSettings resolved = BuildTexturePayloadDowngrader.ResolveSettings(
            settings, BuildTarget.Windows, baseline);

        Assert.Equal(512, resolved.MaxTextureSize);
        Assert.Equal(TextureVariantFormat.RGBA32, resolved.Format);
        Assert.Equal(2048, baseline.MaxTextureSize);
        Assert.Equal(TextureVariantFormat.BC7, baseline.Format);
    }

    private static byte[] MakeRgbaEcho(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 3] = 255;
        }

        EchoObject echo = EchoObject.NewCompound();
        echo["Width"] = new EchoObject((uint)width);
        echo["Height"] = new EchoObject((uint)height);
        echo["ImageFormat"] = new EchoObject((int)TextureImageFormat.Color4b);
        echo["IsMipMapped"] = new EchoObject(false);
        echo["MinFilter"] = new EchoObject(0);
        echo["MagFilter"] = new EchoObject(0);
        echo["Wrap"] = new EchoObject(0);
        echo["Data"] = new EchoObject(pixels);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            echo.WriteToBinary(writer);
        return stream.ToArray();
    }

    private static EchoObject ReadEcho(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return EchoObject.ReadFromBinary(reader);
    }
}
