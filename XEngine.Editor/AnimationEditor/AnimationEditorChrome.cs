// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

using Color = System.Drawing.Color;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// Shared Unity 2022 Professional Dark tokens for Animator / Animation / Timeline windows
/// and the Inspector animation preview bar. Windows must not invent their own FromArgb palettes.
/// </summary>
public static class AnimationEditorChrome
{
    // Surfaces
    public static readonly Color Panel = Color.FromArgb(255, 56, 56, 56);          // #383838
    public static readonly Color Canvas = Color.FromArgb(255, 42, 42, 42);         // #2A2A2A
    public static readonly Color Raised = Color.FromArgb(255, 48, 48, 48);
    public static readonly Color PreviewEmpty = Color.Black;
    public static readonly Color Splitter = Color.FromArgb(255, 26, 26, 26);
    public static readonly Color Selection = Color.FromArgb(255, 44, 93, 135);     // #2C5D87
    public static readonly Color Text = Color.FromArgb(255, 200, 200, 200);
    public static readonly Color TextBright = Color.FromArgb(255, 224, 224, 224);
    public static readonly Color TextMuted = Color.FromArgb(255, 150, 150, 150);

    public static readonly Color32 Panel32 = new(56, 56, 56, 255);
    public static readonly Color32 Canvas32 = new(42, 42, 42, 255);
    public static readonly Color32 Raised32 = new(48, 48, 48, 255);
    public static readonly Color32 PreviewEmpty32 = new(0, 0, 0, 255);
    public static readonly Color32 Splitter32 = new(26, 26, 26, 255);
    public static readonly Color32 Selection32 = new(44, 93, 135, 255);
    public static readonly Color32 Text32 = new(200, 200, 200, 255);
    public static readonly Color32 TextBright32 = new(224, 224, 224, 255);
    public static readonly Color32 TextMuted32 = new(150, 150, 150, 255);
    public static readonly Color32 GridLine32 = new(255, 255, 255, 18);
    public static readonly Color32 Keyframe32 = new(255, 255, 255, 255);
    public static readonly Color32 Playhead32 = new(255, 210, 0, 255);
    public static readonly Color32 Record32 = new(196, 48, 48, 255);

    // Mecanim node accents (Unity Animator, not theme)
    public static readonly Color StateAccent = Color.FromArgb(90, 90, 90);
    public static readonly Color DefaultAccent = Color.FromArgb(214, 133, 44); // Unity's default-state orange
    public static readonly Color EntryAccent = Color.FromArgb(91, 165, 42);
    public static readonly Color AnyStateAccent = Color.FromArgb(208, 69, 69);
    public static readonly Color ExitAccent = Color.FromArgb(201, 123, 20);
    public static readonly Color StateMachineAccent = Color.FromArgb(70, 110, 160);
    public static readonly Color FlowColor = Color.FromArgb(200, 200, 200);
    public static readonly Color ActiveAccent = Color.FromArgb(255, 180, 0);

    // Animation window channel colors (Unity: P=red, R=green, S=blue)
    public static readonly Color32 ChannelPosition = new(231, 76, 60, 255);
    public static readonly Color32 ChannelRotation = new(46, 204, 113, 255);
    public static readonly Color32 ChannelScale = new(52, 152, 219, 255);

    // Timeline track tints on charcoal
    public static readonly Color32 TrackAnimation = new(70, 110, 170, 255);
    public static readonly Color32 TrackAudio = new(180, 80, 120, 255);
    public static readonly Color32 TrackCamera = new(60, 140, 160, 255);
    public static readonly Color32 TrackActivation = new(80, 140, 70, 255);
    public static readonly Color32 TrackParticle = new(160, 110, 50, 255);
    public static readonly Color32 TrackSignal = new(200, 170, 50, 255);
    public static readonly Color32 TrackUnknown = new(100, 100, 110, 255);
    public static readonly Color32 TrackRow32 = new(40, 40, 46, 255);
    public static readonly Color32 RowEven32 = new(35, 35, 40, 255);
    public static readonly Color32 RowOdd32 = new(40, 40, 45, 255);

    public const float ToolbarHeight = 22f;
    public const float RulerHeight = 18f;
    public const float EventTrackHeight = 16f;
    public const float EventInspectorHeight = 64f;
    public const float RowHeight = 18f;
    public const float SidebarWidth = 190f;
    public const float InspectorHeight = 140f;
    public const float FontSize = 13f;
    public const float FontSizeSmall = 12f;

    public const string EmptyAnimator = "List is Empty";
    public const string EmptyClip = "Select a .anim asset";
    public const string EmptyClipCreate = "To begin animating {0}, create an Animation Clip";
    public const string EmptyTimeline = "Select a .timeline asset";
    public const string EmptyPreview =
        "No model is available for preview. Please drag a model into this Preview Area.";
}
