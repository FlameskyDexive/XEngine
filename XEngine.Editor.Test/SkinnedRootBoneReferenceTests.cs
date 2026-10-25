using XEngine.Editor.Core;
using XEngine.Editor.GUI.Popups;
using XEngine.Editor.Inspector;
using XEngine.Editor.Theming;
using XEngine.Runtime;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class SkinnedRootBoneReferenceTests
{
    [Fact]
    public void AssignmentPreservesPaletteAndIsOneUndoStepEvenWithPendingSnapshot()
    {
        using var f = new InspectorComponentHeaderTests.Fixture(335);
        var (root, renderer, hips, spine) = Rig(); f.Scene.Add(root);
        Undo.Snapshot(renderer);
        Assert.True(SkinnedMeshRendererEditor.TryAssignRootBone(renderer, spine.Transform));
        Undo.FlushFrame(); Assert.Same(spine.Transform, renderer.RootBone);
        Assert.Equal(new[] { "Hips", "Hips/Spine" }, renderer.BonePaths);
        Undo.PerformUndo(); Assert.Same(hips.Transform, renderer.RootBone); Assert.False(Undo.CanUndo);
        Undo.PerformRedo(); Assert.Same(spine.Transform, renderer.RootBone);
        Undo.Clear(); Assert.True(SkinnedMeshRendererEditor.TryAssignRootBone(renderer, spine.Transform));
        Undo.FlushFrame(); Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void ForeignAndAmbiguousBonesAreRejectedWithoutUndoOrMutation()
    {
        using var f = new InspectorComponentHeaderTests.Fixture(335);
        var (root, renderer, hips, spine) = Rig(); f.Scene.Add(root);
        var foreign = new GameObject("Foreign"); f.Scene.Add(foreign);
        var duplicate = Child("Hips", root);
        var reserved = Child(".", root);
        Assert.False(SkinnedMeshRendererEditor.TryAssignRootBone(renderer, foreign.Transform));
        Assert.False(SkinnedMeshRendererEditor.TryAssignRootBone(renderer, hips.Transform));
        Assert.False(SkinnedMeshRendererEditor.TryAssignRootBone(renderer, duplicate.Transform));
        Assert.False(SkinnedMeshRendererEditor.TryAssignRootBone(renderer, spine.Transform));
        Assert.False(SkinnedMeshRendererEditor.TryAssignRootBone(renderer, reserved.Transform));
        Undo.FlushFrame(); Assert.False(Undo.CanUndo); Assert.Same(hips.Transform, renderer.RootBone);
        Assert.Equal("Hips", renderer.RootBonePath);
    }

    [Fact]
    public void NormalPanelReplacesPathAndPickerNoneCanBeUndone()
    {
        EditorRegistries.Initialize();
        using var f = new InspectorComponentHeaderTests.Fixture(335);
        var (root, renderer, hips, _) = Rig(); f.Scene.Add(root); Selection.Select(renderer.GameObject);
        try
        {
            f.Frame(); f.Frame();
            Assert.False(f.HasText("Root Bone Path")); var label = f.Text("Root Bone");
            Assert.True(f.HasText("Hips (Transform)"));
            var picker = f.Text(EditorIcons.CircleDot, label.Y);
            f.Click(picker.X + 10, picker.Y + 9); Assert.True(SelectorModal.IsOpen);
            var none = f.Elements().Single(e => e.Paragraph?.EndsWith("None (Transform)") == true);
            f.Click(none.X + 80, none.Y + 9);
            Assert.False(SelectorModal.IsOpen); Assert.Null(renderer.RootBone); Assert.Null(renderer.RootBonePath);
            Undo.PerformUndo(); f.Frame(); Assert.Same(hips.Transform, renderer.RootBone);
            Assert.False(Undo.CanUndo);
            hips.Name = new string('W', 120); renderer.SetBones([hips.Transform], hips.Transform); f.Frame();
            var longName = f.Text(hips.Name + " (Transform)");
            picker = f.Text(EditorIcons.CircleDot, longName.Y);
            Assert.True(longName.X + longName.LayoutWidth <= picker.X);
            Assert.True(picker.X + picker.LayoutWidth <= 335);
            SelectorModal.Open("Select Transform", typeof(Transform), SelectorTabs.Scene, _ => { });
            f.Frame();
            var spineChoice = f.Text("Spine");
            Assert.True(spineChoice.LayoutWidth >= 100, "Long hierarchy paths must leave bone names readable.");
            SelectorModal.Close();
            renderer.RootBonePath = "MissingBone"; renderer.OnValidate(); f.Frame();
            Assert.True(f.HasText("Missing: MissingBone"));
            typeof(XEngine.Editor.GUI.Panels.InspectorPanel).GetField("_debugMode",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(f.Panel, true);
            f.Frame(); Assert.True(f.HasText("Root Bone Path")); Assert.False(f.HasText("Root Bone"));
        }
        finally { SelectorModal.Close(); }
    }

    private static (GameObject root, SkinnedMeshRenderer renderer, GameObject hips, GameObject spine) Rig()
    {
        var root = new GameObject("Model"); var hips = Child("Hips", root); var spine = Child("Spine", hips);
        var renderer = Child("Body", root).AddComponent<SkinnedMeshRenderer>();
        renderer.SetBones([hips.Transform, spine.Transform], hips.Transform);
        return (root, renderer, hips, spine);
    }
    private static GameObject Child(string name, GameObject parent)
    { var go = new GameObject(name); go.SetParent(parent, false); return go; }
}
