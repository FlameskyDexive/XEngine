// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI.Panels;
using XEngine.Runtime;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>
/// M27: Animator canvas interaction baseline — clipboard, node titles, fan-out, splitters.
/// </summary>
public class AnimatorGraphClipboardTests
{
    private static AnimatorController MakeTwoStateController()
    {
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.Parameters.Add(new AnimatorController.Parameter { Name = "Speed", Type = AnimatorParameterType.Float });
        var idle = new AnimatorController.State { Name = "Idle", Speed = 1f, IsLooping = true, PosX = 0, PosY = 0 };
        var walk = new AnimatorController.State { Name = "Walk", Speed = 1.5f, IsLooping = true, PosX = 180, PosY = 40 };
        var t = new AnimatorController.Transition
        {
            Name = "Idle->Walk",
            DestinationStateName = "Walk",
            Duration = 0.2f,
            Conditions =
            {
                new AnimatorController.Condition { Mode = ConditionMode.Greater, ParamName = "Speed", Threshold = 0.1f }
            }
        };
        idle.TransitionNames.Add(t.Name);
        c.States.Add(idle);
        c.States.Add(walk);
        c.Transitions.Add(t);
        return c;
    }

    [Fact]
    public void FormatStateTitle_MarksLoopAndNonDefaultSpeed()
    {
        var looping = new AnimatorController.State { Name = "Idle", IsLooping = true, Speed = 1f };
        Assert.Contains("Idle", AnimatorGraphClipboard.FormatStateTitle(looping));
        Assert.Contains("\u21bb", AnimatorGraphClipboard.FormatStateTitle(looping));

        var fast = new AnimatorController.State { Name = "Run", IsLooping = false, Speed = 2f };
        string title = AnimatorGraphClipboard.FormatStateTitle(fast);
        Assert.DoesNotContain("\u21bb", title);
        Assert.Contains("\u00d7", title);
        Assert.Contains("2", title);
    }

    [Fact]
    public void FanControlPoint_SpreadsParallelEdges()
    {
        Float2 from = new(0, 0);
        Float2 to = new(100, 0);
        Assert.False(AnimatorGraphClipboard.TryFanControlPoint(from, to, 0, 1, out _));

        Assert.True(AnimatorGraphClipboard.TryFanControlPoint(from, to, 0, 3, out Float2 a));
        Assert.True(AnimatorGraphClipboard.TryFanControlPoint(from, to, 1, 3, out Float2 b));
        Assert.True(AnimatorGraphClipboard.TryFanControlPoint(from, to, 2, 3, out Float2 c));
        Assert.True(a.Y < b.Y || a.Y > b.Y);
        Assert.NotEqual(a.Y, c.Y);
        Assert.Equal(50f, b.X, 1);
    }

    [Fact]
    public void CopyPaste_DuplicatesStatesAndInternalTransitions()
    {
        var c = MakeTwoStateController();
        string text = AnimatorGraphClipboard.CopyToText(c, 0, new[] { "Idle", "Walk" });
        Assert.StartsWith(AnimatorGraphClipboard.Header, text);

        var names = AnimatorGraphClipboard.Paste(c, 0, text);
        Assert.Equal(2, names.Count);
        Assert.Equal(4, c.States.Count);
        Assert.Contains("Idle 2", names);
        Assert.Contains("Walk 2", names);

        var idle2 = AnimatorControllerEditGraph.FindState(c, 0, "Idle 2");
        Assert.NotNull(idle2);
        Assert.Single(idle2!.TransitionNames);
        var t = AnimatorControllerEditGraph.FindTransition(c, 0, idle2.TransitionNames[0]);
        Assert.NotNull(t);
        Assert.Equal("Walk 2", t!.DestinationStateName);
        Assert.Single(t.Conditions);
        Assert.Equal("Speed", t.Conditions[0].ParamName);
        Assert.Equal(40f, idle2.PosX);
    }

    [Fact]
    public void Paste_AcrossController_DropsMissingParameterConditions()
    {
        var src = MakeTwoStateController();
        string text = AnimatorGraphClipboard.CopyToText(src, 0, new[] { "Idle", "Walk" });

        var dest = new AnimatorController { DefaultStateName = "Empty" };
        dest.States.Add(new AnimatorController.State { Name = "Empty" });
        var names = AnimatorGraphClipboard.Paste(dest, 0, text);
        Assert.Equal(2, names.Count);

        var idle2 = AnimatorControllerEditGraph.FindState(dest, 0, "Idle");
        Assert.NotNull(idle2);
        Assert.Single(idle2!.TransitionNames);
        var t = AnimatorControllerEditGraph.FindTransition(dest, 0, idle2.TransitionNames[0]);
        Assert.NotNull(t);
        Assert.Empty(t!.Conditions);
    }

    [Fact]
    public void Window_CopyDuplicate_AndSplitterPersist()
    {
        var c = MakeTwoStateController();
        var window = new AnimatorWindow();
        window.SetController(c);
        window.SelectStates("Idle", "Walk");
        Assert.Equal(2, window.SelectedStateNames.Count);

        var dup = window.DuplicateSelection();
        Assert.Equal(2, dup.Count);
        Assert.Equal(4, c.States.Count);

        window.SidebarWidth = 50f;
        Assert.Equal(120f, window.SidebarWidth);
        window.InspectorHeight = 999f;
        Assert.Equal(360f, window.InspectorHeight);

        var json = new System.Text.Json.Nodes.JsonObject();
        Assert.True(window.SerializeState(json));
        Assert.Equal(120f, json["sidebarWidth"]!.GetValue<float>());

        var restored = new AnimatorWindow();
        restored.RestoreState(json);
        Assert.Equal(120f, restored.SidebarWidth);
    }
}
