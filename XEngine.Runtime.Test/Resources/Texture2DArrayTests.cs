// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using System.IO;

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class Texture2DArrayTests
{
    [Fact]
    public void CreatesHeadless_WithLayerCount()
    {
        var texture = new Texture2DArray(4, 4, 3);
        Assert.Equal(4u, texture.Width);
        Assert.Equal(4u, texture.Height);
        Assert.Equal(3u, texture.Layers);
        Assert.Equal(TextureType.Texture2DArray, texture.Type);
        Assert.Equal(4 * 4 * 3 * 4, texture.GetSize());
    }

    [Fact]
    public void SetData_AcceptsFullLayerStack()
    {
        var texture = new Texture2DArray(1, 1, 2);
        texture.SetData(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }.AsMemory());
        Assert.Equal(2u, texture.Layers);
    }

    [Fact]
    public void Material_BindsArrayThroughPropertyState()
    {
        var texture = new Texture2DArray(2, 2, 2);
        var material = new Material();
        material.SetTexture2DArray("_VoxelAtlas", texture);
        Assert.True(material.IsOverridden("_VoxelAtlas"));
        Assert.Same(texture, material._properties.GetTexture2DArray("_VoxelAtlas"));
    }

    [Fact]
    public void ApplyTextureBindings_ResolvesArraySlot()
    {
        var texture = new Texture2DArray(2, 2, 2);
        var properties = new PropertyState();
        properties.SetTexture2DArray("_VoxelAtlas", texture);
        var bindings = new Dictionary<string, GraphicsTexture>(System.StringComparer.Ordinal);
        var slots = new[]
        {
            new ShaderBindingSlot(ShaderBindingKind.Texture, 0, "_VoxelAtlas"),
        };

        MaterialUniformPacking.ApplyTextureBindings(bindings, properties, shader: null, slots);

        Assert.True(bindings.TryGetValue("_VoxelAtlas", out GraphicsTexture bound));
        Assert.Same(texture.Handle, bound);
    }

    [Fact]
    public void ShaderParser_AcceptsTexture2DArrayProperty()
    {
        const string source = """
Shader "VoxelWorld/ArrayPreviewParse"

Properties
{
    _VoxelAtlas ("Atlas", Texture2DArray) = "white"
    _Slice ("Slice", Float) = 0
}

Pass "Preview"
{
    GLSLPROGRAM
    Vertex { void main() { gl_Position = vec4(0.0); } }
    Fragment { layout(location = 0) out vec4 c; void main() { c = vec4(1.0); } }
    ENDGLSL
}
""";
        Assert.True(ShaderParser.ParseShader("VoxelArrayPreview.shader", source, out Shader? shader));
        Assert.NotNull(shader);
        ShaderProperty atlas = Assert.Single(shader!.PropertyArray, p => p.Name == "_VoxelAtlas");
        Assert.Equal(ShaderPropertyType.Texture2DArray, atlas.PropertyType);
        Assert.NotNull(atlas.Texture2DArrayValue);
        Assert.Equal(1u, atlas.Texture2DArrayValue.Layers);
    }

    [Fact]
    public void PackagePreviewShader_ParsesTexture2DArray()
    {
        string path = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.voxelworld", "Runtime", "Shaders", "VoxelArrayPreview.shader");
        Assert.True(File.Exists(path));
        string source = File.ReadAllText(path);
        Assert.True(ShaderParser.ParseShader(path, source, out Shader? shader), "VoxelArrayPreview.shader must parse");
        Assert.NotNull(shader);
        ShaderProperty atlas = Assert.Single(shader!.PropertyArray, p => p.Name == "_VoxelAtlas");
        Assert.Equal(ShaderPropertyType.Texture2DArray, atlas.PropertyType);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
