// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Terrain;

/// <summary>
/// Shared terrain splat layer: albedo, normal, tiling, and surface scalars.
/// Saved as <c>.terrainlayer</c> so multiple <see cref="TerrainData"/> assets can reference the same layer.
/// </summary>
[CreateAssetMenu("Terrain/Terrain Layer", Extension = FileExtension, Order = 4)]
[FormerlySerializedAs("XEngine.Runtime.TerrainLayer")]
[FormerlySerializedAs("TerrainLayer")]
public sealed class TerrainLayer : ScriptableObject, ISerializable
{
    public const string FileExtension = ".terrainlayer";

    public AssetRef<Texture2D> Albedo;
    public AssetRef<Texture2D> NormalMap;
    public float Tiling = 10f;
    public float Roughness = 1f;
    public float Metallic;

    public TerrainLayer()
    {
        Name = "Layer";
    }

    public void Serialize(ref EchoObject value, SerializationContext ctx)
    {
        value.Add("Name", new EchoObject(Name));
        value.Add("Albedo", Serializer.Serialize(Albedo, ctx));
        value.Add("NormalMap", Serializer.Serialize(NormalMap, ctx));
        value.Add("Tiling", new EchoObject(Tiling));
        value.Add("Roughness", new EchoObject(Roughness));
        value.Add("Metallic", new EchoObject(Metallic));
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        string? name = value.Get("Name")?.StringValue;
        if (!string.IsNullOrEmpty(name))
            Name = name;
        Albedo = Serializer.Deserialize<AssetRef<Texture2D>>(value.Get("Albedo"), ctx);
        NormalMap = Serializer.Deserialize<AssetRef<Texture2D>>(value.Get("NormalMap"), ctx);
        Tiling = value.Get("Tiling")?.FloatValue ?? 10f;
        Roughness = value.Get("Roughness")?.FloatValue ?? 1f;
        Metallic = value.Get("Metallic")?.FloatValue ?? 0f;
    }

    internal static TerrainLayer FromEmbeddedCompound(EchoObject compound, SerializationContext ctx)
    {
        var layer = new TerrainLayer();
        layer.Deserialize(compound, ctx);
        return layer;
    }
}
