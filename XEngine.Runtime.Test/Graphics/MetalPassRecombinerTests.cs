// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Backends.Metal;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// M4: CPU-only pending load-op recombination. Universal Pipeline rebinds the same
/// framebuffer after Clear; that must not drop the pending Metal load-op.
/// </summary>
public class MetalPassRecombinerTests
{
    [Fact]
    public void RebindSameTarget_KeepsPendingClear()
    {
        var recombiner = new MetalPassRecombiner();
        var framebuffer = GraphicsFrameBuffer.CreateDeferred(
            [new GraphicsFrameBuffer.Attachment { Texture = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b) }],
            4,
            4);

        Assert.Equal(MetalPassRecombiner.BindAction.SwitchAndReset, recombiner.Bind(framebuffer, passOpen: false));
        recombiner.ApplyBind(framebuffer, MetalPassRecombiner.BindAction.SwitchAndReset);
        recombiner.RecordClear(ClearFlags.Color, 0.125f, 0.375f, 0.625f, 1f, 1f, 0);

        Assert.Equal(MetalPassRecombiner.BindAction.KeepPending, recombiner.Bind(framebuffer, passOpen: false));
        Assert.True(recombiner.ColorLoadClear);
        Assert.Equal(0.125f, recombiner.ColorR);
        Assert.Same(framebuffer, recombiner.PendingTarget);
    }

    [Fact]
    public void SwitchTarget_WithPendingClear_FlushesFirst()
    {
        var recombiner = new MetalPassRecombiner();
        var first = GraphicsFrameBuffer.CreateDeferred(
            [new GraphicsFrameBuffer.Attachment { Texture = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b) }],
            4,
            4);
        var second = GraphicsFrameBuffer.CreateDeferred(
            [new GraphicsFrameBuffer.Attachment { Texture = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b) }],
            4,
            4);

        recombiner.ApplyBind(first, MetalPassRecombiner.BindAction.SwitchAndReset);
        recombiner.RecordClear(ClearFlags.Color, 1f, 0f, 1f, 1f, 1f, 0);

        Assert.Equal(MetalPassRecombiner.BindAction.FlushThenSwitch, recombiner.Bind(second, passOpen: false));
    }
}
