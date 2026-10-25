// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.Core;
using XEngine.OrigamiUI.Gizmo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>
/// Scene-view marquee (rubber-band) selection. While a drag is active, every pickable object's
/// world AABB (renderer bounds; transform position for empties) is projected into panel space and
/// tested against the drag rectangle. Selection updates live: plain drag replaces, Shift adds to,
/// Ctrl subtracts from the selection captured at drag start. Buffers are reused across frames.
/// </summary>
internal sealed class RubberBandSelector
{
    private readonly List<GameObject> _baseSelection = new();
    private readonly List<GameObject> _hits = new();

    public bool Active { get; private set; }
    public Float2 Start { get; private set; }
    public Float2 End { get; private set; }

    public void Begin(Float2 localPos)
    {
        Active = true;
        Start = End = localPos;
        _baseSelection.Clear();
        foreach (var go in Selection.GetSelected<GameObject>())
            _baseSelection.Add(go);
    }

    public void Cancel() => Active = false;

    public void EndDrag() => Active = false;

    /// <summary>Advance the drag to <paramref name="localPos"/> and re-apply the live selection.</summary>
    public void Update(Scene scene, Camera camera, Float2 localPos, Float2 panelSize, bool ctrl, bool shift)
    {
        if (!Active) return;
        End = localPos;

        float minX = MathF.Min(Start.X, End.X), maxX = MathF.Max(Start.X, End.X);
        float minY = MathF.Min(Start.Y, End.Y), maxY = MathF.Max(Start.Y, End.Y);
        // A couple of pixels of drag is a click, not a marquee — don't clear the selection for it.
        if (maxX - minX < 3f && maxY - minY < 3f) return;

        var viewport = new Rect(0, 0, panelSize.X, panelSize.Y);
        Float4x4 viewProjection = camera.ProjectionMatrix * camera.ViewMatrix;

        _hits.Clear();
        foreach (var go in scene.ActiveObjects)
        {
            if (go.HideFlags.HasFlag(HideFlags.Hide)) continue;
            if (!SceneVisibilityManager.IsPickingEnabledInHierarchy(go)) continue;
            if (!TryGetScreenRect(go, viewport, viewProjection,
                    out float goMinX, out float goMinY, out float goMaxX, out float goMaxY))
                continue;
            if (goMaxX < minX || goMinX > maxX || goMaxY < minY || goMinY > maxY) continue;
            _hits.Add(go);
        }

        ApplySelection(ctrl, shift);
    }

    private void ApplySelection(bool ctrl, bool shift)
    {
        Selection.Clear();
        if (shift || ctrl)
        {
            // Shift: base ∪ hits. Ctrl: base ∖ hits.
            foreach (var go in _baseSelection)
            {
                bool hit = _hits.Contains(go);
                if (!ctrl || !hit)
                    Selection.AddToSelection(go);
            }
            if (shift)
                foreach (var go in _hits)
                    if (!_baseSelection.Contains(go))
                        Selection.AddToSelection(go);
        }
        else
        {
            foreach (var go in _hits)
                Selection.AddToSelection(go);
        }
    }

    /// <summary>
    /// Screen-space rect of the object's own world AABB (its renderers only, not children —
    /// each object stands for itself in a marquee). Objects with no renderable bounds project
    /// their transform position as a point. False when fully behind the camera.
    /// </summary>
    private static bool TryGetScreenRect(GameObject go, Rect viewport, Float4x4 viewProjection,
        out float minX, out float minY, out float maxX, out float maxY)
    {
        minX = minY = float.MaxValue;
        maxX = maxY = float.MinValue;

        Float3 bMin = default, bMax = default;
        bool hasBounds = false;

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null && mr.EnabledInHierarchy && mr.Mesh.Res != null)
        {
            var wb = mr.Mesh.Res.bounds.TransformBy(go.Transform.LocalToWorldMatrix);
            bMin = wb.Min; bMax = wb.Max; hasBounds = true;
        }
        else
        {
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.EnabledInHierarchy && smr.SharedMesh.Res != null)
            {
                var wb = smr.SharedMesh.Res.bounds.TransformBy(go.Transform.LocalToWorldMatrix);
                bMin = wb.Min; bMax = wb.Max; hasBounds = true;
            }
        }

        bool any = false;
        if (hasBounds)
        {
            for (int i = 0; i < 8; i++)
            {
                var corner = new Float3(
                    (i & 1) == 0 ? bMin.X : bMax.X,
                    (i & 2) == 0 ? bMin.Y : bMax.Y,
                    (i & 4) == 0 ? bMin.Z : bMax.Z);
                if (GizmoUtils.WorldToScreen(viewport, viewProjection, corner) is not { } s) continue;
                minX = MathF.Min(minX, s.X); maxX = MathF.Max(maxX, s.X);
                minY = MathF.Min(minY, s.Y); maxY = MathF.Max(maxY, s.Y);
                any = true;
            }
        }
        else if (GizmoUtils.WorldToScreen(viewport, viewProjection, go.Transform.Position) is { } p)
        {
            minX = maxX = p.X;
            minY = maxY = p.Y;
            any = true;
        }

        return any;
    }
}
