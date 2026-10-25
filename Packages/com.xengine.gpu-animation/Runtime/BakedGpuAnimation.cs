using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.GpuAnimation;

/// <summary>GPU texture precision of the baked vertex animation textures.</summary>
public enum GpuAnimationPrecision
{
    /// <summary>RGBA32F texels, nearest sampling, shader-side manual frame lerp. Exact bake values.</summary>
    Float32 = 0,

    /// <summary>RGBA16F texels with bilinear filtering (reserved for the size-optimization pass).</summary>
    Half16 = 1,
}

/// <summary>Per-clip layout inside a <see cref="BakedGpuAnimation"/> texture atlas.</summary>
public sealed class GpuAnimationClipInfo
{
    public string Name = "";

    /// <summary>First texture row (atlas slot in V) occupied by this clip.</summary>
    public int FrameStart;

    /// <summary>Number of texture rows occupied by this clip.</summary>
    public int FrameCount;

    /// <summary>Clip duration in seconds.</summary>
    public float AnimLength = 1f;

    /// <summary>Samples per second the bake used.</summary>
    public float Fps = 30f;
}

/// <summary>
/// Baked vertex-animation-texture asset (".gpuanim"): skinned vertex positions — and
/// optionally normals — per sampled frame, in model space. Layout: one texture row per
/// frame, one column per vertex, RGBA float per texel (xyz = position/normal, w = 1).
/// Echo-serialized source data; GPU textures are built lazily on first use.
/// </summary>
public sealed class BakedGpuAnimation : EngineObject, ISerializable
{
    // Echo instantiates via Activator.CreateInstance(Type) — an explicit parameterless ctor is
    // required (optional-parameter ctors are invisible to GetConstructor(Type.EmptyTypes)).
    public BakedGpuAnimation() : base("BakedGpuAnimation") { }

    public BakedGpuAnimation(string? name) : base(name ?? "BakedGpuAnimation") { }

    /// <summary>Texture columns: next power of two >= <see cref="VertexCount"/> (padding columns stay zero).</summary>
    public int TextureWidth { get { EnsureNotDisposed(); return _textureWidth; } set { EnsureNotDisposed(); _textureWidth = value; MarkTexturesDirty(); } }

    /// <summary>Texture rows: total frames across all clips in the atlas.</summary>
    public int TextureHeight { get { EnsureNotDisposed(); return _textureHeight; } set { EnsureNotDisposed(); _textureHeight = value; MarkTexturesDirty(); } }

    /// <summary>Vertex count of the source skinned mesh.</summary>
    public int VertexCount { get { EnsureNotDisposed(); return _vertexCount; } set { EnsureNotDisposed(); _vertexCount = value; MarkTexturesDirty(); } }

    public bool HasNormals { get { EnsureNotDisposed(); return _hasNormals; } set { EnsureNotDisposed(); _hasNormals = value; MarkTexturesDirty(); } }

    public GpuAnimationPrecision Precision { get { EnsureNotDisposed(); return _precision; } set { EnsureNotDisposed(); _precision = value; MarkTexturesDirty(); } }

    /// <summary>RGBA float texels, row-major: (row * TextureWidth + column) * 4. xyz = skinned position, w = 1.</summary>
    public float[] Positions { get { EnsureNotDisposed(); return _positions; } set { EnsureNotDisposed(); _positions = value; MarkTexturesDirty(); } }

    /// <summary>RGBA float texels matching <see cref="Positions"/>; empty when <see cref="HasNormals"/> is false.</summary>
    public float[] Normals { get { EnsureNotDisposed(); return _normals; } set { EnsureNotDisposed(); _normals = value; MarkTexturesDirty(); } }

    public List<GpuAnimationClipInfo> Clips { get { EnsureNotDisposed(); return _clips; } set { EnsureNotDisposed(); _clips = value; } }

    /// <summary>Per texture row: 3 floats (min corner of that frame's skinned positions).</summary>
    public float[] FrameBoundsMin { get { EnsureNotDisposed(); return _frameBoundsMin; } set { EnsureNotDisposed(); _frameBoundsMin = value; } }

    /// <summary>Per texture row: 3 floats (max corner).</summary>
    public float[] FrameBoundsMax { get { EnsureNotDisposed(); return _frameBoundsMax; } set { EnsureNotDisposed(); _frameBoundsMax = value; } }

    /// <summary>Union of all frame bounds (recomputed on demand when empty).</summary>
    public void GetUnionBounds(out Float3 min, out Float3 max)
    {
        EnsureNotDisposed();
        if (_unionMin == null || _unionMax == null)
        {
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            int rows = Math.Min(_frameBoundsMin.Length / 3, _frameBoundsMax.Length / 3);
            for (int r = 0; r < rows; r++)
            {
                minX = Math.Min(minX, _frameBoundsMin[r * 3]);
                minY = Math.Min(minY, _frameBoundsMin[r * 3 + 1]);
                minZ = Math.Min(minZ, _frameBoundsMin[r * 3 + 2]);
                maxX = Math.Max(maxX, _frameBoundsMax[r * 3]);
                maxY = Math.Max(maxY, _frameBoundsMax[r * 3 + 1]);
                maxZ = Math.Max(maxZ, _frameBoundsMax[r * 3 + 2]);
            }
            if (rows == 0) { minX = minY = minZ = 0f; maxX = maxY = maxZ = 0f; }
            _unionMin = new Float3(minX, minY, minZ);
            _unionMax = new Float3(maxX, maxY, maxZ);
        }
        min = _unionMin.Value;
        max = _unionMax.Value;
    }

    /// <summary>Texel start index (in floats) of a vertex column inside a frame row.</summary>
    public int TexelIndex(int rowIndex, int vertexIndex) => (rowIndex * _textureWidth + vertexIndex) * 4;

    public void GetPosition(int rowIndex, int vertexIndex, out Float3 position)
    {
        int i = TexelIndex(rowIndex, vertexIndex);
        position = new Float3(_positions[i], _positions[i + 1], _positions[i + 2]);
    }

    public void GetNormal(int rowIndex, int vertexIndex, out Float3 normal)
    {
        int i = TexelIndex(rowIndex, vertexIndex);
        normal = new Float3(_normals[i], _normals[i + 1], _normals[i + 2]);
    }

    /// <summary>Lazily built GPU position texture (RGBA32F, nearest, clamp, no mips).</summary>
    public Texture2D? GetPositionTexture()
    {
        EnsureNotDisposed();
        if (_positionsDirty || _positionTexture == null)
        {
            _positionTexture?.Dispose();
            _positionTexture = CreateTexture(_positions);
            _positionsDirty = false;
        }
        return _positionTexture;
    }

    /// <summary>Lazily built GPU normal texture; null when the bake has no normals.</summary>
    public Texture2D? GetNormalTexture()
    {
        EnsureNotDisposed();
        if (!_hasNormals) return null;
        if (_normalsDirty || _normalTexture == null)
        {
            _normalTexture?.Dispose();
            _normalTexture = CreateTexture(_normals);
            _normalsDirty = false;
        }
        return _normalTexture;
    }

    private Texture2D CreateTexture(float[] texels)
    {
        // RGBA16F (Short4) halves GPU memory and stays filterable on every backend; the shader
        // samples at texel centers, so Linear filtering returns the exact texel — identical
        // output to the 32F/Nearest path at half precision. RGBA32F stays the exact option
        // (float32 is not filterable on WebGPU, hence Nearest there).
        if (_precision == GpuAnimationPrecision.Half16)
        {
            var halves = new Vector.Half4[texels.Length / 4];
            for (int i = 0; i < halves.Length; i++)
                halves[i] = new Vector.Half4(new Vector.Float4(texels[i * 4], texels[i * 4 + 1], texels[i * 4 + 2], texels[i * 4 + 3]));
            var halfTexture = new Texture2D((uint)_textureWidth, (uint)_textureHeight, false, TextureImageFormat.Short4);
            halfTexture.SetTextureFilters(TextureMin.Linear, TextureMag.Linear);
            Graphics.SetWrapS(halfTexture.Handle, TextureWrap.ClampToEdge);
            Graphics.SetWrapT(halfTexture.Handle, TextureWrap.ClampToEdge);
            unsafe { fixed (Vector.Half4* ptr = halves) halfTexture.SetDataPtr(ptr, 0, 0, (uint)_textureWidth, (uint)_textureHeight); }
            return halfTexture;
        }

        var texture = new Texture2D((uint)_textureWidth, (uint)_textureHeight, false, TextureImageFormat.Float4);
        texture.SetTextureFilters(TextureMin.Nearest, TextureMag.Nearest);
        Graphics.SetWrapS(texture.Handle, TextureWrap.ClampToEdge);
        Graphics.SetWrapT(texture.Handle, TextureWrap.ClampToEdge);
        unsafe { fixed (float* ptr = texels) texture.SetDataPtr(ptr, 0, 0, (uint)_textureWidth, (uint)_textureHeight); }
        return texture;
    }

    private void MarkTexturesDirty()
    {
        _positionsDirty = true;
        _normalsDirty = true;
    }

    public override void OnDispose()
    {
        _positionTexture?.Dispose();
        _normalTexture?.Dispose();
        _positionTexture = null;
        _normalTexture = null;
    }

    private int _textureWidth;
    private int _textureHeight;
    private int _vertexCount;
    private bool _hasNormals;
    private GpuAnimationPrecision _precision;
    private float[] _positions = Array.Empty<float>();
    private float[] _normals = Array.Empty<float>();
    private List<GpuAnimationClipInfo> _clips = new();
    private float[] _frameBoundsMin = Array.Empty<float>();
    private float[] _frameBoundsMax = Array.Empty<float>();
    private Float3? _unionMin;
    private Float3? _unionMax;

    [NonSerialized] private Texture2D? _positionTexture;
    [NonSerialized] private Texture2D? _normalTexture;
    [NonSerialized] private bool _positionsDirty = true;
    [NonSerialized] private bool _normalsDirty = true;

    public void Serialize(ref EchoObject value, SerializationContext ctx)
    {
        value.Add("Name", new EchoObject(Name));
        value.Add("TextureWidth", new EchoObject(_textureWidth));
        value.Add("TextureHeight", new EchoObject(_textureHeight));
        value.Add("VertexCount", new EchoObject(_vertexCount));
        value.Add("HasNormals", new EchoObject(_hasNormals));
        value.Add("Precision", new EchoObject((int)_precision));
        SerializeFloatArray(value, "Positions", _positions);
        SerializeFloatArray(value, "Normals", _normals);
        SerializeFloatArray(value, "FrameBoundsMin", _frameBoundsMin);
        SerializeFloatArray(value, "FrameBoundsMax", _frameBoundsMax);

        var clipList = EchoObject.NewList();
        for (int i = 0; i < _clips.Count; i++)
        {
            var c = EchoObject.NewCompound();
            c.Add("Name", new EchoObject(_clips[i].Name));
            c.Add("FrameStart", new EchoObject(_clips[i].FrameStart));
            c.Add("FrameCount", new EchoObject(_clips[i].FrameCount));
            c.Add("AnimLength", new EchoObject(_clips[i].AnimLength));
            c.Add("Fps", new EchoObject(_clips[i].Fps));
            clipList.ListAdd(c);
        }
        value.Add("Clips", clipList);
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        Name = value.Get("Name")?.StringValue ?? "BakedGpuAnimation";
        _textureWidth = value.Get("TextureWidth")?.IntValue ?? 0;
        _textureHeight = value.Get("TextureHeight")?.IntValue ?? 0;
        _vertexCount = value.Get("VertexCount")?.IntValue ?? 0;
        _hasNormals = value.Get("HasNormals")?.BoolValue ?? false;
        _precision = (GpuAnimationPrecision)(value.Get("Precision")?.IntValue ?? 0);
        _positions = DeserializeFloatArray(value, "Positions") ?? Array.Empty<float>();
        _normals = DeserializeFloatArray(value, "Normals") ?? Array.Empty<float>();
        _frameBoundsMin = DeserializeFloatArray(value, "FrameBoundsMin") ?? Array.Empty<float>();
        _frameBoundsMax = DeserializeFloatArray(value, "FrameBoundsMax") ?? Array.Empty<float>();

        _clips = new List<GpuAnimationClipInfo>();
        var clipList = value.Get("Clips");
        if (clipList != null)
        {
            for (int i = 0; i < clipList.List.Count; i++)
            {
                var c = clipList.List[i];
                _clips.Add(new GpuAnimationClipInfo
                {
                    Name = c.Get("Name")?.StringValue ?? "",
                    FrameStart = c.Get("FrameStart")?.IntValue ?? 0,
                    FrameCount = c.Get("FrameCount")?.IntValue ?? 0,
                    AnimLength = c.Get("AnimLength")?.FloatValue ?? 1f,
                    Fps = c.Get("Fps")?.FloatValue ?? 30f,
                });
            }
        }
        _unionMin = null;
        _unionMax = null;
        MarkTexturesDirty();
    }

    private static void SerializeFloatArray(EchoObject value, string key, float[]? data)
    {
        if (data == null || data.Length == 0) return;
        byte[] bytes = new byte[data.Length * sizeof(float)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        value.Add(key, new EchoObject(Convert.ToBase64String(bytes)));
    }

    private static float[]? DeserializeFloatArray(EchoObject value, string key)
    {
        string? b64 = value.Get(key)?.StringValue;
        if (b64 == null) return null;
        byte[] bytes = Convert.FromBase64String(b64);
        float[] arr = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
        return arr;
    }
}
