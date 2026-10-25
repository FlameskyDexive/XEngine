using System;
using System.Collections.Generic;
using System.IO;

using Spine;

using XEngine.Echo;
using XEngine.Editor;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Spine;

namespace XEngine.Spine.Editor;

/// <summary>
/// Claims <c>.skel</c> always, <c>.bytes</c> when the file is <c>*.skel.bytes</c>,
/// and <c>.json</c> only when the contents sniff as Spine skeleton JSON.
/// </summary>
[ImporterFor(".skel", ".bytes", ".json", Priority = 100)]
public sealed class SpineSkeletonImporter : AssetImporter
{
    public const string ScaleSetting = "scale";
    public const string DefaultMixSetting = "defaultMix";
    public const string CustomMixesSetting = "customMixes";
    public const string MixFromKey = "from";
    public const string MixToKey = "to";
    public const string MixDurationKey = "duration";

    public override int Version => 2;

    public override EchoObject? DefaultSettings()
    {
        var settings = EchoObject.NewCompound();
        settings[ScaleSetting] = new EchoObject(SkeletonDataAsset.DefaultScale);
        settings[DefaultMixSetting] = new EchoObject(0f);
        return settings;
    }

    public override bool CanImport(string absolutePath)
    {
        string ext = Path.GetExtension(absolutePath);
        if (ext.Equals(".skel", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ext.Equals(".bytes", StringComparison.OrdinalIgnoreCase))
            return SpineAssetPaths.IsSkelBytesFileName(absolutePath);
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return SpineAssetPaths.FileLooksLikeSpineJson(absolutePath);
        return false;
    }

    public override bool Import(ImportContext ctx)
    {
        if (!CanImport(ctx.AbsolutePath))
            return false;

        byte[] bytes = File.ReadAllBytes(ctx.AbsolutePath);
        bool isBinary = IsBinarySource(ctx.AbsolutePath);
        float scale = ReadScale(ctx.Settings);

        SkeletonData data;
        try
        {
            data = ReadSkeletonData(bytes, isBinary, scale);
        }
        catch (Exception)
        {
            return false;
        }

        var asset = new SkeletonDataAsset
        {
            Name = SpineAssetPaths.GetDisplayName(ctx.AbsolutePath),
            Scale = scale,
            IsBinary = isBinary,
            SkeletonBytes = bytes,
        };
        ReadMixSettings(ctx.Settings, asset);

        for (int i = 0; i < data.Animations.Count; i++)
            asset.AnimationNames.Add(data.Animations.Items[i].Name);
        for (int i = 0; i < data.Skins.Count; i++)
            asset.SkinNames.Add(data.Skins.Items[i].Name);
        for (int i = 0; i < data.Events.Count; i++)
            asset.EventNames.Add(data.Events.Items[i].Name);

        string? atlasPath = SpineAssetPaths.FindSiblingAtlas(ctx.AbsolutePath);
        if (atlasPath != null)
        {
            asset.AtlasFileNames.Add(Path.GetFileName(atlasPath));
            if (SpineAtlasImporter.TryAddMetaDependency(atlasPath, ctx, nameof(SpineAtlasAsset), out Guid atlasGuid))
                asset.AtlasAssets.Add(new AssetRef<SpineAtlasAsset>(atlasGuid));
        }

        ctx.SetMainAsset(asset);

        for (int i = 0; i < asset.AnimationNames.Count; i++)
        {
            string animationName = asset.AnimationNames[i];
            string stableId = "anim:" + animationName;
            if (ExtractGeneratedAssets.TrySkipExtracted(ctx, stableId))
                continue;

            ctx.AddSubAsset(stableId, animationName, new AnimationReferenceAsset
            {
                Name = animationName,
                AnimationName = animationName,
                SkeletonDataAsset = new AssetRef<SkeletonDataAsset>(ctx.AssetGuid),
            });
        }

        return true;
    }

    private static bool IsBinarySource(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".skel", StringComparison.OrdinalIgnoreCase)
            || SpineAssetPaths.IsSkelBytesFileName(path);
    }

    private static float ReadScale(EchoObject? settings)
    {
        if (settings != null && settings.TryGet(ScaleSetting, out EchoObject? tag))
            return tag.FloatValue;
        return SkeletonDataAsset.DefaultScale;
    }

    internal static void ReadMixSettings(EchoObject? settings, SkeletonDataAsset asset)
    {
        if (settings == null)
            return;

        if (settings.TryGet(DefaultMixSetting, out EchoObject? mixTag))
            asset.DefaultMix = mixTag.FloatValue;

        if (!settings.TryGet(CustomMixesSetting, out EchoObject? listTag))
            return;
        for (int i = 0; i < listTag.Count; i++)
        {
            EchoObject entry = listTag[i];
            if (!entry.TryGet(MixFromKey, out EchoObject? from) || !entry.TryGet(MixToKey, out EchoObject? to))
                continue;
            float duration = entry.TryGet(MixDurationKey, out EchoObject? dur) ? dur.FloatValue : 0f;
            asset.CustomMixes.Add(new SpineMixEntry
            {
                From = from.StringValue,
                To = to.StringValue,
                Duration = duration,
            });
        }
    }

    private static SkeletonData ReadSkeletonData(byte[] bytes, bool isBinary, float scale)
    {
        // Stub attachments (not null): 4.x rejects exports whose timelines reference attachments
        // outside the default skin when the loader returns null ("Timeline attachment not found").
        if (isBinary)
        {
            var binary = new SkeletonBinary(SpineStubAttachmentLoader.Instance) { Scale = scale };
            using var stream = new MemoryStream(bytes, writable: false);
            return binary.ReadSkeletonData(stream);
        }

        var json = new SkeletonJson(SpineStubAttachmentLoader.Instance) { Scale = scale };
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false));
        return json.ReadSkeletonData(reader);
    }
}
