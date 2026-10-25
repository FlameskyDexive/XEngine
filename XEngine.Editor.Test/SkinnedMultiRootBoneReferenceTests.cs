using System.Reflection;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.Popups;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Inspector;
using XEngine.Editor.Theming;
using XEngine.Runtime;
using XEngine.Vector;
using Xunit;
using Fixture = XEngine.Editor.Test.InspectorComponentHeaderTests.Fixture;

namespace XEngine.Editor.Test;

public sealed class SkinnedMultiRootBoneReferenceTests
{
    private sealed class Harness : IDisposable
    {
        internal readonly Fixture F;
        internal readonly GameObject Root = new("Model"), Hips, Spine;
        internal readonly SkinnedMeshRenderer A, B;
        internal readonly object[] Targets;
        internal Harness(float width = 335)
        {
            EditorRegistries.Initialize(); F = new Fixture(width);
            Hips = Child("Hips", Root); Spine = Child("Spine", Hips);
            A = Child("Body", Root).AddComponent<SkinnedMeshRenderer>();
            B = Child("Face", Root).AddComponent<SkinnedMeshRenderer>();
            A.SetBones([Hips.Transform, Spine.Transform], Hips.Transform);
            B.SetBones([Hips.Transform, Spine.Transform], Spine.Transform);
            Targets = [A, B]; F.Scene.Add(Root);
            Selection.Select(A.GameObject); Selection.AddToSelection(B.GameObject);
            F.Frame(); F.Frame(); Undo.Clear(); EditorSceneManager.IsDirty = false;
        }
        internal void Open()
        {
            var label = F.Text("Root Bone"); var picker = F.Text(EditorIcons.CircleDot, label.Y);
            F.Click(picker.X + 10, picker.Y + 9); Assert.True(SelectorModal.IsOpen);
        }
        internal void Choose(string text)
        {
            var row = F.Text(text == "None" ? $"{EditorIcons.Circle}  None (Transform)" : text);
            F.Click(row.X + 30, row.Y + 9); Assert.False(SelectorModal.IsOpen);
        }
        public void Dispose() { SelectorModal.Close(); F.Dispose(); }
    }

    [Theory] [InlineData(240f)] [InlineData(335f)] [InlineData(520f)]
    public void MixedReferenceReplacesPathAndReservesPicker(float width)
    {
        using var h = new Harness(width);
        Assert.False(h.F.HasText("Root Bone Path")); Assert.True(h.F.HasText("Bone Paths"));
        var label = h.F.Text("Root Bone"); var dash = h.F.Text("—", label.Y);
        var picker = h.F.Text(EditorIcons.CircleDot, label.Y);
        Assert.True(dash.X + dash.LayoutWidth <= picker.X); Assert.True(picker.X + picker.LayoutWidth <= width);
        h.Open(); h.Choose("None");
        Assert.Null(h.A.RootBonePath); Assert.Null(h.B.RootBonePath);
        Assert.True(h.F.HasText("None (Transform)")); Assert.True(EditorSceneManager.IsDirty);
        Undo.PerformUndo(); h.F.Frame();
        Assert.Same(h.Hips.Transform, h.A.RootBone); Assert.Same(h.Spine.Transform, h.B.RootBone);
        Assert.False(Undo.CanUndo); Undo.PerformRedo(); Assert.Null(h.A.RootBone); Assert.Null(h.B.RootBone);
    }

    [Fact]
    public void ActualPickerAssignsSharedBoneAndIndependentUndoPreservesPaletteAndOtherFields()
    {
        using var h = new Harness(); var pathsA = h.A.BonePaths; var pathsB = h.B.BonePaths;
        h.Open(); h.Choose("Spine"); Assert.Same(h.Spine.Transform, h.A.RootBone); Assert.Same(h.Spine.Transform, h.B.RootBone);
        h.A.Enabled = false; h.B.Enabled = false;
        h.Open(); h.Choose("None"); Undo.PerformUndo();
        Assert.Same(h.Spine.Transform, h.A.RootBone); Assert.Same(h.Spine.Transform, h.B.RootBone);
        Undo.PerformUndo(); Assert.Same(h.Hips.Transform, h.A.RootBone); Assert.Same(h.Spine.Transform, h.B.RootBone);
        Assert.False(h.A.Enabled); Assert.False(h.B.Enabled);
        Assert.Same(pathsA, h.A.BonePaths); Assert.Same(pathsB, h.B.BonePaths); Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void PendingSnapshotsDoNotDuplicateGroupedReferenceUndo()
    {
        using var h = new Harness(); Undo.Snapshot(h.A); Undo.Snapshot(h.B);
        Assert.True(SkinnedMeshRendererEditor.TryAssignRootBones(h.Targets, h.Spine.Transform)); Undo.FlushFrame();
        Undo.PerformUndo(); Assert.False(Undo.CanUndo); Assert.Equal("Hips", h.A.RootBonePath); Assert.Equal("Hips/Spine", h.B.RootBonePath);
    }

    [Fact]
    public void EqualNoOpAndInvalidReferencePreserveCleanStateAndRedo()
    {
        using var h = new Harness(); SkinnedMeshRendererEditor.TryAssignRootBones(h.Targets, null); Undo.FlushFrame();
        Undo.PerformUndo(); Assert.True(Undo.CanRedo); EditorSceneManager.IsDirty = false;
        // Set both to the same existing value without adding history, then exercise no-op.
        h.B.RootBonePath = h.A.RootBonePath; h.B.OnValidate();
        Assert.True(SkinnedMeshRendererEditor.TryAssignRootBones(h.Targets, h.Hips.Transform));
        var foreign = new GameObject("Foreign"); h.F.Scene.Add(foreign);
        Assert.False(SkinnedMeshRendererEditor.TryAssignRootBones(h.Targets, foreign.Transform)); Undo.FlushFrame();
        Assert.False(EditorSceneManager.IsDirty); Assert.False(Undo.CanUndo); Assert.True(Undo.CanRedo);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void DifferentSkeletonInstancesAreMixedAndForeignAssignmentIsAtomic(bool clear)
    {
        using var h = new Harness(); var root = new GameObject("OtherModel"); var hips = Child("Hips", root);
        var other = Child("OtherFace", root).AddComponent<SkinnedMeshRenderer>(); other.SetBones([hips.Transform], hips.Transform); h.F.Scene.Add(root);
        Selection.Select(h.A.GameObject); Selection.AddToSelection(other.GameObject); h.F.Frame(); h.F.Frame();
        var label = h.F.Text("Root Bone"); Assert.Equal("Hips", h.A.RootBonePath); Assert.Equal("Hips", other.RootBonePath);
        Assert.Equal("—", h.F.Text("—", label.Y).Paragraph);
        object[] targets = [h.A, other];
        Assert.Equal(clear, SkinnedMeshRendererEditor.TryAssignRootBones(targets, clear ? null : h.Spine.Transform)); Undo.FlushFrame();
        if (clear) Undo.PerformUndo(); else Assert.False(Undo.CanUndo);
        Assert.Same(h.Hips.Transform, h.A.RootBone); Assert.Same(hips.Transform, other.RootBone);
        if (!clear) Assert.False(EditorSceneManager.IsDirty);
    }

    [Theory] [InlineData("duplicate")] [InlineData("dot")] [InlineData("disposed")]
    public void InvalidBonePathsRejectWholeSet(string kind)
    {
        using var h = new Harness(); Transform bone = h.Spine.Transform;
        if (kind == "duplicate") Child("Hips", h.Root);
        if (kind == "dot") bone = Child(".", h.Root).Transform;
        if (kind == "disposed") { var go = Child("Dead", h.Root); bone = go.Transform; go.Dispose(); }
        Assert.False(SkinnedMeshRendererEditor.TryAssignRootBones(h.Targets, bone)); Undo.FlushFrame();
        Assert.Equal("Hips", h.A.RootBonePath); Assert.Equal("Hips/Spine", h.B.RootBonePath);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RemovedOrReplacedTargetRejectsWithoutChangingSurvivor(bool replace)
    {
        using var h = new Harness(); h.Open(); var id = h.B.Identifier;
        h.B.GameObject.RemoveComponent(h.B); SkinnedMeshRenderer? replacement = null;
        if (replace)
        {
            replacement = h.B.GameObject.AddComponent<SkinnedMeshRenderer>(); replacement.RootBonePath = "replacement";
            typeof(MonoBehaviour).GetField("_identifier", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(replacement, id);
        }
        h.Choose("None"); Assert.Equal("Hips", h.A.RootBonePath);
        if (replace) Assert.Equal("replacement", replacement!.RootBonePath);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void OpenPickerOwnsOldDisplayedSetAcrossSelectionRebuild()
    {
        using var h = new Harness(); h.Open(); var c = Child("Third", h.Root).AddComponent<SkinnedMeshRenderer>();
        c.SetBones([h.Hips.Transform], h.Hips.Transform);
        Selection.Select(h.A.GameObject); Selection.AddToSelection(c.GameObject); h.F.Frame(); h.F.Frame();
        h.Choose("None"); Assert.Null(h.A.RootBone); Assert.Null(h.B.RootBone); Assert.Same(h.Hips.Transform, c.RootBone);
        Undo.PerformUndo(); Assert.Same(h.Hips.Transform, h.A.RootBone); Assert.Same(h.Spine.Transform, h.B.RootBone);
    }

    [Fact]
    public void MissingReferencesAreDistinctFromNoneAndDebugRetainsPaths()
    {
        using var h = new Harness(); h.A.RootBonePath = "Gone"; h.A.OnValidate(); h.B.RootBonePath = null; h.B.OnValidate(); h.F.Frame();
        Assert.True(h.F.HasText("Missing Root Bone in selection")); Assert.True(h.F.HasText("—"));
        h.B.RootBonePath = "Gone"; h.B.OnValidate(); h.F.Frame(); Assert.True(h.F.HasText("Missing: Gone"));
        h.Open(); h.Choose("None"); Assert.False(h.F.HasText("Missing: Gone")); Undo.PerformUndo(); h.F.Frame();
        Assert.Equal("Gone", h.A.RootBonePath); Assert.Equal("Gone", h.B.RootBonePath);
        typeof(XEngine.Editor.GUI.Panels.InspectorPanel).GetField("_debugMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(h.F.Panel, true);
        h.F.Frame(); Assert.True(h.F.HasText("Root Bone Path")); Assert.False(h.F.HasText("Root Bone"));
    }

    [Fact]
    public void RootSelfUsesReservedPathAndUndoRestoresDifferentOriginals()
    {
        using var h = new Harness(); Assert.True(SkinnedMeshRendererEditor.TryAssignRootBones(h.Targets, h.Root.Transform)); Undo.FlushFrame();
        Assert.Equal(".", h.A.RootBonePath); Assert.Equal(".", h.B.RootBonePath); Assert.Same(h.Root.Transform, h.A.RootBone);
        Undo.PerformUndo(); Assert.Equal("Hips", h.A.RootBonePath); Assert.Equal("Hips/Spine", h.B.RootBonePath);
    }

    private static GameObject Child(string name, GameObject parent)
    { var go = new GameObject(name); go.SetParent(parent, false); return go; }
}
