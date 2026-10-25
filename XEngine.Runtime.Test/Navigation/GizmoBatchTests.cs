// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Navigation;

public sealed class GizmoBatchTests
{
    [Fact]
    public void DrawTriangles_AndDrawLines_PopulateMeshes()
    {
        Debug.ClearGizmos();
        Float3[] tris =
        [
            new(0, 0, 0), new(1, 0, 0), new(0, 0, 1),
            new(1, 0, 0), new(1, 0, 1), new(0, 0, 1),
        ];
        Debug.DrawTriangles(tris, ReadOnlySpan<int>.Empty, Color.Cyan);

        Float3[] lines =
        [
            new(0, 0, 0), new(1, 0, 0),
            new(1, 0, 0), new(1, 0, 1),
        ];
        Debug.DrawLines(lines, ReadOnlySpan<int>.Empty, Color.Yellow);

        (Mesh? wire, Mesh? solid) = Debug.GetGizmoDrawData();
        Assert.NotNull(solid);
        Assert.Equal(6, solid!.VertexCount);
        Assert.NotNull(wire);
        Assert.Equal(4, wire!.VertexCount);
        Debug.ClearGizmos();
    }

    [Fact]
    public void DrawTriangles_Indexed_UsesVertexBuffer()
    {
        Debug.ClearGizmos();
        Float3[] verts = [new(0, 0, 0), new(2, 0, 0), new(0, 0, 2), new(2, 0, 2)];
        int[] indices = [0, 1, 2, 1, 3, 2];
        Debug.DrawTriangles(verts, indices, new Color(0.2f, 0.8f, 1f, 0.4f));
        (_, Mesh? solid) = Debug.GetGizmoDrawData();
        Assert.NotNull(solid);
        Assert.Equal(6, solid!.VertexCount);
        Debug.ClearGizmos();
    }
}
