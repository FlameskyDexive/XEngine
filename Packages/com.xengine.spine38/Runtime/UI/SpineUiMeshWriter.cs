using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Spine38;

/// <summary>Copies posed Spine batches into an xgui <see cref="UIMeshBuilder"/>.</summary>
public static class SpineUiMeshWriter
{
    public static int Write(UIMeshBuilder builder, SpineMeshGenerator generator, float scale, Float2 offset)
    {
        int written = 0;
        for (int b = 0; b < generator.BatchCount; b++)
        {
            SpineMeshBatch batch = generator.GetBatch(b);
            uint @base = builder.NextVertex;
            for (int i = 0; i < batch.Vertices.Length; i++)
            {
                Float3 v = batch.Vertices[i];
                builder.AddVertex(
                    new Float3(v.X * scale + offset.X, v.Y * scale + offset.Y, 0f),
                    batch.UVs[i],
                    batch.Colors[i]);
            }

            for (int i = 0; i < batch.Indices.Length; i++)
                builder.AddIndex(@base + batch.Indices[i]);
            written += batch.Vertices.Length;
        }

        return written;
    }
}
