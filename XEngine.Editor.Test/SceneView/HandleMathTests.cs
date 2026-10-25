// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Xunit;

using XEngine.Editor.GUI.SceneView;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.Test.SceneView;

/// <summary>
/// Verifies the math behind the M10 radius / range / cone-angle scene handles: where a drag lands on
/// the handle axis, how far a point is from the pick ray, and the cone angle ↔ rim radius pair.
/// </summary>
public class HandleMathTests
{
    [Fact]
    public void ClosestAxisDistance_ProjectsRayOntoAxis()
    {
        // Ray looking straight down -Z from x=5: its closest approach to the X axis is at x=5.
        var ray = new Ray(new Float3(5f, 0f, 10f), new Float3(0f, 0f, -1f));
        float t = HandleMath.ClosestAxisDistance(Float3.Zero, Float3.UnitX, ray);
        Assert.Equal(5f, t, 3);
    }

    [Fact]
    public void ClosestAxisDistance_IsRelativeToOrigin()
    {
        var ray = new Ray(new Float3(5f, 0f, 10f), new Float3(0f, 0f, -1f));
        float t = HandleMath.ClosestAxisDistance(new Float3(2f, 0f, 0f), Float3.UnitX, ray);
        Assert.Equal(3f, t, 3);
    }

    [Fact]
    public void ClosestAxisDistance_ParallelRayReturnsZero()
    {
        var ray = new Ray(new Float3(0f, 1f, 0f), Float3.UnitX);
        Assert.Equal(0f, HandleMath.ClosestAxisDistance(Float3.Zero, Float3.UnitX, ray), 3);
    }

    [Fact]
    public void RadiusFromRay_ClampsBehindOriginToMinimum()
    {
        // Closest approach lands at x = -4, which would be a negative radius.
        var ray = new Ray(new Float3(-4f, 0f, 10f), new Float3(0f, 0f, -1f));
        float r = HandleMath.RadiusFromRay(Float3.Zero, Float3.UnitX, ray, min: 0.25f);
        Assert.Equal(0.25f, r, 4);
    }

    [Fact]
    public void DistanceToRay_MeasuresPerpendicularDistance()
    {
        var ray = new Ray(Float3.Zero, Float3.UnitZ);
        Assert.Equal(3f, HandleMath.DistanceToRay(new Float3(3f, 0f, 5f), ray), 3);
        Assert.Equal(0f, HandleMath.DistanceToRay(new Float3(0f, 0f, 7f), ray), 3);
    }

    [Fact]
    public void ConeAngleAndRimRadius_RoundTrip()
    {
        const float range = 8f;
        const float angle = 45f;
        float rim = HandleMath.ConeRimRadius(range, angle);
        Assert.Equal(angle, HandleMath.ConeAngleFromRimRadius(range, rim), 3);

        // A 90° cone's rim radius equals its range (tan 45° = 1).
        Assert.Equal(range, HandleMath.ConeRimRadius(range, 90f), 3);
    }

    [Fact]
    public void ConeAngleFromRimRadius_ClampsToValidRange()
    {
        Assert.True(HandleMath.ConeAngleFromRimRadius(0.001f, 1000f) <= 179f);
        Assert.True(HandleMath.ConeAngleFromRimRadius(10f, 0f) >= 0.1f);
        Assert.True(HandleMath.ConeAngleFromRimRadius(10f, -5f) >= 0.1f);
    }
}
