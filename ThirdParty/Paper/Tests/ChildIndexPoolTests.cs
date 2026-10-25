// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Vector;

namespace Tests;

public class ChildIndexPoolTests
{
    [Fact]
    public void BeginFrame_ReusesChildIndexListOnTheSameSlot()
    {
        var paper = new Paper(new NullRenderer(), 64, 64, new FontAtlasSettings());

        paper.BeginFrame(0);
        var first = paper.Box("slot", 1, 1)._handle.Data.ChildIndices;
        paper.EndFrame();

        paper.BeginFrame(0);
        var second = paper.Box("slot", 1, 1)._handle.Data.ChildIndices;
        paper.EndFrame();

        Assert.Same(first, second);
    }

    [Fact]
    public void CreateElement_ReusesChildIndexListsForEveryWarmSlot()
    {
        var paper = new Paper(new NullRenderer(), 64, 64, new FontAtlasSettings());
        var first = new List<int>[32];

        paper.BeginFrame(0);
        for (int i = 0; i < 32; i++)
            first[i] = paper.Box("n" + i, i, 1)._handle.Data.ChildIndices;
        paper.EndFrame();

        paper.BeginFrame(0);
        for (int i = 0; i < 32; i++)
            Assert.Same(first[i], paper.Box("n" + i, i, 1)._handle.Data.ChildIndices);
        paper.EndFrame();
    }

    private sealed class NullRenderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
