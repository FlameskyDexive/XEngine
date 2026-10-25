// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Ai.Host;
using XEngine.Data.Provenance;
using XEngine.Echo;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// ★ Gen-P0.2/P0.3 — Pins the offline writer/reader against the EDITOR's real save format:
/// child GameObjects nested inline under the parent's Children plus {$id,$type} reference stubs in
/// the flat array, component _identifier in Echo's compact Guid form, and the parallel identity
/// arrays (_goIdentifiers/_compIdentifiers/_compIdOffsets) the engine restores stable Guids from.
/// GameObject.Deserialize always mints fresh Guids, and Scene.OnAfterDeserialize skips the restore
/// when array lengths mismatch — so every structural offline edit MUST keep the arrays aligned.
/// </summary>
public sealed class SceneStructureTests : IDisposable
{
    private const string RigGuid = "11111111-2222-3333-4444-555555555555";
    private const string HipsGuid = "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb";
    private const string AnimatorCompGuid = "cccccccc-1111-2222-3333-dddddddddddd";

    private readonly string _projectRoot;
    private readonly string _sceneAbs;
    private const string SceneRel = "Assets/Scenes/Rig.scene";
    private readonly SceneWriter _writer;

    public SceneStructureTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "xengine-scene-structure-" + Guid.NewGuid().ToString("N"));
        string journalRoot = Path.Combine(_projectRoot, ".xengine", "journal");
        Directory.CreateDirectory(journalRoot);
        string sceneDir = Path.Combine(_projectRoot, "Assets", "Scenes");
        Directory.CreateDirectory(sceneDir);
        _sceneAbs = Path.Combine(sceneDir, "Rig.scene");
        File.WriteAllText(_sceneAbs, EditorFormatScene());
        _writer = new SceneWriter(new ChangeJournal(journalRoot, "scene-structure-tests"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { /* test cleanup */ }
    }

    private static ChangeProvenance Prov(string key) => ChangeProvenance.ForTool("test.structure", null, key);

    private EchoObject ReadRoot() => EchoObject.ReadFromString(File.ReadAllText(_sceneAbs));

    /// <summary>Editor-shaped scene: root "Rig" (one Animator component, compact-Guid _identifier)
    /// with child "Hips" inline under Children; the flat array carries Rig's body + Hips' stub; the
    /// three parallel identity arrays are present and aligned.</summary>
    private static string EditorFormatScene() => $$"""
        {
          "$id": 1,
          "$type": "XEngine.Runtime.Resources.Scene, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
          "serializeObj": {
            "$id": 2,
            "$type": "XEngine.Runtime.GameObject[], XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            "array": [
              {
                "$id": 3,
                "$type": "XEngine.Runtime.GameObject, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                "Name": "Rig",
                "AssetPath": "",
                "Identifier": "{{RigGuid}}",
                "Static": 0B,
                "Enabled": 1B,
                "EnabledInHierarchy": 1B,
                "TagIndex": 0,
                "LayerIndex": 0,
                "HideFlags": 0,
                "Transform": {
                  "$id": 4,
                  "$type": "XEngine.Vector.Transform, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                  "_localPosition": { "X": 0F, "Y": 0F, "Z": 0F },
                  "_localScale": { "X": 1F, "Y": 1F, "Z": 1F },
                  "_localRotation": { "X": 0F, "Y": 0F, "Z": 0F, "W": 1F }
                },
                "Components": [
                  {
                    "$id": 5,
                    "$type": "XEngine.Runtime.Animator, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                    "Speed": 1F,
                    "_identifier": { "$t": "g", "$v": "{{AnimatorCompGuid}}" },
                    "_enabled": true,
                    "_enabledInHierarchy": true,
                    "HideFlags": 0L,
                    "AssetPath": "",
                    "AssetID": { "$t": "g", "$v": "00000000-0000-0000-0000-000000000000" },
                    "Name": "NewAnimator"
                  }
                ],
                "Children": [
                  {
                    "$id": 14,
                    "$type": "XEngine.Runtime.GameObject, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                    "Name": "Hips",
                    "AssetPath": "",
                    "Identifier": "{{HipsGuid}}",
                    "Static": 0B,
                    "Enabled": 1B,
                    "EnabledInHierarchy": 1B,
                    "TagIndex": 0,
                    "LayerIndex": 0,
                    "HideFlags": 0,
                    "Transform": {
                      "$id": 15,
                      "$type": "XEngine.Vector.Transform, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                      "_localPosition": { "X": 0F, "Y": 1F, "Z": 0F },
                      "_localScale": { "X": 1F, "Y": 1F, "Z": 1F },
                      "_localRotation": { "X": 0F, "Y": 0F, "Z": 0F, "W": 1F }
                    },
                    "Components": [],
                    "Children": []
                  }
                ]
              },
              {
                "$id": 14,
                "$type": "XEngine.Runtime.GameObject, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
              }
            ]
          },
          "_goIdentifiers": {
            "$id": 21,
            "$type": "System.Guid[], System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e",
            "array": [
              { "$t": "g", "$v": "{{RigGuid}}" },
              { "$t": "g", "$v": "{{HipsGuid}}" }
            ]
          },
          "_compIdentifiers": {
            "$id": 22,
            "$type": "System.Guid[], System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e",
            "array": [
              { "$t": "g", "$v": "{{AnimatorCompGuid}}" }
            ]
          },
          "_compIdOffsets": {
            "$id": 23,
            "$type": "System.Int32[], System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e",
            "array": [ 0, 1, 1 ]
          },
          "Name": "Rig"
        }
        """;

    // ---- helpers on the raw Echo tree ----

    private static List<EchoObject> FlatArray(EchoObject root)
        => root.Get("serializeObj")!.Get("array")!.List;

    private static List<EchoObject> GuidArray(EchoObject root, string key)
        => root.Get(key)!.Get("array")!.List;

    private static string GuidAt(EchoObject root, string key, int index)
        => GuidArray(root, key)[index].Get("$v")!.StringValue;

    private static List<int> IntArray(EchoObject root, string key)
    {
        var list = new List<int>();
        foreach (EchoObject v in root.Get(key)!.Get("array")!.List) list.Add(v.IntValue);
        return list;
    }

    // ---- hierarchy read: stubs resolved, children nested, compact Guid identifiers ----

    [Fact]
    public void Hierarchy_ResolvesStubs_NestsChildren_ReadsCompactGuidIdentifiers()
    {
        var hierarchy = SceneReader.ReadHierarchy(_sceneAbs);

        Assert.Equal(2, hierarchy.TotalObjects);
        var rig = Assert.Single(hierarchy.RootObjects); // the {$id:14} stub must NOT surface as a phantom root
        Assert.Equal("Rig", rig.Name);
        Assert.Equal(RigGuid, rig.Identifier);

        string childId = Assert.Single(rig.ChildIdentifiers);
        Assert.Equal(HipsGuid, childId);
        Assert.NotNull(rig.Children);
        var hips = Assert.Single(rig.Children!);
        Assert.Equal("Hips", hips.Name);
        Assert.Equal(1f, hips.Transform.Y);

        var animator = Assert.Single(rig.Components);
        Assert.Equal(AnimatorCompGuid, animator.Identifier); // {$t:"g"} form must be readable
    }

    // ---- create under a parent: inline body + flat stub + rebuilt arrays ----

    [Fact]
    public async Task Create_UnderNestedParent_WritesBodyStubAndArrays()
    {
        await _writer.CreateAsync(_sceneAbs, SceneRel, "Toe", HipsGuid, Prov("create-toe-1"), dryRun: false);

        EchoObject root = ReadRoot();
        List<EchoObject> flat = FlatArray(root);
        Assert.Equal(3, flat.Count);

        // The new entry in the flat array is a {$id,$type} stub (no body).
        EchoObject stub = flat[2];
        Assert.False(EchoFacade.IsFullGameObjectNode(stub));
        int stubEchoId = stub.Get("$id")!.IntValue;
        Assert.True(stubEchoId > 15, "new $id must be minted above the scene's existing max");

        // The body lives inline under Hips' Children and carries the same $id.
        EchoObject rig = flat[0];
        EchoObject hips = rig.Get("Children")!.List[0];
        EchoObject toe = Assert.Single(hips.Get("Children")!.List);
        Assert.Equal("Toe", toe.Get("Name")!.StringValue);
        Assert.Equal(stubEchoId, toe.Get("$id")!.IntValue);
        // Editor-faithful field tags: byte flags, not bools.
        Assert.Equal(EchoType.Byte, toe.Get("Static")!.TagType);
        Assert.Equal(EchoType.Byte, toe.Get("Enabled")!.TagType);

        // Identity arrays follow the flat array: 3 GOs, unchanged 1 component.
        Assert.Equal(3, GuidArray(root, "_goIdentifiers").Count);
        Assert.Equal(toe.Get("Identifier")!.StringValue, GuidAt(root, "_goIdentifiers", 2));
        Assert.Equal(new List<int> { 0, 1, 1, 1 }, IntArray(root, "_compIdOffsets"));

        // And the hierarchy read surfaces the grandchild.
        var hierarchy = SceneReader.ReadHierarchy(_sceneAbs);
        var hipsNode = Assert.Single(Assert.Single(hierarchy.RootObjects).Children!);
        var toeNode = Assert.Single(hipsNode.Children!);
        Assert.Equal("Toe", toeNode.Name);
    }

    [Fact]
    public async Task Create_AtRoot_AppendsFullBodyAndArrays()
    {
        await _writer.CreateAsync(_sceneAbs, SceneRel, "Prop", parentIdentifier: null, Prov("create-prop-1"), dryRun: false);

        EchoObject root = ReadRoot();
        List<EchoObject> flat = FlatArray(root);
        Assert.Equal(3, flat.Count);
        EchoObject prop = flat[2];
        Assert.True(EchoFacade.IsFullGameObjectNode(prop));
        Assert.Equal("Prop", prop.Get("Name")!.StringValue);

        Assert.Equal(3, GuidArray(root, "_goIdentifiers").Count);
        Assert.Equal(new List<int> { 0, 1, 1, 1 }, IntArray(root, "_compIdOffsets"));

        var hierarchy = SceneReader.ReadHierarchy(_sceneAbs);
        Assert.Equal(2, hierarchy.RootObjects.Count);
    }

    [Fact]
    public async Task Create_UnknownParent_Rejects()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _writer.CreateAsync(_sceneAbs, SceneRel, "Orphan", "no-such-identifier", Prov("create-orphan-1"), dryRun: false));
    }

    // ---- component ops on a NESTED GameObject + array maintenance + list-building initialFields ----

    [Fact]
    public async Task AddComponent_OnNestedChild_BuildsListFields_AndCompArrays()
    {
        const string meshGuid = "12121212-3434-5656-7878-909090909090";
        const string matGuid = "abababab-cdcd-efef-0101-232323232323";

        await _writer.AddComponentAsync(_sceneAbs, SceneRel, HipsGuid, "MeshRenderer",
            new[]
            {
                new XEngine.Ai.Protocol.SceneFieldPatch("Mesh/AssetID", meshGuid),
                // ★ Gen-P0.3 — list intermediates created on a sparse node in one call.
                new XEngine.Ai.Protocol.SceneFieldPatch("Materials/$values/0/AssetID", matGuid),
            },
            Prov("add-mr-1"), dryRun: false);

        EchoObject root = ReadRoot();
        EchoObject hips = FlatArray(root)[0].Get("Children")!.List[0];
        EchoObject renderer = Assert.Single(hips.Get("Components")!.List);
        Assert.Contains("MeshRenderer", renderer.Get("$type")!.StringValue);
        Assert.Equal(meshGuid, renderer.Get("Mesh")!.Get("AssetID")!.StringValue);

        EchoObject materials = renderer.Get("Materials")!;
        Assert.Equal(EchoType.Compound, materials.TagType);
        EchoObject values = materials.Get("$values")!;
        Assert.Equal(EchoType.List, values.TagType);
        Assert.Equal(matGuid, Assert.Single(values.List).Get("AssetID")!.StringValue);

        // Component identity arrays: Rig's Animator at offset 0, Hips' new renderer at offset 1.
        Assert.Equal(2, GuidArray(root, "_compIdentifiers").Count);
        Assert.Equal(AnimatorCompGuid, GuidAt(root, "_compIdentifiers", 0));
        Assert.Equal(EchoFacade.ReadComponentIdentifier(renderer), GuidAt(root, "_compIdentifiers", 1));
        Assert.Equal(new List<int> { 0, 1, 2 }, IntArray(root, "_compIdOffsets"));
    }

    [Fact]
    public async Task PatchObject_OnNestedChild_Works()
    {
        await _writer.PatchAsync(_sceneAbs, SceneRel, HipsGuid,
            new[] { new XEngine.Ai.Protocol.SceneFieldPatch("Transform/_localPosition/Y", "2.5") },
            Prov("patch-hips-1"), dryRun: false);

        var hierarchy = SceneReader.ReadHierarchy(_sceneAbs);
        var hips = Assert.Single(Assert.Single(hierarchy.RootObjects).Children!);
        Assert.Equal(2.5f, hips.Transform.Y);
    }

    // ---- delete a nested child: body + stub removed, arrays rebuilt ----

    [Fact]
    public async Task Delete_NestedChild_RemovesBodyStubAndArrayEntries()
    {
        await _writer.DeleteAsync(_sceneAbs, SceneRel, HipsGuid, Prov("del-hips-1"), dryRun: false);

        EchoObject root = ReadRoot();
        List<EchoObject> flat = FlatArray(root);
        EchoObject rig = Assert.Single(flat);
        Assert.Empty(rig.Get("Children")!.List);

        Assert.Single(GuidArray(root, "_goIdentifiers"));
        Assert.Equal(RigGuid, GuidAt(root, "_goIdentifiers", 0));
        Assert.Equal(new List<int> { 0, 1 }, IntArray(root, "_compIdOffsets"));

        var hierarchy = SceneReader.ReadHierarchy(_sceneAbs);
        Assert.Equal(1, hierarchy.TotalObjects);
    }

    // ---- SetPathValue list semantics: append allowed, gaps rejected ----

    [Fact]
    public void SetPathValue_AppendsAtCount_RejectsGaps()
    {
        EchoObject node = EchoObject.NewCompound();
        EchoFacade.SetPathValue(node, "Items/$values/0/Value", "1");
        EchoFacade.SetPathValue(node, "Items/$values/1/Value", "2");
        Assert.Equal(2, node.Get("Items")!.Get("$values")!.List.Count);

        Assert.Throws<IndexOutOfRangeException>(() =>
            EchoFacade.SetPathValue(node, "Items/$values/5/Value", "9"));
    }
}
