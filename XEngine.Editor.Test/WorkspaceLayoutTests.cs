using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class WorkspaceLayoutTests
{
    [Theory]
    [InlineData(1725, 1000)]
    [InlineData(1100, 700)]
    public void DefaultWorkspaceKeepsAuthoringPanelsReachableAtDifferentSizes(int width, int height)
    {
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(EditorApplication.CreateDefaultLayout()), new Rect(0, 0, width, height)));

        DockNode hierarchy = LeafAt(workspace, width * 0.1f, height * 0.2f);
        DockNode viewport = LeafAt(workspace, width * 0.5f, height * 0.2f);
        DockNode bottom = LeafAt(workspace, width * 0.5f, height * 0.8f);
        DockNode inspector = LeafAt(workspace, width * 0.88f, height * 0.2f);

        Assert.IsType<HierarchyPanel>(Assert.Single(hierarchy.Tabs!));
        Assert.Collection(viewport.Tabs!,
            panel => Assert.IsType<SceneViewPanel>(panel),
            panel => Assert.IsType<GameViewPanel>(panel));
        Assert.Collection(bottom.Tabs!,
            panel => Assert.IsType<ProjectPanel>(panel),
            panel => Assert.IsType<ConsolePanel>(panel));
        Assert.IsType<InspectorPanel>(Assert.Single(inspector.Tabs!));
        Assert.Same(inspector, LeafAt(workspace, width * 0.88f, height * 0.8f));
        Assert.Same(bottom, LeafAt(workspace, width * 0.1f, height * 0.8f));
    }

    [Fact]
    public void SavedDefaultWorkspacePreservesUserSplitRatiosAndActiveTabs()
    {
        DockNode root = EditorApplication.CreateDefaultLayout();
        root.SplitRatio = 0.68f;
        root.ChildA!.SplitRatio = 0.65f;
        root.ChildA.ChildA!.SplitRatio = 0.3f;
        root.ChildA.ChildA.ChildB!.ActiveTabIndex = 1;
        root.ChildA.ChildB!.ActiveTabIndex = 1;

        DockNode? restored = DockSerializer.Deserialize(DockSerializer.Serialize(new DockSpace(root)),
            new List<FloatingWindow>(), EditorApplication.CreatePanelFromLayout);

        Assert.NotNull(restored);
        Assert.Equal(0.68f, restored.SplitRatio);
        Assert.Equal(0.65f, restored.ChildA!.SplitRatio);
        Assert.Equal(0.3f, restored.ChildA.ChildA!.SplitRatio);
        Assert.IsType<GameViewPanel>(restored.ChildA.ChildA.ChildB!.Tabs![
            restored.ChildA.ChildA.ChildB.ActiveTabIndex]);
        Assert.IsType<ConsolePanel>(restored.ChildA.ChildB!.Tabs![restored.ChildA.ChildB.ActiveTabIndex]);
    }

    private static DockNode LeafAt(EditorWorkspace workspace, float x, float y)
    {
        DockDropCandidate candidate = workspace.FindDropCandidate(new Float2(x, y));
        Assert.True(candidate.IsValid);
        Assert.NotNull(candidate.TargetLeaf);
        return candidate.TargetLeaf;
    }

    [Fact]
    public void LayoutRoundTripsHostBoundsAndDockPayload()
    {
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(7,
            new DockSpace(DockNode.Leaf(new TestPanel())), new Rect(10, 20, 650, 500)));
        EditorWorkspaceLayout source = EditorWorkspaceLayout.Capture(workspace);

        Assert.True(EditorWorkspaceLayout.TryParse(source.ToJson(), out EditorWorkspaceLayout? loaded));
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Hosts);
        Assert.Equal(7, loaded.Hosts[0].Id);
        Assert.Equal(10f, loaded.Hosts[0].LogicalBounds.Min.X);
        Assert.Contains("floatingWindows", loaded.Hosts[0].DockLayoutJson);
    }

    [Fact]
    public void LayoutRoundTripsMonitorIdentityAndDpiScale()
    {
        var source = new EditorWorkspaceLayout();
        source.Hosts.Add(new EditorSurfaceLayout
        {
            Id = 3,
            IsMain = false,
            MonitorId = "DISPLAY\\MONITOR3",
            DpiScale = 1.5f,
            LogicalBounds = new Rect(100, 120, 700, 520),
            DockLayoutJson = DockSerializer.Serialize(new DockSpace(DockNode.Leaf(new TestPanel()))),
        });

        Assert.True(EditorWorkspaceLayout.TryParse(source.ToJson(), out EditorWorkspaceLayout? loaded));
        Assert.NotNull(loaded);
        Assert.Equal("DISPLAY\\MONITOR3", loaded!.Hosts[0].MonitorId);
        Assert.Equal(1.5f, loaded.Hosts[0].DpiScale);
    }

    [Fact]
    public void MalformedOrUnsupportedLayoutFallsBackWithoutThrowing()
    {
        Assert.False(EditorWorkspaceLayout.TryParse("{\"version\":1}", out _));
        Assert.False(EditorWorkspaceLayout.TryParse("not-json", out _));
    }

    [Fact]
    public void SaveAtomicPromotesCompletePayload()
    {
        string path = Path.Combine(Path.GetTempPath(), "xengine-workspace-" + Guid.NewGuid().ToString("N"));
        try
        {
            new EditorWorkspaceLayout().SaveAtomic(path);
            Assert.True(EditorWorkspaceLayout.TryLoad(path, out EditorWorkspaceLayout? loaded));
            Assert.NotNull(loaded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MissingPanelPlaceholderKeepsUnavailableTypeVisible()
    {
        var panel = new MissingDockPanel("Example.MissingPanel");

        Assert.Equal("Example.MissingPanel", panel.MissingTypeName);
        Assert.Contains("Missing", panel.Title);
    }

    [Fact]
    public void LayoutPanelFactoryCreatesPlaceholderForUnknownType()
    {
        DockPanel? panel = EditorApplication.CreatePanelFromLayout("Example.MissingPanel", null);

        Assert.IsType<MissingDockPanel>(panel);
        Assert.Equal("Example.MissingPanel", ((MissingDockPanel)panel!).MissingTypeName);
    }

    [Fact]
    public void LayoutPanelFactoryCanReloadSavedPlaceholderType()
    {
        DockPanel? panel = EditorApplication.CreatePanelFromLayout(
            typeof(MissingDockPanel).FullName!, null);

        Assert.IsType<MissingDockPanel>(panel);
    }

    [Fact]
    public void ClampToDesktopRepairsOutOfBoundsMonitorPlacement()
    {
        var layout = new EditorWorkspaceLayout();
        layout.Hosts.Add(new EditorSurfaceLayout
        {
            Id = 1,
            LogicalBounds = new Rect(-200, -100, 1600, 1200),
        });

        layout.ClampToDesktop(new Rect(0, 0, 800, 600));

        Rect bounds = layout.Hosts[0].LogicalBounds;
        Assert.Equal(0f, bounds.Min.X);
        Assert.Equal(0f, bounds.Min.Y);
        Assert.Equal(800f, bounds.Max.X);
        Assert.Equal(600f, bounds.Max.Y);
    }

    [Fact]
    public void CaptureKeepsMainHostIdentityAfterZOrderChanges()
    {
        var workspace = new EditorWorkspace();
        workspace.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(DockNode.Leaf(new TestPanel())), new Rect(0, 0, 200, 200)));
        workspace.RegisterHost(new EditorSurfaceHost(1,
            new DockSpace(DockNode.Leaf(new TestPanel())), new Rect(250, 0, 450, 200)));

        workspace.BringToFront(0);
        EditorWorkspaceLayout layout = EditorWorkspaceLayout.Capture(workspace);
        bool mainMarked = false;
        bool floatingMarked = false;
        for (int i = 0; i < layout.Hosts.Count; i++)
        {
            if (layout.Hosts[i].Id == 0) mainMarked = layout.Hosts[i].IsMain;
            if (layout.Hosts[i].Id == 1) floatingMarked = layout.Hosts[i].IsMain;
        }

        Assert.True(mainMarked);
        Assert.False(floatingMarked);
    }

    [Fact]
    public void DetachedHostTitleTracksItsActivePanel()
    {
        var first = new TestPanel();
        var second = new NamedTestPanel("Second");
        DockNode leaf = DockNode.Leaf(first, second);
        var host = new EditorSurfaceHost(1, new DockSpace(leaf), new Rect(0, 0, 200, 200));

        Assert.Equal("Test", host.WindowTitle);
        leaf.ActiveTabIndex = 1;
        Assert.Equal("Second", host.WindowTitle);
    }

    [Fact]
    public void ReplacingPrimaryDockSpaceImmediatelyRefreshesOwnership()
    {
        var oldPanel = new NamedTestPanel("Old");
        var newPanel = new NamedTestPanel("New");
        var workspace = new EditorWorkspace();
        var host = new EditorSurfaceHost(0, new DockSpace(DockNode.Leaf(oldPanel)),
            new Rect(0, 0, 200, 200));
        workspace.RegisterHost(host);

        var replacement = new DockSpace(DockNode.Leaf(newPanel));
        Assert.True(workspace.TryReplaceDockSpace(0, replacement));

        Assert.Same(replacement, host.DockSpace);
        Assert.Null(workspace.FindOwner(oldPanel));
        Assert.Same(host, workspace.FindOwner(newPanel));
    }

    [Fact]
    public void RestoreSecondaryHostsRecreatesDockTreeAndZOrder()
    {
        var source = new EditorWorkspace();
        source.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(DockNode.Leaf(new MissingDockPanel("Main.Panel"))), new Rect(0, 0, 200, 200)));
        source.RegisterHost(new EditorSurfaceHost(4,
            new DockSpace(DockNode.Leaf(new MissingDockPanel("Float.Panel"))), new Rect(250, 0, 450, 200)));
        EditorWorkspaceLayout layout = EditorWorkspaceLayout.Capture(source);

        var target = new EditorWorkspace();
        target.RegisterHost(new EditorSurfaceHost(0,
            new DockSpace(DockNode.Leaf(new MissingDockPanel("Main.Panel"))), new Rect(0, 0, 200, 200)));
        Assert.Equal(1, layout.RestoreSecondaryHosts(target,
            (typeName, _) => new MissingDockPanel(typeName)));

        EditorSurfaceHost? restored = target.FindHost(4);
        Assert.NotNull(restored);
        Assert.Equal(new Rect(250, 0, 450, 200), restored!.ScreenBounds);
        Assert.Same(restored, target.FindOwner(restored.DockSpace.Root.Tabs![0]));
        Assert.Equal(4, target.Hosts[^1].Id);
    }

    private sealed class TestPanel : DockPanel
    {
        public override string Title => "Test";
        public override void OnGUI(Paper paper, float width, float height) { }
    }

    private sealed class NamedTestPanel : DockPanel
    {
        private readonly string _title;
        public NamedTestPanel(string title) => _title = title;
        public override string Title => _title;
        public override void OnGUI(Paper paper, float width, float height) { }
    }
}
