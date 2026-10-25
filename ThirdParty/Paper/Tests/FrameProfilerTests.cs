using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Vector;

namespace Tests;

public class FrameProfilerTests
{
    [Fact]
    public void BoundedCaptureRetainsChronologicalSamplesAndReportsLoss()
    {
        var paper = NewPaper();
        var capture = new PaperFrameProfiler(2);
        paper.FrameProfiler = capture;
        for (int i = 0; i < 5; i++) Frame(paper);
        Assert.False(paper.DevTools.Enabled);
        Assert.Equal(5, capture.TotalFrames);
        Assert.Equal(3, capture.DroppedFrames);
        var samples = capture.CopySamples();
        Assert.Equal(new long[] { 4, 5 }, samples.Select(s => s.Sequence));
        foreach (var s in samples)
        {
            double phases = s.BuildMs + s.StylesMs + s.LayoutMs + s.PostLayoutMs + s.CullingMs
                + s.LayeredMs + s.InteractionMs + s.GeometryMs + s.RendererMs + s.CleanupMs;
            Assert.InRange(phases, 0, s.TotalMs + 1e-9); // Allow floating-point summation rounding.
            Assert.Equal(0, s.Elements); // The implicit root is excluded from Paper's public count.
            Assert.True(s.AllocatedBytes >= 0);
        }
    }

    [Fact]
    public void AttachmentIsLatchedAtFrameStart()
    {
        var paper = NewPaper();
        var capture = new PaperFrameProfiler(4);
        paper.BeginFrame(.016f);
        paper.FrameProfiler = capture;
        paper.EndFrame();
        Assert.Equal(0, capture.Count);
        paper.BeginFrame(.016f);
        paper.FrameProfiler = null;
        paper.EndFrame();
        Assert.Equal(1, capture.Count);
        Frame(paper);
        Assert.Equal(1, capture.Count);
    }

    [Fact]
    public void CaptureAddsNoSteadyStateManagedAllocation()
    {
        var paper = NewPaper();
        var capture = new PaperFrameProfiler(2);
        paper.FrameProfiler = capture;
        for (int i = 0; i < 200; i++) Frame(paper);
        long enabled = Measure(paper);
        paper.FrameProfiler = null;
        long disabled = Measure(paper);
        Assert.Equal(disabled, enabled);
    }

    private static long Measure(Paper paper)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Frame(paper);
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static void Frame(Paper paper) { paper.BeginFrame(.016f); paper.EndFrame(); }
    private static Paper NewPaper() => new(new Renderer(), 64, 64, new FontAtlasSettings());
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
