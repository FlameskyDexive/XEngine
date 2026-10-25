// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.VirtualGeometry;
using XEngine.Runtime.VirtualGeometry;
using XEngine.Runtime.VirtualGeometry.Generation;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>Tests for the CPU cluster selection (<see cref="VirtualGeometrySelector"/>).</summary>
public class VirtualGeometrySelectorTests
{
    private static (VirtualGeometryData Data, uint[] Staging, List<int> Visible, VgSubMeshRange[] Ranges) BakeGrid()
    {
        var mesh = VirtualGeometryBakeTests.BuildGridMesh(40, 40, false);
        var data = VirtualGeometryBaker.Generate(mesh, VirtualGeometryBakeOptions.Default);
        return (data, new uint[data.Indices.Length], new List<int>(256), new VgSubMeshRange[1]);
    }

    private static ViewerData MakeViewer(Float3 position, uint pixelHeight)
    {
        var viewer = new ViewerData(position, new Float3(0, 0, -1), new Float3(1, 0, 0), new Float3(0, 1, 0))
        {
            PixelWidth = (uint)(pixelHeight * 16 / 9),
            PixelHeight = pixelHeight,
        };
        var proj = Float4x4.Identity;
        proj[0, 0] = 1.7777f;
        proj[1, 1] = 2.0f; // cot(fovY/2): tanHalfY = 0.5 (≈53° vertical fov)
        viewer.ProjectionMatrix = proj;
        return viewer;
    }

    private static int SelectAll(VirtualGeometryData data, in ViewerData viewer, float pixelError, uint[] staging, List<int> visible, VgSubMeshRange[] ranges)
        => VirtualGeometrySelector.Select(data, Float4x4.Identity, 1f, viewer, pixelError, staging, visible, ranges, 1);

    [Fact]
    public void Select_CloseView_UsesFullDetail()
    {
        var (data, staging, visible, ranges) = BakeGrid();

        // Camera right in front of the 40x40 grid: every cluster's screen error is tiny.
        ViewerData viewer = MakeViewer(new Float3(20, 20, 25), 2000);
        int used = SelectAll(data, viewer, 1f, staging, visible, ranges);

        Assert.Equal(data.Levels[0].TriangleCount * 3, used);
        Assert.Equal(data.Levels[0].ClusterCount, visible.Count);
        Assert.Equal(used, ranges[0].Count);
    }

    [Fact]
    public void Select_FarView_ReducesTriangles()
    {
        var (data, staging, visible, ranges) = BakeGrid();

        ViewerData close = MakeViewer(new Float3(20, 20, 25), 2000);
        int closeUsed = SelectAll(data, close, 1f, staging, visible, ranges);

        // At 500 units a complete first LOD can be exactly half the source. Move
        // far enough to exercise deeper LODs without counting discarded patches.
        ViewerData far = MakeViewer(new Float3(20, 20, 5000), 2000);
        int farUsed = SelectAll(data, far, 1f, staging, visible, ranges);

        Assert.True(farUsed < closeUsed / 2, $"far view must cut triangles hard: far={farUsed / 3} close={closeUsed / 3}");
        Assert.True(farUsed > 0);
        Assert.True(visible.Count < data.Levels[0].ClusterCount);
    }

    [Fact]
    public void Select_BehindCamera_SelectsNothing()
    {
        var (data, staging, visible, ranges) = BakeGrid();

        // Grid spans 0..40 on XY with camera beyond +Z looking -Z; flip to look away.
        ViewerData away = MakeViewer(new Float3(20, 20, 25), 2000);
        away.Forward = new Float3(0, 0, 1);
        away.Right = new Float3(-1, 0, 0);
        int used = SelectAll(data, away, 1f, staging, visible, ranges);

        Assert.Equal(0, used);
        Assert.Empty(visible);
    }

    [Fact]
    public void Select_RelaxedThreshold_SelectsCoarser()
    {
        var (data, staging, visible, ranges) = BakeGrid();

        ViewerData viewer = MakeViewer(new Float3(20, 20, 120), 2000);
        int strict = SelectAll(data, viewer, 1f, staging, visible, ranges);
        int relaxed = SelectAll(data, viewer, 64f, staging, visible, ranges);

        Assert.True(relaxed <= strict, "relaxed threshold may never select more triangles");
    }

    [Fact]
    public void Select_LargerPixelError_NeverExceedsFullDetail()
    {
        var (data, staging, visible, ranges) = BakeGrid();

        ViewerData viewer = MakeViewer(new Float3(20, 20, 25), 2000);
        int used = SelectAll(data, viewer, 8f, staging, visible, ranges);

        Assert.True(used <= data.Indices.Length);
        // Every emitted index must reference a real vertex.
        for (int i = 0; i < used; i++)
            Assert.True(staging[i] < data.Positions.Length, $"staging index {i} out of range: {staging[i]}");
    }
}
