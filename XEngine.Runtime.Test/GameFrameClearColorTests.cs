// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class GameFrameClearColorTests
{
    [Fact]
    public void Editor_MatchesChromeGray()
    {
        Color c = Game.FrameClearColor(isEditor: true);
        const float gray = 56f / 255f;
        Assert.Equal(gray, c.R, 5);
        Assert.Equal(gray, c.G, 5);
        Assert.Equal(gray, c.B, 5);
        Assert.Equal(1f, c.A, 5);
    }

    [Fact]
    public void Player_IsBlack()
    {
        Color c = Game.FrameClearColor(isEditor: false);
        Assert.Equal(Color.Black, c);
    }
}
