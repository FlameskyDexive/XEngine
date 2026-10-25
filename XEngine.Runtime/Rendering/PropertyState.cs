// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using System.Runtime.InteropServices;

using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Vector;

using Texture2D = XEngine.Runtime.Resources.Texture2D;

namespace XEngine.Runtime.Rendering;

public partial class PropertyState
{
    // Internal so backend property binders can walk these directly without
    // forcing every access through allocating accessor methods. Keep [SerializeField]
    // so the editor and Echo serializer still see them.
    [SerializeField] internal Dictionary<string, Color> _colors = [];
    [SerializeField] internal Dictionary<string, Float2> _vectors2 = [];
    [SerializeField] internal Dictionary<string, Float3> _vectors3 = [];
    [SerializeField] internal Dictionary<string, Float4> _vectors4 = [];
    [SerializeField] internal Dictionary<string, float> _floats = [];
    [SerializeField] internal Dictionary<string, int> _ints = [];
    [SerializeField] internal Dictionary<string, Float4x4> _matrices = [];
    [SerializeField] internal Dictionary<string, Float4x4[]> _matrixArr = [];
    [SerializeField] internal Dictionary<string, AssetRef<Texture2D>> _textures = [];
    [SerializeField] internal Dictionary<string, AssetRef<Texture3D>> _textures3D = [];
    [SerializeField] internal Dictionary<string, AssetRef<Texture2DArray>> _textures2DArray = [];
    [SerializeField] internal Dictionary<string, AssetRef<Cubemap>> _texturesCube = [];
    [SerializeField] internal Dictionary<string, GraphicsBuffer> _buffers = [];
    [SerializeField] internal Dictionary<string, uint> _bufferBindings = [];

    public PropertyState() { }

    public PropertyState(PropertyState clone)
    {
        _colors = new(clone._colors);
        _vectors2 = new(clone._vectors2);
        _vectors3 = new(clone._vectors3);
        _vectors4 = new(clone._vectors4);
        _floats = new(clone._floats);
        _ints = new(clone._ints);
        _matrices = new(clone._matrices);
        _matrixArr = new(clone._matrixArr);
        _textures = new(clone._textures);
        _textures3D = new(clone._textures3D);
        _textures2DArray = new(clone._textures2DArray);
        _texturesCube = new(clone._texturesCube);
        _buffers = new(clone._buffers);
        _bufferBindings = new(clone._bufferBindings);
    }

    public bool IsEmpty => _colors.Count == 0 && _vectors4.Count == 0 && _vectors3.Count == 0 && _vectors2.Count == 0 && _floats.Count == 0 && _ints.Count == 0 && _matrices.Count == 0 && _textures.Count == 0 && _textures3D.Count == 0 && _textures2DArray.Count == 0;

    private ulong HashDictionary<T>(Dictionary<string, T> dict, ulong hash)
    {
        // Stable order without LINQ (PR0001): sort a snapshot of the keys.
        // Allocates once per hash, which is acceptable (hashing is not a per-draw hot path).
        var keys = new List<string>(dict.Keys);
        keys.Sort(System.StringComparer.Ordinal);
        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            hash ^= (ulong)key.GetHashCode();
            hash *= 1099511628211UL;
            hash ^= (ulong)dict[key].GetHashCode();
            hash *= 1099511628211UL;
        }
        return hash;
    }

    /// <summary>
    /// Computes a FNV-1a 64-bit hash representing the current state of all properties.
    /// Used for material batching to group objects with identical material properties together,
    /// minimizing GPU state changes. Properties are ordered by key to ensure consistent hashing.
    /// </summary>
    /// <returns>A 64-bit FNV-1a hash of all property key-value pairs</returns>
    public ulong ComputeHash()
    {
        ulong hash = 14695981039346656037UL; // FNV-1a offset basis

        // Hash all property dictionaries (order is important for consistency)
        hash = HashDictionary(_floats, hash);
        hash = HashDictionary(_ints, hash);
        hash = HashDictionary(_vectors2, hash);
        hash = HashDictionary(_vectors3, hash);
        hash = HashDictionary(_vectors4, hash);
        hash = HashDictionary(_colors, hash);
        hash = HashDictionary(_matrices, hash);
        hash = HashDictionary(_textures, hash);
        hash = HashDictionary(_textures3D, hash);
        hash = HashDictionary(_textures2DArray, hash);
        hash = HashDictionary(_buffers, hash);

        return hash;
    }

    // Setters
    public void SetColor(string name, Color value) => _colors[name] = value;
    public void SetVector(string name, Float2 value) => _vectors2[name] = (Float2)value;
    public void SetVector(string name, Float3 value) => _vectors3[name] = (Float3)value;
    public void SetVector(string name, Float4 value) => _vectors4[name] = (Float4)value;
    public void SetFloat(string name, float value) => _floats[name] = value;
    public void SetInt(string name, int value) => _ints[name] = value;
    public void SetMatrix(string name, Float4x4 value) => _matrices[name] = (Float4x4)value;
    public void SetMatrices(string name, Float4x4[] value)
    {
        var converted = new Float4x4[value.Length];
        for (int i = 0; i < value.Length; i++)
            converted[i] = value[i];
        _matrixArr[name] = converted;
    }
    public void SetTexture(string name, Texture2D value) => _textures[name] = new AssetRef<Texture2D>(value);
    public void SetTexture(string name, AssetRef<Texture2D> value) => _textures[name] = value;
    public void SetTexture3D(string name, Texture3D value) => _textures3D[name] = new AssetRef<Texture3D>(value);
    public void SetTexture3D(string name, AssetRef<Texture3D> value) => _textures3D[name] = value;
    public void SetTexture2DArray(string name, Texture2DArray value) => _textures2DArray[name] = new AssetRef<Texture2DArray>(value);
    public void SetTexture2DArray(string name, AssetRef<Texture2DArray> value) => _textures2DArray[name] = value;
    public void SetTextureCube(string name, Cubemap value) => _texturesCube[name] = new AssetRef<Cubemap>(value);
    public void SetTextureCube(string name, AssetRef<Cubemap> value) => _texturesCube[name] = value;
    public void SetBuffer(string name, GraphicsBuffer value, uint bindingPoint = 0)
    {
        _buffers[name] = value;
        _bufferBindings[name] = bindingPoint;
    }

    /// <summary>Yield the names of every property that has a value set on this
    /// <see cref="PropertyState"/>, regardless of type. Used by callers that need
    /// to iterate over "all properties this state owns" e.g. Material's
    /// override-tracking migration.</summary>
    public System.Collections.Generic.IEnumerable<string> EnumerateNames()
    {
        foreach (var k in _floats.Keys)     yield return k;
        foreach (var k in _ints.Keys)       yield return k;
        foreach (var k in _vectors2.Keys)   yield return k;
        foreach (var k in _vectors3.Keys)   yield return k;
        foreach (var k in _vectors4.Keys)   yield return k;
        foreach (var k in _colors.Keys)     yield return k;
        foreach (var k in _matrices.Keys)   yield return k;
        foreach (var k in _textures.Keys)   yield return k;
        foreach (var k in _textures3D.Keys) yield return k;
        foreach (var k in _textures2DArray.Keys) yield return k;
        foreach (var k in _texturesCube.Keys) yield return k;
    }

    /// <summary>Drop the entry for <paramref name="name"/> from every type bucket.
    /// Idempotent no-op for names that aren't present. Used by the material
    /// inspector's revert-to-default action so the next render falls back to the
    /// shader's live default value.</summary>
    public void RemoveProperty(string name)
    {
        _floats.Remove(name);
        _ints.Remove(name);
        _vectors2.Remove(name);
        _vectors3.Remove(name);
        _vectors4.Remove(name);
        _colors.Remove(name);
        _matrices.Remove(name);
        _matrixArr.Remove(name);
        _textures.Remove(name);
        _textures3D.Remove(name);
        _textures2DArray.Remove(name);
        _texturesCube.Remove(name);
        _buffers.Remove(name);
        _bufferBindings.Remove(name);
    }

    // Getters
    // Has* methods check if a property exists without retrieving it
    public bool HasFloat(string name) => _floats.ContainsKey(name);
    public bool HasInt(string name) => _ints.ContainsKey(name);
    public bool HasVector2(string name) => _vectors2.ContainsKey(name);
    public bool HasVector3(string name) => _vectors3.ContainsKey(name);
    public bool HasVector4(string name) => _vectors4.ContainsKey(name);
    public bool HasColor(string name) => _colors.ContainsKey(name);
    public bool HasMatrix(string name) => _matrices.ContainsKey(name);
    public bool HasTexture(string name) => _textures.ContainsKey(name);
    public bool HasTexture3D(string name) => _textures3D.ContainsKey(name);
    public bool HasTexture2DArray(string name) => _textures2DArray.ContainsKey(name);
    public bool HasTextureCube(string name) => _texturesCube.ContainsKey(name);

    public Color GetColor(string name) => _colors.TryGetValue(name, out Color value) ? value : Color.White;
    public Float2 GetVector2(string name) => _vectors2.TryGetValue(name, out Float2 value) ? value : Float2.Zero;
    public Float3 GetVector3(string name) => _vectors3.TryGetValue(name, out Float3 value) ? value : Float3.Zero;
    public Float4 GetVector4(string name) => _vectors4.TryGetValue(name, out Float4 value) ? value : Float4.Zero;
    public float GetFloat(string name) => _floats.TryGetValue(name, out float value) ? value : 0;
    public int GetInt(string name) => _ints.TryGetValue(name, out int value) ? value : 0;
    public Float4x4 GetMatrix(string name) => _matrices.TryGetValue(name, out Float4x4 value) ? (Float4x4)value : Float4x4.Identity;
    // GetValueRefOrNullRef (not TryGetValue): TryGetValue's out param is a copy of the AssetRef, so
    // .Res on it would cache into a throwaway struct instead of the dictionary's own slot.
    public Texture2D? GetTexture(string name) => _textures.ContainsKey(name) ? CollectionsMarshal.GetValueRefOrNullRef(_textures, name).Res : null;
    public AssetRef<Texture2D> GetTextureRef(string name) => _textures.TryGetValue(name, out var value) ? value : default;
    public Texture3D? GetTexture3D(string name) => _textures3D.ContainsKey(name) ? CollectionsMarshal.GetValueRefOrNullRef(_textures3D, name).Res : null;
    public AssetRef<Texture3D> GetTexture3DRef(string name) => _textures3D.TryGetValue(name, out var value) ? value : default;
    public Texture2DArray? GetTexture2DArray(string name) => _textures2DArray.ContainsKey(name) ? CollectionsMarshal.GetValueRefOrNullRef(_textures2DArray, name).Res : null;
    public AssetRef<Texture2DArray> GetTexture2DArrayRef(string name) => _textures2DArray.TryGetValue(name, out var value) ? value : default;
    public Cubemap? GetTextureCube(string name) => _texturesCube.ContainsKey(name) ? CollectionsMarshal.GetValueRefOrNullRef(_texturesCube, name).Res : null;
    public AssetRef<Cubemap> GetTextureCubeRef(string name) => _texturesCube.TryGetValue(name, out var value) ? value : default;
    public GraphicsBuffer GetBuffer(string name) => _buffers.TryGetValue(name, out GraphicsBuffer value) ? value : null;
    public uint GetBufferBinding(string name) => _bufferBindings.TryGetValue(name, out uint value) ? value : 0;


    public void Clear()
    {
        _textures.Clear();
        _textures3D.Clear();
        _textures2DArray.Clear();
        _texturesCube.Clear();
        _matrices.Clear();
        _matrixArr.Clear();
        _ints.Clear();
        _floats.Clear();
        _vectors2.Clear();
        _vectors3.Clear();
        _vectors4.Clear();
        _colors.Clear();
        _buffers.Clear();
        _bufferBindings.Clear();
    }

    internal void TouchAssetReferences()
    {
        foreach (AssetRef<Texture2D> texture in _textures.Values)
            texture.Touch();
        foreach (AssetRef<Texture3D> texture in _textures3D.Values)
            texture.Touch();
        foreach (AssetRef<Texture2DArray> texture in _textures2DArray.Values)
            texture.Touch();
        foreach (AssetRef<Cubemap> texture in _texturesCube.Values)
            texture.Touch();
    }

    internal void ApplyTextureDefaults(Shader shader)
    {
        ShaderProperty[] defaults = shader.PropertyArray;
        for (int i = 0; i < defaults.Length; i++)
        {
            ShaderProperty property = defaults[i];
            if (property.PropertyType != ShaderPropertyType.Texture2D || _textures.ContainsKey(property.Name))
                continue;

            Texture2D? texture = property.Texture2DValue;
            if (texture.IsNotValid())
            {
                if (texture != null && texture.AssetID != System.Guid.Empty)
                    texture = AssetDatabase.Get(texture.AssetID) as Texture2D;
                if (texture.IsNotValid())
                    texture = Texture2D.LoadDefault(DefaultTexture.White);
                property.Texture2DValue = texture;
            }

            _textures[property.Name] = new AssetRef<Texture2D>(texture);
        }

        for (int i = 0; i < defaults.Length; i++)
        {
            ShaderProperty property = defaults[i];
            if (property.PropertyType != ShaderPropertyType.Texture2DArray || _textures2DArray.ContainsKey(property.Name))
                continue;

            Texture2DArray? texture = property.Texture2DArrayValue;
            if (texture.IsNotValid())
                texture = Texture2DArray.White;
            property.Texture2DArrayValue = texture;
            _textures2DArray[property.Name] = new AssetRef<Texture2DArray>(texture);
        }
    }

    public void ApplyOverride(PropertyState properties)
    {
        foreach (KeyValuePair<string, Color> item in properties._colors)
            _colors[item.Key] = item.Value;
        foreach (KeyValuePair<string, Float2> item in properties._vectors2)
            _vectors2[item.Key] = item.Value;
        foreach (KeyValuePair<string, Float3> item in properties._vectors3)
            _vectors3[item.Key] = item.Value;
        foreach (KeyValuePair<string, Float4> item in properties._vectors4)
            _vectors4[item.Key] = item.Value;
        foreach (KeyValuePair<string, float> item in properties._floats)
            _floats[item.Key] = item.Value;
        foreach (KeyValuePair<string, int> item in properties._ints)
            _ints[item.Key] = item.Value;
        foreach (KeyValuePair<string, Float4x4> item in properties._matrices)
            _matrices[item.Key] = item.Value;
        foreach (KeyValuePair<string, Float4x4[]> item in properties._matrixArr)
            _matrixArr[item.Key] = item.Value;
        foreach (KeyValuePair<string, AssetRef<Texture2D>> item in properties._textures)
            _textures[item.Key] = item.Value;
        foreach (KeyValuePair<string, AssetRef<Texture3D>> item in properties._textures3D)
            _textures3D[item.Key] = item.Value;
        foreach (KeyValuePair<string, AssetRef<Texture2DArray>> item in properties._textures2DArray)
            _textures2DArray[item.Key] = item.Value;
        foreach (KeyValuePair<string, AssetRef<Cubemap>> item in properties._texturesCube)
            _texturesCube[item.Key] = item.Value;
        foreach (KeyValuePair<string, GraphicsBuffer> item in properties._buffers)
            _buffers[item.Key] = item.Value;
        foreach (KeyValuePair<string, uint> item in properties._bufferBindings)
            _bufferBindings[item.Key] = item.Value;
    }

}

public partial class PropertyState
{
    // Global static dictionaries. Internal so backend property binders can walk them.
    internal static Dictionary<string, Color> s_globalColors = [];
    internal static Dictionary<string, Float2> s_globalVectors2 = [];
    internal static Dictionary<string, Float3> s_globalVectors3 = [];
    internal static Dictionary<string, Float4> s_globalVectors4 = [];
    internal static Dictionary<string, float> s_globalFloats = [];
    internal static Dictionary<string, int> s_globalInts = [];
    internal static Dictionary<string, Float4x4> s_globalMatrices = [];
    internal static Dictionary<string, System.Numerics.Matrix4x4[]> s_globalMatrixArr = [];
    internal static Dictionary<string, Texture2D> s_globalTextures = [];
    internal static Dictionary<string, Texture3D> s_globalTextures3D = [];
    internal static Dictionary<string, Texture2DArray> s_globalTextures2DArray = [];
    internal static Dictionary<string, Cubemap> s_globalTexturesCube = [];
    internal static Dictionary<string, GraphicsBuffer> s_globalBuffers = [];
    internal static Dictionary<string, uint> s_globalBufferBindings = [];

    // Global setters route through a one-op CommandBuffer so the dict mutation
    // runs on the render thread, ordered against draws and free of races with
    // backend-side property enumeration.
    public static void SetGlobalColor(string name, Color value)
    {
        using var cmd = Graphics.GetCommandBuffer("SetGlobalColor");
        cmd.SetGlobalColor(name, value); Graphics.Submit(cmd);
    }
    public static void SetGlobalVector(string name, Float2 value)
    {
        using var cmd = Graphics.GetCommandBuffer("SetGlobalVec2");
        cmd.SetGlobalVector(name, value); Graphics.Submit(cmd);
    }
    public static void SetGlobalVector(string name, Float3 value)
    {
        using var cmd = Graphics.GetCommandBuffer("SetGlobalVec3");
        cmd.SetGlobalVector(name, value); Graphics.Submit(cmd);
    }
    public static void SetGlobalVector(string name, Float4 value)
    {
        using var cmd = Graphics.GetCommandBuffer("SetGlobalVec4");
        cmd.SetGlobalVector(name, value); Graphics.Submit(cmd);
    }
    public static void SetGlobalFloat(string name, float value)
    {
        using var cmd = Graphics.GetCommandBuffer("SetGlobalFloat");
        cmd.SetGlobalFloat(name, value); Graphics.Submit(cmd);
    }
    public static void SetGlobalInt(string name, int value)
    {
        using var cmd = Graphics.GetCommandBuffer("SetGlobalInt");
        cmd.SetGlobalInt(name, value); Graphics.Submit(cmd);
    }
    public static void SetGlobalMatrix(string name, Float4x4 value)
    {
        using var cmd = Graphics.GetCommandBuffer("SetGlobalMatrix");
        cmd.SetGlobalMatrix(name, value); Graphics.Submit(cmd);
    }
    public static void SetGlobalMatrices(string name, Float4x4[] value)
    {
        using var cmd = Graphics.GetCommandBuffer("SetGlobalMatrices");
        cmd.SetGlobalMatrices(name, value); Graphics.Submit(cmd);
    }
    public static void SetGlobalTexture(string name, Texture2D value)
    {
        using var cmd = Graphics.GetCommandBuffer("SetGlobalTexture");
        cmd.SetGlobalTexture(name, value); Graphics.Submit(cmd);
    }
    public static void SetGlobalTexture3D(string name, Texture3D value)
    {
        using var cmd = Graphics.GetCommandBuffer("SetGlobalTexture3D");
        cmd.SetGlobalTexture3D(name, value); Graphics.Submit(cmd);
    }
    public static void SetGlobalTextureCube(string name, Cubemap? value)
    {
        using var cmd = Graphics.GetCommandBuffer("SetGlobalTextureCube");
        cmd.SetGlobalTextureCube(name, value); Graphics.Submit(cmd);
    }
    public static void SetGlobalBuffer(string name, GraphicsBuffer value, uint bindingPoint = 0)
    {
        using var cmd = Graphics.GetCommandBuffer("SetGlobalBuffer");
        cmd.SetGlobalBuffer(name, value, bindingPoint); Graphics.Submit(cmd);
    }

    // Render-thread-only direct mutations, invoked by executor handlers. Bypass the
    // CB plumbing so they don't recurse into another submit.
    internal static void SetGlobalMatricesInternal(string name, Float4x4[] value)
    {
        var converted = new System.Numerics.Matrix4x4[value.Length];
        for (int i = 0; i < value.Length; i++)
            converted[i] = (System.Numerics.Matrix4x4)(Float4x4)value[i];
        s_globalMatrixArr[name] = converted;
    }

    internal static void ClearGlobalsInternal()
    {
        s_globalTextures.Clear();
        s_globalTextures3D.Clear();
        s_globalTextures2DArray.Clear();
        s_globalTexturesCube.Clear();
        s_globalMatrices.Clear();
        s_globalInts.Clear();
        s_globalFloats.Clear();
        s_globalVectors2.Clear();
        s_globalVectors3.Clear();
        s_globalVectors4.Clear();
        s_globalColors.Clear();
        s_globalMatrixArr.Clear();
        s_globalBuffers.Clear();
        s_globalBufferBindings.Clear();
    }

    // Global getters
    public static Color GetGlobalColor(string name) => s_globalColors.TryGetValue(name, out Color value) ? value : Color.White;
    public static Float2 GetGlobalVector2(string name) => s_globalVectors2.TryGetValue(name, out Float2 value) ? value : Float2.Zero;
    public static Float3 GetGlobalVector3(string name) => s_globalVectors3.TryGetValue(name, out Float3 value) ? value : Float3.Zero;
    public static Float4 GetGlobalVector4(string name) => s_globalVectors4.TryGetValue(name, out Float4 value) ? value : Float4.Zero;
    public static float GetGlobalFloat(string name) => s_globalFloats.TryGetValue(name, out float value) ? value : 0;
    public static int GetGlobalInt(string name) => s_globalInts.TryGetValue(name, out int value) ? value : 0;
    public static Float4x4 GetGlobalMatrix(string name) => s_globalMatrices.TryGetValue(name, out Float4x4 value) ? value : Float4x4.Identity;

    /// <summary>
    /// Read a global matrix array uploaded via <see cref="SetGlobalMatrices"/> (for example
    /// <c>_PointShadowMatrices</c> / <c>_SpotShadowMatrices</c>). Returns null when no array was
    /// uploaded for <paramref name="name"/>; the caller is responsible for sizing the destination.
    /// Stored as <see cref="System.Numerics.Matrix4x4"/> on the render thread; the conversion back
    /// to engine <see cref="Float4x4"/> is deferred to the caller so hot readers can keep it inline.
    /// </summary>
    public static System.Numerics.Matrix4x4[]? GetGlobalMatrices(string name) =>
        s_globalMatrixArr.TryGetValue(name, out System.Numerics.Matrix4x4[]? value) ? value : null;
    public static Texture2D? GetGlobalTexture(string name) => s_globalTextures.TryGetValue(name, out Texture2D value) ? value : null;
    public static Texture3D? GetGlobalTexture3D(string name) => s_globalTextures3D.TryGetValue(name, out Texture3D value) ? value : null;
    public static Texture2DArray? GetGlobalTexture2DArray(string name) => s_globalTextures2DArray.TryGetValue(name, out Texture2DArray value) ? value : null;
    public static Cubemap? GetGlobalTextureCube(string name) => s_globalTexturesCube.TryGetValue(name, out Cubemap value) ? value : null;
    public static GraphicsBuffer GetGlobalBuffer(string name) => s_globalBuffers.TryGetValue(name, out GraphicsBuffer value) ? value : null;
    public static uint GetGlobalBufferBinding(string name) => s_globalBufferBindings.TryGetValue(name, out uint value) ? value : 0;

    public static void ClearGlobals()
    {
        using var cmd = Graphics.GetCommandBuffer("ClearAllGlobals");
        cmd.ClearAllGlobals();
        Graphics.Submit(cmd);
    }
}
