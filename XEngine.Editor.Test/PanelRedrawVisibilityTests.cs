using XEngine.Editor.Core;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public class PanelRedrawVisibilityTests
{
    [Fact]
    public void HiddenProfilerTabDoesNotScheduleItsHost()
    {
        var profiler = new Panel();
        var other = new Panel();
        var leaf = DockNode.Leaf(other, profiler);
        leaf.ActiveTabIndex = 0;
        Assert.False(EditorSurfaceHost.IsPanelVisible(leaf, profiler));
        leaf.ActiveTabIndex = 1;
        Assert.True(EditorSurfaceHost.IsPanelVisible(leaf, profiler));
        Assert.False(EditorSurfaceHost.IsPanelVisible(leaf, new Panel()));
        Assert.False(EditorSurfaceHost.IsPanelVisible(null, profiler));
    }

    [Fact]
    public void HasVisiblePanelFollowsTheActiveTabType()
    {
        var profiler = new Panel();
        var other = new Panel();
        var leaf = DockNode.Leaf(other, profiler);
        leaf.ActiveTabIndex = 0;
        var host = new EditorSurfaceHost(1, new DockSpace(leaf), new Rect(0, 0, 200, 200));
        Assert.True(host.HasVisiblePanel(typeof(Panel)));
        Assert.True(EditorSurfaceHost.HasVisiblePanel(leaf, typeof(Panel)));
        Assert.False(EditorSurfaceHost.HasVisiblePanel(leaf, typeof(string)));
        Assert.False(EditorSurfaceHost.HasVisiblePanel(null, typeof(Panel)));
    }

    private sealed class Panel : DockPanel
    {
        public override string Title => "test";
        public override void OnGUI(Paper paper, float width, float height) { }
    }
}
