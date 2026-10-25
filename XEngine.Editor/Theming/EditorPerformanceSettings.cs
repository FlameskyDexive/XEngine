using System;

namespace XEngine.Editor.Theming;

/// <summary>Editor-wide rendering policy selected independently of the visual theme.</summary>
public enum EditorPerformanceProfile
{
    Performance,
    Balanced,
    Visual,
}

/// <summary>When editor backdrop blur may be recorded.</summary>
public enum BackdropBlurMode
{
    Off,
    InteractiveOnly,
    Always,
}

/// <summary>
/// Persisted editor performance policy. A nullable instance on <see cref="EditorSettings"/>
/// allows older settings files to retain their legacy effect choices until they are edited here.
/// </summary>
public sealed class EditorPerformanceSettings
{
    public EditorPerformanceProfile Profile { get; set; } = EditorPerformanceProfile.Balanced;
    public BackdropBlurMode BackdropBlurMode { get; set; } = BackdropBlurMode.Off;
    public bool AnimatedBackground { get; set; }
    public bool DropShadows { get; set; }
    public bool UiAntialiasing { get; set; } = true;

    public static EditorPerformanceSettings CreateDefault() => new();

    /// <summary>Map the pre-profile theme fields for settings files written by older editors.</summary>
    public static EditorPerformanceSettings FromLegacy(EditorThemeData theme)
    {
        if (theme == null) throw new ArgumentNullException(nameof(theme));

        return new EditorPerformanceSettings
        {
            Profile = theme.GlassBlur ? EditorPerformanceProfile.Visual : EditorPerformanceProfile.Balanced,
            BackdropBlurMode = BackdropBlurMode.Off,
            AnimatedBackground = theme.AnimatedBackground,
            DropShadows = theme.DropShadows,
            UiAntialiasing = theme.AntiAliasing,
        };
    }

    /// <summary>Apply the named preset. Selecting a preset is an explicit user action.</summary>
    public void ApplyProfileDefaults(EditorPerformanceProfile profile)
    {
        Profile = profile;
        switch (profile)
        {
            case EditorPerformanceProfile.Performance:
                BackdropBlurMode = BackdropBlurMode.Off;
                AnimatedBackground = false;
                DropShadows = false;
                UiAntialiasing = false;
                break;
            case EditorPerformanceProfile.Visual:
                BackdropBlurMode = BackdropBlurMode.Off;
                AnimatedBackground = true;
                DropShadows = true;
                UiAntialiasing = true;
                break;
            default:
                BackdropBlurMode = BackdropBlurMode.Off;
                AnimatedBackground = false;
                DropShadows = false;
                UiAntialiasing = true;
                break;
        }
    }
}
