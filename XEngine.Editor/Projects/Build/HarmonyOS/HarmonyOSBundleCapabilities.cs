// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>Capabilities that a provider Bundle may expose to the XEngine packaging pipeline.</summary>
internal enum HarmonyOSBundleCapability
{
    PlayerPublish,
    StageProject,
    HapBuild,
    HapVerify,
    AppBuild,
    HdcDeploy,
}

/// <summary>Validates and matches the capability names signed by a provider Bundle manifest.</summary>
internal static class HarmonyOSBundleCapabilities
{
    internal static IReadOnlyList<string> ValidateDeclared(IReadOnlyList<string>? declared)
    {
        if (declared == null || declared.Count == 0)
            return Array.Empty<string>();

        var result = new List<string>(declared.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < declared.Count; i++)
        {
            string? name = declared[i];
            if (string.IsNullOrWhiteSpace(name) || !IsKnownName(name) || !seen.Add(name))
            {
                throw new HarmonyOSBundleException(
                    HarmonyOSBundleErrorCode.ManifestInvalid,
                    "The OpenHarmony SDK Bundle manifest capability list contains an unknown or duplicate entry.");
            }

            result.Add(name);
        }

        return result.AsReadOnly();
    }

    internal static void Require(
        IReadOnlyList<string> declared,
        IReadOnlyList<HarmonyOSBundleCapability>? required)
    {
        ArgumentNullException.ThrowIfNull(declared);
        if (required == null || required.Count == 0)
            return;

        var actual = new HashSet<string>(declared, StringComparer.Ordinal);
        for (int i = 0; i < required.Count; i++)
        {
            HarmonyOSBundleCapability capability = required[i];
            string name = ToManifestName(capability);
            if (!actual.Contains(name))
            {
                throw new HarmonyOSBundleException(
                    HarmonyOSBundleErrorCode.BundleCapabilityMissing,
                    $"The locked HarmonyOS SDK Bundle does not declare required capability '{name}'.");
            }
        }
    }

    internal static string ToManifestName(HarmonyOSBundleCapability capability) => capability switch
    {
        HarmonyOSBundleCapability.PlayerPublish => "playerPublish",
        HarmonyOSBundleCapability.StageProject => "stageProject",
        HarmonyOSBundleCapability.HapBuild => "hapBuild",
        HarmonyOSBundleCapability.HapVerify => "hapVerify",
        HarmonyOSBundleCapability.AppBuild => "appBuild",
        HarmonyOSBundleCapability.HdcDeploy => "hdcDeploy",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unknown HarmonyOS Bundle capability."),
    };

    private static bool IsKnownName(string name) => name switch
    {
        "playerPublish" or
        "stageProject" or
        "hapBuild" or
        "hapVerify" or
        "appBuild" or
        "hdcDeploy" => true,
        _ => false,
    };

}
