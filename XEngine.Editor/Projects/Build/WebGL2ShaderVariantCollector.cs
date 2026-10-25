// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.RHI;

namespace XEngine.Editor.Build;

/// <summary>
/// Web / mini-game facade over the shared variant collector. Discovery is identical to Vulkan;
/// descriptors stamp <see cref="GraphicsBackend.WebGL2"/> and the resolved GLSL ES profile.
/// Default ShaderImporter variants stay Vulkan+D3D12.
/// </summary>
internal static class WebGL2ShaderVariantCollector
{
    internal const string TargetEnvironment = VulkanShaderVariantCollector.WebGL2TargetEnvironment;

    internal static VulkanShaderVariantCollection Collect(
        IEnumerable<VulkanShaderVariantRoot> roots,
        ShaderVariantStripper? stripper = null) =>
        VulkanShaderVariantCollector.Collect(roots, stripper, GraphicsBackend.WebGL2);

    internal static IReadOnlyList<ShaderArtifactBuildSelection> CreateArtifactSelections(
        VulkanShaderVariantCollection collection) =>
        VulkanShaderVariantCollector.CreateArtifactSelections(collection);

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
            GraphicsBackend.WebGL2);
}
