using System;
using System.Reflection;

using XEngine.Editor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorShellIdleHostsTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void FlagOffBroadcastsEveryReasonToEveryHost()
    {
        using var scope = ShellFlag(false);
        var host = HostWith(new DummyPanel());
        Assert.True(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.Selection));
        Assert.True(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.Layout));
        Assert.True(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.AssetData));
        Assert.True(EditorShellIdleHosts.SecondaryIsInteractive(false, false, true));
    }

    [Fact]
    public void FlagOnKeepsThemeWindowAndExplicitOnIdleHosts()
    {
        using var scope = ShellFlag(true);
        var host = HostWith(new DummyPanel());
        Assert.True(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.Theme));
        Assert.True(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.Window));
        Assert.True(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.ExplicitRepaint));
        Assert.True(EditorShellIdleHosts.HostWantsReason(host,
            EditorSurfaceDirtyReason.Layout | EditorSurfaceDirtyReason.Theme));
    }

    [Fact]
    public void FlagOnDropsLayoutInputAndUnrelatedModelFromIdleHosts()
    {
        using var scope = ShellFlag(true);
        var host = HostWith(new DummyPanel());
        Assert.False(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.Layout));
        Assert.False(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.Input));
        Assert.False(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.Selection));
        Assert.False(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.SceneCamera));
        Assert.False(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.GameView));
        Assert.False(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.AssetData));
        Assert.False(EditorShellIdleHosts.HostWantsReason(host, EditorSurfaceDirtyReason.Animation));
    }

    [Fact]
    public void SelectionWakesOnlyTheVisibleInspectorHost()
    {
        using var scope = ShellFlag(true);
        var inspector = new InspectorPanel();
        var other = new DummyPanel();
        try
        {
            var hidden = HostWith(other, inspector, active: 0);
            var shown = HostWith(other, inspector, active: 1);
            Assert.False(hidden.HasVisiblePanel(typeof(InspectorPanel)));
            Assert.True(shown.HasVisiblePanel(typeof(InspectorPanel)));
            Assert.False(EditorShellIdleHosts.HostWantsReason(hidden, EditorSurfaceDirtyReason.Selection));
            Assert.True(EditorShellIdleHosts.HostWantsReason(shown, EditorSurfaceDirtyReason.Selection));
        }
        finally
        {
            inspector.OnClosed();
        }
    }

    [Fact]
    public void SceneGameAndProjectReasonsFollowTheVisiblePanel()
    {
        using var scope = ShellFlag(true);
        var scene = new SceneViewPanel();
        var game = new GameViewPanel();
        var project = new ProjectPanel();
        var dummy = new DummyPanel();
        try
        {
            var sceneHost = HostWith(scene);
            var gameHost = HostWith(game);
            var projectHost = HostWith(project);
            var idle = HostWith(dummy);
            Assert.True(EditorShellIdleHosts.HostWantsReason(sceneHost, EditorSurfaceDirtyReason.SceneCamera));
            Assert.False(EditorShellIdleHosts.HostWantsReason(idle, EditorSurfaceDirtyReason.SceneCamera));
            Assert.True(EditorShellIdleHosts.HostWantsReason(gameHost, EditorSurfaceDirtyReason.GameView));
            Assert.False(EditorShellIdleHosts.HostWantsReason(idle, EditorSurfaceDirtyReason.GameView));
            Assert.True(EditorShellIdleHosts.HostWantsReason(projectHost, EditorSurfaceDirtyReason.AssetData));
            Assert.False(EditorShellIdleHosts.HostWantsReason(idle, EditorSurfaceDirtyReason.AssetData));
        }
        finally
        {
            scene.OnClosed();
            project.OnClosed();
        }
    }

    [Fact]
    public void AnimationWakesOnlyTheVisibleProfilerHost()
    {
        using var scope = ShellFlag(true);
        var profiler = new ProfilerPanel();
        var other = new DummyPanel();
        try
        {
            var hidden = HostWith(other, profiler, active: 0);
            var shown = HostWith(other, profiler, active: 1);
            Assert.False(hidden.HasVisiblePanel(typeof(ProfilerPanel)));
            Assert.True(shown.HasVisiblePanel(typeof(ProfilerPanel)));
            Assert.False(EditorShellIdleHosts.HostWantsReason(hidden, EditorSurfaceDirtyReason.Animation));
            Assert.True(EditorShellIdleHosts.HostWantsReason(shown, EditorSurfaceDirtyReason.Animation));
            Assert.False(EditorShellIdleHosts.HostWantsReason(HostWith(other), EditorSurfaceDirtyReason.Animation));
        }
        finally
        {
            profiler.OnClosed();
        }
    }

    [Fact]
    public void AnimationWakesPlayingAnimationHostOnly()
    {
        using var scope = ShellFlag(true);
        var window = new AnimationClipWindow();
        var dummy = new DummyPanel();
        try
        {
            AnimationClipWindow.WantsContinuousPresent = false;
            var shown = HostWith(window);
            Assert.False(EditorShellIdleHosts.HostWantsReason(shown, EditorSurfaceDirtyReason.Animation));
            AnimationClipWindow.WantsContinuousPresent = true;
            Assert.True(EditorShellIdleHosts.HostWantsReason(shown, EditorSurfaceDirtyReason.Animation));
            Assert.False(EditorShellIdleHosts.HostWantsReason(HostWith(dummy, window, active: 0),
                EditorSurfaceDirtyReason.Animation));
        }
        finally
        {
            AnimationClipWindow.WantsContinuousPresent = false;
            window.OnClosed();
        }
    }

    [Fact]
    public void ShouldPumpHostFollowsVisibleLiveProfiler()
    {
        var profiler = new ProfilerPanel();
        var dummy = new DummyPanel();
        try
        {
            ProfilerPanel.WantsContinuousPresent = true;
            var live = HostWith(profiler);
            var idleTab = HostWith(dummy, profiler, active: 0);
            Assert.True(EditorShellIdleHosts.ShouldPumpHost(live));
            Assert.False(EditorShellIdleHosts.ShouldPumpHost(idleTab));
            ProfilerPanel.WantsContinuousPresent = false;
            Assert.False(EditorShellIdleHosts.ShouldPumpHost(live));
        }
        finally
        {
            ProfilerPanel.WantsContinuousPresent = false;
            profiler.OnClosed();
        }
    }

    [Fact]
    public void ShouldPumpHostFollowsVisibleLiveAnimation()
    {
        var window = new AnimationClipWindow();
        var dummy = new DummyPanel();
        try
        {
            AnimationClipWindow.WantsContinuousPresent = true;
            var live = HostWith(window);
            var idleTab = HostWith(dummy, window, active: 0);
            Assert.True(EditorShellIdleHosts.ShouldPumpHost(live));
            Assert.False(EditorShellIdleHosts.ShouldPumpHost(idleTab));
            AnimationClipWindow.WantsContinuousPresent = false;
            Assert.False(EditorShellIdleHosts.ShouldPumpHost(live));
        }
        finally
        {
            AnimationClipWindow.WantsContinuousPresent = false;
            window.OnClosed();
        }
    }

    [Fact]
    public void ShouldPumpHostFollowsVisibleLiveAnimator()
    {
        var window = new AnimatorWindow();
        var dummy = new DummyPanel();
        try
        {
            AnimatorWindow.WantsContinuousPresent = true;
            var live = HostWith(window);
            var idleTab = HostWith(dummy, window, active: 0);
            Assert.True(EditorShellIdleHosts.ShouldPumpHost(live));
            Assert.False(EditorShellIdleHosts.ShouldPumpHost(idleTab));
            AnimatorWindow.WantsContinuousPresent = false;
            Assert.False(EditorShellIdleHosts.ShouldPumpHost(live));
        }
        finally
        {
            AnimatorWindow.WantsContinuousPresent = false;
            window.OnClosed();
        }
    }

    [Fact]
    public void RegisteredLiveToolWakesThatHost()
    {
        using var scope = ShellFlag(true);
        bool wants = true;
        EditorShellIdleHosts.RegisterLiveTool(typeof(DummyPanel), () => wants);
        try
        {
            var live = HostWith(new DummyPanel());
            Assert.True(EditorShellIdleHosts.HostWantsReason(live, EditorSurfaceDirtyReason.Animation));
            Assert.True(EditorShellIdleHosts.ShouldPumpHost(live));
            wants = false;
            Assert.False(EditorShellIdleHosts.ShouldPumpHost(live));
        }
        finally
        {
            EditorShellIdleHosts.UnregisterLiveTool(typeof(DummyPanel));
        }
    }

    [Fact]
    public void ModalNoLongerMarksEverySecondaryInteractive()
    {
        using var scope = ShellFlag(true);
        Assert.False(EditorShellIdleHosts.SecondaryIsInteractive(false, false, true));
        Assert.True(EditorShellIdleHosts.SecondaryIsInteractive(true, false, false));
        Assert.True(EditorShellIdleHosts.SecondaryIsInteractive(false, true, false));
    }

    private static EditorSurfaceHost HostWith(params DockPanel[] tabs) => HostWith(active: 0, tabs);

    private static EditorSurfaceHost HostWith(DockPanel first, DockPanel second, int active)
        => HostWith(active, first, second);

    private static EditorSurfaceHost HostWith(int active, params DockPanel[] tabs)
    {
        var leaf = DockNode.Leaf(tabs);
        leaf.ActiveTabIndex = active;
        return new EditorSurfaceHost(1, new DockSpace(leaf), new Rect(0, 0, 320, 240));
    }

    private static FlagScope ShellFlag(bool on)
    {
        var flag = typeof(Program).GetProperty(nameof(Program.ShellPaintIncremental), Any)!;
        return new FlagScope(flag, on);
    }

    private sealed class FlagScope : IDisposable
    {
        private readonly PropertyInfo _flag;
        private readonly bool _previous;

        public FlagScope(PropertyInfo flag, bool value)
        {
            _flag = flag;
            _previous = (bool)flag.GetValue(null)!;
            flag.SetValue(null, value);
        }

        public void Dispose() => _flag.SetValue(null, _previous);
    }

    private sealed class DummyPanel : DockPanel
    {
        public override string Title => "dummy";
        public override void OnGUI(Paper paper, float width, float height) { }
    }
}
