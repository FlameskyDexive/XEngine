// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.Core;
using XEngine.OrigamiUI.Gizmo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>
/// V-key vertex snapping: while translating, the selection is pulled so the gizmo center lands
/// on the mesh vertex of an unselected object nearest to the cursor. Screen-space AABB prefilter
/// keeps the per-frame cost bounded; very dense meshes are stride-sampled.
/// </summary>
internal static class VertexSnap
{
    private const int MaxSampledVertices = 20000;

    /// <summary>Nearest world-space vertex to the cursor among unselected meshes, within
    /// <paramref name="maxScreenDist"/> pixels; null when nothing qualifies.</summary>
    public static Float3? FindNearestVertexToCursor(Scene scene, Camera camera, Float2 cursor,
        Float2 panelSize, float maxScreenDist)
    {
        var viewport = new Rect(0, 0, panelSize.X, panelSize.Y);
        Float4x4 vp = camera.ProjectionMatrix * camera.ViewMatrix;
        float bestD2 = maxScreenDist * maxScreenDist;
        Float3? best = null;

        foreach (var go in scene.ActiveObjects)
        {
            if (go.HideFlags.HasFlag(HideFlags.Hide)) continue;
            if (IsInSelection(go)) continue;

            var mr = go.GetComponent<MeshRenderer>();
            Mesh? mesh = mr != null && mr.EnabledInHierarchy ? mr.Mesh.Res : null;
            if (mesh == null || mesh.VertexCount == 0) continue;

            // Screen-space AABB prefilter: skip the whole mesh when the cursor isn't near its
            // projected bounds (expanded by the snap radius).
            var wb = mesh.bounds.TransformBy(go.Transform.LocalToWorldMatrix);
            if (!CursorNearProjectedBounds(wb.Min, wb.Max, viewport, vp, cursor, maxScreenDist))
                continue;

            var verts = mesh.Vertices;
            var l2w = go.Transform.LocalToWorldMatrix;
            int step = Math.Max(1, verts.Length / MaxSampledVertices);
            for (int i = 0; i < verts.Length; i += step)
            {
                Float3 world = Float4x4.TransformPoint(verts[i], l2w);
                if (GizmoUtils.WorldToScreen(viewport, vp, world) is not { } s) continue;
                float dx = s.X - cursor.X, dy = s.Y - cursor.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = world;
                }
            }
        }
        return best;
    }

    private static bool CursorNearProjectedBounds(Float3 bMin, Float3 bMax, Rect viewport,
        Float4x4 vp, Float2 cursor, float margin)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        bool any = false;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Float3(
                (i & 1) == 0 ? bMin.X : bMax.X,
                (i & 2) == 0 ? bMin.Y : bMax.Y,
                (i & 4) == 0 ? bMin.Z : bMax.Z);
            if (GizmoUtils.WorldToScreen(viewport, vp, corner) is not { } s) continue;
            minX = MathF.Min(minX, s.X); maxX = MathF.Max(maxX, s.X);
            minY = MathF.Min(minY, s.Y); maxY = MathF.Max(maxY, s.Y);
            any = true;
        }
        if (!any) return false;
        return cursor.X >= minX - margin && cursor.X <= maxX + margin
            && cursor.Y >= minY - margin && cursor.Y <= maxY + margin;
    }

    private static bool IsInSelection(GameObject go)
    {
        for (var p = go; p != null && p.IsValid(); p = p.Parent)
            if (Selection.IsSelected(p))
                return true;
        return false;
    }
}
