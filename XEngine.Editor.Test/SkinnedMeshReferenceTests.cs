using System.Reflection;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Popups;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Inspector;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;
using Fixture = XEngine.Editor.Test.InspectorComponentHeaderTests.Fixture;

namespace XEngine.Editor.Test;

public sealed class SkinnedMeshReferenceTests
{
    private static readonly FieldInfo StatefulClick = typeof(XEngine.PaperUI.LayoutEngine.ElementData)
        .GetField("SfClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private sealed class Backend(EngineObject asset) : AssetBackendBase
    { protected override EngineObject? LoadFresh(Guid id) => asset; }

    private sealed class Harness : IDisposable
    {
        internal readonly Fixture F;
        internal readonly GameObject Root = new("Model"), Hips;
        internal readonly SkinnedMeshRenderer A, B;
        internal readonly Mesh MA = new() { Name = "SameName" }, MB = new() { Name = "SameName" };
        internal readonly object[] Targets;
        internal Harness(float width = 335)
        {
            EditorRegistries.Initialize(); F = new Fixture(width);
            Hips = Child("Hips", Root);
            A = Child("Body", Root).AddComponent<SkinnedMeshRenderer>();
            B = Child("Face", Root).AddComponent<SkinnedMeshRenderer>();
            A.SetBones([Hips.Transform], Hips.Transform); B.SetBones([Hips.Transform], Hips.Transform);
            A.SharedMesh = MA; B.SharedMesh = MB; Targets = [A, B]; F.Scene.Add(Root);
            Selection.Select(A.GameObject); Selection.AddToSelection(B.GameObject);
            F.Frame(); F.Frame(); Undo.Clear(); EditorSceneManager.IsDirty = false;
        }
        internal void Open()
        {
            var label = F.Text("Mesh"); var picker = Picker(label.Y);
            F.Click(picker.X + 9, picker.Y + 9); Assert.True(SelectorModal.IsOpen);
        }
        internal XEngine.PaperUI.LayoutEngine.ElementData Picker(float y)
            => F.Elements().Single(e => e.Paragraph == EditorIcons.CircleDot
                && (e.OnClick != null || StatefulClick.GetValue(e) != null) && MathF.Abs(e.Y - y) < .1f);
        internal void None()
        {
            var row = F.Text($"{EditorIcons.Circle}  None (Mesh)"); F.Click(row.X + 30, row.Y + 9);
            Assert.False(SelectorModal.IsOpen);
        }
        internal void Drop(EngineObject value, Type hint)
        {
            var label = F.Text("Mesh"); var picker = Picker(label.Y);
            float x = picker.X - 12, y = label.Y + 9;
            F.Paper.SetPointerPosition(x, y); F.Frame();
            F.Frame(() => { F.Paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false); DragDrop.StartDrag(new AssetDragPayload(value.AssetID, value.Name, hint)); });
            F.Frame(() => F.Paper.SetPointerState(PaperMouseBtn.Left, x, y, false, false)); F.Frame(); F.Frame();
        }
        public void Dispose() { SelectorModal.Close(); F.Dispose(); MA.Dispose(); MB.Dispose(); }
    }

    [Theory] [InlineData(240f)] [InlineData(335f)] [InlineData(520f)]
    public void SameNamesDifferentMeshInstancesDisplayMixedAndPickerFits(float width)
    {
        using var h = new Harness(width); Assert.False(h.F.HasText("Shared Mesh"));
        var label = h.F.Text("Mesh"); var dash = h.F.Text("—", label.Y); var picker = h.Picker(label.Y);
        Assert.True(dash.X + dash.LayoutWidth <= picker.X); Assert.True(picker.X + picker.LayoutWidth <= width);
        h.Open(); h.None(); Assert.True(h.F.HasText("None (Mesh)"));
        Assert.True(h.A.SharedMesh.IsExplicitNull); Assert.True(h.B.SharedMesh.IsExplicitNull);
        Undo.PerformUndo(); h.F.Frame(); Assert.Same(h.MA, h.A.SharedMesh.Res); Assert.Same(h.MB, h.B.SharedMesh.Res);
        Assert.False(Undo.CanUndo); Undo.PerformRedo(); Assert.True(h.A.SharedMesh.IsExplicitNull); Assert.True(h.B.SharedMesh.IsExplicitNull);
    }

    [Fact]
    public void ReferenceIdentityUsesGuidBeforeRuntimeCacheAndNeverName()
    {
        using var h = new Harness(); Guid id = Guid.NewGuid(); h.MA.AssetID = id; h.MB.AssetID = id; h.F.Frame();
        Assert.True(h.F.HasText("SameName (Mesh)")); Assert.False(h.F.HasText("—"));
        h.MB.AssetID = Guid.NewGuid(); h.F.Frame(); Assert.True(h.F.HasText("—"));
    }

    [Fact]
    public void SingleMeshPickerAndDebugUseTheirRespectiveLabels()
    {
        using var h = new Harness(); Selection.Select(h.A.GameObject); h.F.Frame(); h.F.Frame();
        h.Open(); h.None(); Assert.True(h.A.SharedMesh.IsExplicitNull); Assert.Same(h.MB, h.B.SharedMesh.Res);
        Undo.PerformUndo(); h.F.Frame(); Assert.Same(h.MA, h.A.SharedMesh.Res); Assert.False(Undo.CanUndo);
        typeof(XEngine.Editor.GUI.Panels.InspectorPanel).GetField("_debugMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(h.F.Panel, true);
        h.F.Frame(); Assert.True(h.F.HasText("Shared Mesh")); Assert.False(h.F.HasText("Mesh"));
    }

    [Fact]
    public void UnresolvedGuidIsDistinctFromExplicitNone()
    {
        using var h = new Harness(); Guid id = Guid.NewGuid(); h.A.SharedMesh = new AssetRef<Mesh>(id); h.B.SharedMesh = new AssetRef<Mesh>(id); h.F.Frame();
        Assert.True(h.F.HasText("Unresolved (Mesh)")); Assert.False(h.F.HasText("None (Mesh)"));
        h.Open(); h.None(); Undo.PerformUndo(); Assert.Equal(id, h.A.SharedMesh.AssetID); Assert.Equal(id, h.B.SharedMesh.AssetID);
    }

    [Fact]
    public void AssignmentsAreIndependentGroupsAndPreserveRootPaletteMaterials()
    {
        using var h = new Harness(); var pathsA = h.A.BonePaths; var pathsB = h.B.BonePaths;
        var matsA = h.A.Materials; var matsB = h.B.Materials;
        Undo.Snapshot(h.A); Undo.Snapshot(h.B);
        Assert.True(SkinnedMeshRendererEditor.TryAssignMeshes(h.Targets, h.MA)); Undo.FlushFrame();
        Assert.True(SkinnedMeshRendererEditor.TryAssignMeshes(h.Targets, default)); Undo.FlushFrame();
        h.A.Enabled = false; h.B.Enabled = false;
        Undo.PerformUndo(); Assert.Same(h.MA, h.A.SharedMesh.Res); Assert.Same(h.MA, h.B.SharedMesh.Res);
        Undo.PerformUndo(); Assert.Same(h.MA, h.A.SharedMesh.Res); Assert.Same(h.MB, h.B.SharedMesh.Res); Assert.False(Undo.CanUndo);
        Assert.Same(pathsA, h.A.BonePaths); Assert.Same(pathsB, h.B.BonePaths); Assert.Equal("Hips", h.A.RootBonePath); Assert.Equal("Hips", h.B.RootBonePath);
        Assert.Same(matsA, h.A.Materials); Assert.Same(matsB, h.B.Materials); Assert.False(h.A.Enabled); Assert.False(h.B.Enabled);
    }

    [Fact]
    public void NoOpAndDisposedRuntimeMeshRetainCleanStateAndRedo()
    {
        using var h = new Harness(); SkinnedMeshRendererEditor.TryAssignMeshes(h.Targets, default); Undo.FlushFrame(); Undo.PerformUndo();
        h.B.SharedMesh = h.MA; EditorSceneManager.IsDirty = false;
        Assert.True(SkinnedMeshRendererEditor.TryAssignMeshes(h.Targets, h.MA));
        using var dead = new Mesh(); dead.Dispose(); Assert.False(SkinnedMeshRendererEditor.TryAssignMeshes(h.Targets, dead)); Undo.FlushFrame();
        Assert.False(Undo.CanUndo); Assert.True(Undo.CanRedo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void DeferredPickerRejectsRemovedOrReplacedSetAtomically(bool replace)
    {
        using var h = new Harness(); h.Open(); var id = h.B.Identifier; h.B.GameObject.RemoveComponent(h.B);
        SkinnedMeshRenderer? replacement = null;
        if (replace)
        {
            replacement = h.B.GameObject.AddComponent<SkinnedMeshRenderer>(); replacement.SharedMesh = h.MA;
            typeof(MonoBehaviour).GetField("_identifier", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(replacement, id);
        }
        h.None(); Assert.Same(h.MA, h.A.SharedMesh.Res); if (replace) Assert.Same(h.MA, replacement!.SharedMesh.Res);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void PickerRetainsOriginalSetAcrossSelectionChange()
    {
        using var h = new Harness(); h.Open(); var c = Child("Third", h.Root).AddComponent<SkinnedMeshRenderer>(); c.SharedMesh = h.MA;
        Selection.Select(h.A.GameObject); Selection.AddToSelection(c.GameObject); h.F.Frame(); h.F.Frame(); h.None();
        Assert.True(h.A.SharedMesh.IsExplicitNull); Assert.True(h.B.SharedMesh.IsExplicitNull); Assert.Same(h.MA, c.SharedMesh.Res);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void ActualAssetDropValidatesResolvedTypeAndGroupsUndo(bool valid)
    {
        using var h = new Harness(); var previous = Runtime.AssetDatabase.Current;
        using EngineObject asset = valid ? new Mesh { Name = "Dropped", AssetID = Guid.NewGuid() }
            : new Material { Name = "WrongType", AssetID = Guid.NewGuid() };
        Runtime.AssetDatabase.Current = new Backend(asset);
        try
        {
            h.Drop(asset, typeof(Mesh));
            if (valid)
            {
                Assert.Same(asset, h.A.SharedMesh.Res); Assert.Same(asset, h.B.SharedMesh.Res);
                Undo.PerformUndo(); Assert.Same(h.MA, h.A.SharedMesh.Res); Assert.Same(h.MB, h.B.SharedMesh.Res); Assert.False(Undo.CanUndo);
            }
            else
            { Assert.Same(h.MA, h.A.SharedMesh.Res); Assert.Same(h.MB, h.B.SharedMesh.Res); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty); }
        }
        finally { Runtime.AssetDatabase.Current = previous; }
    }

    private static GameObject Child(string name, GameObject parent)
    { var go = new GameObject(name); go.SetParent(parent, false); return go; }
}
