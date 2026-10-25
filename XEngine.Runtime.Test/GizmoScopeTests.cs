// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>Draws a unit wire cube centred on its own transform.</summary>
public sealed class CubeGizmoProbe : MonoBehaviour
{
    public override void DrawGizmos()
        => Debug.DrawWireCube(Transform.Position, new Float3(1f, 1f, 1f), Color.White);
}

/// <summary>Draws nothing but a billboarded icon, like AudioSource / DecalProjector.</summary>
public sealed class IconOnlyGizmoProbe : MonoBehaviour
{
    public static Resources.Texture2D? Icon;

    public override void DrawGizmos()
    {
        if (Icon != null) Debug.DrawIcon(Icon, Transform.Position, 0.5f, Color.White);
    }
}

/// <summary>
/// The per-component gizmo scope behind the Scene view's Gizmos window: Gizmo Scale (geometry scaled
/// about each component's own position, so gizmos shrink in place rather than collapsing towards the
/// world origin) and Fade Gizmos (an alpha multiplier applied only to what that component drew).
/// </summary>
public class GizmoScopeTests : RuntimeTestBase
{
    private readonly float _prevScale = Debug.GizmoScale;
    private readonly Func<Float3, float, float>? _prevFade = Debug.GizmoFadeEvaluator;
    private readonly Func<MonoBehaviour, bool>? _prevIconFilter = Debug.GizmoIconFilter;
    private readonly bool _prevIcons = Debug.GizmoIconsVisible;

    public override void Dispose()
    {
        Debug.GizmoScale = _prevScale;
        Debug.GizmoFadeEvaluator = _prevFade;
        Debug.GizmoIconFilter = _prevIconFilter;
        Debug.GizmoIconsVisible = _prevIcons;
        Debug.ClearGizmos();
        base.Dispose();
    }

    private static (Float3 min, Float3 max) WireBounds()
    {
        Resources.Mesh? wire = Debug.GetGizmoDrawData().wire;
        Assert.NotNull(wire);
        Float3[] verts = wire!.Vertices;
        Assert.NotEmpty(verts);

        Float3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Float3 v in verts)
        {
            min = new Float3(MathF.Min(min.X, v.X), MathF.Min(min.Y, v.Y), MathF.Min(min.Z, v.Z));
            max = new Float3(MathF.Max(max.X, v.X), MathF.Max(max.Y, v.Y), MathF.Max(max.Z, v.Z));
        }
        return (min, max);
    }

    private static float MinWireAlpha()
    {
        Resources.Mesh? wire = Debug.GetGizmoDrawData().wire;
        Assert.NotNull(wire);
        float min = 1f;
        foreach (Color c in wire!.Colors) min = MathF.Min(min, c.A);
        return min;
    }

    [Fact]
    public void GizmoScale_ShrinksAboutTheComponentsOwnPosition()
    {
        var scene = CreateScene(enable: true);
        var go = CreateGameObject();
        go.Transform.Position = new Float3(10f, 0f, 0f);
        go.AddComponent<CubeGizmoProbe>();
        scene.Add(go);

        Debug.GizmoFadeEvaluator = null;
        Debug.GizmoScale = 1f;
        Debug.ClearGizmos();
        scene.DrawGizmos();
        (Float3 min, Float3 max) = WireBounds();
        Assert.Equal(9f, min.X, 3);
        Assert.Equal(11f, max.X, 3);

        Debug.GizmoScale = 0.25f;
        Debug.ClearGizmos();
        scene.DrawGizmos();
        (min, max) = WireBounds();

        // Quarter size, still centred on the object at x=10 — not pulled towards the origin.
        Assert.Equal(9.75f, min.X, 3);
        Assert.Equal(10.25f, max.X, 3);
        Assert.Equal(10f, (min.X + max.X) * 0.5f, 3);
    }

    [Fact]
    public void FadeEvaluator_SeesTheComponentsOwnBoundsAndDimsOnlyIt()
    {
        var scene = CreateScene(enable: true);
        var near = CreateGameObject();
        near.Transform.Position = new Float3(0f, 0f, 0f);
        near.AddComponent<CubeGizmoProbe>();
        scene.Add(near);

        var far = CreateGameObject();
        far.Transform.Position = new Float3(100f, 0f, 0f);
        far.AddComponent<CubeGizmoProbe>();
        scene.Add(far);

        var centers = new List<Float3>();
        Debug.GizmoScale = 1f;
        Debug.GizmoFadeEvaluator = (center, radius) =>
        {
            centers.Add(center);
            // Radius of a unit-half-extent cube's bounds: |(2,2,2)| / 2 = sqrt(3).
            Assert.Equal(MathF.Sqrt(3f), radius, 3);
            return center.X > 50f ? 0.25f : 1f;
        };

        Debug.ClearGizmos();
        scene.DrawGizmos();

        Assert.Equal(2, centers.Count);
        Assert.Contains(centers, c => MathF.Abs(c.X) < 0.001f);
        Assert.Contains(centers, c => MathF.Abs(c.X - 100f) < 0.001f);

        // Only the far cube was dimmed; the near one keeps full alpha.
        Resources.Mesh wire = Debug.GetGizmoDrawData().wire!;
        Float3[] verts = wire.Vertices;
        Color[] colors = wire.Colors;
        for (int i = 0; i < verts.Length; i++)
            Assert.Equal(verts[i].X > 50f ? 0.25f : 1f, colors[i].A, 2);
    }

    [Fact]
    public void FadeEvaluator_UsesTheIconsOwnExtent_SoIconOnlyComponentsDoNotVanish()
    {
        var scene = CreateScene(enable: true);
        var go = CreateGameObject();
        go.AddComponent<IconOnlyGizmoProbe>();
        scene.Add(go);

        IconOnlyGizmoProbe.Icon = Resources.Texture2D.LoadDefault(Resources.DefaultTexture.White);
        Debug.GizmoIconsVisible = true;
        Debug.GizmoIconScale = 1f;
        Debug.GizmoScale = 1f;

        float seenRadius = -1f;
        Debug.GizmoFadeEvaluator = (_, radius) => { seenRadius = radius; return 1f; };

        Debug.ClearGizmos();
        scene.DrawGizmos();

        // A bare icon anchor is a point; without the billboard's own extent the radius would be 0
        // and every fade threshold would erase it.
        Assert.True(seenRadius > 0f, $"icon-only gizmo reported radius {seenRadius}");
    }

    [Fact]
    public void IconFilter_HidesIconsWithoutHidingWireGizmos()
    {
        var scene = CreateScene(enable: true);
        var go = CreateGameObject();
        go.AddComponent<CubeGizmoProbe>();
        go.AddComponent<IconOnlyGizmoProbe>();
        scene.Add(go);

        IconOnlyGizmoProbe.Icon = Resources.Texture2D.LoadDefault(Resources.DefaultTexture.White);
        Debug.GizmoIconsVisible = true;
        Debug.GizmoScale = 1f;
        Debug.GizmoFadeEvaluator = null;
        Debug.GizmoIconFilter = null;

        Debug.ClearGizmos();
        scene.DrawGizmos();
        Assert.NotEmpty(Debug.GetGizmoIcons());
        Assert.NotNull(Debug.GetGizmoDrawData().wire);

        Debug.GizmoIconFilter = c => c is not IconOnlyGizmoProbe;
        Debug.ClearGizmos();
        scene.DrawGizmos();

        Assert.Empty(Debug.GetGizmoIcons());
        Assert.NotNull(Debug.GetGizmoDrawData().wire);
    }

    [Fact]
    public void ObjectScale_ComposesWithAComponentsOwnPushMatrix()
    {
        // Colliders push their own local space; the object matrix must apply on top of that instead
        // of being replaced by the first PushMatrix (AddLine only peeks the stack's top).
        Debug.GizmoFadeEvaluator = null;
        Debug.GizmoScale = 0.5f;
        Debug.ClearGizmos();

        var go = CreateGameObject();
        go.Transform.Position = new Float3(4f, 0f, 0f);
        var probe = go.AddComponent<CubeGizmoProbe>();

        Debug.BeginGizmoObject(probe);
        try
        {
            Debug.PushMatrix(Float4x4.CreateTranslation(new Float3(4f, 0f, 0f)));
            Debug.DrawWireCube(Float3.Zero, new Float3(1f, 1f, 1f), Color.White);
            Debug.PopMatrix();
        }
        finally { Debug.EndGizmoObject(); }

        (Float3 min, Float3 max) = WireBounds();
        // Pushed to x=4 by the component, then halved about the object's own x=4 pivot.
        Assert.Equal(3.5f, min.X, 3);
        Assert.Equal(4.5f, max.X, 3);
    }
}
