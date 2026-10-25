using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

using ModelContextProtocol.Server;

using Spine;

using XEngine.Ai.Server;
using XEngine.Editor.Core;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Spine38;

namespace XEngine.Spine38.Editor;

/// <summary>
/// In-process MCP tools for driving live Spine 3.8 skeletons. Same tool names as 4.x
/// (<c>runtime_spine_*</c>) because the two packages cannot be installed together.
/// </summary>
[McpServerToolType]
public static class Spine38RuntimeTools
{
    [McpServerTool(Name = "runtime_spine_state", UseStructuredContent = true)]
    [Description("Live Spine 3.8 skeleton snapshot: tracks, time scale, animations and skins. identifier is a GameObject Guid; omit to use the current selection.")]
    public static Task<SpineLiveStateResult> SpineState(
        MainThreadDispatcher dispatcher,
        [Description("Optional GameObject Identifier (Guid). When omitted, uses the active selection.")] string? identifier = null)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            SkeletonAnimation? anim = FindSkeletonAnimation(identifier, out string? err, out string? goName);
            if (anim == null)
                return new SpineLiveStateResult(false, goName, null, null, 0f, null, null, null, err);

            SkeletonDataAsset? asset = anim.SkeletonDataAsset.ResWeak;
            List<string> animations = asset != null ? [.. asset.AnimationNames] : [];
            List<string> skins = asset != null ? [.. asset.SkinNames] : [];
            Skeleton? skeleton = anim.Skeleton;
            if (skeleton != null)
            {
                ExposedList<global::Spine.Animation> anims = skeleton.Data.Animations;
                for (int i = 0; i < anims.Count; i++)
                    animations.Add(anims.Items[i].Name);
                ExposedList<Skin> skinList = skeleton.Data.Skins;
                for (int i = 0; i < skinList.Count; i++)
                    skins.Add(skinList.Items[i].Name);
            }

            List<SpineLiveTrack> tracks = [];
            AnimationState? state = anim.AnimationState;
            if (state != null)
            {
                for (int i = 0; i < state.Tracks.Count; i++)
                {
                    TrackEntry? entry = state.Tracks.Items[i];
                    if (entry?.Animation == null) continue;
                    tracks.Add(new SpineLiveTrack(i, entry.Animation.Name, entry.Loop, entry.TrackTime, entry.Animation.Duration));
                }
            }

            return new SpineLiveStateResult(
                true, goName, asset?.Name, asset?.AssetID.ToString(),
                anim.TimeScale, tracks, animations, skins, null);
        });

    [McpServerTool(Name = "runtime_spine_play", UseStructuredContent = true)]
    [Description("Play (SetAnimation) a Spine 3.8 animation on a live SkeletonAnimation.")]
    public static Task<SpineLivePlayResult> SpinePlay(
        MainThreadDispatcher dispatcher,
        [Description("Animation name, e.g. 'walk', 'jump'.")] string animation,
        [Description("Loop the animation.")] bool loop = true,
        [Description("Track index (default 0).")] int track = 0,
        [Description("Optional time scale override (<=0 to keep current).")] float timeScale = 0f,
        [Description("Optional GameObject Identifier (Guid). When omitted, uses the active selection.")] string? identifier = null)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            SkeletonAnimation? anim = FindSkeletonAnimation(identifier, out string? err, out string? goName);
            if (anim == null)
                return new SpineLivePlayResult(false, goName, animation, track, loop, err);

            try
            {
                if (timeScale > 0f)
                    anim.TimeScale = timeScale;
                anim.Initialize(overwrite: false);
                TrackEntry? entry = anim.SetAnimation(track, animation, loop);
                if (entry == null)
                    return new SpineLivePlayResult(false, goName, animation, track, loop,
                        $"Animation '{animation}' not found on '{goName}'.");
                return new SpineLivePlayResult(true, goName, animation, track, loop, null);
            }
            catch (Exception ex)
            {
                return new SpineLivePlayResult(false, goName, animation, track, loop, ex.Message);
            }
        });

    [McpServerTool(Name = "runtime_spine_queue", UseStructuredContent = true)]
    [Description("Queue (AddAnimation) a Spine 3.8 animation after the current one on a track.")]
    public static Task<SpineLivePlayResult> SpineQueue(
        MainThreadDispatcher dispatcher,
        [Description("Animation name to queue next.")] string animation,
        [Description("Loop the queued animation.")] bool loop = true,
        [Description("Track index (default 0).")] int track = 0,
        [Description("Delay before the queued entry starts (seconds; 0 = after current ends).")] float delay = 0f,
        [Description("Optional GameObject Identifier (Guid). When omitted, uses the active selection.")] string? identifier = null)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            SkeletonAnimation? anim = FindSkeletonAnimation(identifier, out string? err, out string? goName);
            if (anim == null)
                return new SpineLivePlayResult(false, goName, animation, track, loop, err);

            try
            {
                anim.Initialize(overwrite: false);
                TrackEntry? entry = anim.AddAnimation(track, animation, loop, delay);
                if (entry == null)
                    return new SpineLivePlayResult(false, goName, animation, track, loop,
                        $"Animation '{animation}' not found on '{goName}'.");
                return new SpineLivePlayResult(true, goName, animation, track, loop, null);
            }
            catch (Exception ex)
            {
                return new SpineLivePlayResult(false, goName, animation, track, loop, ex.Message);
            }
        });

    [McpServerTool(Name = "runtime_spine_set_skin", UseStructuredContent = true)]
    [Description("Set the skin on a live Spine 3.8 skeleton.")]
    public static Task<SpineLiveSkinResult> SpineSetSkin(
        MainThreadDispatcher dispatcher,
        [Description("Skin name.")] string skin,
        [Description("Optional GameObject Identifier (Guid). When omitted, uses the active selection.")] string? identifier = null)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            SkeletonAnimation? anim = FindSkeletonAnimation(identifier, out string? err, out string? goName);
            if (anim == null)
                return new SpineLiveSkinResult(false, goName, skin, err);

            try
            {
                Skeleton? skeleton = anim.Skeleton;
                if (skeleton == null)
                    return new SpineLiveSkinResult(false, goName, skin, "Skeleton not initialized (missing SkeletonDataAsset?).");
                Skin? found = skeleton.Data.FindSkin(skin);
                if (found == null)
                    return new SpineLiveSkinResult(false, goName, skin, $"Skin '{skin}' not found.");
                skeleton.SetSkin(found);
                skeleton.SetSlotsToSetupPose();
                return new SpineLiveSkinResult(true, goName, skin, null);
            }
            catch (Exception ex)
            {
                return new SpineLiveSkinResult(false, goName, skin, ex.Message);
            }
        });

    static SkeletonAnimation? FindSkeletonAnimation(string? identifier, out string? error, out string? goName)
    {
        goName = null;
        GameObject? go = null;
        if (!string.IsNullOrEmpty(identifier))
        {
            if (!Guid.TryParse(identifier, out Guid guid))
            {
                error = $"Invalid identifier: {identifier}";
                return null;
            }

            go = Scene.Current?.FindObjectByIdentifier<GameObject>(guid);
            if (go is null)
            {
                error = $"GameObject '{identifier}' not found in the current scene.";
                return null;
            }
        }
        else if (Selection.ActiveObject is GameObject selected)
            go = selected;

        if (go is null)
        {
            error = "No GameObject: pass identifier or select one in the scene.";
            return null;
        }

        goName = go.Name;
        SkeletonAnimation? anim = go.GetComponent<SkeletonAnimation>();
        if (anim == null)
        {
            error = $"GameObject '{go.Name}' has no SkeletonAnimation.";
            return null;
        }

        error = null;
        return anim;
    }

    public sealed record SpineLiveTrack(int Track, string Animation, bool Loop, float Time, float Duration);

    public sealed record SpineLiveStateResult(
        bool Succeeded, string? ObjectName, string? SkeletonName, string? AssetGuid,
        float TimeScale, List<SpineLiveTrack>? Tracks, List<string>? Animations, List<string>? Skins, string? Error);

    public sealed record SpineLivePlayResult(
        bool Succeeded, string? ObjectName, string Animation, int Track, bool Loop, string? Error);

    public sealed record SpineLiveSkinResult(
        bool Succeeded, string? ObjectName, string Skin, string? Error);
}
