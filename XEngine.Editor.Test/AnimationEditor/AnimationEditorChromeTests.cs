// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Drawing;

using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;

using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>
/// M17-chrome: Unity 2022 Professional Dark tokens stay charcoal, not Origami indigo glass.
/// </summary>
public class AnimationEditorChromeTests
{
    [Fact]
    public void SurfacesMatchUnityProfessionalDark()
    {
        Assert.Equal(Color.FromArgb(255, 56, 56, 56), AnimationEditorChrome.Panel);
        Assert.Equal(Color.FromArgb(255, 42, 42, 42), AnimationEditorChrome.Canvas);
        Assert.Equal(Color.Black, AnimationEditorChrome.PreviewEmpty);
        Assert.Equal(Color.FromArgb(255, 44, 93, 135), AnimationEditorChrome.Selection);
        Assert.Equal(22f, AnimationEditorChrome.ToolbarHeight);
        Assert.Equal(18f, AnimationEditorChrome.RowHeight);
        Assert.Equal("List is Empty", AnimationEditorChrome.EmptyAnimator);
        Assert.Contains("No model is available for preview", AnimationEditorChrome.EmptyPreview);
    }

    [Fact]
    public void DefaultThemeDataUsesUnityDarkDensityAndOpaqueChrome()
    {
        var data = EditorThemeData.CreateDefault();
        Assert.Equal("Unity Dark", data.Name);
        // 12 -> 13 readability bump (2026-08): SDF-rendered Geist reads small next to native chrome.
        Assert.Equal(13f, data.FontSize);
        Assert.Equal(18f, data.RowHeight);
        Assert.Equal(1f, data.DockSpacing);
        Assert.Equal(22f, data.TabBarHeight);
        Assert.Equal(2f, data.Roundness);
        Assert.False(data.AccentGlow);
        Assert.False(data.AnimatedBackground);
        Assert.False(data.DropShadows);
        Assert.Equal("#383838", data.Neutral.Primary);

        var origami = OrigamiTheme.CreateDefaults();
        data.ApplyTo(origami);
        Assert.Equal(Color.FromArgb(255, 69, 69, 69), origami.Glass);
        Assert.Equal(Color.FromArgb(255, 40, 40, 40), origami.Field);
        Assert.Equal(Color.FromArgb(255, 60, 60, 60), origami.Popover);
        Assert.Equal(Color.FromArgb(255, 25, 25, 25), origami.BorderSoft);
        Assert.Equal(Color.FromArgb(255, 44, 93, 135), origami.Selected);
    }
}
