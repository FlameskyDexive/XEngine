// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

using XEngine.Runtime.RHI;

namespace XEngine.Editor.Build.Android;

internal sealed record AndroidShaderVariantTraceUsage(
    string ShaderName,
    string PassName,
    GraphicsBackend Backend,
    string CanonicalKeywordSet,
    bool Found,
    string Source);

internal sealed class AndroidShaderVariantTraceCollection
{
    internal const int SchemaVersion = 1;

    internal AndroidShaderVariantTraceCollection(IReadOnlyList<AndroidShaderVariantTraceUsage> entries)
    {
        Entries = entries;
    }

    internal IReadOnlyList<AndroidShaderVariantTraceUsage> Entries { get; }
}

internal static class AndroidShaderVariantTrace
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static AndroidShaderVariantTraceCollection ReadApproved(
        string projectRoot,
        string? tracePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (string.IsNullOrWhiteSpace(tracePath))
            return new AndroidShaderVariantTraceCollection(Array.Empty<AndroidShaderVariantTraceUsage>());
        if (Path.IsPathRooted(tracePath))
            throw new InvalidDataException("Approved Android shader trace must be project-relative.");

        string root = Path.GetFullPath(projectRoot);
        string fullPath = Path.GetFullPath(Path.Combine(root, tracePath));
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, PathComparison))
            throw new InvalidDataException("Approved Android shader trace escapes the project root.");
        if (!string.Equals(Path.GetExtension(fullPath), ".jsonl", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Approved Android shader trace must use the .jsonl extension.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Approved Android shader trace is missing.", fullPath);

        var entries = new List<AndroidShaderVariantTraceUsage>();
        var identities = new HashSet<TraceIdentity>();
        int lineNumber = 0;
        try
        {
            foreach (string line in File.ReadLines(fullPath, StrictUtf8))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                AndroidShaderVariantTraceUsage usage = ParseLine(
                    line,
                    Path.GetRelativePath(root, fullPath).Replace('\\', '/'),
                    lineNumber);
                var identity = new TraceIdentity(
                    usage.ShaderName,
                    usage.PassName,
                    usage.Backend,
                    usage.CanonicalKeywordSet);
                if (!identities.Add(identity))
                {
                    throw new InvalidDataException(
                        $"Approved Android shader trace contains duplicate lookup at " +
                        $"{usage.Source} ({usage.ShaderName}/{usage.PassName}, variant={DisplayVariant(usage.CanonicalKeywordSet)}).");
                }
                entries.Add(usage);
            }
        }
        catch (Exception exception) when (exception is DecoderFallbackException or IOException)
        {
            throw new InvalidDataException($"Approved Android shader trace cannot be read: {fullPath}.", exception);
        }

        entries.Sort(static (left, right) =>
        {
            int comparison = StringComparer.Ordinal.Compare(left.ShaderName, right.ShaderName);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.PassName, right.PassName);
            if (comparison != 0) return comparison;
            comparison = left.Backend.CompareTo(right.Backend);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.CanonicalKeywordSet, right.CanonicalKeywordSet);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.Source, right.Source);
        });
        return new AndroidShaderVariantTraceCollection(entries.AsReadOnly());
    }

    internal static void WriteReport(string path, AndroidShaderVariantTraceCollection collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(collection);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);

        var entries = new List<object>(collection.Entries.Count);
        for (int i = 0; i < collection.Entries.Count; i++)
        {
            AndroidShaderVariantTraceUsage entry = collection.Entries[i];
            entries.Add(new
            {
                shaderName = entry.ShaderName,
                passName = entry.PassName,
                backend = entry.Backend.ToString(),
                canonicalKeywordSet = entry.CanonicalKeywordSet,
                found = entry.Found,
                source = entry.Source,
            });
        }

        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = AndroidShaderVariantTraceCollection.SchemaVersion,
            entryCount = entries.Count,
            entries,
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n", StrictUtf8);
    }

    internal static void ValidateCanonicalKeywordSet(string value, string source)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
            return;
        if (!value.EndsWith(";", StringComparison.Ordinal))
            throw new InvalidDataException($"{source}: variant key must end with ';'.");

        string previous = string.Empty;
        int keywordStart = 0;
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != ';')
                continue;

            int keywordLength = index - keywordStart;
            if (keywordLength == 0)
                throw new InvalidDataException($"{source}: variant key contains an invalid keyword.");
            string keyword = value.Substring(keywordStart, keywordLength);
            if (string.IsNullOrWhiteSpace(keyword))
                throw new InvalidDataException($"{source}: variant key contains an invalid keyword.");
            if (keywordStart > 0 && StringComparer.Ordinal.Compare(previous, keyword) >= 0)
            {
                throw new InvalidDataException(
                    $"{source}: variant key keywords must be unique and ordinal-sorted.");
            }
            previous = keyword;
            keywordStart = index + 1;
        }
    }

    private static AndroidShaderVariantTraceUsage ParseLine(
        string line,
        string relativePath,
        int lineNumber)
    {
        string source = relativePath + ":" + lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{source}: JSONL record must be an object.");

            int schemaVersion = RequiredInt(root, "schemaVersion", source);
            if (schemaVersion != AndroidShaderVariantTraceCollection.SchemaVersion)
                throw new InvalidDataException($"{source}: unsupported shader trace schema {schemaVersion}.");
            string shaderName = RequiredString(root, "shaderName", source);
            string passName = RequiredString(root, "passName", source);
            string backend = RequiredString(root, "backend", source);
            if (!string.Equals(backend, nameof(GraphicsBackend.Vulkan), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{source}: Android shader traces must use Vulkan, got '{backend}'.");
            }
            string variantKey = RequiredStringAllowEmpty(root, "variantKey", source);
            ValidateCanonicalKeywordSet(variantKey, source);
            if (!root.TryGetProperty("found", out JsonElement foundElement) ||
                (foundElement.ValueKind != JsonValueKind.True && foundElement.ValueKind != JsonValueKind.False))
            {
                throw new InvalidDataException($"{source}: found must be a JSON boolean.");
            }

            return new AndroidShaderVariantTraceUsage(
                shaderName,
                passName,
                GraphicsBackend.Vulkan,
                variantKey,
                foundElement.GetBoolean(),
                source);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{source}: malformed JSONL record.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException($"{source}: malformed JSONL record.", exception);
        }
    }

    private static string RequiredString(JsonElement root, string name, string source)
    {
        string value = RequiredStringAllowEmpty(root, name, source);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{source}: {name} is required.");
        return value;
    }

    private static string RequiredStringAllowEmpty(JsonElement root, string name, string source)
    {
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{source}: {name} must be a JSON string.");
        return element.GetString() ?? string.Empty;
    }

    private static int RequiredInt(JsonElement root, string name, string source)
    {
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out int value))
        {
            throw new InvalidDataException($"{source}: {name} must be a JSON integer.");
        }
        return value;
    }

    private static string DisplayVariant(string value) => string.IsNullOrEmpty(value) ? "<default>" : value;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly record struct TraceIdentity(
        string ShaderName,
        string PassName,
        GraphicsBackend Backend,
        string CanonicalKeywordSet);
}
