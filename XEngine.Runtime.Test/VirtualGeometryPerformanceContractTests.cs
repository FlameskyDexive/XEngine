// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.VirtualGeometry;
using XEngine.Runtime.VirtualGeometry;
using XEngine.Runtime.VirtualGeometry.Generation;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Performance contracts for the virtual geometry per-frame CPU work. The selector runs
/// once per (renderer, view, pass); at the reference scale (7200-triangle height field,
/// ~4600 packed clusters across levels) a selection must stay far under a millisecond so
/// several renderers × passes fit the frame budget. Bake cost is import-time and only
/// gets a sanity ceiling here.
/// </summary>
public sealed class VirtualGeometryPerformanceContractTests
{
    private static (VirtualGeometryData Data, ViewerData Close, ViewerData Far, uint[] Staging, List<int> Visible, VgSubMeshRange[] Ranges) Setup()
    {
        var mesh = VirtualGeometryBakeTests.BuildGridMesh(60, 60, flat: false);
        var data = VirtualGeometryBaker.Generate(mesh, VirtualGeometryBakeOptions.Default);

        var projection = Float4x4.CreatePerspectiveFov(0.9f, 16f / 9f, 0.1f, 2000f);
        ViewerData Make(Float3 position)
        {
            Float3 forward = Float3.Normalize(new Float3(30, 30, 0) - position);
            Float3 right = Float3.Normalize(Float3.Cross(forward, new Float3(0, 0, 1)));
            Float3 up = Float3.Normalize(Float3.Cross(right, forward));
            return new ViewerData(position, forward, right, up)
            {
                PixelWidth = 1920,
                PixelHeight = 1080,
                ProjectionMatrix = projection,
            };
        }

        return (data, Make(new Float3(30, 30, -26)), Make(new Float3(30, 30, -300)),
            new uint[data.Indices.Length], new List<int>(512), new VgSubMeshRange[1]);
    }

    [Fact]
    public void Bake_7200TriangleMesh_CompletesWithinSanityCeiling()
    {
        var mesh = VirtualGeometryBakeTests.BuildGridMesh(60, 60, flat: false);
        var stopwatch = Stopwatch.StartNew();
        var data = VirtualGeometryBaker.Generate(mesh, VirtualGeometryBakeOptions.Default);
        stopwatch.Stop();

        Assert.True(data.Levels.Length >= 3);
        // Import-time budget: QEM cascade + clustering on a 7.2k mesh must stay well under
        // a second on any dev machine (CI included).
        Assert.True(stopwatch.ElapsedMilliseconds < 900,
            $"bake took {stopwatch.ElapsedMilliseconds} ms for 7200 triangles");
    }

    [Fact]
    public void Selector_CloseView_UnderHalfMillisecond()
    {
        var (data, close, _, staging, visible, ranges) = Setup();
        var watch = Stopwatch.StartNew();
        const int iterations = 100;
        for (int i = 0; i < iterations; i++)
            VirtualGeometrySelector.Select(data, Float4x4.Identity, 1f, close, 1f, staging, visible, ranges, 1);
        watch.Stop();

        double perCallMs = watch.Elapsed.TotalMilliseconds / iterations;
        Assert.True(perCallMs < 0.5, $"close-view selection took {perCallMs:F3} ms per call");
    }

    [Fact]
    public void Selector_FarView_UnderHalfMillisecond()
    {
        var (data, _, far, staging, visible, ranges) = Setup();
        var watch = Stopwatch.StartNew();
        const int iterations = 100;
        for (int i = 0; i < iterations; i++)
            VirtualGeometrySelector.Select(data, Float4x4.Identity, 1f, far, 3f, staging, visible, ranges, 1);
        watch.Stop();

        double perCallMs = watch.Elapsed.TotalMilliseconds / iterations;
        Assert.True(perCallMs < 0.5, $"far-view selection took {perCallMs:F3} ms per call");
    }

    [Fact]
    public void Selector_CutsTrianglesVersusFullMesh()
    {
        // The rendering-side comparison that matters: selected triangles vs the full mesh
        // a MeshRenderer would draw. Far view must pay a fraction of the full-mesh cost.
        var (data, _, far, staging, visible, ranges) = Setup();

        int used = VirtualGeometrySelector.Select(data, Float4x4.Identity, 1f, far, 3f, staging, visible, ranges, 1);

        Assert.True(used / 3 < data.SourceTriangleCount / 2,
            $"far view selected {used / 3} of {data.SourceTriangleCount} source triangles");
    }
}
