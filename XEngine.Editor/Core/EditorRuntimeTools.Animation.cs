// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

using ModelContextProtocol.Server;
using XEngine.Ai.Server;
using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI.Panels;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Core;

public static partial class EditorRuntimeTools
{
    [McpServerTool(Name = "runtime_animator_state", UseStructuredContent = true)]
    [Description("Live Animator snapshot: per-layer current state, normalized time, in-transition, parameter values. identifier is a GameObject Guid; omit to use the current selection.")]
    public static Task<AnimatorLiveStateResult> AnimatorState(
        MainThreadDispatcher dispatcher,
        [Description("Optional GameObject Identifier (Guid). When omitted, uses the active selection.")] string? identifier = null)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            Animator? animator = FindAnimator(identifier, out string? err, out string? goName);
            if (animator == null)
                return new AnimatorLiveStateResult(false, goName, null, Array.Empty<AnimatorLiveLayerState>(), Array.Empty<AnimatorLiveParameter>(), err);
            var layers = new List<AnimatorLiveLayerState>();
            int count = Math.Max(1, animator.LayerCount);
            AnimatorController? author = animator.AuthoringController;
            for (int i = 0; i < count; i++)
            {
                AnimatorControllerRuntime? rt = animator.GetLayerRuntime(i);
                AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(i);
                string? name = RuntimeAnimatorInspect.ResolveStateName(author, info.nameHash);
                layers.Add(new AnimatorLiveLayerState(
                    i, name, info.nameHash, info.normalizedTime, info.length, info.isLooping,
                    rt?.IsInTransition == true));
            }
            return new AnimatorLiveStateResult(true, goName, author?.Name,
                layers, RuntimeAnimatorInspect.ReadParameters(animator), null);
        });

    [McpServerTool(Name = "runtime_animator_set_parameter", UseStructuredContent = true)]
    [Description("Set a live Animator parameter (Float/Int/Bool/Trigger). identifier is a GameObject Guid.")]
    public static Task<AnimatorSetParameterResult> AnimatorSetParameter(
        MainThreadDispatcher dispatcher,
        [Description("GameObject Identifier (Guid) that has an Animator.")] string identifier,
        [Description("Parameter name.")] string name,
        [Description("Parameter type: Float, Int, Bool, or Trigger.")] string type,
        [Description("Value. Bool/Trigger: 0 = false/reset, non-zero = true/set.")] float value)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            Animator? animator = FindAnimator(identifier, out string? err, out string? goName);
            if (animator == null)
                return new AnimatorSetParameterResult(false, goName, name, type, value, err);
            RuntimeAnimatorInspect.SetParameter(animator, name, type, value);
            return new AnimatorSetParameterResult(true, goName, name, type, value, null);
        });

    [McpServerTool(Name = "runtime_animator_play", UseStructuredContent = true)]
    [Description("Play or crossfade a named Animator state on a live GameObject.")]
    public static Task<AnimatorPlayResult> AnimatorPlay(
        MainThreadDispatcher dispatcher,
        [Description("GameObject Identifier (Guid) that has an Animator.")] string identifier,
        [Description("State name to play.")] string stateName,
        [Description("Layer index (default 0).")] int layer = 0,
        [Description("Crossfade duration in seconds. 0 snaps.")] float fadeDuration = 0f)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            Animator? animator = FindAnimator(identifier, out string? err, out string? goName);
            if (animator == null)
                return new AnimatorPlayResult(false, goName, stateName, layer, err);
            if (fadeDuration > 0f)
                animator.CrossFade(stateName, fadeDuration, layer);
            else
                animator.Play(stateName, layer);
            return new AnimatorPlayResult(true, goName, stateName, layer, null);
        });

    [McpServerTool(Name = "runtime_animation_preview", UseStructuredContent = true)]
    [Description("Bind a clip on a scene object and scrub the Animation window / preview to a time in seconds so an agent can inspect a pose (e.g. frame 12).")]
    public static Task<AnimationPreviewResult> AnimationPreview(
        MainThreadDispatcher dispatcher,
        [Description("GameObject Identifier (Guid) to bind as the animation root.")] string identifier,
        [Description("Time in seconds to scrub to.")] float time,
        [Description("Optional AnimationClip asset Guid. When omitted, uses the first clip on the object.")] string? clipGuid = null)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            AnimationBindResult bind = BindAnimationWindow(identifier, clipGuid);
            if (!bind.Succeeded)
                return new AnimationPreviewResult(false, bind.RootName, bind.ClipName, time, bind.Error);

            var app = EditorApplication.Instance;
            if (app?.FindOpenPanel(typeof(AnimationClipWindow)) is not AnimationClipWindow win)
                return new AnimationPreviewResult(false, bind.RootName, bind.ClipName, time, "Animation window could not be opened.");

            win.ScrubTo(time);
            return new AnimationPreviewResult(true, bind.RootName, bind.ClipName, win.PlaybackTime, null);
        });

    static AnimationBindResult BindAnimationWindow(string identifier, string? clipGuid)
    {
        if (string.IsNullOrEmpty(identifier) || !Guid.TryParse(identifier, out Guid guid))
            return new AnimationBindResult(false, null, null, false, $"Invalid identifier: {identifier}");

        GameObject? go = Scene.Current?.FindObjectByIdentifier<GameObject>(guid);
        if (go is null)
            return new AnimationBindResult(false, null, null, false, $"GameObject '{identifier}' not found in the current scene.");

        AnimationClip? clip = null;
        if (!string.IsNullOrEmpty(clipGuid))
        {
            if (!Guid.TryParse(clipGuid, out Guid cg))
                return new AnimationBindResult(false, go.Name, null, false, $"Invalid clipGuid: {clipGuid}");
            clip = AssetDatabase.Get(cg) as AnimationClip;
            if (clip == null)
                return new AnimationBindResult(false, go.Name, null, false, $"AnimationClip '{clipGuid}' not found.");
        }

        GameObject root = AnimationWindowBinding.FindAnimationRoot(go) ?? go;
        if (clip == null)
        {
            var clips = new List<AnimationClip>();
            AnimationWindowBinding.CollectClips(root, clips);
            if (clips.Count > 0)
                clip = clips[0];
        }

        var app = EditorApplication.Instance;
        if (app == null)
            return new AnimationBindResult(false, root.Name, clip?.Name, false, "EditorApplication is not running.");

        app.OpenPanel(typeof(AnimationClipWindow));
        if (app.FindOpenPanel(typeof(AnimationClipWindow)) is not AnimationClipWindow win)
            return new AnimationBindResult(false, root.Name, clip?.Name, false, "Animation window could not be opened.");

        win.Bind(root, clip);
        Selection.Select(root);
        return new AnimationBindResult(true, root.Name, clip?.Name,
            clip != null && AnimationWindowBinding.IsReadOnlyClip(clip), null);
    }

    static Animator? FindAnimator(string? identifier, out string? error, out string? goName)
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
        Animator? animator = go.GetComponent<Animator>()
            ?? AnimationWindowBinding.FindAnimationRoot(go)?.GetComponent<Animator>();
        if (animator == null)
        {
            error = $"GameObject '{go.Name}' has no Animator.";
            return null;
        }
        error = null;
        return animator;
    }

    public sealed record AnimatorLiveStateResult(
        bool Succeeded, string? ObjectName, string? ControllerName,
        IReadOnlyList<AnimatorLiveLayerState> Layers,
        IReadOnlyList<AnimatorLiveParameter> Parameters, string? Error);

    public sealed record AnimatorLiveLayerState(
        int Layer, string? StateName, int NameHash, float NormalizedTime, float Length,
        bool IsLooping, bool IsInTransition);

    public sealed record AnimatorSetParameterResult(
        bool Succeeded, string? ObjectName, string Name, string Type, float Value, string? Error);

    public sealed record AnimatorPlayResult(
        bool Succeeded, string? ObjectName, string StateName, int Layer, string? Error);

    public sealed record AnimationPreviewResult(
        bool Succeeded, string? RootName, string? ClipName, float Time, string? Error);
}
