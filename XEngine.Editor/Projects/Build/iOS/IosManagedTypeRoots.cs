// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.Editor.Build.Ios;

internal sealed record IosManagedTypeRootReason(
    Guid AssetGuid,
    string AssetPath,
    string Source);

internal sealed record IosManagedTypeRoot(
    string AssemblyName,
    string TypeName,
    IReadOnlyList<IosManagedTypeRootReason> Reasons);

internal sealed class IosManagedTypeRootCollection
{
    internal static readonly IosManagedTypeRootCollection Empty = new(Array.Empty<IosManagedTypeRoot>());

    internal IosManagedTypeRootCollection(IReadOnlyList<IosManagedTypeRoot> types)
    {
        Types = types;
    }

    internal IReadOnlyList<IosManagedTypeRoot> Types { get; }
}

/// <summary>
/// Collects concrete CLR types named by build assets without instantiating their object graphs.
/// Echo documents are parsed for <c>$type</c> tags; the closure includes base types (Android/HarmonyOS
/// Safe-root idea).
/// </summary>
internal static class IosManagedTypeRootCollector
{
    private static readonly HashSet<string> s_echoSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".asset",
        ".prefab",
        ".scene",
    };

    internal static IosManagedTypeRootCollection Collect(
        AssetCollector.CollectionResult assets,
        IReadOnlyList<IosManagedAssemblyInput> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(assemblies);
        EditorAssetBackend database = EditorAssetBackend.Instance
            ?? throw new InvalidOperationException("iOS managed type root collection requires an initialized asset database.");

        var allowedAssemblies = CreateAllowedAssemblySet(assemblies);
        var builders = new Dictionary<string, TypeRootBuilder>(StringComparer.Ordinal);
        var entries = new List<AssetEntry>();
        foreach (AssetEntry entry in database.GetAllEntries())
        {
            bool selected = assets.AllAssets.Contains(entry.Guid);
            if (!selected && entry.SubAssets != null)
            {
                for (int i = 0; i < entry.SubAssets.Length; i++)
                {
                    if (assets.AllAssets.Contains(entry.SubAssets[i].Guid))
                    {
                        selected = true;
                        break;
                    }
                }
            }
            if (selected)
                entries.Add(entry);
        }
        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));

        for (int i = 0; i < entries.Count; i++)
        {
            AssetEntry entry = entries[i];
            if (assets.AllAssets.Contains(entry.Guid) && !string.IsNullOrWhiteSpace(entry.MainAssetTypeName))
            {
                AddResolvedType(
                    entry.MainAssetTypeName,
                    new IosManagedTypeRootReason(entry.Guid, NormalizePath(entry.Path), "asset-main-type"),
                    allowedAssemblies,
                    builders);
            }

            if (entry.SubAssets != null)
            {
                for (int subIndex = 0; subIndex < entry.SubAssets.Length; subIndex++)
                {
                    SubAssetEntry subAsset = entry.SubAssets[subIndex];
                    if (!assets.AllAssets.Contains(subAsset.Guid) || string.IsNullOrWhiteSpace(subAsset.TypeName))
                        continue;
                    AddResolvedType(
                        subAsset.TypeName,
                        new IosManagedTypeRootReason(subAsset.Guid, NormalizePath(entry.Path), "asset-sub-type"),
                        allowedAssemblies,
                        builders);
                }
            }

            if (!s_echoSourceExtensions.Contains(Path.GetExtension(entry.Path)))
                continue;

            string sourcePath = ResolveAssetSourcePath(database, entry);
            EchoObject document;
            try
            {
                document = EchoObject.ReadFromString(File.ReadAllText(sourcePath));
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    $"iOS managed type root collection could not parse Echo asset '{entry.Path}'.",
                    exception);
            }

            CollectDocumentTypes(
                document,
                new IosManagedTypeRootReason(entry.Guid, NormalizePath(entry.Path), "serialized-$type"),
                allowedAssemblies,
                builders);
        }

        return BuildCollection(builders);
    }

    internal static IosManagedTypeRootCollection CollectDocument(
        EchoObject document,
        Guid assetGuid,
        string assetPath,
        IReadOnlyList<IosManagedAssemblyInput> assemblies)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        ArgumentNullException.ThrowIfNull(assemblies);
        var builders = new Dictionary<string, TypeRootBuilder>(StringComparer.Ordinal);
        CollectDocumentTypes(
            document,
            new IosManagedTypeRootReason(assetGuid, NormalizePath(assetPath), "serialized-$type"),
            CreateAllowedAssemblySet(assemblies),
            builders);
        return BuildCollection(builders);
    }

    private static void CollectDocumentTypes(
        EchoObject document,
        IosManagedTypeRootReason reason,
        HashSet<string> allowedAssemblies,
        Dictionary<string, TypeRootBuilder> builders)
    {
        var pending = new Stack<EchoObject>();
        pending.Push(document);
        while (pending.Count > 0)
        {
            EchoObject node = pending.Pop();
            if (node.TagType == EchoType.Compound)
            {
                if (node.TryGet("$type", out EchoObject? typeTag) &&
                    typeTag.TagType == EchoType.String &&
                    !string.IsNullOrWhiteSpace(typeTag.StringValue))
                {
                    AddResolvedType(typeTag.StringValue, reason, allowedAssemblies, builders);
                }

                foreach (KeyValuePair<string, EchoObject> child in node.Tags)
                    pending.Push(child.Value);
            }
            else if (node.TagType == EchoType.List)
            {
                for (int i = 0; i < node.List.Count; i++)
                    pending.Push(node.List[i]);
            }
        }
    }

    private static void AddResolvedType(
        string assemblyQualifiedName,
        IosManagedTypeRootReason reason,
        HashSet<string> allowedAssemblies,
        Dictionary<string, TypeRootBuilder> builders)
    {
        Type? resolved = RuntimeUtils.ResolveType(assemblyQualifiedName);
        if (resolved == null)
        {
            throw new InvalidDataException(
                $"iOS asset '{reason.AssetPath}' references unresolved serialized type '{assemblyQualifiedName}'.");
        }

        var pending = new Stack<Type>();
        var visited = new HashSet<Type>();
        pending.Push(resolved);
        while (pending.Count > 0)
        {
            Type type = pending.Pop();
            if (!visited.Add(type))
                continue;

            if (type.HasElementType)
            {
                Type? elementType = type.GetElementType();
                if (elementType != null)
                    pending.Push(elementType);
                continue;
            }

            if (type.IsConstructedGenericType)
            {
                pending.Push(type.GetGenericTypeDefinition());
                Type[] arguments = type.GetGenericArguments();
                for (int i = 0; i < arguments.Length; i++)
                    pending.Push(arguments[i]);
                continue;
            }

            string assemblyName = type.Assembly.GetName().Name
                ?? throw new InvalidDataException($"Serialized type '{type}' has no assembly identity.");
            if (!allowedAssemblies.Contains(assemblyName) && !IsFrameworkAssembly(assemblyName))
            {
                throw new InvalidDataException(
                    $"iOS asset '{reason.AssetPath}' requires serialized type '{type.FullName}' from " +
                    $"assembly '{assemblyName}', but that assembly is absent from the iOS managed dependency closure.");
            }

            string typeName = type.FullName
                ?? throw new InvalidDataException($"Serialized type '{type}' has no stable full name.");
            string key = assemblyName + "\0" + typeName;
            if (!builders.TryGetValue(key, out TypeRootBuilder? builder))
            {
                builder = new TypeRootBuilder(assemblyName, typeName);
                builders.Add(key, builder);
            }
            builder.AddReason(reason);

            Type? baseType = type.BaseType;
            if (baseType != null && baseType != typeof(object))
                pending.Push(baseType);
        }
    }

    private static IosManagedTypeRootCollection BuildCollection(Dictionary<string, TypeRootBuilder> builders)
    {
        var types = new List<IosManagedTypeRoot>(builders.Count);
        foreach (TypeRootBuilder builder in builders.Values)
            types.Add(builder.Build());
        types.Sort(static (left, right) =>
        {
            int assembly = StringComparer.Ordinal.Compare(left.AssemblyName, right.AssemblyName);
            return assembly != 0 ? assembly : StringComparer.Ordinal.Compare(left.TypeName, right.TypeName);
        });
        return new IosManagedTypeRootCollection(Array.AsReadOnly(types.ToArray()));
    }

    private static HashSet<string> CreateAllowedAssemblySet(IReadOnlyList<IosManagedAssemblyInput> assemblies)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < assemblies.Count; i++)
            result.Add(assemblies[i].Name);
        return result;
    }

    private static string ResolveAssetSourcePath(EditorAssetBackend database, AssetEntry entry)
    {
        // Entry paths are project-relative logical paths; package assets ("Packages/<pkg>/...") live
        // in embed roots or Library/PackageCache and only the asset database can map them to disk.
        string sourcePath = database.GetEntryAbsolutePath(entry);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Collected iOS asset source is missing: '{entry.Path}'.", sourcePath);
        return sourcePath;
    }

    private static bool IsFrameworkAssembly(string assemblyName) =>
        assemblyName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
        assemblyName.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
        assemblyName.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
        assemblyName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private sealed class TypeRootBuilder
    {
        private readonly List<IosManagedTypeRootReason> _reasons = new();
        private readonly HashSet<string> _reasonKeys = new(StringComparer.Ordinal);

        internal TypeRootBuilder(string assemblyName, string typeName)
        {
            AssemblyName = assemblyName;
            TypeName = typeName;
        }

        internal string AssemblyName { get; }
        internal string TypeName { get; }

        internal void AddReason(IosManagedTypeRootReason reason)
        {
            string key = reason.AssetGuid.ToString("N") + "\0" + reason.AssetPath + "\0" + reason.Source;
            if (_reasonKeys.Add(key))
                _reasons.Add(reason);
        }

        internal IosManagedTypeRoot Build()
        {
            _reasons.Sort(static (left, right) =>
            {
                int path = StringComparer.Ordinal.Compare(left.AssetPath, right.AssetPath);
                if (path != 0)
                    return path;
                int source = StringComparer.Ordinal.Compare(left.Source, right.Source);
                if (source != 0)
                    return source;
                return StringComparer.Ordinal.Compare(left.AssetGuid.ToString("N"), right.AssetGuid.ToString("N"));
            });
            return new IosManagedTypeRoot(AssemblyName, TypeName, Array.AsReadOnly(_reasons.ToArray()));
        }
    }
}

internal static class IosManagedTypeRootReport
{
    internal const int SchemaVersion = 1;
    internal const string FileName = "ios-managed-type-roots.json";

    internal static void Write(string path, IosManagedTypeRootCollection collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(collection);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);
        var payload = new
        {
            schemaVersion = SchemaVersion,
            typeCount = collection.Types.Count,
            types = collection.Types,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }) + Environment.NewLine, new UTF8Encoding(false));
    }
}
