// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Jitter2.Collision.Shapes;

using Xunit;

using XEngine.Vector;

namespace XEngine.Runtime.Test;

/// <summary>
/// Regression tests for the query guard rails (prowl-sync M2.2, ported from upstream 9aeb182cf +
/// de3727b6e): poisoned query inputs must be reported and ignored instead of crashing inside
/// Jitter's support maps, and ShapeCastHit must expose the hit distance in world units.
/// </summary>
public class PhysicsQueryValidationTests : RuntimeTestBase
{
    [Fact]
    public void Raycast_WithNaNDirection_IsIgnoredAndDoesNotThrow()
    {
        var scene = CreateScene(enable: true);
        var go = CreateGameObject("Static");
        go.Transform.Position = new Float3(0, 0, 0);
        go.AddComponent<BoxCollider>().Size = new Float3(2, 2, 2);
        scene.Add(go);

        var physics = scene.Physics;

        // NaN direction would throw inside Jitter's support maps (Math.Sign on NaN) if it got through.
        Assert.False(physics.Raycast(new Float3(0, 5, 0), new Float3(float.NaN, 0, 0), 10f, out _));
        Assert.False(physics.Raycast(new Float3(0, 5, 0), new Float3(0, 0, 0), 10f, out _));
        Assert.False(physics.Raycast(new Float3(float.PositiveInfinity, 5, 0), new Float3(0, -1, 0), 10f, out _));
        Assert.False(physics.Raycast(new Float3(0, 5, 0), new Float3(0, -1, 0), float.NaN, out _));

        // Sanity: a clean query against the same box still hits.
        Assert.True(physics.Raycast(new Float3(0, 5, 0), new Float3(0, -1, 0), 10f, out _));
    }

    [Fact]
    public void ShapeCast_Distance_IsWorldUnitsAlongSweep()
    {
        var scene = CreateScene(enable: true);
        var go = CreateGameObject("Static");
        go.Transform.Position = new Float3(0, 0, 0);
        go.AddComponent<BoxCollider>().Size = new Float3(2, 2, 2);
        scene.Add(go);

        var physics = scene.Physics;
        var sphere = new SphereShape(0.5f);

        // Cast from 5 units above towards the box: the hit lands within a few world units of the start.
        bool hit = physics.ShapeCast(sphere, Quaternion.Identity, new Float3(0, 5, 0),
            new Float3(0, -1, 0), 10f, out ShapeCastHit hitInfo, LayerMask.Everything);

        Assert.True(hit);
        Assert.True(hitInfo.Hit);
        Assert.Equal(10f * hitInfo.Fraction, hitInfo.Distance, 3);
        Assert.InRange(hitInfo.Distance, 1.5f, 3.5f);
    }
}
