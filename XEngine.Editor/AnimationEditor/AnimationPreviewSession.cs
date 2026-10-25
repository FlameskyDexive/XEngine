// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Animation;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.SceneView;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// Edit-mode scene preview of an <see cref="AnimationClip"/> on the selected
/// <see cref="Animator"/> / <see cref="SkinnedMeshRenderer"/> hierarchy (M22).
/// Samples through the existing <see cref="Animator.EvaluateGraph"/> /
/// <see cref="AnimationClipPlayable"/> path (same as Inspector M17b), then restores
/// captured local TRS so the scene file is not dirtied.
/// </summary>
public sealed class AnimationPreviewSession
{
    public static AnimationPreviewSession Instance { get; } = new();

    struct BoneSnap
    {
        public Transform Transform;
        public Float3 Position;
        public Quaternion Rotation;
        public Float3 Scale;
    }

    readonly List<BoneSnap> _bind = new();
    GameObject? _target;
    AnimationClip? _clip;
    Animator? _animator;
    AnimationClipPlayable? _playable;
    bool _ownsAnimator;

    public bool IsActive => _target != null;
    public GameObject? Target => _target;
    public AnimationClip? Clip => _clip;

    /// <summary>
    /// Drive preview from the Animation window playhead. No-ops (and restores bind pose)
    /// when play mode is on, the clip is missing, or no suitable scene object is selected.
    /// </summary>
    public void Sync(AnimationClip? clip, float time, GameObject? root = null)
    {
        if (Application.IsPlaying || clip == null)
        {
            Stop();
            return;
        }

        GameObject? target = root ?? FindPreviewTarget();
        if (target == null)
        {
            Stop();
            return;
        }

        if (_target != target || _clip != clip)
        {
            Stop();
            Begin(target, clip);
        }

        Sample(time);
    }

    public void Stop()
    {
        if (_target == null)
            return;

        bool wasDirty = EditorSceneManager.IsDirty;
        RestoreBindPose();
        if (_ownsAnimator && _animator != null && _target != null)
            _target.RemoveComponent(_animator);
        else
            _playable = null;

        _bind.Clear();
        _target = null;
        _clip = null;
        _animator = null;
        _playable = null;
        _ownsAnimator = false;
        // Assigning true publishes an edit version; restoring an unchanged flag must not.
        if (EditorSceneManager.IsDirty != wasDirty)
            EditorSceneManager.IsDirty = wasDirty;
    }

    public static GameObject? FindPreviewTarget()
    {
        GameObject? go = Selection.ActiveObject as GameObject;
        if (go == null)
        {
            var selected = Selection.Selected;
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i] is GameObject g)
                {
                    go = g;
                    break;
                }
            }
        }
        if (go == null) return null;
        return AnimationWindowBinding.FindPreviewTarget(go);
    }

    void Begin(GameObject target, AnimationClip clip)
    {
        _target = target;
        _clip = clip;
        CaptureBindPose(target);

        _animator = target.GetComponent<Animator>();
        if (_animator == null)
        {
            _animator = target.AddComponent<Animator>();
            _animator.HideFlags = HideFlags.HideAndDontSave;
            _ownsAnimator = true;
        }
        else
        {
            _ownsAnimator = false;
        }

        _animator.PlayAutomatically = false;
        _animator.ApplyRootMotion = false;
        _animator.Wrap = AnimationWrapMode.ClampForever;
        _animator.RebuildGraph();
        _playable = _animator.Play(clip);
        _playable.Speed = 0f;
        _playable.Wrap = AnimationWrapMode.ClampForever;
    }

    void Sample(float time)
    {
        if (_playable == null || _animator == null) return;
        bool wasDirty = EditorSceneManager.IsDirty;
        if (time < 0f) time = 0f;
        _playable.SetTime(time);
        _animator.EvaluateGraph(0f);
        if (EditorSceneManager.IsDirty != wasDirty)
            EditorSceneManager.IsDirty = wasDirty;
    }

    void CaptureBindPose(GameObject root)
    {
        _bind.Clear();
        CaptureRecursive(root);
    }

    void CaptureRecursive(GameObject go)
    {
        Transform t = go.Transform;
        _bind.Add(new BoneSnap
        {
            Transform = t,
            Position = t.LocalPosition,
            Rotation = t.LocalRotation,
            Scale = t.LocalScale,
        });
        var children = go.Children;
        for (int i = 0; i < children.Count; i++)
            CaptureRecursive(children[i]);
    }

    void RestoreBindPose()
    {
        for (int i = 0; i < _bind.Count; i++)
        {
            BoneSnap s = _bind[i];
            if (s.Transform == null) continue;
            s.Transform.LocalPosition = s.Position;
            s.Transform.LocalRotation = s.Rotation;
            s.Transform.LocalScale = s.Scale;
        }
    }
}
