namespace XEngine.UiPrototype.Tests;

public class PrototypeInputTests
{
    [Fact]
    public void TimedReplayDoesNotRepeatTextOrDropDelayedButtonEdges()
    {
        var replay = new TimedInputReplay();
        for (int i = 0; i <= 36; i++) replay.Advance(i / 60d + .000001, 800, 480);
        Assert.Equal(36, replay.CurrentTick);
        Assert.Equal("", replay.Advance(36 / 60d + .001, 800, 480).Text);
        Assert.False(replay.Advanced);
        Assert.Equal(0, replay.Advance(36 / 60d + .002, 800, 480).Wheel);
        var delayed = new TimedInputReplay();
        for (int i = 0; i < 11; i++) delayed.Advance(i / 60d + .000001, 800, 480);
        Assert.True(delayed.Advance(1, 800, 480).LeftDown); // tick 11 press retained
        Assert.False(delayed.Advance(1.001, 800, 480).LeftDown); // tick 12 release retained
        Assert.True(delayed.MaximumBacklogTicks > 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => delayed.Advance(.5, 800, 480));
    }

    [Fact]
    public void TextSelectionReplacementAndSurrogateDeletion()
    {
        var editor = new PrototypeTextEditor(); editor.SetText("Hello 😀");
        editor.Apply(PrototypeKeys.Backspace, PrototypeKeys.None, "");
        Assert.Equal("Hello ", editor.Text);
        editor.Apply(PrototypeKeys.A, PrototypeKeys.Control, "");
        editor.Apply(PrototypeKeys.None, PrototypeKeys.None, "测试");
        Assert.Equal("测试", editor.Text);
        editor.Apply(PrototypeKeys.Left, PrototypeKeys.Shift, "");
        editor.Apply(PrototypeKeys.None, PrototypeKeys.None, "X");
        Assert.Equal("测X", editor.Text);
    }

    [Fact]
    public void ScrollClampsAndSplitterKeepsCaptureOutsideItsBounds()
    {
        var model = new PrototypeWorkload(new TreeDocument(["a", "b"], [-1, -1]), new RowDocument(["p"]));
        var input = new PrototypeViewportInput();
        input.Apply(new PrototypeInput(20, 40, Wheel: -100), 800, 200, model);
        Assert.Equal(0, model.TreeScroll); // all rows fit
        input.Apply(new PrototypeInput(402, 40, true), 800, 200, model);
        Assert.True(input.Dragging);
        input.Apply(new PrototypeInput(600, 500, true), 800, 200, model);
        Assert.InRange(model.SplitRatio, .74, .76);
        input.Apply(new PrototypeInput(600, 500), 800, 200, model);
        Assert.False(input.Dragging);
    }

    [Fact]
    public void FieldEditingTargetsSelectedPropertyAndCanReturnToStandaloneInput()
    {
        var model = new PrototypeWorkload(new TreeDocument(["a"], [-1]), new RowDocument(["p"]));
        model.SelectProperty(0); model.EditField("changed");
        Assert.Equal("changed", model.Properties.GetText(0)); Assert.Equal("", model.InputText);
        model.SelectProperty(-1); model.EditField("standalone");
        Assert.Equal("standalone", model.InputText); Assert.Equal("changed", model.Properties.GetText(0));
    }
}
