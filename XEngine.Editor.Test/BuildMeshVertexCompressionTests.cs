using System.IO;
using XEngine.Echo;
using XEngine.Editor.Build.Profiles;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public class BuildMeshVertexCompressionTests
{
    [Fact]
    public void BuildFlagPreservesSourceMeshDataAndCanRoundTrip()
    {
        using var mesh = new Mesh { Vertices = [new Float3(2, 3, 4)] };
        var source = Serializer.Serialize(mesh);
        using var input = new MemoryStream();
        using (var writer = new BinaryWriter(input, System.Text.Encoding.UTF8, true)) source.WriteToBinary(writer);
        byte[] bytes = input.ToArray();
        byte[] original = (byte[])bytes.Clone();
        var transformed = BuildMeshVertexCompression.Transform(bytes);
        using var reader = new BinaryReader(new MemoryStream(transformed));
        var output = EchoObject.ReadFromBinary(reader);
        Assert.True(output["CompressVertexData"].BoolValue);
        Assert.Equal(source["MeshData"].ByteArrayValue, output["MeshData"].ByteArrayValue);
        Assert.Equal(original, bytes);
        using var restored = Serializer.Deserialize<Mesh>(output);
        Assert.True(restored.CompressVertexData);
        Assert.Equal(mesh.Vertices, restored.Vertices);
    }
}
