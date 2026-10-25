using System;

using XEngine.AI.Navigation;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Theming;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Navigation;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.AI.Navigation.Editor;

/// <summary>Click two ground points in the Scene view to preview a path on the selected surface.</summary>
[SceneViewEditorFor(typeof(NavMeshSurface))]
public sealed class NavMeshSurfaceSceneEditor : ISceneViewEditor
{
    private NavMeshSurface? _surface;
    private Float3? _start;
    private readonly NavMeshPath _path = new();

    public int Priority => 10;

    public void OnActivate(GameObject target)
    {
        _surface = target.GetComponent<NavMeshSurface>();
        _start = null;
        _path.Clear();
    }

    public void OnDeactivate()
    {
        if (_surface != null)
            _surface.PreviewPath = null;
        _surface = null;
        _start = null;
        _path.Clear();
    }

    public bool DrawToolbar(Paper paper, string id, Scribe.FontFile font)
    {
        paper.Box($"{id}_hint").Width(UnitValue.Auto).Height(22)
            .Text(_start == null ? "Click start" : "Click end", font)
            .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
            .Alignment(TextAlignment.MiddleLeft);
        return false;
    }

    public bool OnSceneInput(Camera camera, Scene scene, Rect viewport, Ray mouseRay, Float2 mousePos, bool viewportHovered)
    {
        if (_surface == null) return false;
        if (!viewportHovered || !Input.GetMouseButtonDown(0)) return false;
        if (!TryHitGround(mouseRay, out Float3 point)) return false;

        if (_start == null)
        {
            _start = point;
            _surface.PreviewPath = null;
            return true;
        }

        NavMeshData? data = _surface.BakedData;
        if (data != null)
            XEngine.Runtime.Navigation.NavMesh.AddNavMeshData(data);

        if (XEngine.Runtime.Navigation.NavMesh.CalculatePath(_start.Value, point, _path) && _path.CornerCount >= 2)
        {
            var corners = new Float3[_path.CornerCount];
            for (int i = 0; i < _path.CornerCount; i++)
                corners[i] = _path[i];
            _surface.PreviewPath = corners;
        }
        else
        {
            _surface.PreviewPath = [_start.Value, point];
        }

        _start = null;
        return true;
    }

    public void DrawOverlay(Quill.Canvas canvas, Rect viewport) { }

    private static bool TryHitGround(Ray ray, out Float3 point)
    {
        if (MathF.Abs(ray.Direction.Y) < 1e-5f)
        {
            point = default;
            return false;
        }

        float t = -ray.Origin.Y / ray.Direction.Y;
        if (t < 0f)
        {
            point = default;
            return false;
        }

        point = ray.Origin + ray.Direction * t;
        return true;
    }
}
