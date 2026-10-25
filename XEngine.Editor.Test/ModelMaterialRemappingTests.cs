using XEngine.Echo;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class ModelMaterialRemappingTests
{
    private static Guid[] Ids(List<AssetRef<Material>> slots)
    {
        var ids = new Guid[slots.Count];
        for (int i = 0; i < slots.Count; i++) ids[i] = slots[i].AssetID;
        return ids;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly ModelImportResult Data = new() { RootGO = new GameObject("Source Model") };
        internal readonly ImportContext Context;
        internal readonly Material Body = new() { Name = "Material #27" }, Face = new() { Name = "Material #26" };
        internal readonly SkinnedMeshRenderer Skinned;
        internal readonly MeshRenderer Static;
        internal readonly Guid External = Guid.NewGuid(), Sibling = Guid.NewGuid();
        internal readonly List<string> Warnings = new();
        internal readonly EchoObject Remaps = EchoObject.NewCompound();
        internal Fixture()
        {
            var settings = EchoObject.NewCompound(); settings[ModelMaterialRemapping.SettingsKey] = Remaps;
            Context = new ImportContext(Guid.NewGuid(), "Model.fbx", settings);
            Context.AddSubAsset("Material #27", Body); Context.AddSubAsset("Material #26", Face);
            Data.Materials.Add(Body); Data.Materials.Add(Face);
            Skinned = Data.RootGO!.AddComponent<SkinnedMeshRenderer>();
            Skinned.Enabled = false; Skinned.Materials.Add(Body); Skinned.Materials.Add(Face); Skinned.Materials.Add(Body);
            Static = new GameObject("Static Child").AddComponent<MeshRenderer>(); Static.GameObject.SetParent(Data.RootGO, false);
            Static.Enabled = false; Static.Materials.Add(Face); Static.Materials.Add(Body);
        }
        internal void Map(Guid source, Guid target) => Remaps[source.ToString()] = new EchoObject(target.ToString());
        internal void Apply(Guid sibling = default)
            => ModelMaterialRemapping.Apply(Context, Data, sibling, id => id == External, Warnings.Add);
        public void Dispose() { Data.RootGO!.Dispose(); Body.Dispose(); Face.Dispose(); }
    }

    [Fact]
    public void RemapChangesEveryMatchingSlotOnBothRendererTypesWithoutMutatingEmbeddedIdentity()
    {
        using var f = new Fixture();
        Guid body = f.Body.AssetID, face = f.Face.AssetID;
        f.Map(body, f.External); f.Apply();
        Assert.Equal(new[] { f.External, face, f.External }, Ids(f.Skinned.Materials));
        Assert.Equal(new[] { face, f.External }, Ids(f.Static.Materials));
        Assert.Equal(body, f.Body.AssetID); Assert.Equal(face, f.Face.AssetID);
        Assert.Equal("Material #27", f.Body.Name); Assert.Same(f.Body, f.Data.Materials[0]);
        Assert.Equal(2, f.Context.SubAssets.Count);
        Assert.Contains(new DependencySerializationContext.Dependency(f.External, "Material"), f.Context.DependencyEdges);
        Assert.Single(f.Context.Dependencies); Assert.Empty(f.Warnings);
    }

    [Fact]
    public void ExplicitRemapWinsOverSiblingDefaultAndOtherSlotsRemainOrdered()
    {
        using var f = new Fixture();
        f.Map(f.Body.AssetID, f.External); f.Apply(f.Sibling);
        Assert.Equal(new[] { f.External, f.Face.AssetID, f.External }, Ids(f.Skinned.Materials));
        Assert.Equal(new[] { f.Sibling, f.External }, Ids(f.Static.Materials));
    }

    [Fact]
    public void NoneAndMissingMappingsRetainLegacySiblingDefault()
    {
        using var f = new Fixture();
        f.Map(f.Body.AssetID, Guid.Empty); f.Apply(f.Sibling);
        Assert.Equal(new[] { f.Sibling, f.Face.AssetID, f.Body.AssetID }, Ids(f.Skinned.Materials));
        Assert.Equal(new[] { f.Sibling, f.Body.AssetID }, Ids(f.Static.Materials));
        Assert.Empty(f.Warnings); Assert.Empty(f.Context.Dependencies);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("parent")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("wrongType")]
    public void InvalidMappingsWarnAndPreserveDefaultReferences(string kind)
    {
        using var f = new Fixture();
        Guid target = kind == "self" ? f.Face.AssetID : kind == "parent" ? f.Context.AssetGuid : Guid.NewGuid();
        f.Map(f.Body.AssetID, target);
        if (kind == "malformed") f.Remaps[f.Body.AssetID.ToString()] = new EchoObject("invalid");
        if (kind == "wrongType") f.Remaps[f.Body.AssetID.ToString()] = new EchoObject(42);
        f.Apply();
        Assert.Equal(f.Body.AssetID, f.Skinned.Materials[0].AssetID);
        Assert.Single(f.Warnings); Assert.Empty(f.Context.Dependencies);
    }

    [Fact]
    public void UnknownSourceMappingDoesNotBindBySlotPositionOrName()
    {
        using var f = new Fixture();
        f.Map(Guid.NewGuid(), f.External); f.Apply();
        Assert.Equal(f.Body.AssetID, f.Skinned.Materials[0].AssetID);
        Assert.Empty(f.Warnings); Assert.Empty(f.Context.Dependencies);
    }

    [Fact]
    public void GuidRemapsSurviveSlotReorderingAndReferenceSerialization()
    {
        using var f = new Fixture();
        f.Skinned.Materials.Reverse(); f.Skinned.Materials.Insert(0, f.Face);
        f.Map(f.Face.AssetID, f.External); f.Apply();
        Assert.Equal(new[] { f.External, f.Body.AssetID, f.External, f.Body.AssetID }, Ids(f.Skinned.Materials));
        var context = new DependencySerializationContext();
        var echo = Serializer.Serialize(f.Skinned.Materials.GetType(), f.Skinned.Materials, context);
        var restored = Serializer.Deserialize<List<AssetRef<Material>>>(EchoObject.ReadFromString(echo.WriteToString()));
        Assert.NotNull(restored);
        Assert.Equal(Ids(f.Skinned.Materials), Ids(restored));
        Assert.Contains(new DependencySerializationContext.Dependency(f.External, "Material"), context.Dependencies);
    }
}
