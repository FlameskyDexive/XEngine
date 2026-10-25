using System;
using System.Collections.Generic;
using XEngine.Animation;
using XEngine.Animation.Humanoid;
using XEngine.Playables;

namespace XEngine.Runtime;

public partial class Animator
{
    private AssetBackendBase? _assetChangeBackend;
    private readonly object _graphAssetOwner = new();
    private readonly HashSet<Guid> _graphAssetLocks = new();

    private void PinGraphAsset(Guid id)
    {
        Guid family = AssetDatabase.LockToOwner(id, _graphAssetOwner);
        if (family != Guid.Empty) _graphAssetLocks.Add(family);
    }

    private void PinTreeAssets(Playable node)
    {
        if (node is AnimationClipPlayable clip)
        {
            PinGraphAsset(clip.Clip?.AssetID ?? Guid.Empty);
            PinGraphAsset(clip.SamplingAvatarAssetId);
        }
        for (int i = 0; i < node.InputCount; i++) PinTreeAssets(node.GetInput(i));
    }

    private void SubscribeAssetPublication()
    {
        UnsubscribeAssetPublication();
        _assetChangeBackend = AssetDatabase.Current;
        if (_assetChangeBackend != null) _assetChangeBackend.AssetsPublished += RefreshPublishedAssets;
    }

    private void UnsubscribeAssetPublication()
    {
        if (_assetChangeBackend != null) _assetChangeBackend.AssetsPublished -= RefreshPublishedAssets;
        _assetChangeBackend = null;
        foreach (Guid family in _graphAssetLocks) AssetDatabase.ReleaseOwnerLock(family, _graphAssetOwner);
        _graphAssetLocks.Clear();
    }

    // Cold publication boundary: no directory IO, loading or per-frame scans. Updating leaves
    // in place also preserves controller references, fade internals, clones and nested mixers.
    private void RefreshPublishedAssets(IReadOnlyDictionary<Guid, EngineObject?> changes)
    {
        if (_binding == null || IsDisposed) return;
        bool avatarChanged = Avatar.AssetID != Guid.Empty && changes.ContainsKey(Avatar.AssetID);
        Avatar? avatar = avatarChanged ? changes[Avatar.AssetID] as Avatar : null;
        var visited = new HashSet<Playable>();
        bool changed = RefreshTree(_graph.Root, changes, avatar, avatarChanged, visited);
        foreach (AnimationPlayable cached in _transitionStates.Values)
            changed |= RefreshTree(cached, changes, avatar, avatarChanged, visited);
        if (_layerRuntimes != null)
            for (int i = 0; i < _layerRuntimes.Length; i++)
                _layerRuntimes[i].RefreshPublishedMotions(changes);
        else
            _runtime?.RefreshPublishedMotions(changes);
        if (!changed) return;

        _states.Clear();
        _weightlessClones.Clear();
        if (_layer != null)
            for (int i = 0; i < _layer.InputCount; i++)
            {
                if (_layer.GetInput(i) is not AnimationClipPlayable { Clip: { } clip } state) continue;
                if (state.IsWeightlessClone)
                {
                    if (!_weightlessClones.TryGetValue(clip, out var clones))
                        _weightlessClones[clip] = clones = new List<AnimationClipPlayable>(MaxCloneCount);
                    clones.Add(state);
                }
                else if (state.IsCodeClipState) _states[clip] = state;
            }
        CurrentClip = _current?.Clip;
        IsPlaying = _current?.Clip != null && _current.IsPlaying;
        ResetRootBindSnapshot();
    }

    private bool RefreshTree(Playable? node, IReadOnlyDictionary<Guid, EngineObject?> changes,
        Avatar? avatar, bool avatarChanged, HashSet<Playable> visited)
    {
        if (node == null || !visited.Add(node)) return false;
        bool changed = node is AnimationClipPlayable clip && clip.RefreshImportedAssets(changes, avatar, avatarChanged);
        if (node is AnimationClipPlayable state)
        {
            PinGraphAsset(state.Clip?.AssetID ?? Guid.Empty);
            PinGraphAsset(state.SamplingAvatarAssetId);
        }
        for (int i = 0; i < node.InputCount; i++)
            changed |= RefreshTree(node.GetInput(i), changes, avatar, avatarChanged, visited);
        return changed;
    }

    public override void OnDispose()
    {
        // Scene-less preview owners may never pass MonoBehaviour's enabled lifecycle gate.
        OnDisable();
        base.OnDispose();
    }
}
