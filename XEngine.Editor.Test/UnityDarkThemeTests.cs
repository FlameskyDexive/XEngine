using System.Drawing;

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>Editor chrome is a single locked Unity 6 Professional Dark look.</summary>
public sealed class UnityDarkThemeTests
{
    [Fact]
    public void OrigamiDefaultsMatchUnityProfessionalDark()
    {
        var t = OrigamiTheme.CreateDefaults();

        Assert.Equal(ColorTranslator.FromHtml("#383838"), t.Neutral.C400);
        Assert.Equal(ColorTranslator.FromHtml("#454545"), t.Neutral.C300);
        Assert.Equal(ColorTranslator.FromHtml("#191919"), t.BorderSoft);
        Assert.Equal(ColorTranslator.FromHtml("#D4D4D4"), t.Ink.C500);
        Assert.Equal(ColorTranslator.FromHtml("#2C5D87"), t.Primary.C500);
        Assert.Equal(ColorTranslator.FromHtml("#2C5D87"), t.Selected);
        // Zebra + tab chrome re-sampled off Unity 6000.7.0a4 (2026-09 parity pass, N2):
        // low-contrast dark zebra, near-black tab strip with the active tab as the
        // brightest surface on it.
        Assert.Equal(ColorTranslator.FromHtml("#3C3C3C"), t.RowAlt);
        Assert.Equal(ColorTranslator.FromHtml("#2A2A2A"), t.RowEven);
        Assert.Equal(ColorTranslator.FromHtml("#141414"), t.TabStrip);
        Assert.Equal(ColorTranslator.FromHtml("#3C3C3C"), t.TabActive);
        Assert.Equal(ColorTranslator.FromHtml("#282828"), t.Field);
        Assert.Equal(ColorTranslator.FromHtml("#454545"), t.Glass);
        Assert.Equal(ColorTranslator.FromHtml("#4A4A4A"), EditorTheme.ToolbarActive);
        // Unity 6.7 draws a light OS caption + menu bar (#FFFFFF sampled) over the dark body.
        Assert.Equal(Color.FromArgb(255, 255, 255, 255), EditorTheme.MenuBar);
        Assert.Equal(Color.FromArgb(255, 32, 32, 32), EditorTheme.MenuBarInk);
        Assert.Equal(Color.FromArgb(255, 64, 64, 64), EditorTheme.MenuBarInkMuted);
        Assert.Equal(22f, EditorTheme.MenuBarHeight);
        Assert.Equal(28f, EditorTheme.ToolbarHeight);
    }

    [Fact]
    public void SceneViewDefaultsToShowSkyboxBackground()
    {
        Assert.True(new XEngine.Editor.GUI.SceneView.SceneViewSettings().ShowBackground);
    }

    [Fact]
    public void LauncherWindowMatchesCardSize()
    {
        Assert.Equal(940, XEngine.Editor.GUI.ProjectLauncher.NativeWindowWidth);
        Assert.Equal(620, XEngine.Editor.GUI.ProjectLauncher.NativeWindowHeight);
    }

    [Fact]
    public void ApplyThemeIgnoresPersistedCustomRamps()
    {
        var settings = new EditorSettings
        {
            Theme = new EditorThemeData { Neutral = { Primary = "#FF00FF", OverrideAll = false } },
        };

        settings.ApplyTheme();

        Assert.Equal("#383838", settings.Theme.Neutral.Primary);
        Assert.Equal(ColorTranslator.FromHtml("#383838"), EditorTheme.OrigamiTheme.Neutral.C400);
        Assert.False(EditorTheme.AnimatedBackground);
        Assert.Equal(EditorBackgroundStyle.Color, EditorTheme.BackgroundStyle);
    }
}
