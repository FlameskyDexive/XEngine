// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Editor.AssetBundles.PlatformVariants;
using XEngine.Editor.Projects.Settings;
using XEngine.Echo;

namespace XEngine.Editor.Build.Ios;

/// <summary>
/// Copies a prepared MetalLib catalog into the generated player <c>Content/Shaders</c>
/// and fail-closes ASTC / metallib when this host cannot produce them.
/// </summary>
internal static class IosAssetPackager
{
    internal const string AssetLayoutFileName = "ios-asset-layout.json";

    internal static void WriteContentLayout(string playerDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerDirectory);
        string content = Path.Combine(playerDirectory, "Content");
        string shaders = Path.Combine(content, "Shaders");
        Directory.CreateDirectory(shaders);
        File.WriteAllText(
            Path.Combine(content, AssetLayoutFileName),
            """
            {
              "schemaVersion": 1,
              "contentMount": "Assets",
              "documentsMount": "Files",
              "cachesMount": "Cache",
              "tmpMount": "Tmp",
              "shaderCatalog": "Shaders/catalog.xshadercatalog",
              "textureFormat": "imported-cache",
              "failClosed": ["missing-assets", "vulkan-spirv"]
            }
            """);
    }

    internal static void CopyMetalShaderPackage(string preparedPackageDirectory, string contentShadersDirectory)
    {
        IosShaderCatalogContract.RequirePreparedMetalPackage(preparedPackageDirectory);
        ShaderArtifactBuildPackager.CopyPreparedPackage(preparedPackageDirectory, contentShadersDirectory);
    }

    internal static IosTextureVariantSelection ValidateTexturePlan(
        TexturePlatformSettings? settings,
        bool encoderAvailable)
        => IosTextureVariantResolver.ResolveForBuild(
            settings,
            IosTextureCapabilities.AppleGpu,
            encoderAvailable);

    internal static HashSet<Guid> CollectSettingsAssetGuids(IEnumerable<EchoObject> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var assetGuids = new HashSet<Guid>();
        var pending = new Stack<EchoObject>();
        foreach (EchoObject setting in settings)
        {
            ArgumentNullException.ThrowIfNull(setting);
            pending.Push(setting);
        }

        while (pending.Count > 0)
        {
            EchoObject value = pending.Pop();
            if (value.TagType == EchoType.Compound)
            {
                if (value.TryGet("AssetID", out EchoObject? assetId) &&
                    assetId!.TagType == EchoType.String &&
                    Guid.TryParse(assetId.StringValue, out Guid guid) &&
                    guid != Guid.Empty)
                {
                    assetGuids.Add(guid);
                }

                foreach (KeyValuePair<string, EchoObject> child in value.Tags)
                    pending.Push(child.Value);
            }
            else if (value.TagType == EchoType.List)
            {
                for (int i = 0; i < value.List.Count; i++)
                    pending.Push(value.List[i]);
            }
        }

        return assetGuids;
    }
}
