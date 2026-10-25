using XEngine.RpgVfx.Editor;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HovlMeshReferenceResolverTests
{
    [Fact]
    public void LegacyFileIdToRecycleName_ResolvesSurvivingMeshByName()
    {
        // Unity retains old file ids after reimport; 4300002 is not necessarily mesh 1.
        var names = MeshReferenceResolver.ReadMeshFileIdNames("""
fileFormatVersion: 2
guid: 82dbd8e3418cba140afc86fa4b355d35
ModelImporter:
  fileIDToRecycleName:
    4300000: Cylinder
    4300002: CylinderFromGround
""");
        Assert.Equal("CylinderFromGround", MeshReferenceResolver.SelectMeshName(
            new[] { "CylinderFromGround" }, 4300002, names));
        Assert.Null(MeshReferenceResolver.SelectMeshName(new[] { "Unrelated", "Another" }, 4300002, names));
    }

    [Fact]
    public void ModernIdTable_ResolvesHashedFileId()
    {
        var names = MeshReferenceResolver.ReadMeshFileIdNames("""
fileFormatVersion: 2
guid: d8d8b51a901254542853b1654dba123a
ModelImporter:
  internalIDToNameTable:
  - first:
      43: -3410181147628579750
    second: CylinderFromGround4
""");
        Assert.Equal("CylinderFromGround4", MeshReferenceResolver.SelectMeshName(
            new[] { "OtherMesh", "CylinderFromGround4" }, -3410181147628579750, names));
    }

    [Fact]
    public void OmittedModernIdTable_ResolvesSingleMeshWithoutTreatingHashAsIndex()
    {
        Assert.Equal("OnlyMesh", MeshReferenceResolver.SelectMeshName(
            new[] { "OnlyMesh" }, -3410181147628579750, null));
        Assert.Null(MeshReferenceResolver.SelectMeshName(new[] { "A", "B" }, -3410181147628579750, null));
    }
}
