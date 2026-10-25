// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Vector;

using Gizmo = XEngine.OrigamiUI.Gizmo;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>
/// Immediate-mode transform handles for custom scene-view editors. Wraps Origami's
/// <see cref="Gizmo.TransformGizmo"/> so an editor can manipulate an arbitrary world point/transform
/// (not just a GameObject): e.g. a light probe, a spline knot, a bounds corner.
///
/// Call a handle method from <see cref="ISceneViewEditor.OnSceneInput"/> each frame (it reads mouse +
/// modifier state from <see cref="Input"/> internally and applies the drag to the <c>ref</c> values),
/// then call <see cref="Draw"/> from <see cref="ISceneViewEditor.DrawOverlay"/> to render the handles
/// touched this frame. Each distinct <paramref name="id"/> keeps its own drag state across frames.
/// </summary>
public static class Handles
{
    private static readonly Dictionary<string, Gizmo.TransformGizmo> _gizmos = new();
    private static readonly Dictionary<string, Gizmo.TransformGizmoMode> _modes = new();
    private static readonly List<Gizmo.TransformGizmo> _pending = new();

    /// <summary>A 3-axis translation handle at <paramref name="position"/>. Returns true if it moved the
    /// point this frame; <paramref name="hot"/> is true while the handle is hovered or being dragged
    /// (callers should treat input as consumed and skip their own picking when hot).</summary>
    public static bool PositionHandle(string id, Camera camera, Rect viewport, Ray mouseRay, Float2 mousePos,
                                      ref Float3 position, out bool hot)
    {
        Quaternion rot = Quaternion.Identity;
        Float3 scale = Float3.One;
        return DoTransform(id, Gizmo.TransformGizmoMode.Translate, camera, viewport, mouseRay, mousePos,
                           ref position, ref rot, ref scale, out hot);
    }

    /// <summary>A rotation handle pivoted at <paramref name="pivot"/>. Returns true if it rotated this frame.</summary>
    public static bool RotationHandle(string id, Camera camera, Rect viewport, Ray mouseRay, Float2 mousePos,
                                      Float3 pivot, ref Quaternion rotation, out bool hot)
    {
        Float3 scale = Float3.One;
        return DoTransform(id, Gizmo.TransformGizmoMode.Rotate, camera, viewport, mouseRay, mousePos,
                           ref pivot, ref rotation, ref scale, out hot);
    }

    /// <summary>A full translate/rotate/scale handle. <paramref name="mode"/> selects which axes/planes show.</summary>
    public static bool TransformHandle(string id, Gizmo.TransformGizmoMode mode, Camera camera, Rect viewport,
                                       Ray mouseRay, Float2 mousePos,
                                       ref Float3 position, ref Quaternion rotation, ref Float3 scale, out bool hot)
        => DoTransform(id, mode, camera, viewport, mouseRay, mousePos, ref position, ref rotation, ref scale, out hot);

    /// <summary>Draw every handle driven this frame, then clear the pending set. Call from <c>DrawOverlay</c>.</summary>
    public static void Draw(Canvas canvas)
    {
        foreach (var g in _pending) g.Draw(canvas);
        _pending.Clear();
    }

    /// <summary>Forget a handle's cached gizmo + drag state (e.g. when its target is deleted).</summary>
    public static void Forget(string id)
    {
        _gizmos.Remove(id);
        _modes.Remove(id);
        if (_activeScalarDrag == id) _activeScalarDrag = null;
    }

    // ── Scalar handles (★ M10) ──────────────────────────────────────────

    /// <summary>Id of the scalar handle currently being dragged; only one can be active at a time.</summary>
    private static string? _activeScalarDrag;

    /// <summary>World size that keeps a handle roughly screen-constant at <paramref name="worldPos"/>.</summary>
    public static float HandleSize(Camera? camera, Float3 worldPos, float factor = 0.04f)
    {
        var camGo = camera?.GameObject;
        if (camGo == null) return factor;
        float dist = Float3.Distance(camGo.Transform.Position, worldPos);
        float size = dist * factor;
        return size < 0.01f ? 0.01f : size;
    }

    /// <summary>
    /// A dot handle that slides along <paramref name="axis"/> from <paramref name="center"/>, driving a
    /// scalar (a light range, a cone rim, a collider extent). Returns true when the value changed this
    /// frame; <paramref name="hot"/> is true while hovered or dragging so the caller can consume input.
    /// </summary>
    public static bool RadiusHandle(string id, Camera? camera, Ray mouseRay, Float3 center, Float3 axis,
                                    ref float value, out bool hot, float min = 0.001f)
    {
        hot = false;
        if (camera?.GameObject == null) return false;

        Float3 dir = Float3.Normalize(axis);
        Float3 handlePos = center + dir * value;
        float size = HandleSize(camera, handlePos);
        bool block = Input.GetMouseButton(1) || Input.GetMouseButton(2); // camera fly / pan owns the mouse
        bool hovered = !block && HandleMath.DistanceToRay(handlePos, mouseRay) <= size * 1.6f;

        if (_activeScalarDrag == null && hovered && Input.GetMouseButtonDown(0))
            _activeScalarDrag = id;
        bool dragging = _activeScalarDrag == id;
        hot = hovered || dragging;

        Debug.DrawWireSphere(handlePos, size, dragging ? Color.Yellow : hovered ? Color.White : Color.Gray, 8);

        bool changed = false;
        if (dragging)
        {
            float next = HandleMath.RadiusFromRay(center, dir, mouseRay, min);
            if (MathF.Abs(next - value) > 1e-5f)
            {
                value = next;
                changed = true;
            }
            if (Input.GetMouseButtonUp(0)) _activeScalarDrag = null;
        }
        return changed;
    }

    /// <summary>True while any scalar handle is mid-drag (callers keep their Undo snapshot armed).</summary>
    public static bool IsScalarDragging => _activeScalarDrag != null;

    private static bool DoTransform(string id, Gizmo.TransformGizmoMode mode, Camera camera, Rect viewport,
                                    Ray mouseRay, Float2 mousePos,
                                    ref Float3 position, ref Quaternion rotation, ref Float3 scale, out bool hot)
    {
        hot = false;
        var camGo = camera?.GameObject;
        if (camGo == null) return false;

        if (!_gizmos.TryGetValue(id, out var g))
        {
            g = new Gizmo.TransformGizmo(mode);
            _gizmos[id] = g;
            _modes[id] = mode;
        }
        else if (_modes[id] != mode)
        {
            // SetMode rebuilds the sub-gizmos (dropping drag state), so only call it on a real change.
            g.SetMode(mode);
            _modes[id] = mode;
        }

        g.UpdateCamera(viewport, camera.ViewMatrix, camera.ProjectionMatrix,
            camGo.Transform.Up, camGo.Transform.Forward, camGo.Transform.Right, camGo.Transform.Position);
        g.SetTransform(position, rotation, scale);

        g.Snapping = Input.GetKey(KeyCode.ControlLeft) || Input.GetKey(KeyCode.ControlRight);
        g.IsShiftDown = Input.GetKey(KeyCode.ShiftLeft) || Input.GetKey(KeyCode.ShiftRight);
        g.IsMouseDown = Input.GetMouseButtonDown(0);
        g.IsMouseUp = Input.GetMouseButtonUp(0);
        bool block = Input.GetMouseButton(1) || Input.GetMouseButton(2); // don't pick while flying the camera

        var result = g.Update(mouseRay, mousePos, block);
        _pending.Add(g);
        hot = g.IsOver;

        if (!result.HasValue) return false;
        var r = result.Value;
        bool changed = false;
        if (r.TranslationDelta.HasValue) { position += r.TranslationDelta.Value; changed = true; }
        if (r.RotationDelta.HasValue && r.RotationAxis.HasValue)
        {
            rotation = Quaternion.AxisAngle(r.RotationAxis.Value, r.RotationDelta.Value) * rotation;
            changed = true;
        }
        if (r.ScaleDelta.HasValue) { scale *= r.ScaleDelta.Value; changed = true; }
        return changed;
    }
}
