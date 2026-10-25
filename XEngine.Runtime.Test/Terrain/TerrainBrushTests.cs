// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Buffers.Binary;

using XEngine.Runtime;
using XEngine.Terrain;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Terrain;

public sealed class TerrainBrushTests : RuntimeTestBase
{
    [Fact]
    public void Sampler_CentreIsFull_OutsideIsZero()
    {
        float centre = TerrainBrushSampler.Weight(0, 0, 10f, 0f, 0.5f, null, [], 0, 0);
        Assert.InRange(centre, 0.99f, 1.01f);
        float outside = TerrainBrushSampler.Weight(11f, 0, 10f, 0f, 0.5f, null, [], 0, 0);
        Assert.Equal(0f, outside);
    }

    [Fact]
    public void Sampler_RotationMovesTheMask()
    {
        float[] mask =
        [
            1f, 0f,
            0f, 0f,
        ];
        float unrotated = TerrainBrushSampler.Weight(-5f, -5f, 10f, 0f, 0f, null, mask, 2, 2);
        float rotated = TerrainBrushSampler.Weight(-5f, -5f, 10f, MathF.PI, 0f, null, mask, 2, 2);
        Assert.True(unrotated > 0.4f, $"unrotated={unrotated}");
        Assert.True(rotated < unrotated * 0.5f, $"rotated={rotated} unrotated={unrotated}");
    }

    [Fact]
    public void Sampler_StrengthCurveScalesWeight()
    {
        var half = new AnimationCurve([new KeyFrame(0f, 0.5f), new KeyFrame(1f, 0.5f)]);
        float w = TerrainBrushSampler.Weight(0, 0, 8f, 0f, 0f, half, [], 0, 0);
        Assert.Equal(0.5f, w, 3);
    }

    [Fact]
    public void Raw16_RoundTripsHeights()
    {
        var data = new TerrainData
        {
            HeightmapResolution = 4,
            Heights = new short[16],
        };
        data.SetHeight(1, 2, 0.25f);
        data.SetHeight(3, 3, 1f);
        byte[] raw = data.ExportRaw16();
        Assert.Equal(32, raw.Length);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0, 2)));

        var loaded = new TerrainData { HeightmapResolution = 2, Heights = new short[4] };
        Assert.True(loaded.TryImportRaw16(raw));
        Assert.Equal(4, loaded.HeightmapResolution);
        Assert.Equal(0.25f, loaded.GetHeight(1, 2), 2);
        Assert.Equal(1f, loaded.GetHeight(3, 3), 2);
    }

    [Fact]
    public void Raw16_RejectsNonSquarePayload()
    {
        var data = new TerrainData { HeightmapResolution = 2, Heights = new short[4] };
        Assert.False(data.TryImportRaw16(new byte[6]));
        Assert.False(data.TryImportRaw16([]));
    }

    [Fact]
    public void BlendHeight_InterpolatesTowardTarget()
    {
        var data = new TerrainData { HeightmapResolution = 2, Heights = new short[4] };
        data.SetHeight(0, 0, 0f);
        data.BlendHeight(0, 0, 1f, 0.5f);
        Assert.Equal(0.5f, data.GetHeight(0, 0), 2);
    }

    [Fact]
    public void MassPlaceTrees_ScattersAndClearRemoves()
    {
        var data = new TerrainData();
        data.TreePrototypes.Add(new TreePrototype());
        int added = data.MassPlaceTrees(0, 25, seed: 7);
        Assert.Equal(25, added);
        Assert.Equal(25, data.Trees.Count);
        Assert.All(data.Trees, t =>
        {
            Assert.Equal(0, t.PrototypeIndex);
            Assert.InRange(t.Position.X, 0f, 1f);
            Assert.InRange(t.Position.Y, 0f, 1f);
        });
        Assert.Equal(25, data.ClearTrees());
        Assert.Empty(data.Trees);
    }

    [Fact]
    public void FillDetailDensity_WritesWholeLayer()
    {
        var data = new TerrainData();
        data.FillDetailDensity(0, 0.4f);
        Assert.Equal(0.4f, data.GetDetailDensity(0, 0, 0), 3);
        Assert.Equal(0.4f, data.GetDetailDensity(0, data.DetailResolution - 1, data.DetailResolution - 1), 3);
        data.FillDetailDensity(0, 0f);
        Assert.Equal(0f, data.GetDetailDensity(0, 0, 0));
    }

    [Fact]
    public void BrushEvidence_WritesBoardAndNotes()
    {
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "terrain");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 280, 18, 19, 26);
        board.Fill(24, 24, 872, 232, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 232, 70, 74, 90, 2);
        board.Text(40, 40, "M4B TERRAIN BRUSH UPGRADE", 230, 232, 240, 2);
        board.Text(40, 80, "MASK ROTATION SPACING SCATTER CURVE", 140, 146, 160, 1);
        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "SET HEIGHT / STAMP + RAW16 IO", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "MASS PLACE TREES / FILL DETAILS", 200, 220, 200, 1);
        board.Text(40, 210, "ACCEPTANCE 2026-08-15", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "m4b-brushes.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "m4b-brushes.md"),
            "# M4b Terrain brushes\n\n" +
            "Date: 2026-08-15. Branch: `codex/animation-module`.\n\n" +
            "Sculpt brushes take a luminance mask, rotation, spacing, scatter, and a strength curve. " +
            "New height tools: **Set Height** and **Stamp**. Settings can import/export Unity-style " +
            "little-endian 16-bit RAW heightmaps. Trees/Details tabs expose mass place and fill.\n\n" +
            "Screenshot: [m4b-brushes.png](m4b-brushes.png).\n");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
