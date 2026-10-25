using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>Regression coverage for package-facing UI mesh modifiers.</summary>
public sealed class UIMeshModifierTests
{
    [Fact]
    public void Shadow_AppendsOneTranslatedCopy()
    {
        UIMeshBuilder builder = UIMeshBuilder.Rent();
        try
        {
            builder.AddQuad(new Rect(0, 0, 10, 10), Color.White, Float2.Zero, Float2.One);
            builder.AddShadow(new Float2(2, -3), Color.Black);

            Assert.Equal(8, builder.VertexCount);
            Assert.Equal(12, builder.IndexCount);
        }
        finally
        {
            UIMeshBuilder.Return(builder);
        }
    }

    [Fact]
    public void Outline_AppendsFourTranslatedCopies()
    {
        UIMeshBuilder builder = UIMeshBuilder.Rent();
        try
        {
            builder.AddQuad(new Rect(0, 0, 10, 10), Color.White, Float2.Zero, Float2.One);
            builder.AddOutline(new Float2(1, 1), Color.Black);

            Assert.Equal(20, builder.VertexCount);
            Assert.Equal(30, builder.IndexCount);
        }
        finally
        {
            UIMeshBuilder.Return(builder);
        }
    }

    [Fact]
    public void PositionAsUV1_BakesLocalPositionIntoUv2()
    {
        UIMeshBuilder builder = UIMeshBuilder.Rent();
        try
        {
            builder.AddQuad(new Rect(-2, -3, 4, 6), Color.White, Float2.Zero, Float2.One);
            builder.SetPositionAsUV1();
            Mesh mesh = new();
            builder.Bake(mesh);

            Assert.True(mesh.HasUV2);
            Assert.Equal(mesh.VertexCount, mesh.UV2.Length);
            Assert.Equal(mesh.Vertices[0].X, mesh.UV2[0].X);
            Assert.Equal(mesh.Vertices[0].Y, mesh.UV2[0].Y);
        }
        finally
        {
            UIMeshBuilder.Return(builder);
        }
    }

    [Fact]
    public void RoundedClip_UsesTheOwningBuilderWorkspace()
    {
        UIMeshBuilder first = new();
        UIMeshBuilder second = new();

        first.AddQuad(new Rect(0, 0, 100, 100), Color.White, Float2.Zero, Float2.One);
        second.AddQuad(new Rect(10, 10, 30, 30), Color.White, Float2.Zero, Float2.One);

        first.ClipToRoundedRect(new Rect(0, 0, 100, 100), radius: 20f);

        Assert.True(first.VertexCount > 4);
        Assert.True(first.IndexCount > 6);
        Assert.Equal(4, second.VertexCount);
        Assert.Equal(6, second.IndexCount);

        second.ClipToRoundedRect(new Rect(10, 10, 30, 30), radius: 5f);

        Assert.True(second.VertexCount > 4);
        Assert.True(second.IndexCount > 6);
    }
}
