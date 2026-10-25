// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>
/// M17-perf: NodeGraph dictionaries and PropertyGrid field ids are reused across frames.
/// </summary>
public class AnimationEditorPerfTests
{
    [Fact]
    public void DopesheetDiamondsGenerateLinearGeometry()
    {
        var draw = typeof(XEngine.Editor.GUI.Panels.AnimationClipWindow)
            .GetMethod("DrawDiamond", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Action<Canvas, float, float, float>>();
        var canvas = new Canvas(new NullRenderer(), new FontAtlasSettings { AtlasSize = 32 });
        try
        {
            draw(canvas, 10, 10, 3);
            int vertices = canvas.VertexCount, indices = canvas.IndexCount;
            Assert.True(vertices > 0 && indices > 0);
            for (int i = 1; i < 512; i++) draw(canvas, 10 + i, 10, 3);
            Assert.Equal(vertices * 512, canvas.VertexCount);
            Assert.Equal(indices * 512, canvas.IndexCount);
        }
        finally { canvas.Dispose(); }
    }

    private sealed class ProbeTarget
    {
        public float Speed = 1f;
        public bool Loop = true;
        public string Name = "clip";
    }

    [Fact]
    public void InternFieldId_ReturnsTheSameInstance()
    {
        string a = PropertyGridRenderer.InternFieldId("insp_pg", "Speed");
        string b = PropertyGridRenderer.InternFieldId("insp_pg", "Speed");
        Assert.Same(a, b);
        Assert.Equal("insp_pg_Speed", a);
    }

    [Fact]
    public void PropertyGrid_WarmDraw_ReusesFieldIds()
    {
        var paper = NewPaper();
        var target = new ProbeTarget();
        var config = new PropertyGridConfig();

        DrawGrid(paper, target, config);
        string first = PropertyGridRenderer.InternFieldId("perf_pg", "Speed");
        DrawGrid(paper, target, config);
        string second = PropertyGridRenderer.InternFieldId("perf_pg", "Speed");
        Assert.Same(first, second);
    }

    [Fact]
    public void NodeGraph_TwentyNodes_ShowTwiceWithoutThrowing()
    {
        var paper = NewPaper();
        var nodes = new List<GraphNode>(20);
        var wires = new List<GraphConnection>();
        for (int i = 0; i < 20; i++)
        {
            var n = new GraphNode { Id = "s" + i, Title = "State " + i, Position = new Float2(i * 24f, (i % 4) * 48f) };
            n.Inputs.Add(new GraphPort("in", "In"));
            n.Outputs.Add(new GraphPort("out", "Out"));
            nodes.Add(n);
            if (i > 0)
                wires.Add(new GraphConnection(nodes[i - 1].Id, "out", n.Id, "in"));
        }

        var ctrl = new NodeGraphController();
        ShowGraph(paper, nodes, wires, ctrl);
        ShowGraph(paper, nodes, wires, ctrl);

        Assert.Equal(1f, ctrl.Zoom);
    }

    [Fact]
    public void NodeGraph_WarmFrame_DoesNotAllocateLookupTables()
    {
        var paper = NewPaper();
        var nodes = new List<GraphNode>(20);
        for (int i = 0; i < 20; i++)
            nodes.Add(new GraphNode { Id = "s" + i, Title = "S" + i, Position = new Float2(i * 16f, 0) });
        var wires = Array.Empty<GraphConnection>();

        ShowGraph(paper, nodes, wires, null);
        ShowGraph(paper, nodes, wires, null);

        long before = GC.GetAllocatedBytesForCurrentThread();
        ShowGraph(paper, nodes, wires, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Paint-path Dictionary/ToList are gone. Remaining bytes are Paper string ids / box chrome.
        // A pre-fix 20-node Show allocated several 20-entry dictionaries plus ToList copies (~4KB+).
        Assert.True(allocated < 64_000, $"NodeGraph warm frame allocated {allocated} bytes.");
    }

    [Fact]
    public void BalancedProfileKeepsBackdropBlurOff()
    {
        Assert.Equal(XEngine.Editor.Theming.BackdropBlurMode.Off,
            new XEngine.Editor.Theming.EditorPerformanceSettings().BackdropBlurMode);
        Assert.False(new XEngine.Editor.Theming.EditorPerformanceSettings().DropShadows);
    }

    private static void DrawGrid(Paper paper, object target, PropertyGridConfig config)
    {
        paper.BeginFrame(0.016f);
        Origami.BeginFrame(paper, 0.016f);
        Origami.PropertyGrid(paper, "perf_pg", target, config).Show();
        Origami.EndFrame(paper);
        paper.EndFrame();
    }

    private static void ShowGraph(Paper paper, IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphConnection> wires, NodeGraphController? ctrl)
    {
        paper.BeginFrame(0.016f);
        Origami.BeginFrame(paper, 0.016f);
        var g = Origami.NodeGraph(paper, "perf_graph", 640, 400).Nodes(nodes).Connections(wires).Grid(true);
        if (ctrl != null) g.Controller(ctrl);
        g.Show();
        Origami.EndFrame(paper);
        paper.EndFrame();
    }

    private static Paper NewPaper() => new(new NullRenderer(), 640, 400, new FontAtlasSettings());

    private sealed class NullRenderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
