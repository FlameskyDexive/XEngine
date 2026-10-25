// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class HovlMaterialSerializationTests
{
    private static string MaterialsDirectory => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Packages", "com.xengine.rpgvfx", "Assets", "Materials"));

    [Fact]
    public void GeneratedMaterialNumbers_AreEchoFloatsThroughoutThePackage()
    {
        string[] files = Directory.GetFiles(MaterialsDirectory, "*.mat");
        Assert.True(files.Length >= 205);
        foreach (string file in files)
        {
            EchoObject properties = EchoObject.ReadFromString(File.ReadAllText(file))["_properties"];
            foreach (string bucket in new[] { "_floats", "_colors", "_vectors2", "_vectors3", "_vectors4" })
                AssertNumericLeaves(properties[bucket], Path.GetFileName(file) + "/" + bucket);
        }
    }

    [Theory]
    [InlineData("Point12cg", 8f)]
    [InlineData("MagicCircle29bcg", 2f)]
    public void MaterialEchoRoundTrip_PreservesEmissionColorAndTexture(string name, float emission)
    {
        using Material material = Load(name);
        Assert.Equal(emission, material._properties.GetFloat("_Emission"));
        Assert.Equal(Color.White, material._properties.GetColor("_Color"));
        Guid texture = material._properties._textures["_MainTex"].AssetID;
        Assert.NotEqual(Guid.Empty, texture);

        string saved = Serializer.Serialize(material, TypeMode.Aggressive).WriteToString();
        using Material reloaded = Serializer.Deserialize<Material>(EchoObject.ReadFromString(saved))!;
        Assert.NotNull(reloaded);
        Assert.Equal(emission, reloaded._properties.GetFloat("_Emission"));
        Assert.Equal(Color.White, reloaded._properties.GetColor("_Color"));
        Assert.Equal(texture, reloaded._properties._textures["_MainTex"].AssetID);
        Assert.Equal(material.ShaderRef.AssetID, reloaded.ShaderRef.AssetID);
    }

    [Fact]
    public void MaterialEchoRoundTrip_PreservesTextureScaleAndOffset()
    {
        using Material material = Load("Chain1bn");
        var expected = new Float4(5f, 0.5f, 0f, 0.5f);
        Assert.Equal(expected, material._properties.GetVector4("_MainTex_ST"));
        string saved = Serializer.Serialize(material, TypeMode.Aggressive).WriteToString();
        using Material reloaded = Serializer.Deserialize<Material>(EchoObject.ReadFromString(saved))!;
        Assert.Equal(expected, reloaded._properties.GetVector4("_MainTex_ST"));
    }

    private static Material Load(string name) => Serializer.Deserialize<Material>(
        EchoObject.ReadFromString(File.ReadAllText(Path.Combine(MaterialsDirectory, name + ".mat"))))!;

    private static void AssertNumericLeaves(EchoObject node, string location)
    {
        if (node.TagType == EchoType.Compound)
        {
            foreach (KeyValuePair<string, EchoObject> entry in (Dictionary<string, EchoObject>)node.Value!)
                AssertNumericLeaves(entry.Value, location + "/" + entry.Key);
            return;
        }
        Assert.True(node.TagType == EchoType.Float, $"{location}: expected Echo Float, found {node.TagType}");
    }
}
