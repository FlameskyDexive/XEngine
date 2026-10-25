// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Text;

using XEngine.Echo;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
#if !XENGINE_MOBILE || XENGINE_WEB
using XEngine.Runtime.Backends.WebGL2;
#endif

namespace XEngine.Runtime.Rendering.Shaders;

public enum ShaderModernSourceKind
{
    None,
    LegacyHlsl,
    Slang,
}

public sealed class ShaderPass
{
    [SerializeField] private string _name;

    [SerializeField] private Dictionary<string, string> _tags;
    [SerializeField] private Dictionary<string, int> _tagSortOffsets;
    [SerializeField] private RasterizerState _rasterizerState;

    [SerializeField] private string _vertexSource;
    [SerializeField] private string _fragmentSource;
    [SerializeField] private string _hlslVertexSource;
    [SerializeField] private string _hlslFragmentSource;
    [SerializeField] private string _hlslComputeSource;
    [SerializeField] private ShaderModernSourceKind _modernSourceKind;
    [SerializeField] private string _fallbackAsset;

    [SerializeIgnore] private string _shaderName = "";

    [SerializeIgnore] private HashSet<string>? _offlineVariantKeywords;

    [SerializeField] private string _grabTextureName; // If not empty, captures screen before rendering
    [SerializeField] private string _grabDepthTextureName; // If not empty, also captures the depth buffer

    [SerializeIgnore]
    private Dictionary<string, ShaderVariant> _variants = [];

    [SerializeIgnore]
    private Dictionary<string, IGraphicsDevice?> _variantDevices = [];


    /// <summary>
    /// The name to identify this <see cref="ShaderPass"/>
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// The tags to identify this <see cref="ShaderPass"/>
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> Tags => _tags;

    /// <summary>
    /// The sort offsets for tags (e.g., "Transparent+1000" has offset 1000)
    /// </summary>
    public IReadOnlyDictionary<string, int> TagSortOffsets => _tagSortOffsets;

    /// <summary>
    /// The blending options to use when rendering this <see cref="ShaderPass"/>
    /// </summary>
    public RasterizerState State => _rasterizerState;

    /// <summary>
    /// The name of the texture uniform to bind the grabbed colour texture to. Empty if this pass doesn't grab.
    /// </summary>
    public string GrabTextureName => _grabTextureName;

    /// <summary>
    /// The name of the texture uniform to bind the grabbed depth texture to. Empty if depth isn't grabbed.
    /// </summary>
    public string GrabDepthTextureName => _grabDepthTextureName;

    /// <summary>
    /// Whether this pass captures the screen colour before rendering
    /// </summary>
    public bool HasGrabTexture => !string.IsNullOrEmpty(_grabTextureName);

    /// <summary>
    /// Whether this pass also captures the depth buffer alongside colour. Implies <see cref="HasGrabTexture"/>.
    /// </summary>
    public bool HasGrabDepth => !string.IsNullOrEmpty(_grabDepthTextureName);

    /// <summary>True when this pass has GLSL vertex/fragment sources.</summary>
    public bool HasGlsl => !string.IsNullOrEmpty(_vertexSource) && !string.IsNullOrEmpty(_fragmentSource);

    /// <summary>True when this pass has HLSL vertex/fragment sources.</summary>
    public bool HasHlslGraphics => !string.IsNullOrEmpty(_hlslVertexSource) && !string.IsNullOrEmpty(_hlslFragmentSource);

    /// <summary>True when this pass has an HLSL compute stage.</summary>
    public bool HasCompute => !string.IsNullOrEmpty(_hlslComputeSource);

    /// <summary>True when this pass has HLSL graphics or compute sources.</summary>
    public bool HasHlsl => HasHlslGraphics || HasCompute;

    /// <summary>Authoring marker used for this pass's modern-backend sources.</summary>
    public ShaderModernSourceKind ModernSourceKind => _modernSourceKind == ShaderModernSourceKind.None && HasHlsl
        ? ShaderModernSourceKind.LegacyHlsl
        : _modernSourceKind;

    /// <summary>True when this pass was authored with a SLANGPROGRAM block.</summary>
    public bool HasSlang => HasHlsl && ModernSourceKind == ShaderModernSourceKind.Slang;

    internal string ShaderName => _shaderName;

    public IEnumerable<KeyValuePair<string, ShaderVariant>> Variants => _variants;


    private ShaderPass() { }

    internal void SetShaderName(string shaderName) => _shaderName = shaderName ?? "";

    internal void SetOfflineVariantKeywords(IReadOnlyList<string> keywords, bool hasManifest)
    {
        _offlineVariantKeywords = hasManifest
            ? new HashSet<string>(keywords, StringComparer.Ordinal)
            : null;
    }

    public ShaderPass(
        string name,
        Dictionary<string, string>? tags,
        Dictionary<string, int>? tagSortOffsets,
        RasterizerState state,
        string vertexSource,
        string fragmentSource,
        string fallbackAsset,
        string grabTextureName = "",
        string grabDepthTextureName = "",
        string hlslVertexSource = "",
        string hlslFragmentSource = "",
        ShaderModernSourceKind modernSourceKind = ShaderModernSourceKind.None,
        string hlslComputeSource = "")
    {
        _name = name;

        _tags = tags ?? [];
        _tagSortOffsets = tagSortOffsets ?? [];
        _rasterizerState = state;

        _vertexSource = vertexSource ?? "";
        _fragmentSource = fragmentSource ?? "";
        _hlslVertexSource = hlslVertexSource ?? "";
        _hlslFragmentSource = hlslFragmentSource ?? "";
        _hlslComputeSource = hlslComputeSource ?? "";
        _modernSourceKind = modernSourceKind == ShaderModernSourceKind.None && HasHlsl
            ? ShaderModernSourceKind.LegacyHlsl
            : modernSourceKind;
        _fallbackAsset = fallbackAsset;

        _grabTextureName = grabTextureName;
        _grabDepthTextureName = grabDepthTextureName;

        _variants = [];
        _variantDevices = [];
    }

    /// <summary>
    /// OpenGL-compatible API: returns the linked <see cref="GraphicsProgram"/> for the
    /// variant. On Vulkan/D3D12 this fails if only bytecode is available — prefer
    /// <see cref="TryGetVariant"/>.
    /// </summary>
    public bool TryGetVariantProgram(Dictionary<string, bool>? keywordID, out GraphicsProgram variant)
    {
        if (!TryGetVariant(keywordID, out ShaderVariant? shaderVariant) || shaderVariant == null)
        {
            variant = null!;
            return false;
        }

        if (shaderVariant.GlProgram != null)
        {
            variant = shaderVariant.GlProgram;
            return true;
        }

        Debug.LogWarning(
            $"Shader pass '{Name}' compiled to bytecode for the active backend; GraphicsProgram is unavailable. Use TryGetVariant.");
        variant = null!;
        return false;
    }

    /// <summary>
    /// Compile (or fetch cached) a <see cref="ShaderVariant"/> for the active graphics backend.
    /// OpenGL / Null: GLSL → <see cref="GraphicsProgram"/>.
    /// Vulkan / D3D12: Slang-compatible source → <see cref="CompiledShaderBytecode"/> via the selected modern compiler.
    /// </summary>
    public bool TryGetVariant(Dictionary<string, bool>? keywordID, out ShaderVariant variant)
    {
        string keywords = BuildKeywordKey(keywordID, _offlineVariantKeywords);
        return ResolveVariant(keywordID, keywords, out variant);
    }

    internal bool TryGetMaterialVariant(Resources.Material material, out ShaderVariant variant)
    {
        UpdateLightingVariant(material, Graphics.Device?.Backend ?? GraphicsBackend.OpenGL);
        return ResolveVariant(material._localKeywords, material.KeywordKey, out variant);
    }

    internal void UpdateLightingVariant(Resources.Material material, GraphicsBackend backend)
    {
        if (_shaderName != "Default/Standard" || Name != "Standard")
            return;
        // Vulkan executes the light-uniform submission before resolving the following draws.
        // Unknown state stays on the full path; a local light appearing switches it back immediately.
        bool directionalOnly = backend == GraphicsBackend.Vulkan &&
            PropertyState.s_globalInts.TryGetValue("_StaticLightRoot", out int staticRoot) && staticRoot < 0 &&
            PropertyState.s_globalInts.TryGetValue("_DynamicLightRoot", out int dynamicRoot) && dynamicRoot < 0;
        material.SetKeyword("DIRECTIONAL_ONLY", directionalOnly);
    }

    private bool ResolveVariant(Dictionary<string, bool>? keywordID, string keywords, out ShaderVariant variant)
    {
        IGraphicsDevice? device = Graphics.Device;
        GraphicsBackend backend = device?.Backend ?? GraphicsBackend.OpenGL;

        if (_variants.TryGetValue(keywords, out variant!))
        {
            if (_variantDevices.TryGetValue(keywords, out IGraphicsDevice? variantDevice) &&
                ReferenceEquals(variantDevice, device))
                return true;

            variant.Dispose();
            _variants.Remove(keywords);
            _variantDevices.Remove(keywords);
        }

        try
        {
#if XENGINE_WEB
            if (backend == GraphicsBackend.WebGL2 && HasGlsl)
            {
                variant = CompileGlslVariant(keywordID, keywords);
            }
            else
#endif
            if (ShaderCompilerFactory.RequiresHlsl(backend))
            {
                variant = CompileHlslVariant(backend, keywordID, keywords);
            }
            else
            {
                variant = CompileGlslVariant(keywordID, keywords);
            }
        }
        catch (ShaderArtifactUnavailableException)
        {
            throw;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to compile shader pass of {Name}. Exception: {e.Message}");

            if (string.Equals(Name, "Invalid", StringComparison.Ordinal))
                throw;

            var fallbackShader = Resources.Shader.LoadDefault(Resources.DefaultShader.Invalid);
            if (fallbackShader.IsValid())
            {
                if (!fallbackShader.GetPass(0).TryGetVariant(null, out variant!))
                    throw new Exception($"Failed to compile shader pass of {Name}. Fallback shader also failed to compile.");
            }
            else
            {
                throw new Exception($"Failed to compile shader pass of {Name}. Fallback shader is null.");
            }
        }

        variant.SetDebugSource(this, keywords);
        _variants[keywords] = variant;
        _variantDevices[keywords] = device;
        return true;
    }

    private ShaderVariant CompileGlslVariant(
        IReadOnlyDictionary<string, bool>? keywordID,
        string keywords)
    {
        if (!HasGlsl)
            throw new Exception($"Failed to compile shader pass of {Name}. GLSL Vertex/Fragment sources are missing.");

        IShaderCompiler compiler = ShaderCompilerFactory.GetCompiler(GraphicsBackend.OpenGL);
        ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.OpenGL,
            Language = ShaderLanguage.Glsl,
            VertexSource = _vertexSource,
            FragmentSource = _fragmentSource,
            Keywords = FilterKeywords(keywordID),
        });

        if (!result.Success)
            throw new Exception(result.ErrorMessage ?? "GLSL compile failed.");

        Debug.Log("Compiling shader pass " + Name + " with keywords: " + keywords);

        string vertexSource = result.GlslVertexSource!;
        string fragmentSource = result.GlslFragmentSource!;
        string? geometrySource = result.GlslGeometrySource;
#if !XENGINE_MOBILE || XENGINE_WEB
        if ((Graphics.Device?.Backend ?? GraphicsBackend.OpenGL) == GraphicsBackend.WebGL2)
        {
            WebGL2GlslHost host = OperatingSystem.IsBrowser()
                ? WebGL2GlslHost.Browser
                : WebGL2GlslHost.DesktopCore;
            Debug.Log("[XEngine] WebGL2 GLSL prepare " + Name + " v=" + vertexSource.Length + " f=" + fragmentSource.Length);
            vertexSource = WebGL2Glsl.Prepare(vertexSource, WebGL2GlslStage.Vertex, host);
            fragmentSource = WebGL2Glsl.Prepare(fragmentSource, WebGL2GlslStage.Fragment, host);
            Debug.Log("[XEngine] WebGL2 GLSL prepared " + Name + " v=" + vertexSource.Length + " f=" + fragmentSource.Length);
        }
#endif

        Debug.Log("[XEngine] WebGL2 GLSL compile-program " + Name);
        GraphicsProgram program = Graphics.CompileProgram(
            fragmentSource,
            vertexSource,
            geometrySource ?? string.Empty);
        Debug.Log("[XEngine] WebGL2 GLSL compiled " + Name + " handle=" + program.Handle);

        return new ShaderVariant(program);
    }

    private ShaderVariant CompileHlslVariant(
        GraphicsBackend backend,
        IReadOnlyDictionary<string, bool>? keywordID,
        string keywords)
    {
        if (!HasHlsl)
        {
            Debug.LogWarning(
                $"Shader pass '{Name}' has no SLANGPROGRAM or legacy HLSLPROGRAM for backend {backend}. " +
                "Falling back to Invalid shader. Add a SLANGPROGRAM block for Vulkan/D3D12.");

            var fallbackShader = Resources.Shader.LoadDefault(Resources.DefaultShader.Invalid);
            if (fallbackShader.IsValid() && fallbackShader.GetPass(0).HasHlsl)
            {
                if (fallbackShader.GetPass(0).TryGetVariant(null, out ShaderVariant? fb) && fb != null)
                    return fb;
            }

            throw new Exception(
                $"Failed to compile shader pass of {Name}: HLSL source missing for {backend} and Invalid fallback has no HLSL.");
        }

#if !XENGINE_MOBILE || XENGINE_WEB
        if (backend == GraphicsBackend.WebGL2)
            return CompileWebGL2Variant(ResolveHlslBytecode(backend, keywordID, keywords));
#endif
        return new ShaderVariant(ResolveHlslBytecode(backend, keywordID, keywords));
    }

#if !XENGINE_MOBILE || XENGINE_WEB
    private static ShaderVariant CompileWebGL2Variant(CompiledShaderBytecode bytecode)
    {
        if (bytecode.Format != ShaderBytecodeFormat.GlslEs300)
        {
            throw new Exception(
                $"WebGL2 variants require GLSL ES 3.00 artifacts (got {bytecode.Format}).");
        }

        string vertex = Encoding.UTF8.GetString(bytecode.VertexBytecode);
        string fragment = Encoding.UTF8.GetString(bytecode.FragmentBytecode);
        WebGL2GlslHost host = OperatingSystem.IsBrowser()
            ? WebGL2GlslHost.Browser
            : WebGL2GlslHost.DesktopCore;
        vertex = WebGL2Glsl.Prepare(vertex, WebGL2GlslStage.Vertex, host);
        fragment = WebGL2Glsl.Prepare(fragment, WebGL2GlslStage.Fragment, host);
        GraphicsProgram program = Graphics.CompileProgram(fragment, vertex, string.Empty);
        return new ShaderVariant(program);
    }
#endif

    /// <summary>
    /// Editor-only disk cache for on-demand variant compiles. The offline artifact pipeline
    /// (ShaderImporter) only precompiles declared variants; draw-time keyword combinations
    /// (SKINNED, HAS_UV2, …) compiled here previously re-ran the full Slang compile on every
    /// editor session — the dominant cost of pressing Play. The editor points this at the
    /// project's artifact store (Library/cache/shaders); keys hash the resolved sources,
    /// keywords, compiler identity and target profile, so shader or compiler changes
    /// invalidate automatically.
    /// </summary>
    internal static Runtime.RHI.Shaders.ShaderArtifactStore? EditorArtifactStore;

    internal CompiledShaderBytecode ResolveHlslBytecode(
        GraphicsBackend backend,
        IReadOnlyDictionary<string, bool>? keywordID,
        string keywords)
    {
        if (ShaderArtifactRuntimeLibrary.IsConfigured)
        {
            ShaderArtifact artifact = ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
                _shaderName,
                Name,
                backend,
                keywords);
            Debug.Log(
                "Loading offline HLSL shader " + _shaderName + "/" + Name + " for " + backend +
                " using " + artifact.CompilerKind + " with keywords: " + keywords);
            return artifact.Bytecode;
        }

#if !XENGINE_MOBILE || XENGINE_WEB
        ShaderCompileRequest request = CreateHlslCompileRequest(backend, keywordID, keywords);
        // The persistent editor cache is versioned by Slang's build identity. DXC is an
        // explicit compatibility path with no equivalent versioned cache identity here;
        // it must neither consume nor overwrite Slang artifacts (including their ABI).
        if (ShaderCompilerFactory.GetSelectedCompilerKind(backend, _shaderName) != ShaderCompilerKind.Slang)
            return CompileHlslBytecodeUncached(backend, keywordID, keywords, request);

        string compilerIdentity = RuntimeSlangShaderCompiler.CompilerIdentity;
        string targetProfile = RuntimeSlangShaderCompiler.GetTargetProfile(backend);
        ShaderArtifactStore? editorStore = EditorArtifactStore;
        if (editorStore != null)
        {
            var cacheKey = Runtime.RHI.Shaders.ShaderArtifactKey.Create(
                request, compilerIdentity, targetProfile, null, RuntimeSlangShaderCompiler.MatrixLayout);
            if (editorStore.TryGet(cacheKey, out ShaderArtifact? cached) &&
                cached.CompilerKind == ShaderCompilerKind.Slang)
            {
                Debug.Log(
                    "Loading cached HLSL shader " + _shaderName + "/" + Name + " for " + backend +
                    " with keywords: " + keywords);
                return cached.Bytecode;
            }

            CompiledShaderBytecode cacheMissResult = compilerCacheMiss(backend, keywordID, keywords, request, compilerIdentity, targetProfile, editorStore, cacheKey);
            return cacheMissResult;
        }

        return CompileHlslBytecodeUncached(backend, keywordID, keywords, request);

        CompiledShaderBytecode compilerCacheMiss(
            GraphicsBackend backend,
            IReadOnlyDictionary<string, bool>? keywordID,
            string keywords,
            ShaderCompileRequest request,
            string compilerIdentity,
            string targetProfile,
            Runtime.RHI.Shaders.ShaderArtifactStore store,
            Runtime.RHI.Shaders.ShaderArtifactKey cacheKey)
        {
            IShaderCompiler compiler = ShaderCompilerFactory.GetCompiler(backend, _shaderName);
            ShaderCompileResult result = compiler.Compile(request);

            if (!result.Success)
                throw new Exception(result.ErrorMessage ?? "HLSL compile failed.");

            if (result.FallbackFrom.HasValue)
                Debug.LogWarning(result.FallbackReason ?? "Shader compiler fallback was used.");

            ShaderCompilerKind compilerKind = result.CompilerKind != ShaderCompilerKind.Unknown
                ? result.CompilerKind
                : ShaderCompilerFactory.GetSelectedCompilerKind(backend, _shaderName);
            Debug.Log(
                "Compiling HLSL shader " + _shaderName + "/" + Name + " for " + backend +
                " using " + compilerKind + " with keywords: " + keywords);

            var bytecode = new CompiledShaderBytecode(
                ShaderLanguage.Hlsl,
                result.Format,
                result.VertexBytecode ?? [],
                result.FragmentBytecode ?? [],
                result.BindingLayout,
                result.VertexInputLayout,
                result.ComputeBytecode);
            store.Put(cacheKey, new ShaderArtifact(
                backend,
                compilerKind,
                compilerIdentity,
                Runtime.RHI.Shaders.ShaderArtifactKey.ComputeSourceHash(request),
                targetProfile,
                RuntimeSlangShaderCompiler.MatrixLayout,
                _shaderName,
                Name,
                request.VariantKey ?? string.Empty,
                request.VertexEntryPoint ?? string.Empty,
                request.FragmentEntryPoint ?? string.Empty,
                null,
                null,
                bytecode));
            return bytecode;
        }
#else
        throw new Exception(
            $"Offline shader artifact is required for {_shaderName}/{Name} [{backend}, variant={keywords}]; runtime HLSL compilation is unavailable on this platform.");
#endif
    }

    private CompiledShaderBytecode CompileHlslBytecodeUncached(
        GraphicsBackend backend,
        IReadOnlyDictionary<string, bool>? keywordID,
        string keywords,
        ShaderCompileRequest request)
    {
        IShaderCompiler compiler = ShaderCompilerFactory.GetCompiler(backend, _shaderName);
        ShaderCompileResult result = compiler.Compile(request);

        if (!result.Success)
            throw new Exception(result.ErrorMessage ?? "HLSL compile failed.");

        if (result.FallbackFrom.HasValue)
            Debug.LogWarning(result.FallbackReason ?? "Shader compiler fallback was used.");

        ShaderCompilerKind compilerKind = result.CompilerKind != ShaderCompilerKind.Unknown
            ? result.CompilerKind
            : ShaderCompilerFactory.GetSelectedCompilerKind(backend, _shaderName);
        Debug.Log(
            "Compiling HLSL shader " + _shaderName + "/" + Name + " for " + backend +
            " using " + compilerKind + " with keywords: " + keywords);

        return new CompiledShaderBytecode(
            ShaderLanguage.Hlsl,
            result.Format,
            result.VertexBytecode ?? [],
            result.FragmentBytecode ?? [],
            result.BindingLayout,
            result.VertexInputLayout,
            result.ComputeBytecode);
    }

    internal ShaderCompileRequest CreateHlslCompileRequest(
        GraphicsBackend backend,
        IReadOnlyDictionary<string, bool>? keywords,
        string variantKey,
        ShaderTargetEnvironment targetEnvironment = ShaderTargetEnvironment.Default) => new()
    {
        TargetBackend = backend,
        TargetEnvironment = targetEnvironment,
        Language = ShaderLanguage.Hlsl,
        VertexSource = _hlslVertexSource,
        FragmentSource = _hlslFragmentSource,
        ComputeSource = string.IsNullOrEmpty(_hlslComputeSource) ? null : _hlslComputeSource,
        Keywords = FilterKeywords(keywords),
        ShaderName = _shaderName,
        PassName = Name,
        VariantKey = variantKey,
    };

    private IReadOnlyDictionary<string, bool>? FilterKeywords(
        IReadOnlyDictionary<string, bool>? keywords)
    {
        if (_offlineVariantKeywords == null || keywords == null || keywords.Count == 0)
            return keywords;

        var filtered = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, bool> entry in keywords)
        {
            if (entry.Value && _offlineVariantKeywords.Contains(entry.Key))
                filtered.Add(entry.Key, true);
        }
        return filtered.Count == 0 ? null : filtered;
    }

    internal static string BuildKeywordKey(
        Dictionary<string, bool>? keywordID,
        HashSet<string>? allowedKeywords = null)
    {
        if (keywordID == null || keywordID.Count == 0)
            return string.Empty;

        var enabled = new List<string>();
        foreach (KeyValuePair<string, bool> entry in keywordID)
        {
            if (entry.Value && (allowedKeywords == null || allowedKeywords.Contains(entry.Key)))
                enabled.Add(entry.Key);
        }
        if (enabled.Count == 0)
            return string.Empty;

        enabled.Sort(StringComparer.Ordinal);
        var result = new StringBuilder();
        for (int i = 0; i < enabled.Count; i++)
            result.Append(enabled[i]).Append(';');
        return result.ToString();
    }

    public bool HasTag(string tag, string? tagValue = null)
    {
        if (_tags.TryGetValue(tag, out string value))
            return tagValue == null || value == tagValue;

        return false;
    }

    /// <summary>
    /// Gets the sort offset for a given tag, or 0 if no offset is specified
    /// </summary>
    public int GetTagSortOffset(string tag)
    {
        return _tagSortOffsets.TryGetValue(tag, out int offset) ? offset : 0;
    }

    /// <summary>Disposes every compiled variant program. The pass itself keeps its source/tags so it
    /// can still recompile fresh variants on next use if the owning Shader isn't actually disposed.</summary>
    public void Dispose()
    {
        foreach (var variant in _variants.Values)
            variant.Dispose();
        _variants.Clear();
        _variantDevices.Clear();
    }
}
