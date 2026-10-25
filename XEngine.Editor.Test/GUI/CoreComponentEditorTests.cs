// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Editor.GUI.CustomEditors;
using XEngine.Runtime;

namespace XEngine.Editor.Test.GUI;

/// <summary>
/// Verifies the M10 core component inspectors' edit logic: camera clip-plane repair and frustum
/// math, spot cone clamping, and mesh-renderer material-slot matching. These are the pure helpers
/// behind the inspector buttons, so they run without a UI.
/// </summary>
public class CoreComponentEditorTests
{
    [Fact]
    public void FixClipPlanes_RepairsNonPositiveNear()
    {
        var camera = new Camera { NearClipPlane = 0f, FarClipPlane = 50f };

        Assert.True(CameraEditor.HasInvalidClipPlanes(camera));
        Assert.True(CameraEditor.FixClipPlanes(camera));
        Assert.False(CameraEditor.HasInvalidClipPlanes(camera));
        Assert.Equal(CameraEditor.MinNearClip, camera.NearClipPlane, 4);
        Assert.Equal(50f, camera.FarClipPlane, 4);
    }

    [Fact]
    public void FixClipPlanes_RepairsFarBehindNear()
    {
        var camera = new Camera { NearClipPlane = 5f, FarClipPlane = 1f };

        Assert.True(CameraEditor.FixClipPlanes(camera));
        Assert.True(camera.FarClipPlane > camera.NearClipPlane);
        Assert.False(CameraEditor.HasInvalidClipPlanes(camera));
    }

    [Fact]
    public void FixClipPlanes_LeavesValidPlanesAlone()
    {
        var camera = new Camera { NearClipPlane = 0.3f, FarClipPlane = 1000f };

        Assert.False(CameraEditor.HasInvalidClipPlanes(camera));
        Assert.False(CameraEditor.FixClipPlanes(camera));
        Assert.Equal(0.3f, camera.NearClipPlane, 4);
        Assert.Equal(1000f, camera.FarClipPlane, 4);
    }

    [Fact]
    public void FrustumHeightAt_MatchesProjectionMode()
    {
        // A 90° vertical FOV spans exactly 2× the distance.
        var perspective = new Camera { ProjectionMode = Camera.ProjectionType.Perspective, FieldOfView = 90f };
        Assert.Equal(20f, CameraEditor.FrustumHeightAt(perspective, 10f), 2);

        // Orthographic height is distance-independent.
        var ortho = new Camera { ProjectionMode = Camera.ProjectionType.Orthographic, OrthographicSize = 3f };
        Assert.Equal(6f, CameraEditor.FrustumHeightAt(ortho, 10f), 3);
        Assert.Equal(6f, CameraEditor.FrustumHeightAt(ortho, 100f), 3);
    }

    [Fact]
    public void ClampSpotAngles_PullsInnerConeInsideOuter()
    {
        var spot = new SpotLight { SpotAngle = 30f, InnerSpotAngle = 45f };

        Assert.True(LightEditor.ClampSpotAngles(spot));
        Assert.Equal(30f, spot.InnerSpotAngle, 4);

        // Already consistent: no change, no churn.
        Assert.False(LightEditor.ClampSpotAngles(spot));
    }

    [Fact]
    public void MatchSubmeshCount_WithoutMesh_IsNoOp()
    {
        var renderer = new MeshRenderer();

        Assert.Equal(0, MeshRendererEditor.SubmeshCount(renderer));
        Assert.False(MeshRendererEditor.MatchSubmeshCount(renderer));
    }
}
