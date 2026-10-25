// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

public abstract class ImageEffect
{
    /// <summary>
    /// When false, the effect is skipped by the render pipeline.
    /// </summary>
    public bool Enabled = true;

    /// <summary>
    /// When false, volume-stack helpers on this effect use the instance fields only.
    /// The optional post-processing package sets this so a <c>PostProcessLayer</c> is not
    /// overwritten by a scene <see cref="Volume"/>.
    /// </summary>
    public bool UseVolumeOverrides = true;

    /// <summary>
    /// Defines at which stage of the rendering pipeline this effect should run.
    /// AfterOpaques: runs after opaque geometry, before transparents (GTAO, SSR).
    /// PostProcess: runs after all rendering (tonemapping, bloom, FXAA).
    /// </summary>
    public virtual RenderStage Stage => RenderStage.PostProcess;

    /// <summary>Buffers this effect actually consumes from the unified camera prepass.</summary>
    public virtual RenderFeatureRequirements Requirements => RenderFeatureRequirements.None;

    /// <summary>
    /// Whether this effect transforms HDR to LDR. Used for tonemapping effects.
    /// </summary>
    public virtual bool TransformsToLDR { get; } = false;

    /// <summary>
    /// Called during rendering with access to render targets.
    /// </summary>
    public virtual void OnRenderEffect(RenderContext context) { }

    /// <summary>Called after all rendering is complete for this camera.</summary>
    public virtual void OnPostRender(Camera camera) { }

    /// <summary>Called before culling for this camera.</summary>
    public virtual void OnPreCull(Camera camera) { }

    /// <summary>Called before rendering starts for this camera.</summary>
    public virtual void OnPreRender(Camera camera) { }

    /// <summary>
    /// Called when the effect transitions from active to inactive (Enabled = false,
    /// or removed from Camera.Effects). Override to release GPU resources such as
    /// materials, persistent RenderTextures, or shader-program handles.
    /// </summary>
    public virtual void OnDisable() { }
}

public enum CameraClearFlags
{
    Nothing,
    SolidColor,
    Depth,
    Skybox,
}

public enum CameraRenderType
{
    Base,
    Overlay,
}

[AddComponentMenu("Rendering/Camera")]
[ComponentIcon("\uf030")] // Camera
[GenerateSerializer]
public partial class Camera : MonoBehaviour
{
    // A render invocation may view a scene without owning an object in its hierarchy.
    [SerializeIgnore] internal Scene? RenderSceneOverride;
    /// <summary>Scene being rendered during a pipeline invocation, otherwise the owning scene.
    /// Effects and render extensions should use this instead of GameObject.Scene.</summary>
    public Scene? RenderScene => RenderSceneOverride ?? Scene;

    public List<ImageEffect> Effects = [];

    public CameraClearFlags ClearFlags = CameraClearFlags.Skybox;
    public Color ClearColor = new(0f, 0f, 0f, 1f);
    public LayerMask CullingMask = LayerMask.Everything;

    public enum ProjectionType { Perspective, Orthographic }
    public ProjectionType ProjectionMode = ProjectionType.Perspective;

    public float FieldOfView = 60f;
    public float OrthographicSize = 0.5f;
    public float NearClipPlane = 0.1f;
    public float FarClipPlane = 100f;
    /// <summary>
    /// Normalized camera viewport on the target (Min/Max in 0–1). Default (0,0)–(1,1) is full-frame.
    /// Size drives <see cref="PixelWidth"/> / <see cref="PixelHeight"/>; destination blit uses
    /// <see cref="PixelRectX"/> / <see cref="PixelRectY"/> / <see cref="PixelRectWidth"/> /
    /// <see cref="PixelRectHeight"/> so split-screen cameras do not overwrite each other.
    /// </summary>
    public Rect Viewrect = new(0, 0, 1, 1);
    public int Depth = -1;
    /// <summary>
    /// Which display this camera renders to, 0-based (shown as "Display 1" and up). The editor's Game
    /// view renders one display at a time; a player build only ever presents display 0 today.
    /// </summary>
    public int TargetDisplay = 0;
    /// <summary>
    /// Base cameras render the scene; Overlay cameras composite on a Base's shared color
    /// when listed in that Base's <see cref="CameraStack"/>.
    /// </summary>
    public CameraRenderType RenderType = CameraRenderType.Base;
    /// <summary>Overlay cameras drawn after this Base, in list order. Ignored on Overlay cameras.</summary>
    public List<Camera> CameraStack = [];

    public RenderPipeline? Pipeline;
    public RenderTexture? Target;
    public bool HDR = false;
    public float RenderScale = 1.0f;

    public bool IsOrthographic => ProjectionMode == ProjectionType.Orthographic;

    private float _aspect;
    private bool _customAspect;

    // The projection matrix used for rendering. May be jittered by TAA.
    private Float4x4 _projectionMatrix;
    private bool _customProjectionMatrix;

    // The unjittered projection matrix. Always reflects the clean base projection.
    // TAA sets ProjectionMatrix (jittered) while this stays clean.
    private Float4x4 _nonJitteredProjectionMatrix;
    private bool _customNonJitteredProjectionMatrix;

    // Previous frame state, written by the render pipeline at end of frame.
    private Float4x4 _previousViewProjectionMatrix;
    private bool _hasPreviousViewProjectionMatrix;

    // Image effects that were considered active on the previous render tick for this
    // camera. Compared against the current list each frame to fire OnDisable() on
    // anything that's been disabled, removed, or hot-swapped out.
    [SerializeIgnore]
    private readonly HashSet<ImageEffect> _lastActiveEffects = new();

    public uint PixelWidth { get; private set; }
    public uint PixelHeight { get; private set; }

    /// <summary>Target-pixel origin of this camera's viewrect (unscaled display pixels).</summary>
    public int PixelRectX { get; private set; }
    public int PixelRectY { get; private set; }
    public uint PixelRectWidth { get; private set; }
    public uint PixelRectHeight { get; private set; }

    public float Aspect
    {
        get => _aspect;
        set
        {
            _aspect = value;
            _customAspect = true;
        }
    }

    /// <summary>
    /// The projection matrix used for rendering. May include jitter from TAA.
    /// Setting this overrides the auto-computed projection from FOV/aspect/clip planes.
    /// Also updates NonJitteredProjectionMatrix unless it was explicitly set.
    /// </summary>
    public Float4x4 ProjectionMatrix
    {
        get => _projectionMatrix;
        set
        {
            _projectionMatrix = value;
            _customProjectionMatrix = true;
        }
    }

    /// <summary>
    /// The unjittered projection matrix. Used by the render pipeline for motion vectors
    /// and other operations that need a stable, non-jittered projection.
    /// If not explicitly set, returns the base projection computed from FOV/aspect/clip planes.
    /// TAA effects should set ProjectionMatrix (jittered) while leaving this at the clean value,
    /// or set this explicitly before applying jitter to ProjectionMatrix.
    /// </summary>
    public Float4x4 NonJitteredProjectionMatrix
    {
        get => _nonJitteredProjectionMatrix;
        set
        {
            _nonJitteredProjectionMatrix = value;
            _customNonJitteredProjectionMatrix = true;
        }
    }

    public Float4x4 ViewMatrix { get; private set; }

    /// <summary>
    /// The previous frame's unjittered view-projection matrix.
    /// Set by the render pipeline at the end of each frame. Used for motion vectors
    /// and temporal reprojection. Returns identity on the first frame.
    /// </summary>
    public Float4x4 PreviousViewProjectionMatrix => _previousViewProjectionMatrix;

    /// <summary>
    /// Whether a valid previous view-projection matrix exists (false on the first frame).
    /// </summary>
    public bool HasPreviousViewProjectionMatrix => _hasPreviousViewProjectionMatrix;

    public override void OnEnable()
    {
        _hasPreviousViewProjectionMatrix = false;
    }

    /// <summary>
    /// Fire OnDisable on every image effect we were rendering, because the camera
    /// itself is going away (disabled or destroyed). Render pipelines never get
    /// another chance to do this for us, so it has to happen here.
    /// </summary>
    public override void OnDisable()
    {
        foreach (var effect in _lastActiveEffects)
        {
            if (effect == null) continue;
            try { effect.OnDisable(); }
            catch (Exception e) { Debug.LogError($"ImageEffect.OnDisable threw: {e}"); }
        }
        _lastActiveEffects.Clear();
    }

    /// <summary>
    /// Called once per render tick by the render pipeline. Fires OnDisable on any
    /// effect that was active last frame but isn't in <paramref name="currentlyActive"/>
    /// this frame covers disabled, removed, and hot-swapped effects. Pipelines
    /// don't need to track this themselves; they just pass in whatever they're about
    /// to render.
    /// </summary>
    public void UpdateImageEffectLifecycle(IReadOnlyList<ImageEffect> currentlyActive)
    {
        foreach (ImageEffect previous in _lastActiveEffects)
        {
            bool stillActive = false;
            for (int i = 0; i < currentlyActive.Count; i++)
            {
                if (ReferenceEquals(previous, currentlyActive[i]))
                {
                    stillActive = true;
                    break;
                }
            }
            if (previous == null || stillActive) continue;
            try { previous.OnDisable(); }
            catch (Exception e) { Debug.LogError($"ImageEffect.OnDisable threw: {e}"); }
        }

        _lastActiveEffects.Clear();
        for (int i = 0; i < currentlyActive.Count; i++)
            if (currentlyActive[i] != null)
                _lastActiveEffects.Add(currentlyActive[i]);
    }

    /// <summary>
    /// Icon only while unselected. The frustum spans near to far plane — 110 world units for the
    /// default clip range — so drawing it for every camera all the time buries scenes whose content
    /// is a few units across. Unity shows the icon unselected and the frustum on selection.
    /// </summary>
    public override void DrawGizmos()
    {
        if (GameObject.HideFlags.HasFlag(HideFlags.NoGizmos)) return;

        var icon = Resources.Texture2D.LoadDefault(Resources.DefaultTexture.IconCamera);
        if (icon != null) Debug.DrawIcon(icon, Transform.Position, 0.5f, Color.White);
    }

    /// <summary>The frustum wireframe, drawn on top of the icon <see cref="DrawGizmos"/> already placed.</summary>
    public override void DrawGizmosSelected()
    {
        if (GameObject.HideFlags.HasFlag(HideFlags.NoGizmos)) return;

        float aspect = _aspect > 0.05f ? _aspect : 16f / 9f;
        Float4x4 viewProjectionMatrix = GetProjectionMatrix(aspect) * GetViewMatrix();

        Frustum frustum = Frustum.FromMatrix(viewProjectionMatrix);
        var corners = frustum.GetCorners();

        // Corner indices from GetCorners():
        // 0: Near-Left-Bottom,  1: Near-Right-Bottom,  2: Near-Left-Top,  3: Near-Right-Top
        // 4: Far-Left-Bottom,   5: Far-Right-Bottom,   6: Far-Left-Top,   7: Far-Right-Top

        Debug.DrawLine(corners[0], corners[1], Color.White);
        Debug.DrawLine(corners[1], corners[3], Color.White);
        Debug.DrawLine(corners[3], corners[2], Color.White);
        Debug.DrawLine(corners[2], corners[0], Color.White);

        Debug.DrawLine(corners[4], corners[5], Color.White);
        Debug.DrawLine(corners[5], corners[7], Color.White);
        Debug.DrawLine(corners[7], corners[6], Color.White);
        Debug.DrawLine(corners[6], corners[4], Color.White);

        Debug.DrawLine(corners[0], corners[4], Color.White);
        Debug.DrawLine(corners[1], corners[5], Color.White);
        Debug.DrawLine(corners[2], corners[6], Color.White);
        Debug.DrawLine(corners[3], corners[7], Color.White);
    }

    public void Render(in RenderingData? data = null)
    {
        RenderPipeline pipeline = Pipeline ?? UniversalRenderPipeline.Default;
        pipeline.Render(this, data ?? new());
    }

    /// <summary>
    /// Clamp a normalized viewrect to 0–1 and keep a non-zero size so pixel math cannot collapse.
    /// </summary>
    public static Rect ClampViewrect(Rect viewrect)
    {
        float minX = Maths.Clamp(viewrect.Min.X, 0f, 1f);
        float minY = Maths.Clamp(viewrect.Min.Y, 0f, 1f);
        float maxX = Maths.Clamp(viewrect.Max.X, 0f, 1f);
        float maxY = Maths.Clamp(viewrect.Max.Y, 0f, 1f);
        if (maxX - minX < 1e-4f)
            maxX = Maths.Min(1f, minX + 1e-4f);
        if (maxY - minY < 1e-4f)
            maxY = Maths.Min(1f, minY + 1e-4f);
        return new Rect(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Map a normalized viewrect onto a target size. <paramref name="pixelWidth"/> /
    /// <paramref name="pixelHeight"/> are the scaled internal RT size; pixel-rect values are
    /// the unscaled destination on the target (final blit / overlay UI).
    /// </summary>
    public static void ComputeViewrectPixels(
        Rect viewrect,
        int targetWidth,
        int targetHeight,
        float renderScale,
        out int pixelRectX,
        out int pixelRectY,
        out uint pixelRectWidth,
        out uint pixelRectHeight,
        out uint pixelWidth,
        out uint pixelHeight)
    {
        if (targetWidth < 1) targetWidth = 1;
        if (targetHeight < 1) targetHeight = 1;
        renderScale = Maths.Clamp(renderScale, 0.1f, 2f);
        Rect clamped = ClampViewrect(viewrect);
        Float2 size = clamped.Size;

        pixelRectX = (int)(clamped.Min.X * targetWidth);
        pixelRectY = (int)(clamped.Min.Y * targetHeight);
        int rectW = (int)(size.X * targetWidth + 0.5f);
        int rectH = (int)(size.Y * targetHeight + 0.5f);
        if (rectW < 1) rectW = 1;
        if (rectH < 1) rectH = 1;
        if (pixelRectX + rectW > targetWidth)
            rectW = targetWidth - pixelRectX;
        if (pixelRectY + rectH > targetHeight)
            rectH = targetHeight - pixelRectY;
        if (rectW < 1) rectW = 1;
        if (rectH < 1) rectH = 1;
        pixelRectWidth = (uint)rectW;
        pixelRectHeight = (uint)rectH;

        pixelWidth = (uint)Maths.Max(1, (int)(rectW * renderScale));
        pixelHeight = (uint)Maths.Max(1, (int)(rectH * renderScale));
    }

    public RenderTexture? UpdateRenderData()
    {
        // Since Scene Updating is guranteed to execute before rendering, we can setup camera data for this frame here
        RenderTexture? camTarget = null;

        if (Target.IsValid())
            camTarget = Target;

        int width = camTarget?.Width ?? Window.InternalWindow.FramebufferSize.X;
        int height = camTarget?.Height ?? Window.InternalWindow.FramebufferSize.Y;

        Viewrect = ClampViewrect(Viewrect);
        ComputeViewrectPixels(
            Viewrect,
            width,
            height,
            RenderScale,
            out int pixelRectX,
            out int pixelRectY,
            out uint pixelRectWidth,
            out uint pixelRectHeight,
            out uint pixelWidth,
            out uint pixelHeight);
        PixelRectX = pixelRectX;
        PixelRectY = pixelRectY;
        PixelRectWidth = pixelRectWidth;
        PixelRectHeight = pixelRectHeight;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;

        if (!_customAspect)
            _aspect = PixelWidth / (float)PixelHeight;

        // Recompute the base projection unless the user set it manually
        if (!_customProjectionMatrix)
            _projectionMatrix = GetProjectionMatrix(_aspect);

        // Keep the non-jittered projection in sync unless explicitly overridden
        if (!_customNonJitteredProjectionMatrix)
            _nonJitteredProjectionMatrix = GetProjectionMatrix(_aspect);

        ViewMatrix = Float4x4.CreateLookTo(Transform.Position, Transform.Forward, Transform.Up);

        return camTarget;
    }

    /// <summary>
    /// Order cameras for <see cref="Resources.Scene.Render"/>: Base cameras by
    /// <see cref="Depth"/>, each followed by its <see cref="CameraStack"/> overlays.
    /// Overlay cameras not listed on a Base are skipped. Default (all Base, empty stacks)
    /// matches the previous depth-sorted list.
    /// </summary>
    public static void BuildRenderOrder(
        IReadOnlyList<Camera> collected,
        List<Camera> basesScratch,
        List<Camera> ordered,
        HashSet<Camera> usedOverlays)
    {
        basesScratch.Clear();
        ordered.Clear();
        usedOverlays.Clear();
        if (collected == null)
            return;

        for (int i = 0; i < collected.Count; i++)
        {
            Camera cam = collected[i];
            if (!IsRenderableBase(cam))
                continue;
            basesScratch.Add(cam);
        }

        basesScratch.Sort(static (a, b) => a.Depth.CompareTo(b.Depth));

        for (int b = 0; b < basesScratch.Count; b++)
        {
            Camera baseCam = basesScratch[b];
            ordered.Add(baseCam);
            List<Camera> stack = baseCam.CameraStack;
            if (stack == null)
                continue;
            for (int s = 0; s < stack.Count; s++)
            {
                Camera overlay = stack[s];
                if (!IsRenderableOverlay(overlay) || !usedOverlays.Add(overlay))
                    continue;
                ordered.Add(overlay);
            }
        }
    }

    public static bool IsRenderableBase(Camera? camera)
        => camera != null
            && !camera.IsDisposed
            && camera.RenderType == CameraRenderType.Base
            && camera.Enabled;

    public static bool IsRenderableOverlay(Camera? camera)
        => camera != null
            && !camera.IsDisposed
            && camera.RenderType == CameraRenderType.Overlay
            && camera.Enabled;

    /// <summary>
    /// Called by the render pipeline at end of frame to store the current unjittered
    /// view-projection matrix as the previous frame's matrix for next frame's motion vectors.
    /// </summary>
    public void SavePreviousViewProjectionMatrix()
    {
        _previousViewProjectionMatrix = _nonJitteredProjectionMatrix * ViewMatrix;
        _hasPreviousViewProjectionMatrix = true;
    }

    public void ResetAspect()
    {
        _aspect = PixelWidth / (float)PixelHeight;
        _customAspect = false;
    }

    /// <summary>
    /// Resets the projection matrix to the auto-computed value from FOV/aspect/clip planes.
    /// Also resets the non-jittered projection matrix.
    /// </summary>
    public void ResetProjectionMatrix()
    {
        _projectionMatrix = GetProjectionMatrix(_aspect);
        _customProjectionMatrix = false;
        _nonJitteredProjectionMatrix = _projectionMatrix;
        _customNonJitteredProjectionMatrix = false;
    }

    /// <summary>
    /// Resets the non-jittered projection matrix to the auto-computed value.
    /// </summary>
    public void ResetNonJitteredProjectionMatrix()
    {
        _nonJitteredProjectionMatrix = GetProjectionMatrix(_aspect);
        _customNonJitteredProjectionMatrix = false;
    }

    /// <summary>
    /// Clears the stored previous view-projection matrix, forcing the next frame
    /// to treat itself as the first frame (no temporal history).
    /// </summary>
    public void ResetMotionHistory()
    {
        _hasPreviousViewProjectionMatrix = false;
    }

    public Ray ScreenPointToRay(Float2 screenPoint, Float2 screenSize)
    {
        // Normalize screen coordinates to [-1, 1]
        Float2 ndc = new(
            (screenPoint.X / screenSize.X) * 2.0f - 1.0f,
            1.0f - (screenPoint.Y / screenSize.Y) * 2.0f
        );

        // Create the near and far points in NDC
        Float4 nearPointNDC = new(ndc.X, ndc.Y, 0.0f, 1.0f);
        Float4 farPointNDC = new(ndc.X, ndc.Y, 1.0f, 1.0f);

        // Calculate the inverse view-projection matrix using unjittered projection
        float aspect = screenSize.X / screenSize.Y;
        Float4x4 viewProjectionMatrix = GetProjectionMatrix(aspect) * GetViewMatrix();
        Float4x4 inverseViewProjectionMatrix = viewProjectionMatrix.Invert();

        // Unproject the near and far points to world space
        Float4 nearPointWorld = Float4x4.TransformPoint(nearPointNDC, inverseViewProjectionMatrix);
        Float4 farPointWorld = Float4x4.TransformPoint(farPointNDC, inverseViewProjectionMatrix);

        // Perform perspective divide
        nearPointWorld /= nearPointWorld.W;
        farPointWorld /= farPointWorld.W;

        // Create the ray
        Float3 rayOrigin = new(nearPointWorld.X, nearPointWorld.Y, nearPointWorld.Z);
        Float3 rayDirection = Float3.Normalize(new Float3(farPointWorld.X, farPointWorld.Y, farPointWorld.Z) - rayOrigin);

        return new Ray(rayOrigin, rayDirection);
    }

    public Float4x4 GetViewMatrix(bool applyPosition = true)
    {
        Float3 position = applyPosition ? Transform.Position : Float3.Zero;

        return Float4x4.CreateLookTo(position, Transform.Forward, Transform.Up);
    }

    private Float4x4 GetProjectionMatrix(float aspect)
    {
        if (FieldOfView <= 0)
            FieldOfView = 1f;
        if (FieldOfView >= 180)
            FieldOfView = 179f;

        Float4x4 proj;

        if (ProjectionMode == ProjectionType.Orthographic)
            // OrthographicSize is the vertical extent; widen it by the aspect so the view isn't
            // squashed horizontally on non-square targets.
            proj = Float4x4.CreateOrtho(OrthographicSize * MathF.Max(0.0001f, aspect), OrthographicSize,
                NearClipPlane, FarClipPlane);
        else
            proj = Float4x4.CreatePerspectiveFov(Maths.ToRadians(FieldOfView), aspect, NearClipPlane, FarClipPlane);

        return proj;
    }
}
