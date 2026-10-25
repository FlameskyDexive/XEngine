using System;
using System.Collections.Generic;

using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Spine;

namespace XEngine.Spine.Editor;

[CustomEditor(typeof(SkeletonAnimation))]
public sealed class SkeletonAnimationEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var animation = (SkeletonAnimation)target;
        Undo.SnapshotIfInteracting(animation);
        if (EditorTheme.DefaultFont != null)
            DrawDefaultInspector(paper, $"{id}_def", target);

        SkeletonDataAsset? data = animation.SkeletonDataAsset.ResWeak ?? animation.SkeletonDataAsset.Res;
        IReadOnlyList<string> clips = data?.AnimationNames ?? [];
        if (clips.Count > 0)
        {
            int index = IndexOf(clips, animation.AnimationName);
            EditorGUI.Row(paper, $"{id}_clip", "Animation", () =>
                Origami.Dropdown(paper, $"{id}_clip_v", index, i =>
                {
                    Undo.Snapshot(animation);
                    animation.AnimationName = clips[i];
                    animation.Initialize(overwrite: true);
                    if (!Application.IsPlaying)
                        animation.UpdateAnimation(0f);
                }, clips).Show());
        }

        IReadOnlyList<string> skins = data?.SkinNames ?? [];
        if (skins.Count > 0)
        {
            int skinIndex = IndexOf(skins, animation.InitialSkinName);
            EditorGUI.Row(paper, $"{id}_skin", "Skin", () =>
                Origami.Dropdown(paper, $"{id}_skin_v", skinIndex, i =>
                {
                    Undo.Snapshot(animation);
                    animation.InitialSkinName = skins[i];
                    animation.Initialize(overwrite: true);
                }, skins).Show());
        }

        Origami.Label(paper, $"{id}_state",
            animation.AnimationState != null
                ? $"Track 0: {animation.AnimationState.GetTrack(0)?.Animation.Name ?? "(none)"}"
                : "AnimationState not initialized.").Show();
    }

    private static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i], value, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }
}
