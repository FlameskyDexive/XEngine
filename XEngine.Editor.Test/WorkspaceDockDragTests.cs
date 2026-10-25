using XEngine.Editor.Core;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class WorkspaceDockDragTests
{
    [Fact]
    public void WorkspaceRejectsDuplicatePanelOwnership()
    {
        var panel = new TestPanel("Panel");
        var unique = new TestPanel("Unique");
        var workspace = new EditorWorkspace();

        Assert.True(workspace.RegisterHost(new EditorSurfaceHost(1,
            new DockSpace(DockNode.Leaf(panel)), new Rect(0, 0, 200, 200))));
        Assert.False(workspace.RegisterHost(new EditorSurfaceHost(2,
            new DockSpace(DockNode.Leaf(unique, panel)), new Rect(200, 0, 400, 200))));
        Assert.True(workspace.RegisterHost(new EditorSurfaceHost(3,
            new DockSpace(DockNode.Leaf(unique)), new Rect(400, 0, 600, 200))));
    }

    [Fact]
    public void CandidateUsesTopmostHostAndEdgeZone()
    {
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(1, new DockSpace(DockNode.Leaf(new TestPanel("A"))), new Rect(0, 0, 200, 200)));
        workspace.RegisterHost(new EditorSurfaceHost(2, new DockSpace(DockNode.Leaf(new TestPanel("B"))), new Rect(100, 0, 300, 200)));

        DockDropCandidate candidate = workspace.FindDropCandidate(new Float2(105, 100));

        Assert.True(candidate.IsValid);
        Assert.Equal(2, candidate.HostId);
        Assert.Equal(DockDropZone.Left, candidate.Zone);
    }

    [Fact]
    public void CandidateUsesNativeClientBoundsInsteadOfPersistedWindowBounds()
    {
        var workspace = new EditorWorkspace();
        var host = new EditorSurfaceHost(1, new DockSpace(DockNode.Leaf(new TestPanel("Panel"))),
            new Rect(100, 100, 500, 400));
        host.SetDockingScreenBounds(new Rect(108, 132, 508, 432));
        Assert.True(workspace.RegisterHost(host));

        Assert.False(workspace.FindDropCandidate(new Float2(104, 120)).IsValid);
        Assert.True(workspace.FindDropCandidate(new Float2(112, 136)).IsValid);
        Assert.Equal(new Rect(100, 100, 500, 400), host.ScreenBounds);
    }

    [Fact]
    public void DragThresholdAndCancelLeaveDockTreeUntouched()
    {
        var panel = new TestPanel("Panel");
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(1, new DockSpace(DockNode.Leaf(panel)), new Rect(0, 0, 200, 200)));
        var drag = new DockDragController(workspace);

        Assert.True(drag.BeginDrag(panel, new Float2(10, 10)));
        Assert.False(drag.UpdateDrag(new Float2(14, 10)));
        drag.CancelDrag();

        Assert.False(drag.IsDragging);
        Assert.Same(panel, workspace.FindHost(1)!.DockSpace.Root.Tabs![0]);
    }

    [Fact]
    public void DragOutsideHostsCommitsAsFloatingCandidate()
    {
        var panel = new TestPanel("Panel");
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(1, new DockSpace(DockNode.Leaf(panel)), new Rect(0, 0, 200, 200)));
        var drag = new DockDragController(workspace);

        drag.BeginDrag(panel, new Float2(10, 10));
        Assert.True(drag.UpdateDrag(new Float2(250, 250)));
        DockDragResult result = drag.CommitDrag();

        Assert.Equal(DockDragOutcome.Floating, result.Outcome);
        Assert.Same(panel, result.Panel);
        Assert.Same(panel, workspace.FindOwner(panel)!.DockSpace.Root.Tabs![0]);
        Assert.Equal(2, workspace.Hosts.Count);
    }

    [Fact]
    public void CrossHostTabCommitTransfersPanelOwnership()
    {
        var panel = new TestPanel("Panel");
        var targetPanel = new TestPanel("Target");
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(1, new DockSpace(DockNode.Leaf(panel)), new Rect(0, 0, 150, 150)));
        workspace.RegisterHost(new EditorSurfaceHost(2, new DockSpace(DockNode.Leaf(targetPanel)), new Rect(200, 0, 350, 150)));
        var drag = new DockDragController(workspace);

        drag.BeginDrag(panel, new Float2(10, 10));
        Assert.True(drag.UpdateDrag(new Float2(275, 75)));
        DockDragResult result = drag.CommitDrag();

        Assert.Equal(DockDragOutcome.Docked, result.Outcome);
        Assert.Same(workspace.FindHost(2), workspace.FindOwner(panel));
        Assert.Contains(panel, workspace.FindHost(2)!.DockSpace.Root.Tabs!);
    }

    [Fact]
    public void CandidateTargetsTheLeafUnderThePointerAndSplitsThatLeaf()
    {
        var sourcePanel = new TestPanel("Source");
        var leftPanel = new TestPanel("Left");
        var rightPanel = new TestPanel("Right");
        DockNode rightLeaf = DockNode.Leaf(rightPanel);
        DockNode root = DockNode.Split(SplitDirection.Horizontal, 0.5f,
            DockNode.Leaf(leftPanel), rightLeaf);
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(0, new DockSpace(root), new Rect(0, 0, 400, 300)));
        workspace.RegisterHost(new EditorSurfaceHost(1,
            new DockSpace(DockNode.Leaf(sourcePanel)), new Rect(500, 0, 700, 300)));
        var drag = new DockDragController(workspace);

        drag.BeginDrag(sourcePanel, new Float2(600, 150));
        Assert.True(drag.UpdateDrag(new Float2(210, 150)));
        Assert.Same(rightLeaf, drag.CurrentCandidate.TargetLeaf);
        Assert.Equal(DockDropZone.Left, drag.CurrentCandidate.Zone);

        Assert.Equal(DockDragOutcome.Docked, drag.CommitDrag().Outcome);
        Assert.False(root.ChildB!.IsLeaf);
        Assert.Same(workspace.FindHost(0), workspace.FindOwner(sourcePanel));
    }

    [Fact]
    public void DraggingWithinTheSameHostCanSplitItsSourceLeaf()
    {
        var sourcePanel = new TestPanel("Source");
        var existingPanel = new TestPanel("Existing");
        DockNode root = DockNode.Leaf(sourcePanel, existingPanel);
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(0, new DockSpace(root), new Rect(0, 0, 200, 200)));
        var drag = new DockDragController(workspace);

        drag.BeginDrag(sourcePanel, new Float2(100, 100));
        Assert.True(drag.UpdateDrag(new Float2(10, 100)));
        Assert.Equal(DockDropZone.Left, drag.CurrentCandidate.Zone);
        Assert.Equal(DockDragOutcome.Docked, drag.CommitDrag().Outcome);

        Assert.False(workspace.FindHost(0)!.DockSpace.Root.IsLeaf);
        Assert.Same(workspace.FindHost(0), workspace.FindOwner(sourcePanel));
    }

    [Fact]
    public void DockingLastPanelLeavesDetachedHostEmptyForNativeCleanup()
    {
        var panel = new TestPanel("Panel");
        var targetPanel = new TestPanel("Target");
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(DockNode.Leaf(targetPanel)), new Rect(200, 0, 400, 150)));
        workspace.RegisterHost(new EditorSurfaceHost(1,
            new DockSpace(DockNode.Leaf(panel)), new Rect(0, 0, 150, 150)));
        var drag = new DockDragController(workspace);

        drag.BeginDrag(panel, new Float2(10, 75));
        Assert.True(drag.UpdateDrag(new Float2(275, 75)));
        DockDragResult result = drag.CommitDrag();

        Assert.Equal(DockDragOutcome.Docked, result.Outcome);
        Assert.False(workspace.FindHost(1)!.HasPanels);
        Assert.True(workspace.UnregisterHost(1));
        Assert.Single(workspace.Hosts);
    }

    [Fact]
    public void ClosingFloatingHostDestroysPanelsAndDoesNotRedock()
    {
        var panel = new TrackingPanel("Panel");
        var mainPanel = new TestPanel("Main");
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(DockNode.Leaf(mainPanel)), new Rect(0, 0, 200, 200)));
        workspace.RegisterHost(new EditorSurfaceHost(2,
            new DockSpace(DockNode.Leaf(panel)), new Rect(250, 0, 450, 200)));

        Assert.True(workspace.TryDestroyHost(2, out int destroyed));
        Assert.Equal(1, destroyed);
        Assert.True(panel.Closed);
        Assert.Single(workspace.Hosts);
        Assert.Null(workspace.FindOwner(panel));
        Assert.DoesNotContain(panel, workspace.FindHost(0)!.DockSpace.Root.Tabs!);
    }

    [Fact]
    public void DestroyingMultiTabFloatingHostClosesEveryPanel()
    {
        var first = new TrackingPanel("First");
        var second = new TrackingPanel("Second");
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(DockNode.Leaf(new TestPanel("Main"))), new Rect(0, 0, 200, 200)));
        workspace.RegisterHost(new EditorSurfaceHost(1,
            new DockSpace(DockNode.Leaf(first, second)), new Rect(250, 0, 450, 200)));

        Assert.True(workspace.TryDestroyHost(1, out int destroyed));
        Assert.Equal(2, destroyed);
        Assert.True(first.Closed);
        Assert.True(second.Closed);
        Assert.Single(workspace.Hosts);
        Assert.Null(workspace.FindOwner(first));
        Assert.Null(workspace.FindOwner(second));
    }

    [Fact]
    public void SalvageCloseStillTransfersPanelsToMainHost()
    {
        var panel = new TestPanel("Panel");
        var mainPanel = new TestPanel("Main");
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(DockNode.Leaf(mainPanel)), new Rect(0, 0, 200, 200)));
        workspace.RegisterHost(new EditorSurfaceHost(2,
            new DockSpace(DockNode.Leaf(panel)), new Rect(250, 0, 450, 200)));

        Assert.True(workspace.TryCloseHost(2, 0, out int transferred));
        Assert.Equal(1, transferred);
        Assert.Single(workspace.Hosts);
        Assert.Same(workspace.FindHost(0), workspace.FindOwner(panel));
        Assert.Contains(panel, workspace.FindHost(0)!.DockSpace.Root.Tabs!);
    }

    [Fact]
    public void MainHostCannotBeClosed()
    {
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(DockNode.Leaf(new TestPanel("Main"))), new Rect(0, 0, 200, 200)));

        Assert.False(workspace.TryCloseHost(0, 0, out int transferred));
        Assert.Equal(0, transferred);
        Assert.False(workspace.TryDestroyHost(0, out int destroyed));
        Assert.Equal(0, destroyed);
        Assert.Single(workspace.Hosts);
    }

    [Fact]
    public void FailedNativeHostCreationRestoresTheSourcePanel()
    {
        var panel = new TestPanel("Panel");
        var workspace = new EditorWorkspace();
        Assert.True(workspace.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(DockNode.Leaf(panel)), new Rect(0, 0, 200, 200))));
        workspace.TryCreateHost = _ => false;

        Assert.False(workspace.TryFloatPanel(panel, new Float2(300, 300), out EditorSurfaceHost? host));

        Assert.Null(host);
        Assert.Single(workspace.Hosts);
        Assert.Same(workspace.FindHost(0), workspace.FindOwner(panel));
        Assert.Contains(panel, workspace.FindHost(0)!.DockSpace.Root.Tabs!);
    }

    [Fact]
    public void DestroyingHostReleasesNativeBindingWithoutRedocking()
    {
        var floatingPanel = new TrackingPanel("Floating");
        var workspace = new EditorWorkspace();
        int releases = 0;
        workspace.TryCreateHost = _ => true;
        workspace.ReleaseHost = _ => releases++;
        Assert.True(workspace.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(DockNode.Leaf(new TestPanel("Main"))), new Rect(0, 0, 200, 200))));
        Assert.True(workspace.RegisterHost(new EditorSurfaceHost(1,
            new DockSpace(DockNode.Leaf(floatingPanel)), new Rect(250, 0, 450, 200))));

        Assert.True(workspace.TryDestroyHost(1, out int destroyed));

        Assert.Equal(1, destroyed);
        Assert.Equal(1, releases);
        Assert.True(floatingPanel.Closed);
        Assert.Null(workspace.FindOwner(floatingPanel));
        Assert.DoesNotContain(floatingPanel, workspace.FindHost(0)!.DockSpace.Root.Tabs!);
    }

    [Fact]
    public void LegacyFloatingWindowsMigrateToIndependentHosts()
    {
        var panel = new TestPanel("Legacy");
        var dock = new DockSpace(DockNode.Leaf(new TestPanel("Main")));
        dock.FloatingWindows.Add(new FloatingWindow(DockNode.Leaf(panel), new Float2(20, 30), new Float2(400, 300)));
        var workspace = new EditorWorkspace();
        Assert.True(workspace.RegisterHost(new EditorSurfaceHost(0, dock, new Rect(100, 200, 900, 800))));

        Assert.Equal(1, workspace.MigrateLegacyFloatingWindows(0));

        Assert.Empty(dock.FloatingWindows);
        Assert.Equal(2, workspace.Hosts.Count);
        EditorSurfaceHost detached = workspace.FindOwner(panel)!;
        Assert.Equal(new Rect(120, 230, 520, 530), detached.ScreenBounds);
        Assert.NotEqual(0, detached.Id);
    }

    [Fact]
    public void NewDetachedPanelFallsBackToMainHostWhenNativeCreationFails()
    {
        var panel = new TestPanel("Panel");
        var workspace = new EditorWorkspace { TryCreateHost = _ => true };
        Assert.True(workspace.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(DockNode.Leaf(new TestPanel("Main"))), new Rect(0, 0, 200, 200))));
        workspace.TryCreateHost = _ => false;

        Assert.False(workspace.TryCreateDetachedHost(panel, new Rect(300, 300, 700, 600), out _));
        Assert.True(workspace.TryAddPanelToHost(panel, 0));

        Assert.Single(workspace.Hosts);
        Assert.Same(workspace.FindHost(0), workspace.FindOwner(panel));
    }

    [Fact]
    public void EmptySecondaryHostCanBeUnregisteredWithoutRedock()
    {
        // Last tab X leaves an empty floater; prune path UnregisterHost without TryCloseHost.
        var workspace = new EditorWorkspace();
        int releases = 0;
        workspace.ReleaseHost = _ => releases++;
        Assert.True(workspace.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(DockNode.Leaf(new TestPanel("Main"))), new Rect(0, 0, 200, 200))));
        Assert.True(workspace.RegisterHost(new EditorSurfaceHost(1,
            new DockSpace(DockNode.Leaf()), new Rect(250, 0, 450, 200))));

        Assert.False(workspace.FindHost(1)!.HasPanels);
        Assert.True(workspace.UnregisterHost(1));
        Assert.Equal(1, releases);
        Assert.Single(workspace.Hosts);
        Assert.Single(workspace.FindHost(0)!.DockSpace.Root.Tabs!);
    }

    private sealed class TestPanel : DockPanel
    {
        public TestPanel(string title) => Title = title;
        public override string Title { get; }
        public override void OnGUI(Paper paper, float width, float height) { }
    }

    private sealed class TrackingPanel : DockPanel
    {
        public TrackingPanel(string title) => Title = title;
        public override string Title { get; }
        public bool Closed { get; private set; }
        public override void OnGUI(Paper paper, float width, float height) { }
        public override void OnClosed() => Closed = true;
    }
}
