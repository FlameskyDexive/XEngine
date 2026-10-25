// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Editor.Build;

/// <summary>
/// Supplies shader variants that are created by runtime code rather than serialized materials, for
/// any Vulkan target (Android, HarmonyOS, ...). Implementations must be deterministic and must not
/// enumerate keyword combinations.
/// </summary>
public interface IVulkanShaderVariantContributor
{
    string Id { get; }

    void Contribute(VulkanShaderVariantContributorContext context);
}

/// <summary>
/// Explicit build-time contribution sink for dynamic Vulkan shader variants.
/// </summary>
public sealed class VulkanShaderVariantContributorContext
{
    private readonly Action<Guid, string, IReadOnlyDictionary<string, bool>?, string?> _add;

    internal VulkanShaderVariantContributorContext(
        string contributorId,
        Action<Guid, string, IReadOnlyDictionary<string, bool>?, string?> add)
    {
        ContributorId = contributorId;
        _add = add;
    }

    public string ContributorId { get; }

    /// <summary>
    /// Adds one explicit shader/pass/keyword usage to the Vulkan variant collection.
    /// </summary>
    public void Add(
        Guid shaderAssetGuid,
        string source,
        IReadOnlyDictionary<string, bool>? keywords = null,
        string? passStableId = null) =>
        _add(shaderAssetGuid, source, keywords, passStableId);
}
