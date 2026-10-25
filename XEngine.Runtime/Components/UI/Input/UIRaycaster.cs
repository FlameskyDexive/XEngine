// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Runtime.UI;

public static class UIRaycaster
{
    /// <summary>Result of a successful pointer hit.</summary>
    public readonly struct Hit
    {
        public readonly GameObject GameObject;
        public readonly GameCanvas Canvas;
        /// <summary>Pointer position in the hit canvas's design-pixel space (+Y up, origin bottom-left).</summary>
        public readonly Float2 DesignPosition;

        public Hit(GameObject go, GameCanvas canvas, Float2 designPos)
        {
            GameObject = go;
            Canvas = canvas;
            DesignPosition = designPos;
        }
    }

    public static bool TryPick(Scene? scene, Float2 screenPos, Float2 windowSize, out Hit hit)
    {
        hit = default;
        if (scene == null) return false;

        GameObject? bestGO = null;
        GameCanvas? bestCanvas = null;
        Float2 bestDesign = Float2.Zero;
        int bestSortOrder = int.MinValue;
        long bestLocalSortKey = long.MinValue;

        foreach (GameCanvas canvas in CanvasRegistry.GetCanvases(scene))
        {
            if (!canvas.EnabledInHierarchy) continue;

            // Screen-space canvases only in this pass - World canvases are cast against a camera ray
            // in TryPickWorld below, and only when nothing screen-space is hit (overlay UI composites
            // on top of the 3D scene, so it wins the pointer).
            if (canvas.RenderMode == RenderMode.WorldSpace) continue;

            canvas.RebuildIfDirty();

            if (!ScreenToDesign(canvas, screenPos, windowSize, out Float2 designPt)) continue;

            // Overlay/screen projects orthographically: cast a design-space ray straight into the screen.
            Float3 rayO = new Float3(designPt.X, designPt.Y, 1e6f);
            Float3 rayD = new Float3(0f, 0f, -1f);
            GameObject? localHit = null;
            long localSortKey = long.MinValue;
            TryPickRegistered(canvas, designPt, rayO, rayD, ref localHit, ref localSortKey);
            if (localHit == null) continue;

            // Higher SortOrder wins; ties resolved by deeper DFS index (drawn on top).
            long localOrder = localSortKey != long.MinValue
                ? localSortKey
                : long.MinValue;
            bool wins = canvas.SortOrder > bestSortOrder
                || (canvas.SortOrder == bestSortOrder && localOrder > bestLocalSortKey);

            if (wins)
            {
                bestGO = localHit;
                bestCanvas = canvas;
                bestDesign = designPt;
                bestSortOrder = canvas.SortOrder;
                bestLocalSortKey = localOrder;
            }
        }

        // No screen-space hit -> fall back to world-space canvases via a 3D camera ray.
        if (bestGO == null)
            TryPickWorld(scene, screenPos, windowSize, ref bestGO, ref bestCanvas, ref bestDesign);

        if (bestGO == null || bestCanvas == null) return false;
        hit = new Hit(bestGO, bestCanvas, bestDesign);
        return true;
    }

    /// <summary>
    /// Pick for <see cref="RenderMode.WorldSpace"/> canvases: unproject the pointer through the
    /// scene's main camera, intersect each world canvas's plane, and keep the nearest canvas whose
    /// element sits under the resulting point. Only called when no screen-space canvas was hit.
    /// </summary>
    private static void TryPickWorld(Scene scene, Float2 screenPos, Float2 windowSize,
        ref GameObject? bestGO, ref GameCanvas? bestCanvas, ref Float2 bestDesign)
    {
        Camera? cam = ResolveMainCamera(scene);
        if (cam == null) return;

        Ray ray = cam.ScreenPointToRay(screenPos, windowSize);
        float bestT = float.MaxValue;

        foreach (GameCanvas canvas in CanvasRegistry.GetCanvases(scene))
        {
            if (!canvas.EnabledInHierarchy) continue;
            if (canvas.RenderMode != RenderMode.WorldSpace) continue;

            canvas.RebuildIfDirty();

            if (!WorldRayToDesign(canvas, ray, out Float2 designPt, out float t)) continue;
            if (t >= bestT) continue; // a nearer canvas already owns the pointer

            // The camera ray expressed in the canvas's design space, so element quads (BuildRectModel)
            // are tested in 3D and out-of-plane element rotation is respected.
            Float4x4 w2d = canvas.CanvasToWorld.Invert();
            Float3 rayO = Float4x4.TransformPoint(ray.Origin, w2d);
            Float3 rayD = Float4x4.TransformPoint(ray.Origin + ray.Direction, w2d) - rayO;
            long localSortKey = long.MinValue;
            GameObject? localHit = null;
            TryPickRegistered(canvas, designPt, rayO, rayD, ref localHit, ref localSortKey);
            if (localHit == null) continue;

            bestT = t;
            bestGO = localHit;
            bestCanvas = canvas;
            bestDesign = designPt;
        }
    }

    private static void TryPickRegistered(GameCanvas canvas, Float2 designPoint, Float3 rayOrigin,
        Float3 rayDirection, ref GameObject? best, ref long bestSortKey)
    {
        IReadOnlyList<UIBehaviour> candidates = UIRaycastRegistry.GetCandidates(canvas);
        // Candidates are kept in render order (back to front). The first eligible hit walking
        // backwards is therefore the visual top-most element; do not scan the rest of the canvas.
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            UIBehaviour owner = candidates[i];
            RectTransform? rt = owner?.GameObject.RectTransform;
            if (rt == null || !owner.EnabledInHierarchy || !owner.IsRaycastTarget)
                continue;
            if (!UIRaycastRegistry.TryGetState(owner, out long sortKey, out CanvasClipState clip,
                out bool blocksRaycasts, out Rect? raycastRect)
                || !blocksRaycasts)
                continue;
            if (!RayHitsRect(canvas.BuildRectModel(rt), rt, owner.RaycastPadding, rayOrigin, rayDirection, out _)) continue;
            if (raycastRect is { } coarse && !RectContainsPoint(coarse, designPoint)) continue;
            if (!PointInsideClip(canvas, clip, designPoint)) continue;
            best = owner.GameObject;
            bestSortKey = sortKey;
            return;
        }
    }

    private static bool PointInsideClip(GameCanvas canvas, CanvasClipState clip, Float2 designPoint)
    {
        if (!clip.HasRect) return true;
        Float3 worldPoint = Float4x4.TransformPoint(new Float3(designPoint.X, designPoint.Y, 0f), canvas.CanvasToWorld);
        Float3 local = Float4x4.TransformPoint(worldPoint, clip.ClipToLocal);
        return clip.ContainsLocalPoint(new Float2(local.X, local.Y));
    }

    private static bool RectContainsPoint(Rect rect, Float2 point)
        => point.X >= rect.Min.X && point.X <= rect.Max.X
        && point.Y >= rect.Min.Y && point.Y <= rect.Max.Y;

    /// <summary>The scene's primary rendering camera: the enabled camera with the highest Depth.</summary>
    private static Camera? ResolveMainCamera(Scene scene)
    {
        Camera? best = null;
        foreach (GameObject go in scene.ActiveObjects)
        {
            Camera? c = go.GetComponent<Camera>();
            if (c == null || !c.EnabledInHierarchy) continue;
            if (best == null || c.Depth > best.Depth) best = c;
        }
        return best;
    }

    /// <summary>
    /// Intersects a world-space ray with a world canvas's plane (the design-pixel Z=0 plane mapped
    /// through <see cref="GameCanvas.CanvasToWorld"/>) and returns the hit point in the canvas's
    /// design-pixel space plus the ray distance. False if the ray is parallel or hits behind the origin.
    /// </summary>
    private static bool WorldRayToDesign(GameCanvas canvas, Ray ray, out Float2 designPt, out float t)
    {
        designPt = Float2.Zero;

        Float4x4 toWorld = canvas.CanvasToWorld;
        Float3 p0 = Float4x4.TransformPoint(Float3.Zero, toWorld);
        Float3 p1 = Float4x4.TransformPoint(new Float3(1f, 0f, 0f), toWorld);
        Float3 p2 = Float4x4.TransformPoint(new Float3(0f, 1f, 0f), toWorld);
        Plane plane = new Plane(p0, p1, p2);

        if (!ray.Intersects(plane, out t) || t < 0f) return false;

        Float3 world = ray.Origin + ray.Direction * t;
        Float3 local = Float4x4.TransformPoint(world, toWorld.Invert());
        designPt = new Float2(local.X, local.Y);
        return true;
    }

    /// <summary>
    /// Projects a screen pointer into <paramref name="canvas"/>'s design space without requiring an
    /// element hit. Used to keep a drag tracking the pointer after it leaves every raycast target
    /// (otherwise the hit-test yields no design position and sliders/scrollbars snap to the origin).
    /// Returns false when the projection is undefined (degenerate scale, no camera, or a parallel ray).
    /// </summary>
    internal static bool TryProjectPointer(GameCanvas canvas, Float2 screenPos, Float2 windowSize, out Float2 designPt)
    {
        if (canvas.RenderMode == RenderMode.WorldSpace)
        {
            designPt = Float2.Zero;
            Scene? scene = canvas.GameObject.Scene;
            if (scene == null) return false;
            Camera? cam = ResolveMainCamera(scene);
            if (cam == null) return false;
            Ray ray = cam.ScreenPointToRay(screenPos, windowSize);
            return WorldRayToDesign(canvas, ray, out designPt, out _);
        }
        return ScreenToDesign(canvas, screenPos, windowSize, out designPt);
    }

    /// <summary>
    /// Maps a window/viewport pointer position into the canvas's design space (the same mapping
    /// pointer picking uses). Public so gameplay input (virtual pads, pointer gestures) can share
    /// the exact transform the UI event pipeline uses, including editor game-view viewports.
    /// </summary>
    public static bool ScreenToDesign(GameCanvas canvas, Float2 screenPos, Float2 windowSize, out Float2 designPt)
    {
        float scale = canvas.ScaleFactor;
        if (scale < 0.001f) { designPt = Float2.Zero; return false; }

        designPt = new Float2(screenPos.X / scale, (windowSize.Y - screenPos.Y) / scale);
        return true;
    }

    /// <summary>
    /// True if a ray hits the element's quad. <paramref name="model"/> maps the element's pivot-centered
    /// local space into the ray's space; the ray is intersected with the element's actual (possibly
    /// out-of-plane tilted) plane rather than a flat projection, so 3D rotation is respected. Runtime
    /// passes a canvas-design-space ray + <see cref="GameCanvas.BuildRectModel"/>; the editor passes a
    /// world-space ray + world model.
    /// </summary>
    public static bool RayHitsRect(Float4x4 model, RectTransform rt, Float3 rayOrigin, Float3 rayDir, out float t)
        => RayHitsRect(model, rt, ResolvePadding(rt), rayOrigin, rayDir, out t);

    public static bool RayHitsRect(Float4x4 model, RectTransform rt, Float4 padding, Float3 rayOrigin, Float3 rayDir, out float t)
    {
        t = 0f;
        Rect cr = rt.ComputedRect;
        if (cr.Size.X <= 0 || cr.Size.Y <= 0) return false;

        Float4x4 inv = model.Invert();
        Float3 lo = Float4x4.TransformPoint(rayOrigin, inv);
        Float3 ld = Float4x4.TransformPoint(rayOrigin + rayDir, inv) - lo;
        if (Maths.Abs(ld.Z) < 1e-9f) return false;

        t = -lo.Z / ld.Z;
        if (t < 0f) return false;

        Float3 lh = lo + ld * t;
        return GraphicRaycastPadding.ContainsLocalPoint(cr.Size, rt.Pivot, padding, new Float2(lh.X, lh.Y));
    }

    private static Float4 ResolvePadding(RectTransform rt)
    {
        Float4 padding = default;
        foreach (UIBehaviour ui in rt.GameObject.GetComponents<UIBehaviour>())
        {
            if (!ui.EnabledInHierarchy) continue;
            padding = ui.RaycastPadding;
            if (ui.IsGraphic) break;
        }
        return padding;
    }

    /// <summary>Ray-vs-element test in canvas design space (element model = <see cref="GameCanvas.BuildRectModel"/>).</summary>
    internal static bool RayHitsRect(GameCanvas canvas, RectTransform rt, Float3 rayOrigin, Float3 rayDir)
        => RayHitsRect(canvas.BuildRectModel(rt), rt, rayOrigin, rayDir, out _);

}
