using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class MeshNameSerializationTests
{
    [Theory]
    [InlineData("Anbi_Body")]
    [InlineData("安比 Face #2")]
    [InlineData("")]
    public void TextAndBinaryRoundTripsPreserveAuthoredNameAndPayload(string name)
    {
        using var source = new Mesh { Name = name, Vertices = [new Float3(1, 2, 3)] };
        var echo = Serializer.Serialize(source, TypeMode.Aggressive);
        var bytes = echo["MeshData"].ByteArrayValue;
        using var text = Serializer.Deserialize<Mesh>(EchoObject.ReadFromString(echo.WriteToString()))!;
        Assert.Equal(name, text.Name);
        Assert.Equal(source.Vertices, text.Vertices);
        Assert.Equal(bytes, Serializer.Serialize(text, TypeMode.Aggressive)["MeshData"].ByteArrayValue);

        var file = new FileInfo(Path.Combine(Path.GetTempPath(), $"mesh-name-{Guid.NewGuid():N}.asset"));
        try
        {
            echo.WriteToBinary(file);
            using var binary = Serializer.Deserialize<Mesh>(EchoObject.ReadFromBinary(file))!;
            Assert.Equal(name, binary.Name);
            Assert.Equal(source.Vertices, binary.Vertices);
        }
        finally { file.Delete(); }
    }

    [Fact]
    public void LegacyMeshWithoutNameStillLoadsGeometryAndDefaultName()
    {
        using var source = new Mesh { Name = "Anbi_Face", Vertices = [new Float3(4, 5, 6)] };
        var echo = Serializer.Serialize(source, TypeMode.Aggressive);
        echo.Remove("Name");
        using var restored = Serializer.Deserialize<Mesh>(echo)!;
        Assert.Equal("NewMesh", restored.Name);
        Assert.Equal(source.Vertices, restored.Vertices);
    }
}
