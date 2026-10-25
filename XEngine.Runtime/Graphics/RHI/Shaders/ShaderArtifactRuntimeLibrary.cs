// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

using XEngine.Runtime.AssetStreaming;

namespace XEngine.Runtime.RHI.Shaders;

public readonly record struct ShaderArtifactRuntimeStats(
    int ArtifactCount,
    int LoadedArtifactCount,
    long LoadedBytecodeBytes,
    long LookupHits,
    long LookupMisses);

/// <summary>
/// Structured shader lookup observation for Development collection. The callback is opt-in and is
/// never invoked unless a caller explicitly installs a sink, so Release lookups keep their existing
/// allocation-free path.
/// </summary>
public readonly record struct ShaderArtifactRuntimeLookup(
    string ShaderName,
    string PassName,
    GraphicsBackend Backend,
    string VariantKey,
    bool Found);

public static class ShaderArtifactRuntimeLibrary
{
    private const string CatalogFileName = "catalog.xshadercatalog";
    private const string ArtifactExtension = ".xshader";
    private const string EmbeddedResourcePrefix = "Shaders.";

    private static readonly object s_gate = new();
    private static LoadedPackage? s_package;
    private static long s_lookupHits;
    private static long s_lookupMisses;
    private static Action<ShaderArtifactRuntimeLookup>? s_usageTraceSink;

    public static bool IsConfigured => Volatile.Read(ref s_package) != null;

    public static string SourceDescription =>
        Volatile.Read(ref s_package)?.SourceDescription ?? string.Empty;

    public static ShaderArtifactRuntimeStats GetStats()
    {
        LoadedPackage? package = Volatile.Read(ref s_package);
        return new ShaderArtifactRuntimeStats(
            package?.ArtifactCount ?? 0,
            package?.LoadedArtifacts.Count ?? 0,
            package?.LoadedBytecodeBytes ?? 0,
            Interlocked.Read(ref s_lookupHits),
            Interlocked.Read(ref s_lookupMisses));
    }

    /// <summary>
    /// Installs an optional Development lookup sink. The sink must be thread-safe and should keep
    /// work bounded; it can serialize observations to a reviewed JSONL collection outside the
    /// render thread. Passing <c>null</c> disables tracing.
    /// </summary>
    public static void SetUsageTraceSink(Action<ShaderArtifactRuntimeLookup>? sink) =>
        Volatile.Write(ref s_usageTraceSink, sink);

    /// <summary>
    /// Enables a durable Development lookup trace under the Player's writable data directory unless
    /// the caller supplies another path. The trace is deliberately opt-in: Release Players never
    /// allocate or write lookup diagnostics unless a host explicitly requests it.
    /// </summary>
    public static IDisposable EnableDevelopmentUsageTrace(string? path = null)
    {
        string resolvedPath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Application.PersistentDataPath, "Diagnostics", ShaderArtifactUsageTrace.FileName)
            : path;
        var trace = new ShaderArtifactUsageTrace(resolvedPath);
        Action<ShaderArtifactRuntimeLookup> sink = trace.Record;
        SetUsageTraceSink(sink);
        return new UsageTraceRegistration(trace, sink);
    }

    public static void Configure(
        AssetPackagingMode packagingMode,
        string basePath = "Content",
        Assembly? embeddedAssembly = null)
    {
        switch (packagingMode)
        {
            case AssetPackagingMode.LooseFiles:
            case AssetPackagingMode.XEnginePak:
            case AssetPackagingMode.AssetBundle:
                ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
                string contentDirectory = Path.IsPathFullyQualified(basePath)
                    ? Path.GetFullPath(basePath)
                    : Path.GetFullPath(Path.Combine(Application.DataPath, basePath));
                string shaderDirectory = Path.Combine(contentDirectory, "Shaders");
                ConfigureFromPackage(
                    fileName => OpenFile(shaderDirectory, fileName),
                    shaderDirectory);
                break;

            case AssetPackagingMode.Embedded:
                Assembly assembly = embeddedAssembly ?? Assembly.GetEntryAssembly()
                    ?? throw new InvalidOperationException(
                        "Embedded shader artifacts require a Player entry assembly.");
                ConfigureFromPackage(
                    fileName => assembly.GetManifestResourceStream(GetEmbeddedResourceName(fileName)),
                    $"embedded resources in {assembly.GetName().Name}");
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(packagingMode),
                    packagingMode,
                    "Unknown asset packaging mode.");
        }

        LogConfiguration();
    }

    /// <summary>
    /// Loads a shader package through a platform stream provider. The provider is owned by the
    /// configured package and remains alive until the next configuration or reset.
    /// </summary>
    public static void Configure(IAssetStreamProvider provider, string packageDirectory = "")
    {
        ArgumentNullException.ThrowIfNull(provider);
        string directory = AssetStreamPath.Normalize(packageDirectory, allowEmpty: true);
        string sourceDescription = directory.Length == 0
            ? provider.Description
            : provider.Description + "/" + directory;
        ConfigureFromPackage(
            fileName => provider.OpenRead(AssetStreamPath.Combine(directory, fileName)),
            sourceDescription,
            provider);
        LogConfiguration();
    }

    internal static ShaderArtifact GetRequiredArtifact(
        string shaderName,
        string passName,
        GraphicsBackend backend,
        string variantKey)
    {
        LoadedPackage package = Volatile.Read(ref s_package)
            ?? throw new InvalidOperationException("The Player shader artifact library is not configured.");
        var identity = new ShaderArtifactIdentity(shaderName, passName, backend, variantKey);
        if (package.LoadedArtifacts.TryGetValue(identity, out ShaderArtifact? artifact))
        {
            Interlocked.Increment(ref s_lookupHits);
            ObserveLookup(shaderName, passName, backend, variantKey, found: true);
            return artifact;
        }

        if (package.Locators.TryGetValue(identity, out ShaderArtifactLocator? locator))
        {
            try
            {
                lock (package.LoadGate)
                {
                    if (!package.LoadedArtifacts.TryGetValue(identity, out artifact))
                    {
                        artifact = LoadArtifact(package, identity, locator);
                        package.LoadedArtifacts.TryAdd(identity, artifact);
                        Interlocked.Add(
                            ref package.LoadedBytecodeBytes,
                            artifact.Bytecode.VertexBytecode.Length + artifact.Bytecode.FragmentBytecode.Length);
                    }
                }
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or InvalidDataException or EndOfStreamException)
            {
                Interlocked.Increment(ref s_lookupMisses);
                ObserveLookup(shaderName, passName, backend, variantKey, found: false);
                throw new ShaderArtifactUnavailableException(
                    $"Offline shader artifact is unavailable for {shaderName}/{passName} " +
                    $"[{backend}, variant={DisplayVariant(variantKey)}]: {exception.Message}");
            }

            Interlocked.Increment(ref s_lookupHits);
            ObserveLookup(shaderName, passName, backend, variantKey, found: true);
            return artifact;
        }

        Interlocked.Increment(ref s_lookupMisses);
        ObserveLookup(shaderName, passName, backend, variantKey, found: false);
        string variant = string.IsNullOrEmpty(variantKey) ? "<default>" : variantKey;
        throw new ShaderArtifactUnavailableException(
            $"Offline shader artifact is missing for {shaderName}/{passName} " +
            $"[{backend}, variant={variant}]. Runtime HLSL compilation is disabled in this Player.");
    }

    internal static void ConfigureFromPackage(
        Func<string, Stream?> openStream,
        string sourceDescription,
        IDisposable? ownedProvider = null)
    {
        ArgumentNullException.ThrowIfNull(openStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDescription);

        ShaderArtifactCatalog catalog;
        using (Stream catalogStream = openStream(CatalogFileName)
            ?? throw new FileNotFoundException(
                $"Shader artifact catalog is missing from {sourceDescription}."))
        {
            catalog = ShaderArtifactCatalog.Read(catalogStream);
        }

        LoadedPackage package;
        if (catalog.HasStableEntries)
        {
            var locators = new Dictionary<ShaderArtifactIdentity, ShaderArtifactLocator>(catalog.StableEntries.Count);
            for (int i = 0; i < catalog.StableEntries.Count; i++)
            {
                ShaderArtifactCatalogEntry entry = catalog.StableEntries[i];
                var identity = new ShaderArtifactIdentity(
                    entry.ShaderName,
                    entry.PassStableId,
                    entry.Backend,
                    entry.CanonicalKeywordSet);
                if (!locators.TryAdd(identity, new ShaderArtifactLocator(entry)))
                    throw new InvalidDataException(
                        $"Shader package contains duplicate artifacts for " +
                        $"{entry.ShaderName}/{entry.PassStableId} " +
                        $"[{entry.Backend}, variant={DisplayVariant(entry.CanonicalKeywordSet)}].");
            }
            package = new LoadedPackage(
                locators,
                new ConcurrentDictionary<ShaderArtifactIdentity, ShaderArtifact>(),
                sourceDescription,
                openStream,
                ownedProvider,
                catalog.StableEntries.Count);
        }
        else
        {
            var artifacts = LoadLegacyArtifacts(catalog, openStream, sourceDescription);
            var loaded = new ConcurrentDictionary<ShaderArtifactIdentity, ShaderArtifact>(artifacts);
            var locators = new Dictionary<ShaderArtifactIdentity, ShaderArtifactLocator>(artifacts.Count);
            package = new LoadedPackage(
                locators,
                loaded,
                sourceDescription,
                openStream,
                ownedProvider,
                artifacts.Count);
        }
        lock (s_gate)
        {
            LoadedPackage? previous = Volatile.Read(ref s_package);
            Volatile.Write(ref s_package, package);
            Interlocked.Exchange(ref s_lookupHits, 0);
            Interlocked.Exchange(ref s_lookupMisses, 0);
            previous?.Dispose();
        }
    }

    private static Dictionary<ShaderArtifactIdentity, ShaderArtifact> LoadLegacyArtifacts(
        ShaderArtifactCatalog catalog,
        Func<string, Stream?> openStream,
        string sourceDescription)
    {
        var artifacts = new Dictionary<ShaderArtifactIdentity, ShaderArtifact>();
        var loadedKeys = new HashSet<ShaderArtifactKey>();
        foreach (IReadOnlyList<ShaderArtifactKey> assetKeys in catalog.Entries.Values)
        {
            for (int i = 0; i < assetKeys.Count; i++)
            {
                ShaderArtifactKey key = assetKeys[i];
                if (!loadedKeys.Add(key))
                    continue;
                using Stream artifactStream = openStream(key.Value + ArtifactExtension)
                    ?? throw new FileNotFoundException(
                        $"Shader artifact {key} is missing from {sourceDescription}.");
                ShaderArtifact artifact;
                try
                {
                    artifact = ShaderArtifactCodec.Read(artifactStream, key);
                }
                catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
                {
                    throw new InvalidDataException(
                        $"Shader artifact {key} is corrupt in {sourceDescription}.",
                        exception);
                }
                AddLoadedArtifact(artifacts, artifact);
            }
        }
        return artifacts;
    }

    private static ShaderArtifact LoadArtifact(
        LoadedPackage package,
        ShaderArtifactIdentity identity,
        ShaderArtifactLocator locator)
    {
        ShaderArtifactKey key = locator.Entry.ArtifactKey;
        using Stream artifactStream = package.OpenStream(key.Value + ArtifactExtension)
            ?? throw new FileNotFoundException(
                $"Shader artifact {key} is missing from {package.SourceDescription}.");
        ShaderArtifact artifact;
        try
        {
            artifact = ShaderArtifactCodec.Read(artifactStream, key);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException(
                $"Shader artifact {key} is corrupt in {package.SourceDescription}.",
                exception);
        }

        var actualIdentity = new ShaderArtifactIdentity(
            artifact.ShaderName,
            artifact.PassName,
            artifact.Backend,
            artifact.VariantKey);
        if (!actualIdentity.Equals(identity) ||
            !string.Equals(artifact.TargetProfile, locator.Entry.Tier, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Shader artifact {key} does not match its catalog identity.");
        }
        return artifact;
    }

    private static void AddLoadedArtifact(
        Dictionary<ShaderArtifactIdentity, ShaderArtifact> artifacts,
        ShaderArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.ShaderName) ||
            string.IsNullOrWhiteSpace(artifact.PassName))
        {
            throw new InvalidDataException("Shader artifact has an invalid logical identity.");
        }
        var identity = new ShaderArtifactIdentity(
            artifact.ShaderName,
            artifact.PassName,
            artifact.Backend,
            artifact.VariantKey);
        if (!artifacts.TryAdd(identity, artifact))
        {
            throw new InvalidDataException(
                $"Shader package contains duplicate artifacts for " +
                $"{artifact.ShaderName}/{artifact.PassName} " +
                $"[{artifact.Backend}, variant={DisplayVariant(artifact.VariantKey)}].");
        }
    }

    internal static string GetEmbeddedResourceName(string fileName) =>
        EmbeddedResourcePrefix + fileName;

    internal static void ResetForTests()
    {
        lock (s_gate)
        {
            LoadedPackage? previous = Volatile.Read(ref s_package);
            Volatile.Write(ref s_package, null);
            Interlocked.Exchange(ref s_lookupHits, 0);
            Interlocked.Exchange(ref s_lookupMisses, 0);
            Volatile.Write(ref s_usageTraceSink, null);
            previous?.Dispose();
        }
    }

    private static void ObserveLookup(
        string shaderName,
        string passName,
        GraphicsBackend backend,
        string variantKey,
        bool found)
    {
        Action<ShaderArtifactRuntimeLookup>? sink = Volatile.Read(ref s_usageTraceSink);
        if (sink == null)
            return;

        try
        {
            sink(new ShaderArtifactRuntimeLookup(shaderName, passName, backend, variantKey, found));
        }
        catch (Exception exception)
        {
            // Diagnostics must not turn a shader miss or a successful draw into a render failure.
            Debug.LogWarning($"[ShaderArtifacts] Usage trace sink failed: {exception.Message}");
        }
    }

    private static Stream? OpenFile(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
    }

    private static void LogConfiguration()
    {
        ShaderArtifactRuntimeStats stats = GetStats();
        Debug.Log(
            $"[ShaderArtifacts] Indexed {stats.ArtifactCount} offline artifacts from " +
            $"{SourceDescription}; loaded={stats.LoadedArtifactCount}, " +
            $"bytecodeBytes={stats.LoadedBytecodeBytes}. Runtime HLSL compilation is disabled.");
    }

    private static string DisplayVariant(string variantKey) =>
        string.IsNullOrEmpty(variantKey) ? "<default>" : variantKey;

    private sealed class UsageTraceRegistration(
        ShaderArtifactUsageTrace trace,
        Action<ShaderArtifactRuntimeLookup> sink) : IDisposable
    {
        private ShaderArtifactUsageTrace? _trace = trace;
        private Action<ShaderArtifactRuntimeLookup>? _sink = sink;

        public void Dispose()
        {
            ShaderArtifactUsageTrace? trace = Interlocked.Exchange(ref _trace, null);
            Action<ShaderArtifactRuntimeLookup>? sink = Interlocked.Exchange(ref _sink, null);
            if (sink != null)
                Interlocked.CompareExchange(ref s_usageTraceSink, null, sink);
            trace?.Dispose();
        }
    }

    private readonly record struct ShaderArtifactIdentity(
        string ShaderName,
        string PassName,
        GraphicsBackend Backend,
        string VariantKey);

    private sealed class LoadedPackage : IDisposable
    {
        public LoadedPackage(
            Dictionary<ShaderArtifactIdentity, ShaderArtifactLocator> locators,
            ConcurrentDictionary<ShaderArtifactIdentity, ShaderArtifact> loadedArtifacts,
            string sourceDescription,
            Func<string, Stream?> openStream,
            IDisposable? ownedProvider,
            int artifactCount)
        {
            Locators = locators;
            LoadedArtifacts = loadedArtifacts;
            SourceDescription = sourceDescription;
            OpenStream = openStream;
            OwnedProvider = ownedProvider;
            ArtifactCount = artifactCount;
        }

        public readonly object LoadGate = new();
        public readonly Dictionary<ShaderArtifactIdentity, ShaderArtifactLocator> Locators;
        public readonly ConcurrentDictionary<ShaderArtifactIdentity, ShaderArtifact> LoadedArtifacts;
        public readonly string SourceDescription;
        public readonly Func<string, Stream?> OpenStream;
        public readonly IDisposable? OwnedProvider;
        public readonly int ArtifactCount;
        public long LoadedBytecodeBytes;

        public void Dispose() => OwnedProvider?.Dispose();
    }

    private sealed record ShaderArtifactLocator(ShaderArtifactCatalogEntry Entry);
}

internal sealed class ShaderArtifactUnavailableException(string message)
    : InvalidOperationException(message);
