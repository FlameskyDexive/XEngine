using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.Editor.GUI.PropertyEditors;
using XEngine.Editor.GUI.SceneView;
using XEngine.Vector;
using System;
using System.Collections.Generic;

namespace XEngine.Editor.Inspector;

// ================================================================
//  Skinned Mesh Renderer Component Editor
//  Draws the default fields, then a "Blend Shapes" section with a
//  0-100 weight slider per blend shape on the shared mesh.
// ================================================================

[CustomEditor(typeof(SkinnedMeshRenderer))]
public partial class SkinnedMeshRendererEditor : CustomEditor
{
    private static readonly System.Collections.Generic.HashSet<string> s_referenceFields = new()
        { nameof(SkinnedMeshRenderer.RootBonePath), nameof(SkinnedMeshRenderer.SharedMesh), nameof(SkinnedMeshRenderer.Materials) };
    private static readonly TransformPropertyEditor s_transformField = new();
    private static readonly AssetRefPropertyEditor s_assetField = new();

    public override void OnGUI(Paper paper, string id, object target)
    {
        var renderer = (SkinnedMeshRenderer)target;
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        // Snapshot the component state before any widget (default fields or sliders) mutates it.
        Undo.SnapshotIfInteracting(renderer, paper);

        s_assetField.Draw(paper, $"{id}_mesh", "Mesh", renderer.SharedMesh, value =>
        {
            if (value is AssetRef<Mesh> mesh) TryAssignMeshes(new object[] { renderer }, mesh);
        }, compact: true);

        s_transformField.OnGUI(paper, $"{id}_root_bone", "Root Bone", renderer.RootBone,
            value => TryAssignRootBone(renderer, value as Transform), 0);
        if (renderer.RootBone == null && !string.IsNullOrEmpty(renderer.RootBonePath))
            paper.Box($"{id}_root_bone_missing").Height(EditorTheme.RowHeight).Clip().IsNotInteractable()
                .Text($"Missing: {renderer.RootBonePath}", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

        DrawMaterials(paper, $"{id}_materials", renderer, null);
        // Remaining fields keep their generic editors; reference adapters own their Undo.
        Origami.PropertyGrid(paper, id, renderer, EditorApplication.PropertyGridConfig)
            .ExcludeFields(s_referenceFields).Overrides(PropertyGridUtils.OverriddenFields).Show();

        var mesh = renderer.SharedMesh.Res;
        int count = mesh?.BlendShapeCount ?? 0;
        if (mesh == null || count == 0)
            return;

        Origami.Separator(paper, $"{id}_bs_sep").Show();
        Origami.Header(paper, $"{id}_bs_header", $"Blend Shapes ({count})").Underline().Show();

        for (int i = 0; i < count; i++)
        {
            int index = i; // capture for the closure
            string name = mesh.GetBlendShapeName(index);
            if (string.IsNullOrEmpty(name)) name = $"BlendShape {index}";
            float weight = renderer.GetBlendShapeWeight(index);

            EditorGUI.Row(paper, $"{id}_bs_{index}", name, () =>
                Origami.Slider(paper, $"{id}_bs_{index}_v", weight,
                    v => renderer.SetBlendShapeWeight(index, v),
                    0f, 100f).Format("F1").Show());
        }

        Origami.Button(paper, $"{id}_bs_reset", "Reset Weights", () =>
        {
            for (int i = 0; i < count; i++)
                renderer.SetBlendShapeWeight(i, 0f);
        }).Show();
    }

    internal static bool TryAssignRootBone(SkinnedMeshRenderer renderer, Transform? bone)
    {
        if (renderer.IsDisposed || renderer.GameObject.IsDisposed) return false;
        if (!TryGetRootBonePath(renderer, bone, out var path)) return false;
        if (string.Equals(renderer.RootBonePath, path, System.StringComparison.Ordinal)) return true;
        var oldPath = renderer.RootBonePath;
        var componentId = renderer.Identifier;
        Undo.RegisterAction("Set Root Bone", () => ApplyRootBonePath(componentId, oldPath),
            () => ApplyRootBonePath(componentId, path));
        renderer.RootBonePath = path;
        renderer.OnValidate();
        EditorSceneManager.MarkDirty("Set Root Bone", "editor");
        return true;
    }

    // The caller passes the immutable set owned by BeginComponentNumericFields.
    // Picker callbacks retain that displayed set, never the Inspector's reused selection list.
    internal static void DrawMulti(Paper paper, string id, IReadOnlyList<object> targets)
    {
        var first = (SkinnedMeshRenderer)targets[0];
        bool meshMixed = false;
        for (int i = 1; i < targets.Count; i++)
            meshMixed |= first.SharedMesh != ((SkinnedMeshRenderer)targets[i]).SharedMesh;
        s_assetField.Draw(paper, $"{id}_mesh", "Mesh", first.SharedMesh, value =>
        {
            if (value is AssetRef<Mesh> mesh) TryAssignMeshes(targets, mesh);
        }, meshMixed, compact: true);
        var bone = first.RootBone;
        bool mixed = false, missing = bone == null && !string.IsNullOrEmpty(first.RootBonePath);
        for (int i = 1; i < targets.Count; i++)
        {
            var renderer = (SkinnedMeshRenderer)targets[i];
            var other = renderer.RootBone;
            if (!ReferenceEquals(bone, other) || (bone == null
                && !string.Equals(first.RootBonePath ?? "", renderer.RootBonePath ?? "", StringComparison.Ordinal)))
                mixed = true;
            missing |= other == null && !string.IsNullOrEmpty(renderer.RootBonePath);
        }
        s_transformField.Draw(paper, $"{id}_root_bone", "Root Bone", bone,
            value => TryAssignRootBones(targets, value as Transform), mixed);
        if (missing)
            paper.Box($"{id}_root_bone_missing").Height(EditorTheme.RowHeight).Clip().IsNotInteractable()
                .Text(mixed ? "Missing Root Bone in selection" : $"Missing: {first.RootBonePath}", EditorTheme.DefaultFont!)
                .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
        DrawMaterials(paper, $"{id}_materials", first, targets);
        Origami.PropertyGrid(paper, id, targets, EditorApplication.PropertyGridConfig)
            .ExcludeFields(s_referenceFields).Overrides(PropertyGridUtils.OverriddenFields).Show();
    }

    internal static bool TryAssignRootBones(IReadOnlyList<object> targets, Transform? bone)
    {
        if (targets.Count == 0) return false;
        // Validate the whole operation before writing any target. A removed component may
        // still be alive, and a replacement may reuse its GUID: require exact live instances.
        var paths = new string?[targets.Count];
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] is not SkinnedMeshRenderer renderer || renderer.IsDisposed
                || renderer.GameObject.IsDisposed || !ReferenceEquals(renderer.Scene, Scene.Current)
                || !ReferenceEquals(Undo.FindComponent(renderer.Identifier), renderer)
                || !TryGetRootBonePath(renderer, bone, out paths[i])) return false;
        }
        var actions = new List<(Action undo, Action redo)>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            var renderer = (SkinnedMeshRenderer)targets[i];
            var path = paths[i];
            if (string.Equals(renderer.RootBonePath, path, StringComparison.Ordinal)) continue;
            var oldPath = renderer.RootBonePath;
            var componentId = renderer.Identifier;
            actions.Add((() => ApplyRootBonePath(componentId, oldPath), () => ApplyRootBonePath(componentId, path)));
            renderer.RootBonePath = path;
            renderer.OnValidate();
        }
        if (actions.Count != 0)
        {
            Undo.RegisterActionGroup("Set Root Bone", actions);
            EditorSceneManager.MarkDirty("Set Root Bone", "editor");
        }
        return true;
    }

    internal static bool TryAssignMeshes(IReadOnlyList<object> targets, AssetRef<Mesh> mesh)
    {
        if (targets.Count == 0 || (mesh.IsRuntimeResource && mesh.ResWeak == null)) return false;
        for (int i = 0; i < targets.Count; i++)
            if (targets[i] is not SkinnedMeshRenderer renderer || renderer.IsDisposed
                || renderer.GameObject.IsDisposed || !ReferenceEquals(renderer.Scene, Scene.Current)
                || !ReferenceEquals(Undo.FindComponent(renderer.Identifier), renderer)) return false;
        var actions = new List<(Action undo, Action redo)>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            var renderer = (SkinnedMeshRenderer)targets[i];
            if (renderer.SharedMesh == mesh) continue;
            var oldMesh = renderer.SharedMesh;
            var componentId = renderer.Identifier;
            actions.Add((() => ApplyMesh(componentId, oldMesh), () => ApplyMesh(componentId, mesh)));
            renderer.SharedMesh = mesh;
            renderer.OnValidate();
        }
        if (actions.Count != 0)
        {
            Undo.RegisterActionGroup("Set Mesh", actions);
            EditorSceneManager.MarkDirty("Set Mesh", "editor");
        }
        return true;
    }

    private static void ApplyMesh(Guid id, AssetRef<Mesh> mesh)
    {
        if (Undo.FindComponent(id) is not SkinnedMeshRenderer renderer) return;
        renderer.SharedMesh = mesh;
        renderer.OnValidate();
    }

    private static bool TryGetRootBonePath(SkinnedMeshRenderer renderer, Transform? bone, out string? path)
    {
        var searchRoot = renderer.FindSearchRoot();
        path = null;
        if (bone != null)
        {
            if (bone.GameObject.IsDisposed || searchRoot == null) return false;
            var ancestor = bone;
            while (ancestor != null && ancestor != searchRoot) ancestor = ancestor.Parent;
            if (ancestor == null)
            {
                Debug.LogWarning("Root Bone must belong to this renderer's skeleton hierarchy.");
                return false;
            }
            path = bone == searchRoot ? "." : Transform.GetRelativePath(bone, searchRoot);
            if (path == "." ? bone != searchRoot : searchRoot.Find(path) != bone || !HasUniquePath(bone, searchRoot))
            {
                Debug.LogWarning("Root Bone path is ambiguous. Give the bone a unique hierarchy path first.");
                return false;
            }
        }
        return true;
    }

    private static bool HasUniquePath(Transform bone, Transform searchRoot)
    {
        for (var current = bone; current != searchRoot; current = current.Parent!)
        {
            var siblings = current.Parent!.GameObject.Children;
            int matches = 0;
            for (int i = 0; i < siblings.Count; i++)
                if (string.Equals(siblings[i].Name, current.GameObject.Name, System.StringComparison.Ordinal))
                    matches++;
            if (matches != 1) return false;
        }
        return true;
    }

    private static void ApplyRootBonePath(System.Guid id, string? path)
    {
        if (Undo.FindComponent(id) is not SkinnedMeshRenderer renderer) return;
        renderer.RootBonePath = path;
        renderer.OnValidate();
    }
}
