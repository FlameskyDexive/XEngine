using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Spine;

using XEngine.Runtime;

namespace XEngine.Spine38;

/// <summary>Imported Spine 3.8 skeleton (<c>.json</c> / <c>.skel</c> / <c>.skel.bytes</c>).</summary>
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
    public float DefaultMix;
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

    public void ClearStateCache() => _cachedStateData = null;

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
        if (AtlasAssets.Count == 0)
            return SpineStubAttachmentLoader.Instance;

        var atlases = new List<Atlas>(AtlasAssets.Count);
        for (int i = 0; i < AtlasAssets.Count; i++)
        {
            AssetRef<SpineAtlasAsset> atlasRef = AtlasAssets[i];
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
