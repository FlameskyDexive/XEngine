// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test;

/// <summary>
/// Verifies the trailing blend-shape block of the .mesh binary payload round-trips through Echo
/// text (single frame, multi-frame progressive, optional normal/tangent deltas) and that meshes
/// serialized before morph support still load with an empty shape list.
/// </summary>
public class MeshBlendShapeTests
{
    private static Mesh RoundTrip(Mesh mesh)
    {
        EchoObject echo = Serializer.Serialize(mesh, TypeMode.Aggressive);
        string text = echo.WriteToString();
        return Serializer.Deserialize<Mesh>(EchoObject.ReadFromString(text))!;
    }

    [Fact]
    public void RoundTrip_SingleFrameShape_PreservesNameWeightAndDeltas()
    {
        var mesh = new Mesh
        {
            Vertices = new[] { new Float3(0, 0, 0), new Float3(1, 0, 0), new Float3(0, 1, 0) },
            BlendShapes = new[]
            {
                new BlendShape
                {
                    Name = "Smile",
                    Frames = new[]
                    {
                        new BlendShapeFrame
                        {
                            Weight = 100f,
                            DeltaVertices = new[] { new Float3(0, 0.5f, 0), new Float3(0.1f, 0, 0), new Float3(0, 0, 0.2f) },
                            DeltaNormals = new[] { new Float3(0, 1, 0), new Float3(1, 0, 0), new Float3(0, 0, 1) },
                        },
                    },
                },
            },
        };

        var loaded = RoundTrip(mesh);

        Assert.Equal(1, loaded.BlendShapeCount);
        Assert.Equal("Smile", loaded.GetBlendShapeName(0));
        Assert.Equal(0, loaded.GetBlendShapeIndex("Smile"));
        Assert.Equal(-1, loaded.GetBlendShapeIndex("Frown"));
        Assert.Equal(1, loaded.GetBlendShapeFrameCount(0));
        Assert.Equal(100f, loaded.GetBlendShapeFrameWeight(0, 0));

        var frame = loaded.BlendShapes[0].Frames[0];
        Assert.Equal(3, frame.DeltaVertices.Length);
        Assert.Equal(0.5f, frame.DeltaVertices[0].Y);
        Assert.Equal(0.1f, frame.DeltaVertices[1].X);
        Assert.NotNull(frame.DeltaNormals);
        Assert.Null(frame.DeltaTangents);
    }

    [Fact]
    public void RoundTrip_MultiFrameProgressiveShape_PreservesAllFrames()
    {
        var mesh = new Mesh
        {
            Vertices = new[] { new Float3(0, 0, 0), new Float3(1, 0, 0) },
            BlendShapes = new[]
            {
                new BlendShape
                {
                    Name = "Brow",
                    Frames = new[]
                    {
                        new BlendShapeFrame { Weight = 25f, DeltaVertices = new[] { new Float3(0, 0.1f, 0), new Float3(0, 0.1f, 0) } },
                        new BlendShapeFrame { Weight = 75f, DeltaVertices = new[] { new Float3(0, 0.3f, 0), new Float3(0, 0.3f, 0) } },
                        new BlendShapeFrame
                        {
                            Weight = 100f,
                            DeltaVertices = new[] { new Float3(0, 0.5f, 0), new Float3(0, 0.5f, 0) },
                            DeltaTangents = new[] { new Float3(1, 0, 0), new Float3(1, 0, 0) },
                        },
                    },
                },
                new BlendShape
                {
                    Name = "Frown",
                    Frames = new[] { new BlendShapeFrame { Weight = 50f, DeltaVertices = new[] { new Float3(0, -0.2f, 0), new Float3(0, -0.2f, 0) } } },
                },
            },
        };

        var loaded = RoundTrip(mesh);

        Assert.Equal(2, loaded.BlendShapeCount);
        Assert.Equal(3, loaded.GetBlendShapeFrameCount(0));
        Assert.Equal(25f, loaded.GetBlendShapeFrameWeight(0, 0));
        Assert.Equal(75f, loaded.GetBlendShapeFrameWeight(0, 1));
        Assert.Equal(100f, loaded.GetBlendShapeFrameWeight(0, 2));
        Assert.NotNull(loaded.BlendShapes[0].Frames[2].DeltaTangents);
        Assert.Equal(1, loaded.GetBlendShapeFrameCount(1));
        Assert.Equal(50f, loaded.GetBlendShapeFrameWeight(1, 0));
    }

    [Fact]
    public void Deserialize_MeshWithoutBlendShapes_LeavesShapesEmpty()
    {
        // A pre-morph-format mesh: Serialize drops the trailing block only when shapes are empty,
        // so deserialize must tolerate a payload that ends right after the submeshes.
        var mesh = new Mesh
        {
            Vertices = new[] { new Float3(0, 0, 0), new Float3(1, 0, 0) },
            Indices = new uint[] { 0, 1 },
        };

        var loaded = RoundTrip(mesh);

        Assert.NotNull(loaded);
        Assert.Equal(0, loaded.BlendShapeCount);
        Assert.False(loaded.HasBlendShapes);
        Assert.Equal(2, loaded.Vertices.Length);
    }

    [Fact]
    public void RoundTrip_EmptyShapeNameSurvives()
    {
        var mesh = new Mesh
        {
            Vertices = new[] { new Float3(0, 0, 0) },
            BlendShapes = new[]
            {
                new BlendShape { Name = string.Empty, Frames = new[] { new BlendShapeFrame { Weight = 100f, DeltaVertices = new[] { new Float3(1, 1, 1) } } } },
            },
        };

        var loaded = RoundTrip(mesh);

        Assert.Equal(1, loaded.BlendShapeCount);
        Assert.Equal(string.Empty, loaded.GetBlendShapeName(0));
    }
}
