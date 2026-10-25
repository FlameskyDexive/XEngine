// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Canvas;

public sealed class ScreenSpaceCanvasProjectionTests
{
    [Fact]
    public void BuildScreenOrtho_Uses_Presented_Target_Y_Orientation()
    {
        Float4x4 projection = UniversalRenderPipeline.BuildScreenOrtho(1920, 1080);

        // The game target is later presented through a Vulkan blit, so Canvas layout's +Y axis
        // needs the opposite clip-space slope to remain visually upward in the final image.
        Assert.Equal(-2f / 1080f, projection.c1.Y, 6);
        Assert.Equal(1f, projection.c3.Y, 6);
    }
}
