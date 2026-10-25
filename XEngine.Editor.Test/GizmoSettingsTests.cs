// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.GUI.SceneView;
using XEngine.Runtime;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// The state behind the Gizmos window: the Fade Gizmos metric (a screen-size ramp evaluated against
/// the Scene view's own camera), the slider clamps, and the icon/gizmo pair that persists per type.
/// </summary>
public sealed class GizmoSettingsTests
{
    private static Func<Float3, float, float> InstalledFade()
    {
        GizmoSettings.Install();
        Assert.NotNull(Debug.GizmoFadeEvaluator);
        return Debug.GizmoFadeEvaluator!;
    }

    [Fact]
    public void Fade_OrthographicUsesAConstantPixelScale()
    {
        Func<Float3, float, float> fade = InstalledFade();
        bool prevOn = GizmoSettings.FadeGizmos;
        float prevSize = GizmoSettings.FadeSize;
        try
        {
            GizmoSettings.FadeGizmos = true;
            GizmoSettings.FadeSize = 40f;
            // 100 px per world unit: diameter in pixels is radius * 2 * 100.
            GizmoSettings.SetFadeReference(Float3.Zero, orthographic: true, 100f);

            // 0.5 units across => 100 px, well over the threshold.
            Assert.Equal(1f, fade(new Float3(0, 0, 0), 0.5f), 3);
            // 0.1 units => 20 px, at half the threshold => fully faded.
            Assert.Equal(0f, fade(new Float3(0, 0, 0), 0.1f), 3);
            // 0.15 units => 30 px, halfway up the ramp between 20 and 40.
            Assert.Equal(0.5f, fade(new Float3(0, 0, 0), 0.15f), 3);

            // Orthographic size does not change with distance.
            Assert.Equal(fade(new Float3(0, 0, 0), 0.15f), fade(new Float3(0, 0, 900f), 0.15f), 3);
        }
        finally
        {
            GizmoSettings.FadeGizmos = prevOn;
            GizmoSettings.FadeSize = prevSize;
        }
    }

    [Fact]
    public void Fade_PerspectiveShrinksWithDistance()
    {
        Func<Float3, float, float> fade = InstalledFade();
        bool prevOn = GizmoSettings.FadeGizmos;
        float prevSize = GizmoSettings.FadeSize;
        try
        {
            GizmoSettings.FadeGizmos = true;
            GizmoSettings.FadeSize = 40f;
            GizmoSettings.SetFadeReference(Float3.Zero, orthographic: false, 1000f);

            // radius 0.5 => 1000 px at 1 unit away, 10 px at 100 units away.
            Assert.Equal(1f, fade(new Float3(0, 0, 1f), 0.5f), 3);
            Assert.Equal(0f, fade(new Float3(0, 0, 100f), 0.5f), 3);
        }
        finally
        {
            GizmoSettings.FadeGizmos = prevOn;
            GizmoSettings.FadeSize = prevSize;
        }
    }

    [Fact]
    public void Fade_OffOrUnreferencedLeavesGizmosOpaque()
    {
        Func<Float3, float, float> fade = InstalledFade();
        bool prevOn = GizmoSettings.FadeGizmos;
        float prevSize = GizmoSettings.FadeSize;
        try
        {
            GizmoSettings.FadeSize = 40f;
            GizmoSettings.SetFadeReference(Float3.Zero, orthographic: true, 100f);

            GizmoSettings.FadeGizmos = false;
            Assert.Equal(1f, fade(Float3.Zero, 0.01f), 3);

            // No view has published a metric yet (headless, or before the first Scene view frame).
            GizmoSettings.FadeGizmos = true;
            GizmoSettings.SetFadeReference(Float3.Zero, orthographic: true, 0f);
            Assert.Equal(1f, fade(Float3.Zero, 0.01f), 3);
        }
        finally
        {
            GizmoSettings.FadeGizmos = prevOn;
            GizmoSettings.FadeSize = prevSize;
            GizmoSettings.SetFadeReference(Float3.Zero, orthographic: true, 100f);
        }
    }

    [Fact]
    public void Sliders_ClampToTheirDeclaredRange()
    {
        float prevIcon = GizmoSettings.IconScale;
        float prevScale = GizmoSettings.GizmoScale;
        float prevFade = GizmoSettings.FadeSize;
        try
        {
            GizmoSettings.IconScale = -5f;
            Assert.Equal(GizmoSettings.IconScaleMin, GizmoSettings.IconScale, 4);
            GizmoSettings.IconScale = 500f;
            Assert.Equal(GizmoSettings.IconScaleMax, GizmoSettings.IconScale, 4);

            GizmoSettings.GizmoScale = 0f;
            Assert.Equal(GizmoSettings.GizmoScaleMin, GizmoSettings.GizmoScale, 4);
            GizmoSettings.GizmoScale = 99f;
            Assert.Equal(GizmoSettings.GizmoScaleMax, GizmoSettings.GizmoScale, 4);

            GizmoSettings.FadeSize = -1f;
            Assert.Equal(GizmoSettings.FadeSizeMin, GizmoSettings.FadeSize, 4);
            GizmoSettings.FadeSize = 9999f;
            Assert.Equal(GizmoSettings.FadeSizeMax, GizmoSettings.FadeSize, 4);
        }
        finally
        {
            GizmoSettings.IconScale = prevIcon;
            GizmoSettings.GizmoScale = prevScale;
            GizmoSettings.FadeSize = prevFade;
        }
    }

    [Fact]
    public void IconAndGizmoColumns_AreIndependentAndSurviveARestore()
    {
        const string type = "GizmoSettingsTestsProbeComponent";

        // Unknown types default to visible so a component that has not drawn yet is never hidden.
        Assert.True(GizmoSettings.IsGizmoVisible(type));
        Assert.True(GizmoSettings.IsIconVisible(type));

        GizmoSettings.SetIconVisible(type, false);
        Assert.False(GizmoSettings.IsIconVisible(type));
        Assert.True(GizmoSettings.IsGizmoVisible(type));
        Assert.Contains(type, GizmoSettings.HiddenIconTypeNames());
        Assert.DoesNotContain(type, GizmoSettings.HiddenTypeNames());

        GizmoSettings.SetGizmoVisible(type, false);
        Assert.False(GizmoSettings.IsGizmoVisible(type));
        Assert.Contains(type, GizmoSettings.HiddenTypeNames());

        // A restored layout re-applies both lists.
        GizmoSettings.SetIconVisible(type, true);
        GizmoSettings.SetGizmoVisible(type, true);
        GizmoSettings.RestoreHiddenTypeNames([type]);
        GizmoSettings.RestoreHiddenIconTypeNames([type]);
        Assert.False(GizmoSettings.IsGizmoVisible(type));
        Assert.False(GizmoSettings.IsIconVisible(type));

        GizmoSettings.SetIconVisible(type, true);
        GizmoSettings.SetGizmoVisible(type, true);
    }
}
