// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;

namespace XEngine.Editor.Importers;

/// <summary>
/// Promotes a generated sub-asset to an independent GUID and records an importer remap so later
/// reimports skip that sub-asset and depend on the extracted asset instead.
/// </summary>
public static class ExtractGeneratedAssets
{
    public const string RemapKey = "extractRemap";

    public static Guid Extract(ImportContext ctx, string stableId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        EchoObject settings = ctx.Settings
            ?? throw new InvalidOperationException("Extract requires importer settings on the import context.");

        Guid extracted = AssetEntry.DeriveSubAssetGuid(ctx.AssetGuid, stableId);
        EchoObject remap = GetOrCreateRemap(settings);
        remap[stableId] = new EchoObject(extracted.ToString("N"));
        return extracted;
    }

    public static bool TryGetExternal(EchoObject settings, string stableId, out Guid mapped)
    {
        ArgumentNullException.ThrowIfNull(settings);
        mapped = default;
        if (!settings.TryGet(RemapKey, out EchoObject? remap) || remap is null)
            return false;
        if (!remap.TryGet(stableId, out EchoObject? tag) || tag is null)
            return false;
        return Guid.TryParse(tag.StringValue, out mapped);
    }

    /// <summary>
    /// If <paramref name="stableId"/> was extracted, records a dependency on the external GUID and
    /// returns true so the importer skips <see cref="ImportContext.AddSubAsset"/>.
    /// </summary>
    public static bool TrySkipExtracted(ImportContext ctx, string stableId)
    {
        if (ctx.Settings is null || !TryGetExternal(ctx.Settings, stableId, out Guid mapped))
            return false;
        ctx.AddDependency(mapped);
        return true;
    }

    private static EchoObject GetOrCreateRemap(EchoObject settings)
    {
        if (settings.TryGet(RemapKey, out EchoObject? existing) && existing is not null)
            return existing;

        EchoObject remap = EchoObject.NewCompound();
        settings[RemapKey] = remap;
        return remap;
    }
}
