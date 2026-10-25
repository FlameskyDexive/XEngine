// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ P1-7 — Proves <see cref="AssetWriter"/>'s import/move/delete + idempotency safety.</summary>
public class AssetWriterTests : IClassFixture<TestSceneFixture>
{
    private readonly TestSceneFixture _fx;
    public AssetWriterTests(TestSceneFixture fx) => _fx = fx;

    [Fact]
    public async Task Import_Png_MintsMetaAsTextureImporter()
    {
        string sourceDir = Path.Combine(_fx.ProjectRoot, "source");
        Directory.CreateDirectory(sourceDir);
        string sourcePng = Path.Combine(sourceDir, "grass.png");
        File.WriteAllText(sourcePng, "fake png");

        var result = await _fx.AssetWriter.ImportAsync(sourcePng,
            Path.Combine(_fx.ProjectRoot, "Assets/Textures/Grass.png"),
            "Assets/Textures/Grass.png",
            TestSceneFixture.Provenance("test.import", "imp-grass-1"), overwrite: false);

        Assert.Equal("TextureImporter", result.ImporterType);
        Assert.Equal(3, result.ImporterVersion);
        Assert.False(result.Overwritten);
        Assert.False(string.IsNullOrEmpty(result.Guid));
        Assert.True(File.Exists(Path.Combine(_fx.ProjectRoot, "Assets/Textures/Grass.png.meta")));
    }

    [Fact]
    public async Task Import_Overwrite_PreservesGuid()
    {
        string sourceDir = Path.Combine(_fx.ProjectRoot, "source2");
        Directory.CreateDirectory(sourceDir);
        string srcA = Path.Combine(sourceDir, "a.png");
        string srcB = Path.Combine(sourceDir, "b.png");
        File.WriteAllText(srcA, "A");
        File.WriteAllText(srcB, "B");
        string dest = Path.Combine(_fx.ProjectRoot, "Assets/over.png");

        var first = await _fx.AssetWriter.ImportAsync(srcA, dest, "Assets/over.png",
            TestSceneFixture.Provenance("test.import", "imp-over-1a"), overwrite: false);
        var second = await _fx.AssetWriter.ImportAsync(srcB, dest, "Assets/over.png",
            TestSceneFixture.Provenance("test.import", "imp-over-1b"), overwrite: true);

        Assert.True(second.Overwritten);
        Assert.Equal(first.Guid, second.Guid);  // overwrite preserves the GUID
    }

    [Fact]
    public async Task Import_WithoutOverwrite_RejectsDuplicate()
    {
        string sourceDir = Path.Combine(_fx.ProjectRoot, "source3");
        Directory.CreateDirectory(sourceDir);
        string src = Path.Combine(sourceDir, "x.png");
        File.WriteAllText(src, "x");
        string dest = Path.Combine(_fx.ProjectRoot, "Assets/dup.png");

        await _fx.AssetWriter.ImportAsync(src, dest, "Assets/dup.png",
            TestSceneFixture.Provenance("test.import", "imp-dup-1"), overwrite: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fx.AssetWriter.ImportAsync(src, dest, "Assets/dup.png",
                TestSceneFixture.Provenance("test.import", "imp-dup-2"), overwrite: false));
    }

    [Fact]
    public async Task Import_IdempotentReplay_DoesNotClobberFile()
    {
        string sourceDir = Path.Combine(_fx.ProjectRoot, "source4");
        Directory.CreateDirectory(sourceDir);
        string srcWav = Path.Combine(sourceDir, "beep.wav");
        File.WriteAllText(srcWav, "wav-data");
        string srcMalicious = Path.Combine(sourceDir, "evil.png");
        File.WriteAllText(srcMalicious, "png-data");
        string dest = Path.Combine(_fx.ProjectRoot, "Assets/sound.wav");

        var prov = TestSceneFixture.Provenance("test.import", "imp-idem-1");
        await _fx.AssetWriter.ImportAsync(srcWav, dest, "Assets/sound.wav", prov, overwrite: false);

        // Replay with a DIFFERENT (malicious) source but the same idempotency key — must not clobber.
        await _fx.AssetWriter.ImportAsync(srcMalicious, dest, "Assets/sound.wav", prov, overwrite: true);

        Assert.Equal("wav-data", File.ReadAllText(dest));  // original content preserved
    }

    [Fact]
    public async Task Delete_RemovesFileAndMeta()
    {
        string sourceDir = Path.Combine(_fx.ProjectRoot, "source5");
        Directory.CreateDirectory(sourceDir);
        string src = Path.Combine(sourceDir, "del.png");
        File.WriteAllText(src, "del");
        string dest = Path.Combine(_fx.ProjectRoot, "Assets/del.png");

        await _fx.AssetWriter.ImportAsync(src, dest, "Assets/del.png",
            TestSceneFixture.Provenance("test.import", "imp-del-1"), overwrite: false);
        Assert.True(File.Exists(dest + ".meta"));

        await _fx.AssetWriter.DeleteAsync(dest, "Assets/del.png",
            TestSceneFixture.Provenance("test.delete", "del-1"));

        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".meta"));
    }

    [Fact]
    public async Task Import_Glb_WritesAnimationTypeSetting()
    {
        string sourceDir = Path.Combine(_fx.ProjectRoot, "source-glb");
        Directory.CreateDirectory(sourceDir);
        string src = Path.Combine(sourceDir, "hero.glb");
        File.WriteAllText(src, "fake glb");
        string dest = Path.Combine(_fx.ProjectRoot, "Assets/hero.glb");

        var result = await _fx.AssetWriter.ImportAsync(src, dest, "Assets/hero.glb",
            TestSceneFixture.Provenance("test.import", "imp-glb-1"), overwrite: false, animationType: 2);

        Assert.Equal("EditorModelImporter", result.ImporterType);
        Assert.Equal(6, result.ImporterVersion);
        string meta = File.ReadAllText(dest + ".meta");
        Assert.Contains("animationType", meta);
        Assert.Contains("2", meta);
    }

    [Fact]
    public void WriteAnimatorController_MintsMeta()
    {
        string path = Path.Combine(_fx.ProjectRoot, "Assets", "NewAnim.controller");
        string guid = AssetWriter.WriteAnimatorController(path, "NewAnim");
        Assert.False(string.IsNullOrEmpty(guid));
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".meta"));
        Assert.Contains("AnimatorController", File.ReadAllText(path));
    }
}
