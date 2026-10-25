// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Core;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.GUI.SceneView.Editors;

/// <summary>
/// ★ M10 — Scene handles for lights: drag a point light's range, and a spot light's range and cone
/// angle, straight in the viewport (Unity's light handles). Input is only consumed while a handle is
/// hot, so picking and the transform gizmo keep working the rest of the time.
/// </summary>
[SceneViewEditorFor(typeof(Light))]
public class LightSceneEditor : ISceneViewEditor
{
    private const string RangeHandleId = "light_range";
    private const string AngleHandleId = "light_angle";

    private Light? _light;
    private bool _dragging;

    public int Priority => 10;

    public void OnActivate(GameObject target) => _light = target.GetComponent<Light>();

    public void OnDeactivate()
    {
        _light = null;
        _dragging = false;
        Handles.Forget(RangeHandleId);
        Handles.Forget(AngleHandleId);
    }

    public bool DrawToolbar(Paper paper, string id, Scribe.FontFile font) => false;

    public bool OnSceneInput(Camera camera, Scene scene, Rect viewport, Ray mouseRay, Float2 mousePos, bool viewportHovered)
    {
        if (_light == null || !viewportHovered) return false;

        Transform t = _light.Transform;
        Float3 origin = t.Position;
        Float3 forward = t.Forward;
        Float3 right = camera?.GameObject?.Transform.Right ?? Float3.UnitX;

        bool changed = false, hot = false;

        if (_light is PointLight point)
        {
            float range = point.Range;
            changed = Handles.RadiusHandle(RangeHandleId, camera, mouseRay, origin, right, ref range, out hot, 0.01f);
            if (changed) { ArmUndo(); point.Range = range; }
            Debug.DrawWireSphere(origin, point.Range, point.Color, 24);
        }
        else if (_light is SpotLight spot)
        {
            float range = spot.Range;
            bool rangeChanged = Handles.RadiusHandle(RangeHandleId, camera, mouseRay, origin, forward, ref range, out bool rangeHot, 0.01f);
            if (rangeChanged) { ArmUndo(); spot.Range = range; }

            // The angle handle rides the cone rim: its distance from the cone axis is the rim radius.
            Float3 rimCenter = origin + forward * spot.Range;
            Float3 rimAxis = Float3.Normalize(right - forward * Float3.Dot(right, forward)); // perpendicular to the cone axis
            float rim = HandleMath.ConeRimRadius(spot.Range, spot.SpotAngle);
            bool rimChanged = Handles.RadiusHandle(AngleHandleId, camera, mouseRay, rimCenter, rimAxis, ref rim, out bool angleHot, 0.001f);
            if (rimChanged)
            {
                ArmUndo();
                spot.SpotAngle = HandleMath.ConeAngleFromRimRadius(spot.Range, rim);
                if (spot.InnerSpotAngle > spot.SpotAngle) spot.InnerSpotAngle = spot.SpotAngle;
            }

            changed = rangeChanged || rimChanged;
            hot = rangeHot || angleHot;
            Debug.DrawWireCone(origin, forward, HandleMath.ConeRimRadius(spot.Range, spot.SpotAngle), spot.Color, 24);
        }
        else
        {
            return false;
        }

        if (!Handles.IsScalarDragging) _dragging = false;
        return hot || changed;
    }

    /// <summary>One Undo entry per drag: snapshot on the first change, then leave it armed.</summary>
    private void ArmUndo()
    {
        if (_dragging || _light == null) return;
        _dragging = true;
        Undo.Snapshot(_light);
    }

    public void DrawOverlay(Quill.Canvas canvas, Rect viewport) => Handles.Draw(canvas);
}
