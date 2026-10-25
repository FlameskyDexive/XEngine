using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace XEngine.XTS.Editor.Build;

/// <summary>
/// Marker file written into every XTS script project (by
/// <see cref="XtsToolchainService.CreateProject"/>). Player builds scan for
/// these files to find the script content they must package.
/// </summary>
public sealed class XtsProjectManifest
{
    public const string CurrentSchema = "xengine.xts.project.v1";
    public const string FileName = "xts.project.json";

    public string Schema { get; init; } = CurrentSchema;
    public string Name { get; init; } = string.Empty;
    /// <summary>Bundled entry relative to the project directory.</summary>
    public string Entry { get; init; } = "dist/main.js";
    /// <summary>Host binding ids this project requires at runtime.</summary>
    public List<string> RequiredBindings { get; init; } = new();

    public static XtsProjectManifest Parse(string json) =>
        JsonSerializer.Deserialize<XtsProjectManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("xts project manifest parsed to null");

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}
