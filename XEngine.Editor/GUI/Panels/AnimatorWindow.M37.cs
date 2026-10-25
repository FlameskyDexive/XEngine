// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.GUI.Panels;

public partial class AnimatorWindow
{
    private bool _autoLiveLink = true;
    private string? _pendingMakeTransitionFrom;

    public bool AutoLiveLink
    {
        get => _autoLiveLink;
        set => _autoLiveLink = value;
    }

    public void NavigateToStateMachinePath(IReadOnlyList<string> path)
    {
        if (PathsEqual(_smPath, path)) return;
        _smPath.Clear();
        for (int i = 0; i < path.Count; i++)
            _smPath.Add(path[i]);
        _graphDirty = true;
    }

    public AnimatorController.State? CreateStateFromClip(AnimationClip clip, Float2 graphPos)
    {
        if (_controller == null || clip == null) return null;
        Undo.Snapshot(_controller);
        var states = AnimatorControllerEditGraph.States(_controller, _selectedLayerIndex, _smPath);
        string baseName = string.IsNullOrEmpty(clip.Name) ? "New State" : clip.Name;
        string name = AnimatorControllerEditGraph.UniqueStateName(_controller, _selectedLayerIndex, baseName, _smPath);
        var state = new AnimatorController.State
        {
            Name = name,
            Speed = 1f,
            IsLooping = true,
            PosX = graphPos.X,
            PosY = graphPos.Y,
            Motion = new AssetRef<AnimationClip>(clip),
        };
        states.Add(state);
        if (string.IsNullOrEmpty(AnimatorControllerEditGraph.GetDefaultStateName(_controller, _selectedLayerIndex, _smPath)))
            AnimatorControllerEditGraph.SetDefaultStateName(_controller, _selectedLayerIndex, name, _smPath);
        _graphDirty = true;
        return state;
    }

    public void CreateBlendTreeInState(string name)
    {
        if (_controller == null) return;
        var state = FindStateByName(name);
        if (state == null || BlendTreeEditorModel.HasTree(state)) return;
        Undo.Snapshot(_controller);
        string param = _controller.Parameters.Count > 0 ? _controller.Parameters[0].Name : string.Empty;
        BlendTreeEditorModel.AttachInline(state, BlendTreeEditorModel.CreateInline(state.Name, param));
        _graphDirty = true;
    }

    public void BeginMakeTransition(string fromStateName) => _pendingMakeTransitionFrom = fromStateName;

    public IReadOnlyList<string> CollectParameterRefs(string parameterName)
    {
        var refs = new List<string>();
        if (_controller == null || string.IsNullOrEmpty(parameterName)) return refs;
        CollectParameterRefs(AnimatorControllerEditGraph.EffectiveRoot(_controller, _selectedLayerIndex),
            parameterName, "", refs);
        return refs;
    }

    internal void ApplyLiveLink()
    {
        _activeStateName = null;
        if (!Application.IsPlaying || _controller == null)
        {
            ClearLiveLinkVisuals();
            return;
        }

        Animator? animator = ResolveLiveAnimator();
        AnimatorControllerRuntime? runtime = animator?.GetLayerRuntime(_selectedLayerIndex);
        if (runtime == null)
        {
            ClearLiveLinkVisuals();
            return;
        }

        var info = runtime.GetStateInfo();
        var path = new List<string>();
        if (AnimatorControllerEditGraph.TryFindStatePathByHash(
                _controller, _selectedLayerIndex, info.nameHash, path, out string liveName))
        {
            _activeStateName = liveName;
            if (_autoLiveLink)
                NavigateToStateMachinePath(path);
        }

        string defaultName = AnimatorControllerEditGraph.GetDefaultStateName(_controller, _selectedLayerIndex, _smPath);
        float progress = Wrap01(info.normalizedTime);
        for (int i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (IsSpecialNode(node.Id)) continue;
            var state = FindStateByName(node.Id);
            if (state == null) continue;
            bool isActive = node.Id == _activeStateName;
            bool isDefault = state.Name == defaultName;
            node.Accent = isActive ? ActiveAccent
                         : isDefault ? DefaultAccent
                         : StateAccent;
            node.Progress = isActive && _autoLiveLink ? progress : -1f;
        }
    }

    internal void ClearLiveLinkVisuals()
    {
        if (_controller == null) return;
        string defaultName = AnimatorControllerEditGraph.GetDefaultStateName(_controller, _selectedLayerIndex, _smPath);
        for (int i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (IsSpecialNode(node.Id)) continue;
            var state = FindStateByName(node.Id);
            if (state == null) continue;
            node.Accent = state.Name == defaultName ? DefaultAccent : StateAccent;
            node.Progress = -1f;
        }
    }

    internal Animator? ResolveLiveAnimator()
    {
        if (!Application.IsPlaying || _controller == null) return null;
        if (Selection.ActiveObject is GameObject go)
        {
            Animator? selected = go.GetComponent<Animator>();
            if (selected != null && SameController(selected))
                return selected;
        }
        if (!_autoLiveLink) return null;
        Scene? scene = Scene.Current;
        if (scene == null) return null;
        Animator?[] found = scene.FindObjectsOfType<Animator>();
        for (int i = 0; i < found.Length; i++)
        {
            Animator? candidate = found[i];
            if (candidate != null && SameController(candidate))
                return candidate;
        }
        return null;
    }

    internal void HandleExternalDrop(Float2 graphPos)
    {
        if (_controller == null) return;
        if (DragDrop.Payload is not AssetDragPayload payload) return;
        var clips = new List<AnimationClip>();
        CollectDroppedClips(payload, clips);
        if (clips.Count == 0) return;

        Float2 pos = graphPos;
        for (int i = 0; i < clips.Count; i++)
        {
            CreateStateFromClip(clips[i], pos);
            pos.X += 180f;
        }
    }

    internal void TryCompleteMakeTransition()
    {
        if (_pendingMakeTransitionFrom == null || _selectedStateName == null) return;
        if (_selectedStateName == _pendingMakeTransitionFrom || IsSpecialNode(_selectedStateName)) return;
        CreateTransition(_pendingMakeTransitionFrom, _selectedStateName);
        _pendingMakeTransitionFrom = null;
    }

    internal void ConfirmDelete(string message, Action onYes)
        => Origami.Confirm("Delete", message, onYes);

    internal void ConfirmDeleteParameter()
    {
        if (_controller == null || _selectedParamIndex < 0 || _selectedParamIndex >= _controller.Parameters.Count)
            return;
        string pname = _controller.Parameters[_selectedParamIndex].Name;
        var refs = CollectParameterRefs(pname);
        string body = refs.Count == 0
            ? $"Delete parameter '{pname}'?"
            : $"Delete parameter '{pname}'?\nReferenced by:\n- " + string.Join("\n- ", refs);
        Origami.Confirm("Delete Parameter", body, () =>
        {
            if (_controller == null) return;
            Undo.Snapshot(_controller);
            int idx = -1;
            for (int i = 0; i < _controller.Parameters.Count; i++)
            {
                if (_controller.Parameters[i].Name == pname) { idx = i; break; }
            }
            if (idx >= 0) _controller.Parameters.RemoveAt(idx);
            _selectedParamIndex = -1;
        });
    }

    internal void CreateStateFromSelectedClip(Float2 graphPos)
    {
        var selected = Selection.Selected;
        Float2 pos = graphPos;
        bool any = false;
        for (int i = 0; i < selected.Count; i++)
        {
            if (selected[i] is not ContentItem ci || ci.IsFolder) continue;
            if (!ci.RelativePath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)
                && !ci.Name.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                continue;
            if (AssetDatabase.Get(ci.Guid) is not AnimationClip clip) continue;
            CreateStateFromClip(clip, pos);
            pos.X += 180f;
            any = true;
        }
        if (!any && Selection.ActiveObject is ContentItem active
            && AssetDatabase.Get(active.Guid) is AnimationClip one)
        {
            CreateStateFromClip(one, graphPos);
        }
    }

    private bool SameController(Animator animator)
    {
        AnimatorController? res = animator.Controller.Res;
        if (res == _controller) return true;
        Guid id = animator.Controller.AssetID;
        return _controller != null && id != Guid.Empty && id == _controller.AssetID;
    }

    private static void CollectDroppedClips(AssetDragPayload payload, List<AnimationClip> clips)
    {
        Guid[] guids = payload.AssetGuids is { Length: > 0 } ? payload.AssetGuids : [payload.AssetGuid];
        string[] paths = payload.AssetPaths ?? Array.Empty<string>();
        for (int i = 0; i < guids.Length; i++)
        {
            bool looksLikeClip = payload.AssetType == typeof(AnimationClip);
            if (!looksLikeClip && i < paths.Length)
                looksLikeClip = paths[i].EndsWith(".anim", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeClip && !string.IsNullOrEmpty(payload.AssetName))
                looksLikeClip = payload.AssetName.EndsWith(".anim", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeClip) continue;
            if (AssetDatabase.Get(guids[i]) is AnimationClip clip)
                clips.Add(clip);
        }
    }

    private static void CollectParameterRefs(
        AnimatorController.StateMachine sm, string parameterName, string path, List<string> refs)
    {
        var states = sm.States;
        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            string loc = string.IsNullOrEmpty(path) ? s.Name : path + "/" + s.Name;
            AddParamRef(s.SpeedMultiplierParameter, parameterName, $"State '{loc}' Speed", refs);
            AddParamRef(s.MirrorParameter, parameterName, $"State '{loc}' Mirror", refs);
            AddParamRef(s.CycleOffsetParameter, parameterName, $"State '{loc}' Cycle Offset", refs);
            AddParamRef(s.TimeParameter, parameterName, $"State '{loc}' Time", refs);
            BlendTree? tree = BlendTreeEditorModel.GetTree(s);
            if (tree != null)
                AddParamRef(tree.ParameterName, parameterName, $"State '{loc}' BlendTree", refs);
        }

        CollectTransitionParamRefs(sm.Transitions, parameterName, path, refs);
        CollectTransitionParamRefs(sm.AnyStateTransitions, parameterName, path, refs);
        CollectTransitionParamRefs(sm.EntryTransitions, parameterName, path, refs);
        CollectTransitionParamRefs(sm.StateMachineTransitions, parameterName, path, refs);

        var children = sm.StateMachines;
        for (int i = 0; i < children.Count; i++)
        {
            string childPath = string.IsNullOrEmpty(path) ? children[i].Name : path + "/" + children[i].Name;
            CollectParameterRefs(children[i], parameterName, childPath, refs);
        }
    }

    private static void CollectTransitionParamRefs(
        List<AnimatorController.Transition> list, string parameterName, string path, List<string> refs)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            var conds = t.Conditions;
            for (int c = 0; c < conds.Count; c++)
            {
                if (conds[c].ParamName != parameterName) continue;
                string loc = string.IsNullOrEmpty(path) ? t.Name : path + "/" + t.Name;
                refs.Add($"Transition '{loc}' condition");
            }
        }
    }

    private static void AddParamRef(string field, string parameterName, string label, List<string> refs)
    {
        if (field == parameterName)
            refs.Add(label);
    }

    private static bool PathsEqual(List<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static float Wrap01(float t)
    {
        if (t < 0f) return 0f;
        return t - MathF.Floor(t);
    }
}
