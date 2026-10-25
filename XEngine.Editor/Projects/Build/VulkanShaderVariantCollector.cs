// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

using XEngine.Editor;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Projects.Scripting;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

namespace XEngine.Editor.Build;

// Platform-agnostic shader variant collection for Vulkan targets (Android, HarmonyOS, ...). Both
// mobile platforms compile shaders through the Slang SPIR-V path, so the keyword/mesh/terrain/
// pipeline discovery is identical; only the entry-point error strings and the per-project trace
// format are platform-owned. This is the single home for that shared algorithm.

internal sealed record VulkanShaderVariantUsage(
    string Source,
    IReadOnlyDictionary<string, bool>? Keywords = null,
    string? CanonicalKeywordSet = null,
    string? PassStableId = null);

internal sealed record VulkanShaderVariantRoot(
    Guid ShaderAssetGuid,
    Shader Shader,
    IReadOnlyList<VulkanShaderVariantUsage> Usages);

internal sealed record VulkanPipelineShaderVariantUsage(
    DefaultShader Shader,
    string Source,
    IReadOnlyDictionary<string, bool> Keywords);

internal sealed record VulkanShaderVariantDescriptor(
    Guid ShaderAssetGuid,
    string PassName,
    string PassStableId,
    string Backend,
    string ShaderTier,
    string CanonicalKeywordSet,
    string CompilerIdentity,
    string TargetProfile,
    string TargetEnvironment,
    IReadOnlyList<string> Reasons);

internal sealed class VulkanShaderVariantCollection
{
    internal const int SchemaVersion = 1;

    internal VulkanShaderVariantCollection(IReadOnlyList<VulkanShaderVariantDescriptor> descriptors)
    {
        Descriptors = descriptors;
    }

    internal IReadOnlyList<VulkanShaderVariantDescriptor> Descriptors { get; }

    internal int ShaderCount
    {
        get
        {
            var guids = new HashSet<Guid>();
            for (int i = 0; i < Descriptors.Count; i++)
                guids.Add(Descriptors[i].ShaderAssetGuid);
            return guids.Count;
        }
    }
}

/// <summary>
/// One approved runtime shader lookup that should be replayed at build time. Platform trace readers
/// (for example <c>AndroidShaderVariantTrace</c>) project their on-disk records into this neutral
/// shape so the Vulkan collector can apply them without depending on a platform-specific schema.
/// </summary>
internal readonly record struct ShaderVariantTraceReplay(
    string ShaderName,
    string PassName,
    string CanonicalKeywordSet,
    string Source,
    bool Found);

internal static class VulkanShaderVariantCollector
{
    internal const int MaxContributorContributions = 4096;
    private static readonly IReadOnlyDictionary<string, bool> EmptyKeywords =
        new Dictionary<string, bool>(0, StringComparer.Ordinal);

    internal const string ShaderTier = "baseline";
    internal const string TargetEnvironment = "vulkan1.1";
    internal const string MetalTargetEnvironment = "metal";
    internal const string WebGL2TargetEnvironment = "webgl2";

    internal static VulkanShaderVariantCollection Collect(
        IEnumerable<VulkanShaderVariantRoot> roots,
        ShaderVariantStripper? stripper = null,
        GraphicsBackend backend = GraphicsBackend.Vulkan)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (backend is not GraphicsBackend.Vulkan and not GraphicsBackend.Metal and not GraphicsBackend.WebGL2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(backend),
                backend,
                "Shader variant collection currently supports Vulkan, Metal, and WebGL2.");
        }

        stripper ??= ShaderVariantStripper.Unfiltered;
        string backendName = backend.ToString();
        string targetProfile = RuntimeSlangShaderCompiler.GetTargetProfile(backend);
        string targetEnvironment = backend switch
        {
            GraphicsBackend.Metal => MetalTargetEnvironment,
            GraphicsBackend.WebGL2 => WebGL2TargetEnvironment,
            _ => TargetEnvironment,
        };

        var descriptors = new Dictionary<DescriptorIdentity, MutableDescriptor>();
        foreach (VulkanShaderVariantRoot root in roots)
        {
            if (root.ShaderAssetGuid == Guid.Empty)
                throw new ArgumentException("Shader roots require a non-empty asset GUID.", nameof(roots));
            ArgumentNullException.ThrowIfNull(root.Shader);
            ArgumentNullException.ThrowIfNull(root.Usages);
            if (!stripper.KeepShaderAsset(root.ShaderAssetGuid) || !stripper.KeepShader(root.Shader))
                continue;

            int passIndex = 0;
            foreach (ShaderPass pass in root.Shader.Passes)
            {
                string passStableId = string.IsNullOrWhiteSpace(pass.Name)
                    ? "pass-" + passIndex.ToString("D4", CultureInfo.InvariantCulture)
                    : pass.Name;
                Add(root.ShaderAssetGuid, pass.Name, passStableId, string.Empty, "default");

                // Metal Players cannot compile shader variants at runtime. Include variants
                // declared by the shader even when their selecting assets are instantiated later
                // from Resources and therefore are not visible in the build scene hierarchy.
                if (backend == GraphicsBackend.Metal && pass.HasHlsl)
                {
                    for (int variantIndex = 0; variantIndex < root.Shader.OfflineVariantKeys.Count; variantIndex++)
                    {
                        string declaredKey = root.Shader.OfflineVariantKeys[variantIndex];
                        string keywordSet = root.Shader.BuildKeywordKey(ParseKeywordSet(declaredKey));
                        if (stripper.KeepCanonicalKeywordSet(keywordSet))
                            Add(root.ShaderAssetGuid, pass.Name, passStableId, keywordSet, "shader-manifest");
                    }
                }

                for (int usageIndex = 0; usageIndex < root.Usages.Count; usageIndex++)
                {
                    VulkanShaderVariantUsage usage = root.Usages[usageIndex]
                        ?? throw new InvalidDataException("Shader variant usage cannot be null.");
                    if (usage.PassStableId != null &&
                        !string.Equals(usage.PassStableId, passStableId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string source = string.IsNullOrWhiteSpace(usage.Source) ? "explicit" : usage.Source;
                    // Keep the package lookup key identical to Material.KeywordKey at runtime.
                    // Mesh layout collection carries attributes used by every pass, while a shader
                    // manifest admits only its declared variant axes (for example StandardShadow
                    // keeps HAS_TANGENTS but rejects HAS_NORMALS and HAS_UV).
                    Dictionary<string, bool>? keywords = usage.CanonicalKeywordSet != null
                        ? ParseKeywordSet(usage.CanonicalKeywordSet)
                        : usage.Keywords is Dictionary<string, bool> dictionary
                            ? dictionary
                            : ToDictionary(usage.Keywords);
                    string keywordSet = root.Shader.BuildKeywordKey(keywords);
                    if (!stripper.KeepCanonicalKeywordSet(keywordSet))
                        continue;
                    Add(root.ShaderAssetGuid, pass.Name, passStableId, keywordSet, source);

                    // Standard selects this bounded specialization at runtime when both local
                    // light trees are empty. Keep both paths so newly enabled lights remain lit.
                    if ((backend is GraphicsBackend.Vulkan or GraphicsBackend.Metal) &&
                        pass.ShaderName == "Default/Standard")
                    {
                        var lightingKeywords = ToDictionary(keywords) ?? new Dictionary<string, bool>(StringComparer.Ordinal);
                        lightingKeywords["DIRECTIONAL_ONLY"] = false;
                        string fullLightingKey = root.Shader.BuildKeywordKey(lightingKeywords);
                        if (stripper.KeepCanonicalKeywordSet(fullLightingKey))
                            Add(root.ShaderAssetGuid, pass.Name, passStableId, fullLightingKey, source + ":full-lighting");
                        lightingKeywords["DIRECTIONAL_ONLY"] = true;
                        string lightingKey = root.Shader.BuildKeywordKey(lightingKeywords);
                        if (stripper.KeepCanonicalKeywordSet(lightingKey))
                            Add(root.ShaderAssetGuid, pass.Name, passStableId, lightingKey, source + ":directional-only");
                    }

                    // ParticleSystemComponent clones materials for mesh/local/world alignment
                    // and enables PARTICLE_MESH at runtime. Resource-loaded prefabs are not
                    // scene renderers, so their authored material alone misses this combination.
                    // Keep both bounded particle modes for shaders that declare that axis.
                    var particleKeywords = ToDictionary(keywords) ?? new Dictionary<string, bool>(StringComparer.Ordinal);
                    particleKeywords["PARTICLE_MESH"] = true;
                    if (root.Shader.BuildKeywordKey(particleKeywords).Contains("PARTICLE_MESH;", StringComparison.Ordinal))
                    {
                        particleKeywords["GPU_INSTANCING"] = true;
                        AddParticleMode(true);
                        AddParticleMode(false);
                    }

                    void AddParticleMode(bool oriented)
                    {
                        particleKeywords["PARTICLE_MESH"] = oriented;
                        string particleKey = root.Shader.BuildKeywordKey(particleKeywords);
                        if (stripper.KeepCanonicalKeywordSet(particleKey))
                            Add(root.ShaderAssetGuid, pass.Name, passStableId, particleKey, source + ":runtime-particle");
                    }
                }

                passIndex++;

                void Add(
                    Guid guid,
                    string passName,
                    string stablePassId,
                    string keywordSet,
                    string reason)
                {
                    var identity = new DescriptorIdentity(guid, stablePassId, keywordSet);
                    if (!descriptors.TryGetValue(identity, out MutableDescriptor? descriptor))
                    {
                        descriptor = new MutableDescriptor(
                            guid,
                            passName,
                            stablePassId,
                            keywordSet,
                            RuntimeSlangShaderCompiler.CompilerIdentity,
                            targetProfile);
                        descriptors.Add(identity, descriptor);
                    }
                    descriptor.Reasons.Add(reason);
                }
            }
        }

        var result = new List<VulkanShaderVariantDescriptor>(descriptors.Count);
        foreach (MutableDescriptor descriptor in descriptors.Values)
        {
            string[] reasons = new string[descriptor.Reasons.Count];
            descriptor.Reasons.CopyTo(reasons);
            Array.Sort(reasons, StringComparer.Ordinal);
            result.Add(new VulkanShaderVariantDescriptor(
                descriptor.ShaderAssetGuid,
                descriptor.PassName,
                descriptor.PassStableId,
                backendName,
                ShaderTier,
                descriptor.CanonicalKeywordSet,
                descriptor.CompilerIdentity,
                descriptor.TargetProfile,
                targetEnvironment,
                Array.AsReadOnly(reasons)));
        }

        result.Sort(static (left, right) =>
        {
            int comparison = left.ShaderAssetGuid.CompareTo(right.ShaderAssetGuid);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.PassStableId, right.PassStableId);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.CanonicalKeywordSet, right.CanonicalKeywordSet);
        });
        return new VulkanShaderVariantCollection(Array.AsReadOnly(result.ToArray()));
    }

    internal static IReadOnlyList<ShaderArtifactBuildSelection> CreateArtifactSelections(
        VulkanShaderVariantCollection collection,
        ShaderTargetEnvironment targetEnvironment = ShaderTargetEnvironment.Default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var selections = new ShaderArtifactBuildSelection[collection.Descriptors.Count];
        for (int i = 0; i < collection.Descriptors.Count; i++)
        {
            VulkanShaderVariantDescriptor descriptor = collection.Descriptors[i];
            if (!Enum.TryParse(descriptor.Backend, ignoreCase: false, out GraphicsBackend backend))
            {
                throw new InvalidDataException(
                    $"Shader variant descriptor has an invalid backend '{descriptor.Backend}'.");
            }
            if (targetEnvironment != ShaderTargetEnvironment.Default && backend != GraphicsBackend.Metal)
            {
                throw new ArgumentException(
                    $"Target environment {targetEnvironment} cannot be applied to {backend} shader selections.",
                    nameof(targetEnvironment));
            }
            selections[i] = new ShaderArtifactBuildSelection(
                descriptor.ShaderAssetGuid,
                descriptor.PassName,
                backend,
                descriptor.CanonicalKeywordSet,
                targetEnvironment);
        }
        return Array.AsReadOnly(selections);
    }

    /// <summary>
    /// Builds Vulkan roots from the editor asset closure. Materials contribute their serialized
    /// keyword state, while build scenes additionally contribute the mesh-layout keywords that the
    /// render pipeline applies immediately before drawing. Built-in shaders are included
    /// because runtime fallback and pipeline selection can request them without a serialized
    /// material edge. Optional Feature shaders (ScreenSpace Decal / screen-space shadows) are
    /// stripped unless the matching RendererFeature is enabled. Optional contributors supply runtime-only variants, and an optional trace
    /// replays approved runtime lookups; either may be omitted for platforms that do not own them.
    /// </summary>
    internal static VulkanShaderVariantCollection CollectForBuild(
        IEnumerable<Guid> collectedAssets,
        IEnumerable<IVulkanShaderVariantContributor>? contributors = null,
        IEnumerable<ShaderVariantTraceReplay>? traceReplay = null,
        bool includeBuiltInShaders = true,
        ShaderVariantStripper? stripper = null,
        GraphicsBackend backend = GraphicsBackend.Vulkan)
    {
        ArgumentNullException.ThrowIfNull(collectedAssets);
        stripper ??= ShaderVariantStripper.FromPipeline(UniversalRenderPipeline.Default);
        HashSet<Guid> collectedAssetSet = new(collectedAssets);
        var shaderRoots = new Dictionary<Guid, RootBuilder>();
        QualitySettings.AntiAliasingMode buildAntiAliasing =
            EditorRegistries.GetSettings<GraphicsSettings>().AntiAliasing;
        UniversalRenderPipelineGlobalSettings? globalSettings = UniversalRenderPipelineGlobalSettings.Active;
        if (globalSettings is { ExportShaderVariants: false })
        {
            Debug.Log("[VulkanShaderVariantCollector] ExportShaderVariants is off — skipping variant collection.");
            return new VulkanShaderVariantCollection(Array.Empty<VulkanShaderVariantDescriptor>());
        }
        int logLevel = globalSettings?.ShaderVariantLogLevel ?? 0;
        int strippedShaders = 0;

        if (includeBuiltInShaders)
        {
            foreach (Guid guid in BuildShaderInventory.GetBuiltInShaderGuids(Project.Current))
            {
                if (!stripper.KeepShaderAsset(guid))
                {
                    strippedShaders++;
                    if (logLevel >= 2)
                        Debug.Log($"[VulkanShaderVariantCollector] Stripped built-in shader {guid}.");
                    continue;
                }
                if (BuiltInAssets.Get(guid) is not Shader shader)
                    continue;
                if (!shaderRoots.ContainsKey(guid))
                    shaderRoots.Add(guid, new RootBuilder(shader));
            }
        }
        if (logLevel >= 1 && strippedShaders > 0)
            Debug.Log($"[VulkanShaderVariantCollector] Stripped {strippedShaders} built-in shader(s) by variant settings.");

        EditorAssetBackend? database = EditorAssetBackend.Instance;
        if (database != null)
        {
            foreach (Guid guid in collectedAssets)
            {
                if (BuiltInAssets.IsBuiltIn(guid) && BuiltInAssets.Get(guid) is Shader builtInShader)
                {
                    if (!shaderRoots.ContainsKey(guid))
                        shaderRoots.Add(guid, new RootBuilder(builtInShader));
                    continue;
                }
                AssetEntry? entry = database.GetEntry(guid);
                if (entry == null)
                    continue;
                EngineObject? asset = AssetDatabase.Get(guid);
                if (asset is Shader shader)
                {
                    if (!shaderRoots.ContainsKey(guid))
                        shaderRoots.Add(guid, new RootBuilder(shader));
                    continue;
                }

                if (asset is Material material)
                    AddMaterialUsage(shaderRoots, material, "material:" + entry.Path, material._localKeywords);
                else if (asset is Scene scene)
                {
                    CollectSceneMeshUsages(shaderRoots, scene, entry.Path);
                    CollectRegisteredSceneUsages(shaderRoots, scene, entry.Path);
                    CollectScenePipelineUsages(shaderRoots, scene, entry.Path, buildAntiAliasing);
                }
            }
        }

        IReadOnlyList<IVulkanShaderVariantContributor> discoveredContributors =
            contributors == null ? DiscoverContributors() : MaterializeContributors(contributors);
        if (discoveredContributors.Count > 0)
            AddContributorUsages(shaderRoots, collectedAssetSet, discoveredContributors);

        var rootList = new List<VulkanShaderVariantRoot>(shaderRoots.Count);
        foreach (KeyValuePair<Guid, RootBuilder> root in shaderRoots)
            rootList.Add(new VulkanShaderVariantRoot(root.Key, root.Value.Shader, root.Value.Usages));
        IReadOnlyList<VulkanShaderVariantRoot> roots = rootList;
        if (traceReplay != null)
            roots = AddTraceReplayUsages(roots, traceReplay);
        return Collect(roots, stripper, backend);
    }

    internal static IReadOnlyList<IVulkanShaderVariantContributor> DiscoverContributors()
    {
        var types = new List<Type>();
        var seen = new HashSet<Type>();
        foreach (Type type in ScriptAssemblyManager.GetAllTypes())
        {
            if (!typeof(IVulkanShaderVariantContributor).IsAssignableFrom(type) ||
                type.IsAbstract || type.IsInterface || type.ContainsGenericParameters ||
                !seen.Add(type))
            {
                continue;
            }
            // Only public, externally-deployable contributor types are reflectively instantiated.
            // Private/nested wrapper adapters (e.g. a platform bridge that wraps another contributor
            // interface) are created on demand by their host pipeline, never discovered here.
            if (!type.IsPublic && !type.IsNestedPublic)
                continue;
            types.Add(type);
        }

        types.Sort(static (left, right) => StringComparer.Ordinal.Compare(
            left.FullName ?? left.Name,
            right.FullName ?? right.Name));

        var contributors = new List<IVulkanShaderVariantContributor>(types.Count);
        foreach (Type type in types)
        {
            ConstructorInfo? constructor = type.GetConstructor(Type.EmptyTypes);
            if (constructor == null)
            {
                throw new InvalidDataException(
                    $"Shader variant contributor '{type.FullName ?? type.Name}' requires a public parameterless constructor.");
            }

            IVulkanShaderVariantContributor contributor;
            try
            {
                contributor = (IVulkanShaderVariantContributor)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"Failed to instantiate shader variant contributor '{type.FullName ?? type.Name}'.", ex);
            }
            contributors.Add(contributor);
        }
        return MaterializeContributors(contributors);
    }

    private static IReadOnlyList<IVulkanShaderVariantContributor> MaterializeContributors(
        IEnumerable<IVulkanShaderVariantContributor> contributors)
    {
        var materialized = new List<IVulkanShaderVariantContributor>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (IVulkanShaderVariantContributor contributor in contributors)
        {
            ArgumentNullException.ThrowIfNull(contributor);
            string id = contributor.Id?.Trim() ?? string.Empty;
            if (id.Length == 0)
                throw new InvalidDataException("Shader variant contributor IDs cannot be empty.");
            if (!ids.Add(id))
                throw new InvalidDataException($"Duplicate shader variant contributor ID '{id}'.");
            materialized.Add(contributor);
        }

        materialized.Sort(static (left, right) =>
        {
            int comparison = StringComparer.Ordinal.Compare(left.Id, right.Id);
            if (comparison != 0) return comparison;
            return StringComparer.Ordinal.Compare(left.GetType().FullName, right.GetType().FullName);
        });
        return materialized;
    }

    internal static IReadOnlyList<VulkanShaderVariantRoot> AddContributorUsages(
        IReadOnlyList<VulkanShaderVariantRoot> shaderRoots,
        IReadOnlySet<Guid> collectedAssets,
        IEnumerable<IVulkanShaderVariantContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(shaderRoots);
        ArgumentNullException.ThrowIfNull(collectedAssets);
        ArgumentNullException.ThrowIfNull(contributors);
        var roots = new Dictionary<Guid, RootBuilder>(shaderRoots.Count);
        for (int i = 0; i < shaderRoots.Count; i++)
        {
            VulkanShaderVariantRoot root = shaderRoots[i];
            roots.Add(root.ShaderAssetGuid, new RootBuilder(root.Shader, root.Usages));
        }
        AddContributorUsages(roots, collectedAssets, MaterializeContributors(contributors));

        var result = new List<VulkanShaderVariantRoot>(roots.Count);
        foreach (KeyValuePair<Guid, RootBuilder> root in roots)
            result.Add(new VulkanShaderVariantRoot(root.Key, root.Value.Shader, root.Value.Usages));
        return result;
    }

    private static void AddContributorUsages(
        Dictionary<Guid, RootBuilder> shaderRoots,
        IReadOnlySet<Guid> collectedAssets,
        IReadOnlyList<IVulkanShaderVariantContributor> contributors)
    {
        int totalContributionCount = 0;
        for (int contributorIndex = 0; contributorIndex < contributors.Count; contributorIndex++)
        {
            IVulkanShaderVariantContributor contributor = contributors[contributorIndex];
            int contributorContributionCount = 0;
            string contributorId = contributor.Id.Trim();
            var context = new VulkanShaderVariantContributorContext(
                contributorId,
                (shaderGuid, source, keywords, passStableId) =>
                {
                    if (shaderGuid == Guid.Empty)
                        throw new InvalidDataException(
                            $"Shader variant contributor '{contributorId}' supplied an empty shader asset GUID.");
                    if (!collectedAssets.Contains(shaderGuid) && !shaderRoots.ContainsKey(shaderGuid))
                        throw new InvalidDataException(
                            $"Shader variant contributor '{contributorId}' references shader '{shaderGuid}', " +
                            "which is not present in the asset closure.");

                    string normalizedSource = source?.Trim() ?? string.Empty;
                    if (normalizedSource.Length == 0)
                        throw new InvalidDataException(
                            $"Shader variant contributor '{contributorId}' supplied an empty source.");
                    string? normalizedPass = passStableId?.Trim();
                    if (passStableId != null && normalizedPass!.Length == 0)
                        throw new InvalidDataException(
                            $"Shader variant contributor '{contributorId}' supplied an empty pass ID.");
                    if (keywords != null)
                    {
                        foreach (KeyValuePair<string, bool> keyword in keywords)
                        {
                            if (string.IsNullOrWhiteSpace(keyword.Key))
                                throw new InvalidDataException(
                                    $"Shader variant contributor '{contributorId}' supplied an empty keyword name.");
                        }
                    }

                    contributorContributionCount++;
                    totalContributionCount++;
                    if (contributorContributionCount > MaxContributorContributions ||
                        totalContributionCount > MaxContributorContributions)
                    {
                        throw new InvalidDataException(
                            $"Shader variant contributor '{contributorId}' exceeded the {MaxContributorContributions} contribution limit.");
                    }

                    if (!shaderRoots.TryGetValue(shaderGuid, out RootBuilder? root))
                    {
                        EngineObject? asset = AssetDatabase.Get(shaderGuid);
                        if (asset is not Shader shader)
                            throw new InvalidDataException(
                                $"Shader variant contributor '{contributorId}' references shader '{shaderGuid}', " +
                                "but the asset is not a Shader.");
                        root = new RootBuilder(shader);
                        shaderRoots.Add(shaderGuid, root);
                    }

                    var copiedKeywords = keywords == null
                        ? null
                        : new Dictionary<string, bool>(keywords, StringComparer.Ordinal);
                    root.Usages.Add(new VulkanShaderVariantUsage(
                        $"contributor:{contributorId}:{normalizedSource}",
                        copiedKeywords,
                        PassStableId: normalizedPass));
                });

            try
            {
                contributor.Contribute(context);
            }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                throw new InvalidDataException(
                    $"Shader variant contributor '{contributorId}' failed while contributing variants.", ex);
            }
        }
    }

    internal static IReadOnlyList<VulkanShaderVariantRoot> AddTraceReplayUsages(
        IReadOnlyList<VulkanShaderVariantRoot> shaderRoots,
        IEnumerable<ShaderVariantTraceReplay> trace)
    {
        ArgumentNullException.ThrowIfNull(shaderRoots);
        ArgumentNullException.ThrowIfNull(trace);

        List<ShaderVariantTraceReplay> entries = MaterializeTraceReplay(trace);
        if (entries.Count == 0)
            return shaderRoots;

        var additions = new List<VulkanShaderVariantUsage>?[shaderRoots.Count];
        for (int traceIndex = 0; traceIndex < entries.Count; traceIndex++)
        {
            ShaderVariantTraceReplay entry = entries[traceIndex];
            int matchedRootIndex = -1;
            for (int rootIndex = 0; rootIndex < shaderRoots.Count; rootIndex++)
            {
                VulkanShaderVariantRoot root = shaderRoots[rootIndex];
                // Runtime lookup records ShaderPass.ShaderName, which is derived from the
                // authored artifact name. BuiltInAssets later gives the same Shader object a
                // concise display Name (for example "Blit"), so Name is not a valid replay key.
                if (!string.Equals(root.Shader.ArtifactName, entry.ShaderName, StringComparison.Ordinal))
                    continue;
                if (matchedRootIndex >= 0)
                {
                    throw new InvalidDataException(
                        $"Approved shader trace '{entry.Source}' matches multiple shaders named '{entry.ShaderName}'.");
                }
                matchedRootIndex = rootIndex;
            }

            if (matchedRootIndex < 0)
            {
                throw new InvalidDataException(
                    $"Approved shader trace '{entry.Source}' references shader '{entry.ShaderName}', " +
                    "which is not present in the asset closure.");
            }

            bool passFound = false;
            foreach (ShaderPass pass in shaderRoots[matchedRootIndex].Shader.Passes)
            {
                if (string.Equals(pass.Name, entry.PassName, StringComparison.Ordinal))
                {
                    passFound = true;
                    break;
                }
            }
            if (!passFound)
            {
                throw new InvalidDataException(
                    $"Approved shader trace '{entry.Source}' references missing pass " +
                    $"'{entry.PassName}' on shader '{entry.ShaderName}'.");
            }

            List<VulkanShaderVariantUsage> rootAdditions = additions[matchedRootIndex] ??= [];
            rootAdditions.Add(new VulkanShaderVariantUsage(
                "runtime-trace:" + entry.Source + (entry.Found ? ":hit" : ":miss"),
                CanonicalKeywordSet: entry.CanonicalKeywordSet,
                PassStableId: entry.PassName));
        }

        var mergedRoots = new List<VulkanShaderVariantRoot>(shaderRoots.Count);
        for (int rootIndex = 0; rootIndex < shaderRoots.Count; rootIndex++)
        {
            VulkanShaderVariantRoot root = shaderRoots[rootIndex];
            List<VulkanShaderVariantUsage>? rootAdditions = additions[rootIndex];
            if (rootAdditions == null)
            {
                mergedRoots.Add(root);
                continue;
            }

            var usages = new List<VulkanShaderVariantUsage>(root.Usages.Count + rootAdditions.Count);
            for (int usageIndex = 0; usageIndex < root.Usages.Count; usageIndex++)
                usages.Add(root.Usages[usageIndex]);
            for (int usageIndex = 0; usageIndex < rootAdditions.Count; usageIndex++)
                usages.Add(rootAdditions[usageIndex]);
            mergedRoots.Add(new VulkanShaderVariantRoot(root.ShaderAssetGuid, root.Shader, usages));
        }
        return mergedRoots;
    }

    private static List<ShaderVariantTraceReplay> MaterializeTraceReplay(
        IEnumerable<ShaderVariantTraceReplay> trace)
    {
        if (trace is IReadOnlyList<ShaderVariantTraceReplay> list)
        {
            var copy = new List<ShaderVariantTraceReplay>(list.Count);
            for (int i = 0; i < list.Count; i++)
                copy.Add(list[i]);
            return copy;
        }

        var materialized = new List<ShaderVariantTraceReplay>();
        foreach (ShaderVariantTraceReplay entry in trace)
            materialized.Add(entry);
        return materialized;
    }

    internal static IReadOnlyDictionary<string, bool> CreateMeshVariantKeywords(
        IReadOnlyDictionary<string, bool>? materialKeywords,
        Mesh mesh,
        bool supportsBlendShapes = true,
        bool isInstanced = false)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var keywords = new Dictionary<string, bool>(
            (materialKeywords?.Count ?? 0) + 9,
            StringComparer.Ordinal);
        if (materialKeywords != null)
        {
            foreach (KeyValuePair<string, bool> keyword in materialKeywords)
                keywords[keyword.Key] = keyword.Value;
        }

        // Keep the build descriptor aligned with the per-batch mutations in RenderPipeline.
        keywords["HAS_NORMALS"] = mesh.HasNormals;
        keywords["HAS_TANGENTS"] = mesh.HasTangents;
        keywords["HAS_UV"] = mesh.HasUV;
        keywords["HAS_UV2"] = mesh.HasUV2;
        keywords["HAS_COLORS"] = mesh.HasColors || mesh.HasColors32;
        keywords["HAS_BONEINDICES"] = mesh.HasBoneIndices;
        keywords["HAS_BONEWEIGHTS"] = mesh.HasBoneWeights;
        keywords["SKINNED"] = mesh.HasBoneIndices && mesh.HasBoneWeights;
        keywords["BLENDSHAPES"] = supportsBlendShapes && mesh.HasBlendShapes;
        if (isInstanced)
            keywords["GPU_INSTANCING"] = true;
        return keywords;
    }

    internal static IReadOnlyDictionary<string, bool> CreateTerrainVariantKeywords(
        IReadOnlyDictionary<string, bool>? materialKeywords,
        int layerCount,
        bool bicubic)
    {
        var keywords = new Dictionary<string, bool>(
            (materialKeywords?.Count ?? 0) + 12,
            StringComparer.Ordinal);
        if (materialKeywords != null)
        {
            foreach (KeyValuePair<string, bool> keyword in materialKeywords)
                keywords[keyword.Key] = keyword.Value;
        }

        // TerrainComponent creates an instanced grid with position and UV only before rendering.
        keywords["HAS_NORMALS"] = false;
        keywords["HAS_TANGENTS"] = false;
        keywords["HAS_UV"] = true;
        keywords["HAS_UV2"] = false;
        keywords["HAS_COLORS"] = false;
        keywords["HAS_BONEINDICES"] = false;
        keywords["HAS_BONEWEIGHTS"] = false;
        keywords["SKINNED"] = false;
        keywords["BLENDSHAPES"] = false;
        keywords["GPU_INSTANCING"] = true;
        keywords["TERRAIN_8_LAYERS"] = layerCount > 4;
        keywords["TERRAIN_BICUBIC"] = bicubic;
        return keywords;
    }

    internal static IReadOnlyDictionary<string, bool> CreateGrassVariantKeywords(
        IReadOnlyDictionary<string, bool>? materialKeywords)
    {
        var keywords = new Dictionary<string, bool>(
            (materialKeywords?.Count ?? 0) + 10,
            StringComparer.Ordinal);
        if (materialKeywords != null)
        {
            foreach (KeyValuePair<string, bool> keyword in materialKeywords)
                keywords[keyword.Key] = keyword.Value;
        }

        keywords["HAS_NORMALS"] = true;
        keywords["HAS_TANGENTS"] = false;
        keywords["HAS_UV"] = true;
        keywords["HAS_UV2"] = false;
        keywords["HAS_COLORS"] = false;
        keywords["HAS_BONEINDICES"] = false;
        keywords["HAS_BONEWEIGHTS"] = false;
        keywords["SKINNED"] = false;
        keywords["BLENDSHAPES"] = false;
        keywords["GPU_INSTANCING"] = true;
        return keywords;
    }

    private static void CollectRegisteredSceneUsages(
        Dictionary<Guid, RootBuilder> shaderRoots,
        Scene scene,
        string scenePath)
    {
        var sink = new ShaderVariantSceneSink(
            (material, fallbackShader, fallbackGuid, source, keywords) =>
                AddMaterialOrBuiltInUsage(shaderRoots, material, fallbackShader, fallbackGuid, source, keywords),
            (material, mesh, source) =>
                AddMaterialOrBuiltInUsage(
                    shaderRoots,
                    material,
                    Shader.LoadDefault(DefaultShader.Standard),
                    BuiltInAssets.GuidFor(DefaultShader.Standard),
                    source,
                    CreateMeshVariantKeywords(material?._localKeywords, mesh, supportsBlendShapes: false, isInstanced: true)));
        ShaderVariantSceneCollectors.CollectAll(scene, scenePath, sink);
    }

    internal static void CollectScenePipelineUsages(
        Scene scene,
        string scenePath,
        List<VulkanPipelineShaderVariantUsage> usages,
        QualitySettings.AntiAliasingMode? projectAntiAliasing = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenePath);
        ArgumentNullException.ThrowIfNull(usages);

        foreach (Camera? camera in scene.FindObjectsOfType<Camera>())
        {
            if (camera == null)
                continue;

            bool hasCameraAntiAliasing = false;
            for (int effectIndex = 0; effectIndex < camera.Effects.Count; effectIndex++)
            {
                ImageEffect? effect = camera.Effects[effectIndex];
                if (effect == null || !effect.Enabled)
                    continue;

                if (effect is FXAAEffect or SMAAEffect or TAAEffect)
                    hasCameraAntiAliasing = true;

                string source = "scene-pipeline:" + scenePath + ":" + camera.Identifier.ToString("N") + ":effect-" + effectIndex.ToString(CultureInfo.InvariantCulture);
                switch (effect)
                {
                    case AutoExposureEffect:
                        usages.Add(CreateBuiltInEffectUsage(DefaultShader.AutoExposure, source, "auto-exposure"));
                        break;
                    case BloomEffect:
                        usages.Add(CreateBuiltInEffectUsage(DefaultShader.Bloom, source, "bloom"));
                        break;
                    case TonemapperEffect tonemapper:
                        usages.Add(new VulkanPipelineShaderVariantUsage(
                            DefaultShader.Tonemapper,
                            source + ":tonemapper",
                            CreateTonemapperVariantKeywords(tonemapper.Type)));
                        break;
                    case BokehDepthOfFieldEffect bokeh:
                        usages.Add(new VulkanPipelineShaderVariantUsage(
                            DefaultShader.BokehDoF,
                            source + ":bokeh-dof",
                            CreateBokehVariantKeywords(bokeh.UseAutoFocus)));
                        break;
                    case CinematicEffects:
                        usages.Add(CreateBuiltInEffectUsage(DefaultShader.CinematicEffects, source, "cinematic-effects"));
                        break;
                    case FXAAEffect:
                        usages.Add(CreateBuiltInEffectUsage(DefaultShader.FXAA, source, "fxaa"));
                        break;
                    case GTAOEffect:
                        usages.Add(CreateBuiltInEffectUsage(DefaultShader.GTAO, source, "gtao"));
                        break;
                    case MotionBlurEffect:
                        usages.Add(CreateBuiltInEffectUsage(DefaultShader.MotionBlur, source, "motion-blur"));
                        break;
                    case ScreenSpaceReflectionEffect:
                        usages.Add(CreateBuiltInEffectUsage(DefaultShader.SSR, source, "screen-space-reflection"));
                        break;
                    case SMAAEffect:
                        usages.Add(CreateBuiltInEffectUsage(DefaultShader.SMAA, source, "smaa"));
                        break;
                    case TAAEffect:
                        usages.Add(CreateBuiltInEffectUsage(DefaultShader.TAA, source, "taa"));
                        break;
                    case VolumetricFogEffect:
                        usages.Add(CreateBuiltInEffectUsage(DefaultShader.VolumetricFog, source, "volumetric-fog"));
                    break;
                }
            }

            if (!hasCameraAntiAliasing)
            {
                DefaultShader? qualityShader = UniversalRenderPipeline.ResolveFallbackAntiAliasingShader(
                    UniversalRenderPipeline.Default.AntialiasingMode,
                    projectAntiAliasing ?? QualitySettings.AntiAliasing,
                    hasCameraAntiAliasing: false);
                if (qualityShader.HasValue)
                {
                    string sourcePrefix = qualityShader.Value == DefaultShader.TAA
                        ? "project-pipeline:"
                        : "project-graphics:";
                    usages.Add(CreateBuiltInEffectUsage(
                        qualityShader.Value,
                        sourcePrefix + scenePath + ":" + camera.Identifier.ToString("N"),
                        "quality-anti-aliasing"));
                }
            }
        }
    }

    private static VulkanPipelineShaderVariantUsage CreateBuiltInEffectUsage(
        DefaultShader shader,
        string source,
        string effectName)
    {
        return new VulkanPipelineShaderVariantUsage(shader, source + ":" + effectName, EmptyKeywords);
    }

    internal static IReadOnlyDictionary<string, bool> CreateTonemapperVariantKeywords(TonemapperType type)
    {
        string keyword = type switch
        {
            TonemapperType.Melon => "TONEMAP_MELON",
            TonemapperType.ACES => "TONEMAP_ACES",
            TonemapperType.ACESSimple => "TONEMAP_ACES_SIMPLE",
            TonemapperType.AgX => "TONEMAP_AGX",
            TonemapperType.ReinhardSimple => "TONEMAP_REINHARD_SIMPLE",
            TonemapperType.ReinhardLuma => "TONEMAP_REINHARD_LUMA",
            TonemapperType.ReinhardWhitePreserving => "TONEMAP_REINHARD_WHITE",
            TonemapperType.RomBinDaHouse => "TONEMAP_ROMBINDAHOUSE",
            TonemapperType.Uncharted2 => "TONEMAP_UNCHARTED2",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported tonemapper type."),
        };
        return new Dictionary<string, bool>(1, StringComparer.Ordinal) { [keyword] = true };
    }

    internal static IReadOnlyDictionary<string, bool> CreateBokehVariantKeywords(bool useAutoFocus)
    {
        var keywords = new Dictionary<string, bool>(1, StringComparer.Ordinal);
        if (useAutoFocus)
            keywords.Add("AUTOFOCUS", true);
        return keywords;
    }

    internal static void WriteReport(string path, VulkanShaderVariantCollection collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(collection);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);

        var report = new
        {
            schemaVersion = VulkanShaderVariantCollection.SchemaVersion,
            backend = "Vulkan",
            shaderTier = ShaderTier,
            targetEnvironment = TargetEnvironment,
            shaderCount = collection.ShaderCount,
            descriptorCount = collection.Descriptors.Count,
            descriptors = collection.Descriptors,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }) + Environment.NewLine);
    }

    private static Dictionary<string, bool>? ParseKeywordSet(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var keywords = new Dictionary<string, bool>(StringComparer.Ordinal);
        string[] entries = value.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < entries.Length; i++)
        {
            string keyword = entries[i].Trim();
            if (keyword.Length > 0)
                keywords[keyword] = true;
        }
        return keywords.Count == 0 ? null : keywords;
    }

    private static Dictionary<string, bool>? ToDictionary(IReadOnlyDictionary<string, bool>? keywords)
    {
        if (keywords == null || keywords.Count == 0)
            return null;
        var result = new Dictionary<string, bool>(keywords.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, bool> keyword in keywords)
            result[keyword.Key] = keyword.Value;
        return result;
    }

    private static void CollectSceneMeshUsages(
        Dictionary<Guid, RootBuilder> shaderRoots,
        Scene scene,
        string scenePath)
    {
        foreach (MeshRenderer? renderer in scene.FindObjectsOfType<MeshRenderer>())
        {
            if (renderer == null)
                continue;
            AssetRef<Mesh> meshRef = renderer.Mesh;
            meshRef.EnsureLoaded();
            Mesh? mesh = meshRef.Res;
            if (mesh == null)
                continue;
            AddRendererMaterialUsages(shaderRoots, renderer.Materials, mesh, scenePath, renderer.Identifier, supportsBlendShapes: true);
        }

        foreach (SkinnedMeshRenderer? renderer in scene.FindObjectsOfType<SkinnedMeshRenderer>())
        {
            if (renderer == null)
                continue;
            AssetRef<Mesh> meshRef = renderer.SharedMesh;
            meshRef.EnsureLoaded();
            Mesh? mesh = meshRef.Res;
            if (mesh == null)
                continue;
            AddRendererMaterialUsages(shaderRoots, renderer.Materials, mesh, scenePath, renderer.Identifier, supportsBlendShapes: false);
        }
    }

    private static void CollectScenePipelineUsages(
        Dictionary<Guid, RootBuilder> shaderRoots,
        Scene scene,
        string scenePath,
        QualitySettings.AntiAliasingMode projectAntiAliasing)
    {
        var usages = new List<VulkanPipelineShaderVariantUsage>();
        CollectScenePipelineUsages(scene, scenePath, usages, projectAntiAliasing);
        for (int usageIndex = 0; usageIndex < usages.Count; usageIndex++)
        {
            VulkanPipelineShaderVariantUsage usage = usages[usageIndex];
            DefaultShader builtIn = usage.Shader;
            AddShaderUsage(
                shaderRoots,
                BuiltInAssets.GuidFor(builtIn),
                Shader.LoadDefault(builtIn),
                usage.Source,
                usage.Keywords);
        }
    }

    private static void AddRendererMaterialUsages(
        Dictionary<Guid, RootBuilder> shaderRoots,
        List<AssetRef<Material>> materials,
        Mesh mesh,
        string scenePath,
        Guid rendererId,
        bool supportsBlendShapes)
    {
        string source = "scene-mesh:" + scenePath + ":" + rendererId.ToString("N");
        for (int materialIndex = 0; materialIndex < materials.Count; materialIndex++)
        {
            AssetRef<Material> materialRef = materials[materialIndex];
            materialRef.EnsureLoaded();
            Material? material = materialRef.Res;
            if (material == null)
                continue;
            AddMaterialUsage(
                shaderRoots,
                material,
                source,
                CreateMeshVariantKeywords(material._localKeywords, mesh, supportsBlendShapes));
        }
    }

    private static Material? ResolveMaterial(AssetRef<Material> materialRef)
    {
        materialRef.EnsureLoaded();
        return materialRef.Res;
    }

    private static Mesh? ResolveMesh(AssetRef<Mesh> meshRef)
    {
        meshRef.EnsureLoaded();
        return meshRef.Res;
    }

    private static void AddMaterialOrBuiltInUsage(
        Dictionary<Guid, RootBuilder> shaderRoots,
        Material? material,
        DefaultShader fallback,
        string source,
        IReadOnlyDictionary<string, bool> keywords) =>
        AddMaterialOrBuiltInUsage(
            shaderRoots,
            material,
            Shader.LoadDefault(fallback),
            BuiltInAssets.GuidFor(fallback),
            source,
            keywords);

    private static void AddMaterialOrBuiltInUsage(
        Dictionary<Guid, RootBuilder> shaderRoots,
        Material? material,
        Shader? fallbackShader,
        Guid fallbackGuid,
        string source,
        IReadOnlyDictionary<string, bool> keywords)
    {
        if (material != null)
        {
            AddMaterialUsage(shaderRoots, material, source, keywords);
            return;
        }

        if (fallbackShader == null || fallbackGuid == Guid.Empty)
            return;

        AddShaderUsage(
            shaderRoots,
            fallbackGuid,
            fallbackShader,
            source + ":default",
            keywords);
    }

    private static void AddMaterialUsage(
        Dictionary<Guid, RootBuilder> shaderRoots,
        Material material,
        string source,
        IReadOnlyDictionary<string, bool>? keywords)
    {
        AssetRef<Shader> shaderRef = material.ShaderRef;
        shaderRef.EnsureLoaded();
        Shader? materialShader = shaderRef.Res;
        if (materialShader == null)
            return;
        Guid shaderGuid = shaderRef.AssetID;
        if (shaderGuid == Guid.Empty)
            shaderGuid = materialShader.AssetID;
        if (shaderGuid == Guid.Empty)
            return;
        AddShaderUsage(shaderRoots, shaderGuid, materialShader, source, keywords);
    }

    private static void AddShaderUsage(
        Dictionary<Guid, RootBuilder> shaderRoots,
        Guid shaderGuid,
        Shader shader,
        string source,
        IReadOnlyDictionary<string, bool>? keywords)
    {
        if (!shaderRoots.TryGetValue(shaderGuid, out RootBuilder? root))
        {
            root = new RootBuilder(shader);
            shaderRoots.Add(shaderGuid, root);
        }
        root.Usages.Add(new VulkanShaderVariantUsage(source, keywords));
    }

    private readonly record struct DescriptorIdentity(Guid ShaderAssetGuid, string PassStableId, string CanonicalKeywordSet);

    private sealed class MutableDescriptor(
        Guid shaderAssetGuid,
        string passName,
        string passStableId,
        string canonicalKeywordSet,
        string compilerIdentity,
        string targetProfile)
    {
        internal Guid ShaderAssetGuid { get; } = shaderAssetGuid;
        internal string PassName { get; } = passName;
        internal string PassStableId { get; } = passStableId;
        internal string CanonicalKeywordSet { get; } = canonicalKeywordSet;
        internal string CompilerIdentity { get; } = compilerIdentity;
        internal string TargetProfile { get; } = targetProfile;
        internal SortedSet<string> Reasons { get; } = new(StringComparer.Ordinal);
    }

    private sealed class RootBuilder(Shader shader, IReadOnlyList<VulkanShaderVariantUsage>? usages = null)
    {
        internal Shader Shader { get; } = shader;
        internal List<VulkanShaderVariantUsage> Usages { get; } = usages == null
            ? []
            : new List<VulkanShaderVariantUsage>(usages);
    }
}
