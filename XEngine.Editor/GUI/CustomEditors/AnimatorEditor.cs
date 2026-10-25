// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;

namespace XEngine.Editor.Inspector;

[CustomEditor(typeof(Animator))]
public class AnimatorEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var animator = (Animator)target;
        if (EditorTheme.DefaultFont == null) return;

        Undo.SnapshotIfInteracting(animator, paper);

        PropertyGridUtils.DrawField(paper, $"{id}_ctrl", "Controller",
            typeof(AssetRef<AnimatorController>), animator.Controller,
            v => animator.Controller = v is AssetRef<AnimatorController> r ? r : default);

        PropertyGridUtils.DrawField(paper, $"{id}_clip", "Clip",
            typeof(AssetRef<AnimationClip>), animator.Clip,
            v => animator.Clip = v is AssetRef<AnimationClip> r ? r : default);

        EditorGUI.Row(paper, $"{id}_arm", "Apply Root Motion", () =>
            Origami.Toggle(paper, $"{id}_arm_v", animator.ApplyRootMotion, v => animator.ApplyRootMotion = v).Show());

        EditorGUI.Row(paper, $"{id}_spd", "Speed", () =>
            Origami.NumericField(paper, $"{id}_spd_v", animator.Speed, v => animator.Speed = v).Show());

        EditorGUI.Row(paper, $"{id}_wrap", "Wrap", () =>
            Origami.EnumDropdown(paper, $"{id}_wrap_v", animator.Wrap, v => animator.Wrap = v).Show());

        Origami.Button(paper, $"{id}_open", "Open Animator Window",
            () => AnimationAssetEditorUtil.OpenAnimatorWindow(animator.AuthoringController)).Show();

        if (animator.Controller.Res == null)
        {
            Origami.Button(paper, $"{id}_create", "Create Controller", () =>
            {
                Undo.Snapshot(animator);
                AnimatorComponentSetup.CreateAndBindController(animator);
            }).Show();
        }

        if (!Application.IsPlaying)
            return;

        Origami.Separator(paper, $"{id}_pm_sep").Show();
        Origami.Header(paper, $"{id}_pm_h", "Play Mode").Underline().Show();

        using (Origami.BeginReadOnlyScope())
        {
            Origami.Label(paper, $"{id}_pm_state", $"Current State: {CurrentStateName(animator)}").Show();
            Origami.Label(paper, $"{id}_pm_trans",
                $"Is In Transition: {(animator.Runtime?.IsInTransition == true)}").Show();
        }

        DrawLiveParameters(paper, id, animator);
    }

    private static string CurrentStateName(Animator animator)
    {
        AnimatorControllerRuntime? runtime = animator.Runtime;
        AnimatorController? controller = animator.AuthoringController;
        if (runtime != null && controller != null)
        {
            int idx = runtime.CurrentStateIndex;
            var states = controller.States;
            if (idx >= 0 && idx < states.Count)
                return states[idx].Name;
        }

        return animator.CurrentClip?.Name ?? "(none)";
    }

    private static void DrawLiveParameters(Paper paper, string id, Animator animator)
    {
        AnimatorController? controller = animator.AuthoringController;
        if (controller == null || animator.Runtime == null)
            return;

        var parameters = controller.Parameters;
        int n = parameters.Count;
        if (n == 0)
            return;

        Origami.Header(paper, $"{id}_pm_p", "Parameters").Show();
        for (int i = 0; i < n; i++)
        {
            AnimatorController.Parameter p = parameters[i];
            string name = p.Name;
            if (string.IsNullOrEmpty(name))
                continue;

            string rowId = $"{id}_pm_p_{i}";
            switch (p.Type)
            {
                case AnimatorParameterType.Float:
                    EditorGUI.Row(paper, rowId, name, () =>
                        Origami.NumericField(paper, $"{rowId}_v", animator.GetFloat(name),
                            v => animator.SetFloat(name, v)).Show());
                    break;
                case AnimatorParameterType.Int:
                    EditorGUI.Row(paper, rowId, name, () =>
                        Origami.NumericField(paper, $"{rowId}_v", animator.GetInteger(name),
                            v => animator.SetInteger(name, v)).Show());
                    break;
                case AnimatorParameterType.Bool:
                    EditorGUI.Row(paper, rowId, name, () =>
                        Origami.Toggle(paper, $"{rowId}_v", animator.GetBool(name),
                            v => animator.SetBool(name, v)).Show());
                    break;
                case AnimatorParameterType.Trigger:
                    EditorGUI.Row(paper, rowId, name, () =>
                        Origami.Button(paper, $"{rowId}_v", "Set", () => animator.SetTrigger(name)).Show());
                    break;
            }
        }
    }
}
