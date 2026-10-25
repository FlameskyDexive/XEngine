// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace XEngine.Ai.SpineGen;

/// <summary>Object model for a Spine 4.x <c>.json</c> skeleton export, writable by
/// <see cref="SpineExporter"/> and readable by the vendored spine-csharp runtime.</summary>
public sealed class SpineSkeletonDoc
{
    [JsonPropertyName("skeleton")] public SpineSkeletonMeta? Skeleton { get; set; }
    [JsonPropertyName("bones")] public List<SpineBoneDef>? Bones { get; set; }
    [JsonPropertyName("slots")] public List<SpineSlotDef>? Slots { get; set; }
    [JsonPropertyName("skins")] public List<SpineSkinDef>? Skins { get; set; }
    [JsonPropertyName("animations")] public Dictionary<string, SpineAnimation>? Animations { get; set; }
}

public sealed class SpineSkeletonMeta
{
    /// <summary>Parser requires this key even when unknown — exporter always emits it.</summary>
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("spine")] public string Spine { get; set; } = "4.3.74";
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("width")] public float Width { get; set; }
    [JsonPropertyName("height")] public float Height { get; set; }
    [JsonPropertyName("fps")] public float Fps { get; set; } = 30f;
    [JsonPropertyName("images")] public string? Images { get; set; }
}

public sealed class SpineBoneDef
{
    [JsonPropertyName("name")] public required string Name { get; set; }
    [JsonPropertyName("parent")] public string? Parent { get; set; }

    [JsonPropertyName("length")] public float? Length { get; set; }
    [JsonPropertyName("x")] public float? X { get; set; }
    [JsonPropertyName("y")] public float? Y { get; set; }
    [JsonPropertyName("rotation")] public float? Rotation { get; set; }
    [JsonPropertyName("scaleX")] public float? ScaleX { get; set; }
    [JsonPropertyName("scaleY")] public float? ScaleY { get; set; }
    [JsonPropertyName("shearX")] public float? ShearX { get; set; }
    [JsonPropertyName("shearY")] public float? ShearY { get; set; }
}

public sealed class SpineSlotDef
{
    [JsonPropertyName("name")] public required string Name { get; set; }
    [JsonPropertyName("bone")] public required string Bone { get; set; }
    [JsonPropertyName("attachment")] public string? Attachment { get; set; }
    [JsonPropertyName("blend")] public string? Blend { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
}

/// <summary>A skin: slot name → placeholder name → attachment. The generator writes a single
/// "default" skin whose attachments carry the setup transforms.</summary>
public sealed class SpineSkinDef
{
    [JsonPropertyName("name")] public string Name { get; set; } = "default";
    [JsonPropertyName("attachments")]
    public Dictionary<string, Dictionary<string, SpineAttachment>> Attachments { get; set; } = new();

    public void Add(string slot, string placeholder, SpineAttachment attachment)
    {
        if (!Attachments.TryGetValue(slot, out var byPlaceholder))
            Attachments[slot] = byPlaceholder = new Dictionary<string, SpineAttachment>();
        byPlaceholder[placeholder] = attachment;
    }
}

/// <summary>Single JSON shape for every attachment type the generator emits. Unused fields stay
/// null and are omitted, matching what the parser defaults back to.</summary>
public sealed class SpineAttachment
{
    [JsonPropertyName("type")] public string Type { get; set; } = "region";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }

    // Region / point.
    [JsonPropertyName("x")] public float? X { get; set; }
    [JsonPropertyName("y")] public float? Y { get; set; }
    [JsonPropertyName("rotation")] public float? Rotation { get; set; }
    [JsonPropertyName("width")] public float? Width { get; set; }
    [JsonPropertyName("height")] public float? Height { get; set; }

    // Mesh.
    [JsonPropertyName("uvs")] public float[]? Uvs { get; set; }
    [JsonPropertyName("triangles")] public int[]? Triangles { get; set; }
    [JsonPropertyName("vertices")] public SpineMeshVertices? Vertices { get; set; }
    [JsonPropertyName("hull")] public int? Hull { get; set; }
    [JsonPropertyName("edges")] public int[]? Edges { get; set; }

    public static SpineAttachment Region(string path, float x, float y, float rotation, float width, float height) =>
        new()
        {
            Type = "region",
            Path = path,
            X = x == 0 ? null : x,
            Y = y == 0 ? null : y,
            Rotation = rotation == 0 ? null : rotation,
            Width = width,
            Height = height,
        };

    /// <summary>Mesh with local-space vertex positions in pixels relative to the attachment origin
    /// (setup bone), UVs normalized 0..1 inside the region rect (y top-down).</summary>
    public static SpineAttachment Mesh(string path, SpineMeshVertices vertices, float[] uvs, int[] triangles,
        int hullPointCount, float width, float height) =>
        new()
        {
            Type = "mesh",
            Path = path,
            Uvs = uvs,
            Triangles = triangles,
            Vertices = vertices,
            Hull = hullPointCount > 0 ? hullPointCount : null,
            Width = width,
            Height = height,
        };
}

/// <summary>The Spine JSON <c>vertices</c> array. Unweighted meshes store x/y pairs. Weighted
/// meshes store the official flat stream per vertex: boneCount, then repeated
/// boneIndex/x/y/weight tuples. <see cref="Bones"/> and <see cref="Values"/> are kept split only
/// while authoring so callers cannot accidentally corrupt tuple boundaries.</summary>
public sealed class SpineMeshVertices
{
    public int[]? Bones;
    public float[]? Values;

    public static SpineMeshVertices Unweighted(float[] values) => new() { Values = values };

    public static SpineMeshVertices Weighted(int[] bones, float[] interleaved) =>
        new() { Bones = bones, Values = interleaved };

    internal float[] ToJsonArray()
    {
        if (Bones is not { Length: > 0 })
            return Values ?? [];

        float[] weights = Values ?? [];
        var encoded = new List<float>(Bones.Length + weights.Length);
        int boneCursor = 0;
        int valueCursor = 0;
        while (boneCursor < Bones.Length)
        {
            int count = Bones[boneCursor++];
            if (count <= 0 || boneCursor + count > Bones.Length || valueCursor + count * 3 > weights.Length)
                throw new InvalidOperationException("Malformed weighted Spine vertex stream.");
            encoded.Add(count);
            for (int i = 0; i < count; i++)
            {
                encoded.Add(Bones[boneCursor++]);
                encoded.Add(weights[valueCursor++]);
                encoded.Add(weights[valueCursor++]);
                encoded.Add(weights[valueCursor++]);
            }
        }
        if (valueCursor != weights.Length)
            throw new InvalidOperationException("Weighted Spine vertex values contain trailing data.");
        return encoded.ToArray();
    }
}

/// <summary>Always writes the standard flat Spine array. The object shape remains readable only
/// for migration from pre-M9 generator artifacts, which were never consumable by spine-csharp.</summary>
public sealed class SpineMeshVerticesConverter : JsonConverter<SpineMeshVertices>
{
    public override SpineMeshVertices Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var values = new List<float>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                values.Add(reader.GetSingle());
            return new SpineMeshVertices { Values = values.ToArray() };
        }
        int[]? bones = null;
        float[]? values2 = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string prop = reader.GetString()!;
            reader.Read();
            if (prop == "bones")
                bones = JsonSerializer.Deserialize<int[]>(ref reader, options);
            else if (prop == "vertices")
                values2 = JsonSerializer.Deserialize<float[]>(ref reader, options);
        }
        return new SpineMeshVertices { Bones = bones, Values = values2 };
    }

    public override void Write(Utf8JsonWriter writer, SpineMeshVertices value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value.ToJsonArray(), options);
    }
}

public sealed class SpineAnimation
{
    [JsonPropertyName("bones")] public Dictionary<string, SpineBoneTimelines>? Bones { get; set; }
    [JsonPropertyName("slots")] public Dictionary<string, SpineSlotTimelines>? Slots { get; set; }

    public SpineBoneTimelines Bone(string name)
    {
        Bones ??= new Dictionary<string, SpineBoneTimelines>();
        if (!Bones.TryGetValue(name, out var timelines))
            Bones[name] = timelines = new SpineBoneTimelines();
        return timelines;
    }
}

public sealed class SpineBoneTimelines
{
    [JsonPropertyName("rotate")] public List<SpineRotateKey>? Rotate { get; set; }
    [JsonPropertyName("translate")] public List<SpineTranslateKey>? Translate { get; set; }
    [JsonPropertyName("scale")] public List<SpineScaleKey>? Scale { get; set; }
}

public sealed class SpineSlotTimelines
{
    [JsonPropertyName("attachment")] public List<SpineAttachmentKey>? Attachment { get; set; }
}

/// <summary>A single-value bone key (4.2+ JSON format: rotate writes <c>value</c> in degrees).
/// Curve omitted = linear.</summary>
public sealed class SpineRotateKey
{
    [JsonPropertyName("time")] public float Time { get; set; }
    [JsonPropertyName("value")] public float Angle { get; set; }
    [JsonPropertyName("curve")] public SpineKeyCurve? Curve { get; set; }
}

public sealed class SpineTranslateKey
{
    [JsonPropertyName("time")] public float Time { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("x")] public float? X { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("y")] public float? Y { get; set; }
    [JsonPropertyName("curve")] public SpineKeyCurve? Curve { get; set; }
}

public sealed class SpineScaleKey
{
    [JsonPropertyName("time")] public float Time { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("x")] public float? X { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("y")] public float? Y { get; set; }
    [JsonPropertyName("curve")] public SpineKeyCurve? Curve { get; set; }
}

public sealed class SpineAttachmentKey
{
    [JsonPropertyName("time")] public float Time { get; set; }
    /// <summary>Null clears (hides) the slot attachment; switching is discrete.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("curve")] public SpineKeyCurve? Curve { get; set; }
}

/// <summary>"linear" | "stepped" | bezier control points [c1,c2,c3,c4].</summary>
public sealed class SpineKeyCurve
{
    public string? Mode;
    public float[]? ControlPoints;

    public static readonly SpineKeyCurve Stepped = new() { Mode = "stepped" };

    public static SpineKeyCurve Bezier(float c1, float c2, float c3, float c4) =>
        new() { ControlPoints = new[] { c1, c2, c3, c4 } };
}

public sealed class SpineCurveConverter : JsonConverter<SpineKeyCurve>
{
    public override SpineKeyCurve Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new SpineKeyCurve { Mode = reader.GetString() };
        var bezier = new List<float>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            bezier.Add(reader.GetSingle());
        return new SpineKeyCurve { ControlPoints = bezier.ToArray() };
    }

    public override void Write(Utf8JsonWriter writer, SpineKeyCurve value, JsonSerializerOptions options)
    {
        if (value.Mode != null)
            writer.WriteStringValue(value.Mode);
        else if (value.ControlPoints != null)
            JsonSerializer.Serialize(writer, value.ControlPoints, options);
        else
            writer.WriteStringValue("linear");
    }
}
