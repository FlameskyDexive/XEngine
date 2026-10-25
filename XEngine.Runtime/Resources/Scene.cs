// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;

using XEngine.Echo;
using XEngine.PaperUI;
using XEngine.Runtime.Rendering;
using XEngine.Vector;

namespace XEngine.Runtime.Resources;

[CreateAssetMenu("Scene", Extension = ".scene", Order = 0)]
public class Scene : EngineObject, ISerializationCallbackReceiver
{
    #region Scene Manager

    /// <summary>
    /// The currently active scene managed by the built-in Scene Manager.
    /// For simple games, use Scene.Load() and Scene.Current for automatic scene management.
    /// For advanced use cases (e.g., multiplayer servers with multiple scenes),
    /// create and manage your own Scene instances directly.
    /// </summary>
    public static Scene? Current { get; private set; }

    /// <summary>Fires after a scene is loaded via Load().</summary>
    public static event Action? OnSceneLoaded;

    /// <summary>
    /// Optional editor filter for <see cref="CollectRenderables"/>. Return false to skip
    /// this GameObject's OnRenderCollect. Null means allow all. Must be allocation-free.
    /// </summary>
    public static Func<GameObject, Camera, bool>? RenderCollectFilter;

    [OnAssemblyUnload]
    internal static void ClearSceneEvents()
    {
        OnSceneLoaded = null;
        RenderCollectFilter = null;
        _dontDestroyOnLoad.Clear();
    }

    // Objects marked via DontDestroyOnLoad (ReferenceEqualityComparer: GameObject is a class with
    // identity semantics). They detach from the outgoing scene in Load() and re-parent into the new one.
    private static readonly HashSet<GameObject> _dontDestroyOnLoad = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Marks a GameObject to survive <see cref="Load(Scene)"/>: instead of being disposed with the
    /// outgoing scene, it is detached and added to the newly loaded scene. Unity's
    /// <c>Object.DontDestroyOnLoad</c> analog. Root objects migrate with their whole subtree.
    /// <para/>
    /// Marked objects do not survive <see cref="Unload"/> — with no new scene to move into they are
    /// disposed together with the old one.
    /// </summary>
    public static void DontDestroyOnLoad(GameObject gameObject)
    {
        if (gameObject == null || gameObject.IsDisposed) return;
        _dontDestroyOnLoad.Add(gameObject);
    }

    /// <summary>
    /// Loads a scene as the current active scene, replacing any previously loaded scene.
    /// The previous scene will be disabled and disposed, except objects marked with
    /// <see cref="DontDestroyOnLoad"/>, which migrate into the new scene.
    /// </summary>
    public static void Load(Scene scene)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        List<GameObject>? carried = null;
        if (Current != null)
        {
            // Detach marked roots before Dispose() so the outgoing scene cannot reach them.
            // A marked child keeps its parent's subtree: detaching the root carries everything.
            foreach (GameObject go in _dontDestroyOnLoad)
            {
                if (go.IsDisposed || go.Scene != Current || go.Parent.IsValid()) continue;
                (carried ??= new List<GameObject>()).Add(go);
            }
            if (carried != null)
                foreach (GameObject go in carried)
                    Current.Remove(go);


            if (Current.IsActive)
                Current.Disable();
            Current.Dispose();
        }

        Current = scene;
        Current.Enable();

        if (carried != null)
        {
            foreach (GameObject go in carried)
            {
                if (go.IsDisposed) continue;
                scene.Add(go);
            }
        }

        _dontDestroyOnLoad.RemoveWhere(go => go.IsDisposed);
        OnSceneLoaded?.Invoke();
    }

    /// <summary>
    /// Unloads the current scene, disabling and disposing it.
    /// After calling this, Scene.Current will be null.
    /// </summary>
    /// <summary>
    /// Loads a scene as Current without calling Enable().
    /// Kept for backward compatibility now just calls Load() since lifecycle gating
    /// is handled per-component via ShouldExecuteGameplay.
    /// </summary>
    [Obsolete("Use Scene.Load() instead. Lifecycle gating is now per-component via [ExecuteAlways].")]
    public static void LoadWithoutEnable(Scene scene) => Load(scene);

    public static void Unload()
    {
        if (Current != null)
        {
            if (Current.IsActive)
                Current.Disable();
            Current.Dispose();
            Current = null;
        }

        // Marked objects went down with the scene (there was no new scene to move into).
        _dontDestroyOnLoad.Clear();
    }

    #endregion

    [SerializeField]
    private GameObject[] serializeObj = null;

    /// <summary>
    /// Parallel to serializeObj stores the original identifier for each GO.
    /// </summary>
    [SerializeField]
    private Guid[] _goIdentifiers = null;

    /// <summary>
    /// Flat array of component identifiers. _compIdOffsets[i] is the index into this
    /// array for GO i's first component. Component count = offset[i+1] - offset[i].
    /// </summary>
    [SerializeField]
    private Guid[] _compIdentifiers = null;

    [SerializeField]
    private int[] _compIdOffsets = null;

    [SerializeIgnore]
    private List<GameObject> _allObj = new();
    [SerializeIgnore]
    private HashSet<GameObject> _allObjSet = new(ReferenceEqualityComparer.Instance);

    // Lazily constructed: a scene with no physics users (editor preview scenes, UI-only scenes,
    // asset thumbnails) should not pay for a Jitter2 world (4 unmanaged partitioned buffers +
    // dynamic tree) plus a Box2D world, nor step them every FixedUpdate. First touch of the
    // Physics/Physics2D property creates the world; an untouched world skips stepping entirely.
    private PhysicsWorld? _physics;
    private PhysicsWorld2D? _physics2D;

    public PhysicsWorld Physics { get { EnsureNotDisposed(); return _physics ??= new PhysicsWorld(); } }

    /// <summary>Per-scene Box2D world. Independent of <see cref="Physics"/> (Jitter3D).</summary>
    public PhysicsWorld2D Physics2D { get { EnsureNotDisposed(); return _physics2D ??= new PhysicsWorld2D(); } }

    [SerializeIgnore]
    private readonly SceneComponentRegistry _componentRegistry = new();

    /// <summary>Per-scene registry of components that implement per-frame callbacks. Drives Update.</summary>
    internal SceneComponentRegistry ComponentRegistry => _componentRegistry;

    /// <summary>Appends the cameras currently enabled in this scene (object and component enabled) to
    /// <paramref name="output"/>. Maintained on enable/disable, so this costs O(cameras), not a walk
    /// over every GameObject; use it for per-frame "which cameras exist" queries.</summary>
    public void CollectEnabledCameras(List<Camera> output)
    {
        EnsureNotDisposed();
        _componentRegistry.CollectEnabledCameras(output);
    }

    [SerializeIgnore]
    private readonly List<Camera> _collectedCameras = new();
    [SerializeIgnore]
    private readonly HashSet<Camera> _seenCameras = new();
    [SerializeIgnore]
    private readonly List<Camera> _baseCameras = new();
    [SerializeIgnore]
    private readonly List<Camera> _orderedCameras = new();
    [SerializeIgnore]
    private readonly HashSet<Camera> _usedOverlays = new();

    [SerializeIgnore]
    private bool _isActive = false;

    [SerializeIgnore]
    private long _structureVersion;

    /// <summary>
    /// Monotonic version bumped whenever this scene's topology changes: a GameObject is added or
    /// removed, reparented, or its sibling/root order changes. Editor tree views and other cached
    /// hierarchy consumers key their caches on this instead of walking the whole scene per frame.
    /// </summary>
    public long StructureVersion { get { EnsureNotDisposed(); return _structureVersion; } }

    /// <summary>Advances <see cref="StructureVersion"/>. Called by topology mutation paths only.</summary>
    internal void BumpStructureVersion() => _structureVersion++;

    public struct FogParams
    {
        public enum FogMode
        {
            Off,
            Linear,
            Exponential,
            ExponentialSquared
        }
        public FogMode Mode = FogMode.ExponentialSquared;
        public Color Color = new(0.5f, 0.5f, 0.5f, 1.0f);
        public float Start = 20;
        public float End = 100;
        public float Density = 0.01f;

        public bool IsFogLinear => Mode == FogMode.Linear;

        public FogParams()
        {
        }
    }

    public FogParams Fog = new();

    public struct AmbientLightParams
    {
        public enum AmbientMode
        {
            Uniform,
            Hemisphere
        }

        public AmbientMode Mode = AmbientMode.Uniform;

        public float Strength = 1f;

        // Uniform ambient
        public Float4 Color = new(0.43f, 0.55f, 0.65f, 1.0f);

        // Hemisphere ambient
        public Float4 SkyColor = new(0.3f, 0.3f, 0.4f, 1.0f);
        public Float4 GroundColor = new(0.2f, 0.2f, 0.2f, 1.0f);

        public bool UseHemisphere => Mode == AmbientMode.Hemisphere;

        public AmbientLightParams()
        {
        }
    }

    public AmbientLightParams Ambient = new();

    /// <summary>
    /// Per-scene realtime global illumination settings (Surface Cache GI, screen-space tier).
    /// These are the scene-level baseline; an active <see cref="Rendering.Volume"/> with a
    /// <c>GlobalIlluminationVolumeComponent</c> overrides them spatially for blendable control.
    /// </summary>
    public struct GiParams
    {
        public bool Enable;

        public float Intensity = 1f;

        /// <summary>Maximum bounce distance in world units. Larger = wider light spread, more leaks.</summary>
        public float Radius = 2.5f;

        /// <summary>0 = Full, 1 = Half, 2 = Quarter (gather resolution; composited at full res).</summary>
        public int Resolution = 1;

        public bool UseTemporal = true;
        public float TemporalResponse = 0.9f;
        public float BlurRadius = 1.0f;

        /// <summary>Dynamic irradiance probe grid cell size in world units (probe tier).</summary>
        public float ProbeSpacing = 2f;

        /// <summary>Probe tiles refreshed per frame (round-robin; capped by
        /// <see cref="Rendering.ShaderConfig.MaxGiProbeUpdatesPerFrame"/>).</summary>
        public int ProbesPerFrame = 16;

        /// <summary>Debug overlay: 0 = off, 1 = irradiance, 2 = albedo capture, 3 = AO + hit rate.</summary>
        public int DebugView;

        public GiParams()
        {
        }
    }

    public GiParams Gi = new();

    /// <summary>
    /// ★ Layer 0 (A1) — Provenance carried on the scene itself: who last edited it, why, when, and
    /// via what tool. This is the scene-level twin of prefab override <c>Reason</c>. Previously a
    /// .scene file recorded only final state (like Unity YAML); an AI could read "the fog is orange"
    /// but never "it was set to dusk for cutscene X by the level designer". Empty fields deserialize
    /// to defaults for backward compatibility (Echo has no schema), so old scenes load unchanged.
    /// </summary>
    public SceneProvenance Provenance = new();

    /// <summary>
    /// Provenance metadata stored alongside a scene's serialized state. Plain struct so it
    /// serializes as a nested compound on the scene with zero ceremony.
    /// </summary>
    [Serializable]
    public struct SceneProvenance
    {
        /// <summary>Who last meaningfully edited the scene: a username, "editor", or an AI tool.</summary>
        public string Author;
        /// <summary>Natural-language intent for the last change ("dusk lighting for the intro cutscene").</summary>
        public string Intent;
        /// <summary>What produced the change: "editor", "xengine-cli.scene-object-patch", ...</summary>
        public string Source;
        /// <summary>UTC ticks of the last edit, or 0 when pre-provenance.</summary>
        public long LastEditAtUtcTicks;
        /// <summary>Optional stable id of the parent change this scene state builds on (delta-chain link).</summary>
        public string ParentDeltaId;
    }

    public enum SkyboxMode
    {
        Procedural,
        SolidColor,
        Gradient,
        Material
    }

    public struct SkyboxParams
    {
        public SkyboxMode Mode = SkyboxMode.Procedural;
        public Color SolidColor = new(0.2f, 0.3f, 0.5f, 1f);
        public Color GradientTop = new(0.4f, 0.6f, 0.9f, 1f);
        public Color GradientBottom = new(0.8f, 0.8f, 0.7f, 1f);
        public float GradientExponent = 1f;
        public AssetRef<Resources.Material> CustomMaterial;

        public SkyboxParams() { }
    }

    public SkyboxParams Skybox = new();

    /// <summary>Baked lightmaps + light-probe data for this scene, produced by the editor lightmap bake.</summary>
    public sealed class BakedLightingData
    {
        /// <summary>Baked lightmap atlas pages (RGBM-encoded). A renderer's <c>LightmapIndex</c> selects one.</summary>
        public List<AssetRef<Texture2D>> Lightmaps = new();
        /// <summary>World-space light-probe positions.</summary>
        public Float3[] ProbePositions = [];
        /// <summary>Baked SH per probe, indexed with <see cref="ProbePositions"/>.</summary>
        public SphericalHarmonicsL2[] ProbeSH = [];
        /// <summary>Tetrahedralization of the probes: 4 probe indices per tetrahedron.</summary>
        public int[] ProbeTetrahedra = [];
        /// <summary>Per-tetra neighbour links: 4 per tetra (across the face opposite vertex i), -1 = hull.</summary>
        public int[] ProbeTetNeighbours = [];

        public bool HasLightmaps => Lightmaps.Count > 0;
        public bool HasProbes => ProbeSH.Length > 0;
    }

    public BakedLightingData BakedLighting = new();

    /// <summary>
    /// Per-scene lightmapper configuration, edited in the editor's Environment panel and consumed by
    /// the bake. Persisted with the scene (it's a public field) so a bake's settings survive editor
    /// reloads.
    /// </summary>
    public sealed class LightmapBakeSettings
    {
        // Atlas / resolution
        public int AtlasSize = 1024;
        public float TexelsPerUnit = 20f;
        public int DilatePixels = 2;          // edge dilation to stop bilinear bleed at seams

        // Quality
        public int Bounces = 2;
        public int Samples = 64;              // progressive indirect iterations before finalize
        public int ProbeSamples = 256;
        public bool DoBackfaceCull = false;   // cull back faces on all bake rays (matches XEngine's backface-culled rendering)
        public float RussianRoulette = 0f;    // 0 = off

        // Edge-avoiding denoiser (runs once at finalize); geometry-guided only.
        public bool Denoise = false;
        public int DenoiseRadius = 5;         // a-trous pass count; each step ~doubles the smoothing reach (~2^N texels)

        // Feed the scene's ambient colour in as ray-miss (sky) radiance.
        public bool BakeSkyLighting = false;

        // Debug: bake every surface as a white Lambertian (isolates light/GI from albedo).
        public bool IgnoreAlbedo = false;
    }

    public LightmapBakeSettings LightmapBake = new();

    [NonSerialized] private LightProbeVolume? _probeVolume;

    /// <summary>Runtime probe sampler built from <see cref="BakedLighting"/> (lazy). Null when there are no baked probes.</summary>
    public LightProbeVolume? ProbeVolume
    {
        get
        {
            EnsureNotDisposed();
            if (_probeVolume == null && BakedLighting.HasProbes)
                _probeVolume = new LightProbeVolume(BakedLighting.ProbePositions, BakedLighting.ProbeSH,
                                                    BakedLighting.ProbeTetrahedra, BakedLighting.ProbeTetNeighbours);
            return _probeVolume;
        }
    }

    /// <summary>Drop the cached probe volume so the next access rebuilds it (call after a rebake).</summary>
    public void InvalidateProbeVolume() { EnsureNotDisposed(); _probeVolume = null; }

    /// <summary> The number of registered, non-disposed objects. </summary>
    public int Count
    {
        get
        {
            EnsureNotDisposed();
            return _allObj.Count(static obj => !obj.IsDisposed);
        }
    }

    /// <summary> Enumerates all registered objects. </summary>
    public IEnumerable<GameObject> AllObjects
    {
        get
        {
            EnsureNotDisposed();
            foreach (GameObject obj in _allObj)
                if (!obj.IsDisposed)
                    yield return obj;
        }
    }

    /// <summary> Enumerates all registered objects that are currently active and saveable. </summary>
    public IEnumerable<GameObject> SaveableObjects
    {
        get
        {
            EnsureNotDisposed();
            foreach (GameObject obj in _allObj)
                if (!obj.IsDisposed && !obj.HideFlags.HasFlag(HideFlags.DontSave) && !obj.HideFlags.HasFlag(HideFlags.HideAndDontSave))
                    yield return obj;
        }
    }

    /// <summary> Enumerates all registered objects that are currently active. </summary>
    public IEnumerable<GameObject> ActiveObjects
    {
        get
        {
            EnsureNotDisposed();
            foreach (GameObject obj in _allObj)
                if (!obj.IsDisposed && obj.EnabledInHierarchy)
                    yield return obj;
        }
    }

    /// <summary> Enumerates all root GameObjects, i.e. all GameObjects without a parent object. </summary>
    public IEnumerable<GameObject> RootObjects
    {
        get
        {
            EnsureNotDisposed();
            foreach (GameObject obj in _allObj)
                if (!obj.IsDisposed && obj.Transform.Parent == null)
                    yield return obj;
        }
    }

    /// <summary> Enumerates all <see cref="RootObjects"/> that are currently active. </summary>
    public IEnumerable<GameObject> ActiveRootObjects
    {
        get
        {
            EnsureNotDisposed();
            foreach (GameObject obj in _allObj)
                if (!obj.IsDisposed && obj.Transform.Parent == null && obj.EnabledInHierarchy)
                    yield return obj;
        }
    }

    /// <summary> Returns whether this Scene is completely empty. </summary>
    public bool IsEmpty
    {
        get
        {
            EnsureNotDisposed();
            foreach (GameObject obj in _allObj)
                if (!obj.IsDisposed)
                    return false;
            return true;
        }
    }

    /// <summary> Returns whether this scene is currently active. </summary>
    public bool IsActive { get { EnsureNotDisposed(); return _isActive; } }

    /// <summary>
    /// Creates a new, empty scene which does not contain any <see cref="GameObject">GameObjects</see>.
    /// </summary>
    public Scene()
    {
    }

    /// <summary>
    /// Enables this scene, triggering OnEnable callbacks for its components.
    /// For most use cases, prefer using Scene.Load() instead of calling Enable() directly.
    /// </summary>
    public void Enable()
    {
        EnsureNotDisposed();
        if (_isActive) throw new Exception("Scene is already enabled!");

        _isActive = true;

        // Create a copy to avoid collection modification during enumeration
        List<GameObject> allObjectsCopy = [.. AllObjects];

        // Trigger OnEnable for all enabled components in the scene. Slow components (>200ms)
        // are logged with their type + object name — a play-start wall of these is a startup
        // regression signal (play entered via EditorApplication.EnterPlayMode).
        var enableSw = System.Diagnostics.Stopwatch.StartNew();
        foreach (GameObject go in allObjectsCopy)
        {
            if (go.IsDisposed) continue;

            if (go.EnabledInHierarchy)
            {
                // Create a copy of components to avoid modification during enumeration
                MonoBehaviour[] components = [.. go.GetComponents<MonoBehaviour>()];
                foreach (MonoBehaviour component in components)
                {
                    if (component.IsDisposed) continue;
                    if (component.Enabled && component.EnabledInHierarchy)
                    {
                        var componentSw = System.Diagnostics.Stopwatch.StartNew();
                        component.InternalOnEnable();
                        if (componentSw.ElapsedMilliseconds > 200)
                            Debug.LogWarning($"[PlayInit] slow OnEnable: {component.GetType().Name} on '{go.Name}' took {componentSw.ElapsedMilliseconds}ms");
                    }
                }
            }
        }
        enableSw.Stop();
        Debug.Log($"[PlayInit] Scene.Enable: {allObjectsCopy.Count} objects in {enableSw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Disables this scene, triggering OnDisable callbacks for its components.
    /// For most use cases, prefer using Scene.Unload() or Scene.Load() instead of calling Disable() directly.
    /// </summary>
    public void Disable()
    {
        EnsureNotDisposed();
        if (!_isActive) throw new Exception("Scene is not enabled!");

        // Create a copy to avoid collection modification during enumeration
        List<GameObject> allObjectsCopy = [.. AllObjects];

        // Trigger OnDisable for all enabled components in the scene
        foreach (GameObject go in allObjectsCopy)
        {
            if (go.IsDisposed) continue;

            if (go.EnabledInHierarchy)
            {
                // Create a copy of components to avoid modification during enumeration
                MonoBehaviour[] components = [.. go.GetComponents<MonoBehaviour>()];
                foreach (MonoBehaviour component in components)
                {
                    if (component.IsDisposed) continue;
                    if (component.Enabled && component.EnabledInHierarchy)
                        component.InternalOnDisable();
                }
            }
        }

        _isActive = false;
    }


    /// <summary>
    /// Registers a GameObject and all of its children.
    /// </summary>
    public void Add(GameObject obj)
    {
        EnsureNotDisposed();
        if (obj.Scene.IsValid() && obj.Scene != this) obj.Scene.Remove(obj);
        AddObject(obj);
    }

    /// <summary>
    /// Move a root-level GameObject to a specific index within the root object list.
    /// Only works for root objects (no parent). Index is clamped to valid range.
    /// </summary>
    public void SetRootIndex(GameObject obj, int index)
    {
        EnsureNotDisposed();
        if (obj.Scene != this || obj.Parent.IsValid()) return;
        int current = _allObj.IndexOf(obj);
        if (current < 0) return;
        _allObj.RemoveAt(current);
        // Collect root indices to map root-order index to list index
        var rootIndices = new List<int>();
        for (int i = 0; i < _allObj.Count; i++)
            if (!_allObj[i].IsDisposed && _allObj[i].Transform.Parent == null)
                rootIndices.Add(i);
        index = Math.Max(0, Math.Min(index, rootIndices.Count));
        int insertAt = index < rootIndices.Count ? rootIndices[index] : _allObj.Count;
        _allObj.Insert(insertAt, obj);
        BumpStructureVersion();
    }

    /// <summary>
    /// Get the index of a root-level GameObject among other root objects.
    /// Returns -1 if not a root or not in this scene.
    /// </summary>
    public int GetRootIndex(GameObject obj)
    {
        EnsureNotDisposed();
        if (obj.Scene != this || obj.Parent.IsValid()) return -1;
        int rootIdx = 0;
        foreach (var go in _allObj)
        {
            if (go.IsDisposed || go.Transform.Parent != null) continue;
            if (go == obj) return rootIdx;
            rootIdx++;
        }
        return -1;
    }

    /// <summary>
    /// Unregisters a GameObject and all of its children
    /// </summary>
    public void Remove(GameObject obj)
    {
        EnsureNotDisposed();
        if (obj.Scene != this) return;
        if (obj.Parent.IsValid() && obj.Parent.Scene == this)
        {
            obj.SetParent(null);
        }
        RemoveObject(obj);
    }

    private void AddObject(GameObject obj)
    {
        if (_allObjSet.Add(obj))
        {
            _allObj.Add(obj);
            obj.Scene = this;
            BumpStructureVersion();

            // Create a copy of components to avoid modification during enumeration
            MonoBehaviour[] components = [.. obj.GetComponents<MonoBehaviour>()];

            // Call OnAddedToScene for all components
            foreach (MonoBehaviour component in components)
            {
                if (component.IsDisposed) continue;
                try { component.OnAddedToScene(); }
                catch (Exception ex) { Debug.LogError($"[{obj.Name}/{component.GetType().Name}] OnAddedToScene() threw: {ex.Message}\n{ex.StackTrace}"); }
            }

            // Call OnEnable for enabled components, but only if the scene is active
            if (IsActive && obj.EnabledInHierarchy)
            {
                foreach (MonoBehaviour component in components)
                {
                    if (component.IsDisposed) continue;
                    if (component.Enabled && component.EnabledInHierarchy)
                        component.InternalOnEnable();
                }
            }
        }

        // Create a copy to avoid modification during enumeration
        List<GameObject> children = [.. obj.Children];
        foreach (GameObject child in children)
            AddObject(child);
    }

    private void RemoveObject(GameObject obj)
    {
        // Create a copy to avoid modification during enumeration
        List<GameObject> children = [.. obj.Children];
        foreach (GameObject child in children)
            RemoveObject(child);

        if (_allObjSet.Remove(obj))
        {
            _allObj.Remove(obj);
            BumpStructureVersion();
            // Create a copy of components to avoid modification during enumeration
            MonoBehaviour[] components = [.. obj.GetComponents<MonoBehaviour>()];

            // Call OnDisable for currently enabled components (only if scene is active)
            if (IsActive && obj.EnabledInHierarchy)
            {
                foreach (MonoBehaviour component in components)
                {
                    if (component.IsDisposed) continue;
                    if (component.Enabled && component.EnabledInHierarchy)
                        component.InternalOnDisable();
                }
            }

            // Call OnRemovedFromScene for all components
            foreach (MonoBehaviour component in components)
            {
                if (component.IsDisposed) continue;
                try { component.OnRemovedFromScene(); }
                catch (Exception ex) { Debug.LogError($"[{component.Name}/{component.GetType().Name}] OnRemovedFromScene() threw: {ex.Message}\n{ex.StackTrace}"); }
            }

            obj.Scene = null;
        }
    }

    public T?[] FindObjectsOfType<T>() where T : EngineObject
    {
        EnsureNotDisposed();
        List<T> objects = [];
        foreach (GameObject go in AllObjects)
        {
            if (go is T t)
                objects.Add(t);

            foreach (MonoBehaviour comp in go.GetComponents<MonoBehaviour>())
                if (comp is T t2)
                    objects.Add(t2);
        }
        return [.. objects];
    }

    public T? FindObjectByID<T>(int id) where T : EngineObject
    {
        EnsureNotDisposed();
        foreach (GameObject go in AllObjects)
        {
            if (go.InstanceID == id)
                return go as T;
            foreach (MonoBehaviour comp in go.GetComponents<MonoBehaviour>())
                if (comp.InstanceID == id)
                    return comp as T;
        }
        return null;
    }

    public T? FindObjectByIdentifier<T>(Guid identifier) where T : EngineObject
    {
        EnsureNotDisposed();
        foreach (GameObject go in AllObjects)
        {
            if (go.Identifier == identifier)
                return go as T;
            foreach (MonoBehaviour comp in go.GetComponents<MonoBehaviour>())
                if (comp.Identifier == identifier)
                    return comp as T;
        }
        return null;
    }

    /// <summary> Unregisters all GameObjects. </summary>
    public void Clear()
    {
        EnsureNotDisposed();
        // Create a copy to iterate over since RemoveObject modifies the collection
        List<GameObject> rootObjects = [.. RootObjects];
        foreach (GameObject obj in rootObjects)
        {
            Remove(obj);
        }
    }

    /// <summary> Unregisters all dead / disposed GameObjects </summary>
    public void Flush()
    {
        EnsureNotDisposed();
        for (int i = _allObj.Count - 1; i >= 0; i--)
        {
            GameObject obj = _allObj[i];
            if (!obj.IsDisposed)
                continue;

            _allObj.RemoveAt(i);
            _allObjSet.Remove(obj);
            obj.Scene = null;
            BumpStructureVersion();
        }
    }

    public override void OnDispose()
    {
        base.OnDispose();

        // Clear the current scene reference if this is the current scene
        if (Current == this)
            Current = null;

        // Scene-scoped locks auto-expire with the scene rather than leaking forever.
        AssetDatabase.ReleaseSceneLocks(this);

        // Clear the physics worlds
        _physics?.Clear();
        _physics2D?.Clear();

        // Dispose all GameObjects which will also remove them from the scene. Dispose() (not the raw
        // OnDispose() body) sets IsDisposed and is idempotent, so the flat list's double-hits on
        // already-disposed children are no-ops.
        List<GameObject> allObjects = [];
        foreach (GameObject obj in _allObj)
            if (!obj.IsDisposed)
                allObjects.Add(obj);
        foreach (GameObject g in allObjects)
            g.Dispose();

        // Clear any remaining references
        _allObj.Clear();
        _allObjSet.Clear();
        // Disposing the objects unregistered them, but the registry keeps its sorted snapshots until
        // the next iteration - which never comes for a disposed scene. Drop them so a panel or Paper
        // closure that still points at this Scene cannot pin user-script components (hot-reload).
        _componentRegistry.Clear();

        // Remove all identifiers and reference to any possible gameobject that could hold a
        // user-defined script as it might leave the ALC alive
        serializeObj = null;
        _goIdentifiers = null;
        _compIdentifiers = null;
        _compIdOffsets = null;
    }

    public void OnBeforeSerialize()
    {
        serializeObj = [.. AllObjects];

        // Capture identifiers so they can be restored after deserialization
        _goIdentifiers = new Guid[serializeObj.Length];
        var compIds = new List<Guid>();
        _compIdOffsets = new int[serializeObj.Length + 1];

        for (int i = 0; i < serializeObj.Length; i++)
        {
            _goIdentifiers[i] = serializeObj[i].Identifier;
            _compIdOffsets[i] = compIds.Count;
            foreach (var comp in serializeObj[i].GetComponents<MonoBehaviour>())
                compIds.Add(comp.Identifier);
        }
        _compIdOffsets[serializeObj.Length] = compIds.Count;
        _compIdentifiers = compIds.ToArray();
    }

    public void OnAfterDeserialize()
    {
        if (serializeObj == null) return;

        // Restore identifiers GOs and components got fresh IDs during deserialization
        if (_goIdentifiers != null && _goIdentifiers.Length == serializeObj.Length)
        {
            for (int i = 0; i < serializeObj.Length; i++)
            {
                // A flat scene list can contain a reference stub for a GameObject which is already
                // represented in a parent's Children array. If the referenced object could not be
                // resolved during deserialization, Echo supplies a null slot. Preserve the other
                // objects and their parallel identifier offsets instead of failing the whole scene.
                GameObject? obj = serializeObj[i];
                if (obj == null)
                    continue;

                obj.SetIdentifier(_goIdentifiers[i]);

                if (_compIdentifiers != null && _compIdOffsets != null)
                {
                    int start = _compIdOffsets[i];
                    int end = _compIdOffsets[i + 1];
                    var comps = new List<MonoBehaviour>();
                    foreach (MonoBehaviour component in obj.GetComponents<MonoBehaviour>())
                        comps.Add(component);
                    for (int c = 0; c < comps.Count && start + c < end; c++)
                        comps[c].Identifier = _compIdentifiers[start + c];
                }
            }
        }

        // Identity uniqueness pass: live objects must own unique GO and component identifiers —
        // hierarchy rows, undo lookups and identifier search all key on them. Scenes saved while
        // clone paths still shared source identities (prowl-sync M4.2 regression) carry exact
        // duplicates; the first object keeps its id and later twins get fresh ones, so the fix
        // persists on the next save.
        if (serializeObj != null)
        {
            HashSet<Guid> seenIds = new();
            foreach (GameObject? obj in serializeObj)
            {
                if (obj == null) continue;
                if (!seenIds.Add(obj.Identifier))
                    obj.SetIdentifier(Guid.NewGuid());

                foreach (MonoBehaviour component in obj.GetComponents<MonoBehaviour>())
                    if (!seenIds.Add(component.Identifier))
                        component.Identifier = Guid.NewGuid();
            }
        }

        // Clear temp data
        _goIdentifiers = null;
        _compIdentifiers = null;
        _compIdOffsets = null;

        foreach (GameObject? obj in serializeObj)
            if (obj != null)
                Add(obj);
    }

    /// <summary>
    /// Runs Start, then Update, then LateUpdate for the scene's registered components (those that
    /// implement those callbacks and are enabled in an active scene), in execution order. Each call
    /// is still gated per-component by ShouldExecuteGameplay.
    /// </summary>
    public void Update()
    {
        EnsureNotDisposed();
        _componentRegistry.RunStart();

        // A component callback can synchronously load another scene (UI click -> gate ->
        // Scene.Load), which disposes THIS scene while its own Update is still on the stack.
        // Bail out instead of running the remaining phases against a disposed scene.
        if (IsDisposed) return;
        _componentRegistry.RunUpdate();
        if (IsDisposed) return;
        _componentRegistry.RunLateUpdate();
        if (IsDisposed) return;

        Flush();
    }

    /// <summary>
    /// Executes physics update on all active GameObjects and their components.
    /// FixedUpdate is gated internally by each component's ShouldExecuteGameplay.
    /// </summary>
    public void FixedUpdate()
    {
        EnsureNotDisposed();
        // Start must run before a component's first FixedUpdate. The loop runs FixedUpdate before
        // Update, so drive Start here too (RunStart is idempotent - it only starts un-started ones).
        _componentRegistry.RunStart();
        if (IsDisposed) return;

        // A solver blow up (NaN or Inf transforms, degenerate collider) must not crash the frame.
        // Worlds that were never touched (no bodies, colliders or queries) do not step at all.
        if (_physics != null)
            try { _physics.Update(); }
        catch (Exception ex) { Debug.LogError($"[Physics] Step threw and was skipped this frame: {ex.Message}\n{ex.StackTrace}"); }

        if (_physics2D != null)
            try { _physics2D.Update(); }
        catch (Exception ex) { Debug.LogError($"[Physics2D] Step threw and was skipped this frame: {ex.Message}\n{ex.StackTrace}"); }

        // Same mid-step disposal hazard as Update: a fixed-step callback can load a scene.
        if (IsDisposed) return;
        _componentRegistry.RunFixedUpdate();
        if (IsDisposed) return;

        Flush();
    }

    /// <summary>
    /// Collects render data from all active components for the given camera.
    /// Components add their renderables and lights to the provided lists.
    /// </summary>
    public void CollectRenderables(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        EnsureNotDisposed();
        SpriteRendererRegistry.BeginCollect();
        _componentRegistry.RunRenderCollect(camera, renderables, lights);
        SpriteRendererRegistry.EndCollect(renderables);
    }

    /// <summary>
    /// Draws gizmos for all active GameObjects and their components.
    /// </summary>
    public void DrawGizmos()
    {
        EnsureNotDisposed();
        _componentRegistry.RunDrawGizmos();
        if (IsDisposed) return;

        Flush();
    }

    /// <summary>
    /// Executes GUI update on all active GameObjects and their components.
    /// Calls OnGUI.
    /// </summary>
    public void OnGui(Paper paper)
    {
        EnsureNotDisposed();
        _componentRegistry.RunOnGui(paper);
        if (IsDisposed) return;

        Flush();
    }

    /// <summary>
    /// Renders cameras in this scene: Base cameras by depth, each followed by its overlay stack.
    /// Overlay cameras share the Base color target and blit once after the last camera in the stack.
    /// </summary>
    /// <param name="target">Optional render target to render into</param>
    /// <returns>True if any cameras were rendered, false otherwise</returns>
    public bool Render(RenderTexture? target = null)
    {
        EnsureNotDisposed();

        _collectedCameras.Clear();
        _seenCameras.Clear();
        // _allObj is a flat list of every object (children included), so scanning each object's
        // own component list visits every camera exactly once. The previous per-object
        // GetComponentsInChildren recursion re-walked each subtree once per ancestor and
        // allocated a nested iterator chain per object per frame.
        foreach (GameObject obj in _allObj)
        {
            if (obj.IsDisposed || !obj.EnabledInHierarchy)
                continue;
            List<MonoBehaviour> components = obj._components;
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] is Camera camera && _seenCameras.Add(camera))
                    _collectedCameras.Add(camera);
            }
        }

        Camera.BuildRenderOrder(_collectedCameras, _baseCameras, _orderedCameras, _usedOverlays);

        if (_orderedCameras.Count == 0)
            return false;

        bool ownsRenderStatsFrame = !RenderStats.IsFrameActive;
        if (ownsRenderStatsFrame)
            RenderStats.BeginFrame();

        try
        {
            for (int i = 0; i < _orderedCameras.Count; i++)
            {
                Camera cam = _orderedCameras[i];
                bool shareColor = cam.RenderType == CameraRenderType.Overlay;
                bool skipBlit = i + 1 < _orderedCameras.Count
                    && _orderedCameras[i + 1].RenderType == CameraRenderType.Overlay;

                try
                {
                    RenderPipeline pipeline = cam.Pipeline ?? UniversalRenderPipeline.Default;
                    var data = new RenderingData
                    {
                        StackShareColor = shareColor,
                        StackSkipFinalBlit = skipBlit,
                    };

                    if (target.IsValid() && cam.Target.IsNotValid())
                    {
                        try { cam.Target = target; pipeline.Render(cam, data); }
                        finally { cam.Target = null; }
                    }
                    else
                    {
                        pipeline.Render(cam, data);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Render] Camera '{cam.GameObject?.Name}' render threw and was skipped: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }
        finally
        {
            if (ownsRenderStatsFrame)
                RenderStats.EndFrame();
        }

        return true;
    }

}
