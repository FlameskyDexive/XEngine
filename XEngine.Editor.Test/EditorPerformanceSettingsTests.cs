using System.Text.Json;

using XEngine.Editor.GUI;
using XEngine.Editor.Theming;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorPerformanceSettingsTests
{
    [Fact]
    public void NewSettings_DefaultToBalancedWithBackdropBlurOff()
    {
        EditorSettings settings = new();
        EditorPerformanceSettings performance = settings.EffectivePerformance;

        Assert.Equal(EditorPerformanceProfile.Balanced, performance.Profile);
        Assert.Equal(BackdropBlurMode.Off, performance.BackdropBlurMode);
        Assert.False(performance.AnimatedBackground);
        Assert.False(performance.DropShadows);
        Assert.True(performance.UiAntialiasing);
    }

    [Fact]
    public void LegacyThemeFieldsRemainReadableUntilPerformanceSettingsAreEdited()
    {
        EditorSettings settings = new()
        {
            Theme = new EditorThemeData { GlassBlur = true, AnimatedBackground = false, DropShadows = false, AntiAliasing = false },
        };

        EditorPerformanceSettings performance = settings.EffectivePerformance;

        Assert.Null(settings.Performance);
        Assert.Equal(EditorPerformanceProfile.Visual, performance.Profile);
        Assert.Equal(BackdropBlurMode.Off, performance.BackdropBlurMode);
        Assert.False(performance.AnimatedBackground);
        Assert.False(performance.DropShadows);
        Assert.False(performance.UiAntialiasing);
    }

    [Fact]
    public void ProfilePresetsAreExplicitAndRoundTripThroughJson()
    {
        EditorSettings settings = new();
        EditorPerformanceSettings performance = settings.EnsurePerformance();
        performance.ApplyProfileDefaults(EditorPerformanceProfile.Performance);

        string json = JsonSerializer.Serialize(settings);
        EditorSettings restored = JsonSerializer.Deserialize<EditorSettings>(json)!;

        Assert.NotNull(restored.Performance);
        Assert.Equal(EditorPerformanceProfile.Performance, restored.Performance!.Profile);
        Assert.Equal(BackdropBlurMode.Off, restored.Performance.BackdropBlurMode);
        Assert.False(restored.Performance.AnimatedBackground);
        Assert.False(restored.Performance.DropShadows);
        Assert.False(restored.Performance.UiAntialiasing);
    }

    [Fact]
    public void ApplyingPerformancePolicyUpdatesLiveThemeTokens()
    {
        EditorSettings settings = new();
        EditorPerformanceSettings performance = settings.EnsurePerformance();
        performance.Profile = EditorPerformanceProfile.Visual;
        performance.BackdropBlurMode = BackdropBlurMode.InteractiveOnly;
        performance.AnimatedBackground = false;
        performance.DropShadows = false;
        performance.UiAntialiasing = false;

        settings.ApplyTheme();

        Assert.Equal(EditorPerformanceProfile.Visual, EditorTheme.PerformanceProfile);
        Assert.Equal(BackdropBlurMode.Off, EditorTheme.BackdropBlurMode);
        Assert.False(EditorTheme.IsBackdropBlurActive(interactive: false));
        Assert.False(EditorTheme.IsBackdropBlurActive(interactive: true));
        Assert.False(EditorTheme.AnimatedBackground);
        Assert.False(EditorTheme.DropShadows);
        Assert.False(EditorTheme.AntiAliasing);
    }

    [Fact]
    public void BackgroundAnimationStopsWhenIdleOrInactiveAndResumesOnFocusOrInput()
    {
        var policy = new EditorBackgroundAnimationPolicy(idleTimeoutSeconds: 5f);

        Assert.True(policy.ShouldAdvance(true, true, false, false, 0f));
        Assert.True(policy.ShouldAdvance(true, true, false, false, 4.9f));
        Assert.False(policy.ShouldAdvance(true, true, false, false, 5f));

        Assert.True(policy.ShouldAdvance(true, true, false, true, 5.1f));
        Assert.False(policy.ShouldAdvance(true, false, false, false, 5.2f));
        Assert.True(policy.ShouldAdvance(true, true, false, false, 5.3f));

        Assert.False(policy.ShouldAdvance(true, true, true, false, 5.4f));
        Assert.True(policy.ShouldAdvance(true, true, false, false, 5.5f));
        Assert.False(policy.ShouldAdvance(false, true, false, true, 5.6f));
    }

    [Fact]
    public void BackgroundAnimationRecoversWhenTheTimeSourceResets()
    {
        var policy = new EditorBackgroundAnimationPolicy(idleTimeoutSeconds: 5f);

        Assert.True(policy.ShouldAdvance(true, true, false, false, 20f));
        Assert.True(policy.ShouldAdvance(true, true, false, false, 1f));
    }
}
