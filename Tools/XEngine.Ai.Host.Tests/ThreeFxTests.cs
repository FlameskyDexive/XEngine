using System;
using System.Collections.Generic;
using System.IO;
using XEngine.Ai.Protocol;
using XEngine.Ai.ThreeFx;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// A2 — .threefx recipe parsing, validation, cache identity semantics and staged-export
/// guarantees. The identity rules encode the acceptance gate: a change to source content, any
/// declared dependency's content, seed, export size, time or tool version invalidates the cache;
/// formatting-only differences and file location do not.
/// </summary>
public sealed class ThreeFxTests
{
    private const string ValidRecipe = """
        {
          "schemaVersion": 1,
          "stableId": "smoke_knot",
          "recipe": {
            "mesh": "torusKnot",
            "hue": 0.25D,
            "metalness": 0.85D,
            "roughness": 0.25D
          },
          "dependencies": [],
          "seed": 0,
          "exportWidth": 512,
          "exportHeight": 512
        }
        """;

    private static string Identity(
        string recipeText = ValidRecipe,
        int seed = 0,
        int width = 512,
        int height = 512,
        double time = 0,
        Dictionary<string, string>? deps = null,
        string three = "r180",
        string exporter = "xengine-three-exporter/0.2",
        string host = "1.0.0")
    {
        return ThreeFxCacheIdentity.Compute(
            ThreeFxDocument.Normalize(recipeText),
            deps ?? new Dictionary<string, string>(),
            seed, width, height,
            three, exporter, host, time);
    }

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void Parse_ValidRecipe_ProducesTypedDocument()
    {
        bool ok = ThreeFxDocument.TryParse(ValidRecipe, out ThreeFxRecipe? recipe, out List<ThreeAuthoringDiagnostic> issues);
        Assert.True(ok);
        Assert.Empty(issues);
        Assert.NotNull(recipe);
        Assert.Equal("smoke_knot", recipe!.StableId);
        Assert.Equal("torusKnot", recipe.Recipe.Mesh);
        Assert.Equal(0.25, recipe.Recipe.Hue, 6);
        Assert.Equal(0.85, recipe.Recipe.Metalness, 6);
        Assert.Equal(0, recipe.Seed);
        Assert.Equal(512, recipe.ExportWidth);
    }

    [Fact]
    public void Parse_MalformedText_FailsWithDiagnostic()
    {
        bool ok = ThreeFxDocument.TryParse("{ not valid !!!", out ThreeFxRecipe? recipe, out List<ThreeAuthoringDiagnostic> issues);
        Assert.False(ok);
        Assert.Null(recipe);
        Assert.Contains(issues, d => d.Code == "parse_failed");
    }

    // ----------------------------------------------------------------- validate

    [Fact]
    public void Validate_RejectsBadStableId()
    {
        bool ok = ThreeFxDocument.TryParse(ValidRecipe.Replace("smoke_knot", "Bad-Id!"), out ThreeFxRecipe? recipe, out _);
        Assert.True(ok);
        Assert.Contains(ThreeFxValidator.Validate(recipe!), d => d.Code == "stable_id");
    }

    [Fact]
    public void Validate_RejectsUnknownMesh()
    {
        bool ok = ThreeFxDocument.TryParse(ValidRecipe.Replace("torusKnot", "dragon"), out ThreeFxRecipe? recipe, out _);
        Assert.True(ok);
        Assert.Contains(ThreeFxValidator.Validate(recipe!), d => d.Code == "mesh");
    }

    [Fact]
    public void Validate_RejectsOutOfRangeHue()
    {
        bool ok = ThreeFxDocument.TryParse(ValidRecipe.Replace("0.25D", "1.5D"), out ThreeFxRecipe? recipe, out _);
        Assert.True(ok);
        Assert.Contains(ThreeFxValidator.Validate(recipe!), d => d.Code == "recipe.hue");
    }

    [Fact]
    public void Validate_RejectsParentTraversalDependency()
    {
        bool ok = ThreeFxDocument.TryParse(
            ValidRecipe.Replace("\"dependencies\": [],", "\"dependencies\": [\"../secrets.bin\"],"),
            out ThreeFxRecipe? recipe, out _);
        Assert.True(ok);
        Assert.Contains(ThreeFxValidator.Validate(recipe!), d => d.Code == "dependency_path");
    }

    [Fact]
    public void ValidateDependencies_ReportsMissingFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "threefx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            bool ok = ThreeFxDocument.TryParse(
                ValidRecipe.Replace("\"dependencies\": [],", "\"dependencies\": [\"Tex/missing.png\"],"),
                out ThreeFxRecipe? recipe, out _);
            Assert.True(ok);

            var diagnostics = new List<ThreeAuthoringDiagnostic>();
            ThreeFxValidator.ValidateDependencies(recipe!, root, diagnostics);
            Assert.Contains(diagnostics, d => d.Code == "dependency_missing");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ----------------------------------------------------------- cache identity

    [Fact]
    public void Identity_SameInputs_AreEqual()
    {
        Assert.Equal(Identity(), Identity());
    }

    [Fact]
    public void Identity_FormattingOnlyDifferences_AreEqual()
    {
        string reformatted = """
            {
              "stableId":  "smoke_knot",
              "seed": 0,
              "recipe": { "roughness": 0.25D, "hue": 0.25D, "metalness": 0.85D, "mesh": "torusKnot" },
              "schemaVersion": 1,
              "exportWidth": 512,
              "exportHeight": 512,
              "dependencies": []
            }
            """;
        Assert.Equal(Identity(), Identity(reformatted));
    }

    [Fact]
    public void Identity_ContentChange_Invalidates()
    {
        string changed = ValidRecipe.Replace("0.25D", "0.75D");
        Assert.NotEqual(Identity(), Identity(changed));
    }

    [Fact]
    public void Identity_SeedChange_Invalidates()
    {
        Assert.NotEqual(Identity(), Identity(seed: 7));
    }

    [Fact]
    public void Identity_SizeChange_Invalidates()
    {
        Assert.NotEqual(Identity(), Identity(width: 1024));
    }

    [Fact]
    public void Identity_TimeChange_Invalidates()
    {
        Assert.NotEqual(Identity(), Identity(time: 2.5));
    }

    [Fact]
    public void Identity_DependencyContentChange_Invalidates()
    {
        var before = new Dictionary<string, string> { ["Tex/t.png"] = "hash-a" };
        var after = new Dictionary<string, string> { ["Tex/t.png"] = "hash-b" };
        Assert.NotEqual(Identity(deps: before), Identity(deps: after));
    }

    [Fact]
    public void Identity_ToolVersionChange_Invalidates()
    {
        Assert.NotEqual(Identity(), Identity(exporter: "xengine-three-exporter/0.3"));
        Assert.NotEqual(Identity(), Identity(three: "r181"));
    }

    [Fact]
    public void Identity_IsSha256Hex()
    {
        string id = Identity();
        Assert.Equal(64, id.Length);
        Assert.Matches("^[0-9a-f]{64}$", id);
    }

    // ------------------------------------------------------------- staged export

    [Fact]
    public void StagedExport_Promote_ReplacesPreviousArtifacts()
    {
        string output = Path.Combine(Path.GetTempPath(), "threefx-tests-" + Guid.NewGuid().ToString("N"), "out");
        Directory.CreateDirectory(output);
        try
        {
            File.WriteAllText(Path.Combine(output, "artifact.png"), "old");

            using (var staging = new ThreeFxStagedExport(output))
            {
                staging.WriteFile("artifact.png", new byte[] { 1, 2, 3 });
                staging.Promote();
            }

            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(output, "artifact.png")));
            Assert.False(Directory.Exists(Path.Combine(output, ".staging")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(output)!, recursive: true);
        }
    }

    [Fact]
    public void StagedExport_Failure_PreservesPreviousArtifacts()
    {
        string output = Path.Combine(Path.GetTempPath(), "threefx-tests-" + Guid.NewGuid().ToString("N"), "out");
        Directory.CreateDirectory(output);
        try
        {
            File.WriteAllText(Path.Combine(output, "artifact.png"), "old");

            using (var staging = new ThreeFxStagedExport(output))
            {
                staging.WriteFile("artifact.png", new byte[] { 9, 9 });
                staging.WriteFile("broken.json", Array.Empty<byte>()); // empty → verification fails
                Assert.Throws<IOException>(staging.Promote);
            }

            // Nothing was promoted: the old artifact survives, staging is cleaned up.
            Assert.Equal("old", File.ReadAllText(Path.Combine(output, "artifact.png")));
            Assert.False(File.Exists(Path.Combine(output, "broken.json")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(output)!, recursive: true);
        }
    }

    [Fact]
    public void ExportStateFile_RoundTrips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "threefx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(ThreeFxExportStateFile.TryRead(dir));

            var state = new ThreeFxExportState("id-123", "artifact.png", "sha-abc");
            ThreeFxExportStateFile.Write(dir, state);

            ThreeFxExportState? read = ThreeFxExportStateFile.TryRead(dir);
            Assert.NotNull(read);
            Assert.Equal("id-123", read.CacheIdentity);
            Assert.Equal("artifact.png", read.ArtifactFile);
            Assert.Equal("sha-abc", read.ArtifactSha256);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class ThreeFxEffectsTests
{
    private const string ValidEffectsRecipe = """
        {
          "schemaVersion": 1,
          "stableId": "fx_demo",
          "recipe": { "mesh": "sphere", "hue": 0.3D },
          "effects": {
            "fresnel": { "color": [1.0D, 0.5D, 0.2D], "power": 2.5D, "intensity": 1.0D },
            "particles": { "mode": "burst", "count": 300, "color": [1.0D, 0.1D, 0.1D], "size": 0.05D, "speed": 2.0D },
            "post": { "bloomIntensity": 1.0D, "bloomThreshold": 0.7D, "vignette": 0.5D, "saturation": 0.1D, "brightness": 0.0D, "tonemap": "aces" }
          },
          "dependencies": [],
          "seed": 0,
          "exportWidth": 512,
          "exportHeight": 512
        }
        """;

    [Fact]
    public void Parse_EffectsSection_ProducesTypedEffects()
    {
        bool ok = ThreeFxDocument.TryParse(ValidEffectsRecipe, out ThreeFxRecipe? recipe, out List<ThreeAuthoringDiagnostic> issues);
        Assert.True(ok);
        Assert.NotNull(recipe!.Effects.Fresnel);
        Assert.Equal(2.5, recipe.Effects.Fresnel!.Power, 6);
        Assert.Equal("burst", recipe.Effects.Particles!.Mode);
        Assert.Equal(300, recipe.Effects.Particles.Count);
        Assert.Equal(1.0, recipe.Effects.Post!.BloomIntensity, 6);
        Assert.Equal("aces", recipe.Effects.Post.Tonemap);
    }

    [Fact]
    public void Validate_RejectsUnknownParticleMode()
    {
        bool ok = ThreeFxDocument.TryParse(ValidEffectsRecipe.Replace("burst", "spiral"), out ThreeFxRecipe? recipe, out _);
        Assert.True(ok);
        Assert.Contains(ThreeFxValidator.Validate(recipe!), d => d.Code == "effects.particles.mode");
    }

    [Fact]
    public void Validate_RejectsOutOfRangeBloom()
    {
        bool ok = ThreeFxDocument.TryParse(ValidEffectsRecipe.Replace("1.0D, \"bloomThreshold\"", "9.0D, \"bloomThreshold\""), out ThreeFxRecipe? recipe, out _);
        Assert.True(ok);
        Assert.Contains(ThreeFxValidator.Validate(recipe!), d => d.Code == "effects.post.bloomIntensity");
    }

    [Fact]
    public void Validate_RejectsBadTonemap()
    {
        bool ok = ThreeFxDocument.TryParse(ValidEffectsRecipe.Replace("aces", "filmnoir"), out ThreeFxRecipe? recipe, out _);
        Assert.True(ok);
        Assert.Contains(ThreeFxValidator.Validate(recipe!), d => d.Code == "effects.post.tonemap");
    }

    [Fact]
    public void Validate_AcceptsEffectsWithinBounds()
    {
        bool ok = ThreeFxDocument.TryParse(ValidEffectsRecipe, out ThreeFxRecipe? recipe, out _);
        Assert.True(ok);
        Assert.Empty(ThreeFxValidator.Validate(recipe!));
    }
}

public sealed class ThreeFxBakeTests
{
    private const string BakeRecipe = """
        {
          "schemaVersion": 1,
          "stableId": "bake_demo",
          "recipe": { "mesh": "torusKnot", "hue": 0.45D },
          "bake": { "frames": 8, "duration": 2.0D, "layout": "both", "transparent": true },
          "dependencies": [],
          "seed": 5,
          "exportWidth": 256,
          "exportHeight": 256
        }
        """;

    [Fact]
    public void Parse_BakeSection_ProducesTypedSettings()
    {
        bool ok = ThreeFxDocument.TryParse(BakeRecipe, out ThreeFxRecipe? recipe, out _);
        Assert.True(ok);
        Assert.NotNull(recipe!.Bake);
        Assert.Equal(8, recipe.Bake!.Frames);
        Assert.Equal(2.0, recipe.Bake.Duration, 6);
        Assert.Equal("both", recipe.Bake.Layout);
        Assert.True(recipe.Bake.Transparent);
    }

    [Fact]
    public void Validate_RejectsBadBakeParams()
    {
        string bad = BakeRecipe.Replace("\"frames\": 8", "\"frames\": 65")
            .Replace("\"duration\": 2.0D", "\"duration\": 0.01D")
            .Replace("\"layout\": \"both\"", "\"layout\": \"grid\"");
        bool ok = ThreeFxDocument.TryParse(bad, out ThreeFxRecipe? recipe, out _);
        Assert.True(ok);
        var issues = ThreeFxValidator.Validate(recipe!);
        Assert.Contains(issues, d => d.Code == "bake.frames");
        Assert.Contains(issues, d => d.Code == "bake.duration");
        Assert.Contains(issues, d => d.Code == "bake.layout");
    }

    [Fact]
    public void Validate_AcceptsValidBake()
    {
        bool ok = ThreeFxDocument.TryParse(BakeRecipe, out ThreeFxRecipe? recipe, out _);
        Assert.True(ok);
        Assert.DoesNotContain(ThreeFxValidator.Validate(recipe!), d => d.Code.StartsWith("bake.", StringComparison.Ordinal));
    }

    /// <summary>Loop-phase-aligned sampling: t_i = i/N · duration, so t=0 ≡ t=duration.</summary>
    [Fact]
    public void BakeFrameTimes_AreLoopAligned()
    {
        int frames = 8;
        double duration = 2.0;
        var times = new List<double>();
        for (int i = 0; i < frames; i++)
            times.Add(i * duration / frames);

        Assert.Equal(0, times[0]);
        Assert.Equal(duration * (frames - 1) / frames, times[^1], 9);
        Assert.Equal(duration, times[0] + duration, 9); // seam: frame[0] + duration == implied end
        for (int i = 1; i < frames; i++)
            Assert.True(times[i] > times[i - 1]);
    }
}
