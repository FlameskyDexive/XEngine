// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Animation;
using XEngine.Echo;
using XEngine.Editor.Projects;
using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// Resolves the Animation window's (root GameObject, clip list) binding from a scene selection.
/// </summary>
public static class AnimationWindowBinding
{
    public static GameObject? FindAnimationRoot(GameObject? go)
    {
        if (go == null) return null;
        if (HasAnimationComponent(go)) return go;

        Animator? anim = go.GetComponentInParent<Animator>();
        if (anim != null) return anim.GameObject;
        AnimationComponent? legacy = go.GetComponentInParent<AnimationComponent>();
        if (legacy != null) return legacy.GameObject;
        return go;
    }

    /// <summary>Same walk as <see cref="FindAnimationRoot"/> but null when nothing is previewable.</summary>
    public static GameObject? FindPreviewTarget(GameObject? go)
    {
        GameObject? root = FindAnimationRoot(go);
        if (root == null) return null;
        if (HasAnimationComponent(root)) return root;
        if (root.GetComponent<SkinnedMeshRenderer>() != null) return root;
        SkinnedMeshRenderer? smr = root.GetComponentInParent<SkinnedMeshRenderer>()
            ?? go!.GetComponentInParent<SkinnedMeshRenderer>();
        return smr?.GameObject;
    }

    public static void CollectClips(GameObject? root, List<AnimationClip> dst)
    {
        dst.Clear();
        if (root == null) return;

        var animator = root.GetComponent<Animator>();
        if (animator != null)
        {
            AddUnique(dst, animator.Clip.Res);
            AnimatorController? ctrl = animator.Controller.Res;
            if (ctrl != null)
            {
                var refs = ctrl.GetClipReferences();
                for (int i = 0; i < refs.Count; i++)
                    AddUnique(dst, refs[i].Res);
            }
            return;
        }

        var legacy = root.GetComponent<AnimationComponent>();
        if (legacy == null) return;
        AddUnique(dst, legacy.DefaultClip.Res);
        var clips = legacy.Clips;
        for (int i = 0; i < clips.Count; i++)
            AddUnique(dst, clips[i].Res);
    }

    public static bool HasAnimationComponent(GameObject? root)
        => root != null && (root.GetComponent<Animator>() != null || root.GetComponent<AnimationComponent>() != null);

    /// <summary>
    /// Model-imported sub-asset clips (path is .fbx/.gltf/…, not .anim) are read-only in the window.
    /// In-memory clips and standalone .anim files are editable.
    /// </summary>
    public static bool IsReadOnlyClip(AnimationClip? clip)
    {
        if (clip == null) return false;
        string path = clip.AssetPath ?? "";
        if (path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrEmpty(path) && clip.AssetID == Guid.Empty)
            return false;
        if (path.Length > 0)
            return true;
        return clip.AssetID != Guid.Empty;
    }

    public static AnimationClip CreateEmptyClip(string name)
        => new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? "New Animation" : name,
            Duration = 1f,
            TicksPerSecond = 30f,
            Wrap = AnimationWrapMode.Loop,
        };

    /// <summary>
    /// Echo-clone a clip for "Duplicate to edit". Clears AssetID/path so the copy is a new .anim.
    /// </summary>
    public static AnimationClip DuplicateClip(AnimationClip source)
    {
        EchoObject echo = Serializer.Serialize(typeof(object), source);
        var copy = Serializer.Deserialize<AnimationClip>(echo) ?? CreateEmptyClip(source.Name);
        copy.AssetID = Guid.Empty;
        copy.AssetPath = string.Empty;
        string baseName = string.IsNullOrEmpty(source.Name) ? "New Animation" : source.Name;
        copy.Name = baseName.EndsWith(" (edit)", StringComparison.Ordinal) ? baseName : baseName + " (edit)";
        return copy;
    }

    /// <summary>
    /// Attach <paramref name="clip"/> to <paramref name="root"/>. Adds Animator + controller when
    /// the object has no animation component; otherwise updates Clip / Clips / controller states.
    /// </summary>
    public static void AssignClipToRoot(GameObject root, AnimationClip clip)
    {
        if (root == null || clip == null) return;

        var animator = root.GetComponent<Animator>();
        var legacy = root.GetComponent<AnimationComponent>();
        if (animator == null && legacy == null)
        {
            animator = root.AddComponent<Animator>();
            animator.Clip = new AssetRef<AnimationClip>(clip);
            EnsureController(animator, clip);
            return;
        }

        if (animator != null)
        {
            animator.Clip = new AssetRef<AnimationClip>(clip);
            EnsureController(animator, clip);
            return;
        }

        if (legacy.DefaultClip.Res == null && legacy.DefaultClip.AssetID == Guid.Empty)
            legacy.DefaultClip = new AssetRef<AnimationClip>(clip);
        bool found = false;
        var clips = legacy.Clips;
        for (int i = 0; i < clips.Count; i++)
        {
            if (ReferenceEquals(clips[i].Res, clip))
            {
                found = true;
                break;
            }
        }
        if (!found)
            clips.Add(new AssetRef<AnimationClip>(clip));
    }

    static void EnsureController(Animator animator, AnimationClip clip)
    {
        AnimatorController? ctrl = animator.Controller.Res;
        if (ctrl is AnimatorOverrideController ov && ov.Base.Res != null)
            ctrl = ov.Base.Res;
        if (ctrl == null)
        {
            if (EditorAssetBackend.Instance != null && Project.Current != null)
            {
                AnimatorComponentSetup.CreateAndBindController(animator);
                ctrl = animator.Controller.Res;
            }
            if (ctrl == null)
            {
                ctrl = AnimatorComponentSetup.CreateControllerForClip(clip, animator.GameObject?.Name);
                animator.Controller = new AssetRef<AnimatorController>(ctrl);
            }
            return;
        }

        AddClipStateIfMissing(ctrl, clip);
    }

    static void AddClipStateIfMissing(AnimatorController ctrl, AnimationClip clip)
    {
        var states = ctrl.Layers.Count > 0 ? ctrl.Layers[0].States : ctrl.States;
        for (int i = 0; i < states.Count; i++)
        {
            AnimationClip? motion = states[i].Motion.Res;
            if (ReferenceEquals(motion, clip)
                || (clip.AssetID != Guid.Empty && states[i].Motion.AssetID == clip.AssetID))
                return;
        }

        string name = string.IsNullOrEmpty(clip.Name) ? "New State" : clip.Name;
        string unique = name;
        int n = 1;
        while (StateNameTaken(states, unique))
            unique = $"{name} {++n}";

        states.Add(new AnimatorController.State
        {
            Name = unique,
            Speed = 1f,
            IsLooping = true,
            Motion = new AssetRef<AnimationClip>(clip),
        });
        if (string.IsNullOrEmpty(ctrl.DefaultStateName))
            ctrl.DefaultStateName = unique;
        if (ctrl.Layers.Count > 0 && string.IsNullOrEmpty(ctrl.Layers[0].DefaultStateName))
            ctrl.Layers[0].DefaultStateName = unique;
    }

    static bool StateNameTaken(List<AnimatorController.State> states, string name)
    {
        for (int i = 0; i < states.Count; i++)
            if (string.Equals(states[i].Name, name, StringComparison.Ordinal))
                return true;
        return false;
    }

    static void AddUnique(List<AnimationClip> dst, AnimationClip? clip)
    {
        if (clip == null) return;
        for (int i = 0; i < dst.Count; i++)
            if (ReferenceEquals(dst[i], clip) || (clip.AssetID != Guid.Empty && dst[i].AssetID == clip.AssetID))
                return;
        dst.Add(clip);
    }
}
