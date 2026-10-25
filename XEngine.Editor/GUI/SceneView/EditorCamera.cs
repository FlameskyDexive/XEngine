using System;
using System.Collections.Generic;

using XEngine.Editor.Core;
using XEngine.Editor.Theming;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>
/// Editor camera controller with orbit, pan, zoom, and FPS navigation modes.
/// Manages a hidden runtime Camera that renders the scene to a RenderTexture.
/// </summary>
/// <summary>
/// Cursor lock context for the scene view, locks to the center of the scene panel.
/// Paper-logical coordinates are converted to OS/winSize space using the same scale as
/// <c>EditorApplication.GetPaperMousePosition</c> (content scale × user scale).
/// </summary>
public class SceneViewLockContext : CursorLockContext
{
    /// <summary>Panel origin in Paper-logical coordinates.</summary>
    public Float2 PanelOrigin;
    /// <summary>Panel size in Paper-logical coordinates.</summary>
    public Float2 PanelSize;

    public override Int2 GetLockCenter()
    {
        var fb = Window.InternalWindow.FramebufferSize;
        var win = Window.InternalWindow.Size;
        float cs = Math.Max(0.01f, Window.ContentScale);
        float us = Math.Max(0.01f, EditorTheme.UserScale);
        float csFbWin = win.X > 0 ? (float)fb.X / win.X : 1f;
        // Inverse of paper = win * csFbWin / (cs * us).
        float scale = csFbWin > 0 ? (cs * us) / csFbWin : 1f;
        float centerX = (PanelOrigin.X + PanelSize.X / 2) * scale;
        float centerY = (PanelOrigin.Y + PanelSize.Y / 2) * scale;
        return new Int2((int)centerX, (int)centerY);
    }
}

public class EditorCamera : IDisposable
{
    private GameObject _cameraObject;
    private Camera _camera;
    private RenderTexture? _renderTarget;
    private uint _pendingTargetWidth;
    private uint _pendingTargetHeight;
    private double _pendingTargetSince;
    private SceneViewLockContext _lockContext = new();
    private bool _lookLocked;
    private readonly WeakReference<Scene> _renderScene = new(null!);

    // Camera state
    private Float3 _position = new Float3(0, 5, -15);
    private float _yaw = 0f;
    private float _pitch = 15f;
    private float _moveSpeed = 5f;
    private double _speedChangedTime;

    /// <summary>Current fly speed.</summary>
    public float MoveSpeed => _moveSpeed;

    /// <summary>Time (UnscaledTotalTime) when move speed last changed via scroll. Used for HUD indicators.</summary>
    public double SpeedChangedTime => _speedChangedTime;

    // Orbit distance and the pivot captured at the start of an orbit gesture.
    internal const float MinOrbitDistance = 0.1f;
    internal const float MaxOrbitDistance = 100_000f;
    internal const float MaxClipExtent = 1_000_000f;
    private float _orbitDistance = 10f;
    private Float3 _orbitPivot;
    private bool _wasOrbiting;

    private int _poseVersion;

    /// <summary>Bumps whenever the editor camera moves, so a Scene view that caches its RT knows to redraw.</summary>
    public int PoseVersion => _poseVersion;
    public bool ShowGizmos { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    /// <summary>Scene draw mode forwarded into <see cref="RenderingData.DebugView"/>.</summary>
    public DebugViewMode DebugView { get; set; } = DebugViewMode.Shaded;

    /// <summary>★ M7 — Objects the pipeline should ring with the selection outline. Null = no outline.</summary>
    public IReadOnlyList<GameObject>? SelectionOutlineTargets { get; set; }

    /// <summary>★ M7 — Outline colour; the pipeline falls back to Unity's orange when unset.</summary>
    public Vector.Color SelectionOutlineColor { get; set; } = new(1f, 0.45f, 0f, 1f);

    public Camera Camera => _camera;
    public RenderTexture? RenderTarget => _renderTarget;
    public Float3 Position => _position;
    public float Yaw => _yaw;
    public float Pitch => _pitch;
    public Float3 Forward => _cameraObject.Transform.Forward;

    /// <summary>Set the camera position directly.</summary>
    public void SetPosition(Float3 position)
    {
        _position = position;
        UpdateTransform();
    }

    /// <summary>Restore full navigation state (position + yaw/pitch). Pitch is clamped.</summary>
    public void SetPose(Float3 position, float yaw, float pitch)
    {
        _position = position;
        _yaw = yaw;
        _pitch = MathF.Max(-89f, MathF.Min(89f, pitch));
        UpdateTransform();
    }

    public EditorCamera()
    {
        _cameraObject = new GameObject("EditorCamera");
        _cameraObject.Enabled = true;
        _cameraObject.HideFlags = HideFlags.HideAndDontSave | HideFlags.NoGizmos;

        _camera = _cameraObject.AddComponent<Camera>();
        _camera.FieldOfView = 60f;
        _camera.NearClipPlane = 0.1f;
        _camera.FarClipPlane = 1000f;
        SyncClipPlanes();
        _camera.ClearFlags = CameraClearFlags.SolidColor;
        _camera.ClearColor = new Color(0.22f, 0.22f, 0.22f, 1f);

        UpdateTransform();
    }

    /// <summary>
    /// Ensure the render target matches the given size.
    /// </summary>
    public void EnsureRenderTarget(uint width, uint height)
    {
        if (width == 0 || height == 0) return;

        if (_renderTarget != null && _renderTarget.Width == width && _renderTarget.Height == height)
        {
            _pendingTargetWidth = 0;
            _pendingTargetHeight = 0;
            return;
        }

        if (_renderTarget != null)
        {
            if (_pendingTargetWidth != width || _pendingTargetHeight != height)
            {
                _pendingTargetWidth = width;
                _pendingTargetHeight = height;
                _pendingTargetSince = Time.UnscaledTotalTime;
                return;
            }

            if (Time.UnscaledTotalTime - _pendingTargetSince < 0.12)
                return;
        }

        if (_renderTarget != null)
            Graphics.DeferDispose(_renderTarget);
        _renderTarget = new RenderTexture(
            (int)width, (int)height, true,
            new[] { TextureImageFormat.Color4b });
        _renderTarget.SetDebugName("Scene view");

        _pendingTargetWidth = 0;
        _pendingTargetHeight = 0;
        _camera.Target = _renderTarget;
    }

    /// <summary>
    /// Whether to copy image effects from the scene's main camera onto the editor camera.
    /// </summary>
    public bool UseSceneEffects { get; set; } = true;

    /// <summary>
    /// Render the scene from this camera's perspective.
    /// </summary>
    public void Render(Scene scene, bool drawUI = true)
    {
        if (_renderTarget == null) return;
        // The editor camera renders through RenderSceneOverride instead of being added to and
        // removed from the scene every frame: that churn advanced Scene.StructureVersion twice per
        // render, so every topology-keyed consumer (Hierarchy model, structure-version caches)
        // saw a changed scene on every frame. Five-round measurement of the whole-list Hierarchy
        // with and without the churn (legacy vs legacy-stable) is recorded in
        // docs/benchmarks/editor-performance/2026-09-14-incremental-ui-n4/perf-rounds.
        if (!_renderScene.TryGetTarget(out var previousScene) || !ReferenceEquals(previousScene, scene))
        {
            ReleaseSceneReferences();
            _renderScene.SetTarget(scene);
            _camera.OnEnable();
        }

        // Scene view always owns the full target. A leaked game-camera Viewrect would
        // letterbox the blit and leave a clear-color band on the unused edge.
        _camera.Viewrect = new Rect(0f, 0f, 1f, 1f);
        SyncClipPlanes();

        // Copy image effects from the scene's main camera (if enabled)
        if (UseSceneEffects)
            CopySceneEffects(scene);
        else
            _camera.Effects.Clear();

        // Update camera pixel dimensions
        _camera.UpdateRenderData();

        // Build rendering data
        var renderData = new RenderingData
        {
            DisplayGizmos = ShowGizmos,
            DisplayGrid = ShowGrid,
            IsSceneView = true,
            DebugView = DebugView,
            SkipUI = !drawUI,
            SelectionOutlineTargets = SelectionOutlineTargets,
            SelectionOutlineColor = SelectionOutlineColor,
        };

        // Render
        var pipeline = _camera.Pipeline ?? UniversalRenderPipeline.Default;
        pipeline.RenderScene(_camera, scene, renderData);
    }

    // Cached cloned effects for the editor camera. Persistent across frames so
    // temporal effects (TAA, motion blur) keep their history buffers intact.
    private readonly List<ImageEffect> _clonedEffects = new();
    private Camera? _lastSceneCamera;

    /// <summary>
    /// Drop references to scene-derived objects: the cached scene <see cref="Camera"/> and the
    /// cloned <see cref="ImageEffect"/> instances (which can be user-script types). These persist
    /// across frames, so they must be released before a script hot-reload unloads the ALC.
    /// </summary>
    public void ReleaseSceneReferences()
    {
        _camera.OnDisable();
        _renderScene.SetTarget(null!);
        DisposeClonedEffects();
        _camera.Effects.Clear();
        _lastSceneCamera = null;
    }

    /// <summary>
    /// Sync image effects from the scene's main camera. Clones effect instances on first
    /// use or when the effect list changes, then uses DeserializeInto each frame to copy
    /// settings without destroying internal state (TAA history, etc.).
    /// </summary>
    private void CopySceneEffects(Scene scene)
    {
        Camera? sceneCamera = FindSceneCamera(scene);
        if (sceneCamera == null || sceneCamera == _camera)
        {
            _camera.Effects.Clear();
            DisposeClonedEffects();
            _lastSceneCamera = null;
            return;
        }

        // Build a filtered list of non-null effects from the scene camera so null
        // entries don't cause index misalignment between the source and clone lists.
        var sourceEffects = new List<ImageEffect>();
        foreach (var effect in sceneCamera.Effects)
        {
            if (effect != null)
                sourceEffects.Add(effect);
        }

        // Check if the effect list structure changed (different types, count, or camera)
        bool needsReclone = sceneCamera != _lastSceneCamera
            || sourceEffects.Count != _clonedEffects.Count;

        if (!needsReclone)
        {
            for (int i = 0; i < sourceEffects.Count; i++)
            {
                if (sourceEffects[i].GetType() != _clonedEffects[i].GetType())
                {
                    needsReclone = true;
                    break;
                }
            }
        }

        if (needsReclone)
        {
            // Dispose old clones first so persistent GPU resources (TAA history,
            // adaptation buffers, etc.) are freed before creating new instances.
            DisposeClonedEffects();

            foreach (var effect in sourceEffects)
            {
                try
                {
                    var echo = Echo.Serializer.Serialize(effect);
                    var clone = Echo.Serializer.Deserialize(echo, effect.GetType()) as ImageEffect;
                    if (clone != null)
                        _clonedEffects.Add(clone);
                }
                catch
                {
                    // Fallback: create a blank instance
                    if (Activator.CreateInstance(effect.GetType()) is ImageEffect blank)
                        _clonedEffects.Add(blank);
                }
            }
            _lastSceneCamera = sceneCamera;
        }
        else
        {
            // Sync settings into existing clones (preserves internal state like TAA history)
            for (int i = 0; i < sourceEffects.Count; i++)
            {
                try
                {
                    var echo = Echo.Serializer.Serialize(sourceEffects[i]);
                    Echo.Serializer.DeserializeInto(echo, _clonedEffects[i]);
                }
                catch { }
            }
        }

        // Apply cloned effects to editor camera
        _camera.Effects.Clear();
        _camera.Effects.AddRange(_clonedEffects);
        _camera.HDR = sceneCamera.HDR;
    }

    /// <summary>
    /// Dispose all cloned effects so persistent GPU resources (history buffers, adaptation
    /// RTs, etc.) are properly freed.
    /// </summary>
    private void DisposeClonedEffects()
    {
        foreach (var effect in _clonedEffects)
        {
            try { effect.OnDisable(); }
            catch { }
        }
        _clonedEffects.Clear();
    }

    private static Camera? FindSceneCamera(Scene scene)
    {
        // First try to find a camera tagged "Main Camera"
        foreach (var go in scene.AllObjects)
        {
            if (go.HideFlags.HasFlag(HideFlags.HideAndDontSave)) continue;
            if (!go.CompareTag("Main Camera")) continue;

            var cam = go.GetComponent<Camera>();
            if (cam != null) return cam;
        }

        // Fallback: first visible Camera in the scene
        foreach (var go in scene.AllObjects)
        {
            if (go.HideFlags.HasFlag(HideFlags.HideAndDontSave)) continue;

            var cam = go.GetComponent<Camera>();
            if (cam != null) return cam;
        }

        return null;
    }

    // ================================================================
    //  Camera Controls
    // ================================================================

    /// <summary>
    /// Process input for the editor camera. Call each frame with delta time.
    /// Right-click + mouse look, WASD to move, middle mouse to pan, scroll to zoom/speed.
    /// </summary>
    public bool ProcessInput(float dt, bool isHovered, Float2 mousePos, Float2 panelOrigin, Float2 panelSize)
    {
        bool rmbHeld = Input.GetMouseButton(1);
        if (_lookLocked && !rmbHeld)
            ReleaseLookLock();

        bool looking = _lookLocked || rmbHeld;
        if (!isHovered && !looking)
            return false;

        _lockContext.PanelOrigin = panelOrigin;
        _lockContext.PanelSize = panelSize;

        bool consumed = false;
        float scroll = Input.MouseWheelDelta;

        if (isHovered)
        {
            // Scroll to dolly forward/back (also adjusts orbit distance). Orthographic views
            // must change OrthographicSize — moving along forward does not zoom an ortho camera.
            if (scroll != 0 && !rmbHeld)
            {
                DollyByScroll(scroll);
                consumed = true;
            }

            // Alt + Left mouse = orbit around a pivot captured when the gesture starts. Orbiting the
            // selection (when present) keeps the thing you care about centered, otherwise a point ahead.
            bool orbiting = Input.IsAltPressed && Input.GetMouseButton(0);
            if (orbiting && !_wasOrbiting)
            {
                _orbitPivot = ComputeOrbitPivot();
                _orbitDistance = MathF.Max(0.1f, Float3.Length(_orbitPivot - _position));
            }
            if (orbiting)
            {
                Float2 delta = Input.MouseDelta;

                _yaw += delta.X * 0.3f;
                _pitch += delta.Y * 0.3f;
                _pitch = MathF.Max(-89f, MathF.Min(89f, _pitch));

                // Reposition so the pivot stays fixed and centered, using the transform's actual forward.
                UpdateTransform();
                _position = _orbitPivot - _cameraObject.Transform.Forward * _orbitDistance;
                UpdateTransform();
                consumed = true;
            }
            _wasOrbiting = orbiting;

            // Alt + Right mouse = dolly zoom
            if (Input.IsAltPressed && rmbHeld)
            {
                Float2 delta = Input.MouseDelta;
                float zoomDelta = (delta.X + delta.Y) * 0.02f * _orbitDistance;
                Float3 forward = GetForwardFromAngles();
                _position += forward * zoomDelta;
                _orbitDistance = MathF.Max(0.1f, _orbitDistance - zoomDelta);
                UpdateTransform();
                consumed = true;
            }

            // Middle mouse = pan
            if (Input.GetMouseButton(2))
            {
                Float2 delta = Input.MouseDelta;
                float panScale = 0.01f * _moveSpeed;
                Float3 right = _cameraObject.Transform.Right;
                Float3 up = _cameraObject.Transform.Up;
                _position -= right * delta.X * panScale;
                _position += up * delta.Y * panScale;
                UpdateTransform();
                consumed = true;
            }

            if (Input.GetMouseButtonDown(1) && !_lookLocked)
                BeginLookLock();
        }

        // Right mouse = look + WASD. Unlock is level-triggered above so a skipped GUI frame
        // (AnimatedBackground off, hover lost while Silk CursorMode.Disabled) cannot stick.
        if (rmbHeld)
        {
            Float2 delta = Input.MouseDelta;

            // Mouse look
            _yaw += delta.X * 0.2f;
            _pitch += delta.Y * 0.2f;
            _pitch = MathF.Max(-89f, MathF.Min(89f, _pitch));

            // WASD movement
            Float3 forward = _cameraObject.Transform.Forward;
            Float3 right = _cameraObject.Transform.Right;
            Float3 move = Float3.Zero;

            if (Input.GetKey(KeyCode.W)) move += forward;
            if (Input.GetKey(KeyCode.S)) move -= forward;
            if (Input.GetKey(KeyCode.D)) move += right;
            if (Input.GetKey(KeyCode.A)) move -= right;
            if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.Space)) move += Float3.UnitY;
            if (Input.GetKey(KeyCode.Q)) move -= Float3.UnitY;

            float speed = _moveSpeed * dt;
            if (Input.IsShiftPressed) speed *= 3f;

            if (Float3.LengthSquared(move) > 0)
            {
                move = Float3.Normalize(move) * speed;
                _position += move;
            }

            // Scroll adjusts move speed while RMB held
            if (scroll != 0)
            {
                _moveSpeed *= 1f + scroll * 0.15f;
                _moveSpeed = MathF.Max(0.5f, MathF.Min(100f, _moveSpeed));
                _speedChangedTime = Runtime.Time.UnscaledTotalTime;
            }

            UpdateTransform();
            consumed = true;
        }

        // F = focus on selection
        if (isHovered && ShortcutManager.IsPressed("Scene/Focus"))
        {
            FocusSelection();
            consumed = true;
        }

        return consumed;
    }

    private void BeginLookLock()
    {
        Input.PushLockContext(_lockContext);
        Input.LockCursor();
        _lookLocked = true;
    }

    /// <summary>Show the cursor and pop the scene-view lock context if this camera holds it.</summary>
    internal void ReleaseLookLock()
    {
        if (!_lookLocked) return;
        _lookLocked = false;
        Input.UnlockCursor();
        Input.PopLockContext();
    }

    /// <summary>
    /// Wheel zoom. Perspective dollies along the view forward; isometric/2D scales the ortho size.
    /// </summary>
    internal void DollyByScroll(float scroll)
    {
        if (scroll == 0f) return;

        if (IsOrthographic)
        {
            float factor = MathF.Max(0.05f, 1f - scroll * 0.1f);
            _orbitDistance = Math.Clamp(_orbitDistance * factor, MinOrbitDistance, MaxOrbitDistance);
            _camera.OrthographicSize = Math.Clamp(_camera.OrthographicSize * factor, MinOrbitDistance, MaxOrbitDistance);
            SyncClipPlanes();
            UpdateTransform();
            return;
        }

        Float3 forward = GetForwardFromAngles();
        float dolly = scroll * _orbitDistance * 0.1f;
        _position += forward * dolly;
        _orbitDistance = Math.Clamp(_orbitDistance - dolly, MinOrbitDistance, MaxOrbitDistance);
        UpdateTransform();
    }

    /// <summary>
    /// Pivot for an orbit gesture: the selection's bounds/position center when something is selected,
    /// otherwise a point ahead of the camera at the current orbit distance.
    /// </summary>
    private Float3 ComputeOrbitPivot()
    {
        Float3 min = new(float.MaxValue);
        Float3 max = new(float.MinValue);
        bool anyBounds = false;
        int count = 0;
        Float3 positionSum = Float3.Zero;

        foreach (var go in Selection.GetSelected<GameObject>())
        {
            count++;
            positionSum += go.Transform.Position;
            AccumulateRendererBounds(go, ref min, ref max, ref anyBounds);
        }

        if (anyBounds) return (min + max) * 0.5f;
        if (count > 0) return positionSum / count;
        return _position + _cameraObject.Transform.Forward * _orbitDistance;
    }

    public void FocusSelection()
    {
        // Overlay UI is laid out in world space only while this override is on (the Scene view
        // render path also sets it). Framing from the Hierarchy would otherwise see identity-scale
        // screen verts and fly the camera to the origin.
        bool prevWorldSpace = GameCanvas.EditorWorldSpaceOverride;
        GameCanvas.EditorWorldSpaceOverride = true;
        try
        {
            FocusSelectionCore();
        }
        finally
        {
            GameCanvas.EditorWorldSpaceOverride = prevWorldSpace;
        }
    }

    private void FocusSelectionCore()
    {
        Float3 min = new(float.MaxValue);
        Float3 max = new(float.MinValue);
        bool anyBounds = false;
        int goCount = 0;
        Float3 positionSum = Float3.Zero;

        foreach (var go in Selection.GetSelected<GameObject>())
        {
            goCount++;
            positionSum += go.Transform.Position;
            AccumulateRendererBounds(go, ref min, ref max, ref anyBounds);
        }
        if (goCount == 0) return;

        // Frame size is driven by renderer bounds when any are present; otherwise fall
        // back to the Transform position(s) so even empty GOs (lights, cameras) focus.
        Float3 target;
        float radius;
        if (anyBounds)
        {
            target = (min + max) * 0.5f;
            Float3 size = max - min;
            radius = MathF.Max(0.5f, Float3.Length(size) * 0.5f);
        }
        else
        {
            target = positionSum / goCount;
            radius = 0.5f;
        }

        // Distance that fits the object in the vertical FOV, with a little padding.
        float fovRad = _camera.FieldOfView * MathF.PI / 180f;
        float dist = radius / MathF.Tan(fovRad * 0.5f) + radius;
        dist = MathF.Max(dist, 0.5f);

        // Set orbit distance and position camera to look at target
        _orbitDistance = dist;
        _orbitPivot = target;
        _position = target - GetForwardFromAngles() * dist;
        UpdateTransform();

        // An orthographic camera doesn't zoom by moving, so framing must also size the ortho
        // volume: fit the selection's extents (projected onto the view axes) into the vertical
        // extent, accounting for the viewport aspect, with ~10% padding.
        if (IsOrthographic)
        {
            Float3 halfExtents = anyBounds ? (max - min) * 0.5f : new Float3(radius);
            Float3 viewRight = _cameraObject.Transform.Right;
            Float3 viewUp = _cameraObject.Transform.Up;
            float radiusX = MathF.Abs(halfExtents.X * viewRight.X)
                          + MathF.Abs(halfExtents.Y * viewRight.Y)
                          + MathF.Abs(halfExtents.Z * viewRight.Z);
            float radiusY = MathF.Abs(halfExtents.X * viewUp.X)
                          + MathF.Abs(halfExtents.Y * viewUp.Y)
                          + MathF.Abs(halfExtents.Z * viewUp.Z);

            float aspect = _renderTarget != null && _renderTarget.Height > 0
                ? _renderTarget.Width / (float)_renderTarget.Height
                : 16f / 9f;

            // OrthographicSize is the FULL vertical extent (see Camera.GetProjectionMatrix:
            // CreateOrtho(size * aspect, size)), so fit twice the vertical half-extent.
            float halfVertical = MathF.Max(radiusY, radiusX / MathF.Max(aspect, 0.01f));
            _camera.OrthographicSize = MathF.Max(0.1f, 2f * halfVertical * 1.1f);
            SyncClipPlanes();
        }
    }

    /// <summary>True when the editor camera is orthographic (2D mode or the view-cube Iso projection).</summary>
    public bool IsOrthographic => _camera.ProjectionMode == Camera.ProjectionType.Orthographic;

    /// <summary>
    /// Switch projection without snapping the view. The view cube's Perspective/Iso toggle uses this;
    /// the toolbar 2D button still calls <see cref="SetOrthographic"/> so entering 2D also squares up.
    /// </summary>
    public void SetProjectionOrthographic(bool orthographic)
    {
        var mode = orthographic ? Camera.ProjectionType.Orthographic : Camera.ProjectionType.Perspective;
        if (_camera.ProjectionMode == mode) return;
        _camera.ProjectionMode = mode;
        if (orthographic)
        {
            float fovRad = _camera.FieldOfView * MathF.PI / 180f;
            _camera.OrthographicSize = MathF.Max(0.1f, 2f * _orbitDistance * MathF.Tan(fovRad * 0.5f));
        }
        SyncClipPlanes();
        _poseVersion++;
    }

    /// <summary>
    /// Point the camera along <paramref name="forward"/> while keeping the current orbit pivot, like
    /// clicking an axis on Unity's scene gizmo (Right/Top/Front/...).
    /// </summary>
    public void LookAlong(Float3 forward)
    {
        Float3 dir = Float3.Normalize(forward);
        if (Float3.LengthSquared(dir) < 1e-8f) return;

        Float3 pivot = _position + GetForwardFromAngles() * _orbitDistance;
        float yaw = MathF.Atan2(dir.X, dir.Z) * (180f / MathF.PI);
        float pitch = MathF.Asin(Math.Clamp(-dir.Y, -1f, 1f)) * (180f / MathF.PI);
        _yaw = yaw;
        _pitch = Math.Clamp(pitch, -90f, 90f);
        _orbitPivot = pivot;
        _position = pivot - GetForwardFromAngles() * MathF.Max(_orbitDistance, 0.1f);
        UpdateTransform();
    }

    private static void AccumulateRendererBounds(GameObject go, ref Float3 min, ref Float3 max, ref bool any)
    {
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null && mr.Mesh.Res != null)
        {
            var wb = mr.Mesh.Res.bounds.TransformBy(go.Transform.LocalToWorldMatrix);
            min = new Float3(MathF.Min(min.X, wb.Min.X), MathF.Min(min.Y, wb.Min.Y), MathF.Min(min.Z, wb.Min.Z));
            max = new Float3(MathF.Max(max.X, wb.Max.X), MathF.Max(max.Y, wb.Max.Y), MathF.Max(max.Z, wb.Max.Z));
            any = true;
        }
        var smr = go.GetComponent<SkinnedMeshRenderer>();
        if (smr != null && smr.SharedMesh.Res != null)
        {
            var wb = smr.SharedMesh.Res.bounds.TransformBy(go.Transform.LocalToWorldMatrix);
            min = new Float3(MathF.Min(min.X, wb.Min.X), MathF.Min(min.Y, wb.Min.Y), MathF.Min(min.Z, wb.Min.Z));
            max = new Float3(MathF.Max(max.X, wb.Max.X), MathF.Max(max.Y, wb.Max.Y), MathF.Max(max.Z, wb.Max.Z));
            any = true;
        }

        // UI lives on RectTransforms, not MeshRenderers. Overlay canvases are shown in world space
        // in the Scene view, so framing has to use that same mapping or F / Hierarchy double-click
        // would orbit a 0.5-unit point at the transform origin while the canvas is 13 units across.
        if (go.GetComponent<GameCanvas>() is { } canvas
            && canvas.TryGetWorldAabb(out Float3 canvasMin, out Float3 canvasMax))
        {
            Encapsulate(ref min, ref max, canvasMin, canvasMax);
            any = true;
        }
        else if (go.GetComponent<UIBehaviour>() is { } ui
            && ui.GetCanvas() is { } owner
            && owner.TryGetWorldAabb(ui, out Float3 uiMin, out Float3 uiMax))
        {
            Encapsulate(ref min, ref max, uiMin, uiMax);
            any = true;
        }
        else if (go.RectTransform is { } rectOnly
            && go.GetComponentInParent<GameCanvas>() is { } rectCanvas
            && rectCanvas.TryGetWorldAabb(rectOnly, out Float3 rectMin, out Float3 rectMax))
        {
            // Grouping nodes carry only a RectTransform (no UIBehaviour); map their layout rect
            // through the owning canvas exactly like rendered elements so framing fits them too.
            Encapsulate(ref min, ref max, rectMin, rectMax);
            any = true;
        }

        foreach (var child in go.Children)
            AccumulateRendererBounds(child, ref min, ref max, ref any);
    }

    private static void Encapsulate(ref Float3 min, ref Float3 max, Float3 a, Float3 b)
    {
        min = new Float3(MathF.Min(min.X, a.X), MathF.Min(min.Y, a.Y), MathF.Min(min.Z, a.Z));
        max = new Float3(MathF.Max(max.X, b.X), MathF.Max(max.Y, b.Y), MathF.Max(max.Z, b.Z));
        min = new Float3(MathF.Min(min.X, b.X), MathF.Min(min.Y, b.Y), MathF.Min(min.Z, b.Z));
        max = new Float3(MathF.Max(max.X, a.X), MathF.Max(max.Y, a.Y), MathF.Max(max.Z, a.Z));
    }

    /// <summary>
    /// Switch between the perspective fly camera and the axis-aligned orthographic view used for 2D
    /// work. Entering 2D squares up the view down +Z and keeps the framed extent roughly the same as
    /// the perspective view had, so the toggle doesn't jump the user somewhere unexpected.
    /// </summary>
    public void SetOrthographic(bool orthographic)
    {
        var mode = orthographic ? Camera.ProjectionType.Orthographic : Camera.ProjectionType.Perspective;
        if (_camera.ProjectionMode == mode) return;

        _camera.ProjectionMode = mode;
        if (!orthographic)
        {
            SyncClipPlanes();
            _poseVersion++;
            return;
        }

        // Match the vertical extent the perspective view covered at the current orbit distance.
        float fovRad = _camera.FieldOfView * MathF.PI / 180f;
        _camera.OrthographicSize = MathF.Max(0.1f, 2f * _orbitDistance * MathF.Tan(fovRad * 0.5f));
        SyncClipPlanes();
        SetOrientation(0f, 0f);
    }

    /// <summary>
    /// Orthographic iso/2D must see the ground that sits toward the camera (screen-bottom).
    /// A positive near plane clips that slab and leaves a clear-color band; a symmetric
    /// clip volume around the camera matches how an editor ortho frustum is supposed to work.
    /// </summary>
    internal void SyncClipPlanes()
    {
        if (_camera.ProjectionMode == Camera.ProjectionType.Orthographic)
        {
            float extent = MathF.Max(_camera.OrthographicSize, _orbitDistance);
            extent = Math.Clamp(MathF.Max(extent * 8f, 50f), 50f, MaxClipExtent);
            _camera.NearClipPlane = -extent;
            _camera.FarClipPlane = extent;
            return;
        }

        _camera.NearClipPlane = 0.1f;
        _camera.FarClipPlane = Math.Clamp(MathF.Max(1000f, _orbitDistance * 100f), 1000f, MaxClipExtent);
    }

    public void SetOrientation(float yaw, float pitch)
    {
        _yaw = yaw;
        _pitch = MathF.Max(-89f, MathF.Min(89f, pitch));
        UpdateTransform();
    }

    public Ray ScreenPointToRay(Float2 screenPos, Float2 panelSize)
    {
        return _camera.ScreenPointToRay(screenPos, panelSize);
    }

    /// <summary>Compute forward direction from yaw/pitch angles without reading the transform.</summary>
    private Float3 GetForwardFromAngles()
    {
        float yawRad = _yaw * MathF.PI / 180f;
        float pitchRad = _pitch * MathF.PI / 180f;
        float cosPitch = MathF.Cos(pitchRad);
        return new Float3(
            MathF.Sin(yawRad) * cosPitch,
            -MathF.Sin(pitchRad),
            MathF.Cos(yawRad) * cosPitch
        );
    }

    private void UpdateTransform()
    {
        _cameraObject.Transform.Position = _position;
        _cameraObject.Transform.LocalEulerAngles = new Float3(_pitch, _yaw, 0);
        _poseVersion++;
    }

    public void Dispose()
    {
        ReleaseLookLock();
        ReleaseSceneReferences();
        _cameraObject.Dispose();
        _renderTarget?.Dispose();
        _renderTarget = null;
        _pendingTargetWidth = 0;
        _pendingTargetHeight = 0;
    }
}
