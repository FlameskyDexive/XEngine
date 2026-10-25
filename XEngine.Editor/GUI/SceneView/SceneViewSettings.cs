using System;
using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>Which point transform handles pivot around when several objects are selected.</summary>
public enum PivotMode
{
    /// <summary>The center of the whole selection.</summary>
    Center,
    /// <summary>The active object's own transform origin.</summary>
    Pivot,
}

/// <summary>Whether transform handles align to world axes or the active object's rotation.</summary>
public enum HandleOrientation
{
    Global,
    Local,
}

/// <summary>
/// Toolbar state for one Scene view: handle pivot/orientation, grid and snapping increments, 2D mode
/// and the per-view visibility toggles. One instance per <see cref="Panels.SceneViewPanel"/> so two
/// open Scene tabs can be configured independently, matching how the rest of the panel state works.
/// </summary>
public sealed class SceneViewSettings
{
    public PivotMode Pivot = PivotMode.Center;
    public HandleOrientation Orientation = HandleOrientation.Local;

    public bool ShowGrid = true;
    public float GridSize = 1f;

    /// <summary>Snap handles to the increments below without holding Ctrl.</summary>
    public bool SnapEnabled;
    public float MoveSnap = 0.25f;
    public float RotateSnap = 15f;
    public float ScaleSnap = 1f;

    /// <summary>Orthographic, axis-aligned view for 2D work.</summary>
    public bool Is2D;

    /// <summary>
    /// Orthographic projection from the view cube's Iso toggle, independent of <see cref="Is2D"/>.
    /// 2D mode also implies this; turning 2D off restores perspective unless Iso is still on.
    /// </summary>
    public bool ViewOrthographic;

    /// <summary>Whether scene audio is audible while not in play mode (Unity's speaker toggle).</summary>
    public bool AudioEnabled = true;

    /// <summary>Apply the main camera's image effects to the Scene view (Unity's "fx" toggle).</summary>
    public bool EffectsEnabled = true;

    public bool ShowGizmos = true;

    /// <summary>
    /// Selection highlighting, delegated to the shared <see cref="GizmoSettings"/> so the Gizmos
    /// window reports the same state in the Scene and Game views (Unity keeps these global too).
    /// True while either the silhouette outline or the wireframe bounds box is on.
    /// </summary>
    public bool ShowSelectionOutline
    {
        get => GizmoSettings.SelectionOutline || GizmoSettings.SelectionWire;
        set { GizmoSettings.SelectionOutline = value; GizmoSettings.SelectionWire = value; }
    }

    /// <summary>Show the skybox (or scene background) behind the Scene view, matching Unity's default.</summary>
    public bool ShowBackground = true;

    /// <summary>Scene draw mode (Unity's Shaded / Wireframe dropdown).</summary>
    public Runtime.Rendering.DebugViewMode DrawMode = Runtime.Rendering.DebugViewMode.Shaded;

    /// <summary>
    /// ★ M7 — Unity's scene-view light-bulb toggle. Off drops the frame's lights so geometry shades
    /// from ambient / GI only; it is the same state as the Unlit draw mode, surfaced as a button
    /// because that is where Unity puts it.
    /// </summary>
    public bool SceneLightingEnabled
    {
        get => DrawMode != Runtime.Rendering.DebugViewMode.Unlit;
        set => DrawMode = value
            ? (DrawMode == Runtime.Rendering.DebugViewMode.Unlit ? Runtime.Rendering.DebugViewMode.Shaded : DrawMode)
            : Runtime.Rendering.DebugViewMode.Unlit;
    }

    /// <summary>
    /// ★ M7 — Draw the selection as a screen-space silhouette outline (Unity's orange ring). This is
    /// the Gizmos window's "Selection Outline" row; the axis-aligned bounds box became its own
    /// "Selection Wire" row, so the two are independent instead of either/or.
    /// </summary>
    public bool UseSilhouetteOutline
    {
        get => GizmoSettings.SelectionOutline;
        set => GizmoSettings.SelectionOutline = value;
    }
}
