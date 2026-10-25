// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

namespace XEngine.Editor.Build;

/// <summary>
/// iOS / Metal facade over the shared variant collector. Discovery (materials, mesh layout,
/// pipeline keywords) is identical to Vulkan; descriptors stamp <see cref="GraphicsBackend.Metal"/>
/// and the Slang <c>metal</c> profile. Default ShaderImporter variants stay Vulkan+D3D12.
/// </summary>
internal static class MetalShaderVariantCollector
{
    internal const string TargetEnvironment = VulkanShaderVariantCollector.MetalTargetEnvironment;

    internal static VulkanShaderVariantCollection Collect(
        IEnumerable<VulkanShaderVariantRoot> roots,
        ShaderVariantStripper? stripper = null) =>
        VulkanShaderVariantCollector.Collect(roots, stripper, GraphicsBackend.Metal);

    internal static IReadOnlyList<ShaderArtifactBuildSelection> CreateArtifactSelections(
        VulkanShaderVariantCollection collection,
        ShaderTargetEnvironment targetEnvironment = ShaderTargetEnvironment.Default) =>
        VulkanShaderVariantCollector.CreateArtifactSelections(collection, targetEnvironment);

    internal static VulkanShaderVariantCollection CollectForBuild(
        IEnumerable<Guid> collectedAssets,
        IEnumerable<IVulkanShaderVariantContributor>? contributors = null,
        IEnumerable<ShaderVariantTraceReplay>? traceReplay = null,
        bool includeBuiltInShaders = true,
        ShaderVariantStripper? stripper = null) =>
        VulkanShaderVariantCollector.CollectForBuild(
            collectedAssets,
            contributors,
            traceReplay,
            includeBuiltInShaders,
            stripper,
            GraphicsBackend.Metal);
}
