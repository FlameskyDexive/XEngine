// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Reflection;

using XEngine.Animation;
using XEngine.Animation.Transitions;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Playables;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Inspector;

/// <summary>
/// M47 — inspector for <see cref="TransitionAsset"/> (<c>.transition</c>): pick the concrete
/// <see cref="AnimationTransition"/> payload type from a reflection-discovered list, edit the common
/// fade/speed/start-time fields plus the type's own payload (clip + loop for
/// <see cref="ClipTransition"/>), and save back through the asset pipeline.
/// </summary>
[CustomAssetEditor(typeof(TransitionAsset))]
public class TransitionAssetEditor : AssetImporterEditor
{
    private static List<Type>? _transitionTypes;

    private Guid _currentGuid;
    private bool _dirty;

    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        if (_currentGuid != entry.Guid)
        {
            _currentGuid = entry.Guid;
            _dirty = false;
        }

        id = $"{id}_{entry.Guid:N}";
        Origami.Header(paper, $"{id}_h", $"{EditorIcons.Film}  Transition").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();

        var transitionAsset = asset as TransitionAsset;
        if (transitionAsset == null)
        {
            Origami.Label(paper, $"{id}_none", "TransitionAsset failed to load.").Show();
            return;
        }

        DrawTypePicker(paper, id, transitionAsset);

        AnimationTransition? transition = transitionAsset.Transition;
        if (transition != null)
            DrawCommonFields(paper, id, transition);

        if (transition is ClipTransition clip)
            DrawClipFields(paper, id, clip);
        else if (transition is LinearMixerTransition linear)
            DrawMixerFields(paper, id, linear.ParameterName, v => linear.ParameterName = v,
                null, null, linear.Children, showThreshold: true);
        else if (transition is CartesianMixerTransition cartesian)
            DrawMixerFields(paper, id, cartesian.ParameterName, v => cartesian.ParameterName = v,
                cartesian.ParameterNameY, v => cartesian.ParameterNameY = v, cartesian.Children, showThreshold: false);
        else if (transition is DirectionalMixerTransition directional)
            DrawMixerFields(paper, id, directional.ParameterName, v => directional.ParameterName = v,
                directional.ParameterNameY, v => directional.ParameterNameY = v, directional.Children, showThreshold: false);
        else if (transition is ManualMixerTransition manual)
            DrawMixerFields(paper, id, null, null, null, null, manual.Children,
                showThreshold: false, directNames: true);
        else if (transition is BlendTreeTransition treeTransition)
            DrawBlendTreeField(paper, id, treeTransition);

        if (!_dirty)
            return;

        Origami.Separator(paper, $"{id}_sep_save").Show();
        Origami.Button(paper, $"{id}_save", $"{EditorIcons.FloppyDisk}  Save Asset",
            () =>
            {
                if (AnimationAssetEditorUtil.Save(transitionAsset, entry))
                    _dirty = false;
            }).Show();
    }

    private static void DrawTypePicker(Paper paper, string id, TransitionAsset asset)
    {
        List<Type> types = GetTransitionTypes();
        var labels = new List<string>(types.Count) { "(none)" };
        for (int i = 0; i < types.Count; i++)
            labels.Add(types[i].Name);

        int selected = 0;
        if (asset.Transition != null)
        {
            for (int i = 0; i < types.Count; i++)
                if (types[i] == asset.Transition.GetType())
                    selected = i + 1;
        }

        EditorGUI.Row(paper, $"{id}_type", "Type", () =>
            Origami.Dropdown(paper, $"{id}_type_v", selected, i =>
            {
                asset.Transition = i == 0
                    ? null
                    : (AnimationTransition?)Activator.CreateInstance(types[i - 1]);
            }, labels).Show());
    }

    private void DrawCommonFields(Paper paper, string id, AnimationTransition t)
    {
        Origami.Header(paper, $"{id}_common", "Fade").Show();

        EditorGUI.Row(paper, $"{id}_fade", "Duration", () =>
            Origami.NumericField(paper, $"{id}_fade_v", t.FadeDuration, v =>
            {
                t.FadeDuration = v;
                _dirty = true;
            }).Show());

        EditorGUI.Row(paper, $"{id}_mode", "Mode", () =>
            Origami.EnumDropdown(paper, $"{id}_mode_v", t.FadeMode, v =>
            {
                t.FadeMode = v;
                _dirty = true;
            }).Show());

        EditorGUI.Row(paper, $"{id}_ease", "Easing", () =>
            Origami.EnumDropdown(paper, $"{id}_ease_v", t.FadeEasing, v =>
            {
                t.FadeEasing = v;
                _dirty = true;
            }).Show());

        EditorGUI.Row(paper, $"{id}_speed", "Speed", () =>
            Origami.NumericField(paper, $"{id}_speed_v", t.Speed, v =>
            {
                t.Speed = v;
                _dirty = true;
            }).Show());

        bool useStart = !float.IsNaN(t.NormalizedStartTime);
        EditorGUI.Row(paper, $"{id}_us", "Use Start Time", () =>
            Origami.Checkbox(paper, $"{id}_us_v", useStart, v =>
            {
                t.NormalizedStartTime = v ? 0f : float.NaN;
                _dirty = true;
            }).Show());
        if (useStart)
        {
            EditorGUI.Row(paper, $"{id}_start", "Normalized Start", () =>
                Origami.NumericField(paper, $"{id}_start_v", t.NormalizedStartTime, v =>
                {
                    t.NormalizedStartTime = v;
                    _dirty = true;
                }).Show());
        }

        // M49 — named timed events (routed through Animator.Events at flush time).
        Origami.Header(paper, $"{id}_ev_h", $"Events ({t.Events.Count})").Show();
        for (int i = 0; i < t.Events.Count; i++)
        {
            int idx = i;
            TransitionEvent te = t.Events[i];
            string row = $"{id}_ev_{i}";
            EditorGUI.Row(paper, $"{row}_t", $"Time {i}", () =>
                Origami.NumericField(paper, $"{row}_t_v", te.NormalizedTime, v =>
                {
                    te.NormalizedTime = v;
                    _dirty = true;
                }).Show());
            EditorGUI.Row(paper, $"{row}_n", "Name", () =>
                Origami.TextField(paper, $"{row}_n_v", te.Name, v =>
                {
                    te.Name = v ?? "";
                    _dirty = true;
                }).Show());
            Origami.Button(paper, $"{row}_del", "Remove", () =>
            {
                t.Events.RemoveAt(idx);
                _dirty = true;
            }).Show();
        }
        Origami.Button(paper, $"{id}_ev_add", "Add Event", () =>
        {
            t.Events.Add(new TransitionEvent { NormalizedTime = 0.5f, Name = "Event" });
            _dirty = true;
        }).Show();
    }

    private void DrawClipFields(Paper paper, string id, ClipTransition t)
    {
        Origami.Header(paper, $"{id}_clip_h", "Clip").Show();

        PropertyGridUtils.DrawField(paper, $"{id}_clip", "Clip",
            typeof(AssetRef<AnimationClip>), t.Clip,
            v =>
            {
                t.Clip = v is AssetRef<AnimationClip> r ? r : default;
                _dirty = true;
            });

        EditorGUI.Row(paper, $"{id}_loop", "Loop", () =>
            Origami.Checkbox(paper, $"{id}_loop_v", t.Loop, v =>
            {
                t.Loop = v;
                _dirty = true;
            }).Show());
    }

    /// <summary>M48 — inline mixer fields: parameter names plus the child list (clip/threshold/position/direct).</summary>
    private void DrawMixerFields(
        Paper paper,
        string id,
        string? parameterName,
        Action<string>? setParameterName,
        string? parameterNameY,
        Action<string>? setParameterNameY,
        List<MixerChild> children,
        bool showThreshold,
        bool directNames = false)
    {
        Origami.Header(paper, $"{id}_mix_h", "Mixer").Show();

        if (setParameterName != null)
        {
            EditorGUI.Row(paper, $"{id}_pname", "Parameter", () =>
                Origami.TextField(paper, $"{id}_pname_v", parameterName ?? "", v =>
                {
                    setParameterName(v ?? "");
                    _dirty = true;
                }).Show());
        }
        if (setParameterNameY != null)
        {
            EditorGUI.Row(paper, $"{id}_pnamey", "Parameter Y", () =>
                Origami.TextField(paper, $"{id}_pnamey_v", parameterNameY ?? "", v =>
                {
                    setParameterNameY(v ?? "");
                    _dirty = true;
                }).Show());
        }

        Origami.Header(paper, $"{id}_ch_h", $"Children ({children.Count})").Show();
        for (int i = 0; i < children.Count; i++)
        {
            int idx = i;
            MixerChild child = children[i];
            string row = $"{id}_c_{i}";

            PropertyGridUtils.DrawField(paper, $"{row}_clip", $"Clip {i}",
                typeof(AssetRef<AnimationClip>), child.Clip,
                v =>
                {
                    child.Clip = v is AssetRef<AnimationClip> r ? r : default;
                    _dirty = true;
                });
            if (showThreshold)
            {
                EditorGUI.Row(paper, $"{row}_th", "Threshold", () =>
                    Origami.NumericField(paper, $"{row}_th_v", child.Threshold, v =>
                    {
                        child.Threshold = v;
                        _dirty = true;
                    }).Show());
            }
            else
            {
                EditorGUI.Row(paper, $"{row}_px", "Pos X", () =>
                    Origami.NumericField(paper, $"{row}_px_v", child.Position.X, v =>
                    {
                        child.Position = new XEngine.Vector.Float2(v, child.Position.Y);
                        _dirty = true;
                    }).Show());
                EditorGUI.Row(paper, $"{row}_py", "Pos Y", () =>
                    Origami.NumericField(paper, $"{row}_py_v", child.Position.Y, v =>
                    {
                        child.Position = new XEngine.Vector.Float2(child.Position.X, v);
                        _dirty = true;
                    }).Show());
            }
            if (directNames)
            {
                EditorGUI.Row(paper, $"{row}_dp", "Weight Param", () =>
                    Origami.TextField(paper, $"{row}_dp_v", child.DirectParameterName, v =>
                    {
                        child.DirectParameterName = v ?? "";
                        _dirty = true;
                    }).Show());
            }
            Origami.Button(paper, $"{row}_del", "Remove", () =>
            {
                children.RemoveAt(idx);
                _dirty = true;
            }).Show();
        }

        Origami.Button(paper, $"{id}_add", "Add Child", () =>
        {
            children.Add(new MixerChild());
            _dirty = true;
        }).Show();
    }

    private void DrawBlendTreeField(Paper paper, string id, BlendTreeTransition t)
    {
        Origami.Header(paper, $"{id}_tree_h", "Blend Tree").Show();
        PropertyGridUtils.DrawField(paper, $"{id}_tree", "Tree",
            typeof(AssetRef<BlendTree>), t.Tree,
            v =>
            {
                t.Tree = v is AssetRef<BlendTree> r ? r : default;
                _dirty = true;
            });
    }

    /// <summary>Reflection-discovered concrete transition types (cached; editor cold path).</summary>
    private static List<Type> GetTransitionTypes()
    {
        if (_transitionTypes != null)
            return _transitionTypes;

        var found = new List<Type>();
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                continue; // reflection-only / partially loaded assemblies
            }

            foreach (Type type in types)
            {
                if (!type.IsAbstract && typeof(AnimationTransition).IsAssignableFrom(type))
                    found.Add(type);
            }
        }

        _transitionTypes = found;
        return found;
    }
}
