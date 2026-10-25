// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// ★ M10 — Inspectors for the core components (Camera / Light / Collider / MeshRenderer).
// Each one draws the default reflected fields and then adds the checks and one-click fixes Unity
// offers for that component. The actual edits live in static helpers so they can be unit-tested and
// driven from the MCP REPL without a UI.

using System;

using XEngine.Editor.Core;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.GUI.CustomEditors;

/// <summary>Camera inspector: clipping-plane sanity plus a read-only frustum summary.</summary>
[CustomEditor(typeof(Camera))]
public class CameraEditor : CustomEditor
{
    public const float MinNearClip = 0.01f;

    /// <summary>True when the clip planes are unusable (non-positive near, or near at/behind far).</summary>
    public static bool HasInvalidClipPlanes(Camera camera)
        => camera.NearClipPlane < MinNearClip || camera.FarClipPlane <= camera.NearClipPlane;

    /// <summary>Clamps the clip planes back into a valid range. Returns true if anything changed.</summary>
    public static bool FixClipPlanes(Camera camera)
    {
        float near = camera.NearClipPlane;
        float far = camera.FarClipPlane;
        if (near < MinNearClip) near = MinNearClip;
        if (far <= near) far = near * 1000f;
        if (MathF.Abs(near - camera.NearClipPlane) < 1e-6f && MathF.Abs(far - camera.FarClipPlane) < 1e-6f)
            return false;
        camera.NearClipPlane = near;
        camera.FarClipPlane = far;
        return true;
    }

    /// <summary>Vertical extent of the frustum at <paramref name="distance"/> (ortho is distance-independent).</summary>
    public static float FrustumHeightAt(Camera camera, float distance)
        => camera.ProjectionMode == Camera.ProjectionType.Orthographic
            ? camera.OrthographicSize * 2f
            : 2f * distance * MathF.Tan(camera.FieldOfView * 0.5f * (MathF.PI / 180f));

    public override void OnGUI(Paper paper, string id, object target)
    {
        var camera = (Camera)target;
        Undo.SnapshotIfInteracting(camera, paper);
        DrawDefaultInspector(paper, id, camera);

        Origami.Separator(paper, id + "_cam_sep").Show();
        Origami.Header(paper, id + "_cam_hdr", "Frustum").Underline().Show();

        string projection = camera.ProjectionMode == Camera.ProjectionType.Orthographic
            ? "Orthographic — height " + (camera.OrthographicSize * 2f).ToString("F2")
            : "Perspective — " + camera.FieldOfView.ToString("F1") + "° vertical";
        Origami.Label(paper, id + "_cam_proj", projection).Show();

        float ratio = camera.FarClipPlane / MathF.Max(camera.NearClipPlane, 1e-6f);
        Origami.Label(paper, id + "_cam_clip",
            "Clip " + camera.NearClipPlane.ToString("F3") + " → " + camera.FarClipPlane.ToString("F1")
            + "  (ratio " + ratio.ToString("F0") + ":1)").Show();
        Origami.Label(paper, id + "_cam_h10",
            "View height at 10m: " + FrustumHeightAt(camera, 10f).ToString("F2")).Show();

        if (HasInvalidClipPlanes(camera))
        {
            Origami.Label(paper, id + "_cam_warn", "Invalid clip planes: near must be > 0 and less than far.").Show();
            Origami.Button(paper, id + "_cam_fix", "Fix Clip Planes", () =>
            {
                Undo.Snapshot(camera);
                FixClipPlanes(camera);
            }).Show();
        }
    }
}

/// <summary>Light inspector: keeps the spot cone angles consistent and points at the scene handles.</summary>
[CustomEditor(typeof(Light))]
public class LightEditor : CustomEditor
{
    /// <summary>Clamps a spot light's inner cone inside its outer cone. Returns true if it changed.</summary>
    public static bool ClampSpotAngles(SpotLight spot)
    {
        if (spot.InnerSpotAngle <= spot.SpotAngle) return false;
        spot.InnerSpotAngle = spot.SpotAngle;
        return true;
    }

    public override void OnGUI(Paper paper, string id, object target)
    {
        var light = (Light)target;
        Undo.SnapshotIfInteracting(light, paper);
        DrawDefaultInspector(paper, id, light);

        Origami.Separator(paper, id + "_light_sep").Show();

        switch (light)
        {
            case SpotLight spot:
                if (spot.InnerSpotAngle > spot.SpotAngle)
                {
                    Origami.Label(paper, id + "_light_warn", "Inner cone is wider than the outer cone.").Show();
                    Origami.Button(paper, id + "_light_fix", "Clamp Inner Angle", () =>
                    {
                        Undo.Snapshot(spot);
                        ClampSpotAngles(spot);
                    }).Show();
                }
                Origami.Label(paper, id + "_light_hint",
                    "Scene view: drag the range handle along the cone axis, or the rim handle to set the angle.").Show();
                break;
            case PointLight:
                Origami.Label(paper, id + "_light_hint", "Scene view: drag the range handle to resize the light.").Show();
                break;
            default:
                Origami.Label(paper, id + "_light_hint", "Directional light — rotation sets the direction.").Show();
                break;
        }
    }
}

/// <summary>Collider inspector: adds Unity's fit-the-shape-to-the-mesh one-click sizing.</summary>
[CustomEditor(typeof(Collider))]
public class ColliderEditor : CustomEditor
{
    /// <summary>
    /// Sizes the collider to the mesh on the same GameObject (Unity's default collider sizing).
    /// Returns false when there is no mesh to measure or the shape is not one we can fit.
    /// </summary>
    public static bool FitToMesh(Collider collider)
    {
        var renderer = collider.GameObject?.GetComponent<MeshRenderer>();
        var mesh = renderer?.Mesh.Res;
        if (mesh == null) return false;

        AABB bounds = mesh.bounds;
        Float3 size = bounds.Max - bounds.Min;
        Float3 center = (bounds.Max + bounds.Min) * 0.5f;
        if (size.X <= 0f && size.Y <= 0f && size.Z <= 0f) return false;

        switch (collider)
        {
            case BoxCollider box:
                box.Center = center;
                box.Size = size;
                return true;
            case SphereCollider sphere:
                sphere.Center = center;
                sphere.Radius = MathF.Max(MathF.Max(size.X, size.Y), size.Z) * 0.5f;
                return true;
            case CapsuleCollider capsule:
                capsule.Center = center;
                capsule.Radius = MathF.Max(size.X, size.Z) * 0.5f;
                capsule.Height = size.Y;
                return true;
            default:
                return false;
        }
    }

    public override void OnGUI(Paper paper, string id, object target)
    {
        var collider = (Collider)target;
        Undo.SnapshotIfInteracting(collider, paper);
        DrawDefaultInspector(paper, id, collider);

        Origami.Separator(paper, id + "_col_sep").Show();

        bool fittable = collider is BoxCollider or SphereCollider or CapsuleCollider;
        bool hasMesh = collider.GameObject?.GetComponent<MeshRenderer>()?.Mesh.Res != null;
        if (fittable)
        {
            Origami.Button(paper, id + "_col_fit", "Fit to Mesh", () =>
            {
                Undo.Snapshot(collider);
                FitToMesh(collider);
            }).Disabled(!hasMesh).Show();
            Origami.Label(paper, id + "_col_hint",
                "Scene view: drag the shape handles to resize.").Show();
        }
        else
        {
            Origami.Label(paper, id + "_col_hint", "This shape has no scene handles yet.").Show();
        }
    }
}

/// <summary>Mesh renderer inspector: flags a material-slot count that does not match the submeshes.</summary>
[CustomEditor(typeof(MeshRenderer))]
public class MeshRendererEditor : CustomEditor
{
    /// <summary>Submesh count of the assigned mesh, or 0 when there is no mesh.</summary>
    public static int SubmeshCount(MeshRenderer renderer)
    {
        var mesh = renderer.Mesh.Res;
        return mesh?.SubMeshCount ?? 0;
    }

    /// <summary>
    /// Grows or trims the material list to one slot per submesh, padding with the first material.
    /// Returns true when the list changed.
    /// </summary>
    public static bool MatchSubmeshCount(MeshRenderer renderer)
    {
        int want = SubmeshCount(renderer);
        if (want <= 0) return false;
        var list = renderer.Materials;
        if (list.Count == want) return false;

        while (list.Count > want) list.RemoveAt(list.Count - 1);
        while (list.Count < want) list.Add(list.Count > 0 ? list[0] : default);
        return true;
    }

    public override void OnGUI(Paper paper, string id, object target)
    {
        var renderer = (MeshRenderer)target;
        Undo.SnapshotIfInteracting(renderer, paper);
        DrawDefaultInspector(paper, id, renderer);

        Origami.Separator(paper, id + "_mr_sep").Show();
        Origami.Header(paper, id + "_mr_hdr", "Mesh").Underline().Show();

        var mesh = renderer.Mesh.Res;
        if (mesh == null)
        {
            Origami.Label(paper, id + "_mr_nomesh", "No mesh assigned.").Show();
            return;
        }

        int submeshes = SubmeshCount(renderer);
        AABB b = mesh.bounds;
        Float3 size = b.Max - b.Min;
        Origami.Label(paper, id + "_mr_stats",
            submeshes + " submesh(es) · " + renderer.Materials.Count + " material slot(s)").Show();
        Origami.Label(paper, id + "_mr_bounds",
            "Bounds " + size.X.ToString("F2") + " × " + size.Y.ToString("F2") + " × " + size.Z.ToString("F2")).Show();

        if (renderer.Materials.Count != submeshes)
        {
            Origami.Label(paper, id + "_mr_warn",
                renderer.Materials.Count < submeshes
                    ? "Fewer materials than submeshes — the extra submeshes fall back to the first material."
                    : "More materials than submeshes — the extra slots are unused.").Show();
            Origami.Button(paper, id + "_mr_match", "Match Submesh Count", () =>
            {
                Undo.Snapshot(renderer);
                MatchSubmeshCount(renderer);
            }).Show();
        }
    }
}
