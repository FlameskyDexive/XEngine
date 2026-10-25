using XEngine.Echo;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Editor.Test;

[ImporterFor(".meshnametest")]
public sealed class MeshNameTestImporter : AssetImporter
{
    public static int Imports;
    public override int Version => 1;
    public override bool Import(ImportContext ctx)
    {
        Imports++;
        ctx.SetMainAsset(new Mesh { Name = "Authored main" });
        ctx.AddSubAsset("body-source-id", "Anbi_Body", new Mesh { Name = "Anbi_Body" });
        ctx.AddSubAsset("face-source-id", "安比 Face", new Mesh { Name = "安比 Face" });
        return true;
    }
}

public sealed class MeshCacheNameTests : EditorTestHarness
{
    private (Guid parent, Guid body, Guid face) Prepare(bool legacy)
    {
        File.WriteAllText(AssetAbsolutePath("Hero.meshnametest"), "source");
        Guid parent = Assets.ImportFile("Hero.meshnametest");
        var subs = Assets.GetSubAssets(parent);
        Assert.Equal(2, subs.Length);
        if (legacy)
        {
            // Generate the previous schema via Echo's binary writer; never treat a cache as text.
            foreach (var id in new[] { parent, subs[0].Guid, subs[1].Guid })
            {
                var file = new FileInfo(Path.Combine(Project.CachePath, $"{id}.asset"));
                var echo = EchoObject.ReadFromBinary(file);
                echo.Remove("Name"); echo.WriteToBinary(file);
            }
        }
        AssetDatabase.ForceIdle(parent); Assets.ForceIdleSweep();
        Assert.Null(Assets.GetLoadedAsset(parent));
        return (parent, subs[0].Guid, subs[1].Guid);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void SubassetColdLoadRestoresNameWithoutLoadingSiblingsOrRewritingCache(bool legacy)
    {
        var (parent, body, face) = Prepare(legacy);
        int imports = MeshNameTestImporter.Imports;
        string path = Path.Combine(Project.CachePath, $"{body}.asset");
        var bytes = File.ReadAllBytes(path);
        string source = File.ReadAllText(AssetAbsolutePath("Hero.meshnametest"));
        var mesh = Assert.IsType<Mesh>(Assets.Get(body));
        Assert.Equal("Anbi_Body", mesh.Name); Assert.Equal(body, mesh.AssetID);
        Assert.Equal("Hero.meshnametest#Anbi_Body", mesh.AssetPath);
        Assert.Null(Assets.GetLoadedAsset(parent)); Assert.Null(Assets.GetLoadedAsset(face));
        Assert.Same(mesh, Assets.Get(body));
        Assert.Equal(imports, MeshNameTestImporter.Imports);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(source, File.ReadAllText(AssetAbsolutePath("Hero.meshnametest")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void MainCacheUsesAuthoredNameOrLegacyFilename(bool legacy)
    {
        var (parent, body, face) = Prepare(legacy);
        int imports = MeshNameTestImporter.Imports;
        var mesh = Assert.IsType<Mesh>(Assets.Get(parent));
        Assert.Equal(legacy ? "Hero" : "Authored main", mesh.Name);
        Assert.Equal(parent, mesh.AssetID); Assert.Equal("Hero.meshnametest", mesh.AssetPath);
        Assert.Null(Assets.GetLoadedAsset(body)); Assert.Null(Assets.GetLoadedAsset(face));
        Assert.Equal(imports, MeshNameTestImporter.Imports);
    }

    [Fact]
    public void ExplicitCachedNameWinsOverMetadataDisplayName()
    {
        var (parent, body, _) = Prepare(false);
        var file = new FileInfo(Path.Combine(Project.CachePath, $"{body}.asset"));
        var echo = EchoObject.ReadFromBinary(file);
        echo["Name"] = new EchoObject("Explicit mesh name"); echo.WriteToBinary(file);
        Assert.Equal("Explicit mesh name", Assert.IsType<Mesh>(Assets.Get(body)).Name);
        Assert.Equal("Anbi_Body", Assets.GetSubAssets(parent)[0].Name);
    }

    [Fact]
    public void ReimportAndDatabaseReopenPreserveSubassetGuidNameAndOwnership()
    {
        var (parent, body, face) = Prepare(true);
        Assert.Equal("Anbi_Body", Assert.IsType<Mesh>(Assets.Get(body)).Name);
        Assets.Reimport(parent);
        Assert.Equal(new[] { body, face }, Assets.GetSubAssets(parent).Select(s => s.Guid));
        ReopenDatabase();
        var restored = Assert.IsType<Mesh>(Assets.Get(face));
        Assert.Equal("安比 Face", restored.Name); Assert.Equal(face, restored.AssetID);
        Assert.Equal("Hero.meshnametest#安比 Face", restored.AssetPath);
    }
}
