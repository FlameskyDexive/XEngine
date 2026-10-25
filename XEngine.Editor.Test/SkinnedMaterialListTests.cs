using System.Reflection;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Popups;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Inspector;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using Xunit;
using Fixture = XEngine.Editor.Test.InspectorComponentHeaderTests.Fixture;

namespace XEngine.Editor.Test;

public sealed partial class SkinnedMaterialListTests
{
    private static readonly FieldInfo StatefulClick = typeof(ElementData).GetField("SfClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private sealed class Backend(EngineObject asset) : AssetBackendBase
    { protected override EngineObject? LoadFresh(Guid id) => asset; }
    private sealed class Harness : IDisposable
    {
        internal readonly Fixture F;
        internal readonly GameObject Root = new("Model");
        internal readonly SkinnedMeshRenderer A, B;
        internal readonly Material MA = new() { Name = "SameName" }, MB = new() { Name = "SameName" };
        internal readonly object[] Targets;
        internal Harness(float width = 335)
        {
            EditorRegistries.Initialize(); F = new Fixture(width);
            var hips = Child("Hips", Root);
            A = Child("Body", Root).AddComponent<SkinnedMeshRenderer>(); B = Child("Face", Root).AddComponent<SkinnedMeshRenderer>();
            A.SetBones([hips.Transform], hips.Transform); B.SetBones([hips.Transform], hips.Transform);
            A.Materials.Add(MA); B.Materials.Add(MB); Targets = [A, B]; F.Scene.Add(Root);
            Selection.Select(A.GameObject); Selection.AddToSelection(B.GameObject); F.Frame(); F.Frame(); Undo.Clear(); EditorSceneManager.IsDirty = false;
        }
        internal ElementData Picker(int slot = 0)
        {
            float y = F.Text($"Element {slot}").Y;
            return F.Elements().Single(e => e.Paragraph == EditorIcons.CircleDot
                && (e.OnClick != null || StatefulClick.GetValue(e) != null) && MathF.Abs(e.Y - y) < .1f);
        }
        internal ElementData Size() => F.Elements().Single(e => e.IsAHookedParent && MathF.Abs(e.Y - F.Text("Materials").Y) < .1f);
        internal void DraftSize(string text)
        {
            var row = Size(); F.Click(row.X + 10, row.Y + 9); F.Frame(() => F.Paper.AddInputCharacter(text)); F.Frame();
        }
        internal void Key(PaperKey key)
        { F.Frame(() => F.Paper.SetKeyState(key, true)); F.Frame(() => F.Paper.SetKeyState(key, false)); F.Frame(); }
        internal void Open(int slot = 0) { var p = Picker(slot); F.Click(p.X + 9, p.Y + 9); Assert.True(SelectorModal.IsOpen); }
        internal void None()
        {
            var row = F.Text($"{EditorIcons.Circle}  None (Material)"); F.Click(row.X + 30, row.Y + 9); Assert.False(SelectorModal.IsOpen);
        }
        internal void Button(string text)
        {
            var button = F.Text(text);
            Assert.Equal(24, button.LayoutWidth); Assert.Equal(F.Text("Materials").LayoutHeight, button.LayoutHeight);
            F.Click(button.X + button.LayoutWidth / 2, button.Y + button.LayoutHeight / 2);
        }
        internal void Drop(EngineObject asset)
        {
            var p = Picker(); float x = p.X - 12, y = p.Y + 9;
            F.Paper.SetPointerPosition(x, y); F.Frame();
            F.Frame(() => { F.Paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false); DragDrop.StartDrag(new AssetDragPayload(asset.AssetID, asset.Name, typeof(Material))); });
            F.Frame(() => F.Paper.SetPointerState(PaperMouseBtn.Left, x, y, false, false)); F.Frame(); F.Frame();
        }
        public void Dispose() { SelectorModal.Close(); F.Dispose(); MA.Dispose(); MB.Dispose(); }
    }

    [Theory] [InlineData(240f)] [InlineData(335f)] [InlineData(520f)]
    public void InlineMixedSlotCountFoldAndFooterHaveIndependentTargets(float width)
    {
        using var h = new Harness(width); var label = h.F.Text("Element 0"); var dash = h.F.Text("—", label.Y); var picker = h.Picker();
        Assert.True(dash.X + dash.LayoutWidth <= picker.X); Assert.True(picker.X + picker.LayoutWidth <= width);
        var size = h.Size(); Assert.InRange(size.X + size.LayoutWidth, 1, width); Assert.Equal(44, size.LayoutWidth);
        h.F.Click(size.X + 8, size.Y + 9); Assert.True(h.F.HasText("Element 0")); h.Key(PaperKey.Escape);
        var title = h.F.Text("Materials"); h.F.Click(title.X + 15, title.Y + 9); Assert.False(h.F.HasText("Element 0"));
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        title = h.F.Text("Materials"); h.F.Click(title.X + 15, title.Y + 9); Assert.True(h.F.HasText("Element 0"));
        h.Button("+"); Assert.Equal(2, h.A.Materials.Count); Assert.True(h.F.HasText("Element 1"));
        Undo.PerformUndo(); h.F.Frame(); Assert.Single(h.A.Materials); Assert.Single(h.B.Materials); Assert.False(Undo.CanUndo);
        h.Open(); h.None(); Assert.True(h.A.Materials[0].IsExplicitNull); Assert.True(h.B.Materials[0].IsExplicitNull);
        Undo.PerformUndo(); h.F.Frame(); Assert.Same(h.MA, h.A.Materials[0].Res); Assert.Same(h.MB, h.B.Materials[0].Res);
        Assert.False(Undo.CanUndo); Undo.PerformRedo(); Assert.True(h.A.Materials[0].IsExplicitNull); Assert.True(h.B.Materials[0].IsExplicitNull);
    }

    [Fact]
    public void MaterialIdentityUsesGuidOrInstanceAndDebugKeepsRawList()
    {
        using var h = new Harness(); Guid id = Guid.NewGuid(); h.MA.AssetID = id; h.MB.AssetID = id; h.F.Frame();
        Assert.True(h.F.HasText("SameName (Material)"));
        h.MB.AssetID = Guid.NewGuid(); h.F.Frame(); Assert.True(h.F.HasText("—"));
        Selection.Select(h.A.GameObject); h.F.Frame(); h.F.Frame(); h.Open(); h.None(); Assert.Same(h.MB, h.B.Materials[0].Res);
        Undo.PerformUndo(); h.F.Frame(); Assert.Same(h.MA, h.A.Materials[0].Res);
        typeof(XEngine.Editor.GUI.Panels.InspectorPanel).GetField("_debugMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(h.F.Panel, true);
        h.F.Frame(); Assert.True(h.F.HasText("Materials")); Assert.False(h.F.HasText("Element 0"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void DelayedSizeCommitsOnceByEnterOrBlur(bool blur)
    {
        using var h = new Harness(); h.DraftSize("3"); Assert.Single(h.A.Materials); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        if (blur) h.F.Click(300, 850); else h.Key(PaperKey.Enter);
        Assert.Equal(3, h.A.Materials.Count); Assert.Equal(3, h.B.Materials.Count);
        Assert.Same(h.MA, h.A.Materials[0].Res); Assert.Same(h.MB, h.B.Materials[0].Res);
        Assert.True(h.A.Materials[1].IsExplicitNull); Assert.True(h.B.Materials[2].IsExplicitNull);
        Undo.PerformUndo(); h.F.Frame(); Assert.Single(h.A.Materials); Assert.Single(h.B.Materials); Assert.False(Undo.CanUndo);
        Undo.PerformRedo(); h.F.Frame(); Assert.Equal(3, h.A.Materials.Count); Assert.Equal(3, h.B.Materials.Count);
    }

    [Fact]
    public void SizeCancelInvalidAndNoOpPreserveCleanStateAndRedo()
    {
        using var h = new Harness(); SkinnedMeshRendererEditor.TryResizeMaterials(h.Targets, 2); Undo.FlushFrame(); Undo.PerformUndo(); EditorSceneManager.IsDirty = false;
        h.DraftSize("5"); h.Key(PaperKey.Escape); Assert.Single(h.A.Materials);
        foreach (int count in new[] { -1, 1025, int.MaxValue, int.MinValue }) Assert.False(SkinnedMeshRendererEditor.TryResizeMaterials(h.Targets, count));
        Assert.True(SkinnedMeshRendererEditor.TryResizeMaterials(h.Targets, 1));
        using var disposed = new Material(); disposed.Dispose(); Assert.False(SkinnedMeshRendererEditor.TryAssignMaterialSlot(h.Targets, 0, disposed));
        Undo.FlushFrame(); Assert.False(Undo.CanUndo); Assert.True(Undo.CanRedo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void UnequalListsShowSharedSlotsAndResizePreservesPerTargetOrder()
    {
        using var h = new Harness(); h.B.Materials.Add(h.MA); h.F.Frame();
        Assert.True(h.F.HasText("Shared slots: 1")); Assert.False(h.F.HasText("Element 1"));
        h.Button("+"); Assert.Equal(2, h.A.Materials.Count); Assert.Equal(3, h.B.Materials.Count);
        Assert.True(h.A.Materials[1].IsExplicitNull); Assert.Same(h.MA, h.B.Materials[1].Res);
        h.Button("−"); Assert.Single(h.A.Materials); Assert.Equal(2, h.B.Materials.Count);
        Undo.PerformUndo(); h.F.Frame(); Assert.Equal(2, h.A.Materials.Count); Assert.Equal(3, h.B.Materials.Count);
        Undo.PerformUndo(); h.F.Frame(); Assert.Single(h.A.Materials); Assert.Equal(2, h.B.Materials.Count); Assert.False(Undo.CanUndo);
        h.DraftSize("1"); h.Key(PaperKey.Enter); Assert.Single(h.B.Materials);
        Undo.PerformUndo(); h.F.Frame(); Assert.Equal(2, h.B.Materials.Count); Assert.Same(h.MB, h.B.Materials[0].Res); Assert.Same(h.MA, h.B.Materials[1].Res);
    }

    [Fact]
    public void SlotGroupsPreserveOtherSlotsMeshRootPaletteAndListInstances()
    {
        using var h = new Harness(); h.A.Materials.Add(h.MB); h.B.Materials.Add(h.MA);
        var aList = h.A.Materials; var bList = h.B.Materials; var pathsA = h.A.BonePaths; var pathsB = h.B.BonePaths;
        var meshA = h.A.SharedMesh; var meshB = h.B.SharedMesh;
        Undo.Snapshot(h.A); Undo.Snapshot(h.B);
        SkinnedMeshRendererEditor.TryAssignMaterialSlot(h.Targets, 0, h.MA); Undo.FlushFrame();
        SkinnedMeshRendererEditor.TryAssignMaterialSlot(h.Targets, 0, default); Undo.FlushFrame();
        h.A.Materials[1] = default; h.B.Enabled = false;
        Undo.PerformUndo(); Assert.Same(h.MA, h.A.Materials[0].Res); Assert.Same(h.MA, h.B.Materials[0].Res);
        Undo.PerformUndo(); Assert.Same(h.MA, h.A.Materials[0].Res); Assert.Same(h.MB, h.B.Materials[0].Res); Assert.False(Undo.CanUndo);
        Assert.True(h.A.Materials[1].IsExplicitNull); Assert.Same(h.MA, h.B.Materials[1].Res); Assert.False(h.B.Enabled);
        Assert.Same(aList, h.A.Materials); Assert.Same(bList, h.B.Materials); Assert.Same(pathsA, h.A.BonePaths); Assert.Same(pathsB, h.B.BonePaths);
        Assert.Equal(meshA, h.A.SharedMesh); Assert.Equal(meshB, h.B.SharedMesh); Assert.Equal("Hips", h.A.RootBonePath); Assert.Equal("Hips", h.B.RootBonePath);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void DeferredPickerRejectsRemovedOrReplacedTargetAtomically(bool replace)
    {
        using var h = new Harness(); h.Open(); var id = h.B.Identifier; h.B.GameObject.RemoveComponent(h.B);
        if (replace)
        {
            var replacement = h.B.GameObject.AddComponent<SkinnedMeshRenderer>(); replacement.Materials.Add(h.MA);
            typeof(MonoBehaviour).GetField("_identifier", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(replacement, id);
        }
        h.None(); Assert.Same(h.MA, h.A.Materials[0].Res); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        Assert.False(SkinnedMeshRendererEditor.TryResizeMaterials(h.Targets, 2)); Assert.Single(h.A.Materials);
    }

    [Fact]
    public void MissingSlotRejectsWholeSetAndPickerOwnsOriginalSelection()
    {
        using var h = new Harness(); h.Open(); h.B.Materials.Clear(); h.None();
        Assert.Same(h.MA, h.A.Materials[0].Res); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        h.B.Materials.Add(h.MB); h.F.Frame(); h.Open();
        var c = Child("Third", h.Root).AddComponent<SkinnedMeshRenderer>(); c.Materials.Add(h.MA);
        Selection.Select(h.A.GameObject); Selection.AddToSelection(c.GameObject); h.F.Frame(); h.F.Frame(); h.None();
        Assert.True(h.A.Materials[0].IsExplicitNull); Assert.True(h.B.Materials[0].IsExplicitNull); Assert.Same(h.MA, c.Materials[0].Res);
    }

    [Fact]
    public void SizeDraftCannotFollowSelectionAndZeroSlotsDisableRemove()
    {
        using var h = new Harness(); Selection.Select(h.A.GameObject); h.F.Frame(); h.F.Frame(); h.DraftSize("5");
        Selection.Select(h.B.GameObject); h.F.Frame(); h.F.Frame(); h.Key(PaperKey.Enter); Assert.Single(h.A.Materials); Assert.Single(h.B.Materials);
        h.DraftSize("0"); h.Key(PaperKey.Enter); Assert.Empty(h.B.Materials); Assert.False(h.F.HasText("Element 0"));
        h.Button("−"); Assert.Empty(h.B.Materials); Undo.PerformUndo(); h.F.Frame(); Assert.Single(h.B.Materials); Assert.False(Undo.CanUndo);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void ActualCompatibleDropValidatesResolvedTypeAndUndo(bool valid)
    {
        using var h = new Harness(); var previous = Runtime.AssetDatabase.Current;
        using EngineObject asset = valid ? new Material { Name = "Dropped", AssetID = Guid.NewGuid() } : new Mesh { Name = "Wrong", AssetID = Guid.NewGuid() };
        Runtime.AssetDatabase.Current = new Backend(asset);
        try
        {
            h.Drop(asset);
            if (valid)
            {
                Assert.Same(asset, h.A.Materials[0].Res); Assert.Same(asset, h.B.Materials[0].Res);
                Undo.PerformUndo(); Assert.Same(h.MA, h.A.Materials[0].Res); Assert.Same(h.MB, h.B.Materials[0].Res); Assert.False(Undo.CanUndo);
            }
            else { Assert.Same(h.MA, h.A.Materials[0].Res); Assert.Same(h.MB, h.B.Materials[0].Res); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty); }
        }
        finally { Runtime.AssetDatabase.Current = previous; }
    }

    private static GameObject Child(string name, GameObject parent)
    { var go = new GameObject(name); go.SetParent(parent, false); return go; }
}
