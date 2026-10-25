using System.Reflection;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Inspector;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Editor.Test;

public sealed partial class SkinnedMaterialListTests
{
    private static void ThreeSlots(Harness h)
    {
        h.A.Materials.Add(h.MB); h.A.Materials.Add(default);
        h.B.Materials.Add(h.MA); h.B.Materials.Add(default); h.F.Frame(); h.F.Frame();
    }

    private static void BeginReorder(Harness h, int slot)
    {
        var label = h.F.Text($"Element {slot}");
        var grip = h.F.Text(EditorIcons.GripLines, label.Y);
        Assert.Equal(10, grip.LayoutWidth);
        float x = grip.X + 5, y = grip.Y + 9;
        h.F.Paper.SetPointerPosition(x, y); h.F.Frame();
        h.F.Frame(() => h.F.Paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
        h.F.Frame(() => h.F.Paper.SetPointerPosition(x + 18, y)); h.F.Frame();
        Assert.IsType<SkinnedMeshRendererEditor.MaterialSlotDragPayload>(DragDrop.Payload);
    }

    private static void HoverReorder(Harness h, int slot, bool before)
    {
        var label = h.F.Text($"Element {slot}");
        h.F.Paper.SetPointerPosition(label.X + 30, label.Y + (before ? 2 : label.LayoutHeight - 2));
        h.F.Frame(); h.F.Frame();
    }

    private static void ReleaseReorder(Harness h)
    {
        var p = h.F.Paper.PointerPos;
        h.F.Frame(() => h.F.Paper.SetPointerState(PaperMouseBtn.Left, p.X, p.Y, false, false));
        h.F.Frame(); h.F.Frame();
    }

    [Theory] [InlineData(240f)] [InlineData(335f)] [InlineData(520f)]
    public void RealSlotGestureMovesPerTargetOrderAndHasOneUndo(float width)
    {
        using var h = new Harness(width); ThreeSlots(h);
        var listA = h.A.Materials; var listB = h.B.Materials;
        var paths = h.A.BonePaths; var mesh = h.A.SharedMesh; string root = h.A.RootBonePath;
        BeginReorder(h, 0); HoverReorder(h, 2, false);
        Assert.Equal(DragVisualMode.Move, DragDrop.VisualMode);
        Assert.Same(h.MA, h.A.Materials[0].Res); Assert.Same(h.MB, h.B.Materials[0].Res);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        Assert.Contains(h.F.Elements(), e => e.LayoutHeight == 2 && e.LayoutWidth > 0);
        ReleaseReorder(h);
        Assert.Equal(new AssetRef<Material>[] { h.MB, default, h.MA }, h.A.Materials);
        Assert.Equal(new AssetRef<Material>[] { h.MA, default, h.MB }, h.B.Materials);
        Undo.PerformUndo(); h.F.Frame();
        Assert.Equal(new AssetRef<Material>[] { h.MA, h.MB, default }, h.A.Materials);
        Assert.Equal(new AssetRef<Material>[] { h.MB, h.MA, default }, h.B.Materials); Assert.False(Undo.CanUndo);
        Undo.PerformRedo(); h.F.Frame(); Assert.Same(h.MA, h.A.Materials[2].Res); Assert.Same(h.MB, h.B.Materials[2].Res);
        Assert.Same(listA, h.A.Materials); Assert.Same(listB, h.B.Materials); Assert.Same(paths, h.A.BonePaths);
        Assert.Equal(mesh, h.A.SharedMesh); Assert.Equal(root, h.A.RootBonePath);
    }

    [Fact]
    public void BackwardSingleGestureAndAdjacentNoOpKeepRedo()
    {
        using var h = new Harness(); ThreeSlots(h); Selection.Select(h.A.GameObject); h.F.Frame(); h.F.Frame();
        BeginReorder(h, 2); HoverReorder(h, 0, true); ReleaseReorder(h);
        Assert.Equal(new AssetRef<Material>[] { default, h.MA, h.MB }, h.A.Materials);
        Assert.Equal(new AssetRef<Material>[] { h.MB, h.MA, default }, h.B.Materials);
        Undo.PerformUndo(); h.F.Frame(); EditorSceneManager.IsDirty = false;
        foreach (bool before in new[] { true, false })
        { BeginReorder(h, 0); HoverReorder(h, 0, before); ReleaseReorder(h); }
        Assert.False(Undo.CanUndo); Assert.True(Undo.CanRedo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void UnequalListReorderKeepsNonCommonSuffixAndEqualValuesMakeNoHistory()
    {
        using var h = new Harness(); ThreeSlots(h); h.B.Materials.Add(h.MB); h.F.Frame();
        BeginReorder(h, 0); HoverReorder(h, 2, false); ReleaseReorder(h);
        Assert.Equal(new AssetRef<Material>[] { h.MA, default, h.MB, h.MB }, h.B.Materials);
        Undo.PerformUndo(); h.F.Frame(); EditorSceneManager.IsDirty = false;
        for (int i = 0; i < h.A.Materials.Count; i++) h.A.Materials[i] = h.MA;
        for (int i = 0; i < h.B.Materials.Count; i++) h.B.Materials[i] = h.MB;
        h.F.Frame(); BeginReorder(h, 0); HoverReorder(h, 2, false); ReleaseReorder(h);
        Assert.False(Undo.CanUndo); Assert.True(Undo.CanRedo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ChangedListDuringDragRejectsWholeOperation(bool resize)
    {
        using var h = new Harness(); ThreeSlots(h); BeginReorder(h, 0);
        if (resize) h.B.Materials.Add(h.MA); else h.B.Materials[1] = h.MB;
        HoverReorder(h, 2, false); Assert.Equal(DragVisualMode.Rejected, DragDrop.VisualMode); ReleaseReorder(h);
        Assert.Same(h.MA, h.A.Materials[0].Res); Assert.Same(h.MB, h.B.Materials[0].Res);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RemovedOrReplacedComponentRejectsCapturedReorder(bool replace)
    {
        using var h = new Harness(); ThreeSlots(h);
        var payload = SkinnedMeshRendererEditor.MaterialSlotDragPayload.Capture(h.Targets, 0)!;
        Guid id = h.B.Identifier; h.B.GameObject.RemoveComponent(h.B);
        if (replace)
        {
            var replacement = h.B.GameObject.AddComponent<SkinnedMeshRenderer>(); replacement.Materials.Add(h.MA);
            typeof(MonoBehaviour).GetField("_identifier", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(replacement, id);
        }
        Assert.False(SkinnedMeshRendererEditor.TryReorderMaterials(payload, 3));
        Assert.Same(h.MA, h.A.Materials[0].Res); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void SelectionChangeEscapeAndOutsideDropCannotReorder()
    {
        using var h = new Harness(); ThreeSlots(h); BeginReorder(h, 0);
        Selection.Select(h.A.GameObject); h.F.Frame(); h.F.Frame();
        HoverReorder(h, 2, false); Assert.Equal(DragVisualMode.Rejected, DragDrop.VisualMode); ReleaseReorder(h);
        BeginReorder(h, 0); HoverReorder(h, 2, false); h.Key(PaperKey.Escape); ReleaseReorder(h);
        Assert.Null(DragDrop.Payload);
        BeginReorder(h, 0); h.F.Paper.SetPointerPosition(300, 850); h.F.Frame(); ReleaseReorder(h);
        Assert.Same(h.MA, h.A.Materials[0].Res); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void ReorderUndoIsIndependentFromSlotAssignmentAndInvalidCaptureRejects()
    {
        using var h = new Harness(); ThreeSlots(h);
        Assert.Null(SkinnedMeshRendererEditor.MaterialSlotDragPayload.Capture([], 0));
        Assert.Null(SkinnedMeshRendererEditor.MaterialSlotDragPayload.Capture(h.Targets, -1));
        Assert.Null(SkinnedMeshRendererEditor.MaterialSlotDragPayload.Capture(h.Targets, 3));
        BeginReorder(h, 0); HoverReorder(h, 2, false); ReleaseReorder(h);
        SkinnedMeshRendererEditor.TryAssignMaterialSlot(h.Targets, 0, default); Undo.FlushFrame();
        Undo.PerformUndo(); Assert.Same(h.MB, h.A.Materials[0].Res); Assert.Same(h.MA, h.B.Materials[0].Res);
        Undo.PerformUndo(); Assert.Same(h.MA, h.A.Materials[0].Res); Assert.Same(h.MB, h.B.Materials[0].Res); Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void ReadOnlyAndUnrelatedPayloadDoNotChangeMaterialOrder()
    {
        using var h = new Harness(); ThreeSlots(h); BeginReorder(h, 0);
        Origami.BeginReadOnly();
        try { HoverReorder(h, 2, false); Assert.Equal(DragVisualMode.Rejected, DragDrop.VisualMode); ReleaseReorder(h); }
        finally { Origami.EndReadOnly(); }
        DragDrop.StartDrag(new GameObjectDragPayload(h.A.GameObject));
        HoverReorder(h, 2, false); ReleaseReorder(h);
        Assert.Same(h.MA, h.A.Materials[0].Res); Assert.Same(h.MB, h.B.Materials[0].Res);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
    }
}
