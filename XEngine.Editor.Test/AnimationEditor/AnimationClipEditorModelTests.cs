// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Editor.AnimationEditor;
using XEngine.Runtime;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>
/// Verifies the Animation Clip editor model: clip load + view range, time↔screen transforms
/// (round-trip, clamping), bone enumeration, and keyframe hit-testing within a pick radius.
/// </summary>
public class AnimationClipEditorModelTests
{
    private static AnimationClip MakeTwoKeyClip()
    {
        var clip = new AnimationClip { Name = "c" };
        clip.Duration = 1f;
        var bone = new AnimationClip.AnimBone { BoneName = "Hips" };
        bone.PosX = new AnimationCurve(new[] { new KeyFrame(0f, 0f), new KeyFrame(1f, 10f) });
        bone.PosX.SmoothTangents(CurveTangent.Linear);
        bone.PosY = new AnimationCurve(new[] { new KeyFrame(0f, 0f) });
        bone.PosZ = new AnimationCurve(new[] { new KeyFrame(0f, 0f) });
        clip.AddBone(bone);
        return clip;
    }

    [Fact]
    public void SetClip_InitializesViewRangeToDuration()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());

        Assert.NotNull(m.Clip);
        Assert.Equal(0f, m.ViewStartTime);
        Assert.Equal(1f, m.ViewEndTime);
    }

    [Fact]
    public void TimeToScreenX_MapsViewRangeAcrossWidth()
    {
        var m = new AnimationClipEditorModel { ViewStartTime = 0f, ViewEndTime = 2f };
        const float width = 400f;

        Assert.Equal(0f, m.TimeToScreenX(0f, width));
        Assert.Equal(200f, m.TimeToScreenX(1f, width));
        Assert.Equal(400f, m.TimeToScreenX(2f, width));
    }

    [Fact]
    public void ScreenXToTime_InvertsTimeToScreenX()
    {
        var m = new AnimationClipEditorModel { ViewStartTime = 0.5f, ViewEndTime = 2.5f };
        const float width = 300f;
        float t = 1.25f;
        float round = m.ScreenXToTime(m.TimeToScreenX(t, width), width);
        Assert.Equal(t, round, 4);
    }

    [Fact]
    public void GetBonePaths_ListsAllBones()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());

        var paths = m.GetBonePaths();
        Assert.Single(paths);
        Assert.Equal("Hips", paths[0]);
    }

    [Fact]
    public void HitKeyframe_AtKnownTime_ReturnsIndex()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip()); // PosX keys at t=0 and t=1.

        int k0 = m.HitKeyframe("Hips", AnimationClipEditorModel.Channel.Position, time: 0f);
        int k1 = m.HitKeyframe("Hips", AnimationClipEditorModel.Channel.Position, time: 1f);

        Assert.Equal(0, k0);
        Assert.Equal(1, k1);
    }

    [Fact]
    public void HitKeyframe_OutsideRadius_ReturnsMinusOne()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip()); // keys at 0 and 1.

        // t=0.5 with default radius 0.05 → too far from either key.
        int k = m.HitKeyframe("Hips", AnimationClipEditorModel.Channel.Position, time: 0.5f);
        Assert.Equal(-1, k);
    }

    [Fact]
    public void HitKeyframe_UnknownBone_ReturnsMinusOne()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());

        Assert.Equal(-1, m.HitKeyframe("Tail", AnimationClipEditorModel.Channel.Position, time: 0f));
    }

    [Fact]
    public void GetCurves_ReturnsChannelCurves()
    {
        var clip = MakeTwoKeyClip();
        var bone = clip.GetBone("Hips")!;

        Assert.Equal(3, AnimationClipEditorModel.GetCurves(bone, AnimationClipEditorModel.Channel.Position).Count);
        Assert.Equal(4, AnimationClipEditorModel.GetCurves(bone, AnimationClipEditorModel.Channel.Rotation).Count);
        Assert.Equal(3, AnimationClipEditorModel.GetCurves(bone, AnimationClipEditorModel.Channel.Scale).Count);
    }

    [Fact]
    public void AddKeyframe_InsertsSortedKeysOnAllComponents()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());

        Assert.True(m.AddKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0.5f));

        var bone = m.Clip!.GetBone("Hips")!;
        Assert.Equal(3, bone.PosX.Keys.Count);
        Assert.Equal(0.5f, bone.PosX.Keys[1].Position, 4);
        Assert.Equal(5f, bone.PosX.Keys[1].Value, 3); // evaluated between 0 and 10
        Assert.Equal(2, bone.PosY.Keys.Count);
        Assert.True(m.HasKeyframeSelection);
        Assert.Equal(0.5f, m.SelectedKeyframeTime, 4);
    }

    [Fact]
    public void AddKeyframe_IsIdempotentAtSameTime()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        m.AddKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0.25f);
        int count = m.Clip!.GetBone("Hips")!.PosX.Keys.Count;

        m.AddKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0.25f);

        Assert.Equal(count, m.Clip.GetBone("Hips")!.PosX.Keys.Count);
    }

    [Fact]
    public void MoveKeyframe_ReordersByTime()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        m.AddKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0.25f);

        Assert.True(m.MoveKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0.25f, 0.75f));

        var keys = m.Clip!.GetBone("Hips")!.PosX.Keys;
        Assert.Equal(0f, keys[0].Position, 4);
        Assert.Equal(0.75f, keys[1].Position, 4);
        Assert.Equal(1f, keys[2].Position, 4);
        Assert.Equal(0.75f, m.SelectedKeyframeTime, 4);
    }

    [Fact]
    public void RemoveSelectedKeyframe_DeletesMatchingTimeOnAllComponents()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        m.AddKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0.4f);
        Assert.Equal(3, m.Clip!.GetBone("Hips")!.PosX.Keys.Count);

        Assert.True(m.RemoveSelectedKeyframe());

        Assert.Equal(2, m.Clip.GetBone("Hips")!.PosX.Keys.Count);
        Assert.Single(m.Clip.GetBone("Hips")!.PosY.Keys); // original t=0 remains
        Assert.False(m.HasKeyframeSelection);
    }

    [Fact]
    public void SetKeyframeValue_WritesComponent()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());

        Assert.True(m.SetKeyframeValue("Hips", AnimationClipEditorModel.Channel.Position, 1f, 0, 42f));
        Assert.Equal(42f, m.Clip!.GetBone("Hips")!.PosX.Keys[1].Value, 3);
        Assert.True(m.TryGetKeyframeValue("Hips", AnimationClipEditorModel.Channel.Position, 1f, 0, out float v));
        Assert.Equal(42f, v, 3);
    }

    [Fact]
    public void AddProperty_CreatesMissingScaleCurves()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        var bone = m.Clip!.GetBone("Hips")!;
        Assert.False(AnimationClipEditorModel.HasChannelCurves(bone, AnimationClipEditorModel.Channel.Scale));

        Assert.True(m.AddProperty("Hips", AnimationClipEditorModel.Channel.Scale, 0.2f));

        Assert.NotNull(bone.ScaleX);
        Assert.Single(bone.ScaleX.Keys);
        Assert.Equal(1f, bone.ScaleX.Keys[0].Value, 3); // default scale
        Assert.Equal(0.2f, bone.ScaleX.Keys[0].Position, 4);
    }

    [Fact]
    public void FillAddableProperties_ListsMissingChannelsOnly()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        var dest = new System.Collections.Generic.List<(string, AnimationClipEditorModel.Channel)>();

        m.FillAddableProperties(dest);

        Assert.Equal(2, dest.Count);
        Assert.Equal("Hips", dest[0].Item1);
        Assert.Equal(AnimationClipEditorModel.Channel.Rotation, dest[0].Item2);
        Assert.Equal(AnimationClipEditorModel.Channel.Scale, dest[1].Item2);
    }

    [Fact]
    public void ZoomView_KeepsAnchorTimeUnderCursor()
    {
        var m = new AnimationClipEditorModel { ViewStartTime = 0f, ViewEndTime = 2f };
        m.ZoomView(0.5f, anchorTime: 1f);

        Assert.Equal(0.5f, m.ViewStartTime, 4);
        Assert.Equal(1.5f, m.ViewEndTime, 4);
        Assert.Equal(1f, m.ScreenXToTime(m.TimeToScreenX(1f, 100f), 100f), 4);
    }

    [Fact]
    public void PanView_ShiftsRange()
    {
        var m = new AnimationClipEditorModel { ViewStartTime = 0f, ViewEndTime = 1f };
        m.PanView(0.25f);
        Assert.Equal(0.25f, m.ViewStartTime, 4);
        Assert.Equal(1.25f, m.ViewEndTime, 4);
    }

    [Fact]
    public void StepFrame_UsesTicksPerSecond()
    {
        var clip = MakeTwoKeyClip();
        clip.TicksPerSecond = 10f;
        var m = new AnimationClipEditorModel();
        m.SetClip(clip);

        Assert.Equal(0.1f, m.StepFrame(0f, 1), 4);
        Assert.Equal(0f, m.StepFrame(0f, -1), 4);
    }

    [Fact]
    public void AddKeyframe_ExpandsClipDuration()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());

        m.AddKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 2.5f);

        Assert.Equal(2.5f, m.Clip!.Duration, 4);
    }

    [Fact]
    public void TryHitKeyframe_FindsKeyOnNonFirstComponent()
    {
        var clip = new AnimationClip { Name = "c", Duration = 1f };
        var bone = new AnimationClip.AnimBone { BoneName = "Hips" };
        bone.PosX = new AnimationCurve(new[] { new KeyFrame(0f, 0f) });
        bone.PosY = new AnimationCurve(new[] { new KeyFrame(0f, 0f), new KeyFrame(0.5f, 3f) });
        bone.PosZ = new AnimationCurve(new[] { new KeyFrame(0f, 0f) });
        clip.AddBone(bone);
        var m = new AnimationClipEditorModel();
        m.SetClip(clip);

        Assert.True(m.TryHitKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0.5f, 0.05f, out _, out float t));
        Assert.Equal(0.5f, t, 4);
    }

    [Fact]
    public void SnapToFrame_RoundsToTicksPerSecond()
    {
        var clip = MakeTwoKeyClip();
        clip.TicksPerSecond = 10f;
        var m = new AnimationClipEditorModel();
        m.SetClip(clip);

        Assert.Equal(0.5f, m.SnapToFrame(0.46f), 4);
        Assert.Equal(0.5f, m.SnapToFrame(0.54f), 4);
        Assert.Equal(0f, m.SnapToFrame(-1f), 4);
    }

    [Fact]
    public void SelectKeyframe_Additive_KeepsMultipleKeys()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0, 0f);
        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 1, 1f, additive: true);

        Assert.Equal(2, m.SelectedKeys.Count);
        Assert.True(m.IsKeySelected("Hips", AnimationClipEditorModel.Channel.Position, 0f));
        Assert.True(m.IsKeySelected("Hips", AnimationClipEditorModel.Channel.Position, 1f));
    }

    [Fact]
    public void MoveSelectedKeys_ShiftsGroupAndSnaps()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        m.Clip!.TicksPerSecond = 10f;
        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0, 0f);
        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 1, 1f, additive: true);

        Assert.True(m.MoveSelectedKeys(0.16f, snap: true));

        var keys = m.Clip.GetBone("Hips")!.PosX.Keys;
        Assert.Equal(0.2f, keys[0].Position, 4);
        Assert.Equal(1.2f, keys[1].Position, 4);
        Assert.Equal(2, m.SelectedKeys.Count);
    }

    [Fact]
    public void RemoveSelectedKeys_DeletesAll()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0, 0f);
        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 1, 1f, additive: true);

        Assert.True(m.RemoveSelectedKeys());
        Assert.Empty(m.Clip!.GetBone("Hips")!.PosX.Keys);
        Assert.False(m.HasKeyframeSelection);
    }

    [Fact]
    public void FillVisibleRows_CollapsesBoneAndExpandsComponents()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        var rows = new System.Collections.Generic.List<AnimationClipEditorModel.PropertyRow>();

        m.FillVisibleRows(rows);
        Assert.Equal(4, rows.Count); // Hips + Pos/Rot/Scale
        Assert.Equal(AnimationClipEditorModel.PropertyRowKind.Bone, rows[0].Kind);

        m.ToggleBoneExpanded("Hips");
        m.FillVisibleRows(rows);
        Assert.Single(rows);

        m.ToggleBoneExpanded("Hips");
        m.ToggleComponentsVisible("Hips", AnimationClipEditorModel.Channel.Position);
        m.FillVisibleRows(rows);
        Assert.True(rows.Count > 4);
        Assert.Contains(rows, r => r.Kind == AnimationClipEditorModel.PropertyRowKind.Component && r.Label == "X");
    }

    [Fact]
    public void FillRowKeyTimes_SkipsKeysOutsideView()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        m.AddKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 5f);
        var rows = new System.Collections.Generic.List<AnimationClipEditorModel.PropertyRow>();
        m.FillVisibleRows(rows);
        var pos = rows.Find(r =>
            r.Kind == AnimationClipEditorModel.PropertyRowKind.Channel
            && r.Channel == AnimationClipEditorModel.Channel.Position);
        var dest = new System.Collections.Generic.List<float>();
        m.FillRowKeyTimes(pos, dest, 0f, 1.1f);
        Assert.Contains(dest, t => AnimationClipEditorModel.Near(t, 0f));
        Assert.Contains(dest, t => AnimationClipEditorModel.Near(t, 1f));
        Assert.DoesNotContain(dest, t => t > 2f);
        m.FillRowKeyTimes(pos, dest);
        Assert.Contains(dest, t => AnimationClipEditorModel.Near(t, 5f));
    }

    [Fact]
    public void MarqueeSelect_PicksKeysInTimeAndRowRange()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        var rows = new System.Collections.Generic.List<AnimationClipEditorModel.PropertyRow>();
        m.FillVisibleRows(rows);

        int posRow = rows.FindIndex(r =>
            r.Kind == AnimationClipEditorModel.PropertyRowKind.Channel
            && r.Channel == AnimationClipEditorModel.Channel.Position);
        m.MarqueeSelect(rows, posRow, posRow, -0.1f, 1.1f, additive: false);

        Assert.Equal(2, m.SelectedKeys.Count);
    }

    [Fact]
    public void Window_SplitterClampsAndSerializes()
    {
        var w = new XEngine.Editor.GUI.Panels.AnimationClipWindow();
        w.SidebarWidth = 10f;
        Assert.Equal(120f, w.SidebarWidth);
        w.CurveSplit = 2f;
        Assert.Equal(0.85f, w.CurveSplit);

        var json = new System.Text.Json.Nodes.JsonObject();
        Assert.True(w.SerializeState(json));
        var restored = new XEngine.Editor.GUI.Panels.AnimationClipWindow();
        restored.RestoreState(json);
        Assert.Equal(120f, restored.SidebarWidth);
        Assert.Equal(0.85f, restored.CurveSplit);
    }

    [Fact]
    public void Bind_SetsRootAndClip_SetClipKeepsRoot()
    {
        var go = new GameObject("Rig");
        var clip = MakeTwoKeyClip();
        var m = new AnimationClipEditorModel();
        m.Bind(go, clip);

        Assert.Same(go, m.Root);
        Assert.Same(clip, m.Clip);

        var other = new AnimationClip { Name = "other", Duration = 2f };
        m.SetClip(other);
        Assert.Same(go, m.Root);
        Assert.Same(other, m.Clip);
        Assert.Contains(other, m.AvailableClips);
    }

    [Fact]
    public void NeedsCreatePrompt_WhenRootHasNoAnimationComponent()
    {
        var go = new GameObject("Cube");
        var m = new AnimationClipEditorModel();
        m.Root = go;
        Assert.True(m.NeedsCreatePrompt);

        go.AddComponent<Animator>();
        Assert.False(m.NeedsCreatePrompt);
    }

    [Fact]
    public void ClipIsReadOnly_ForModelPath_NotStandaloneAnim()
    {
        var m = new AnimationClipEditorModel();
        var imported = new AnimationClip { Name = "Take", AssetPath = "Models/Hero.glb" };
        imported.AssetID = Guid.NewGuid();
        m.SetClip(imported);
        Assert.True(m.ClipIsReadOnly);

        var standalone = new AnimationClip { Name = "Walk", AssetPath = "Anims/Walk.anim" };
        m.SetClip(standalone);
        Assert.False(m.ClipIsReadOnly);
    }

    [Fact]
    public void PropertyCatalog_FindsDirectionalLightIntensity()
    {
        var go = new GameObject("Lamp");
        go.AddComponent<DirectionalLight>();
        var dest = new System.Collections.Generic.List<AnimationPropertyCatalog.Entry>();
        AnimationPropertyCatalog.Collect(go, null, "Intensity", dest);
        Assert.Contains(dest, e => e.Property == "Intensity" && e.TypeName.Contains("DirectionalLight"));
    }

    [Fact]
    public void AddGenericProperty_AppearsInVisibleRows()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(new AnimationClip { Name = "Lit", Duration = 1f });
        Assert.True(m.AddGenericProperty("", typeof(DirectionalLight).FullName!, "Intensity", 0f));
        var rows = new System.Collections.Generic.List<AnimationClipEditorModel.PropertyRow>();
        m.FillVisibleRows(rows);
        Assert.Contains(rows, r => r.Kind == AnimationClipEditorModel.PropertyRowKind.Generic
            && r.Property == "Intensity");
    }

    [Fact]
    public void PrevNextKey_AndAddKeyframeAtTime_AndSampleRate()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip()); // keys at 0 and 1
        Assert.True(m.TryFindNextKeyTime(0f, out float next));
        Assert.Equal(1f, next);
        Assert.True(m.TryFindPrevKeyTime(1f, out float prev));
        Assert.Equal(0f, prev);
        Assert.False(m.TryFindPrevKeyTime(0f, out _));

        Assert.True(m.TrySetTicksPerSecond(30f));
        Assert.True(m.AddKeyframeAtTime(0.5f));
        Assert.True(m.TryFindNextKeyTime(0f, out next));
        Assert.Equal(0.5f, next, 3);

        Assert.True(m.TrySetTicksPerSecond(60f));
        Assert.Equal(60f, m.Clip!.TicksPerSecond);
        Assert.Equal(60f, m.FrameRate);
    }

    [Fact]
    public void ApplyTangentPreset_SetsModesAndTangents()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0, 0f, 0);

        Assert.True(m.ApplyTangentPreset("Flat"));
        Assert.True(m.TryGetSelectedCurve(out var curve, out int idx));
        Assert.Equal(0, idx);
        Assert.Equal(0f, curve.Keys[0].TangentOut);
        Assert.Equal(AnimationTangentMode.Free, curve.Keys[0].OutTangentMode);

        Assert.True(m.SetSelectedSideTangentMode(false, AnimationTangentMode.Linear));
        Assert.Equal(AnimationTangentMode.Linear, curve.Keys[0].OutTangentMode);
        Assert.Equal(10f, curve.Keys[0].TangentOut);

        Assert.True(m.SetSelectedWeighted(incoming: false, on: true));
        Assert.Equal(AnimationWeightedMode.Out, curve.Keys[0].WeightedMode);
    }

    [Fact]
    public void CopyPasteKeys_LaysBlockDownAtPasteTime_PreservingOffsets()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip()); // PosX keys at 0 and 1; PosY/PosZ key at 0 only.
        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0, 0f);
        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 1, 1f, additive: true);

        Assert.Equal(2, m.CopySelectedKeys());
        Assert.Equal(2, m.PasteKeys(2f));

        var bone = m.Clip!.GetBone("Hips")!;
        Assert.Equal(4, bone.PosX!.Keys.Count); // 0, 1, 2, 3
        Assert.True(AnimationClipEditorModel.IndexOfKeyNear(bone.PosX, 2f) >= 0);
        Assert.True(AnimationClipEditorModel.IndexOfKeyNear(bone.PosX, 3f) >= 0);
        // PosY had no key at t=1, so the second pasted column only lands on PosX.
        Assert.Equal(2, bone.PosY!.Keys.Count); // 0, 2
        Assert.True(AnimationClipEditorModel.IndexOfKeyNear(bone.PosY, 2f) >= 0);
        // Values travel with the paste.
        int x3 = AnimationClipEditorModel.IndexOfKeyNear(bone.PosX, 3f);
        Assert.Equal(10f, bone.PosX.Keys[x3].Value, 3);
        // Duration expands to cover the pasted block; pasted keys become the selection.
        Assert.True(m.Clip.Duration >= 3f);
        Assert.Equal(2, m.SelectedKeys.Count);
    }

    [Fact]
    public void PasteKeys_ReplacesExistingKeyAtTargetTime_AndPreservesTangents()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        var bone = m.Clip!.GetBone("Hips")!;
        bone.PosX!.Keys[1].TangentIn = 7f;
        bone.PosX.Keys[1].InTangentMode = AnimationTangentMode.Free;

        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 1, 1f);
        Assert.Equal(1, m.CopySelectedKeys());
        // Paste onto t=0 where keys already exist: replaced, not duplicated.
        Assert.Equal(1, m.PasteKeys(0f));

        Assert.Equal(2, bone.PosX.Keys.Count);
        int k0 = AnimationClipEditorModel.IndexOfKeyNear(bone.PosX, 0f);
        Assert.Equal(10f, bone.PosX.Keys[k0].Value, 3);
        Assert.Equal(7f, bone.PosX.Keys[k0].TangentIn, 3);
        Assert.Equal(AnimationTangentMode.Free, bone.PosX.Keys[k0].InTangentMode);
    }

    [Fact]
    public void CopyPasteKeys_GenericTrack_RoundTrips()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        Assert.True(m.AddGenericProperty("Hips", "Demo.Comp", "Alpha", 0.5f));
        var g = m.Clip!.FindGenericCurve("Hips", "Demo.Comp", "Alpha")!;
        g.Curve!.Keys[0].Value = 0.75f;

        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Generic, 0, 0.5f, -1, false, "Demo.Comp", "Alpha");
        Assert.Equal(1, m.CopySelectedKeys());
        Assert.Equal(1, m.PasteKeys(2f));

        Assert.Equal(2, g.Curve.Keys.Count);
        int idx = AnimationClipEditorModel.IndexOfKeyNear(g.Curve, 2f);
        Assert.True(idx >= 0);
        Assert.Equal(0.75f, g.Curve.Keys[idx].Value, 3);
    }

    [Fact]
    public void FrameSelection_FramesSelectedKeyRangeWithMargin()
    {
        var m = new AnimationClipEditorModel();
        m.SetClip(MakeTwoKeyClip());
        m.ViewStartTime = -3f;
        m.ViewEndTime = 8f;
        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0, 0f);
        m.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 1, 1f, additive: true);

        Assert.True(m.FrameSelection());
        Assert.Equal(-0.1f, m.ViewStartTime, 3);
        Assert.Equal(1.1f, m.ViewEndTime, 3);
    }
}
