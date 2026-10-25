// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Core;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorWindowChromeTests
{
    [Fact]
    public void CaptionMatchesMenuBarLightChrome()
    {
        // The client erase brush is the dark editor body; the DWM caption is the light
        // menu-bar strip (EditorTheme.MenuBar #FFFFFF) so the native title bar reads as
        // one continuous light chrome with the menu row below it — Unity 6.7's arrangement.
        Assert.Equal(unchecked((int)0x00383838), EditorWindowChrome.ClientEraseColor);
        Assert.Equal(unchecked((int)0x00FFFFFF), EditorWindowChrome.CaptionColor);
        Assert.Equal(unchecked((int)0x00202020), EditorWindowChrome.CaptionTextColor);
    }
}
