// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Core;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.GUI.SceneView.Editors;

/// <summary>
/// ★ M10 — Scene handles for the collider family: box half-extents on each axis, sphere radius,
/// capsule radius and height. Handles sit on the shape surface in the collider's own space and only
/// consume input while hot, so selection and the transform gizmo are unaffected.
/// </summary>
[SceneViewEditorFor(typeof(Collider))]
public class ColliderSceneEditor : ISceneViewEditor
{
    private const string XHandleId = "collider_x";
    private const string YHandleId = "collider_y";
    private const string ZHandleId = "collider_z";

    private Collider? _collider;
    private bool _dragging;

    public int Priority => 10;

    public void OnActivate(GameObject target) => _collider = target.GetComponent<Collider>();

    public void OnDeactivate()
    {
        _collider = null;
        _dragging = false;
        Handles.Forget(XHandleId);
        Handles.Forget(YHandleId);
        Handles.Forget(ZHandleId);
    }

    public bool DrawToolbar(Paper paper, string id, Scribe.FontFile font) => false;

    public bool OnSceneInput(Camera camera, Scene scene, Rect viewport, Ray mouseRay, Float2 mousePos, bool viewportHovered)
    {
        if (_collider == null || !viewportHovered) return false;

        Transform t = _collider.Transform;
        // Handles live in the collider's local frame (its own Rotation offset included), so a rotated
        // object drags its extents along the shape's axes rather than the world's.
        Quaternion rot = t.Rotation * Quaternion.FromEuler(_collider.Rotation);
        Float3 center = t.Position + rot * _collider.Center;
        Float3 axisX = rot * Float3.UnitX;
        Float3 axisY = rot * Float3.UnitY;
        Float3 axisZ = rot * Float3.UnitZ;
        Float3 camRight = camera?.GameObject?.Transform.Right ?? Float3.UnitX;

        bool changed = false, hot = false;

        switch (_collider)
        {
            case BoxCollider box:
            {
                Float3 half = box.Size * 0.5f;
                float hx = half.X, hy = half.Y, hz = half.Z;
                bool cx = Handles.RadiusHandle(XHandleId, camera, mouseRay, center, axisX, ref hx, out bool hotX, 0.005f);
                bool cy = Handles.RadiusHandle(YHandleId, camera, mouseRay, center, axisY, ref hy, out bool hotY, 0.005f);
                bool cz = Handles.RadiusHandle(ZHandleId, camera, mouseRay, center, axisZ, ref hz, out bool hotZ, 0.005f);
                if (cx || cy || cz)
                {
                    ArmUndo();
                    box.Size = new Float3(hx * 2f, hy * 2f, hz * 2f);
                }
                changed = cx || cy || cz;
                hot = hotX || hotY || hotZ;
                break;
            }
            case SphereCollider sphere:
            {
                float r = sphere.Radius;
                changed = Handles.RadiusHandle(XHandleId, camera, mouseRay, center, camRight, ref r, out hot, 0.005f);
                if (changed) { ArmUndo(); sphere.Radius = r; }
                break;
            }
            case CapsuleCollider capsule:
            {
                float r = capsule.Radius;
                bool cr = Handles.RadiusHandle(XHandleId, camera, mouseRay, center, camRight, ref r, out bool hotR, 0.005f);
                if (cr) { ArmUndo(); capsule.Radius = r; }

                float halfHeight = capsule.Height * 0.5f;
                bool ch = Handles.RadiusHandle(YHandleId, camera, mouseRay, center, axisY, ref halfHeight, out bool hotH, 0.005f);
                if (ch) { ArmUndo(); capsule.Height = halfHeight * 2f; }

                changed = cr || ch;
                hot = hotR || hotH;
                break;
            }
            default:
                return false; // Mesh / cone / cylinder colliders keep the plain gizmo for now.
        }

        if (!Handles.IsScalarDragging) _dragging = false;
        return hot || changed;
    }

    /// <summary>One Undo entry per drag: snapshot on the first change, then leave it armed.</summary>
    private void ArmUndo()
    {
        if (_dragging || _collider == null) return;
        _dragging = true;
        Undo.Snapshot(_collider);
    }

    public void DrawOverlay(Quill.Canvas canvas, Rect viewport) => Handles.Draw(canvas);
}
