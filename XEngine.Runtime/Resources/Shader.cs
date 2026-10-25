// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Vector;

namespace XEngine.Runtime.Resources;

/// <summary>
/// The Shader class itself doesnt do much, It stores the properties of the shader and the shader code and Keywords.
/// This is used in conjunction with the Material class to create shader variants with the correct keywords and to render things
/// </summary>
public sealed class Shader : EngineObject, ISerializationCallbackReceiver
{
    [SerializeField]
    private ShaderProperty[] _properties;
    public IEnumerable<ShaderProperty> Properties { get { EnsureNotDisposed(); return _properties; } }
    internal ShaderProperty[] PropertyArray => _properties;


    [SerializeField]
    private ShaderPass[] _passes;
    public IEnumerable<ShaderPass> Passes { get { EnsureNotDisposed(); return _passes; } }

    /// <summary>Allocation-free view for per-frame iteration: foreach over the
    /// <see cref="Passes"/> interface boxes the array enumerator on every call, which the
    /// batch builder pays per material per pass per frame.</summary>
    internal ShaderPass[] PassArray { get { EnsureNotDisposed(); return _passes; } }

    [SerializeField]
    private string[] _offlineVariantKeys = [];

    [SerializeField]
    private string[] _offlineVariantKeywords = [];

    [SerializeField]
    private bool _hasOfflineVariantManifest;

    [SerializeField]
    private string _artifactName = "";

    [SerializeIgnore]
    private HashSet<string>? _offlineVariantKeywordSet;

    [SerializeIgnore]
    private bool _hasGBufferPass;

    /// <summary>True when any pass is tagged <c>LightMode=GBuffer</c> (Deferred path).</summary>
    public bool HasGBufferPass
    {
        get
        {
            EnsureNotDisposed();
            return _hasGBufferPass;
        }
    }

    /// <summary>
    /// Stable logical name used by offline shader artifacts. This remains the authored shader
    /// name even when a built-in asset registry assigns a shorter display name to <see cref="EngineObject.Name"/>.
    /// </summary>
    internal string ArtifactName => _artifactName;

    internal IReadOnlyList<string> OfflineVariantKeys => _offlineVariantKeys;
    internal bool HasOfflineVariantManifest => _hasOfflineVariantManifest;


    private Dictionary<string, int> _nameIndexLookup = [];
    private Dictionary<string, List<int>> _tagIndexLookup = [];


    internal Shader() : base("New Shader") { }

    public Shader(
        string name,
        ShaderProperty[] properties,
        ShaderPass[] passes,
        string[]? offlineVariantKeys = null,
        string[]? offlineVariantKeywords = null) : base(name)
    {
        _properties = properties;
        _passes = passes;
        _offlineVariantKeys = offlineVariantKeys ?? [];
        _offlineVariantKeywords = offlineVariantKeywords ?? [];
        _hasOfflineVariantManifest = offlineVariantKeys != null || offlineVariantKeywords != null;
        _artifactName = name;

        OnAfterDeserialize();
    }

    private void RegisterPass(ShaderPass pass, int index)
    {
        pass.SetShaderName(_artifactName);
        pass.SetOfflineVariantKeywords(_offlineVariantKeywords, _hasOfflineVariantManifest);

        if (!string.IsNullOrWhiteSpace(pass.Name))
        {
            if (!_nameIndexLookup.TryAdd(pass.Name, index))
                throw new InvalidOperationException($"Pass with name {pass.Name} conflicts with existing pass at index {_nameIndexLookup[pass.Name]}. Ensure no two passes have equal names.");
        }

        foreach (KeyValuePair<string, string> pair in pass.Tags)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            if (!_tagIndexLookup.TryGetValue(pair.Key, out _))
                _tagIndexLookup.Add(pair.Key, []);

            _tagIndexLookup[pair.Key].Add(index);
        }

        if (pass.HasTag("LightMode", "GBuffer"))
            _hasGBufferPass = true;
    }

    public ShaderPass GetPass(int passIndex)
    {
        EnsureNotDisposed();
        passIndex = Maths.Clamp(passIndex, 0, _passes.Length - 1);
        return _passes[passIndex];
    }

    public ShaderPass GetPass(string passName)
    {
        EnsureNotDisposed();
        return _passes[GetPassIndex(passName)];
    }

    public int GetPassIndex(string passName)
    {
        EnsureNotDisposed();
        return _nameIndexLookup.GetValueOrDefault(passName, -1);
    }

    public int? GetPassWithTag(string tag, string? tagValue = null)
    {
        EnsureNotDisposed();
        List<int> passes = GetPassesWithTag(tag, tagValue);
        return passes.Count > 0 ? passes[0] : null;
    }

    public List<int> GetPassesWithTag(string tag, string? tagValue = null)
    {
        EnsureNotDisposed();
        List<int> passes = [];

        if (_tagIndexLookup.TryGetValue(tag, out List<int> passesWithTag))
        {
            foreach (int index in passesWithTag)
            {
                ShaderPass pass = _passes[index];

                if (pass.HasTag(tag, tagValue))
                    passes.Add(index);
            }
        }

        return passes;
    }

    /// <summary>
    /// Loads a shader from a file path
    /// </summary>
    public static Shader LoadFromFile(string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            throw new System.IO.FileNotFoundException($"Shader file not found: {filePath}");

        string shaderCode = System.IO.File.ReadAllText(filePath);

        if (!AssetImporting.ShaderParser.ParseShader(filePath, shaderCode, path =>
        {
            // Include resolver for #include directives
            string? absolutePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(filePath)!, path));
            if (System.IO.File.Exists(absolutePath))
                return System.IO.File.ReadAllText(absolutePath);

            // Then try embedded resources (for default includes like VertexAttributes, Fragment, etc.)
            try
            {
                return EmbeddedResources.ReadAllText(path);
            }
            catch
            {
                // Also try with Assets/Defaults/ prefix
                try
                {
                    return EmbeddedResources.ReadAllText($"Assets/Defaults/{path}");
                }
                catch
                {
                    return null;
                }
            }
        }, out Shader? shader))
        {
            throw new System.Exception($"Failed to parse shader: {filePath}");
        }

        if (shader.IsNotValid())
            throw new System.Exception($"Shader parsing returned null: {filePath}");

        shader.AssetPath = filePath;
        return shader;
    }

    /// <summary>
    /// Get the shared instance of a default embedded shader. Returns the same instance
    /// across the whole app so ShaderPass variant caches aren't defeated by repeated
    /// re-parsing the parse happens exactly once per shader enum value.
    /// </summary>
    public static Shader LoadDefault(DefaultShader shader)
    {
        if (BuiltInAssets.Get(BuiltInAssets.GuidFor(shader)) is Shader cached)
            return cached;
        // BuiltInAssets.Initialize() hasn't run, or the loader errored parse directly
        // as a last resort so this method never silently returns null.
        return ParseDefault(shader);
    }

    /// <summary>
    /// Raw parse of a default embedded shader invoked by <see cref="BuiltInAssets"/>
    /// on the first cache miss. Public callers should use <see cref="LoadDefault"/>.
    /// </summary>
    internal static Shader ParseDefault(DefaultShader shader)
    {
        string fileName = shader.ToString();

        string resourcePath = $"Assets/Defaults/{fileName}.shader";
        string shaderCode = EmbeddedResources.ReadAllText(resourcePath);

        if (!AssetImporting.ShaderParser.ParseShader(resourcePath, shaderCode, path =>
        {
            // Include resolver for embedded resources
            try
            {
                return EmbeddedResources.ReadAllText(path);
            }
            catch
            {
                return null;
            }
        }, out Shader? result))
        {
            throw new System.Exception($"Failed to parse default shader: {shader}");
        }

        if (result.IsNotValid())
            throw new System.Exception($"Default shader parsing returned null: {shader}");

        // AssetID/AssetPath/Name are set by BuiltInAssets.Get after the loader returns,
        // so we don't set them here keeping the raw parse free of registry coupling.
        return result;
    }

    /// <summary>
    /// Loads a default shader include file (for use by shader parser)
    /// </summary>
    internal static string LoadDefaultInclude(DefaultShaderInclude include)
    {
        string fileName = include.ToString();

        return EmbeddedResources.ReadAllText($"Assets/Defaults/{fileName}.glsl");
    }

    public void OnBeforeSerialize() { }

    public void OnAfterDeserialize()
    {
        _offlineVariantKeys ??= [];
        _offlineVariantKeywords ??= [];
        if (_offlineVariantKeys.Length > 0 || _offlineVariantKeywords.Length > 0)
            _hasOfflineVariantManifest = true;
        if (string.IsNullOrWhiteSpace(_artifactName))
            _artifactName = Name;
        _offlineVariantKeywordSet = _hasOfflineVariantManifest
            ? new HashSet<string>(_offlineVariantKeywords, StringComparer.Ordinal)
            : null;
        _hasGBufferPass = false;
        for (int i = 0; i < _passes.Length; i++)
            RegisterPass(_passes[i], i);
    }

    internal string BuildKeywordKey(Dictionary<string, bool>? keywords) =>
        ShaderPass.BuildKeywordKey(keywords, _offlineVariantKeywordSet);

    public override void OnDispose()
    {
        foreach (var pass in _passes)
            pass.Dispose();
    }

}
