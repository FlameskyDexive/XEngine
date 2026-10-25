// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>Scene-view chrome icons: Unity bitmap dumps when present, Font Awesome otherwise.</summary>
internal static class SceneViewIcons
{
    public static IOrigamiIcon Pivot => BitmapOrGlyph("d_ToolHandleCenter.png", EditorIcons.Crosshairs);
    public static IOrigamiIcon Local => BitmapOrGlyph("d_ToolHandleLocal.png", EditorIcons.Cube);
    public static IOrigamiIcon Global => BitmapOrGlyph("d_ToolHandleGlobal.png", EditorIcons.Globe);
    public static IOrigamiIcon Grid =>
        EditorPngIcon.TryLoad("SceneView.d_GridAndSnap.png")
        ?? new SvgIcon("M1.5 1.5h13v13h-13z M1.5 6h13 M1.5 10.5h13 M6 1.5v13 M10.5 1.5v13", fill: false, scale: 0.9f);
    public static IOrigamiIcon Snap => BitmapOrGlyph("d_SceneViewSnap.png", EditorIcons.Magnet);
    public static IOrigamiIcon Audio => BitmapOrGlyph("d_SceneViewAudio.png", EditorIcons.VolumeHigh);
    public static IOrigamiIcon Overlays => BitmapOrGlyph("d_SettingsIcon.png", EditorIcons.LayerGroup);
    public static IOrigamiIcon Caret => BitmapOrGlyph("d_icon_dropdown.png", EditorIcons.CaretDown);
    public static IOrigamiIcon Move => BitmapOrGlyph("d_MoveTool.png", EditorIcons.ArrowsUpDownLeftRight);
    public static IOrigamiIcon Rotate => BitmapOrGlyph("d_RotateTool.png", EditorIcons.ArrowsRotate);
    public static IOrigamiIcon Scale => BitmapOrGlyph("d_ScaleTool.png", EditorIcons.Maximize);
    public static IOrigamiIcon Universal => BitmapOrGlyph("d_ViewToolMove.png", EditorIcons.Expand);
    public static IOrigamiIcon Camera => BitmapOrGlyph("d_SceneViewCamera.png", EditorIcons.Camera);
    public static IOrigamiIcon Fx => BitmapOrGlyph("d_SceneViewFx.png", EditorIcons.WandMagicSparkles);
    public static IOrigamiIcon Ellipsis => Glyph(EditorIcons.EllipsisVertical);
    public static IOrigamiIcon Grip => Glyph(EditorIcons.GripLines);
    public static IOrigamiIcon ChevronUp => Glyph(EditorIcons.ChevronUp);
    public static IOrigamiIcon ChevronDown => Glyph(EditorIcons.ChevronDown);

    public static IOrigamiIcon Glyph(string glyph) => new EditorGlyphIcon(glyph, weight: GlyphWeight.Solid);

    public static IOrigamiIcon BitmapOrGlyph(string fileName, string glyph)
        => EditorPngIcon.TryLoad("SceneView." + fileName) ?? Glyph(glyph);
}
