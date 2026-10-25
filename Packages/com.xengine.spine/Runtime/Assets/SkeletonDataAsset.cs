using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Spine;

using XEngine.Runtime;

namespace XEngine.Spine;

/// <summary>Imported Spine skeleton (<c>.json</c> / <c>.skel</c> / <c>.skel.bytes</c>).</summary>
public sealed class SkeletonDataAsset : ScriptableObject
{
    public const float DefaultScale = 0.01f;

    public float Scale = DefaultScale;
    public bool IsBinary;
    public byte[] SkeletonBytes = [];
    public List<string> AnimationNames = [];
    public List<string> SkinNames = [];
    public List<string> EventNames = [];
    public List<string> AtlasFileNames = [];
    public List<AssetRef<SpineAtlasAsset>> AtlasAssets = [];

    /// <summary>Crossfade duration used when no per-pair mix is defined (spine-unity's Default Mix Duration).</summary>
    public float DefaultMix;
    /// <summary>Per-animation-pair crossfade durations (spine-unity's Custom Mix Durations).</summary>
    public List<SpineMixEntry> CustomMixes = [];

    [NonSerialized] private SkeletonData? _cachedData;
    [NonSerialized] private AnimationStateData? _cachedStateData;

    public SkeletonData GetSkeletonData()
    {
        if (_cachedData != null)
            return _cachedData;

        AttachmentLoader loader = CreateAttachmentLoader();
        if (IsBinary)
        {
            var binary = new SkeletonBinary(loader) { Scale = Scale };
            using var stream = new MemoryStream(SkeletonBytes, writable: false);
            _cachedData = binary.ReadSkeletonData(stream);
        }
        else
        {
            var json = new SkeletonJson(loader) { Scale = Scale };
            using var reader = new StreamReader(new MemoryStream(SkeletonBytes, writable: false), Encoding.UTF8);
            _cachedData = json.ReadSkeletonData(reader);
        }

        return _cachedData;
    }

    public AnimationStateData GetAnimationStateData()
    {
        if (_cachedStateData != null)
            return _cachedStateData;

        SkeletonData data = GetSkeletonData();
        var stateData = new AnimationStateData(data) { DefaultMix = DefaultMix };
        for (int i = 0; i < CustomMixes.Count; i++)
        {
            SpineMixEntry mix = CustomMixes[i];
            // Skip pairs whose animations no longer exist (SetMix throws on unknown names).
            if (data.FindAnimation(mix.From) == null || data.FindAnimation(mix.To) == null)
                continue;
            stateData.SetMix(mix.From, mix.To, mix.Duration);
        }

        return _cachedStateData = stateData;
    }

    public void ClearCache()
    {
        _cachedData = null;
        _cachedStateData = null;
    }

    /// <summary>Drop only the cached <see cref="AnimationStateData"/> (mix settings changed; skeleton parse still valid).</summary>
    public void ClearStateCache() => _cachedStateData = null;

    /// <summary>Build a skeleton asset from export bytes and an atlas (runtime / tests).</summary>
    public static SkeletonDataAsset CreateRuntime(
        byte[] skeletonBytes,
        SpineAtlasAsset? atlas,
        float scale = DefaultScale,
        bool isBinary = false)
    {
        var asset = ScriptableObject.CreateInstance<SkeletonDataAsset>();
        asset.Scale = scale;
        asset.IsBinary = isBinary;
        asset.SkeletonBytes = skeletonBytes ?? [];
        if (atlas != null)
            asset.AtlasAssets.Add(atlas);

        SkeletonData data = asset.GetSkeletonData();
        ExposedList<global::Spine.Animation> animations = data.Animations;
        for (int i = 0; i < animations.Count; i++)
            asset.AnimationNames.Add(animations.Items[i].Name);
        ExposedList<Skin> skins = data.Skins;
        for (int i = 0; i < skins.Count; i++)
            asset.SkinNames.Add(skins.Items[i].Name);
        return asset;
    }

    private AttachmentLoader CreateAttachmentLoader()
    {
        // Stub attachments (not null) when no atlas is resident: parse must still succeed for
        // exports whose timelines reference attachments outside the default skin (4.x rejects
        // null-loader parses with "Timeline attachment not found").
        if (AtlasAssets.Count == 0)
            return SpineStubAttachmentLoader.Instance;

        var atlases = new List<Atlas>(AtlasAssets.Count);
        for (int i = 0; i < AtlasAssets.Count; i++)
        {
            AssetRef<SpineAtlasAsset> atlasRef = AtlasAssets[i];
            // Skeleton data is parsed once and cached; a non-blocking Res here would parse with
            // texture-less stub attachments on the first attempt and cache white silhouettes
            // forever (the empty-attachment retry guard can't catch stubs). Resolve through
            // AssetDatabase.Get so deserialization (and the page textures' GPU upload) happens
            // synchronously on the main thread rather than the background loader thread.
            SpineAtlasAsset? atlasAsset = AssetDatabase.Get(atlasRef.AssetID) as SpineAtlasAsset;
            if (atlasAsset == null || string.IsNullOrEmpty(atlasAsset.AtlasText))
                continue;
            atlases.Add(atlasAsset.CreateAtlas(new SpineRuntimeTextureLoader(atlasAsset)));
        }

        return atlases.Count == 0
            ? SpineStubAttachmentLoader.Instance
            : new AtlasAttachmentLoader(atlases.ToArray());
    }
}
