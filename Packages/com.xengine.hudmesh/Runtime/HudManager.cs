using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>
/// Owns every world-space HUD, combines them into one dynamic mesh per material, and submits
/// the meshes as <see cref="MeshRenderable"/>s during <see cref="OnRenderCollect"/>. Port of
/// UnityHUD's HUDManager; UnityHUD's vertex-shader billboard expansion is done on the CPU
/// here against the camera basis, which keeps frustum culling correct for free.
/// </summary>
[AddComponentMenu("Hudmesh/Hud Manager")]
public sealed class HudManager : MonoBehaviour
{
    /// <summary>World units one layout pixel spans at scale 1 (matches UnityHUD's PPU and TextMeshComponent's default).</summary>
    public const float DefaultPixelsPerUnit = 100f;

    public static HudManager? Instance { get; private set; }

    public HudSpriteTable Sprites { get; private set; } = new();

    /// <summary>Active configuration (hot-swappable via <see cref="ChangeConfig"/>).</summary>
    public HudmeshConfig Config { get; private set; } = new();

    public float PixelsPerUnit = DefaultPixelsPerUnit;

    /// <summary>Vertical gap each sprite slot contributes to the stack (kept in sync with the config).</summary>
    public float SpriteLineGap => Config.SpriteLineGap;

    /// <summary>Vertical gap each text slot contributes to the stack.</summary>
    public float TextLineGap => Config.TextLineGap;

    /// <summary>
    /// false (default) draws sprites with <c>Hudmesh/SpriteTop</c> (ZTest Off — always on top,
    /// the UnityHUD default); true uses <c>Hudmesh/SpriteOccluded</c> (ZTest LEqual). Text is
    /// always depth-tested (UnityHUD parity).
    /// </summary>
    public bool OccludeByScene;

    /// <summary>Global toggle — hides every HUD without destroying them (cutscenes).</summary>
    public bool Active = true;

    /// <summary>Floating-number styles indexable by <see cref="Hud.PopNumber"/> (UnityHUD config.NumberTypes).</summary>
    public IReadOnlyList<HudNumberStyle> NumberStyles => Config.NumberStyles;

    /// <summary>Hot-swaps the active configuration (UnityHUD ChangeConfig); existing HUDs restyle on their next update.</summary>
    public void ChangeConfig(HudmeshConfig config)
    {
        Config = config ?? new HudmeshConfig();
    }

    private Camera? _lastCamera; // most recent render camera; used for camera-space spawn offsets

    internal Camera? ResolveCamera() => _lastCamera;

    private readonly Dictionary<long, Hud> _huds = new();
    private readonly List<HudObject> _objects = new();
    private readonly List<HudObject> _sorted = new();
    private readonly List<HudMeshData?> _meshData = new(); // [0] = font (text milestone), [1..N] = textures
    private readonly Dictionary<Texture2D, int> _textureSlots = new();

    public static HudManager Init(HudSpriteTable? sprites = null)
    {
        if (Instance is not null)
        {
            if (sprites is not null)
                Instance.Sprites = sprites;
            return Instance;
        }

        HudManager manager = new GameObject("HudManager").AddComponent<HudManager>();
        if (sprites is not null)
            manager.Sprites = sprites;
        Instance = manager;
        return manager;
    }

    public Hud CreateHud(long id)
    {
        if (_huds.TryGetValue(id, out Hud? existing))
        {
            Debug.LogWarning($"[hudmesh] HUD id {id} already exists; returning the existing handle.");
            return existing;
        }

        Hud hud = new(id) { Manager = this };
        _huds.Add(id, hud);
        return hud;
    }

    public bool DeleteHud(long id)
    {
        if (!_huds.Remove(id, out Hud? hud))
            return false;
        if (hud.Object is not null)
        {
            hud.Object.Release();
            HudPool<HudObject>.Push(hud.Object);
            _objects.Remove(hud.Object);
            hud.Object = null;
        }
        hud.Manager = null;
        return true;
    }

    internal void Attach(Hud hud, Transform target, Float3 worldOffset, Float2 pixelOffset)
    {
        HudObject obj = hud.Object ?? HudPool<HudObject>.Pop();
        obj.Init(this, hud.Id, target, worldOffset, pixelOffset);
        if (hud.Object is null)
        {
            hud.Object = obj;
            _objects.Add(obj);
        }
    }

    /// <summary>Assigns (lazily) the combined-mesh slot for a sprite's texture. Slot 0 stays reserved for the font.</summary>
    internal int ResolveMaterialSlot(HudSpriteInfo sprite)
    {
        if (sprite.Texture is null)
            return -1;
        if (_textureSlots.TryGetValue(sprite.Texture, out int slot))
            return slot;

        slot = _meshData.Count;
        if (slot == 0)
        {
            _meshData.Add(null); // reserve slot 0 for the SDF font material
            slot = 1;
        }
        _meshData.Add(CreateMeshData(sprite.Texture));
        _textureSlots[sprite.Texture] = slot;
        return slot;
    }

    private Material? _fontMaterial;

    /// <summary>Creates the slot-0 combined mesh (shared SDF font material) on first text use.</summary>
    internal void EnsureFontSlot()
    {
        if (_meshData.Count == 0)
            _meshData.Add(null);
        if (_meshData[0] is not null)
            return;

        _fontMaterial ??= new Material(Shader.LoadDefault(DefaultShader.DefaultTextMesh));
        _meshData[0] = new HudMeshData { Texture = null, Material = _fontMaterial };
    }

    private HudMeshData CreateMeshData(Texture2D texture)
    {
        Shader shader = OccludeByScene
            ? HudmeshAssetLibrary.LoadSpriteOccludedShader()
            : HudmeshAssetLibrary.LoadSpriteTopShader();
        return new HudMeshData
        {
            Texture = texture,
            Material = new Material(shader),
        };
    }

    private void LateUpdate()
    {
        float now = Time.TimeSinceStartup;
        for (int i = 0; i < _objects.Count; i++)
            _objects[i].Tick(now);
    }

    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        _lastCamera = camera;
        if (!Active || _objects.Count == 0)
            return;

        // Painter's sort, far → near, so overlapping quads inside one combined mesh layer correctly.
        Float3 cameraPosition = camera.Transform.Position;
        _sorted.Clear();
        for (int i = 0; i < _objects.Count; i++)
        {
            HudObject obj = _objects[i];
            Float3 delta = obj.RolePos - cameraPosition;
            obj.SortKey = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;
            if (obj.HasVisibleContent)
                _sorted.Add(obj);
        }
        if (_sorted.Count == 0)
            return;
        _sorted.Sort(static (a, b) => b.SortKey.CompareTo(a.SortKey));

        float unit = 1f / float.Max(0.0001f, PixelsPerUnit);
        HudFillContext context = new()
        {
            RightStep = camera.Transform.Right * unit,
            UpStep = camera.Transform.Up * unit,
        };

        for (int i = 0; i < _meshData.Count; i++)
            _meshData[i]?.BeginFrame();

        for (int i = 0; i < _sorted.Count; i++)
            _sorted[i].Fill(_meshData, context);

        for (int i = 0; i < _meshData.Count; i++)
        {
            HudMeshData? data = _meshData[i];
            if (data is null || !data.HasContent)
                continue;

            data.Props.Clear();
            // Sprite slots carry their own texture; the font slot (Texture null) draws the
            // shared SDF glyph atlas, which may be reallocated when it grows.
            Texture2D? tex = data.Texture ?? UIFontSystem.Default.Atlas;
            data.Props.SetTexture("_MainTex", tex ?? Texture2D.LoadDefault(DefaultTexture.White));
            data.Props.SetColor("_MainColor", Color.White);
            data.Props.SetInt("_ObjectID", 0);
            data.SealAndUpload();

            MeshRenderable renderable = new(data.Mesh, data.Material, Float4x4.Identity, GameObject.LayerIndex, data.Props);
            renderable.SetOwner(GameObject);
            renderables.Add(renderable);
        }
    }

    public override void OnDispose()
    {
        for (int i = 0; i < _objects.Count; i++)
        {
            _objects[i].Release();
            HudPool<HudObject>.Push(_objects[i]);
        }
        _objects.Clear();
        _huds.Clear();
        _sorted.Clear();

        for (int i = 0; i < _meshData.Count; i++)
            _meshData[i]?.Dispose();
        _meshData.Clear();
        _textureSlots.Clear();

        if (Instance == this)
            Instance = null;
    }
}
