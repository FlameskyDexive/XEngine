using System;
using System.IO;

using XEngine.AI.Navigation;
using XEngine.Editor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Navigation;
using XEngine.Runtime.Resources;

namespace XEngine.AI.Navigation.Editor;

public sealed class NavigationPanel : DockPanel
{
    [MenuItem("Window/AI/Navigation", priority: 24, Icon = EditorIcons.Route)]
    static void Open() => EditorApplication.Instance?.OpenPanel(typeof(NavigationPanel));

    public override string Title => "Navigation";
    public override string Icon => EditorIcons.Route;

    private readonly NavMeshBakeService _bake = new();
    private string _cat = "bake";

    private static readonly (string id, string label, string icon)[] Cats =
    [
        ("agents", "Agents", EditorIcons.PersonWalking),
        ("areas", "Areas", EditorIcons.LayerGroup),
        ("bake", "Bake", EditorIcons.Map),
        ("object", "Object", EditorIcons.Cube),
    ];

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        _bake.Poll();

        Scene? scene = Scene.Current;
        if (scene == null)
        {
            paper.Box("nav_noscene").Height(30)
                .Text("No scene", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleCenter);
            return;
        }

        using (paper.Row("nav_root").Width(width).Height(height).Clip().Enter())
        {
            float side = EditorGUI.Sidebar(paper, "nav_side", Cats, _cat, c => _cat = c);
            paper.Box("nav_vdiv").Width(1).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
            Origami.ScrollView(paper, "nav_scroll", width - side - 1, height).Body(() =>
            {
                using (paper.Column("nav_content").Height(UnitValue.Auto).Padding(0, 0, 8, 12).Enter())
                {
                    NavMeshSurface? surface = FindSurface(scene);
                    switch (_cat)
                    {
                        case "agents": DrawAgents(paper, font, surface); break;
                        case "areas": DrawAreas(paper, font, surface); break;
                        case "object": DrawObject(paper, font, scene, surface); break;
                        default: DrawBake(paper, font, scene, surface); break;
                    }
                }
            });
        }
    }

    private void DrawBake(Paper paper, XEngine.Scribe.FontFile font, Scene scene, NavMeshSurface? surface)
    {
        Origami.Header(paper, "nav_bake_h", "Bake").Underline().Show();
        if (surface == null)
            paper.Box("nav_nosurf").Height(22)
                .Text("Add a NavMesh Surface to bake.", font)
                .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall);

        bool baking = _bake.IsBaking;
        if (baking)
        {
            Origami.ProgressBar(paper, "nav_pb", _bake.Progress).Show();
            paper.Box("nav_status").Height(18)
                .Text($"{_bake.Status}  ({_bake.Progress * 100f:F0}%)", font)
                .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall);
            EditorGUI.CtaButton(paper, "nav_cancel", $"{EditorIcons.Xmark}  Cancel", EditorTheme.Red400,
                () => _bake.Cancel(), height: 34f);
            return;
        }

        paper.Box("nav_idle").Height(18)
            .Text(_bake.Status, font)
            .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall);

        EditorGUI.CtaButton(paper, "nav_bake", $"{EditorIcons.Map}  Bake NavMesh", EditorTheme.Accent,
            () => _bake.Start(scene, surface), grow: true, height: 34f);

        Origami.Button(paper, "nav_clear", "Clear", () => _bake.Clear(scene, surface)).Show();

        NavMeshData? data = surface?.BakedData;
        if (data == null) return;

        Origami.Header(paper, "nav_exp_h", "Export").Underline().Show();
        Origami.Button(paper, "nav_obj", "Export OBJ", () => Export(data, ".obj", NavMeshExport.WriteObj)).Show();
        Origami.Button(paper, "nav_json", "Export JSON", () => Export(data, ".json", NavMeshExport.WriteJson)).Show();
        Origami.Button(paper, "nav_bin", "Export BIN", () => Export(data, ".bin", NavMeshExport.WriteBin)).Show();
    }

    private static void DrawAgents(Paper paper, XEngine.Scribe.FontFile font, NavMeshSurface? surface)
    {
        Origami.Header(paper, "nav_ag_h", "Agent").Underline().Show();
        if (surface == null)
        {
            paper.Box("nav_ag_none").Height(22).Text("Select or add a NavMesh Surface.", font)
                .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall);
            return;
        }

        PropertyGridUtils.Draw(paper, "nav_ag_pg", surface.Settings);
    }

    private static void DrawAreas(Paper paper, XEngine.Scribe.FontFile font, NavMeshSurface? surface)
    {
        Origami.Header(paper, "nav_ar_h", "Areas").Underline().Show();
        NavMeshData? data = surface?.BakedData;
        var areas = data?.Areas ?? NavMeshAreas.DefaultAreas();
        for (int i = 0; i < areas.Count; i++)
        {
            NavMeshAreaInfo a = areas[i];
            paper.Box($"nav_ar{i}").Height(20)
                .Text($"{a.Id}  {a.Name}  cost={a.Cost:0.##}", font)
                .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall);
        }
    }

    private static void DrawObject(Paper paper, XEngine.Scribe.FontFile font, Scene scene, NavMeshSurface? surface)
    {
        Origami.Header(paper, "nav_ob_h", "Selected").Underline().Show();
        if (Selection.ActiveObject is GameObject go)
        {
            paper.Box("nav_ob_name").Height(20).Text(go.Name ?? "(unnamed)", font)
                .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSize);
            if (go.GetComponent<NavMeshSurface>() is { } s)
                PropertyGridUtils.Draw(paper, "nav_ob_surf", s);
            else if (go.GetComponent<NavMeshModifier>() is { } m)
                PropertyGridUtils.Draw(paper, "nav_ob_mod", m);
            else if (go.GetComponent<NavMeshLink>() is { } link)
                PropertyGridUtils.Draw(paper, "nav_ob_link", link);
            else if (go.GetComponent<NavMeshModifierVolume>() is { } vol)
                PropertyGridUtils.Draw(paper, "nav_ob_vol", vol);
            else
                paper.Box("nav_ob_none").Height(20).Text("No navigation component.", font)
                    .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall);
            return;
        }

        paper.Box("nav_ob_empty").Height(20).Text("Nothing selected.", font)
            .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall);
        _ = scene;
        _ = surface;
    }

    private static NavMeshSurface? FindSurface(Scene scene)
    {
        if (Selection.ActiveObject is GameObject go)
        {
            NavMeshSurface? selected = go.GetComponent<NavMeshSurface>();
            if (selected != null) return selected;
        }

        NavMeshSurface?[] all = scene.FindObjectsOfType<NavMeshSurface>();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null) return all[i];
        }

        return null;
    }

    private static void Export(NavMeshData data, string ext, Action<NavMeshData, string> write)
    {
        string? scenePath = EditorSceneManager.CurrentScenePath;
        string folder;
        if (!string.IsNullOrEmpty(scenePath) && Project.Current != null)
        {
            string sceneDir = Path.GetDirectoryName(scenePath)?.Replace('\\', '/') ?? "";
            string sceneName = Path.GetFileNameWithoutExtension(scenePath);
            string rel = string.IsNullOrEmpty(sceneDir) ? $"{sceneName}_navmesh" : $"{sceneDir}/{sceneName}_navmesh";
            folder = Path.Combine(Project.Current.AssetsPath, rel.Replace('/', Path.DirectorySeparatorChar));
        }
        else
        {
            folder = Path.GetTempPath();
        }

        Directory.CreateDirectory(folder);
        string name = Path.GetFileNameWithoutExtension(data.Name ?? "NavMesh");
        if (string.IsNullOrEmpty(name)) name = "NavMesh";
        string abs = Path.Combine(folder, name + ext);
        write(data, abs);
        Runtime.Debug.Log($"[NavMesh] Exported {abs}");
    }
}
