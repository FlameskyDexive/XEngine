using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

using XEngine.Runtime.Resources;

namespace XEngine.Runtime;

/// <summary>
/// Registry of built-in assets (embedded in the runtime assembly).
/// Provides deterministic GUIDs so they can be referenced like any other asset.
/// Works at both runtime and editor level.
/// </summary>
public static class BuiltInAssets
{
    public struct BuiltInEntry
    {
        public Guid Guid;
        public string Name;
        public string Path; // e.g. "$Default:Standard"
        public Type AssetType;
        public Func<EngineObject> Loader;
        /// <summary>Owning optional package, or null for engine / legacy registrations.</summary>
        public string? PackageName;
    }

    private static readonly Dictionary<Guid, BuiltInEntry> _entries = new();
    private static readonly Dictionary<Guid, EngineObject> _cache = new();
    private static readonly object _initializationGate = new();
    private static bool _initialized;

    public static IReadOnlyDictionary<Guid, BuiltInEntry> Entries => _entries;

    /// <summary>
    /// Generate a deterministic GUID from a built-in asset path.
    /// Always produces the same GUID for the same path string.
    /// </summary>
    public static Guid DeterministicGuid(string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        // Set version bits to indicate this is a name-based UUID (version 5-like)
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Initialize and register all built-in assets. Safe to call multiple times.
    /// </summary>
    public static void Initialize()
    {
        lock (_initializationGate)
        {
            if (_initialized)
                return;

            // Shaders loader calls the raw parse so LoadDefault can route through the cache
            // without recursing through itself.
            foreach (DefaultShader s in Enum.GetValues<DefaultShader>())
            {
                var shader = s;
                RegisterCore($"$Default:Shader/{shader}", shader.ToString(), typeof(Shader),
                    () => Shader.ParseDefault(shader));
            }


            // Default meshes (parsed directly from embedded OBJ files)
            foreach (DefaultModel m in Enum.GetValues<DefaultModel>())
            {
                var model = m;
                string fileName = model switch
                {
                    DefaultModel.Cube => "Cube.obj",
                    DefaultModel.Sphere => "Sphere.obj",
                    DefaultModel.Cylinder => "Cylinder.obj",
                    DefaultModel.Plane => "Plane.obj",
                    DefaultModel.SkyDome => "SkyDome.obj",
                    _ => null
                };
                if (fileName == null) continue;

                RegisterCore($"$Default:Model/{model}/Mesh/0", model.ToString(), typeof(Mesh),
                    () =>
                    {
                        using var stream = EmbeddedResources.GetStream($"Assets/Defaults/{fileName}");
                        // Built-in primitives always get lightmap UV2 so they're lightmappable out of the
                        // box. The SkyDome is the skybox mesh (never lightmapped), so it's skipped.
                        var importResult = new AssetImporting.ModelImporter().Import(stream, fileName, new AssetImporting.ModelImporterSettings() { RecalculateNormals = true, GenerateNormals = true, GenerateSmoothNormals = true, CalculateTangentSpace = true, GenerateLightmapUVs = model != DefaultModel.SkyDome });
                        var mesh = importResult.Meshes.Count > 0 ? importResult.Meshes[0] : new Mesh { Name = model.ToString() };
                        mesh.AssetID = GuidForMesh(model);
                        mesh.AssetPath = $"$Default:Mesh/{model}";
                        return mesh;
                    });
            }

            // Materials register the raw parse so LoadDefault routes through this cache.
            foreach (DefaultMaterial m in Enum.GetValues<DefaultMaterial>())
            {
                var mat = m;
                RegisterCore($"$Default:Material/{mat}", mat.ToString(), typeof(Material),
                    () => Material.ParseDefault(mat));
            }

            // Textures same: raw load, shared instance.
            foreach (DefaultTexture t in Enum.GetValues<DefaultTexture>())
            {
                var tex = t;
                RegisterCore($"$Default:Texture/{tex}", tex.ToString(), typeof(Texture2D),
                    () => Texture2D.ParseDefault(tex));
            }

            // Sprites: built from a default texture, shared instance.
            foreach (DefaultSprite sp in Enum.GetValues<DefaultSprite>())
            {
                var sprite = sp;
                RegisterCore($"$Default:Sprite/{sprite}", sprite.ToString(), typeof(Sprite),
                    () => Sprite.ParseDefault(sprite));
            }

            // Fonts: raw load, shared instance (fallback for UI text with no font assigned).
            foreach (DefaultFont f in Enum.GetValues<DefaultFont>())
            {
                var font = f;
                RegisterCore($"$Default:Font/{font}", font.ToString(), typeof(FontAsset),
                    () => FontAsset.ParseDefault(font));
            }

            _initialized = true;
        }
    }

    /// <summary>
    /// Registers a packaged built-in (same GUID scheme as core defaults). Safe after
    /// <see cref="Initialize"/> so optional packages can restore extracted assets.
    /// </summary>
    public static void Register(string path, string name, Type type, Func<EngineObject> loader)
        => RegisterEntry(path, name, type, loader, null);

    /// <summary>
    /// Registers a package-owned default. Build inventories include it only when that package is
    /// resolved by the project; the asset remains globally resolvable for compatibility references.
    /// </summary>
    public static void Register(string path, string name, Type type, Func<EngineObject> loader, string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        RegisterEntry(path, name, type, loader, packageName);
    }

    private static void RegisterEntry(string path, string name, Type type, Func<EngineObject> loader, string? packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(loader);
        Initialize();
        lock (_initializationGate)
            RegisterCore(path, name, type, loader, packageName);
    }

    private static void RegisterCore(string path, string name, Type type, Func<EngineObject> loader, string? packageName = null)
    {
        var guid = DeterministicGuid(path);
        _entries[guid] = new BuiltInEntry
        {
            Guid = guid,
            Name = name,
            Path = path,
            AssetType = type,
            Loader = loader,
            PackageName = packageName,
        };
    }

    /// <summary>
    /// Try to resolve a built-in asset by GUID. Returns null if not a built-in asset.
    /// Caches loaded assets.
    /// </summary>
    public static EngineObject? Get(Guid guid)
    {
        Initialize();

        if (!_entries.TryGetValue(guid, out var entry))
            return null;

        // _entries is populated once in Initialize() and read-only afterward, but _cache is
        // filled lazily and is now hit from multiple threads (render thread texture fallback,
        // AssetLoader background thread, main thread). Guard it so a built-in is created once.
        lock (_cache)
        {
            if (_cache.TryGetValue(guid, out var cached) && cached != null && !cached.IsDisposed)
                return cached;

            try
            {
                var obj = entry.Loader();
                if (obj != null)
                {
                    obj.AssetID = guid;
                    obj.AssetPath = entry.Path;
                    obj.Name = entry.Name;
                    _cache[guid] = obj;
                }
                return obj;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to load built-in asset '{entry.Path}': {ex.Message}");
                return null;
            }
        }
    }

    internal static void DisposeCached()
    {
        lock (_cache)
        {
            foreach (EngineObject asset in _cache.Values)
                asset.Dispose();
            _cache.Clear();
        }
    }

    /// <summary>
    /// Hot-reload: drops the registrations whose loader lambda lives in a collectible package
    /// assembly (Spine/Terrain shader libraries register from <c>[OnAssemblyLoad]</c>). A delegate
    /// bound to a collectible static method carries its LoaderAllocator, so one such entry in
    /// <see cref="_entries"/> kept the whole script context alive across every unload. The packages
    /// re-register from their own <c>[OnAssemblyLoad]</c> after the new assemblies load; cached
    /// objects produced by those loaders are disposed so the next Get rebuilds them.
    /// </summary>
    [OnAssemblyUnload]
    internal static void RemoveCollectibleRegistrations()
    {
        lock (_initializationGate)
        {
            List<Guid>? removed = null;
            foreach (var (guid, entry) in _entries)
            {
                if (!XEngine.Async.Internal.CollectibleBinding.IsBound(entry.Loader)) continue;
                (removed ??= new List<Guid>()).Add(guid);
            }
            if (removed == null) return;

            lock (_cache)
            {
                for (int i = 0; i < removed.Count; i++)
                {
                    Guid guid = removed[i];
                    _entries.Remove(guid);
                    if (_cache.Remove(guid, out EngineObject? cached) && cached != null && !cached.IsDisposed)
                        cached.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Find all built-in assets assignable to the given type.
    /// </summary>
    public static IEnumerable<(Guid guid, string name, string path, Type type)> FindAllOfType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Initialize();
        foreach (var (guid, entry) in _entries)
        {
            if (type.IsAssignableFrom(entry.AssetType))
                yield return (guid, entry.Name, entry.Path, entry.AssetType);
        }
    }

    /// <summary>Check if a GUID corresponds to a built-in asset.</summary>
    public static bool IsBuiltIn(Guid guid) => _entries.ContainsKey(guid);

    /// <summary>
    /// Get the deterministic GUID for a specific default shader.
    /// </summary>
    public static Guid GuidFor(DefaultShader shader) => DeterministicGuid($"$Default:Shader/{shader}");

    /// <summary>
    /// Get the deterministic GUID for a specific default model.
    /// </summary>
    public static Guid GuidFor(DefaultModel model) => DeterministicGuid($"$Default:Model/{model}");

    /// <summary>
    /// Get the deterministic GUID for a specific default texture.
    /// </summary>
    public static Guid GuidFor(DefaultMaterial material) => DeterministicGuid($"$Default:Material/{material}");

    public static Guid GuidFor(DefaultTexture tex) => DeterministicGuid($"$Default:Texture/{tex}");

    /// <summary>Get the deterministic GUID for a specific default sprite.</summary>
    public static Guid GuidFor(DefaultSprite sprite) => DeterministicGuid($"$Default:Sprite/{sprite}");

    /// <summary>Deterministic GUID for a built-in default font.</summary>
    public static Guid GuidFor(DefaultFont font) => DeterministicGuid($"$Default:Font/{font}");

    /// <summary>
    /// Get the deterministic GUID for a default model's first mesh.
    /// </summary>
    public static Guid GuidForMesh(DefaultModel model, int meshIndex = 0) => DeterministicGuid($"$Default:Model/{model}/Mesh/{meshIndex}");
}
