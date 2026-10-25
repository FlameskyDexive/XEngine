using System;

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.GUI;

/// <summary>
/// Renders 3D previews of assets (models, materials, meshes) to a RenderTexture.
/// Creates an isolated Scene with camera + light for clean rendering.
/// Supports orbit camera for interactive previews.
/// </summary>
public class PreviewRenderer : IDisposable
{
    private Scene _scene;
    private GameObject _cameraGo;
    private Camera _camera;
    private GameObject _lightGo;
    private GameObject? _subjectGo;
    private RenderTexture? _rt;

    private Animator? _previewAnimator;
    private AnimationClipPlayable? _previewClipPlayable;
    private BlendTreePlayable? _previewBlendTree;
    private bool _previewPlaying;
    private bool _previewDirty = true;
    private bool _hovered;
    private float _lastPreviewX, _lastPreviewY, _lastPreviewW, _lastPreviewH;
    private bool _hasSkinnedAvatar;
    private float _previewSpeed = 1f;
    private string _previewClipName = "";
    private static Mesh? s_previewSphere;

    /// <summary>Whether to draw a grid plane in the preview.</summary>
    public bool ShowGrid { get; set; }

    // Orbit camera state
    private float _orbitYaw = 30f;
    private float _orbitPitch = 20f;
    private float _orbitDistance = 3f;
    private Float3 _orbitTarget = Float3.Zero;

    public RenderTexture? Result => _rt;
    public GameObject? Subject => _subjectGo;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public PreviewRenderer(int width = 256, int height = 256)
    {
        Width = width;
        Height = height;

        _scene = new Scene();
        _scene.Name = "Preview";

        // Camera
        _cameraGo = new GameObject("PreviewCamera");
        _cameraGo.HideFlags = HideFlags.HideAndDontSave | HideFlags.NoGizmos;
        _camera = _cameraGo.AddComponent<Camera>();
        _camera.FieldOfView = 35f;
        _camera.NearClipPlane = 0.01f;
        _camera.FarClipPlane = 100f;
        ApplyPreviewCameraSettings(_camera);
        _scene.Add(_cameraGo);

        // Light
        _lightGo = new GameObject("PreviewLight");
        _lightGo.HideFlags = HideFlags.HideAndDontSave | HideFlags.NoGizmos;
        _lightGo.Transform.LocalEulerAngles = new Float3(-45, 45, 0);
        var light = _lightGo.AddComponent<DirectionalLight>();
        light.Intensity = 1f;
        light.CastShadows = false;
        _scene.Add(_lightGo);

        _scene.Enable();

        EnsureRT();
        UpdateCameraPosition();
    }

    /// <summary>Set up the preview to show a prefab (model imports are prefabs too — prowl-sync M4.1).</summary>
    public void SetupForModel(PrefabAsset model)
    {
        ClearSubject();
        if (model == null) return;

        // Instantiate the model's GO hierarchy for preview
        _subjectGo = model.Instantiate();
        if (_subjectGo == null) { _subjectGo = new GameObject("PreviewSubject"); return; }
        _subjectGo.Name = "PreviewSubject";
        _subjectGo.HideFlags = HideFlags.HideAndDontSave;

        NormalizeSubjectToUnitCube(_subjectGo);

        _scene.Add(_subjectGo);
        FitToSubject(AABB.FromCenterAndSize(Float3.Zero, Float3.One));
    }

    /// <summary>Set up the preview to show a Mesh with a material.</summary>
    public void SetupForMesh(Mesh mesh, Material? material = null)
    {
        ClearSubject();
        if (mesh == null) return;

        _subjectGo = new GameObject("PreviewSubject");
        _subjectGo.HideFlags = HideFlags.HideAndDontSave;
        var renderer = _subjectGo.AddComponent<MeshRenderer>();
        renderer.Mesh = mesh;
        renderer.Material = material ?? new Material(Shader.LoadDefault(DefaultShader.Standard));

        NormalizeSubjectToUnitCube(_subjectGo);

        _scene.Add(_subjectGo);
        FitToSubject(AABB.FromCenterAndSize(Float3.Zero, Float3.One));
    }

    /// <summary>Set up the preview to show a Prefab's serialized GameObject hierarchy.</summary>
    public void SetupForPrefab(PrefabAsset prefab)
    {
        ClearSubject();
        if (prefab == null) return;

        _subjectGo = prefab.Instantiate();
        if (_subjectGo == null) { _subjectGo = new GameObject("PreviewSubject"); return; }
        _subjectGo.Name = "PreviewSubject";
        _subjectGo.HideFlags = HideFlags.HideAndDontSave;

        // Non-visual prefabs (script-only hierarchies with no MeshRenderers) fall back to a
        // unit-sized default inside the helper, rendering as an empty preview.
        NormalizeSubjectToUnitCube(_subjectGo);

        _scene.Add(_subjectGo);
        FitToSubject(AABB.FromCenterAndSize(Float3.Zero, Float3.One));
    }

    /// <summary>Set up the preview to show a Material on a sphere.</summary>
    public void SetupForMaterial(Material material)
    {
        ClearSubject();
        if (material == null) return;

        PrepareMaterialAssets(material);

        _subjectGo = new GameObject("PreviewSubject");
        _subjectGo.HideFlags = HideFlags.HideAndDontSave;
        var renderer = _subjectGo.AddComponent<MeshRenderer>();
        renderer.Mesh = PreviewSphere();

        if (material.Shader == null || !material.Shader.IsValid())
            material.Shader = Shader.LoadDefault(DefaultShader.Standard);
        renderer.Material = material;

        _scene.Add(_subjectGo);

        FitToSubject(AABB.FromCenterAndSize(Float3.Zero, Float3.One));
        _orbitDistance *= 1.25f; // Zoom out a bit more for materials
        UpdateCameraPosition();
    }

    /// <summary>Current preview subject — lets callers attach extra renderers (e.g. the Frame
    /// Debugger's wireframe overlay) after a Setup* call. May be null before any setup.</summary>
    public GameObject? SubjectGameObject => _subjectGo;

    /// <summary>
    /// Drop in a caller-configured GameObject as the preview subject (optional packages, VFX, etc.).
    /// The renderer takes ownership and disposes the previous subject.
    /// </summary>
    public void ReplaceSubject(GameObject subject, AABB? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ClearSubject();
        _subjectGo = subject;
        _subjectGo.Name = string.IsNullOrEmpty(_subjectGo.Name) ? "PreviewSubject" : _subjectGo.Name;
        HideHierarchy(_subjectGo);
        PrepareSubjectAssets(_subjectGo);
        _scene.Add(_subjectGo);
        FitToSubject(bounds ?? AABB.FromCenterAndSize(Float3.Zero, Float3.One));
    }

    public bool PreviewPlaying
    {
        get => _previewPlaying;
        set
        {
            _previewPlaying = value;
            if (_previewClipPlayable == null) return;
            if (value) _previewClipPlayable.Play();
            else _previewClipPlayable.Pause();
        }
    }

    public bool HasSkinnedAvatar => _hasSkinnedAvatar;
    public float PreviewDuration => _previewClipPlayable != null
        ? (float)(_previewClipPlayable.Duration > 0d ? _previewClipPlayable.Duration : 0d)
        : (_previewAnimator?.CurrentClip?.Duration ?? 0f);
    public float PreviewTime => _previewClipPlayable != null
        ? (float)_previewClipPlayable.Time
        : 0f;
    public float PreviewNormalizedTime
    {
        get
        {
            float d = PreviewDuration;
            return d > 0f ? PreviewTime / d : 0f;
        }
    }

    public float BlendParameter
    {
        get => _previewBlendTree?.ParameterValue ?? 0f;
        set
        {
            if (_previewBlendTree != null)
                _previewBlendTree.ParameterValue = value;
        }
    }

    public bool HasBlendParameter => _previewBlendTree != null;

    /// <summary>Playback speed multiplier for <see cref="TickPreview"/> (Unity Inspector 1.00x).</summary>
    public float PreviewSpeed
    {
        get => _previewSpeed;
        set => _previewSpeed = value < 0.01f ? 0.01f : (value > 8f ? 8f : value);
    }

    public string PreviewClipName => _previewClipName;

    /// <summary>Instantiate a skinned (or skeleton) subject, attach a hidden Animator, and play <paramref name="clip"/>.</summary>
    public void SetupForAnimationClip(AnimationClip clip, EngineObject? avatar)
    {
        ClearSubject();
        if (clip == null) return;

        _subjectGo = InstantiateAvatarOrSkeleton(avatar, clip, out _hasSkinnedAvatar);
        AttachNormalizedSubject(_subjectGo);

        _previewAnimator = AttachHiddenAnimator(_subjectGo);
        _previewClipPlayable = _previewAnimator.Play(clip);
        _previewPlaying = true;
        _previewClipName = clip.Name ?? "";
    }

    /// <summary>Same as clip preview, driven by the controller's compiled FSM (default state).</summary>
    public void SetupForAnimatorController(AnimatorController controller, EngineObject? avatar)
    {
        ClearSubject();
        if (controller == null) return;

        AnimationClip? firstClip = FirstControllerClip(controller);
        _subjectGo = InstantiateAvatarOrSkeleton(avatar, firstClip, out _hasSkinnedAvatar);
        AttachNormalizedSubject(_subjectGo);

        _previewAnimator = AttachHiddenAnimator(_subjectGo);
        _previewAnimator.SetControllerAsset(controller);
        _previewClipPlayable = null;
        _previewPlaying = true;
        _previewClipName = controller.Name ?? "";
    }

    /// <summary>1D BlendTree preview: skinned avatar plus a live parameter, or skeleton from the first child clip.</summary>
    public void SetupForBlendTree(BlendTree tree, EngineObject? avatar)
    {
        ClearSubject();
        if (tree == null) return;

        AnimationClip? first = tree.Children.Count > 0 ? tree.Children[0].Clip.Res : null;
        _subjectGo = InstantiateAvatarOrSkeleton(avatar, first, out _hasSkinnedAvatar);
        AttachNormalizedSubject(_subjectGo);

        _previewAnimator = AttachHiddenAnimator(_subjectGo);
        _previewBlendTree = BlendTreePlayable.Build(tree, _previewAnimator.Skeleton!);
        _previewAnimator.PlayPlayable(_previewBlendTree);
        _previewClipPlayable = null;
        _previewPlaying = true;
        _previewClipName = tree.Name ?? "";
    }

    // The normalization wrapper: animation FBXs carry curves on their ROOT node (usually a
    // constant identity/zero TRS), and sampling them absolutely overwrote the unit-cube
    // normalization that NormalizeSubjectToUnitCube had placed on the subject root — the model
    // popped back to raw import size/pivot and the camera (framed for a unit cube) ended up
    // inside it. Parenting the subject under a frame node and moving the normalization THERE
    // keeps it out of the animation's reach; clip frame-0 deltas still compose correctly.
    private GameObject? _subjectFrame;

    private void AttachNormalizedSubject(GameObject subject)
    {
        HideHierarchy(subject);
        NormalizeSubjectToUnitCube(subject);

        var frame = new GameObject("PreviewSubjectFrame");
        frame.HideFlags = HideFlags.HideAndDontSave;
        frame.Transform.LocalScale = subject.Transform.LocalScale;
        frame.Transform.Position = subject.Transform.Position;
        subject.Transform.LocalScale = Float3.One;
        subject.Transform.Position = Float3.Zero;
        subject.Transform.SetParent(frame.Transform, worldPositionStays: false);

        _subjectFrame = frame;
        _scene.Add(_subjectFrame);
        FitToSubject(AABB.FromCenterAndSize(Float3.Zero, Float3.One));
    }

    /// <summary>Advance the isolated preview graph. Does not touch <see cref="Scene.Current"/> or mark the edited scene dirty.</summary>
    public void TickPreview(float dt)
    {
        if (_previewAnimator == null || !_previewPlaying) return;
        _previewAnimator.EvaluateGraph(dt * _previewSpeed);
    }

    public void SeekPreview(float normalizedTime)
    {
        float d = PreviewDuration;
        double t = d > 0f ? Math.Clamp(normalizedTime, 0f, 1f) * d : 0d;
        if (_previewClipPlayable != null)
            _previewClipPlayable.SetTime(t);
        _previewAnimator?.EvaluateGraph(0f);
        _previewDirty = true;
    }

    private GameObject InstantiateAvatarOrSkeleton(EngineObject? avatar, AnimationClip? clipForSkeleton, out bool skinned)
    {
        skinned = false;
        GameObject? go = avatar switch
        {
            PrefabAsset prefab => prefab.Instantiate(),
            _ => null,
        };
        if (go != null)
        {
            go.Name = "PreviewSubject";
            skinned = go.GetComponentInChildren<SkinnedMeshRenderer>() != null
                || go.GetComponentInChildren<MeshRenderer>() != null;
            return go;
        }

        if (clipForSkeleton != null)
            return AnimationPreviewAvatar.BuildSkeletonSubject(clipForSkeleton);

        return new GameObject("PreviewSubject");
    }

    private Animator AttachHiddenAnimator(GameObject go)
    {
        var anim = go.GetComponent<Animator>() ?? go.AddComponent<Animator>();
        anim.HideFlags = HideFlags.HideAndDontSave;
        anim.PlayAutomatically = false;
        anim.ApplyRootMotion = false;
        return anim;
    }

    private static AnimationClip? FirstControllerClip(AnimatorController controller)
    {
        var states = controller.States;
        for (int i = 0; i < states.Count; i++)
        {
            AnimationClip? clip = states[i].Motion.Res;
            if (clip != null) return clip;
            BlendTree? bt = states[i].BlendTree?.Res;
            if (bt != null && bt.Children.Count > 0)
            {
                AnimationClip? child = bt.Children[0].Clip.Res;
                if (child != null) return child;
            }
        }
        return null;
    }

    private static void HideHierarchy(GameObject go)
    {
        go.HideFlags = HideFlags.HideAndDontSave;
        var children = go.Children;
        for (int i = 0; i < children.Count; i++)
            HideHierarchy(children[i]);
    }

    /// <summary>Render the preview to the RenderTexture.</summary>
    public void Render()
    {
        if (_rt == null) return;

        _camera.UpdateRenderData();

        var pipeline = _camera.Pipeline ?? UniversalRenderPipeline.Default;
        pipeline.Render(_camera, new RenderingData { DisplayGrid = ShowGrid });
        _previewDirty = false;
    }

    /// <summary>
    /// Static material/mesh previews render once (and again on orbit / resize).
    /// Animation previews keep rendering while playing. Hovering alone does NOT re-render:
    /// orbit/zoom interactions mark the preview dirty through UpdateCameraPosition, so an idle
    /// pointer resting on a preview no longer costs a full pipeline submit per frame.
    /// </summary>
    public static bool ShouldRenderPreview(bool dirty, bool hovered, bool livePlaying)
        => dirty || livePlaying;

    /// <summary>
    /// Inspector / thumbnail cameras must not inherit the missing-component default
    /// (post and shadows on). That path compiles the full stack on first select.
    /// </summary>
    public static void ApplyPreviewCameraSettings(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        camera.ClearFlags = CameraClearFlags.SolidColor;
        camera.ClearColor = Game.FrameClearColor(isEditor: true);
        var extra = camera.GetComponent<UniversalAdditionalCameraData>()
            ?? camera.AddComponent<UniversalAdditionalCameraData>();
        extra.RenderPostProcessing = false;
        extra.RenderShadows = false;
        extra.Antialiasing = CameraAntialiasing.None;
    }

    internal static Mesh PreviewSphere()
        => s_previewSphere ??= Mesh.CreateSphere(0.5f, 24, 24);

    public bool HasLiveAnimation => _previewPlaying && _previewAnimator != null;

    public void MarkPreviewDirty() => _previewDirty = true;

    /// <summary>
    /// Reset the orbit to explicit angles and re-frame the camera onto <paramref name="bounds"/>.
    /// Flat 2D subjects (sprites, spine skeletons) pass yaw/pitch 0 for a straight-on view.
    /// </summary>
    public void FrameSubject(AABB bounds, float yaw = 30f, float pitch = 20f)
    {
        _orbitYaw = yaw;
        _orbitPitch = pitch;
        FitToSubject(bounds);
    }

    /// <summary>Resize the preview render target.</summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (Width == width && Height == height) return;
        Width = width;
        Height = height;
        EnsureRT();
        _previewDirty = true;
    }

    /// <summary>
    /// Draw the preview into a Paper element area. Returns true if hovered.
    /// </summary>
    public void DrawPreview(Paper paper, string id, float width, float height)
    {
        Resize((int)width, (int)height);
        _hovered = IsPointerOverLastPreview(paper);
        if (ShouldRenderPreview(_previewDirty, _hovered, HasLiveAnimation))
            Render();

        if (_rt == null || _rt.MainTexture == null) return;

        paper.Box(id)
            .Size(width, height)
            .BackgroundColor(System.Drawing.Color.FromArgb(255, 38, 38, 42))
            .Rounded(4)
            .StopEventPropagation()
            .OnDragging((e) => {
                Float2 delta = e.Delta;
                _orbitYaw += delta.X * 0.5f;
                _orbitPitch += delta.Y * 0.5f;
                _orbitPitch = MathF.Max(-89f, MathF.Min(89f, _orbitPitch));
                UpdateCameraPosition();
            })
            .OnScroll((e) =>
            {
                _orbitDistance *= 1f - e.Delta * 0.1f;
                _orbitDistance = MathF.Max(0.5f, MathF.Min(50f, _orbitDistance));
                UpdateCameraPosition();
            })
            .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, r) =>
            {
                float rx = (float)r.Min.X;
                float ry = (float)r.Min.Y;
                float rw = (float)r.Size.X;
                float rh = (float)r.Size.Y;

                // Camera target orientation depends on the raster backend.
                canvas.SetBrushTexture(_rt.MainTexture);
                canvas.SetBrushTextureTransform(
                    RenderTargetPreview.Transform(rx, ry, rw, rh));
                canvas.RoundedRectFilled(rx, ry, rw, rh, 4, 4, 4, 4, new Color32(255, 255, 255, 255));
                canvas.ClearBrushTexture();
                _lastPreviewX = rx;
                _lastPreviewY = ry;
                _lastPreviewW = rw;
                _lastPreviewH = rh;
            }));
    }

    private bool IsPointerOverLastPreview(Paper paper)
    {
        if (_lastPreviewW <= 0f || _lastPreviewH <= 0f) return false;
        double px = paper.PointerPos.X;
        double py = paper.PointerPos.Y;
        return px >= _lastPreviewX && px <= _lastPreviewX + _lastPreviewW
            && py >= _lastPreviewY && py <= _lastPreviewY + _lastPreviewH;
    }

    /// <summary>
    /// Resets the root transform of <paramref name="subject"/>, measures its world-space mesh
    /// bounds (children included), then scales + translates the root so the aggregate bounds
    /// fit inside a unit cube centered at the origin. Robust to hierarchies whose root has a
    /// saved non-identity transform (e.g. prefabs) the reset ensures the bounds we measure
    /// are in the same frame we then apply the normalization into.
    /// </summary>
    private static void NormalizeSubjectToUnitCube(GameObject subject)
    {
        // Preview setup is a one-shot editor operation. Streaming refs must be resolved before
        // measuring bounds and drawing the static texture; later completion alone won't dirty it.
        PrepareSubjectAssets(subject);

        // Reset position/scale for a clean measurement frame, but KEEP the root's authored
        // rotation: model FBX roots carry the Z-up→Y-up orientation correction, and zeroing it
        // made every model preview lie face-down on the floor. The bounds below are measured
        // through the children's world matrices, so the authored rotation is already included.
        subject.Transform.Position = Float3.Zero;
        subject.Transform.LocalScale = Float3.One;

        AABB bounds;
        var meshRenderer = subject.GetComponent<MeshRenderer>();
        var skinnedRenderer = subject.GetComponent<SkinnedMeshRenderer>();

        // Fast path for single-MeshRenderer subjects (SetupForMesh case): use the mesh's own
        // bounds directly, since there is no child hierarchy to walk and world == local at this
        // point thanks to the identity reset above.
        if (meshRenderer != null && meshRenderer.Mesh.Res != null && subject.Children.Count == 0)
            bounds = meshRenderer.Mesh.Res.bounds;
        else if (skinnedRenderer != null && skinnedRenderer.SharedMesh.Res != null && subject.Children.Count == 0)
            bounds = skinnedRenderer.SharedMesh.Res.bounds;
        else
            bounds = ComputeHierarchyBounds(subject);

        float maxExtent = MathF.Max(MathF.Max(bounds.Size.X, bounds.Size.Y), bounds.Size.Z);
        if (maxExtent <= 0.001f) return; // no visuals leave at identity

        float scale = 1f / maxExtent;
        subject.Transform.LocalScale = new Float3(scale, scale, scale);
        subject.Transform.Position = -bounds.Center * scale;
    }

    private static void PrepareSubjectAssets(GameObject subject)
    {
        foreach (var renderer in subject.GetComponents<MeshRenderer>())
        {
            renderer.Mesh.EnsureLoaded();
            PrepareMaterials(renderer.Materials);
        }
        foreach (var renderer in subject.GetComponents<SkinnedMeshRenderer>())
        {
            renderer.SharedMesh.EnsureLoaded();
            PrepareMaterials(renderer.Materials);
        }
        var children = subject.Children;
        for (int i = 0; i < children.Count; i++) PrepareSubjectAssets(children[i]);
    }

    private static void PrepareMaterials(System.Collections.Generic.List<AssetRef<Material>> materials)
    {
        for (int i = 0; i < materials.Count; i++)
        {
            var reference = materials[i];
            reference.EnsureLoaded();
            PrepareMaterialAssets(reference.ResWeak);
        }
    }

    private static void PrepareMaterialAssets(Material? material)
    {
        if (material == null) return;
        var shader = material.ShaderRef;
        shader.EnsureLoaded();
        var properties = material._properties;
        foreach (string name in properties.EnumerateNames())
        {
            var texture = properties.GetTextureRef(name); texture.EnsureLoaded();
            var texture3D = properties.GetTexture3DRef(name); texture3D.EnsureLoaded();
            var textureArray = properties.GetTexture2DArrayRef(name); textureArray.EnsureLoaded();
            var cubemap = properties.GetTextureCubeRef(name); cubemap.EnsureLoaded();
        }
    }

    private void ClearSubject()
    {
        _previewAnimator = null;
        _previewClipPlayable = null;
        _previewBlendTree = null;
        _previewPlaying = false;
        _previewDirty = true;
        _hasSkinnedAvatar = false;
        _previewClipName = "";

        if (_subjectGo != null)
        {
            _subjectGo.Dispose();
            _subjectGo = null;
        }
        if (_subjectFrame != null)
        {
            _scene.Remove(_subjectFrame);
            _subjectFrame.Dispose();
            _subjectFrame = null;
        }
    }

    private void FitToSubject(AABB bounds)
    {
        _orbitTarget = bounds.Center;
        float maxDim = MathF.Max(MathF.Max(bounds.Size.X, bounds.Size.Y), bounds.Size.Z);
        // For FOV 35 deg, distance to fit a unit object ~ 1.7
        _orbitDistance = MathF.Max(0.5f, maxDim * 1.7f);
        UpdateCameraPosition();
    }

    private void UpdateCameraPosition()
    {
        float yawRad = _orbitYaw * MathF.PI / 180f;
        float pitchRad = _orbitPitch * MathF.PI / 180f;

        Float3 offset = new Float3(
            MathF.Cos(pitchRad) * MathF.Sin(yawRad),
            MathF.Sin(pitchRad),
            MathF.Cos(pitchRad) * MathF.Cos(yawRad)
        ) * _orbitDistance;

        Float3 camPos = _orbitTarget + offset;
        _cameraGo.Transform.Position = camPos;

        Float3 dir = Float3.Normalize(_orbitTarget - camPos);
        if (Float3.LengthSquared(dir) > 0.0001f)
            _cameraGo.Transform.Rotation = Quaternion.LookRotation(dir, Float3.UnitY);
        _previewDirty = true;
    }

    private void EnsureRT()
    {
        _rt?.Dispose();
        _rt = new RenderTexture(Width, Height, true, new[] { TextureImageFormat.Color4b });
        _camera.Target = _rt;
    }

    /// <summary>
    /// Compute bounds by walking the GO hierarchy for all MeshRenderer and SkinnedMeshRenderer components.
    /// </summary>
    private static AABB ComputeHierarchyBounds(GameObject root)
    {
        Float3 min = new Float3(float.MaxValue);
        Float3 max = new Float3(float.MinValue);
        bool found = false;

        CollectBoundsRecursive(root, ref min, ref max, ref found);

        if (!found)
            return AABB.FromCenterAndSize(Float3.Zero, Float3.One);

        return new AABB(min, max);
    }

    private static void CollectBoundsRecursive(GameObject go, ref Float3 min, ref Float3 max, ref bool found)
    {
        // Check MeshRenderer
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            var mesh = mr.Mesh.Res;
            if (mesh != null)
            {
                var worldBounds = mesh.bounds.TransformBy(go.Transform.LocalToWorldMatrix);
                min = new Float3(MathF.Min(min.X, worldBounds.Min.X), MathF.Min(min.Y, worldBounds.Min.Y), MathF.Min(min.Z, worldBounds.Min.Z));
                max = new Float3(MathF.Max(max.X, worldBounds.Max.X), MathF.Max(max.Y, worldBounds.Max.Y), MathF.Max(max.Z, worldBounds.Max.Z));
                found = true;
            }
        }

        // Check SkinnedMeshRenderer
        var smr = go.GetComponent<SkinnedMeshRenderer>();
        if (smr != null)
        {
            var mesh = smr.SharedMesh.Res;
            if (mesh != null)
            {
                var worldBounds = mesh.bounds.TransformBy(go.Transform.LocalToWorldMatrix);
                min = new Float3(MathF.Min(min.X, worldBounds.Min.X), MathF.Min(min.Y, worldBounds.Min.Y), MathF.Min(min.Z, worldBounds.Min.Z));
                max = new Float3(MathF.Max(max.X, worldBounds.Max.X), MathF.Max(max.Y, worldBounds.Max.Y), MathF.Max(max.Z, worldBounds.Max.Z));
                found = true;
            }
        }

        foreach (var child in go.Children)
            CollectBoundsRecursive(child, ref min, ref max, ref found);
    }

    public void Dispose()
    {
        ClearSubject();
        _rt?.Dispose();
        _rt = null;

        // Disposes every GameObject still in the scene (camera + light), not just disables it.
        _scene.Dispose();
    }
}
