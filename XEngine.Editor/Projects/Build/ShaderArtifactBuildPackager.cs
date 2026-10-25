// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

namespace XEngine.Editor.Build;

internal readonly record struct ShaderArtifactBuildPackage(
    string DirectoryPath,
    string CatalogPath,
    int ShaderAssetCount,
    int ArtifactCount,
    long ArtifactBytes);

/// <summary>
/// Logical shader artifact required by a target-aware build. The selector contains the shader
/// asset GUID as well as the runtime lookup identity, so identically named shaders cannot cause
/// a variant from the wrong asset to enter a Player package.
/// </summary>
internal readonly record struct ShaderArtifactBuildSelection(
    Guid ShaderAssetGuid,
    string PassName,
    GraphicsBackend Backend,
    string VariantKey,
    ShaderTargetEnvironment TargetEnvironment = ShaderTargetEnvironment.Default);

internal static class ShaderArtifactBuildPackager
{
    internal const string CatalogFileName = "catalog.xshadercatalog";
    internal const string ImportIndexDirectoryName = "imports";
    internal const string ImportIndexExtension = ".xshaderindex";

    internal static string GetImportIndexPath(Project project, Guid shaderAssetGuid)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (shaderAssetGuid == Guid.Empty)
            throw new ArgumentException("Shader asset GUID cannot be empty.", nameof(shaderAssetGuid));
        return Path.Combine(
            project.CachePath,
            "shaders",
            ImportIndexDirectoryName,
            shaderAssetGuid.ToString("N") + ImportIndexExtension);
    }

    internal static void WriteImportIndex(
        Project project,
        Guid shaderAssetGuid,
        IReadOnlyList<ShaderArtifactKey> artifactKeys)
    {
        ArgumentNullException.ThrowIfNull(artifactKeys);
        string path = GetImportIndexPath(project, shaderAssetGuid);
        var entries = new Dictionary<Guid, IReadOnlyList<ShaderArtifactKey>>(1)
        {
            [shaderAssetGuid] = artifactKeys,
        };
        var catalog = new ShaderArtifactCatalog(entries);
        if (ImportIndexMatches(path, shaderAssetGuid, catalog.Entries[shaderAssetGuid]))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                catalog.Write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    internal static void DeleteImportIndex(Project project, Guid shaderAssetGuid) =>
        TryDeleteFile(GetImportIndexPath(project, shaderAssetGuid));

    internal static ShaderArtifactBuildPackage Prepare(
        Project project,
        IEnumerable<Guid> collectedAssets,
        string outputDirectory,
        bool includeBuiltInShaders = true,
        GraphicsBackend? targetBackend = null,
        IReadOnlyList<ShaderArtifactBuildSelection>? selections = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(collectedAssets);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        HashSet<ShaderArtifactBuildSelection>? requiredSelections = null;
        HashSet<ShaderArtifactBuildSelection>? matchedSelections = null;
        if (selections != null)
        {
            requiredSelections = new HashSet<ShaderArtifactBuildSelection>(selections);
            if (requiredSelections.Count == 0)
                throw new ArgumentException("Shader artifact selection cannot be empty.", nameof(selections));
            if (targetBackend.HasValue)
            {
                foreach (ShaderArtifactBuildSelection selection in requiredSelections)
                {
                    if (selection.Backend != targetBackend.Value)
                    {
                        throw new ArgumentException(
                            $"Shader artifact selection backend {selection.Backend} does not match target {targetBackend.Value}.",
                            nameof(selections));
                    }
                }
            }
            matchedSelections = new HashSet<ShaderArtifactBuildSelection>();
        }

        EditorAssetBackend database = EditorAssetBackend.Instance
            ?? throw new InvalidOperationException("Shader artifact packaging requires an active asset database.");
        var shaderGuidSet = new HashSet<Guid>();
        foreach (Guid guid in collectedAssets)
        {
            AssetEntry? entry = database.GetEntry(guid);
            if ((entry != null && string.Equals(Path.GetExtension(entry.Path), ".shader", StringComparison.OrdinalIgnoreCase)) ||
                (BuiltInAssets.Entries.TryGetValue(guid, out BuiltInAssets.BuiltInEntry builtIn) &&
                 typeof(Shader).IsAssignableFrom(builtIn.AssetType)))
                shaderGuidSet.Add(guid);
        }
        if (includeBuiltInShaders)
        {
            foreach (Guid guid in BuildShaderInventory.GetBuiltInShaderGuids(project))
                shaderGuidSet.Add(guid);
        }
        // Scene/contributor fallbacks can select registered defaults without a serialized asset
        // edge. Keep these explicit requirements even if their owner is absent from the manifest.
        if (requiredSelections != null)
        {
            foreach (ShaderArtifactBuildSelection selection in requiredSelections)
                shaderGuidSet.Add(selection.ShaderAssetGuid);
        }
        var shaderGuids = new List<Guid>(shaderGuidSet);
        shaderGuids.Sort();

        var catalogEntries = new Dictionary<Guid, IReadOnlyList<ShaderArtifactKey>>(shaderGuids.Count);
        var uniqueKeys = new SortedDictionary<string, ShaderArtifactKey>(StringComparer.Ordinal);
        for (int i = 0; i < shaderGuids.Count; i++)
        {
            Guid shaderGuid = shaderGuids[i];
            string indexPath = GetImportIndexPath(project, shaderGuid);
            if (!File.Exists(indexPath))
                throw new InvalidDataException(
                    $"Shader artifact index is missing for asset {shaderGuid}. Reimport the shader before building.");

            ShaderArtifactCatalog importIndex;
            try
            {
                using var stream = File.OpenRead(indexPath);
                importIndex = ShaderArtifactCatalog.Read(stream);
            }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
            {
                throw new InvalidDataException(
                    $"Shader artifact index is corrupt for asset {shaderGuid}. Reimport the shader before building.",
                    exception);
            }

            if (importIndex.Entries.Count != 1 || !importIndex.TryGetArtifacts(shaderGuid, out IReadOnlyList<ShaderArtifactKey>? keys))
                throw new InvalidDataException($"Shader artifact index does not match asset {shaderGuid}.");

            IReadOnlyList<ShaderArtifactKey> selectedKeys = targetBackend.HasValue
                ? FilterByBackend(
                    storeRoot: Path.Combine(project.CachePath, "shaders"),
                    keys,
                    targetBackend.Value,
                    shaderGuid,
                    requiredSelections,
                    matchedSelections)
                : FilterBySelections(
                    storeRoot: Path.Combine(project.CachePath, "shaders"),
                    keys,
                    shaderGuid,
                    requiredSelections,
                    matchedSelections);
            if (selectedKeys.Count == 0 && requiredSelections == null)
            {
                string backend = targetBackend?.ToString() ?? "requested";
                throw new InvalidDataException(
                    $"Shader artifact index for asset {shaderGuid} contains no {backend} artifacts.");
            }
            catalogEntries.Add(shaderGuid, selectedKeys);
            for (int keyIndex = 0; keyIndex < selectedKeys.Count; keyIndex++)
                uniqueKeys.TryAdd(selectedKeys[keyIndex].Value!, selectedKeys[keyIndex]);
        }

        if (requiredSelections != null && matchedSelections != null &&
            matchedSelections.Count != requiredSelections.Count)
        {
            throw CreateMissingSelectionException(requiredSelections, matchedSelections);
        }

        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        string stagingDirectory = fullOutputDirectory + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var store = new ShaderArtifactStore(Path.Combine(project.CachePath, "shaders"));
        long artifactBytes = 0;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            foreach (KeyValuePair<string, ShaderArtifactKey> entry in uniqueKeys)
            {
                if (!store.Contains(entry.Value))
                    throw new InvalidDataException(
                        $"Shader artifact {entry.Key} is missing or corrupt. Reimport its shader before building.");
                string sourcePath = store.GetArtifactPath(entry.Value);
                string destinationPath = Path.Combine(stagingDirectory, entry.Key + ".xshader");
                File.Copy(sourcePath, destinationPath, overwrite: false);
                artifactBytes += new FileInfo(destinationPath).Length;
            }

            var stableEntries = new List<ShaderArtifactCatalogEntry>(uniqueKeys.Count);
            foreach (KeyValuePair<Guid, IReadOnlyList<ShaderArtifactKey>> assetEntry in catalogEntries)
            {
                IReadOnlyList<ShaderArtifactKey> keys = assetEntry.Value;
                for (int i = 0; i < keys.Count; i++)
                {
                    ShaderArtifactKey key = keys[i];
                    string? value = key.Value;
                    if (string.IsNullOrEmpty(value))
                        throw new InvalidDataException("Shader artifact catalog contains an empty key.");
                    string artifactPath = Path.Combine(stagingDirectory, value + ".xshader");
                    using FileStream artifactStream = File.OpenRead(artifactPath);
                    ShaderArtifact artifact = ShaderArtifactCodec.Read(artifactStream, key);
                    stableEntries.Add(new ShaderArtifactCatalogEntry(
                        assetEntry.Key,
                        artifact.ShaderName,
                        artifact.PassName,
                        artifact.Backend,
                        artifact.TargetProfile,
                        artifact.VariantKey,
                        key));
                }
            }

            string catalogPath = Path.Combine(stagingDirectory, CatalogFileName);
            using (var stream = new FileStream(catalogPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                new ShaderArtifactCatalog(stableEntries).Write(stream);

            if (Directory.Exists(fullOutputDirectory))
                Directory.Delete(fullOutputDirectory, recursive: true);
            Directory.Move(stagingDirectory, fullOutputDirectory);
            return new ShaderArtifactBuildPackage(
                fullOutputDirectory,
                Path.Combine(fullOutputDirectory, CatalogFileName),
                shaderGuids.Count,
                uniqueKeys.Count,
                artifactBytes);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    internal static void CopyPreparedPackage(string sourceDirectory, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (Directory.Exists(destinationDirectory))
            Directory.Delete(destinationDirectory, recursive: true);
        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            File.Copy(sourcePath, Path.Combine(destinationDirectory, Path.GetFileName(sourcePath)), overwrite: false);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static bool ImportIndexMatches(
        string path,
        Guid shaderAssetGuid,
        IReadOnlyList<ShaderArtifactKey> expectedKeys)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var stream = File.OpenRead(path);
            ShaderArtifactCatalog existing = ShaderArtifactCatalog.Read(stream);
            if (existing.Entries.Count != 1 ||
                !existing.TryGetArtifacts(shaderAssetGuid, out IReadOnlyList<ShaderArtifactKey>? existingKeys) ||
                existingKeys.Count != expectedKeys.Count)
            {
                return false;
            }
            for (int i = 0; i < existingKeys.Count; i++)
            {
                if (existingKeys[i] != expectedKeys[i])
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            return false;
        }
    }

    private static IReadOnlyList<ShaderArtifactKey> FilterByBackend(
        string storeRoot,
        IReadOnlyList<ShaderArtifactKey> keys,
        GraphicsBackend targetBackend,
        Guid shaderGuid,
        HashSet<ShaderArtifactBuildSelection>? requiredSelections,
        HashSet<ShaderArtifactBuildSelection>? matchedSelections)
    {
        var selected = new List<ShaderArtifactKey>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            ShaderArtifactKey key = keys[i];
            string? value = key.Value;
            if (string.IsNullOrEmpty(value))
                throw new InvalidDataException($"Shader artifact index contains an empty key for asset {shaderGuid}.");
            string path = Path.Combine(storeRoot, value + ".xshader");
            if (!File.Exists(path))
                throw new InvalidDataException($"Shader artifact {value} is missing or corrupt.");
            using FileStream stream = File.OpenRead(path);
            ShaderArtifact artifact = ShaderArtifactCodec.Read(stream, key);
            if (artifact.Backend != targetBackend)
                continue;
            AddIfSelected(selected, key, shaderGuid, artifact, requiredSelections, matchedSelections);
        }
        return selected.AsReadOnly();
    }

    private static IReadOnlyList<ShaderArtifactKey> FilterBySelections(
        string storeRoot,
        IReadOnlyList<ShaderArtifactKey> keys,
        Guid shaderGuid,
        HashSet<ShaderArtifactBuildSelection>? requiredSelections,
        HashSet<ShaderArtifactBuildSelection>? matchedSelections)
    {
        if (requiredSelections == null)
            return keys;

        var selected = new List<ShaderArtifactKey>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            ShaderArtifactKey key = keys[i];
            string? value = key.Value;
            if (string.IsNullOrEmpty(value))
                throw new InvalidDataException($"Shader artifact index contains an empty key for asset {shaderGuid}.");
            string path = Path.Combine(storeRoot, value + ".xshader");
            if (!File.Exists(path))
                throw new InvalidDataException($"Shader artifact {value} is missing or corrupt.");
            using FileStream stream = File.OpenRead(path);
            ShaderArtifact artifact = ShaderArtifactCodec.Read(stream, key);
            AddIfSelected(selected, key, shaderGuid, artifact, requiredSelections, matchedSelections);
        }
        return selected.AsReadOnly();
    }

    private static void AddIfSelected(
        List<ShaderArtifactKey> selected,
        ShaderArtifactKey key,
        Guid shaderGuid,
        ShaderArtifact artifact,
        HashSet<ShaderArtifactBuildSelection>? requiredSelections,
        HashSet<ShaderArtifactBuildSelection>? matchedSelections)
    {
        if (requiredSelections == null)
        {
            selected.Add(key);
            return;
        }

        var selection = new ShaderArtifactBuildSelection(
            shaderGuid,
            artifact.PassName,
            artifact.Backend,
            artifact.VariantKey,
            RuntimeSlangShaderCompiler.GetArtifactTargetEnvironment(
                artifact.Backend,
                artifact.TargetProfile));
        if (!requiredSelections.Contains(selection))
            return;

        selected.Add(key);
        matchedSelections!.Add(selection);
    }

    private static InvalidDataException CreateMissingSelectionException(
        HashSet<ShaderArtifactBuildSelection> requiredSelections,
        HashSet<ShaderArtifactBuildSelection> matchedSelections)
    {
        ShaderArtifactBuildSelection missing = default;
        bool hasMissing = false;
        foreach (ShaderArtifactBuildSelection selection in requiredSelections)
        {
            if (matchedSelections.Contains(selection))
                continue;
            if (!hasMissing || CompareSelections(selection, missing) < 0)
            {
                missing = selection;
                hasMissing = true;
            }
        }

        return new InvalidDataException(
            "Required shader artifact is not available for asset " +
            $"{missing.ShaderAssetGuid:D} ({missing.PassName}, {missing.Backend}, " +
            $"variant={DisplayVariant(missing.VariantKey)}). Add it to the shader's offline variants or import an approved trace.");
    }

    private static int CompareSelections(
        ShaderArtifactBuildSelection left,
        ShaderArtifactBuildSelection right)
    {
        int comparison = left.ShaderAssetGuid.CompareTo(right.ShaderAssetGuid);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.PassName, right.PassName);
        if (comparison != 0)
            return comparison;
        comparison = left.Backend.CompareTo(right.Backend);
        return comparison != 0
            ? comparison
            : CompareEnvironmentThenVariant(left, right);
    }

    private static int CompareEnvironmentThenVariant(
        ShaderArtifactBuildSelection left,
        ShaderArtifactBuildSelection right)
    {
        int comparison = left.TargetEnvironment.CompareTo(right.TargetEnvironment);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.VariantKey, right.VariantKey);
    }

    private static string DisplayVariant(string value) => string.IsNullOrEmpty(value) ? "<default>" : value;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
