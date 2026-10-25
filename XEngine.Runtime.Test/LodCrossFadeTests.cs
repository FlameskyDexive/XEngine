// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class LodCrossFadeTests
{
    [Fact]
    public void LodCrossFade_DitherAndStencil()
    {
        Assert.Equal(0f, LodCrossFade.FadeFactor(0.5f, 0.2f));
        Assert.Equal(1f, LodCrossFade.FadeFactor(1f, 0.2f));
        Assert.Equal(0.5f, LodCrossFade.FadeFactor(0.9f, 0.2f), 3);

        Assert.Equal(LodCrossFade.StencilBit0, LodCrossFade.StencilRef(0));
        Assert.Equal(LodCrossFade.StencilBit1, LodCrossFade.StencilRef(1));
        Assert.Equal(LodCrossFade.StencilMask, LodCrossFade.StencilBit0 | LodCrossFade.StencilBit1);

        Assert.False(LodCrossFade.DitherVisible(0f, 0, 0));
        Assert.True(LodCrossFade.DitherVisible(1f, 0, 0));

        var data = new UniversalRenderData { LodCrossFade = LodCrossFadeMode.Stencil };
        var clone = Serializer.Deserialize<UniversalRenderData>(Serializer.Serialize(typeof(object), data));
        Assert.Equal(LodCrossFadeMode.Stencil, clone!.LodCrossFade);

        data.LodCrossFade = (LodCrossFadeMode)9;
        data.OnValidate();
        Assert.Equal(LodCrossFadeMode.None, data.LodCrossFade);
    }
}
