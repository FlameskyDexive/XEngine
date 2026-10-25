// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;

using XEngine.Editor.GUI;

namespace XEngine.Editor.Projects.Settings;

/// <summary>
/// Player-facing defaults (Unity Player Settings → Resolution and Presentation). The default
/// orientation is applied by <see cref="PlayerSettingsLoader"/> at player startup; games can
/// still override it at runtime through <see cref="Screen.Orientation"/>.
/// </summary>
[ProjectSettings("Player", EditorIcons.Mobile, order: 15)]
public sealed class PlayerSettings : ProjectSettingsBase
{
    /// <summary>Orientation the player window requests at startup.</summary>
    public ScreenOrientation DefaultOrientation = ScreenOrientation.AutoRotation;
    public bool KeepScreenOn;
    public int TargetFrameRate;

    public bool AutoRotateToPortrait = true;
    public bool AutoRotateToPortraitUpsideDown = true;
    public bool AutoRotateToLandscapeLeft = true;
    public bool AutoRotateToLandscapeRight = true;

    public override void Apply()
    {
        // Orientation only means something to a play-mode/player window; the editor window is
        // desktop-controlled and must keep its own state.
        if (Runtime.Application.IsEditor && !Runtime.Application.IsPlaying)
            return;

        Screen.AutoRotateToPortrait = AutoRotateToPortrait;
        Screen.AutoRotateToPortraitUpsideDown = AutoRotateToPortraitUpsideDown;
        Screen.AutoRotateToLandscapeLeft = AutoRotateToLandscapeLeft;
        Screen.AutoRotateToLandscapeRight = AutoRotateToLandscapeRight;
        Screen.Orientation = DefaultOrientation;
        PlatformServices.KeepScreenOn = KeepScreenOn;
    }

    public override void ResetToDefaults()
    {
        TargetFrameRate = 0;
        KeepScreenOn = false;
        DefaultOrientation = ScreenOrientation.AutoRotation;
        AutoRotateToPortrait = true;
        AutoRotateToPortraitUpsideDown = true;
        AutoRotateToLandscapeLeft = true;
        AutoRotateToLandscapeRight = true;
    }

    public override void OnGUI(Paper paper, float width)
    {
        EditorGUI.SettingsIntSlider(paper, "player_frame_limit", "Desktop FPS Limit (0 = Unlimited)", TargetFrameRate, 0, 240,
            value => { TargetFrameRate = value; EditorRegistries.SaveSettings(); });
        EditorGUI.SettingsToggle(paper, "player_keep_awake", "Keep Screen Awake", KeepScreenOn,
            value => { KeepScreenOn = value; Apply(); EditorRegistries.SaveSettings(); });
        Origami.Header(paper, "player_h_orientation", $"{EditorIcons.Mobile}  Resolution").Underline().Show();

        EditorGUI.SettingsEnumDropdown(paper, "player_default_orientation", "Default Orientation", DefaultOrientation,
            value => { DefaultOrientation = value; Apply(); EditorRegistries.SaveSettings(); });

        if (DefaultOrientation != ScreenOrientation.AutoRotation)
            return;

        paper.Box("player_sp_autorotate").Height(8);
        Origami.Header(paper, "player_h_autorotate", $"{EditorIcons.ArrowsRotate}  Allowed Orientations For Auto Rotation").Underline().Show();

        EditorGUI.SettingsToggle(paper, "player_auto_portrait", "Portrait", AutoRotateToPortrait,
            value => { AutoRotateToPortrait = value; Apply(); EditorRegistries.SaveSettings(); });
        EditorGUI.SettingsToggle(paper, "player_auto_portrait_down", "Portrait Upside Down", AutoRotateToPortraitUpsideDown,
            value => { AutoRotateToPortraitUpsideDown = value; Apply(); EditorRegistries.SaveSettings(); });
        EditorGUI.SettingsToggle(paper, "player_auto_landscape_left", "Landscape Left", AutoRotateToLandscapeLeft,
            value => { AutoRotateToLandscapeLeft = value; Apply(); EditorRegistries.SaveSettings(); });
        EditorGUI.SettingsToggle(paper, "player_auto_landscape_right", "Landscape Right", AutoRotateToLandscapeRight,
            value => { AutoRotateToLandscapeRight = value; Apply(); EditorRegistries.SaveSettings(); });
    }
}
