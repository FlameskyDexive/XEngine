// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using XEngine.Editor.Projects.Scripting;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;

namespace XEngine.Editor.Build.Android;

// Android-specific facade over the shared VulkanShaderVariantCollector. Both mobile platforms render
// through Vulkan/SPIR-V, so the scene/material/mesh/terrain/keyword discovery is identical; the
// Android record types are retained for source compatibility with the existing build pipeline and
// tests, and each method adapts its inputs to the neutral Vulkan collector and the results back.
internal sealed record AndroidShaderVariantUsage(
    string Source,
    IReadOnlyDictionary<string, bool>? Keywords = null,
    string? CanonicalKeywordSet = null,
    string? PassStableId = null);

internal sealed record AndroidShaderVariantRoot(
    Guid ShaderAssetGuid,
    Shader Shader,
    IReadOnlyList<AndroidShaderVariantUsage> Usages);

internal sealed record AndroidPipelineShaderVariantUsage(
    DefaultShader Shader,
    string Source,
    IReadOnlyDictionary<string, bool> Keywords);

internal sealed record AndroidShaderVariantDescriptor(
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

internal sealed class AndroidShaderVariantCollection
{
    internal const int SchemaVersion = 1;

    internal AndroidShaderVariantCollection(IReadOnlyList<AndroidShaderVariantDescriptor> descriptors)
    {
        Descriptors = descriptors;
    }

    internal IReadOnlyList<AndroidShaderVariantDescriptor> Descriptors { get; }

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

internal static class AndroidShaderVariantCollector
{
    internal static AndroidShaderVariantCollection Collect(
        IEnumerable<AndroidShaderVariantRoot> roots,
        ShaderVariantStripper? stripper = null) =>
        ToAndroid(VulkanShaderVariantCollector.Collect(roots.Select(ToVulkan), stripper));

    internal static IReadOnlyList<ShaderArtifactBuildSelection> CreateArtifactSelections(
        AndroidShaderVariantCollection collection) =>
        VulkanShaderVariantCollector.CreateArtifactSelections(ToVulkan(collection));

    /// <summary>
    /// Builds Android roots from the editor asset closure. Materials contribute their serialized
    /// keyword state, while build scenes additionally contribute the mesh-layout keywords that the
    /// render pipeline applies immediately before drawing. Built-in shaders are always included
    /// because runtime fallback and pipeline selection can request them without a serialized
    /// material edge. Optional Feature shaders (ScreenSpace Decal / screen-space shadows) are
    /// stripped unless the matching RendererFeature is enabled.
    /// </summary>
    internal static AndroidShaderVariantCollection CollectForBuild(
        IEnumerable<Guid> collectedAssets,
        AndroidShaderVariantTraceCollection? approvedTrace = null,
        bool includeBuiltInShaders = true,
        IEnumerable<IAndroidShaderVariantContributor>? contributors = null,
        ShaderVariantStripper? stripper = null)
    {
        ArgumentNullException.ThrowIfNull(collectedAssets);
        List<Guid> assets = collectedAssets is IReadOnlyCollection<Guid> readOnly
            ? new List<Guid>(readOnly.Count)
            : [];
        foreach (Guid asset in collectedAssets)
            assets.Add(asset);

        IEnumerable<IVulkanShaderVariantContributor>? vulkanContributors =
            contributors?.Select(ToVulkan);
        IEnumerable<ShaderVariantTraceReplay>? traceReplay =
            approvedTrace?.Entries.Select(ToTraceReplay);

        return ToAndroid(VulkanShaderVariantCollector.CollectForBuild(
            assets,
            vulkanContributors,
            traceReplay,
            includeBuiltInShaders,
            stripper));
    }

    internal static IReadOnlyList<IAndroidShaderVariantContributor> DiscoverContributors()
    {
        // Scan for IAndroidShaderVariantContributor directly. We deliberately do NOT delegate to
        // VulkanShaderVariantCollector.DiscoverContributors here: that scan instantiates every
        // IVulkanShaderVariantContributor via a public parameterless ctor, and ContributingAndroid
        // below is a wrapper (it takes a contributor argument) that would crash the reflective
        // instantiation. ContributingAndroid is only ever created on demand in ToVulkan/Add paths.
        var types = new List<Type>();
        var seen = new HashSet<Type>();
        foreach (Type type in ScriptAssemblyManager.GetAllTypes())
        {
            if (!typeof(IAndroidShaderVariantContributor).IsAssignableFrom(type) ||
                type.IsAbstract || type.IsInterface || type.ContainsGenericParameters ||
                !seen.Add(type))
            {
                continue;
            }
            types.Add(type);
        }

        types.Sort(static (left, right) => StringComparer.Ordinal.Compare(
            left.FullName ?? left.Name,
            right.FullName ?? right.Name));

        var contributors = new List<IAndroidShaderVariantContributor>(types.Count);
        foreach (Type type in types)
        {
            ConstructorInfo? constructor = type.GetConstructor(Type.EmptyTypes);
            if (constructor == null)
            {
                throw new InvalidDataException(
                    $"Android shader variant contributor '{type.FullName ?? type.Name}' requires a public parameterless constructor.");
            }

            IAndroidShaderVariantContributor contributor;
            try
            {
                contributor = (IAndroidShaderVariantContributor)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"Failed to instantiate Android shader variant contributor '{type.FullName ?? type.Name}'.", ex);
            }
            contributors.Add(contributor);
        }
        return contributors;
    }

    internal static IReadOnlyList<AndroidShaderVariantRoot> AddContributorUsages(
        IReadOnlyList<AndroidShaderVariantRoot> shaderRoots,
        IReadOnlySet<Guid> collectedAssets,
        IEnumerable<IAndroidShaderVariantContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(shaderRoots);
        ArgumentNullException.ThrowIfNull(collectedAssets);
        ArgumentNullException.ThrowIfNull(contributors);
        IReadOnlyList<VulkanShaderVariantRoot> vulkanRoots =
            VulkanShaderVariantCollector.AddContributorUsages(
                shaderRoots.Select(ToVulkan).ToList(),
                collectedAssets,
                contributors.Select(ToVulkan));
        return vulkanRoots.Select(ToAndroid).ToList();
    }

    internal static IReadOnlyList<AndroidShaderVariantRoot> AddApprovedTraceUsages(
        IReadOnlyList<AndroidShaderVariantRoot> shaderRoots,
        AndroidShaderVariantTraceCollection trace)
    {
        ArgumentNullException.ThrowIfNull(shaderRoots);
        ArgumentNullException.ThrowIfNull(trace);
        IReadOnlyList<VulkanShaderVariantRoot> vulkanRoots =
            VulkanShaderVariantCollector.AddTraceReplayUsages(
                shaderRoots.Select(ToVulkan).ToList(),
                trace.Entries.Select(ToTraceReplay));
        return vulkanRoots.Select(ToAndroid).ToList();
    }

    internal static IReadOnlyDictionary<string, bool> CreateMeshVariantKeywords(
        IReadOnlyDictionary<string, bool>? materialKeywords,
        Mesh mesh,
        bool supportsBlendShapes = true,
        bool isInstanced = false) =>
        VulkanShaderVariantCollector.CreateMeshVariantKeywords(materialKeywords, mesh, supportsBlendShapes, isInstanced);

    internal static IReadOnlyDictionary<string, bool> CreateTerrainVariantKeywords(
        IReadOnlyDictionary<string, bool>? materialKeywords,
        int layerCount,
        bool bicubic) =>
        VulkanShaderVariantCollector.CreateTerrainVariantKeywords(materialKeywords, layerCount, bicubic);

    internal static void CollectScenePipelineUsages(
        Scene scene,
        string scenePath,
        List<AndroidPipelineShaderVariantUsage> usages)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenePath);
        ArgumentNullException.ThrowIfNull(usages);

        var vulkanUsages = new List<VulkanPipelineShaderVariantUsage>(usages.Count);
        VulkanShaderVariantCollector.CollectScenePipelineUsages(scene, scenePath, vulkanUsages);
        for (int i = 0; i < vulkanUsages.Count; i++)
        {
            VulkanPipelineShaderVariantUsage usage = vulkanUsages[i];
            usages.Add(new AndroidPipelineShaderVariantUsage(usage.Shader, usage.Source, usage.Keywords));
        }
    }

    internal static IReadOnlyDictionary<string, bool> CreateTonemapperVariantKeywords(TonemapperType type) =>
        VulkanShaderVariantCollector.CreateTonemapperVariantKeywords(type);

    internal static IReadOnlyDictionary<string, bool> CreateBokehVariantKeywords(bool useAutoFocus) =>
        VulkanShaderVariantCollector.CreateBokehVariantKeywords(useAutoFocus);

    internal static void WriteReport(string path, AndroidShaderVariantCollection collection) =>
        VulkanShaderVariantCollector.WriteReport(path, ToVulkan(collection));

    // --- adapters ---------------------------------------------------------------------------

    private static VulkanShaderVariantRoot ToVulkan(AndroidShaderVariantRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var usages = new List<VulkanShaderVariantUsage>(root.Usages.Count);
        for (int i = 0; i < root.Usages.Count; i++)
        {
            AndroidShaderVariantUsage usage = root.Usages[i];
            usages.Add(new VulkanShaderVariantUsage(
                usage.Source,
                usage.Keywords,
                usage.CanonicalKeywordSet,
                usage.PassStableId));
        }

        return new VulkanShaderVariantRoot(root.ShaderAssetGuid, root.Shader, usages);
    }

    private static VulkanShaderVariantUsage ToVulkan(AndroidShaderVariantUsage usage) =>
        new(usage.Source, usage.Keywords, usage.CanonicalKeywordSet, usage.PassStableId);

    private static AndroidShaderVariantRoot ToAndroid(VulkanShaderVariantRoot root)
    {
        var usages = new List<AndroidShaderVariantUsage>(root.Usages.Count);
        for (int i = 0; i < root.Usages.Count; i++)
        {
            VulkanShaderVariantUsage usage = root.Usages[i];
            usages.Add(new AndroidShaderVariantUsage(
                usage.Source,
                usage.Keywords,
                usage.CanonicalKeywordSet,
                usage.PassStableId));
        }

        return new AndroidShaderVariantRoot(root.ShaderAssetGuid, root.Shader, usages);
    }

    private static AndroidShaderVariantUsage ToAndroid(VulkanShaderVariantUsage usage) =>
        new(usage.Source, usage.Keywords, usage.CanonicalKeywordSet, usage.PassStableId);

    private static VulkanShaderVariantCollection ToVulkan(AndroidShaderVariantCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var descriptors = new List<VulkanShaderVariantDescriptor>(collection.Descriptors.Count);
        for (int i = 0; i < collection.Descriptors.Count; i++)
        {
            AndroidShaderVariantDescriptor descriptor = collection.Descriptors[i];
            descriptors.Add(ToVulkan(descriptor));
        }
        return new VulkanShaderVariantCollection(descriptors);
    }

    private static AndroidShaderVariantCollection ToAndroid(VulkanShaderVariantCollection collection)
    {
        var descriptors = new List<AndroidShaderVariantDescriptor>(collection.Descriptors.Count);
        for (int i = 0; i < collection.Descriptors.Count; i++)
        {
            VulkanShaderVariantDescriptor descriptor = collection.Descriptors[i];
            descriptors.Add(ToAndroid(descriptor));
        }
        return new AndroidShaderVariantCollection(descriptors);
    }

    private static VulkanShaderVariantDescriptor ToVulkan(AndroidShaderVariantDescriptor descriptor) =>
        new(
            descriptor.ShaderAssetGuid,
            descriptor.PassName,
            descriptor.PassStableId,
            descriptor.Backend,
            descriptor.ShaderTier,
            descriptor.CanonicalKeywordSet,
            descriptor.CompilerIdentity,
            descriptor.TargetProfile,
            descriptor.TargetEnvironment,
            descriptor.Reasons);

    private static AndroidShaderVariantDescriptor ToAndroid(VulkanShaderVariantDescriptor descriptor) =>
        new(
            descriptor.ShaderAssetGuid,
            descriptor.PassName,
            descriptor.PassStableId,
            descriptor.Backend,
            descriptor.ShaderTier,
            descriptor.CanonicalKeywordSet,
            descriptor.CompilerIdentity,
            descriptor.TargetProfile,
            descriptor.TargetEnvironment,
            descriptor.Reasons);

    private static IVulkanShaderVariantContributor ToVulkan(IAndroidShaderVariantContributor contributor) =>
        new ContributingAndroid(contributor);

    private static ShaderVariantTraceReplay ToTraceReplay(AndroidShaderVariantTraceUsage entry) =>
        new(entry.ShaderName, entry.PassName, entry.CanonicalKeywordSet, entry.Source, entry.Found);

    /// <summary>
    /// Wraps an <see cref="IAndroidShaderVariantContributor"/> so it can flow through the shared
    /// Vulkan collector discovery/contribution pipeline.
    /// </summary>
    private sealed class ContributingAndroid(IAndroidShaderVariantContributor contributor)
        : IVulkanShaderVariantContributor
    {
        internal IAndroidShaderVariantContributor Contributor { get; } = contributor;

        public string Id => Contributor.Id;

        public void Contribute(VulkanShaderVariantContributorContext context)
        {
            Contributor.Contribute(new AndroidShaderVariantContributorContext(
                context.ContributorId,
                (shaderAssetGuid, source, keywords, passStableId) =>
                    context.Add(shaderAssetGuid, source, keywords, passStableId)));
        }
    }
}
